using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Recovery;

internal static class LocalRecoveryRestoreTests
{
    public static async Task ExplicitCurrentStateRestoreAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var stateRoot = Path.GetDirectoryName(context.DatabasePath)!;
        var registration = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId);
        var authorityPath = Path.Combine(registration.RootPath, "STATE.md");
        await File.WriteAllTextAsync(
            authorityPath,
            "restore must not mutate project authority\n").ConfigureAwait(false);

        var packet = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var handoff = context.HandoffReceipt(packet, "handoff-restore");
        var acceptance = context.AcceptanceReceipt(
            handoff,
            "acceptance-restore");
        var evidence = new AgentTaskEvidenceReferenceV1(
            "restore-evidence",
            AgentTaskEvidenceKindIds.WorkPacket,
            packet.WorkId,
            packet.PacketRevision,
            LocalTestContext.ContentHash(packet),
            packet.PacketHash);
        var firstTask = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            stateSuffix: "-restore-one");
        var gate = new AgentWorkApprovalGateV1(
            AgentWorkApprovalGateStateIds.Satisfied,
            "restore-approval-authority",
            "restore-approval-reference",
            "The producer reports an externally verifiable approval.",
            grantsAuthority: false);
        var secondTemplate = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            revision: 2,
            previousHash: firstTask.SnapshotHash,
            stateSuffix: "-restore-two",
            evidenceReferences: [evidence],
            approvalGate: gate);
        var sourceTask = secondTemplate.Tasks.Single();
        const string ExpectedNextAction =
            "Reopen the exact recovered task and verify its evidence.";
        const string ExpectedBlocker =
            "Await independent verification of the recovery receipt.";
        var secondTask = LocalTestContext.WithTasks(
            secondTemplate,
            [
                new AgentTaskV1(
                    sourceTask.TaskId,
                    sourceTask.ParentTaskId,
                    sourceTask.Ordinal,
                    AgentTaskStateIds.Blocked,
                    sourceTask.Objective,
                    "The protected recovery export is ready.",
                    ExpectedNextAction,
                    sourceTask.Constraints,
                    sourceTask.DependencyTaskIds,
                    [ExpectedBlocker],
                    sourceTask.KnownFailures,
                    sourceTask.ReportedOwner,
                    gate,
                    sourceTask.EvidenceIds,
                    sourceTask.SourceRevisionIds)
            ]);

        var seeded = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitWork(2, packet),
                SubmitReceipt(
                    3,
                    "cortex_submit_work_packet_handoff_receipt",
                    handoff),
                SubmitReceipt(
                    4,
                    "cortex_submit_work_packet_acceptance_receipt",
                    acceptance),
                SubmitTask(5, firstTask),
                SubmitTask(6, secondTask),
                ProposalCall(7)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            seeded.ExitCode,
            $"Restore fixture population failed: {seeded.StandardError}");
        foreach (var id in Enumerable.Range(2, 6))
            RequireToolSuccess(seeded.Responses, id, "restore fixture population");

        var recoveryBase = Path.Combine(context.Root, "restore-exports");
        Directory.CreateDirectory(recoveryBase);
        CortexLocalRegistryAclPolicy.ProtectDirectory(recoveryBase);
        var exported = await LocalProcessHarness.RunAsync(
            ExportArguments(
                context,
                recoveryBase,
                "restore-source-run",
                secondTask.CapturedAt.AddSeconds(1)),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            exported.ExitCode,
            $"Restore fixture export failed: {exported.StandardError}");
        var exportResult = JsonNode.Parse(exported.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("Restore fixture export result is not JSON.");
        var exportRoot = Path.Combine(
            recoveryBase,
            exportResult["exportRelativePath"]!.GetValue<string>());
        var restoreArguments = RestoreArguments(
            context,
            LocalTestContext.PrimaryProjectLinkId,
            exportRoot);

        DeleteDatabaseEntries(context.DatabasePath);
        RequireTargetAbsent(context.DatabasePath);

        // An abandoned staged child blocks both normal startup and a second
        // restore until an operator removes the exact protected artifact.
        var abandoned = Path.Combine(stateRoot, ".restore-abandoned");
        Directory.CreateDirectory(abandoned);
        CortexLocalRegistryAclPolicy.ProtectDirectory(abandoned);
        var blockedStartup = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            blockedStartup.ExitCode,
            "Normal startup accepted an abandoned restore staging child.");
        var blockedRestore = await LocalProcessHarness.RunAsync(
            restoreArguments,
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            blockedRestore.ExitCode,
            "A second restore ignored abandoned staging cleanup.");
        Directory.Delete(abandoned);
        RequireTargetAbsent(context.DatabasePath);

        var rollbackJournal = context.DatabasePath + "-journal";
        await File.WriteAllTextAsync(rollbackJournal, "occupied")
            .ConfigureAwait(false);
        var journalBlocked = await LocalProcessHarness.RunAsync(
            restoreArguments,
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            journalBlocked.ExitCode,
            "Restore accepted an existing rollback journal.");
        File.Delete(rollbackJournal);
        RequireTargetAbsent(context.DatabasePath);

        var wrongScope = await LocalProcessHarness.RunAsync(
            RestoreArguments(
                context,
                LocalTestContext.ReadOnlyProjectLinkId,
                exportRoot),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            wrongScope.ExitCode,
            "Restore accepted a project without the exact read/write grant.");
        RequireTargetAbsent(context.DatabasePath);

        var briefPath = Path.Combine(exportRoot, "project-brief.json");
        var originalBrief = await File.ReadAllBytesAsync(briefPath)
            .ConfigureAwait(false);
        var tamperedBrief = originalBrief.ToArray();
        tamperedBrief[^2] ^= 1;
        await File.WriteAllBytesAsync(briefPath, tamperedBrief)
            .ConfigureAwait(false);
        var tampered = await LocalProcessHarness.RunAsync(
            restoreArguments,
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            tampered.ExitCode,
            "Restore accepted a tampered protected export.");
        RequireTargetAbsent(context.DatabasePath);
        await File.WriteAllBytesAsync(briefPath, originalBrief)
            .ConfigureAwait(false);

        var exportFingerprint = TreeFingerprint(exportRoot);
        var registryFingerprint = FileFingerprint(context.RegistryPath);
        var authorityFingerprint = FileFingerprint(authorityPath);

        // Promotion is already durable if deleting the now-empty staging child
        // fails. That state must be reported as cleanup-required, never as an
        // accepted restore that normal startup cannot use.
        var registry = await CortexLocalProjectRegistry.LoadAsync(
            context.RegistryPath,
            context.ProfileId,
            CancellationToken.None).ConfigureAwait(false);
        var cleanupFailureRestorer = new CortexLocalRecoveryExportRestorer(
            registry,
            context.ProfileId,
            stateRoot,
            context.DatabasePath,
            _ => throw new IOException(
                "Injected post-promotion staging cleanup failure."));
        var cleanupRequired = await cleanupFailureRestorer.RestoreAsync(
            LocalTestContext.PrimaryProjectLinkId,
            exportRoot,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.False(
            cleanupRequired.Accepted,
            "Post-promotion cleanup failure returned Accepted=true.");
        LocalAssert.Equal(
            "cleanup-required",
            cleanupRequired.Disposition,
            "Post-promotion cleanup failure hid its unusable startup state.");
        LocalAssert.True(
            cleanupRequired.FallbackRequired &&
            string.Equals(
                cleanupRequired.DiagnosticCode,
                "cortex_local_recovery_restore_cleanup_required",
                StringComparison.Ordinal),
            "Post-promotion cleanup failure omitted actionable machine state.");
        LocalAssert.True(
            File.Exists(context.DatabasePath),
            "Injected cleanup failure lost the atomically promoted database.");
        var cleanupChild = Directory
            .EnumerateDirectories(stateRoot)
            .Single(path => Path.GetFileName(path).StartsWith(
                ".restore-",
                StringComparison.Ordinal));
        LocalAssert.False(
            Directory.EnumerateFileSystemEntries(cleanupChild).Any(),
            "Injected cleanup failure left an unexpected staging payload.");
        var cleanupBlockedStartup = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            cleanupBlockedStartup.ExitCode,
            "Normal startup accepted cleanup-required restore state.");
        LocalAssert.Equal(
            exportFingerprint,
            TreeFingerprint(exportRoot),
            "Cleanup-required restore changed the protected export.");
        Directory.Delete(cleanupChild);
        DeleteDatabaseEntries(context.DatabasePath);
        RequireTargetAbsent(context.DatabasePath);

        var restored = await LocalProcessHarness.RunAsync(
            restoreArguments,
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            restored.ExitCode,
            $"Explicit recovery restore failed: {restored.StandardError}");
        var result = JsonNode.Parse(restored.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("Restore result is not JSON.");
        LocalAssert.True(
            result["accepted"]!.GetValue<bool>(),
            "Restore did not return an accepted result.");
        LocalAssert.Equal(
            "current-state-rebuilt",
            result["disposition"]!.GetValue<string>(),
            "Restore overstated its functional current-state scope.");
        LocalAssert.True(
            result["contextLineageReset"]!.GetValue<bool>(),
            "Restore did not expose the derived context-lineage reset.");
        LocalAssert.False(
            result["historicalProjectBriefChainRestored"]!.GetValue<bool>(),
            "Restore claimed historical derived-brief exactness.");
        LocalAssert.False(
            result["usesNetwork"]!.GetValue<bool>() ||
            result["sourceMutationAllowed"]!.GetValue<bool>(),
            "Restore claimed network or source-mutation authority.");
        LocalAssert.False(
            string.Equals(
                result["projectBriefContextSetId"]!.GetValue<string>(),
                result["exportedProjectBriefContextSetId"]!.GetValue<string>(),
                StringComparison.Ordinal),
            "The result hid the intentional derived lineage reset.");
        LocalAssert.Equal(
            exportFingerprint,
            TreeFingerprint(exportRoot),
            "Restore changed export bytes or timestamps.");
        LocalAssert.Equal(
            registryFingerprint,
            FileFingerprint(context.RegistryPath),
            "Restore changed the protected registry.");
        LocalAssert.Equal(
            authorityFingerprint,
            FileFingerprint(authorityPath),
            "Restore changed project authority.");
        LocalAssert.False(
            Directory.EnumerateFileSystemEntries(stateRoot).Any(entry =>
                Path.GetFileName(entry).StartsWith(
                    ".restore-",
                    StringComparison.Ordinal)),
            "Successful restore left a staging child.");

        var restarted = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                LocalMcpProtocol.ToolCall(
                    2,
                    "cortex_project_brief",
                    LocalMcpProtocol.ProjectBriefArguments(
                        LocalTestContext.PrimaryProjectLinkId,
                        CortexProjectBriefService.ComputeAuthoritySetHash(
                            secondTask.SourceRevisions))),
                ReadTask(3, firstTask),
                ReadTask(4, secondTask),
                ReadWork(5, packet),
                ReadReceipt(
                    6,
                    CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                    handoff.ReceiptId,
                    LocalTestContext.ContentHash(handoff)),
                ReadReceipt(
                    7,
                    CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
                    acceptance.ReceiptId,
                    LocalTestContext.ContentHash(acceptance)),
                ListProposals(8)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            restarted.ExitCode,
            $"Restored state failed fresh-process reads: {restarted.StandardError}");
        foreach (var id in Enumerable.Range(2, 7))
            RequireToolSuccess(restarted.Responses, id, "restored restart read");
        var brief = LocalMcpProtocol.Structured(
            LocalMcpProtocol.ResponseById(restarted.Responses, 2));
        LocalAssert.Equal(
            secondTask.SnapshotRevision,
            brief["stateRevision"]!.GetValue<long>(),
            "Restored brief lost the exact latest task revision.");
        LocalAssert.Equal(
            secondTask.SnapshotHash,
            brief["sourceHash"]!.GetValue<string>(),
            "Restored brief lost the exact latest task hash.");
        var currentTask = brief["currentTask"]!.AsObject();
        LocalAssert.Equal(
            ExpectedNextAction,
            currentTask["nextAction"]!.GetValue<string>(),
            "Restored brief changed the current next action.");
        LocalAssert.Equal(
            ExpectedBlocker,
            currentTask["blocker"]!.GetValue<string>(),
            "Restored brief changed the current blocker.");
        LocalAssert.True(
            brief["checkpointReferences"]!.AsArray().Count > 0,
            "Restored brief lost its exact checkpoint reference.");
        LocalAssert.True(
            brief["evidenceReferences"]!.AsArray().Count > 0,
            "Restored brief lost its exact evidence reference.");
        var proposalPage = LocalMcpProtocol.Structured(
            LocalMcpProtocol.ResponseById(restarted.Responses, 8));
        LocalAssert.Equal(
            1,
            proposalPage["count"]!.GetValue<int>(),
            "Restored private proposal was not restart-readable.");

        var restoredDatabaseFingerprint = FileFingerprint(context.DatabasePath);
        var existing = await LocalProcessHarness.RunAsync(
            restoreArguments,
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            existing.ExitCode,
            "Restore overwrote an existing standalone database.");
        LocalAssert.Equal(
            restoredDatabaseFingerprint,
            FileFingerprint(context.DatabasePath),
            "Rejected restore changed the existing database.");
        LocalAssert.Equal(
            exportFingerprint,
            TreeFingerprint(exportRoot),
            "Rejected replay changed the protected export.");
    }

    private static string[] StdioArguments(LocalTestContext context) =>
    [
        "stdio",
        "--profile",
        context.ProfileId,
        "--state-root",
        Path.GetDirectoryName(context.DatabasePath)!
    ];

    private static string[] ExportArguments(
        LocalTestContext context,
        string destination,
        string runId,
        DateTimeOffset capturedAt) =>
    [
        "export-recovery",
        "--profile",
        context.ProfileId,
        "--state-root",
        Path.GetDirectoryName(context.DatabasePath)!,
        "--project-link",
        LocalTestContext.PrimaryProjectLinkId,
        "--destination-root",
        destination,
        "--run-id",
        runId,
        "--captured-at-utc",
        capturedAt.ToString("O")
    ];

    private static string[] RestoreArguments(
        LocalTestContext context,
        string projectLinkId,
        string exportRoot) =>
    [
        "restore-recovery-export",
        "--profile",
        context.ProfileId,
        "--state-root",
        Path.GetDirectoryName(context.DatabasePath)!,
        "--project-link",
        projectLinkId,
        "--export-root",
        exportRoot
    ];

    private static string SubmitTask(int id, AgentTaskSnapshotV1 snapshot) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_task_snapshot",
            LocalMcpProtocol.SubmitArguments(
                snapshot.ProjectLinkId!,
                LocalTestContext.PrimaryNamespace,
                LocalMcpProtocol.AssertionNode(snapshot)));

    private static string SubmitWork(int id, AgentWorkPacketV1 packet) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_work_packet",
            LocalMcpProtocol.SubmitArguments(
                LocalTestContext.PrimaryProjectLinkId,
                LocalTestContext.PrimaryNamespace,
                LocalMcpProtocol.AssertionNode(packet)));

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

    private static string ProposalCall(int id) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_proposal",
            new JsonObject
            {
                ["projectLinkId"] = LocalTestContext.PrimaryProjectLinkId,
                ["idempotencyKey"] = "restore-private-proposal",
                ["feedbackKind"] = "quality",
                ["title"] = "Bound exact recovery",
                ["summary"] = "Keep private recovery feedback exact.",
                ["content"] = "Validate the protected export before current-state rebuild.",
                ["taxonomy"] = "continuity",
                ["keywords"] = new JsonArray("exact", "recovery"),
                ["applicability"] = new JsonArray(
                    new JsonObject
                    {
                        ["productId"] = "pegasus-cortex",
                        ["componentId"] = "recovery"
                    }),
                ["occurredAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")
            });

    private static string ReadTask(int id, AgentTaskSnapshotV1 snapshot) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_read_retained_record",
            LocalMcpProtocol.ReadArguments(
                LocalTestContext.PrimaryProjectLinkId,
                LocalTestContext.PrimaryProjectId,
                LocalTestContext.PrimaryNamespace,
                CortexRetainedDocumentKinds.TaskSnapshot,
                snapshot.TaskSetId,
                snapshot.SnapshotRevision,
                LocalTestContext.ContentHash(snapshot)));

    private static string ReadWork(int id, AgentWorkPacketV1 packet) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_read_retained_record",
            LocalMcpProtocol.ReadArguments(
                LocalTestContext.PrimaryProjectLinkId,
                LocalTestContext.PrimaryProjectId,
                LocalTestContext.PrimaryNamespace,
                CortexRetainedDocumentKinds.WorkPacket,
                packet.WorkId,
                packet.PacketRevision,
                LocalTestContext.ContentHash(packet)));

    private static string ReadReceipt(
        int id,
        string kind,
        string receiptId,
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

    private static string ListProposals(int id) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_list_proposals",
            new JsonObject
            {
                ["projectLinkId"] = LocalTestContext.PrimaryProjectLinkId,
                ["limit"] = 3,
                ["includeContent"] = true
            });

    private static void RequireToolSuccess(
        IReadOnlyList<JsonObject> responses,
        int id,
        string operation)
    {
        var envelope = LocalMcpProtocol.ToolEnvelope(
            LocalMcpProtocol.ResponseById(responses, id));
        LocalAssert.False(
            envelope["isError"]?.GetValue<bool>() ?? false,
            $"{operation} call {id} failed: {envelope}");
    }

    private static void DeleteDatabaseEntries(string databasePath)
    {
        foreach (var suffix in new[]
                 {
                     string.Empty,
                     "-wal",
                     "-shm",
                     "-journal"
                 })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void RequireTargetAbsent(string databasePath)
    {
        foreach (var suffix in new[]
                 {
                     string.Empty,
                     "-wal",
                     "-shm",
                     "-journal"
                 })
        {
            var path = databasePath + suffix;
            LocalAssert.False(
                File.Exists(path) || Directory.Exists(path),
                $"Rejected restore left target entry '{Path.GetFileName(path)}'.");
        }
    }

    private static string FileFingerprint(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return string.Join(
            ":",
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            File.GetLastWriteTimeUtc(path).Ticks);
    }

    private static string TreeFingerprint(string root) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(
                    root,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                    $"{Path.GetRelativePath(root, path)}:{FileFingerprint(path)}"));
}
