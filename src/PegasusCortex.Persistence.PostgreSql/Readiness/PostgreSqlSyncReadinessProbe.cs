using System.Data;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Migrations;
using Pegasus.Cortex.Persistence.PostgreSql.Sync;

namespace Pegasus.Cortex.Persistence.PostgreSql.Readiness;

public sealed record PostgreSqlSyncReadiness(
    PostgreSqlReadinessState State,
    bool MigrationCurrent,
    bool SchemaContractPresent,
    bool RuntimePrivilegesRestricted,
    bool RowLevelSecurityForced,
    bool PolicyContractMatches,
    IReadOnlyList<string> IssueCodes,
    DateTimeOffset ObservedAt);

/// <summary>
/// Attests the isolated synchronization schema after base PostgreSQL readiness succeeds.
/// It does not infer global service health or mutate the journal.
/// </summary>
public sealed class PostgreSqlSyncReadinessProbe
{
    private const string ProbeSql = """
        WITH journal_table(table_name) AS
        (
            VALUES
                ('journal_batch'),
                ('journal_entry'),
                ('journal_commit'),
                ('journal_outcome'),
                ('journal_source_head'),
                ('journal_ack')
        ),
        readable_table(table_name) AS
        (
            SELECT table_name FROM journal_table
            UNION ALL VALUES ('rls_policy_contract'), ('schema_object_contract')
        ),
        readonly_table(table_name) AS
        (
            VALUES ('rls_policy_contract'), ('schema_object_contract')
        ),
        installed_schema_object AS
        (
            SELECT
                'schema'::text AS object_kind,
                namespace_value.nspname::text AS object_identity,
                owner_role.rolname::text AS owner_name,
                namespace_value.nspname::text AS object_definition
            FROM pg_catalog.pg_namespace AS namespace_value
            JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = namespace_value.nspowner
            WHERE namespace_value.nspname = 'cortex_sync'

            UNION ALL

            SELECT
                'relation',
                namespace_value.nspname || '.' || relation_value.relname,
                owner_role.rolname,
                pg_catalog.concat_ws(
                    '|',
                    relation_value.relkind::text,
                    relation_value.relpersistence::text,
                    relation_value.relispartition::text,
                    relation_value.relrowsecurity::text,
                    relation_value.relforcerowsecurity::text)
            FROM pg_catalog.pg_class AS relation_value
            JOIN pg_catalog.pg_namespace AS namespace_value
              ON namespace_value.oid = relation_value.relnamespace
            JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = relation_value.relowner
            WHERE namespace_value.nspname = 'cortex_sync'
              AND relation_value.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')

            UNION ALL

            SELECT
                'column',
                namespace_value.nspname || '.' || relation_value.relname || '.' || attribute_value.attname,
                owner_role.rolname,
                pg_catalog.concat_ws(
                    '|',
                    attribute_value.attnum::text,
                    pg_catalog.format_type(attribute_value.atttypid, attribute_value.atttypmod),
                    attribute_value.attnotnull::text,
                    attribute_value.attidentity::text,
                    attribute_value.attgenerated::text,
                    attribute_value.attstorage::text,
                    COALESCE(collation_namespace.nspname || '.' || collation_value.collname, ''),
                    COALESCE(pg_catalog.pg_get_expr(default_value.adbin, default_value.adrelid, true), ''))
            FROM pg_catalog.pg_attribute AS attribute_value
            JOIN pg_catalog.pg_class AS relation_value ON relation_value.oid = attribute_value.attrelid
            JOIN pg_catalog.pg_namespace AS namespace_value
              ON namespace_value.oid = relation_value.relnamespace
            JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = relation_value.relowner
            LEFT JOIN pg_catalog.pg_attrdef AS default_value
              ON default_value.adrelid = attribute_value.attrelid
             AND default_value.adnum = attribute_value.attnum
            LEFT JOIN pg_catalog.pg_collation AS collation_value
              ON collation_value.oid = attribute_value.attcollation
            LEFT JOIN pg_catalog.pg_namespace AS collation_namespace
              ON collation_namespace.oid = collation_value.collnamespace
            WHERE namespace_value.nspname = 'cortex_sync'
              AND relation_value.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND attribute_value.attnum > 0
              AND NOT attribute_value.attisdropped

            UNION ALL

            SELECT
                'constraint',
                namespace_value.nspname || '.' || relation_value.relname || '.' || constraint_value.conname,
                owner_role.rolname,
                pg_catalog.concat_ws(
                    '|',
                    constraint_value.contype::text,
                    constraint_value.condeferrable::text,
                    constraint_value.condeferred::text,
                    constraint_value.convalidated::text,
                    constraint_value.connoinherit::text,
                    pg_catalog.pg_get_constraintdef(constraint_value.oid, true))
            FROM pg_catalog.pg_constraint AS constraint_value
            JOIN pg_catalog.pg_class AS relation_value ON relation_value.oid = constraint_value.conrelid
            JOIN pg_catalog.pg_namespace AS namespace_value
              ON namespace_value.oid = relation_value.relnamespace
            JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = relation_value.relowner
            WHERE namespace_value.nspname = 'cortex_sync'

            UNION ALL

            SELECT
                'index',
                namespace_value.nspname || '.' || table_value.relname || '.' || index_value.relname,
                owner_role.rolname,
                pg_catalog.concat_ws(
                    '|',
                    index_contract.indisunique::text,
                    index_contract.indisprimary::text,
                    index_contract.indisexclusion::text,
                    index_contract.indisclustered::text,
                    index_contract.indisreplident::text,
                    index_contract.indisvalid::text,
                    index_contract.indisready::text,
                    index_contract.indislive::text,
                    pg_catalog.pg_get_indexdef(index_contract.indexrelid))
            FROM pg_catalog.pg_index AS index_contract
            JOIN pg_catalog.pg_class AS table_value ON table_value.oid = index_contract.indrelid
            JOIN pg_catalog.pg_class AS index_value ON index_value.oid = index_contract.indexrelid
            JOIN pg_catalog.pg_namespace AS namespace_value
              ON namespace_value.oid = table_value.relnamespace
            JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = index_value.relowner
            WHERE namespace_value.nspname = 'cortex_sync'

            UNION ALL

            SELECT
                'trigger',
                namespace_value.nspname || '.' || table_value.relname || '.' || trigger_value.tgname,
                table_owner.rolname || '|' || function_owner.rolname,
                pg_catalog.concat_ws(
                    '|',
                    trigger_value.tgenabled::text,
                    trigger_value.tgtype::text,
                    COALESCE(trigger_constraint.conname, ''),
                    COALESCE(trigger_constraint.condeferrable::text, ''),
                    COALESCE(trigger_constraint.condeferred::text, ''),
                    function_namespace.nspname || '.' || function_value.proname,
                    pg_catalog.pg_get_triggerdef(trigger_value.oid, true))
            FROM pg_catalog.pg_trigger AS trigger_value
            JOIN pg_catalog.pg_class AS table_value ON table_value.oid = trigger_value.tgrelid
            JOIN pg_catalog.pg_namespace AS namespace_value ON namespace_value.oid = table_value.relnamespace
            JOIN pg_catalog.pg_roles AS table_owner ON table_owner.oid = table_value.relowner
            JOIN pg_catalog.pg_proc AS function_value ON function_value.oid = trigger_value.tgfoid
            JOIN pg_catalog.pg_namespace AS function_namespace
              ON function_namespace.oid = function_value.pronamespace
            JOIN pg_catalog.pg_roles AS function_owner ON function_owner.oid = function_value.proowner
            LEFT JOIN pg_catalog.pg_constraint AS trigger_constraint
              ON trigger_constraint.oid = trigger_value.tgconstraint
            WHERE namespace_value.nspname = 'cortex_sync'
              AND NOT trigger_value.tgisinternal

            UNION ALL

            SELECT
                'function',
                function_namespace.nspname || '.' || function_value.proname || '(' ||
                    pg_catalog.pg_get_function_identity_arguments(function_value.oid) || ')',
                owner_role.rolname,
                pg_catalog.pg_get_functiondef(function_value.oid)
            FROM pg_catalog.pg_proc AS function_value
            JOIN pg_catalog.pg_namespace AS function_namespace
              ON function_namespace.oid = function_value.pronamespace
            JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = function_value.proowner
            WHERE function_namespace.nspname = 'cortex_private'
              AND function_value.proname IN
                  ('reject_immutable_change', 'validate_sync_bundle_integrity',
                   'validate_sync_commit_integrity', 'validate_sync_receipt_integrity')
        ),
        schema_object_difference AS
        (
            SELECT 1
            FROM installed_schema_object AS installed
            FULL OUTER JOIN cortex_sync.schema_object_contract AS expected
              ON expected.object_kind = installed.object_kind
             AND expected.object_identity = installed.object_identity
            WHERE installed.object_kind IS NULL OR expected.object_kind IS NULL OR
                  expected.owner_name IS DISTINCT FROM installed.owner_name OR
                  expected.object_definition IS DISTINCT FROM installed.object_definition
        ),
        policy_difference AS
        (
            SELECT
                COALESCE(installed.tablename::text, expected.table_name) AS table_name,
                COALESCE(installed.policyname::text, expected.policy_name) AS policy_name
            FROM pg_catalog.pg_policies AS installed
            FULL OUTER JOIN cortex_sync.rls_policy_contract AS expected
              ON installed.schemaname = 'cortex_sync'
             AND expected.table_name = installed.tablename::text
             AND expected.policy_name = installed.policyname::text
             AND expected.permissive = installed.permissive
             AND expected.role_names = installed.roles::text[]
             AND expected.command_name = installed.cmd
             AND expected.using_expression IS NOT DISTINCT FROM installed.qual
             AND expected.check_expression IS NOT DISTINCT FROM installed.with_check
            WHERE (installed.schemaname = 'cortex_sync' OR installed.schemaname IS NULL)
              AND
              (
                  installed.tablename IS NULL OR expected.table_name IS NULL OR
                  expected.permissive IS DISTINCT FROM installed.permissive OR
                  expected.role_names IS DISTINCT FROM installed.roles::text[] OR
                  expected.command_name IS DISTINCT FROM installed.cmd OR
                  expected.using_expression IS DISTINCT FROM installed.qual OR
                  expected.check_expression IS DISTINCT FROM installed.with_check
              )
        )
        SELECT
            EXISTS
            (
                SELECT 1
                FROM cortex_meta.schema_migrations
                WHERE migration_id = '0003' AND up_sha256 = @migration_sha256
            ) AS migration_current,
            (
                SELECT COUNT(*) = 8
                FROM readable_table
                WHERE to_regclass('cortex_sync.' || table_name) IS NOT NULL
            ) AND
            (SELECT COUNT(*) > 0 FROM cortex_sync.schema_object_contract) AND
            NOT EXISTS (SELECT 1 FROM schema_object_difference) AS schema_contract_present,
            has_schema_privilege(current_user, 'cortex_sync', 'USAGE') AND
            NOT has_schema_privilege(current_user, 'cortex_sync', 'CREATE') AND
            NOT EXISTS
            (
                SELECT 1
                FROM readonly_table
                WHERE has_table_privilege(
                    current_user,
                    'cortex_sync.' || table_name,
                    'INSERT') OR
                      has_any_column_privilege(
                          current_user,
                          'cortex_sync.' || table_name,
                          'INSERT')
            ) AND
            NOT EXISTS
            (
                SELECT 1
                FROM readable_table
                WHERE NOT has_table_privilege(
                    current_user,
                    'cortex_sync.' || table_name,
                    'SELECT')
            ) AND
            NOT EXISTS
            (
                SELECT 1
                FROM journal_table
                WHERE NOT has_table_privilege(
                    current_user,
                    'cortex_sync.' || table_name,
                    'INSERT')
            ) AND
            NOT EXISTS
            (
                SELECT 1
                FROM readable_table
                WHERE has_table_privilege(current_user, 'cortex_sync.' || table_name, 'UPDATE') OR
                      has_any_column_privilege(current_user, 'cortex_sync.' || table_name, 'UPDATE') OR
                      has_table_privilege(current_user, 'cortex_sync.' || table_name, 'DELETE') OR
                      has_table_privilege(current_user, 'cortex_sync.' || table_name, 'TRUNCATE') OR
                      has_table_privilege(current_user, 'cortex_sync.' || table_name, 'REFERENCES') OR
                      has_any_column_privilege(current_user, 'cortex_sync.' || table_name, 'REFERENCES') OR
                      has_table_privilege(current_user, 'cortex_sync.' || table_name, 'TRIGGER') OR
                      CASE
                          WHEN current_setting('server_version_num')::integer >= 170000
                          THEN has_table_privilege(
                              current_user,
                              'cortex_sync.' || table_name,
                              'MAINTAIN')
                          ELSE false
                      END
            ) AS runtime_privileges_restricted,
            (
                SELECT COUNT(*) = 6
                FROM journal_table AS expected
                JOIN pg_catalog.pg_class AS relation
                  ON relation.relname = expected.table_name
                JOIN pg_catalog.pg_namespace AS schema_value
                  ON schema_value.oid = relation.relnamespace
                WHERE schema_value.nspname = 'cortex_sync'
                  AND relation.relrowsecurity
                  AND relation.relforcerowsecurity
            ) AS row_level_security_forced,
            (SELECT COUNT(*) FROM cortex_sync.rls_policy_contract) = 12 AND
            NOT EXISTS (SELECT 1 FROM policy_difference) AS policy_contract_matches;
        """;

    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly CortexSyncRepositoryScope _scope;
    private readonly TimeProvider _timeProvider;

    public PostgreSqlSyncReadinessProbe(
        PostgreSqlTransactionRunner transactions,
        CortexSyncRepositoryScope sentinelScope,
        TimeProvider? timeProvider = null)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        if (!PostgreSqlSyncCanonicalCodec.ValidScope(sentinelScope))
            throw new ArgumentException("A valid server-derived sync sentinel scope is required.", nameof(sentinelScope));
        _scope = sentinelScope with { Operation = "cortex.postgresql.sync.readiness" };
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<PostgreSqlSyncReadiness> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var migration = PostgreSqlMigrationCatalog.All.Single(value => value.Id == "0003");
        try
        {
            return await _transactions.ExecuteAsync(
                PostgreSqlTransactionContext.FromSync(_scope),
                IsolationLevel.ReadCommitted,
                async (connection, transaction, token) =>
                {
                    await using var command = PostgreSqlCommandFactory.Create(
                        connection,
                        transaction,
                        ProbeSql,
                        new PostgreSqlParameter("migration_sha256", DbType.String, migration.Sha256));
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        return Result(false, false, false, false, false, "cortex_postgresql_sync_readiness_row_missing");
                    var result = Result(
                        reader.GetBoolean(0),
                        reader.GetBoolean(1),
                        reader.GetBoolean(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4));
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                        return Result(false, false, false, false, false, "cortex_postgresql_sync_readiness_row_ambiguous");
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(false, false, false, false, false, "cortex_postgresql_sync_readiness_probe_failed");
        }
    }

    private PostgreSqlSyncReadiness Result(
        bool migrationCurrent,
        bool schemaContractPresent,
        bool runtimePrivilegesRestricted,
        bool rowLevelSecurityForced,
        bool policyContractMatches,
        string? additionalIssue = null)
    {
        var issues = new List<string>(6);
        if (!migrationCurrent)
            issues.Add("cortex_postgresql_sync_migration_not_current");
        if (!schemaContractPresent)
            issues.Add("cortex_postgresql_sync_schema_contract_missing");
        if (!runtimePrivilegesRestricted)
            issues.Add("cortex_postgresql_sync_runtime_privileges_invalid");
        if (!rowLevelSecurityForced)
            issues.Add("cortex_postgresql_sync_rls_not_forced");
        if (!policyContractMatches)
            issues.Add("cortex_postgresql_sync_policy_contract_mismatch");
        if (additionalIssue is not null)
            issues.Add(additionalIssue);
        var ordered = issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new PostgreSqlSyncReadiness(
            ordered.Length == 0 ? PostgreSqlReadinessState.Ready : PostgreSqlReadinessState.NotReady,
            migrationCurrent,
            schemaContractPresent,
            runtimePrivilegesRestricted,
            rowLevelSecurityForced,
            policyContractMatches,
            ordered,
            _timeProvider.GetUtcNow().ToUniversalTime());
    }
}
