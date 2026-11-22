using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Migrations;
using Pegasus.Cortex.Persistence.PostgreSql.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Tests;

internal static class PostgreSqlAgentRetentionTests
{
    public static Task MigrationAndAdapterBoundaryAsync()
    {
        var migration = PostgreSqlMigrationCatalog.All.Single(value => value.Id == "0006");
        var normalized = Normalize(migration.Sql).ToLowerInvariant();
        TestAssert.Equal("0007", PostgreSqlMigrationCatalog.LatestId);
        foreach (var table in new[]
                 {
                     "cortex_data.retained_chain",
                     "cortex_data.retained_document",
                     "cortex_data.retention_audit"
                 })
        {
            TestAssert.Contains($"create table {table}", normalized, $"Migration 0006 does not create {table}.");
            TestAssert.Contains($"alter table {table} enable row level security", normalized, $"{table} does not enable RLS.");
            TestAssert.Contains($"alter table {table} force row level security", normalized, $"{table} does not force RLS.");
        }
        foreach (var trigger in new[]
                 {
                     "retained_chain_immutable",
                     "retained_document_immutable",
                     "retention_audit_immutable"
                 })
        {
            TestAssert.Contains($"create trigger {trigger}", normalized, $"{trigger} is missing.");
        }
        TestAssert.Contains(
            "previous_revision bigint generated always as",
            normalized,
            "Retention revisions do not derive the exact predecessor ordinal.");
        TestAssert.Contains(
            "references cortex_data.retained_document",
            normalized,
            "Retention revisions are not foreign-key bound to their exact predecessor.");
        TestAssert.Contains(
            "match full deferrable initially deferred",
            normalized,
            "Retention predecessor integrity is not atomic and deferred.");
        TestAssert.Contains(
            "where kind = 'task-snapshot'",
            normalized,
            "A project can create multiple competing task-snapshot chains.");
        TestAssert.Contains(
            "to_tsvector('simple', search_text)",
            normalized,
            "Retention lexical projection is not deterministic.");
        TestAssert.Equal(
            3,
            Count(normalized, "payload bytea not null check (octet_length(payload) between 1 and 131072)"),
            "Every retained chain, document, and audit row must preserve bounded authenticated payload bytes.");
        TestAssert.Equal(
            6,
            Count(normalized, "create policy "),
            "Migration 0006 must install exactly one read and append policy per table.");
        TestAssert.Contains(
            "'retained_read', 'retained_write', 'readiness'",
            normalized,
            "Retention read policies do not distinguish read, write-continuity, and readiness.");
        TestAssert.Contains(
            "= 'retained_write'",
            normalized,
            "Retention append policies do not require retained-write authority.");
        TestAssert.Contains(
            "select count(*) from cortex_meta.rls_policy_contract) <> 29",
            normalized,
            "Migration 0006 does not fail closed on the final policy inventory.");
        foreach (var forbidden in new[] { "drop table", "truncate table", "alter column", " on conflict " })
        {
            TestAssert.False(
                normalized.Contains(forbidden, StringComparison.Ordinal),
                $"Migration 0006 contains destructive or ambiguous behavior: {forbidden}.");
        }

        var readScope = Scope(PostgreSqlCortexRetentionCapability.Read);
        var writeScope = Scope(PostgreSqlCortexRetentionCapability.Write);
        AssertContext(
            PostgreSqlTransactionContext.FromRetention(readScope, "cortex.retention.head.read"),
            "retained_read");
        AssertContext(
            PostgreSqlTransactionContext.FromRetention(writeScope, "cortex.retention.append"),
            "retained_write");
        var briefScope = new CortexProjectBriefReadScope(
            readScope.TenantId,
            "plink-a",
            "project-a",
            readScope.Namespace,
            readScope.AuthorityId,
            readScope.SubjectId,
            readScope.ServiceId,
            readScope.PolicyRevision);
        TestAssert.Equal(
            readScope,
            PostgreSqlCortexRetentionScope.FromProjectBrief(briefScope),
            "Project-brief reads did not preserve the exact server-derived authority scope.");

        using var dataSource = new TraceDbDataSource();
        var authenticator = new TestAuthenticator();
        var repository = new PostgreSqlCortexAgentRetentionRepository(
            new PostgreSqlTransactionRunner(dataSource),
            authenticator,
            readScope);
        TestAssert.Throws<UnauthorizedAccessException>(() => repository.AppendAsync(
            Request(),
            CancellationToken.None).AsTask().GetAwaiter().GetResult());
        TestAssert.Equal(0, dataSource.Commands.Count, "A read-only append reached PostgreSQL.");
        _ = new PostgreSqlCortexProjectBriefStore(
            new PostgreSqlTransactionRunner(dataSource),
            authenticator);

        var repositoryType = typeof(PostgreSqlCortexAgentRetentionRepository);
        var insert = PrivateSql(repositoryType, "InsertDocumentSql");
        TestAssert.False(
            insert.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase),
            "Retention append depends on ambiguous upsert visibility.");
        var search = PrivateSql(repositoryType, "SearchHeadsSql");
        TestAssert.Contains("d.payload", search, "Search does not read the authenticated canonical payload.");
        TestAssert.Contains("d.auth_signature", search, "Search does not read the row authentication seal.");
        TestAssert.Contains("NOT EXISTS", search, "Search does not derive immutable current heads.");
        TestAssert.Contains("current_setting('pegasus.cortex.tenant_id')", search, "Search is not tenant bound.");
        TestAssert.Contains("current_setting('pegasus.cortex.namespace')", search, "Search is not namespace bound.");
        TestAssert.DoesNotContain("search_document @@", search, "Search filters on unauthenticated lexical text.");
        TestAssert.DoesNotContain("expires_at >", search, "Search filters on unauthenticated expiry.");
        var readiness = PrivateSql(
            typeof(PostgreSqlPersistenceReadinessProbe),
            "RowLevelSecuritySql");
        foreach (var value in new[]
                 {
                     "retained_chain_read_scope",
                     "retained_chain_append_scope",
                     "retained_document_read_scope",
                     "retained_document_append_scope",
                     "retention_audit_read_scope",
                     "retention_audit_append_scope"
                 })
        {
            TestAssert.Contains(value, readiness, $"Base readiness does not attest {value}.");
        }
        TestAssert.Contains(
            "SELECT COUNT(*) = 39 FROM installed_policy",
            readiness,
            "Base readiness does not reject missing or extra retention policies.");
        var runtimePrivileges = PrivateSql(
            typeof(PostgreSqlPersistenceReadinessProbe),
            "RuntimeSql");
        foreach (var table in new[]
                 {
                     "cortex_data.retained_chain",
                     "cortex_data.retained_document",
                     "cortex_data.retention_audit"
                 })
        {
            TestAssert.Contains(table, runtimePrivileges, $"Runtime readiness omits {table} privileges.");
        }
        return Task.CompletedTask;
    }

    public static Task AuthenticatedProjectionTamperDenialAsync()
    {
        var authenticator = new TestAuthenticator();
        using var dataSource = new TraceDbDataSource();
        var repository = new PostgreSqlCortexAgentRetentionRepository(
            new PostgreSqlTransactionRunner(dataSource),
            authenticator,
            Scope(PostgreSqlCortexRetentionCapability.Read));
        var observedAt = new DateTimeOffset(2026, 8, 2, 15, 0, 0, TimeSpan.Zero);
        var first = Document(
            "task-a",
            "alpha beta",
            observedAt.AddMinutes(-10),
            observedAt.AddHours(1));
        var second = Document(
            "task-b",
            "alpha beta",
            observedAt.AddMinutes(-5),
            observedAt.AddHours(1));
        var expired = Document(
            "task-c",
            "alpha beta beta beta",
            observedAt.AddMinutes(-1),
            observedAt.AddSeconds(-1));
        var projected = Projected(first, authenticator);

        var roundTrip = repository.AuthenticateProjectedDocument(projected);
        TestAssert.Equal(first.ContentHash, roundTrip.ContentHash);
        TestAssert.Throws<InvalidDataException>(() =>
            repository.AuthenticateProjectedDocument(projected with
            {
                SearchText = "alpha beta injected"
            }));
        TestAssert.Throws<InvalidDataException>(() =>
            repository.AuthenticateProjectedDocument(projected with
            {
                CapturedAt = projected.CapturedAt.AddHours(1)
            }));
        TestAssert.Throws<InvalidDataException>(() =>
            repository.AuthenticateProjectedDocument(projected with
            {
                ExpiresAt = projected.ExpiresAt.AddHours(1)
            }));

        var search = PostgreSqlCortexAgentRetentionRepository.SearchAuthenticatedHeads(
            [first, second, expired],
            "alpha beta",
            observedAt,
            limit: 8);
        TestAssert.Equal(2, search.Count, "Signed expiry was not used for search visibility.");
        TestAssert.Equal(
            second.DocumentId,
            search[0].DocumentId,
            "Signed capture time was not used for deterministic search ordering.");
        var visible = PostgreSqlCortexAgentRetentionRepository.ProjectAuthenticatedHeadPage(
            [first, second, expired],
            afterDocumentId: null,
            limit: 8,
            includeExpired: false,
            observedAt);
        TestAssert.Equal(2, visible.Items.Count, "Signed expiry was not used for head visibility.");
        var all = PostgreSqlCortexAgentRetentionRepository.ProjectAuthenticatedHeadPage(
            [first, second, expired],
            afterDocumentId: null,
            limit: 8,
            includeExpired: true,
            observedAt);
        TestAssert.Equal(3, all.Items.Count, "Explicit history reads did not include expired signed rows.");

        var repositoryType = typeof(PostgreSqlCortexAgentRetentionRepository);
        var searchSql = PrivateSql(repositoryType, "SearchHeadsSql");
        TestAssert.Contains("d.payload", searchSql, "Search does not retrieve authenticated payload bytes.");
        TestAssert.Contains("d.auth_signature", searchSql, "Search does not retrieve the row authentication seal.");
        TestAssert.DoesNotContain("search_document @@", searchSql, "Search filters on an unauthenticated lexical projection.");
        TestAssert.DoesNotContain("expires_at >", searchSql, "Search filters visibility on an unauthenticated expiry projection.");
        TestAssert.DoesNotContain("ORDER BY", searchSql, "Search orders on unauthenticated row projections.");
        var listSql = PrivateSql(repositoryType, "ListHeadsSuffixSql");
        TestAssert.DoesNotContain("@include_expired", listSql, "Head visibility is filtered before row authentication.");
        TestAssert.DoesNotContain("@after_document_id", listSql, "Head paging is filtered before row authentication.");
        TestAssert.DoesNotContain("ORDER BY", listSql, "Head paging orders before row authentication.");
        return Task.CompletedTask;
    }

    private static PostgreSqlCortexRetentionScope Scope(
        PostgreSqlCortexRetentionCapability capability) =>
        new(
            "tenant-a",
            "project:neural-commander",
            "authority-a",
            "subject-a",
            "service-a",
            12,
            capability);

    private static CortexRetentionAppendRequest Request()
    {
        var canonical = CortexCanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["contractId"] = "pegasus.test/1.0",
            ["value"] = "retained"
        });
        var capturedAt = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        return new CortexRetentionAppendRequest(
            "project-a",
            CortexRetainedDocumentKinds.TaskSnapshot,
            "task-set-a",
            1,
            null,
            "idem-a",
            "producer-a",
            "pegasus.test/1.0",
            canonical,
            CortexCanonicalJson.Sha256(canonical),
            "retained",
            capturedAt,
            capturedAt.AddHours(24)).Validate();
    }

    private static CortexRetainedDocument Document(
        string documentId,
        string searchText,
        DateTimeOffset capturedAt,
        DateTimeOffset expiresAt)
    {
        var canonical = CortexCanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["contractId"] = "pegasus.test/1.0",
            ["documentId"] = documentId,
            ["value"] = searchText
        });
        var request = new CortexRetentionAppendRequest(
            "project-a",
            CortexRetainedDocumentKinds.TaskSnapshot,
            documentId,
            1,
            null,
            $"idem-{documentId}",
            "producer-a",
            "pegasus.test/1.0",
            canonical,
            CortexCanonicalJson.Sha256(canonical),
            searchText,
            capturedAt,
            expiresAt).Validate();
        return new CortexRetainedDocument(
            request.ProjectId,
            request.Kind,
            request.DocumentId,
            request.Revision,
            request.ExpectedPreviousHash,
            request.IdempotencyKey,
            request.ProducerId,
            request.ContractId,
            request.CanonicalUtf8,
            request.ContentHash,
            request.SearchText,
            request.CapturedAt,
            request.ExpiresAt);
    }

    private static PostgreSqlRetentionProjectedDocument Projected(
        CortexRetainedDocument document,
        ICortexRowAuthenticator authenticator)
    {
        var primaryId = PostgreSqlRetentionRecordIdentity.ChainPrimaryId(
            document.ProjectId,
            document.Kind,
            document.DocumentId);
        var revisionId = PostgreSqlRetentionRecordIdentity.DocumentRevisionId(
            document.Revision);
        var stored = new PostgreSqlStoredRowCodec(authenticator).Encode(
            PostgreSqlRetentionRecordIdentity.DocumentEntityKind,
            "tenant-a",
            "project:neural-commander",
            primaryId,
            revisionId,
            document);
        return new PostgreSqlRetentionProjectedDocument(
            document.ProjectId,
            document.Kind,
            document.DocumentId,
            document.Revision,
            document.PreviousHash,
            document.IdempotencyKey,
            document.ProducerId,
            document.ContractId,
            document.ContentHash,
            document.SearchText,
            document.CapturedAt,
            document.ExpiresAt,
            stored.Payload,
            stored.PayloadSha256,
            stored.Seal.Version,
            stored.Seal.Algorithm,
            stored.Seal.KeyId,
            stored.Seal.GetSignature());
    }

    private static void AssertContext(
        PostgreSqlTransactionContext context,
        string capability)
    {
        TestAssert.Equal("tenant-a", context.TenantId);
        TestAssert.Equal("project:neural-commander", context.Namespace);
        TestAssert.Equal("authority-a", context.AuthorityId);
        TestAssert.Equal("subject-a", context.SubjectId);
        TestAssert.Equal("service-a", context.ServiceId);
        TestAssert.Equal(12L, context.PolicyRevision);
        TestAssert.Equal(capability, context.Capability);
        TestAssert.Equal("internal", context.MaximumSensitivity);
        TestAssert.False(context.AllowStale, "Retention context unexpectedly allows stale knowledge.");
        TestAssert.False(context.AllowBlockedOrUnverified, "Retention context unexpectedly widens blocked knowledge visibility.");
    }

    private static string PrivateSql(Type type, string fieldName) =>
        type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string
        ?? throw new InvalidOperationException($"Missing private SQL field {type.Name}.{fieldName}.");

    private static string Normalize(string value) =>
        Regex.Replace(
            value,
            "\\s+",
            " ",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static int Count(string value, string token)
    {
        var count = 0;
        for (var offset = 0; (offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0; offset += token.Length)
            count++;
        return count;
    }

    private sealed class TestAuthenticator : ICortexRowAuthenticator
    {
        private static readonly byte[] Key = SHA256.HashData("postgresql-retention-tests"u8);

        public CortexRowSeal Seal(CortexRowAuthenticationEnvelope envelope)
        {
            using var hmac = new HMACSHA256(Key);
            return new CortexRowSeal(
                1,
                "hmac-sha256",
                "test-key",
                hmac.ComputeHash(CortexRowAuthentication.GetSigningBytes(envelope)));
        }

        public bool Verify(CortexRowAuthenticationEnvelope envelope, CortexRowSeal seal)
        {
            var expected = Seal(envelope);
            return seal.Version == expected.Version &&
                string.Equals(seal.Algorithm, expected.Algorithm, StringComparison.Ordinal) &&
                string.Equals(seal.KeyId, expected.KeyId, StringComparison.Ordinal) &&
                CryptographicOperations.FixedTimeEquals(
                    seal.GetSignature(),
                    expected.GetSignature());
        }
    }
}
