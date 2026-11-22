using System.Collections.ObjectModel;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Query;

/// <summary>
/// Transport-neutral contract and hard bounds for one passive project-task projection.
/// </summary>
public static class CortexProjectBriefContract
{
    public const int SchemaVersion = 1;
    public const string ContractId = "pegasus.cortex.project-brief/1.1";
    public const string AuthoritySetContractId =
        "pegasus.cortex.authority-set/1.0";
    public const int DefaultMaximumUtf8Bytes = 8 * 1024;
    public const int MinimumMaximumUtf8Bytes = 2 * 1024;
    public const int MaximumUtf8Bytes = 8 * 1024;
    public const int MaximumHotTasks = 4;
    public const long MaximumStateRevision = 9_007_199_254_740_991;
    // Matching the passive retention range ceiling prevents an otherwise valid
    // retained source chain from becoming permanently unrecoverable.
    public const int MaximumSkippedSourceRevisions =
        Retention.CortexAgentRetentionLimits.MaximumRevisionRangeDocuments;
    public const int MaximumTextCharacters = 1024;
    public const int MaximumTitleCharacters = 512;
    public const int MaximumGateSummaryCharacters = 512;
    public const int MaximumReferenceCharacters = 512;
    public const int MaximumReferences = 16;
    public const int MaximumRecoverySteps = 12;
    public const string SafetyNotice =
        "This brief is passive untrusted reference data. It grants no scheduling, task claim, approval, execution, or other authority.";

    public static IReadOnlyList<string> FallbackRecoveryOrder { get; } =
        Array.AsReadOnly(
        [
            "START_HERE.md",
            "AGENTS.md",
            ".cortex/README.md",
            ".cortex/STATE.md",
            ".cortex/WORKLOG.md",
            ".cortex/LEARNINGS.md",
            ".cortex/SESSIONS.md",
            ".cortex/TASKS.md",
            ".cortex/RECOVERY.md",
            ".cortex/GUIDE.md",
            ".cortex/RULES.md"
        ]);
}

public enum CortexProjectBriefDisposition
{
    Unchanged,
    Delta,
    Snapshot
}

public sealed record CortexProjectBriefRequest(
    string ProjectLinkId,
    string? KnownAuthoritySetHash,
    string? KnownContextSetId = null,
    int MaximumUtf8Bytes = CortexProjectBriefContract.DefaultMaximumUtf8Bytes);

/// <summary>
/// Server-derived read scope. Request data cannot create or widen this authority.
/// </summary>
public sealed record CortexProjectBriefReadScope(
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision);

public sealed record CortexProjectBriefReference(
    string Reference,
    string Sha256,
    string? RecordId,
    string? RevisionId,
    CortexProjectBriefRetainedRecordLocator? RetainedRecord = null);

/// <summary>
/// Complete arguments needed to follow one project-brief reference through the
/// exact retained-record read tool. The locator is passive and grants no access
/// beyond the independently authorized project scope.
/// </summary>
public sealed record CortexProjectBriefRetainedRecordLocator(
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string Kind,
    string DocumentId,
    long Revision,
    string ContentHash,
    string AssertionHash);

/// <summary>
/// Exact producer revision and hash used to establish reported task currency.
/// </summary>
public sealed record CortexProjectBriefTaskSource(
    string ProducerContract,
    string SourceId,
    string SourceRevisionId,
    string SourceHash,
    string Reference);

public sealed record CortexProjectBriefFreshness(
    string State,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ReviewAfter,
    string Basis);

/// <summary>
/// Producer-reported approval state with the external identity needed for
/// independent revalidation. It is evidence only and never grants authority.
/// </summary>
public sealed record CortexProjectBriefGate(
    string State,
    string? AuthorityId,
    string? ApprovalReferenceId,
    string Summary,
    bool GrantsAuthority);

/// <summary>
/// Reported task state only. Owner, gate, and status fields never confer authority.
/// Rank zero is reserved for the current task; hot-task ranks begin at one.
/// </summary>
public sealed record CortexProjectBriefTask(
    string TaskId,
    int SelectionRank,
    string Title,
    string State,
    string CurrentState,
    string NextAction,
    CortexProjectBriefGate Gate,
    string? Blocker,
    CortexProjectBriefTaskSource Source,
    CortexProjectBriefFreshness Freshness);

/// <summary>
/// Immutable read-model snapshot. A SQLite or PostgreSQL adapter can implement
/// the same read interface without changing Core or the MCP projection.
/// </summary>
public sealed record CortexProjectBriefSnapshot(
    int SchemaVersion,
    string ContractId,
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string ContextSetId,
    string? PreviousContextSetId,
    long BriefRevision,
    long StateRevision,
    string SourceHash,
    string SelectionVersion,
    CortexProjectBriefTask CurrentTask,
    IReadOnlyList<CortexProjectBriefTask> HotTasks,
    bool HasMore,
    IReadOnlyList<CortexProjectBriefReference> CheckpointReferences,
    IReadOnlyList<CortexProjectBriefReference> EvidenceReferences,
    bool Complete,
    IReadOnlyList<string> RecoveryOrder,
    CortexProjectBriefFreshness Freshness,
    string AuthoritySetHash = "");

/// <summary>
/// Snapshot returns the full task projection, delta only changed/upserted tasks,
/// and unchanged omits tasks. RemovedTaskIds lets a cached caller apply a delta.
/// </summary>
public sealed record CortexProjectBriefResponse(
    int SchemaVersion,
    string ContractId,
    CortexProjectBriefDisposition Disposition,
    string? ProjectLinkId,
    string? ProjectId,
    string? ProjectNamespace,
    string? ContextSetId,
    string? BaseContextSetId,
    long? StateRevision,
    string? SourceHash,
    string? SelectionVersion,
    CortexProjectBriefTask? CurrentTask,
    IReadOnlyList<CortexProjectBriefTask> HotTasks,
    IReadOnlyList<string> RemovedTaskIds,
    bool HasMore,
    IReadOnlyList<CortexProjectBriefReference> CheckpointReferences,
    IReadOnlyList<CortexProjectBriefReference> EvidenceReferences,
    CortexProjectBriefFreshness? Freshness,
    bool Complete,
    bool Truncated,
    bool FallbackRequired,
    string? FallbackReason,
    IReadOnlyList<string> RecoveryOrder,
    string? AuthoritySetHash = null);

public sealed record CortexProjectBriefResult(
    bool Accepted,
    CortexProjectBriefResponse Response,
    IReadOnlyList<PegasusDiagnostic> Diagnostics);

/// <summary>
/// Read-only project-brief port. Implementations return only explicitly created,
/// hash-bound snapshots and never discover project files or live task state.
/// </summary>
public interface ICortexProjectBriefReadModel
{
    ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken);

    ValueTask<CortexProjectBriefSnapshot?> FindAsync(
        CortexProjectBriefReadScope scope,
        string contextSetId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exact historical read used by durable synchronization replay. Implementations
/// must authenticate the retained bytes and return only a revision whose
/// producer state revision and source hash both match the requested identity.
/// </summary>
public interface ICortexProjectBriefHistoryReadModel : ICortexProjectBriefReadModel
{
    ValueTask<CortexProjectBriefSnapshot?> FindBySourceAsync(
        CortexProjectBriefReadScope scope,
        long stateRevision,
        string sourceHash,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable in-memory adapter for focused tests and pre-persistence composition.
/// It intentionally has no add, update, claim, or scheduling API.
/// </summary>
public sealed class InMemoryCortexProjectBriefReadModel : ICortexProjectBriefReadModel
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CortexProjectBriefSnapshot>> _snapshots;

    public InMemoryCortexProjectBriefReadModel(
        IEnumerable<CortexProjectBriefSnapshot>? snapshots = null)
    {
        var captured = (snapshots ?? [])
            .Select(CaptureSnapshot)
            .GroupBy(SnapshotKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CortexProjectBriefSnapshot>)Array.AsReadOnly(
                    group.OrderByDescending(item => item.StateRevision)
                        .ThenBy(item => item.ContextSetId, StringComparer.Ordinal)
                        .ToArray()),
                StringComparer.Ordinal);
        _snapshots = new ReadOnlyDictionary<string, IReadOnlyList<CortexProjectBriefSnapshot>>(captured);
    }

    public ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshots.TryGetValue(ScopeKey(scope), out var values)
            ? values.FirstOrDefault()
            : null;
        return ValueTask.FromResult(snapshot is null ? null : CaptureSnapshot(snapshot));
    }

    public ValueTask<CortexProjectBriefSnapshot?> FindAsync(
        CortexProjectBriefReadScope scope,
        string contextSetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshots.TryGetValue(ScopeKey(scope), out var values)
            ? values.SingleOrDefault(
                item => string.Equals(item.ContextSetId, contextSetId, StringComparison.Ordinal))
            : null;
        return ValueTask.FromResult(snapshot is null ? null : CaptureSnapshot(snapshot));
    }

    private static string ScopeKey(CortexProjectBriefReadScope scope) =>
        $"{scope.TenantId}\n{scope.ProjectLinkId}\n{scope.ProjectId}\n" +
        scope.ProjectNamespace;

    private static string SnapshotKey(CortexProjectBriefSnapshot snapshot) =>
        $"{snapshot.TenantId}\n{snapshot.ProjectLinkId}\n{snapshot.ProjectId}\n" +
        snapshot.ProjectNamespace;

    private static CortexProjectBriefSnapshot CaptureSnapshot(CortexProjectBriefSnapshot source) =>
        source with
        {
            CurrentTask = CaptureTask(source.CurrentTask),
            HotTasks = Array.AsReadOnly(source.HotTasks.Select(CaptureTask).ToArray()),
            CheckpointReferences = Array.AsReadOnly(
                source.CheckpointReferences.Select(item => item with
                {
                    RetainedRecord = item.RetainedRecord is null
                        ? null
                        : item.RetainedRecord with { }
                }).ToArray()),
            EvidenceReferences = Array.AsReadOnly(
                source.EvidenceReferences.Select(item => item with
                {
                    RetainedRecord = item.RetainedRecord is null
                        ? null
                        : item.RetainedRecord with { }
                }).ToArray()),
            RecoveryOrder = Array.AsReadOnly(source.RecoveryOrder.ToArray()),
            Freshness = source.Freshness with { }
        };

    private static CortexProjectBriefTask CaptureTask(CortexProjectBriefTask source) =>
        source with
        {
            Source = source.Source with { },
            Freshness = source.Freshness with { }
        };
}

public sealed class CortexProjectBriefService
{
    private readonly ICortexProjectBriefReadModel _readModel;
    private readonly TimeProvider _timeProvider;

    public CortexProjectBriefService(
        ICortexProjectBriefReadModel readModel,
        TimeProvider? timeProvider = null)
    {
        _readModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CortexProjectBriefResult> ReadAsync(
        CortexProjectBriefRequest request,
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidRequest(request) ||
            !ValidScope(scope) ||
            !string.Equals(request.ProjectLinkId, scope.ProjectLinkId, StringComparison.Ordinal))
        {
            return Unavailable(
                "cortex_project_brief_authority_invalid",
                "The project-brief request does not match exact server-derived project-link authority.",
                "authority_invalid");
        }

        // Version 1.1 is meaningful only when the caller proves that its local
        // authority files match the source set retained by Cortex. Missing or
        // malformed probes fail as an authority mismatch without reading or
        // returning any project, task, or source claims.
        if (!ValidSha256(request.KnownAuthoritySetHash))
            return AuthorityMismatch();

        var currentResult = await ReadCurrentAsync(scope, cancellationToken).ConfigureAwait(false);
        if (currentResult.Diagnostic is not null)
            return currentResult.Diagnostic;

        var now = _timeProvider.GetUtcNow();
        if (!TryCapture(
                currentResult.Snapshot,
                scope,
                now,
                requireCurrent: true,
                out var current))
        {
            return Unavailable(
                "cortex_project_brief_snapshot_not_available",
                "No current, hash-bound project-brief snapshot is available.",
                "snapshot_not_available");
        }
        var currentSnapshot = current!;

        // Every response, including a first snapshot, is contingent on the
        // exact local authority-set probe. This prevents a cold caller from
        // bypassing CURRENT.json verification and treating Cortex as authority.
        if (!string.Equals(
                request.KnownAuthoritySetHash,
                currentSnapshot.AuthoritySetHash,
                StringComparison.Ordinal))
            return AuthorityMismatch();

        CortexProjectBriefSnapshot? known = null;
        if (request.KnownContextSetId is not null &&
            !string.Equals(
                request.KnownContextSetId,
                currentSnapshot.ContextSetId,
                StringComparison.Ordinal))
        {
            var knownResult = await ReadKnownAsync(
                scope,
                request.KnownContextSetId,
                cancellationToken).ConfigureAwait(false);
            if (knownResult.Diagnostic is not null)
                return knownResult.Diagnostic;
            if (knownResult.Snapshot is not null &&
                !TryCapture(
                    knownResult.Snapshot,
                    scope,
                    now,
                    requireCurrent: false,
                    out known))
            {
                return Unavailable(
                    "cortex_project_brief_snapshot_invalid",
                    "The known project-brief snapshot failed its hash, source, or freshness boundary.",
                    "known_snapshot_invalid");
            }
        }

        var disposition = request.KnownContextSetId is null
            ? CortexProjectBriefDisposition.Snapshot
            : string.Equals(
                request.KnownContextSetId,
                currentSnapshot.ContextSetId,
                StringComparison.Ordinal)
                ? CortexProjectBriefDisposition.Unchanged
                : known is not null &&
                  known.BriefRevision == currentSnapshot.BriefRevision - 1 &&
                  known.StateRevision == currentSnapshot.StateRevision - 1 &&
                  string.Equals(
                      known.ContextSetId,
                      currentSnapshot.PreviousContextSetId,
                      StringComparison.Ordinal)
                    ? CortexProjectBriefDisposition.Delta
                    : CortexProjectBriefDisposition.Snapshot;

        var projection = ProjectTasks(disposition, known, currentSnapshot);
        var complete = currentSnapshot.Complete;
        var fallbackReason = complete ? null : "source_incomplete";
        var response = new CortexProjectBriefResponse(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            disposition,
            currentSnapshot.ProjectLinkId,
            currentSnapshot.ProjectId,
            currentSnapshot.ProjectNamespace,
            currentSnapshot.ContextSetId,
            disposition == CortexProjectBriefDisposition.Delta
                ? known!.ContextSetId
                : null,
            currentSnapshot.StateRevision,
            currentSnapshot.SourceHash,
            currentSnapshot.SelectionVersion,
            projection.CurrentTask,
            Array.AsReadOnly(projection.HotTasks),
            Array.AsReadOnly(projection.RemovedTaskIds),
            currentSnapshot.HasMore,
            currentSnapshot.CheckpointReferences,
            currentSnapshot.EvidenceReferences,
            currentSnapshot.Freshness,
            complete,
            Truncated: false,
            FallbackRequired: !complete,
            fallbackReason,
            currentSnapshot.RecoveryOrder,
            currentSnapshot.AuthoritySetHash);
        // The provider-neutral budget is the exact canonical JSON encoding of
        // the typed response. Transports may add their own envelopes, but no
        // direct Core consumer can receive a response above its declared bound.
        if (CortexCanonicalJson.Serialize(response).Length > request.MaximumUtf8Bytes)
        {
            return Unavailable(
                "cortex_project_brief_budget_exceeded",
                "The current project brief exceeds the caller's canonical UTF-8 budget.",
                "response_budget_exceeded");
        }
        return new CortexProjectBriefResult(true, response, []);
    }

    /// <summary>
    /// Context identity binds the complete canonical task selection, source hashes,
    /// freshness, coverage, and recovery metadata.
    /// </summary>
    public static string ComputeContextSetId(CortexProjectBriefSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var projection = new
        {
            contractId = CortexProjectBriefContract.ContractId,
            snapshot.TenantId,
            snapshot.ProjectLinkId,
            snapshot.ProjectId,
            snapshot.ProjectNamespace,
            snapshot.PreviousContextSetId,
            snapshot.BriefRevision,
            snapshot.StateRevision,
            snapshot.SourceHash,
            snapshot.AuthoritySetHash,
            snapshot.SelectionVersion,
            currentTask = snapshot.CurrentTask,
            hotTasks = snapshot.HotTasks.OrderBy(item => item.SelectionRank),
            snapshot.HasMore,
            checkpointReferences = snapshot.CheckpointReferences
                .OrderBy(item => item.Reference, StringComparer.Ordinal),
            evidenceReferences = snapshot.EvidenceReferences
                .OrderBy(item => item.Reference, StringComparer.Ordinal),
            snapshot.Complete,
            snapshot.RecoveryOrder,
            snapshot.Freshness
        };
        return $"ctx_{CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(projection))}";
    }

    /// <summary>
    /// Deterministically identifies the exact bounded authority files and
    /// revisions from which an agent task snapshot was produced. The hash is
    /// data provenance only; it grants no read, task, approval, or execution
    /// authority.
    /// </summary>
    public static string ComputeAuthoritySetHash(
        IReadOnlyList<AgentWorkSourceRevisionV1> sourceRevisions)
    {
        ArgumentNullException.ThrowIfNull(sourceRevisions);
        var captured = sourceRevisions
            .Take(AgentWorkPacketContractLimits.MaximumSourceRevisions + 1)
            .ToArray();
        if (captured.Length == 0 ||
            captured.Length > AgentWorkPacketContractLimits.MaximumSourceRevisions ||
            captured.Any(item => item is null) ||
            captured.Select(item => item.SourceId)
                .Distinct(StringComparer.Ordinal).Count() != captured.Length)
        {
            throw new ArgumentException(
                "An authority set requires unique source revisions.",
                nameof(sourceRevisions));
        }

        var projection = new
        {
            contractId = CortexProjectBriefContract.AuthoritySetContractId,
            sources = captured
                .OrderBy(item => item.SourceId, StringComparer.Ordinal)
                .Select(item => new
                {
                    item.SourceId,
                    item.RevisionId,
                    item.Sha256
                })
        };
        return CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(projection));
    }

    private async ValueTask<SnapshotReadResult> ReadCurrentAsync(
        CortexProjectBriefReadScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return new SnapshotReadResult(
                await _readModel.FindCurrentAsync(scope, cancellationToken).ConfigureAwait(false),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SnapshotReadResult(
                null,
                Unavailable(
                    "cortex_project_brief_source_failed",
                    "The project-brief read model failed closed.",
                    "source_failed",
                    exception.GetType().Name));
        }
    }

    private async ValueTask<SnapshotReadResult> ReadKnownAsync(
        CortexProjectBriefReadScope scope,
        string contextSetId,
        CancellationToken cancellationToken)
    {
        try
        {
            return new SnapshotReadResult(
                await _readModel.FindAsync(
                    scope,
                    contextSetId,
                    cancellationToken).ConfigureAwait(false),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SnapshotReadResult(
                null,
                Unavailable(
                    "cortex_project_brief_source_failed",
                    "The project-brief read model failed closed.",
                    "source_failed",
                    exception.GetType().Name));
        }
    }

    private static TaskProjection ProjectTasks(
        CortexProjectBriefDisposition disposition,
        CortexProjectBriefSnapshot? known,
        CortexProjectBriefSnapshot current)
    {
        if (disposition == CortexProjectBriefDisposition.Unchanged)
            return new TaskProjection(null, [], []);
        if (disposition == CortexProjectBriefDisposition.Snapshot || known is null)
        {
            return new TaskProjection(
                CaptureTask(current.CurrentTask),
                current.HotTasks.Select(CaptureTask).ToArray(),
                []);
        }

        var knownTasks = EnumerateTasks(known)
            .ToDictionary(item => item.TaskId, StringComparer.Ordinal);
        var currentTasks = EnumerateTasks(current)
            .ToDictionary(item => item.TaskId, StringComparer.Ordinal);
        var currentTask = !knownTasks.TryGetValue(current.CurrentTask.TaskId, out var priorCurrent) ||
                          priorCurrent != current.CurrentTask
            ? CaptureTask(current.CurrentTask)
            : null;
        var changedHotTasks = current.HotTasks
            .Where(item =>
                !knownTasks.TryGetValue(item.TaskId, out var prior) ||
                prior != item)
            .Select(CaptureTask)
            .ToArray();
        var removed = knownTasks.Keys
            .Where(taskId => !currentTasks.ContainsKey(taskId))
            .OrderBy(taskId => taskId, StringComparer.Ordinal)
            .ToArray();
        return new TaskProjection(currentTask, changedHotTasks, removed);
    }

    private static IEnumerable<CortexProjectBriefTask> EnumerateTasks(
        CortexProjectBriefSnapshot snapshot)
    {
        yield return snapshot.CurrentTask;
        foreach (var task in snapshot.HotTasks)
            yield return task;
    }

    private static bool TryCapture(
        CortexProjectBriefSnapshot? source,
        CortexProjectBriefReadScope scope,
        DateTimeOffset now,
        bool requireCurrent,
        out CortexProjectBriefSnapshot? captured)
    {
        captured = null;
        if (source is null ||
            source.SchemaVersion != CortexProjectBriefContract.SchemaVersion ||
            !string.Equals(source.ContractId, CortexProjectBriefContract.ContractId, StringComparison.Ordinal) ||
            !string.Equals(source.TenantId, scope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(source.ProjectLinkId, scope.ProjectLinkId, StringComparison.Ordinal) ||
            !ValidIdentifier(source.ProjectId) ||
            !string.Equals(
                source.ProjectId,
                scope.ProjectId,
                StringComparison.Ordinal) ||
            !string.Equals(source.ProjectNamespace, scope.ProjectNamespace, StringComparison.Ordinal) ||
            source.BriefRevision is < 1 or > CortexProjectBriefContract.MaximumStateRevision ||
            source.StateRevision is < 1 or > CortexProjectBriefContract.MaximumStateRevision ||
            source.StateRevision < source.BriefRevision ||
            source.BriefRevision == 1 && source.PreviousContextSetId is not null ||
            source.BriefRevision > 1 && source.PreviousContextSetId is null ||
            source.PreviousContextSetId is not null &&
            (!IsPrefixedHash(source.PreviousContextSetId, "ctx_") ||
             string.Equals(
                 source.PreviousContextSetId,
                 source.ContextSetId,
                 StringComparison.Ordinal)) ||
            !ValidSha256(source.SourceHash) ||
            !ValidSha256(source.AuthoritySetHash) ||
            !ValidIdentifier(source.SelectionVersion) ||
            source.CurrentTask is null ||
            source.HotTasks is null ||
            source.CheckpointReferences is null ||
            source.EvidenceReferences is null ||
            source.RecoveryOrder is null ||
            source.Freshness is null ||
            !ValidFreshness(source.Freshness, now) ||
            requireCurrent && !IsCurrentFreshness(source.Freshness, now))
        {
            return false;
        }

        var hotTasks = source.HotTasks.Take(CortexProjectBriefContract.MaximumHotTasks + 1).ToArray();
        var checkpoints = source.CheckpointReferences.Take(CortexProjectBriefContract.MaximumReferences + 1).ToArray();
        var evidence = source.EvidenceReferences.Take(CortexProjectBriefContract.MaximumReferences + 1).ToArray();
        var recovery = source.RecoveryOrder.Take(CortexProjectBriefContract.MaximumRecoverySteps + 1).ToArray();
        if (!ValidTask(source.CurrentTask, expectedRank: 0, now, requireCurrent) ||
            hotTasks.Length > CortexProjectBriefContract.MaximumHotTasks ||
            hotTasks.Where((task, index) =>
                !ValidTask(task, index + 1, now, requireCurrent)).Any() ||
            hotTasks.Select(item => item.TaskId)
                .Append(source.CurrentTask.TaskId)
                .Distinct(StringComparer.Ordinal).Count() != hotTasks.Length + 1 ||
            checkpoints.Length > CortexProjectBriefContract.MaximumReferences ||
            evidence.Length > CortexProjectBriefContract.MaximumReferences ||
            !ValidReferences(checkpoints) ||
            !ValidReferences(evidence) ||
            !ValidRetainedReferenceScope(
                checkpoints,
                scope,
                source.ProjectId) ||
            !ValidRetainedReferenceScope(
                evidence,
                scope,
                source.ProjectId) ||
            recovery.Length > CortexProjectBriefContract.MaximumRecoverySteps ||
            !recovery.SequenceEqual(
                CortexProjectBriefContract.FallbackRecoveryOrder,
                StringComparer.Ordinal))
        {
            return false;
        }

        captured = source with
        {
            CurrentTask = CaptureTask(source.CurrentTask),
            HotTasks = Array.AsReadOnly(hotTasks.Select(CaptureTask).ToArray()),
            CheckpointReferences = Array.AsReadOnly(
                checkpoints.OrderBy(item => item.Reference, StringComparer.Ordinal)
                    .Select(CaptureReference).ToArray()),
            EvidenceReferences = Array.AsReadOnly(
                evidence.OrderBy(item => item.Reference, StringComparer.Ordinal)
                    .Select(CaptureReference).ToArray()),
            RecoveryOrder = Array.AsReadOnly(recovery.ToArray()),
            Freshness = source.Freshness with { }
        };
        return string.Equals(
            captured.ContextSetId,
            ComputeContextSetId(captured),
            StringComparison.Ordinal);
    }

    private static CortexProjectBriefTask CaptureTask(CortexProjectBriefTask task) =>
        task with
        {
            Source = task.Source with { },
            Freshness = task.Freshness with { }
        };

    private static bool ValidTask(
        CortexProjectBriefTask? task,
        int expectedRank,
        DateTimeOffset now,
        bool requireCurrent) =>
        task is not null &&
        ValidIdentifier(task.TaskId) &&
        task.SelectionRank == expectedRank &&
        ValidText(task.Title, CortexProjectBriefContract.MaximumTitleCharacters) &&
        task.State is
            AgentTaskStateIds.Planned or
            AgentTaskStateIds.Active or
            AgentTaskStateIds.Blocked or
            AgentTaskStateIds.Held or
            AgentTaskStateIds.Done or
            AgentTaskStateIds.Cancelled or
            AgentTaskStateIds.Unknown &&
        ValidText(task.CurrentState, CortexProjectBriefContract.MaximumTextCharacters) &&
        ValidText(task.NextAction, CortexProjectBriefContract.MaximumTextCharacters) &&
        ValidGate(task.Gate) &&
        ValidOptionalText(task.Blocker, CortexProjectBriefContract.MaximumTextCharacters) &&
        (task.State is not (AgentTaskStateIds.Blocked or AgentTaskStateIds.Held) ||
        task.Blocker is not null) &&
        ValidTaskSource(task.Source) &&
        ValidFreshness(task.Freshness, now) &&
        (!requireCurrent || IsCurrentFreshness(task.Freshness, now));

    private static bool ValidTaskSource(CortexProjectBriefTaskSource? source) =>
        source is not null &&
        ValidContractId(source.ProducerContract) &&
        ValidIdentifier(source.SourceId) &&
        ValidIdentifier(source.SourceRevisionId) &&
        ValidSha256(source.SourceHash) &&
        ValidReference(source.Reference);

    private static bool ValidFreshness(
        CortexProjectBriefFreshness? freshness,
        DateTimeOffset now) =>
        freshness is not null &&
        freshness.State is
            AgentWorkFreshnessStateIds.Current or
            AgentWorkFreshnessStateIds.Stale or
            AgentWorkFreshnessStateIds.Blocked or
            AgentWorkFreshnessStateIds.Unverified or
            AgentWorkFreshnessStateIds.Unknown &&
        freshness.ObservedAt.Offset == TimeSpan.Zero &&
        freshness.ObservedAt <= now + AgentWorkPacketContractLimits.MaximumClockSkew &&
        (freshness.ReviewAfter is null ||
         freshness.ReviewAfter.Value.Offset == TimeSpan.Zero &&
        freshness.ReviewAfter.Value >= freshness.ObservedAt) &&
        ValidText(freshness.Basis, CortexProjectBriefContract.MaximumTextCharacters);

    private static bool IsCurrentFreshness(
        CortexProjectBriefFreshness freshness,
        DateTimeOffset now) =>
        freshness.State == AgentWorkFreshnessStateIds.Current &&
        freshness.ReviewAfter is not null &&
        now < freshness.ReviewAfter;

    private static bool ValidReferences(IReadOnlyList<CortexProjectBriefReference> values) =>
        values.All(CortexProjectBriefReferencePolicy.IsValid) &&
        values.Select(item => item.Reference).Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool ValidRetainedReferenceScope(
        IReadOnlyList<CortexProjectBriefReference> values,
        CortexProjectBriefReadScope scope,
        string projectId) =>
        values.All(item =>
            CortexProjectBriefReferencePolicy.IsValidForProject(
                item,
                scope.ProjectLinkId,
                projectId,
                scope.ProjectNamespace));

    private static bool ValidRequest(CortexProjectBriefRequest request) =>
        IsPrefixedHash(request.ProjectLinkId, "plink_") &&
        (request.KnownContextSetId is null ||
         IsPrefixedHash(request.KnownContextSetId, "ctx_")) &&
        request.MaximumUtf8Bytes is >= CortexProjectBriefContract.MinimumMaximumUtf8Bytes and
            <= CortexProjectBriefContract.MaximumUtf8Bytes;

    private static bool ValidScope(CortexProjectBriefReadScope scope) =>
        ValidIdentifier(scope.TenantId) &&
        IsPrefixedHash(scope.ProjectLinkId, "plink_") &&
        ValidIdentifier(scope.ProjectId) &&
        ValidIdentifier(scope.ProjectNamespace) &&
        ValidIdentifier(scope.AuthorityId) &&
        ValidIdentifier(scope.SubjectId) &&
        ValidIdentifier(scope.ServiceId) &&
        scope.PolicyRevision >= 1;

    private static bool ValidGate(CortexProjectBriefGate? value)
    {
        if (value is null ||
            value.GrantsAuthority ||
            !ValidText(
                value.Summary,
                CortexProjectBriefContract.MaximumGateSummaryCharacters))
        {
            return false;
        }

        if (value.State == AgentWorkApprovalGateStateIds.NotRequired)
            return value.AuthorityId is null && value.ApprovalReferenceId is null;
        if (value.State is not (
                AgentWorkApprovalGateStateIds.Pending or
                AgentWorkApprovalGateStateIds.Satisfied or
                AgentWorkApprovalGateStateIds.Denied) ||
            !ValidIdentifier(value.AuthorityId))
        {
            return false;
        }
        return value.State == AgentWorkApprovalGateStateIds.Pending
            ? value.ApprovalReferenceId is null ||
              ValidIdentifier(value.ApprovalReferenceId)
            : ValidIdentifier(value.ApprovalReferenceId);
    }

    private static bool ValidOptionalText(string? value, int maximumCharacters) =>
        value is null || ValidText(value, maximumCharacters);

    private static bool ValidText(string? value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(character =>
            char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');

    private static bool ValidReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexProjectBriefContract.MaximumReferenceCharacters &&
        !value.Any(char.IsControl) &&
        (value.StartsWith("checkpoint://", StringComparison.Ordinal) ||
         value.StartsWith("artifact://", StringComparison.Ordinal) ||
         value.StartsWith("record://", StringComparison.Ordinal) ||
         value.StartsWith("revision://", StringComparison.Ordinal) ||
         value.StartsWith("hash://", StringComparison.Ordinal));

    private static CortexProjectBriefReference CaptureReference(
        CortexProjectBriefReference source) =>
        source with
        {
            RetainedRecord = source.RetainedRecord is null
                ? null
                : source.RetainedRecord with { }
        };

    private static bool ValidContractId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.Contains("/", StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool IsPrefixedHash(string? value, string prefix) =>
        value is not null &&
        value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>
    /// Creates the claim-free fallback used when a transport omits the required
    /// local authority probe before a provider runtime can be called.
    /// </summary>
    public static CortexProjectBriefResult AuthorityMismatch() =>
        Unavailable(
            "cortex_project_brief_authority_mismatch",
            "The caller's project authority set does not match the current project brief.",
            "authority_mismatch");

    private static CortexProjectBriefResult Unavailable(
        string code,
        string message,
        string fallbackReason,
        string? cause = null)
    {
        var diagnostic = new PegasusDiagnostic(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.project-brief.read",
            retryable: false,
            context: null,
            cause: cause);
        var response = new CortexProjectBriefResponse(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            CortexProjectBriefDisposition.Snapshot,
            ProjectLinkId: null,
            ProjectId: null,
            ProjectNamespace: null,
            ContextSetId: null,
            BaseContextSetId: null,
            StateRevision: null,
            SourceHash: null,
            SelectionVersion: null,
            CurrentTask: null,
            HotTasks: [],
            RemovedTaskIds: [],
            HasMore: false,
            CheckpointReferences: [],
            EvidenceReferences: [],
            Freshness: null,
            Complete: false,
            Truncated: false,
            FallbackRequired: true,
            fallbackReason,
            CortexProjectBriefContract.FallbackRecoveryOrder,
            AuthoritySetHash: null);
        return new CortexProjectBriefResult(false, response, [diagnostic]);
    }

    private sealed record SnapshotReadResult(
        CortexProjectBriefSnapshot? Snapshot,
        CortexProjectBriefResult? Diagnostic);

    private sealed record TaskProjection(
        CortexProjectBriefTask? CurrentTask,
        CortexProjectBriefTask[] HotTasks,
        string[] RemovedTaskIds);
}
