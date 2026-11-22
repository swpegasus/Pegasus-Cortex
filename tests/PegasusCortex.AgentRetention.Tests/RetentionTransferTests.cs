using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;

internal static class RetentionTransferTests
{
    private const string ProjectLinkId =
        "plink_1111111111111111111111111111111111111111111111111111111111111111";
    private const string TenantId = "tenant-1";
    private const string ProjectNamespace = "neural-commander";

    public static async Task BundleSealingAndPortabilityAsync()
    {
        var fixture = await CreateFixtureAsync();
        var canonical = CortexCanonicalJson.Serialize(fixture.Bundle);

        var validated = CortexRetentionTransferBundlePolicy.ReadCanonical(canonical);

        Assert.Equal(fixture.Bundle.BundleHash, validated.BundleHash);
        Assert.Equal(5, validated.AuthoritativeDocuments.Count);
        Assert.Equal(2, validated.DerivedBriefEvidence.Count);
        var json = Encoding.UTF8.GetString(canonical);
        Assert.Contains("+00:00", json);
        Assert.DoesNotContain("D:\\", json);
        Assert.DoesNotContain("/home/", json);
        Assert.DoesNotContain("projects.json", json);
        Assert.DoesNotContain("wal", json);

        var exception = CaptureValidation(() =>
            CortexRetentionTransferBundlePolicy.Validate(
                fixture.Bundle with { BundleId = "transfer-tampered" }));
        Assert.Equal("transfer_bundle_hash_mismatch", exception.Code);
    }

    public static async Task ImportPreservesChainsAndRegeneratesBriefAsync()
    {
        var fixture = await CreateFixtureAsync();
        var target = new FakeAgentRetentionRepository();
        var regenerator = new FakeBriefRegenerator();
        var service = new CortexRetentionTransferImportService(target, regenerator);

        var result = await service.ImportAsync(
            fixture.Bundle,
            AuthorizedScope(),
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(CortexRetentionTransferImportDispositions.Imported, result.Disposition);
        Assert.Equal(fixture.Bundle.AuthoritativeDocuments.Count, result.AppendedCount);
        Assert.Equal(0, result.ExistingCount);
        Assert.Equal(fixture.Bundle.AuthoritativeDocuments.Count, target.DocumentCount);
        Assert.Equal(1, regenerator.CallCount);
        Assert.Equal(2L, regenerator.LatestSnapshotRevision);
        Assert.Equal(2, regenerator.ComparisonEvidenceCount);
        var taskHead = await target.FindHeadAsync(
            "project-1",
            CortexRetainedDocumentKinds.TaskSnapshot,
            "task-set-1",
            CancellationToken.None);
        Assert.Equal(2L, taskHead!.Revision);
        Assert.Equal(
            fixture.Bundle.AuthoritativeDocuments
                .Single(item => item.Kind == CortexRetainedDocumentKinds.TaskSnapshot && item.Revision == 1)
                .ContentHash,
            taskHead.PreviousHash);
        var briefHead = await target.FindHeadAsync(
            ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            ProjectNamespace,
            CancellationToken.None);
        Assert.True(briefHead is null, "Comparison briefs must never be appended as authority.");
    }

    public static async Task AuthorizedScopeMismatchFailsBeforeRepositoryAccessAsync()
    {
        var fixture = await CreateFixtureAsync();
        var target = new FakeAgentRetentionRepository();
        var regenerator = new FakeBriefRegenerator();
        var wrongScope = AuthorizedScope() with
        {
            ProjectNamespace = "different-project"
        };

        var result = await new CortexRetentionTransferImportService(target, regenerator)
            .ImportAsync(fixture.Bundle, wrongScope, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("transfer_authorized_scope_mismatch", result.DiagnosticCode);
        Assert.Equal(0, target.AppendCalls);
        Assert.Equal(0, target.FindRevisionCalls);
        Assert.Equal(0, regenerator.CallCount);
    }

    public static async Task ExactReimportIsIdempotentAsync()
    {
        var fixture = await CreateFixtureAsync();
        var target = new FakeAgentRetentionRepository();
        var regenerator = new FakeBriefRegenerator();
        var service = new CortexRetentionTransferImportService(target, regenerator);
        var first = await service.ImportAsync(
            fixture.Bundle,
            AuthorizedScope(),
            CancellationToken.None);
        var count = target.DocumentCount;

        var replay = await service.ImportAsync(
            fixture.Bundle,
            AuthorizedScope(),
            CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.True(replay.Accepted);
        Assert.Equal(CortexRetentionTransferImportDispositions.Idempotent, replay.Disposition);
        Assert.Equal(0, replay.AppendedCount);
        Assert.Equal(fixture.Bundle.AuthoritativeDocuments.Count, replay.ExistingCount);
        Assert.Equal(count, target.DocumentCount);
        Assert.Equal(2, regenerator.CallCount);
    }

    public static async Task DivergentTargetFailsBeforeWritesAsync()
    {
        var fixture = await CreateFixtureAsync();
        var target = new FakeAgentRetentionRepository();
        var targetService = new CortexAgentRetentionApplicationService(target);
        var divergent = RetentionContractSamples.TaskSnapshot(
            idempotencyKey: "target-divergent-revision-1",
            tasks: [RetentionContractSamples.Task(currentState: "A different source state was retained.")],
            projectLinkId: ProjectLinkId);
        await targetService.SubmitTaskSnapshotAsync(divergent, CancellationToken.None);
        var appendCalls = target.AppendCalls;
        var regenerator = new FakeBriefRegenerator();

        var result = await new CortexRetentionTransferImportService(target, regenerator)
            .ImportAsync(fixture.Bundle, AuthorizedScope(), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("transfer_target_diverged", result.DiagnosticCode);
        Assert.Equal(appendCalls, target.AppendCalls);
        Assert.Equal(1, target.DocumentCount);
        Assert.Equal(0, regenerator.CallCount);
    }

    public static async Task GappedTargetFailsBeforeWritesAsync()
    {
        var fixture = await CreateFixtureAsync();
        var target = new FakeAgentRetentionRepository();
        await new CortexRetentionTransferImportService(target, new FakeBriefRegenerator())
            .ImportAsync(fixture.Bundle, AuthorizedScope(), CancellationToken.None);
        target.RemoveRevision(
            "project-1",
            CortexRetainedDocumentKinds.TaskSnapshot,
            "task-set-1",
            revision: 1);
        var appendCalls = target.AppendCalls;
        var regenerator = new FakeBriefRegenerator();

        var result = await new CortexRetentionTransferImportService(target, regenerator)
            .ImportAsync(fixture.Bundle, AuthorizedScope(), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("transfer_target_predecessor_missing", result.DiagnosticCode);
        Assert.Equal(appendCalls, target.AppendCalls);
        Assert.Equal(0, regenerator.CallCount);
    }

    public static async Task ExtraTargetHeadFailsBeforeWritesAsync()
    {
        var fixture = await CreateFixtureAsync();
        var target = new FakeAgentRetentionRepository();
        var extraPacket = RetentionContractSamples.WorkPacket(
            idempotencyKey: "extra-packet-idempotency",
            projectLinkId: ProjectLinkId);
        // The immutable record does not expose a copy-with WorkId operation, so
        // use a second task chain to exercise the same complete-set boundary.
        var extraTask = new AgentTaskSnapshotV1(
            extraPacket.SchemaVersion,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            "extra-task-set",
            "extra-snapshot-1",
            1,
            previousSnapshotHash: null,
            "extra-task-idempotency",
            "project-1",
            ProjectLinkId,
            extraPacket.ContextSetId,
            extraPacket.IssuedAt,
            extraPacket.ExpiresAt,
            extraPacket.Owner,
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Current,
                extraPacket.IssuedAt,
                extraPacket.ExpiresAt,
                "The source is current for this bounded test."),
            extraPacket.SourceRevisions,
            evidenceReferences: [],
            AgentTaskSnapshotContractIds.Coverage,
            [RetentionContractSamples.Task()],
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        extraTask = AgentTaskSnapshotPolicy.Seal(extraTask);
        await new CortexAgentRetentionApplicationService(target)
            .SubmitTaskSnapshotAsync(extraTask, CancellationToken.None);
        var appendCalls = target.AppendCalls;

        var result = await new CortexRetentionTransferImportService(
                target,
                new FakeBriefRegenerator())
            .ImportAsync(fixture.Bundle, AuthorizedScope(), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("transfer_target_extra_head", result.DiagnosticCode);
        Assert.Equal(appendCalls, target.AppendCalls);
    }

    public static async Task AbsoluteMachinePathsAreRejectedAsync()
    {
        var source = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(source);
        var snapshot = RetentionContractSamples.TaskSnapshot(
            tasks:
            [
                RetentionContractSamples.Task(
                    currentState: "Inspect D:\\private-host\\state before migration.")
            ],
            projectLinkId: ProjectLinkId);
        await service.SubmitTaskSnapshotAsync(snapshot, CancellationToken.None);
        var project = ProjectScope(snapshot);

        var exception = CaptureValidation(() =>
            CortexRetentionTransferBundleFactory.Create(
                "transfer-path-rejected",
                snapshot.CapturedAt.AddMinutes(5),
                project,
                Provenance(),
                source.SnapshotDocuments()));

        Assert.Equal("transfer_bundle_absolute_path_prohibited", exception.Code);
    }

    private static async Task<TransferFixture> CreateFixtureAsync()
    {
        var source = new FakeAgentRetentionRepository();
        var service = new CortexAgentRetentionApplicationService(source);
        var task1 = RetentionContractSamples.TaskSnapshot(projectLinkId: ProjectLinkId);
        var task2 = RetentionContractSamples.TaskSnapshot(
            revision: 2,
            previousHash: task1.SnapshotHash,
            projectLinkId: ProjectLinkId);
        await service.SubmitTaskSnapshotAsync(task1, CancellationToken.None);
        await service.SubmitTaskSnapshotAsync(task2, CancellationToken.None);

        var packet = RetentionContractSamples.WorkPacket(projectLinkId: ProjectLinkId);
        await service.SubmitWorkPacketAsync(packet, CancellationToken.None);
        var intendedOwner = new AgentWorkOwnerV1(
            "nexus-authority",
            "coder-agent-2",
            "pegasus-coder");
        var handoff = AgentWorkPacketPolicy.Seal(new AgentWorkPacketHandoffReceiptV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.HandoffReceipt,
            AgentWorkPacketContractIds.Canonicalization,
            "handoff-1",
            packet.WorkId,
            packet.IdempotencyKey,
            packet.Stage,
            packet.PacketRevision,
            packet.PacketHash,
            packet.Owner,
            intendedOwner,
            packet.IssuedAt.AddMinutes(10),
            packet.ExpiresAt,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentWorkPacketContractIds.EmptySha256));
        await service.SubmitWorkPacketHandoffReceiptAsync(
            "project-1",
            handoff,
            CancellationToken.None);
        var acceptance = AgentWorkPacketPolicy.CreateAcceptanceReceipt(
            handoff,
            "acceptance-1",
            AgentWorkAcceptanceDispositionIds.Accepted,
            intendedOwner,
            packet.IssuedAt.AddMinutes(20),
            "The exact passive packet bytes were accepted for retention.");
        await service.SubmitWorkPacketAcceptanceReceiptAsync(
            "project-1",
            acceptance,
            CancellationToken.None);

        var scope = ProjectScope(task2);
        var readScope = new CortexProjectBriefReadScope(
            TenantId,
            ProjectLinkId,
            "project-1",
            ProjectNamespace,
            "nexus-authority",
            "operator-1",
            "pegasus-cortex",
            PolicyRevision: 1);
        var reference = new CortexProjectBriefReference(
            "checkpoint://current",
            new string('c', 64),
            RecordId: null,
            RevisionId: null);
        var input = new CortexProjectBriefProjectionInput(
            "project-1",
            readScope,
            [reference],
            [],
            SkippedSourceChainProof: null);
        var brief1 = CortexProjectBriefProjector.Project(task1, input);
        var brief2 = CortexProjectBriefProjector.Project(task2, input, brief1);
        var briefDocuments = new[]
        {
            BriefDocument(brief1, previousHash: null),
            BriefDocument(
                brief2,
                CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(brief1)))
        };
        var bundle = CortexRetentionTransferBundleFactory.Create(
            "transfer-neural-commander-1",
            acceptance.AcceptedAt.AddMinutes(5),
            scope,
            Provenance(),
            source.SnapshotDocuments(),
            briefDocuments);
        return new TransferFixture(bundle);
    }

    private static CortexRetentionTransferProjectScope ProjectScope(
        AgentTaskSnapshotV1 snapshot) => new(
            TenantId,
            ProjectLinkId,
            snapshot.ProjectId,
            ProjectNamespace,
            CortexProjectBriefService.ComputeAuthoritySetHash(snapshot.SourceRevisions));

    private static CortexRetentionTransferImportScope AuthorizedScope() => new(
        TenantId,
        ProjectLinkId,
        "project-1",
        ProjectNamespace);

    private static CortexRetentionTransferProvenance Provenance() => new(
        "sqlite",
        SourceStoreEpoch: 1,
        "pegasus-cortex-operator",
        [new string('d', 64), new string('e', 64)]);

    private static CortexRetainedDocument BriefDocument(
        CortexProjectBriefSnapshot brief,
        string? previousHash)
    {
        var canonical = CortexCanonicalJson.Serialize(brief);
        return new CortexRetainedDocument(
            brief.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            brief.ProjectNamespace,
            brief.BriefRevision,
            previousHash,
            $"brief-{brief.BriefRevision}",
            "projector:tests",
            brief.ContractId,
            canonical,
            CortexCanonicalJson.Sha256(canonical),
            "passive project brief comparison evidence",
            brief.Freshness.ObservedAt,
            brief.Freshness.ReviewAfter!.Value);
    }

    private static CortexRetentionTransferValidationException CaptureValidation(
        Action action)
    {
        try
        {
            action();
        }
        catch (CortexRetentionTransferValidationException exception)
        {
            return exception;
        }
        throw new InvalidOperationException(
            $"Expected {nameof(CortexRetentionTransferValidationException)}.");
    }

    private sealed record TransferFixture(CortexRetentionTransferBundleV1 Bundle);

    private sealed class FakeBriefRegenerator : ICortexRetentionTransferBriefRegenerator
    {
        public int CallCount { get; private set; }
        public long LatestSnapshotRevision { get; private set; }
        public int ComparisonEvidenceCount { get; private set; }

        public ValueTask<CortexRetentionTransferBriefRegenerationResult> RegenerateAsync(
            CortexRetentionTransferProjectScope project,
            AgentTaskSnapshotV1 authoritativeTaskSnapshot,
            IReadOnlyList<CortexRetentionTransferDocument> comparisonEvidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LatestSnapshotRevision = authoritativeTaskSnapshot.SnapshotRevision;
            ComparisonEvidenceCount = comparisonEvidence.Count;
            return ValueTask.FromResult(new CortexRetentionTransferBriefRegenerationResult(
                Accepted: true,
                "regenerated",
                $"ctx_{new string('f', 64)}",
                authoritativeTaskSnapshot.SnapshotHash,
                project.AuthoritySetHash,
                comparisonEvidence.LastOrDefault()?.ContentHash,
                DiagnosticCode: null));
        }
    }
}
