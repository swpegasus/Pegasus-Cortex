using System.Data;
using Npgsql;
using Pegasus.Cortex.Service.Persistence;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Applies and independently verifies the exact runtime-role privileges required by the
/// destructive-free integration fixture. Pegasus Server creates the database and login
/// roles; the production Cortex migrator and this isolated fixture both reconcile the
/// same versioned object-level least-privilege contract.
/// </summary>
internal static class FixtureRuntimePrivilegeBootstrap
{
    private static readonly string[] Schemas =
        [.. CortexPostgreSqlRuntimePrivilegeProvisioner.RequiredSchemas];

    private static readonly string[] ReadTables =
        [.. CortexPostgreSqlRuntimePrivilegeProvisioner.ReadTables];

    private static readonly string[] AppendTables =
        [.. CortexPostgreSqlRuntimePrivilegeProvisioner.AppendTables];

    private static readonly string[] ProtectedTables =
        [.. CortexPostgreSqlRuntimePrivilegeProvisioner.ProtectedTables];

    private static readonly string[] RequiredFunctions =
        [.. CortexPostgreSqlRuntimePrivilegeProvisioner.RequiredFunctions];

    private static readonly string[] SyncJournalTables =
    [
        "journal_batch",
        "journal_entry",
        "journal_commit",
        "journal_outcome",
        "journal_source_head",
        "journal_ack"
    ];

    internal static IReadOnlyList<string> RequiredSchemas => Schemas;
    internal static IReadOnlyList<string> RequiredReadTables => ReadTables;
    internal static IReadOnlyList<string> RequiredAppendTables => AppendTables;
    internal static IReadOnlyList<string> RequiredProtectedTables => ProtectedTables;
    internal static IReadOnlyList<string> RequiredRuntimeFunctions => RequiredFunctions;

    public static async Task ApplyAndVerifyAsync(
        NpgsqlDataSource migrationDataSource,
        FixtureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrationDataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        // Exercise the production reconciler first; the queries below independently
        // attest the resulting owner, RLS, table, and function boundaries.
        var provisioner = new CortexPostgreSqlRuntimePrivilegeProvisioner(
            migrationDataSource,
            configuration.MigrationUsername,
            configuration.RuntimeUsername);
        await provisioner.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await migrationDataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await SetLocalTimeoutsAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var identities = await ReadAndValidateIdentitiesAsync(
                    connection,
                    transaction,
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            await ValidateOwnedObjectsAsync(
                    connection,
                    transaction,
                    identities.MigrationRoleOid,
                    cancellationToken)
                .ConfigureAwait(false);
            await ValidateSyncRowSecurityAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            await ValidatePublicationChangeRowSecurityAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);

            await VerifyExactPrivilegesAsync(
                    connection,
                    transaction,
                    identities.RuntimeRoleOid,
                    identities.DatabaseName,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception originalException)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The privilege bootstrap failed and its transaction rollback also failed.",
                    originalException,
                    rollbackException);
            }

            throw;
        }
    }

    private static async Task SetLocalTimeoutsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FixtureDatabaseIdentities> ReadAndValidateIdentitiesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FixtureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                current_database(),
                current_user,
                migration_role.oid,
                runtime_role.oid,
                runtime_role.rolname,
                runtime_role.rolcanlogin,
                runtime_role.rolinherit,
                runtime_role.rolsuper,
                runtime_role.rolcreatedb,
                runtime_role.rolcreaterole,
                runtime_role.rolreplication,
                runtime_role.rolbypassrls,
                NOT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_auth_members membership
                    WHERE membership.member = runtime_role.oid
                       OR membership.roleid = runtime_role.oid
                ) AS membership_is_empty,
                database_value.datdba = migration_role.oid AS migration_owns_database
            FROM pg_catalog.pg_roles migration_role
            JOIN pg_catalog.pg_roles runtime_role ON runtime_role.rolname = @runtime_role
            JOIN pg_catalog.pg_database database_value
              ON database_value.datname = current_database()
            WHERE migration_role.rolname = current_user;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("runtime_role", NpgsqlTypes.NpgsqlDbType.Text, configuration.RuntimeUsername);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The fixture could not resolve its migration and runtime roles.");
        var databaseName = reader.GetString(0);
        var migrationRoleName = reader.GetString(1);
        var migrationRoleOid = reader.GetFieldValue<uint>(2);
        var runtimeRoleOid = reader.GetFieldValue<uint>(3);
        var runtimeRoleName = reader.GetString(4);
        var runtimeIsRestricted =
            reader.GetBoolean(5) &&
            !reader.GetBoolean(6) &&
            Enumerable.Range(7, 5).All(index => !reader.GetBoolean(index)) &&
            reader.GetBoolean(12) &&
            reader.GetBoolean(13);
        FixtureAssert.True(
            string.Equals(databaseName, configuration.DatabaseName, StringComparison.Ordinal) &&
            string.Equals(migrationRoleName, configuration.MigrationUsername, StringComparison.Ordinal) &&
            string.Equals(runtimeRoleName, configuration.RuntimeUsername, StringComparison.Ordinal) &&
            migrationRoleOid != runtimeRoleOid &&
            runtimeIsRestricted,
            "The fixture database or role boundary is not the exact restricted two-role contract.");
        FixtureAssert.True(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The fixture role lookup returned more than one row.");

        return new FixtureDatabaseIdentities(
            databaseName,
            migrationRoleOid,
            runtimeRoleOid,
            runtimeRoleName);
    }

    private static async Task ValidateOwnedObjectsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        uint expectedOwnerOid,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH schema_check AS
            (
                SELECT
                    COUNT(*) FILTER (WHERE namespace_value.nspowner <> @owner_oid) AS wrong_owners,
                    COUNT(*) AS object_count
                FROM pg_catalog.pg_namespace namespace_value
                WHERE namespace_value.nspname = ANY(@schemas)
            ),
            table_check AS
            (
                SELECT
                    COUNT(*) FILTER (WHERE relation_value.relowner <> @owner_oid) AS wrong_owners,
                    COUNT(*) AS object_count
                FROM pg_catalog.pg_class relation_value
                JOIN pg_catalog.pg_namespace namespace_value
                  ON namespace_value.oid = relation_value.relnamespace
                WHERE namespace_value.nspname || '.' || relation_value.relname = ANY(@tables)
                  AND relation_value.relkind IN ('r', 'p')
            ),
            function_check AS
            (
                SELECT COUNT(*) FILTER (WHERE procedure_value.proowner <> @owner_oid) AS wrong_owners
                FROM pg_catalog.pg_proc procedure_value
                JOIN pg_catalog.pg_namespace namespace_value
                  ON namespace_value.oid = procedure_value.pronamespace
                WHERE namespace_value.nspname = ANY(@schemas)
            )
            SELECT
                schema_check.wrong_owners,
                schema_check.object_count,
                table_check.wrong_owners,
                table_check.object_count,
                function_check.wrong_owners
            FROM schema_check, table_check, function_check;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("owner_oid", NpgsqlTypes.NpgsqlDbType.Oid, expectedOwnerOid);
        command.Parameters.AddWithValue("schemas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, Schemas);
        command.Parameters.AddWithValue(
            "tables",
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
            ReadTables.Concat(ProtectedTables).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetInt64(0) == 0 &&
            reader.GetInt64(1) == Schemas.Length &&
            reader.GetInt64(2) == 0 &&
            reader.GetInt64(3) == ReadTables.Length + ProtectedTables.Length &&
            reader.GetInt64(4) == 0,
            "The migration role does not own the exact fixture schemas, tables, and functions.");
    }

    private static async Task ValidatePublicationChangeRowSecurityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH protected_table_check AS
            (
                SELECT
                    COUNT(*) = @protected_table_count AND
                    bool_and(relation_value.relrowsecurity AND relation_value.relforcerowsecurity) AS valid
                FROM pg_catalog.pg_class relation_value
                JOIN pg_catalog.pg_namespace namespace_value
                  ON namespace_value.oid = relation_value.relnamespace
                WHERE namespace_value.nspname || '.' || relation_value.relname = ANY(@protected_tables)
                  AND relation_value.relkind IN ('r', 'p')
            ),
            expected_policy
                (schema_name, table_name, policy_name, permissive, command_name,
                 has_using_expression, has_check_expression) AS
            (
                VALUES
                    ('cortex_data', 'knowledge_revision',
                     'knowledge_revision_publication_private_read_scope',
                     'PERMISSIVE', 'SELECT', true, false),
                    ('cortex_data', 'knowledge_revision',
                     'knowledge_revision_current_head_restrictive_scope',
                     'RESTRICTIVE', 'SELECT', true, false),
                    ('cortex_data', 'knowledge_revision',
                     'knowledge_revision_publication_change_insert_scope',
                     'PERMISSIVE', 'INSERT', false, true),
                    ('cortex_data', 'knowledge_head',
                     'knowledge_head_publication_private_read_scope',
                     'PERMISSIVE', 'SELECT', true, false),
                    ('cortex_data', 'knowledge_head',
                     'knowledge_head_publication_change_update_scope',
                     'PERMISSIVE', 'UPDATE', true, true),
                    ('cortex_data', 'knowledge_link',
                     'knowledge_link_publication_change_insert_scope',
                     'PERMISSIVE', 'INSERT', false, true),
                    ('cortex_data', 'knowledge_publication_origin',
                     'knowledge_publication_origin_private_read_scope',
                     'PERMISSIVE', 'SELECT', true, false),
                    ('cortex_data', 'knowledge_publication_origin',
                     'knowledge_publication_origin_private_insert_scope',
                     'PERMISSIVE', 'INSERT', false, true),
                    ('cortex_data', 'knowledge_publication_change',
                     'knowledge_publication_change_private_read_scope',
                     'PERMISSIVE', 'SELECT', true, false),
                    ('cortex_data', 'knowledge_publication_change',
                     'knowledge_publication_change_private_insert_scope',
                     'PERMISSIVE', 'INSERT', false, true)
            ),
            installed_policy AS
            (
                SELECT
                    policy_value.schemaname AS schema_name,
                    policy_value.tablename AS table_name,
                    policy_value.policyname AS policy_name,
                    policy_value.permissive,
                    policy_value.roles::text[] AS role_names,
                    policy_value.cmd AS command_name,
                    policy_value.qual AS using_expression,
                    policy_value.with_check AS check_expression
                FROM pg_catalog.pg_policies policy_value
                JOIN expected_policy expected
                  ON expected.schema_name = policy_value.schemaname
                 AND expected.policy_name = policy_value.policyname
            ),
            recorded_policy AS
            (
                SELECT
                    contract_value.schema_name,
                    contract_value.table_name,
                    contract_value.policy_name,
                    contract_value.permissive,
                    contract_value.role_names,
                    contract_value.command_name,
                    contract_value.using_expression,
                    contract_value.check_expression
                FROM cortex_meta.rls_policy_contract contract_value
                JOIN expected_policy expected
                  ON expected.schema_name = contract_value.schema_name
                 AND expected.policy_name = contract_value.policy_name
            ),
            exact_policy_check AS
            (
                SELECT
                    (SELECT COUNT(*) = 10 FROM expected_policy) AND
                    (SELECT COUNT(*) = 10 FROM installed_policy) AND
                    (SELECT COUNT(*) = 10 FROM recorded_policy) AND
                    NOT EXISTS
                    (
                        SELECT 1
                        FROM expected_policy expected
                        FULL OUTER JOIN installed_policy installed
                          ON installed.schema_name = expected.schema_name
                         AND installed.table_name = expected.table_name
                         AND installed.policy_name = expected.policy_name
                        WHERE expected.policy_name IS NULL OR
                              installed.policy_name IS NULL OR
                              installed.permissive IS DISTINCT FROM expected.permissive OR
                              installed.role_names IS DISTINCT FROM ARRAY['public']::text[] OR
                              installed.command_name IS DISTINCT FROM expected.command_name OR
                              (installed.using_expression IS NOT NULL) IS DISTINCT FROM
                                  expected.has_using_expression OR
                              (installed.check_expression IS NOT NULL) IS DISTINCT FROM
                                  expected.has_check_expression
                    ) AND
                    NOT EXISTS
                    (
                        SELECT 1
                        FROM installed_policy installed
                        FULL OUTER JOIN recorded_policy recorded
                          ON recorded.schema_name = installed.schema_name
                         AND recorded.table_name = installed.table_name
                         AND recorded.policy_name = installed.policy_name
                        WHERE installed.policy_name IS NULL OR
                              recorded.policy_name IS NULL OR
                              installed.permissive IS DISTINCT FROM recorded.permissive OR
                              installed.role_names IS DISTINCT FROM recorded.role_names OR
                              installed.command_name IS DISTINCT FROM recorded.command_name OR
                              installed.using_expression IS DISTINCT FROM recorded.using_expression OR
                              installed.check_expression IS DISTINCT FROM recorded.check_expression
                    ) AS valid
            )
            SELECT
                COALESCE((SELECT valid FROM protected_table_check), false),
                COALESCE((SELECT valid FROM exact_policy_check), false);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "protected_table_count",
            NpgsqlTypes.NpgsqlDbType.Integer,
            ProtectedTables.Length);
        command.Parameters.AddWithValue(
            "protected_tables",
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
            ProtectedTables);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetBoolean(0) &&
            reader.GetBoolean(1) &&
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "Migration 0007 does not retain the exact forced-RLS publication-change contract.");
    }

    private static async Task ValidateSyncRowSecurityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH sync_table_check AS
            (
                SELECT
                    COUNT(*) = @sync_table_count AND
                    bool_and(relation_value.relrowsecurity AND relation_value.relforcerowsecurity) AS valid
                FROM pg_catalog.pg_class relation_value
                JOIN pg_catalog.pg_namespace namespace_value
                  ON namespace_value.oid = relation_value.relnamespace
                WHERE namespace_value.nspname = 'cortex_sync'
                  AND relation_value.relname = ANY(@sync_tables)
                  AND relation_value.relkind IN ('r', 'p')
            ),
            policy_check AS
            (
                SELECT
                    COUNT(*) = 12 AND
                    bool_and(
                        policy_value.permissive = contract_value.permissive AND
                        policy_value.roles::text[] = contract_value.role_names AND
                        policy_value.cmd = contract_value.command_name AND
                        policy_value.qual IS NOT DISTINCT FROM contract_value.using_expression AND
                        policy_value.with_check IS NOT DISTINCT FROM contract_value.check_expression) AS valid
                FROM cortex_sync.rls_policy_contract contract_value
                JOIN pg_catalog.pg_policies policy_value
                  ON policy_value.schemaname = 'cortex_sync'
                 AND policy_value.tablename = contract_value.table_name
                 AND policy_value.policyname = contract_value.policy_name
            ),
            policy_inventory AS
            (
                SELECT COUNT(*) = 12 AS valid
                FROM pg_catalog.pg_policies policy_value
                WHERE policy_value.schemaname = 'cortex_sync'
                  AND policy_value.tablename = ANY(@sync_tables)
            )
            SELECT
                COALESCE((SELECT valid FROM sync_table_check), false),
                COALESCE((SELECT valid FROM policy_check), false),
                COALESCE((SELECT valid FROM policy_inventory), false);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "sync_table_count",
            NpgsqlTypes.NpgsqlDbType.Integer,
            SyncJournalTables.Length);
        command.Parameters.AddWithValue(
            "sync_tables",
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
            SyncJournalTables);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            Enumerable.Range(0, 3).All(reader.GetBoolean) &&
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The sync tables do not retain the exact forced-RLS policy contract.");
    }

    private static async Task VerifyExactPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        uint runtimeRoleOid,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH expected_table AS
            (
                SELECT
                    table_name,
                    table_name = ANY(@read_tables) AS can_select,
                    table_name = ANY(@append_tables) AS can_insert
                FROM unnest(@read_tables || @append_tables) AS listed(table_name)
                GROUP BY table_name
            ),
            table_check AS
            (
                SELECT bool_and(
                    has_table_privilege(@role_oid, table_name, 'SELECT') = can_select AND
                    has_table_privilege(@role_oid, table_name, 'INSERT') = can_insert AND
                    has_any_column_privilege(@role_oid, table_name, 'INSERT') = can_insert AND
                    NOT has_table_privilege(@role_oid, table_name, 'UPDATE') AND
                    NOT has_any_column_privilege(@role_oid, table_name, 'UPDATE') AND
                    NOT has_table_privilege(@role_oid, table_name, 'DELETE') AND
                    NOT has_table_privilege(@role_oid, table_name, 'TRUNCATE') AND
                    NOT has_table_privilege(@role_oid, table_name, 'REFERENCES') AND
                    NOT has_any_column_privilege(@role_oid, table_name, 'REFERENCES') AND
                    NOT has_table_privilege(@role_oid, table_name, 'TRIGGER') AND
                    NOT CASE
                        WHEN current_setting('server_version_num')::integer >= 170000
                        THEN has_table_privilege(@role_oid, table_name, 'MAINTAIN')
                        ELSE false
                    END) AS valid
                FROM expected_table
            ),
            protected_table_check AS
            (
                SELECT bool_and(
                    NOT has_table_privilege(@role_oid, table_name, 'SELECT') AND
                    NOT has_table_privilege(@role_oid, table_name, 'INSERT') AND
                    NOT has_any_column_privilege(@role_oid, table_name, 'INSERT') AND
                    NOT has_table_privilege(@role_oid, table_name, 'UPDATE') AND
                    NOT has_any_column_privilege(@role_oid, table_name, 'UPDATE') AND
                    NOT has_table_privilege(@role_oid, table_name, 'DELETE') AND
                    NOT has_table_privilege(@role_oid, table_name, 'TRUNCATE') AND
                    NOT has_table_privilege(@role_oid, table_name, 'REFERENCES') AND
                    NOT has_any_column_privilege(@role_oid, table_name, 'REFERENCES') AND
                    NOT has_table_privilege(@role_oid, table_name, 'TRIGGER')) AS valid
                FROM unnest(@protected_tables) AS listed(table_name)
            ),
            schema_check AS
            (
                SELECT bool_and(
                    has_schema_privilege(@role_oid, schema_name, 'USAGE') AND
                    NOT has_schema_privilege(@role_oid, schema_name, 'CREATE')) AS valid
                FROM unnest(@schemas) AS listed(schema_name)
            ),
            persistent_schema_check AS
            (
                SELECT
                    bool_and(NOT has_schema_privilege(@role_oid, namespace_value.oid, 'CREATE')) AS create_denied,
                    bool_and(namespace_value.nspowner <> @role_oid) AS ownership_denied
                FROM pg_catalog.pg_namespace namespace_value
                WHERE namespace_value.nspname !~ '^pg_'
                  AND namespace_value.nspname <> 'information_schema'
            ),
            function_check AS
            (
                SELECT
                    COUNT(*) = cardinality(@required_functions) AND
                    bool_and(
                        pg_catalog.to_regprocedure(function_name) IS NOT NULL AND
                        has_function_privilege(
                            @role_oid,
                            pg_catalog.to_regprocedure(function_name),
                            'EXECUTE')) AND
                    NOT EXISTS
                    (
                        SELECT 1
                        FROM pg_catalog.pg_proc procedure_value
                        JOIN pg_catalog.pg_namespace namespace_value
                          ON namespace_value.oid = procedure_value.pronamespace
                        WHERE namespace_value.nspname = 'cortex_private'
                          AND has_function_privilege(@role_oid, procedure_value.oid, 'EXECUTE')
                          AND procedure_value.oid NOT IN
                              (SELECT pg_catalog.to_regprocedure(function_name)
                               FROM unnest(@required_functions) AS listed(function_name))
                    ) AS valid
                FROM unnest(@required_functions) AS listed(function_name)
            )
            SELECT
                has_database_privilege(@role_oid, @database_name, 'CONNECT') AND
                NOT has_database_privilege(@role_oid, @database_name, 'CREATE') AND
                NOT has_database_privilege(@role_oid, @database_name, 'TEMPORARY'),
                COALESCE((SELECT valid FROM schema_check), false),
                COALESCE((SELECT valid FROM table_check), false),
                COALESCE((SELECT valid FROM protected_table_check), false),
                COALESCE((SELECT valid FROM function_check), false),
                COALESCE((SELECT create_denied FROM persistent_schema_check), false),
                COALESCE((SELECT ownership_denied FROM persistent_schema_check), false),
                NOT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_class relation_value
                    WHERE relation_value.relowner = @role_oid
                      AND relation_value.relnamespace IN
                          (SELECT oid
                           FROM pg_catalog.pg_namespace
                           WHERE nspname !~ '^pg_'
                             AND nspname <> 'information_schema')
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("role_oid", NpgsqlTypes.NpgsqlDbType.Oid, runtimeRoleOid);
        command.Parameters.AddWithValue("database_name", NpgsqlTypes.NpgsqlDbType.Text, databaseName);
        command.Parameters.AddWithValue("schemas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, Schemas);
        command.Parameters.AddWithValue("read_tables", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, ReadTables);
        command.Parameters.AddWithValue("append_tables", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, AppendTables);
        command.Parameters.AddWithValue(
            "protected_tables",
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
            ProtectedTables);
        command.Parameters.AddWithValue(
            "required_functions",
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
            RequiredFunctions);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            Enumerable.Range(0, 8).All(reader.GetBoolean),
            "The fixture runtime role did not retain the exact allow/deny privilege matrix.");
    }

    private static string QuoteQualifiedIdentifier(string value) =>
        string.Join('.', value.Split('.').Select(QuoteIdentifier));

    private static string QuoteIdentifier(string value)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(value);
    }

    private sealed record FixtureDatabaseIdentities(
        string DatabaseName,
        uint MigrationRoleOid,
        uint RuntimeRoleOid,
        string RuntimeRoleName);
}
