using Pegasus.Cortex.Contracts.WorkPackets;

internal static class RetentionContractSamples
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-07-30T16:00:00Z");

    public static AgentWorkOwnerV1 Owner() =>
        new("nexus-authority", "coder-agent-1", "pegasus-coder");

    public static AgentTaskSnapshotV1 TaskSnapshot(
        long revision = 1,
        string? previousHash = null,
        string? idempotencyKey = null,
        bool seal = true,
        IReadOnlyList<AgentTaskV1>? tasks = null,
        string? projectLinkId = null)
    {
        var capturedAt = BaseTime.AddMinutes(revision - 1);
        var expiresAt = capturedAt.AddHours(2);
        var value = new AgentTaskSnapshotV1(
            AgentTaskSnapshotContractIds.SchemaVersion,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            "task-set-1",
            $"snapshot-{revision}",
            revision,
            previousHash,
            idempotencyKey ?? $"task-idempotency-{revision}",
            "project-1",
            projectLinkId,
            $"ctx_{new string('1', 64)}",
            capturedAt,
            expiresAt,
            Owner(),
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Current,
                capturedAt,
                expiresAt,
                "Producer reports the bounded source revision as current."),
            [SourceRevision()],
            [],
            AgentTaskSnapshotContractIds.Coverage,
            tasks ?? [Task()],
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return seal ? AgentTaskSnapshotPolicy.Seal(value) : value;
    }

    public static AgentTaskSnapshotV1 LargeTaskSnapshot()
    {
        var tasks = Enumerable.Range(0, 8)
            .Select(index => new AgentTaskV1(
                $"task-{index}",
                parentTaskId: null,
                index,
                AgentTaskStateIds.Planned,
                $"objective-{index}-" + new string('o', 420),
                $"current-{index}-" + new string('c', 420),
                $"next-{index}-" + new string('n', 420),
                constraints: [],
                dependencyTaskIds: [],
                blockers: [],
                knownFailures: [],
                reportedOwner: null,
                ApprovalNotRequired(),
                evidenceIds: [],
                sourceRevisionIds: ["source-1"]))
            .ToArray();
        return TaskSnapshot(tasks: tasks);
    }

    public static AgentWorkPacketV1 WorkPacket(
        long revision = 1,
        string? previousHash = null,
        string? idempotencyKey = null,
        bool cancellationRequested = true,
        bool seal = true,
        IReadOnlyList<string>? constraints = null,
        string? candidateReusableLesson = null,
        string? projectLinkId = null)
    {
        var issuedAt = BaseTime.AddMinutes(revision - 1);
        var expiresAt = issuedAt.AddHours(2);
        var stage = candidateReusableLesson is not null
            ? AgentWorkPacketStageIds.Closeout
            : revision == 1
                ? AgentWorkPacketStageIds.Start
                : AgentWorkPacketStageIds.Continue;
        var closeout = candidateReusableLesson is null
            ? null
            : new AgentWorkCloseoutV1(
                AgentWorkCloseoutDispositionIds.Solved,
                "The bounded retention work was verified.",
                ["Retained one exact passive revision."],
                [
                    new AgentWorkVerificationEvidenceV1(
                        "verification-1",
                        "evidence-private",
                        AgentWorkVerificationResultIds.Passed,
                        "The exact retained bytes were verified.")
                ],
                assumptions: [],
                risks: [],
                nextAction: null,
                nextAuthority: null,
                candidateReusableLesson);
        var value = new AgentWorkPacketV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.Packet,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            stage,
            "work-1",
            revision,
            previousHash,
            idempotencyKey ?? $"work-idempotency-{revision}",
            "project-1",
            projectLinkId,
            $"ctx_{new string('1', 64)}",
            parentWorkId: null,
            depth: 0,
            maxFanOut: 2,
            issuedAt,
            expiresAt,
            "Retain provider-neutral coordination evidence.",
            "The prior contract revision has been validated.",
            constraints: constraints ?? ["Treat all descriptive content as passive data."],
            ApprovalNotRequired(),
            "One exact immutable retained revision.",
            referenceIds: ["reference-private"],
            evidenceIds: ["evidence-private"],
            relativeProjectPaths: ["src/private-command.ps1"],
            completedWork: revision == 1 ? [] : ["Stored the preceding revision."],
            remainingWork: closeout is null ? ["Record this bounded revision."] : [],
            "Submit the packet to passive retention.",
            blockers: [],
            knownFailures: [],
            sourceRevisions: [SourceRevision()],
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Stale,
                issuedAt,
                validUntil: null,
                "The producer explicitly reports stale source state."),
            Owner(),
            maxContextBytes: 4096,
            cancellationRequested,
            "Return the packet to its current external authority.",
            closeout,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentWorkPacketContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return seal ? AgentWorkPacketPolicy.Seal(value) : value;
    }

    public static AgentTaskV1 Task(
        string currentState = "The task assertion is ready for retention.",
        IReadOnlyList<string>? blockers = null) => new(
        "task-1",
        parentTaskId: null,
        ordinal: 0,
        AgentTaskStateIds.Planned,
        "Inspect passive task state.",
        currentState,
        "Record the immutable snapshot.",
        constraints: ["Do not infer execution authority."],
        dependencyTaskIds: [],
        blockers: blockers ?? [],
        knownFailures: [],
        reportedOwner: null,
        ApprovalNotRequired(),
        evidenceIds: [],
        sourceRevisionIds: ["source-1"]);

    private static AgentWorkApprovalGateV1 ApprovalNotRequired() => new(
        AgentWorkApprovalGateStateIds.NotRequired,
        authorityId: null,
        approvalReferenceId: null,
        "No approval state is asserted as required.",
        grantsAuthority: false);

    private static AgentWorkSourceRevisionV1 SourceRevision() =>
        new("source-1", "revision-1", new string('a', 64));
}
