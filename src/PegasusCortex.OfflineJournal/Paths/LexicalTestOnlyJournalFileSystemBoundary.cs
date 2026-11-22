using Pegasus.Cortex.OfflineJournal.Contracts;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pegasus.Cortex.OfflineJournal.Paths;

/// <summary>
/// Path-based implementation retained only for the friend test assembly. Its checks are useful
/// for deterministic validation, but they are inherently subject to check/use races and must
/// never be accepted by a production constructor.
/// </summary>
internal sealed class LexicalTestOnlyJournalFileSystemBoundaryFactory : IJournalFileSystemBoundaryFactory
{
    public static LexicalTestOnlyJournalFileSystemBoundaryFactory Instance { get; } = new();

    private LexicalTestOnlyJournalFileSystemBoundaryFactory()
    {
    }

    public JournalFileSystemBoundarySafety Safety => JournalFileSystemBoundarySafety.LexicalTestOnly;

    public IJournalFileSystemBoundary OpenJournalRoot(string requestedRoot, string approvedRoot)
    {
        if (!JournalPathPolicy.TryValidateLexicalTestRoot(requestedRoot, approvedRoot, out var policy, out var reason))
            throw new ArgumentException(reason, nameof(requestedRoot));
        return new LexicalTestOnlyJournalFileSystemBoundary(
            policy!,
            JournalFileSystemBoundarySafety.LexicalTestOnly);
    }

    public ILegacySourceFileSystemBoundary OpenLegacySourceRoot(
        string requestedRoot,
        string approvedRoot) =>
        new LexicalTestOnlyLegacySourceFileSystemBoundary(
            requestedRoot,
            approvedRoot,
            JournalFileSystemBoundarySafety.LexicalTestOnly);
}

internal sealed class LexicalTestOnlyJournalFileSystemBoundary :
    IJournalFileSystemBoundary,
    IJournalDurableDirectoryPromotion
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly JournalPathPolicy _paths;
    private readonly JournalFileSystemBoundarySafety _safety;

    public LexicalTestOnlyJournalFileSystemBoundary(
        JournalPathPolicy paths,
        JournalFileSystemBoundarySafety safety)
    {
        _paths = paths;
        _safety = safety;
    }

    public JournalFileSystemBoundarySafety Safety => _safety;
    public JournalDurabilityCapabilities Durability { get; } = new(
        FileContentFlush: true,
        AtomicSameVolumePromotion: true,
        ParentDirectoryFlush: false,
        Guarantee: "The lexical test boundary flushes file contents before same-volume promotion but does not flush parent directories.");
    public string RootPath => _paths.Root;
    public string AuthorizedRootIdentity => $"lexical:{RootPath}";
    public string OpenedRootIdentity => AuthorizedRootIdentity;
    public bool DurableDirectoryPromotionSupported =>
        _safety == JournalFileSystemBoundarySafety.ProtectedLocalSingleUser &&
        OperatingSystem.IsWindows();

    public void EnsureDirectory(string relativePath)
    {
        var path = Resolve(relativePath);
        if (Directory.Exists(path))
        {
            EnsureDirectoryEntry(path);
            EnsurePrivateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
            EnsurePrivateDirectory(path);
        }
        _paths.EnsureExistingPathIsSafe(path);
    }

    public bool DirectoryExists(string relativePath) => Directory.Exists(Resolve(relativePath));

    public void CreateDirectory(string relativePath)
    {
        var path = Resolve(relativePath);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("The journal directory already exists.");
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        _paths.EnsureExistingPathIsSafe(path);
    }

    public bool FileExists(string relativePath) => File.Exists(Resolve(relativePath));

    public JournalFileSystemEntryKind GetEntryKind(string relativePath)
    {
        var path = Resolve(relativePath);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Journal entries cannot be links or reparse points.");
        return (attributes & FileAttributes.Directory) != 0
            ? JournalFileSystemEntryKind.Directory
            : JournalFileSystemEntryKind.RegularFile;
    }

    public FileStream AcquireExclusiveLock(string relativePath)
    {
        var path = Resolve(relativePath);
        if (File.Exists(path))
            EnsureRegularFileEntry(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 1,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFileMode;
        var stream = new FileStream(path, options);
        try
        {
            EnsureRegularFileEntry(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public FileStream CreateNewFile(string relativePath, int bufferSize, bool asynchronous)
    {
        var path = Resolve(relativePath);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = bufferSize,
            Options = asynchronous ? FileOptions.Asynchronous | FileOptions.SequentialScan : FileOptions.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFileMode;
        return new FileStream(path, options);
    }

    public FileStream OpenReadFile(
        string relativePath,
        int bufferSize,
        bool asynchronous,
        bool sequentialScan)
    {
        var path = Resolve(relativePath);
        EnsureRegularFileEntry(path);
        var options = FileOptions.None;
        if (asynchronous)
            options |= FileOptions.Asynchronous;
        if (sequentialScan)
            options |= FileOptions.SequentialScan;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, options);
        try
        {
            EnsureRegularFileEntry(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(
        string relativePath,
        int maximumEntries)
    {
        if (maximumEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var directory = Resolve(relativePath);
        EnsureDirectoryEntry(directory);
        var entries = new List<JournalFileSystemEntry>(Math.Min(maximumEntries, 256));
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (entries.Count == maximumEntries)
                throw new IOException("The directory exceeds its bounded enumeration limit.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Journal entries cannot be links or reparse points.");
            var kind = (attributes & FileAttributes.Directory) != 0
                ? JournalFileSystemEntryKind.Directory
                : JournalFileSystemEntryKind.RegularFile;
            entries.Add(new JournalFileSystemEntry(
                ToRelative(path),
                Path.GetFullPath(path),
                kind));
        }
        return Array.AsReadOnly(entries.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
    }

    public void MoveDirectory(string sourceRelativePath, string destinationRelativePath)
    {
        var source = Resolve(sourceRelativePath);
        var destination = Resolve(destinationRelativePath);
        EnsureDirectoryEntry(source);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("The destination already exists.");
        Directory.Move(source, destination);
        _paths.EnsureExistingPathIsSafe(destination);
    }

    public void MoveDirectoryDurably(
        string sourceRelativePath,
        string destinationRelativePath)
    {
        if (!DurableDirectoryPromotionSupported)
        {
            throw new NotSupportedException(
                "This path-based boundary has no truthful durable-promotion primitive on the active profile.");
        }

        var source = Resolve(sourceRelativePath);
        var destination = Resolve(destinationRelativePath);
        EnsureDirectoryEntry(source);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("The durable-promotion destination already exists.");
        if (!WindowsMoveFileEx(source, destination, MoveFileWriteThrough))
        {
            throw new IOException(
                "The Windows write-through directory promotion failed.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        _paths.EnsureExistingPathIsSafe(destination);
    }

    public void DeleteFile(string relativePath)
    {
        var path = Resolve(relativePath);
        EnsureRegularFileEntry(path);
        File.Delete(path);
    }

    public void DeleteEmptyDirectory(string relativePath)
    {
        var path = Resolve(relativePath);
        EnsureDirectoryEntry(path);
        Directory.Delete(path, recursive: false);
    }

    public void Dispose()
    {
    }

    private string Resolve(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return RootPath;
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (segments.Any(segment => !JournalPathPolicy.IsPortableIdentifier(segment, 128)))
            throw new IOException("A journal-relative path is invalid or unbounded.");
        return _paths.Resolve(segments);
    }

    private string ToRelative(string exactPath) =>
        Path.GetRelativePath(RootPath, Path.GetFullPath(exactPath)).Replace('\\', '/');

    private void EnsureDirectoryEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Journal directories cannot be links or reparse points.");
        _paths.EnsureExistingPathIsSafe(path);
    }

    private static void EnsureRegularFileEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Journal files must be regular non-reparse files.");
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        var mode = File.GetUnixFileMode(path);
        var nonOwner = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & nonOwner) != 0)
            throw new UnauthorizedAccessException("Existing journal-owned directories must be private to their owner.");
    }

    private const int MoveFileWriteThrough = 0x00000008;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsMoveFileEx(
        string existingFileName,
        string newFileName,
        int flags);
}

internal sealed class LexicalTestOnlyLegacySourceFileSystemBoundary : ILegacySourceFileSystemBoundary
{
    private readonly JournalFileSystemBoundarySafety _safety;

    public LexicalTestOnlyLegacySourceFileSystemBoundary(
        string requestedRoot,
        string approvedRoot,
        JournalFileSystemBoundarySafety safety)
    {
        RootPath = AuthorizeRoot(requestedRoot, approvedRoot);
        _safety = safety;
    }

    public JournalFileSystemBoundarySafety Safety => _safety;
    public string RootPath { get; }
    public string AuthorizedRootIdentity => $"lexical:{RootPath}";
    public string OpenedRootIdentity => AuthorizedRootIdentity;

    public IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(
        string relativePath,
        int maximumEntries)
    {
        if (maximumEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var directory = Resolve(relativePath, allowEmpty: true);
        EnsureNonReparseDirectory(directory);
        var entries = new List<JournalFileSystemEntry>(Math.Min(maximumEntries, 256));
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (entries.Count == maximumEntries)
                throw new IOException("The legacy source exceeds its bounded enumeration limit.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy imports cannot traverse links or reparse points.");
            var relative = Path.GetRelativePath(RootPath, Path.GetFullPath(path)).Replace('\\', '/');
            ValidateRelativePath(relative);
            entries.Add(new JournalFileSystemEntry(
                relative,
                Path.GetFullPath(path),
                (attributes & FileAttributes.Directory) != 0
                    ? JournalFileSystemEntryKind.Directory
                    : JournalFileSystemEntryKind.RegularFile));
        }
        return Array.AsReadOnly(entries.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
    }

    public FileStream OpenReadFile(
        string relativePath,
        int bufferSize,
        bool asynchronous,
        bool sequentialScan)
    {
        var path = Resolve(relativePath, allowEmpty: false);
        EnsureRegularFile(path);
        var options = FileOptions.None;
        if (asynchronous)
            options |= FileOptions.Asynchronous;
        if (sequentialScan)
            options |= FileOptions.SequentialScan;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, options);
        try
        {
            EnsureRegularFile(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
    }

    private string Resolve(string relativePath, bool allowEmpty)
    {
        if (allowEmpty && string.IsNullOrEmpty(relativePath))
            return RootPath;
        ValidateRelativePath(relativePath);
        var path = Path.GetFullPath(Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(RootPath + Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("The legacy-source path escapes its approved root.");
        return path;
    }

    private static string AuthorizeRoot(string requestedRoot, string approvedRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot) || string.IsNullOrWhiteSpace(approvedRoot) ||
            requestedRoot.Any(char.IsControl) || approvedRoot.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(requestedRoot) || !Path.IsPathFullyQualified(approvedRoot))
        {
            throw new ArgumentException("A fully-qualified requested and authority-approved legacy source root is required.");
        }
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var approved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        if (!string.Equals(requestedRoot, requested, StringComparison.Ordinal) ||
            !string.Equals(approvedRoot, approved, StringComparison.Ordinal) ||
            !string.Equals(requested, approved, StringComparison.Ordinal) ||
            !Directory.Exists(requested))
        {
            throw new ArgumentException("Requested and approved legacy source roots must be the same existing canonical path.");
        }
        for (var current = new DirectoryInfo(requested); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The legacy source root crosses a link or reparse point.");
        }
        EnsureNonReparseDirectory(requested);
        return requested;
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Any(char.IsControl) ||
            relativePath.Contains('\\') || relativePath.Contains(':') ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/', StringSplitOptions.None).Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new IOException("A legacy-source relative path is unsafe or cannot be represented by the v1 contract.");
        }
    }

    private static void EnsureNonReparseDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Legacy source traversal requires regular non-reparse directories.");
    }

    private static void EnsureRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Legacy source capture requires regular non-reparse files.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

}
