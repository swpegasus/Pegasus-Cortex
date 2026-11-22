using System.Data;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Hybrid;
using Pegasus.Cortex.Persistence.PostgreSql.Migrations;
using Pegasus.Cortex.Persistence.PostgreSql.Learning;
using Pegasus.Cortex.Persistence.PostgreSql.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Readiness;
using Pegasus.Cortex.Persistence.PostgreSql.Recovery;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Tests;

internal static class PostgreSqlProviderContractTests
{
    private static readonly string[] ImmutableTables =
    [
        "collection_snapshot",
        "knowledge_revision",
        "knowledge_link",
        "learning_proposal",
        "learning_decision",
        "audit_event"
    ];

    public static Task MigrationCatalogAsync()
    {
        var migrations = PostgreSqlMigrationCatalog.All.ToArray();
        TestAssert.True(migrations.Length is > 0 and <= 1024, "Migration count is empty or unbounded.");
        TestAssert.Equal(migrations[^1].Id, PostgreSqlMigrationCatalog.LatestId);

        var previous = string.Empty;
        foreach (var migration in migrations)
        {
            TestAssert.True(
                migration.Id.Length == 4 && migration.Id.All(char.IsAsciiDigit),
                $"Migration ID '{migration.Id}' is not a four-digit ordinal.");
            TestAssert.True(
                string.CompareOrdinal(previous, migration.Id) < 0,
                "Migrations are duplicated or not in strict ordinal order.");
            TestAssert.True(
                migration.Sql.Length is > 0 and <= 512 * 1024 &&
                Encoding.UTF8.GetByteCount(migration.Sql) <= 512 * 1024,
                $"Migration '{migration.Id}' exceeds its offline size bound.");
            TestAssert.False(migration.Sql.Contains('\r'), $"Migration '{migration.Id}' is not LF-normalized.");
            TestAssert.True(migration.Sql.EndsWith('\n'), $"Migration '{migration.Id}' lacks its canonical final LF.");
            TestAssert.Equal(
                CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(migration.Sql)),
                migration.Sha256,
                $"Migration '{migration.Id}' hash does not match its embedded normalized SQL.");
            previous = migration.Id;
        }

        var initial = migrations.Single(migration => migration.Id == "0001").Sql;
        TestAssert.Equal(
            "579841cc0844c0af08d0b341e248ad02573e6eb50eef96e8cdc462461c43787c",
            migrations.Single(migration => migration.Id == "0001").Sha256,
            "The immutable 0001 migration changed while extending the catalog.");
        var normalized = NormalizeWhitespace(initial).ToLowerInvariant();
        var tenantTables = ExtractTenantTables(initial);
        TestAssert.True(
            tenantTables.Count is > 0 and <= 128,
            "The migration has no tenant tables or exceeds the bounded table contract.");
        foreach (var table in tenantTables)
        {
            var definition = ExtractCreateTable(initial, table);
            var normalizedDefinition = NormalizeWhitespace(definition).ToLowerInvariant();
            TestAssert.Contains("tenant_id", normalizedDefinition, $"{table} lacks tenant_id.");
            TestAssert.Contains("namespace_name", normalizedDefinition, $"{table} lacks namespace_name.");
            TestAssert.Contains(
                "primary key (tenant_id, namespace_name",
                normalizedDefinition,
                $"{table} does not lead its primary key with tenant and namespace.");
            TestAssert.Contains(
                $"alter table {table} enable row level security;",
                normalized,
                $"{table} does not enable RLS.");
            TestAssert.Contains(
                $"alter table {table} force row level security;",
                normalized,
                $"{table} does not force RLS for its owner.");
        }

        TestAssert.Contains(
            "foreign key (tenant_id, namespace_name, record_id, current_revision_id)",
            normalized,
            "The current-head foreign key is not tenant and namespace scoped.");
        TestAssert.Contains(
            "foreign key (tenant_id, namespace_name, maximum_sensitivity, current_collection_id)",
            normalized,
            "The collection head is not scoped to an exact sensitivity projection.");
        TestAssert.Contains(
            "foreign key (tenant_id, namespace_name, record_id, revision_id)",
            normalized,
            "The revision-link foreign key is not tenant and namespace scoped.");
        TestAssert.Contains(
            "foreign key (tenant_id, namespace_name, proposal_id, proposal_hash)",
            normalized,
            "The review-decision foreign key is not bound to the scoped proposal hash.");
        TestAssert.Contains(
            "target_revision_id, target_content_hash) references cortex_data.knowledge_revision (tenant_id, namespace_name, record_id, revision_id, content_hash)",
            normalized,
            "A lineage target is not bound to the exact referenced content hash.");
        TestAssert.Contains(
            "unique (tenant_id, namespace_name, record_id, revision_id, content_hash)",
            normalized,
            "The content-hash lineage foreign key lacks a matching unique target constraint.");

        TestAssert.Contains(" using (", normalized, "No RLS USING predicate is present.");
        TestAssert.Contains(" with check (", normalized, "No RLS WITH CHECK predicate is present.");
        TestAssert.Contains(
            "current_setting('pegasus.cortex.tenant_id', true)",
            normalized,
            "RLS does not fail closed through missing-ok tenant context.");
        TestAssert.Contains(
            "current_setting('pegasus.cortex.namespace', true)",
            normalized,
            "RLS does not fail closed through missing-ok namespace context.");
        TestAssert.Contains(
            "maximum_sensitivity = nullif(current_setting('pegasus.cortex.maximum_sensitivity', true), '')",
            normalized,
            "Collection metadata RLS is not bound to the caller's sensitivity ceiling.");
        TestAssert.Contains(
            "current_setting('pegasus.cortex.capability', true), '') = 'readiness'",
            normalized,
            "Internal store verification lacks a private RLS capability.");
        TestAssert.Contains(
            "create table cortex_meta.rls_policy_contract",
            normalized,
            "The migration does not preserve exact installed policy definitions for drift detection.");
        TestAssert.Contains(
            "insert into cortex_meta.rls_policy_contract",
            normalized,
            "The migration does not capture the installed RLS policy definitions.");
        TestAssert.Contains(
            "create trigger rls_policy_contract_immutable",
            normalized,
            "The captured RLS policy contract is mutable through ordinary DML.");

        foreach (var table in ImmutableTables)
        {
            TestAssert.Contains(
                $"create trigger {table}_immutable before update or delete on cortex_data.{table}",
                normalized,
                $"{table} lacks its immutable update/delete trigger.");
        }

        TestAssert.Contains(
            "to_tsvector('simple',",
            normalized,
            "The lexical document does not pin the simple configuration.");
        var revisionDefinition = ExtractCreateTable(initial, "cortex_data.knowledge_revision").ToLowerInvariant();
        foreach (var projection in new[] { "title", "summary", "content", "keyword_text" })
        {
            TestAssert.Contains(
                projection,
                revisionDefinition,
                $"The lexical document omits the {projection} projection.");
        }
        TestAssert.Contains(
            "using gin (search_document)",
            normalized,
            "The lexical search document lacks a GIN index.");
        foreach (var schema in new[] { "cortex_meta", "cortex_data", "cortex_private", "cortex_index" })
        {
            TestAssert.Contains(
                $"revoke all on schema {schema} from public;",
                normalized,
                $"Schema {schema} retains default PUBLIC privileges.");
        }
        TestAssert.Contains(
            "revoke all on all tables in schema cortex_data from public;",
            normalized,
            "Cortex data tables do not revoke PUBLIC privileges.");
        TestAssert.Contains(
            "revoke all on all functions in schema cortex_private from public;",
            normalized,
            "Cortex private functions do not revoke PUBLIC execution.");
        TestAssert.True(
            CountOccurrences(
                normalized,
                "payload bytea not null check (octet_length(payload) between 1 and ") >= 5,
            "Durable canonical payload columns are not bounded before materialization.");
        foreach (var textBoundary in new[]
                 {
                     "char_length(title) between 1 and 512",
                     "char_length(summary) between 1 and 2048",
                     "char_length(content) between 1 and 32768",
                     "char_length(keyword_text) between 0 and 8255",
                     "char_length(applicability::text) <= 131072"
                 })
        {
            TestAssert.Contains(
                textBoundary,
                normalized,
                $"The durable knowledge projection lacks its {textBoundary} boundary.");
        }
        return Task.CompletedTask;
    }

    public static Task OfflineSyncMigrationAsync()
    {
        var migration = PostgreSqlMigrationCatalog.All.Single(value => value.Id == "0002");
        var sql = migration.Sql;
        var normalized = NormalizeWhitespace(sql).ToLowerInvariant();

        TestAssert.Equal("0007", PostgreSqlMigrationCatalog.LatestId);
        TestAssert.True(
            PostgreSqlMigrationCatalog.OfflineSyncRuntimeAvailable,
            "The implemented repository and sync readiness boundary are not discoverable.");
        TestAssert.Equal(
            "repository_and_sync_readiness_available",
            PostgreSqlMigrationCatalog.OfflineSyncRuntimeAvailabilityReason);
        TestAssert.Contains(
            "attempt-first durable storage contract",
            normalized,
            "Migration 0002 does not state its attempt-first persistence boundary.");
        TestAssert.Contains(
            "base readiness boundary and the isolated sync readiness probe attest this schema",
            normalized,
            "Migration 0002 does not name both required runtime attestations.");
        TestAssert.Contains(
            "create schema cortex_sync;",
            normalized,
            "Offline-sync state is not isolated from the established Cortex data/readiness boundary.");
        TestAssert.Contains(
            "revoke all on schema cortex_sync from public;",
            normalized,
            "The offline-sync schema retains default PUBLIC privileges.");
        TestAssert.Contains(
            "revoke all on all tables in schema cortex_sync from public;",
            normalized,
            "Offline-sync tables retain default PUBLIC privileges.");

        var tables = new[]
        {
            "journal_batch",
            "journal_entry",
            "journal_commit",
            "journal_outcome",
            "journal_source_head",
            "journal_ack"
        };
        foreach (var table in tables)
        {
            var qualified = $"cortex_sync.{table}";
            var definition = NormalizeWhitespace(ExtractCreateTable(sql, qualified)).ToLowerInvariant();
            TestAssert.Contains("tenant_id", definition, $"{qualified} lacks tenant scope.");
            TestAssert.Contains("namespace_name", definition, $"{qualified} lacks namespace scope.");
            TestAssert.Contains("store_id", definition, $"{qualified} lacks store identity.");
            TestAssert.Contains("store_epoch", definition, $"{qualified} lacks the store epoch.");
            TestAssert.Contains("project_id", definition, $"{qualified} lacks project identity.");
            TestAssert.Contains("attempt_id", definition, $"{qualified} lacks immutable attempt identity.");
            TestAssert.Contains(
                "primary key (tenant_id, namespace_name, store_id, store_epoch, project_id",
                definition,
                $"{qualified} does not lead its primary key with the full durable scope.");
            TestAssert.Contains(
                $"alter table {qualified} enable row level security;",
                normalized,
                $"{qualified} does not enable RLS.");
            TestAssert.Contains(
                $"alter table {qualified} force row level security;",
                normalized,
                $"{qualified} does not force RLS for its owner.");
            TestAssert.Contains(
                $"create policy {table}_select_scope on {qualified} for select",
                normalized,
                $"{qualified} lacks its scoped read policy.");
            TestAssert.Contains(
                $"create policy {table}_append_scope on {qualified} for insert",
                normalized,
                $"{qualified} lacks its scoped append policy.");
            TestAssert.Contains(
                $"create trigger {table}_immutable before update or delete on {qualified}",
                normalized,
                $"{qualified} is not immutable after append.");
        }

        TestAssert.Equal(6, CountOccurrences(normalized, " for select "));
        TestAssert.Equal(6, CountOccurrences(normalized, " for insert "));
        TestAssert.Equal(
            12,
            CountOccurrences(
                normalized,
                "nullif(current_setting('pegasus.cortex.capability', true), '') = 'sync'"),
            "Every offline-sync SELECT and INSERT policy must require server-derived sync capability.");
        foreach (var contextName in new[]
                 {
                     "tenant_id", "namespace", "authority_id", "subject_id", "service_id",
                     "policy_revision", "capability"
                 })
        {
            TestAssert.Contains(
                $"current_setting('pegasus.cortex.{contextName}', true)",
                normalized,
                $"Offline-sync RLS omits server-derived {contextName} context.");
        }

        foreach (var exactCanonicalEvidence in new[]
                 {
                     "bundle_canonical bytea not null check (octet_length(bundle_canonical) between 1 and 33554432)",
                     "event_canonical bytea not null check (octet_length(event_canonical) between 1 and 1048576)",
                     "receipt_canonical bytea not null check (octet_length(receipt_canonical) between 1 and 33554432)",
                     "source_provenance_canonical bytea not null check (octet_length(source_provenance_canonical) between 1 and 8192)"
                 })
        {
            TestAssert.Contains(
                exactCanonicalEvidence,
                normalized,
                $"Canonical public evidence is absent or unbounded: {exactCanonicalEvidence}.");
        }
        TestAssert.True(
            CountOccurrences(
                normalized,
                "extensions_canonical bytea not null check (octet_length(extensions_canonical) between 2 and 131072)") == 3,
            "Bundle, event, and receipt extension evidence is not retained with one shared bound.");
        foreach (var canonicalHash in new[]
                 {
                     "bundle_canonical_sha256", "event_canonical_sha256", "receipt_canonical_sha256"
                 })
        {
            TestAssert.Contains(
                $"{canonicalHash} text collate \"c\" not null check ({canonicalHash} ~ '^[0-9a-f]{{64}}$')",
                normalized,
                $"Canonical evidence hash {canonicalHash} is missing or malformed.");
        }
        TestAssert.Contains(
            "payload bytea not null check (octet_length(payload) between 1 and 524288)",
            normalized,
            "Offline journal payloads are not bounded before materialization.");
        TestAssert.Contains(
            "payload_bytes = octet_length(payload)",
            normalized,
            "Decoded payload bytes are not bound to the public declared length.");
        TestAssert.Contains(
            "source_kind in ('client_operation', 'legacy_file_copy')",
            normalized,
            "Source provenance kinds are not constrained to the public contract.");
        TestAssert.Contains(
            "source_captured_at <= occurred_at",
            normalized,
            "Source provenance can claim capture after the event.");
        TestAssert.DoesNotContain(
            "auth_signature",
            normalized,
            "Migration requires a batch/event authentication field absent from the public contracts.");
        TestAssert.DoesNotContain(
            "auth_version",
            normalized,
            "Migration requires a batch/event authentication version absent from the public contracts.");

        foreach (var acceptedIdentityIndex in new[]
                 {
                     "journal_batch_accepted_bundle_id_unique",
                     "journal_batch_accepted_bundle_hash_unique",
                     "journal_batch_accepted_canonical_unique",
                     "journal_entry_accepted_sequence_unique",
                     "journal_entry_accepted_event_id_unique",
                     "journal_entry_accepted_event_hash_unique"
                 })
        {
            TestAssert.Contains(
                $"create unique index {acceptedIdentityIndex}",
                normalized,
                $"Accepted-chain replay protection {acceptedIdentityIndex} is missing.");
        }
        TestAssert.Equal(
            6,
            CountOccurrences(normalized, "where advances_source_head"),
            "Accepted identities must be unique without preventing durable conflict attempts.");
        TestAssert.DoesNotContain(
            "journal_batch_base_server_revision_fk",
            normalized,
            "A stale or missing base revision would prevent durable conflict evidence.");
        TestAssert.Contains(
            "source_sequence between bundle_first_source_sequence and bundle_last_source_sequence",
            normalized,
            "Later entry appends are not structurally confined to the immutable bundle range.");
        TestAssert.Contains(
            "attempt_id, bundle_id, event_id, source_sequence, event_hash ) references cortex_sync.journal_entry",
            normalized,
            "An outcome is not bound to the exact event inside the exact bundle.");
        TestAssert.Contains(
            "attempt_id, bundle_id, commit_id, storage_revision, commit_disposition ) references cortex_sync.journal_commit",
            normalized,
            "A source head can reference a non-successful commit attempt.");
        TestAssert.Contains(
            "commit_disposition text collate \"c\" not null check (commit_disposition = 'committed')",
            normalized,
            "Source-head advancement is not structurally limited to successful commits.");

        TestAssert.Contains(
            "create function cortex_private.validate_sync_bundle_integrity()",
            normalized,
            "Transaction-end bundle validation is missing.");
        TestAssert.Contains(
            "lag(entry_row.event_hash) over (order by entry_row.source_sequence)",
            normalized,
            "Event order and hash continuation are not checked as one bounded set.");
        TestAssert.Contains(
            "batch_value.previous_bundle_hash is distinct from base_bundle_hash",
            normalized,
            "Bundle continuation is not bound to the previous durable bundle hash.");
        TestAssert.Contains(
            "batch_value.prior_event_hash is distinct from base_chain_hash",
            normalized,
            "Bundle continuation is not bound to the previous durable event head.");
        TestAssert.Contains(
            "offline bundle has no durable commit attempt",
            normalized,
            "A new bundle can commit without a durable processing attempt.");
        TestAssert.Contains(
            "entry_row.advances_source_head is distinct from batch_value.advances_source_head",
            normalized,
            "Event projections can disagree with the attempt's accepted/conflict disposition.");
        TestAssert.Contains(
            "batch_value.advances_source_head is distinct from commit_value.advances_source_head",
            normalized,
            "Commit disposition can disagree with its attempt projection.");
        foreach (var provenanceField in new[]
                 {
                     "received_at", "authority_id", "subject_id", "service_id", "policy_revision"
                 })
        {
            TestAssert.Contains(
                $"batch_value.{provenanceField} is distinct from commit_value.{provenanceField}",
                normalized,
                $"Commit provenance is not bound to its batch field {provenanceField}.");
        }

        TestAssert.Contains(
            "create function cortex_private.validate_sync_commit_integrity()",
            normalized,
            "Transaction-end commit validation is missing.");
        TestAssert.Contains(
            "actual_outcome_count <> commit_value.source_event_count",
            normalized,
            "Commit outcome cardinality is not checked.");
        TestAssert.Contains(
            "source head does not equal the exact successful offline commit",
            normalized,
            "Source-head equality is not checked against the terminal bundle event.");
        TestAssert.Contains(
            "head_value.committed_bundle_hash is distinct from commit_value.bundle_hash",
            normalized,
            "The source head is not bound to the exact committed bundle hash.");
        TestAssert.Contains(
            "head_value.committed_chain_hash is distinct from terminal_event_hash",
            normalized,
            "The source head is not bound to the exact terminal event hash.");

        TestAssert.Contains(
            "reconciliation_state in ('reconciled', 'mismatch')",
            normalized,
            "Receipt storage admits a non-terminal reconciliation state.");
        TestAssert.DoesNotContain(
            "create table cortex_sync.journal_reconciliation",
            normalized,
            "Terminal reconciliation is split into unauthenticated follow-up evidence.");
        TestAssert.DoesNotContain(
            "reconciliation_state in ('pending'",
            normalized,
            "An immutable pending receipt can be stranded before terminal reconciliation.");
        TestAssert.Contains(
            "covered_receipt_hash = receipt_hash",
            normalized,
            "Receipt proof is not bound to the public semantic receipt hash.");
        TestAssert.Contains(
            "unique (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id, commit_id)",
            NormalizeWhitespace(ExtractCreateTable(sql, "cortex_sync.journal_ack")).ToLowerInvariant(),
            "More than one terminal receipt can be appended for the exact commit attempt.");
        TestAssert.Contains(
            "create function cortex_private.validate_sync_receipt_integrity()",
            normalized,
            "Terminal receipt projections are not checked against their exact commit.");
        TestAssert.Contains(
            "receipt_value.read_back_hash is distinct from commit_value.durable_state_hash",
            normalized,
            "A reconciled receipt can disagree with durable read-back state.");

        TestAssert.Equal(
            3,
            CountOccurrences(normalized, "create constraint trigger "),
            "Integrity scans must be queued once per parent bundle, commit, and receipt row.");
        foreach (var parentTrigger in new[]
                 {
                     "journal_batch_integrity", "journal_commit_integrity", "journal_ack_integrity"
                 })
        {
            TestAssert.Contains(
                $"create constraint trigger {parentTrigger}",
                normalized,
                $"Deferred integrity trigger {parentTrigger} is missing.");
        }
        foreach (var redundantChildTrigger in new[]
                 {
                     "journal_entry_integrity", "journal_outcome_integrity", "journal_source_head_integrity"
                 })
        {
            TestAssert.DoesNotContain(
                $"create constraint trigger {redundantChildTrigger}",
                normalized,
                $"Child trigger {redundantChildTrigger} would repeat full-set scans.");
        }
        TestAssert.True(
            CountOccurrences(normalized, "deferrable initially deferred") >= 10,
            "Cross-row foreign keys and parent integrity checks are not deferred to transaction end.");
        TestAssert.True(
            CountOccurrences(normalized, "security definer") == 3,
            "Integrity functions do not use one consistent protected execution boundary.");
        TestAssert.Contains(
            "revoke all on all functions in schema cortex_private from public;",
            normalized,
            "New private integrity functions retain default PUBLIC execution.");

        TestAssert.Contains(
            "create unique index journal_source_head_origin_unique",
            normalized,
            "A store epoch can record more than one origin head.");
        TestAssert.Contains(
            "create unique index journal_source_head_child_unique",
            normalized,
            "A durable source head can branch into multiple children.");
        TestAssert.Contains(
            "references cortex_sync.journal_source_head",
            normalized,
            "Bundle continuation does not reference immutable source-head state.");
        TestAssert.Contains(
            "create table cortex_sync.rls_policy_contract",
            normalized,
            "The isolated sync RLS policy inventory is missing.");
        TestAssert.Contains(
            "from cortex_sync.rls_policy_contract) <> 12",
            normalized,
            "The sync policy inventory does not fail on a missing or extra policy.");
        TestAssert.DoesNotContain(
            "insert into cortex_meta.rls_policy_contract",
            normalized,
            "The passive slice altered the established 0001 readiness policy inventory.");
        TestAssert.Contains(
            "create table cortex_sync.schema_object_contract",
            normalized,
            "The live sync catalog shape has no immutable readiness contract.");
        TestAssert.Contains(
            "insert into cortex_sync.schema_object_contract",
            normalized,
            "The sync schema-object contract is not captured from the installed catalog.");
        foreach (var catalogDefinition in new[]
                 {
                     "pg_get_constraintdef", "pg_get_indexdef", "pg_get_triggerdef", "pg_get_functiondef"
                 })
        {
            TestAssert.Contains(
                catalogDefinition,
                normalized,
                $"The sync schema contract omits {catalogDefinition} evidence.");
        }

        foreach (var forbidden in new[]
                 {
                     " for update ",
                     " for delete ",
                     "delete from ",
                     "truncate ",
                     "drop table ",
                     "drop schema ",
                     "cutover",
                     "prune",
                     "cleanup"
                 })
        {
            TestAssert.DoesNotContain(
                forbidden,
                normalized,
                $"Offline-sync migration contains forbidden destructive behavior: {forbidden.Trim()}.");
        }

        return Task.CompletedTask;
    }

    public static Task AuthorOwnedPublicationChangeMigrationAsync()
    {
        var migration = PostgreSqlMigrationCatalog.All.Single(value => value.Id == "0007");
        var normalized = NormalizeWhitespace(migration.Sql).ToLowerInvariant();

        foreach (var table in new[]
                 {
                     "knowledge_publication_origin",
                     "knowledge_publication_change"
                 })
        {
            TestAssert.Contains(
                $"create table cortex_data.{table}",
                normalized,
                $"Migration 0007 does not create {table}.");
            TestAssert.Contains(
                $"create trigger {table}_immutable before update or delete on cortex_data.{table}",
                normalized,
                $"{table} is not immutable.");
            TestAssert.Contains(
                $"alter table cortex_data.{table} force row level security",
                normalized,
                $"{table} does not force RLS.");
        }
        foreach (var ownershipColumn in new[]
                 {
                     "authority_id",
                     "subject_id",
                     "service_id",
                     "proposal_id",
                     "proposal_hash",
                     "origin_revision_id",
                     "origin_content_hash"
                 })
        {
            TestAssert.Contains(
                ownershipColumn,
                ExtractCreateTable(
                    migration.Sql,
                    "cortex_data.knowledge_publication_origin").ToLowerInvariant(),
                $"Publication origin omits immutable {ownershipColumn} ownership evidence.");
        }
        TestAssert.Contains(
            "references cortex_data.learning_proposal (tenant_id, namespace_name, proposal_id, proposal_hash)",
            normalized,
            "Publication ownership is not bound to the exact immutable proposal hash.");
        TestAssert.Contains(
            "references cortex_data.knowledge_revision (tenant_id, namespace_name, record_id, revision_id, content_hash)",
            normalized,
            "Publication ownership or replacement evidence is not bound to exact immutable content.");
        TestAssert.DoesNotContain(
            "insert into cortex_data.knowledge_publication_origin select",
            normalized,
            "Migration 0007 inferred owners for legacy publications instead of failing closed.");

        foreach (var functionName in new[]
                 {
                     "register_knowledge_publication_origin",
                     "apply_knowledge_publication_change"
                 })
        {
            TestAssert.Contains(
                $"create function cortex_private.{functionName}",
                normalized,
                $"The narrow {functionName} function is missing.");
            TestAssert.Contains(
                $"revoke all on function cortex_private.{functionName}",
                normalized,
                $"PUBLIC can execute {functionName}.");
        }
        TestAssert.True(
            CountOccurrences(normalized, "language plpgsql security definer") == 2,
            "Publication changes do not use exactly two narrow SECURITY DEFINER functions.");
        TestAssert.True(
            CountOccurrences(normalized, "set search_path = pg_catalog") == 2,
            "A SECURITY DEFINER function has an unsafe search path.");
        foreach (var contractFragment in new[]
                 {
                     "create table cortex_meta.privileged_function_contract",
                     "create trigger privileged_function_contract_immutable",
                     "pg_catalog.pg_get_functiondef(function_value.oid)",
                     "function_value.prosecdef",
                     "coalesce(function_value.proconfig, array[]::text[])",
                     "array['search_path=pg_catalog']::text[]"
                 })
        {
            TestAssert.Contains(
                contractFragment,
                normalized,
                $"Privileged-function contract omits {contractFragment}.");
        }
        TestAssert.Contains(
            "current_user <> session_user",
            normalized,
            "Private RLS policies do not distinguish definer execution from direct runtime SQL.");
        TestAssert.DoesNotContain(
            "grant update",
            normalized,
            "Migration 0007 grants general UPDATE instead of execute-only CAS authority.");
        TestAssert.DoesNotContain(
            "delete from cortex_data.knowledge_revision",
            normalized,
            "Publication changes physically delete immutable knowledge revisions.");
        TestAssert.DoesNotContain(
            "update cortex_data.knowledge_revision",
            normalized,
            "Publication changes rewrite immutable knowledge revisions.");

        TestAssert.Contains(
            "for update",
            normalized,
            "The expected head is not locked before the compare-and-swap transition.");
        TestAssert.Contains(
            "order by head_value.record_id collate \"c\" for update",
            normalized,
            "Target and replacement heads are not locked in deterministic order.");
        TestAssert.Contains(
            "current_revision_id = p_expected_head_revision_id and generation = p_expected_head_generation",
            normalized,
            "The head UPDATE is not bound to both expected revision and generation.");
        TestAssert.Contains(
            "using errcode = '40001'",
            normalized,
            "A lost compare-and-swap does not surface as a retryable serialization failure.");
        TestAssert.Contains(
            "return query select 'existing'::text",
            normalized,
            "An exact committed retry is not idempotent.");
        TestAssert.True(
            normalized.IndexOf("if found then", StringComparison.Ordinal) <
            normalized.IndexOf("lock the target and optional replacement heads", StringComparison.Ordinal),
            "Exact retries are evaluated only after current-head validation.");
        var replayStart = normalized.IndexOf("if found then", StringComparison.Ordinal);
        var replayEnd = normalized.IndexOf(
            "if p_change_id <>",
            replayStart,
            StringComparison.Ordinal);
        var replaySql = normalized[replayStart..replayEnd];
        TestAssert.Contains(
            "change_value.marker_payload_sha256 = p_marker_payload_sha256",
            replaySql,
            "Exact replay is not bound to the deterministic marker projection seal.");
        TestAssert.DoesNotContain(
            "from cortex_data.knowledge_revision",
            replaySql,
            "A applied, B head advance, retry A can falsely conflict on historical-marker RLS visibility.");
        TestAssert.Contains(
            "v_existing_change_hash <> p_change_hash",
            normalized,
            "A reused change ID with a different hash does not conflict.");
        foreach (var exactRetryProjection in new[]
                 {
                     "change_value.policy_revision = v_policy_revision",
                     "change_value.action = p_action",
                     "change_value.target_record_id = p_target_record_id",
                     "change_value.expected_head_revision_id = p_expected_head_revision_id",
                     "change_value.expected_head_generation = p_expected_head_generation",
                     "change_value.replacement_record_id is not distinct from p_replacement_record_id",
                     "change_value.reason_code = p_reason_code",
                     "change_value.reason = p_reason",
                     "change_value.marker_payload_sha256 = p_marker_payload_sha256",
                     "change_value.payload = p_change_payload",
                     "change_value.payload_sha256 = p_change_payload_sha256",
                     "change_value.occurred_at = p_occurred_at"
                 })
        {
            TestAssert.Contains(
                exactRetryProjection,
                normalized,
                $"Exact retry does not compare {exactRetryProjection}.");
        }
        foreach (var storageSealProjection in new[]
                 {
                     "change_value.auth_version = p_change_auth_version",
                     "change_value.auth_algorithm = p_change_auth_algorithm",
                     "change_value.auth_key_id = p_change_auth_key_id",
                     "change_value.auth_signature = p_change_auth_signature"
                 })
        {
            TestAssert.DoesNotContain(
                storageSealProjection,
                replaySql,
                "Storage-key rotation incorrectly changes logical retry identity.");
        }
        TestAssert.Contains(
            "return query select 'ownership_unavailable'::text",
            normalized,
            "Legacy publications without ownership do not fail closed.");
        TestAssert.Contains(
            "return query select 'not_owner'::text",
            normalized,
            "Non-owner publication changes do not fail closed.");
        TestAssert.Contains(
            "return query select 'stale_head'::text",
            normalized,
            "Stale head coordinates do not fail closed.");
        TestAssert.Contains(
            "return query select 'replacement_unavailable'::text",
            normalized,
            "Unavailable or cross-namespace replacements do not fail closed.");
        TestAssert.Contains(
            "replacement_head.namespace_name = v_namespace_name",
            normalized,
            "Replacement resolution is not fixed to the accepted event namespace.");
        TestAssert.Contains(
            "replacement_revision.record_status = 'active'",
            normalized,
            "A non-active replacement may supersede a publication.");
        TestAssert.Contains(
            "case p_action when 'supersede' then 'superseded' else 'tombstone' end",
            normalized,
            "The store-owned marker status mapping is missing.");
        foreach (var evidenceInsert in new[]
                 {
                     "insert into cortex_data.knowledge_revision",
                     "insert into cortex_data.knowledge_link",
                     "insert into cortex_data.audit_event",
                     "insert into cortex_data.knowledge_publication_change"
                 })
        {
            TestAssert.Contains(
                evidenceInsert,
                normalized,
                $"The atomic transition omits {evidenceInsert} evidence.");
        }

        var providerSql = PrivateSql(
            typeof(PostgreSqlLearningFeedbackRepository),
            "ApplyPublicationChangeSql");
        TestAssert.Contains(
            "cortex_private.apply_knowledge_publication_change",
            providerSql,
            "The PostgreSQL provider bypasses the narrow publication-change function.");
        TestAssert.DoesNotContain(
            "UPDATE cortex_data.knowledge_head",
            providerSql,
            "The provider performs a direct head UPDATE.");
        var providerSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "PegasusCortex.Persistence.PostgreSql",
            "Learning",
            "PostgreSqlLearningFeedbackRepository.PublicationChange.cs"));
        TestAssert.Contains(
            "IsolationLevel.Serializable",
            providerSource,
            "The provider does not run publication changes at SERIALIZABLE isolation.");

        foreach (var queryField in new[]
                 {
                     "CatalogSql",
                     "ExactSearchSql",
                     "BroadSearchSql",
                     "ReadSql"
                 })
        {
            TestAssert.Contains(
                "record_status = 'active'",
                PrivateSql(typeof(PostgreSqlCortexQuerySource), queryField),
                $"Normal {queryField} visibility does not hide superseded/tombstone heads.");
        }
        var exactRead = PrivateSql(typeof(PostgreSqlCortexQuerySource), "ReadSql");
        TestAssert.Contains(
            "FROM cortex_data.knowledge_head h",
            exactRead,
            "Exact reads can bypass the current publication head.");
        TestAssert.Contains(
            "k.revision_id = h.current_revision_id",
            exactRead,
            "Exact reads can materialize an old active revision after supersession.");

        var supersede = AcceptedPublicationChange(
            LearningFeedbackPublicationChangeActions.Supersede,
            LearningFeedbackPublicationChangeReasonCodes.Corrected,
            replacement: true);
        var supersedeMarker =
            PostgreSqlLearningFeedbackRepository.CreatePublicationChangeMarker(supersede);
        TestAssert.Equal("superseded", supersedeMarker.RecordStatus);
        TestAssert.Equal($"krev_{supersede.ChangeHash}", supersedeMarker.RevisionId);
        TestAssert.Equal(
            CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(supersedeMarker.Content)),
            supersedeMarker.ContentHash,
            "The immutable supersede marker content hash is not deterministic.");

        var withdrawal = AcceptedPublicationChange(
            LearningFeedbackPublicationChangeActions.Withdraw,
            LearningFeedbackPublicationChangeReasonCodes.UnsafeContent,
            replacement: false);
        var withdrawalMarker =
            PostgreSqlLearningFeedbackRepository.CreatePublicationChangeMarker(withdrawal);
        TestAssert.Equal("tombstone", withdrawalMarker.RecordStatus);
        TestAssert.Equal($"krev_{withdrawal.ChangeHash}", withdrawalMarker.RevisionId);

        return Task.CompletedTask;
    }

    public static async Task AuthorOwnedPublicationChangeServiceAsync()
    {
        var now = DateTimeOffset.Parse("2026-08-07T19:00:00Z");
        var authority = PublicationChangeAuthority();
        var request = PublicationChangeRequest(
            LearningFeedbackPublicationChangeActions.Supersede,
            LearningFeedbackPublicationChangeReasonCodes.Corrected,
            replacement: true,
            now);
        var repository = new PublicationChangeRepositoryDouble(
            LearningFeedbackPublicationChangeWriteDisposition.Applied);
        var service = new LearningFeedbackPublicationChangeService(
            repository,
            new FixedTimeProvider(now));

        var applied = await service.ChangeAsync(
            new ChangeLearningFeedbackPublicationCommand(
                "project:publication-change",
                request,
                authority),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Accepted, applied.Disposition);
        TestAssert.True(applied.Succeeded, "An applied publication change was not successful.");
        TestAssert.True(repository.AcceptedChange is not null, "The service did not persist the accepted event.");
        TestAssert.Equal(repository.AcceptedChange!.ChangeId, applied.ChangeId);
        TestAssert.Equal(
            repository.AcceptedChange.ExpectedHeadGeneration + 1,
            applied.HeadGeneration);

        repository.Disposition = LearningFeedbackPublicationChangeWriteDisposition.Existing;
        var retry = await service.ChangeAsync(
            new ChangeLearningFeedbackPublicationCommand(
                "project:publication-change",
                request,
                authority),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Idempotent, retry.Disposition);

        repository.Disposition = LearningFeedbackPublicationChangeWriteDisposition.NotOwner;
        var rejected = await service.ChangeAsync(
            new ChangeLearningFeedbackPublicationCommand(
                "project:publication-change",
                request,
                authority),
            CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, rejected.Disposition);
        TestAssert.False(rejected.Succeeded, "A non-owner publication change succeeded.");
        TestAssert.True(
            rejected.Diagnostics.All(diagnostic =>
                !diagnostic.Message.Contains(authority.SubjectId, StringComparison.Ordinal) &&
                (diagnostic.Context is null ||
                 !diagnostic.Context.ContainsKey("reason"))),
            "The rejection leaked author or reason content.");

        var publicProperties = typeof(LearningFeedbackPublicationChangeResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var prohibited in new[]
                 {
                     "Reason",
                     "AuthorityId",
                     "SubjectId",
                     "ServiceId",
                     "RecordContent"
                 })
        {
            TestAssert.False(
                publicProperties.Contains(prohibited),
                $"The content-safe service result exposes {prohibited}.");
        }
    }

    public static Task LegacyMigrationCursorAuthorityMigrationAsync()
    {
        var migration = PostgreSqlMigrationCatalog.All.Single(value => value.Id == "0003");
        var normalized = NormalizeWhitespace(migration.Sql).ToLowerInvariant();
        foreach (var required in new[]
                 {
                     "migration_page_canonical",
                     "migration_current_cursor_hash",
                     "migration_next_cursor_hash",
                     "journal_batch_migration_page_complete",
                     "journal_batch_accepted_migration_page_number_unique",
                     "journal_batch_accepted_migration_page_id_unique",
                     "journal_batch_accepted_migration_page_hash_unique",
                     "insert into cortex_sync.schema_object_contract"
                 })
        {
            TestAssert.Contains(required, normalized,
                $"Migration 0003 lacks required cursor-authority evidence '{required}'.");
        }
        foreach (var forbidden in new[] { " drop ", " delete ", " truncate ", " update cortex_sync.journal_batch" })
        {
            TestAssert.False(normalized.Contains(forbidden, StringComparison.Ordinal),
                $"Migration 0003 contains forbidden destructive behavior: {forbidden.Trim()}.");
        }
        return Task.CompletedTask;
    }

    public static Task HybridProjectRetentionMigrationAsync()
    {
        var migration = PostgreSqlMigrationCatalog.All.Single(value => value.Id == "0004");
        var normalized = NormalizeWhitespace(migration.Sql).ToLowerInvariant();
        foreach (var required in new[]
                 {
                     "create table cortex_data.project_link",
                     "create trigger project_link_immutable",
                     "alter table cortex_data.project_link force row level security",
                     "project_link_read_scope",
                     "project_link_correlate_scope",
                     "knowledge_revision_review_insert_scope",
                     "knowledge_head_review_insert_scope",
                     "record_status = 'active'",
                     "content_treatment = 'untrusted_data'",
                     "executable = false",
                     "generation = 1",
                     "insert into cortex_meta.rls_policy_contract",
                     "revoke all on table cortex_data.project_link from public"
                 })
        {
            TestAssert.Contains(
                required,
                normalized,
                $"Migration 0004 lacks required hybrid boundary '{required}'.");
        }
        foreach (var forbidden in new[] { " drop ", "delete from", " truncate ", " update cortex_data" })
        {
            TestAssert.False(
                normalized.Contains(forbidden, StringComparison.Ordinal),
                $"Migration 0004 contains forbidden destructive behavior: {forbidden.Trim()}.");
        }
        var listLinks = NormalizeWhitespace(PrivateSql(
            typeof(PostgreSqlProjectLinkRepository),
            "ListSql")).ToLowerInvariant();
        TestAssert.Contains(
            "order by project_link_id collate \"c\" limit @limit",
            listLinks,
            "Collective routing must enumerate project links through one bounded deterministic query.");
        return Task.CompletedTask;
    }

    public static Task AuditDelegationMigrationAsync()
    {
        var migration = PostgreSqlMigrationCatalog.All.Single(value => value.Id == "0005");
        var normalized = NormalizeWhitespace(migration.Sql).ToLowerInvariant();
        foreach (var required in new[]
                 {
                     "learning_proposal_audit_read_scope",
                     "learning_decision_audit_read_scope",
                     "learning_decision_audit_insert_scope",
                     "audit_event_auditor_read_scope",
                     "audit_event_auditor_append_scope",
                     "knowledge_revision_audit_insert_scope",
                     "knowledge_head_audit_insert_scope",
                     "in ('audit', 'delegate_review')",
                     "record_status = 'active'",
                     "content_treatment = 'untrusted_data'",
                     "executable = false",
                     "generation = 1",
                     "create index audit_event_kind_id_order",
                     "insert into cortex_meta.rls_policy_contract",
                     "count(*) from cortex_meta.rls_policy_contract) <> 23"
                 })
        {
            TestAssert.Contains(
                required,
                normalized,
                $"Migration 0005 lacks required audit boundary '{required}'.");
        }
        foreach (var forbidden in new[] { " drop ", " delete ", " truncate ", " update cortex_data" })
        {
            TestAssert.False(
                normalized.Contains(forbidden, StringComparison.Ordinal),
                $"Migration 0005 contains forbidden destructive behavior: {forbidden.Trim()}.");
        }

        var lockGrant = PrivateSql(
            typeof(PostgreSqlLearningFeedbackRepository),
            "LockGrantSql").ToLowerInvariant();
        var countUses = NormalizeWhitespace(PrivateSql(
            typeof(PostgreSqlLearningFeedbackRepository),
            "CountGrantUsesSql")).ToLowerInvariant();
        var grantUseKind = PrivateSql(
            typeof(PostgreSqlLearningFeedbackRepository),
            "ReviewGrantUseEventKind");
        var insertAudit = NormalizeWhitespace(PrivateSql(
            typeof(PostgreSqlLearningFeedbackRepository),
            "InsertAuditEventSql")).ToLowerInvariant();
        TestAssert.Contains(
            "pg_advisory_xact_lock(hashtextextended(@grant_id, 0))",
            lockGrant,
            "Grant consumption lacks a transaction-scoped per-grant serialization lock.");
        TestAssert.Contains(
            "event_kind = @event_kind",
            countUses,
            "Grant use accounting is not scoped to one typed event kind.");
        TestAssert.Equal(
            "learning-review-grant-use",
            grantUseKind,
            "Grant use accounting does not identify immutable use receipts.");
        TestAssert.Contains(
            "insert into cortex_data.audit_event",
            insertAudit,
            "Audit assessments, grants, revocations, and uses are not append-only events.");
        return Task.CompletedTask;
    }

    public static Task LearningMetadataPromotionAsync()
    {
        var access = new LearningFeedbackNamespaceAccess(
            "tenant-1",
            "project:project-1",
            "nexus",
            "reviewer-1",
            "cortex-review",
            PolicyRevision: 7,
            LearningFeedbackNamespaceCapability.Review);
        var legacyProposal = AcceptedPromotionProposal(metadata: false);
        var metadataProposal = AcceptedPromotionProposal(metadata: true);
        var legacyDecision = ProvisionalDecision(legacyProposal);
        var metadataDecision = ProvisionalDecision(metadataProposal);

        var legacyRecord = PostgreSqlLearningFeedbackRepository
            .CreateRetainedKnowledgeRecord(
                access,
                legacyProposal,
                legacyDecision,
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidence,
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidenceBasis);
        var metadataRecord = PostgreSqlLearningFeedbackRepository
            .CreateRetainedKnowledgeRecord(
                access,
                metadataProposal,
                metadataDecision,
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidence,
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidenceBasis);

        TestAssert.Equal(
            "continuity",
            legacyRecord.Taxonomy,
            "A legacy proposal did not retain the continuity taxonomy default.");
        TestAssert.Equal(0, legacyRecord.Keywords.Count);
        TestAssert.Equal(0, legacyRecord.Applicability.Count);
        TestAssert.Equal("testing", metadataRecord.Taxonomy);
        TestAssert.SequenceEqual(
            new[] { "bounded recovery", "rollback" },
            metadataRecord.Keywords,
            "Proposal keywords did not reach the searchable keyword projection.");
        TestAssert.Equal(2, metadataRecord.Applicability.Count);
        TestAssert.Equal("pegasus-coder", metadataRecord.Applicability[0].ProductId);
        TestAssert.Equal("coordination", metadataRecord.Applicability[0].ComponentId);
        TestAssert.Equal("unverified", metadataRecord.Applicability[0].State);
        TestAssert.Equal("pegasus-cortex", metadataRecord.Applicability[1].ProductId);
        TestAssert.Equal("persistence", metadataRecord.Applicability[1].ComponentId);
        TestAssert.Equal("unverified", metadataRecord.Applicability[1].State);
        TestAssert.Equal(
            "pegasus.cortex.learning-feedback-promotion/1.1",
            metadataRecord.Provenance.ProducerContract);
        TestAssert.True(
            metadataRecord.Provenance.Transformations.Contains(
                "provisional-policy-publication",
                StringComparer.Ordinal),
            "The metadata projection lost provisional-publication provenance.");

        const string projectContent =
            "Neural Commander reused pathfinding cache invalidation from " +
            "\"D:\\Codex Projects\\Neural Commander\\src\\Navigation.cs\" " +
            "on hostname=pegasus-ai-arm64 at 10.10.10.15.";
        var projectProposal = metadataProposal with
        {
            Namespace = "project:neural-commander",
            Summary = "Neural Commander fixed a reusable game pathfinding cache issue.",
            Source = metadataProposal.Source with
            {
                MachineId = "pegasus-ai-arm64",
                CorrelationId = $"plink_{new string('c', 64)}",
                ContentHash = CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(projectContent))
            },
            Payload = new SortedDictionary<string, string>(
                metadataProposal.Payload.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal)
            {
                ["content"] = projectContent,
                ["title"] = "Neural Commander pathfinding cache fix"
            },
            Keywords = ["Neural Commander pathfinding", "cache invalidation"],
            Applicability =
            [
                new LearningFeedbackApplicabilityV1("neural-commander", "navigation"),
                new LearningFeedbackApplicabilityV1("game-engine", "pathfinding")
            ]
        };
        var projectDecision = ProvisionalDecision(projectProposal);
        var projectRecord = PostgreSqlLearningFeedbackRepository
            .CreateRetainedKnowledgeRecord(
                access with { Namespace = projectProposal.Namespace },
                projectProposal,
                projectDecision,
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidence,
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidenceBasis);

        TestAssert.Contains(
            "pathfinding",
            projectRecord.Content,
            "Generalization removed the reusable domain concept.");
        TestAssert.Contains(
            LearningFeedbackCollectiveProjectionPolicy.ProjectPlaceholder,
            projectRecord.Content,
            "The project name was not replaced with a typed placeholder.");
        TestAssert.DoesNotContain(
            "Neural Commander",
            projectRecord.Content,
            "A provisionally published record leaked its source project name.");
        TestAssert.DoesNotContain(
            "D:\\Codex Projects",
            projectRecord.Content,
            "A provisionally published record leaked an absolute path.");
        TestAssert.DoesNotContain(
            "pegasus-ai-arm64",
            projectRecord.Content,
            "A provisionally published record leaked a host identifier.");
        TestAssert.DoesNotContain(
            "10.10.10.15",
            projectRecord.Content,
            "A provisionally published record leaked a network address.");
        TestAssert.True(
            projectRecord.Keywords.Any(item =>
                item.Contains("pathfinding", StringComparison.Ordinal)),
            "Generalized metadata is not discoverable by a related project query.");
        var relatedProjectMatch = CortexKnowledgeLexicalSearch.Match(
            projectRecord,
            new CortexSearchQuery(
                projectRecord.Namespace,
                "another game pathfinding cache problem",
                Limit: 3,
                IncludeBlockedOrUnverified: true,
                Mode: CortexSearchMode.Broad));
        TestAssert.True(
            relatedProjectMatch.IsMatch,
            "A related project could not discover the generalized lesson through broad search.");
        TestAssert.True(
            projectRecord.Provenance.Transformations.Contains(
                "deterministic-collective-generalization",
                StringComparer.Ordinal),
            "The derived projection lacks explicit generalization provenance.");

        const string unsafeContent = "api_key=sk-examplevalue12345678901234567890";
        var unsafeProposal = projectProposal with
        {
            Source = projectProposal.Source with
            {
                ContentHash = CortexCanonicalJson.Sha256(
                    Encoding.UTF8.GetBytes(unsafeContent))
            },
            Payload = new SortedDictionary<string, string>(
                projectProposal.Payload.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal)
            {
                ["content"] = unsafeContent
            }
        };
        var unsafeException = TestAssert.Throws<PostgreSqlPersistenceException>(() =>
            PostgreSqlLearningFeedbackRepository.CreateRetainedKnowledgeRecord(
                access with { Namespace = unsafeProposal.Namespace },
                unsafeProposal,
                ProvisionalDecision(unsafeProposal),
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidence,
                LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidenceBasis));
        TestAssert.Equal(
            "cortex_postgresql_collective_projection_rejected",
            unsafeException.Code,
            "Unsafe content did not fail closed at publication.");
        return Task.CompletedTask;
    }

    public static async Task MigrationRunnerAsync()
    {
        await using var dataSource = new TraceDbDataSource { MigrationLedgerExists = false };
        var result = await new PostgreSqlMigrator(dataSource)
            .ApplyAsync("test-release", CancellationToken.None)
            .ConfigureAwait(false);

        TestAssert.SequenceEqual(
            PostgreSqlMigrationCatalog.All.Select(migration => migration.Id),
            result.AppliedMigrationIds,
            "Migration runner did not apply the exact local migration sequence.");
        TestAssert.Equal(PostgreSqlMigrationCatalog.LatestId, result.CurrentMigrationId);
        AssertOrdered(
            dataSource.Events,
            "open",
            "begin:ReadCommitted",
            "nonquery:advisory-lock",
            "scalar:migration-ledger-exists",
            "nonquery:migration-sql",
            "nonquery:migration-ledger-insert",
            "commit");

        var advisory = dataSource.Commands.Single(command => command.Classification == "advisory-lock");
        TestAssert.Contains("pg_advisory_xact_lock(@lock_key)", advisory.CommandText, "Migration lock is not transaction scoped.");
        var lockParameter = advisory.Parameters.Single(parameter => parameter.Name == "lock_key");
        TestAssert.Equal(DbType.Int64, lockParameter.Type, "Migration lock key is not a stable 64-bit value.");
        TestAssert.True(lockParameter.Value is long, "Migration lock parameter is not an Int64 value.");

        await using var commitFailureSource = new TraceDbDataSource
        {
            MigrationLedgerExists = false,
            FailCommit = true
        };
        var commitFailure = await TestAssert.ThrowsAsync<PostgreSqlPersistenceException>(async () =>
            await new PostgreSqlMigrator(commitFailureSource)
                .ApplyAsync("test-release", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        TestAssert.Equal("cortex_postgresql_migration_commit_state_unknown", commitFailure.Code);
        TestAssert.False(commitFailure.Retryable, "An ambiguous migration commit was marked retryable.");
        TestAssert.True(commitFailureSource.Events.Contains("rollback"), "Ambiguous migration commit did not attempt cleanup.");
    }

    public static Task MigrationHistoryAsync()
    {
        var first = PostgreSqlMigrationCatalog.All[0];
        PostgreSqlMigrator.ValidateAppliedHistory([]);
        PostgreSqlMigrator.ValidateAppliedHistory([(first.Id, first.Sha256)]);

        var drift = TestAssert.Throws<PostgreSqlPersistenceException>(() =>
            PostgreSqlMigrator.ValidateAppliedHistory([(first.Id, new string('0', 64))]));
        TestAssert.Equal("cortex_postgresql_migration_history_invalid", drift.Code);
        TestAssert.False(drift.Retryable, "Checksum drift was incorrectly marked retryable.");

        var unknown = TestAssert.Throws<PostgreSqlPersistenceException>(() =>
            PostgreSqlMigrator.ValidateAppliedHistory(
                [(first.Id, first.Sha256), ("9999", new string('f', 64))]));
        TestAssert.Equal("cortex_postgresql_migration_history_invalid", unknown.Code);
        TestAssert.False(unknown.Retryable, "A newer database migration was incorrectly marked retryable.");
        return Task.CompletedTask;
    }

    public static Task TransactionContextMappingAsync()
    {
        var query = new CortexQuerySourceScope(
            "tenant-a",
            "authority-a",
            "subject-a",
            "service-a",
            17,
            "shared:pegasus-default-library",
            CortexQueryCapability.Search,
            "cortex.query.search",
            "internal",
            IncludeStale: true,
            IncludeBlockedOrUnverified: false);
        var queryContext = PostgreSqlTransactionContext.FromQuery(query);
        AssertContext(
            queryContext,
            query.TenantId,
            query.Namespace,
            query.AuthorityId,
            query.SubjectId,
            query.ServiceId,
            query.PolicyRevision,
            "search",
            query.Operation,
            "internal",
            allowStale: true,
            allowBlocked: false);

        var learning = new LearningFeedbackNamespaceAccess(
            "tenant-b",
            "private:learning",
            "authority-b",
            "subject-b",
            "service-b",
            23,
            LearningFeedbackNamespaceCapability.Review);
        var learningContext = PostgreSqlTransactionContext.FromLearning(
            learning,
            "cortex.learning.review");
        AssertContext(
            learningContext,
            learning.TenantId,
            learning.Namespace,
            learning.AuthorityId,
            learning.SubjectId,
            learning.ServiceId,
            learning.PolicyRevision,
            "review",
            "cortex.learning.review",
            "restricted",
            allowStale: false,
            allowBlocked: false);

        var learningCapabilityTokens =
            new (LearningFeedbackNamespaceCapability Capability, string Token)[]
            {
                (LearningFeedbackNamespaceCapability.Read, "read"),
                (LearningFeedbackNamespaceCapability.Submit, "submit"),
                (LearningFeedbackNamespaceCapability.Review, "review"),
                (LearningFeedbackNamespaceCapability.Audit, "audit"),
                (LearningFeedbackNamespaceCapability.DelegateReview, "delegate_review")
            };
        foreach (var (capability, token) in learningCapabilityTokens)
        {
            var context = PostgreSqlTransactionContext.FromLearning(
                learning with { Capability = capability },
                "cortex.learning.capability-token");
            TestAssert.Equal(
                token,
                context.Capability,
                $"Learning capability {capability} drifted from its PostgreSQL RLS token.");
        }

        var readinessContext = PostgreSqlTransactionContext.FromReadiness(query with
        {
            Capability = CortexQueryCapability.Status,
            Operation = "cortex.query.status",
            MaximumSensitivity = "restricted",
            IncludeStale = false
        });
        AssertContext(
            readinessContext,
            query.TenantId,
            query.Namespace,
            query.AuthorityId,
            query.SubjectId,
            query.ServiceId,
            query.PolicyRevision,
            "readiness",
            "cortex.postgresql.readiness.store",
            "restricted",
            allowStale: false,
            allowBlocked: false);
        return Task.CompletedTask;
    }

    public static Task TransactionContextSqlAsync()
    {
        var clean = PostgreSqlTransactionRunner.CleanContextCommandText;
        var bind = PostgreSqlTransactionRunner.BindContextCommandText;
        var names = new[]
        {
            "tenant_id", "namespace", "authority_id", "subject_id", "service_id",
            "policy_revision", "capability", "operation", "maximum_sensitivity",
            "allow_stale", "allow_blocked_or_unverified"
        };

        foreach (var name in names)
        {
            TestAssert.Contains(
                $"current_setting('pegasus.cortex.{name}', true)",
                clean,
                $"Pre-bind clean check omits {name} or is not missing-safe.");
            TestAssert.Contains(
                $"set_config('pegasus.cortex.{name}', @{name}, true)",
                bind,
                $"Bind SQL omits the parameterized transaction-local {name} value.");
        }
        TestAssert.Equal(names.Length, CountOccurrences(bind, "set_config("));
        TestAssert.Equal(names.Length, CountOccurrences(clean, " IS NULL"));
        return Task.CompletedTask;
    }

    public static async Task TransactionCommitSequenceAsync()
    {
        await using var dataSource = new TraceDbDataSource { ContextIsClean = true };
        var runner = new PostgreSqlTransactionRunner(dataSource);
        var context = SampleContext();
        var value = await runner.ExecuteAsync(
            context,
            IsolationLevel.RepeatableRead,
            async (connection, transaction, cancellationToken) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    "SELECT operation_marker(@value);",
                    new PostgreSqlParameter("value", DbType.Int32, 42));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return 42;
            },
            CancellationToken.None).ConfigureAwait(false);

        TestAssert.Equal(42, value);
        TestAssert.False(runner.IsPoisoned, "A successful transaction poisoned its data source.");
        AssertOrdered(
            dataSource.Events,
            "open",
            "begin:RepeatableRead",
            "scalar:clean-context",
            "nonquery:bind-context",
            "nonquery:operation",
            "commit");
        TestAssert.False(dataSource.Events.Contains("rollback"), "Successful transaction rolled back.");

        var bind = dataSource.Commands.Single(command => command.Classification == "bind-context");
        TestAssert.Equal(11, bind.Parameters.Count, "Bind command did not receive the exact context parameter set.");
        TestAssert.Equal(context.TenantId, bind.ParameterValue("tenant_id"));
        TestAssert.Equal(context.Namespace, bind.ParameterValue("namespace"));
        TestAssert.Equal(context.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), bind.ParameterValue("policy_revision"));
    }

    public static async Task TransactionCancellationAsync()
    {
        await using var dataSource = new TraceDbDataSource { ContextIsClean = true };
        var runner = new PostgreSqlTransactionRunner(dataSource);
        var cancellation = await TestAssert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await runner.ExecuteAsync<int>(
                SampleContext(),
                IsolationLevel.ReadCommitted,
                async (connection, transaction, cancellationToken) =>
                {
                    await using var command = PostgreSqlCommandFactory.Create(
                        connection,
                        transaction,
                        "SELECT operation_marker(@value);",
                        new PostgreSqlParameter("value", DbType.Int32, 7));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    throw new OperationCanceledException("Synthetic operation cancellation.");
                },
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        TestAssert.Contains("Synthetic", cancellation.Message, "Cancellation context was not retained.");
        TestAssert.True(dataSource.Events.Contains("rollback"), "Cancellation did not roll back its transaction.");
        TestAssert.False(dataSource.Events.Contains("commit"), "Cancellation committed its transaction.");
        TestAssert.False(runner.IsPoisoned, "A successfully rolled-back cancellation poisoned the data source.");
    }

    public static async Task TransactionContaminationAsync()
    {
        await using var dataSource = new TraceDbDataSource { ContextIsClean = false };
        var runner = new PostgreSqlTransactionRunner(dataSource, new ThrowingFaultSink());
        var failure = await TestAssert.ThrowsAsync<PostgreSqlPersistenceException>(async () =>
        {
            await runner.ExecuteAsync(
                SampleContext(),
                IsolationLevel.ReadCommitted,
                (_, _, _) => ValueTask.FromResult(1),
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        TestAssert.Equal("cortex_postgresql_context_contaminated", failure.Code);
        TestAssert.True(runner.IsPoisoned, "Contaminated pooled state did not poison the runner.");
        TestAssert.Equal(
            typeof(InvalidOperationException).FullName,
            runner.FaultSinkFailureType,
            "A fault-sink failure was not retained for readiness diagnostics.");
        TestAssert.True(dataSource.Events.Contains("rollback"), "Context contamination did not roll back its transaction.");
        TestAssert.False(
            dataSource.Commands.Any(command => command.Classification == "bind-context"),
            "Context values were bound after contamination was detected.");

        var opensBeforeRetry = dataSource.Events.Count(value => value == "open");
        var poisoned = await TestAssert.ThrowsAsync<PostgreSqlPersistenceException>(async () =>
        {
            await runner.ExecuteAsync(
                SampleContext(),
                IsolationLevel.ReadCommitted,
                (_, _, _) => ValueTask.FromResult(1),
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
        TestAssert.Equal("cortex_postgresql_data_source_poisoned", poisoned.Code);
        TestAssert.Equal(
            opensBeforeRetry,
            dataSource.Events.Count(value => value == "open"),
            "A poisoned runner opened another pooled connection.");
    }

    public static Task DependencyBoundaryAsync()
    {
        var assembly = typeof(PostgreSqlMigrationCatalog).Assembly;
        var banned = new[] { "Npgsql", "EntityFrameworkCore", "Dapper", "FluentMigrator", "DbUp" };
        var references = string.Join('\n', assembly.GetReferencedAssemblies().Select(reference => reference.Name));
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(
            root,
            "src",
            "PegasusCortex.Persistence.PostgreSql",
            "PegasusCortex.Persistence.PostgreSql.csproj");
        var assetsPath = Path.Combine(
            root,
            "src",
            "PegasusCortex.Persistence.PostgreSql",
            "obj",
            "project.assets.json");
        TestAssert.True(File.Exists(projectPath), "Provider project file is unavailable.");
        TestAssert.True(File.Exists(assetsPath), "Provider resolved-assets file is unavailable after the focused build.");
        var project = File.ReadAllText(projectPath);
        var assets = File.ReadAllText(assetsPath);
        foreach (var token in banned)
        {
            TestAssert.DoesNotContain(token, references, $"Provider assembly directly references {token}.");
            TestAssert.DoesNotContain(token, project, $"Provider project directly references {token}.");
            TestAssert.DoesNotContain(token, assets, $"Provider resolved assets contain {token}.");
        }

        TestAssert.Contains("System.Data.Common", references, "Provider does not retain its BCL DbDataSource boundary.");
        return Task.CompletedTask;
    }

    public static Task QueryAndReadinessSqlAsync()
    {
        var recordColumns = PrivateSql(typeof(PostgreSqlCortexQuerySource), "RecordColumns");
        foreach (var guard in new[]
                 {
                     "char_length(k.title) BETWEEN 1 AND 512",
                     "char_length(k.summary) BETWEEN 1 AND 2048",
                     "char_length(k.content) BETWEEN 1 AND 32768",
                     "char_length(k.keyword_text) BETWEEN 0 AND 8255",
                     "char_length(k.applicability::text) <= 131072"
                 })
        {
            TestAssert.Contains(guard, recordColumns, $"Query projections lack the {guard} pre-materialization guard.");
        }

        var exactRead = PrivateSql(typeof(PostgreSqlCortexQuerySource), "ReadSql");
        TestAssert.Contains(
            "FROM cortex_data.knowledge_head h",
            exactRead,
            "Exact reads are not bound to the current publication head.");
        TestAssert.Contains(
            "k.revision_id = h.current_revision_id",
            exactRead,
            "Exact reads can expose an old active revision after supersession or withdrawal.");

        var exactSearch = PrivateSql(typeof(PostgreSqlCortexQuerySource), "ExactSearchSql");
        TestAssert.Contains(
            "plainto_tsquery('simple'::regconfig, @search_text)",
            exactSearch,
            "Exact search no longer preserves its conjunctive lexical contract.");
        TestAssert.DoesNotContain(
            "' | '",
            exactSearch,
            "Exact search unexpectedly opted into broad OR matching.");
        TestAssert.Contains(
            "setweight(to_tsvector('simple'::regconfig, k.taxonomy), 'B')",
            exactSearch,
            "Exact search no longer includes taxonomy in its cross-field conjunction.");

        var broadSearch = PrivateSql(typeof(PostgreSqlCortexQuerySource), "BroadSearchSql");
        TestAssert.Contains(
            "string_agg(quote_literal(term), ' | ' ORDER BY term)",
            broadSearch,
            "Broad search no longer ORs the exact finite fold variants supplied by the shared matcher.");
        TestAssert.DoesNotContain(
            ":*",
            broadSearch,
            "Broad search reintroduced prefix candidates that can crowd valid folded terms out of the bounded page.");
        foreach (var weightedField in new[]
                 {
                     "k.title), 'A'",
                     "k.taxonomy), 'B'",
                     "k.keyword_text), 'B'",
                     "k.summary), 'C'",
                     "k.content), 'D'"
                 })
        {
            TestAssert.Contains(
                weightedField,
                broadSearch,
                $"Broad search lacks the expected field weight for {weightedField}.");
        }
        TestAssert.Contains(
            "k.search_document ||",
            broadSearch,
            "Broad search no longer combines the stored document with searchable taxonomy terms.");

        var snapshotRead = PrivateSql(typeof(PostgreSqlCortexQuerySource), "SnapshotSql");
        TestAssert.Contains(
            "snapshot.maximum_sensitivity = head_value.maximum_sensitivity",
            snapshotRead,
            "Snapshot reads can cross sensitivity-specific collection heads.");
        TestAssert.Contains(
            "snapshot.maximum_sensitivity = @maximum_sensitivity",
            snapshotRead,
            "Snapshot reads are not bound to the exact authorized sensitivity ceiling.");

        var runtime = PrivateSql(typeof(PostgreSqlPersistenceReadinessProbe), "RuntimeSql");
        TestAssert.Contains(
            "pg_has_role(current_user, reachable_role.oid, 'MEMBER')",
            runtime,
            "Readiness does not preserve pre-16 role-reachability semantics.");
        TestAssert.Contains(
            "pg_has_role(current_user, reachable_role.oid, 'USAGE')",
            runtime,
            "Readiness does not reject privileges inherited from a reachable role.");
        TestAssert.Contains(
            "pg_has_role(current_user, reachable_role.oid, 'SET')",
            runtime,
            "Readiness does not reject PostgreSQL 16+ SET-role reachability.");
        TestAssert.Contains(
            "pg_has_role(current_user, admin_role.oid, 'MEMBER WITH ADMIN OPTION')",
            runtime,
            "Readiness permits an ADMIN-only membership to self-escalate or propagate access.");
        TestAssert.Contains(
            ">= 160000",
            runtime,
            "Readiness does not version-gate PostgreSQL 16 role-membership semantics.");
        TestAssert.Contains(
            "session_user = current_user",
            runtime,
            "Readiness permits a privileged authenticated session to hide behind SET ROLE.");
        TestAssert.Contains(
            "cortex_meta",
            runtime,
            "Readiness does not audit migration-ledger ownership and privileges.");
        TestAssert.Contains(
            "schema_migrations",
            runtime,
            "Readiness omits the unprotected migration ledger from its runtime-role audit.");
        TestAssert.Contains(
            "rls_policy_contract",
            runtime,
            "Readiness omits the exact RLS policy contract from its runtime-role audit.");
        TestAssert.Contains(
            "has_table_privilege(current_user, class_value.oid, 'INSERT')",
            runtime,
            "Readiness permits direct migration-ledger inserts.");
        TestAssert.Contains(
            "has_table_privilege(current_user, 'cortex_data.audit_event', 'INSERT')",
            runtime,
            "Readiness can accept a runtime role that cannot append delegated audit events.");
        TestAssert.Contains(
            "has_table_privilege(reachable_role.oid, reachable_class.oid, 'INSERT')",
            runtime,
            "Readiness permits SET-reachable migration-ledger inserts.");
        TestAssert.Contains(
            "has_any_column_privilege(current_user, class_value.oid, 'UPDATE')",
            runtime,
            "Readiness permits direct column-level migration-ledger updates.");
        TestAssert.Contains(
            "has_any_column_privilege(reachable_role.oid, reachable_class.oid, 'UPDATE')",
            runtime,
            "Readiness permits SET-reachable column-level migration-ledger updates.");
        TestAssert.Contains(
            "'MAINTAIN'",
            runtime,
            "Readiness does not reject PostgreSQL 17+ MAINTAIN privileges.");
        TestAssert.Contains(
            "FROM pg_proc",
            runtime,
            "Readiness does not reject ownership of its security-definer helper functions.");
        foreach (var protectedObject in new[]
                 {
                     "privileged_function_contract",
                     "knowledge_publication_origin",
                     "knowledge_publication_change",
                     "register_knowledge_publication_origin",
                     "apply_knowledge_publication_change"
                 })
        {
            TestAssert.Contains(
                protectedObject,
                runtime,
                $"Readiness omits protected publication-change object {protectedObject}.");
        }
        foreach (var functionAttestation in new[]
                 {
                     "pg_catalog.pg_get_functiondef(function_value.oid)",
                     "function_value.prosecdef",
                     "function_value.proconfig",
                     "owner_role.rolname",
                     "installed.function_definition IS DISTINCT FROM expected.function_definition"
                 })
        {
            TestAssert.Contains(
                functionAttestation,
                runtime,
                $"Readiness omits privileged-function attestation {functionAttestation}.");
        }
        TestAssert.Contains(
            "relkind IN ('r', 'p', 'S')",
            runtime,
            "Readiness does not include migration identity sequences in its ownership audit.");
        TestAssert.Contains(
            "has_sequence_privilege(current_user, class_value.oid, 'USAGE')",
            runtime,
            "Readiness permits direct use of a migration identity sequence.");
        TestAssert.Contains(
            "has_sequence_privilege(reachable_role.oid, reachable_class.oid, 'UPDATE')",
            runtime,
            "Readiness permits SET-reachable mutation of a migration identity sequence.");

        var rowLevelSecurity = PrivateSql(
            typeof(PostgreSqlPersistenceReadinessProbe),
            "RowLevelSecuritySql");
        TestAssert.Contains(
            "FROM pg_policies",
            rowLevelSecurity,
            "Readiness does not attest the installed RLS policy catalog.");
        TestAssert.Contains(
            "SELECT COUNT(*) = (SELECT COUNT(*) FROM required_table)",
            rowLevelSecurity,
            "Readiness must bind the forced-RLS count to the declared required-table inventory.");

        var promotedRevision = PrivateSql(
            typeof(PostgreSqlLearningFeedbackRepository),
            "InsertPromotedRevisionSql");
        var promotedHead = PrivateSql(
            typeof(PostgreSqlLearningFeedbackRepository),
            "InsertPromotedHeadSql");
        TestAssert.False(
            promotedRevision.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase),
            "Append-only retention must not require review-time SELECT visibility through ON CONFLICT.");
        TestAssert.False(
            promotedHead.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase),
            "Append-only retention heads must not require review-time SELECT visibility through ON CONFLICT.");
        TestAssert.Contains(
            "SELECT COUNT(*) = 39 FROM installed_policy",
            rowLevelSecurity,
            "Readiness does not reject missing or additional Cortex policies.");
        TestAssert.Contains(
            "SELECT COUNT(*) = 39 FROM recorded_policy",
            rowLevelSecurity,
            "Readiness does not require the immutable migration-time policy contract.");
        TestAssert.Contains(
            "installed_policy.qual IS DISTINCT FROM recorded_policy.using_expression",
            rowLevelSecurity,
            "Readiness does not compare installed and recorded USING expressions exactly.");
        TestAssert.Contains(
            "installed_policy.with_check IS DISTINCT FROM recorded_policy.check_expression",
            rowLevelSecurity,
            "Readiness does not compare installed and recorded WITH CHECK expressions exactly.");
        foreach (var policyName in new[]
                 {
                     "collection_snapshot_scope",
                     "knowledge_revision_read_scope",
                     "learning_proposal_submit_scope",
                     "learning_decision_review_scope",
                     "project_link_read_scope",
                     "project_link_correlate_scope",
                     "knowledge_revision_review_insert_scope",
                     "knowledge_head_review_insert_scope",
                     "learning_proposal_audit_read_scope",
                     "learning_decision_audit_insert_scope",
                     "audit_event_auditor_read_scope",
                     "audit_event_auditor_append_scope",
                     "knowledge_revision_audit_insert_scope",
                     "knowledge_head_audit_insert_scope",
                     "knowledge_revision_publication_private_read_scope",
                     "knowledge_revision_current_head_restrictive_scope",
                     "knowledge_revision_publication_change_insert_scope",
                     "knowledge_head_publication_private_read_scope",
                     "knowledge_head_publication_change_update_scope",
                     "knowledge_link_publication_change_insert_scope",
                     "knowledge_publication_origin_private_read_scope",
                     "knowledge_publication_origin_private_insert_scope",
                     "knowledge_publication_change_private_read_scope",
                     "knowledge_publication_change_private_insert_scope",
                     "audit_event_append_scope",
                     "generation_state_read_scope"
                 })
        {
            TestAssert.Contains(
                policyName,
                rowLevelSecurity,
                $"Readiness does not attest the {policyName} policy.");
        }
        foreach (var contextName in new[]
                 {
                     "pegasus.cortex.tenant_id",
                     "pegasus.cortex.namespace",
                     "pegasus.cortex.maximum_sensitivity",
                     "pegasus.cortex.capability",
                     "pegasus.cortex.allow_stale",
                     "pegasus.cortex.allow_blocked_or_unverified"
                 })
        {
            TestAssert.Contains(
                contextName,
                rowLevelSecurity,
                $"Readiness policy attestation omits {contextName}.");
        }

        var sentinelScopeIsolation = PrivateSql(
            typeof(PostgreSqlPersistenceReadinessProbe),
            "SentinelScopeIsolationSql");
        TestAssert.Contains(
            "cortex_data.collection_snapshot",
            sentinelScopeIsolation,
            "Readiness does not behaviorally probe cross-tenant collection isolation.");
        TestAssert.Contains(
            "cortex_data.knowledge_revision",
            sentinelScopeIsolation,
            "Readiness does not behaviorally probe cross-tenant knowledge isolation.");
        TestAssert.Contains(
            "tenant_id = @sentinel_tenant AND namespace_name = @sentinel_namespace",
            sentinelScopeIsolation,
            "Cross-scope probes are not pinned to the known populated sentinel scope.");
        var publicTierIsolation = PrivateSql(
            typeof(PostgreSqlPersistenceReadinessProbe),
            "PublicTierIsolationSql");
        TestAssert.Contains(
            "maximum_sensitivity <> 'public'",
            publicTierIsolation,
            "Readiness does not behaviorally probe sensitivity-tier isolation.");
        TestAssert.Contains(
            "sensitivity <> 'public'",
            publicTierIsolation,
            "Readiness does not behaviorally probe knowledge-revision sensitivity isolation.");

        var reachableRoles = PrivateSql(typeof(PostgreSqlPersistenceReadinessProbe), "ReachableRolesSql");
        TestAssert.Contains(
            "pg_has_role(current_user, reachable_role.oid, 'MEMBER')",
            reachableRoles,
            "Readiness does not enumerate pre-16 role membership for allowlist evaluation.");
        TestAssert.Contains(
            "pg_has_role(current_user, reachable_role.oid, 'USAGE')",
            reachableRoles,
            "Readiness does not enumerate inherited roles for allowlist evaluation.");
        TestAssert.Contains(
            "pg_has_role(current_user, reachable_role.oid, 'SET')",
            reachableRoles,
            "Readiness does not enumerate PostgreSQL 16+ SET-reachable roles for allowlist evaluation.");
        TestAssert.Contains(
            "pg_has_role(current_user, reachable_role.oid, 'MEMBER WITH ADMIN OPTION')",
            reachableRoles,
            "Readiness does not enumerate ADMIN-only memberships for categorical rejection.");

        var tiers = PrivateValue<string[]>(typeof(PostgreSqlPersistenceReadinessProbe), "SensitivityTiers");
        TestAssert.SequenceEqual(
            new[] { "public", "internal", "confidential", "restricted" },
            tiers,
            "Readiness does not require all supported sensitivity projections.");

        var store = PrivateSql(typeof(PostgreSqlPersistenceReadinessProbe), "StoreStateSql");
        TestAssert.Contains(
            "snapshot_value.payload_sha256",
            store,
            "Readiness does not bind the authenticated snapshot to the probed store.");
        TestAssert.Contains(
            "state_value.generation = head_value.generation",
            store,
            "Readiness accepts an index generation unrelated to the current collection head.");
        TestAssert.Contains(
            "state_value.indexed_through >= head_value.updated_at",
            store,
            "Readiness accepts an index timestamp older than the current collection head.");

        var constructor = typeof(PostgreSqlPersistenceReadinessProbe).GetConstructors().Single();
        TestAssert.Equal(
            typeof(ICortexRowAuthenticator),
            constructor.GetParameters()[1].ParameterType,
            "Readiness accepts an arbitrary query source instead of constructing one over its probed data source.");

        using var dataSource = new TraceDbDataSource();
        var scope = new CortexQuerySourceScope(
            "tenant-a",
            "authority-a",
            "subject-a",
            "service-a",
            1,
            "shared:pegasus-default-library",
            CortexQueryCapability.Status,
            "cortex.query.status",
            "restricted",
            IncludeStale: false,
            IncludeBlockedOrUnverified: false);
        TestAssert.Throws<ArgumentException>(() => new PostgreSqlPersistenceReadinessProbe(
            new PostgreSqlTransactionRunner(dataSource),
            new UnusedRowAuthenticator(),
            scope,
            new PostgreSqlReadinessOptions(
                MinimumServerMajor: 14,
                RequireTls: true,
                HostTlsPolicyVerified: true,
                AllowedReachableRoleNames: ["pg_read_server_files"])));

        var snapshot = new CortexKnowledgeSourceSnapshot(
            "1.2026.07.14.12.00.A",
            "1.20260714.1200-a",
            "collection-1",
            new string('a', 64),
            scope.Namespace,
            1,
            1,
            ["cortex"],
            [new CortexFreshnessCount("current", 1)],
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            CortexQueryPolicy.SafetyNotice);
        TestAssert.True(
            SnapshotReady(snapshot, scope.Namespace, requireNonEmpty: true),
            "A valid restricted readiness snapshot was rejected.");
        TestAssert.False(
            SnapshotReady(snapshot with { ReleaseVersion = new string('r', 129) }, scope.Namespace, true),
            "Readiness accepted a release version that Core rejects.");
        TestAssert.False(
            SnapshotReady(snapshot with { DefaultReadableRecordCount = 0 }, scope.Namespace, true),
            "Readiness accepted a default-readable count inconsistent with current freshness.");
        TestAssert.True(
            SnapshotReady(
                snapshot with { RecordCount = 0, DefaultReadableRecordCount = 0, Freshness = [] },
                scope.Namespace,
                requireNonEmpty: false),
            "Readiness rejected a valid empty lower-sensitivity projection.");
        return Task.CompletedTask;
    }

    public static Task RecoveryPolicyAsync()
    {
        var cases = new[]
        {
            (
                Evidence(
                    source: true, backup: true, committed: true, compatible: true,
                    restore: true, history: true, authentic: true, heads: true, index: true),
                PostgreSqlRecoveryAction.Continue),
            (
                Evidence(
                    source: true, backup: true, committed: true, compatible: true,
                    restore: true, history: true, authentic: true, heads: false, index: true),
                PostgreSqlRecoveryAction.RollBackApplication),
            (
                Evidence(
                    source: true, backup: true, committed: true, compatible: false,
                    restore: true, history: false, authentic: true, heads: true, index: true),
                PostgreSqlRecoveryAction.RestoreIntoIsolation),
            (
                Evidence(
                    source: false, backup: false, committed: true, compatible: false,
                    restore: false, history: false, authentic: false, heads: false, index: false),
                PostgreSqlRecoveryAction.BlockAndPreserveEvidence),
            (
                Evidence(
                    source: false, backup: true, committed: false, compatible: true,
                    restore: true, history: true, authentic: true, heads: true, index: true),
                PostgreSqlRecoveryAction.BlockAndPreserveEvidence)
        };

        foreach (var (evidence, expected) in cases)
        {
            var decision = PostgreSqlRecoveryPolicy.Evaluate(evidence);
            TestAssert.Equal(expected, decision.Action, "Recovery evidence selected the wrong bounded plan.");
            TestAssert.False(
                decision.DestructiveActionAuthorized,
                $"Recovery action {decision.Action} authorized a destructive operation.");
        }

        return Task.CompletedTask;
    }

    private static string PrivateSql(Type type, string fieldName)
        => PrivateValue<string>(type, fieldName);

    private static T PrivateValue<T>(Type type, string fieldName) where T : class
    {
        var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Field '{type.FullName}.{fieldName}' is unavailable.");
        return field.GetValue(null) as T
            ?? throw new InvalidOperationException($"Field '{type.FullName}.{fieldName}' is not {typeof(T).Name}.");
    }

    private static bool SnapshotReady(
        CortexKnowledgeSourceSnapshot snapshot,
        string expectedNamespace,
        bool requireNonEmpty)
    {
        var method = typeof(PostgreSqlPersistenceReadinessProbe).GetMethod(
            "SnapshotValidForReadiness",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Readiness snapshot validator is unavailable.");
        return method.Invoke(null, [snapshot, expectedNamespace, requireNonEmpty]) is true;
    }

    private sealed class UnusedRowAuthenticator : ICortexRowAuthenticator
    {
        public CortexRowSeal Seal(CortexRowAuthenticationEnvelope envelope) =>
            throw new InvalidOperationException("The constructor must not use row-authentication keys.");

        public bool Verify(CortexRowAuthenticationEnvelope envelope, CortexRowSeal seal) =>
            throw new InvalidOperationException("The constructor must not use row-authentication keys.");
    }

    private static PostgreSqlTransactionContext SampleContext() =>
        new(
            "tenant-a",
            "shared:pegasus-default-library",
            "authority-a",
            "subject-a",
            "service-a",
            19,
            "search",
            "cortex.query.search",
            "internal",
            AllowStale: false,
            AllowBlockedOrUnverified: false);

    private static PostgreSqlRecoveryEvidence Evidence(
        bool source,
        bool backup,
        bool committed,
        bool compatible,
        bool restore,
        bool history,
        bool authentic,
        bool heads,
        bool index) =>
        new(source, backup, committed, compatible, restore, history, authentic, heads, index);

    private static void AssertContext(
        PostgreSqlTransactionContext actual,
        string tenantId,
        string namespaceName,
        string authorityId,
        string subjectId,
        string serviceId,
        long policyRevision,
        string capability,
        string operation,
        string maximumSensitivity,
        bool allowStale,
        bool allowBlocked)
    {
        TestAssert.Equal(tenantId, actual.TenantId);
        TestAssert.Equal(namespaceName, actual.Namespace);
        TestAssert.Equal(authorityId, actual.AuthorityId);
        TestAssert.Equal(subjectId, actual.SubjectId);
        TestAssert.Equal(serviceId, actual.ServiceId);
        TestAssert.Equal(policyRevision, actual.PolicyRevision);
        TestAssert.Equal(capability, actual.Capability);
        TestAssert.Equal(operation, actual.Operation);
        TestAssert.Equal(maximumSensitivity, actual.MaximumSensitivity);
        TestAssert.Equal(allowStale, actual.AllowStale);
        TestAssert.Equal(allowBlocked, actual.AllowBlockedOrUnverified);
    }

    private static string ExtractCreateTable(string sql, string table)
    {
        var marker = $"CREATE TABLE {table}";
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        TestAssert.True(start >= 0, $"Migration does not create {table}.");
        var end = sql.IndexOf("\n);", start, StringComparison.Ordinal);
        TestAssert.True(end > start, $"Migration table definition for {table} is not bounded.");
        return sql[start..(end + 3)];
    }

    private static IReadOnlyList<string> ExtractTenantTables(string sql) =>
        Regex.Matches(
                sql,
                "^CREATE TABLE (?<name>cortex_(?:data|index)\\.[a-z_]+)[ \\t]*$",
                RegexOptions.CultureInvariant | RegexOptions.Multiline,
                TimeSpan.FromSeconds(1))
            .Select(match => match.Groups["name"].Value)
            .ToArray();

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(
            value,
            "\\s+",
            " ",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

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

    private static void AssertOrdered(IReadOnlyList<string> events, params string[] expected)
    {
        var position = -1;
        foreach (var value in expected)
        {
            var next = IndexOf(events, value, position + 1);
            TestAssert.True(next >= 0, $"Trace event '{value}' is missing after position {position}: {string.Join(", ", events)}");
            position = next;
        }
    }

    private static int IndexOf(IReadOnlyList<string> values, string expected, int start)
    {
        for (var index = start; index < values.Count; index++)
        {
            if (string.Equals(values[index], expected, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static LearningFeedbackProposalV1 AcceptedPromotionProposal(bool metadata)
    {
        var now = DateTimeOffset.Parse("2026-08-07T18:00:00Z");
        const string content =
            "Use bounded recovery checkpoints and verify rollback before activation.";
        var screeningHash = new string('b', 64);
        var authority = new LearningFeedbackAuthorityContext(
            "tenant-1",
            "nexus",
            "submitter-1",
            "pegasus-coder",
            PolicyRevision: 7,
            [new LearningFeedbackNamespaceGrant("project:project-1", CanSubmit: true)],
            new HashSet<string>(["cortex-screening"], StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["screening-1"] = screeningHash
            });
        var submitted = new LearningFeedbackProposalV1(
            SchemaVersion: 1,
            metadata
                ? LearningFeedbackContractIds.ProposalV1_1
                : LearningFeedbackContractIds.Proposal,
            LearningFeedbackContractIds.Canonicalization,
            metadata ? "proposal-metadata" : "proposal-legacy",
            authority.TenantId,
            "project:project-1",
            "quality",
            new LearningFeedbackSourceV1(
                authority.AuthorityId,
                authority.SubjectId,
                authority.ServiceId,
                AgentId: "codex",
                InstanceId: null,
                MachineId: "endpoint-1",
                OperationId: "inline-content-submit",
                TaskId: null,
                CorrelationId: "plink-1",
                RecordId: "inline-content-1",
                RevisionId: "inline-content-revision-1",
                ContentHash: CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(content)),
                OccurredAt: now),
            "Bound recovery and rollback behavior.",
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["content"] = content,
                ["retentionRecord"] = "true",
                ["sourceDisposition"] = "inline-submitted-content",
                ["title"] = "Bound recovery before activation"
            },
            ["retrieval", "evaluation"],
            Evidence: [],
            new LearningFeedbackScreeningV1(
                "sanitized",
                "passed",
                "passed",
                "passed",
                "passed",
                "evaluation",
                authority.PolicyRevision,
                "cortex-screening",
                "screening-1",
                screeningHash,
                now),
            ReviewState: "pending",
            LearningFeedbackContractIds.ContentTreatment,
            Executable: false,
            CreatedAt: now,
            ProposalHash: new string('0', 64),
            Taxonomy: metadata ? "testing" : null,
            Keywords: metadata ? ["rollback", "bounded recovery"] : null,
            Applicability: metadata
                ?
                [
                    new LearningFeedbackApplicabilityV1("pegasus-cortex", "persistence"),
                    new LearningFeedbackApplicabilityV1("pegasus-coder", "coordination")
                ]
                : null);
        var accepted = LearningFeedbackPolicy.AcceptProposal(
            submitted,
            authority,
            now,
            CancellationToken.None);
        TestAssert.True(accepted.Accepted, "The promotion proposal fixture was rejected.");
        return accepted.Value!;
    }

    private static LearningFeedbackReviewDecisionV1 ProvisionalDecision(
        LearningFeedbackProposalV1 proposal)
    {
        var decision = new LearningFeedbackReviewDecisionV1(
            SchemaVersion: 1,
            LearningFeedbackContractIds.ReviewDecision,
            LearningFeedbackContractIds.Canonicalization,
            $"decision-{proposal.ProposalId}",
            proposal.ProposalId,
            proposal.ProposalHash,
            proposal.TenantId,
            proposal.Namespace,
            "approved",
            LearningFeedbackPublicationPolicy.ProvisionalAuthorityId,
            LearningFeedbackPublicationPolicy.ProvisionalReviewerSubjectId,
            PolicyRevision: 7,
            LearningFeedbackPublicationPolicy.ProvisionalReason,
            proposal.CreatedAt,
            DecisionHash: new string('0', 64));
        return decision with
        {
            DecisionHash = LearningFeedbackPolicy.ComputeDecisionHash(decision)
        };
    }

    private static LearningFeedbackPublicationChangeV1 AcceptedPublicationChange(
        string action,
        string reasonCode,
        bool replacement)
    {
        var now = DateTimeOffset.Parse("2026-08-07T19:00:00Z");
        var access = new LearningFeedbackNamespaceAccess(
            "tenant-publication",
            "project:publication-change",
            "nexus",
            "author-1",
            "pegasus-coder",
            17,
            LearningFeedbackNamespaceCapability.Submit);
        var accepted = LearningFeedbackPublicationChangePolicy.Accept(
            PublicationChangeRequest(action, reasonCode, replacement, now),
            access,
            now,
            CancellationToken.None);
        TestAssert.True(
            accepted.Accepted,
            "The publication-change fixture was rejected by the neutral contract.");
        return accepted.Value!;
    }

    private static LearningFeedbackPublicationChangeRequestV1 PublicationChangeRequest(
        string action,
        string reasonCode,
        bool replacement,
        DateTimeOffset occurredAt) =>
        new(
            1,
            LearningFeedbackContractIds.PublicationChange,
            LearningFeedbackContractIds.Canonicalization,
            action,
            $"krec_{new string('a', 64)}",
            $"krev_{new string('b', 64)}",
            ExpectedHeadGeneration: 7,
            replacement
                ? new LearningFeedbackPublicationReplacementV1(
                    $"krec_{new string('c', 64)}",
                    $"krev_{new string('d', 64)}",
                    new string('e', 64))
                : null,
            reasonCode,
            reasonCode == LearningFeedbackPublicationChangeReasonCodes.UnsafeContent
                ? "The publication contains unsafe guidance and must be withdrawn."
                : "The author corrected inaccurate durable guidance.",
            occurredAt);

    private static LearningFeedbackAuthorityContext PublicationChangeAuthority() =>
        new(
            "tenant-publication",
            "nexus",
            "author-1",
            "pegasus-coder",
            PolicyRevision: 17,
            [new LearningFeedbackNamespaceGrant(
                "project:publication-change",
                CanSubmit: true)],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed class PublicationChangeRepositoryDouble(
        LearningFeedbackPublicationChangeWriteDisposition disposition) :
        ILearningFeedbackPublicationChangeRepository
    {
        public LearningFeedbackPublicationChangeWriteDisposition Disposition { get; set; } =
            disposition;

        public LearningFeedbackPublicationChangeV1? AcceptedChange { get; private set; }

        public ValueTask<LearningFeedbackPublicationChangeWriteResult>
            ApplyPublicationChangeAsync(
                LearningFeedbackNamespaceAccess access,
                LearningFeedbackPublicationChangeV1 change,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcceptedChange = change;
            var headRevision = Disposition switch
            {
                LearningFeedbackPublicationChangeWriteDisposition.Applied or
                LearningFeedbackPublicationChangeWriteDisposition.Existing =>
                    $"krev_{change.ChangeHash}",
                LearningFeedbackPublicationChangeWriteDisposition.StaleHead or
                LearningFeedbackPublicationChangeWriteDisposition.ReplacementUnavailable =>
                    change.ExpectedHeadRevisionId,
                _ => null
            };
            long? headGeneration = Disposition switch
            {
                LearningFeedbackPublicationChangeWriteDisposition.Applied or
                LearningFeedbackPublicationChangeWriteDisposition.Existing =>
                    change.ExpectedHeadGeneration + 1,
                LearningFeedbackPublicationChangeWriteDisposition.StaleHead or
                LearningFeedbackPublicationChangeWriteDisposition.ReplacementUnavailable =>
                    change.ExpectedHeadGeneration,
                _ => null
            };
            return ValueTask.FromResult(new LearningFeedbackPublicationChangeWriteResult(
                Disposition,
                change.ChangeId,
                change.TargetRecordId,
                headRevision,
                headGeneration,
                change.Replacement?.RecordId,
                change.Replacement?.RevisionId));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                    Directory.Exists(Path.Combine(current.FullName, "src", "PegasusCortex.Persistence.PostgreSql")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex repository root.");
    }

    private sealed class ThrowingFaultSink : IPostgreSqlDataSourceFaultSink
    {
        public void Report(PostgreSqlDataSourceFault fault) =>
            throw new InvalidOperationException("Synthetic fault-sink failure.");
    }
}
