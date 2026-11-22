using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

internal sealed record FixtureRevocationEvidenceSummary(
    string Profile,
    string Transport,
    int Port,
    int MaximumRequests,
    int MaximumResponseBytes,
    DateTimeOffset NextUpdateUtc)
{
    public override string ToString() =>
        $"profile={Profile}; transport={Transport}; port={Port}; " +
        $"maximumRequests={MaximumRequests}; maximumResponseBytes={MaximumResponseBytes}; " +
        $"nextUpdateUtc={NextUpdateUtc:O}";
}

/// <summary>
/// Validates the public, task-local private-PKI evidence used by the native PostgreSQL fixture.
/// The product connection still performs VerifyFull and platform revocation checking; this
/// contract only proves that a bounded, signed, fresh CRL can be served from the certificate's
/// literal loopback distribution point.
/// </summary>
internal sealed class FixtureRevocationEvidence
{
    public const string EvidenceRootVariable = "PEGASUS_CORTEX_PG_REVOCATION_EVIDENCE_ROOT";
    public const string ManifestFileVariable = "PEGASUS_CORTEX_PG_REVOCATION_MANIFEST_FILE";
    public const string ProfileV1 = "pegasus.cortex.postgresql.private-ca-crl/1";
    public const int MaximumManifestBytes = 32 * 1024;
    public const int MaximumCertificateBytes = 1024 * 1024;
    public const int MaximumCrlBytes = 1024 * 1024;
    public const int MaximumLeafCharacters = 64;
    public const int MaximumUtf8SegmentBytes = 255;
    public const int MaximumAbsolutePathCharacters = 240;
    public const int MinimumResponderPort = 1024;
    public const int MaximumResponderRequests = 64;
    public const int MinimumRequestTimeoutMilliseconds = 100;
    public const int MaximumRequestTimeoutMilliseconds = 5000;

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumCrlLifetime = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private FixtureRevocationEvidence(
        Uri distributionPointUri,
        byte[] crlBytes,
        int maximumRequests,
        int maximumResponseBytes,
        TimeSpan requestTimeout,
        DateTimeOffset nextUpdateUtc)
    {
        DistributionPointUri = distributionPointUri;
        CrlBytes = crlBytes;
        MaximumRequests = maximumRequests;
        MaximumResponseBytes = maximumResponseBytes;
        RequestTimeout = requestTimeout;
        NextUpdateUtc = nextUpdateUtc;
    }

    internal Uri DistributionPointUri { get; }
    internal byte[] CrlBytes { get; }
    internal int MaximumRequests { get; }
    internal int MaximumResponseBytes { get; }
    internal TimeSpan RequestTimeout { get; }
    internal DateTimeOffset NextUpdateUtc { get; }

    internal FixtureRevocationEvidenceSummary GetSanitizedSummary() =>
        new(
            ProfileV1,
            "loopback-http-signed-crl",
            DistributionPointUri.Port,
            MaximumRequests,
            MaximumResponseBytes,
            NextUpdateUtc);

    internal static FixtureRevocationEvidence Load(
        IReadOnlyDictionary<string, string?> values,
        string expectedRootCertificatePath,
        IReadOnlyCollection<IPAddress> databaseAddresses,
        IReadOnlyCollection<int> databasePorts,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRootCertificatePath);
        ArgumentNullException.ThrowIfNull(databaseAddresses);
        ArgumentNullException.ThrowIfNull(databasePorts);

        var evidenceRoot = ValidateRoot(
            Required(values, EvidenceRootVariable, "fixture_revocation_evidence_root_missing"));
        var manifestLeaf = ValidateLeaf(
            Required(values, ManifestFileVariable, "fixture_revocation_manifest_file_missing"),
            ".json",
            "fixture_revocation_manifest_file_invalid");
        var manifestPath = ResolveLeaf(
            evidenceRoot,
            manifestLeaf,
            "fixture_revocation_manifest_file_invalid");
        var manifestBytes = ReadPrivateFile(
            manifestPath,
            MaximumManifestBytes,
            "fixture_revocation_manifest_missing",
            "fixture_revocation_manifest_unsafe",
            "fixture_revocation_manifest_size_invalid");

        PrivatePkiRevocationManifestV1 manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PrivatePkiRevocationManifestV1>(
                    manifestBytes,
                    JsonOptions) ??
                throw new JsonException("The manifest was null.");
        }
        catch (JsonException exception)
        {
            throw Failure("fixture_revocation_manifest_invalid", exception);
        }

        ValidateManifestShape(manifest);
        var endpoint = ValidateEndpoint(manifest.DistributionPointUri, manifest.CrlFile);
        if (databasePorts.Contains(endpoint.Port))
            throw Failure("fixture_revocation_endpoint_conflicts_with_postgresql");

        var rootCertificatePath = ResolveManifestFile(
            evidenceRoot,
            manifest.RootCertificateFile,
            ".pem");
        if (!PathsEqual(rootCertificatePath, expectedRootCertificatePath))
            throw Failure("fixture_revocation_root_certificate_mismatch");
        var serverCertificatePath = ResolveManifestFile(
            evidenceRoot,
            manifest.ServerCertificateFile,
            ".pem");
        var crlPath = ResolveManifestFile(
            evidenceRoot,
            manifest.CrlFile,
            ".crl");

        var rootCertificateBytes = ReadPrivateFile(
            rootCertificatePath,
            MaximumCertificateBytes,
            "fixture_revocation_root_certificate_missing",
            "fixture_revocation_root_certificate_unsafe",
            "fixture_revocation_root_certificate_size_invalid");
        var serverCertificateBytes = ReadPrivateFile(
            serverCertificatePath,
            MaximumCertificateBytes,
            "fixture_revocation_server_certificate_missing",
            "fixture_revocation_server_certificate_unsafe",
            "fixture_revocation_server_certificate_size_invalid");
        var crlFileBytes = ReadPrivateFile(
            crlPath,
            manifest.MaximumResponseBytes,
            "fixture_revocation_crl_missing",
            "fixture_revocation_crl_unsafe",
            "fixture_revocation_crl_size_invalid");

        RequireHash(rootCertificateBytes, manifest.RootCertificateSha256);
        RequireHash(serverCertificateBytes, manifest.ServerCertificateSha256);
        RequireHash(crlFileBytes, manifest.CrlSha256);

        using var rootCertificate = ReadPublicCertificate(
            rootCertificateBytes,
            "fixture_revocation_root_certificate_invalid");
        using var serverCertificate = ReadPublicCertificate(
            serverCertificateBytes,
            "fixture_revocation_server_certificate_invalid");
        ValidateRootCertificate(rootCertificate);
        ValidateServerCertificate(
            serverCertificate,
            rootCertificate,
            databaseAddresses,
            endpoint,
            now ?? DateTimeOffset.UtcNow);

        var crlBytes = ReadCrlDer(crlFileBytes);
        var crl = ParseAndVerifyCrl(
            crlBytes,
            rootCertificate,
            serverCertificate,
            now ?? DateTimeOffset.UtcNow);

        return new FixtureRevocationEvidence(
            endpoint,
            crlBytes,
            manifest.MaximumRequests,
            manifest.MaximumResponseBytes,
            TimeSpan.FromMilliseconds(manifest.RequestTimeoutMilliseconds),
            crl.NextUpdateUtc);
    }

    private static void ValidateManifestShape(PrivatePkiRevocationManifestV1 manifest)
    {
        if (manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.Profile, ProfileV1, StringComparison.Ordinal))
        {
            throw Failure("fixture_revocation_manifest_version_unsupported");
        }
        ValidateLeaf(
            manifest.RootCertificateFile,
            ".pem",
            "fixture_revocation_manifest_file_reference_invalid");
        ValidateLeaf(
            manifest.ServerCertificateFile,
            ".pem",
            "fixture_revocation_manifest_file_reference_invalid");
        ValidateLeaf(
            manifest.CrlFile,
            ".crl",
            "fixture_revocation_manifest_file_reference_invalid");
        ValidateHash(manifest.RootCertificateSha256);
        ValidateHash(manifest.ServerCertificateSha256);
        ValidateHash(manifest.CrlSha256);
        if (manifest.MaximumResponseBytes is < 1 or > MaximumCrlBytes)
            throw Failure("fixture_revocation_manifest_response_bound_invalid");
        if (manifest.MaximumRequests is < 1 or > MaximumResponderRequests)
            throw Failure("fixture_revocation_manifest_request_bound_invalid");
        if (manifest.RequestTimeoutMilliseconds is
            < MinimumRequestTimeoutMilliseconds or > MaximumRequestTimeoutMilliseconds)
        {
            throw Failure("fixture_revocation_manifest_timeout_invalid");
        }
    }

    private static Uri ValidateEndpoint(string? value, string crlFile)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            endpoint.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            !IPAddress.TryParse(endpoint.Host, out var address) ||
            !IPAddress.IsLoopback(address) ||
            endpoint.Port is < MinimumResponderPort or > 65535 ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw Failure("fixture_revocation_endpoint_invalid");
        }

        var escapedPath = endpoint.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (!string.Equals(
                Uri.UnescapeDataString(escapedPath),
                crlFile,
                StringComparison.Ordinal))
        {
            throw Failure("fixture_revocation_endpoint_path_mismatch");
        }
        return endpoint;
    }

    private static void ValidateRootCertificate(X509Certificate2 rootCertificate)
    {
        if (rootCertificate.HasPrivateKey)
            throw Failure("fixture_revocation_private_key_prohibited");
        var constraints = rootCertificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        var usage = rootCertificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .SingleOrDefault();
        if (constraints is null ||
            !constraints.CertificateAuthority ||
            usage is null ||
            (usage.KeyUsages & (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign)) !=
                (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign))
        {
            throw Failure("fixture_revocation_root_certificate_not_qualified");
        }
    }

    private static void ValidateServerCertificate(
        X509Certificate2 serverCertificate,
        X509Certificate2 rootCertificate,
        IReadOnlyCollection<IPAddress> databaseAddresses,
        Uri expectedDistributionPoint,
        DateTimeOffset now)
    {
        if (serverCertificate.HasPrivateKey)
            throw Failure("fixture_revocation_private_key_prohibited");

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(rootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (!chain.Build(serverCertificate) ||
            chain.ChainElements.Count < 2 ||
            !string.Equals(
                chain.ChainElements[^1].Certificate.Thumbprint,
                rootCertificate.Thumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("fixture_revocation_server_certificate_chain_invalid");
        }

        var sanExtension = serverCertificate.Extensions["2.5.29.17"];
        if (sanExtension is null)
            throw Failure("fixture_revocation_server_certificate_san_missing");
        try
        {
            var san = new X509SubjectAlternativeNameExtension(
                sanExtension.RawData,
                sanExtension.Critical);
            var addresses = san.EnumerateIPAddresses().ToArray();
            if (databaseAddresses.Count == 0 ||
                databaseAddresses.Any(expected => !addresses.Contains(expected)))
            {
                throw Failure("fixture_revocation_server_certificate_san_mismatch");
            }
        }
        catch (CryptographicException exception)
        {
            throw Failure("fixture_revocation_server_certificate_san_invalid", exception);
        }

        var distributionPoints = ReadCrlDistributionPoints(serverCertificate);
        if (distributionPoints.Count != 1 ||
            !string.Equals(
                distributionPoints[0].AbsoluteUri,
                expectedDistributionPoint.AbsoluteUri,
                StringComparison.Ordinal))
        {
            throw Failure("fixture_revocation_distribution_point_mismatch");
        }
    }

    private static IReadOnlyList<Uri> ReadCrlDistributionPoints(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions["2.5.29.31"] ??
            throw Failure("fixture_revocation_distribution_point_missing");
        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var points = reader.ReadSequence();
            var values = new List<Uri>();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                if (!point.HasData ||
                    !point.PeekTag().HasSameClassAndValue(
                        new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    throw new CryptographicException("Distribution point name is absent.");
                }
                var distributionPoint = point.ReadSequence(
                    new Asn1Tag(TagClass.ContextSpecific, 0, true));
                var fullName = distributionPoint.ReadSequence(
                    new Asn1Tag(TagClass.ContextSpecific, 0, true));
                while (fullName.HasData)
                {
                    var tag = fullName.PeekTag();
                    if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                    {
                        var uri = fullName.ReadCharacterString(
                            UniversalTagNumber.IA5String,
                            new Asn1Tag(TagClass.ContextSpecific, 6));
                        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                            throw new CryptographicException("Distribution point URI is invalid.");
                        values.Add(parsed);
                    }
                    else
                    {
                        fullName.ReadEncodedValue();
                    }
                }
                distributionPoint.ThrowIfNotEmpty();
                while (point.HasData)
                    point.ReadEncodedValue();
            }
            reader.ThrowIfNotEmpty();
            return values;
        }
        catch (AsnContentException exception)
        {
            throw Failure("fixture_revocation_distribution_point_invalid", exception);
        }
    }

    private static ParsedCrl ParseAndVerifyCrl(
        byte[] crlBytes,
        X509Certificate2 rootCertificate,
        X509Certificate2 serverCertificate,
        DateTimeOffset now)
    {
        try
        {
            var reader = new AsnReader(crlBytes, AsnEncodingRules.DER);
            var certificateList = reader.ReadSequence();
            var tbsEncoded = certificateList.PeekEncodedValue().ToArray();
            var tbs = certificateList.ReadSequence();
            if (tbs.HasData &&
                tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
            {
                _ = tbs.ReadInteger();
            }

            var innerAlgorithm = ReadAlgorithmIdentifier(tbs);
            var issuer = tbs.ReadEncodedValue().ToArray();
            var thisUpdate = ReadTime(tbs);
            var nextUpdate = ReadOptionalTime(tbs) ??
                throw new CryptographicException("CRL nextUpdate is required.");
            var revokedSerials = new List<byte[]>();
            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var revoked = tbs.ReadSequence();
                while (revoked.HasData)
                {
                    var entry = revoked.ReadSequence();
                    revokedSerials.Add(NormalizeSerial(entry.ReadIntegerBytes().Span));
                    _ = ReadTime(entry);
                    while (entry.HasData)
                        entry.ReadEncodedValue();
                }
            }
            while (tbs.HasData)
                tbs.ReadEncodedValue();

            var outerAlgorithm = ReadAlgorithmIdentifier(certificateList);
            var signature = certificateList.ReadBitString(out var unusedBitCount);
            certificateList.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            if (unusedBitCount != 0 ||
                !string.Equals(innerAlgorithm, outerAlgorithm, StringComparison.Ordinal))
            {
                throw new CryptographicException("CRL signature algorithm is inconsistent.");
            }
            if (!issuer.AsSpan().SequenceEqual(rootCertificate.SubjectName.RawData))
                throw Failure("fixture_revocation_crl_issuer_mismatch");
            if (!VerifySignature(rootCertificate, innerAlgorithm, tbsEncoded, signature))
                throw Failure("fixture_revocation_crl_signature_invalid");
            if (thisUpdate > now + ClockSkew)
                throw Failure("fixture_revocation_crl_not_yet_valid");
            if (nextUpdate <= now - ClockSkew)
                throw Failure("fixture_revocation_crl_stale");
            if (nextUpdate <= thisUpdate || nextUpdate - thisUpdate > MaximumCrlLifetime)
                throw Failure("fixture_revocation_crl_validity_invalid");

            var serverSerial = NormalizeSerial(serverCertificate.GetSerialNumber().Reverse().ToArray());
            if (revokedSerials.Any(value => value.AsSpan().SequenceEqual(serverSerial)))
                throw Failure("fixture_revocation_server_certificate_revoked");
            return new ParsedCrl(thisUpdate, nextUpdate);
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is AsnContentException or CryptographicException or InvalidOperationException)
        {
            throw Failure("fixture_revocation_crl_invalid", exception);
        }
    }

    private static string ReadAlgorithmIdentifier(AsnReader parent)
    {
        var algorithm = parent.ReadSequence();
        var oid = algorithm.ReadObjectIdentifier();
        while (algorithm.HasData)
            algorithm.ReadEncodedValue();
        return oid;
    }

    private static DateTimeOffset ReadTime(AsnReader reader)
    {
        var tag = reader.PeekTag();
        if (tag.HasSameClassAndValue(Asn1Tag.UtcTime))
            return reader.ReadUtcTime();
        if (tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime))
            return reader.ReadGeneralizedTime();
        throw new CryptographicException("Expected an ASN.1 time value.");
    }

    private static DateTimeOffset? ReadOptionalTime(AsnReader reader)
    {
        if (!reader.HasData)
            return null;
        var tag = reader.PeekTag();
        return tag.HasSameClassAndValue(Asn1Tag.UtcTime) ||
            tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime)
            ? ReadTime(reader)
            : null;
    }

    private static bool VerifySignature(
        X509Certificate2 rootCertificate,
        string algorithmOid,
        byte[] data,
        byte[] signature)
    {
        var hashAlgorithm = algorithmOid switch
        {
            "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => throw Failure("fixture_revocation_crl_signature_algorithm_unsupported")
        };

        if (algorithmOid.StartsWith("1.2.840.113549.1.1.", StringComparison.Ordinal))
        {
            using var rsa = rootCertificate.GetRSAPublicKey() ??
                throw Failure("fixture_revocation_crl_signature_key_mismatch");
            return rsa.VerifyData(data, signature, hashAlgorithm, RSASignaturePadding.Pkcs1);
        }

        using var ecdsa = rootCertificate.GetECDsaPublicKey() ??
            throw Failure("fixture_revocation_crl_signature_key_mismatch");
        return ecdsa.VerifyData(
            data,
            signature,
            hashAlgorithm,
            DSASignatureFormat.Rfc3279DerSequence);
    }

    private static byte[] ReadCrlDer(byte[] fileBytes)
    {
        var text = Encoding.ASCII.GetString(fileBytes);
        if (!PemEncoding.TryFind(text, out var fields) ||
            !text.AsSpan(fields.Label).SequenceEqual("X509 CRL"))
        {
            throw Failure("fixture_revocation_crl_invalid");
        }
        try
        {
            return Convert.FromBase64String(text[fields.Base64Data]);
        }
        catch (FormatException exception)
        {
            throw Failure("fixture_revocation_crl_invalid", exception);
        }
    }

    private static X509Certificate2 ReadPublicCertificate(byte[] bytes, string failureCode)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw Failure("fixture_revocation_private_key_prohibited");
        try
        {
            var certificate = X509Certificate2.CreateFromPem(text);
            if (certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw Failure("fixture_revocation_private_key_prohibited");
            }
            return certificate;
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw Failure(failureCode, exception);
        }
    }

    private static string ValidateRoot(string value)
    {
        if (!Path.IsPathFullyQualified(value))
            throw Failure("fixture_revocation_evidence_root_invalid");
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure("fixture_revocation_evidence_root_invalid", exception);
        }
        if (fullPath.Length > MaximumAbsolutePathCharacters ||
            !PathsEqual(fullPath, Path.TrimEndingDirectorySeparator(value)) ||
            !Directory.Exists(fullPath))
        {
            throw Failure("fixture_revocation_evidence_root_invalid");
        }
        EnsureNoLinks(fullPath, "fixture_revocation_evidence_root_unsafe");
        if (!OperatingSystem.IsWindows() &&
            File.GetUnixFileMode(fullPath) !=
                (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
        {
            throw Failure("fixture_revocation_evidence_root_not_private");
        }
        return fullPath;
    }

    private static string ResolveManifestFile(string root, string value, string requiredExtension) =>
        ResolveLeaf(
            root,
            ValidateLeaf(
                value,
                requiredExtension,
                "fixture_revocation_manifest_file_reference_invalid"),
            "fixture_revocation_manifest_file_reference_invalid");

    private static string ResolveLeaf(string root, string leaf, string failureCode)
    {
        var path = Path.GetFullPath(Path.Combine(root, leaf));
        if (path.Length > MaximumAbsolutePathCharacters ||
            !PathsEqual(Path.GetDirectoryName(path)!, root))
        {
            throw Failure(failureCode);
        }
        return path;
    }

    private static string ValidateLeaf(string value, string requiredExtension, string failureCode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumLeafCharacters ||
            Encoding.UTF8.GetByteCount(value) > MaximumUtf8SegmentBytes ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            !value.EndsWith(requiredExtension, StringComparison.Ordinal) ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')) ||
            value.EndsWith(' ') ||
            value.EndsWith('.') ||
            IsReservedName(Path.GetFileNameWithoutExtension(value)))
        {
            throw Failure(failureCode);
        }
        return value;
    }

    private static byte[] ReadPrivateFile(
        string path,
        int maximumBytes,
        string missingCode,
        string unsafeCode,
        string sizeCode)
    {
        if (!File.Exists(path))
            throw Failure(missingCode);
        EnsureNoLinks(path, unsafeCode);
        var item = new FileInfo(path);
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw Failure(unsafeCode);
        if (!OperatingSystem.IsWindows() &&
            File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw Failure(unsafeCode);
        }
        if (item.Length is < 1 || item.Length > maximumBytes)
            throw Failure(sizeCode);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            var bytes = new byte[item.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw Failure(sizeCode);
            return bytes;
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(unsafeCode, exception);
        }
    }

    private static void EnsureNoLinks(string path, string failureCode)
    {
        try
        {
            FileSystemInfo? current = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    current.LinkTarget is not null)
                {
                    throw Failure(failureCode);
                }
                current = current switch
                {
                    DirectoryInfo directory => directory.Parent,
                    FileInfo file => file.Directory,
                    _ => null
                };
            }
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(failureCode, exception);
        }
    }

    private static void RequireHash(byte[] bytes, string expected)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw Failure("fixture_revocation_evidence_hash_mismatch");
    }

    private static void ValidateHash(string value)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure("fixture_revocation_manifest_hash_invalid");
        }
    }

    private static byte[] NormalizeSerial(ReadOnlySpan<byte> value)
    {
        var offset = 0;
        while (offset < value.Length - 1 && value[offset] == 0)
            offset++;
        return value[offset..].ToArray();
    }

    private static bool IsReservedName(string value)
    {
        var upper = value.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL" ||
            (upper.Length == 4 &&
             (upper.StartsWith("COM", StringComparison.Ordinal) ||
              upper.StartsWith("LPT", StringComparison.Ordinal)) &&
             upper[3] is >= '1' and <= '9');
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Required(
        IReadOnlyDictionary<string, string?> values,
        string name,
        string failureCode)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw Failure(failureCode);
        return value;
    }

    private static FixtureConfigurationException Failure(
        string code,
        Exception? cause = null) =>
        new(code, cause);

    private sealed record ParsedCrl(
        DateTimeOffset ThisUpdateUtc,
        DateTimeOffset NextUpdateUtc);

    private sealed record PrivatePkiRevocationManifestV1(
        int SchemaVersion,
        string Profile,
        string RootCertificateFile,
        string ServerCertificateFile,
        string CrlFile,
        string DistributionPointUri,
        string RootCertificateSha256,
        string ServerCertificateSha256,
        string CrlSha256,
        int MaximumResponseBytes,
        int MaximumRequests,
        int RequestTimeoutMilliseconds);
}
