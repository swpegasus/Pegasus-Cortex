using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;

internal static class AgentCoordinationContractSourceTests
{
    private static readonly DateTimeOffset Baseline =
        DateTimeOffset.Parse("2026-07-30T18:00:00Z");
    private static readonly string SourceHash = new('a', 64);

    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
    [
        ("agent work packets are canonical bounded passive data", WorkPacketIntegrityAndBounds),
        ("work packet revisions and receipts match exact immutable bytes", WorkPacketRevisionsAndReceipts),
        ("work packet closeout is disposition and evidence complete", WorkPacketCloseoutIsComplete),
        ("task snapshots are complete bounded immutable assertions", TaskSnapshotIntegrityAndBounds),
        ("task snapshot evidence mappings are exact and hash bound", TaskSnapshotEvidenceMappings),
        ("task snapshot revision conflicts and freshness fail closed", TaskSnapshotRevisionAndFreshness),
        ("agent coordination schemas are closed bounded and passive", SchemasAreClosedBoundedAndPassive)
    ];

    private static void WorkPacketIntegrityAndBounds()
    {
        var callerPaths = new List<string> { "src/component.cs" };
        var packet = AgentWorkPacketPolicy.Seal(CreateStartPacket(relativeProjectPaths: callerPaths));
        callerPaths[0] = "changed.cs";

        Check(AgentWorkPacketPolicy.HasValidHash(packet), "The sealed packet hash did not validate.");
        Check(packet.RelativeProjectPaths.Single() == "src/component.cs", "Packet paths retained caller memory.");
        Check(CortexCanonicalJson.Serialize(packet).Length <=
              AgentWorkPacketContractLimits.MaximumSerializedPacketBytes,
            "The packet exceeded eight KiB.");
        Check(!packet.Executable && !packet.GrantsSchedulingAuthority &&
              !packet.GrantsTaskClaim && !packet.GrantsAuthority &&
              packet.ContentTreatment == AgentWorkPacketContractIds.ContentTreatment,
            "Passive safety labels changed.");

        var roundTrip = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
            CortexCanonicalJson.Serialize(packet));
        Check(AgentWorkPacketPolicy.HasValidHash(roundTrip), "Round-tripped packet integrity changed.");
        Check(AgentWorkPacketPolicy.SourceHashesMatch(
                packet,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["source-1"] = SourceHash }),
            "Exact source hash matching failed.");
        Check(!AgentWorkPacketPolicy.SourceHashesMatch(
                packet,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["source-1"] = new string('b', 64) }),
            "A changed source hash was accepted.");

        var node = JsonNode.Parse(CortexCanonicalJson.Serialize(packet))!.AsObject();
        node["unexpected"] = true;
        Assert.Throws<JsonException>(() => CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
            Encoding.UTF8.GetBytes(node.ToJsonString())));
        Assert.Throws<ArgumentException>(() => CreateStartPacket(relativeProjectPaths: ["D:/project/source.cs"]));
        Assert.Throws<ArgumentException>(() => CreateStartPacket(relativeProjectPaths: ["src\\source.cs"]));
        Assert.Throws<ArgumentException>(() => CreateStartPacket(contextSetId: "context-unrelated"));
        Assert.Throws<ArgumentException>(() => CreateStartPacket(projectLinkId: "project-link-unrelated"));
        Assert.Throws<ArgumentException>(() => new AgentWorkSourceRevisionV1(
            "source-1", "revision-1", new string('A', 64)));
        Assert.Throws<ArgumentException>(() => CreateStartPacket(grantsAuthority: true));

        var oversizedConstraints = Enumerable.Range(0, AgentWorkPacketContractLimits.MaximumConstraints)
            .Select(index => $"{index:D2}-{new string('x', 509)}")
            .ToArray();
        Assert.Throws<ArgumentException>(() => CreateStartPacket(constraints: oversizedConstraints));

        var stale = AgentWorkPacketPolicy.Seal(CreateStartPacket(
            freshness: new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Stale,
                Baseline,
                null,
                "The source head was not revalidated.")));
        var staleValidation = AgentWorkPacketPolicy.ValidateForHandoff(stale, Baseline.AddMinutes(5));
        Check(!staleValidation.Valid &&
              staleValidation.IssueCodes.Contains("agent_work_packet_freshness_invalid"),
            "A stale packet was accepted for handoff.");

        var cancelled = AgentWorkPacketPolicy.Seal(CreateStartPacket(cancellationRequested: true));
        var cancelledValidation = AgentWorkPacketPolicy.ValidateForHandoff(
            cancelled,
            Baseline.AddMinutes(5));
        Check(!cancelledValidation.Valid &&
              cancelledValidation.IssueCodes.Contains("agent_work_packet_cancellation_requested"),
            "A cancellation request did not block handoff.");
    }

    private static void WorkPacketRevisionsAndReceipts()
    {
        var start = AgentWorkPacketPolicy.Seal(CreateStartPacket());
        var continuation = AgentWorkPacketPolicy.Seal(CreateNextPacket(
            start,
            contextSetId: ContextSetTwo));
        Check(
            AgentWorkPacketPolicy.ValidateContinuation(start, continuation).Valid &&
            string.Equals(continuation.ContextSetId, ContextSetTwo, StringComparison.Ordinal),
            "A continuation linked to a refreshed exact recovery context was rejected.");

        var wrongPredecessor = AgentWorkPacketPolicy.Seal(CreateNextPacket(
            start,
            previousPacketHash: new string('f', 64)));
        var conflict = AgentWorkPacketPolicy.ValidateContinuation(start, wrongPredecessor);
        Check(!conflict.Valid && conflict.IssueCodes.Contains("agent_work_packet_revision_conflict"),
            "A forked continuation was accepted.");
        var lineageDrift = AgentWorkPacketPolicy.Seal(CreateNextPacket(
            start,
            parentWorkId: "different-parent",
            depth: 1,
            maxFanOut: start.MaxFanOut + 1));
        var lineageValidation = AgentWorkPacketPolicy.ValidateContinuation(start, lineageDrift);
        Check(
            !lineageValidation.Valid &&
            lineageValidation.IssueCodes.Contains("agent_work_packet_lineage_changed") &&
            lineageValidation.IssueCodes.Contains("agent_work_packet_fan_out_increased"),
            "A continuation rewrote its lineage or increased its fan-out ceiling.");
        var narrowedFanOut = AgentWorkPacketPolicy.Seal(CreateNextPacket(
            start,
            maxFanOut: start.MaxFanOut - 1));
        Check(
            AgentWorkPacketPolicy.ValidateContinuation(start, narrowedFanOut).Valid,
            "A continuation could not reduce its passive fan-out ceiling.");
        Check(AgentWorkPacketPolicy.IsIdempotentReplay(start, start),
            "An exact work-packet replay was not idempotent.");
        var sameKeyChanged = AgentWorkPacketPolicy.Seal(CreateStartPacket(
            objective: "A different objective under the same idempotency key."));
        Check(AgentWorkPacketPolicy.HasIdempotencyConflict(start, sameKeyChanged),
            "Changed bytes under one work idempotency key were not conflicting.");

        var intendedOwner = new AgentWorkOwnerV1("nexus", "worker-2", "pegasus-coder");
        var handoff = AgentWorkPacketPolicy.CreateHandoffReceipt(
            start,
            "handoff-1",
            intendedOwner,
            Baseline.AddMinutes(5));
        Check(AgentWorkPacketPolicy.ValidateHandoff(start, handoff).Valid,
            "The exact handoff receipt did not match its packet.");
        var acceptance = AgentWorkPacketPolicy.CreateAcceptanceReceipt(
            handoff,
            "acceptance-1",
            AgentWorkAcceptanceDispositionIds.Accepted,
            intendedOwner,
            Baseline.AddMinutes(6),
            "The exact passive packet was received.");
        Check(AgentWorkPacketPolicy.ValidateAcceptance(handoff, acceptance).Valid,
            "The exact acceptance receipt did not match its handoff.");

        var mismatched = AgentWorkPacketPolicy.Seal(new AgentWorkPacketAcceptanceReceiptV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.AcceptanceReceipt,
            AgentWorkPacketContractIds.Canonicalization,
            "acceptance-2",
            handoff.ReceiptId,
            new string('e', 64),
            handoff.WorkId,
            handoff.IdempotencyKey,
            handoff.PacketStage,
            handoff.PacketRevision,
            handoff.PacketHash,
            AgentWorkAcceptanceDispositionIds.Rejected,
            intendedOwner,
            Baseline.AddMinutes(7),
            "The handoff hash does not match.",
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentWorkPacketContractIds.EmptySha256));
        var mismatchValidation = AgentWorkPacketPolicy.ValidateAcceptance(handoff, mismatched);
        Check(!mismatchValidation.Valid &&
              mismatchValidation.IssueCodes.Contains("agent_work_acceptance_handoff_mismatch"),
            "A mismatched acceptance receipt was accepted.");
        Assert.Throws<ArgumentException>(() => AgentWorkPacketPolicy.CreateHandoffReceipt(
            start,
            "handoff-expired",
            intendedOwner,
            start.ExpiresAt));
    }

    private static void WorkPacketCloseoutIsComplete()
    {
        var start = AgentWorkPacketPolicy.Seal(CreateStartPacket());
        var solvedCloseout = new AgentWorkCloseoutV1(
            AgentWorkCloseoutDispositionIds.Solved,
            "The bounded contract change is complete.",
            ["Added passive contract models."],
            [new AgentWorkVerificationEvidenceV1(
                "verification-1",
                "evidence-1",
                AgentWorkVerificationResultIds.Passed,
                "The focused source harness passed.")],
            [],
            [],
            null,
            null,
            "Keep retained task assertions separate from execution authority.");
        var solved = AgentWorkPacketPolicy.Seal(CreateNextPacket(
            start,
            stage: AgentWorkPacketStageIds.Closeout,
            remainingWork: [],
            nextAction: "No further work is authorized by this packet.",
            closeout: solvedCloseout));
        Check(AgentWorkPacketPolicy.ValidateContinuation(start, solved).Valid &&
              solved.Closeout?.Disposition == AgentWorkCloseoutDispositionIds.Solved,
            "A verified solved closeout was rejected.");

        var nextOwner = new AgentWorkOwnerV1("nexus", "maintainer-1", "pegasus-coder");
        const string nextAction = "Run the separately authorized integration matrix.";
        var partialCloseout = new AgentWorkCloseoutV1(
            AgentWorkCloseoutDispositionIds.Partial,
            "Source-level contract work is complete but integration remains.",
            ["Added source-level contracts."],
            [new AgentWorkVerificationEvidenceV1(
                "verification-2",
                "evidence-1",
                AgentWorkVerificationResultIds.Inconclusive,
                "The broader matrix was not part of this packet.")],
            ["The owning integrator will preserve the contract IDs."],
            ["A downstream adapter may still require changes."],
            nextAction,
            nextOwner,
            null);
        var partial = AgentWorkPacketPolicy.Seal(CreateNextPacket(
            start,
            stage: AgentWorkPacketStageIds.Closeout,
            remainingWork: ["Run the integration matrix."],
            nextAction: nextAction,
            closeout: partialCloseout));
        Check(AgentWorkPacketPolicy.HasValidHash(partial), "A bounded partial closeout did not seal.");

        Assert.Throws<ArgumentException>(() => new AgentWorkCloseoutV1(
            AgentWorkCloseoutDispositionIds.Solved,
            "The result is incorrectly described as solved.",
            [],
            [new AgentWorkVerificationEvidenceV1(
                "verification-3",
                "evidence-1",
                AgentWorkVerificationResultIds.Failed,
                "Verification failed.")],
            [],
            [],
            null,
            null,
            null));
    }

    private static void TaskSnapshotIntegrityAndBounds()
    {
        var states = new[]
        {
            AgentTaskStateIds.Planned,
            AgentTaskStateIds.Active,
            AgentTaskStateIds.Blocked,
            AgentTaskStateIds.Held,
            AgentTaskStateIds.Done,
            AgentTaskStateIds.Cancelled,
            AgentTaskStateIds.Unknown
        };
        var tasks = states
            .Select((state, ordinal) => CreateTask(ordinal, state))
            .Reverse()
            .ToArray();
        var snapshot = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(tasks));
        Check(AgentTaskSnapshotPolicy.HasValidHash(snapshot), "The sealed task snapshot hash did not validate.");
        Check(snapshot.Tasks.Select(task => task.Ordinal).SequenceEqual(Enumerable.Range(0, states.Length)),
            "Tasks were not canonicalized into ordinal order.");
        Check(snapshot.Tasks.Select(task => task.ReportedState).Order(StringComparer.Ordinal)
                .SequenceEqual(states.Order(StringComparer.Ordinal)),
            "A supported task state changed.");
        Check(CortexCanonicalJson.Serialize(snapshot).Length <=
              AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes,
            "The task snapshot exceeded 32 KiB.");
        var roundTrip = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            CortexCanonicalJson.Serialize(snapshot));
        Check(AgentTaskSnapshotPolicy.HasValidHash(roundTrip), "Round-tripped task snapshot integrity changed.");
        Check(AgentTaskSnapshotPolicy.SourceHashesMatch(
                snapshot,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["source-1"] = SourceHash }),
            "Task snapshot source hashes did not match.");

        var maximumTasks = Enumerable.Range(0, AgentTaskSnapshotContractLimits.MaximumTasks)
            .Select(index => CreateTask(index, AgentTaskStateIds.Unknown))
            .ToArray();
        var maximumSnapshot = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(maximumTasks));
        Check(maximumSnapshot.Tasks.Count == AgentTaskSnapshotContractLimits.MaximumTasks,
            "The exact 32-task ceiling was rejected.");
        var tooMany = maximumTasks
            .Concat([CreateTask(31, AgentTaskStateIds.Unknown, taskId: "task-overflow")])
            .ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(tooMany));

        var oversizedTasks = Enumerable.Range(0, AgentTaskSnapshotContractLimits.MaximumTasks)
            .Select(index => CreateTask(
                index,
                AgentTaskStateIds.Unknown,
                objective: new string((char)('a' + index % 26), 1024),
                currentState: new string((char)('A' + index % 26), 1024)))
            .ToArray();
        Assert.Throws<ArgumentException>(() => CreateSnapshot(oversizedTasks));

        var first = CreateTask(
            0,
            AgentTaskStateIds.Unknown,
            dependencyTaskIds: ["task-1"]);
        var second = CreateTask(
            1,
            AgentTaskStateIds.Unknown,
            dependencyTaskIds: ["task-0"]);
        Assert.Throws<ArgumentException>(() => CreateSnapshot([first, second]));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(
            [CreateTask(0, AgentTaskStateIds.Unknown)],
            mode: AgentCoordinationModeIds.Integrated,
            projectLinkId: null));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(
            [CreateTask(0, AgentTaskStateIds.Unknown)],
            contextSetId: "context-unrelated"));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(
            [CreateTask(0, AgentTaskStateIds.Unknown)],
            projectLinkId: "project-link-unrelated"));
    }

    private static void TaskSnapshotEvidenceMappings()
    {
        var callerReferences = new List<AgentTaskEvidenceReferenceV1>
        {
            EvidenceReference(
                "evidence-b",
                AgentTaskEvidenceKindIds.WorkPacketHandoffReceipt,
                "handoff-1",
                contentHash: new string('c', 64),
                assertionHash: new string('d', 64)),
            EvidenceReference("evidence-a")
        };
        var tasks =
            new[] { CreateTask(
                0,
                AgentTaskStateIds.Unknown,
                evidenceIds: ["evidence-b", "evidence-a"]) };
        var snapshot = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(
            tasks,
            evidenceReferences: callerReferences));
        callerReferences.Clear();

        Check(
            snapshot.EvidenceReferences.Select(item => item.EvidenceId)
                .SequenceEqual(["evidence-a", "evidence-b"]) &&
            snapshot.EvidenceReferences.Count == 2,
            "Evidence references were not copied and sorted deterministically.");
        Check(
            AgentTaskSnapshotPolicy.HasValidHash(snapshot) &&
            !snapshot.Executable &&
            !snapshot.GrantsSchedulingAuthority &&
            !snapshot.GrantsTaskClaim &&
            !snapshot.GrantsAuthority,
            "A mapped snapshot lost integrity or passive no-authority semantics.");

        var roundTrip = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            CortexCanonicalJson.Serialize(snapshot));
        Check(
            AgentTaskSnapshotPolicy.HasValidHash(roundTrip) &&
            roundTrip.EvidenceReferences.Select(item => item.EvidenceId)
                .SequenceEqual(["evidence-a", "evidence-b"]),
            "A hash-bound evidence map did not survive canonical round-trip.");
        var unknownField = JsonNode.Parse(CortexCanonicalJson.Serialize(snapshot))!.AsObject();
        unknownField["evidenceReferences"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        Assert.Throws<JsonException>(() => CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            Encoding.UTF8.GetBytes(unknownField.ToJsonString())));

        var changedBinding = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(
            tasks,
            evidenceReferences:
            [
                EvidenceReference(
                    "evidence-b",
                    AgentTaskEvidenceKindIds.WorkPacketHandoffReceipt,
                    "handoff-1",
                    contentHash: new string('c', 64),
                    assertionHash: new string('e', 64)),
                EvidenceReference("evidence-a")
            ]));
        Check(
            !string.Equals(snapshot.SnapshotHash, changedBinding.SnapshotHash, StringComparison.Ordinal),
            "Changing an evidence assertion hash did not change the snapshot self-hash.");

        Assert.Throws<ArgumentException>(() => CreateSnapshot(
            tasks,
            evidenceReferences: [EvidenceReference("evidence-a")]));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(
            [CreateTask(
                0,
                AgentTaskStateIds.Unknown,
                evidenceIds: ["evidence-a"])],
            evidenceReferences:
            [
                EvidenceReference("evidence-a"),
                EvidenceReference("evidence-extra", documentId: "work-extra")
            ]));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(
            [CreateTask(
                0,
                AgentTaskStateIds.Unknown,
                evidenceIds: ["evidence-a"])],
            evidenceReferences:
            [
                EvidenceReference("evidence-a"),
                EvidenceReference("evidence-a", documentId: "work-other")
            ]));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(
            [CreateTask(
                0,
                AgentTaskStateIds.Unknown,
                evidenceIds: ["evidence-a", "evidence-b"])],
            evidenceReferences:
            [
                EvidenceReference("evidence-a"),
                EvidenceReference("evidence-b")
            ]));

        Assert.Throws<ArgumentException>(() => EvidenceReference(
            "evidence-a",
            kind: "arbitrary-record"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceReference(
            "evidence-a",
            revision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvidenceReference(
            "evidence-a",
            revision: AgentTaskSnapshotContractLimits.MaximumEvidenceRevision + 1));
        Assert.Throws<ArgumentException>(() => EvidenceReference(
            "evidence-a",
            contentHash: new string('A', 64)));
        Assert.Throws<ArgumentException>(() => EvidenceReference(
            "evidence-a",
            assertionHash: new string('B', 64)));
        Assert.Throws<ArgumentException>(() => EvidenceReference(
            "-evidence",
            documentId: "work-1"));
        Assert.Throws<ArgumentException>(() => EvidenceReference(
            "evidence-a",
            documentId: "-work"));

        var tooManyReferences = Enumerable
            .Range(0, AgentTaskSnapshotContractLimits.MaximumEvidenceReferences + 1)
            .Select(index => EvidenceReference(
                $"evidence-{index}",
                documentId: $"work-{index}"))
            .ToArray();
        var tooManyIds = tooManyReferences.Select(item => item.EvidenceId).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(
            [
                CreateTask(
                    0,
                    AgentTaskStateIds.Unknown,
                    evidenceIds: tooManyIds.Take(
                        AgentTaskSnapshotContractLimits.MaximumEvidencePerTask).ToArray()),
                CreateTask(
                    1,
                    AgentTaskStateIds.Unknown,
                    evidenceIds: tooManyIds.Skip(
                        AgentTaskSnapshotContractLimits.MaximumEvidencePerTask).ToArray())
            ],
            evidenceReferences: tooManyReferences));
    }

    private static void TaskSnapshotRevisionAndFreshness()
    {
        var initial = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(
            [CreateTask(0, AgentTaskStateIds.Active)]));
        var next = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(
            [CreateTask(0, AgentTaskStateIds.Done)],
            snapshotRevision: 2,
            previousSnapshotHash: initial.SnapshotHash,
            idempotencyKey: "snapshot-idempotency-2",
            snapshotId: "snapshot-2",
            capturedAt: Baseline.AddMinutes(10),
            contextSetId: ContextSetTwo));
        Check(
            AgentTaskSnapshotPolicy.ValidateRevision(initial, next).Valid &&
            string.Equals(next.ContextSetId, ContextSetTwo, StringComparison.Ordinal),
            "A task revision linked to a refreshed exact recovery context was rejected.");
        Check(AgentTaskSnapshotPolicy.IsFreshAt(next, Baseline.AddMinutes(15)),
            "A current task snapshot was reported stale.");

        var fork = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(
            [CreateTask(0, AgentTaskStateIds.Done)],
            snapshotRevision: 2,
            previousSnapshotHash: new string('f', 64),
            idempotencyKey: "snapshot-idempotency-3",
            snapshotId: "snapshot-3",
            capturedAt: Baseline.AddMinutes(10)));
        var conflict = AgentTaskSnapshotPolicy.ValidateRevision(initial, fork);
        Check(!conflict.Valid && conflict.IssueCodes.Contains("agent_task_snapshot_revision_conflict"),
            "A forked task snapshot was accepted.");
        Check(AgentTaskSnapshotPolicy.IsIdempotentReplay(initial, initial),
            "An exact task-snapshot replay was not idempotent.");

        var sameKeyChanged = AgentTaskSnapshotPolicy.Seal(CreateSnapshot(
            [CreateTask(0, AgentTaskStateIds.Unknown)],
            idempotencyKey: initial.IdempotencyKey,
            snapshotId: "snapshot-changed"));
        Check(AgentTaskSnapshotPolicy.HasIdempotencyConflict(initial, sameKeyChanged),
            "Changed bytes under one task-snapshot idempotency key were not conflicting.");
        Check(!AgentTaskSnapshotPolicy.IsFreshAt(initial, initial.ExpiresAt),
            "An expired task snapshot was reported current.");
    }

    private static void SchemasAreClosedBoundedAndPassive()
    {
        var root = FindProjectRoot();
        var names = new[]
        {
            "agent-work-packet.v1.schema.json",
            "agent-work-packet-handoff-receipt.v1.schema.json",
            "agent-work-packet-acceptance-receipt.v1.schema.json",
            "agent-task-snapshot.v1.schema.json"
        };
        foreach (var name in names)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", name)));
            Check(!document.RootElement.GetProperty("additionalProperties").GetBoolean(),
                $"{name} must reject unknown root fields.");
            Check(!document.RootElement.GetRawText().Contains("\"additionalProperties\": true", StringComparison.Ordinal),
                $"{name} contains an open nested object.");
            Check(document.RootElement.GetRawText().Contains("\"untrusted_data\"", StringComparison.Ordinal),
                $"{name} lost its untrusted-data label.");
            foreach (var safetyFlag in new[]
                     {
                         "executable",
                         "grantsSchedulingAuthority",
                         "grantsTaskClaim",
                         "grantsAuthority"
                     })
            {
                Check(!document.RootElement.GetProperty("properties")
                        .GetProperty(safetyFlag)
                        .GetProperty("const")
                        .GetBoolean(),
                    $"{name} lost passive safety flag {safetyFlag}.");
            }
        }

        using var packet = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[0])));
        var packetProperties = packet.RootElement.GetProperty("properties");
        Check(packetProperties.GetProperty("contractId").GetProperty("const").GetString() ==
              AgentWorkPacketContractIds.Packet, "Work-packet contract ID drifted.");
        Check(packetProperties.GetProperty("maxContextBytes").GetProperty("maximum").GetInt32() ==
              AgentWorkPacketContractLimits.MaximumContextBytes, "Context byte limit drifted.");
        Check(packetProperties.GetProperty("relativeProjectPaths").GetProperty("maxItems").GetInt32() ==
              AgentWorkPacketContractLimits.MaximumProjectPaths, "Project-path limit drifted.");
        var closeout = packet.RootElement.GetProperty("$defs").GetProperty("closeout");
        Check(closeout.GetRawText().Contains("\"solved\"", StringComparison.Ordinal) &&
              closeout.GetRawText().Contains("\"partial\"", StringComparison.Ordinal) &&
              closeout.GetRawText().Contains("\"blocked\"", StringComparison.Ordinal) &&
              closeout.GetRawText().Contains("\"failed\"", StringComparison.Ordinal),
            "Closeout dispositions are incomplete.");

        using var taskSnapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[3])));
        var taskProperties = taskSnapshot.RootElement.GetProperty("properties");
        Check(taskProperties.GetProperty("contractId").GetProperty("const").GetString() ==
              AgentTaskSnapshotContractIds.Snapshot, "Task-snapshot contract ID drifted.");
        Check(taskProperties.GetProperty("tasks").GetProperty("maxItems").GetInt32() ==
              AgentTaskSnapshotContractLimits.MaximumTasks, "The 32-task ceiling drifted.");
        Check(taskProperties.GetProperty("evidenceReferences").GetProperty("maxItems").GetInt32() ==
              AgentTaskSnapshotContractLimits.MaximumEvidenceReferences,
            "The evidence-reference ceiling drifted.");
        Check(
            taskSnapshot.RootElement.GetProperty("required")
                .EnumerateArray()
                .Any(item => item.GetString() == "evidenceReferences"),
            "The evidence-reference map is not required by the task-snapshot schema.");
        var evidenceReference = taskSnapshot.RootElement.GetProperty("$defs")
            .GetProperty("evidenceReference");
        Check(!evidenceReference.GetProperty("additionalProperties").GetBoolean(),
            "Task evidence references must reject unknown fields.");
        Check(
            evidenceReference.GetProperty("properties")
                .GetProperty("revision")
                .GetProperty("maximum")
                .GetInt64() == AgentTaskSnapshotContractLimits.MaximumEvidenceRevision,
            "The evidence revision bound drifted.");
        Check(
            evidenceReference.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(
                [
                    AgentTaskEvidenceKindIds.WorkPacket,
                    AgentTaskEvidenceKindIds.WorkPacketHandoffReceipt,
                    AgentTaskEvidenceKindIds.WorkPacketAcceptanceReceipt
                ]),
            "Task evidence kinds drifted from the source contract.");
        var states = taskSnapshot.RootElement.GetProperty("$defs")
            .GetProperty("task")
            .GetProperty("properties")
            .GetProperty("reportedState")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        Check(states.SetEquals(new[]
            {
                AgentTaskStateIds.Planned,
                AgentTaskStateIds.Active,
                AgentTaskStateIds.Blocked,
                AgentTaskStateIds.Held,
                AgentTaskStateIds.Done,
                AgentTaskStateIds.Cancelled,
                AgentTaskStateIds.Unknown
            }),
            "Task states drifted from the source contract.");
    }

    private static AgentWorkPacketV1 CreateStartPacket(
        IReadOnlyList<string>? relativeProjectPaths = null,
        IReadOnlyList<string>? constraints = null,
        AgentWorkFreshnessV1? freshness = null,
        bool cancellationRequested = false,
        bool grantsAuthority = false,
        string objective = "Implement one bounded passive contract revision.",
        string? projectLinkId = ProjectLinkOne,
        string? contextSetId = null)
    {
        return new AgentWorkPacketV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.Packet,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Integrated,
            AgentWorkPacketStageIds.Start,
            "work-1",
            1,
            null,
            "work-idempotency-1",
            "project-1",
            projectLinkId,
            contextSetId ?? ContextSetOne,
            null,
            0,
            4,
            Baseline,
            Baseline.AddHours(1),
            objective,
            "Authority and source contracts were inspected.",
            constraints ?? ["No live service contact.", "Retain passive untrusted semantics."],
            ApprovalGate(),
            "A source-level contract and focused tests.",
            ["reference-1"],
            ["evidence-1"],
            relativeProjectPaths ?? ["src/component.cs"],
            [],
            ["Implement the source contract."],
            "Implement and verify the bounded source contract.",
            [],
            [],
            [SourceRevision()],
            freshness ?? Freshness(Baseline),
            Owner(),
            64 * 1024,
            cancellationRequested,
            "Return the exact unresolved constraint without attempting execution.",
            null,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority,
            AgentWorkPacketContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
    }

    private static AgentWorkPacketV1 CreateNextPacket(
        AgentWorkPacketV1 previous,
        string stage = AgentWorkPacketStageIds.Continue,
        string? previousPacketHash = null,
        string? contextSetId = null,
        IReadOnlyList<string>? remainingWork = null,
        string nextAction = "Complete the remaining bounded contract work.",
        AgentWorkCloseoutV1? closeout = null,
        string? parentWorkId = null,
        int? depth = null,
        int? maxFanOut = null)
    {
        var issuedAt = previous.IssuedAt.AddMinutes(10);
        return new AgentWorkPacketV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.Packet,
            AgentWorkPacketContractIds.Canonicalization,
            previous.Mode,
            stage,
            previous.WorkId,
            previous.PacketRevision + 1,
            previousPacketHash ?? previous.PacketHash,
            $"work-idempotency-{previous.PacketRevision + 1}",
            previous.ProjectId,
            previous.ProjectLinkId,
            contextSetId ?? previous.ContextSetId,
            parentWorkId ?? previous.ParentWorkId,
            depth ?? previous.Depth,
            maxFanOut ?? previous.MaxFanOut,
            issuedAt,
            issuedAt.AddHours(1),
            previous.Objective,
            "The preceding immutable work revision is complete.",
            previous.Constraints,
            previous.ApprovalGate,
            previous.ExpectedOutput,
            previous.ReferenceIds,
            previous.EvidenceIds,
            previous.RelativeProjectPaths,
            ["Completed the preceding work revision."],
            remainingWork ?? ["Complete the next contract revision."],
            nextAction,
            [],
            [],
            previous.SourceRevisions,
            Freshness(issuedAt),
            previous.Owner,
            previous.MaxContextBytes,
            cancellationRequested: false,
            previous.Fallback,
            closeout,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentWorkPacketContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
    }

    private static AgentTaskV1 CreateTask(
        int ordinal,
        string state,
        string? taskId = null,
        IReadOnlyList<string>? dependencyTaskIds = null,
        string? objective = null,
        string? currentState = null,
        IReadOnlyList<string>? evidenceIds = null)
    {
        var nextAction = state is AgentTaskStateIds.Planned or AgentTaskStateIds.Active
            ? $"Continue task {ordinal}."
            : null;
        var blockers = state is AgentTaskStateIds.Blocked or AgentTaskStateIds.Held
            ? new[] { $"Task {ordinal} awaits an external decision." }
            : [];
        return new AgentTaskV1(
            taskId ?? $"task-{ordinal}",
            null,
            ordinal,
            state,
            objective ?? $"Complete bounded task {ordinal}.",
            currentState ?? $"Task {ordinal} is reported as {state}.",
            nextAction,
            ["Do not infer execution authority."],
            dependencyTaskIds ?? [],
            blockers,
            [],
            Owner(),
            ApprovalGate(),
            evidenceIds ?? [],
            ["source-1"]);
    }

    private static AgentTaskSnapshotV1 CreateSnapshot(
        IReadOnlyList<AgentTaskV1> tasks,
        long snapshotRevision = 1,
        string? previousSnapshotHash = null,
        string idempotencyKey = "snapshot-idempotency-1",
        string snapshotId = "snapshot-1",
        DateTimeOffset? capturedAt = null,
        string mode = AgentCoordinationModeIds.Integrated,
        string? projectLinkId = ProjectLinkOne,
        string? contextSetId = null,
        IReadOnlyList<AgentTaskEvidenceReferenceV1>? evidenceReferences = null)
    {
        var captured = capturedAt ?? Baseline;
        return new AgentTaskSnapshotV1(
            AgentTaskSnapshotContractIds.SchemaVersion,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            mode,
            "task-set-1",
            snapshotId,
            snapshotRevision,
            previousSnapshotHash,
            idempotencyKey,
            "project-1",
            projectLinkId,
            contextSetId ?? ContextSetOne,
            captured,
            captured.AddHours(1),
            Owner(),
            Freshness(captured),
            [SourceRevision()],
            evidenceReferences ?? [],
            AgentTaskSnapshotContractIds.Coverage,
            tasks,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
    }

    private static AgentTaskEvidenceReferenceV1 EvidenceReference(
        string evidenceId,
        string kind = AgentTaskEvidenceKindIds.WorkPacket,
        string documentId = "work-1",
        long revision = 1,
        string? contentHash = null,
        string? assertionHash = null) =>
        new(
            evidenceId,
            kind,
            documentId,
            revision,
            contentHash ?? new string('a', 64),
            assertionHash ?? new string('b', 64));

    private static AgentWorkOwnerV1 Owner() =>
        new("nexus", "agent-1", "pegasus-coder");

    private static readonly string ContextSetOne = $"ctx_{new string('1', 64)}";
    private static readonly string ContextSetTwo = $"ctx_{new string('2', 64)}";
    private const string ProjectLinkOne =
        "plink_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static AgentWorkApprovalGateV1 ApprovalGate() =>
        new(
            AgentWorkApprovalGateStateIds.Satisfied,
            "nexus",
            "approval-1",
            "The reported approval must be revalidated externally.",
            grantsAuthority: false);

    private static AgentWorkFreshnessV1 Freshness(DateTimeOffset observedAt) =>
        new(
            AgentWorkFreshnessStateIds.Current,
            observedAt,
            observedAt.AddHours(2),
            "Exact source revisions were observed by the producer.");

    private static AgentWorkSourceRevisionV1 SourceRevision() =>
        new("source-1", "revision-1", SourceHash);

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
