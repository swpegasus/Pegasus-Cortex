using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexVerifiedPackageFileV1(string RelativePath, long Bytes, string Sha256);

public sealed record CortexVerifiedProductPackageV1(
    string Root,
    string ReleaseVersion,
    string CatalogPath,
    string ManifestPath,
    string SignaturePath,
    string PublisherThumbprint,
    string RootThumbprint,
    IReadOnlyList<CortexVerifiedPackageFileV1> Files);

internal sealed record CortexVerifiedInstalledServiceV1(
    CortexProductCatalogV1 Catalog,
    string Executable);

public static class CortexSignedProductPackageVerifier
{
    private const string PackageSchema = "pegasus.cortex.local-operational-package/1.2";
    private const string SignatureSchema = "pegasus.package-signature/1.0";
    private const string TrustSchema = "pegasus.public-trust-bundle/1.0";
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    private const int TrustSuccess = 0;
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const int TrustEBadDigest = unchecked((int)0x80096010);

    public static CortexVerifiedProductPackageV1 Verify(string packageRoot) =>
        VerifyCore(packageRoot, installedServiceOnly: false);

    internal static CortexVerifiedInstalledServiceV1 VerifyInstalledService(CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinDeclaredRoots();
        CortexPrivilegedCodePath.RequireProtectedAncestors(layout.InstallRoot);
        CortexProductStateProvisioner.RequireInstallReceiptBoundary(layout);
        // Admission of the privileged image must not enumerate the user's data,
        // inspect databases, or treat an installed tree as an unpacked package.
        var admitted = VerifyCore(layout.InstallRoot, installedServiceOnly: true);
        var signed = CortexProductCatalogV1.ReadExact(admitted.CatalogPath);
        var selected = CortexProductCatalogV1.ReadExact(layout.ActiveCatalogPath);
        if (selected.ReleaseVersion != signed.ReleaseVersion ||
            selected.Roles.Single(role => role.Role == "service") !=
                signed.Roles.Single(role => role.Role == "service"))
            throw new InvalidDataException("The selected service does not match the signed installed product.");
        return new(signed, CortexWindowsProductRegistration.ResolveRole(layout, signed, "service"));
    }

    private static CortexVerifiedProductPackageV1 VerifyCore(string packageRoot, bool installedServiceOnly)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Signed Cortex package admission requires Windows.");
        var root = RequireOrdinaryRoot(packageRoot);
        if (!installedServiceOnly)
        {
            RejectReparseTree(root);
            RejectPrivateKeyShapedContent(root);
        }

        var manifestPath = RequireOrdinaryFile(root, "package-manifest.json");
        var signaturePath = RequireOrdinaryFile(root, "package-manifest.p7s");
        var trustManifestPath = RequireOrdinaryFile(root, "trust/trust-bundle.json");
        if (installedServiceOnly)
            foreach (var path in new[] { manifestPath, signaturePath, trustManifestPath })
                CortexUserSessionStartup.RequireOwnedFile(root, path, 4 * 1024 * 1024);
        using var trustDocument = ReadJson(trustManifestPath, "public trust bundle");
        var trust = trustDocument.RootElement;
        RequireString(trust, "schema", TrustSchema);
        RequireString(trust, "channel", CortexProductIdentity.Channel);
        RequireString(trust, "rootThumbprint", CortexProductIdentity.ProdAlphaRootThumbprint);
        RequireString(trust, "publisherThumbprint", CortexProductIdentity.ProdAlphaPublisherThumbprint);
        var certificates = trust.GetProperty("certificates");
        if (certificates.ValueKind != JsonValueKind.Array || certificates.GetArrayLength() != 2)
            throw new InvalidDataException("The public trust bundle must contain exactly root and publisher certificates.");

        var rootCertificate = LoadCertificate(
            root, certificates, "root", "pegasus-prod-alpha-root.cer",
            CortexProductIdentity.ProdAlphaRootThumbprint);
        var publisherCertificate = LoadCertificate(
            root, certificates, "publisher", "pegasus-prod-alpha-publisher.cer",
            CortexProductIdentity.ProdAlphaPublisherThumbprint);
        using (rootCertificate)
        using (publisherCertificate)
        {
            if (installedServiceOnly)
                foreach (var name in new[] { "pegasus-prod-alpha-root.cer", "pegasus-prod-alpha-publisher.cer" })
                    CortexUserSessionStartup.RequireOwnedFile(root, Path.Combine(root, "trust", name), 16 * 1024);
            ValidateCertificateIdentities(rootCertificate, publisherCertificate);
            var manifestBytes = ReadMetadataBytes(manifestPath, 4 * 1024 * 1024, "package manifest");
            VerifyDetachedCms(manifestBytes,
                ReadMetadataBytes(signaturePath, 4 * 1024 * 1024, "manifest signature"), publisherCertificate);
            using var manifestDocument = JsonDocument.Parse(manifestBytes, JsonOptions());
            var manifest = manifestDocument.RootElement;
            RequireString(manifest, "schema", PackageSchema);
            RequireString(manifest, "role", "product");
            RequireString(manifest, "channel", CortexProductIdentity.Channel);
            var releaseVersion = RequiredString(manifest, "releaseVersion");
            if (!CortexProductIdentity.IsReleaseVersion(releaseVersion))
                throw new InvalidDataException("The signed product release version is invalid.");
            var signing = manifest.GetProperty("signing");
            RequireString(signing, "schema", SignatureSchema);
            RequireString(signing, "channel", CortexProductIdentity.Channel);
            RequireString(signing, "algorithm", "CMS-SHA256-RSA");
            RequireString(signing, "signedRole", "product");
            RequireString(signing, "signedReleaseVersion", releaseVersion);
            RequireString(signing, "publisherThumbprint", CortexProductIdentity.ProdAlphaPublisherThumbprint);
            RequireString(signing, "rootThumbprint", CortexProductIdentity.ProdAlphaRootThumbprint);
            RequireString(signing, "detachedSignature", "package-manifest.p7s");

            var files = installedServiceOnly
                ? VerifyServiceInventory(root, manifest.GetProperty("files"), publisherCertificate, releaseVersion)
                : VerifyInventory(root, manifest.GetProperty("files"), publisherCertificate);
            var catalogRelative = $"manifests/releases/{releaseVersion}/product-catalog.v1.json";
            if (!files.Any(file => file.RelativePath == catalogRelative))
                throw new InvalidDataException("The signed package inventory omits the product catalog.");
            var catalogPath = RequireOrdinaryFile(root, catalogRelative);
            var catalog = CortexProductCatalogV1.ReadExact(catalogPath);
            if (catalog.ReleaseVersion != releaseVersion)
                throw new InvalidDataException("The signed manifest and product catalog identify different releases.");
            return new CortexVerifiedProductPackageV1(
                root, releaseVersion, catalogPath, manifestPath, signaturePath,
                CortexProductIdentity.ProdAlphaPublisherThumbprint,
                CortexProductIdentity.ProdAlphaRootThumbprint,
                files);
        }
    }

    private static Dictionary<string, CortexVerifiedPackageFileV1> ReadInventory(JsonElement entries)
    {
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            throw new InvalidDataException("The signed package inventory is empty.");
        var expected = new Dictionary<string, CortexVerifiedPackageFileV1>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var relative = RequiredString(entry, "path");
            RequireImmutableInstallPath(relative);
            if (!IsSafeRelativePath(relative) || !expected.TryAdd(relative, new CortexVerifiedPackageFileV1(
                    relative,
                    entry.GetProperty("bytes").GetInt64(),
                    RequiredSha256(entry, "sha256"))))
                throw new InvalidDataException("The signed package inventory contains an unsafe or duplicate path.");
        }

        return expected;
    }

    private static IReadOnlyList<CortexVerifiedPackageFileV1> VerifyInventory(
        string root,
        JsonElement entries,
        X509Certificate2 publisher)
    {
        var expected = ReadInventory(entries);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The package contains a reparse file.");
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is "package-manifest.json" or "package-manifest.p7s")
                continue;
            if (!actual.Add(relative) || !expected.TryGetValue(relative, out var record) ||
                info.Length != record.Bytes || ComputeSha256(file) != record.Sha256)
                throw new InvalidDataException($"Package content verification failed for '{relative}'.");
            if (info.Extension is ".exe" or ".dll")
                VerifyAuthenticode(file, publisher);
        }
        if (actual.Count != expected.Count)
            throw new InvalidDataException("The signed package inventory has missing content.");
        return expected.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<CortexVerifiedPackageFileV1> VerifyServiceInventory(
        string root, JsonElement entries, X509Certificate2 publisher, string release)
    {
        var catalog = $"manifests/releases/{release}/product-catalog.v1.json";
        var expected = ReadInventory(entries).Values.Where(file =>
            file.RelativePath.StartsWith("bin/service/", StringComparison.Ordinal) ||
            file.RelativePath == catalog).ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        if (!expected.ContainsKey(CortexProductCatalogV1.StableRolePaths["service"]) ||
            !expected.ContainsKey(catalog))
            throw new InvalidDataException("The signed product omits the service closure or catalog.");
        var serviceRoot = Path.Combine(root, "bin", "service");
        RejectReparseTree(serviceRoot);
        RejectPrivateKeyShapedContent(serviceRoot);
        var actual = Directory.EnumerateFiles(serviceRoot, "*", SearchOption.AllDirectories)
            .Append(Path.Combine(root, catalog.Replace('/', Path.DirectorySeparatorChar))).ToArray();
        if (actual.Length != expected.Count)
            throw new InvalidDataException("The installed service closure differs from its signed inventory.");
        foreach (var path in actual)
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!expected.TryGetValue(relative, out var file))
                throw new InvalidDataException("The installed service contains unadmitted content.");
            CortexUserSessionStartup.RequireOwnedFile(root, path, 512L * 1024 * 1024);
            if (new FileInfo(path).Length != file.Bytes || ComputeSha256(path) != file.Sha256)
                throw new InvalidDataException("The installed service differs from its signed content.");
            if (Path.GetExtension(path) is ".exe" or ".dll") VerifyAuthenticode(path, publisher);
        }
        return expected.Values.ToArray();
    }

    internal static void VerifyAuthenticode(string path, X509Certificate2 publisher)
    {
        X509Certificate2 signer;
        try
        {
#pragma warning disable SYSLIB0057 // No X509CertificateLoader API extracts the signer from a PE Authenticode table.
            signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"An executable is unsigned: '{path}'.", exception);
        }
        using (signer)
        {
            if (NormalizeThumbprint(signer.Thumbprint) != NormalizeThumbprint(publisher.Thumbprint))
                throw new InvalidDataException($"An executable has the wrong signer: '{path}'.");
        }

        var status = WinTrust.Verify(path);
        if (status == TrustEBadDigest || status is not (TrustSuccess or CertEUntrustedRoot or CertEChaining))
            throw new InvalidDataException($"Authenticode verification failed for '{path}' (0x{status:X8}).");
    }

    internal static void RequireImmutableInstallPath(string relative)
    {
        if (!IsSafeRelativePath(relative) ||
            relative.Split('/')[0].Equals("state", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A product package cannot install mutable state or database files.");
        var name = Path.GetFileName(relative);
        if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Database files are application-owned and cannot be installer payload.");
    }

    private static void VerifyDetachedCms(byte[] content, byte[] signature, X509Certificate2 publisher)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        try
        {
            cms.Decode(signature);
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The detached product-manifest signature is invalid.", exception);
        }
        if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate is null ||
            NormalizeThumbprint(cms.SignerInfos[0].Certificate!.Thumbprint) != NormalizeThumbprint(publisher.Thumbprint) ||
            cms.SignerInfos[0].DigestAlgorithm.Value != "2.16.840.1.101.3.4.2.1")
            throw new InvalidDataException("The detached product-manifest signer is invalid.");
    }

    private static void ValidateCertificateIdentities(X509Certificate2 root, X509Certificate2 publisher)
    {
        if (root.Subject != "CN=Pegasus Local Development Root CA" || root.Issuer != root.Subject ||
            NormalizeThumbprint(root.Thumbprint) != CortexProductIdentity.ProdAlphaRootThumbprint)
            throw new InvalidDataException("The package root certificate identity is invalid.");
        if (publisher.Subject != "CN=Pegasus Software Signing Prod Alpha" ||
            NormalizeThumbprint(publisher.Thumbprint) != CortexProductIdentity.ProdAlphaPublisherThumbprint)
            throw new InvalidDataException("The package publisher certificate identity is invalid.");
        var now = DateTime.UtcNow;
        if (now < root.NotBefore.ToUniversalTime() || now > root.NotAfter.ToUniversalTime() ||
            now < publisher.NotBefore.ToUniversalTime() || now > publisher.NotAfter.ToUniversalTime())
            throw new InvalidDataException("The package signing certificate is outside its validity window.");
        var eku = publisher.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || eku.EnhancedKeyUsages.Count != 1 || eku.EnhancedKeyUsages[0].Value != CodeSigningOid)
            throw new InvalidDataException("The package publisher does not have the exact Code Signing EKU.");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningOid));
        if (!chain.Build(publisher) || chain.ChainElements.Count != 2 ||
            NormalizeThumbprint(chain.ChainElements[^1].Certificate.Thumbprint) != NormalizeThumbprint(root.Thumbprint))
            throw new InvalidDataException("The package publisher does not chain to the pinned root.");
    }

    private static X509Certificate2 LoadCertificate(
        string root,
        JsonElement entries,
        string role,
        string expectedFile,
        string expectedThumbprint)
    {
        var matches = entries.EnumerateArray().Where(entry => RequiredString(entry, "role") == role).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"The trust bundle does not contain exactly one '{role}' certificate.");
        var entry = matches[0];
        RequireString(entry, "file", expectedFile);
        RequireString(entry, "thumbprint", expectedThumbprint);
        var path = RequireOrdinaryFile(root, "trust/" + expectedFile);
        var bytes = ReadMetadataBytes(path, 16 * 1024, "public trust certificate");
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != RequiredSha256(entry, "sha256"))
            throw new InvalidDataException("A public trust certificate digest is invalid.");
        return X509CertificateLoader.LoadCertificate(bytes);
    }

    private static JsonDocument ReadJson(string path, string description)
    {
        try { return JsonDocument.Parse(ReadMetadataBytes(path, 4 * 1024 * 1024, description), JsonOptions()); }
        catch (JsonException exception) { throw new InvalidDataException($"The {description} is malformed.", exception); }
    }

    private static byte[] ReadMetadataBytes(string path, int maximumBytes, string description)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBoundedMetadata(input, maximumBytes, description);
    }

    internal static byte[] ReadBoundedMetadata(Stream input, int maximumBytes, string description)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var length = input.Length;
        if (length is < 1 || length > maximumBytes)
            throw new InvalidDataException($"The {description} is outside its byte limit.");
        var bytes = new byte[checked((int)length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || input.Length != length)
            throw new InvalidDataException($"The {description} changed during its bounded read.");
        return bytes;
    }

    private static JsonDocumentOptions JsonOptions() => new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };

    private static string RequireOrdinaryRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("A canonical local package root is required.", nameof(path));
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(canonical, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The package root must already be canonical.", nameof(path));
        var info = new DirectoryInfo(canonical);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The package root is absent or is a reparse point.");
        return canonical;
    }

    private static string RequireOrdinaryFile(string root, string relative)
    {
        if (!IsSafeRelativePath(relative))
            throw new InvalidDataException("A package-relative path is unsafe.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(Path.GetDirectoryName(path) is null ? path : Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A package path is not canonical.");
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("A required package file is absent or unsafe.", path);
        return path;
    }

    private static bool IsSafeRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains(':') &&
        !value.Contains('\\') && value.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static void RejectReparseTree(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The product package contains a reparse point.");
    }

    private static void RejectPrivateKeyShapedContent(string root)
    {
        var extensions = new HashSet<string>([".pfx", ".p12", ".p8", ".key", ".pem", ".pvk", ".jks", ".keystore"], StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (extensions.Contains(Path.GetExtension(file)) || Path.GetFileNameWithoutExtension(file).Contains("private-key", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Private-key-shaped content is prohibited in a Cortex package.");
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"Required string '{name}' is absent.");
        return property.GetString()!;
    }

    private static string RequiredSha256(JsonElement value, string name)
    {
        var result = RequiredString(value, name);
        if (result.Length != 64 || result.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException($"Required SHA-256 '{name}' is invalid.");
        return result;
    }

    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (RequiredString(value, name) != expected)
            throw new InvalidDataException($"Package field '{name}' has an unexpected value.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string NormalizeThumbprint(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static class WinTrust
    {
        private static readonly Guid Action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        public static int Verify(string path)
        {
            using var file = new FileInfoHandle(path);
            using var data = new TrustDataHandle(file.Pointer);
            var action = Action;
            return WinVerifyTrust(IntPtr.Zero, ref action, data.Pointer);
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, IntPtr trustData);

        private sealed class FileInfoHandle : IDisposable
        {
            public FileInfoHandle(string path)
            {
                var pathPointer = Marshal.StringToCoTaskMemUni(path);
                Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(new WinTrustFileInfo
                {
                    Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = pathPointer
                }, Pointer, false);
            }
            public IntPtr Pointer { get; }
            public void Dispose()
            {
                if (Pointer == IntPtr.Zero) return;
                var value = Marshal.PtrToStructure<WinTrustFileInfo>(Pointer);
                Marshal.FreeCoTaskMem(value.FilePath);
                Marshal.FreeCoTaskMem(Pointer);
            }
        }

        private sealed class TrustDataHandle : IDisposable
        {
            public TrustDataHandle(IntPtr fileInfo)
            {
                Pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
                Marshal.StructureToPtr(new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfo,
                    StateAction = 0,
                    ProviderFlags = 0x00001000,
                    UiContext = 0
                }, Pointer, false);
            }
            public IntPtr Pointer { get; }
            public void Dispose() { if (Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(Pointer); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }
    }
}
