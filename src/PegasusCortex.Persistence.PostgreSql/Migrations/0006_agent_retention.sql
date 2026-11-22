-- Passive agent retention is append-only. Current heads are derived from the
-- highest retained revision; no mutable server row can silently select a winner.
CREATE TABLE cortex_data.retained_chain
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    kind text COLLATE "C" NOT NULL CHECK (char_length(kind) BETWEEN 1 AND 128),
    document_id text COLLATE "C" NOT NULL CHECK (char_length(document_id) BETWEEN 1 AND 128),
    created_at timestamptz NOT NULL,
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 64),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    PRIMARY KEY (tenant_id, namespace_name, project_id, kind, document_id)
);

-- A project has one task-snapshot chain. Other passive kinds may have multiple
-- document chains, matching the provider-neutral retention contract.
CREATE UNIQUE INDEX retained_chain_single_task_snapshot
    ON cortex_data.retained_chain (tenant_id, namespace_name, project_id)
    WHERE kind = 'task-snapshot';

CREATE TABLE cortex_data.retained_document
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    kind text COLLATE "C" NOT NULL CHECK (char_length(kind) BETWEEN 1 AND 128),
    document_id text COLLATE "C" NOT NULL CHECK (char_length(document_id) BETWEEN 1 AND 128),
    revision bigint NOT NULL CHECK (revision > 0),
    previous_revision bigint GENERATED ALWAYS AS
        (CASE WHEN revision > 1 THEN revision - 1 ELSE NULL END) STORED,
    previous_hash text COLLATE "C" NULL,
    idempotency_key text COLLATE "C" NOT NULL CHECK (char_length(idempotency_key) BETWEEN 1 AND 128),
    producer_id text COLLATE "C" NOT NULL CHECK (char_length(producer_id) BETWEEN 1 AND 128),
    contract_id text COLLATE "C" NOT NULL CHECK (char_length(contract_id) BETWEEN 1 AND 256),
    content_hash text COLLATE "C" NOT NULL CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    search_text text NOT NULL CHECK (char_length(search_text) <= 4096),
    search_document tsvector GENERATED ALWAYS AS
        (to_tsvector('simple', search_text)) STORED,
    captured_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > captured_at),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 64),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    PRIMARY KEY (tenant_id, namespace_name, project_id, kind, document_id, revision),
    UNIQUE (tenant_id, namespace_name, project_id, kind, document_id, revision, content_hash),
    UNIQUE (tenant_id, namespace_name, project_id, kind, producer_id, idempotency_key),
    FOREIGN KEY (tenant_id, namespace_name, project_id, kind, document_id)
        REFERENCES cortex_data.retained_chain
            (tenant_id, namespace_name, project_id, kind, document_id)
        ON DELETE RESTRICT,
    FOREIGN KEY
        (tenant_id, namespace_name, project_id, kind, document_id,
         previous_revision, previous_hash)
        REFERENCES cortex_data.retained_document
            (tenant_id, namespace_name, project_id, kind, document_id,
             revision, content_hash)
        MATCH FULL DEFERRABLE INITIALLY DEFERRED,
    CHECK
    (
        (revision = 1 AND previous_hash IS NULL) OR
        (revision > 1 AND previous_hash ~ '^[0-9a-f]{64}$')
    )
);

CREATE INDEX retained_document_head_order
    ON cortex_data.retained_document
        (tenant_id, namespace_name, project_id, kind, document_id, revision DESC);

CREATE INDEX retained_document_search
    ON cortex_data.retained_document USING gin (search_document);

CREATE TABLE cortex_data.retention_audit
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    audit_id text COLLATE "C" NOT NULL CHECK (audit_id ~ '^[0-9a-f]{64}$'),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    kind text COLLATE "C" NOT NULL CHECK (char_length(kind) BETWEEN 1 AND 128),
    document_id text COLLATE "C" NOT NULL CHECK (char_length(document_id) BETWEEN 1 AND 128),
    revision bigint NOT NULL CHECK (revision > 0),
    content_hash text COLLATE "C" NOT NULL CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    producer_id text COLLATE "C" NOT NULL CHECK (char_length(producer_id) BETWEEN 1 AND 128),
    occurred_at timestamptz NOT NULL,
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 64),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    PRIMARY KEY (tenant_id, namespace_name, audit_id),
    FOREIGN KEY
        (tenant_id, namespace_name, project_id, kind, document_id,
         revision, content_hash)
        REFERENCES cortex_data.retained_document
            (tenant_id, namespace_name, project_id, kind, document_id,
             revision, content_hash)
        ON DELETE RESTRICT
);

CREATE TRIGGER retained_chain_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.retained_chain
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

CREATE TRIGGER retained_document_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.retained_document
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

CREATE TRIGGER retention_audit_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.retention_audit
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

ALTER TABLE cortex_data.retained_chain ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.retained_chain FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.retained_document ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.retained_document FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.retention_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.retention_audit FORCE ROW LEVEL SECURITY;

CREATE POLICY retained_chain_read_scope ON cortex_data.retained_chain
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('retained_read', 'retained_write', 'readiness')
    );

CREATE POLICY retained_chain_append_scope ON cortex_data.retained_chain
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'retained_write'
    );

CREATE POLICY retained_document_read_scope ON cortex_data.retained_document
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('retained_read', 'retained_write', 'readiness')
    );

CREATE POLICY retained_document_append_scope ON cortex_data.retained_document
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'retained_write'
    );

CREATE POLICY retention_audit_read_scope ON cortex_data.retention_audit
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('retained_read', 'retained_write', 'readiness')
    );

CREATE POLICY retention_audit_append_scope ON cortex_data.retention_audit
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'retained_write'
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
        'retained_chain_read_scope',
        'retained_chain_append_scope',
        'retained_document_read_scope',
        'retained_document_append_scope',
        'retention_audit_read_scope',
        'retention_audit_append_scope'
    )
ORDER BY policy_value.tablename COLLATE "C", policy_value.policyname COLLATE "C";

DO $cortex_agent_retention_policy_contract$
BEGIN
    IF (SELECT COUNT(*) FROM cortex_meta.rls_policy_contract) <> 29 THEN
        RAISE EXCEPTION 'unexpected Cortex agent-retention RLS policy inventory' USING ERRCODE = '55000';
    END IF;
END;
$cortex_agent_retention_policy_contract$;

REVOKE ALL ON TABLE cortex_data.retained_chain FROM PUBLIC;
REVOKE ALL ON TABLE cortex_data.retained_document FROM PUBLIC;
REVOKE ALL ON TABLE cortex_data.retention_audit FROM PUBLIC;
