using System.Data;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Pegasus.Cortex.Service.Persistence;

/// <summary>
/// Reconciles the complete least-privilege runtime contract for a migrated
/// Cortex database. Pegasus Server still creates the database and its two login
/// roles; Cortex owns the exact privileges required by its versioned schema.
/// Role provisioning stays in the startup-only migration process so the long-lived
/// MCP runtime never receives migration credentials or role-management authority.
/// </summary>
internal sealed class CortexPostgreSqlRuntimePrivilegeProvisioner
{
    internal static readonly string[] RequiredSchemas =
    [
        "cortex_meta",
        "cortex_data",
        "cortex_index",
        "cortex_private",
        "cortex_sync"
    ];

    internal static readonly string[] ReadTables =
    [
        "cortex_meta.schema_migrations",
        "cortex_meta.rls_policy_contract",
        "cortex_meta.privileged_function_contract",
        "cortex_data.collection_snapshot",
        "cortex_data.collection_head",
        "cortex_data.knowledge_revision",
        "cortex_data.knowledge_head",
        "cortex_data.knowledge_link",
        "cortex_data.learning_proposal",
        "cortex_data.learning_decision",
        "cortex_data.audit_event",
        "cortex_data.project_link",
        "cortex_data.retained_chain",
        "cortex_data.retained_document",
        "cortex_data.retention_audit",
        "cortex_index.generation_state",
        "cortex_sync.journal_batch",
        "cortex_sync.journal_entry",
        "cortex_sync.journal_commit",
        "cortex_sync.journal_outcome",
        "cortex_sync.journal_source_head",
        "cortex_sync.journal_ack",
        "cortex_sync.rls_policy_contract",
        "cortex_sync.schema_object_contract"
    ];

    internal static readonly string[] AppendTables =
    [
        "cortex_data.learning_proposal",
        "cortex_data.learning_decision",
        "cortex_data.project_link",
        "cortex_data.knowledge_revision",
        "cortex_data.knowledge_head",
        "cortex_data.audit_event",
        "cortex_data.retained_chain",
        "cortex_data.retained_document",
        "cortex_data.retention_audit",
        "cortex_sync.journal_batch",
        "cortex_sync.journal_entry",
        "cortex_sync.journal_commit",
        "cortex_sync.journal_outcome",
        "cortex_sync.journal_source_head",
        "cortex_sync.journal_ack"
    ];

    // These tables are reachable only through narrow SECURITY DEFINER
    // functions. The long-lived runtime role must retain no direct table or
    // column privilege on either immutable evidence surface.
    internal static readonly string[] ProtectedTables =
    [
        "cortex_data.knowledge_publication_origin",
        "cortex_data.knowledge_publication_change"
    ];

    internal const string RequiredFunction = "cortex_private.sensitivity_rank(text)";
    internal static readonly string[] RequiredFunctions =
    [
        RequiredFunction,
        "cortex_private.register_knowledge_publication_origin(text,text,text,text,text,text,text,text,bigint,timestamp with time zone)",
        "cortex_private.apply_knowledge_publication_change(text,text,text,text,text,bigint,text,text,text,text,text,timestamp with time zone,text,text,text,text,text,text,text,text,text,text,jsonb,bytea,text,integer,text,text,bytea,bytea,text,integer,text,text,bytea)"
    ];
    internal const long AdvisoryLockKey = 7_061_710_428_297_633_685;

    internal const string AdvisoryLockSql = """
        SELECT pg_advisory_xact_lock(@lock_key);
        """;

    internal const string RoleAttestationSql = """
        SELECT
            runtime_role.oid,
            runtime_role.rolname,
            current_database(),
            session_user = current_user,
            session_user = @migration_role,
            pg_get_userbyid(database_value.datdba) = session_user,
            runtime_role.rolcanlogin,
            runtime_role.rolinherit,
            runtime_role.rolsuper,
            runtime_role.rolbypassrls,
            runtime_role.rolcreaterole,
            runtime_role.rolcreatedb,
            runtime_role.rolreplication,
            NOT EXISTS
            (
                SELECT 1
                FROM pg_catalog.pg_auth_members membership
                WHERE membership.member = runtime_role.oid
                   OR membership.roleid = runtime_role.oid
            ) AS membership_is_empty
        FROM pg_roles runtime_role
        JOIN pg_database database_value
          ON database_value.datname = current_database()
        WHERE runtime_role.rolname = @runtime_role;
        """;

    internal const string VerificationSql = """
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
                has_table_privilege(@runtime_role_oid, table_name, 'SELECT') = can_select AND
                has_table_privilege(@runtime_role_oid, table_name, 'INSERT') = can_insert AND
                has_any_column_privilege(@runtime_role_oid, table_name, 'INSERT') = can_insert AND
                NOT has_table_privilege(@runtime_role_oid, table_name, 'UPDATE') AND
                NOT has_any_column_privilege(@runtime_role_oid, table_name, 'UPDATE') AND
                NOT has_table_privilege(@runtime_role_oid, table_name, 'DELETE') AND
                NOT has_table_privilege(@runtime_role_oid, table_name, 'TRUNCATE') AND
                NOT has_table_privilege(@runtime_role_oid, table_name, 'REFERENCES') AND
                NOT has_any_column_privilege(@runtime_role_oid, table_name, 'REFERENCES') AND
                NOT has_table_privilege(@runtime_role_oid, table_name, 'TRIGGER') AND
                NOT CASE
                    WHEN current_setting('server_version_num')::integer >= 170000
                    THEN has_table_privilege(@runtime_role_oid, table_name, 'MAINTAIN')
                    ELSE false
                END) AS valid
            FROM expected_table
        ),
        protected_table_check AS
        (
            SELECT
                COUNT(*) = cardinality(@protected_tables) AND
                bool_and(
                    pg_catalog.to_regclass(table_name) IS NOT NULL AND
                    NOT has_table_privilege(@runtime_role_oid, table_name, 'SELECT') AND
                    NOT has_any_column_privilege(@runtime_role_oid, table_name, 'SELECT') AND
                    NOT has_table_privilege(@runtime_role_oid, table_name, 'INSERT') AND
                    NOT has_any_column_privilege(@runtime_role_oid, table_name, 'INSERT') AND
                    NOT has_table_privilege(@runtime_role_oid, table_name, 'UPDATE') AND
                    NOT has_any_column_privilege(@runtime_role_oid, table_name, 'UPDATE') AND
                    NOT has_table_privilege(@runtime_role_oid, table_name, 'DELETE') AND
                    NOT has_table_privilege(@runtime_role_oid, table_name, 'TRUNCATE') AND
                    NOT has_table_privilege(@runtime_role_oid, table_name, 'REFERENCES') AND
                    NOT has_any_column_privilege(@runtime_role_oid, table_name, 'REFERENCES') AND
                    NOT has_table_privilege(@runtime_role_oid, table_name, 'TRIGGER') AND
                    NOT CASE
                        WHEN current_setting('server_version_num')::integer >= 170000
                        THEN has_table_privilege(@runtime_role_oid, table_name, 'MAINTAIN')
                        ELSE false
                    END) AS valid
            FROM unnest(@protected_tables) AS listed(table_name)
        ),
        schema_check AS
        (
            SELECT bool_and(
                has_schema_privilege(@runtime_role_oid, schema_name, 'USAGE') AND
                NOT has_schema_privilege(@runtime_role_oid, schema_name, 'CREATE')) AS valid
            FROM unnest(@schemas) AS listed(schema_name)
        ),
        persistent_schema_check AS
        (
            SELECT
                bool_and(NOT has_schema_privilege(
                    @runtime_role_oid, namespace_value.oid, 'CREATE')) AS create_denied,
                bool_and(namespace_value.nspowner <> @runtime_role_oid) AS ownership_denied
            FROM pg_catalog.pg_namespace namespace_value
            WHERE namespace_value.nspname !~ '^pg_'
              AND namespace_value.nspname <> 'information_schema'
        ),
        function_check AS
        (
            SELECT
                (
                    SELECT
                        COUNT(*) = cardinality(@required_functions) AND
                        bool_and(
                            pg_catalog.to_regprocedure(function_name) IS NOT NULL AND
                            has_function_privilege(
                                @runtime_role_oid,
                                pg_catalog.to_regprocedure(function_name),
                                'EXECUTE'))
                    FROM unnest(@required_functions) AS listed(function_name)
                ) AND
                NOT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_proc procedure_value
                    JOIN pg_catalog.pg_namespace namespace_value
                      ON namespace_value.oid = procedure_value.pronamespace
                    WHERE namespace_value.nspname = 'cortex_private'
                      AND has_function_privilege(
                          @runtime_role_oid,
                          procedure_value.oid,
                          'EXECUTE')
                      AND procedure_value.oid NOT IN
                          (SELECT pg_catalog.to_regprocedure(function_name)
                           FROM unnest(@required_functions) AS listed(function_name))
                ) AS valid
        ),
        privileged_function_contract_check AS
        (
            SELECT
                (SELECT COUNT(*) = 2
                 FROM cortex_meta.privileged_function_contract) AND
                (SELECT COUNT(*) = 2 FROM
                 (
                     SELECT function_value.oid
                     FROM pg_catalog.pg_proc function_value
                     JOIN pg_catalog.pg_namespace function_namespace
                       ON function_namespace.oid = function_value.pronamespace
                     WHERE function_namespace.nspname = 'cortex_private'
                       AND function_value.proname IN
                           ('register_knowledge_publication_origin',
                            'apply_knowledge_publication_change')
                 ) installed_count) AND
                NOT EXISTS
                (
                    SELECT 1
                    FROM
                    (
                        SELECT
                            function_namespace.nspname || '.' ||
                                function_value.proname || '(' ||
                                pg_catalog.pg_get_function_identity_arguments(
                                    function_value.oid) || ')' AS function_identity,
                            owner_role.rolname AS owner_name,
                            function_value.prosecdef AS security_definer,
                            COALESCE(
                                function_value.proconfig,
                                ARRAY[]::text[]) AS configuration,
                            pg_catalog.pg_get_functiondef(
                                function_value.oid) AS function_definition
                        FROM pg_catalog.pg_proc function_value
                        JOIN pg_catalog.pg_namespace function_namespace
                          ON function_namespace.oid = function_value.pronamespace
                        JOIN pg_catalog.pg_roles owner_role
                          ON owner_role.oid = function_value.proowner
                        WHERE function_namespace.nspname = 'cortex_private'
                          AND function_value.proname IN
                              ('register_knowledge_publication_origin',
                               'apply_knowledge_publication_change')
                    ) installed_function
                    FULL OUTER JOIN cortex_meta.privileged_function_contract expected
                      ON expected.function_identity =
                         installed_function.function_identity
                    WHERE installed_function.function_identity IS NULL OR
                          expected.function_identity IS NULL OR
                          installed_function.owner_name IS DISTINCT FROM
                              expected.owner_name OR
                          installed_function.security_definer IS DISTINCT FROM
                              expected.security_definer OR
                          installed_function.configuration IS DISTINCT FROM
                              expected.configuration OR
                          installed_function.function_definition IS DISTINCT FROM
                              expected.function_definition
                ) AS valid
        ),
        sequence_check AS
        (
            SELECT COALESCE(bool_and(
                NOT has_sequence_privilege(@runtime_role_oid, relation_value.oid, 'USAGE') AND
                NOT has_sequence_privilege(@runtime_role_oid, relation_value.oid, 'SELECT') AND
                NOT has_sequence_privilege(@runtime_role_oid, relation_value.oid, 'UPDATE')), true)
                AS valid
            FROM pg_catalog.pg_class relation_value
            JOIN pg_catalog.pg_namespace namespace_value
              ON namespace_value.oid = relation_value.relnamespace
            WHERE relation_value.relkind = 'S'
              AND namespace_value.nspname = ANY(@schemas)
        )
        SELECT
            has_database_privilege(
                @runtime_role_oid, @database_name, 'CONNECT') AND
            NOT has_database_privilege(
                @runtime_role_oid, @database_name, 'CREATE') AND
            NOT has_database_privilege(
                @runtime_role_oid, @database_name, 'TEMPORARY') AND
            COALESCE((SELECT valid FROM schema_check), false) AND
            COALESCE((SELECT valid FROM table_check), false) AND
            COALESCE((SELECT valid FROM protected_table_check), false) AND
            COALESCE((SELECT valid FROM function_check), false) AND
            COALESCE((SELECT valid FROM privileged_function_contract_check), false) AND
            COALESCE((SELECT valid FROM sequence_check), false) AND
            COALESCE((SELECT create_denied FROM persistent_schema_check), false) AND
            COALESCE((SELECT ownership_denied FROM persistent_schema_check), false) AND
            NOT EXISTS
            (
                SELECT 1
                FROM pg_catalog.pg_class relation_value
                WHERE relation_value.relowner = @runtime_role_oid
                  AND relation_value.relnamespace IN
                      (SELECT oid
                       FROM pg_catalog.pg_namespace
                       WHERE nspname !~ '^pg_'
                         AND nspname <> 'information_schema')
            );
        """;

    private readonly NpgsqlDataSource _migrationDataSource;
    private readonly string _migrationRoleName;
    private readonly string _runtimeRoleName;

    internal CortexPostgreSqlRuntimePrivilegeProvisioner(
        NpgsqlDataSource migrationDataSource,
        string migrationRoleName,
        string runtimeRoleName)
    {
        _migrationDataSource =
            migrationDataSource ?? throw new ArgumentNullException(nameof(migrationDataSource));
        _migrationRoleName = ValidateRoleName(migrationRoleName);
        _runtimeRoleName = ValidateRoleName(runtimeRoleName);
        if (string.Equals(_migrationRoleName, _runtimeRoleName, StringComparison.OrdinalIgnoreCase))
            throw Failure();
    }

    internal async ValueTask ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _migrationDataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            await AcquireLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var attestation = await AttestRolesAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);

            await using (var grant = new NpgsqlCommand(
                BuildGrantSql(attestation.RuntimeRoleName, attestation.DatabaseName),
                connection,
                transaction))
            {
                await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await VerifyAsync(
                    connection,
                    transaction,
                    attestation,
                    cancellationToken)
                .ConfigureAwait(false))
                throw Failure();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
            when (string.Equals(
                exception.Message,
                "cortex_postgresql_runtime_privilege_reconciliation_failed",
                StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Failure(exception);
        }
    }

    internal static string BuildGrantSql(string runtimeRoleName, string databaseName)
    {
        var runtimeRole = QuoteIdentifier(runtimeRoleName);
        var database = QuoteIdentifier(databaseName);
        var schemas = string.Join(", ", RequiredSchemas.Select(QuoteIdentifier));
        var readTables = string.Join(", ", ReadTables.Select(QuoteQualifiedIdentifier));
        var appendTables = string.Join(", ", AppendTables.Select(QuoteQualifiedIdentifier));
        var protectedTables = string.Join(", ", ProtectedTables.Select(QuoteQualifiedIdentifier));
        var requiredFunctions = string.Join(", ", RequiredFunctions);
        var revokePublicTables = string.Join(' ', RequiredSchemas.Select(schema =>
            $"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {QuoteIdentifier(schema)} FROM PUBLIC;"));
        var revokeRuntimeTables = string.Join(' ', RequiredSchemas.Select(schema =>
            $"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {QuoteIdentifier(schema)} FROM {runtimeRole};"));
        var revokePublicSequences = string.Join(' ', RequiredSchemas.Select(schema =>
            $"REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {QuoteIdentifier(schema)} FROM PUBLIC;"));
        var revokeRuntimeSequences = string.Join(' ', RequiredSchemas.Select(schema =>
            $"REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {QuoteIdentifier(schema)} FROM {runtimeRole};"));
        var revokePublicFunctions = string.Join(' ', RequiredSchemas.Select(schema =>
            $"REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA {QuoteIdentifier(schema)} FROM PUBLIC;"));
        var revokeRuntimeFunctions = string.Join(' ', RequiredSchemas.Select(schema =>
            $"REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA {QuoteIdentifier(schema)} FROM {runtimeRole};"));

        return $"""
            REVOKE ALL PRIVILEGES ON DATABASE {database} FROM PUBLIC;
            REVOKE ALL PRIVILEGES ON DATABASE {database} FROM {runtimeRole};
            GRANT CONNECT ON DATABASE {database} TO {runtimeRole};
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            REVOKE CREATE ON SCHEMA public FROM {runtimeRole};
            REVOKE ALL PRIVILEGES ON SCHEMA {schemas} FROM PUBLIC;
            REVOKE ALL PRIVILEGES ON SCHEMA {schemas} FROM {runtimeRole};
            GRANT USAGE ON SCHEMA {schemas} TO {runtimeRole};
            {revokePublicTables}
            {revokeRuntimeTables}
            {revokePublicSequences}
            {revokeRuntimeSequences}
            {revokePublicFunctions}
            {revokeRuntimeFunctions}
            REVOKE ALL PRIVILEGES ON TABLE {protectedTables} FROM {runtimeRole};
            GRANT SELECT ON TABLE {readTables} TO {runtimeRole};
            GRANT INSERT ON TABLE {appendTables} TO {runtimeRole};
            GRANT EXECUTE ON FUNCTION {requiredFunctions} TO {runtimeRole};
            """;
    }

    internal static string QuoteIdentifier(string value)
    {
        var validated = ValidateRoleName(value);
        return $"\"{validated.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private async ValueTask AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AdvisoryLockSql, connection, transaction);
        command.Parameters.AddWithValue("lock_key", NpgsqlDbType.Bigint, AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RuntimeRoleAttestation> AttestRolesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RoleAttestationSql, connection, transaction);
        command.Parameters.AddWithValue(
            "migration_role",
            NpgsqlDbType.Text,
            _migrationRoleName);
        command.Parameters.AddWithValue(
            "runtime_role",
            NpgsqlDbType.Text,
            _runtimeRoleName);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.IsDBNull(0) ||
            reader.IsDBNull(1) ||
            reader.IsDBNull(2) ||
            !reader.GetBoolean(3) ||
            !reader.GetBoolean(4) ||
            !reader.GetBoolean(5) ||
            !reader.GetBoolean(6) ||
            reader.GetBoolean(7) ||
            reader.GetBoolean(8) ||
            reader.GetBoolean(9) ||
            reader.GetBoolean(10) ||
            reader.GetBoolean(11) ||
            reader.GetBoolean(12) ||
            !reader.GetBoolean(13))
        {
            throw Failure();
        }

        var roleOid = reader.GetFieldValue<uint>(0);
        var roleName = reader.GetString(1);
        var databaseName = reader.GetString(2);
        if (!string.Equals(roleName, _runtimeRoleName, StringComparison.Ordinal) ||
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Failure();
        }

        return new RuntimeRoleAttestation(
            roleOid,
            ValidateIdentifier(roleName),
            ValidateIdentifier(databaseName));
    }

    private async ValueTask<bool> VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRoleAttestation attestation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(VerificationSql, connection, transaction);
        command.Parameters.AddWithValue(
            "runtime_role_oid",
            NpgsqlDbType.Oid,
            attestation.RuntimeRoleOid);
        command.Parameters.AddWithValue(
            "database_name",
            NpgsqlDbType.Text,
            attestation.DatabaseName);
        command.Parameters.AddWithValue(
            "schemas",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            RequiredSchemas);
        command.Parameters.AddWithValue(
            "read_tables",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            ReadTables);
        command.Parameters.AddWithValue(
            "append_tables",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            AppendTables);
        command.Parameters.AddWithValue(
            "protected_tables",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            ProtectedTables);
        command.Parameters.AddWithValue(
            "required_functions",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            RequiredFunctions);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool verified && verified;
    }

    private static string ValidateRoleName(string? value)
        => ValidateIdentifier(value);

    private static string ValidateIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > 63 ||
            value.Any(character => char.IsControl(character) || character == '\0'))
        {
            throw Failure();
        }

        return value;
    }

    private static string QuoteQualifiedIdentifier(string value) =>
        string.Join('.', value.Split('.').Select(QuoteIdentifier));

    private sealed record RuntimeRoleAttestation(
        uint RuntimeRoleOid,
        string RuntimeRoleName,
        string DatabaseName);

    private static InvalidOperationException Failure(Exception? cause = null) =>
        new(
            "cortex_postgresql_runtime_privilege_reconciliation_failed",
            cause);
}
