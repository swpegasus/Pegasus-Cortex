using System.Text.Json;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

internal static class CortexMcpSecurityTests
{
    public static Task ProtectedAuthorityAndRowKeyAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pegasus-cortex-security-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var authorityPath = Path.Combine(root, "authority.json");
            var keyPath = Path.Combine(root, "row-key.hex");
            File.WriteAllText(
                authorityPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 4,
                    tenantId = "local",
                    endpointId = "pegasus-cortex-test",
                    policyRevision = 7,
                    principals = new[]
                    {
                        new
                        {
                            name = Environment.UserName,
                            read = true,
                            correlate = true,
                            propose = true,
                            review = true,
                            audit = true,
                            delegateReview = true,
                            subjectId = "stable-test-subject",
                            autoPublishSafeCollectiveProposals = true,
                            retainedRead = true,
                            retainedWrite = true,
                            projectBrief = true,
                            sync = true
                        }
                    }
                }));
            File.WriteAllText(keyPath, new string('a', 64));

            var authority = CortexLocalAuthority.Load(authorityPath);
            Assert.Equal("pegasus-cortex-test", authority.EndpointId);
            var submit = authority.LearningAuthority(
                "project-test",
                LearningFeedbackNamespaceCapability.Submit,
                $"screen_{new string('b', 64)}",
                new string('c', 64));
            var review = authority.LearningAuthority(
                "project-test",
                LearningFeedbackNamespaceCapability.Review);
            Assert.True(
                string.Equals(submit.SubjectId, review.SubjectId, StringComparison.Ordinal),
                "One local principal did not retain a stable cross-capability subject.");
            Assert.Equal("stable-test-subject", authority.SubjectId);
            Assert.True(
                authority.AutoPublishSafeCollectiveProposals,
                "The configured provisional-publication policy was not loaded.");
            var provisional = authority.ProvisionalReviewAuthority("project-test");
            Assert.Equal(
                LearningFeedbackPublicationPolicy.ProvisionalAuthorityId,
                provisional.AuthorityId);
            Assert.Equal(
                LearningFeedbackPublicationPolicy.ProvisionalReviewerSubjectId,
                provisional.SubjectId);
            Assert.True(
                provisional.NamespaceGrants.Single().CanReview,
                "The server-owned provisional identity did not receive its narrow review scope.");
            Assert.False(
                string.Equals(provisional.SubjectId, submit.SubjectId, StringComparison.Ordinal),
                "Provisional publication reused the proposal submitter identity.");
            Assert.True(
                authority.HasCapability(CortexLocalCapability.Audit),
                "The configured auditor capability was not loaded.");
            Assert.True(
                authority.HasCapability(CortexLocalCapability.RetainedRead) &&
                authority.HasCapability(CortexLocalCapability.RetainedWrite) &&
                authority.HasCapability(CortexLocalCapability.ProjectBrief) &&
                authority.HasCapability(CortexLocalCapability.Sync),
                "The protected retention and synchronization capabilities were not loaded.");
            Assert.True(
                authority.SyncAuthority(
                    "project-test",
                    [CortexSyncContractIds.LegacyMigrationRecord])
                    .Grants.Single().CanSync,
                "The protected synchronization grant was not emitted.");
            Assert.Equal(
                CortexProjectLinkCapability.Correlate,
                authority.ProjectLinkAccess(CortexProjectLinkCapability.Correlate).Capability);

            using var authenticator = HmacSha256CortexRowAuthenticator.Load(
                keyPath,
                "test-key-v1");
            var envelope = CortexRowAuthentication.CreateEnvelope(
                "project-link",
                "local",
                CortexProjectCorrelationContract.ControlNamespace,
                $"plink_{new string('d', 64)}",
                revisionId: null,
                new string('e', 64));
            var seal = authenticator.Seal(envelope);
            Assert.True(authenticator.Verify(envelope, seal), "A fresh row seal did not verify.");
            var tampered = CortexRowAuthentication.CreateEnvelope(
                "project-link",
                "local",
                CortexProjectCorrelationContract.ControlNamespace,
                $"plink_{new string('f', 64)}",
                revisionId: null,
                new string('e', 64));
            Assert.False(
                authenticator.Verify(tampered, seal),
                "A row seal verified for a different project-link identity.");

            File.WriteAllText(
                authorityPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 3,
                    tenantId = "local",
                    endpointId = "pegasus-cortex-test",
                    policyRevision = 7,
                    principals = new[]
                    {
                        new
                        {
                            name = Environment.UserName,
                            read = false,
                            correlate = false,
                            propose = true,
                            review = false,
                            subjectId = "stable-test-subject",
                            autoPublishSafeCollectiveProposals = true
                        }
                    }
                }));
            Assert.Throws<InvalidOperationException>(
                () => CortexLocalAuthority.Load(authorityPath));
            return Task.CompletedTask;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static async Task ProtectedReceiptAuthorityAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pegasus-cortex-receipt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var keyPath = Path.Combine(root, "receipt-key.hex");
            File.WriteAllText(keyPath, new string('b', 64));
            using var issuer = HmacSha256CortexSyncReceiptIssuer.Load(
                keyPath,
                "receipt-key-v1",
                "server-test-v1");

            var now = DateTimeOffset.Parse("2026-08-03T06:00:00+00:00");
            var bundle = CreateBundle(now);
            var outcome = new CortexSyncReceiptOutcomeV1(
                1,
                bundle.Events.Single().EventId,
                bundle.Events.Single().EventHash,
                CortexSyncDispositionIds.Applied,
                durable: true,
                "retained-entity-1",
                "retained-revision-1",
                diagnosticCode: null);
            var durableHash = new string('d', 64);
            var commit = new CortexSyncCommitSnapshot(
                CortexSyncCommitDisposition.Committed,
                "commit-1",
                "storage-revision-1",
                now,
                now.AddSeconds(1),
                durableHash,
                [outcome]);
            var issuance = new CortexSyncReceiptIssuance(
                new CortexSyncRepositoryScope(
                    "tenant-1",
                    "project:project-1",
                    "authority-1",
                    "subject-1",
                    "service-1",
                    1,
                    bundle.StoreId,
                    bundle.StoreEpoch,
                    bundle.ProjectId,
                    "cortex.sync.submit"),
                bundle,
                now,
                commit,
                new CortexSyncReceiptReconciliationV1(
                    "reconciled",
                    1,
                    1,
                    now.AddSeconds(2),
                    durableHash));

            var receipt = await issuer.IssueAsync(issuance, CancellationToken.None);
            Assert.True(
                await issuer.VerifyProofAsync(receipt, CancellationToken.None),
                "A freshly issued production receipt proof did not verify.");
            Assert.Equal("server-test-v1", receipt.ServerInstanceId);
            Assert.Equal(
                HmacSha256CortexSyncReceiptIssuer.ProofContractId,
                receipt.Authentication.ProofContractId);

            var changedProof = new byte[32];
            var changedAuthentication = new CortexSyncReceiptAuthenticationV1(
                receipt.Authentication.ProofContractId,
                receipt.Authentication.KeyId,
                receipt.Authentication.AlgorithmId,
                receipt.ReceiptHash,
                Base64Url(changedProof),
                changedProof.Length,
                CortexCanonicalJson.Sha256(changedProof));
            var tampered = new CortexSyncReceiptV1(
                receipt.SchemaVersion,
                receipt.ContractId,
                receipt.Canonicalization,
                receipt.ReceiptId,
                receipt.ServerInstanceId,
                receipt.StoreId,
                receipt.StoreEpoch,
                receipt.ProjectId,
                receipt.BundleId,
                receipt.BundleHash,
                receipt.ReceivedAt,
                changedAuthentication,
                receipt.Durability,
                receipt.Outcomes,
                receipt.Reconciliation,
                receipt.Extensions,
                receipt.ReceiptHash);
            Assert.False(
                await issuer.VerifyProofAsync(tampered, CancellationToken.None),
                "A changed receipt proof verified.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CortexOfflineBundleV1 CreateBundle(DateTimeOffset now)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"contractId\":\"pegasus.cortex.test-payload/1.0\",\"schemaVersion\":1}");
        var payloadHash = CortexCanonicalJson.Sha256(payload);
        var eventValue = CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
            1,
            CortexSyncContractIds.OfflineEvent,
            CortexSyncContractIds.Canonicalization,
            "store-1",
            "epoch-1",
            "project-1",
            1,
            "event-1",
            CortexSyncContractIds.EmptySha256,
            now,
            "producer-1",
            "operation-1",
            "tenant-1",
            "project:project-1",
            1,
            "pegasus.cortex.test-payload/1.0",
            Base64Url(payload),
            payload.Length,
            payloadHash,
            new CortexSyncSourceProvenanceV1(
                "client_operation",
                null,
                null,
                payload.Length,
                payloadHash,
                now),
            CortexSyncContractIds.ContentTreatment,
            executable: false,
            [],
            CortexSyncContractIds.EmptySha256));
        return CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            "bundle-1",
            "store-1",
            "epoch-1",
            "project-1",
            null,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            1,
            1,
            now,
            [eventValue],
            [],
            CortexSyncContractIds.EmptySha256));
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
