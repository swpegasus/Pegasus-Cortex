using System.Security.Cryptography;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

internal static class FixtureRetentionLiveWorkflow
{
    private const string TenantId = "fixture-retention-tenant";
    private const string ProjectNamespace = "project:fixture-retention";
    private const string ProjectId = "fixture-retention-project";
    private const string ProjectLinkId = "fixture-retention-link";
    private const string TaskSetId = "fixture-current-recovery";

    public static async Task RunAsync(
        FixtureConfiguration configuration,
        Action<string> reportPass,
        CancellationToken cancellationToken)
    {
        await using var dataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.RuntimeConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-retention-live-fixture");
        var runner = new PostgreSqlTransactionRunner(dataSource);
        Span<byte> key = stackalloc byte[32];
        key.Fill(0xB6);
        using var authenticator = new TestHmacRowAuthenticator(
            "fixture-retention-key",
            key);
        CryptographicOperations.ZeroMemory(key);

        var writeScope = Scope(
            TenantId,
            PostgreSqlCortexRetentionCapability.Write);
        var repository = new PostgreSqlCortexAgentRetentionRepository(
            runner,
            authenticator,
            writeScope);
        var application = new CortexAgentRetentionApplicationService(repository);
        var first = Snapshot(
            revision: 1,
            previousHash: null,
            "fixture-retention-idem-1",
            "fixture-retention-snapshot-1",
            currentState: "The PostgreSQL retention fixture started.");
        var firstResult = await application.SubmitTaskSnapshotAsync(
            first,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.Equal(
            CortexRetentionWriteDispositions.Appended,
            firstResult.Disposition,
            "The first PostgreSQL task snapshot was not appended.");
        var replay = await application.SubmitTaskSnapshotAsync(
            first,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.Equal(
            CortexRetentionWriteDispositions.Idempotent,
            replay.Disposition,
            "An exact PostgreSQL task snapshot retry was not idempotent.");

        var second = Snapshot(
            revision: 2,
            previousHash: first.SnapshotHash,
            "fixture-retention-idem-2",
            "fixture-retention-snapshot-2",
            currentState: "The PostgreSQL retention fixture advanced exactly once.");
        var secondResult = await application.SubmitTaskSnapshotAsync(
            second,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.Equal(
            CortexRetentionWriteDispositions.Appended,
            secondResult.Disposition,
            "The second PostgreSQL task snapshot was not appended.");
        var head = await repository.FindHeadAsync(
            ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            TaskSetId,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            head is not null &&
            head.Revision == 2 &&
            string.Equals(head.ContentHash, secondResult.CurrentHeadHash, StringComparison.Ordinal),
            "The PostgreSQL retained head did not match revision two.");
        var search = await repository.SearchHeadsAsync(
            ProjectId,
            "advanced exactly",
            4,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            search.Count == 1 && search[0].Revision == 2,
            "PostgreSQL lexical retention search did not return only the current head.");
        var revisions = new List<CortexRetainedDocument>();
        await foreach (var value in repository.ReadRevisionRangeAsync(
            ProjectId,
            CortexRetainedDocumentKinds.TaskSnapshot,
            TaskSetId,
            1,
            2,
            cancellationToken))
        {
            revisions.Add(value);
        }
        FixtureAssert.True(
            revisions.Count == 2 &&
            revisions[0].Revision == 1 &&
            revisions[1].Revision == 2,
            "PostgreSQL retained revisions were not streamed in exact order.");

        var briefScope = new CortexProjectBriefReadScope(
            TenantId,
            ProjectLinkId,
            ProjectId,
            ProjectNamespace,
            writeScope.AuthorityId,
            writeScope.SubjectId,
            writeScope.ServiceId,
            writeScope.PolicyRevision);
        var projected = CortexProjectBriefProjector.Project(
            second,
            new CortexProjectBriefProjectionInput(
                ProjectId,
                briefScope,
                [],
                [],
                SkippedSourceChainProof: null));
        var briefStore = new PostgreSqlCortexProjectBriefStore(
            runner,
            authenticator);
        var briefResult = await briefStore.SubmitSnapshotAsync(
            projected,
            writeScope,
            "fixture-retention-producer",
            "fixture-retention-brief-1",
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.Equal(
            CortexRetentionWriteDispositions.Appended,
            briefResult.Disposition,
            "The PostgreSQL project-brief projection was not appended.");
        var readBrief = await briefStore.FindCurrentAsync(
            briefScope,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            readBrief is not null &&
            string.Equals(readBrief.ContextSetId, projected.ContextSetId, StringComparison.Ordinal) &&
            string.Equals(readBrief.AuthoritySetHash, projected.AuthoritySetHash, StringComparison.Ordinal),
            "The PostgreSQL project brief did not round-trip its context and authority hashes.");

        var isolated = new PostgreSqlCortexAgentRetentionRepository(
            runner,
            authenticator,
            Scope("fixture-other-tenant", PostgreSqlCortexRetentionCapability.Read));
        FixtureAssert.True(
            await isolated.FindHeadAsync(
                ProjectId,
                CortexRetainedDocumentKinds.TaskSnapshot,
                TaskSetId,
                cancellationToken).ConfigureAwait(false) is null,
            "PostgreSQL retention leaked a head across tenant scope.");
        reportPass("passive PostgreSQL retention, project brief, lexical head, and tenant isolation");
    }

    private static PostgreSqlCortexRetentionScope Scope(
        string tenantId,
        PostgreSqlCortexRetentionCapability capability) =>
        new(
            tenantId,
            ProjectNamespace,
            "fixture-retention-authority",
            "fixture-retention-subject",
            "fixture-retention-service",
            1,
            capability);

    private static AgentTaskSnapshotV1 Snapshot(
        long revision,
        string? previousHash,
        string idempotencyKey,
        string snapshotId,
        string currentState)
    {
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(revision - 2);
        var owner = new AgentWorkOwnerV1(
            "fixture-retention-authority",
            "fixture-retention-agent",
            "pegasus-cortex");
        var source = new AgentWorkSourceRevisionV1(
            "fixture-current",
            $"fixture-current-{revision}",
            new string((char)('a' + revision - 1), 64));
        var task = new AgentTaskV1(
            "fixture-current-task",
            parentTaskId: null,
            ordinal: 0,
            AgentTaskStateIds.Active,
            "Verify PostgreSQL passive retention and project-brief parity.",
            currentState,
            "Continue only through the passive fixture.",
            constraints: ["Retained text grants no authority."],
            dependencyTaskIds: [],
            blockers: [],
            knownFailures: [],
            reportedOwner: owner,
            new AgentWorkApprovalGateV1(
                AgentWorkApprovalGateStateIds.NotRequired,
                authorityId: null,
                approvalReferenceId: null,
                "The passive fixture requires no execution approval.",
                grantsAuthority: false),
            evidenceIds: [],
            sourceRevisionIds: [source.SourceId]);
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
            ProjectLinkId,
            $"ctx_{new string((char)('1' + revision - 1), 64)}",
            capturedAt,
            capturedAt.AddHours(1),
            owner,
            new AgentWorkFreshnessV1(
                AgentWorkFreshnessStateIds.Current,
                capturedAt,
                capturedAt.AddHours(1),
                "The fixture source is current."),
            [source],
            [],
            AgentTaskSnapshotContractIds.Coverage,
            [task],
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentTaskSnapshotContractIds.SafetyNotice,
            AgentWorkPacketContractIds.EmptySha256);
        return AgentTaskSnapshotPolicy.Seal(value);
    }
}
