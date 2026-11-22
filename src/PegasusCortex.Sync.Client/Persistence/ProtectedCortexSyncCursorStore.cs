using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.Sync.Client.Contracts;

namespace Pegasus.Cortex.Sync.Client.Persistence;

/// <summary>
/// Append-only cursor checkpoints inside an already owner-private local root. This has the
/// same trust boundary as standalone Cortex: it authenticates the OS user, not individual
/// processes running as that user.
/// </summary>
public sealed class ProtectedCortexSyncCursorStore : ICortexSyncCursorStore, IAsyncDisposable
{
    private const int MaximumCursorBytes = 32 * 1024;
    private const int MaximumCheckpoints = 50_000;
    private const int MaximumRecoveryStages = 8;
    private const long MaximumRecoveryStageBytes = 2L * MaximumCursorBytes;
    private const string CursorFileName = "cursor.json";
    private readonly IJournalFileSystemBoundary _boundary;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ProtectedCortexSyncCursorStore(
        string requestedRoot,
        string approvedRoot,
        IJournalFileSystemBoundaryFactory? boundaryFactory = null)
    {
        var canonicalRoot = RequireExactRoot(requestedRoot, approvedRoot);
        var factory = boundaryFactory ?? ProtectedLocalJournalFileSystemBoundaryFactory.Instance;
        _boundary = factory.OpenJournalRoot(requestedRoot, approvedRoot);
        if (_boundary.Safety is not (
                JournalFileSystemBoundarySafety.HandleRelativeNoFollow or
                JournalFileSystemBoundarySafety.ProtectedLocalSingleUser) ||
            !string.Equals(_boundary.RootPath, canonicalRoot, PathComparison) ||
            string.IsNullOrWhiteSpace(_boundary.AuthorizedRootIdentity) ||
            !string.Equals(
                _boundary.AuthorizedRootIdentity,
                _boundary.OpenedRootIdentity,
                StringComparison.Ordinal) ||
            !_boundary.Durability.FileContentFlush ||
            !_boundary.Durability.AtomicSameVolumePromotion)
        {
            _boundary.Dispose();
            throw new UnauthorizedAccessException(
                "The cursor store requires the exact authority-approved protected local root.");
        }
        if (_boundary is not IJournalDurableDirectoryPromotion
            {
                DurableDirectoryPromotionSupported: true
            })
        {
            _boundary.Dispose();
            throw new NotSupportedException(
                "The cursor store requires crash-durable directory promotion before retirement.");
        }
    }

    public async ValueTask<CortexSyncCursorV1?> ReadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLock = OpenLayoutAndLock();
            return await ReadAndReconcileChainAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        CortexSyncCursorV1 cursor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cursor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            using var writerLock = OpenLayoutAndLock();
            var previous = await ReadAndReconcileChainAsync(cancellationToken).ConfigureAwait(false);
            if (previous is not null &&
                string.Equals(previous.CursorHash, cursor.CursorHash, StringComparison.Ordinal))
            {
                RequireSameCursor(previous, cursor);
                return;
            }
            CortexSyncCursorPolicy.Validate(cursor, previous);
            var final = CheckpointPath(cursor);
            if (_boundary.DirectoryExists(final))
            {
                var existing = await ReadCursorAsync(final, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(existing.CursorHash, cursor.CursorHash, StringComparison.Ordinal))
                    throw new InvalidDataException("A different cursor occupies the deterministic checkpoint path.");
                return;
            }

            RequireRecoveryStageCapacity();
            stage = $"c/s/c-{Guid.NewGuid():N}";
            _boundary.CreateDirectory(stage);
            var bytes = CortexCanonicalJson.Serialize(cursor);
            if (bytes.Length is 0 or > MaximumCursorBytes)
                throw new InvalidDataException("The canonical cursor exceeds its fixed local bound.");
            await using (var stream = _boundary.CreateNewFile(
                             $"{stage}/{CursorFileName}",
                             16 * 1024,
                             asynchronous: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            MoveDirectoryDurably(stage, final);
            stage = null;
            var persisted = await ReadCursorAsync(final, cancellationToken).ConfigureAwait(false);
            CortexSyncCursorPolicy.Validate(persisted, previous);
            if (!string.Equals(persisted.CursorHash, cursor.CursorHash, StringComparison.Ordinal) ||
                !CortexCanonicalJson.Serialize(persisted).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("The durable cursor checkpoint did not read back exactly.");
        }
        finally
        {
            if (stage is not null)
                CleanupStage(stage);
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _boundary.Dispose();
            _gate.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private FileStream OpenLayoutAndLock()
    {
        _boundary.EnsureDirectory(string.Empty);
        _boundary.EnsureDirectory("c");
        _boundary.EnsureDirectory("c/s");
        _boundary.EnsureDirectory("c/h");
        _boundary.EnsureDirectory("c/l");
        return _boundary.AcquireExclusiveLock("c/l/w.lock");
    }

    private async ValueTask<CortexSyncCursorV1?> ReadAndReconcileChainAsync(
        CancellationToken cancellationToken)
    {
        var history = await ReadHistoryAsync(cancellationToken).ConfigureAwait(false);
        var stageEntries = _boundary.EnumerateDirectory("c/s", MaximumRecoveryStages).ToArray();
        if (stageEntries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException("The cursor stage root contains an unexpected file.");

        var budget = new CursorStageBudget(MaximumRecoveryStageBytes);
        var stages = new List<CursorStageArtifact>(stageEntries.Length);
        foreach (var entry in stageEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leaf = Leaf(entry.RelativePath);
            if (!IsStageLeaf(leaf))
                throw new InvalidDataException("A cursor stage has an invalid lowercase-hex name.");
            var children = _boundary.EnumerateDirectory(entry.RelativePath, 2).ToArray();
            if (children.Length == 0)
            {
                stages.Add(new CursorStageArtifact(entry.RelativePath, null));
                continue;
            }
            stages.Add(new CursorStageArtifact(
                entry.RelativePath,
                await ReadCursorAsync(entry.RelativePath, cancellationToken, budget).ConfigureAwait(false)));
        }

        var historyByHash = history.ToDictionary(item => item.CursorHash, StringComparer.Ordinal);
        var stale = new List<CursorStageArtifact>();
        var candidates = new List<CursorStageArtifact>();
        foreach (var stage in stages.Where(item => item.Cursor is not null))
        {
            if (historyByHash.TryGetValue(stage.Cursor!.CursorHash, out var existing))
            {
                RequireSameCursor(existing, stage.Cursor);
                stale.Add(stage);
            }
            else
            {
                candidates.Add(stage);
            }
        }

        var distinctCandidates = candidates.Select(item => item.Cursor!.CursorHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctCandidates.Length > 1)
            throw new InvalidDataException("Multiple different cursor checkpoints occupy recovery stages.");
        if (candidates.Count > 1)
        {
            var exemplar = candidates[0].Cursor!;
            foreach (var duplicate in candidates.Skip(1))
                RequireSameCursor(exemplar, duplicate.Cursor!);
        }

        var previous = history.LastOrDefault();
        if (candidates.Count > 0)
            CortexSyncCursorPolicy.Validate(candidates[0].Cursor!, previous);

        // Every stage is validated before cleanup or promotion. Empty and already-checkpointed
        // stages are hints; a failed delete leaves bounded evidence for the next pass.
        foreach (var empty in stages.Where(item => item.Cursor is null))
            CleanupStage(empty.RelativePath);
        foreach (var duplicate in stale)
            CleanupStage(duplicate.RelativePath);

        if (candidates.Count == 0)
            return previous;

        var selected = candidates[0];
        var cursor = selected.Cursor!;
        var final = CheckpointPath(cursor);
        if (_boundary.DirectoryExists(final) || _boundary.FileExists(final))
            throw new InvalidDataException("A conflicting cursor checkpoint appeared during recovery.");
        MoveDirectoryDurably(selected.RelativePath, final);
        var persisted = await ReadCursorAsync(final, cancellationToken).ConfigureAwait(false);
        CortexSyncCursorPolicy.Validate(persisted, previous);
        RequireSameCursor(persisted, cursor);
        foreach (var duplicate in candidates.Skip(1))
            CleanupStage(duplicate.RelativePath);
        return persisted;
    }

    private async ValueTask<IReadOnlyList<CortexSyncCursorV1>> ReadHistoryAsync(
        CancellationToken cancellationToken)
    {
        var entries = _boundary.EnumerateDirectory("c/h", MaximumCheckpoints)
            .ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException("The append-only cursor checkpoint root contains an unexpected file.");
        var checkpoints = entries.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        var history = new List<CortexSyncCursorV1>(checkpoints.Length);
        CortexSyncCursorV1? previous = null;
        foreach (var entry in checkpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cursor = await ReadCursorAsync(entry.RelativePath, cancellationToken).ConfigureAwait(false);
            CortexSyncCursorPolicy.ValidateStored(cursor, previous);
            var expectedSuffix = $"{cursor.Revision:D16}-{cursor.CursorHash[..16]}";
            if (!string.Equals(Leaf(entry.RelativePath), expectedSuffix, StringComparison.Ordinal))
                throw new InvalidDataException("A cursor checkpoint directory is not bound to its revision and hash.");
            history.Add(cursor);
            previous = cursor;
        }
        return history;
    }

    private async ValueTask<CortexSyncCursorV1> ReadCursorAsync(
        string directory,
        CancellationToken cancellationToken,
        CursorStageBudget? budget = null)
    {
        var children = _boundary.EnumerateDirectory(directory, 2).ToArray();
        if (children.Length != 1 ||
            children[0].Kind != JournalFileSystemEntryKind.RegularFile ||
            !string.Equals(Leaf(children[0].RelativePath), CursorFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A cursor checkpoint must contain exactly one canonical cursor file.");
        }
        await using var stream = _boundary.OpenReadFile(
            $"{directory}/{CursorFileName}",
            16 * 1024,
            asynchronous: true,
            sequentialScan: true);
        if (stream.Length is <= 0 or > MaximumCursorBytes)
            throw new InvalidDataException("A cursor checkpoint is empty or exceeds its fixed bound.");
        budget?.Reserve(stream.Length);
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var cursor = CortexCanonicalJson.Deserialize<CortexSyncCursorV1>(bytes);
        if (!CortexCanonicalJson.Serialize(cursor).AsSpan().SequenceEqual(bytes))
        {
            if (string.Equals(
                    cursor.ContractId,
                    CortexSyncClientContractIds.CursorV1_0,
                    StringComparison.Ordinal))
            {
                throw CortexSyncCursorPolicy.LegacyUpgradeRequired();
            }
            throw new InvalidDataException("A cursor checkpoint is not canonical JSON.");
        }
        return cursor;
    }

    private void CleanupStage(string stage)
    {
        try
        {
            var file = $"{stage}/{CursorFileName}";
            if (_boundary.FileExists(file))
                _boundary.DeleteFile(file);
            if (_boundary.DirectoryExists(stage))
                _boundary.DeleteEmptyDirectory(stage);
        }
        catch
        {
            // Retain an owned stage for bounded operator recovery rather than masking the cause.
        }
    }

    private void MoveDirectoryDurably(string source, string destination)
    {
        if (_boundary is not IJournalDurableDirectoryPromotion
            {
                DurableDirectoryPromotionSupported: true
            } promotion)
        {
            throw new NotSupportedException(
                "The cursor boundary lost its durable-promotion capability.");
        }
        promotion.MoveDirectoryDurably(source, destination);
    }

    private void RequireRecoveryStageCapacity()
    {
        // Recount after reconciliation because best-effort cleanup may have retained every
        // bounded stage. Refuse the write before creating a ninth recovery artifact.
        var entries = _boundary.EnumerateDirectory("c/s", MaximumRecoveryStages).ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException("The cursor stage root contains an unexpected file.");
        if (entries.Length >= MaximumRecoveryStages)
        {
            throw new InvalidDataException(
                "Cursor recovery stages have no reserved capacity for a new checkpoint.");
        }
    }

    private static void RequireSameCursor(
        CortexSyncCursorV1 existing,
        CortexSyncCursorV1 expected)
    {
        if (!string.Equals(existing.CursorHash, expected.CursorHash, StringComparison.Ordinal) ||
            !CortexCanonicalJson.Serialize(existing).AsSpan().SequenceEqual(
                CortexCanonicalJson.Serialize(expected)))
        {
            throw new InvalidDataException("A different cursor occupies the same recovery identity.");
        }
    }

    private static string CheckpointPath(CortexSyncCursorV1 cursor) =>
        $"c/h/{cursor.Revision:D16}-{cursor.CursorHash[..16]}";

    private static bool IsStageLeaf(string value) =>
        value.Length == 34 &&
        value.StartsWith("c-", StringComparison.Ordinal) &&
        value.Skip(2).All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Leaf(string relativePath) =>
        relativePath[(relativePath.LastIndexOf('/') + 1)..];

    private static string RequireExactRoot(string requestedRoot, string approvedRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot) || string.IsNullOrWhiteSpace(approvedRoot) ||
            requestedRoot.Any(char.IsControl) || approvedRoot.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(requestedRoot) || !Path.IsPathFullyQualified(approvedRoot))
        {
            throw new ArgumentException("Cursor roots must be fully-qualified canonical local paths.");
        }
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var approved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        if (!string.Equals(requestedRoot, requested, PathComparison) ||
            !string.Equals(approvedRoot, approved, PathComparison) ||
            !string.Equals(requested, approved, PathComparison) ||
            OperatingSystem.IsWindows() && requested.Length > 240)
        {
            throw new ArgumentException("Requested and approved cursor roots must be the same canonical path.");
        }
        return requested;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record CursorStageArtifact(
        string RelativePath,
        CortexSyncCursorV1? Cursor);

    private sealed class CursorStageBudget(long remainingBytes)
    {
        private long _remainingBytes = remainingBytes;

        public void Reserve(long cursorBytes)
        {
            if (cursorBytes <= 0 || cursorBytes > _remainingBytes)
                throw new InvalidDataException("Cursor recovery stages exceed their total byte budget.");
            _remainingBytes -= cursorBytes;
        }
    }
}
