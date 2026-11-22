using System.Reflection;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Persistence.PostgreSql.Sync;

namespace Pegasus.Cortex.Persistence.PostgreSql.Tests;

internal static class PostgreSqlSyncRepositoryTests
{
    internal static Task CanonicalIdentityAsync()
    {
        var observedAt = DateTimeOffset.UnixEpoch.AddTicks(17);
        var bundle = CreateBundle(observedAt);
        var scope = CreateScope();
        var captured = PostgreSqlSyncCanonicalCodec.Capture(
            new CortexSyncCommitRequest(scope, bundle, observedAt));
        var second = PostgreSqlSyncCanonicalCodec.Capture(
            new CortexSyncCommitRequest(scope, bundle, observedAt));

        TestAssert.Equal(DateTimeOffset.UnixEpoch.AddTicks(10), captured.ReceivedAt);
        TestAssert.True(captured.Canonical.AsSpan().SequenceEqual(second.Canonical),
            "Canonical bundle capture was not deterministic.");
        TestAssert.Equal(captured.CanonicalSha256, second.CanonicalSha256);
        TestAssert.Equal(1, captured.Events.Count);
        var payload = Encoding.UTF8.GetBytes(
            "{\"contractId\":\"pegasus.cortex.test-payload/1.0\",\"schemaVersion\":1}");
        TestAssert.True(captured.Events[0].Payload.AsSpan().SequenceEqual(payload),
            "Captured event payload changed during strict base64url decoding.");

        var attemptId = PostgreSqlSyncHashPolicy.AttemptId(captured);
        TestAssert.Equal(attemptId, PostgreSqlSyncHashPolicy.AttemptId(second));
        TestAssert.True(attemptId.StartsWith("at_", StringComparison.Ordinal),
            "Attempt identity prefix is missing.");
        TestAssert.Equal(67, attemptId.Length);
        var committedId = PostgreSqlSyncHashPolicy.CommitId(
            captured,
            attemptId,
            CortexSyncCommitDisposition.Committed);
        var conflictId = PostgreSqlSyncHashPolicy.CommitId(
            captured,
            attemptId,
            CortexSyncCommitDisposition.Conflict);
        TestAssert.False(string.Equals(committedId, conflictId, StringComparison.Ordinal),
            "Commit identity omitted the durable disposition.");
        TestAssert.Equal(
            "cortex_sync_identity_conflict",
            PostgreSqlCortexSyncRepository.ClassifyConflictDiagnostic(
                acceptedIdentityCollision: true,
                exactlyContinuesSourceHead: false));
        TestAssert.Equal(
            "cortex_sync_head_conflict",
            PostgreSqlCortexSyncRepository.ClassifyConflictDiagnostic(
                acceptedIdentityCollision: false,
                exactlyContinuesSourceHead: false));
        TestAssert.Throws<ArgumentException>(() =>
            PostgreSqlCortexSyncRepository.ClassifyConflictDiagnostic(
                acceptedIdentityCollision: false,
                exactlyContinuesSourceHead: true));

        var outcome = new CortexSyncReceiptOutcomeV1(
            1,
            bundle.Events[0].EventId,
            bundle.Events[0].EventHash,
            CortexSyncDispositionIds.Applied,
            durable: true,
            serverEntityId: null,
            serverRevisionId: null,
            diagnosticCode: null);
        var storageRevision = PostgreSqlSyncHashPolicy.StorageRevision(
            captured,
            attemptId,
            CortexSyncCommitDisposition.Committed);
        var head = new PostgreSqlSyncSourceHead(
            null,
            bundle.BundleId,
            storageRevision,
            1,
            bundle.Events[0].EventId,
            bundle.Events[0].EventHash);
        var durable = new PostgreSqlSyncDurableState(
            scope.TenantId,
            scope.Namespace,
            scope.StoreId,
            scope.StoreEpoch,
            scope.ProjectId,
            attemptId,
            bundle.BundleId,
            bundle.BundleHash,
            committedId,
            storageRevision,
            CortexSyncCommitDisposition.Committed,
            captured.ReceivedAt,
            captured.ReceivedAt,
            [outcome],
            head);
        var durableHash = PostgreSqlSyncHashPolicy.DurableStateHash(durable);
        TestAssert.Equal(durableHash, PostgreSqlSyncHashPolicy.DurableStateHash(durable));
        TestAssert.False(string.Equals(
                durableHash,
                PostgreSqlSyncHashPolicy.DurableStateHash(durable with { SourceHead = null }),
                StringComparison.Ordinal),
            "Durable hash omitted source-head evidence.");

        var decoded = PostgreSqlSyncCanonicalCodec.DecodeBundle(
            captured.Canonical,
            captured.CanonicalSha256,
            scope);
        TestAssert.Equal(bundle.BundleHash, decoded.BundleHash);
        var mismatch = TestAssert.Throws<PostgreSqlPersistenceException>(() =>
            PostgreSqlSyncCanonicalCodec.DecodeBundle(
                captured.Canonical,
                CortexSyncContractIds.EmptySha256,
                scope));
        TestAssert.Equal("cortex_postgresql_sync_bundle_canonical_mismatch", mismatch.Code);
        return Task.CompletedTask;
    }

    internal static Task AppendOnlyAndReadinessBoundaryAsync()
    {
        var repositoryType = typeof(PostgreSqlCortexSyncRepository);
        var insertSql = repositoryType
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.Name.StartsWith("Insert", StringComparison.Ordinal) &&
                            field.FieldType == typeof(string))
            .Select(field => (string)(field.GetValue(null) ?? string.Empty))
            .ToArray();
        TestAssert.Equal(4, insertSql.Length);
        foreach (var sql in insertSql)
        {
            var normalized = sql.ToLowerInvariant();
            TestAssert.Contains("insert into cortex_sync.", normalized,
                "A sync writer is not an append operation.");
            TestAssert.DoesNotContain("update ", normalized,
                "The sync repository requested UPDATE privilege.");
            TestAssert.DoesNotContain("delete ", normalized,
                "The sync repository requested DELETE privilege.");
            TestAssert.DoesNotContain("nextval", normalized,
                "The sync repository unexpectedly depends on a sequence.");
        }
        var root = FindRepositoryRoot();
        var writerSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PegasusCortex.Persistence.PostgreSql",
            "Sync",
            "PostgreSqlCortexSyncRepository.Write.cs")).ToLowerInvariant();
        TestAssert.DoesNotContain("update cortex_sync.", writerSource,
            "The dynamic sync writer requested UPDATE privilege.");
        TestAssert.DoesNotContain("delete from cortex_sync.", writerSource,
            "The dynamic sync writer requested DELETE privilege.");
        TestAssert.Equal(
            2,
            CountOccurrences(writerSource, "insert into cortex_sync."),
            "The two bounded dynamic event/outcome append statements changed unexpectedly.");

        var context = PostgreSqlTransactionContext.FromSync(CreateScope());
        TestAssert.Equal("sync", context.Capability);
        TestAssert.Equal("restricted", context.MaximumSensitivity);
        TestAssert.False(context.AllowStale, "Sync context enabled stale reads.");
        TestAssert.False(context.AllowBlockedOrUnverified,
            "Sync context enabled blocked or unverified reads.");

        var readinessSql = (string)(typeof(PostgreSqlSyncReadinessProbe)
            .GetField("ProbeSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null) ?? throw new InvalidOperationException("Sync readiness SQL is unavailable."));
        var readiness = readinessSql.ToLowerInvariant();
        foreach (var required in new[]
                 {
                     "cortex_meta.schema_migrations",
                     "cortex_sync.rls_policy_contract",
                     "has_schema_privilege",
                     "has_table_privilege",
                     "has_any_column_privilege",
                     "relrowsecurity",
                     "relforcerowsecurity",
                     "pg_catalog.pg_policies",
                     "schema_object_difference",
                     "pg_catalog.pg_get_constraintdef",
                     "pg_catalog.pg_get_indexdef",
                     "pg_catalog.pg_get_triggerdef",
                     "pg_catalog.pg_get_functiondef"
                 })
        {
            TestAssert.Contains(required, readiness,
                $"Sync readiness omits required evidence: {required}.");
        }
        TestAssert.Contains(
            "full outer join cortex_sync.rls_policy_contract as expected on installed.schemaname = 'cortex_sync'",
            NormalizeWhitespace(readiness),
            "A same-name policy in another schema can consume expected Cortex policy evidence.");
        TestAssert.Contains(
            "values ('rls_policy_contract'), ('schema_object_contract')",
            NormalizeWhitespace(readiness),
            "Sync readiness does not identify both immutable read-only contracts.");
        TestAssert.Contains(
            "current_setting('server_version_num')::integer >= 170000",
            readiness,
            "Sync readiness does not reject PostgreSQL 17+ MAINTAIN privileges.");
        TestAssert.DoesNotContain("insert ", readiness,
            "Sync readiness mutates durable state.");
        TestAssert.DoesNotContain("update ", readiness,
            "Sync readiness mutates durable state.");
        TestAssert.DoesNotContain("delete ", readiness,
            "Sync readiness mutates durable state.");

        var storedCommitSql = (string)(repositoryType
            .GetField("StoredCommitSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null) ?? throw new InvalidOperationException("Stored commit SQL is unavailable."));
        var normalizedStoredCommit = NormalizeWhitespace(storedCommitSql).ToLowerInvariant();
        foreach (var provenanceField in new[]
                 {
                     "authority_id", "subject_id", "service_id", "policy_revision"
                 })
        {
            TestAssert.Contains(
                $"c.{provenanceField}",
                normalizedStoredCommit,
                $"Stored commit read-back omits commit provenance field {provenanceField}.");
        }
        foreach (var migrationField in new[]
                 {
                     "b.migration_page_canonical",
                     "b.migration_current_cursor_hash",
                     "b.migration_next_cursor_hash",
                     "b.migration_inventory_hash"
                 })
        {
            TestAssert.Contains(
                migrationField,
                normalizedStoredCommit,
                $"Stored commit read-back discards migration proof field {migrationField}.");
        }
        TestAssert.True(
            typeof(ICortexLegacyMigrationPageCommitRepository).IsAssignableFrom(repositoryType),
            "The PostgreSQL repository does not expose the atomic migration-page proof boundary.");
        var currentLeafSql = (string)(repositoryType
            .GetField("CurrentLeafSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null) ?? throw new InvalidOperationException("Current source-leaf SQL is unavailable."));
        var normalizedCurrentLeaf = NormalizeWhitespace(currentLeafSql).ToLowerInvariant();
        TestAssert.Contains("head_value.attempt_id", normalizedCurrentLeaf,
            "The current source leaf omits its durable attempt identity.");
        TestAssert.Contains("head_value.commit_id", normalizedCurrentLeaf,
            "The current source leaf omits its durable commit identity.");
        var predecessorSql = (string)(repositoryType
            .GetField("CurrentMigrationPredecessorSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null) ?? throw new InvalidOperationException("Migration predecessor SQL is unavailable."));
        var normalizedPredecessor = NormalizeWhitespace(predecessorSql).ToLowerInvariant();
        TestAssert.Contains("join cortex_sync.journal_ack as a", normalizedPredecessor,
            "Migration continuation is not bound to a durable receipt row.");
        TestAssert.Contains("a.reconciliation_state = 'reconciled'", normalizedPredecessor,
            "Migration continuation accepts an unreconciled predecessor.");

        // Keep the stored-head projection, value shape, and reader ordinals locked together.
        // This is source-level because the provider harness deliberately does not open a database.
        var storedHeadSql = (string)(repositoryType
            .GetField("StoredHeadSql", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null) ?? throw new InvalidOperationException("Stored source-head SQL is unavailable."));
        TestAssert.Contains(
            "select base_server_revision, bundle_id, storage_revision, committed_source_sequence, committed_event_id, committed_chain_hash, recorded_at",
            NormalizeWhitespace(storedHeadSql).ToLowerInvariant(),
            "Stored source-head projection no longer matches its six-field value plus timestamp shape.");
        TestAssert.Equal(6, typeof(PostgreSqlSyncSourceHead).GetProperties().Length);

        var readerSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PegasusCortex.Persistence.PostgreSql",
            "Sync",
            "PostgreSqlCortexSyncRepository.Read.cs"));
        var readerStart = readerSource.IndexOf(
            "private static async ValueTask<PostgreSqlSyncSourceHead?> ReadStoredHeadAsync",
            StringComparison.Ordinal);
        var readerEnd = readerStart < 0
            ? -1
            : readerSource.IndexOf(
                "private static async ValueTask<string?> FindAttemptByCommitAsync",
                readerStart,
                StringComparison.Ordinal);
        TestAssert.True(readerStart >= 0 && readerEnd > readerStart,
            "Stored source-head reader boundary is unavailable.");
        var storedHeadReader = readerSource[readerStart..readerEnd];
        TestAssert.Contains("reader.GetString(5)", storedHeadReader,
            "Stored source-head reader omits the chain-hash field at ordinal 5.");
        TestAssert.Contains("ReadTimestamp(reader, 6)", storedHeadReader,
            "Stored source-head reader omits recorded_at at ordinal 6.");
        TestAssert.DoesNotContain("reader.GetString(6)", storedHeadReader,
            "Stored source-head reader treats recorded_at as a value-shape string field.");
        TestAssert.DoesNotContain("reader.GetString(7)", storedHeadReader,
            "Stored source-head reader reads beyond its seven-column projection.");

        var repositorySource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PegasusCortex.Persistence.PostgreSql",
            "Sync",
            "PostgreSqlCortexSyncRepository.cs"));
        var existingAttemptStart = repositorySource.IndexOf("if (existing is not null)", StringComparison.Ordinal);
        var existingAttemptEnd = existingAttemptStart < 0
            ? -1
            : repositorySource.IndexOf(
                "var head = await ReadCurrentLeafAsync",
                existingAttemptStart,
                StringComparison.Ordinal);
        TestAssert.True(existingAttemptStart >= 0 && existingAttemptEnd > existingAttemptStart,
            "Existing-attempt classification boundary is unavailable.");
        var existingAttempt = repositorySource[existingAttemptStart..existingAttemptEnd];
        TestAssert.Contains(
            "CortexLegacyMigrationPageCommitDiagnosticCodes.ExactAttemptConflict",
            existingAttempt,
            "An immutable same-bundle migration-page collision is not classified permanently.");
        TestAssert.Contains("RejectedMigrationCommit", existingAttempt,
            "An immutable migration-page collision can still escape through the generic exception path.");
        return Task.CompletedTask;
    }

    internal static async Task ExactPublishedLookupScopeAsync()
    {
        await using var dataSource = new TraceDbDataSource { ContextIsClean = true };
        var repository = new PostgreSqlCortexSyncRepository(
            new PostgreSqlTransactionRunner(dataSource));
        var scope = CreateScope();
        const string bundleId = "bundle-1";
        var bundleHash = new string('a', 64);

        var value = await repository.FindPublishedAsync(
            new CortexSyncPublishedBundleLookup(scope, bundleId, bundleHash),
            CancellationToken.None);

        TestAssert.Equal<CortexSyncPublishedJournalHead?>(null, value);
        var command = dataSource.Commands.Single(item =>
            item.CommandText.Contains("head_value.bundle_id = @bundle_id", StringComparison.Ordinal));
        foreach (var required in new[]
                 {
                     "head_value.tenant_id = @tenant_id",
                     "head_value.namespace_name = @namespace",
                     "head_value.store_id = @store_id",
                     "head_value.store_epoch = @store_epoch",
                     "head_value.project_id = @project_id",
                     "ack.reconciliation_state = 'reconciled'",
                     "ack.matched_event_count = ack.source_event_count"
                 })
        {
            TestAssert.Contains(required, command.CommandText,
                $"Exact published lookup omits required scope or receipt evidence: {required}.");
        }
        TestAssert.Equal(scope.TenantId, command.ParameterValue("tenant_id"));
        TestAssert.Equal(scope.Namespace, command.ParameterValue("namespace"));
        TestAssert.Equal(scope.StoreId, command.ParameterValue("store_id"));
        TestAssert.Equal(scope.StoreEpoch, command.ParameterValue("store_epoch"));
        TestAssert.Equal(scope.ProjectId, command.ParameterValue("project_id"));
        TestAssert.Equal(bundleId, command.ParameterValue("bundle_id"));
        TestAssert.Equal(bundleHash, command.ParameterValue("bundle_hash"));
    }

    private static CortexSyncRepositoryScope CreateScope() => new(
        "tenant-1",
        "project:project-1",
        "authority-1",
        "subject-1",
        "service-1",
        7,
        "store-1",
        "epoch-1",
        "project-1",
        "cortex.sync.submit");

    private static CortexOfflineBundleV1 CreateBundle(DateTimeOffset observedAt)
    {
        var utc = observedAt.ToUniversalTime();
        var payload = Encoding.UTF8.GetBytes(
            "{\"contractId\":\"pegasus.cortex.test-payload/1.0\",\"schemaVersion\":1}");
        var payloadHash = CortexCanonicalJson.Sha256(payload);
        var eventValue = CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineEvent,
            CortexSyncContractIds.Canonicalization,
            "store-1",
            "epoch-1",
            "project-1",
            1,
            "event-1",
            CortexSyncContractIds.EmptySha256,
            utc,
            "producer-1",
            "operation-1",
            "tenant-1",
            "project:project-1",
            7,
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
                utc),
            CortexSyncContractIds.ContentTreatment,
            executable: false,
            [],
            CortexSyncContractIds.EmptySha256));
        return CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            CortexSyncContractIds.SchemaVersion,
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
            utc,
            [eventValue],
            [],
            CortexSyncContractIds.EmptySha256));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static string NormalizeWhitespace(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value,
            "\\s+",
            " ",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                    Directory.Exists(Path.Combine(
                        current.FullName,
                        "src",
                        "PegasusCortex.Persistence.PostgreSql")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex repository root.");
    }
}
