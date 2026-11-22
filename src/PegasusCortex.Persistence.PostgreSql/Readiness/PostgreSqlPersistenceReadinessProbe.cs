using System.Data;
using System.Data.Common;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Persistence.PostgreSql.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Security;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Migrations;
using Pegasus.Cortex.Core.Query;

namespace Pegasus.Cortex.Persistence.PostgreSql.Readiness;

public enum PostgreSqlReadinessState
{
    Ready,
    Degraded,
    NotReady
}

public sealed record PostgreSqlReadinessOptions(
    int MinimumServerMajor,
    bool RequireTls,
    bool HostTlsPolicyVerified,
    IReadOnlyList<string>? AllowedReachableRoleNames = null,
    bool RequireStoreSentinel = true);

public sealed record PostgreSqlPersistenceReadiness(
    PostgreSqlReadinessState State,
    int? ServerMajor,
    bool TlsActive,
    bool HostTlsPolicyVerified,
    bool RuntimeRoleRestricted,
    bool ContextClean,
    bool MigrationsCurrent,
    bool RowLevelSecurityForced,
    bool StoreSentinelVerified,
    bool IndexStateVerified,
    IReadOnlyList<string> IssueCodes,
    DateTimeOffset ObservedAt);

/// <summary>
/// Probes actual database invariants rather than returning a synthetic health value. The
/// host must separately attest client-side hostname/CA, pin validation, or an explicit
/// literal-loopback transport policy; SQL can only prove whether the backend connection
/// is encrypted.
/// </summary>
public sealed class PostgreSqlPersistenceReadinessProbe
{
    private static readonly string[] SensitivityTiers =
        ["public", "internal", "confidential", "restricted"];

    private static readonly HashSet<string> DangerousPredefinedRoles = new(StringComparer.Ordinal)
    {
        "pg_checkpoint",
        "pg_create_subscription",
        "pg_execute_server_program",
        "pg_maintain",
        "pg_read_all_data",
        "pg_read_server_files",
        "pg_signal_backend",
        "pg_signal_autovacuum_worker",
        "pg_write_all_data",
        "pg_write_server_files"
    };

    private const string RuntimeSql = """
        WITH installed_privileged_function AS
        (
            SELECT
                function_namespace.nspname || '.' || function_value.proname || '(' ||
                    pg_catalog.pg_get_function_identity_arguments(function_value.oid) || ')'
                    AS function_identity,
                owner_role.rolname AS owner_name,
                function_value.prosecdef AS security_definer,
                COALESCE(function_value.proconfig, ARRAY[]::text[]) AS configuration,
                pg_catalog.pg_get_functiondef(function_value.oid) AS function_definition
            FROM pg_catalog.pg_proc function_value
            JOIN pg_catalog.pg_namespace function_namespace
              ON function_namespace.oid = function_value.pronamespace
            JOIN pg_catalog.pg_roles owner_role
              ON owner_role.oid = function_value.proowner
            WHERE function_namespace.nspname = 'cortex_private'
              AND function_value.proname IN
                  ('register_knowledge_publication_origin',
                   'apply_knowledge_publication_change')
        ),
        privileged_function_contract_valid AS
        (
            SELECT
                (SELECT COUNT(*) = 2
                 FROM installed_privileged_function) AND
                (SELECT COUNT(*) = 2
                 FROM cortex_meta.privileged_function_contract) AND
                NOT EXISTS
                (
                    SELECT 1
                    FROM installed_privileged_function installed
                    FULL OUTER JOIN cortex_meta.privileged_function_contract expected
                      ON expected.function_identity = installed.function_identity
                    WHERE installed.function_identity IS NULL OR
                          expected.function_identity IS NULL OR
                          installed.owner_name IS DISTINCT FROM expected.owner_name OR
                          installed.security_definer IS DISTINCT FROM expected.security_definer OR
                          installed.configuration IS DISTINCT FROM expected.configuration OR
                          installed.function_definition IS DISTINCT FROM expected.function_definition
                ) AS valid
        )
        SELECT
            current_setting('server_version_num')::integer,
            COALESCE((SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()), false),
            role_value.rolsuper,
            role_value.rolbypassrls,
            (
                EXISTS
                (
                    SELECT 1
                    FROM pg_class class_value
                    JOIN pg_namespace namespace_value ON namespace_value.oid = class_value.relnamespace
                    WHERE namespace_value.nspname IN ('cortex_meta', 'cortex_data', 'cortex_index')
                      AND class_value.relkind IN ('r', 'p', 'S')
                      AND
                      (
                          pg_has_role(current_user, class_value.relowner, 'USAGE') OR
                          CASE
                              WHEN current_setting('server_version_num')::integer >= 160000
                              THEN pg_has_role(current_user, class_value.relowner, 'SET')
                              ELSE pg_has_role(current_user, class_value.relowner, 'MEMBER')
                          END
                      )
                ) OR
                EXISTS
                (
                    SELECT 1
                    FROM pg_proc function_value
                    JOIN pg_namespace function_namespace ON function_namespace.oid = function_value.pronamespace
                    WHERE function_namespace.nspname = 'cortex_private'
                      AND
                      (
                          pg_has_role(current_user, function_value.proowner, 'USAGE') OR
                          CASE
                              WHEN current_setting('server_version_num')::integer >= 160000
                              THEN pg_has_role(current_user, function_value.proowner, 'SET')
                              ELSE pg_has_role(current_user, function_value.proowner, 'MEMBER')
                          END
                      )
                )
            ),
            EXISTS
            (
                SELECT 1
                FROM pg_class class_value
                JOIN pg_namespace namespace_value ON namespace_value.oid = class_value.relnamespace
                WHERE
                  (
                      namespace_value.nspname = 'cortex_meta' AND
                      (
                          class_value.relname IN
                              ('schema_migrations', 'rls_policy_contract',
                               'privileged_function_contract') OR
                          class_value.relkind = 'S'
                      ) OR
                      namespace_value.nspname = 'cortex_data' AND
                      class_value.relname IN
                          ('collection_snapshot', 'collection_head', 'knowledge_revision', 'knowledge_head',
                           'knowledge_link', 'learning_proposal', 'learning_decision', 'audit_event',
                           'project_link', 'knowledge_publication_origin',
                           'knowledge_publication_change') OR
                      namespace_value.nspname = 'cortex_index' AND
                      class_value.relname = 'generation_state'
                  )
                  AND
                  CASE class_value.relkind
                      WHEN 'S' THEN
                          has_sequence_privilege(current_user, class_value.oid, 'USAGE') OR
                          has_sequence_privilege(current_user, class_value.oid, 'UPDATE') OR
                          has_sequence_privilege(current_user, class_value.oid, 'SELECT')
                      ELSE
                          namespace_value.nspname = 'cortex_data' AND
                          class_value.relname IN
                              ('knowledge_publication_origin', 'knowledge_publication_change') AND
                          (
                              has_table_privilege(current_user, class_value.oid, 'SELECT') OR
                              has_any_column_privilege(current_user, class_value.oid, 'SELECT') OR
                              has_table_privilege(current_user, class_value.oid, 'INSERT') OR
                              has_any_column_privilege(current_user, class_value.oid, 'INSERT') OR
                              has_table_privilege(current_user, class_value.oid, 'REFERENCES') OR
                              has_any_column_privilege(current_user, class_value.oid, 'REFERENCES')
                          ) OR
                          namespace_value.nspname = 'cortex_meta' AND
                          (
                              has_table_privilege(current_user, class_value.oid, 'INSERT') OR
                              has_any_column_privilege(current_user, class_value.oid, 'INSERT') OR
                              has_any_column_privilege(current_user, class_value.oid, 'UPDATE')
                          ) OR
                          has_table_privilege(current_user, class_value.oid, 'UPDATE') OR
                          has_table_privilege(current_user, class_value.oid, 'DELETE') OR
                          has_table_privilege(current_user, class_value.oid, 'TRUNCATE') OR
                          has_table_privilege(current_user, class_value.oid, 'TRIGGER') OR
                          CASE
                              WHEN current_setting('server_version_num')::integer >= 170000
                              THEN has_table_privilege(current_user, class_value.oid, 'MAINTAIN')
                              ELSE false
                          END
                  END
            ),
            COALESCE(has_schema_privilege(current_user, 'cortex_meta', 'USAGE'), false) AND
            COALESCE(has_schema_privilege(current_user, 'cortex_data', 'USAGE'), false) AND
            COALESCE(has_schema_privilege(current_user, 'cortex_index', 'USAGE'), false) AND
            COALESCE(has_schema_privilege(current_user, 'cortex_private', 'USAGE'), false),
            COALESCE(has_database_privilege(current_user, current_database(), 'CREATE'), false) OR
            COALESCE(has_schema_privilege(current_user, 'cortex_meta', 'CREATE'), false) OR
            COALESCE(has_schema_privilege(current_user, 'cortex_data', 'CREATE'), false) OR
            COALESCE(has_schema_privilege(current_user, 'cortex_index', 'CREATE'), false) OR
            COALESCE(has_schema_privilege(current_user, 'cortex_private', 'CREATE'), false),
            (
                SELECT COUNT(*) = 16 AND bool_and(has_table_privilege(current_user, class_value.oid, 'SELECT'))
                FROM pg_class class_value
                JOIN pg_namespace namespace_value ON namespace_value.oid = class_value.relnamespace
                WHERE
                    (namespace_value.nspname = 'cortex_meta' AND class_value.relname IN
                        ('schema_migrations', 'rls_policy_contract',
                         'privileged_function_contract')) OR
                    (namespace_value.nspname = 'cortex_data' AND class_value.relname IN
                        ('collection_snapshot', 'collection_head', 'knowledge_revision', 'knowledge_head',
                         'knowledge_link', 'learning_proposal', 'learning_decision', 'audit_event',
                         'project_link', 'retained_chain', 'retained_document', 'retention_audit')) OR
                    (namespace_value.nspname = 'cortex_index' AND class_value.relname = 'generation_state')
            ),
            has_table_privilege(current_user, 'cortex_data.learning_proposal', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.learning_decision', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.project_link', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.knowledge_revision', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.knowledge_head', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.audit_event', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.retained_chain', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.retained_document', 'INSERT') AND
            has_table_privilege(current_user, 'cortex_data.retention_audit', 'INSERT'),
            has_function_privilege(
                current_user,
                'cortex_private.sensitivity_rank(text)',
                'EXECUTE') AND
            has_function_privilege(
                current_user,
                'cortex_private.register_knowledge_publication_origin(text,text,text,text,text,text,text,text,bigint,timestamp with time zone)',
                'EXECUTE') AND
            has_function_privilege(
                current_user,
                'cortex_private.apply_knowledge_publication_change(text,text,text,text,text,bigint,text,text,text,text,text,timestamp with time zone,text,text,text,text,text,text,text,text,text,text,jsonb,bytea,text,integer,text,text,bytea,bytea,text,integer,text,text,bytea)',
                'EXECUTE') AND
            NOT EXISTS
            (
                SELECT 1
                FROM pg_proc executable_function
                JOIN pg_namespace executable_namespace
                  ON executable_namespace.oid = executable_function.pronamespace
                WHERE executable_namespace.nspname = 'cortex_private'
                  AND has_function_privilege(current_user, executable_function.oid, 'EXECUTE')
                  AND executable_function.oid NOT IN
                  (
                      'cortex_private.sensitivity_rank(text)'::regprocedure,
                      'cortex_private.register_knowledge_publication_origin(text,text,text,text,text,text,text,text,bigint,timestamp with time zone)'::regprocedure,
                      'cortex_private.apply_knowledge_publication_change(text,text,text,text,text,bigint,text,text,text,text,text,timestamp with time zone,text,text,text,text,text,text,text,text,text,text,jsonb,bytea,text,integer,text,text,bytea,bytea,text,integer,text,text,bytea)'::regprocedure
                  )
            ) AND
            COALESCE(
                (SELECT valid FROM privileged_function_contract_valid),
                false),
            role_value.rolcreaterole,
            role_value.rolcreatedb,
            role_value.rolreplication,
            EXISTS
            (
                SELECT 1
                FROM pg_roles admin_role
                WHERE admin_role.rolname <> current_user
                  AND pg_has_role(current_user, admin_role.oid, 'MEMBER WITH ADMIN OPTION')
            ),
            EXISTS
            (
                SELECT 1
                FROM pg_roles reachable_role
                WHERE reachable_role.rolname <> current_user
                  AND
                  (
                      pg_has_role(current_user, reachable_role.oid, 'USAGE') OR
                      CASE
                          WHEN current_setting('server_version_num')::integer >= 160000
                          THEN pg_has_role(current_user, reachable_role.oid, 'SET')
                          ELSE pg_has_role(current_user, reachable_role.oid, 'MEMBER')
                      END
                  )
                  AND
                  (
                      reachable_role.rolsuper OR
                      reachable_role.rolbypassrls OR
                      reachable_role.rolcreaterole OR
                      reachable_role.rolcreatedb OR
                      reachable_role.rolreplication OR
                      has_database_privilege(reachable_role.oid, current_database(), 'CREATE') OR
                      has_schema_privilege(reachable_role.oid, 'cortex_meta', 'CREATE') OR
                      has_schema_privilege(reachable_role.oid, 'cortex_data', 'CREATE') OR
                      has_schema_privilege(reachable_role.oid, 'cortex_index', 'CREATE') OR
                      has_schema_privilege(reachable_role.oid, 'cortex_private', 'CREATE') OR
                      EXISTS
                      (
                          SELECT 1
                          FROM pg_proc reachable_function
                          JOIN pg_namespace reachable_function_namespace
                            ON reachable_function_namespace.oid = reachable_function.pronamespace
                          WHERE reachable_function_namespace.nspname = 'cortex_private'
                            AND reachable_function.proowner = reachable_role.oid
                      ) OR
                      EXISTS
                      (
                          SELECT 1
                          FROM pg_class reachable_class
                          JOIN pg_namespace reachable_namespace
                            ON reachable_namespace.oid = reachable_class.relnamespace
                          WHERE reachable_namespace.nspname IN ('cortex_meta', 'cortex_data', 'cortex_index')
                            AND reachable_class.relkind IN ('r', 'p', 'S')
                            AND
                            (
                                reachable_class.relowner = reachable_role.oid OR
                                CASE reachable_class.relkind
                                    WHEN 'S' THEN
                                        has_sequence_privilege(reachable_role.oid, reachable_class.oid, 'USAGE') OR
                                        has_sequence_privilege(reachable_role.oid, reachable_class.oid, 'UPDATE') OR
                                        has_sequence_privilege(reachable_role.oid, reachable_class.oid, 'SELECT')
                                    ELSE
                                        reachable_namespace.nspname = 'cortex_meta' AND
                                        (
                                            has_table_privilege(reachable_role.oid, reachable_class.oid, 'INSERT') OR
                                            has_any_column_privilege(reachable_role.oid, reachable_class.oid, 'INSERT') OR
                                            has_any_column_privilege(reachable_role.oid, reachable_class.oid, 'UPDATE')
                                        ) OR
                                        has_table_privilege(reachable_role.oid, reachable_class.oid, 'UPDATE') OR
                                        has_table_privilege(reachable_role.oid, reachable_class.oid, 'DELETE') OR
                                        has_table_privilege(reachable_role.oid, reachable_class.oid, 'TRUNCATE') OR
                                        has_table_privilege(reachable_role.oid, reachable_class.oid, 'TRIGGER') OR
                                        CASE
                                            WHEN current_setting('server_version_num')::integer >= 170000
                                            THEN has_table_privilege(reachable_role.oid, reachable_class.oid, 'MAINTAIN')
                                            ELSE false
                                        END
                                END
                            )
                      )
                  )
            ),
            session_user = current_user
        FROM pg_roles role_value
        WHERE role_value.rolname = current_user;
        """;

    private const string ReachableRolesSql = """
        SELECT
            CASE WHEN char_length(reachable_role.rolname) BETWEEN 1 AND 63
                 THEN reachable_role.rolname ELSE NULL END
        FROM pg_roles reachable_role
        WHERE reachable_role.rolname <> current_user
          AND
          (
              pg_has_role(current_user, reachable_role.oid, 'MEMBER WITH ADMIN OPTION') OR
              pg_has_role(current_user, reachable_role.oid, 'USAGE') OR
              CASE
                  WHEN current_setting('server_version_num')::integer >= 160000
                  THEN pg_has_role(current_user, reachable_role.oid, 'SET')
                  ELSE pg_has_role(current_user, reachable_role.oid, 'MEMBER')
              END
          )
        ORDER BY reachable_role.rolname COLLATE "C";
        """;

    private const string RowLevelSecuritySql = """
        WITH required_table(schema_name, table_name) AS
        (
            VALUES
                ('cortex_data', 'collection_snapshot'),
                ('cortex_data', 'collection_head'),
                ('cortex_data', 'knowledge_revision'),
                ('cortex_data', 'knowledge_head'),
                ('cortex_data', 'knowledge_link'),
                ('cortex_data', 'knowledge_publication_origin'),
                ('cortex_data', 'knowledge_publication_change'),
                ('cortex_data', 'learning_proposal'),
                ('cortex_data', 'learning_decision'),
                ('cortex_data', 'project_link'),
                ('cortex_data', 'audit_event'),
                ('cortex_data', 'retained_chain'),
                ('cortex_data', 'retained_document'),
                ('cortex_data', 'retention_audit'),
                ('cortex_index', 'generation_state')
        ),
        expected_policy(
            schema_name,
            table_name,
            policy_name,
            command_name,
            required_fragments) AS
        (
            VALUES
                ('cortex_data', 'collection_snapshot', 'collection_snapshot_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.maximum_sensitivity', 'pegasus.cortex.capability',
                          '''discover''', '''status''', '''catalog''', '''search''', '''read''', '''readiness''']),
                ('cortex_data', 'collection_head', 'collection_head_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.maximum_sensitivity', 'pegasus.cortex.capability',
                          '''discover''', '''status''', '''catalog''', '''search''', '''read''', '''readiness''']),
                ('cortex_data', 'knowledge_revision', 'knowledge_revision_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.maximum_sensitivity', 'pegasus.cortex.capability',
                          'pegasus.cortex.allow_stale', 'pegasus.cortex.allow_blocked_or_unverified',
                          '''catalog''', '''search''', '''read''', '''readiness''']),
                ('cortex_data', 'knowledge_head', 'knowledge_head_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''catalog''', '''search''', '''read''', '''readiness''']),
                ('cortex_data', 'knowledge_link', 'knowledge_link_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''read''']),
                ('cortex_data', 'knowledge_revision',
                    'knowledge_revision_publication_private_read_scope', 'SELECT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability',
                          '''submit''', '''review''', '''audit''']),
                ('cortex_data', 'knowledge_revision',
                    'knowledge_revision_current_head_restrictive_scope', 'SELECT',
                    ARRAY['pegasus.cortex.capability', '''readiness''', '''audit''',
                          'knowledge_head', 'current_revision_id', 'revision_id']),
                ('cortex_data', 'knowledge_revision',
                    'knowledge_revision_publication_change_insert_scope', 'INSERT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability',
                          '''submit''', 'record_status', '''superseded''', '''tombstone''',
                          'content_treatment', 'executable']),
                ('cortex_data', 'knowledge_head',
                    'knowledge_head_publication_private_read_scope', 'SELECT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability',
                          '''submit''', '''review''', '''audit''']),
                ('cortex_data', 'knowledge_head',
                    'knowledge_head_publication_change_update_scope', 'UPDATE',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability', '''submit''']),
                ('cortex_data', 'knowledge_link',
                    'knowledge_link_publication_change_insert_scope', 'INSERT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability',
                          'target_namespace_name', '''submit''', '''supersedes''', '''tombstones''']),
                ('cortex_data', 'knowledge_publication_origin',
                    'knowledge_publication_origin_private_read_scope', 'SELECT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability',
                          '''submit''', '''review''', '''audit''']),
                ('cortex_data', 'knowledge_publication_origin',
                    'knowledge_publication_origin_private_insert_scope', 'INSERT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability',
                          '''review''', '''audit''']),
                ('cortex_data', 'knowledge_publication_change',
                    'knowledge_publication_change_private_read_scope', 'SELECT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.capability', '''submit''']),
                ('cortex_data', 'knowledge_publication_change',
                    'knowledge_publication_change_private_insert_scope', 'INSERT',
                    ARRAY['current_user', 'session_user', 'pegasus.cortex.tenant_id',
                          'pegasus.cortex.namespace', 'pegasus.cortex.authority_id',
                          'pegasus.cortex.subject_id', 'pegasus.cortex.service_id',
                          'pegasus.cortex.policy_revision', 'pegasus.cortex.capability', '''submit''']),
                ('cortex_data', 'learning_proposal', 'learning_proposal_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''read''', '''review''', '''submit''']),
                ('cortex_data', 'learning_proposal', 'learning_proposal_submit_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''submit''']),
                ('cortex_data', 'learning_proposal', 'learning_proposal_audit_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''audit''']),
                ('cortex_data', 'learning_decision', 'learning_decision_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''read''', '''review''']),
                ('cortex_data', 'learning_decision', 'learning_decision_review_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''review''']),
                ('cortex_data', 'learning_decision', 'learning_decision_audit_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''audit''']),
                ('cortex_data', 'learning_decision', 'learning_decision_audit_insert_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''audit''']),
                ('cortex_data', 'project_link', 'project_link_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''read''', '''correlate''']),
                ('cortex_data', 'project_link', 'project_link_correlate_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''correlate''']),
                ('cortex_data', 'knowledge_revision', 'knowledge_revision_review_insert_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''review''',
                          'record_status', 'content_treatment', 'executable']),
                ('cortex_data', 'knowledge_head', 'knowledge_head_review_insert_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''review''', 'generation']),
                ('cortex_data', 'knowledge_revision', 'knowledge_revision_audit_insert_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''audit''',
                          'record_status', 'content_treatment', 'executable']),
                ('cortex_data', 'knowledge_head', 'knowledge_head_audit_insert_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''audit''', 'generation']),
                ('cortex_data', 'audit_event', 'audit_event_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''read''']),
                ('cortex_data', 'audit_event', 'audit_event_append_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''submit''', '''review''']),
                ('cortex_data', 'audit_event', 'audit_event_auditor_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''audit''', '''delegate_review''']),
                ('cortex_data', 'audit_event', 'audit_event_auditor_append_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''audit''', '''delegate_review''']),
                ('cortex_data', 'retained_chain', 'retained_chain_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''retained_read''', '''retained_write''', '''readiness''']),
                ('cortex_data', 'retained_chain', 'retained_chain_append_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''retained_write''']),
                ('cortex_data', 'retained_document', 'retained_document_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''retained_read''', '''retained_write''', '''readiness''']),
                ('cortex_data', 'retained_document', 'retained_document_append_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''retained_write''']),
                ('cortex_data', 'retention_audit', 'retention_audit_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''retained_read''', '''retained_write''', '''readiness''']),
                ('cortex_data', 'retention_audit', 'retention_audit_append_scope', 'INSERT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''retained_write''']),
                ('cortex_index', 'generation_state', 'generation_state_read_scope', 'SELECT',
                    ARRAY['pegasus.cortex.tenant_id', 'pegasus.cortex.namespace',
                          'pegasus.cortex.capability', '''readiness'''])
        ),
        installed_policy AS
        (
            SELECT
                policy_value.schemaname,
                policy_value.tablename,
                policy_value.policyname,
                policy_value.permissive,
                policy_value.roles,
                policy_value.cmd,
                policy_value.qual,
                policy_value.with_check
            FROM pg_policies policy_value
            WHERE policy_value.schemaname IN ('cortex_data', 'cortex_index')
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
        ),
        matched_policy AS
        (
            SELECT
                expected_policy.*,
                installed_policy.policyname AS installed_policy_name,
                installed_policy.permissive,
                installed_policy.roles,
                installed_policy.cmd,
                installed_policy.qual,
                installed_policy.with_check,
                concat_ws(' ', installed_policy.qual, installed_policy.with_check) AS policy_clause
            FROM expected_policy
            LEFT JOIN installed_policy
              ON installed_policy.schemaname = expected_policy.schema_name
             AND installed_policy.tablename = expected_policy.table_name
             AND installed_policy.policyname = expected_policy.policy_name
             AND installed_policy.cmd = expected_policy.command_name
        )
        SELECT
            (
                SELECT COUNT(*) = (SELECT COUNT(*) FROM required_table) AND
                       bool_and(class_value.relrowsecurity AND class_value.relforcerowsecurity)
                FROM required_table
                JOIN pg_namespace namespace_value
                  ON namespace_value.nspname = required_table.schema_name
                JOIN pg_class class_value
                  ON class_value.relnamespace = namespace_value.oid
                 AND class_value.relname = required_table.table_name
                 AND class_value.relkind IN ('r', 'p')
            ) AND
            (SELECT COUNT(*) = 39 FROM installed_policy) AND
            (SELECT COUNT(*) = 39 FROM recorded_policy) AND
            NOT EXISTS
            (
                SELECT 1
                FROM installed_policy
                FULL JOIN recorded_policy
                  ON recorded_policy.schema_name = installed_policy.schemaname
                 AND recorded_policy.table_name = installed_policy.tablename
                 AND recorded_policy.policy_name = installed_policy.policyname
                WHERE installed_policy.policyname IS NULL OR
                      recorded_policy.policy_name IS NULL OR
                      installed_policy.permissive IS DISTINCT FROM recorded_policy.permissive OR
                      installed_policy.roles::text[] IS DISTINCT FROM recorded_policy.role_names OR
                      installed_policy.cmd IS DISTINCT FROM recorded_policy.command_name OR
                      installed_policy.qual IS DISTINCT FROM recorded_policy.using_expression OR
                      installed_policy.with_check IS DISTINCT FROM recorded_policy.check_expression
            ) AND
            NOT EXISTS
            (
                SELECT 1
                FROM matched_policy
                WHERE installed_policy_name IS NULL OR
                      permissive <>
                          CASE policy_name
                              WHEN 'knowledge_revision_current_head_restrictive_scope' THEN 'RESTRICTIVE'
                              ELSE 'PERMISSIVE'
                          END OR
                      roles <> ARRAY['public']::name[] OR
                      CASE command_name
                          WHEN 'SELECT' THEN qual IS NULL OR with_check IS NOT NULL
                          WHEN 'INSERT' THEN qual IS NOT NULL OR with_check IS NULL
                          WHEN 'UPDATE' THEN qual IS NULL OR with_check IS NULL
                          ELSE true
                      END OR
                      EXISTS
                      (
                          SELECT 1
                          FROM unnest(required_fragments) fragment_value
                          WHERE position(fragment_value IN policy_clause) = 0
                      )
            );
        """;

    private const string SentinelScopeIsolationSql = """
        SELECT
            NOT EXISTS
            (
                SELECT 1 FROM cortex_data.collection_snapshot
                WHERE tenant_id = @sentinel_tenant AND namespace_name = @sentinel_namespace
            ) AND
            NOT EXISTS
            (
                SELECT 1 FROM cortex_data.collection_head
                WHERE tenant_id = @sentinel_tenant AND namespace_name = @sentinel_namespace
            ) AND
            NOT EXISTS
            (
                SELECT 1 FROM cortex_data.knowledge_revision
                WHERE tenant_id = @sentinel_tenant AND namespace_name = @sentinel_namespace
            ) AND
            NOT EXISTS
            (
                SELECT 1 FROM cortex_data.knowledge_head
                WHERE tenant_id = @sentinel_tenant AND namespace_name = @sentinel_namespace
            ) AND
            NOT EXISTS
            (
                SELECT 1 FROM cortex_index.generation_state
                WHERE tenant_id = @sentinel_tenant AND namespace_name = @sentinel_namespace
            );
        """;

    private const string PublicTierIsolationSql = """
        SELECT
            NOT EXISTS
            (
                SELECT 1
                FROM cortex_data.collection_snapshot
                WHERE tenant_id = @sentinel_tenant
                  AND namespace_name = @sentinel_namespace
                  AND maximum_sensitivity <> 'public'
            ) AND
            NOT EXISTS
            (
                SELECT 1
                FROM cortex_data.collection_head
                WHERE tenant_id = @sentinel_tenant
                  AND namespace_name = @sentinel_namespace
                  AND maximum_sensitivity <> 'public'
            ) AND
            NOT EXISTS
            (
                SELECT 1
                FROM cortex_data.knowledge_revision
                WHERE tenant_id = @sentinel_tenant
                  AND namespace_name = @sentinel_namespace
                  AND sensitivity <> 'public'
            );
        """;

    private const string StoreStateSql = """
        SELECT
            CASE WHEN char_length(head_value.current_collection_id) BETWEEN 1 AND 128
                 THEN head_value.current_collection_id ELSE NULL END,
            CASE WHEN char_length(snapshot_value.payload_sha256) = 64
                 THEN snapshot_value.payload_sha256 ELSE NULL END,
            (
                SELECT COUNT(*)
                FROM cortex_data.knowledge_head active_head
                JOIN cortex_data.knowledge_revision revision_value
                  ON revision_value.tenant_id = active_head.tenant_id
                 AND revision_value.namespace_name = active_head.namespace_name
                 AND revision_value.record_id = active_head.record_id
                 AND revision_value.revision_id = active_head.current_revision_id
                WHERE revision_value.tenant_id = @tenant_id
                  AND revision_value.namespace_name = @namespace
                  AND revision_value.record_status = 'active'
                  AND revision_value.content_treatment = 'untrusted_data'
                  AND revision_value.executable = false
            ),
            EXISTS
            (
                SELECT 1
                FROM cortex_index.generation_state state_value
                WHERE state_value.tenant_id = @tenant_id
                  AND state_value.namespace_name = @namespace
                  AND state_value.generation = head_value.generation
                  AND state_value.indexed_through IS NOT NULL
                  AND state_value.indexed_through >= head_value.updated_at
                  AND state_value.updated_at >= state_value.indexed_through
            )
        FROM cortex_data.collection_head head_value
        JOIN cortex_data.collection_snapshot snapshot_value
          ON snapshot_value.tenant_id = head_value.tenant_id
         AND snapshot_value.namespace_name = head_value.namespace_name
         AND snapshot_value.maximum_sensitivity = head_value.maximum_sensitivity
         AND snapshot_value.collection_id = head_value.current_collection_id
        WHERE head_value.tenant_id = @tenant_id
          AND head_value.namespace_name = @namespace
          AND head_value.maximum_sensitivity = @maximum_sensitivity;
        """;

    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly ICortexQuerySource _querySource;
    private readonly CortexQuerySourceScope _sentinelScope;
    private readonly PostgreSqlReadinessOptions _options;
    private readonly HashSet<string> _allowedReachableRoleNames;
    private readonly TimeProvider _timeProvider;

    public PostgreSqlPersistenceReadinessProbe(
        PostgreSqlTransactionRunner transactions,
        ICortexRowAuthenticator rowAuthenticator,
        CortexQuerySourceScope sentinelScope,
        PostgreSqlReadinessOptions options,
        TimeProvider? timeProvider = null)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _querySource = new PostgreSqlCortexQuerySource(
            _transactions,
            rowAuthenticator ?? throw new ArgumentNullException(nameof(rowAuthenticator)));
        _sentinelScope = ValidateSentinelScope(sentinelScope);
        _options = ValidateOptions(options ?? throw new ArgumentNullException(nameof(options)));
        _allowedReachableRoleNames = _options.AllowedReachableRoleNames!.ToHashSet(StringComparer.Ordinal);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<PostgreSqlPersistenceReadiness> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<string>();
        if (_transactions.IsPoisoned)
        {
            issues.Add("cortex_postgresql_data_source_poisoned");
            return Result(
                PostgreSqlReadinessState.NotReady,
                null,
                tlsActive: false,
                roleRestricted: false,
                contextClean: false,
                migrationsCurrent: false,
                rlsForced: false,
                storeSentinelVerified: false,
                indexStateVerified: false,
                issues);
        }

        int? serverMajor = null;
        var tlsActive = false;
        var roleRestricted = false;
        var contextClean = false;
        var migrationsCurrent = false;
        var rlsForced = false;
        var storeSentinelVerified = false;
        var indexStateVerified = false;
        try
        {
            // Dispose the direct probe connection before repository probes open pooled connections.
            // This preserves readiness behavior even when the worker configures a one-connection pool.
            {
                await using var connection = await _transactions.DataSource.OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken).ConfigureAwait(false);

                await using (var runtime = PostgreSqlCommandFactory.Create(connection, transaction, RuntimeSql))
                await using (var reader = await runtime.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        serverMajor = reader.GetInt32(0) / 10000;
                        tlsActive = reader.GetBoolean(1);
                        roleRestricted = !reader.GetBoolean(2) && !reader.GetBoolean(3) &&
                            !reader.GetBoolean(4) && !reader.GetBoolean(5) &&
                            reader.GetBoolean(6) && !reader.GetBoolean(7) &&
                            reader.GetBoolean(8) && reader.GetBoolean(9) && reader.GetBoolean(10) &&
                            !reader.GetBoolean(11) && !reader.GetBoolean(12) && !reader.GetBoolean(13) &&
                            !reader.GetBoolean(14) && !reader.GetBoolean(15) && reader.GetBoolean(16);
                    }
                }

                var reachableRolesRestricted = true;
                await using (var reachable = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    ReachableRolesSql))
                await using (var reader = await reachable.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    var roleCount = 0;
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        roleCount++;
                        if (roleCount > 128 || reader.IsDBNull(0))
                        {
                            reachableRolesRestricted = false;
                            break;
                        }

                        var roleName = reader.GetString(0);
                        if (roleName.StartsWith("pg_", StringComparison.Ordinal) ||
                            DangerousPredefinedRoles.Contains(roleName) ||
                            !_allowedReachableRoleNames.Contains(roleName))
                        {
                            reachableRolesRestricted = false;
                        }
                    }
                }
                roleRestricted &= reachableRolesRestricted;

                await using (var clean = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    PostgreSqlTransactionRunner.CleanContextCommandText))
                {
                    contextClean = await clean.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
                }

                await using (var rls = PostgreSqlCommandFactory.Create(connection, transaction, RowLevelSecuritySql))
                {
                    rlsForced = await rls.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
                }

                var applied = await ReadMigrationsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                try
                {
                    PostgreSqlMigrator.ValidateAppliedHistory(applied);
                    migrationsCurrent = applied.Count == PostgreSqlMigrationCatalog.All.Count;
                }
                catch
                {
                    migrationsCurrent = false;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // A newly provisioned hybrid retention store has no approved records yet.
            // Its explicit infrastructure-only admission still verifies TLS, migration
            // history, least privilege, RLS policy, and scope isolation. All existing
            // callers retain the fail-closed non-empty sentinel requirement by default.
            storeSentinelVerified = !_options.RequireStoreSentinel;
            indexStateVerified = !_options.RequireStoreSentinel;
            if (_options.RequireStoreSentinel)
            {
                storeSentinelVerified = true;
                indexStateVerified = true;
                CortexKnowledgeSourceSnapshot? previousSnapshot = null;
                foreach (var tier in SensitivityTiers)
                {
                    var tierScope = _sentinelScope with { MaximumSensitivity = tier };
                    try
                    {
                        var snapshot = await _querySource.GetSnapshotAsync(tierScope, cancellationToken)
                            .ConfigureAwait(false);
                        var snapshotValid = SnapshotValidForReadiness(
                            snapshot,
                            tierScope.Namespace,
                            requireNonEmpty: string.Equals(tier, "restricted", StringComparison.Ordinal)) &&
                            (previousSnapshot is null || ProjectionContains(previousSnapshot, snapshot));
                        var storeState = await ProbeStoreStateAsync(tierScope, snapshot, cancellationToken)
                            .ConfigureAwait(false);
                        if (!snapshotValid || !storeState.StoreSentinelMatches)
                        {
                            storeSentinelVerified = false;
                            issues.Add($"cortex_postgresql_store_sentinel_{tier}_unverified");
                        }
                        if (!storeState.IndexStateVerified)
                        {
                            indexStateVerified = false;
                            issues.Add($"cortex_postgresql_index_state_{tier}_unverified");
                        }
                        if (snapshotValid)
                            previousSnapshot = snapshot;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        storeSentinelVerified = false;
                        indexStateVerified = false;
                        issues.Add($"cortex_postgresql_store_sentinel_{tier}_probe_failed");
                    }
                }
            }

            try
            {
                var scopeIsolationVerified =
                    await ProbeIsolationAsync(
                        _sentinelScope with
                        {
                            TenantId = DifferentScopeValue(
                                _sentinelScope.TenantId,
                                "pegasus-readiness-other-tenant")
                        },
                        SentinelScopeIsolationSql,
                        cancellationToken).ConfigureAwait(false) &&
                    await ProbeIsolationAsync(
                        _sentinelScope with
                        {
                            Namespace = DifferentScopeValue(
                                _sentinelScope.Namespace,
                                "pegasus-readiness-other-namespace")
                        },
                        SentinelScopeIsolationSql,
                        cancellationToken).ConfigureAwait(false) &&
                    await ProbeIsolationAsync(
                        _sentinelScope with { MaximumSensitivity = "public" },
                        PublicTierIsolationSql,
                        cancellationToken).ConfigureAwait(false);
                if (!scopeIsolationVerified)
                {
                    rlsForced = false;
                    issues.Add("cortex_postgresql_rls_scope_isolation_unverified");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                rlsForced = false;
                issues.Add("cortex_postgresql_rls_scope_isolation_probe_failed");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            issues.Add("cortex_postgresql_readiness_probe_failed");
            return Result(
                PostgreSqlReadinessState.NotReady,
                serverMajor,
                tlsActive,
                roleRestricted,
                contextClean,
                migrationsCurrent,
                rlsForced,
                storeSentinelVerified,
                indexStateVerified,
                issues);
        }

        if (serverMajor is null || serverMajor < _options.MinimumServerMajor)
            issues.Add("cortex_postgresql_server_version_unsupported");
        if (_options.RequireTls && !tlsActive)
            issues.Add("cortex_postgresql_tls_inactive");
        if (_options.RequireTls && !_options.HostTlsPolicyVerified)
            issues.Add("cortex_postgresql_host_tls_policy_unverified");
        // A trusted-loopback host attestation can explicitly authorize plaintext local
        // transport. Bootstrap withholds that attestation and therefore stays degraded.
        if (!_options.RequireTls && !_options.HostTlsPolicyVerified)
            issues.Add("cortex_postgresql_local_tls_exception_active");
        if (!roleRestricted)
            issues.Add("cortex_postgresql_runtime_role_overprivileged");
        if (!contextClean)
            issues.Add("cortex_postgresql_context_contaminated");
        if (!migrationsCurrent)
            issues.Add("cortex_postgresql_migrations_not_current");
        if (!rlsForced)
            issues.Add("cortex_postgresql_rls_not_forced");
        if (!storeSentinelVerified)
            issues.Add("cortex_postgresql_store_sentinel_unverified");
        if (!indexStateVerified)
            issues.Add("cortex_postgresql_index_state_unverified");
        if (_transactions.FaultSinkFailureType is not null)
            issues.Add("cortex_postgresql_fault_sink_failed");

        var state = issues.Count == 0
            ? PostgreSqlReadinessState.Ready
            : !_options.RequireTls && issues.All(issue =>
                    issue == "cortex_postgresql_local_tls_exception_active")
                ? PostgreSqlReadinessState.Degraded
                : PostgreSqlReadinessState.NotReady;
        return Result(
            state,
            serverMajor,
            tlsActive,
            roleRestricted,
            contextClean,
            migrationsCurrent,
            rlsForced,
            storeSentinelVerified,
            indexStateVerified,
            issues);
    }

    private async ValueTask<(bool StoreSentinelMatches, bool IndexStateVerified)> ProbeStoreStateAsync(
        CortexQuerySourceScope scope,
        CortexKnowledgeSourceSnapshot snapshot,
        CancellationToken cancellationToken) =>
        await _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromReadiness(scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    StoreStateSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, scope.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, scope.Namespace),
                    new PostgreSqlParameter(
                        "maximum_sensitivity",
                        DbType.String,
                        scope.MaximumSensitivity));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    return (false, false);
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    return (false, false);
                var currentCollectionId = reader.GetString(0);
                var storedPayloadSha256 = reader.GetString(1);
                var recordCount = reader.GetInt64(2);
                var indexReady = reader.GetBoolean(3);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    return (false, false);
                var expectedPayloadSha256 = CortexCanonicalJson.Sha256(
                    CortexCanonicalJson.Serialize(snapshot));
                var sentinelMatches =
                    string.Equals(currentCollectionId, snapshot.CollectionId, StringComparison.Ordinal) &&
                    string.Equals(storedPayloadSha256, expectedPayloadSha256, StringComparison.Ordinal) &&
                    recordCount == snapshot.RecordCount;
                return (sentinelMatches, indexReady);
            },
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<bool> ProbeIsolationAsync(
        CortexQuerySourceScope scope,
        string commandText,
        CancellationToken cancellationToken) =>
        await _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromReadiness(scope),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    commandText,
                    new PostgreSqlParameter(
                        "sentinel_tenant",
                        DbType.String,
                        _sentinelScope.TenantId),
                    new PostgreSqlParameter(
                        "sentinel_namespace",
                        DbType.String,
                        _sentinelScope.Namespace));
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
            },
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<IReadOnlyList<(string Id, string Sha256)>> ReadMigrationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            """
            SELECT migration_id, up_sha256
            FROM cortex_meta.schema_migrations
            ORDER BY sequence_number;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string Id, string Sha256)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (result.Count == 1024)
                throw new InvalidOperationException("The migration ledger exceeds its bounded contract.");
            result.Add((reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    private PostgreSqlPersistenceReadiness Result(
        PostgreSqlReadinessState state,
        int? serverMajor,
        bool tlsActive,
        bool roleRestricted,
        bool contextClean,
        bool migrationsCurrent,
        bool rlsForced,
        bool storeSentinelVerified,
        bool indexStateVerified,
        IReadOnlyList<string> issues) =>
        new(
            state,
            serverMajor,
            tlsActive,
            _options.HostTlsPolicyVerified,
            roleRestricted,
            contextClean,
            migrationsCurrent,
            rlsForced,
            storeSentinelVerified,
            indexStateVerified,
            issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            _timeProvider.GetUtcNow());

    private static PostgreSqlReadinessOptions ValidateOptions(PostgreSqlReadinessOptions options)
    {
        if (options.MinimumServerMajor is < 14 or > 99)
            throw new ArgumentOutOfRangeException(nameof(options), "The minimum PostgreSQL major version is invalid.");
        var roles = options.AllowedReachableRoleNames?.ToArray() ?? [];
        if (roles.Length > 32 ||
            roles.Any(role => string.IsNullOrWhiteSpace(role) || role.Length > 63) ||
            roles.Distinct(StringComparer.Ordinal).Count() != roles.Length ||
            roles.Any(role => role.StartsWith("pg_", StringComparison.Ordinal)) ||
            roles.Any(DangerousPredefinedRoles.Contains))
        {
            throw new ArgumentException(
                "The reachable-role allowlist is invalid or contains a prohibited predefined role.",
                nameof(options));
        }
        return options with { AllowedReachableRoleNames = roles };
    }

    private static bool SnapshotValidForReadiness(
        CortexKnowledgeSourceSnapshot snapshot,
        string expectedNamespace,
        bool requireNonEmpty)
    {
        if (snapshot is null || snapshot.RecordCount < (requireNonEmpty ? 1 : 0) ||
            snapshot.DefaultReadableRecordCount is < 0 ||
            snapshot.DefaultReadableRecordCount > snapshot.RecordCount ||
            string.IsNullOrWhiteSpace(snapshot.ReleaseVersion) || snapshot.ReleaseVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(snapshot.PackageVersion) || snapshot.PackageVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(snapshot.CollectionId) ||
            snapshot.CollectionHash is not { Length: 64 } ||
            snapshot.CollectionHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !string.Equals(snapshot.DefaultNamespace, expectedNamespace, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ContentTreatment, CortexQueryPolicy.ContentTreatment, StringComparison.Ordinal) ||
            snapshot.Executable ||
            !string.Equals(snapshot.SafetyNotice, CortexQueryPolicy.SafetyNotice, StringComparison.Ordinal) ||
            snapshot.Taxonomies is null || snapshot.Freshness is null)
        {
            return false;
        }

        try
        {
            var currentCount = snapshot.Freshness
                .FirstOrDefault(value => string.Equals(value.State, "current", StringComparison.Ordinal))?.Count ?? 0;
            return snapshot.Taxonomies.Count <= CortexQueryLimits.MaximumTaxonomies &&
                snapshot.Taxonomies.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128) &&
                snapshot.Taxonomies.Distinct(StringComparer.Ordinal).Count() == snapshot.Taxonomies.Count &&
                snapshot.Freshness.All(value => value is not null && value.Count >= 0 &&
                    value.State is "current" or "stale" or "blocked" or "unverified" or "unknown") &&
                snapshot.Freshness.Select(value => value.State).Distinct(StringComparer.Ordinal).Count() ==
                    snapshot.Freshness.Count &&
                snapshot.Freshness.Aggregate(0, (total, value) => checked(total + value.Count)) ==
                    snapshot.RecordCount &&
                snapshot.DefaultReadableRecordCount == currentCount;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ProjectionContains(
        CortexKnowledgeSourceSnapshot lower,
        CortexKnowledgeSourceSnapshot higher)
    {
        if (!string.Equals(lower.ReleaseVersion, higher.ReleaseVersion, StringComparison.Ordinal) ||
            !string.Equals(lower.PackageVersion, higher.PackageVersion, StringComparison.Ordinal) ||
            !string.Equals(lower.CollectionId, higher.CollectionId, StringComparison.Ordinal) ||
            !string.Equals(lower.CollectionHash, higher.CollectionHash, StringComparison.Ordinal) ||
            !string.Equals(lower.DefaultNamespace, higher.DefaultNamespace, StringComparison.Ordinal) ||
            !string.Equals(lower.ContentTreatment, higher.ContentTreatment, StringComparison.Ordinal) ||
            !string.Equals(lower.SafetyNotice, higher.SafetyNotice, StringComparison.Ordinal) ||
            lower.Executable != higher.Executable ||
            lower.RecordCount > higher.RecordCount ||
            lower.DefaultReadableRecordCount > higher.DefaultReadableRecordCount ||
            !lower.Taxonomies.SequenceEqual(higher.Taxonomies, StringComparer.Ordinal))
        {
            return false;
        }

        var higherFreshness = higher.Freshness.ToDictionary(value => value.State, value => value.Count, StringComparer.Ordinal);
        return lower.Freshness.All(value =>
            higherFreshness.TryGetValue(value.State, out var higherCount) && value.Count <= higherCount);
    }

    private static string DifferentScopeValue(string current, string candidate) =>
        string.Equals(current, candidate, StringComparison.Ordinal)
            ? $"{candidate}-alternate"
            : candidate;

    private static CortexQuerySourceScope ValidateSentinelScope(CortexQuerySourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Capability != CortexQueryCapability.Status ||
            !string.Equals(scope.Operation, "cortex.query.status", StringComparison.Ordinal) ||
            !string.Equals(scope.MaximumSensitivity, "restricted", StringComparison.Ordinal) ||
            scope.IncludeStale || scope.IncludeBlockedOrUnverified)
        {
            throw new ArgumentException("The readiness sentinel must use an exact Core status scope.", nameof(scope));
        }
        return scope with { };
    }
}
