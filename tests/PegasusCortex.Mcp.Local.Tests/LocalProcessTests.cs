using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.LocalTransport.Configuration;
using Pegasus.Cortex.LocalTransport.Pipes;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

internal static class LocalProcessTests
{
    public static async Task DirectStdioRoundTripAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var first = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var second = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            revision: 2,
            previousHash: first.SnapshotHash);
        var conflicting = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            revision: 1,
            idempotencyKey: first.IdempotencyKey,
            stateSuffix: "-conflict");
        var stale = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            revision: 4,
            previousHash: new string('f', 64),
            idempotencyKey: "task-idempotency-stale",
            stateSuffix: "-stale");
        var work = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var readOnlyWork = context.WorkPacket(
            projectId: LocalTestContext.ReadOnlyProjectId,
            projectLinkId: LocalTestContext.ReadOnlyProjectLinkId,
            workId: "read-only-work",
            idempotencyKey: "read-only-work-idempotency");
        var writeOnlyWork = context.WorkPacket(
            projectId: LocalTestContext.WriteOnlyProjectId,
            projectLinkId: LocalTestContext.WriteOnlyProjectLinkId,
            workId: "write-only-work",
            idempotencyKey: "write-only-work-idempotency");
        var unregisteredWork = context.WorkPacket(
            projectId: "project-unregistered",
            projectLinkId: $"plink_{new string('d', 64)}",
            workId: "unregistered-work",
            idempotencyKey: "unregistered-work-idempotency");

        var validSubmit = LocalMcpProtocol.ToolCall(
            17,
            "cortex_submit_task_snapshot",
            LocalMcpProtocol.SubmitArguments(
                LocalTestContext.PrimaryProjectLinkId,
                LocalTestContext.PrimaryNamespace,
                LocalMcpProtocol.AssertionNode(first)));
        var outerNeedle =
            $"\"projectLinkId\":\"{LocalTestContext.PrimaryProjectLinkId}\",\"projectNamespace\"";
        var duplicateOuter = validSubmit.Replace(
            outerNeedle,
            $"\"projectLinkId\":\"{LocalTestContext.PrimaryProjectLinkId}\"," +
            outerNeedle,
            StringComparison.Ordinal);
        LocalAssert.False(
            string.Equals(validSubmit, duplicateOuter, StringComparison.Ordinal),
            "The duplicate-outer-key test request was not constructed.");

        var nestedAssertion = Encoding.UTF8.GetString(
            Pegasus.Cortex.Contracts.Serialization.CortexCanonicalJson.Serialize(first));
        var nestedNeedle = "\"producer\":{";
        var duplicateNestedAssertion = nestedAssertion.Replace(
            nestedNeedle,
            "\"producer\":{\"authorityId\":\"duplicate\",",
            StringComparison.Ordinal);
        LocalAssert.False(
            string.Equals(
                nestedAssertion,
                duplicateNestedAssertion,
                StringComparison.Ordinal),
            "The duplicate-nested-key test request was not constructed.");
        var duplicateNested = RawSubmit(
            18,
            LocalTestContext.PrimaryProjectLinkId,
            LocalTestContext.PrimaryNamespace,
            duplicateNestedAssertion);

        var result = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                LocalMcpProtocol.ToolsList(2),
                SubmitTask(3, first),
                SubmitTask(4, second),
                SubmitTask(5, first),
                LocalMcpProtocol.ToolCall(
                    6,
                    "cortex_project_brief",
                    LocalMcpProtocol.ProjectBriefArguments(
                        LocalTestContext.PrimaryProjectLinkId,
                        CortexProjectBriefService.ComputeAuthoritySetHash(
                            second.SourceRevisions))),
                ReadTask(7, first),
                SubmitTask(8, conflicting),
                SubmitTask(9, stale),
                SubmitWork(10, work, LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryNamespace),
                ReadWork(11, work, LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryProjectId,
                    LocalTestContext.PrimaryNamespace),
                SubmitWork(12, unregisteredWork, unregisteredWork.ProjectLinkId!,
                    LocalTestContext.PrimaryNamespace),
                SubmitWork(13, readOnlyWork, LocalTestContext.ReadOnlyProjectLinkId,
                    LocalTestContext.ReadOnlyNamespace),
                SubmitWork(14, writeOnlyWork, LocalTestContext.WriteOnlyProjectLinkId,
                    LocalTestContext.WriteOnlyNamespace),
                ReadWork(15, writeOnlyWork, LocalTestContext.WriteOnlyProjectLinkId,
                    LocalTestContext.WriteOnlyProjectId,
                    LocalTestContext.WriteOnlyNamespace),
                duplicateOuter,
                duplicateNested
            ]).ConfigureAwait(false);

        LocalAssert.Equal(
            0,
            result.ExitCode,
            $"Direct stdio failed closed unexpectedly: {result.StandardError}");
        var responses = result.Responses;
        var listed = LocalMcpProtocol.ResponseById(responses, 2)["result"]!
            .AsObject()["tools"]!.AsArray()
            .Select(item => item!.AsObject()["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        LocalAssert.True(
            listed.Contains("cortex_submit_task_snapshot") &&
            listed.Contains("cortex_submit_work_packet") &&
            listed.Contains("cortex_read_retained_record") &&
            listed.Contains("cortex_project_brief"),
            "The read/write local composition did not advertise all bounded local tools.");

        var firstWrite = Structured(responses, 3, expectedError: false);
        LocalAssert.Equal(
            CortexRetentionWriteDispositions.Appended,
            firstWrite["disposition"]!.GetValue<string>(),
            "The first task snapshot did not append.");
        LocalAssert.Equal(
            CortexRetentionWriteDispositions.Appended,
            firstWrite["projectBriefDisposition"]!.GetValue<string>(),
            "The first task snapshot did not materialize its project brief.");

        var secondWrite = Structured(responses, 4, expectedError: false);
        var secondContentHash = LocalTestContext.ContentHash(second);
        LocalAssert.Equal(
            secondContentHash,
            secondWrite["actualHeadHash"]!.GetValue<string>(),
            "The successor write did not report its exact retained head.");

        var replay = Structured(responses, 5, expectedError: false);
        LocalAssert.Equal(
            CortexRetentionWriteDispositions.Idempotent,
            replay["disposition"]!.GetValue<string>(),
            "The old exact task revision was not accepted as idempotent.");
        LocalAssert.Equal(
            LocalTestContext.ContentHash(first),
            replay["retainedContentHash"]!.GetValue<string>(),
            "The replay did not preserve the old immutable revision hash.");
        LocalAssert.Equal(
            secondContentHash,
            replay["actualHeadHash"]!.GetValue<string>(),
            "The replay did not preserve the newer actual head hash.");
        LocalAssert.Equal(
            "already-advanced",
            replay["projectBriefDisposition"]!.GetValue<string>(),
            "The old replay did not recognize the already advanced deterministic brief.");

        var brief = Structured(responses, 6, expectedError: false);
        LocalAssert.Equal(
            second.SnapshotHash,
            brief["sourceHash"]!.GetValue<string>(),
            "The project brief was not deterministically projected from revision two.");
        LocalAssert.Equal(
            2L,
            brief["stateRevision"]!.GetValue<long>(),
            "The project brief state revision did not advance with the task snapshot.");

        var exactOld = Structured(responses, 7, expectedError: false);
        LocalAssert.Equal(
            LocalTestContext.ContentHash(first),
            exactOld["contentHash"]!.GetValue<string>(),
            "The exact old revision read returned different retained bytes.");
        LocalAssert.Equal(
            secondContentHash,
            exactOld["actualHeadHash"]!.GetValue<string>(),
            "The exact old revision read hid the current newer head.");
        LocalAssert.False(
            exactOld["authoritative"]!.GetValue<bool>(),
            "A retained assertion was incorrectly marked authoritative.");

        var conflict = Structured(responses, 8, expectedError: true);
        LocalAssert.False(
            conflict["accepted"]!.GetValue<bool>(),
            "A conflicting idempotency replay was accepted.");
        LocalAssert.Equal(
            secondContentHash,
            conflict["actualHeadHash"]!.GetValue<string>(),
            "A conflict did not report the actual advanced head.");
        LocalAssert.Equal(
            "cortex_retained_assertion_conflict",
            LocalMcpProtocol.DiagnosticCode(conflict),
            "A conflict did not use the bounded retention diagnostic.");

        var staleResult = Structured(responses, 9, expectedError: true);
        LocalAssert.False(
            staleResult["accepted"]!.GetValue<bool>(),
            "A stale-base task assertion was accepted.");
        LocalAssert.Equal(
            secondContentHash,
            staleResult["actualHeadHash"]!.GetValue<string>(),
            "A stale base did not report the actual retained head.");

        var workWrite = Structured(responses, 10, expectedError: false);
        LocalAssert.True(
            workWrite["projectBriefDisposition"] is null,
            "A work packet unexpectedly materialized a project brief.");
        var workRead = Structured(responses, 11, expectedError: false);
        LocalAssert.Equal(
            work.PacketHash,
            workRead["assertionHash"]!.GetValue<string>(),
            "The exact work-packet read returned a different typed assertion.");

        LocalAssert.Equal(
            "cortex_local_project_write_not_allowed",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 12, expectedError: true)),
            "An unregistered project write did not fail closed.");
        LocalAssert.Equal(
            "cortex_local_project_write_not_allowed",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 13, expectedError: true)),
            "A read-only project accepted a retained write.");
        _ = Structured(responses, 14, expectedError: false);
        LocalAssert.Equal(
            "cortex_retained_record_not_available",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 15, expectedError: true)),
            "A write-only project allowed exact retained reads.");
        LocalAssert.Equal(
            "cortex_retained_assertion_arguments_invalid",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 17, expectedError: true)),
            "Duplicate outer argument keys were not rejected.");
        LocalAssert.Equal(
            "cortex_retained_assertion_arguments_invalid",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 18, expectedError: true)),
            "Duplicate nested assertion keys were not rejected recursively.");
    }

    public static async Task ReceiptLinkageAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var work = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            workId: "receipt-work",
            idempotencyKey: "receipt-work-idempotency");
        var handoff = context.HandoffReceipt(work, "handoff-receipt");
        var acceptance = context.AcceptanceReceipt(
            handoff,
            "acceptance-receipt");

        // This packet has the same retained identity but different exact bytes.
        // A receipt sealed against it must not link to the retained packet.
        var mismatchedWork = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            workId: work.WorkId,
            idempotencyKey: work.IdempotencyKey,
            constraints:
            [
                "These deliberately different bytes are not the retained packet."
            ]);
        var mismatchedHandoff = context.HandoffReceipt(
            mismatchedWork,
            "handoff-mismatch");
        var shadowHandoff = context.HandoffReceipt(
            mismatchedWork,
            handoff.ReceiptId);
        var mismatchedAcceptance = context.AcceptanceReceipt(
            shadowHandoff,
            "acceptance-mismatch");

        var absentWork = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            workId: "absent-work",
            idempotencyKey: "absent-work-idempotency");
        var missingPacketHandoff = context.HandoffReceipt(
            absentWork,
            "handoff-missing-packet");
        var missingHandoff = context.HandoffReceipt(
            work,
            "handoff-not-retained");
        var missingHandoffAcceptance = context.AcceptanceReceipt(
            missingHandoff,
            "acceptance-missing-handoff");

        var authorityReceipt = context.HandoffReceipt(
            work,
            "handoff-authority-tamper");
        var authorityNode =
            LocalMcpProtocol.AssertionNode(authorityReceipt).AsObject();
        authorityNode["grantsAuthority"] = true;

        var result = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                LocalMcpProtocol.ToolsList(2),
                SubmitWork(
                    3,
                    work,
                    LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryNamespace),
                SubmitReceipt(
                    4,
                    "cortex_submit_work_packet_handoff_receipt",
                    handoff),
                ReadReceipt(
                    5,
                    CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                    handoff.ReceiptId,
                    handoff.ReceiptHash,
                    LocalTestContext.ContentHash(handoff)),
                SubmitReceipt(
                    6,
                    "cortex_submit_work_packet_acceptance_receipt",
                    acceptance),
                ReadReceipt(
                    7,
                    CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
                    acceptance.ReceiptId,
                    acceptance.ReceiptHash,
                    LocalTestContext.ContentHash(acceptance)),
                SubmitReceipt(
                    8,
                    "cortex_submit_work_packet_handoff_receipt",
                    handoff),
                SubmitReceipt(
                    9,
                    "cortex_submit_work_packet_acceptance_receipt",
                    acceptance),
                SubmitReceipt(
                    10,
                    "cortex_submit_work_packet_handoff_receipt",
                    missingPacketHandoff),
                ReadReceipt(
                    11,
                    CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                    missingPacketHandoff.ReceiptId,
                    missingPacketHandoff.ReceiptHash,
                    LocalTestContext.ContentHash(missingPacketHandoff)),
                SubmitReceipt(
                    12,
                    "cortex_submit_work_packet_handoff_receipt",
                    mismatchedHandoff),
                ReadReceipt(
                    13,
                    CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                    mismatchedHandoff.ReceiptId,
                    mismatchedHandoff.ReceiptHash,
                    LocalTestContext.ContentHash(mismatchedHandoff)),
                SubmitReceipt(
                    14,
                    "cortex_submit_work_packet_acceptance_receipt",
                    missingHandoffAcceptance),
                ReadReceipt(
                    15,
                    CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
                    missingHandoffAcceptance.ReceiptId,
                    missingHandoffAcceptance.ReceiptHash,
                    LocalTestContext.ContentHash(missingHandoffAcceptance)),
                SubmitReceipt(
                    16,
                    "cortex_submit_work_packet_acceptance_receipt",
                    mismatchedAcceptance),
                ReadReceipt(
                    17,
                    CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
                    mismatchedAcceptance.ReceiptId,
                    mismatchedAcceptance.ReceiptHash,
                    LocalTestContext.ContentHash(mismatchedAcceptance)),
                LocalMcpProtocol.ToolCall(
                    18,
                    "cortex_submit_work_packet_handoff_receipt",
                    LocalMcpProtocol.SubmitReceiptArguments(
                        LocalTestContext.PrimaryProjectLinkId,
                        LocalTestContext.PrimaryProjectId,
                        LocalTestContext.PrimaryNamespace,
                        authorityNode)),
                ReadReceipt(
                    19,
                    CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                    authorityReceipt.ReceiptId,
                    authorityReceipt.ReceiptHash,
                    LocalTestContext.ContentHash(authorityReceipt)),
                LocalMcpProtocol.ToolCall(
                    20,
                    "cortex_submit_work_packet_handoff_receipt",
                    LocalMcpProtocol.SubmitReceiptArguments(
                        LocalTestContext.WriteOnlyProjectLinkId,
                        LocalTestContext.WriteOnlyProjectId,
                        LocalTestContext.WriteOnlyNamespace,
                        LocalMcpProtocol.AssertionNode(handoff))),
                LocalMcpProtocol.ToolCall(
                    21,
                    "cortex_read_retained_record",
                    LocalMcpProtocol.ReadArguments(
                        LocalTestContext.ReadOnlyProjectLinkId,
                        LocalTestContext.ReadOnlyProjectId,
                        LocalTestContext.ReadOnlyNamespace,
                        CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                        handoff.ReceiptId,
                        revision: 1,
                        LocalTestContext.ContentHash(handoff)))
            ]).ConfigureAwait(false);

        LocalAssert.Equal(
            0,
            result.ExitCode,
            $"Receipt linkage escaped as a process failure: {result.StandardError}");
        var responses = result.Responses;
        var listed = LocalMcpProtocol.ResponseById(responses, 2)["result"]!
            .AsObject()["tools"]!.AsArray()
            .Select(item => item!.AsObject()["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        LocalAssert.True(
            listed.Contains("cortex_submit_work_packet_handoff_receipt") &&
            listed.Contains("cortex_submit_work_packet_acceptance_receipt") &&
            listed.Contains("cortex_read_retained_record"),
            "The local composition did not advertise its narrow receipt tools.");

        _ = Structured(responses, 3, expectedError: false);
        var handoffWrite = Structured(responses, 4, expectedError: false);
        AssertReceiptWrite(
            handoffWrite,
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
            handoff.ReceiptId,
            handoff.ReceiptHash,
            CortexRetentionWriteDispositions.Appended);
        var handoffRead = Structured(responses, 5, expectedError: false);
        AssertPassiveReceiptRead(
            handoffRead,
            handoff.ReceiptHash,
            AgentWorkPacketContractIds.HandoffReceipt);

        var acceptanceWrite = Structured(responses, 6, expectedError: false);
        AssertReceiptWrite(
            acceptanceWrite,
            CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
            acceptance.ReceiptId,
            acceptance.ReceiptHash,
            CortexRetentionWriteDispositions.Appended);
        var acceptanceRead = Structured(responses, 7, expectedError: false);
        AssertPassiveReceiptRead(
            acceptanceRead,
            acceptance.ReceiptHash,
            AgentWorkPacketContractIds.AcceptanceReceipt);

        AssertReceiptWrite(
            Structured(responses, 8, expectedError: false),
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
            handoff.ReceiptId,
            handoff.ReceiptHash,
            CortexRetentionWriteDispositions.Idempotent);
        AssertReceiptWrite(
            Structured(responses, 9, expectedError: false),
            CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
            acceptance.ReceiptId,
            acceptance.ReceiptHash,
            CortexRetentionWriteDispositions.Idempotent);

        foreach (var id in new[] { 10, 12, 14, 16 })
        {
            LocalAssert.Equal(
                "cortex_retained_assertion_revision_invalid",
                LocalMcpProtocol.DiagnosticCode(
                    Structured(responses, id, expectedError: true)),
                $"Receipt linkage failure {id} did not use the fixed diagnostic.");
        }
        foreach (var id in new[] { 11, 13, 15, 17, 19 })
        {
            LocalAssert.Equal(
                "cortex_retained_record_not_available",
                LocalMcpProtocol.DiagnosticCode(
                    Structured(responses, id, expectedError: true)),
                $"Rejected receipt {id} was unexpectedly retained.");
        }
        LocalAssert.Equal(
            "cortex_retained_assertion_contract_invalid",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 18, expectedError: true)),
            "An authority-granting receipt did not fail at the typed contract boundary.");
        LocalAssert.Equal(
            "cortex_retained_assertion_revision_invalid",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 20, expectedError: true)),
            "A receipt write crossed its exact registered project scope.");
        LocalAssert.Equal(
            "cortex_retained_record_not_available",
            LocalMcpProtocol.DiagnosticCode(
                Structured(responses, 21, expectedError: true)),
            "A receipt read crossed its exact registered project scope.");
    }

    public static async Task ColdBriefRecoveryAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var work = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            workId: "cold-work",
            idempotencyKey: "cold-work-idempotency");
        var handoff = context.HandoffReceipt(work, "cold-handoff");
        var acceptance = context.AcceptanceReceipt(
            handoff,
            "cold-acceptance");
        AgentTaskEvidenceReferenceV1[] evidence =
        [
            EvidenceReference(
                "evidence-work",
                CortexRetainedDocumentKinds.WorkPacket,
                work.WorkId,
                work.PacketRevision,
                LocalTestContext.ContentHash(work),
                work.PacketHash),
            EvidenceReference(
                "evidence-handoff",
                CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                handoff.ReceiptId,
                revision: 1,
                LocalTestContext.ContentHash(handoff),
                handoff.ReceiptHash),
            EvidenceReference(
                "evidence-acceptance",
                CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
                acceptance.ReceiptId,
                revision: 1,
                LocalTestContext.ContentHash(acceptance),
                acceptance.ReceiptHash)
        ];
        var gate = new AgentWorkApprovalGateV1(
            AgentWorkApprovalGateStateIds.Satisfied,
            "external-approval-authority",
            "approval-proof-42",
            "The producer reports an external approval that must be revalidated.",
            grantsAuthority: false);
        var snapshot = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            taskSetId: "cold-task-set",
            idempotencyKey: "cold-task-idempotency",
            evidenceReferences: evidence,
            approvalGate: gate);

        var seed = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitWork(
                    2,
                    work,
                    LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryNamespace),
                SubmitReceipt(
                    3,
                    "cortex_submit_work_packet_handoff_receipt",
                    handoff),
                SubmitReceipt(
                    4,
                    "cortex_submit_work_packet_acceptance_receipt",
                    acceptance),
                SubmitTask(5, snapshot)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            seed.ExitCode,
            $"The cold-recovery seed failed: {seed.StandardError}");
        _ = Structured(seed.Responses, 5, expectedError: false);

        // This fresh process receives no writer result or in-memory locator.
        // It must recover exact-read arguments solely from the brief.
        using var cold = LocalProcessHarness.Start(
        [
            "stdio",
            "--profile",
            context.ProfileId,
            "--state-root",
            Path.GetDirectoryName(context.DatabasePath)!
        ]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var standardError = cold.StandardError.ReadToEndAsync();
        _ = await ColdCallAsync(
            cold,
            LocalMcpProtocol.Initialize(),
            deadline.Token).ConfigureAwait(false);
        await cold.StandardInput.WriteLineAsync(
            LocalMcpProtocol.Initialized()).ConfigureAwait(false);
        await cold.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);

        var briefEnvelope = await ColdCallAsync(
            cold,
            LocalMcpProtocol.ToolCall(
                2,
                "cortex_project_brief",
                LocalMcpProtocol.ProjectBriefArguments(
                    LocalTestContext.PrimaryProjectLinkId,
                    CortexProjectBriefService.ComputeAuthoritySetHash(
                        snapshot.SourceRevisions))),
            deadline.Token).ConfigureAwait(false);
        var brief = LocalMcpProtocol.Structured(briefEnvelope);
        LocalAssert.False(
            LocalMcpProtocol.ToolEnvelope(briefEnvelope)["isError"]!
                .GetValue<bool>(),
            "A fresh process could not read the retained project brief.");

        var checkpoints = brief["checkpointReferences"]!.AsArray();
        var taskReference = FindRetainedReference(
            checkpoints,
            CortexRetainedDocumentKinds.TaskSnapshot);
        var taskLocator = taskReference["retainedRecord"]!.AsObject();
        LocalAssert.Equal(
            snapshot.TaskSetId,
            taskLocator["documentId"]!.GetValue<string>(),
            "The cold brief did not identify the exact task-set document.");
        LocalAssert.Equal(
            LocalTestContext.PrimaryProjectLinkId,
            taskLocator["projectLinkId"]!.GetValue<string>(),
            "The cold locator did not bind the original project-link request.");
        LocalAssert.Equal(
            LocalTestContext.PrimaryNamespace,
            taskLocator["projectNamespace"]!.GetValue<string>(),
            "The cold locator omitted the namespace required by exact read.");
        LocalAssert.Equal(
            LocalTestContext.ContentHash(snapshot),
            taskLocator["contentHash"]!.GetValue<string>(),
            "The cold brief used the typed snapshot hash instead of the repository content hash.");
        LocalAssert.False(
            string.Equals(
                snapshot.SnapshotHash,
                taskLocator["contentHash"]!.GetValue<string>(),
                StringComparison.Ordinal),
            "The task fixture did not distinguish typed and repository hashes.");

        var evidenceReferences = brief["evidenceReferences"]!.AsArray();
        foreach (var kind in new[]
                 {
                     CortexRetainedDocumentKinds.WorkPacket,
                     CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                     CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt
                 })
        {
            _ = FindRetainedReference(evidenceReferences, kind);
        }

        var projectedGate = brief["currentTask"]!.AsObject()["gate"]!.AsObject();
        LocalAssert.Equal(
            AgentWorkApprovalGateStateIds.Satisfied,
            projectedGate["state"]!.GetValue<string>(),
            "The brief lost the reported gate state.");
        LocalAssert.Equal(
            gate.AuthorityId!,
            projectedGate["authorityId"]!.GetValue<string>(),
            "The brief lost the external gate authority identity.");
        LocalAssert.Equal(
            gate.ApprovalReferenceId!,
            projectedGate["approvalReferenceId"]!.GetValue<string>(),
            "The brief lost the external approval reference.");
        LocalAssert.Equal(
            gate.Summary,
            projectedGate["summary"]!.GetValue<string>(),
            "The brief lost the bounded gate summary.");
        LocalAssert.False(
            projectedGate["grantsAuthority"]!.GetValue<bool>(),
            "The projected gate incorrectly granted authority.");

        var exactTaskEnvelope = await ColdCallAsync(
            cold,
            ReadLocator(3, taskLocator),
            deadline.Token).ConfigureAwait(false);
        var exactTask = LocalMcpProtocol.Structured(exactTaskEnvelope);
        LocalAssert.False(
            LocalMcpProtocol.ToolEnvelope(exactTaskEnvelope)["isError"]!
                .GetValue<bool>(),
            "The cold brief locator could not drive one exact task read.");
        LocalAssert.Equal(
            snapshot.SnapshotHash,
            exactTask["assertionHash"]!.GetValue<string>(),
            "The cold exact read returned different task bytes.");

        var nextId = 4;
        foreach (var reference in evidenceReferences)
        {
            var locator = reference!.AsObject()["retainedRecord"]!.AsObject();
            var exactEnvelope = await ColdCallAsync(
                cold,
                ReadLocator(nextId++, locator),
                deadline.Token).ConfigureAwait(false);
            LocalAssert.False(
                LocalMcpProtocol.ToolEnvelope(exactEnvelope)["isError"]!
                    .GetValue<bool>(),
                "A snapshot-bound continuation locator could not drive an exact read.");
        }

        var contextSetId = brief["contextSetId"]!.GetValue<string>();
        var knownArguments = LocalMcpProtocol.ProjectBriefArguments(
            LocalTestContext.PrimaryProjectLinkId,
            brief["authoritySetHash"]!.GetValue<string>());
        knownArguments["knownContextSetId"] = contextSetId;
        var unchangedEnvelope = await ColdCallAsync(
            cold,
            LocalMcpProtocol.ToolCall(
                nextId++,
                "cortex_project_brief",
                knownArguments),
            deadline.Token).ConfigureAwait(false);
        var unchanged = LocalMcpProtocol.Structured(unchangedEnvelope);
        LocalAssert.Equal(
            "unchanged",
            unchanged["disposition"]!.GetValue<string>(),
            "An exact known context did not remain unchanged.");
        LocalAssert.Equal(
            checkpoints.ToJsonString(),
            unchanged["checkpointReferences"]!.ToJsonString(),
            "An unchanged context returned different task locators.");
        LocalAssert.Equal(
            evidenceReferences.ToJsonString(),
            unchanged["evidenceReferences"]!.ToJsonString(),
            "An unchanged context returned different evidence locators.");

        var missingInitialAuthorityArguments = LocalMcpProtocol.ProjectBriefArguments(
            LocalTestContext.PrimaryProjectLinkId,
            brief["authoritySetHash"]!.GetValue<string>());
        missingInitialAuthorityArguments.Remove("knownAuthoritySetHash");
        var missingInitialAuthorityEnvelope = await ColdCallAsync(
            cold,
            LocalMcpProtocol.ToolCall(
                nextId++,
                "cortex_project_brief",
                missingInitialAuthorityArguments),
            deadline.Token).ConfigureAwait(false);
        AssertAuthoritySetMismatchFallback(
            missingInitialAuthorityEnvelope,
            "missing initial authority-set hash");

        var missingAuthorityArguments = LocalMcpProtocol.ProjectBriefArguments(
            LocalTestContext.PrimaryProjectLinkId,
            brief["authoritySetHash"]!.GetValue<string>());
        missingAuthorityArguments["knownContextSetId"] = contextSetId;
        missingAuthorityArguments.Remove("knownAuthoritySetHash");
        var missingAuthorityEnvelope = await ColdCallAsync(
            cold,
            LocalMcpProtocol.ToolCall(
                nextId++,
                "cortex_project_brief",
                missingAuthorityArguments),
            deadline.Token).ConfigureAwait(false);
        AssertAuthoritySetMismatchFallback(
            missingAuthorityEnvelope,
            "missing authority-set hash");

        var mismatchedAuthorityArguments = LocalMcpProtocol.ProjectBriefArguments(
            LocalTestContext.PrimaryProjectLinkId,
            new string('9', 64));
        mismatchedAuthorityArguments["knownContextSetId"] = contextSetId;
        var mismatchedAuthorityEnvelope = await ColdCallAsync(
            cold,
            LocalMcpProtocol.ToolCall(
                nextId,
                "cortex_project_brief",
                mismatchedAuthorityArguments),
            deadline.Token).ConfigureAwait(false);
        AssertAuthoritySetMismatchFallback(
            mismatchedAuthorityEnvelope,
            "mismatched authority-set hash");

        cold.StandardInput.Close();
        await cold.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            cold.ExitCode,
            $"The cold reader process failed: {await standardError.ConfigureAwait(false)}");
        await AssertExactReadBriefScopeBoundaryAsync(context).ConfigureAwait(false);
    }

    private static void AssertAuthoritySetMismatchFallback(
        JsonObject envelope,
        string caseName)
    {
        LocalAssert.True(
            LocalMcpProtocol.ToolEnvelope(envelope)["isError"]!.GetValue<bool>(),
            $"The {caseName} was presented as an accepted brief.");
        var brief = LocalMcpProtocol.Structured(envelope);
        LocalAssert.False(
            brief["accepted"]!.GetValue<bool>(),
            $"The {caseName} remained accepted.");
        LocalAssert.Equal(
            "authority_mismatch",
            brief["fallbackReason"]!.GetValue<string>(),
            $"The {caseName} returned the wrong fallback reason.");
        LocalAssert.True(
            brief["fallbackRequired"]!.GetValue<bool>() &&
            !brief["complete"]!.GetValue<bool>(),
            $"The {caseName} did not require the full file fallback.");
        LocalAssert.True(
            brief["projectLinkId"] is null &&
            brief["projectId"] is null &&
            brief["projectNamespace"] is null &&
            brief["contextSetId"] is null &&
            brief["baseContextSetId"] is null &&
            brief["stateRevision"] is null &&
            brief["sourceHash"] is null &&
            brief["authoritySetHash"] is null &&
            brief["selectionVersion"] is null &&
            brief["freshness"] is null &&
            brief["currentTask"] is null,
            $"The {caseName} exposed current task claims.");
        LocalAssert.True(
            !brief["hasMore"]!.GetValue<bool>() &&
            brief["hotTasks"]!.AsArray().Count == 0 &&
            brief["removedTaskIds"]!.AsArray().Count == 0 &&
            brief["checkpointReferences"]!.AsArray().Count == 0 &&
            brief["evidenceReferences"]!.AsArray().Count == 0,
            $"The {caseName} exposed task or source collections.");
        LocalAssert.Equal(
            "cortex_project_brief_authority_mismatch",
            brief["diagnostic"]!.AsObject()["code"]!.GetValue<string>(),
            $"The {caseName} returned the wrong diagnostic.");
    }

    private static async Task AssertExactReadBriefScopeBoundaryAsync(
        LocalTestContext context)
    {
        await using var repository = new SqliteCortexRetentionStore(
            new SqliteCortexStoreOptions(context.DatabasePath));
        var retained = await repository.FindHeadAsync(
            LocalTestContext.PrimaryProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            LocalTestContext.PrimaryNamespace,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.True(
            retained is not null,
            "The cold-recovery fixture retained no exact project brief.");

        var validator = typeof(CortexMcpLocalRuntime).GetMethod(
            "TryValidateRetainedDocument",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(CortexMcpLocalRuntime).FullName,
                "TryValidateRetainedDocument");
        bool Invoke(string tenantId, string projectId)
        {
            object?[] arguments =
            [
                retained!,
                tenantId,
                LocalTestContext.PrimaryProjectLinkId,
                projectId,
                LocalTestContext.PrimaryNamespace,
                null
            ];
            return (bool)(validator.Invoke(null, arguments)
                ?? throw new InvalidOperationException(
                    "The retained-document validator returned no result."));
        }

        LocalAssert.True(
            Invoke(LocalTestContext.TenantId, LocalTestContext.PrimaryProjectId),
            "The exact project brief failed its authorized scope boundary.");
        LocalAssert.False(
            Invoke(LocalTestContext.TenantId, "different-project"),
            "The exact-read boundary accepted a project-misbound brief.");
        LocalAssert.False(
            Invoke("different-tenant", LocalTestContext.PrimaryProjectId),
            "The exact-read boundary accepted a tenant-misbound brief.");
    }

    public static async Task EvidenceProjectionBoundaryAsync()
    {
        await using (var missingContext = await LocalTestContext.CreateAsync(
                         includeSplitAllowlists: false))
        {
            var missingReference = EvidenceReference(
                "missing-evidence",
                CortexRetainedDocumentKinds.WorkPacket,
                "missing-work",
                revision: 1,
                new string('a', 64),
                new string('b', 64));
            var missingSnapshot = missingContext.TaskSnapshot(
                projectLinkId: LocalTestContext.PrimaryProjectLinkId,
                taskSetId: "missing-evidence-tasks",
                idempotencyKey: "missing-evidence-task-idempotency",
                evidenceReferences: [missingReference]);
            await AssertEvidenceProjectionFailureAsync(
                missingContext,
                missingSnapshot,
                seedRequests: [],
                "missing",
                expectedIssueCode:
                    "cortex_project_brief_materialization_incomplete",
                expectedRetryable: true).ConfigureAwait(false);
        }

        await using (var mismatchContext = await LocalTestContext.CreateAsync(
                         includeSplitAllowlists: false))
        {
            var work = mismatchContext.WorkPacket(
                projectLinkId: LocalTestContext.PrimaryProjectLinkId,
                workId: "hash-mismatch-work",
                idempotencyKey: "hash-mismatch-work-idempotency");
            var mismatchedHash = string.Equals(
                    LocalTestContext.ContentHash(work),
                    new string('c', 64),
                    StringComparison.Ordinal)
                ? new string('d', 64)
                : new string('c', 64);
            var mismatchReference = EvidenceReference(
                "hash-mismatch-evidence",
                CortexRetainedDocumentKinds.WorkPacket,
                work.WorkId,
                work.PacketRevision,
                mismatchedHash,
                work.PacketHash);
            var mismatchSnapshot = mismatchContext.TaskSnapshot(
                projectLinkId: LocalTestContext.PrimaryProjectLinkId,
                taskSetId: "hash-mismatch-tasks",
                idempotencyKey: "hash-mismatch-task-idempotency",
                evidenceReferences: [mismatchReference]);
            await AssertEvidenceProjectionFailureAsync(
                mismatchContext,
                mismatchSnapshot,
                seedRequests:
                [
                    SubmitWork(
                        2,
                        work,
                        LocalTestContext.PrimaryProjectLinkId,
                        LocalTestContext.PrimaryNamespace)
                ],
                "hash-mismatch",
                expectedIssueCode:
                    "cortex_project_brief_projection_unrepresentable",
                expectedRetryable: false).ConfigureAwait(false);
        }

        await using (var crossProjectContext =
                     await LocalTestContext.CreateAsync())
        {
            var primaryWork = crossProjectContext.WorkPacket(
                projectLinkId: LocalTestContext.PrimaryProjectLinkId,
                workId: "cross-project-work",
                idempotencyKey: "cross-project-work-idempotency");
            var crossReference = EvidenceReference(
                "cross-project-evidence",
                CortexRetainedDocumentKinds.WorkPacket,
                primaryWork.WorkId,
                primaryWork.PacketRevision,
                LocalTestContext.ContentHash(primaryWork),
                primaryWork.PacketHash);
            var crossSnapshot = crossProjectContext.TaskSnapshot(
                projectId: LocalTestContext.WriteOnlyProjectId,
                projectLinkId: LocalTestContext.WriteOnlyProjectLinkId,
                taskSetId: "cross-project-tasks",
                idempotencyKey: "cross-project-task-idempotency",
                evidenceReferences: [crossReference]);
            var crossSubmit = LocalMcpProtocol.ToolCall(
                3,
                "cortex_submit_task_snapshot",
                LocalMcpProtocol.SubmitArguments(
                    LocalTestContext.WriteOnlyProjectLinkId,
                    LocalTestContext.WriteOnlyNamespace,
                    LocalMcpProtocol.AssertionNode(crossSnapshot)));
            var crossResult = await LocalProcessHarness.RunAsync(
                [
                    "stdio",
                    "--profile",
                    crossProjectContext.ProfileId,
                    "--state-root",
                    Path.GetDirectoryName(crossProjectContext.DatabasePath)!
                ],
                [
                    LocalMcpProtocol.Initialize(),
                    LocalMcpProtocol.Initialized(),
                    SubmitWork(
                        2,
                        primaryWork,
                        LocalTestContext.PrimaryProjectLinkId,
                        LocalTestContext.PrimaryNamespace),
                    crossSubmit
                ]).ConfigureAwait(false);
            LocalAssert.Equal(
                0,
                crossResult.ExitCode,
                $"The cross-project evidence fixture failed: {crossResult.StandardError}");
            AssertIncompleteEvidenceProjection(
                Structured(crossResult.Responses, 3, expectedError: false),
                "cross-project",
                "cortex_project_brief_materialization_incomplete",
                expectedRetryable: true);
        }

        await using (var omittedContext = await LocalTestContext.CreateAsync(
                         includeSplitAllowlists: false))
        {
            var omittedReference = EvidenceReference(
                "omitted-task-evidence",
                CortexRetainedDocumentKinds.WorkPacket,
                "omitted-missing-work",
                revision: 1,
                new string('e', 64),
                new string('f', 64));
            var source = omittedContext.TaskSnapshot(
                projectLinkId: LocalTestContext.PrimaryProjectLinkId,
                taskSetId: "omitted-evidence-tasks",
                idempotencyKey: "omitted-evidence-task-idempotency",
                evidenceReferences: [omittedReference]);
            var current = TaskVariant(
                source.Tasks[0],
                taskId: "current-without-evidence",
                ordinal: 0,
                evidenceIds: []);
            var omitted = TaskVariant(
                source.Tasks[0],
                taskId: "terminal-with-evidence",
                ordinal: 1,
                reportedState: AgentTaskStateIds.Done,
                evidenceIds: [omittedReference.EvidenceId]);
            var snapshot = LocalTestContext.WithTasks(
                source,
                [current, omitted]);
            var result = await LocalProcessHarness.RunAsync(
                [
                    "stdio",
                    "--profile",
                    omittedContext.ProfileId,
                    "--state-root",
                    Path.GetDirectoryName(omittedContext.DatabasePath)!
                ],
                [
                    LocalMcpProtocol.Initialize(),
                    LocalMcpProtocol.Initialized(),
                    SubmitTask(2, snapshot),
                    LocalMcpProtocol.ToolCall(
                        3,
                        "cortex_project_brief",
                        LocalMcpProtocol.ProjectBriefArguments(
                            LocalTestContext.PrimaryProjectLinkId,
                            CortexProjectBriefService.ComputeAuthoritySetHash(
                                snapshot.SourceRevisions)))
                ]).ConfigureAwait(false);
            LocalAssert.Equal(
                0,
                result.ExitCode,
                $"Omitted-task evidence escaped as a process failure: {result.StandardError}");
            var write = Structured(result.Responses, 2, expectedError: false);
            LocalAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                write["projectBriefDisposition"]!.GetValue<string>(),
                "Evidence used only by an omitted terminal task blocked the hot brief.");
            var brief = Structured(result.Responses, 3, expectedError: false);
            LocalAssert.True(
                brief["hasMore"]!.GetValue<bool>(),
                "The omitted terminal task was not disclosed by hasMore.");
            LocalAssert.Equal(
                0,
                brief["evidenceReferences"]!.AsArray().Count,
                "Evidence belonging only to an omitted task entered the hot brief.");
        }
    }

    public static async Task PipeBridgeRestartAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var snapshot = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var work = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var seed = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitTask(2, snapshot),
                SubmitWork(3, work, LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryNamespace)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            seed.ExitCode,
            $"The restart fixture could not retain state: {seed.StandardError}");

        await using var composition = await CortexMcpLocalComposition.CreateAsync(
            context.ProfileId,
            context.DatabasePath,
            context.RegistryPath,
            "service",
            TimeProvider.System,
            CancellationToken.None);
        using var serviceCancellation = new CancellationTokenSource();
        var service = RunServerAsync(
            context.ProfileId,
            composition,
            serviceCancellation.Token);
        var bridge = await LocalProcessHarness.RunAsync(
            ["bridge", "--profile", context.ProfileId],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                LocalMcpProtocol.ToolCall(
                    2,
                    "cortex_project_brief",
                    LocalMcpProtocol.ProjectBriefArguments(
                        LocalTestContext.PrimaryProjectLinkId,
                        CortexProjectBriefService.ComputeAuthoritySetHash(
                            snapshot.SourceRevisions))),
                ReadTask(3, snapshot),
                ReadWork(4, work, LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryProjectId,
                    LocalTestContext.PrimaryNamespace)
            ]).ConfigureAwait(false);
        serviceCancellation.Cancel();
        await ObserveCancellationAsync(service).ConfigureAwait(false);

        LocalAssert.Equal(
            0,
            bridge.ExitCode,
            $"The explicit pipe bridge could not read restarted state: {bridge.StandardError}");
        var brief = Structured(bridge.Responses, 2, expectedError: false);
        LocalAssert.Equal(
            snapshot.SnapshotHash,
            brief["sourceHash"]!.GetValue<string>(),
            "The restarted pipe service did not recover the deterministic project brief.");
        _ = Structured(bridge.Responses, 3, expectedError: false);
        _ = Structured(bridge.Responses, 4, expectedError: false);
    }

    public static async Task ProjectionLimitsDoNotVetoRetentionAsync()
    {
        await AssertProjectionLimitRetainedAsync(
            context =>
            {
                var source = context.TaskSnapshot(
                    projectLinkId: LocalTestContext.PrimaryProjectLinkId);
                return LocalTestContext.WithTasks(source, []);
            },
            "zero-task").ConfigureAwait(false);

        await AssertProjectionLimitRetainedAsync(
            context =>
            {
                var source = context.TaskSnapshot(
                    projectLinkId: LocalTestContext.PrimaryProjectLinkId);
                return LocalTestContext.WithTasks(
                    source,
                    [TaskVariant(
                        source.Tasks[0],
                        objective: new string('o', 1024))]);
            },
            "long-objective").ConfigureAwait(false);

        await AssertProjectionLimitRetainedAsync(
            context =>
            {
                var source = context.TaskSnapshot(
                    projectLinkId: LocalTestContext.PrimaryProjectLinkId);
                return LocalTestContext.WithTasks(
                    source,
                    [TaskVariant(
                        source.Tasks[0],
                        blockers:
                        [
                            new string('b', 512),
                            new string('c', 512),
                            new string('d', 512)
                        ])]);
            },
            "aggregate-blockers").ConfigureAwait(false);

        await AssertProjectionLimitRetainedAsync(
            context =>
            {
                var source = context.TaskSnapshot(
                    projectLinkId: LocalTestContext.PrimaryProjectLinkId);
                var tasks = Enumerable.Range(0, 5)
                    .Select(index => TaskVariant(
                        source.Tasks[0],
                        taskId: $"task-{index}",
                        ordinal: index,
                        objective: new string((char)('a' + index), 500),
                        currentState: new string((char)('f' + index), 1000),
                        nextAction: new string((char)('k' + index), 500),
                        blockers:
                        [
                            new string((char)('p' + index), 500),
                            new string((char)('u' + index), 500)
                        ]))
                    .ToArray();
                return LocalTestContext.WithTasks(source, tasks);
            },
            "eight-kib-overflow").ConfigureAwait(false);

        await AssertProjectionLimitRetainedAsync(
            CreateNearLimitProjectionSnapshot,
            "near-limit-response-overflow").ConfigureAwait(false);
    }

    public static async Task ProjectionGapRecoveryAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var first = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            taskSetId: "gap-recovery-task-set",
            idempotencyKey: "gap-recovery-1");
        var secondSource = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            taskSetId: first.TaskSetId,
            revision: 2,
            previousHash: first.SnapshotHash,
            idempotencyKey: "gap-recovery-2");
        var second = LocalTestContext.WithTasks(
            secondSource,
            [
                TaskVariant(
                    secondSource.Tasks[0],
                    objective: new string('u', 1024))
            ]);

        var seed = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitTask(2, first),
                SubmitTask(3, second),
                LocalMcpProtocol.ToolCall(
                    4,
                    "cortex_project_brief",
                    LocalMcpProtocol.ProjectBriefArguments(
                        LocalTestContext.PrimaryProjectLinkId,
                        CortexProjectBriefService.ComputeAuthoritySetHash(
                            first.SourceRevisions))),
                ReadTask(5, second)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            seed.ExitCode,
            $"The skipped-source seed failed: {seed.StandardError}");
        var firstWrite = Structured(seed.Responses, 2, expectedError: false);
        var firstContextSetId =
            firstWrite["projectBriefContextSetId"]!.GetValue<string>();
        AssertIncompleteEvidenceProjection(
            Structured(seed.Responses, 3, expectedError: false),
            "unrepresentable predecessor",
            "cortex_project_brief_projection_unrepresentable",
            expectedRetryable: false);
        AssertTaskHeadMismatchFallback(
            Structured(seed.Responses, 4, expectedError: true),
            "same-process read without a known context");
        _ = Structured(seed.Responses, 5, expectedError: false);

        var knownStaleArguments = LocalMcpProtocol.ProjectBriefArguments(
            LocalTestContext.PrimaryProjectLinkId,
            CortexProjectBriefService.ComputeAuthoritySetHash(
                first.SourceRevisions));
        knownStaleArguments["knownContextSetId"] = firstContextSetId;
        var staleReads = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                LocalMcpProtocol.ToolCall(
                    2,
                    "cortex_project_brief",
                    LocalMcpProtocol.ProjectBriefArguments(
                        LocalTestContext.PrimaryProjectLinkId,
                        CortexProjectBriefService.ComputeAuthoritySetHash(
                            first.SourceRevisions))),
                LocalMcpProtocol.ToolCall(
                    3,
                    "cortex_project_brief",
                    knownStaleArguments)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            staleReads.ExitCode,
            "The restarted stale-head reads escaped as a process failure: " +
            staleReads.StandardError);
        AssertTaskHeadMismatchFallback(
            Structured(staleReads.Responses, 2, expectedError: true),
            "restarted read without a known context");
        AssertTaskHeadMismatchFallback(
            Structured(staleReads.Responses, 3, expectedError: true),
            "restarted read with the prior known context");

        var thirdTemplate = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            taskSetId: first.TaskSetId,
            revision: 3,
            previousHash: second.SnapshotHash,
            idempotencyKey: "gap-recovery-3",
            stateSuffix: "-concise");
        var recoveredAt = second.CapturedAt;
        var third = WithTaskSnapshotTiming(thirdTemplate, recoveredAt);
        var knownArguments = LocalMcpProtocol.ProjectBriefArguments(
            LocalTestContext.PrimaryProjectLinkId,
            CortexProjectBriefService.ComputeAuthoritySetHash(
                third.SourceRevisions));
        knownArguments["knownContextSetId"] = firstContextSetId;
        var recovered = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitTask(2, third),
                LocalMcpProtocol.ToolCall(
                    3,
                    "cortex_project_brief",
                    knownArguments)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            recovered.ExitCode,
            $"The concise source could not recover the hot brief: {recovered.StandardError}");
        var rawThird = LocalMcpProtocol.ResponseById(recovered.Responses, 2);
        LocalAssert.False(
            LocalMcpProtocol.ToolEnvelope(rawThird)["isError"]!.GetValue<bool>(),
            "The concise source submission failed before recovery: " +
            rawThird.ToJsonString());
        var thirdWrite = Structured(recovered.Responses, 2, expectedError: false);
        LocalAssert.Equal(
            CortexRetentionWriteDispositions.Appended,
            thirdWrite["projectBriefDisposition"]!.GetValue<string>(),
            "The verified skipped source did not append a recovered brief: " +
            thirdWrite.ToJsonString());
        var brief = Structured(recovered.Responses, 3, expectedError: false);
        LocalAssert.Equal(
            "snapshot",
            brief["disposition"]!.GetValue<string>(),
            "A source gap fabricated an immediate-predecessor delta.");
        LocalAssert.True(
            brief["baseContextSetId"] is null,
            "A source gap exposed a false delta base.");
        LocalAssert.Equal(
            3L,
            brief["stateRevision"]!.GetValue<long>(),
            "The recovered brief lost the exact task-source revision.");
        LocalAssert.Equal(
            third.SnapshotHash,
            brief["sourceHash"]!.GetValue<string>(),
            "The recovered brief did not bind the concise task head.");
    }

    private static void AssertTaskHeadMismatchFallback(
        JsonObject brief,
        string caseName)
    {
        LocalAssert.False(
            brief["accepted"]!.GetValue<bool>(),
            $"The {caseName} accepted a stale project brief.");
        LocalAssert.True(
            brief["fallbackRequired"]!.GetValue<bool>(),
            $"The {caseName} did not require authoritative fallback.");
        LocalAssert.Equal(
            "retained_task_head_mismatch",
            brief["fallbackReason"]!.GetValue<string>(),
            $"The {caseName} returned the wrong fallback reason.");
        LocalAssert.True(
            brief["contextSetId"] is null &&
            brief["baseContextSetId"] is null &&
            brief["stateRevision"] is null &&
            brief["sourceHash"] is null &&
            brief["currentTask"] is null,
            $"The {caseName} exposed stale snapshot identity or task data.");
        LocalAssert.Equal(
            0,
            brief["hotTasks"]!.AsArray().Count,
            $"The {caseName} exposed stale hot-task data.");
        LocalAssert.Equal(
            "cortex_project_brief_source_head_mismatch",
            brief["diagnostic"]!.AsObject()["code"]!.GetValue<string>(),
            $"The {caseName} did not expose the source-head mismatch.");
    }

    public static async Task ProhibitedContentFailsBoundedlyAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var taskSource = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var prohibitedTask = LocalTestContext.WithTasks(
            taskSource,
            [TaskVariant(
                taskSource.Tasks[0],
                currentState:
                    "Blocked because password=correct-horse-battery-staple.",
                blockers:
                [
                    "Raw terminal output: captured during diagnosis."
                ])]);
        var prohibitedWork = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            workId: "secret-work",
            idempotencyKey: "secret-work-idempotency",
            constraints:
            [
                "Use Bearer abcdefghijklmnopqrstuvwxyz123456 only for this run."
            ]);
        var cleanWork = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            workId: "clean-work",
            idempotencyKey: "clean-work-idempotency");

        var result = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitTask(2, prohibitedTask),
                ReadTask(3, prohibitedTask),
                SubmitWork(4, prohibitedWork,
                    LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryNamespace),
                ReadWork(5, prohibitedWork,
                    LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryProjectId,
                    LocalTestContext.PrimaryNamespace),
                SubmitWork(6, cleanWork,
                    LocalTestContext.PrimaryProjectLinkId,
                    LocalTestContext.PrimaryNamespace)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            result.ExitCode,
            $"Content screening escaped as a process failure: {result.StandardError}");
        LocalAssert.False(
            result.StandardOutput.Contains(
                "correct-horse-battery-staple",
                StringComparison.Ordinal) ||
            result.StandardOutput.Contains(
                "abcdefghijklmnopqrstuvwxyz123456",
                StringComparison.Ordinal),
            "Rejected prohibited bytes leaked into the bounded MCP response.");

        var taskRejected = Structured(result.Responses, 2, expectedError: true);
        LocalAssert.Equal(
            "cortex_retained_assertion_content_rejected",
            LocalMcpProtocol.DiagnosticCode(taskRejected),
            "Task prohibited content did not use the fixed tool diagnostic.");
        var taskFindings = taskRejected["diagnostic"]!.AsObject()["findingCodes"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        LocalAssert.True(
            taskFindings.Contains("credential_assignment") &&
            taskFindings.Contains("raw_private_reasoning"),
            "Task prohibited-content findings were not returned as bounded codes.");
        LocalAssert.Equal(
            "cortex_retained_record_not_available",
            LocalMcpProtocol.DiagnosticCode(
                Structured(result.Responses, 3, expectedError: true)),
            "A prohibited task assertion reached retention.");

        var workRejected = Structured(result.Responses, 4, expectedError: true);
        LocalAssert.Equal(
            "cortex_retained_assertion_content_rejected",
            LocalMcpProtocol.DiagnosticCode(workRejected),
            "Work-packet prohibited content did not use the fixed tool diagnostic.");
        LocalAssert.True(
            workRejected["diagnostic"]!.AsObject()["findingCodes"]!
                .AsArray()
                .Any(item => string.Equals(
                    item!.GetValue<string>(),
                    "bearer_token",
                    StringComparison.Ordinal)),
            "Work-packet screening omitted its bounded bearer-token finding.");
        LocalAssert.Equal(
            "cortex_retained_record_not_available",
            LocalMcpProtocol.DiagnosticCode(
                Structured(result.Responses, 5, expectedError: true)),
            "A prohibited work packet reached retention.");
        _ = Structured(result.Responses, 6, expectedError: false);
    }

    public static async Task ServiceSessionIsolationAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        await using var composition = await CortexMcpLocalComposition.CreateAsync(
            context.ProfileId,
            context.DatabasePath,
            context.RegistryPath,
            "service",
            TimeProvider.System,
            CancellationToken.None);
        using var serviceCancellation = new CancellationTokenSource();
        var service = RunServerAsync(
            context.ProfileId,
            composition,
            serviceCancellation.Token);
        var endpoint = CortexLocalEndpoint.Create(context.ProfileId);

        await using (var first = await CortexLocalPipeClient.ConnectAsync(
            endpoint,
            TimeSpan.FromSeconds(5),
            CancellationToken.None))
        {
            var initialized = await PipeCallAsync(
                first,
                LocalMcpProtocol.Initialize()).ConfigureAwait(false);
            LocalAssert.True(
                initialized is not null && initialized["result"] is JsonObject,
                "The first pipe session could not initialize.");
            var notification = await PipeCallAsync(
                first,
                LocalMcpProtocol.Initialized()).ConfigureAwait(false);
            LocalAssert.True(
                notification is null,
                "The initialized notification unexpectedly produced a response.");
        }

        await using (var second = await CortexLocalPipeClient.ConnectAsync(
            endpoint,
            TimeSpan.FromSeconds(5),
            CancellationToken.None))
        {
            var uninitialized = await PipeCallAsync(
                second,
                LocalMcpProtocol.ToolsList(2)).ConfigureAwait(false);
            LocalAssert.Equal(
                "cortex_mcp_not_initialized",
                uninitialized!["error"]!.AsObject()["data"]!
                    .AsObject()["code"]!.GetValue<string>(),
                "A new pipe session inherited another client's MCP lifecycle state.");
        }

        await using (var third = await CortexLocalPipeClient.ConnectAsync(
            endpoint,
            TimeSpan.FromSeconds(5),
            CancellationToken.None))
        {
            _ = await PipeCallAsync(
                third,
                LocalMcpProtocol.Initialize()).ConfigureAwait(false);
            _ = await PipeCallAsync(
                third,
                LocalMcpProtocol.Initialized()).ConfigureAwait(false);
            var tools = await PipeCallAsync(
                third,
                LocalMcpProtocol.ToolsList(3)).ConfigureAwait(false);
            LocalAssert.True(
                tools?["result"]?.AsObject()["tools"] is JsonArray,
                "A newly initialized pipe session could not list tools.");
        }

        serviceCancellation.Cancel();
        await ObserveCancellationAsync(service).ConfigureAwait(false);
    }

    public static async Task ExplicitModeFailureAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var profile = $"absent-{suffix}";
        var paths =
            CortexLocalProjectRegistryProvisioner.CurrentUserStatePaths(profile);
        LocalAssert.False(
            Directory.Exists(paths.StateRoot) || File.Exists(paths.StateRoot),
            "The explicit failure fixture unexpectedly selected existing service state.");

        var service = await LocalProcessHarness.RunAsync(
            ["service", "--profile", profile],
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            service.ExitCode,
            "Service mode did not fail closed for an unprovisioned derived profile.");
        LocalAssert.False(
            Directory.Exists(paths.StateRoot) || File.Exists(paths.StateRoot),
            "Failed service startup created or modified derived current-user state.");

        var bridge = await LocalProcessHarness.RunAsync(
            ["bridge", "--profile", profile],
            [],
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            bridge.ExitCode,
            "Bridge mode silently switched when its explicit local service was unavailable.");

        var stdioMissingRoot = await LocalProcessHarness.RunAsync(
            ["stdio", "--profile", profile],
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            2,
            stdioMissingRoot.ExitCode,
            "Retired stdio startup was accepted.");
        LocalAssert.True(
            stdioMissingRoot.StandardError.Contains(PegasusCortexMcpLocalProgram.StdioRetirementMessage, StringComparison.Ordinal),
            "Retired stdio startup omitted the shared HTTPS endpoint remedy.");

        var serviceWithPortablePath = await LocalProcessHarness.RunAsync(
            [
                "service",
                "--profile",
                profile,
                "--state-root",
                Path.GetTempPath()
            ],
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            2,
            serviceWithPortablePath.ExitCode,
            "Service mode accepted an arbitrary portable state path.");
    }

    private static string SubmitTask(int id, Pegasus.Cortex.Contracts.WorkPackets.AgentTaskSnapshotV1 snapshot) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_task_snapshot",
            LocalMcpProtocol.SubmitArguments(
                snapshot.ProjectLinkId!,
                LocalTestContext.PrimaryNamespace,
                LocalMcpProtocol.AssertionNode(snapshot)));

    private static string ReadTask(
        int id,
        Pegasus.Cortex.Contracts.WorkPackets.AgentTaskSnapshotV1 snapshot) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_read_retained_record",
            LocalMcpProtocol.ReadArguments(
                snapshot.ProjectLinkId!,
                snapshot.ProjectId,
                LocalTestContext.PrimaryNamespace,
                CortexRetainedDocumentKinds.TaskSnapshot,
                snapshot.TaskSetId,
                snapshot.SnapshotRevision,
                LocalTestContext.ContentHash(snapshot)));

    private static string SubmitWork(
        int id,
        Pegasus.Cortex.Contracts.WorkPackets.AgentWorkPacketV1 packet,
        string projectLinkId,
        string projectNamespace) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_work_packet",
            LocalMcpProtocol.SubmitArguments(
                projectLinkId,
                projectNamespace,
                LocalMcpProtocol.AssertionNode(packet)));

    private static string ReadWork(
        int id,
        Pegasus.Cortex.Contracts.WorkPackets.AgentWorkPacketV1 packet,
        string projectLinkId,
        string projectId,
        string projectNamespace) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_read_retained_record",
            LocalMcpProtocol.ReadArguments(
                projectLinkId,
                projectId,
                projectNamespace,
                CortexRetainedDocumentKinds.WorkPacket,
                packet.WorkId,
                packet.PacketRevision,
                LocalTestContext.ContentHash(packet)));

    private static string SubmitReceipt<T>(
        int id,
        string toolName,
        T receipt) =>
        LocalMcpProtocol.ToolCall(
            id,
            toolName,
            LocalMcpProtocol.SubmitReceiptArguments(
                LocalTestContext.PrimaryProjectLinkId,
                LocalTestContext.PrimaryProjectId,
                LocalTestContext.PrimaryNamespace,
                LocalMcpProtocol.AssertionNode(receipt)));

    private static string ReadReceipt(
        int id,
        string kind,
        string receiptId,
        string receiptHash,
        string contentHash) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_read_retained_record",
            LocalMcpProtocol.ReadArguments(
                LocalTestContext.PrimaryProjectLinkId,
                LocalTestContext.PrimaryProjectId,
                LocalTestContext.PrimaryNamespace,
                kind,
                receiptId,
                revision: 1,
                contentHash));

    private static AgentTaskEvidenceReferenceV1 EvidenceReference(
        string evidenceId,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        string assertionHash) =>
        new(
            evidenceId,
            kind,
            documentId,
            revision,
            contentHash,
            assertionHash);

    private static JsonObject FindRetainedReference(
        JsonArray references,
        string kind) =>
        references
            .Select(item => item!.AsObject())
            .Single(reference =>
                string.Equals(
                    reference["retainedRecord"]?.AsObject()["kind"]
                        ?.GetValue<string>(),
                    kind,
                    StringComparison.Ordinal));

    private static string ReadLocator(int id, JsonObject locator) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_read_retained_record",
            LocalMcpProtocol.ReadArguments(
                locator["projectLinkId"]!.GetValue<string>(),
                locator["projectId"]!.GetValue<string>(),
                locator["projectNamespace"]!.GetValue<string>(),
                locator["kind"]!.GetValue<string>(),
                locator["documentId"]!.GetValue<string>(),
                locator["revision"]!.GetValue<long>(),
                locator["contentHash"]!.GetValue<string>()));

    private static async ValueTask<JsonObject> ColdCallAsync(
        Process process,
        string request,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(request)
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        var response = await process.StandardOutput.ReadLineAsync(
            cancellationToken).ConfigureAwait(false);
        return response is null
            ? throw new EndOfStreamException(
                "The cold MCP process closed before returning a response.")
            : LocalMcpProtocol.ParseResponse(response);
    }

    private static void AssertReceiptWrite(
        JsonObject result,
        string expectedKind,
        string expectedReceiptId,
        string expectedReceiptHash,
        string expectedDisposition)
    {
        LocalAssert.Equal(
            expectedDisposition,
            result["disposition"]!.GetValue<string>(),
            "The receipt write returned the wrong immutable disposition.");
        LocalAssert.Equal(
            expectedKind,
            result["kind"]!.GetValue<string>(),
            "The receipt write returned the wrong retained kind.");
        LocalAssert.Equal(
            expectedReceiptId,
            result["documentId"]!.GetValue<string>(),
            "The receipt write returned the wrong document identity.");
        LocalAssert.Equal(
            1L,
            result["revision"]!.GetValue<long>(),
            "A receipt was not immutable revision one.");
        LocalAssert.Equal(
            expectedReceiptHash,
            result["assertionHash"]!.GetValue<string>(),
            "The receipt write returned a different self-hash.");
        AssertPassiveEnvelope(result, "receipt write");
    }

    private static void AssertPassiveReceiptRead(
        JsonObject result,
        string expectedReceiptHash,
        string expectedContractId)
    {
        LocalAssert.Equal(
            expectedReceiptHash,
            result["assertionHash"]!.GetValue<string>(),
            "The exact receipt read returned a different self-hash.");
        LocalAssert.Equal(
            expectedContractId,
            result["contractId"]!.GetValue<string>(),
            "The exact receipt read returned a different contract.");
        var assertion = result["assertion"]!.AsObject();
        LocalAssert.Equal(
            expectedReceiptHash,
            assertion["receiptHash"]!.GetValue<string>(),
            "The exact read did not expose the retained receipt bytes.");
        LocalAssert.False(
            assertion["executable"]!.GetValue<bool>() ||
            assertion["grantsSchedulingAuthority"]!.GetValue<bool>() ||
            assertion["grantsTaskClaim"]!.GetValue<bool>() ||
            assertion["grantsAuthority"]!.GetValue<bool>(),
            "A retained receipt asserted executable or authority semantics.");
        AssertPassiveEnvelope(result, "receipt read");
    }

    private static void AssertPassiveEnvelope(JsonObject result, string context)
    {
        LocalAssert.False(
            result["authoritative"]!.GetValue<bool>() ||
            result["executable"]!.GetValue<bool>() ||
            result["grantsSchedulingAuthority"]!.GetValue<bool>() ||
            result["grantsTaskClaim"]!.GetValue<bool>() ||
            result["grantsAuthority"]!.GetValue<bool>(),
            $"The {context} envelope granted authority.");
        LocalAssert.Equal(
            AgentWorkPacketContractIds.ContentTreatment,
            result["contentTreatment"]!.GetValue<string>(),
            $"The {context} envelope changed the passive content treatment.");
    }

    private static async Task AssertEvidenceProjectionFailureAsync(
        LocalTestContext context,
        AgentTaskSnapshotV1 snapshot,
        IReadOnlyList<string> seedRequests,
        string caseName,
        string expectedIssueCode,
        bool expectedRetryable)
    {
        var submitId = 2 + seedRequests.Count;
        var readId = submitId + 1;
        var briefId = submitId + 2;
        var requests = new List<string>
        {
            LocalMcpProtocol.Initialize(),
            LocalMcpProtocol.Initialized()
        };
        requests.AddRange(seedRequests);
        requests.Add(SubmitTask(submitId, snapshot));
        requests.Add(ReadTask(readId, snapshot));
        requests.Add(LocalMcpProtocol.ToolCall(
            briefId,
            "cortex_project_brief",
            LocalMcpProtocol.ProjectBriefArguments(
                LocalTestContext.PrimaryProjectLinkId,
                CortexProjectBriefService.ComputeAuthoritySetHash(
                    snapshot.SourceRevisions))));
        var result = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            requests).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            result.ExitCode,
            $"The {caseName} evidence fixture failed: {result.StandardError}");
        AssertIncompleteEvidenceProjection(
            Structured(result.Responses, submitId, expectedError: false),
            caseName,
            expectedIssueCode,
            expectedRetryable);
        _ = Structured(result.Responses, readId, expectedError: false);
        LocalAssert.Equal(
            "cortex_project_brief_snapshot_not_available",
            LocalMcpProtocol.DiagnosticCode(
                Structured(result.Responses, briefId, expectedError: true)),
            $"The {caseName} evidence unexpectedly materialized a brief.");
    }

    private static void AssertIncompleteEvidenceProjection(
        JsonObject write,
        string caseName,
        string expectedIssueCode,
        bool expectedRetryable)
    {
        LocalAssert.True(
            write["accepted"]!.GetValue<bool>(),
            $"The exact {caseName} task assertion was not retained.");
        LocalAssert.Equal(
            CortexProjectBriefMaterializationDispositions.Unavailable,
            write["projectBriefDisposition"]!.GetValue<string>(),
            $"The {caseName} evidence did not mark its derived view unavailable.");
        LocalAssert.Equal(
            expectedIssueCode,
            write["materializationDiagnostic"]!.AsObject()["code"]!
                .GetValue<string>(),
            $"The {caseName} evidence did not expose the fixed projection diagnostic.");
        LocalAssert.Equal(
            expectedRetryable,
            write["materializationDiagnostic"]!.AsObject()["retryable"]!
                .GetValue<bool>(),
            $"The {caseName} evidence returned the wrong retry classification.");
    }

    private static JsonObject Structured(
        IReadOnlyList<JsonObject> responses,
        int id,
        bool expectedError)
    {
        var response = LocalMcpProtocol.ResponseById(responses, id);
        LocalAssert.Equal(
            expectedError,
            LocalMcpProtocol.ToolEnvelope(response)["isError"]!.GetValue<bool>(),
            $"Tool call {id} returned the wrong MCP error envelope.");
        return LocalMcpProtocol.Structured(response);
    }

    private static string RawSubmit(
        int id,
        string projectLinkId,
        string projectNamespace,
        string rawAssertion)
    {
        const string Marker = "__raw_assertion_marker__";
        var template = LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_task_snapshot",
            LocalMcpProtocol.SubmitArguments(
                projectLinkId,
                projectNamespace,
                JsonValue.Create(Marker)!));
        return template.Replace(
            $"\"{Marker}\"",
            rawAssertion,
            StringComparison.Ordinal);
    }

    private static Task RunServerAsync(
        string profileId,
        CortexMcpLocalComposition composition,
        CancellationToken cancellationToken)
    {
        var diagnostics = TextWriter.Synchronized(new StringWriter());
        var server = new CortexLocalPipeServer(
            CortexLocalEndpoint.Create(profileId),
            () => new CortexMcpLocalPipeHandler(
                composition.Application,
                diagnostics));
        return server.RunAsync(cancellationToken);
    }

    private static async ValueTask<JsonObject?> PipeCallAsync(
        CortexLocalPipeClient client,
        string request)
    {
        var response = await client.CallAsync(
            Encoding.UTF8.GetBytes(request),
            CancellationToken.None).ConfigureAwait(false);
        return response is null
            ? null
            : LocalMcpProtocol.ParseResponse(
                Encoding.UTF8.GetString(response.Value.Span));
    }

    private static async Task ObserveCancellationAsync(Task service)
    {
        try
        {
            await service.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected termination for an in-process service fixture.
        }
    }

    private static async Task AssertProjectionLimitRetainedAsync(
        Func<LocalTestContext, AgentTaskSnapshotV1> createSnapshot,
        string caseName)
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var snapshot = createSnapshot(context);
        LocalAssert.True(
            AgentTaskSnapshotPolicy.HasValidHash(snapshot),
            $"The {caseName} fixture is not a valid exact task snapshot.");

        var result = await LocalProcessHarness.RunAsync(
            [
                "stdio",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!
            ],
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitTask(2, snapshot),
                ReadTask(3, snapshot),
                LocalMcpProtocol.ToolCall(
                    4,
                    "cortex_project_brief",
                    LocalMcpProtocol.ProjectBriefArguments(
                        LocalTestContext.PrimaryProjectLinkId,
                        CortexProjectBriefService.ComputeAuthoritySetHash(
                            snapshot.SourceRevisions)))
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            result.ExitCode,
            $"The {caseName} retention process failed: {result.StandardError}");

        var retained = Structured(result.Responses, 2, expectedError: false);
        LocalAssert.True(
            retained["accepted"]!.GetValue<bool>(),
            $"The valid {caseName} task snapshot was not accepted for retention.");
        LocalAssert.Equal(
            CortexRetentionWriteDispositions.Appended,
            retained["disposition"]!.GetValue<string>(),
            $"The valid {caseName} task snapshot was not appended.");
        LocalAssert.Equal(
            "unavailable",
            retained["projectBriefDisposition"]!.GetValue<string>(),
            $"The {caseName} projection limitation was not reported.");
        LocalAssert.False(
            retained["projectBriefMaterialized"]!.GetValue<bool>(),
            $"The unrepresentable {caseName} brief was marked materialized.");
        LocalAssert.True(
            retained["fallbackRequired"]!.GetValue<bool>(),
            $"The {caseName} response did not require exact-read fallback.");
        var materialization =
            retained["materializationDiagnostic"]!.AsObject();
        LocalAssert.Equal(
            "cortex_project_brief_projection_unrepresentable",
            materialization["code"]!.GetValue<string>(),
            $"The {caseName} response did not expose a bounded materialization warning.");
        LocalAssert.False(
            materialization["retryable"]!.GetValue<bool>(),
            $"The deterministic {caseName} projection limitation invited an exact-replay loop.");
        _ = Structured(result.Responses, 3, expectedError: false);
        _ = Structured(result.Responses, 4, expectedError: true);
    }

    private static AgentTaskV1 TaskVariant(
        AgentTaskV1 source,
        string? taskId = null,
        int? ordinal = null,
        string? reportedState = null,
        string? objective = null,
        string? currentState = null,
        string? nextAction = null,
        IReadOnlyList<string>? blockers = null,
        IReadOnlyList<string>? evidenceIds = null) =>
        new(
            taskId ?? source.TaskId,
            source.ParentTaskId,
            ordinal ?? source.Ordinal,
            reportedState ?? source.ReportedState,
            objective ?? source.Objective,
            currentState ?? source.CurrentState,
            nextAction ?? source.NextAction,
            source.Constraints,
            source.DependencyTaskIds,
            blockers ?? source.Blockers,
            source.KnownFailures,
            source.ReportedOwner,
            source.ApprovalGate,
            evidenceIds ?? source.EvidenceIds,
            source.SourceRevisionIds);

    private static AgentTaskSnapshotV1 WithTaskSnapshotTiming(
        AgentTaskSnapshotV1 source,
        DateTimeOffset capturedAt)
    {
        var expiresAt = capturedAt.AddHours(2);
        return AgentTaskSnapshotPolicy.Seal(new AgentTaskSnapshotV1(
            source.SchemaVersion,
            source.ContractId,
            source.Canonicalization,
            source.Mode,
            source.TaskSetId,
            source.SnapshotId,
            source.SnapshotRevision,
            source.PreviousSnapshotHash,
            source.IdempotencyKey,
            source.ProjectId,
            source.ProjectLinkId,
            source.ContextSetId,
            capturedAt,
            expiresAt,
            source.Producer,
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Current,
                capturedAt,
                expiresAt,
                "The producer reports this exact source revision as current."),
            source.SourceRevisions,
            source.EvidenceReferences,
            source.Coverage,
            source.Tasks,
            source.ContentTreatment,
            source.Executable,
            source.GrantsSchedulingAuthority,
            source.GrantsTaskClaim,
            source.GrantsAuthority,
            source.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256));
    }

    private static AgentTaskSnapshotV1 CreateNearLimitProjectionSnapshot(
        LocalTestContext context)
    {
        var source = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var scope = new Pegasus.Cortex.Core.Query.CortexProjectBriefReadScope(
            LocalTestContext.TenantId,
            LocalTestContext.PrimaryProjectLinkId,
            LocalTestContext.PrimaryProjectId,
            LocalTestContext.PrimaryNamespace,
            "local-test-authority",
            "local-test-subject",
            "local-test-service",
            PolicyRevision: 1);
        AgentTaskSnapshotV1? selected = null;
        var selectedLength = 0;
        for (var payloadLength = 10; payloadLength <= 1000; payloadLength += 10)
        {
            AgentTaskSnapshotV1 candidate;
            try
            {
                var tasks = Enumerable.Range(0, 5)
                    .Select(index => TaskVariant(
                        source.Tasks[0],
                        taskId: $"task-{index}",
                        ordinal: index,
                        objective: new string((char)('a' + index), 50),
                        currentState:
                            new string((char)('f' + index), payloadLength),
                        nextAction: new string((char)('k' + index), 50),
                        blockers:
                        [
                            new string((char)('p' + index), 50)
                        ]))
                    .ToArray();
                candidate = LocalTestContext.WithTasks(source, tasks);
                var checkpoint =
                    new Pegasus.Cortex.Core.Query.CortexProjectBriefReference(
                        $"hash://{candidate.SnapshotHash}",
                        candidate.SnapshotHash,
                        RecordId: null,
                        RevisionId: null);
                var projected =
                    Pegasus.Cortex.Core.Query.CortexProjectBriefProjector.Project(
                        candidate,
                        new Pegasus.Cortex.Core.Query.CortexProjectBriefProjectionInput(
                             candidate.ProjectId,
                             scope,
                             [checkpoint],
                             [],
                             SkippedSourceChainProof: null));
                var length =
                    Pegasus.Cortex.Contracts.Serialization.CortexCanonicalJson
                        .Serialize(projected).Length;
                if (length > selectedLength)
                {
                    selected = candidate;
                    selectedLength = length;
                }
            }
            catch (ArgumentException exception)
            {
                if (selected is null)
                {
                    throw new InvalidOperationException(
                        "The first near-limit fixture candidate was invalid.",
                        exception);
                }
                break;
            }
        }

        LocalAssert.True(
            selected is not null && selectedLength > 7600,
            $"The near-limit project-brief fixture did not approach the 8 KiB snapshot boundary (selected {selectedLength} bytes).");
        return selected!;
    }
}
