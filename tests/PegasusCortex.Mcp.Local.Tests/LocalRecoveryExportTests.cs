using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Recovery;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

internal static class LocalRecoveryExportTests
{
    public static async Task ExplicitExactExportAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var registration = context.Registrations.Single(item =>
            item.ProjectLinkId == LocalTestContext.PrimaryProjectLinkId);
        var authorityFile = Path.Combine(registration.RootPath, "STATE.md");
        var authorityBytes = "project authority remains untouched\n"u8.ToArray();
        await File.WriteAllBytesAsync(authorityFile, authorityBytes)
            .ConfigureAwait(false);
        var authorityTimestamp = File.GetLastWriteTimeUtc(authorityFile);

        var firstTask = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            revision: 1,
            stateSuffix: "-one");
        var secondTask = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            revision: 2,
            previousHash: firstTask.SnapshotHash,
            stateSuffix: "-two");
        var packet = context.WorkPacket(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId);
        var continuation = ContinuePacket(packet);
        var handoff = context.HandoffReceipt(packet, "handoff-export");
        var acceptance = context.AcceptanceReceipt(
            handoff,
            "acceptance-export");
        var populated = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                SubmitTask(3, firstTask),
                SubmitTask(4, secondTask),
                SubmitWork(5, packet),
                SubmitWork(6, continuation),
                SubmitReceipt(
                    7,
                    "cortex_submit_work_packet_handoff_receipt",
                    handoff),
                SubmitReceipt(
                    8,
                    "cortex_submit_work_packet_acceptance_receipt",
                    acceptance),
                ProposalCall(
                    9,
                    "export-proposal-one",
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            populated.ExitCode,
            $"Export fixture population failed: {populated.StandardError}");
        foreach (var id in Enumerable.Range(3, 7))
        {
            var envelope = LocalMcpProtocol.ToolEnvelope(
                LocalMcpProtocol.ResponseById(populated.Responses, id));
            LocalAssert.False(
                envelope["isError"]?.GetValue<bool>() ?? false,
                $"Export fixture call {id} failed: {envelope}");
        }

        var recoveryBase = Path.Combine(context.Root, "recovery");
        Directory.CreateDirectory(recoveryBase);
        CortexLocalRegistryAclPolicy.ProtectDirectory(recoveryBase);
        LocalAssert.Equal(
            0,
            Directory.EnumerateFileSystemEntries(recoveryBase).Count(),
            "Normal stdio startup invoked recovery export automatically.");

        var capture = secondTask.CapturedAt.AddSeconds(1);
        const string RunId = "export-run-1";
        var exportArguments = ExportArguments(
            context,
            recoveryBase,
            RunId,
            capture);
        var beforeSource = SourceFingerprint(context.DatabasePath);
        var beforeState = TreeFingerprint(
            Path.GetDirectoryName(context.DatabasePath)!);
        var first = await LocalProcessHarness.RunAsync(exportArguments, [])
            .ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            first.ExitCode,
            $"Explicit recovery export failed: {first.StandardError}");
        var result = JsonNode.Parse(first.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("Export result is not JSON.");
        LocalAssert.True(
            result["accepted"]!.GetValue<bool>(),
            "Explicit recovery export was not accepted.");
        LocalAssert.Equal(
            "exported",
            result["disposition"]!.GetValue<string>(),
            "First export had the wrong disposition.");
        LocalAssert.False(
            result["sourceMutationAllowed"]!.GetValue<bool>(),
            "The export claimed source mutation authority.");
        LocalAssert.Equal(
            beforeSource,
            SourceFingerprint(context.DatabasePath),
            "Export changed the closed source SQLite file.");
        LocalAssert.Equal(
            beforeState,
            TreeFingerprint(Path.GetDirectoryName(context.DatabasePath)!),
            "Export changed SQLite state entries, bytes, or timestamps.");
        LocalAssert.True(
            File.ReadAllBytes(authorityFile).AsSpan().SequenceEqual(authorityBytes) &&
            File.GetLastWriteTimeUtc(authorityFile) == authorityTimestamp,
            "Export changed project authority bytes or timestamps.");

        var outputRoot = Path.Combine(
            recoveryBase,
            result["exportRelativePath"]!.GetValue<string>());
        var readback = await CortexLocalRecoveryExportReader.ReadAsync(
            outputRoot,
            limits: null,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.Equal(
            8,
            readback.Manifest.EntryCount,
            "Export did not retain current brief plus every passive revision.");
        LocalAssert.Equal(
            2,
            readback.Manifest.Entries.Count(entry =>
                entry.Kind == CortexRetainedDocumentKinds.TaskSnapshot),
            "Both task revisions were not exported.");
        foreach (var kind in new[]
                 {
                     CortexRetainedDocumentKinds.ProjectBrief,
                     CortexRetainedDocumentKinds.WorkPacket,
                     CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                     CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
                     CortexRetainedDocumentKinds.LearningFeedbackProposal
                 })
        {
            var expected = kind == CortexRetainedDocumentKinds.WorkPacket
                ? 2
                : 1;
            LocalAssert.Equal(
                expected,
                readback.Manifest.Entries.Count(entry => entry.Kind == kind),
                $"Export omitted or duplicated '{kind}'.");
        }
        _ = CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
            readback.ProjectBriefUtf8);

        var replay = await LocalProcessHarness.RunAsync(exportArguments, [])
            .ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            replay.ExitCode,
            $"Exact export replay failed: {replay.StandardError}");
        var replayResult = JsonNode.Parse(replay.StandardOutput)!.AsObject();
        LocalAssert.Equal(
            "idempotent",
            replayResult["disposition"]!.GetValue<string>(),
            "Exact export replay was not idempotent.");

        var thirdTask = context.TaskSnapshot(
            projectLinkId: LocalTestContext.PrimaryProjectLinkId,
            revision: 3,
            previousHash: secondTask.SnapshotHash,
            stateSuffix: "-three");
        await using (var store = new SqliteCortexRetentionStore(
                         new SqliteCortexStoreOptions(context.DatabasePath)))
        {
            var retained = await new CortexAgentRetentionApplicationService(store)
                .SubmitTaskSnapshotAsync(
                    thirdTask,
                    CancellationToken.None)
                .ConfigureAwait(false);
            LocalAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                retained.Disposition,
                "The stale-brief fixture task was not retained.");
        }
        var stale = await LocalProcessHarness.RunAsync(
            ExportArguments(
                context,
                recoveryBase,
                "stale-brief-run",
                thirdTask.CapturedAt.AddSeconds(1)),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            stale.ExitCode,
            "An export accepted a brief older than the sole task head.");

        AssertThrows<ArgumentException>(
            () => CortexLocalRecoveryExportArguments.Parse(
                ExportArguments(context, recoveryBase, "unsafe:run.", capture)),
            "An unsafe run identity passed export admission.");
        AssertThrows<ArgumentException>(
            () => CortexLocalRecoveryImportArguments.Parse(
                ImportArguments(context, recoveryBase, "unsafe:run.", capture)),
            "An unsafe run identity passed import admission.");
        AssertThrows<ArgumentException>(
            () => CortexLocalRecoveryExportPathPolicy.RequireSupportedDriveType(
                DriveType.Network),
            "A mapped network drive passed standalone recovery admission.");

        var registry = await CortexLocalProjectRegistry.LoadAsync(
            context.RegistryPath,
            context.ProfileId,
            CancellationToken.None).ConfigureAwait(false);
        var exporter = new CortexLocalRecoveryExporter(
            registry,
            context.ProfileId,
            Path.GetDirectoryName(context.DatabasePath)!,
            context.DatabasePath);
        await ThrowsAnyAsync(
            () => exporter.ExportAsync(
                LocalTestContext.PrimaryProjectLinkId,
                Path.GetDirectoryName(context.DatabasePath)!,
                "overlap-run",
                capture,
                CancellationToken.None).AsTask(),
            "An overlapping state-root destination passed export admission.")
            .ConfigureAwait(false);

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = context.DatabasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
        var offline = await CortexLocalRecoveryExportReader.ReadAsync(
            outputRoot,
            limits: null,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.True(
            offline.ProjectBriefUtf8.AsSpan()
                .SequenceEqual(readback.ProjectBriefUtf8),
            "SQLite outage changed exported project-brief recovery.");

        var extraPath = Path.Combine(outputRoot, "unmanifested.json");
        await File.WriteAllTextAsync(extraPath, "{}").ConfigureAwait(false);
        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => CortexLocalRecoveryExportReader.ReadAsync(
                outputRoot,
                limits: null,
                CancellationToken.None).AsTask(),
            "An unmanifested recovery file passed offline validation.")
            .ConfigureAwait(false);
        File.Delete(extraPath);

        var linkPath = Path.Combine(outputRoot, "unmanifested-link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, recoveryBase);
            await LocalAssert.ThrowsAsync<InvalidDataException>(
                () => CortexLocalRecoveryExportReader.ReadAsync(
                    outputRoot,
                    limits: null,
                    CancellationToken.None).AsTask(),
                "A recovery reparse entry passed offline validation.")
                .ConfigureAwait(false);
            Directory.Delete(linkPath);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                PlatformNotSupportedException)
        {
            Console.WriteLine(
                "SKIP export reparse probe: symbolic-link creation is unavailable.");
        }

        var briefPath = Path.Combine(outputRoot, "project-brief.json");
        var originalBrief = await File.ReadAllBytesAsync(briefPath)
            .ConfigureAwait(false);
        var tampered = originalBrief.ToArray();
        tampered[^2] ^= 1;
        await File.WriteAllBytesAsync(briefPath, tampered).ConfigureAwait(false);
        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => CortexLocalRecoveryExportReader.ReadAsync(
                outputRoot,
                limits: null,
                CancellationToken.None).AsTask(),
            "A tampered exported payload passed offline validation.")
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(briefPath, originalBrief)
            .ConfigureAwait(false);
        await WriteRehashedInvalidWorkContinuationAsync(
            outputRoot,
            offline).ConfigureAwait(false);
        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => CortexLocalRecoveryExportReader.ReadAsync(
                outputRoot,
                limits: null,
                CancellationToken.None).AsTask(),
            "A rehashed invalid work-packet continuation passed offline validation.")
            .ConfigureAwait(false);
        await RestoreExactBundleAsync(outputRoot, offline)
            .ConfigureAwait(false);

        await WriteRehashedInvalidTaskRevisionAsync(
            outputRoot,
            offline).ConfigureAwait(false);
        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => CortexLocalRecoveryExportReader.ReadAsync(
                outputRoot,
                limits: null,
                CancellationToken.None).AsTask(),
            "A rehashed invalid task-snapshot revision passed offline validation.")
            .ConfigureAwait(false);
        await RestoreExactBundleAsync(outputRoot, offline)
            .ConfigureAwait(false);

        await WriteRehashedSemanticCorruptionAsync(
            outputRoot,
            offline).ConfigureAwait(false);
        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => CortexLocalRecoveryExportReader.ReadAsync(
                outputRoot,
                limits: null,
                CancellationToken.None).AsTask(),
            "A rehashed semantically malformed brief passed normal recovery validation.")
            .ConfigureAwait(false);
    }

    private static string SubmitTask(
        int id,
        Pegasus.Cortex.Contracts.WorkPackets.AgentTaskSnapshotV1 snapshot) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_task_snapshot",
            LocalMcpProtocol.SubmitArguments(
                snapshot.ProjectLinkId!,
                LocalTestContext.PrimaryNamespace,
                LocalMcpProtocol.AssertionNode(snapshot)));

    private static AgentWorkPacketV1 ContinuePacket(
        AgentWorkPacketV1 source)
    {
        var value = new AgentWorkPacketV1(
            source.SchemaVersion,
            source.ContractId,
            source.Canonicalization,
            source.Mode,
            AgentWorkPacketStageIds.Continue,
            source.WorkId,
            packetRevision: 2,
            source.PacketHash,
            "work-idempotency-2",
            source.ProjectId,
            source.ProjectLinkId,
            source.ContextSetId,
            source.ParentWorkId,
            source.Depth,
            source.MaxFanOut,
            source.IssuedAt.AddMinutes(1),
            source.ExpiresAt,
            source.Objective,
            "The second exact bounded packet revision is ready.",
            source.Constraints,
            source.ApprovalGate,
            source.ExpectedOutput,
            source.ReferenceIds,
            source.EvidenceIds,
            source.RelativeProjectPaths,
            source.CompletedWork,
            source.RemainingWork,
            source.NextAction,
            source.Blockers,
            source.KnownFailures,
            source.SourceRevisions,
            source.Freshness,
            source.Owner,
            source.MaxContextBytes,
            source.CancellationRequested,
            source.Fallback,
            source.Closeout,
            source.ContentTreatment,
            source.Executable,
            source.GrantsSchedulingAuthority,
            source.GrantsTaskClaim,
            source.GrantsAuthority,
            source.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return AgentWorkPacketPolicy.Seal(value);
    }

    private static AgentWorkPacketV1 CopyPacket(
        AgentWorkPacketV1 source,
        int maxFanOut)
    {
        var value = new AgentWorkPacketV1(
            source.SchemaVersion,
            source.ContractId,
            source.Canonicalization,
            source.Mode,
            source.Stage,
            source.WorkId,
            source.PacketRevision,
            source.PreviousPacketHash,
            source.IdempotencyKey,
            source.ProjectId,
            source.ProjectLinkId,
            source.ContextSetId,
            source.ParentWorkId,
            source.Depth,
            maxFanOut,
            source.IssuedAt,
            source.ExpiresAt,
            source.Objective,
            source.CurrentState,
            source.Constraints,
            source.ApprovalGate,
            source.ExpectedOutput,
            source.ReferenceIds,
            source.EvidenceIds,
            source.RelativeProjectPaths,
            source.CompletedWork,
            source.RemainingWork,
            source.NextAction,
            source.Blockers,
            source.KnownFailures,
            source.SourceRevisions,
            source.Freshness,
            source.Owner,
            source.MaxContextBytes,
            source.CancellationRequested,
            source.Fallback,
            source.Closeout,
            source.ContentTreatment,
            source.Executable,
            source.GrantsSchedulingAuthority,
            source.GrantsTaskClaim,
            source.GrantsAuthority,
            source.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return AgentWorkPacketPolicy.Seal(value);
    }

    private static AgentTaskSnapshotV1 CopyTaskSnapshot(
        AgentTaskSnapshotV1 source,
        string snapshotId)
    {
        var value = new AgentTaskSnapshotV1(
            source.SchemaVersion,
            source.ContractId,
            source.Canonicalization,
            source.Mode,
            source.TaskSetId,
            snapshotId,
            source.SnapshotRevision,
            source.PreviousSnapshotHash,
            source.IdempotencyKey,
            source.ProjectId,
            source.ProjectLinkId,
            source.ContextSetId,
            source.CapturedAt,
            source.ExpiresAt,
            source.Producer,
            source.Freshness,
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
            AgentWorkPacketContractIds.EmptySha256);
        return AgentTaskSnapshotPolicy.Seal(value);
    }

    private static string SubmitWork(
        int id,
        Pegasus.Cortex.Contracts.WorkPackets.AgentWorkPacketV1 packet) =>
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

    private static string ProposalCall(
        int id,
        string idempotencyKey,
        DateTimeOffset occurredAt) =>
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_proposal",
            new JsonObject
            {
                ["projectLinkId"] = LocalTestContext.PrimaryProjectLinkId,
                ["idempotencyKey"] = idempotencyKey,
                ["feedbackKind"] = "quality",
                ["title"] = "Bound recovery context",
                ["summary"] = "Retain a private bounded recovery lesson.",
                ["content"] = "Recover from exact verified passive records.",
                ["taxonomy"] = "continuity",
                ["keywords"] = new JsonArray("bounded", "recovery"),
                ["applicability"] = new JsonArray(
                    new JsonObject
                    {
                        ["productId"] = "pegasus-cortex",
                        ["componentId"] = "recovery"
                    }),
                ["occurredAtUtc"] = occurredAt.ToString("O")
            });

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

    private static string[] ImportArguments(
        LocalTestContext context,
        string destination,
        string runId,
        DateTimeOffset capturedAt) =>
    [
        "import-recovery",
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

    private static string SourceFingerprint(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return string.Join(
            ":",
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            File.GetLastWriteTimeUtc(path).Ticks);
    }

    private static async Task WriteRehashedSemanticCorruptionAsync(
        string outputRoot,
        CortexLocalRecoveryExportReadback source)
    {
        var brief = CortexCanonicalJson.Deserialize<CortexProjectBriefSnapshot>(
            source.ProjectBriefUtf8);
        var malformed = brief with
        {
            CurrentTask = brief.CurrentTask with { SelectionRank = 2 }
        };
        malformed = malformed with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(malformed)
        };
        var briefBytes = CortexCanonicalJson.Serialize(malformed);
        var oldEntry = source.Manifest.Entries.Single(entry =>
            entry.Kind == CortexRetainedDocumentKinds.ProjectBrief);
        var newEntry = oldEntry with
        {
            Sha256 = CortexCanonicalJson.Sha256(briefBytes),
            ByteCount = briefBytes.Length
        };
        var entries = source.Manifest.Entries
            .Select(entry => ReferenceEquals(entry, oldEntry) ? newEntry : entry)
            .ToArray();
        await WriteRehashedBundleAsync(
            outputRoot,
            source,
            entries,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [oldEntry.RelativePath] = briefBytes
            }).ConfigureAwait(false);
    }

    private static async Task WriteRehashedInvalidWorkContinuationAsync(
        string outputRoot,
        CortexLocalRecoveryExportReadback source)
    {
        var retained = source.RetainedDocuments.Single(document =>
            document.Kind == CortexRetainedDocumentKinds.WorkPacket &&
            document.Revision == 2);
        var packet = CortexCanonicalJson.Deserialize<AgentWorkPacketV1>(
            retained.CanonicalUtf8);
        var malformed = CopyPacket(
            packet,
            packet.MaxFanOut + 1);
        var bytes = CortexCanonicalJson.Serialize(malformed);
        var oldEntry = source.Manifest.Entries.Single(entry =>
            entry.Kind == retained.Kind &&
            entry.DocumentId == retained.DocumentId &&
            entry.Revision == retained.Revision);
        var newEntry = oldEntry with
        {
            Sha256 = CortexCanonicalJson.Sha256(bytes),
            ByteCount = bytes.Length
        };
        var entries = source.Manifest.Entries
            .Select(entry => ReferenceEquals(entry, oldEntry) ? newEntry : entry)
            .ToArray();
        await WriteRehashedBundleAsync(
            outputRoot,
            source,
            entries,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [oldEntry.RelativePath] = bytes
            }).ConfigureAwait(false);
    }

    private static async Task WriteRehashedInvalidTaskRevisionAsync(
        string outputRoot,
        CortexLocalRecoveryExportReadback source)
    {
        var firstRetained = source.RetainedDocuments.Single(document =>
            document.Kind == CortexRetainedDocumentKinds.TaskSnapshot &&
            document.Revision == 1);
        var secondRetained = source.RetainedDocuments.Single(document =>
            document.Kind == CortexRetainedDocumentKinds.TaskSnapshot &&
            document.Revision == 2);
        var first = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            firstRetained.CanonicalUtf8);
        var second = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            secondRetained.CanonicalUtf8);
        var malformedFirst = CopyTaskSnapshot(
            first,
            second.SnapshotId);
        var bytes = CortexCanonicalJson.Serialize(malformedFirst);
        var firstEntry = source.Manifest.Entries.Single(entry =>
            entry.Kind == firstRetained.Kind &&
            entry.DocumentId == firstRetained.DocumentId &&
            entry.Revision == 1);
        var secondEntry = source.Manifest.Entries.Single(entry =>
            entry.Kind == secondRetained.Kind &&
            entry.DocumentId == secondRetained.DocumentId &&
            entry.Revision == 2);
        var newFirst = firstEntry with
        {
            Sha256 = CortexCanonicalJson.Sha256(bytes),
            ByteCount = bytes.Length
        };
        var newSecond = secondEntry with
        {
            PreviousHash = newFirst.Sha256
        };
        var entries = source.Manifest.Entries
            .Select(entry =>
                ReferenceEquals(entry, firstEntry)
                    ? newFirst
                    : ReferenceEquals(entry, secondEntry)
                        ? newSecond
                        : entry)
            .ToArray();
        await WriteRehashedBundleAsync(
            outputRoot,
            source,
            entries,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [firstEntry.RelativePath] = bytes
            }).ConfigureAwait(false);
    }

    private static async Task WriteRehashedBundleAsync(
        string outputRoot,
        CortexLocalRecoveryExportReadback source,
        IReadOnlyList<CortexLocalRecoveryExportEntry> entries,
        IReadOnlyDictionary<string, byte[]> changedPayloads)
    {
        foreach (var changed in changedPayloads)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(
                    outputRoot,
                    changed.Key.Replace('/', Path.DirectorySeparatorChar)),
                changed.Value).ConfigureAwait(false);
        }
        var exportedBytes = entries.Sum(entry => (long)entry.ByteCount);
        var manifest = source.Manifest with
        {
            Entries = Array.AsReadOnly(entries.ToArray()),
            ExportedBytes = exportedBytes
        };
        var manifestBytes = CortexCanonicalJson.Serialize(manifest);
        var receiptTemplate = source.Receipt with
        {
            ManifestSha256 = CortexCanonicalJson.Sha256(manifestBytes),
            ManifestByteCount = manifestBytes.Length,
            ExportedBytes = exportedBytes,
            ReceiptHash = CortexLocalRecoveryExportContractIds.EmptySha256
        };
        var receipt = receiptTemplate with
        {
            ReceiptHash = CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(receiptTemplate))
        };

        await File.WriteAllBytesAsync(
            Path.Combine(outputRoot, "manifest.json"),
            manifestBytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.Combine(outputRoot, "receipt.json"),
            CortexCanonicalJson.Serialize(receipt)).ConfigureAwait(false);
    }

    private static async Task RestoreExactBundleAsync(
        string outputRoot,
        CortexLocalRecoveryExportReadback source)
    {
        for (var index = 0; index < source.Manifest.Entries.Count; index++)
        {
            var entry = source.Manifest.Entries[index];
            var retained = source.RetainedDocuments[index];
            await File.WriteAllBytesAsync(
                Path.Combine(
                    outputRoot,
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                retained.CanonicalUtf8).ConfigureAwait(false);
        }
        await File.WriteAllBytesAsync(
            Path.Combine(outputRoot, "manifest.json"),
            CortexCanonicalJson.Serialize(source.Manifest)).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.Combine(outputRoot, "receipt.json"),
            CortexCanonicalJson.Serialize(source.Receipt)).ConfigureAwait(false);
    }

    private static string TreeFingerprint(string root) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(root)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                    $"{Path.GetFileName(path)}:{SourceFingerprint(path)}"));

    private static async Task ThrowsAnyAsync(
        Func<Task> action,
        string message)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                UnauthorizedAccessException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(
        Action action,
        string message)
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
        throw new InvalidOperationException(message);
    }
}
