using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexProductInstallReceiptV1(
    string Contract,
    int Version,
    string ReleaseVersion,
    string InstallRoot,
    int CreatedFiles,
    int ReplacedFiles,
    int PreservedFiles,
    CortexStateMigrationReceiptV1? Migration,
    string ActiveCatalogPath,
    string? DataRoot = null,
    bool DatabaseFilesTouched = false);

public sealed class CortexProductInstallStageException : Exception
{
    public CortexProductInstallStageException(string stage, string? target, Exception innerException)
        : base("A bounded Cortex product installation stage failed.", innerException)
    {
        Stage = string.IsNullOrWhiteSpace(stage)
            ? throw new ArgumentException("The installation stage is required.", nameof(stage))
            : stage;
        Target = target;
    }

    public string Stage { get; }
    public string? Target { get; }
}

public static class CortexProductInstaller
{
    public const string ReceiptContract = "pegasus-cortex-product-install-receipt";

    public static async Task<CortexProductInstallReceiptV1> InstallCurrentMachineAsync(
        string packageRoot,
        bool migrateLegacyState,
        CancellationToken cancellationToken = default)
    {
        if (migrateLegacyState)
            throw new InvalidOperationException("Normal installation does not migrate databases. Database interaction belongs to the application.");
        var package = CortexSignedProductPackageVerifier.Verify(packageRoot);
        var layout = CortexProductLayout.ResolveCurrent();
        return await InstallVerifiedFilesAsync(package, layout, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<CortexProductInstallReceiptV1> InstallVerifiedFilesAsync(
        CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout,
        CortexLegacyStateRootsV1? legacyRoots = null,
        CancellationToken cancellationToken = default) =>
        InstallVerifiedFilesCoreAsync(
            package,
            layout,
            legacyRoots,
            CortexProductStateProvisioner.Provision,
            CortexProductStateProvisioner.EnsureServiceConfiguration,
            afterActivation: null,
            cancellationToken);

    public static Task<CortexProductInstallReceiptV1> InstallVerifiedFilesAndRegisterAsync(
        CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout,
        CortexLegacyStateRootsV1? legacyRoots,
        Action<CortexProductCatalogV1> afterActivation,
        CancellationToken cancellationToken = default,
        Action<CortexProductLayout>? afterProvisioning = null) =>
        InstallVerifiedFilesCoreAsync(
            package,
            layout,
            legacyRoots,
            CortexProductStateProvisioner.Provision,
            CortexProductStateProvisioner.EnsureServiceConfiguration,
            afterActivation ?? throw new ArgumentNullException(nameof(afterActivation)),
            cancellationToken,
            afterProvisioning);

    internal static Task<CortexProductInstallReceiptV1> InstallVerifiedFilesForTestAsync(
        CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout,
        CancellationToken cancellationToken = default) =>
        InstallVerifiedFilesCoreAsync(
            package,
            layout,
            legacyRoots: null,
            provisioner: value =>
            {
                foreach (var entry in CortexProductStateProvisioner.Plan(value).Entries)
                    Directory.CreateDirectory(entry.Path);
            },
            serviceStateSealer: CortexProductStateProvisioner.EnsureServiceConfiguration,
            afterActivation: null,
            cancellationToken);

    internal static Task<CortexProductInstallReceiptV1> InstallRelocatedFilesAndRegisterAsync(
        CortexVerifiedProductPackageV1 package, CortexDataRootRelocation relocation,
        Action<CortexProductCatalogV1> afterActivation, CancellationToken cancellationToken,
        Action<CortexProductLayout>? afterProvisioning = null) =>
        InstallVerifiedFilesCoreAsync(package, relocation.InstallLayout, null,
            relocation.RequirePrivateInstallLayout, CortexProductStateProvisioner.EnsureServiceConfiguration,
            afterActivation, cancellationToken, afterProvisioning, relocation);

    internal static Task<CortexProductInstallReceiptV1> InstallVerifiedFilesWithActivationForTestAsync(
        CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout,
        Action<CortexProductCatalogV1> afterActivation,
        CancellationToken cancellationToken = default) =>
        InstallVerifiedFilesCoreAsync(
            package,
            layout,
            legacyRoots: null,
            provisioner: value =>
            {
                foreach (var entry in CortexProductStateProvisioner.Plan(value).Entries)
                    Directory.CreateDirectory(entry.Path);
            },
            serviceStateSealer: CortexProductStateProvisioner.EnsureServiceConfiguration,
            afterActivation,
            cancellationToken);

    private static async Task<CortexProductInstallReceiptV1> InstallVerifiedFilesCoreAsync(
        CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout,
        CortexLegacyStateRootsV1? legacyRoots,
        Action<CortexProductLayout> provisioner,
        Action<CortexProductLayout> serviceStateSealer,
        Action<CortexProductCatalogV1>? afterActivation,
        CancellationToken cancellationToken,
        Action<CortexProductLayout>? afterProvisioning = null,
        CortexDataRootRelocation? relocation = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        if (legacyRoots is not null)
            throw new InvalidOperationException("Normal installation cannot copy, seed, replace, or migrate database files.");
        CortexProductInstallPlanner.RequireDestinationPolicy(layout, relocation: relocation);
        if (!CortexProductIdentity.IsReleaseVersion(package.ReleaseVersion))
            throw new InvalidDataException("The admitted product release is invalid.");
        RequireSeparateRoots(package.Root, layout.InstallRoot);
        RequireSeparateRoots(package.Root, layout.DataRoot);
        foreach (var file in package.Files)
            CortexSignedProductPackageVerifier.RequireImmutableInstallPath(file.RelativePath);
        RejectExistingReparseComponents(layout.InstallRoot);
        try
        {
            provisioner(layout);
            CortexProductInstallPlanner.PreserveLocations(layout);
            afterProvisioning?.Invoke(layout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CortexProductInstallStageException("provision-state", null, exception);
        }

        // Development installs build forward in place. Each file is verified and
        // atomically replaced; a later failure leaves completed writes for repair.
        // No product backup tree is created, and mutable databases are never replaced.
        var created = 0;
        var replaced = 0;
        var preserved = 0;
            foreach (var file in package.Files.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = RequireContainedOrdinaryFile(package.Root, file.RelativePath);
                    if (new FileInfo(source).Length != file.Bytes || ComputeSha256(source) != file.Sha256)
                        throw new InvalidDataException("An admitted package file changed before installation.");
                    var destination = ContainedPath(layout.InstallRoot, file.RelativePath);
                    var parent = Path.GetDirectoryName(destination) ??
                        throw new InvalidDataException("An installed file has no parent directory.");
                    Directory.CreateDirectory(parent);
                    RejectExistingReparseComponents(parent);
                    var existed = File.Exists(destination);
                    if (existed)
                    {
                        RejectExistingReparseComponents(destination);
                        if (new FileInfo(destination).Length == file.Bytes && ComputeSha256(destination) == file.Sha256)
                        {
                            preserved++;
                            continue;
                        }
                    }
                    else if (Directory.Exists(destination))
                    {
                        throw new IOException("An installed file path identifies a directory.");
                    }
                    ReplaceFromVerifiedSource(source, destination, file.Bytes, file.Sha256);
                    if (existed) replaced++;
                    else created++;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException and not CortexProductInstallStageException)
                {
                    throw new CortexProductInstallStageException(
                        "install-file", file.RelativePath, exception);
                }
            }

            var releaseManifestDirectory = Path.Combine(
                layout.InstallRoot, "manifests", "releases", package.ReleaseVersion);
            Directory.CreateDirectory(releaseManifestDirectory);
            foreach (var evidence in new[]
                     {
                         (Source: package.ManifestPath, Destination: Path.Combine(releaseManifestDirectory, "package-manifest.json")),
                         (Source: package.SignaturePath, Destination: Path.Combine(releaseManifestDirectory, "package-manifest.p7s"))
                     })
            {
                var sha = ComputeSha256(evidence.Source);
                var bytes = new FileInfo(evidence.Source).Length;
                if (File.Exists(evidence.Destination) &&
                    new FileInfo(evidence.Destination).Length == bytes && ComputeSha256(evidence.Destination) == sha)
                {
                    preserved++;
                    continue;
                }
                var existed = File.Exists(evidence.Destination);
                RejectExistingReparseComponents(evidence.Destination);
                ReplaceFromVerifiedSource(evidence.Source, evidence.Destination, bytes, sha);
                if (existed) replaced++;
                else created++;
            }

            try
            {
                serviceStateSealer(layout);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new CortexProductInstallStageException("seal-service-state", null, exception);
            }
            CortexProductCatalogActivator.Activate(
                layout,
                ContainedPath(layout.InstallRoot,
                    $"manifests/releases/{package.ReleaseVersion}/product-catalog.v1.json"));
            var activeCatalog = CortexProductCatalogV1.ReadExact(layout.ActiveCatalogPath);
            afterActivation?.Invoke(activeCatalog);

            return new CortexProductInstallReceiptV1(
                ReceiptContract, 1, package.ReleaseVersion, layout.InstallRoot,
                created, replaced, preserved, null, layout.ActiveCatalogPath,
                layout.DataRoot, DatabaseFilesTouched: false);
    }

    private static void ReplaceFromVerifiedSource(
        string source,
        string destination,
        long expectedBytes,
        string expectedSha256)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            "." + Path.GetFileName(destination) + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length != expectedBytes || ComputeSha256(temporary) != expectedSha256)
                throw new InvalidDataException("A staged installed file failed post-copy verification.");
            try
            {
                File.Move(temporary, destination, overwrite: true);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new IOException(
                    "The admitted installed file could not be atomically published.", exception);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string RequireContainedOrdinaryFile(string root, string relative)
    {
        var path = ContainedPath(root, relative);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("An admitted source file is absent or unsafe.", path);
        RejectExistingReparseComponents(path);
        return path;
    }

    private static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("A product-relative path is unsafe.");
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var resolved = Path.GetRelativePath(canonicalRoot, path);
        if (Path.IsPathRooted(resolved) || resolved == ".." ||
            resolved.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A product-relative path escaped its root.");
        return path;
    }

    private static void RequireSeparateRoots(string packageRoot, string installRoot)
    {
        var package = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var packageFromInstall = Path.GetRelativePath(install, package);
        var installFromPackage = Path.GetRelativePath(package, install);
        if (packageFromInstall == "." || installFromPackage == "." ||
            (!Path.IsPathRooted(packageFromInstall) && packageFromInstall != ".." &&
             !packageFromInstall.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) ||
            (!Path.IsPathRooted(installFromPackage) && installFromPackage != ".." &&
             !installFromPackage.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new InvalidOperationException("The admitted package and install roots must be separate trees.");
    }

    private static void RejectExistingReparseComponents(string path)
    {
        var canonical = Path.GetFullPath(path);
        var root = Path.GetPathRoot(canonical) ?? throw new InvalidDataException("A path has no filesystem root.");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, canonical).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A product path crosses a reparse point.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
