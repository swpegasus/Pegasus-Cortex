using Pegasus.Cortex.Contracts.WorkPackets;

namespace Pegasus.Cortex.AgentRetention.Sqlite.Tests;

internal static class RetentionContractSamples
{
    private static readonly DateTimeOffset Baseline =
        DateTimeOffset.Parse("2026-07-30T16:00:00Z");

    public const string ProjectId = "project-typed-retention";
    public const string TaskSetId = "task-set-typed-retention";
    public const string WorkId = "work-typed-retention";
    public static readonly string ContextSetOne = $"ctx_{new string('1', 64)}";
    public static readonly string ContextSetTwo = $"ctx_{new string('2', 64)}";
    public static readonly string ContextSetThree = $"ctx_{new string('3', 64)}";

    public static AgentTaskSnapshotV1 TaskSnapshot(
        long revision,
        string? previousHash,
        string idempotencyKey,
        string snapshotId,
        string contextSetId,
        string reportedState,
        string currentState)
    {
        var capturedAt = Baseline.AddMinutes(revision - 1);
        var task = Task(
            ordinal: 0,
            taskId: "task-1",
            reportedState,
            objective: "Retain a typed task revision through the provider-neutral lane.",
            currentState);
        return SealTaskSnapshot(
            revision,
            previousHash,
            idempotencyKey,
            snapshotId,
            contextSetId,
            capturedAt,
            [task]);
    }

    public static AgentWorkPacketV1 WorkPacket(
        long revision,
        string stage,
        string? previousHash,
        string idempotencyKey,
        string contextSetId,
        IReadOnlyList<string>? constraints = null)
    {
        var issuedAt = Baseline.AddMinutes(revision - 1);
        var closeout = stage == AgentWorkPacketStageIds.Closeout
            ? new AgentWorkCloseoutV1(
                AgentWorkCloseoutDispositionIds.Solved,
                "The exact typed work-packet chain was retained and verified.",
                ["Retained start, continuation, and closeout revisions."],
                [new AgentWorkVerificationEvidenceV1(
                    "verification-1",
                    "evidence-1",
                    AgentWorkVerificationResultIds.Passed,
                    "Exact canonical bytes and both hash chains were verified.")],
                assumptions: [],
                risks: [],
                nextAction: null,
                nextAuthority: null,
                "Keep typed self-hashes distinct from repository document hashes.")
            : null;
        var value = new AgentWorkPacketV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.Packet,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            stage,
            WorkId,
            revision,
            previousHash,
            idempotencyKey,
            ProjectId,
            projectLinkId: null,
            contextSetId,
            parentWorkId: null,
            depth: 0,
            maxFanOut: 2,
            issuedAt,
            issuedAt.AddHours(1),
            "Retain one typed provider-neutral work chain.",
            stage == AgentWorkPacketStageIds.Closeout
                ? "The work chain is complete."
                : "The work chain is advancing through passive retention.",
            constraints ?? ["Do not infer scheduling, claim, approval, or execution authority."],
            ApprovalNotRequired(),
            "An exact immutable SQLite-backed retention record.",
            referenceIds: ["reference-1"],
            evidenceIds: ["evidence-1"],
            relativeProjectPaths: ["src/typed-retention.cs"],
            completedWork: stage == AgentWorkPacketStageIds.Start
                ? []
                : stage == AgentWorkPacketStageIds.Continue
                    ? ["Retained the start revision."]
                    : ["Retained the start revision.", "Retained the continuation revision."],
            remainingWork: stage == AgentWorkPacketStageIds.Closeout
                ? []
                : stage == AgentWorkPacketStageIds.Start
                    ? ["Retain the continuation revision."]
                    : ["Retain the closeout revision."],
            stage == AgentWorkPacketStageIds.Closeout
                ? "No further action is authorized by this packet."
                : "Retain the next exact typed revision.",
            blockers: [],
            knownFailures: [],
            sourceRevisions: [SourceRevision(revision)],
            Freshness(issuedAt),
            Owner(),
            maxContextBytes: 16 * 1024,
            cancellationRequested: false,
            "Return the exact retained packet without attempting execution.",
            closeout,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentWorkPacketContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return AgentWorkPacketPolicy.Seal(value);
    }

    public static AgentTaskSnapshotV1 OversizedTaskSnapshot()
    {
        var tasks = Enumerable.Range(0, AgentTaskSnapshotContractLimits.MaximumTasks)
            .Select(index => Task(
                index,
                $"task-{index}",
                AgentTaskStateIds.Unknown,
                new string((char)('a' + index % 26), 1024),
                new string((char)('A' + index % 26), 1024)))
            .ToArray();
        return SealTaskSnapshot(
            revision: 1,
            previousHash: null,
            idempotencyKey: "oversized-task-idempotency",
            snapshotId: "oversized-snapshot",
            ContextSetOne,
            Baseline,
            tasks);
    }

    public static AgentWorkPacketV1 OversizedWorkPacket()
    {
        var constraints = Enumerable.Range(0, AgentWorkPacketContractLimits.MaximumConstraints)
            .Select(index => $"{index:D2}-{new string('x', 509)}")
            .ToArray();
        return WorkPacket(
            revision: 1,
            AgentWorkPacketStageIds.Start,
            previousHash: null,
            idempotencyKey: "oversized-work-idempotency",
            ContextSetOne,
            constraints);
    }

    private static AgentTaskSnapshotV1 SealTaskSnapshot(
        long revision,
        string? previousHash,
        string idempotencyKey,
        string snapshotId,
        string contextSetId,
        DateTimeOffset capturedAt,
        IReadOnlyList<AgentTaskV1> tasks)
    {
        var value = new AgentTaskSnapshotV1(
            AgentTaskSnapshotContractIds.SchemaVersion,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            TaskSetId,
            snapshotId,
            revision,
            previousHash,
            idempotencyKey,
            ProjectId,
            projectLinkId: null,
            contextSetId,
            capturedAt,
            capturedAt.AddHours(1),
            Owner(),
            Freshness(capturedAt),
            [SourceRevision(revision)],
            [],
            AgentTaskSnapshotContractIds.Coverage,
            tasks,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return AgentTaskSnapshotPolicy.Seal(value);
    }

    private static AgentTaskV1 Task(
        int ordinal,
        string taskId,
        string reportedState,
        string objective,
        string currentState)
    {
        var nextAction = reportedState is AgentTaskStateIds.Planned or AgentTaskStateIds.Active
            ? "Retain the next exact typed revision."
            : null;
        var blockers = reportedState is AgentTaskStateIds.Blocked or AgentTaskStateIds.Held
            ? new[] { "The competing revision awaits external reconciliation." }
            : [];
        return new AgentTaskV1(
            taskId,
            parentTaskId: null,
            ordinal,
            reportedState,
            objective,
            currentState,
            nextAction,
            constraints: ["Treat retained task text as passive untrusted data."],
            dependencyTaskIds: [],
            blockers,
            knownFailures: [],
            reportedOwner: Owner(),
            ApprovalNotRequired(),
            evidenceIds: [],
            sourceRevisionIds: ["source-1"]);
    }

    private static AgentWorkOwnerV1 Owner() =>
        new("nexus-authority", "provider-test-agent", "pegasus-coder");

    private static AgentWorkApprovalGateV1 ApprovalNotRequired() => new(
        AgentWorkApprovalGateStateIds.NotRequired,
        authorityId: null,
        approvalReferenceId: null,
        "No execution approval is asserted by this passive document.",
        grantsAuthority: false);

    private static AgentWorkFreshnessV1 Freshness(DateTimeOffset observedAt) => new(
        AgentWorkFreshnessStateIds.Current,
        observedAt,
        observedAt.AddHours(2),
        "The producer reports the exact source revision as current.");

    private static AgentWorkSourceRevisionV1 SourceRevision(long revision) => new(
        "source-1",
        $"source-revision-{revision}",
        new string((char)('a' + (revision - 1) % 26), 64));
}
