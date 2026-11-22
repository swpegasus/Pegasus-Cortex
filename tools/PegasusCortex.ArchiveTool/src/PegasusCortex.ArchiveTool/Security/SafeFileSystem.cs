using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pegasus.Cortex.ArchiveTool.Archive;

namespace Pegasus.Cortex.ArchiveTool.Security;

internal static class SafeFileSystem
{
    private const UnixFileMode DefaultImplicitDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    internal static string CreatePrivateStagingDirectory(ValidatedRequest request)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var name = $".pct-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
            var path = Path.Combine(request.DestinationRoot, name);
            if (path.Length > request.Limits.MaximumCanonicalPathUtf16)
                throw Failure("destination_unsafe", "The destination root leaves insufficient path budget for safe staging.");
            try
            {
                CreateDirectoryNew(path);
                ArchivePathPolicy.EnsurePathIsNotReparsePoint(path, "destination_unsafe");
                ArchivePathPolicy.EnsureExistingAncestorsAreNotReparsePoints(path, "destination_unsafe");
                return path;
            }
            catch (ArchiveBoundaryException exception) when (
                exception.Code == "path_already_exists")
            {
                // A random-name collision is retried without adopting the colliding object.
            }
        }

        throw Failure("destination_unsafe", "A private staging directory could not be allocated.");
    }

    internal static void CreateMaterializedDirectories(
        string stagingRoot,
        IReadOnlyList<MaterializedEntryRecord> entries)
    {
        foreach (var entry in entries
                     .Where(candidate => candidate.Kind == MaterializedEntryKind.Directory)
                     .OrderBy(candidate => SegmentCount(candidate.RelativePath))
                     .ThenBy(candidate => candidate.RelativePath, StringComparer.Ordinal))
        {
            var path = ArchivePathPolicy.ResolveEntryPath(stagingRoot, entry.RelativePath);
            if (ArchivePathPolicy.PathExistsNoFollow(path))
                throw Failure("preflight_mismatch", "A materialized archive directory is not unique.");

            var parent = Path.GetDirectoryName(path) ?? stagingRoot;
            ArchivePathPolicy.EnsureExistingAncestorsAreNotReparsePoints(parent, "destination_unsafe");
            CreateDirectoryNew(path);
            ArchivePathPolicy.EnsurePathIsNotReparsePoint(path, "destination_unsafe");
        }
    }

    internal static void EnsureMaterializedPathsWithinBudget(
        string stagingRoot,
        IReadOnlyList<MaterializedEntryRecord> entries,
        int maximumCanonicalPathUtf16)
    {
        foreach (var entry in entries)
        {
            var path = ArchivePathPolicy.ResolveEntryPath(stagingRoot, entry.RelativePath);
            if (path.Length > maximumCanonicalPathUtf16)
                throw Failure("entry_path_over_budget", "An archive entry exceeds the staging path budget.");
        }
    }

    internal static string WriteFileCreateNew(
        string path,
        Stream source,
        long expectedLength,
        int entryIndex)
    {
        var parent = Path.GetDirectoryName(path) ?? throw Failure(
            "extraction_io",
            "An extracted file has no destination parent.");
        ArchivePathPolicy.EnsureExistingAncestorsAreNotReparsePoints(parent, "destination_unsafe");

        byte[]? buffer = null;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var destination = new FileStream(path, options);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long written = 0;
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                written = checked(written + read);
                if (written > expectedLength)
                    throw EntryFailure("preflight_mismatch", "An archive file exceeded its preflighted length.", entryIndex);
                destination.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }

            if (written != expectedLength)
                throw EntryFailure("preflight_mismatch", "An archive file did not match its preflighted length.", entryIndex);
            destination.Flush(flushToDisk: true);
            ArchivePathPolicy.EnsurePathIsNotReparsePoint(path, "destination_unsafe");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw EntryFailure("extraction_io", "An archive file could not be materialized safely.", entryIndex, exception);
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static void ApplyModes(
        string stagingRoot,
        IReadOnlyList<MaterializedEntryRecord> entries)
    {
        if (OperatingSystem.IsWindows())
            return;

        // Files are set before directories so a restrictive directory cannot block its children.
        foreach (var entry in entries
                     .OrderBy(candidate => candidate.Kind == MaterializedEntryKind.Directory ? 1 : 0)
                     .ThenByDescending(candidate => SegmentCount(candidate.RelativePath)))
        {
            var path = ArchivePathPolicy.ResolveEntryPath(stagingRoot, entry.RelativePath);
            try
            {
                File.SetUnixFileMode(path, entry.Mode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw Failure("mode_apply_failed", "An extracted entry mode could not be applied.", exception);
            }
        }
    }

    internal static IReadOnlyList<MaterializedEntryRecord> InventoryAndVerify(
        string root,
        IReadOnlyList<MaterializedEntryRecord> expected)
    {
        ArchivePathPolicy.EnsurePathIsNotReparsePoint(root, "post_inventory_mismatch");
        ArchivePathPolicy.EnsureExistingAncestorsAreNotReparsePoints(root, "post_inventory_mismatch");

        var expectedByPath = expected.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var actualByPath = new Dictionary<string, MaterializedEntryRecord>(StringComparer.Ordinal);
        Walk(root, root, expectedByPath, actualByPath);

        if (actualByPath.Count != expectedByPath.Count)
            throw Failure("post_inventory_mismatch", "The extracted filesystem inventory differs from preflight.");
        foreach (var expectedEntry in expectedByPath.Values)
        {
            if (!actualByPath.TryGetValue(expectedEntry.RelativePath, out var actualEntry) ||
                actualEntry.Kind != expectedEntry.Kind ||
                actualEntry.Length != expectedEntry.Length ||
                !string.Equals(actualEntry.Sha256, expectedEntry.Sha256, StringComparison.Ordinal) ||
                actualEntry.Mode != expectedEntry.Mode)
            {
                throw Failure("post_inventory_mismatch", "The extracted filesystem inventory differs from preflight.");
            }
        }

        return actualByPath.Values.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    internal static void CommitDirectoryNoReplace(string stagingPath, string destinationPath)
    {
        if (ArchivePathPolicy.PathExistsNoFollow(destinationPath))
            throw Failure("destination_exists", "The destination already exists.");
        try
        {
            Directory.Move(stagingPath, destinationPath);
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (IOException exception) when (ArchivePathPolicy.PathExistsNoFollow(destinationPath))
        {
            throw Failure("destination_exists", "The destination was created before the no-replace commit.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure("destination_commit_failed", "The staged destination could not be committed safely.", exception);
        }
    }

    internal static string CreateInventoryTemporaryPath(string inventoryPath, int maximumCanonicalPathUtf16)
    {
        var parent = Path.GetDirectoryName(inventoryPath) ?? throw Failure(
            "inventory_path_invalid",
            "The inventory output has no parent directory.");
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(
                parent,
                $".pcti-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}");
            if (candidate.Length > maximumCanonicalPathUtf16)
                throw Failure("inventory_path_invalid", "The inventory parent leaves insufficient path budget for safe staging.");
            if (!ArchivePathPolicy.PathExistsNoFollow(candidate))
                return candidate;
        }
        throw Failure("inventory_path_invalid", "A private inventory staging path could not be allocated.");
    }

    internal static void CommitFileNoReplace(string temporaryPath, string destinationPath)
    {
        if (ArchivePathPolicy.PathExistsNoFollow(destinationPath))
            throw Failure("inventory_exists", "The inventory output already exists.");
        try
        {
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch (IOException exception) when (ArchivePathPolicy.PathExistsNoFollow(destinationPath))
        {
            throw Failure("inventory_exists", "The inventory output was created before the no-replace commit.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure("inventory_write_failed", "The inventory output could not be committed safely.", exception);
        }
    }

    internal static IReadOnlyList<Exception> DeleteTreeNoFollow(
        string? path,
        ArchiveExtractionHooks hooks)
    {
        if (string.IsNullOrEmpty(path))
            return [];

        var failures = new List<Exception>();
        DeleteOwnedPath(path, hooks, failures);
        return failures;
    }

    internal static UnixFileMode ImplicitDirectoryMode => DefaultImplicitDirectoryMode;

    private static void CreateDirectoryNew(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (NativeMethods.CreateDirectoryWindows(path, IntPtr.Zero))
                return;
            var error = Marshal.GetLastPInvokeError();
            if (error == 183)
                throw Failure("path_already_exists", "A no-replace directory path already exists.");
            throw Failure("directory_create_failed", "A no-replace directory could not be created.", new Win32Exception(error));
        }

        const uint ownerOnly = 0x1c0; // POSIX 0700; final validated modes are applied after extraction.
        if (NativeMethods.CreateDirectoryUnix(path, ownerOnly) == 0)
            return;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == 17)
            throw Failure("path_already_exists", "A no-replace directory path already exists.");
        throw Failure("directory_create_failed", "A no-replace directory could not be created.", new Win32Exception(errno));
    }

    private static void Walk(
        string root,
        string directory,
        IReadOnlyDictionary<string, MaterializedEntryRecord> expected,
        IDictionary<string, MaterializedEntryRecord> actual)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(
                directory,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false
                }).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("post_inventory_mismatch", "The extracted filesystem inventory could not be enumerated.", exception);
        }

        foreach (var child in children)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(child);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Failure("post_inventory_mismatch", "An extracted filesystem entry could not be inspected.", exception);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Device) != 0)
                throw Failure("post_inventory_mismatch", "The extracted filesystem contains a reparse or special entry.");

            var relative = Path.GetRelativePath(root, child).Replace(Path.DirectorySeparatorChar, '/');
            if (!expected.TryGetValue(relative, out var expectedEntry) || actual.ContainsKey(relative))
                throw Failure("post_inventory_mismatch", "The extracted filesystem inventory differs from preflight.");

            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (expectedEntry.Kind != MaterializedEntryKind.Directory)
                    throw Failure("post_inventory_mismatch", "The extracted filesystem entry type differs from preflight.");
                var mode = OperatingSystem.IsWindows() ? expectedEntry.Mode : File.GetUnixFileMode(child);
                actual.Add(relative, new MaterializedEntryRecord(
                    relative,
                    MaterializedEntryKind.Directory,
                    0,
                    mode,
                    null));
                Walk(root, child, expected, actual);
            }
            else
            {
                if (expectedEntry.Kind != MaterializedEntryKind.File)
                    throw Failure("post_inventory_mismatch", "The extracted filesystem entry type differs from preflight.");
                var file = new FileInfo(child);
                if (file.LinkTarget is not null)
                    throw Failure("post_inventory_mismatch", "The extracted filesystem contains a link.");
                var mode = OperatingSystem.IsWindows() ? expectedEntry.Mode : File.GetUnixFileMode(child);
                var hash = ComputeFileSha256(child);
                actual.Add(relative, new MaterializedEntryRecord(
                    relative,
                    MaterializedEntryKind.File,
                    file.Length,
                    mode,
                    hash));
            }
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void DeleteOwnedPath(
        string path,
        ArchiveExtractionHooks hooks,
        ICollection<Exception> failures)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception);
            return;
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
        if (isDirectory && !isReparse)
        {
            try
            {
                var children = Directory.EnumerateFileSystemEntries(
                    path,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false
                    }).ToArray();
                foreach (var child in children)
                    DeleteOwnedPath(child, hooks, failures);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }
        }

        try
        {
            hooks.BeforeCleanupDelete?.Invoke(path);
            if (isDirectory)
                Directory.Delete(path, recursive: false);
            else
                File.Delete(path);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static int SegmentCount(string relativePath) =>
        relativePath.Count(character => character == '/') + 1;

    private static ArchiveBoundaryException Failure(
        string code,
        string message,
        Exception? innerException = null) => new(code, message, innerException);

    private static ArchiveBoundaryException EntryFailure(
        string code,
        string message,
        int entryIndex,
        Exception? innerException = null) => new(code, message, innerException, entryIndex);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);

        [DllImport("libc", EntryPoint = "mkdir", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern int CreateDirectoryUnix(string path, uint mode);
    }
}
