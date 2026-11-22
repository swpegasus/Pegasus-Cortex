using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pegasus.AgentOps.Certificates;

/// <summary>Certificate material checks only. No name resolution, hostname match, network or trust installation.</summary>
public static class ServerCertificateMaterial
{
    public static ServerCertificateNames Require(X509Certificate2 certificate, string expectedCertificateId,
        DateTimeOffset now, bool privateKey)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        CertificateMaterial.RequireIdentity(certificate, expectedCertificateId);
        if (now.Offset != TimeSpan.Zero || now < new DateTimeOffset(certificate.NotBefore.ToUniversalTime()) ||
            now > new DateTimeOffset(certificate.NotAfter.ToUniversalTime()))
            throw new CertificateOperationException("certificate_time_invalid");
        var basic = One(certificate, "2.5.29.19");
        var usage = One(certificate, "2.5.29.15");
        var purposes = new X509EnhancedKeyUsageExtension(One(certificate, "2.5.29.37"), false).EnhancedKeyUsages;
        var keyUsage = new X509KeyUsageExtension(usage, usage.Critical).KeyUsages;
        if (new X509BasicConstraintsExtension(basic, basic.Critical).CertificateAuthority ||
            (keyUsage & X509KeyUsageFlags.DigitalSignature) == 0 ||
            (keyUsage & (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign)) != 0 ||
            purposes.Count != 1 || purposes[0].Value != CertificateOperations.ServerAuthenticationOid)
            throw new CertificateOperationException("server_certificate_purpose_invalid");
        var names = ServerCertificateNames.FromCertificate(certificate);
        using var publicKey = certificate.GetRSAPublicKey();
        if (publicKey is null || publicKey.KeySize is not (2048 or 3072 or 4096))
            throw new CertificateOperationException("rsa_key_required");
        if (privateKey)
        {
            if (!certificate.HasPrivateKey) throw new CertificateOperationException("certificate_private_key_required");
            using var key = certificate.GetRSAPrivateKey();
            if (key is null || key.KeySize != publicKey.KeySize)
                throw new CertificateOperationException("certificate_private_key_required");
        }
        return names;
    }

    public static void ProvePrivateKey(X509Certificate2 certificate)
    {
        using var key = certificate.GetRSAPrivateKey();
        using var publicKey = certificate.GetRSAPublicKey();
        if (key is null || publicKey is null) throw new CertificateOperationException("certificate_private_key_required");
        var challenge = RandomNumberGenerator.GetBytes(32);
        byte[]? signature = null;
        try
        {
            signature = key.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!publicKey.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CertificateOperationException("server_private_key_unusable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static X509Extension One(X509Certificate2 certificate, string oid)
    {
        var matches = certificate.Extensions.Cast<X509Extension>().Where(value => value.Oid?.Value == oid).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new CertificateOperationException("server_certificate_extension_invalid");
    }
}

public sealed record ServerCertificateBindingResult(string State, string CertificateId, bool KeyPersistenceIssued,
    bool StoreActionIssued, bool PostconditionObserved, bool OutcomeUnknown);

public sealed class ServerCertificateBindingException(string code, bool keyPersistenceIssued, bool storeActionIssued,
    bool outcomeUnknown, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
    public bool KeyPersistenceIssued { get; } = keyPersistenceIssued;
    public bool StoreActionIssued { get; } = storeActionIssued;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

/// <summary>Separate from the public-only store backend; this action binds only CurrentUser/My.</summary>
public interface ICertificateServerBinding
{
    ServerCertificateBindingResult BindServer(X509Certificate2 certificate, string expectedCertificateId,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit owner-authorized private-key persistence. No machine/trust target, password/file output or listener.
/// The returned certificate ID selects the exact public DER; native key-container identities never leave this boundary.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCertificateServerBinding(Action requireOwner) : ICertificateServerBinding
{
    public ServerCertificateBindingResult BindServer(X509Certificate2 certificate, string expectedCertificateId,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requireOwner);
        requireOwner();
        _ = ServerCertificateMaterial.Require(certificate, expectedCertificateId, now, privateKey: true);
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadWrite);
            using (var existing = ReadExact(store, expectedCertificateId))
            {
                if (existing is not null)
                {
                    if (!existing.HasPrivateKey) throw new CertificateOperationException("server_binding_public_only_conflict");
                    _ = ServerCertificateMaterial.Require(existing, expectedCertificateId, now, privateKey: true);
                    requireOwner();
                    ServerCertificateMaterial.ProvePrivateKey(existing);
                    return new("unchanged", expectedCertificateId, false, false, true, false);
                }
            }
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        { throw new ServerCertificateBindingException("server_binding_precondition_failed", false, false, false, error); }

        var password = new char[64];
        var random = RandomNumberGenerator.GetBytes(32);
        const string hex = "0123456789abcdef";
        for (var index = 0; index < random.Length; index++)
        {
            password[index * 2] = hex[random[index] >> 4];
            password[index * 2 + 1] = hex[random[index] & 15];
        }
        CryptographicOperations.ZeroMemory(random);
        byte[]? pfx = null;
        var keyIssued = false;
        var storeIssued = false;
        try
        {
            // Export is RAM-only and has no native persistence effect. The
            // string-only explicit-PBE API requires one bounded call-only copy.
            pfx = certificate.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc,
                HashAlgorithmName.SHA256, 100_000), new string(password));
            if (pfx.Length is < 1 or > CertificateMaterial.MaximumCertificateBytes)
                throw new CertificateOperationException("certificate_output_bound");
            requireOwner();
            cancellationToken.ThrowIfCancellationRequested();
            // Once import starts, failure may leave a native key even if no
            // certificate is returned. Never erase uncertainty, retry or delete an unproven key.
            keyIssued = true;
            using (var persisted = X509CertificateLoader.LoadPkcs12(pfx, password.AsSpan(),
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet))
            {
                _ = ServerCertificateMaterial.Require(persisted, expectedCertificateId, now, privateKey: true);
                requireOwner();
                storeIssued = true;
                store.Add(persisted);
            }
            // Observe a fresh store context after disposing the importing object.
            store.Close();
            store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);
            using var observed = ReadExact(store, expectedCertificateId);
            if (observed is null) throw new CertificateOperationException("server_binding_not_observed");
            _ = ServerCertificateMaterial.Require(observed, expectedCertificateId, now, privateKey: true);
            requireOwner();
            ServerCertificateMaterial.ProvePrivateKey(observed);
            return new("bound", expectedCertificateId, true, true, true, false);
        }
        catch (Exception error) when ((keyIssued || storeIssued) &&
            error is not OutOfMemoryException and not StackOverflowException)
        {
            throw new ServerCertificateBindingException("server_binding_outcome_unknown", keyIssued, storeIssued, true, error);
        }
        catch (Exception error) when (!keyIssued && !storeIssued &&
            error is CryptographicException or IOException or UnauthorizedAccessException)
        { throw new ServerCertificateBindingException("server_binding_precondition_failed", false, false, false, error); }
        finally
        {
            Array.Clear(password);
            if (pfx is not null) CryptographicOperations.ZeroMemory(pfx);
        }
    }

    internal static X509Certificate2? ReadExact(X509Store store, string id)
    {
        var certificates = store.Certificates;
        X509Certificate2? selected = null;
        try
        {
            if (certificates.Count > 4096) throw new CertificateOperationException("certificate_store_bound");
            foreach (var certificate in certificates)
            {
                if (CertificateOperations.Inspect(certificate).CertificateId != id) continue;
                if (selected is not null) throw new CertificateOperationException("certificate_identity_ambiguous");
                selected = certificate;
            }
            return selected;
        }
        catch { selected = null; throw; }
        finally { foreach (var certificate in certificates) if (!ReferenceEquals(certificate, selected)) certificate.Dispose(); }
    }
}
