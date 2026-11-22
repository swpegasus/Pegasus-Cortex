using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexUninstallFileV1(
    string RelativePath,
    long Bytes,
    string Sha256);

public sealed record CortexUninstallInventoryV1(
    string Contract,
    int Version,
    string InstallRoot,
    string ReleaseVersion,
    IReadOnlyList<CortexUninstallFileV1> Files,
    IReadOnlyList<string> RegistryKeys,
    IReadOnlyList<string> Shortcuts);

public sealed record CortexUninstallRemovalReceiptV1(
    string Contract,
    int Version,
    string InstallRoot,
    int RemovedFiles,
    int MissingFiles,
    int PreservedChangedFiles,
    bool StateRetained);

public static class CortexUninstallInventory
{
    public const string Contract = "pegasus-cortex-uninstall-inventory";
    public const string RemovalContract = "pegasus-cortex-uninstall-removal";
    public const string FileName = "uninstall.v1.dat";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static CortexUninstallInventoryV1 Write(
        CortexVerifiedProductPackageV1 package,
        CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(layout);
        var path = Path.Combine(layout.InstallStateDirectory, FileName);
        var merged = new Dictionary<string, CortexUninstallFileV1>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (var file in Read(layout).Files)
                merged[file.RelativePath] = file;
        }
        foreach (var file in package.Files)
        {
            var relative = RequireImmutableRelativePath(file.RelativePath);
            merged[relative] = new CortexUninstallFileV1(relative, file.Bytes, file.Sha256);
        }
        foreach (var evidence in new[]
                 {
                     (Source: package.ManifestPath, Name: "package-manifest.json"),
                     (Source: package.SignaturePath, Name: "package-manifest.p7s")
                 })
        {
            var relative = RequireImmutableRelativePath(
                $"manifests/releases/{package.ReleaseVersion}/{evidence.Name}");
            var info = new FileInfo(evidence.Source);
            merged[relative] = new CortexUninstallFileV1(relative, info.Length, ComputeSha256(evidence.Source));
        }

        var inventory = new CortexUninstallInventoryV1(
            Contract,
            1,
            layout.InstallRoot,
            package.ReleaseVersion,
            merged.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray(),
            [
                @"HKLM64\Software\Pegasus\Cortex",
                @"HKLM64\Software\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Cortex",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Pegasus Cortex"
            ],
            [@"CommonPrograms\Pegasus\Pegasus Cortex.lnk",
             @"CommonPrograms\Pegasus\Pegasus Cortex Configuration.lnk",
             @"CommonPrograms\Pegasus\Pegasus Cortex Tray.lnk"]);
        Directory.CreateDirectory(layout.InstallStateDirectory);
        WriteAtomic(path, JsonSerializer.Serialize(inventory, Json) + "\n");
        return inventory;
    }

    public static CortexUninstallInventoryV1 Read(CortexProductLayout layout) => Read(layout, out _);

    internal static CortexUninstallInventoryV1 Read(CortexProductLayout layout, out byte[] preimage)
    {
        var path = Path.Combine(layout.InstallStateDirectory, FileName);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 4 * 1024 * 1024 ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Cortex uninstall inventory is absent or unsafe.");
        preimage = CortexCodexFileUpdate.ReadBounded(path, 4 * 1024 * 1024);
        var value = JsonSerializer.Deserialize<CortexUninstallInventoryV1>(preimage, Json) ??
            throw new InvalidDataException("The Cortex uninstall inventory is invalid.");
        if (value.Contract != Contract || value.Version != 1 ||
            string.IsNullOrWhiteSpace(value.InstallRoot) ||
            !string.Equals(Path.GetFullPath(value.InstallRoot), layout.InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            !CortexProductIdentity.IsReleaseVersion(value.ReleaseVersion) ||
            value.Files.Count is 0 or > 100_000 ||
            value.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Files.Count)
            throw new InvalidDataException("The Cortex uninstall inventory does not match this install instance.");
        foreach (var file in value.Files)
        {
            _ = RequireImmutableRelativePath(file.RelativePath);
            if (file.Bytes < 0 || file.Sha256.Length != 64 || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("The Cortex uninstall inventory contains invalid file evidence.");
        }
        return value;
    }

    internal static int CompactAfterCleanup(CortexProductLayout layout, CortexUninstallInventoryV1 admitted,
        byte[] preimage, IReadOnlySet<string> removedOrMissing)
    {
        var retained = admitted.Files.Where(file => !removedOrMissing.Contains(file.RelativePath.Replace('\\', '/'))).ToArray();
        var removed = admitted.Files.Count - retained.Length;
        if (removed == 0) return 0;
        if (retained.Length == 0)
            throw new InvalidDataException("Cleanup cannot discard the current installation's entire immutable inventory.");
        // Preserve identity, current release, registry keys and shortcuts exactly.
        // The existing atomic updater rejects any concurrent preimage change and
        // replaces this one file without making a backup or converted manifest.
        var result = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(admitted with { Files = retained }, Json) + "\n");
        if (result.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The compacted inventory exceeds its existing read bound.");
        new CortexCodexFileUpdate(Path.Combine(layout.InstallStateDirectory, FileName), true, preimage, result).Apply();
        return removed;
    }

    public static CortexUninstallRemovalReceiptV1 RemoveImmutableFiles(CortexProductLayout layout)
    {
        var inventory = Read(layout);
        var removed = 0;
        var missing = 0;
        var changed = 0;
        foreach (var file in inventory.Files.OrderByDescending(value => value.RelativePath.Length))
        {
            if (file.RelativePath.StartsWith("installer/", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Contained(layout.InstallRoot, file.RelativePath);
            if (!File.Exists(path))
            {
                missing++;
                continue;
            }
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length != file.Bytes || ComputeSha256(path) != file.Sha256.ToLowerInvariant())
            {
                changed++;
                continue;
            }
            File.Delete(path);
            removed++;
        }

        foreach (var directory in Directory.EnumerateDirectories(layout.InstallRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !IsSameOrContained(layout.StateDirectory, path) &&
                                    !IsSameOrContained(Path.Combine(layout.InstallRoot, "installer"), path))
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        return new CortexUninstallRemovalReceiptV1(
            RemovalContract, 1, layout.InstallRoot, removed, missing, changed, StateRetained: true);
    }

    private static string RequireImmutableRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 512 ||
            Path.IsPathRooted(normalized) || normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is "" or "." or "..") ||
            normalized.StartsWith("state/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An uninstall inventory path is unsafe or mutable.");
        return normalized;
    }

    private static string Contained(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var resolved = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(resolved) || resolved == ".." ||
            resolved.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("An uninstall inventory path escaped the install root.");
        return path;
    }

    private static bool IsSameOrContained(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
