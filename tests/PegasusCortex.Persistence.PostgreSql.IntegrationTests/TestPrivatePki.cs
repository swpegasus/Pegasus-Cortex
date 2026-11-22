using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

internal enum TestCrlState
{
    Fresh,
    Stale,
    InvalidSignature,
    Revoked
}

/// <summary>
/// Generates ephemeral public PKI evidence for the offline harness. Private keys remain
/// memory-only and are disposed before the returned fixture is used.
/// </summary>
internal sealed class TestPrivatePki : IDisposable
{
    private const string RootCertificateFile = "root-ca.pem";
    private const string ServerCertificateFile = "server.pem";
    private const string CrlFile = "server.crl";
    private const string ManifestFile = "revocation.json";
    private int _disposed;

    private TestPrivatePki(
        string rootPath,
        Uri distributionPointUri)
    {
        RootPath = rootPath;
        DistributionPointUri = distributionPointUri;
    }

    internal string RootPath { get; }
    internal string ManifestFileName => ManifestFile;
    internal string RootCertificatePath => Path.Combine(RootPath, RootCertificateFile);
    internal string CrlPath => Path.Combine(RootPath, CrlFile);
    internal Uri DistributionPointUri { get; }

    internal static TestPrivatePki Create(
        TestCrlState crlState = TestCrlState.Fresh,
        bool sanMismatch = false,
        bool chainMismatch = false)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ctxpki_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        SetPrivateDirectory(rootPath);

        var port = ReserveEphemeralPort();
        var distributionPoint = new Uri(
            $"http://127.0.0.1:{port}/{CrlFile}",
            UriKind.Absolute);
        try
        {
            using var rootKey = RSA.Create(2048);
            using var rootCertificate = CreateCertificateAuthority(
                rootKey,
                "CN=Pegasus Cortex Test Root");

            using var alternateKey = chainMismatch ? RSA.Create(2048) : null;
            using var alternateCertificate = chainMismatch
                ? CreateCertificateAuthority(
                    alternateKey!,
                    "CN=Pegasus Cortex Alternate Test Root")
                : null;
            using var serverKey = RSA.Create(2048);
            var serverSerial = NewSerial();
            using var serverCertificate = CreateServerCertificate(
                serverKey,
                chainMismatch ? alternateCertificate! : rootCertificate,
                serverSerial,
                distributionPoint,
                sanMismatch);

            var now = DateTimeOffset.UtcNow;
            var thisUpdate = crlState == TestCrlState.Stale
                ? now.AddDays(-2)
                : now.AddMinutes(-2);
            var nextUpdate = crlState == TestCrlState.Stale
                ? now.AddMinutes(-10)
                : now.AddDays(1);
            var crl = CreateCrl(
                rootCertificate,
                rootKey,
                thisUpdate,
                nextUpdate,
                crlState == TestCrlState.Revoked ? [serverSerial] : []);
            if (crlState == TestCrlState.InvalidSignature)
                crl[^1] ^= 0x01;

            WritePrivatePublicFile(
                Path.Combine(rootPath, RootCertificateFile),
                rootCertificate.ExportCertificatePem());
            WritePrivatePublicFile(
                Path.Combine(rootPath, ServerCertificateFile),
                serverCertificate.ExportCertificatePem());
            WritePrivatePublicFile(
                Path.Combine(rootPath, CrlFile),
                PemEncoding.WriteString("X509 CRL", crl));

            var manifest = new
            {
                schemaVersion = 1,
                profile = FixtureRevocationEvidence.ProfileV1,
                rootCertificateFile = RootCertificateFile,
                serverCertificateFile = ServerCertificateFile,
                crlFile = CrlFile,
                distributionPointUri = distributionPoint.AbsoluteUri,
                rootCertificateSha256 = HashFile(Path.Combine(rootPath, RootCertificateFile)),
                serverCertificateSha256 = HashFile(Path.Combine(rootPath, ServerCertificateFile)),
                crlSha256 = HashFile(Path.Combine(rootPath, CrlFile)),
                maximumResponseBytes = FixtureRevocationEvidence.MaximumCrlBytes,
                maximumRequests = 32,
                requestTimeoutMilliseconds = 2000
            };
            WritePrivatePublicFile(
                Path.Combine(rootPath, ManifestFile),
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true }) +
                Environment.NewLine);

            return new TestPrivatePki(rootPath, distributionPoint);
        }
        catch
        {
            DeleteOwnedRoot(rootPath);
            throw;
        }
    }

    internal void AddEnvironment(IDictionary<string, string?> values)
    {
        values[FixtureRevocationEvidence.EvidenceRootVariable] = RootPath;
        values[FixtureRevocationEvidence.ManifestFileVariable] = ManifestFile;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        DeleteOwnedRoot(RootPath);
    }

    private static X509Certificate2 CreateCertificateAuthority(
        RSA key,
        string subject)
    {
        var request = new CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(7));
    }

    private static X509Certificate2 CreateServerCertificate(
        RSA key,
        X509Certificate2 issuer,
        byte[] serial,
        Uri distributionPoint,
        bool sanMismatch)
    {
        var request = new CertificateRequest(
            "CN=Pegasus Cortex PostgreSQL Fixture",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        var enhancedUsage = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1", "Server Authentication")
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(enhancedUsage, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(sanMismatch ? "127.0.0.2" : "127.0.0.1"));
        if (!sanMismatch)
            san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build(true));
        request.CertificateExtensions.Add(CreateDistributionPointExtension(distributionPoint));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(2),
            serial);
    }

    private static X509Extension CreateDistributionPointExtension(Uri endpoint)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    {
                        writer.WriteCharacterString(
                            UniversalTagNumber.IA5String,
                            endpoint.AbsoluteUri,
                            new Asn1Tag(TagClass.ContextSpecific, 6));
                    }
                }
            }
        }
        return new X509Extension("2.5.29.31", writer.Encode(), false);
    }

    private static byte[] CreateCrl(
        X509Certificate2 issuer,
        RSA issuerKey,
        DateTimeOffset thisUpdate,
        DateTimeOffset nextUpdate,
        IReadOnlyList<byte[]> revokedSerials)
    {
        var tbsWriter = new AsnWriter(AsnEncodingRules.DER);
        using (tbsWriter.PushSequence())
        {
            tbsWriter.WriteInteger(1);
            WriteSha256RsaAlgorithm(tbsWriter);
            tbsWriter.WriteEncodedValue(issuer.SubjectName.RawData);
            tbsWriter.WriteGeneralizedTime(thisUpdate, omitFractionalSeconds: true);
            tbsWriter.WriteGeneralizedTime(nextUpdate, omitFractionalSeconds: true);
            if (revokedSerials.Count > 0)
            {
                using (tbsWriter.PushSequence())
                {
                    foreach (var serial in revokedSerials)
                    {
                        using (tbsWriter.PushSequence())
                        {
                            tbsWriter.WriteIntegerUnsigned(serial);
                            tbsWriter.WriteGeneralizedTime(
                                thisUpdate.AddMinutes(1),
                                omitFractionalSeconds: true);
                        }
                    }
                }
            }
        }
        var tbs = tbsWriter.Encode();
        var signature = issuerKey.SignData(
            tbs,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(tbs);
            WriteSha256RsaAlgorithm(writer);
            writer.WriteBitString(signature);
        }
        return writer.Encode();
    }

    private static void WriteSha256RsaAlgorithm(AsnWriter writer)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.2.840.113549.1.1.11");
            writer.WriteNull();
        }
    }

    private static byte[] NewSerial()
    {
        // Each generated server has a unique ephemeral CA, so a fixed positive serial
        // removes an unnecessary random input without creating a within-issuer collision.
        return
        [
            0x01, 0x23, 0x45, 0x67,
            0x10, 0x32, 0x54, 0x76,
            0x11, 0x22, 0x33, 0x44,
            0x55, 0x66, 0x77, 0x01
        ];
    }

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0)
        {
            ExclusiveAddressUse = true
        };
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WritePrivatePublicFile(string path, string value)
    {
        File.WriteAllText(path, value, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void DeleteOwnedRoot(string path)
    {
        if (!Directory.Exists(path))
            return;
        var fullPath = Path.GetFullPath(path);
        var expectedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ctxpki_");
        if (!fullPath.StartsWith(
                expectedPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FixtureAssertionException(
                "Refused to clean an unowned private-PKI test root.");
        }
        Directory.Delete(fullPath, recursive: true);
    }
}
