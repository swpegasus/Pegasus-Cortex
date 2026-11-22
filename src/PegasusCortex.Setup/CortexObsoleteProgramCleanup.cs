using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexObsoleteProgramCleanupReceiptV1(
    string Contract, int Version, string InstallRoot, string ReleaseVersion,
    int RemovedFiles, int MissingFiles, int PreservedChangedFiles, int PreservedUnknownEntries,
    int RemovedDirectories, int CompactedInventoryEntries = 0, bool DatabaseFilesTouched = false, bool BackupCreated = false);

public sealed class CortexObsoleteProgramCleanupException : IOException
{
    public CortexObsoleteProgramCleanupException(CortexObsoleteProgramCleanupReceiptV1 receipt, string stage, Exception cause)
        : base("Obsolete Cortex program cleanup stopped; inspect the retained files before retrying the same cleanup.", cause)
    {
        Receipt = receipt;
        Stage = stage is "preflight" or "remove-file" or "remove-directory" or "compact-inventory"
            ? stage : throw new ArgumentOutOfRangeException(nameof(stage));
    }
    public CortexObsoleteProgramCleanupReceiptV1 Receipt { get; }
    public string Stage { get; }
}

/// <summary>Removes only obsolete immutable files proved by the merged install inventory.</summary>
public static class CortexObsoleteProgramCleanup
{
    public const string Contract = "pegasus-cortex-obsolete-program-cleanup";
    private const int MaximumEntries = 100_000;
    private const int MaximumOpenFiles = 16_384;

    public static CortexObsoleteProgramCleanupReceiptV1 Remove(CortexProductLayout layout)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cortex program cleanup requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Obsolete installed program cleanup requires the elevated product installer.");
        return RemoveCore(layout, fixture: false, CortexProductIdentity.ProdAlphaPublisherThumbprint);
    }

    internal static CortexObsoleteProgramCleanupReceiptV1 RemoveForTest(CortexProductLayout layout, string? fixturePublisherThumbprint = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cortex program cleanup requires Windows.");
        if (!layout.IsIsolatedFixture) throw new InvalidOperationException("Cleanup fixtures require an explicit isolated layout.");
        return RemoveCore(layout, fixture: true, fixturePublisherThumbprint ?? CortexProductIdentity.ProdAlphaPublisherThumbprint);
    }

    private static CortexObsoleteProgramCleanupReceiptV1 RemoveCore(CortexProductLayout layout, bool fixture, string publisherThumbprint)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var root = CortexCodexFileUpdate.RequireCanonical(layout.InstallRoot);
        CortexCodexFileUpdate.RequireDirectory(root, allowMissing: false);
        var inventoryPath = Path.Combine(layout.InstallStateDirectory, CortexUninstallInventory.FileName);
        foreach (var metadata in new[] { inventoryPath, layout.ActiveCatalogPath })
        {
            CortexCodexFileUpdate.RequireTarget(metadata, Path.GetFileName(metadata));
            if (!fixture) RequireProtectedMetadata(metadata, root);
        }
        if (!fixture) CortexProductStateProvisioner.RequireInstallReceiptBoundary(layout);
        var inventory = CortexUninstallInventory.Read(layout, out var inventoryPreimage);
        if (new FileInfo(layout.ActiveCatalogPath).Length is <= 0 or > 32 * 1024)
            throw new InvalidDataException("The active Cortex catalog is absent or exceeds its cleanup bound.");
        var catalog = CortexProductCatalogV1.ReadExact(layout.ActiveCatalogPath);
        // Preserve catalog evidence for admitted component versions. Executable
        // directories are preserved only when a role actually points into that
        // slot; a stable bin-layout catalog makes every legacy versions/* slot
        // eligible for exact-inventory cleanup.
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in catalog.Roles.Select(role => role.ComponentVersion)
                     .Append(catalog.ReleaseVersion).Append(inventory.ReleaseVersion))
            active.Add("manifests/releases/" + version);
        foreach (var role in catalog.Roles)
        {
            if (Slot(role.RelativePath) is { } slot) active.Add(slot);
        }

        var allFiles = inventory.Files.ToDictionary(file => file.RelativePath.Replace('\\', '/'),
            file => file, StringComparer.OrdinalIgnoreCase);
        var unadmittedSlots = AddHistoricalInventories(layout, active, allFiles, fixture, publisherThumbprint);

        var candidates = new Dictionary<string, CortexUninstallFileV1>(StringComparer.OrdinalIgnoreCase);
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in allFiles.Values)
        {
            var relative = file.RelativePath.Replace('\\', '/');
            var slot = Slot(relative);
            if (slot is null || active.Contains(slot)) continue;
            RequireImmutableFile(relative);
            var path = RequireProgramPath(layout, relative);
            candidates.Add(path, file);
            slots.Add(RequireProgramPath(layout, slot, directory: true));
        }

        var missing = 0; var changed = 0; var unknown = unadmittedSlots; var removed = 0; var removedDirectories = 0; var compacted = 0;
        CortexObsoleteProgramCleanupReceiptV1 Receipt() => new(Contract, 1, root, inventory.ReleaseVersion,
            removed, missing, changed, unknown, removedDirectories, compacted);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var opened = new List<(FileStream Stream, string RelativePath)>();
        var removedOrMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stage = "preflight";
        try
        {
            // Traverse only exact obsolete slots represented in the inventory.
            // Unknown mutable subtrees remain opaque; no state/data tree is walked.
            var pending = new Stack<string>(slots.Where(Directory.Exists));
            var observed = 0;
            while (pending.TryPop(out var directory))
            {
                RequireProgramPath(layout, Path.GetRelativePath(root, directory).Replace('\\', '/'), directory: true);
                directories.Add(directory);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (++observed > MaximumEntries) throw new InvalidDataException("Obsolete program cleanup exceeded its entry bound.");
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                        throw new InvalidDataException("Obsolete program cleanup encountered a reparse entry; nothing was removed.");
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (IsMutableDirectory(entry.Name)) { unknown++; continue; }
                        pending.Push(entry.FullName);
                    }
                    else if (!candidates.ContainsKey(entry.FullName)) unknown++;
                }
            }
            foreach (var (path, file) in candidates.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                RequireProgramPath(layout, Path.GetRelativePath(root, path).Replace('\\', '/'));
                if (!File.Exists(path)) { missing++; removedOrMissing.Add(file.RelativePath.Replace('\\', '/')); continue; }
                if (new FileInfo(path).Length != file.Bytes) { changed++; continue; }
                if (opened.Count >= MaximumOpenFiles)
                    throw new InvalidDataException("Obsolete program cleanup exceeds its retained file-handle bound.");
                FileStream? stream = OpenExclusiveForDeletion(path);
                try
                {
                    if (stream.Length != file.Bytes || !string.Equals(
                            Convert.ToHexStringLower(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    { changed++; continue; }
                    opened.Add((stream, file.RelativePath.Replace('\\', '/')));
                    stream = null;
                }
                finally { stream?.Dispose(); }
            }

            // All candidate identities, hashes, reparse boundaries and exclusive
            // read/write/delete access were checked before the first deletion.
            // Delete through retained handles, not re-resolved file names. No
            // delete-on-close flag is used during preflight and no backup is made.
            stage = "remove-file";
            // Keep historical admission evidence until all program payloads
            // have been removed so a partial failure can retry the same files.
            foreach (var (stream, relative) in opened.OrderBy(entry =>
                         Path.GetFileName(entry.RelativePath) is "package-manifest.json" or "package-manifest.p7s" ? 1 : 0))
            {
                var disposition = new FileDisposition { Delete = true };
                if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref disposition, 1))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "An exact obsolete program file could not be removed.");
                stream.Dispose(); removed++; removedOrMissing.Add(relative);
            }
            stage = "remove-directory";
            foreach (var directory in directories.OrderByDescending(path => path.Length))
            {
                RequireProgramPath(layout, Path.GetRelativePath(root, directory).Replace('\\', '/'), directory: true);
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                { Directory.Delete(directory, recursive: false); removedDirectories++; }
            }
            stage = "compact-inventory";
            compacted = CortexUninstallInventory.CompactAfterCleanup(layout, inventory, inventoryPreimage, removedOrMissing);
            return Receipt();
        }
        catch (Exception exception) when (exception is not CortexObsoleteProgramCleanupException)
        { throw new CortexObsoleteProgramCleanupException(Receipt(), stage, exception); }
        finally { foreach (var entry in opened) entry.Stream.Dispose(); }
    }

    private static string? Slot(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length >= 3 && parts[0] == "versions" && LegacyVersion(parts[1]) is not null)
            return string.Join('/', parts.Take(2));
        return parts.Length >= 4 &&
            ((parts[0] == "versions" && parts[1] is "execution" or "semantics") ||
             (parts[0] == "manifests" && parts[1] == "releases")) &&
            CortexProductIdentity.IsReleaseVersion(parts[2])
            ? string.Join('/', parts.Take(3)) : null;
    }

    private static string? LegacyVersion(string folder)
    {
        if (CortexProductIdentity.IsReleaseVersion(folder)) return folder;
        var match = Regex.Match(folder, @"^\.failed-(1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D])-\d{8}T\d{13}Z$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int AddHistoricalInventories(CortexProductLayout layout, HashSet<string> active,
        Dictionary<string, CortexUninstallFileV1> files, bool fixture, string publisherThumbprint)
    {
        var unadmitted = 0; var count = 0;
        foreach (var relativeParent in new[] { "manifests/releases", "versions" })
        {
            var parent = RequireProgramPath(layout, relativeParent, directory: true);
            if (!Directory.Exists(parent)) continue;
            foreach (var directory in new DirectoryInfo(parent).EnumerateDirectories())
            {
                if (++count > 256) throw new InvalidDataException("Historical program cleanup exceeds its slot bound.");
                var flat = relativeParent == "versions";
                var version = flat ? LegacyVersion(directory.Name) :
                    CortexProductIdentity.IsReleaseVersion(directory.Name) ? directory.Name : null;
                if (version is null) continue;
                var slot = relativeParent + "/" + directory.Name;
                if (active.Contains(slot) || flat && active.Contains("manifests/releases/" + version)) continue;
                // A malformed, unsigned-modern, or unowned historical tree is
                // retained as one opaque entry, never adopted or recursively erased.
                try
                {
                    RequireProgramPath(layout, slot, directory: true);
                    if (!fixture) RequireProtectedMetadata(directory.FullName, layout.InstallRoot, directory: true);
                    var admitted = ReadHistoricalInventory(layout, slot, version, flat, fixture, publisherThumbprint);
                    if (files.Count + admitted.Count > MaximumEntries)
                        throw new InvalidDataException("Historical program inventory exceeds its combined entry bound.");
                    foreach (var file in admitted) files.TryAdd(file.RelativePath, file);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                    JsonException or CryptographicException or ArgumentException or InvalidOperationException or
                    KeyNotFoundException or OverflowException or FormatException or System.Security.SecurityException)
                { unadmitted++; }
            }
        }
        return unadmitted;
    }

    private static IReadOnlyList<CortexUninstallFileV1> ReadHistoricalInventory(
        CortexProductLayout layout, string slot, string version, bool flat, bool fixture, string publisherThumbprint)
    {
        var path = RequireProgramPath(layout, slot + "/package-manifest.json");
        if (!fixture) RequireProtectedMetadata(path, layout.InstallRoot);
        var bytes = CortexCodexFileUpdate.ReadBounded(path, 4 * 1024 * 1024);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var manifest = document.RootElement;
        RequireUniqueObject(manifest);
        var schema = Text(manifest, "schema");
        var role = flat ? "application" : "product";
        if (Text(manifest, "releaseVersion") != version || Text(manifest, "role") != role ||
            schema is not ("pegasus.cortex.local-operational-package/1.1" or "pegasus.cortex.local-operational-package/1.2") ||
            !flat && schema != "pegasus.cortex.local-operational-package/1.2")
            throw new InvalidDataException("Historical manifest identity does not match its exact program slot.");
        var identity = manifest.GetProperty("identity"); RequireUniqueObject(identity);
        if (Text(identity, "entryPoint") != "PegasusCortex.exe" || Text(identity, "productVersion") != version ||
            Text(identity, "runtimeIdentifier") != "win-x64" ||
            !identity.TryGetProperty("selfContained", out var contained) || contained.ValueKind != JsonValueKind.True ||
            Text(identity, "deploymentMode") != (flat ? "self-contained-apphost" : "unified-one-root-nsis-product"))
            throw new InvalidDataException("Historical manifest has a foreign application identity.");
        var metadata = new List<CortexUninstallFileV1>
        { new(slot + "/package-manifest.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))) };
        if (schema.EndsWith("/1.2", StringComparison.Ordinal))
        {
            var signing = manifest.GetProperty("signing"); RequireUniqueObject(signing);
            if (Text(signing, "schema") != "pegasus.package-signature/1.0" ||
                Text(signing, "signedRole") != role || Text(signing, "signedReleaseVersion") != version ||
                Text(signing, "publisherThumbprint") != publisherThumbprint ||
                Text(signing, "detachedSignature") != "package-manifest.p7s")
                throw new InvalidDataException("Historical signature metadata differs from its exact program slot.");
            var signaturePath = RequireProgramPath(layout, slot + "/package-manifest.p7s");
            if (!fixture) RequireProtectedMetadata(signaturePath, layout.InstallRoot);
            var signature = CortexCodexFileUpdate.ReadBounded(signaturePath, 128 * 1024);
            var cms = new SignedCms(new ContentInfo(bytes), detached: true);
            cms.Decode(signature); cms.CheckSignature(verifySignatureOnly: true);
            if (cms.SignerInfos.Count != 1 ||
                cms.SignerInfos[0].Certificate?.Thumbprint != publisherThumbprint ||
                cms.SignerInfos[0].DigestAlgorithm.Value != "2.16.840.1.101.3.4.2.1")
                throw new InvalidDataException("Historical manifest signature is not the fixed Cortex publisher.");
            metadata.Add(new(slot + "/package-manifest.p7s", signature.Length, Convert.ToHexStringLower(SHA256.HashData(signature))));
        }
        else if (manifest.TryGetProperty("signing", out _))
            throw new InvalidDataException("Unsigned historical metadata cannot claim a signing contract.");

        var entries = manifest.GetProperty("files");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is 0 or > MaximumOpenFiles)
            throw new InvalidDataException("Historical immutable file inventory exceeds its bound.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CortexUninstallFileV1>();
        foreach (var entry in entries.EnumerateArray())
        {
            RequireUniqueObject(entry);
            var relative = Text(entry, "path");
            RequireImmutableFile(relative);
            var size = entry.GetProperty("bytes").GetInt64(); var sha = Text(entry, "sha256");
            if (!seen.Add(relative) || size < 0 || sha.Length != 64 || sha.Any(value => !Uri.IsHexDigit(value)))
                throw new InvalidDataException("Historical file evidence is malformed or duplicated.");
            var final = flat ? slot + "/" + relative : relative;
            var candidateSlot = Slot(final);
            if (!flat && candidateSlot is null) continue;
            if (!flat && !string.Equals(candidateSlot, slot, StringComparison.Ordinal) &&
                candidateSlot != "versions/execution/" + version && candidateSlot != "versions/semantics/" + version) continue;
            RequireProgramPath(layout, final);
            result.Add(new(final, size, sha));
        }
        if (!seen.Contains("PegasusCortex.exe")) throw new InvalidDataException("Historical inventory omits its Cortex entry point.");
        result.AddRange(metadata);
        return result;
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } text
            ? text : throw new InvalidDataException("Historical manifest is missing required text.");

    private static void RequireUniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new InvalidDataException("Historical manifest contains a malformed or duplicate object.");
    }

    private static string RequireProgramPath(CortexProductLayout layout, string relative, bool directory = false)
    {
        var path = CortexCodexFileUpdate.RequireCanonical(Path.Combine(layout.InstallRoot, relative.Replace('/', '\\')));
        foreach (var excluded in new[] { layout.StateDirectory, layout.DataRoot })
        {
            var test = Path.GetRelativePath(excluded, path);
            if (test == "." || !Path.IsPathRooted(test) && test != ".." && !test.StartsWith("..\\", StringComparison.Ordinal))
                throw new InvalidDataException("Program cleanup cannot enter a mutable state or database root.");
        }
        var contained = Path.GetRelativePath(layout.InstallRoot, path);
        if (Path.IsPathRooted(contained) || contained == ".." || contained.StartsWith("..\\", StringComparison.Ordinal))
            throw new InvalidDataException("Obsolete program cleanup escaped its installed root.");
        if (relative.Split('/').Skip(relative.StartsWith("versions/", StringComparison.Ordinal) &&
                relative.Split('/').Length > 1 && LegacyVersion(relative.Split('/')[1]) is not null ? 2 : 3).Any(IsMutableDirectory))
            throw new InvalidDataException("Obsolete program inventory names a mutable subtree.");
        if (directory) CortexCodexFileUpdate.RequireDirectory(path, allowMissing: true);
        else CortexCodexFileUpdate.RequireTarget(path, Path.GetFileName(path));
        return path;
    }

    private static bool IsMutableDirectory(string name) =>
        name.Equals("state", StringComparison.OrdinalIgnoreCase) || name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("users", StringComparison.OrdinalIgnoreCase) || name.Equals("backups", StringComparison.OrdinalIgnoreCase);

    private static void RequireImmutableFile(string relative)
    {
        CortexSignedProductPackageVerifier.RequireImmutableInstallPath(relative);
        if (Regex.IsMatch(Path.GetFileName(relative), @"\.(?:db|sqlite|sqlite3)(?:-(?:wal|shm|journal))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("Cleanup never adopts database files or their sidecars.");
    }

    private static void RequireProtectedMetadata(string path, string root, bool directory = false)
    {
        if (!directory) RequireMetadataAcl(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        for (var current = new DirectoryInfo(directory ? path : Path.GetDirectoryName(path)!); current is not null; current = current.Parent)
        {
            RequireMetadataAcl(current.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            if (string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new InvalidDataException("Historical cleanup metadata escaped the protected installed root.");
    }

    internal static void RequireMetadataAclForTest(FileSystemSecurity security) => RequireMetadataAcl(security);

    private static void RequireMetadataAcl(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner?.Value is not ("S-1-5-18" or "S-1-5-32-544") || descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("Cleanup inventory and catalog must be administrator-owned.");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("Cleanup evidence is writable by an unowned principal.");
    }

    private static FileStream OpenExclusiveForDeletion(string path)
    {
        var handle = CreateFileW(path, 0xc0010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error, "An obsolete program file is locked or not deletable; nothing was removed during preflight.");
        }
        try { return new FileStream(handle, FileAccess.ReadWrite); }
        catch { handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr attributes,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref FileDisposition information, uint size);
}
