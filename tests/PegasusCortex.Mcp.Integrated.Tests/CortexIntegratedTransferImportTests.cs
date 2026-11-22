using System.Security.Cryptography;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Integrated.Recovery;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Mcp.Knowledge;

internal static class CortexIntegratedTransferImportTests
{
    private const string TenantId = "tenant-1";
    private const string ProjectId = "project-1";
    private const string ProjectNamespace = "neural-commander";
    private const string ProjectLinkId =
        "plink_1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-08-02T16:00:00+00:00");

    internal static Task BundlePathHashAndCanonicalityAsync()
    {
        var bundle = Bundle();
        var canonical = CortexCanonicalJson.Serialize(bundle);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cortex-transfer-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(path, canonical);
            Protect(path);
            var fileHash = Convert.ToHexString(SHA256.HashData(canonical))
                .ToLowerInvariant();
            var parsed = CortexIntegratedProjectTransferImportArguments.Parse(
            [
                "import-project-transfer",
                "--bundle", Path.GetFullPath(path),
                "--expected-sha256", fileHash,
                "--tenant", TenantId,
                "--project-link", ProjectLinkId,
                "--project-id", ProjectId,
                "--namespace", ProjectNamespace
            ]);
            var read = CortexIntegratedProjectTransferBundleReader.Read(
                parsed.BundlePath,
                parsed.ExpectedBundleFileSha256);
            Assert.Equal(bundle.BundleHash, read.Bundle.BundleHash);
            Assert.Equal(fileHash, read.FileSha256);

            var mismatch = CaptureValidation(() =>
                CortexIntegratedProjectTransferBundleReader.Read(
                    parsed.BundlePath,
                    new string('f', 64)));
            Assert.Equal("transfer_bundle_file_hash_mismatch", mismatch.Code);
            Assert.Throws<ArgumentException>(() =>
                CortexIntegratedProjectTransferImportArguments.Parse(
                [
                    "import-project-transfer",
                    "--bundle", "relative.json",
                    "--expected-sha256", fileHash,
                    "--tenant", TenantId,
                    "--project-link", ProjectLinkId,
                    "--project-id", ProjectId,
                    "--namespace", ProjectNamespace
                ]));
            Assert.Throws<ArgumentException>(() =>
                CortexIntegratedProjectTransferImportArguments.Parse(
                [
                    "import-project-transfer",
                    "--bundle", Path.GetFullPath(path),
                    "--expected-sha256", fileHash.ToUpperInvariant(),
                    "--tenant", TenantId,
                    "--project-link", ProjectLinkId,
                    "--project-id", ProjectId,
                    "--namespace", ProjectNamespace
                ]));
            return Task.CompletedTask;
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static async Task ServerProjectAuthorizationAsync()
    {
        var authorityPath = Path.Combine(
            Path.GetTempPath(),
            $"cortex-authority-{Guid.NewGuid():N}.json");
        var prior = Environment.GetEnvironmentVariable("PEGASUS_CORTEX_AUTHORITY_FILE");
        try
        {
            var principal = Environment.UserName;
            var json = $$"""
                {"schemaVersion":4,"tenantId":"{{TenantId}}","endpointId":"endpoint-1","policyRevision":1,"principals":[{"name":"{{principal}}","read":false,"correlate":false,"propose":false,"review":false,"audit":false,"delegateReview":false,"subjectId":"operator-1","autoPublishSafeCollectiveProposals":false,"retainedRead":true,"retainedWrite":true,"projectBrief":true,"sync":false}]}
                """;
            File.WriteAllText(authorityPath, json);
            Protect(authorityPath);
            var authority = CortexLocalAuthority.Load(authorityPath);
            var metadata = new CortexProjectCorrelationMetadataV1(
                CortexProjectCorrelationContract.SchemaVersion,
                CortexProjectCorrelationContract.ContractId,
                ProjectId,
                "store-1",
                1,
                ProjectLinkId,
                ProjectNamespace,
                "endpoint-1",
                "1.0");
            var link = new CortexProjectLinkV1(metadata, new string('a', 64));
            var repository = new FakeProjectLinkRepository(link);
            var options = Options(authorityPath);

            var accepted = await CortexIntegratedProjectTransferImportCommand.AuthorizeProjectAsync(
                options,
                authority,
                repository,
                CancellationToken.None);
            Assert.Equal(ProjectLinkId, accepted.Metadata.ProjectLinkId);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CortexIntegratedProjectTransferImportCommand.AuthorizeProjectAsync(
                    options with { ProjectNamespace = "wrong-project" },
                    authority,
                    repository,
                    CancellationToken.None).AsTask());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PEGASUS_CORTEX_AUTHORITY_FILE", prior);
            File.Delete(authorityPath);
        }
    }

    internal static async Task BriefRegenerationReadbackAsync()
    {
        var source = Snapshot();
        var project = ProjectScope(source);
        var scope = ReadScope();
        var retainedContentHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(source));
        var reference = CortexProjectBriefReferencePolicy.CreateRetained(
            ProjectLinkId,
            ProjectId,
            ProjectNamespace,
            CortexRetainedDocumentKinds.TaskSnapshot,
            source.TaskSetId,
            source.SnapshotRevision,
            retainedContentHash,
            source.SnapshotHash);
        var brief = CortexProjectBriefProjector.Project(
            source,
            new CortexProjectBriefProjectionInput(
                ProjectId,
                scope,
                [reference],
                [],
                SkippedSourceChainProof: null));
        var writer = new FakeRetainedWriter(brief.ContextSetId);
        var regenerator = new CortexIntegratedTransferBriefRegenerator(
            writer,
            new FixedBriefReadModel(brief),
            scope);

        var result = await regenerator.RegenerateAsync(
            project,
            source,
            [],
            CancellationToken.None);
        Assert.True(result.Accepted, "A matching persisted brief was rejected.");
        Assert.True(regenerator.LastReadback?.Verified == true,
            "The final persisted brief was not marked verified.");
        Assert.Equal(source.SnapshotHash, result.SourceHash);
        Assert.Equal(project.AuthoritySetHash, result.AuthoritySetHash);

        var changed = brief with
        {
            CurrentTask = brief.CurrentTask with { Title = "changed after projection" }
        };
        var rejected = await new CortexIntegratedTransferBriefRegenerator(
                writer,
                new FixedBriefReadModel(changed),
                scope)
            .RegenerateAsync(project, source, [], CancellationToken.None);
        Assert.False(rejected.Accepted, "A changed current-task field passed readback.");
        Assert.Equal("transfer_brief_readback_mismatch", rejected.DiagnosticCode);
    }

    private static CortexIntegratedProjectTransferImportArguments Options(string path) => new(
        Path.GetFullPath(path),
        new string('a', 64),
        TenantId,
        ProjectLinkId,
        ProjectId,
        ProjectNamespace);

    private static CortexRetentionTransferBundleV1 Bundle()
    {
        var snapshot = Snapshot();
        var canonical = CortexCanonicalJson.Serialize(snapshot);
        var producer = $"agent:{CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(snapshot.Producer))}";
        var search = string.Join(
            " | ",
            "treatment: passive untrusted reference data; no scheduling, claim, approval, or execution authority",
            $"task state: {snapshot.Tasks[0].ReportedState}",
            $"task objective: {snapshot.Tasks[0].Objective}",
            $"task current state: {snapshot.Tasks[0].CurrentState}",
            $"task next action: {snapshot.Tasks[0].NextAction}");
        var document = new CortexRetainedDocument(
            ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            snapshot.TaskSetId,
            snapshot.SnapshotRevision,
            snapshot.PreviousSnapshotHash,
            snapshot.IdempotencyKey,
            producer,
            snapshot.ContractId,
            canonical,
            CortexCanonicalJson.Sha256(canonical),
            search,
            snapshot.CapturedAt,
            snapshot.ExpiresAt);
        return CortexRetentionTransferBundleFactory.Create(
            "transfer-integrated-test",
            CapturedAt.AddMinutes(1),
            ProjectScope(snapshot),
            new CortexRetentionTransferProvenance(
                "sqlite",
                1,
                "pegasus-cortex-test",
                [document.ContentHash]),
            [document],
            []);
    }

    private static AgentTaskSnapshotV1 Snapshot()
    {
        var owner = new AgentWorkOwnerV1(
            "nexus-authority",
            "cortex-agent-1",
            "pegasus-cortex");
        var gate = new AgentWorkApprovalGateV1(
            AgentWorkApprovalGateStateIds.NotRequired,
            authorityId: null,
            approvalReferenceId: null,
            "No approval state is asserted as required.",
            grantsAuthority: false);
        var task = new AgentTaskV1(
            "task-1",
            parentTaskId: null,
            ordinal: 0,
            AgentTaskStateIds.Active,
            "Recover the project.",
            "The project is ready for transfer.",
            "Import the retained chain.",
            constraints: [],
            dependencyTaskIds: [],
            blockers: [],
            knownFailures: [],
            reportedOwner: null,
            gate,
            evidenceIds: [],
            sourceRevisionIds: ["source-1"]);
        var value = new AgentTaskSnapshotV1(
            AgentTaskSnapshotContractIds.SchemaVersion,
            AgentTaskSnapshotContractIds.Snapshot,
            AgentWorkPacketContractIds.Canonicalization,
            AgentCoordinationModeIds.Standalone,
            "task-set-1",
            "snapshot-1",
            1,
            previousSnapshotHash: null,
            "task-idempotency-1",
            ProjectId,
            ProjectLinkId,
            $"ctx_{new string('1', 64)}",
            CapturedAt,
            CapturedAt.AddHours(24),
            owner,
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Current,
                CapturedAt,
                CapturedAt.AddHours(24),
                "The source revision was verified."),
            [new AgentWorkSourceRevisionV1(
                "source-1",
                "revision-1",
                new string('b', 64))],
            evidenceReferences: [],
            AgentTaskSnapshotContractIds.Coverage,
            tasks: [task],
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return AgentTaskSnapshotPolicy.Seal(value);
    }

    private static CortexRetentionTransferProjectScope ProjectScope(
        AgentTaskSnapshotV1 source) => new(
        TenantId,
        ProjectLinkId,
        ProjectId,
        ProjectNamespace,
        CortexProjectBriefService.ComputeAuthoritySetHash(source.SourceRevisions));

    private static CortexProjectBriefReadScope ReadScope() => new(
        TenantId,
        ProjectLinkId,
        ProjectId,
        ProjectNamespace,
        "local-cortex-policy",
        "operator-1",
        "pegasus-cortex-transfer-import",
        1);

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

    private static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class FakeProjectLinkRepository(CortexProjectLinkV1 link) :
        ICortexProjectLinkRepository
    {
        public ValueTask<CortexProjectLinkV1?> FindAsync(
            CortexProjectLinkAccess access,
            string projectLinkId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<CortexProjectLinkV1?>(
                string.Equals(projectLinkId, link.Metadata.ProjectLinkId, StringComparison.Ordinal)
                    ? link
                    : null);

        public ValueTask<IReadOnlyList<CortexProjectLinkV1>> ListAsync(
            CortexProjectLinkAccess access,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CortexProjectLinkWriteResult> AddAsync(
            CortexProjectLinkAccess access,
            CortexProjectLinkV1 value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedBriefReadModel(CortexProjectBriefSnapshot value) :
        ICortexProjectBriefReadModel
    {
        public ValueTask<CortexProjectBriefSnapshot?> FindCurrentAsync(
            CortexProjectBriefReadScope scope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<CortexProjectBriefSnapshot?>(value);

        public ValueTask<CortexProjectBriefSnapshot?> FindAsync(
            CortexProjectBriefReadScope scope,
            string contextSetId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<CortexProjectBriefSnapshot?>(
                string.Equals(value.ContextSetId, contextSetId, StringComparison.Ordinal)
                    ? value
                    : null);
    }

    private sealed class FakeRetainedWriter(string contextSetId) :
        ICortexRetainedAssertionWriterRuntime
    {
        public ValueTask<CortexRetainedAssertionWriteResult?> SubmitTaskSnapshotAsync(
            string projectLinkId,
            string projectNamespace,
            AgentTaskSnapshotV1 snapshot,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<CortexRetainedAssertionWriteResult?>(new(
                CortexRetentionWriteDispositions.Idempotent,
                projectLinkId,
                snapshot.ProjectId,
                projectNamespace,
                CortexRetainedDocumentKinds.TaskSnapshot,
                snapshot.TaskSetId,
                snapshot.SnapshotRevision,
                snapshot.SnapshotHash,
                CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(snapshot)),
                CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(snapshot)),
                CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(snapshot)),
                CortexRetentionWriteDispositions.Idempotent,
                contextSetId,
                ProjectionIssueCode: null));

        public ValueTask<CortexRetainedAssertionWriteResult?> SubmitWorkPacketAsync(
            string projectLinkId,
            string projectNamespace,
            AgentWorkPacketV1 packet,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CortexRetainedAssertionWriteResult?>
            SubmitWorkPacketHandoffReceiptAsync(
                string projectLinkId,
                string projectId,
                string projectNamespace,
                AgentWorkPacketHandoffReceiptV1 receipt,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CortexRetainedAssertionWriteResult?>
            SubmitWorkPacketAcceptanceReceiptAsync(
                string projectLinkId,
                string projectId,
                string projectNamespace,
                AgentWorkPacketAcceptanceReceiptV1 receipt,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
