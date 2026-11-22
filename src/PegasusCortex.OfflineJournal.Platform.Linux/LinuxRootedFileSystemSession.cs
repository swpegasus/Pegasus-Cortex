using System.Text;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.OfflineJournal.Paths;

namespace Pegasus.Cortex.OfflineJournal.Platform.Linux;

internal enum LinuxRootEntryPolicy
{
    JournalOwned,
    ReadOnlyLegacySource
}

/// <summary>
/// Owns one approved root descriptor and centralizes all openat2/statx validation. Wrappers expose
/// only the mutations appropriate to a journal or the reads appropriate to a legacy source.
/// </summary>
internal sealed class LinuxRootedFileSystemSession : IDisposable
{
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryType = 0x4000;
    private const ushort SymbolicLinkType = 0xa000;
    private const ushort PermissionMask = 0x01ff;
    private const ushort FullPermissionAndSpecialModeMask = 0x0fff; // 07777
    private const ushort PrivateDirectoryMode = 0x01c0; // 0700
    private const ushort PrivateFileMode = 0x0180; // 0600
    private const ushort NonOwnerWriteMode = 0x0012; // 0022
    private const int NotFound = 2;
    private const int AlreadyExists = 17;
    private const int MaximumRelativeUtf8Bytes = 2048;
    private const int MaximumPathSegments = 32;
    private const int MaximumEnumerationEntries = 100_000;

    private const ulong RootResolveFlags =
        LinuxNative.ResolveNoSymbolicLinks |
        LinuxNative.ResolveNoMagicLinks;

    private const ulong DescendantResolveFlags =
        LinuxNative.ResolveBeneath |
        LinuxNative.ResolveNoSymbolicLinks |
        LinuxNative.ResolveNoMagicLinks |
        LinuxNative.ResolveNoCrossDevice;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _lifetimeGate = new();
    private readonly LinuxRootEntryPolicy _entryPolicy;
    private readonly uint _effectiveUserId;
    private readonly ulong _rootMountId;
    private SafeFileHandle? _root;

    private LinuxRootedFileSystemSession(
        string rootPath,
        LinuxRootEntryPolicy entryPolicy,
        SafeFileHandle root,
        uint effectiveUserId,
        LinuxFileMetadata rootMetadata)
    {
        RootPath = rootPath;
        _entryPolicy = entryPolicy;
        _root = root;
        _effectiveUserId = effectiveUserId;
        _rootMountId = rootMetadata.MountId;
        RootIdentity = FormatIdentity(rootMetadata);
    }

    public string RootPath { get; }

    public string RootIdentity { get; }

    public static LinuxRootedFileSystemSession Open(
        string canonicalRoot,
        LinuxRootEntryPolicy entryPolicy,
        string operation)
    {
        var root = LinuxNative.OpenAt2(
            LinuxNative.AtCurrentWorkingDirectory,
            canonicalRoot,
            LinuxNative.OpenReadOnly |
            LinuxNative.OpenDirectory |
            LinuxNative.OpenNonBlocking |
            LinuxNative.OpenNoControllingTerminal |
            LinuxNative.OpenCloseOnExec |
            LinuxNative.OpenNoFollow,
            mode: 0,
            RootResolveFlags,
            operation);
        try
        {
            var userId = LinuxNative.GetEffectiveUserId(operation);
            var metadata = LinuxNative.StatFileDescriptor(root, operation);
            ValidateMetadata(
                metadata,
                JournalFileSystemEntryKind.Directory,
                entryPolicy,
                userId,
                expectedMountId: null,
                operation);
            return new LinuxRootedFileSystemSession(
                canonicalRoot,
                entryPolicy,
                root,
                userId,
                metadata);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public void EnsureDirectory(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath, allowEmpty: true, "ensure_directory");
        if (normalized.Length == 0)
        {
            UseRoot(root =>
            {
                ValidateOpened(root, JournalFileSystemEntryKind.Directory, "ensure_directory");
                return 0;
            });
            return;
        }

        var segments = normalized.Split('/');
        var current = string.Empty;
        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            var currentPath = current;
            UseRoot(root =>
            {
                using var existing = TryOpenAny(root, currentPath, "ensure_directory", out var errno);
                if (existing is not null)
                {
                    var (existingParentPath, _) = SplitParent(currentPath);
                    using var existingParent = OpenDirectory(
                        root,
                        existingParentPath,
                        "ensure_directory");
                    FlushAndConfirmDirectoryEntry(
                        root,
                        existingParent,
                        existing,
                        currentPath,
                        "ensure_directory");
                    return 0;
                }
                if (errno != NotFound)
                    throw LinuxJournalFileSystemException.FromErrno("ensure_directory", errno);

                var (parentPath, leaf) = SplitParent(currentPath);
                using var parent = OpenDirectory(root, parentPath, "ensure_directory");
                try
                {
                    LinuxNative.CreateDirectoryAt(parent, leaf, PrivateDirectoryMode, "ensure_directory");
                }
                catch (LinuxJournalFileSystemException exception)
                    when (exception.Cause == LinuxJournalIoCause.AlreadyExists)
                {
                    // Another cooperating writer may have created the same layout component.
                }

                // The mkdirat mutation (including a cooperating writer's EEXIST race) receives
                // an immediate persistence barrier even if later identity validation fails.
                LinuxNative.FlushDirectory(parent, "flush_parent_after_directory_create");
                using var created = OpenDirectory(root, currentPath, "ensure_directory");
                FlushAndConfirmDirectoryEntry(
                    root,
                    parent,
                    created,
                    currentPath,
                    "ensure_directory");
                return 0;
            });
        }
    }

    public void CreateDirectory(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath, allowEmpty: false, "create_directory");
        UseRoot(root =>
        {
            var (parentPath, leaf) = SplitParent(normalized);
            using var parent = OpenDirectory(root, parentPath, "create_directory");
            LinuxNative.CreateDirectoryAt(parent, leaf, PrivateDirectoryMode, "create_directory");
            LinuxNative.FlushDirectory(parent, "flush_parent_after_directory_create");
            using var created = OpenDirectory(root, normalized, "create_directory");
            FlushAndConfirmDirectoryEntry(
                root,
                parent,
                created,
                normalized,
                "create_directory");
            return 0;
        });
    }

    public bool EntryExists(string relativePath, JournalFileSystemEntryKind expectedKind, string operation)
    {
        var normalized = NormalizeRelative(relativePath, allowEmpty: true, operation);
        return UseRoot(root =>
        {
            using var entry = TryOpenAny(root, normalized, operation, out var errno);
            if (entry is null)
            {
                if (errno == NotFound)
                    return false;
                throw LinuxJournalFileSystemException.FromErrno(operation, errno);
            }

            var metadata = ValidateOpenedType(entry, operation);
            var actualKind = ToEntryKind(metadata, operation);
            if (actualKind != expectedKind)
                return false;
            ValidateMetadata(metadata, expectedKind, operation);
            return true;
        });
    }

    public JournalFileSystemEntryKind GetEntryKind(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath, allowEmpty: true, "get_entry_kind");
        return UseRoot(root =>
        {
            using var entry = OpenAny(root, normalized, "get_entry_kind");
            var metadata = ValidateOpenedType(entry, "get_entry_kind");
            var kind = ToEntryKind(metadata, "get_entry_kind");
            ValidateMetadata(metadata, kind, "get_entry_kind");
            return kind;
        });
    }

    public FileStream AcquireExclusiveLock(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath, allowEmpty: false, "acquire_lock");
        return UseRoot(root =>
        {
            var (parentPath, leaf) = SplitParent(normalized);
            using var parent = OpenDirectory(root, parentPath, "open_lock_parent");
            var file = OpenOrCreateLockFile(root, parent, normalized, leaf, out var created);

            try
            {
                if (created)
                    LinuxNative.FlushDirectory(parent, "flush_parent_after_lock_create");
                ValidateOpened(file, JournalFileSystemEntryKind.RegularFile, "acquire_lock");
                LinuxNative.AcquireExclusiveNonBlockingLock(file, "acquire_lock");
                return CreateFileStream(file, FileAccess.ReadWrite, bufferSize: 1, asynchronous: false);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        });
    }

    public FileStream CreateNewFile(string relativePath, int bufferSize, bool asynchronous)
    {
        if (bufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        var normalized = NormalizeRelative(relativePath, allowEmpty: false, "create_file");
        return UseRoot(root =>
        {
            var (parentPath, leaf) = SplitParent(normalized);
            using var parent = OpenDirectory(root, parentPath, "create_file");
            var file = LinuxNative.OpenAt2(
                LinuxNative.GetFileDescriptor(parent),
                leaf,
                LinuxNative.OpenWriteOnly |
                LinuxNative.OpenCreate |
                LinuxNative.OpenExclusive |
                LinuxNative.OpenNonBlocking |
                LinuxNative.OpenNoControllingTerminal |
                LinuxNative.OpenCloseOnExec |
                LinuxNative.OpenNoFollow,
                PrivateFileMode,
                DescendantResolveFlags,
                "create_file");
            try
            {
                LinuxNative.FlushDirectory(parent, "flush_parent_after_file_create");
                ValidateOpened(file, JournalFileSystemEntryKind.RegularFile, "create_file");
                return CreateFileStream(file, FileAccess.Write, bufferSize, asynchronous);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        });
    }

    public FileStream OpenReadFile(
        string relativePath,
        int bufferSize,
        bool asynchronous,
        bool sequentialScan)
    {
        if (bufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        var normalized = NormalizeRelative(relativePath, allowEmpty: false, "open_read_file");
        return UseRoot(root =>
        {
            using var prevalidated = OpenAny(root, normalized, "prevalidate_read_file");
            var expectedMetadata = ValidateOpened(
                prevalidated,
                JournalFileSystemEntryKind.RegularFile,
                "prevalidate_read_file");
            var file = LinuxNative.OpenAt2(
                LinuxNative.GetFileDescriptor(root),
                normalized,
                LinuxNative.OpenReadOnly |
                LinuxNative.OpenNonBlocking |
                LinuxNative.OpenNoControllingTerminal |
                LinuxNative.OpenCloseOnExec |
                LinuxNative.OpenNoFollow,
                mode: 0,
                DescendantResolveFlags,
                "open_read_file");
            try
            {
                var actualMetadata = ValidateOpened(
                    file,
                    JournalFileSystemEntryKind.RegularFile,
                    "open_read_file");
                RequireSameOpenedObject(expectedMetadata, actualMetadata, "open_read_file");
                if (sequentialScan)
                    LinuxNative.AdviseSequential(file, "advise_sequential_read");
                return CreateFileStream(file, FileAccess.Read, bufferSize, asynchronous);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        });
    }

    public IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(
        string relativePath,
        int maximumEntries)
    {
        if (maximumEntries is < 0 or > MaximumEnumerationEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var normalized = NormalizeRelative(relativePath, allowEmpty: true, "enumerate_directory");
        return UseRoot(root =>
        {
            using var directory = OpenDirectory(root, normalized, "enumerate_directory");
            var names = LinuxNative.EnumerateNames(directory, maximumEntries, "enumerate_directory");
            var results = new List<JournalFileSystemEntry>(names.Count);
            foreach (var name in names)
            {
                var child = CombineRelative(normalized, name, "enumerate_directory");
                using var entry = OpenAny(root, child, "enumerate_directory_entry");
                var metadata = ValidateOpenedType(entry, "enumerate_directory_entry");
                var kind = ToEntryKind(metadata, "enumerate_directory_entry");
                ValidateMetadata(metadata, kind, "enumerate_directory_entry");
                results.Add(new JournalFileSystemEntry(child, ExactPath(child), kind));
            }
            return Array.AsReadOnly(results.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
        });
    }

    public void MoveDirectory(string sourceRelativePath, string destinationRelativePath)
    {
        var source = NormalizeRelative(sourceRelativePath, allowEmpty: false, "move_directory");
        var destination = NormalizeRelative(destinationRelativePath, allowEmpty: false, "move_directory");
        UseRoot(root =>
        {
            using (var sourceEntry = OpenDirectory(root, source, "move_directory"))
                ValidateOpened(sourceEntry, JournalFileSystemEntryKind.Directory, "move_directory");
            var (sourceParentPath, sourceLeaf) = SplitParent(source);
            var (destinationParentPath, destinationLeaf) = SplitParent(destination);
            using var sourceParent = OpenDirectory(root, sourceParentPath, "move_directory");
            using var destinationParent = OpenDirectory(root, destinationParentPath, "move_directory");
            LinuxNative.RenameAtNoReplace(
                sourceParent,
                sourceLeaf,
                destinationParent,
                destinationLeaf,
                "move_directory");
            LinuxNative.FlushDirectory(sourceParent, "flush_source_parent_after_rename");
            LinuxNative.FlushDirectory(destinationParent, "flush_destination_parent_after_rename");
            return 0;
        });
    }

    public void DeleteFile(string relativePath) =>
        DeleteEntry(relativePath, JournalFileSystemEntryKind.RegularFile, flags: 0, "delete_file");

    public void DeleteEmptyDirectory(string relativePath) =>
        DeleteEntry(
            relativePath,
            JournalFileSystemEntryKind.Directory,
            LinuxNative.AtRemoveDirectory,
            "delete_empty_directory");

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            _root?.Dispose();
            _root = null;
        }
    }

    private void DeleteEntry(
        string relativePath,
        JournalFileSystemEntryKind expectedKind,
        int flags,
        string operation)
    {
        var normalized = NormalizeRelative(relativePath, allowEmpty: false, operation);
        UseRoot(root =>
        {
            using (var entry = OpenAny(root, normalized, operation))
                ValidateOpened(entry, expectedKind, operation);
            var (parentPath, leaf) = SplitParent(normalized);
            using var parent = OpenDirectory(root, parentPath, operation);
            LinuxNative.UnlinkAt(parent, leaf, flags, operation);
            LinuxNative.FlushDirectory(parent, $"flush_parent_after_{operation}");
            return 0;
        });
    }

    private void FlushAndConfirmDirectoryEntry(
        SafeFileHandle root,
        SafeFileHandle parent,
        SafeFileHandle openedDirectory,
        string relativePath,
        string operation)
    {
        var expectedMetadata = ValidateOpened(
            openedDirectory,
            JournalFileSystemEntryKind.Directory,
            operation);
        LinuxNative.FlushDirectory(parent, $"flush_parent_after_{operation}");
        using var confirmed = OpenDirectory(root, relativePath, operation);
        var actualMetadata = ValidateOpened(
            confirmed,
            JournalFileSystemEntryKind.Directory,
            operation);
        RequireSameOpenedObject(expectedMetadata, actualMetadata, operation);
    }

    private SafeFileHandle OpenOrCreateLockFile(
        SafeFileHandle root,
        SafeFileHandle parent,
        string normalizedPath,
        string leaf,
        out bool created)
    {
        const int maximumRaceRetries = 3;
        for (var attempt = 0; attempt <= maximumRaceRetries; attempt++)
        {
            using var prevalidated = TryOpenAny(
                root,
                normalizedPath,
                "prevalidate_lock_file",
                out var prevalidationErrno);
            if (prevalidated is not null)
            {
                var expectedMetadata = ValidateOpened(
                    prevalidated,
                    JournalFileSystemEntryKind.RegularFile,
                    "prevalidate_lock_file");
                var existing = TryOpenFileAt(
                    parent,
                    leaf,
                    LinuxNative.OpenReadWrite,
                    "open_lock_file",
                    out var openErrno);
                if (existing is null)
                {
                    if (openErrno == NotFound)
                        continue;
                    throw LinuxJournalFileSystemException.FromErrno("open_lock_file", openErrno);
                }
                try
                {
                    var actualMetadata = ValidateOpened(
                        existing,
                        JournalFileSystemEntryKind.RegularFile,
                        "open_lock_file");
                    RequireSameOpenedObject(expectedMetadata, actualMetadata, "open_lock_file");
                    created = false;
                    return existing;
                }
                catch
                {
                    existing.Dispose();
                    throw;
                }
            }

            if (prevalidationErrno != NotFound)
            {
                throw LinuxJournalFileSystemException.FromErrno(
                    "prevalidate_lock_file",
                    prevalidationErrno);
            }

            var newFile = LinuxNative.TryOpenAt2(
                LinuxNative.GetFileDescriptor(parent),
                leaf,
                LinuxNative.OpenReadWrite |
                LinuxNative.OpenCreate |
                LinuxNative.OpenExclusive |
                LinuxNative.OpenNonBlocking |
                LinuxNative.OpenNoControllingTerminal |
                LinuxNative.OpenCloseOnExec |
                LinuxNative.OpenNoFollow,
                PrivateFileMode,
                DescendantResolveFlags,
                "create_lock_file",
                out var createErrno);
            if (newFile is not null)
            {
                created = true;
                return newFile;
            }
            if (createErrno != AlreadyExists)
                throw LinuxJournalFileSystemException.FromErrno("create_lock_file", createErrno);
        }

        throw new LinuxJournalFileSystemException(
            "open_lock_file",
            LinuxJournalIoCause.IoFailure);
    }

    private SafeFileHandle OpenDirectory(SafeFileHandle root, string relativePath, string operation)
    {
        // Reopen "." instead of dup(root): dup shares the root open-file-description directory
        // offset, causing one fdopendir/readdir call to consume all later root enumerations.
        var path = relativePath.Length == 0 ? "." : relativePath;
        var handle = LinuxNative.OpenAt2(
            LinuxNative.GetFileDescriptor(root),
            path,
            LinuxNative.OpenReadOnly |
            LinuxNative.OpenDirectory |
            LinuxNative.OpenNonBlocking |
            LinuxNative.OpenNoControllingTerminal |
            LinuxNative.OpenCloseOnExec |
            LinuxNative.OpenNoFollow,
            mode: 0,
            DescendantResolveFlags,
            operation);
        try
        {
            ValidateOpened(handle, JournalFileSystemEntryKind.Directory, operation);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private SafeFileHandle OpenAny(SafeFileHandle root, string relativePath, string operation)
    {
        if (relativePath.Length == 0)
            return LinuxNative.Duplicate(root, operation);
        return LinuxNative.OpenAt2(
            LinuxNative.GetFileDescriptor(root),
            relativePath,
            LinuxNative.OpenPath |
            LinuxNative.OpenCloseOnExec |
            LinuxNative.OpenNoFollow,
            mode: 0,
            DescendantResolveFlags,
            operation);
    }

    private SafeFileHandle? TryOpenAny(
        SafeFileHandle root,
        string relativePath,
        string operation,
        out int errno)
    {
        if (relativePath.Length == 0)
        {
            errno = 0;
            return LinuxNative.Duplicate(root, operation);
        }
        return LinuxNative.TryOpenAt2(
            LinuxNative.GetFileDescriptor(root),
            relativePath,
            LinuxNative.OpenPath |
            LinuxNative.OpenCloseOnExec |
            LinuxNative.OpenNoFollow,
            mode: 0,
            DescendantResolveFlags,
            operation,
            out errno);
    }

    private static SafeFileHandle? TryOpenFileAt(
        SafeFileHandle parent,
        string leaf,
        ulong access,
        string operation,
        out int errno) =>
        LinuxNative.TryOpenAt2(
            LinuxNative.GetFileDescriptor(parent),
            leaf,
            access |
            LinuxNative.OpenNonBlocking |
            LinuxNative.OpenNoControllingTerminal |
            LinuxNative.OpenCloseOnExec |
            LinuxNative.OpenNoFollow,
            mode: 0,
            DescendantResolveFlags,
            operation,
            out errno);

    private LinuxFileMetadata ValidateOpenedType(SafeFileHandle handle, string operation)
    {
        var metadata = LinuxNative.StatFileDescriptor(handle, operation);
        if (metadata.MountId != _rootMountId)
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.CrossDevice);
        return metadata;
    }

    private static void RequireSameOpenedObject(
        LinuxFileMetadata expected,
        LinuxFileMetadata actual,
        string operation)
    {
        if (expected.DeviceMajor != actual.DeviceMajor ||
            expected.DeviceMinor != actual.DeviceMinor ||
            expected.MountId != actual.MountId ||
            expected.Inode != actual.Inode ||
            (expected.Mode & FileTypeMask) != (actual.Mode & FileTypeMask))
        {
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.IoFailure);
        }
    }

    private LinuxFileMetadata ValidateOpened(
        SafeFileHandle handle,
        JournalFileSystemEntryKind expectedKind,
        string operation)
    {
        var metadata = LinuxNative.StatFileDescriptor(handle, operation);
        ValidateMetadata(metadata, expectedKind, operation);
        return metadata;
    }

    private void ValidateMetadata(
        LinuxFileMetadata metadata,
        JournalFileSystemEntryKind expectedKind,
        string operation) =>
        ValidateMetadata(
            metadata,
            expectedKind,
            _entryPolicy,
            _effectiveUserId,
            _rootMountId,
            operation);

    private static void ValidateMetadata(
        LinuxFileMetadata metadata,
        JournalFileSystemEntryKind expectedKind,
        LinuxRootEntryPolicy entryPolicy,
        uint effectiveUserId,
        ulong? expectedMountId,
        string operation)
    {
        if (expectedMountId is not null && metadata.MountId != expectedMountId.Value)
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.CrossDevice);
        var kind = ToEntryKind(metadata, operation);
        if (kind != expectedKind)
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.InvalidEntryType);
        if (metadata.UserId != effectiveUserId)
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.OwnershipMismatch);

        if (entryPolicy == LinuxRootEntryPolicy.JournalOwned)
        {
            var permissions = (ushort)(metadata.Mode & FullPermissionAndSpecialModeMask);
            var expectedMode = expectedKind == JournalFileSystemEntryKind.Directory
                ? PrivateDirectoryMode
                : PrivateFileMode;
            if (permissions != expectedMode)
                throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.PermissionMismatch);
        }
        else if (((ushort)(metadata.Mode & PermissionMask) & NonOwnerWriteMode) != 0)
        {
            // Legacy capture is read-only and never chmods user data, but it rejects roots or
            // entries writable by another principal during the capture window.
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.PermissionMismatch);
        }

        if (expectedKind == JournalFileSystemEntryKind.RegularFile && metadata.LinkCount != 1)
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.HardLinkRejected);
    }

    private static JournalFileSystemEntryKind ToEntryKind(
        LinuxFileMetadata metadata,
        string operation)
    {
        var type = (ushort)(metadata.Mode & FileTypeMask);
        if (type == SymbolicLinkType)
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.LinkRejected);
        return type switch
        {
            RegularFileType => JournalFileSystemEntryKind.RegularFile,
            DirectoryType => JournalFileSystemEntryKind.Directory,
            _ => throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.InvalidEntryType)
        };
    }

    private static FileStream CreateFileStream(
        SafeFileHandle handle,
        FileAccess access,
        int bufferSize,
        bool asynchronous)
    {
        try
        {
            return new FileStream(handle, access, bufferSize, asynchronous);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private T UseRoot<T>(Func<SafeFileHandle, T> action)
    {
        lock (_lifetimeGate)
        {
            var root = _root;
            ObjectDisposedException.ThrowIf(root is null || root.IsClosed || root.IsInvalid, this);
            return action(root);
        }
    }

    private string ExactPath(string relativePath) =>
        relativePath.Length == 0
            ? RootPath
            : $"{RootPath}/{relativePath}";

    private static string CombineRelative(string parent, string leaf, string operation) =>
        NormalizeRelative(parent.Length == 0 ? leaf : $"{parent}/{leaf}", allowEmpty: false, operation);

    private static (string Parent, string Leaf) SplitParent(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0
            ? (string.Empty, relativePath)
            : (relativePath[..separator], relativePath[(separator + 1)..]);
    }

    private static string NormalizeRelative(string? value, bool allowEmpty, string operation)
    {
        if (value is null || value.Length == 0)
        {
            if (allowEmpty && value is not null)
                return string.Empty;
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.InvalidRelativePath);
        }
        if (value[0] == '/' || value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl))
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.InvalidRelativePath);

        string[] segments;
        try
        {
            segments = value.Split('/', StringSplitOptions.None);
            if (segments.Length > MaximumPathSegments ||
                StrictUtf8.GetByteCount(value) > MaximumRelativeUtf8Bytes ||
                segments.Any(segment =>
                    segment.Length == 0 ||
                    segment is "." or ".." ||
                    StrictUtf8.GetByteCount(segment) > 255))
            {
                throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.InvalidRelativePath);
            }
        }
        catch (EncoderFallbackException exception)
        {
            _ = exception;
            // Encoder exceptions include the rejected code unit and index. Do not attach that
            // path-derived diagnostic to the public, path-redacted exception graph.
            throw new LinuxJournalFileSystemException(
                operation,
                LinuxJournalIoCause.InvalidRelativePath);
        }
        return value;
    }

    private static string FormatIdentity(LinuxFileMetadata metadata) =>
        $"linux-statx:{metadata.DeviceMajor:x}:{metadata.DeviceMinor:x}:{metadata.MountId:x}:{metadata.Inode:x}:{metadata.UserId:x}";
}
