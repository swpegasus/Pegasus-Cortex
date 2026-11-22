CREATE SCHEMA cortex_meta;
CREATE SCHEMA cortex_data;
CREATE SCHEMA cortex_private;
CREATE SCHEMA cortex_index;

REVOKE ALL ON SCHEMA cortex_meta FROM PUBLIC;
REVOKE ALL ON SCHEMA cortex_data FROM PUBLIC;
REVOKE ALL ON SCHEMA cortex_private FROM PUBLIC;
REVOKE ALL ON SCHEMA cortex_index FROM PUBLIC;

CREATE TABLE cortex_meta.schema_migrations
(
    sequence_number bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    migration_id text COLLATE "C" NOT NULL UNIQUE,
    description text NOT NULL,
    up_sha256 text COLLATE "C" NOT NULL CHECK (up_sha256 ~ '^[0-9a-f]{64}$'),
    application_version text COLLATE "C" NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT transaction_timestamp()
);
REVOKE ALL ON TABLE cortex_meta.schema_migrations FROM PUBLIC;

CREATE TABLE cortex_meta.rls_policy_contract
(
    schema_name text COLLATE "C" NOT NULL CHECK (char_length(schema_name) BETWEEN 1 AND 63),
    table_name text COLLATE "C" NOT NULL CHECK (char_length(table_name) BETWEEN 1 AND 63),
    policy_name text COLLATE "C" NOT NULL CHECK (char_length(policy_name) BETWEEN 1 AND 63),
    permissive text COLLATE "C" NOT NULL CHECK (permissive IN ('PERMISSIVE', 'RESTRICTIVE')),
    role_names text[] NOT NULL CHECK (cardinality(role_names) BETWEEN 1 AND 64),
    command_name text COLLATE "C" NOT NULL CHECK (command_name IN ('SELECT', 'INSERT')),
    using_expression text CHECK (using_expression IS NULL OR char_length(using_expression) <= 32768),
    check_expression text CHECK (check_expression IS NULL OR char_length(check_expression) <= 32768),
    PRIMARY KEY (schema_name, table_name, policy_name),
    CHECK
    (
        command_name = 'SELECT' AND using_expression IS NOT NULL AND check_expression IS NULL OR
        command_name = 'INSERT' AND using_expression IS NULL AND check_expression IS NOT NULL
    )
);
REVOKE ALL ON TABLE cortex_meta.rls_policy_contract FROM PUBLIC;

CREATE FUNCTION cortex_private.sensitivity_rank(value text)
RETURNS smallint
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog
AS $$
    SELECT CASE value
        WHEN 'public' THEN 0::smallint
        WHEN 'internal' THEN 1::smallint
        WHEN 'confidential' THEN 2::smallint
        WHEN 'restricted' THEN 3::smallint
        ELSE NULL::smallint
    END;
$$;

CREATE FUNCTION cortex_private.reject_immutable_change()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'immutable Cortex row cannot be changed' USING ERRCODE = '55000';
END;
$$;

CREATE TABLE cortex_data.collection_snapshot
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    maximum_sensitivity text COLLATE "C" NOT NULL
        CHECK (maximum_sensitivity IN ('public', 'internal', 'confidential', 'restricted')),
    collection_id text COLLATE "C" NOT NULL CHECK (char_length(collection_id) BETWEEN 1 AND 128),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 524288),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 128),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    PRIMARY KEY (tenant_id, namespace_name, maximum_sensitivity, collection_id)
);

CREATE TABLE cortex_data.collection_head
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    maximum_sensitivity text COLLATE "C" NOT NULL
        CHECK (maximum_sensitivity IN ('public', 'internal', 'confidential', 'restricted')),
    current_collection_id text COLLATE "C" NOT NULL CHECK (char_length(current_collection_id) BETWEEN 1 AND 128),
    generation bigint NOT NULL CHECK (generation > 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, maximum_sensitivity),
    FOREIGN KEY (tenant_id, namespace_name, maximum_sensitivity, current_collection_id)
        REFERENCES cortex_data.collection_snapshot
            (tenant_id, namespace_name, maximum_sensitivity, collection_id)
);

CREATE TABLE cortex_data.knowledge_revision
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    record_id text COLLATE "C" NOT NULL CHECK (char_length(record_id) BETWEEN 1 AND 128),
    revision_id text COLLATE "C" NOT NULL CHECK (char_length(revision_id) BETWEEN 1 AND 128),
    taxonomy text COLLATE "C" NOT NULL CHECK (char_length(taxonomy) BETWEEN 1 AND 128),
    title text COLLATE "C" NOT NULL CHECK (char_length(title) BETWEEN 1 AND 512),
    summary text NOT NULL CHECK (char_length(summary) BETWEEN 1 AND 2048),
    freshness_state text COLLATE "C" NOT NULL CHECK (freshness_state IN ('current', 'stale', 'blocked', 'unverified', 'unknown')),
    sensitivity text COLLATE "C" NOT NULL CHECK (sensitivity IN ('public', 'internal', 'confidential', 'restricted')),
    record_status text COLLATE "C" NOT NULL CHECK (record_status IN ('active', 'superseded', 'tombstone')),
    content_treatment text COLLATE "C" NOT NULL CHECK (content_treatment = 'untrusted_data'),
    executable boolean NOT NULL CHECK (executable = false),
    content_hash text COLLATE "C" NOT NULL CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    content text NOT NULL CHECK (char_length(content) BETWEEN 1 AND 32768),
    keyword_text text NOT NULL CHECK (char_length(keyword_text) BETWEEN 0 AND 8255),
    applicability jsonb NOT NULL CHECK
        (jsonb_typeof(applicability) = 'array' AND jsonb_array_length(applicability) <= 32 AND
         char_length(applicability::text) <= 131072),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 524288),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 128),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    search_document tsvector GENERATED ALWAYS AS
        (to_tsvector('simple', title || ' ' || summary || ' ' || content || ' ' || keyword_text)) STORED,
    PRIMARY KEY (tenant_id, namespace_name, record_id, revision_id),
    UNIQUE (tenant_id, namespace_name, record_id, revision_id, content_hash)
);

CREATE TABLE cortex_data.knowledge_head
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    record_id text COLLATE "C" NOT NULL CHECK (char_length(record_id) BETWEEN 1 AND 128),
    current_revision_id text COLLATE "C" NOT NULL CHECK (char_length(current_revision_id) BETWEEN 1 AND 128),
    generation bigint NOT NULL CHECK (generation > 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, record_id),
    FOREIGN KEY (tenant_id, namespace_name, record_id, current_revision_id)
        REFERENCES cortex_data.knowledge_revision
            (tenant_id, namespace_name, record_id, revision_id)
);

CREATE TABLE cortex_data.knowledge_link
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    record_id text COLLATE "C" NOT NULL CHECK (char_length(record_id) BETWEEN 1 AND 128),
    revision_id text COLLATE "C" NOT NULL CHECK (char_length(revision_id) BETWEEN 1 AND 128),
    link_kind text COLLATE "C" NOT NULL CHECK (link_kind IN ('supersedes', 'tombstones', 'provenance')),
    target_namespace_name text COLLATE "C" NOT NULL CHECK (char_length(target_namespace_name) BETWEEN 1 AND 256),
    target_record_id text COLLATE "C" NOT NULL CHECK (char_length(target_record_id) BETWEEN 1 AND 128),
    target_revision_id text COLLATE "C" NOT NULL CHECK (char_length(target_revision_id) BETWEEN 1 AND 128),
    target_content_hash text COLLATE "C" NOT NULL CHECK (target_content_hash ~ '^[0-9a-f]{64}$'),
    created_at timestamptz NOT NULL,
    PRIMARY KEY
        (tenant_id, namespace_name, record_id, revision_id, link_kind,
         target_namespace_name, target_record_id, target_revision_id),
    FOREIGN KEY (tenant_id, namespace_name, record_id, revision_id)
        REFERENCES cortex_data.knowledge_revision
            (tenant_id, namespace_name, record_id, revision_id),
    FOREIGN KEY
        (tenant_id, target_namespace_name, target_record_id, target_revision_id, target_content_hash)
        REFERENCES cortex_data.knowledge_revision
            (tenant_id, namespace_name, record_id, revision_id, content_hash)
);

CREATE TABLE cortex_data.learning_proposal
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    proposal_id text COLLATE "C" NOT NULL CHECK (char_length(proposal_id) BETWEEN 1 AND 128),
    proposal_hash text COLLATE "C" NOT NULL CHECK (proposal_hash ~ '^[0-9a-f]{64}$'),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 128),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    policy_revision bigint NOT NULL CHECK (policy_revision > 0),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, proposal_id),
    UNIQUE (tenant_id, namespace_name, proposal_id, proposal_hash)
);

CREATE TABLE cortex_data.learning_decision
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    proposal_id text COLLATE "C" NOT NULL CHECK (char_length(proposal_id) BETWEEN 1 AND 128),
    decision_id text COLLATE "C" NOT NULL CHECK (char_length(decision_id) BETWEEN 1 AND 128),
    proposal_hash text COLLATE "C" NOT NULL CHECK (proposal_hash ~ '^[0-9a-f]{64}$'),
    decision_hash text COLLATE "C" NOT NULL CHECK (decision_hash ~ '^[0-9a-f]{64}$'),
    decision_state text COLLATE "C" NOT NULL CHECK (decision_state IN ('approved', 'rejected')),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 128),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    policy_revision bigint NOT NULL CHECK (policy_revision > 0),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, proposal_id),
    UNIQUE (tenant_id, namespace_name, decision_id),
    FOREIGN KEY (tenant_id, namespace_name, proposal_id, proposal_hash)
        REFERENCES cortex_data.learning_proposal
            (tenant_id, namespace_name, proposal_id, proposal_hash)
);

CREATE TABLE cortex_data.audit_event
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    event_id text COLLATE "C" NOT NULL CHECK (char_length(event_id) BETWEEN 1 AND 128),
    event_kind text COLLATE "C" NOT NULL CHECK (char_length(event_kind) BETWEEN 1 AND 128),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 128),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, event_id)
);

CREATE TABLE cortex_index.generation_state
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    generation bigint NOT NULL CHECK (generation >= 0),
    indexed_through timestamptz,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name)
);

CREATE INDEX knowledge_revision_search_gin
    ON cortex_data.knowledge_revision USING gin (search_document);
CREATE INDEX knowledge_revision_catalog_order
    ON cortex_data.knowledge_revision
        (tenant_id, namespace_name, record_id COLLATE "C", revision_id COLLATE "C");
CREATE INDEX learning_proposal_page_order
    ON cortex_data.learning_proposal
        (tenant_id, namespace_name, proposal_id COLLATE "C");

CREATE TRIGGER collection_snapshot_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.collection_snapshot
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER knowledge_revision_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.knowledge_revision
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER knowledge_link_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.knowledge_link
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER learning_proposal_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.learning_proposal
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER learning_decision_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.learning_decision
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER audit_event_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.audit_event
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

ALTER TABLE cortex_data.collection_snapshot ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.collection_snapshot FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.collection_head ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.collection_head FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_revision ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_revision FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_head ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_head FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_link FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.learning_proposal ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.learning_proposal FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.learning_decision ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.learning_decision FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.audit_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.audit_event FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_index.generation_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_index.generation_state FORCE ROW LEVEL SECURITY;

CREATE POLICY collection_snapshot_scope ON cortex_data.collection_snapshot
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        maximum_sensitivity = NULLIF(current_setting('pegasus.cortex.maximum_sensitivity', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('discover', 'status', 'catalog', 'search', 'read', 'readiness')
    );

CREATE POLICY collection_head_scope ON cortex_data.collection_head
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        maximum_sensitivity = NULLIF(current_setting('pegasus.cortex.maximum_sensitivity', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('discover', 'status', 'catalog', 'search', 'read', 'readiness')
    );

CREATE POLICY knowledge_revision_read_scope ON cortex_data.knowledge_revision
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('catalog', 'search', 'read', 'readiness') AND
        cortex_private.sensitivity_rank(sensitivity) <=
            cortex_private.sensitivity_rank(NULLIF(current_setting('pegasus.cortex.maximum_sensitivity', true), '')) AND
        (
            NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'readiness' OR
            freshness_state = 'current' OR
            freshness_state = 'stale' AND
                NULLIF(current_setting('pegasus.cortex.allow_stale', true), '') = 'true' OR
            freshness_state IN ('blocked', 'unverified', 'unknown') AND
                NULLIF(current_setting('pegasus.cortex.allow_blocked_or_unverified', true), '') = 'true'
        )
    );

CREATE POLICY knowledge_head_read_scope ON cortex_data.knowledge_head
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('catalog', 'search', 'read', 'readiness')
    );

CREATE POLICY knowledge_link_read_scope ON cortex_data.knowledge_link
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'read'
    );

CREATE POLICY learning_proposal_read_scope ON cortex_data.learning_proposal
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('read', 'review', 'submit')
    );
CREATE POLICY learning_proposal_submit_scope ON cortex_data.learning_proposal
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit'
    );

CREATE POLICY learning_decision_read_scope ON cortex_data.learning_decision
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('read', 'review')
    );
CREATE POLICY learning_decision_review_scope ON cortex_data.learning_decision
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'review'
    );

CREATE POLICY audit_event_read_scope ON cortex_data.audit_event
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'read'
    );
CREATE POLICY audit_event_append_scope ON cortex_data.audit_event
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('submit', 'review')
    );

CREATE POLICY generation_state_read_scope ON cortex_index.generation_state
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'readiness'
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
WHERE policy_value.schemaname IN ('cortex_data', 'cortex_index')
ORDER BY
    policy_value.schemaname COLLATE "C",
    policy_value.tablename COLLATE "C",
    policy_value.policyname COLLATE "C";

DO $cortex_policy_contract$
BEGIN
    IF (SELECT COUNT(*) FROM cortex_meta.rls_policy_contract) <> 12 THEN
        RAISE EXCEPTION 'unexpected Cortex RLS policy inventory' USING ERRCODE = '55000';
    END IF;
END;
$cortex_policy_contract$;

CREATE TRIGGER rls_policy_contract_immutable
BEFORE UPDATE OR DELETE ON cortex_meta.rls_policy_contract
FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

REVOKE ALL ON ALL TABLES IN SCHEMA cortex_data FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA cortex_index FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA cortex_private FROM PUBLIC;
