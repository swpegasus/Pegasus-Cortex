CREATE TABLE cortex_data.project_link
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL
        CHECK (namespace_name = 'project-links'),
    project_link_id text COLLATE "C" NOT NULL CHECK (char_length(project_link_id) BETWEEN 1 AND 128),
    project_namespace text COLLATE "C" NOT NULL CHECK (char_length(project_namespace) BETWEEN 1 AND 128),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    store_id text COLLATE "C" NOT NULL CHECK (char_length(store_id) BETWEEN 1 AND 128),
    store_epoch bigint NOT NULL CHECK (store_epoch > 0),
    endpoint_id text COLLATE "C" NOT NULL CHECK (char_length(endpoint_id) BETWEEN 1 AND 128),
    correlation_hash text COLLATE "C" NOT NULL CHECK (correlation_hash ~ '^[0-9a-f]{64}$'),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 128),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, project_link_id),
    UNIQUE (tenant_id, namespace_name, project_id, store_id, store_epoch, endpoint_id),
    UNIQUE (tenant_id, project_namespace)
);

CREATE TRIGGER project_link_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.project_link
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

ALTER TABLE cortex_data.project_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.project_link FORCE ROW LEVEL SECURITY;

CREATE POLICY project_link_read_scope ON cortex_data.project_link
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('read', 'correlate')
    );

CREATE POLICY project_link_correlate_scope ON cortex_data.project_link
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'correlate'
    );

-- Approved retention proposals use unique record identities, so promotion is append-only.
-- The runtime role receives INSERT but never UPDATE/DELETE on these two tables.
CREATE POLICY knowledge_revision_review_insert_scope ON cortex_data.knowledge_revision
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'review' AND
        record_status = 'active' AND
        content_treatment = 'untrusted_data' AND
        executable = false
    );

CREATE POLICY knowledge_head_review_insert_scope ON cortex_data.knowledge_head
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'review' AND
        generation = 1
    );

INSERT INTO cortex_meta.rls_policy_contract
    (schema_name, table_name, policy_name, permissive, role_names, command_name,
     using_expression, check_expression)
SELECT
    policy_value.schemaname,
    policy_value.tablename,
    policy_value.policyname,
    policy_value.permissive,
    policy_value.roles::text[],
    policy_value.cmd,
    policy_value.qual,
    policy_value.with_check
FROM pg_policies policy_value
WHERE
    policy_value.schemaname = 'cortex_data' AND
    policy_value.policyname IN
    (
        'project_link_read_scope',
        'project_link_correlate_scope',
        'knowledge_revision_review_insert_scope',
        'knowledge_head_review_insert_scope'
    )
ORDER BY policy_value.tablename COLLATE "C", policy_value.policyname COLLATE "C";

DO $cortex_hybrid_policy_contract$
BEGIN
    IF (SELECT COUNT(*) FROM cortex_meta.rls_policy_contract) <> 16 THEN
        RAISE EXCEPTION 'unexpected Cortex hybrid RLS policy inventory' USING ERRCODE = '55000';
    END IF;
END;
$cortex_hybrid_policy_contract$;

REVOKE ALL ON TABLE cortex_data.project_link FROM PUBLIC;
