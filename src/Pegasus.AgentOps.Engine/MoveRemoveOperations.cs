using System.Buffers;
using System.Security.Cryptography;

namespace Pegasus.AgentOps.Engine;

public sealed record MoveRemoveOptions(
    IReadOnlyCollection<string>? RegisteredWorkspaceRoots = null,
    bool AllowRootTarget = false,
    Func<bool>? AuthorizeRootTarget = null,
    string? RecoveryRoot = null);

public sealed record MoveOperationResult(
    bool Ok,
    string? Error,
    string Source,
    string Destination,
    bool CrossVolume,
    bool Verified,
    bool SourceRemoved,
    bool Partial,
    bool RecoveryRequired,
    bool DestinationReplaced,
    long Copied);

public sealed record RemoveOperationResult(
    bool Ok,
    string? Error,
    string Target,
    bool Permanent,
    string? RecoveryPath,
    long Removed,
    bool Partial,
    bool RecoveryRequired);

/// <summary>
/// Isolates the platform decisions that cannot be reproduced reliably on a
/// single-volume test machine. The engine still owns path admission, copying,
/// verification, and the exact no-follow deletion callback.
/// </summary>
public interface IMoveRemovePlatform
{
    bool IsSameVolume(string source, string destination);

    Stream CreateDestinationFile(string destination, int bufferSize);

    void DeleteVerifiedSource(string source, Action deleteExact);
}

public sealed class WindowsMoveRemovePlatform : IMoveRemovePlatform
{
    public bool IsSameVolume(string source, string destination) =>
        MoveRemoveOperations.HaveSameVolume(source, destination);

    public Stream CreateDestinationFile(string destination, int bufferSize) =>
        new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.SequentialScan);

    public void DeleteVerifiedSource(string source, Action deleteExact) => deleteExact();
}

public sealed class MoveRemoveOperations
{
    private const string RecoveryDirectoryName = ".PegasusAgentOps-Recovery";
    private readonly IMoveRemovePlatform _platform;

    public MoveRemoveOperations(IMoveRemovePlatform? platform = null) =>
        _platform = platform ?? new WindowsMoveRemovePlatform();

    public MoveOperationResult Move(
        string source,
        string destination,
        string? policy = null,
        MoveRemoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new MoveRemoveOptions();
        var src = CanonicalizeExactPath(source);
        var dst = CanonicalizeExactPath(destination);
        var pol = NormalizeMovePolicy(policy);

        RejectUnsafeRelationship(src, dst);
        RequireRootTargetAuthorization([src, dst], options);
        RejectReparseAncestors(src);
        RejectReparseAncestors(dst);
        RequireExistingDestinationParent(dst);

        var sourceEntry = GetRequiredEntry(src);
        var destinationExists = TryGetEntry(dst, out var destinationEntry);
        if (destinationExists && pol != "replace") throw new AgentOpsException("collision");

        var crossVolume = !_platform.IsSameVolume(src, dst);
        if (!crossVolume)
            return MoveSameVolume(src, dst, sourceEntry, destinationEntry, destinationExists, cancellationToken);

        return MoveCrossVolume(src, dst, sourceEntry, destinationExists, cancellationToken);
    }

    public RemoveOperationResult Remove(
        string target,
        bool permanent = false,
        MoveRemoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new MoveRemoveOptions();
        var path = CanonicalizeExactPath(target);
        if (!permanent && IsVolumeRoot(path)) throw new AgentOpsException("precondition");
        RequireRootTargetAuthorization([path], options);
        RejectReparseAncestors(path);
        _ = GetRequiredEntry(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (permanent)
        {
            long removed = 0;
            try
            {
                DeleteEntryNoFollow(path, ref removed, cancellationToken);
                return new(true, null, path, true, null, removed, false, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(
                    false,
                    removed > 0 ? "remove_incomplete" : "remove_failed",
                    path,
                    true,
                    null,
                    removed,
                    removed > 0,
                    removed > 0);
            }
        }

        var recoveryRoot = ResolveRecoveryRoot(path, options.RecoveryRoot);
        RejectReparseAncestors(recoveryRoot);
        if (TryGetEntry(recoveryRoot, out var recoveryEntry))
        {
            if (!recoveryEntry.IsDirectory || recoveryEntry.IsReparsePoint)
                throw new AgentOpsException("reparse_boundary");
        }
        else
        {
            Directory.CreateDirectory(recoveryRoot);
        }

        var recoveryPath = AllocateRecoveryPath(recoveryRoot);
        try
        {
            MoveEntry(path, recoveryPath, GetRequiredEntry(path));
            return new(true, null, path, false, recoveryPath, 1, false, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "remove_failed", path, false, null, 0, false, false);
        }
    }

    internal static bool HaveSameVolume(string source, string destination)
    {
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(sourceRoot) || string.IsNullOrEmpty(destinationRoot)) return false;
        return StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(sourceRoot),
            Path.TrimEndingDirectorySeparator(destinationRoot));
    }

    private MoveOperationResult MoveSameVolume(
        string src,
        string dst,
        Entry sourceEntry,
        Entry destinationEntry,
        bool destinationExists,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (destinationExists && !sourceEntry.IsDirectory && !destinationEntry.IsDirectory)
        {
            try
            {
                File.Move(src, dst, overwrite: true);
                return SuccessfulMove(src, dst, crossVolume: false, verified: true, destinationReplaced: true, copied: 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FailedMove(src, dst, crossVolume: false, "move_failed", destinationReplaced: false, copied: 0, mutationStarted: false);
            }
        }

        var destinationReplaced = false;
        if (destinationExists)
        {
            long deleted = 0;
            try
            {
                DeleteEntryNoFollow(dst, ref deleted, cancellationToken);
                destinationReplaced = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FailedMove(src, dst, crossVolume: false, "replace_failed", destinationReplaced: deleted > 0, copied: 0, mutationStarted: deleted > 0);
            }
        }

        try
        {
            MoveEntry(src, dst, sourceEntry);
            return SuccessfulMove(src, dst, crossVolume: false, verified: true, destinationReplaced, copied: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailedMove(src, dst, crossVolume: false, "move_failed", destinationReplaced, copied: 0, mutationStarted: destinationReplaced);
        }
    }

    private MoveOperationResult MoveCrossVolume(
        string src,
        string dst,
        Entry sourceEntry,
        bool destinationExists,
        CancellationToken cancellationToken)
    {
        // Fully enumerate and hash the source before replacing any destination.
        // This also rejects every nested reparse entry before destructive work.
        var admittedManifest = BuildManifest(src, sourceEntry, cancellationToken);
        var destinationReplaced = false;
        long copied = 0;

        if (destinationExists)
        {
            long deleted = 0;
            try
            {
                DeleteEntryNoFollow(dst, ref deleted, cancellationToken);
                destinationReplaced = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FailedMove(src, dst, crossVolume: true, "replace_failed", destinationReplaced: deleted > 0, copied: 0, mutationStarted: deleted > 0);
            }
        }

        try
        {
            copied = CopyExact(src, dst, sourceEntry, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AgentOpsException)
        {
            return FailedMove(src, dst, crossVolume: true, "move_incomplete", destinationReplaced, copied, mutationStarted: true);
        }

        Manifest sourceAfter;
        Manifest destinationAfter;
        try
        {
            sourceAfter = BuildManifest(src, sourceEntry, cancellationToken);
            destinationAfter = BuildManifest(dst, GetRequiredEntry(dst), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AgentOpsException)
        {
            return FailedMove(src, dst, crossVolume: true, "verification_failed", destinationReplaced, copied, mutationStarted: true);
        }

        if (!admittedManifest.Equals(sourceAfter) || !sourceAfter.Equals(destinationAfter))
            return FailedMove(src, dst, crossVolume: true, "verification_failed", destinationReplaced, copied, mutationStarted: true);

        var deleteInvoked = 0;
        try
        {
            _platform.DeleteVerifiedSource(src, () =>
            {
                if (Interlocked.Exchange(ref deleteInvoked, 1) != 0)
                    throw new InvalidOperationException("source_cleanup_reentered");
                long removed = 0;
                DeleteEntryNoFollow(src, ref removed, cancellationToken);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!EntryStillExists(src))
                return SuccessfulMove(src, dst, crossVolume: true, verified: true, destinationReplaced, copied);
            return FailedMove(src, dst, crossVolume: true, "source_cleanup_failed", destinationReplaced, copied, mutationStarted: true, verified: true, recoveryRequired: true);
        }

        if (EntryStillExists(src))
            return FailedMove(src, dst, crossVolume: true, "source_cleanup_failed", destinationReplaced, copied, mutationStarted: true, verified: true, recoveryRequired: true);

        return SuccessfulMove(src, dst, crossVolume: true, verified: true, destinationReplaced, copied);
    }

    private static MoveOperationResult SuccessfulMove(
        string src,
        string dst,
        bool crossVolume,
        bool verified,
        bool destinationReplaced,
        long copied) =>
        new(true, null, src, dst, crossVolume, verified, true, false, false, destinationReplaced, copied);

    private static MoveOperationResult FailedMove(
        string src,
        string dst,
        bool crossVolume,
        string error,
        bool destinationReplaced,
        long copied,
        bool mutationStarted,
        bool verified = false,
        bool recoveryRequired = false) =>
        new(
            false,
            error,
            src,
            dst,
            crossVolume,
            verified,
            !EntryStillExists(src),
            mutationStarted || destinationReplaced || copied > 0 || verified,
            recoveryRequired,
            destinationReplaced,
            copied);

    private static string CanonicalizeExactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['*', '?']) >= 0)
            throw new AgentOpsException("invalid");
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }
    }

    private static string NormalizeMovePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy) || StringComparer.OrdinalIgnoreCase.Equals(policy, "fail")) return "fail";
        if (StringComparer.OrdinalIgnoreCase.Equals(policy, "replace")) return "replace";
        throw new AgentOpsException("invalid");
    }

    private static void RequireRootTargetAuthorization(IEnumerable<string> targets, MoveRemoveOptions options)
    {
        var roots = (options.RegisteredWorkspaceRoots ?? Array.Empty<string>())
            .Select(CanonicalizeExactPath)
            .ToArray();
        var protectedTarget = targets.Any(target => IsVolumeRoot(target) || roots.Any(root => PathsEqual(root, target)));
        if (!protectedTarget) return;
        if (!options.AllowRootTarget) throw new AgentOpsException("root_target");
        if (options.AuthorizeRootTarget is null || !options.AuthorizeRootTarget())
            throw new AgentOpsException("authorization");
    }

    private static bool IsVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && PathsEqual(root, path);
    }

    private static void RequireExistingDestinationParent(string destination)
    {
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent)) throw new AgentOpsException("precondition");
        var entry = GetRequiredEntry(parent);
        if (!entry.IsDirectory) throw new AgentOpsException("precondition");
        if (entry.IsReparsePoint) throw new AgentOpsException("reparse_boundary");
    }

    private static bool PathsEqual(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)));

    private static void RejectUnsafeRelationship(string source, string destination)
    {
        if (PathsEqual(source, destination) || IsStrictDescendant(source, destination) || IsStrictDescendant(destination, source))
            throw new AgentOpsException("precondition");
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != "." &&
            !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RejectReparseAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new AgentOpsException("invalid");
        var components = Path.GetRelativePath(root, full)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < components.Length - 1; index++)
        {
            current = Path.Combine(current, components[index]);
            if (!TryGetEntry(current, out var entry)) break;
            if (entry.IsReparsePoint) throw new AgentOpsException("reparse_boundary");
        }
    }

    private static Entry GetRequiredEntry(string path)
    {
        if (!TryGetEntry(path, out var entry)) throw new AgentOpsException("not_found");
        return entry;
    }

    private static bool TryGetEntry(string path, out Entry entry)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            entry = new Entry(
                (attributes & FileAttributes.Directory) != 0,
                (attributes & FileAttributes.ReparsePoint) != 0);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            entry = default;
            return false;
        }
    }

    private static bool EntryStillExists(string path)
    {
        try
        {
            return TryGetEntry(path, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An inaccessible source must never be reported as successfully removed.
            return true;
        }
    }

    private static void MoveEntry(string source, string destination, Entry sourceEntry)
    {
        if (sourceEntry.IsDirectory) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static string ResolveRecoveryRoot(string target, string? configuredRoot)
    {
        var root = configuredRoot is null
            ? Path.Combine(Path.GetDirectoryName(target)!, RecoveryDirectoryName)
            : CanonicalizeExactPath(configuredRoot);
        root = CanonicalizeExactPath(root);
        if (!HaveSameVolume(target, root)) throw new AgentOpsException("recovery_volume");
        if (PathsEqual(target, root) || IsStrictDescendant(target, root)) throw new AgentOpsException("precondition");
        return root;
    }

    private static string AllocateRecoveryPath(string recoveryRoot)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(
                recoveryRoot,
                $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}");
            if (!TryGetEntry(candidate, out _)) return candidate;
        }
        throw new AgentOpsException("collision");
    }

    private long CopyExact(string source, string destination, Entry sourceEntry, CancellationToken cancellationToken)
    {
        if (sourceEntry.IsReparsePoint) throw new AgentOpsException("reparse_boundary");
        if (!sourceEntry.IsDirectory)
        {
            CopyFile(source, destination, cancellationToken);
            return 1;
        }

        long copied = 0;
        Directory.CreateDirectory(destination);
        CopyDirectoryContents(source, destination, ref copied, cancellationToken);
        return copied;
    }

    private void CopyDirectoryContents(string source, string destination, ref long copied, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var child in OrderedChildren(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = GetRequiredEntry(child);
            if (entry.IsReparsePoint) throw new AgentOpsException("reparse_boundary");
            var target = Path.Combine(destination, Path.GetFileName(child));
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                CopyDirectoryContents(child, target, ref copied, cancellationToken);
            }
            else
            {
                CopyFile(child, target, cancellationToken);
                copied++;
            }
        }
    }

    private void CopyFile(string source, string destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            using var output = _platform.CreateDestinationFile(destination, buffer.Length);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            output.Flush();
            if (output is FileStream fileOutput) fileOutput.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static Manifest BuildManifest(string root, Entry rootEntry, CancellationToken cancellationToken)
    {
        if (rootEntry.IsReparsePoint) throw new AgentOpsException("reparse_boundary");
        if (!rootEntry.IsDirectory)
        {
            var info = new FileInfo(root);
            return new Manifest(false, [new ManifestEntry(".", false, info.Length, HashFile(root, cancellationToken))]);
        }

        var entries = new List<ManifestEntry>();
        BuildDirectoryManifest(root, root, entries, cancellationToken);
        return new Manifest(true, entries);
    }

    private static void BuildDirectoryManifest(
        string root,
        string current,
        List<ManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var child in OrderedChildren(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = GetRequiredEntry(child);
            if (entry.IsReparsePoint) throw new AgentOpsException("reparse_boundary");
            var relative = Path.GetRelativePath(root, child).Replace(Path.DirectorySeparatorChar, '/');
            if (entry.IsDirectory)
            {
                entries.Add(new ManifestEntry(relative, true, 0, string.Empty));
                BuildDirectoryManifest(root, child, entries, cancellationToken);
            }
            else
            {
                var info = new FileInfo(child);
                entries.Add(new ManifestEntry(relative, false, info.Length, HashFile(child, cancellationToken)));
            }
        }
    }

    private static IEnumerable<string> OrderedChildren(string directory) =>
        Directory.EnumerateFileSystemEntries(directory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal);

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hasher.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void DeleteEntryNoFollow(string path, ref long removed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
        if (isReparse)
        {
            if (isDirectory) Directory.Delete(path);
            else File.Delete(path);
            removed++;
            return;
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

        if (!isDirectory)
        {
            File.Delete(path);
            removed++;
            return;
        }

        foreach (var child in OrderedChildren(path).ToArray())
            DeleteEntryNoFollow(child, ref removed, cancellationToken);
        Directory.Delete(path);
        removed++;
    }

    private readonly record struct Entry(bool IsDirectory, bool IsReparsePoint);
    private sealed record ManifestEntry(string RelativePath, bool IsDirectory, long Length, string Sha256);

    private sealed record Manifest(bool IsDirectory, IReadOnlyList<ManifestEntry> Entries)
    {
        public bool Equals(Manifest? other) =>
            other is not null &&
            IsDirectory == other.IsDirectory &&
            Entries.SequenceEqual(other.Entries);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(IsDirectory);
            foreach (var entry in Entries) hash.Add(entry);
            return hash.ToHashCode();
        }
    }
}
