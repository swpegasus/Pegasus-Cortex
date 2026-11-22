using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;

internal static class ProjectBriefProjectorTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-07-30T12:00:00Z");
    private static readonly string ProjectLinkId = $"plink_{new string('a', 64)}";

    public static Task AuthorityHashCompatibilityVectorAsync()
    {
        var sources = new[]
        {
            new AgentWorkSourceRevisionV1(
                "start_here", $"sha256-{new string('a', 64)}", new string('a', 64)),
            new AgentWorkSourceRevisionV1(
                "agents", $"sha256-{new string('b', 64)}", new string('b', 64)),
            new AgentWorkSourceRevisionV1(
                "cortex_state", $"sha256-{new string('c', 64)}", new string('c', 64)),
            new AgentWorkSourceRevisionV1(
                "cortex_tasks", $"sha256-{new string('d', 64)}", new string('d', 64)),
            new AgentWorkSourceRevisionV1(
                "cortex_recovery", $"sha256-{new string('e', 64)}", new string('e', 64))
        };
        Require(
            CortexProjectBriefService.ComputeAuthoritySetHash(sources) ==
                "8ccfbcd5fe7335915c33bf94cc33dea4349cb019ad2da38281ac79dff6ce889a",
            "The Core authority hash diverged from the project-local projection vector.");
        return Task.CompletedTask;
    }

    public static Task DeterministicOrdinalStateProjectionAsync()
    {
        var source = Snapshot(
            tasks:
            [
                TaskItem(0, AgentTaskStateIds.Planned, "task-planned"),
                TaskItem(
                    1,
                    AgentTaskStateIds.Active,
                    "task-current",
                    evidenceIds: ["evidence-current"],
                    approvalGate: new AgentWorkApprovalGateV1(
                        AgentWorkApprovalGateStateIds.Satisfied,
                        "external-gate-authority",
                        "approval-reference-7",
                        "Revalidate this reported approval externally.",
                        grantsAuthority: false)),
                TaskItem(
                    2,
                    AgentTaskStateIds.Blocked,
                    "task-blocked",
                    nextAction: null,
                    blockers: ["First reported blocker.", "Second reported blocker."]),
                TaskItem(3, AgentTaskStateIds.Done, "task-done", nextAction: null),
                TaskItem(4, AgentTaskStateIds.Active, "task-active-later"),
                TaskItem(5, AgentTaskStateIds.Planned, "task-planned-later"),
                TaskItem(
                    6,
                    AgentTaskStateIds.Held,
                    "task-held",
                    nextAction: null,
                    blockers: ["Reported hold."])
            ]);

        var input = Input(
            evidence:
            [
                new CortexProjectBriefProjectionEvidence(
                    "evidence-current",
                    new CortexProjectBriefReference(
                        "artifact://evidence/current",
                        new string('e', 64),
                        RecordId: null,
                        RevisionId: null))
            ]);
        var first = CortexProjectBriefProjector.Project(source, input);
        var repeated = CortexProjectBriefProjector.Project(source, input);

        Require(
            CortexCanonicalJson.Serialize(first).AsSpan().SequenceEqual(
                CortexCanonicalJson.Serialize(repeated)),
            "Equivalent projection inputs did not produce exact canonical bytes.");
        Require(first.CurrentTask.TaskId == "task-current",
            "The lowest agent-authored active ordinal was not selected as current.");
        Require(
            first.HotTasks.Select(task => task.TaskId).SequenceEqual(
            [
                "task-planned",
                "task-blocked",
                "task-active-later",
                "task-planned-later"
            ]),
            "Hot tasks did not preserve reported ordinal after state filtering.");
        Require(
            first.HotTasks.Select(task => task.SelectionRank)
                .SequenceEqual([1, 2, 3, 4]),
            "Projected task ranks were not contiguous.");
        Require(first.HasMore, "A fifth eligible hot task did not set hasMore.");
        Require(
            first.HotTasks[1].NextAction ==
                CortexProjectBriefProjector.MissingNextAction,
            "An absent next action was not represented by the explicit safe value.");
        Require(
            first.HotTasks[1].Blocker ==
                "First reported blocker.\nSecond reported blocker.",
            "Reported blockers were not preserved deterministically.");
        Require(
            first.CurrentTask.Source.SourceHash == source.SnapshotHash &&
            first.CurrentTask.Source.ProducerContract ==
                AgentTaskSnapshotContractIds.Snapshot &&
            first.CurrentTask.Source.Reference == $"hash://{source.SnapshotHash}",
            "The projected task source did not preserve the exact snapshot binding.");
        Require(
            first.CurrentTask.Gate.State ==
                AgentWorkApprovalGateStateIds.Satisfied &&
            first.CurrentTask.Gate.AuthorityId ==
                "external-gate-authority" &&
            first.CurrentTask.Gate.ApprovalReferenceId ==
                "approval-reference-7" &&
            first.CurrentTask.Gate.Summary ==
                "Revalidate this reported approval externally." &&
            !first.CurrentTask.Gate.GrantsAuthority,
            "The project brief lost the external gate revalidation reference.");
        Require(
            first.TenantId == Scope().TenantId &&
            first.ProjectLinkId == Scope().ProjectLinkId &&
            first.ProjectId == source.ProjectId &&
            first.ProjectNamespace == Scope().ProjectNamespace,
            "The projector did not use the explicit registered scope.");
        Require(
            first.ContextSetId ==
                CortexProjectBriefService.ComputeContextSetId(first),
            "The projected context identity did not bind the complete brief.");
        Require(
            first.AuthoritySetHash ==
                CortexProjectBriefService.ComputeAuthoritySetHash(
                    source.SourceRevisions),
            "The projected brief did not bind its exact authority source set.");
        Require(
            first.CheckpointReferences.Single().Reference ==
                "checkpoint://accepted/source" &&
            first.EvidenceReferences.Single().Reference ==
                "artifact://evidence/current",
            "Explicit hash-bound checkpoint or evidence references were not preserved.");
        Require(
            CortexCanonicalJson.Serialize(first).Length <=
                CortexProjectBriefContract.MaximumUtf8Bytes,
            "The projected brief exceeded its canonical byte boundary.");

        var allTerminal = CortexProjectBriefProjector.Project(
            Snapshot(tasks:
            [
                TaskItem(0, AgentTaskStateIds.Done, "terminal-0", nextAction: null),
                TaskItem(1, AgentTaskStateIds.Cancelled, "terminal-1", nextAction: null),
                TaskItem(2, AgentTaskStateIds.Done, "terminal-2", nextAction: null),
                TaskItem(3, AgentTaskStateIds.Cancelled, "terminal-3", nextAction: null),
                TaskItem(4, AgentTaskStateIds.Done, "terminal-4", nextAction: null)
            ]),
            Input());
        Require(
            allTerminal.CurrentTask.TaskId == "terminal-0" &&
            allTerminal.HotTasks.Count == 0 &&
            allTerminal.HasMore,
            "Omitted terminal source tasks did not preserve exact-read follow-up.");
        return Task.CompletedTask;
    }

    public static Task PredecessorAndFailClosedProjectionAsync()
    {
        var firstSource = Snapshot(
            tasks: [TaskItem(0, AgentTaskStateIds.Active, "task-1")]);
        var first = CortexProjectBriefProjector.Project(firstSource, Input());
        var secondSource = Snapshot(
            revision: 2,
            previousHash: firstSource.SnapshotHash,
            tasks:
            [
                TaskItem(
                    0,
                    AgentTaskStateIds.Blocked,
                    "task-1",
                    blockers: ["Blocked."])
            ]);
        var second = CortexProjectBriefProjector.Project(
            secondSource,
            Input(),
            first);

        Require(
            second.BriefRevision == 2 &&
            second.StateRevision == 2 &&
            second.PreviousContextSetId == first.ContextSetId &&
            second.SourceHash == secondSource.SnapshotHash,
            "The successor brief did not preserve its exact predecessor chain.");

        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                Snapshot(
                    seal: false,
                    tasks: [TaskItem(0, AgentTaskStateIds.Active, "task-unsealed")]),
                Input()));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                Snapshot(
                    freshnessState: AgentWorkFreshnessStateIds.Stale,
                    tasks: [TaskItem(0, AgentTaskStateIds.Active, "task-stale")]),
                Input()));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                firstSource,
                Input() with
                {
                    RegisteredScope = Scope() with
                    {
                        ProjectLinkId = $"plink_{new string('b', 64)}"
                    }
                }));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                Snapshot(tasks: []),
                Input()));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                Snapshot(tasks:
                [
                    TaskItem(
                        0,
                        AgentTaskStateIds.Active,
                        "task-title-too-long",
                        objective: new string('x', 513))
                ]),
                Input()));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                Snapshot(tasks:
                [
                    TaskItem(
                        0,
                        AgentTaskStateIds.Blocked,
                        "task-blocker-too-long",
                        blockers: [new string('a', 512), new string('b', 512)])
                ]),
                Input()));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                Snapshot(
                    revision: 2,
                    previousHash: new string('f', 64),
                    tasks: [TaskItem(0, AgentTaskStateIds.Active, "task-wrong-base")]),
                Input(),
                first));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                firstSource,
                Input() with { SourceProjectId = "different-project" }));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                firstSource,
                Input() with { CheckpointReferences = [] }));
        var retainedCheckpoint = CortexProjectBriefReferencePolicy.CreateRetained(
            ProjectLinkId,
            "project-brief-source",
            Scope().ProjectNamespace,
            "task-snapshot",
            firstSource.TaskSetId,
            firstSource.SnapshotRevision,
            new string('9', 64),
            firstSource.SnapshotHash);
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                firstSource,
                Input() with
                {
                    CheckpointReferences =
                    [
                        retainedCheckpoint with
                        {
                            RetainedRecord = retainedCheckpoint.RetainedRecord!
                                with
                                {
                                    ProjectLinkId =
                                        $"plink_{new string('9', 64)}"
                                }
                        }
                    ]
                }));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                firstSource,
                Input() with
                {
                    CheckpointReferences =
                    [
                        retainedCheckpoint with
                        {
                            RetainedRecord = retainedCheckpoint.RetainedRecord!
                                with { ProjectId = "different-project" }
                        }
                    ]
                }));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                firstSource,
                Input() with
                {
                    CheckpointReferences =
                    [
                        retainedCheckpoint with
                        {
                            RetainedRecord = retainedCheckpoint.RetainedRecord!
                                with
                                {
                                    ProjectNamespace = "different.namespace"
                                }
                        }
                    ]
                }));
        var skippedSource = Snapshot(
            revision: 2,
            previousHash: firstSource.SnapshotHash,
            tasks: [TaskItem(0, AgentTaskStateIds.Active, "task-skipped")]);
        var recoveredSource = Snapshot(
            revision: 3,
            previousHash: skippedSource.SnapshotHash,
            tasks: [TaskItem(0, AgentTaskStateIds.Active, "task-recovered")]);
        var recovered = CortexProjectBriefProjector.Project(
            recoveredSource,
            Input(
                skipped: new CortexProjectBriefSkippedSourceChainProof(
                    skippedSource.SnapshotRevision,
                    skippedSource.SnapshotRevision,
                    RevisionCount: 1,
                    skippedSource.PreviousSnapshotHash,
                    skippedSource.SnapshotHash)),
            first);
        Require(
            recovered.BriefRevision == 2 &&
            recovered.StateRevision == 3 &&
            recovered.PreviousContextSetId == first.ContextSetId,
            "A verified skipped source did not preserve independent brief and source lineages.");
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                recoveredSource,
                Input(),
                first));
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                recoveredSource,
                Input(skipped: new CortexProjectBriefSkippedSourceChainProof(
                    skippedSource.SnapshotRevision,
                    skippedSource.SnapshotRevision,
                    RevisionCount: 1,
                    StartingPreviousSnapshotHash: new string('a', 64),
                    EndingSnapshotHash: skippedSource.SnapshotHash)),
                first));
        var beyondBoundSource = Snapshot(
            revision:
                CortexProjectBriefContract.MaximumSkippedSourceRevisions + 3L,
            previousHash: new string('f', 64),
            tasks: [TaskItem(0, AgentTaskStateIds.Active, "task-gap-bound")]);
        Expect<ArgumentException>(() =>
            CortexProjectBriefProjector.Project(
                beyondBoundSource,
                Input(skipped: new CortexProjectBriefSkippedSourceChainProof(
                    FirstRevision: 2,
                    LastRevision: beyondBoundSource.SnapshotRevision - 1,
                    RevisionCount:
                        CortexProjectBriefContract.MaximumSkippedSourceRevisions,
                    StartingPreviousSnapshotHash: first.SourceHash,
                    EndingSnapshotHash: beyondBoundSource.PreviousSnapshotHash!)),
                first));
        return Task.CompletedTask;
    }

    private static AgentTaskSnapshotV1 Snapshot(
        long revision = 1,
        string? previousHash = null,
        bool seal = true,
        string freshnessState = AgentWorkFreshnessStateIds.Current,
        IReadOnlyList<AgentTaskV1>? tasks = null)
    {
        var expiresAt = CapturedAt.AddHours(2);
        var taskItems = tasks ?? [TaskItem(0, AgentTaskStateIds.Active, "task-default")];
        var evidenceReferences = taskItems
            .SelectMany(task => task.EvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(evidenceId => evidenceId, StringComparer.Ordinal)
            .Select(evidenceId => new AgentTaskEvidenceReferenceV1(
                evidenceId,
                AgentTaskEvidenceKindIds.WorkPacket,
                $"evidence-record-{evidenceId}",
                revision: 1,
                new string('e', 64),
                new string('f', 64)))
            .ToArray();
        var value = new AgentTaskSnapshotV1(
            AgentTaskSnapshotContractIds.SchemaVersion,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Integrated,
            "task-set-project-brief",
            $"snapshot-project-brief-{revision}",
            revision,
            previousHash,
            $"project-brief-idempotency-{revision}",
            "project-brief-source",
            ProjectLinkId,
            $"ctx_{new string((char)('0' + revision % 10), 64)}",
            CapturedAt.AddMinutes(revision - 1),
            expiresAt.AddMinutes(revision - 1),
            new AgentWorkOwnerV1(
                "reported-source-authority",
                "reported-source-agent",
                "reported-source-service"),
            new AgentWorkFreshnessV1(
                freshnessState,
                CapturedAt.AddMinutes(revision - 1),
                freshnessState == AgentWorkFreshnessStateIds.Current
                    ? expiresAt.AddMinutes(revision - 1)
                    : null,
                "Exact producer-reported source freshness."),
            [new AgentWorkSourceRevisionV1(
                "source-revision",
                $"source-revision-{revision}",
                new string((char)('a' + (revision - 1) % 6), 64))],
            evidenceReferences,
            AgentTaskSnapshotContractIds.Coverage,
            taskItems,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return seal ? AgentTaskSnapshotPolicy.Seal(value) : value;
    }

    private static AgentTaskV1 TaskItem(
        int ordinal,
        string state,
        string taskId,
        string? nextAction = "Continue from the reported task state.",
        IReadOnlyList<string>? blockers = null,
        string? objective = null,
        IReadOnlyList<string>? evidenceIds = null,
        AgentWorkApprovalGateV1? approvalGate = null) =>
        new(
            taskId,
            parentTaskId: null,
            ordinal,
            state,
            objective ?? $"Reported objective for {taskId}.",
            $"Reported current state for {taskId}.",
            nextAction,
            constraints: [],
            dependencyTaskIds: [],
            blockers ?? [],
            knownFailures: [],
            reportedOwner: null,
            approvalGate ?? new AgentWorkApprovalGateV1(
                AgentWorkApprovalGateStateIds.NotRequired,
                authorityId: null,
                approvalReferenceId: null,
                "No approval requirement was reported.",
                grantsAuthority: false),
            evidenceIds ?? [],
            sourceRevisionIds: ["source-revision"]);

    private static CortexProjectBriefProjectionInput Input(
        IReadOnlyList<CortexProjectBriefProjectionEvidence>? evidence = null,
        CortexProjectBriefSkippedSourceChainProof? skipped = null) =>
        new(
            "project-brief-source",
            Scope(),
            [
                new CortexProjectBriefReference(
                    "checkpoint://accepted/source",
                    new string('c', 64),
                    RecordId: null,
                    RevisionId: null)
            ],
            evidence ?? [],
            skipped);

    private static CortexProjectBriefReadScope Scope() => new(
        "tenant-local",
        ProjectLinkId,
        "project-brief-source",
        "project-brief-local",
        "registered-authority",
        "registered-subject",
        "registered-service",
        PolicyRevision: 1);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Expect<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}.");
    }

}
