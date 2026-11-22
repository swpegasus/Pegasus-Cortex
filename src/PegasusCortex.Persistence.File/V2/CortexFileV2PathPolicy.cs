using System.Text;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Persistence.File.V2;

internal static partial class CortexFileV2PathPolicy
{
    private const int MaximumWindowsPathCharacters = 240;
    private const int MaximumSegmentUtf8Bytes = 255;

    public static string ValidateExistingProjectRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!Path.IsPathFullyQualified(projectRoot))
            throw new ArgumentException("The project root must be an absolute path.", nameof(projectRoot));
        var root = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The file Cortex project root does not exist.");
        RejectExistingAncestry(root);
        ValidatePath(root);
        return root;
    }

    public static string ManifestPath(string root) => Contained(
        root,
        Path.Combine(root, ".cortex", "project.json"));

    public static string HotPath(string root) => Contained(
        root,
        Path.Combine(root, ".cortex", "HOT.json"));

    public static string DataRoot(string root) => Contained(
        root,
        Path.Combine(root, ".cortex", "v2"));

    public static string ObjectRoot(string root) => Contained(
        root,
        Path.Combine(DataRoot(root), "objects"));

    public static string GenerationsRoot(string root) => Contained(
        root,
        Path.Combine(DataRoot(root), "generations"));

    public static string CurrentPointerPath(string root) => Contained(
        root,
        Path.Combine(DataRoot(root), "current.json"));

    public static string WriteLockPath(string root) => Contained(
        root,
        Path.Combine(DataRoot(root), ".write.lock"));

    public static string OfflineRoot(string root) => Contained(
        root,
        Path.Combine(root, ".cortex", "offline"));

    public static string OfflineScopesRoot(string root) => Contained(
        root,
        Path.Combine(OfflineRoot(root), "scopes"));

    public static string OfflineScopeKey(string taskId)
    {
        CortexOfflineTaskIdentity.Require(taskId, nameof(taskId));
        return $"t-{CortexCanonicalJson.Sha256(taskId)[..32]}";
    }

    public static string OfflineScopeRoot(string root, string taskId) => Contained(
        root,
        Path.Combine(OfflineScopesRoot(root), OfflineScopeKey(taskId)));

    public static string OfflineScopeManifestPath(string root, string taskId) => Contained(
        root,
        Path.Combine(OfflineScopeRoot(root, taskId), "manifest.json"));

    public static string OfflineHotPath(string root, string taskId) => Contained(
        root,
        Path.Combine(OfflineScopeRoot(root, taskId), "HOT.json"));

    public static string OfflineFramesRoot(string root, string taskId) => Contained(
        root,
        Path.Combine(OfflineScopeRoot(root, taskId), "frames"));

    public static string OfflineWriteLockPath(string root, string taskId) => Contained(
        root,
        Path.Combine(OfflineScopeRoot(root, taskId), ".write.lock"));

    public static string OfflineFramePath(string root, string taskId, string frameId)
    {
        CortexFileV2Validation.RequireIdentifier(frameId, nameof(frameId));
        return Contained(root, Path.Combine(OfflineFramesRoot(root, taskId), $"{frameId}.json"));
    }

    public static string ObjectPath(string root, string sha256)
    {
        if (!Sha256Pattern().IsMatch(sha256))
            throw new ArgumentException("The object hash is invalid.", nameof(sha256));
        return Contained(root, Path.Combine(ObjectRoot(root), $"{sha256}.json"));
    }

    public static string GenerationPath(string root, long revision, string token)
    {
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (!TokenPattern().IsMatch(token))
            throw new ArgumentException("The generation token is invalid.", nameof(token));
        return Contained(
            root,
            Path.Combine(GenerationsRoot(root), $"g-{revision:D20}-{token[..8]}"));
    }

    public static string GenerationStagingPath(string root, string token)
    {
        if (!TokenPattern().IsMatch(token))
            throw new ArgumentException("The generation token is invalid.", nameof(token));
        return Contained(
            root,
            Path.Combine(GenerationsRoot(root), $".g-{token}.tmp"));
    }

    public static string PrepareOrdinaryDirectory(string path, string containmentRoot)
    {
        var root = Path.GetFullPath(containmentRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var candidate = string.Equals(root, full, comparison) ? root : Contained(root, full);
        if (System.IO.File.Exists(candidate))
            throw new InvalidDataException("A file occupies a required Cortex directory.");
        if (!Directory.Exists(candidate))
            Directory.CreateDirectory(candidate);
        RejectReparseAncestry(root, candidate);
        return candidate;
    }

    public static void PrepareStoreDirectories(string root)
    {
        var cortex = PrepareOrdinaryDirectory(Path.Combine(root, ".cortex"), root);
        var data = PrepareOrdinaryDirectory(DataRoot(root), cortex);
        PrepareOrdinaryDirectory(ObjectRoot(root), data);
        PrepareOrdinaryDirectory(GenerationsRoot(root), data);
    }

    public static void PrepareOfflineDirectories(string root, string taskId)
    {
        var cortex = PrepareOrdinaryDirectory(Path.Combine(root, ".cortex"), root);
        var offline = PrepareOrdinaryDirectory(OfflineRoot(root), cortex);
        var scopes = PrepareOrdinaryDirectory(OfflineScopesRoot(root), offline);
        var scope = PrepareOrdinaryDirectory(OfflineScopeRoot(root, taskId), scopes);
        PrepareOrdinaryDirectory(OfflineFramesRoot(root, taskId), scope);
    }

    public static void ValidateOfflineScopeLayout(string root, string taskId)
    {
        var validatedRoot = ValidateExistingProjectRoot(root);
        var scopeRoot = OfflineScopeRoot(validatedRoot, taskId);
        var manifest = OfflineScopeManifestPath(validatedRoot, taskId);
        var hot = OfflineHotPath(validatedRoot, taskId);
        var representativeFrame = OfflineFramePath(
            validatedRoot,
            taskId,
            "of_00000000000000000000000000000000");
        _ = OfflineWriteLockPath(validatedRoot, taskId);
        ValidateAtomicTemporaryPath(scopeRoot, manifest, new string('0', 32));
        ValidateAtomicTemporaryPath(scopeRoot, hot, new string('0', 32));
        ValidateAtomicTemporaryPath(scopeRoot, representativeFrame, new string('0', 32));
    }

    public static string ValidateAtomicTemporaryPath(
        string containmentRoot,
        string target,
        string token)
    {
        if (!TokenPattern().IsMatch(token))
            throw new ArgumentException("The atomic-write token is invalid.", nameof(token));
        var directory = Path.GetDirectoryName(target) ??
            throw new InvalidDataException("The Cortex document target has no directory.");
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(target)}.{token}.tmp");
        return Contained(containmentRoot, temporary);
    }

    /// <summary>
    /// Unscoped offline files cannot be assigned to a task safely. They must be
    /// handled by an explicit migration operation before scoped access begins.
    /// </summary>
    public static void RejectLegacyUnscopedOfflineState(string root)
    {
        var legacyHot = HotPath(root);
        if (System.IO.File.Exists(legacyHot) || Directory.Exists(legacyHot))
            throw LegacyOfflineConflict();

        var offline = OfflineRoot(root);
        if (!Directory.Exists(offline))
        {
            if (System.IO.File.Exists(offline))
                throw LegacyOfflineConflict();
            return;
        }
        RejectReparseAncestry(root, offline);
        foreach (var entry in new DirectoryInfo(offline).EnumerateFileSystemInfos())
        {
            if (!string.Equals(entry.Name, "scopes", StringComparison.Ordinal))
                throw LegacyOfflineConflict();
            if (!entry.Attributes.HasFlag(FileAttributes.Directory) ||
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw LegacyOfflineConflict();
            }
        }
    }

    public static void RejectFileReparsePoint(string path)
    {
        if (!System.IO.File.Exists(path))
            return;
        var attributes = System.IO.File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A Cortex file target is a link or reparse point.");
    }

    public static string RelativeToProject(string root, string path)
    {
        var candidate = Contained(root, path);
        return Path.GetRelativePath(root, candidate).Replace('\\', '/');
    }

    public static string FromProjectRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
            throw new InvalidDataException("A stored Cortex path is not project-relative.");
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment is "." or ".."))
            throw new InvalidDataException("A stored Cortex path contains traversal.");
        var candidate = Contained(root, Path.Combine(root, normalized));
        RejectReparseAncestry(root, candidate);
        return candidate;
    }

    private static string Contained(string root, string candidate)
    {
        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(candidate);
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(prefix, comparison))
            throw new InvalidDataException("A Cortex path escaped the selected project root.");
        ValidatePath(full);
        return full;
    }

    private static void RejectReparseAncestry(string root, string candidate)
    {
        RejectReparsePoint(root);
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || System.IO.File.Exists(current))
                RejectReparsePoint(current);
        }
    }

    private static void RejectExistingAncestry(string path)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(full) ??
            throw new ArgumentException("The Cortex path has no volume root.", nameof(path));
        var current = volumeRoot;
        if (Directory.Exists(current))
            RejectReparsePoint(current);
        foreach (var segment in full[volumeRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || System.IO.File.Exists(current))
                RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        var attributes = System.IO.File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A Cortex path crosses a link or reparse point.");
    }

    private static void ValidatePath(string path)
    {
        if (path.Length > MaximumWindowsPathCharacters)
            throw new PathTooLongException("A Cortex path exceeds 240 UTF-16 characters.");
        var root = Path.GetPathRoot(path) ?? throw new ArgumentException("The Cortex path has no root.");
        foreach (var segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.') ||
                Encoding.UTF8.GetByteCount(segment) > MaximumSegmentUtf8Bytes)
            {
                throw new ArgumentException("A Cortex path segment is not portable.");
            }
        }
    }

    private static InvalidOperationException LegacyOfflineConflict() => new(
        "Legacy project-wide offline state is present. An explicit task-scope migration is required; scoped offline access will not assign or merge it.");

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
