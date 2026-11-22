using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Pegasus.AgentOps.Certificates;

public sealed record CertificateSpec(string CommonName, DateTimeOffset NotBefore,
    DateTimeOffset NotAfter, int RsaBits = 3072);

public enum PublicCertificateFormat { Der, Pem }

public sealed record CertificateSummary(string CertificateId, string Subject, string Issuer,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool IsAuthority, bool HasPrivateKey);

public sealed class CertificateOperationException(string code, Exception? innerException = null) : Exception(code, innerException)
{
    public string Code { get; } = code;
}

/// <summary>
/// Cryptographic primitives only: creation and issuance never install trust or persist keys.
/// Store and exchange operations are separate explicit services.
/// Caller owns and must dispose returned private-key-bearing certificates.
/// </summary>
public sealed class CertificateOperations(TimeProvider? timeProvider = null)
{
    public const string Schema = "pegasus.pao.certificates/1";
    internal const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    internal const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public X509Certificate2 CreateAuthority(CertificateSpec spec)
    {
        ValidateSpec(spec, maximumDays: 3650);
        using var key = RSA.Create(spec.RsaBits);
        var request = NewRequest(spec, key);
        // This first authoring slice issues leaves only; it cannot mint intermediates.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(spec.NotBefore, spec.NotAfter);
    }

    public X509Certificate2 IssueCodeSigning(CertificateSpec spec, X509Certificate2 issuer) =>
        IssueLeaf(spec, issuer, CodeSigningOid, null);

    public X509Certificate2 IssueServer(CertificateSpec spec, X509Certificate2 issuer, ServerCertificateNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return IssueLeaf(spec, issuer, ServerAuthenticationOid, names);
    }

    /// <summary>
    /// Signs a complete DER PKCS#10 request (1..16384 bytes) using the admitted
    /// subject, validity and SANs. Only RSA 2048/3072/4096 with a SHA256/PKCS#1
    /// request signature is accepted. Requested subject, extensions and attributes
    /// confer no policy: only the signature-validated public key is carried forward.
    /// The returned leaf is public-only; no subject or issuer private key is copied.
    /// </summary>
    public X509Certificate2 IssueServerFromSigningRequest(CertificateSpec spec, X509Certificate2 issuer,
        ServerCertificateNames names, ReadOnlySpan<byte> signingRequest)
    {
        ArgumentNullException.ThrowIfNull(names);
        RequireLeafIssuer(spec, issuer);
        var supplied = ReadServerSigningRequest(signingRequest, spec.RsaBits);
        var request = new CertificateRequest(NewSubject(spec), supplied.PublicKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var issued = CreateLeaf(request, spec, issuer, ServerAuthenticationOid, names);
        try
        {
            if (issued.HasPrivateKey || !issued.PublicKey.ExportSubjectPublicKeyInfo().AsSpan()
                .SequenceEqual(supplied.PublicKey.ExportSubjectPublicKeyInfo()))
                throw Rejected("server_csr_result_invalid");
            return issued;
        }
        catch
        {
            issued.Dispose();
            throw;
        }
    }

    private X509Certificate2 IssueLeaf(CertificateSpec spec, X509Certificate2 issuer, string purpose, ServerCertificateNames? names)
    {
        RequireLeafIssuer(spec, issuer);
        using var key = RSA.Create(spec.RsaBits);
        var request = NewRequest(spec, key);
        using var issued = CreateLeaf(request, spec, issuer, purpose, names);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// Nonmutating preflight for an admitted leaf operation, including a machine
    /// CSR whose key has not yet been created. Issuance repeats the same checks.
    /// </summary>
    public void ValidateLeafIssuer(CertificateSpec spec, X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(issuer);
        RequireLeafIssuer(spec, issuer);
    }

    private void RequireLeafIssuer(CertificateSpec spec, X509Certificate2 issuer)
    {
        ValidateSpec(spec, maximumDays: 366);
        RequireAuthority(issuer);
        RequireCurrent(issuer);
        if (!issuer.HasPrivateKey) throw Rejected("issuer_private_key_required");
        if (spec.NotBefore < Utc(issuer.NotBefore) || spec.NotAfter > Utc(issuer.NotAfter))
            throw Rejected("issuer_validity_exceeded");
        using var issuerKey = issuer.GetRSAPrivateKey();
        RequireRsa(issuerKey);
    }

    private static X509Certificate2 CreateLeaf(CertificateRequest request, CertificateSpec spec,
        X509Certificate2 issuer, string purpose, ServerCertificateNames? names)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(names is null ? X509KeyUsageFlags.DigitalSignature :
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(purpose) }, true));
        names?.AddTo(request);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        // Positive, nonzero, unpredictable 128-bit serial; no shared serial state.
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        serial[^1] |= 1;
        return request.Create(issuer, spec.NotBefore, spec.NotAfter, serial);
    }

    private static CertificateRequest ReadServerSigningRequest(ReadOnlySpan<byte> encoded, int expectedRsaBits)
    {
        if (encoded.Length is < 1 or > 16 * 1024) throw Rejected("server_csr_bound");
        // Keep admission and signature verification on the same immutable
        // snapshot even if a caller still owns the input array.
        var snapshot = encoded.ToArray();
        try
        {
            // The toolkit deliberately supports one CSR signature suite. Checking
            // this envelope is separate from the loader's mandatory proof of
            // possession; the SHA256 argument to LoadSigningRequest alone does
            // not constrain the signature algorithm actually present in the CSR.
            var reader = new AsnReader(snapshot, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            _ = sequence.ReadEncodedValue(); // CertificationRequestInfo: loader validates it below.
            var algorithm = sequence.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != "1.2.840.113549.1.1.11")
                throw Rejected("server_csr_signature_algorithm_unsupported");
            if (algorithm.HasData) algorithm.ReadNull();
            algorithm.ThrowIfNotEmpty();
            _ = sequence.ReadBitString(out var unusedBits);
            if (unusedBits != 0) throw Rejected("server_csr_invalid");
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            // Default options verify the embedded signature and do not load any
            // requested extensions. Do not opt into either unsafe load option.
            var request = CertificateRequest.LoadSigningRequest(snapshot.AsSpan(), HashAlgorithmName.SHA256,
                out var consumed, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
            if (consumed != snapshot.Length) throw Rejected("server_csr_invalid");
            if (request.PublicKey.Oid.Value != "1.2.840.113549.1.1.1")
                throw Rejected("rsa_key_required");
            using var key = request.PublicKey.GetRSAPublicKey();
            RequireRsa(key);
            if (key?.KeySize != expectedRsaBits) throw Rejected("server_csr_key_size_mismatch");
            return request;
        }
        catch (Exception failure) when (failure is AsnContentException or CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new CertificateOperationException("server_csr_invalid", failure);
        }
    }

    public static CertificateSummary Inspect(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var basic = Extension(certificate, "2.5.29.19");
        var authority = basic is not null && new X509BasicConstraintsExtension(basic, basic.Critical).CertificateAuthority;
        return new(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)),
            certificate.Subject, certificate.Issuer, Utc(certificate.NotBefore), Utc(certificate.NotAfter),
            authority, certificate.HasPrivateKey);
    }

    /// <summary>Always public-only. Never returns a key, PKCS#12, or a secret-bearing format.</summary>
    public static byte[] ExportPublic(X509Certificate2 certificate, PublicCertificateFormat format)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var bytes = format switch
        {
            PublicCertificateFormat.Der => certificate.Export(X509ContentType.Cert),
            PublicCertificateFormat.Pem => Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()),
            _ => throw Rejected("public_format_unsupported")
        };
        if (bytes.Length > CertificateMaterial.MaximumCertificateBytes) throw Rejected("certificate_output_bound");
        return bytes;
    }

    private static CertificateRequest NewRequest(CertificateSpec spec, RSA key) =>
        new(NewSubject(spec), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    private static X500DistinguishedName NewSubject(CertificateSpec spec)
    {
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(spec.CommonName);
        return name.Build();
    }

    private static void ValidateSpec(CertificateSpec spec, int maximumDays)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.CommonName) || spec.CommonName.Length > 128 ||
            spec.CommonName != spec.CommonName.Trim() || spec.CommonName.Any(char.IsControl))
            throw Rejected("subject_invalid");
        if (spec.RsaBits is not (2048 or 3072 or 4096)) throw Rejected("key_algorithm_unsupported");
        // Whole-second UTC bounds match certificate encoding; reject silent rounding.
        if (spec.NotBefore.Offset != TimeSpan.Zero || spec.NotAfter.Offset != TimeSpan.Zero ||
            spec.NotBefore.Ticks % TimeSpan.TicksPerSecond != 0 || spec.NotAfter.Ticks % TimeSpan.TicksPerSecond != 0 ||
            spec.NotAfter <= spec.NotBefore || spec.NotAfter - spec.NotBefore > TimeSpan.FromDays(maximumDays))
            throw Rejected("validity_invalid");
    }

    private static void RequireAuthority(X509Certificate2 certificate)
    {
        var basic = Extension(certificate, "2.5.29.19");
        var usage = Extension(certificate, "2.5.29.15");
        if (basic is null || !new X509BasicConstraintsExtension(basic, basic.Critical).CertificateAuthority ||
            usage is null || (new X509KeyUsageExtension(usage, usage.Critical).KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0)
            throw Rejected("issuer_not_authority");
        using var key = certificate.GetRSAPublicKey();
        RequireRsa(key);
    }

    private void RequireCurrent(X509Certificate2 certificate)
    {
        var now = _clock.GetUtcNow();
        if (now < Utc(certificate.NotBefore) || now > Utc(certificate.NotAfter))
            throw Rejected("certificate_time_invalid");
    }

    private static void RequireRsa(RSA? key)
    {
        if (key is null || key.KeySize is not (2048 or 3072 or 4096)) throw Rejected("rsa_key_required");
    }

    private static X509Extension? Extension(X509Certificate2 certificate, string oid)
    {
        var matches = certificate.Extensions.Cast<X509Extension>().Where(item => item.Oid?.Value == oid).Take(2).ToArray();
        if (matches.Length > 1) throw Rejected("certificate_extension_duplicate");
        return matches.SingleOrDefault();
    }

    private static DateTimeOffset Utc(DateTime value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static CertificateOperationException Rejected(string code) => new(code);
}
