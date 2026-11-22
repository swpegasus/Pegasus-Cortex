using System.Text.Json.Serialization;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Contracts.WorkPackets;

/// <summary>
/// Exact immutable retained-record identity and hashes for one task evidence ID.
/// The reference is passive data: consumers must independently authorize and
/// resolve the named record before trusting its content.
/// </summary>
public sealed record AgentTaskEvidenceReferenceV1
{
    [JsonConstructor]
    public AgentTaskEvidenceReferenceV1(
        string evidenceId,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        string assertionHash)
    {
        AgentWorkPacketShapes.RequireIdentifier(evidenceId, nameof(evidenceId));
        if (kind is not (
                AgentTaskEvidenceKindIds.WorkPacket or
                AgentTaskEvidenceKindIds.WorkPacketHandoffReceipt or
                AgentTaskEvidenceKindIds.WorkPacketAcceptanceReceipt))
        {
            throw new ArgumentException("The task evidence kind is unsupported.", nameof(kind));
        }
        AgentWorkPacketShapes.RequireIdentifier(documentId, nameof(documentId));
        if (revision is < 1 or > AgentTaskSnapshotContractLimits.MaximumEvidenceRevision)
            throw new ArgumentOutOfRangeException(nameof(revision));
        AgentWorkPacketShapes.RequireHash(contentHash, nameof(contentHash));
        AgentWorkPacketShapes.RequireHash(assertionHash, nameof(assertionHash));

        EvidenceId = evidenceId;
        Kind = kind;
        DocumentId = documentId;
        Revision = revision;
        ContentHash = contentHash;
        AssertionHash = assertionHash;
    }

    public string EvidenceId { get; }
    public string Kind { get; }
    public string DocumentId { get; }
    public long Revision { get; }
    public string ContentHash { get; }
    public string AssertionHash { get; }
}

public sealed record AgentTaskV1
{
    [JsonConstructor]
    public AgentTaskV1(
        string taskId,
        string? parentTaskId,
        int ordinal,
        string reportedState,
        string objective,
        string currentState,
        string? nextAction,
        IReadOnlyList<string> constraints,
        IReadOnlyList<string> dependencyTaskIds,
        IReadOnlyList<string> blockers,
        IReadOnlyList<string> knownFailures,
        AgentWorkOwnerV1? reportedOwner,
        AgentWorkApprovalGateV1 approvalGate,
        IReadOnlyList<string> evidenceIds,
        IReadOnlyList<string> sourceRevisionIds)
    {
        AgentWorkPacketShapes.RequireIdentifier(taskId, nameof(taskId));
        if (parentTaskId is not null)
        {
            AgentWorkPacketShapes.RequireIdentifier(parentTaskId, nameof(parentTaskId));
            if (string.Equals(parentTaskId, taskId, StringComparison.Ordinal))
                throw new ArgumentException("A task cannot name itself as its parent.", nameof(parentTaskId));
        }
        if (ordinal is < 0 or >= AgentTaskSnapshotContractLimits.MaximumTasks)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (reportedState is not (
                AgentTaskStateIds.Planned or
                AgentTaskStateIds.Active or
                AgentTaskStateIds.Blocked or
                AgentTaskStateIds.Held or
                AgentTaskStateIds.Done or
                AgentTaskStateIds.Cancelled or
                AgentTaskStateIds.Unknown))
        {
            throw new ArgumentException("The reported task state is invalid.", nameof(reportedState));
        }
        AgentWorkPacketShapes.RequireText(
            objective,
            AgentWorkPacketContractLimits.MaximumObjectiveCharacters,
            nameof(objective));
        AgentWorkPacketShapes.RequireText(
            currentState,
            AgentWorkPacketContractLimits.MaximumStateCharacters,
            nameof(currentState));
        if (nextAction is not null)
        {
            AgentWorkPacketShapes.RequireText(
                nextAction,
                AgentWorkPacketContractLimits.MaximumItemCharacters,
                nameof(nextAction));
        }

        var constraintSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            constraints,
            AgentWorkPacketContractLimits.MaximumConstraints,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(constraints));
        var dependencySnapshot = AgentWorkPacketShapes.SnapshotIdentifierSet(
            dependencyTaskIds,
            AgentTaskSnapshotContractLimits.MaximumDependenciesPerTask,
            nameof(dependencyTaskIds));
        var blockerSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            blockers,
            AgentWorkPacketContractLimits.MaximumBlockers,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(blockers));
        var failureSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            knownFailures,
            AgentWorkPacketContractLimits.MaximumKnownFailures,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(knownFailures));
        var evidenceSnapshot = AgentWorkPacketShapes.SnapshotIdentifierSet(
            evidenceIds,
            AgentTaskSnapshotContractLimits.MaximumEvidencePerTask,
            nameof(evidenceIds));
        var sourceSnapshot = AgentWorkPacketShapes.SnapshotIdentifierSet(
            sourceRevisionIds,
            AgentTaskSnapshotContractLimits.MaximumSourcesPerTask,
            nameof(sourceRevisionIds));

        if (dependencySnapshot.Contains(taskId, StringComparer.Ordinal) ||
            (reportedState is AgentTaskStateIds.Planned or AgentTaskStateIds.Active &&
                nextAction is null) ||
            (reportedState is AgentTaskStateIds.Blocked or AgentTaskStateIds.Held &&
                blockerSnapshot.Count == 0))
        {
            throw new ArgumentException("The task state, next action, blockers, or dependencies are inconsistent.");
        }

        TaskId = taskId;
        ParentTaskId = parentTaskId;
        Ordinal = ordinal;
        ReportedState = reportedState;
        Objective = objective;
        CurrentState = currentState;
        NextAction = nextAction;
        Constraints = constraintSnapshot;
        DependencyTaskIds = dependencySnapshot;
        Blockers = blockerSnapshot;
        KnownFailures = failureSnapshot;
        ReportedOwner = reportedOwner;
        ApprovalGate = approvalGate ?? throw new ArgumentNullException(nameof(approvalGate));
        EvidenceIds = evidenceSnapshot;
        SourceRevisionIds = sourceSnapshot;
    }

    public string TaskId { get; }
    public string? ParentTaskId { get; }
    public int Ordinal { get; }
    public string ReportedState { get; }
    public string Objective { get; }
    public string CurrentState { get; }
    public string? NextAction { get; }
    public IReadOnlyList<string> Constraints { get; }
    public IReadOnlyList<string> DependencyTaskIds { get; }
    public IReadOnlyList<string> Blockers { get; }
    public IReadOnlyList<string> KnownFailures { get; }
    public AgentWorkOwnerV1? ReportedOwner { get; }
    public AgentWorkApprovalGateV1 ApprovalGate { get; }
    public IReadOnlyList<string> EvidenceIds { get; }
    public IReadOnlyList<string> SourceRevisionIds { get; }
}

/// <summary>
/// Complete bounded task assertions from one producer. Cortex may retain and repeat this
/// immutable revision but never infer assignments, claims, scheduling, approval, or execution.
/// </summary>
public sealed record AgentTaskSnapshotV1
{
    [JsonConstructor]
    public AgentTaskSnapshotV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string mode,
        string taskSetId,
        string snapshotId,
        long snapshotRevision,
        string? previousSnapshotHash,
        string idempotencyKey,
        string projectId,
        string? projectLinkId,
        string contextSetId,
        DateTimeOffset capturedAt,
        DateTimeOffset expiresAt,
        AgentWorkOwnerV1 producer,
        AgentWorkFreshnessV1 freshness,
        IReadOnlyList<AgentWorkSourceRevisionV1> sourceRevisions,
        IReadOnlyList<AgentTaskEvidenceReferenceV1> evidenceReferences,
        string coverage,
        IReadOnlyList<AgentTaskV1> tasks,
        string contentTreatment,
        bool executable,
        bool grantsSchedulingAuthority,
        bool grantsTaskClaim,
        bool grantsAuthority,
        string safetyNotice,
        string snapshotHash)
    {
        AgentWorkPacketShapes.RequireHeader(
            schemaVersion,
            contractId,
            AgentTaskSnapshotContractIds.Snapshot,
            canonicalization);
        if (mode is not (AgentCoordinationModeIds.Standalone or AgentCoordinationModeIds.Integrated))
            throw new ArgumentException("The coordination mode is invalid.", nameof(mode));
        AgentWorkPacketShapes.RequireIdentifier(taskSetId, nameof(taskSetId));
        AgentWorkPacketShapes.RequireIdentifier(snapshotId, nameof(snapshotId));
        AgentWorkPacketShapes.RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        AgentWorkPacketShapes.RequireIdentifier(projectId, nameof(projectId));
        if (projectLinkId is not null)
            AgentWorkPacketShapes.RequirePrefixedHash(
                projectLinkId,
                "plink_",
                nameof(projectLinkId));
        if (mode == AgentCoordinationModeIds.Integrated && projectLinkId is null)
            throw new ArgumentException("Integrated snapshots require an exact project-link identifier.", nameof(projectLinkId));
        AgentWorkPacketShapes.RequirePrefixedHash(
            contextSetId,
            "ctx_",
            nameof(contextSetId));
        if (previousSnapshotHash is not null)
            AgentWorkPacketShapes.RequireHash(previousSnapshotHash, nameof(previousSnapshotHash));
        if ((snapshotRevision == 1 && previousSnapshotHash is not null) ||
            (snapshotRevision is < 1 or > 9_007_199_254_740_991) ||
            (snapshotRevision > 1 && previousSnapshotHash is null))
        {
            throw new ArgumentException("The task-snapshot revision chain is invalid.");
        }
        AgentWorkPacketShapes.RequireUtc(capturedAt, nameof(capturedAt));
        AgentWorkPacketShapes.RequireUtc(expiresAt, nameof(expiresAt));
        if (expiresAt <= capturedAt ||
            expiresAt - capturedAt > AgentWorkPacketContractLimits.MaximumPacketLifetime)
        {
            throw new ArgumentException("The task-snapshot lifetime is invalid.", nameof(expiresAt));
        }

        Producer = producer ?? throw new ArgumentNullException(nameof(producer));
        Freshness = freshness ?? throw new ArgumentNullException(nameof(freshness));
        if (Freshness.ObservedAt > capturedAt + AgentWorkPacketContractLimits.MaximumClockSkew ||
            (Freshness.State == AgentWorkFreshnessStateIds.Current &&
                Freshness.ValidUntil < expiresAt))
        {
            throw new ArgumentException("The snapshot lifetime exceeds its reported source freshness.", nameof(freshness));
        }
        var sourceSnapshot = AgentWorkPacketShapes.SnapshotList(
                sourceRevisions,
                AgentWorkPacketContractLimits.MaximumSourceRevisions,
                nameof(sourceRevisions),
                requireNonEmpty: true)
            .OrderBy(item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (sourceSnapshot.Select(item => item.SourceId)
                .Distinct(StringComparer.Ordinal).Count() != sourceSnapshot.Length)
        {
            throw new ArgumentException("Source identifiers must be unique.", nameof(sourceRevisions));
        }
        var evidenceSnapshot = AgentWorkPacketShapes.SnapshotList(
                evidenceReferences,
                AgentTaskSnapshotContractLimits.MaximumEvidenceReferences,
                nameof(evidenceReferences))
            .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
        if (evidenceSnapshot.Select(item => item.EvidenceId)
                .Distinct(StringComparer.Ordinal).Count() != evidenceSnapshot.Length)
        {
            throw new ArgumentException("Evidence identifiers must be unique.", nameof(evidenceReferences));
        }
        if (evidenceSnapshot.Select(item => (item.Kind, item.DocumentId, item.Revision))
                .Distinct().Count() != evidenceSnapshot.Length)
        {
            throw new ArgumentException(
                "Each exact retained evidence record may be mapped only once.",
                nameof(evidenceReferences));
        }
        if (!string.Equals(coverage, AgentTaskSnapshotContractIds.Coverage, StringComparison.Ordinal))
            throw new ArgumentException("Version 1 accepts complete snapshots only.", nameof(coverage));

        var taskSnapshot = AgentWorkPacketShapes.SnapshotList(
                tasks,
                AgentTaskSnapshotContractLimits.MaximumTasks,
                nameof(tasks))
            .OrderBy(item => item.Ordinal)
            .ToArray();
        ValidateTaskSet(taskSnapshot, sourceSnapshot, evidenceSnapshot);

        if (!string.Equals(contentTreatment, AgentWorkPacketContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable || grantsSchedulingAuthority || grantsTaskClaim || grantsAuthority ||
            !string.Equals(safetyNotice, AgentTaskSnapshotContractIds.SafetyNotice, StringComparison.Ordinal))
        {
            throw new ArgumentException("The task snapshot must remain passive untrusted data without authority.");
        }
        AgentWorkPacketShapes.RequireHash(snapshotHash, nameof(snapshotHash));

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        Mode = mode;
        TaskSetId = taskSetId;
        SnapshotId = snapshotId;
        SnapshotRevision = snapshotRevision;
        PreviousSnapshotHash = previousSnapshotHash;
        IdempotencyKey = idempotencyKey;
        ProjectId = projectId;
        ProjectLinkId = projectLinkId;
        ContextSetId = contextSetId;
        CapturedAt = capturedAt;
        ExpiresAt = expiresAt;
        SourceRevisions = Array.AsReadOnly(sourceSnapshot);
        EvidenceReferences = Array.AsReadOnly(evidenceSnapshot);
        Coverage = coverage;
        Tasks = Array.AsReadOnly(taskSnapshot);
        ContentTreatment = contentTreatment;
        Executable = executable;
        GrantsSchedulingAuthority = grantsSchedulingAuthority;
        GrantsTaskClaim = grantsTaskClaim;
        GrantsAuthority = grantsAuthority;
        SafetyNotice = safetyNotice;
        SnapshotHash = snapshotHash;

        if (CortexCanonicalJson.Serialize(this).Length >
            AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes)
        {
            throw new ArgumentException("The canonical task snapshot exceeds 32 KiB.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string Mode { get; }
    public string TaskSetId { get; }
    public string SnapshotId { get; }
    public long SnapshotRevision { get; }
    public string? PreviousSnapshotHash { get; }
    public string IdempotencyKey { get; }
    public string ProjectId { get; }
    public string? ProjectLinkId { get; }
    /// <summary>
    /// Identity of the exact bounded recovery context used for this revision.
    /// This is not the task-set lineage key and may change in a valid successor.
    /// </summary>
    public string ContextSetId { get; }
    public DateTimeOffset CapturedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public AgentWorkOwnerV1 Producer { get; }
    public AgentWorkFreshnessV1 Freshness { get; }
    public IReadOnlyList<AgentWorkSourceRevisionV1> SourceRevisions { get; }
    public IReadOnlyList<AgentTaskEvidenceReferenceV1> EvidenceReferences { get; }
    public string Coverage { get; }
    public IReadOnlyList<AgentTaskV1> Tasks { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public bool GrantsSchedulingAuthority { get; }
    public bool GrantsTaskClaim { get; }
    public bool GrantsAuthority { get; }
    public string SafetyNotice { get; }
    public string SnapshotHash { get; }

    internal AgentTaskSnapshotV1 CopyWithSnapshotHash(string snapshotHash) => new(
        SchemaVersion,
        ContractId,
        Canonicalization,
        Mode,
        TaskSetId,
        SnapshotId,
        SnapshotRevision,
        PreviousSnapshotHash,
        IdempotencyKey,
        ProjectId,
        ProjectLinkId,
        ContextSetId,
        CapturedAt,
        ExpiresAt,
        Producer,
        Freshness,
        SourceRevisions,
        EvidenceReferences,
        Coverage,
        Tasks,
        ContentTreatment,
        Executable,
        GrantsSchedulingAuthority,
        GrantsTaskClaim,
        GrantsAuthority,
        SafetyNotice,
        snapshotHash);

    private static void ValidateTaskSet(
        IReadOnlyList<AgentTaskV1> tasks,
        IReadOnlyList<AgentWorkSourceRevisionV1> sources,
        IReadOnlyList<AgentTaskEvidenceReferenceV1> evidenceReferences)
    {
        if (tasks.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != tasks.Count ||
            tasks.Select(item => item.Ordinal).Distinct().Count() != tasks.Count ||
            tasks.Where((item, index) => item.Ordinal != index).Any())
        {
            throw new ArgumentException("Task identifiers and ordinals must be unique and contiguous.", nameof(tasks));
        }

        var byId = tasks.ToDictionary(item => item.TaskId, StringComparer.Ordinal);
        var sourceIds = sources.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if ((task.ParentTaskId is not null && !byId.ContainsKey(task.ParentTaskId)) ||
                task.DependencyTaskIds.Any(dependency => !byId.ContainsKey(dependency)) ||
                task.SourceRevisionIds.Any(sourceId => !sourceIds.Contains(sourceId)))
            {
                throw new ArgumentException("A task references an unknown task or source revision.", nameof(tasks));
            }
        }
        if (HasDependencyCycle(byId))
            throw new ArgumentException("The task parent/dependency graph contains a cycle.", nameof(tasks));

        var mappedEvidenceIds = evidenceReferences
            .Select(item => item.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        var taskEvidenceIds = tasks
            .SelectMany(item => item.EvidenceIds)
            .ToHashSet(StringComparer.Ordinal);
        if (!mappedEvidenceIds.SetEquals(taskEvidenceIds))
        {
            throw new ArgumentException(
                "Evidence references must exactly map the evidence identifiers used by tasks.",
                nameof(evidenceReferences));
        }
    }

    private static bool HasDependencyCycle(IReadOnlyDictionary<string, AgentTaskV1> tasks)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(string taskId)
        {
            if (visited.Contains(taskId))
                return false;
            if (!visiting.Add(taskId))
                return true;
            var task = tasks[taskId];
            if (task.ParentTaskId is not null && Visit(task.ParentTaskId))
                return true;
            if (task.DependencyTaskIds.Any(Visit))
                return true;
            visiting.Remove(taskId);
            visited.Add(taskId);
            return false;
        }

        return tasks.Keys.Any(Visit);
    }
}
