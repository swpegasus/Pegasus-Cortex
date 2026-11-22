using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pegasus.AgentOps.Certificates;

/// <summary>Opaque provider references only; neither field contains a key or password.</summary>
public sealed record CertificateKeyReference(string Reference, string ExpectedCertificateId);

public interface ICertificateKeyResolver
{
    /// <summary>Return an owned disposable certificate for exactly this reference, without UI or identity substitution.</summary>
    X509Certificate2 Resolve(CertificateKeyReference reference);
}

public interface ICertificateSecretResolver
{
    /// <summary>Resolve inside the admitted owner/provider. Never serialize the returned lease.</summary>
    CertificateSecret Resolve(string reference);
}

/// <summary>A short-lived owned password buffer, deliberately without public secret properties.</summary>
public sealed class CertificateSecret : IDisposable
{
    private char[]? _characters;
    public CertificateSecret(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 1024 || value.Contains('\0')) throw new CertificateOperationException("secret_invalid");
        _characters = value.ToArray();
    }
    internal ReadOnlySpan<char> Value => _characters ?? throw new ObjectDisposedException(nameof(CertificateSecret));
    public override string ToString() => "<protected certificate secret>";
    public void Dispose()
    {
        if (_characters is not { } characters) return;
        Array.Clear(characters);
        _characters = null;
    }
}

internal static class CertificateMaterial
{
    internal const int MaximumCertificateBytes = 1024 * 1024;
    internal static string Id(X509Certificate2 certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
    internal static void RequireId(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new CertificateOperationException("certificate_id_invalid");
    }
    internal static void RequireReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new CertificateOperationException("credential_reference_invalid");
    }
    internal static void RequireKeyReference(CertificateKeyReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RequireReference(reference.Reference);
        RequireId(reference.ExpectedCertificateId);
    }
    internal static void RequireIdentity(X509Certificate2 certificate, string expected)
    {
        RequireId(expected);
        if (Id(certificate) != expected) throw new CertificateOperationException("certificate_identity_mismatch");
    }
    internal static void RequireSigning(X509Certificate2 certificate, DateTimeOffset now, bool privateKey)
    {
        if (certificate.Extensions.Cast<X509Extension>().GroupBy(value => value.Oid?.Value, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new CertificateOperationException("certificate_extension_duplicate");
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (basic?.CertificateAuthority == true || usage is null || (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0 ||
            eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CertificateOperations.CodeSigningOid))
            throw new CertificateOperationException("code_signing_purpose_required");
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            throw new CertificateOperationException("certificate_time_invalid");
        if (privateKey && !certificate.HasPrivateKey) throw new CertificateOperationException("signer_private_key_required");
        using var key = certificate.GetRSAPublicKey();
        if (key is null || key.KeySize is not (2048 or 3072 or 4096)) throw new CertificateOperationException("rsa_key_required");
    }
    internal static byte[] ReadBounded(Stream input, int maximum)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new CertificateOperationException("input_unreadable");
        if (maximum is < 1 or > MaximumCertificateBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        // A single fixed allocation avoids abandoned MemoryStream growth arrays containing rejected input.
        var buffer = new byte[checked(maximum + 1)];
        try
        {
            var length = 0;
            int count;
            while ((count = input.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += count;
                if (length > maximum) throw new CertificateOperationException("input_too_large");
            }
            if (length == 0) throw new CertificateOperationException("input_empty");
            return buffer.AsSpan(0, length).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
