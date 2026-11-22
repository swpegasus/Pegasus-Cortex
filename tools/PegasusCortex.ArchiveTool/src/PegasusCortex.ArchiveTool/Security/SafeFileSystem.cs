using System.Buffers;
using System.Security.Cryptography;
using Pegasus.Cortex.ArchiveTool.Archive;
using Pegasus.Cortex.RootedIo;

namespace Pegasus.Cortex.ArchiveTool.Security;

/// <summary>Owns admitted parents and exact created identities until publication or cleanup completes.</summary>
internal sealed class SafeFileSystem : IDisposable
{
    internal static UnixFileMode ImplicitDirectoryMode => (UnixFileMode)0x1ed;
    private readonly ValidatedRequest _request;
    private readonly RootedDirectory _destination;
    private readonly RootedDirectory _inventoryParent;
    private readonly Dictionary<string, RootedDirectory.FileIdentity> _owned = new(StringComparer.Ordinal);
    private RootedDirectory? _tree;
    private RootedDirectory.RootedFile? _inventory;
    private string? _inventoryTemporaryPath;
    private bool _inventoryCommitted, _inventoryDeleted, _treeDeleted, _released;

    internal SafeFileSystem(ValidatedRequest request)
    {
        _request = request;
        try { _destination = RootedDirectory.OpenRoot(request.DestinationRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("destination_unsafe", "The destination root could not be pinned safely.", error); }
        try { _inventoryParent = RootedDirectory.OpenRoot(Path.GetDirectoryName(request.InventoryPath)!); }
        catch (Exception error)
        { _destination.Dispose(); throw Failure("inventory_path_invalid", "The inventory parent could not be pinned safely.", error); }
    }

    internal string CreatePrivateStagingDirectory()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var name = ".pct-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var path = Path.Combine(_request.DestinationRoot, name);
            if (path.Length > _request.Limits.MaximumCanonicalPathUtf16)
                throw Failure("destination_unsafe", "The destination root leaves insufficient path budget for safe staging.");
            try { _tree = _destination.OpenDirectory(name, createMissing: true, exclusive: true, mode: (UnixFileMode)0x1c0); return path; }
            catch (IOException error) when (RootedDirectory.IsCollision(error)) { }
        }
        throw Failure("destination_unsafe", "A private staging directory could not be allocated.");
    }

    internal void EnsureMaterializedPathsWithinBudget(string stagingRoot, IReadOnlyList<MaterializedEntryRecord> entries, int maximum)
    {
        foreach (var entry in entries)
            if (ArchivePathPolicy.ResolveEntryPath(stagingRoot, entry.RelativePath).Length > maximum)
                throw Failure("entry_path_over_budget", "An archive entry exceeds the staging path budget.");
    }

    internal void CreateMaterializedDirectories(IReadOnlyList<MaterializedEntryRecord> entries)
    {
        foreach (var entry in entries.Where(entry => entry.Kind == MaterializedEntryKind.Directory)
                     .OrderBy(entry => entry.RelativePath.Count(character => character == '/')).ThenBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            using var parent = OpenParent(entry.RelativePath);
            using var directory = parent.OpenDirectory(Leaf(entry.RelativePath), createMissing: true, exclusive: true, mode: (UnixFileMode)0x1c0);
            _owned.Add(entry.RelativePath, directory.Identity);
        }
    }

    private RootedDirectory OpenParent(string relative)
    {
        var parts = relative.Split('/');
        var tree = _tree ?? throw new InvalidOperationException("No staged tree is owned.");
        var current = tree.Retain();
        var prefix = string.Empty;
        try
        {
            for (var index = 0; index < parts.Length - 1; index++)
            {
                prefix = prefix.Length == 0 ? parts[index] : prefix + "/" + parts[index];
                var next = current.OpenDirectory(parts[index]);
                if (!_owned.TryGetValue(prefix, out var identity) || next.Identity != identity)
                { next.Dispose(); throw Failure("destination_unsafe", "A staged directory identity changed."); }
                current.Dispose(); current = next;
            }
            var result = current; current = null!; return result;
        }
        finally { current?.Dispose(); }
    }

    internal string WriteFileCreateNew(string relative, Stream source, long expectedLength, int index)
    {
        try
        {
            using var parent = OpenParent(relative);
            using var file = parent.CreateNew(Leaf(relative), (UnixFileMode)0x180);
            _owned.Add(relative, file.Identity);
            using var destination = file.OpenStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long written = 0;
                while (true)
                {
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    written = checked(written + read);
                    if (written > expectedLength) throw EntryFailure("preflight_mismatch", "An archive file exceeded its preflighted length.", index);
                    destination.Write(buffer, 0, read); hash.AppendData(buffer, 0, read);
                }
                if (written != expectedLength) throw EntryFailure("preflight_mismatch", "An archive file did not match its preflighted length.", index);
                destination.Flush(flushToDisk: true);
                return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        catch (ArchiveBoundaryException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { throw EntryFailure("extraction_io", "An archive file could not be materialized safely.", index, error); }
    }

    internal void ApplyModes(IReadOnlyList<MaterializedEntryRecord> entries)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var entry in entries.OrderBy(entry => entry.Kind == MaterializedEntryKind.Directory ? 1 : 0)
                     .ThenByDescending(entry => entry.RelativePath.Count(character => character == '/')))
        {
            try
            {
                using var parent = OpenParent(entry.RelativePath);
                if (entry.Kind == MaterializedEntryKind.Directory)
                {
                    using var directory = parent.OpenDirectory(Leaf(entry.RelativePath));
                    RequireOwned(entry.RelativePath, directory.Identity); directory.SetMode(entry.Mode);
                }
                else
                {
                    using var file = parent.OpenRead(Leaf(entry.RelativePath));
                    RequireOwned(entry.RelativePath, file.Identity); file.SetMode(entry.Mode);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { throw Failure("mode_apply_failed", "An extracted entry mode could not be applied.", error); }
        }
    }

    private void RequireOwned(string relative, RootedDirectory.FileIdentity identity)
    { if (!_owned.TryGetValue(relative, out var original) || original != identity) throw Failure("post_inventory_mismatch", "A staged entry identity changed."); }

    internal IReadOnlyList<MaterializedEntryRecord> InventoryAndVerify(IReadOnlyList<MaterializedEntryRecord> expected)
    {
        var byPath = expected.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var actual = new Dictionary<string, MaterializedEntryRecord>(StringComparer.Ordinal);
        Walk(_tree ?? throw new InvalidOperationException("No owned tree."), string.Empty);
        if (actual.Count != expected.Count) throw Failure("post_inventory_mismatch", "The extracted filesystem inventory differs from preflight.");
        return actual.Values.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();

        void Walk(RootedDirectory directory, string prefix)
        {
            foreach (var entry in directory.Enumerate())
            {
                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                if (entry.Reparse || !byPath.TryGetValue(relative, out var original))
                    throw Failure("post_inventory_mismatch", "The extracted filesystem inventory contains an unexpected entry.");
                RequireOwned(relative, entry.Identity);
                if (entry.Directory)
                {
                    if (original.Kind != MaterializedEntryKind.Directory) throw Failure("post_inventory_mismatch", "An extracted entry type changed.");
                    using var child = directory.OpenDirectory(entry.Name);
                    RequireOwned(relative, child.Identity);
                    var mode = OperatingSystem.IsWindows() ? original.Mode : child.GetMode();
                    if (mode != original.Mode) throw Failure("post_inventory_mismatch", "An extracted entry mode changed.");
                    actual.Add(relative, original); Walk(child, relative);
                }
                else
                {
                    if (original.Kind != MaterializedEntryKind.File) throw Failure("post_inventory_mismatch", "An extracted entry type changed.");
                    using var file = directory.OpenRead(entry.Name);
                    RequireOwned(relative, file.Identity);
                    using var stream = file.OpenStream();
                    var mode = OperatingSystem.IsWindows() ? original.Mode : file.GetMode();
                    if (stream.Length != original.Length || mode != original.Mode) throw Failure("post_inventory_mismatch", "An extracted file size or mode changed.");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    try
                    {
                        var remaining = original.Length;
                        while (remaining > 0)
                        {
                            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read == 0) throw Failure("post_inventory_mismatch", "An extracted file was truncated.");
                            hash.AppendData(buffer, 0, read); remaining -= read;
                        }
                        if (stream.ReadByte() != -1 || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), original.Sha256, StringComparison.Ordinal))
                            throw Failure("post_inventory_mismatch", "An extracted file hash changed.");
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
                    actual.Add(relative, original);
                }
            }
        }
    }

    internal void CommitDirectoryNoReplace()
    {
        try { _tree!.MoveNoReplace(_request.DestinationName); }
        catch (IOException error) when (RootedDirectory.IsCollision(error))
        { throw Failure("destination_exists", "The destination was created before the no-replace commit.", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { throw Failure("destination_commit_failed", "The staged destination could not be committed safely.", error); }
    }

    internal FileStream CreateInventoryStream()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var name = ".pcti-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            _inventoryTemporaryPath = Path.Combine(Path.GetDirectoryName(_request.InventoryPath)!, name);
            if (_inventoryTemporaryPath.Length > _request.Limits.MaximumCanonicalPathUtf16)
                throw Failure("inventory_path_invalid", "The inventory parent leaves insufficient path budget for safe staging.");
            try { _inventory = _inventoryParent.CreateNew(name, (UnixFileMode)0x180); return _inventory.OpenStream(); }
            catch (IOException error) when (RootedDirectory.IsCollision(error)) { }
        }
        throw Failure("inventory_path_invalid", "A private inventory staging path could not be allocated.");
    }

    internal void CommitInventoryNoReplace()
    {
        try { _inventory!.Publish(Path.GetFileName(_request.InventoryPath), replace: false); _inventoryCommitted = true; }
        catch (IOException error) when (RootedDirectory.IsCollision(error))
        { throw Failure("inventory_exists", "The inventory output was created before the no-replace commit.", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { throw Failure("inventory_write_failed", "The inventory output could not be committed safely.", error); }
        finally { if (_inventory?.WasPublished == true) _inventoryCommitted = true; }
    }

    internal void ReleaseOwnership() => _released = true;

    internal IReadOnlyList<Exception> Cleanup(ArchiveExtractionHooks hooks)
    {
        var failures = new List<Exception>();
        if (_released) return failures;
        if (_inventory is not null && !_inventoryDeleted)
            try
            {
                hooks.BeforeCleanupDelete?.Invoke(_inventoryCommitted ? _request.InventoryPath : _inventoryTemporaryPath!);
                _inventory.DeleteIfOwned(); _inventoryDeleted = true;
            }
            catch (Exception error) { failures.Add(error); }
        if (_tree is not null && !_treeDeleted)
            try { _tree.DeleteOwnedTree(_owned, hooks.BeforeCleanupDelete); _treeDeleted = true; }
            catch (Exception error) { failures.Add(error); }
        return failures;
    }

    public void Dispose()
    {
        _inventory?.Dispose(); _inventory = null;
        _tree?.Dispose(); _tree = null;
        _inventoryParent.Dispose(); _destination.Dispose();
    }

    private static string Leaf(string relative) => relative[(relative.LastIndexOf('/') + 1)..];
    private static ArchiveBoundaryException Failure(string code, string message, Exception? inner = null) => new(code, message, inner);
    private static ArchiveBoundaryException EntryFailure(string code, string message, int index, Exception? inner = null) => new(code, message, inner, index);
}
