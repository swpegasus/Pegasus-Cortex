using Pegasus.Cortex.Product;
using System.Text.Json;

namespace Pegasus.Cortex.Setup;

public enum CortexInstallMode
{
    Install,
    Upgrade,
    Repair
}

public sealed record CortexProductInstallPlanV1(
    string Contract,
    int Version,
    CortexInstallMode Mode,
    string? InstalledVersion,
    string CandidateVersion,
    string InstallRoot,
    long RequiredFreeBytes,
    long AvailableFreeBytes,
    bool StatePreserved,
    string? DataRoot = null);

public static class CortexProductInstallPlanner
{
    public const string Contract = "pegasus-cortex-install-plan";
    private const long SafetyReserveBytes = 64L * 1024 * 1024;

    // Installer-owned forward-repair evidence, not a second runtime selector.
    // The application reads only the machine registration after successful setup.
    private sealed record Locations(string Contract, int Version, string InstallRoot, string DataRoot);
    private static string LocationsPath(CortexProductLayout layout) =>
        Path.Combine(layout.InstallStateDirectory, "locations.v1.json");

    internal static string? ReadEstablishedDataRoot(CortexProductLayout layout)
    {
        var path = LocationsPath(layout);
        if (!File.Exists(path)) return null;
        layout.RequireRuntimePathsWithinDeclaredRoots();
        if (!layout.IsIsolatedFixture)
            CortexProductStateProvisioner.RequireInstallReceiptBoundary(layout);
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 4096)
            throw new InvalidDataException("The install location receipt is unsafe or oversized.");
        var value = JsonSerializer.Deserialize<Locations>(File.ReadAllBytes(path), new JsonSerializerOptions
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        });
        if (value is null || value.Contract != "pegasus-cortex-install-locations" || value.Version != 1 ||
            !string.Equals(value.InstallRoot, layout.InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(value.DataRoot) || value.DataRoot.Length > CortexProductLayout.MaximumWindowsPathCharacters ||
            !Path.IsPathFullyQualified(value.DataRoot))
            throw new InvalidDataException("The install location receipt does not bind this installation.");
        return value.DataRoot;
    }

    internal static void PreserveLocations(CortexProductLayout layout)
    {
        if (ReadEstablishedDataRoot(layout) is { } existing)
        {
            if (!string.Equals(existing, layout.DataRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Repair must preserve the established data location.");
            return;
        }
        // Provisioning has secured this installer-owned directory. CreateNew
        // prevents a concurrent setup from replacing another location binding.
        var destination = LocationsPath(layout);
        var staging = Path.Combine(layout.InstallStateDirectory, ".locations-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owned = false;
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                JsonSerializer.Serialize(output, new Locations("pegasus-cortex-install-locations", 1, layout.InstallRoot, layout.DataRoot));
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, destination, overwrite: false);
        }
        finally { if (owned && File.Exists(staging)) File.Delete(staging); }
    }

    public static CortexProductInstallPlanV1 Create(
        CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout) => Create(package, layout, null);

    internal static CortexProductInstallPlanV1 Create(CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout, CortexDataRootRelocation? relocation)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        RequireDestinationPolicy(layout, relocation: relocation);

        string? installedVersion = null;
        if (File.Exists(layout.ActiveCatalogPath))
            installedVersion = CortexProductCatalogV1.ReadInstalledUpgradeSource(
                layout.ActiveCatalogPath).ReleaseVersion;

        var comparison = installedVersion is null
            ? 1
            : CompareReleaseVersions(package.ReleaseVersion, installedVersion);
        if (comparison < 0)
            throw new InvalidDataException(
                $"Downgrade is blocked: installed={installedVersion}; candidate={package.ReleaseVersion}.");

        var mode = installedVersion is null
            ? ReadEstablishedDataRoot(layout) is null ? CortexInstallMode.Install : CortexInstallMode.Repair
            : comparison == 0 ? CortexInstallMode.Repair : CortexInstallMode.Upgrade;
        var packageBytes = package.Files.Aggregate(0L, (total, file) => checked(total + file.Bytes));
        var largestFile = package.Files.Count == 0 ? 0 : package.Files.Max(file => file.Bytes);
        var required = checked(packageBytes + largestFile + SafetyReserveBytes);
        var driveRoot = Path.GetPathRoot(layout.InstallRoot) ??
            throw new InvalidDataException("The Cortex install root has no volume.");
        var available = new DriveInfo(driveRoot).AvailableFreeSpace;
        if (available < required)
            throw new IOException(
                $"Insufficient free space: required={required}; available={available}; volume={driveRoot}.");

        return new CortexProductInstallPlanV1(
            Contract, 1, mode, installedVersion, package.ReleaseVersion,
            layout.InstallRoot, required, available, StatePreserved: true, layout.DataRoot);
    }

    /// <summary>
    /// Fresh installs may not adopt an arbitrary populated folder. An owned
    /// location receipt, registry-bound catalog, or verified legacy ARP plus
    /// product registration identity admits repair/upgrade; the
    /// runtime, not setup, initializes and evolves database contents.
    /// </summary>
    internal static void RequireDestinationPolicy(
        CortexProductLayout layout, CortexLegacyArpIdentity? legacyIdentityForTest = null,
        CortexDataRootRelocation? relocation = null)
    {
        if (legacyIdentityForTest is not null && !layout.IsIsolatedFixture)
            throw new InvalidOperationException("Injected legacy identity is restricted to isolated setup fixtures.");
        layout.RequireRuntimePathsWithinInstallRoot();
        if (relocation is not null)
        {
            relocation.RequireInstallLayout(layout);
            if (ReadEstablishedDataRoot(layout) is { } copied &&
                !string.Equals(copied, layout.DataRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The copied location receipt does not bind the prepared target.");
            return;
        }
        if (layout.RegisteredDataRoot is not null && !string.Equals(
                layout.RegisteredDataRoot, layout.DataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An upgrade must preserve the machine-registered database location.");
        if (ReadEstablishedDataRoot(layout) is { } established)
        {
            if (!string.Equals(established, layout.DataRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Upgrade/repair cannot change the established data location.");
            return;
        }
        if (File.Exists(layout.ActiveCatalogPath))
        {
            var catalog = CortexProductCatalogV1.ReadInstalledUpgradeSource(layout.ActiveCatalogPath);
            if (layout.RegisteredDataRoot is not null) return;
            if (CortexLegacyInstallIdentity.IsEstablished(layout, catalog, legacyIdentityForTest)) return;
            throw new InvalidDataException("A catalog without an installation location binding cannot adopt an occupied folder.");
        }
        RequireEmptyOrAbsent(layout.InstallRoot, "program");
        RequireEmptyOrAbsent(layout.DataRoot, "database");
    }

    private static void RequireEmptyOrAbsent(string path, string role)
    {
        if (File.Exists(path) || Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException($"A fresh Cortex {role} location must be empty. Select an empty dedicated folder or upgrade the recognized installation; existing files were not changed.");
    }

    internal static int CompareReleaseVersions(string left, string right)
    {
        if (!CortexProductIdentity.IsReleaseVersion(left) ||
            !CortexProductIdentity.IsReleaseVersion(right))
            throw new InvalidDataException("A Cortex release version is invalid.");

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < leftParts.Length - 1; index++)
        {
            var result = int.Parse(leftParts[index], System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(int.Parse(rightParts[index], System.Globalization.CultureInfo.InvariantCulture));
            if (result != 0)
                return result;
        }
        return string.CompareOrdinal(leftParts[^1], rightParts[^1]);
    }
}
