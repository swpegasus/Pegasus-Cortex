using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.Sync.Client.Contracts;

namespace Pegasus.Cortex.Sync.Client.Persistence;

/// <summary>
/// One active immutable submit attempt inside an authority-approved owner-private root. Promotion
/// and retirement use a crash-durable, same-filesystem directory move. After the journal receipt
/// and cursor make acknowledgment durable, retired full-request bytes are bounded cleanup hints.
/// </summary>
public sealed class ProtectedCortexSyncPendingAttemptStore :
    ICortexSyncPendingAttemptStoreV1_1,
    IAsyncDisposable
{
    private const int MaximumAttemptFileBytes =
        ((CortexSyncPendingAttemptPolicy.MaximumCanonicalRequestBytes + 2) / 3 * 4) + (64 * 1024);
    private const string AttemptFileName = "attempt.json";
    private const string DurabilityMarkerFileName = "durability.json";
    private const string DurabilityMarkerContractId =
        "pegasus.cortex/sync-client-pending-durability/1.1";
    private const int MaximumDurabilityMarkerBytes = 4 * 1024;
    private const int MaximumRecoveryStages = 8;
    private const int MaximumRetiredAttempts = 16;
    private const int MaximumCleanupArtifacts = 32;
    private const long MaximumRecoveryEvidenceBytes =
        2L * (MaximumAttemptFileBytes + MaximumDurabilityMarkerBytes);
    private readonly IJournalFileSystemBoundary _boundary;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ProtectedCortexSyncPendingAttemptStore(
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
                "The pending-attempt store requires the exact authority-approved protected local root.");
        }
        if (_boundary is not IJournalDurableDirectoryPromotion
            {
                DurableDirectoryPromotionSupported: true
            })
        {
            _boundary.Dispose();
            throw new NotSupportedException(
                "The pending-attempt store requires crash-durable directory promotion before upload.");
        }
    }

    public async ValueTask<CortexSyncPendingSubmitAttempt?> ReadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLock = OpenLayoutAndLock();
            _ = await ReconcileArtifactsAsync(cancellationToken).ConfigureAwait(false);
            return await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<CortexSyncPendingAttemptSaveResult> SaveAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CortexSyncPendingAttemptPolicy.Validate(attempt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            using var writerLock = OpenLayoutAndLock();
            _ = await ReconcileArtifactsAsync(cancellationToken).ConfigureAwait(false);
            var activeRead = await ReadActiveWithDurabilityAsync(cancellationToken).ConfigureAwait(false);
            if (activeRead is not null)
            {
                RequireSameAttempt(activeRead.Attempt, attempt);
                if (!activeRead.DurableForReplay)
                {
                    // A pre-1.1 active directory is readable evidence, not a durability proof.
                    // Copy it into a fully flushed marked stage, durably move the old directory
                    // out of replay authority, and then durably promote the marked replacement.
                    RequireArtifactCapacity(
                        "a/s",
                        MaximumRecoveryStages,
                        requiredSlots: 1,
                        "pending-attempt recovery stages");
                    RequireArtifactCapacity(
                        "a/x",
                        MaximumCleanupArtifacts,
                        requiredSlots: 1,
                        "pending-attempt cleanup artifacts");

                    stage = $"a/s/a-{Guid.NewGuid():N}";
                    var legacyActivePath = $"a/p/{attempt.Evidence.AttemptId}";
                    var legacyCleanup = StageCleanupPath(stage);
                    if (_boundary.DirectoryExists(legacyCleanup) || _boundary.FileExists(legacyCleanup))
                        throw new InvalidDataException("A conflicting legacy-attempt cleanup artifact already exists.");
                    _boundary.CreateDirectory(stage);
                    await WriteDurableStageAsync(stage, attempt, cancellationToken).ConfigureAwait(false);

                    // From the first durable move onward, the stage is recovery authority. Never
                    // best-effort-delete it when a move reports an uncertain failure.
                    var legacyRecoveryStage = stage;
                    stage = null;
                    MoveDirectoryDurably(legacyActivePath, legacyCleanup);
                    MoveDirectoryDurably(legacyRecoveryStage, legacyActivePath);
                    var resealed = await ReadActiveWithDurabilityAsync(cancellationToken).ConfigureAwait(false) ??
                        throw new InvalidDataException("The re-sealed pending attempt disappeared after durable promotion.");
                    RequireSameAttempt(resealed.Attempt, attempt);
                    if (!resealed.DurableForReplay)
                        throw new InvalidDataException("The re-sealed pending attempt has no verified 1.1 durability marker.");
                    _ = TryCleanupOwnedDirectory(legacyCleanup);
                }
                return DurableSaveResult(attempt);
            }

            var final = $"a/p/{attempt.Evidence.AttemptId}";
            var retired = $"a/r/{attempt.Evidence.AttemptId}";
            if (_boundary.DirectoryExists(retired))
                throw new InvalidDataException("A retired attempt cannot be silently reused as a new submit.");
            if (_boundary.DirectoryExists(final))
                throw new InvalidDataException("The active attempt layout changed during the protected write.");

            RequireArtifactCapacity(
                "a/s",
                MaximumRecoveryStages,
                requiredSlots: 1,
                "pending-attempt recovery stages");
            stage = $"a/s/a-{Guid.NewGuid():N}";
            _boundary.CreateDirectory(stage);
            await WriteDurableStageAsync(stage, attempt, cancellationToken).ConfigureAwait(false);
            var recoveryStage = stage;
            stage = null;
            MoveDirectoryDurably(recoveryStage, final);
            var persisted = await ReadActiveWithDurabilityAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("The pending attempt disappeared after durable promotion.");
            RequireSameAttempt(persisted.Attempt, attempt);
            if (!persisted.DurableForReplay)
                throw new InvalidDataException("The pending attempt has no verified 1.1 durability marker.");
            return DurableSaveResult(attempt);
        }
        finally
        {
            if (stage is not null)
                CleanupStage(stage);
            _gate.Release();
        }
    }

    public async ValueTask RetireAsync(
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CortexSyncPendingAttemptPolicy.Validate(attempt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writerLock = OpenLayoutAndLock();
            var recovery = await ReconcileArtifactsAsync(cancellationToken).ConfigureAwait(false);
            var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
            if (active is null)
            {
                if (recovery.RetiredGuards.Any(guard => GuardMatches(guard, attempt)))
                    return;
                throw new InvalidDataException("The pending attempt disappeared before durable retirement.");
            }
            RequireSameAttempt(active, attempt);
            var retired = $"a/r/{attempt.Evidence.AttemptId}";
            if (_boundary.DirectoryExists(retired))
                throw new InvalidDataException("Conflicting retained evidence blocks pending-attempt retirement.");
            RequireArtifactCapacity(
                "a/x",
                MaximumCleanupArtifacts,
                requiredSlots: 1,
                "pending-attempt cleanup artifacts");
            MoveDirectoryDurably($"a/p/{attempt.Evidence.AttemptId}", retired);
            QuarantineRetiredAttempt(retired, attempt);
            CleanupQuarantine();
        }
        finally
        {
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
        _boundary.EnsureDirectory("a");
        _boundary.EnsureDirectory("a/s");
        _boundary.EnsureDirectory("a/p");
        _boundary.EnsureDirectory("a/r");
        _boundary.EnsureDirectory("a/x");
        _boundary.EnsureDirectory("a/l");
        return _boundary.AcquireExclusiveLock("a/l/w.lock");
    }

    private async ValueTask WriteDurableStageAsync(
        string stage,
        CortexSyncPendingSubmitAttempt attempt,
        CancellationToken cancellationToken)
    {
        var attemptBytes = CortexCanonicalJson.Serialize(attempt.Evidence);
        if (attemptBytes.Length is <= 0 or > MaximumAttemptFileBytes)
            throw new InvalidDataException("The canonical pending-attempt evidence exceeds its fixed local bound.");
        await WriteFlushedFileAsync(
                $"{stage}/{AttemptFileName}",
                attemptBytes,
                cancellationToken)
            .ConfigureAwait(false);

        var marker = new PendingAttemptDurabilityMarker(
            SchemaVersion: 1,
            ContractId: DurabilityMarkerContractId,
            Canonicalization: CortexSyncClientContractIds.Canonicalization,
            AttemptId: attempt.Evidence.AttemptId,
            AttemptHash: attempt.Evidence.AttemptHash);
        var markerBytes = CortexCanonicalJson.Serialize(marker);
        if (markerBytes.Length is <= 0 or > MaximumDurabilityMarkerBytes)
            throw new InvalidDataException("The pending-attempt durability marker exceeds its fixed local bound.");
        await WriteFlushedFileAsync(
                $"{stage}/{DurabilityMarkerFileName}",
                markerBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask WriteFlushedFileAsync(
        string relativePath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = _boundary.CreateNewFile(
            relativePath,
            16 * 1024,
            asynchronous: true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async ValueTask<RecoveryReconciliation> ReconcileArtifactsAsync(
        CancellationToken cancellationToken)
    {
        // Load and validate every bounded recovery artifact before mutating any of them. This
        // prevents lexical ordering from promoting one request before a conflicting later stage
        // is discovered.
        var budget = new RecoveryEvidenceBudget(MaximumRecoveryEvidenceBytes);
        var retired = await LoadRetiredArtifactsAsync(budget, cancellationToken).ConfigureAwait(false);
        var quarantine = await LoadQuarantineArtifactsAsync(budget, cancellationToken).ConfigureAwait(false);
        var stages = await LoadStageArtifactsAsync(budget, cancellationToken).ConfigureAwait(false);

        var guards = retired.Select(item => item.Guard!)
            .Concat(quarantine.Where(item => item.Guard is not null).Select(item => item.Guard!))
            .ToArray();
        foreach (var group in guards.GroupBy(item => item.AttemptId, StringComparer.Ordinal))
        {
            var knownHashes = group.Where(item => item.AttemptHash is not null)
                .Select(item => item.AttemptHash!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (knownHashes.Length > 1)
                throw new InvalidDataException("Conflicting retired guards exist for one attempt identifier.");
        }

        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null && guards.Any(guard =>
                string.Equals(guard.AttemptId, active.Evidence.AttemptId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Active and retired evidence exist for the same submit attempt.");
        }

        var completeStages = stages.Where(item => item.Attempt is not null).ToArray();
        var distinctStageHashes = completeStages.Select(item => item.Attempt!.Evidence.AttemptHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctStageHashes.Length > 1)
            throw new InvalidDataException("Multiple different submit attempts occupy the recovery stages.");
        if (completeStages.Length > 1)
        {
            var exemplar = completeStages[0].Attempt!;
            foreach (var duplicate in completeStages.Skip(1))
                RequireSameAttempt(exemplar, duplicate.Attempt!);
        }

        var staleStages = new List<RecoveryArtifact>();
        var candidateStages = new List<RecoveryArtifact>();
        foreach (var stage in completeStages)
        {
            var matchingGuards = guards.Where(guard => string.Equals(
                    guard.AttemptId,
                    stage.Attempt!.Evidence.AttemptId,
                    StringComparison.Ordinal))
                .ToArray();
            if (matchingGuards.Length > 0)
            {
                if (matchingGuards.Any(guard => !GuardMatches(guard, stage.Attempt!)))
                    throw new InvalidDataException("A staged request conflicts with retained retirement evidence.");
                staleStages.Add(stage);
            }
            else
            {
                candidateStages.Add(stage);
            }
        }

        if (active is not null)
        {
            foreach (var candidate in candidateStages)
                RequireSameAttempt(active, candidate.Attempt!);
        }

        // Keep the first candidate only when no active attempt exists; it is the stage promoted
        // below. Build the bounded list explicitly so an empty candidate set does not depend on
        // Skip/Concat iterator count optimizations during materialization.
        var stagesToQuarantine = new List<RecoveryArtifact>(staleStages);
        var firstCandidateToQuarantine = active is null ? 1 : 0;
        for (var index = firstCandidateToQuarantine; index < candidateStages.Count; index++)
            stagesToQuarantine.Add(candidateStages[index]);
        var plannedCleanupPaths = stagesToQuarantine
            .Select(item => StageCleanupPath(item.RelativePath))
            .Concat(retired.Select(RetiredCleanupPath))
            .ToArray();
        var existingCleanupPaths = quarantine.Select(item => item.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        if (plannedCleanupPaths.Distinct(StringComparer.Ordinal).Count() != plannedCleanupPaths.Length ||
            plannedCleanupPaths.Any(existingCleanupPaths.Contains))
        {
            throw new InvalidDataException(
                "Pending-attempt reconciliation found a conflicting cleanup destination.");
        }
        if (quarantine.Count + plannedCleanupPaths.Length > MaximumCleanupArtifacts)
        {
            throw new InvalidDataException(
                "Pending-attempt reconciliation would exceed its cleanup-artifact budget.");
        }

        // Empty stages contain no request authority and are best-effort cleanup hints. Complete
        // stale/duplicate stages first move durably out of the promotable namespace so a power
        // loss cannot resurrect them after a retired guard is removed.
        foreach (var empty in stages.Where(item => item.Attempt is null))
            _ = TryCleanupOwnedDirectory(empty.RelativePath);
        foreach (var stale in staleStages)
            QuarantineStage(stale.RelativePath);

        if (active is not null)
        {
            foreach (var duplicate in candidateStages)
                QuarantineStage(duplicate.RelativePath);
        }
        else if (candidateStages.Count > 0)
        {
            var selected = candidateStages[0];
            var final = $"a/p/{selected.Attempt!.Evidence.AttemptId}";
            if (_boundary.DirectoryExists(final))
                throw new InvalidDataException("The active attempt changed during stage recovery.");
            MoveDirectoryDurably(selected.RelativePath, final);
            var persisted = await ReadAttemptAsync(final, cancellationToken).ConfigureAwait(false);
            RequireSameAttempt(persisted, selected.Attempt);
            foreach (var duplicate in candidateStages.Skip(1))
                QuarantineStage(duplicate.RelativePath);
        }

        // Only after every related stage is durably outside a/s may retired full-request evidence
        // leave a/r. Cleanup itself is a hint; a failed delete remains bounded under a/x and never
        // becomes replay authority. Journal receipt/cursor proof lives outside this layout.
        foreach (var artifact in retired)
            QuarantineRetiredArtifact(artifact);
        CleanupQuarantine();
        return new RecoveryReconciliation(guards);
    }

    private async ValueTask<IReadOnlyList<RecoveryArtifact>> LoadRetiredArtifactsAsync(
        RecoveryEvidenceBudget budget,
        CancellationToken cancellationToken)
    {
        var entries = _boundary.EnumerateDirectory("a/r", MaximumRetiredAttempts).ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException("The retired-attempt root contains unexpected non-directory evidence.");

        var artifacts = new List<RecoveryArtifact>(entries.Length);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptId = Leaf(entry.RelativePath);
            if (!IsAttemptId(attemptId))
                throw new InvalidDataException("A retired attempt has an invalid bounded identifier.");
            var children = _boundary.EnumerateDirectory(entry.RelativePath, 2).ToArray();
            if (children.Length == 0)
            {
                artifacts.Add(new RecoveryArtifact(
                    entry.RelativePath,
                    null,
                    0,
                    new RetiredGuard(attemptId, null)));
                continue;
            }
            var read = await ReadAttemptWithSizeAsync(
                    entry.RelativePath,
                    cancellationToken,
                    budget)
                .ConfigureAwait(false);
            if (!string.Equals(attemptId, read.Attempt.Evidence.AttemptId, StringComparison.Ordinal))
                throw new InvalidDataException("Retired attempt evidence is not bound to its identifier.");
            artifacts.Add(new RecoveryArtifact(
                entry.RelativePath,
                read.Attempt,
                read.EvidenceBytes,
                new RetiredGuard(attemptId, read.Attempt.Evidence.AttemptHash)));
        }
        return artifacts;
    }

    private async ValueTask<IReadOnlyList<RecoveryArtifact>> LoadStageArtifactsAsync(
        RecoveryEvidenceBudget budget,
        CancellationToken cancellationToken)
    {
        var entries = _boundary.EnumerateDirectory("a/s", MaximumRecoveryStages).ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException("The pending-attempt stage root contains an unexpected file.");

        var artifacts = new List<RecoveryArtifact>(entries.Length);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leaf = Leaf(entry.RelativePath);
            if (!IsStageLeaf(leaf))
                throw new InvalidDataException("A pending-attempt stage has an invalid lowercase-hex name.");
            var children = _boundary.EnumerateDirectory(entry.RelativePath, 2).ToArray();
            if (children.Length == 0)
            {
                artifacts.Add(new RecoveryArtifact(entry.RelativePath, null, 0, null));
                continue;
            }
            var read = await ReadAttemptWithSizeAsync(
                    entry.RelativePath,
                    cancellationToken,
                    budget)
                .ConfigureAwait(false);
            artifacts.Add(new RecoveryArtifact(entry.RelativePath, read.Attempt, read.EvidenceBytes, null));
        }
        return artifacts;
    }

    private async ValueTask<IReadOnlyList<RecoveryArtifact>> LoadQuarantineArtifactsAsync(
        RecoveryEvidenceBudget budget,
        CancellationToken cancellationToken)
    {
        var entries = _boundary.EnumerateDirectory("a/x", MaximumCleanupArtifacts).ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException("The pending-attempt cleanup root contains an unexpected file.");

        var artifacts = new List<RecoveryArtifact>(entries.Length);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leaf = Leaf(entry.RelativePath);
            var children = _boundary.EnumerateDirectory(entry.RelativePath, 2).ToArray();
            AttemptRead? read = children.Length == 0
                ? null
                : await ReadAttemptWithSizeAsync(entry.RelativePath, cancellationToken, budget).ConfigureAwait(false);
            RetiredGuard? guard = null;
            if (leaf.StartsWith("r-", StringComparison.Ordinal))
            {
                guard = ParseRetiredCleanupLeaf(leaf);
                if (read is not null && !GuardMatches(guard, read.Attempt))
                    throw new InvalidDataException("Quarantined retired evidence conflicts with its cleanup guard.");
            }
            else if (!leaf.StartsWith("s-", StringComparison.Ordinal) || !IsStageLeaf(leaf[2..]))
            {
                throw new InvalidDataException("A pending-attempt cleanup artifact has an invalid bounded name.");
            }
            artifacts.Add(new RecoveryArtifact(
                entry.RelativePath,
                read?.Attempt,
                read?.EvidenceBytes ?? 0,
                guard));
        }
        return artifacts;
    }

    private async ValueTask<CortexSyncPendingSubmitAttempt?> ReadActiveAsync(
        CancellationToken cancellationToken) =>
        (await ReadActiveWithDurabilityAsync(cancellationToken).ConfigureAwait(false))?.Attempt;

    private async ValueTask<AttemptRead?> ReadActiveWithDurabilityAsync(
        CancellationToken cancellationToken)
    {
        var entries = _boundary.EnumerateDirectory("a/p", 2).ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory) || entries.Length > 1)
        {
            throw new InvalidDataException(
                "The pending-attempt root contains an unexpected file or more than one active request.");
        }
        if (entries.Length == 0)
            return null;
        var read = await ReadAttemptWithSizeAsync(entries[0].RelativePath, cancellationToken).ConfigureAwait(false);
        if (!entries[0].RelativePath.EndsWith("/" + read.Attempt.Evidence.AttemptId, StringComparison.Ordinal))
            throw new InvalidDataException("The pending-attempt directory is not bound to its sealed identifier.");
        return read;
    }

    private async ValueTask<CortexSyncPendingSubmitAttempt> ReadAttemptAsync(
        string directory,
        CancellationToken cancellationToken) =>
        (await ReadAttemptWithSizeAsync(directory, cancellationToken).ConfigureAwait(false)).Attempt;

    private async ValueTask<AttemptRead> ReadAttemptWithSizeAsync(
        string directory,
        CancellationToken cancellationToken,
        RecoveryEvidenceBudget? budget = null)
    {
        var children = _boundary.EnumerateDirectory(directory, 3).ToArray();
        if (children.Length is < 1 or > 2 ||
            children.Any(item => item.Kind != JournalFileSystemEntryKind.RegularFile))
        {
            throw new InvalidDataException(
                "A pending-attempt directory must contain one evidence file and at most one durability marker.");
        }
        var attemptFile = children.SingleOrDefault(item =>
            string.Equals(Leaf(item.RelativePath), AttemptFileName, StringComparison.Ordinal));
        var markerFile = children.SingleOrDefault(item =>
            string.Equals(Leaf(item.RelativePath), DurabilityMarkerFileName, StringComparison.Ordinal));
        if (attemptFile is null || children.Length != 1 + (markerFile is null ? 0 : 1))
            throw new InvalidDataException("A pending-attempt directory contains an unexpected evidence file.");

        await using var stream = _boundary.OpenReadFile(
            attemptFile.RelativePath,
            16 * 1024,
            asynchronous: true,
            sequentialScan: true);
        if (stream.Length is <= 0 or > MaximumAttemptFileBytes)
            throw new InvalidDataException("A pending-attempt evidence file is empty or exceeds its fixed bound.");
        budget?.Reserve(stream.Length);
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var evidence = CortexCanonicalJson.Deserialize<CortexSyncPendingAttemptV1>(bytes);
        if (!CortexCanonicalJson.Serialize(evidence).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("The pending-attempt evidence is not canonical JSON.");
        var attempt = CortexSyncPendingAttemptPolicy.ValidateAndDecode(evidence);

        var durableForReplay = false;
        long evidenceBytes = bytes.LongLength;
        if (markerFile is not null)
        {
            await using var markerStream = _boundary.OpenReadFile(
                markerFile.RelativePath,
                4 * 1024,
                asynchronous: true,
                sequentialScan: true);
            if (markerStream.Length is <= 0 or > MaximumDurabilityMarkerBytes)
                throw new InvalidDataException("A pending-attempt durability marker exceeds its fixed bound.");
            budget?.Reserve(markerStream.Length);
            var markerBytes = new byte[checked((int)markerStream.Length)];
            await markerStream.ReadExactlyAsync(markerBytes, cancellationToken).ConfigureAwait(false);
            var marker = CortexCanonicalJson.Deserialize<PendingAttemptDurabilityMarker>(markerBytes);
            if (!CortexCanonicalJson.Serialize(marker).AsSpan().SequenceEqual(markerBytes) ||
                marker.SchemaVersion != 1 ||
                !string.Equals(marker.ContractId, DurabilityMarkerContractId, StringComparison.Ordinal) ||
                !string.Equals(
                    marker.Canonicalization,
                    CortexSyncClientContractIds.Canonicalization,
                    StringComparison.Ordinal) ||
                !string.Equals(marker.AttemptId, evidence.AttemptId, StringComparison.Ordinal) ||
                !string.Equals(marker.AttemptHash, evidence.AttemptHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A pending-attempt durability marker is not canonical 1.1 evidence for its request.");
            }
            evidenceBytes = checked(evidenceBytes + markerBytes.LongLength);
            durableForReplay = true;
        }
        return new AttemptRead(
            attempt,
            evidenceBytes,
            durableForReplay);
    }

    private static void RequireSameAttempt(
        CortexSyncPendingSubmitAttempt existing,
        CortexSyncPendingSubmitAttempt expected)
    {
        if (!string.Equals(existing.Evidence.AttemptHash, expected.Evidence.AttemptHash, StringComparison.Ordinal) ||
            !existing.CanonicalRequestBytes.Span.SequenceEqual(expected.CanonicalRequestBytes.Span))
        {
            throw new InvalidDataException(
                "A different immutable submit request already occupies the pending-attempt slot.");
        }
    }

    private void CleanupStage(string stage)
    {
        // Cleanup is never authority. Preserve the original write outcome and let the next
        // bounded reconciliation pass recover or retry any owner-protected residue.
        _ = TryCleanupOwnedDirectory(stage);
    }

    private void QuarantineStage(string stage)
    {
        var destination = StageCleanupPath(stage);
        if (_boundary.DirectoryExists(destination) || _boundary.FileExists(destination))
            throw new InvalidDataException("A conflicting stage cleanup artifact already exists.");
        MoveDirectoryDurably(stage, destination);
    }

    private void QuarantineRetiredAttempt(
        string retired,
        CortexSyncPendingSubmitAttempt attempt) =>
        QuarantineRetiredArtifact(new RecoveryArtifact(
            retired,
            attempt,
            0,
            new RetiredGuard(attempt.Evidence.AttemptId, attempt.Evidence.AttemptHash)));

    private void QuarantineRetiredArtifact(RecoveryArtifact artifact)
    {
        var destination = RetiredCleanupPath(artifact);
        if (_boundary.DirectoryExists(destination) || _boundary.FileExists(destination))
            throw new InvalidDataException("A conflicting retired cleanup artifact already exists.");
        MoveDirectoryDurably(artifact.RelativePath, destination);
    }

    private static string StageCleanupPath(string stage) =>
        $"a/x/s-{Leaf(stage)}";

    private static string RetiredCleanupPath(RecoveryArtifact artifact)
    {
        var guard = artifact.Guard ??
            throw new InvalidDataException("Retired cleanup requires a bounded identity guard.");
        return $"a/x/r-{guard.AttemptId}-{guard.AttemptHash ?? "unknown"}";
    }

    private void CleanupQuarantine()
    {
        var entries = _boundary.EnumerateDirectory("a/x", MaximumCleanupArtifacts).ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException("The pending-attempt cleanup root contains an unexpected file.");
        foreach (var entry in entries)
            _ = TryCleanupOwnedDirectory(entry.RelativePath);
    }

    private bool TryCleanupOwnedDirectory(string directory)
    {
        try
        {
            var marker = $"{directory}/{DurabilityMarkerFileName}";
            if (_boundary.FileExists(marker))
                _boundary.DeleteFile(marker);
            var file = $"{directory}/{AttemptFileName}";
            if (_boundary.FileExists(file))
                _boundary.DeleteFile(file);
            if (_boundary.DirectoryExists(directory))
                _boundary.DeleteEmptyDirectory(directory);
            return !_boundary.DirectoryExists(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Replace/delete races and transient cleanup failures are hints only. A surviving
            // file or empty directory is reconciled under the exclusive lock on the next call.
            return false;
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
                "The pending-attempt boundary lost its durable-promotion capability.");
        }
        promotion.MoveDirectoryDurably(source, destination);
    }

    private void RequireArtifactCapacity(
        string relativeRoot,
        int maximumArtifacts,
        int requiredSlots,
        string description)
    {
        if (requiredSlots is <= 0 || requiredSlots > maximumArtifacts)
            throw new ArgumentOutOfRangeException(nameof(requiredSlots));
        var entries = _boundary.EnumerateDirectory(relativeRoot, maximumArtifacts).ToArray();
        if (entries.Any(item => item.Kind != JournalFileSystemEntryKind.Directory))
            throw new InvalidDataException($"The {description} root contains an unexpected file.");
        if (entries.Length > maximumArtifacts - requiredSlots)
            throw new InvalidDataException($"The {description} have no reserved capacity for a durable transition.");
    }

    private static CortexSyncPendingAttemptSaveResult DurableSaveResult(
        CortexSyncPendingSubmitAttempt attempt) =>
        new(attempt.Evidence.AttemptHash, DurableForReplay: true);

    private static RetiredGuard ParseRetiredCleanupLeaf(string leaf)
    {
        var separator = leaf.LastIndexOf('-');
        if (separator <= 2 || separator == leaf.Length - 1)
            throw new InvalidDataException("A retired cleanup guard has an invalid bounded name.");
        var attemptId = leaf[2..separator];
        var hash = leaf[(separator + 1)..];
        if (!IsAttemptId(attemptId) ||
            hash is not "unknown" && !IsLowerHex(hash, 64))
        {
            throw new InvalidDataException("A retired cleanup guard has an invalid bounded name.");
        }
        return new RetiredGuard(attemptId, hash == "unknown" ? null : hash);
    }

    private static bool GuardMatches(
        RetiredGuard guard,
        CortexSyncPendingSubmitAttempt attempt) =>
        guard.AttemptHash is not null &&
        string.Equals(guard.AttemptId, attempt.Evidence.AttemptId, StringComparison.Ordinal) &&
        string.Equals(guard.AttemptHash, attempt.Evidence.AttemptHash, StringComparison.Ordinal);

    private static bool IsAttemptId(string value) =>
        value is { Length: >= 9 and <= 64 } &&
        value.StartsWith("attempt-", StringComparison.Ordinal) &&
        value.Skip(8).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsStageLeaf(string value) =>
        value.Length == 34 &&
        value.StartsWith("a-", StringComparison.Ordinal) &&
        IsLowerHex(value[2..], 32);

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Leaf(string relativePath) =>
        relativePath[(relativePath.LastIndexOf('/') + 1)..];

    private static string RequireExactRoot(string requestedRoot, string approvedRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot) || string.IsNullOrWhiteSpace(approvedRoot) ||
            requestedRoot.Any(char.IsControl) || approvedRoot.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(requestedRoot) || !Path.IsPathFullyQualified(approvedRoot))
        {
            throw new ArgumentException("Pending-attempt roots must be fully-qualified canonical local paths.");
        }
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var approved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        if (!string.Equals(requestedRoot, requested, PathComparison) ||
            !string.Equals(approvedRoot, approved, PathComparison) ||
            !string.Equals(requested, approved, PathComparison) ||
            OperatingSystem.IsWindows() && requested.Length > 240)
        {
            throw new ArgumentException("Requested and approved pending-attempt roots must be the same canonical path.");
        }
        return requested;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record AttemptRead(
        CortexSyncPendingSubmitAttempt Attempt,
        long EvidenceBytes,
        bool DurableForReplay);

    private sealed record PendingAttemptDurabilityMarker(
        int SchemaVersion,
        string ContractId,
        string Canonicalization,
        string AttemptId,
        string AttemptHash);

    private sealed record RetiredGuard(
        string AttemptId,
        string? AttemptHash);

    private sealed record RecoveryArtifact(
        string RelativePath,
        CortexSyncPendingSubmitAttempt? Attempt,
        long EvidenceBytes,
        RetiredGuard? Guard);

    private sealed record RecoveryReconciliation(
        IReadOnlyList<RetiredGuard> RetiredGuards);

    private sealed class RecoveryEvidenceBudget(long remainingBytes)
    {
        private long _remainingBytes = remainingBytes;

        public void Reserve(long evidenceBytes)
        {
            if (evidenceBytes <= 0 || evidenceBytes > _remainingBytes)
                throw new InvalidDataException("Pending-attempt recovery evidence exceeds its total byte budget.");
            _remainingBytes -= evidenceBytes;
        }
    }
}
