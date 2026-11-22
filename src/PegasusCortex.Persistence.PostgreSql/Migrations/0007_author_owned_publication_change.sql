-- Author-owned publication changes append evidence and move only the mutable
-- knowledge head. Existing knowledge revisions and publication history remain
-- immutable. Legacy publications deliberately receive no inferred owner row.

-- Migration 0007 introduces one UPDATE policy for the narrow SECURITY DEFINER
-- compare-and-swap function. Extend the immutable policy ledger's shape without
-- rewriting any previously captured policy rows.
ALTER TABLE cortex_meta.rls_policy_contract
    DROP CONSTRAINT rls_policy_contract_command_name_check;
ALTER TABLE cortex_meta.rls_policy_contract
    DROP CONSTRAINT rls_policy_contract_check;
ALTER TABLE cortex_meta.rls_policy_contract
    ADD CONSTRAINT rls_policy_contract_command_name_check
        CHECK (command_name IN ('SELECT', 'INSERT', 'UPDATE'));
ALTER TABLE cortex_meta.rls_policy_contract
    ADD CONSTRAINT rls_policy_contract_check
        CHECK
        (
            command_name = 'SELECT' AND using_expression IS NOT NULL AND check_expression IS NULL OR
            command_name = 'INSERT' AND using_expression IS NULL AND check_expression IS NOT NULL OR
            command_name = 'UPDATE' AND using_expression IS NOT NULL AND check_expression IS NOT NULL
        );

-- The two mutation-capable functions are part of the durable schema contract,
-- not merely executable names. Readiness compares their complete installed
-- definitions, owners, SECURITY DEFINER flags, and fixed configurations to
-- this immutable migration-time capture.
CREATE TABLE cortex_meta.privileged_function_contract
(
    function_identity text COLLATE "C" NOT NULL
        CHECK (char_length(function_identity) BETWEEN 1 AND 2048),
    owner_name text COLLATE "C" NOT NULL
        CHECK (char_length(owner_name) BETWEEN 1 AND 63),
    security_definer boolean NOT NULL CHECK (security_definer),
    configuration text[] NOT NULL CHECK (cardinality(configuration) BETWEEN 1 AND 16),
    function_definition text NOT NULL
        CHECK (char_length(function_definition) BETWEEN 1 AND 131072),
    PRIMARY KEY (function_identity)
);
CREATE TRIGGER privileged_function_contract_immutable
    BEFORE UPDATE OR DELETE ON cortex_meta.privileged_function_contract
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
REVOKE ALL ON TABLE cortex_meta.privileged_function_contract FROM PUBLIC;

CREATE TABLE cortex_data.knowledge_publication_origin
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    record_id text COLLATE "C" NOT NULL CHECK (record_id ~ '^krec_[0-9a-f]{64}$'),
    origin_revision_id text COLLATE "C" NOT NULL CHECK (origin_revision_id ~ '^krev_[0-9a-f]{64}$'),
    origin_content_hash text COLLATE "C" NOT NULL CHECK (origin_content_hash ~ '^[0-9a-f]{64}$'),
    proposal_id text COLLATE "C" NOT NULL CHECK (char_length(proposal_id) BETWEEN 1 AND 128),
    proposal_hash text COLLATE "C" NOT NULL CHECK (proposal_hash ~ '^[0-9a-f]{64}$'),
    authority_id text COLLATE "C" NOT NULL CHECK (char_length(authority_id) BETWEEN 1 AND 128),
    subject_id text COLLATE "C" NOT NULL CHECK (char_length(subject_id) BETWEEN 1 AND 128),
    service_id text COLLATE "C" NOT NULL CHECK (char_length(service_id) BETWEEN 1 AND 128),
    policy_revision bigint NOT NULL CHECK (policy_revision > 0),
    published_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, record_id),
    FOREIGN KEY
        (tenant_id, namespace_name, record_id, origin_revision_id, origin_content_hash)
        REFERENCES cortex_data.knowledge_revision
            (tenant_id, namespace_name, record_id, revision_id, content_hash)
        ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, namespace_name, proposal_id, proposal_hash)
        REFERENCES cortex_data.learning_proposal
            (tenant_id, namespace_name, proposal_id, proposal_hash)
        ON DELETE RESTRICT
);

CREATE TABLE cortex_data.knowledge_publication_change
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    change_id text COLLATE "C" NOT NULL CHECK (change_id ~ '^pchg_[0-9a-f]{64}$'),
    change_hash text COLLATE "C" NOT NULL CHECK (change_hash ~ '^[0-9a-f]{64}$'),
    authority_id text COLLATE "C" NOT NULL CHECK (char_length(authority_id) BETWEEN 1 AND 128),
    subject_id text COLLATE "C" NOT NULL CHECK (char_length(subject_id) BETWEEN 1 AND 128),
    service_id text COLLATE "C" NOT NULL CHECK (char_length(service_id) BETWEEN 1 AND 128),
    policy_revision bigint NOT NULL CHECK (policy_revision > 0),
    action text COLLATE "C" NOT NULL CHECK (action IN ('supersede', 'withdraw')),
    target_record_id text COLLATE "C" NOT NULL CHECK (target_record_id ~ '^krec_[0-9a-f]{64}$'),
    expected_head_revision_id text COLLATE "C" NOT NULL
        CHECK (expected_head_revision_id ~ '^krev_[0-9a-f]{64}$'),
    expected_head_generation bigint NOT NULL
        CHECK (expected_head_generation BETWEEN 1 AND 9223372036854775806),
    replacement_record_id text COLLATE "C",
    replacement_revision_id text COLLATE "C",
    replacement_content_hash text COLLATE "C",
    reason_code text COLLATE "C" NOT NULL
        CHECK (reason_code IN ('corrected', 'obsolete', 'duplicate', 'unsafe_content', 'invalid_content')),
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 1024),
    marker_revision_id text COLLATE "C" NOT NULL CHECK (marker_revision_id ~ '^krev_[0-9a-f]{64}$'),
    marker_payload_sha256 text COLLATE "C" NOT NULL
        CHECK (marker_payload_sha256 ~ '^[0-9a-f]{64}$'),
    resulting_head_generation bigint NOT NULL CHECK (resulting_head_generation > 1),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 131072),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    auth_version integer NOT NULL CHECK (auth_version > 0),
    auth_algorithm text COLLATE "C" NOT NULL CHECK (char_length(auth_algorithm) BETWEEN 1 AND 128),
    auth_key_id text COLLATE "C" NOT NULL CHECK (char_length(auth_key_id) BETWEEN 1 AND 128),
    auth_signature bytea NOT NULL CHECK (octet_length(auth_signature) BETWEEN 1 AND 16384),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_name, change_id),
    UNIQUE (tenant_id, namespace_name, change_id, change_hash),
    FOREIGN KEY (tenant_id, namespace_name, target_record_id)
        REFERENCES cortex_data.knowledge_publication_origin
            (tenant_id, namespace_name, record_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, namespace_name, target_record_id, marker_revision_id)
        REFERENCES cortex_data.knowledge_revision
            (tenant_id, namespace_name, record_id, revision_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, namespace_name, change_id)
        REFERENCES cortex_data.audit_event
            (tenant_id, namespace_name, event_id)
        ON DELETE RESTRICT,
    FOREIGN KEY
        (tenant_id, namespace_name, replacement_record_id,
         replacement_revision_id, replacement_content_hash)
        REFERENCES cortex_data.knowledge_revision
            (tenant_id, namespace_name, record_id, revision_id, content_hash)
        MATCH FULL ON DELETE RESTRICT,
    CHECK (change_id = 'pchg_' || change_hash),
    CHECK (marker_revision_id = 'krev_' || change_hash),
    CHECK (resulting_head_generation = expected_head_generation + 1),
    CHECK
    (
        replacement_record_id IS NULL AND
        replacement_revision_id IS NULL AND
        replacement_content_hash IS NULL OR
        replacement_record_id ~ '^krec_[0-9a-f]{64}$' AND
        replacement_revision_id ~ '^krev_[0-9a-f]{64}$' AND
        replacement_content_hash ~ '^[0-9a-f]{64}$' AND
        replacement_record_id <> target_record_id
    ),
    CHECK
    (
        action = 'supersede' AND replacement_record_id IS NOT NULL OR
        action = 'withdraw' AND
        (
            replacement_record_id IS NOT NULL OR
            reason_code IN ('unsafe_content', 'invalid_content')
        )
    )
);

CREATE INDEX knowledge_publication_change_target_order
    ON cortex_data.knowledge_publication_change
        (tenant_id, namespace_name, target_record_id COLLATE "C", occurred_at, change_id COLLATE "C");

CREATE TRIGGER knowledge_publication_origin_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.knowledge_publication_origin
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER knowledge_publication_change_immutable
    BEFORE UPDATE OR DELETE ON cortex_data.knowledge_publication_change
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

ALTER TABLE cortex_data.knowledge_publication_origin ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_publication_origin FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_publication_change ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_data.knowledge_publication_change FORCE ROW LEVEL SECURITY;

-- These policies are usable only while executing a SECURITY DEFINER function:
-- direct runtime SQL has current_user = session_user and therefore fails closed.
CREATE POLICY knowledge_revision_publication_private_read_scope
    ON cortex_data.knowledge_revision
    FOR SELECT
    USING
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('submit', 'review', 'audit')
    );

-- Immutable historical revisions remain available to readiness and audit, but
-- every normal read path is restricted to the revision selected by its current
-- head. This closes direct provider reads as well as catalog/search joins.
CREATE POLICY knowledge_revision_current_head_restrictive_scope
    ON cortex_data.knowledge_revision
    AS RESTRICTIVE
    FOR SELECT
    USING
    (
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN
            ('readiness', 'audit') OR
        EXISTS
        (
            SELECT 1
            FROM cortex_data.knowledge_head current_head
            WHERE current_head.tenant_id = knowledge_revision.tenant_id
              AND current_head.namespace_name = knowledge_revision.namespace_name
              AND current_head.record_id = knowledge_revision.record_id
              AND current_head.current_revision_id = knowledge_revision.revision_id
        )
    );

CREATE POLICY knowledge_revision_publication_change_insert_scope
    ON cortex_data.knowledge_revision
    FOR INSERT
    WITH CHECK
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit' AND
        record_status IN ('superseded', 'tombstone') AND
        content_treatment = 'untrusted_data' AND
        executable = false
    );

CREATE POLICY knowledge_head_publication_private_read_scope
    ON cortex_data.knowledge_head
    FOR SELECT
    USING
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('submit', 'review', 'audit')
    );

CREATE POLICY knowledge_head_publication_change_update_scope
    ON cortex_data.knowledge_head
    FOR UPDATE
    USING
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit'
    )
    WITH CHECK
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit'
    );

CREATE POLICY knowledge_link_publication_change_insert_scope
    ON cortex_data.knowledge_link
    FOR INSERT
    WITH CHECK
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        target_namespace_name = namespace_name AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit' AND
        link_kind IN ('supersedes', 'tombstones')
    );

CREATE POLICY knowledge_publication_origin_private_read_scope
    ON cortex_data.knowledge_publication_origin
    FOR SELECT
    USING
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('submit', 'review', 'audit')
    );

CREATE POLICY knowledge_publication_origin_private_insert_scope
    ON cortex_data.knowledge_publication_origin
    FOR INSERT
    WITH CHECK
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('review', 'audit')
    );

CREATE POLICY knowledge_publication_change_private_read_scope
    ON cortex_data.knowledge_publication_change
    FOR SELECT
    USING
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit'
    );

CREATE POLICY knowledge_publication_change_private_insert_scope
    ON cortex_data.knowledge_publication_change
    FOR INSERT
    WITH CHECK
    (
        current_user <> session_user AND
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        authority_id = NULLIF(current_setting('pegasus.cortex.authority_id', true), '') AND
        subject_id = NULLIF(current_setting('pegasus.cortex.subject_id', true), '') AND
        service_id = NULLIF(current_setting('pegasus.cortex.service_id', true), '') AND
        policy_revision::text = NULLIF(current_setting('pegasus.cortex.policy_revision', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit'
    );

CREATE FUNCTION cortex_private.register_knowledge_publication_origin
(
    p_record_id text,
    p_revision_id text,
    p_content_hash text,
    p_proposal_id text,
    p_proposal_hash text,
    p_authority_id text,
    p_subject_id text,
    p_service_id text,
    p_policy_revision bigint,
    p_published_at timestamptz
)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $cortex_register_publication_origin$
DECLARE
    v_tenant_id text := NULLIF(current_setting('pegasus.cortex.tenant_id', true), '');
    v_namespace_name text := NULLIF(current_setting('pegasus.cortex.namespace', true), '');
    v_capability text := NULLIF(current_setting('pegasus.cortex.capability', true), '');
    v_inserted integer;
BEGIN
    IF
    (
        current_user <> session_user AND
        v_tenant_id IS NOT NULL AND v_namespace_name IS NOT NULL AND
        v_capability IN ('review', 'audit') AND
        p_record_id ~ '^krec_[0-9a-f]{64}$' AND
        p_revision_id ~ '^krev_[0-9a-f]{64}$' AND
        p_content_hash ~ '^[0-9a-f]{64}$' AND
        char_length(p_proposal_id) BETWEEN 1 AND 128 AND
        p_proposal_hash ~ '^[0-9a-f]{64}$' AND
        char_length(p_authority_id) BETWEEN 1 AND 128 AND
        char_length(p_subject_id) BETWEEN 1 AND 128 AND
        char_length(p_service_id) BETWEEN 1 AND 128 AND
        p_policy_revision > 0 AND p_published_at IS NOT NULL
    ) IS NOT TRUE THEN
        RAISE EXCEPTION 'invalid publication-origin registration' USING ERRCODE = '22023';
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM cortex_data.learning_proposal proposal_value
        WHERE proposal_value.tenant_id = v_tenant_id
          AND proposal_value.namespace_name = v_namespace_name
          AND proposal_value.proposal_id = p_proposal_id
          AND proposal_value.proposal_hash = p_proposal_hash
    ) OR
       NOT EXISTS
    (
        SELECT 1
        FROM cortex_data.knowledge_head head_value
        JOIN cortex_data.knowledge_revision revision_value
          ON revision_value.tenant_id = head_value.tenant_id
         AND revision_value.namespace_name = head_value.namespace_name
         AND revision_value.record_id = head_value.record_id
         AND revision_value.revision_id = head_value.current_revision_id
        WHERE head_value.tenant_id = v_tenant_id
          AND head_value.namespace_name = v_namespace_name
          AND head_value.record_id = p_record_id
          AND head_value.current_revision_id = p_revision_id
          AND head_value.generation = 1
          AND revision_value.content_hash = p_content_hash
          AND revision_value.record_status = 'active'
          AND revision_value.content_treatment = 'untrusted_data'
          AND revision_value.executable = false
    ) THEN
        RAISE EXCEPTION 'publication origin is not a new active head' USING ERRCODE = '55000';
    END IF;

    INSERT INTO cortex_data.knowledge_publication_origin
    (
        tenant_id, namespace_name, record_id, origin_revision_id,
        origin_content_hash, proposal_id, proposal_hash, authority_id,
        subject_id, service_id, policy_revision, published_at
    )
    VALUES
    (
        v_tenant_id, v_namespace_name, p_record_id, p_revision_id,
        p_content_hash, p_proposal_id, p_proposal_hash, p_authority_id,
        p_subject_id, p_service_id, p_policy_revision, p_published_at
    )
    ON CONFLICT (tenant_id, namespace_name, record_id) DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    IF v_inserted = 1 THEN
        RETURN true;
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM cortex_data.knowledge_publication_origin origin_value
        WHERE origin_value.tenant_id = v_tenant_id
          AND origin_value.namespace_name = v_namespace_name
          AND origin_value.record_id = p_record_id
          AND origin_value.origin_revision_id = p_revision_id
          AND origin_value.origin_content_hash = p_content_hash
          AND origin_value.proposal_id = p_proposal_id
          AND origin_value.proposal_hash = p_proposal_hash
          AND origin_value.authority_id = p_authority_id
          AND origin_value.subject_id = p_subject_id
          AND origin_value.service_id = p_service_id
          AND origin_value.policy_revision = p_policy_revision
          AND origin_value.published_at = p_published_at
    ) THEN
        RETURN false;
    END IF;

    RAISE EXCEPTION 'publication origin conflicts with immutable ownership' USING ERRCODE = '23505';
END;
$cortex_register_publication_origin$;

CREATE FUNCTION cortex_private.apply_knowledge_publication_change
(
    p_change_id text,
    p_change_hash text,
    p_action text,
    p_target_record_id text,
    p_expected_head_revision_id text,
    p_expected_head_generation bigint,
    p_replacement_record_id text,
    p_replacement_revision_id text,
    p_replacement_content_hash text,
    p_reason_code text,
    p_reason text,
    p_occurred_at timestamptz,
    p_marker_revision_id text,
    p_marker_taxonomy text,
    p_marker_title text,
    p_marker_summary text,
    p_marker_freshness_state text,
    p_marker_sensitivity text,
    p_marker_record_status text,
    p_marker_content_hash text,
    p_marker_content text,
    p_marker_keyword_text text,
    p_marker_applicability jsonb,
    p_marker_payload bytea,
    p_marker_payload_sha256 text,
    p_marker_auth_version integer,
    p_marker_auth_algorithm text,
    p_marker_auth_key_id text,
    p_marker_auth_signature bytea,
    p_change_payload bytea,
    p_change_payload_sha256 text,
    p_change_auth_version integer,
    p_change_auth_algorithm text,
    p_change_auth_key_id text,
    p_change_auth_signature bytea
)
RETURNS TABLE (disposition text, head_revision_id text, head_generation bigint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $cortex_apply_publication_change$
DECLARE
    v_tenant_id text := NULLIF(current_setting('pegasus.cortex.tenant_id', true), '');
    v_namespace_name text := NULLIF(current_setting('pegasus.cortex.namespace', true), '');
    v_authority_id text := NULLIF(current_setting('pegasus.cortex.authority_id', true), '');
    v_subject_id text := NULLIF(current_setting('pegasus.cortex.subject_id', true), '');
    v_service_id text := NULLIF(current_setting('pegasus.cortex.service_id', true), '');
    v_policy_revision_text text := NULLIF(current_setting('pegasus.cortex.policy_revision', true), '');
    v_policy_revision bigint;
    v_owner_authority_id text;
    v_owner_subject_id text;
    v_owner_service_id text;
    v_current_revision_id text;
    v_current_generation bigint;
    v_current_content_hash text;
    v_existing_change_hash text;
    v_existing_authority_id text;
    v_existing_subject_id text;
    v_existing_service_id text;
    v_existing_marker_revision_id text;
    v_existing_generation bigint;
    v_updated integer;
BEGIN
    IF
    (
        current_user <> session_user AND
        v_tenant_id IS NOT NULL AND v_namespace_name IS NOT NULL AND
        v_authority_id IS NOT NULL AND v_subject_id IS NOT NULL AND
        v_service_id IS NOT NULL AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'submit' AND
        v_policy_revision_text ~ '^[1-9][0-9]{0,17}$'
    ) IS NOT TRUE THEN
        RAISE EXCEPTION 'invalid publication-change execution context' USING ERRCODE = '22023';
    END IF;
    v_policy_revision := v_policy_revision_text::bigint;

    IF
    (
        p_change_id ~ '^pchg_[0-9a-f]{64}$' AND
        p_change_hash ~ '^[0-9a-f]{64}$' AND
        p_marker_revision_id ~ '^krev_[0-9a-f]{64}$' AND
        p_target_record_id ~ '^krec_[0-9a-f]{64}$' AND
        p_expected_head_revision_id ~ '^krev_[0-9a-f]{64}$' AND
        p_expected_head_generation BETWEEN 1 AND 9223372036854775806 AND
        p_action IN ('supersede', 'withdraw') AND
        p_reason_code IN ('corrected', 'obsolete', 'duplicate', 'unsafe_content', 'invalid_content') AND
        char_length(p_reason) BETWEEN 1 AND 1024 AND
        p_occurred_at IS NOT NULL AND
        p_marker_record_status =
            CASE p_action WHEN 'supersede' THEN 'superseded' ELSE 'tombstone' END AND
        p_marker_content_hash ~ '^[0-9a-f]{64}$' AND
        p_marker_freshness_state IN ('current', 'stale', 'blocked', 'unverified', 'unknown') AND
        p_marker_sensitivity IN ('public', 'internal', 'confidential', 'restricted')
    ) IS NOT TRUE THEN
        RAISE EXCEPTION 'invalid publication-change request' USING ERRCODE = '22023';
    END IF;

    IF
    (
        p_replacement_record_id IS NULL OR
        p_replacement_revision_id IS NULL OR
        p_replacement_content_hash IS NULL
    ) AND NOT
    (
        p_replacement_record_id IS NULL AND
        p_replacement_revision_id IS NULL AND
        p_replacement_content_hash IS NULL
    ) OR
       p_replacement_record_id IS NOT NULL AND
       (
           p_replacement_record_id !~ '^krec_[0-9a-f]{64}$' OR
           p_replacement_revision_id !~ '^krev_[0-9a-f]{64}$' OR
           p_replacement_content_hash !~ '^[0-9a-f]{64}$' OR
           p_replacement_record_id = p_target_record_id
       ) OR
       p_action = 'supersede' AND p_replacement_record_id IS NULL OR
       p_action = 'withdraw' AND p_replacement_record_id IS NULL AND
           p_reason_code NOT IN ('unsafe_content', 'invalid_content') THEN
        RAISE EXCEPTION 'invalid publication-change replacement' USING ERRCODE = '22023';
    END IF;

    SELECT
        change_value.change_hash,
        change_value.authority_id,
        change_value.subject_id,
        change_value.service_id,
        change_value.marker_revision_id,
        change_value.resulting_head_generation
    INTO
        v_existing_change_hash,
        v_existing_authority_id,
        v_existing_subject_id,
        v_existing_service_id,
        v_existing_marker_revision_id,
        v_existing_generation
    FROM cortex_data.knowledge_publication_change change_value
    WHERE change_value.tenant_id = v_tenant_id
      AND change_value.namespace_name = v_namespace_name
      AND change_value.change_id = p_change_id;
    IF FOUND THEN
        IF v_existing_change_hash <> p_change_hash THEN
            RETURN QUERY SELECT 'conflict'::text, NULL::text, NULL::bigint;
        ELSIF v_existing_authority_id <> v_authority_id OR
              v_existing_subject_id <> v_subject_id OR
              v_existing_service_id <> v_service_id THEN
            RETURN QUERY SELECT 'not_owner'::text, NULL::text, NULL::bigint;
        ELSIF NOT EXISTS
        (
            SELECT 1
            FROM cortex_data.knowledge_publication_change change_value
            WHERE change_value.tenant_id = v_tenant_id
              AND change_value.namespace_name = v_namespace_name
              AND change_value.change_id = p_change_id
              AND change_value.change_hash = p_change_hash
              AND change_value.authority_id = v_authority_id
              AND change_value.subject_id = v_subject_id
              AND change_value.service_id = v_service_id
              AND change_value.policy_revision = v_policy_revision
              AND change_value.action = p_action
              AND change_value.target_record_id = p_target_record_id
              AND change_value.expected_head_revision_id = p_expected_head_revision_id
              AND change_value.expected_head_generation = p_expected_head_generation
              AND change_value.replacement_record_id IS NOT DISTINCT FROM
                  p_replacement_record_id
              AND change_value.replacement_revision_id IS NOT DISTINCT FROM
                  p_replacement_revision_id
              AND change_value.replacement_content_hash IS NOT DISTINCT FROM
                  p_replacement_content_hash
              AND change_value.reason_code = p_reason_code
              AND change_value.reason = p_reason
              AND change_value.marker_revision_id = p_marker_revision_id
              AND change_value.marker_payload_sha256 = p_marker_payload_sha256
              AND change_value.resulting_head_generation =
                  p_expected_head_generation + 1
              AND change_value.payload = p_change_payload
              AND change_value.payload_sha256 = p_change_payload_sha256
              -- Authentication seals protect the stored row, but are not part
              -- of the canonical publication-change identity. A retry after
              -- storage-key rotation must resolve to the existing logical
              -- event when its accepted payload and projections still match.
              AND change_value.occurred_at = p_occurred_at
        ) THEN
            RETURN QUERY SELECT 'conflict'::text, NULL::text, NULL::bigint;
        ELSE
            RETURN QUERY SELECT
                'existing'::text,
                v_existing_marker_revision_id,
                v_existing_generation;
        END IF;
        RETURN;
    END IF;

    IF p_change_id <> 'pchg_' || p_change_hash OR
       p_marker_revision_id <> 'krev_' || p_change_hash THEN
        RAISE EXCEPTION 'invalid publication-change identity' USING ERRCODE = '22023';
    END IF;

    SELECT
        origin_value.authority_id,
        origin_value.subject_id,
        origin_value.service_id
    INTO
        v_owner_authority_id,
        v_owner_subject_id,
        v_owner_service_id
    FROM cortex_data.knowledge_publication_origin origin_value
    WHERE origin_value.tenant_id = v_tenant_id
      AND origin_value.namespace_name = v_namespace_name
      AND origin_value.record_id = p_target_record_id;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'ownership_unavailable'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;
    IF v_owner_authority_id <> v_authority_id OR
       v_owner_subject_id <> v_subject_id OR
       v_owner_service_id <> v_service_id THEN
        RETURN QUERY SELECT 'not_owner'::text, NULL::text, NULL::bigint;
        RETURN;
    END IF;

    -- Lock the target and optional replacement heads in one deterministic
    -- record-id order. This keeps the replacement active through commit and
    -- prevents cross-supersession transactions from taking opposite locks.
    PERFORM 1
    FROM cortex_data.knowledge_head head_value
    WHERE head_value.tenant_id = v_tenant_id
      AND head_value.namespace_name = v_namespace_name
      AND
      (
          head_value.record_id = p_target_record_id OR
          head_value.record_id = p_replacement_record_id
      )
    ORDER BY head_value.record_id COLLATE "C"
    FOR UPDATE;

    SELECT head_value.current_revision_id, head_value.generation
    INTO v_current_revision_id, v_current_generation
    FROM cortex_data.knowledge_head head_value
    WHERE head_value.tenant_id = v_tenant_id
      AND head_value.namespace_name = v_namespace_name
      AND head_value.record_id = p_target_record_id;
    IF NOT FOUND OR
       v_current_revision_id <> p_expected_head_revision_id OR
       v_current_generation <> p_expected_head_generation THEN
        RETURN QUERY SELECT 'stale_head'::text, v_current_revision_id, v_current_generation;
        RETURN;
    END IF;

    SELECT revision_value.content_hash
    INTO v_current_content_hash
    FROM cortex_data.knowledge_revision revision_value
    WHERE revision_value.tenant_id = v_tenant_id
      AND revision_value.namespace_name = v_namespace_name
      AND revision_value.record_id = p_target_record_id
      AND revision_value.revision_id = p_expected_head_revision_id
      AND revision_value.record_status = 'active'
      AND revision_value.content_treatment = 'untrusted_data'
      AND revision_value.executable = false;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'stale_head'::text, v_current_revision_id, v_current_generation;
        RETURN;
    END IF;

    IF p_replacement_record_id IS NOT NULL AND NOT EXISTS
    (
        SELECT 1
        FROM cortex_data.knowledge_head replacement_head
        JOIN cortex_data.knowledge_revision replacement_revision
          ON replacement_revision.tenant_id = replacement_head.tenant_id
         AND replacement_revision.namespace_name = replacement_head.namespace_name
         AND replacement_revision.record_id = replacement_head.record_id
         AND replacement_revision.revision_id = replacement_head.current_revision_id
        WHERE replacement_head.tenant_id = v_tenant_id
          AND replacement_head.namespace_name = v_namespace_name
          AND replacement_head.record_id = p_replacement_record_id
          AND replacement_head.current_revision_id = p_replacement_revision_id
          AND replacement_revision.content_hash = p_replacement_content_hash
          AND replacement_revision.record_status = 'active'
          AND replacement_revision.content_treatment = 'untrusted_data'
          AND replacement_revision.executable = false
    ) THEN
        RETURN QUERY SELECT 'replacement_unavailable'::text, v_current_revision_id, v_current_generation;
        RETURN;
    END IF;

    INSERT INTO cortex_data.knowledge_revision
    (
        tenant_id, namespace_name, record_id, revision_id, taxonomy,
        title, summary, freshness_state, sensitivity, record_status,
        content_treatment, executable, content_hash, content, keyword_text,
        applicability, payload, payload_sha256, auth_version, auth_algorithm,
        auth_key_id, auth_signature
    )
    VALUES
    (
        v_tenant_id, v_namespace_name, p_target_record_id, p_marker_revision_id,
        p_marker_taxonomy, p_marker_title, p_marker_summary,
        p_marker_freshness_state, p_marker_sensitivity, p_marker_record_status,
        'untrusted_data', false, p_marker_content_hash, p_marker_content,
        p_marker_keyword_text, p_marker_applicability, p_marker_payload,
        p_marker_payload_sha256, p_marker_auth_version, p_marker_auth_algorithm,
        p_marker_auth_key_id, p_marker_auth_signature
    );

    INSERT INTO cortex_data.knowledge_link
    (
        tenant_id, namespace_name, record_id, revision_id, link_kind,
        target_namespace_name, target_record_id, target_revision_id,
        target_content_hash, created_at
    )
    VALUES
    (
        v_tenant_id, v_namespace_name, p_target_record_id, p_marker_revision_id,
        'tombstones', v_namespace_name, p_target_record_id,
        p_expected_head_revision_id, v_current_content_hash, p_occurred_at
    );

    IF p_replacement_record_id IS NOT NULL THEN
        INSERT INTO cortex_data.knowledge_link
        (
            tenant_id, namespace_name, record_id, revision_id, link_kind,
            target_namespace_name, target_record_id, target_revision_id,
            target_content_hash, created_at
        )
        VALUES
        (
            v_tenant_id, v_namespace_name, p_replacement_record_id,
            p_replacement_revision_id, 'supersedes', v_namespace_name,
            p_target_record_id, p_expected_head_revision_id,
            v_current_content_hash, p_occurred_at
        );
    END IF;

    INSERT INTO cortex_data.audit_event
    (
        tenant_id, namespace_name, event_id, event_kind, payload,
        payload_sha256, auth_version, auth_algorithm, auth_key_id,
        auth_signature, occurred_at
    )
    VALUES
    (
        v_tenant_id, v_namespace_name, p_change_id,
        'knowledge-publication-change', p_change_payload,
        p_change_payload_sha256, p_change_auth_version,
        p_change_auth_algorithm, p_change_auth_key_id,
        p_change_auth_signature, p_occurred_at
    );

    INSERT INTO cortex_data.knowledge_publication_change
    (
        tenant_id, namespace_name, change_id, change_hash, authority_id,
        subject_id, service_id, policy_revision, action, target_record_id,
        expected_head_revision_id, expected_head_generation,
        replacement_record_id, replacement_revision_id,
        replacement_content_hash, reason_code, reason, marker_revision_id,
        marker_payload_sha256, resulting_head_generation, payload,
        payload_sha256, auth_version,
        auth_algorithm, auth_key_id, auth_signature, occurred_at
    )
    VALUES
    (
        v_tenant_id, v_namespace_name, p_change_id, p_change_hash,
        v_authority_id, v_subject_id, v_service_id, v_policy_revision,
        p_action, p_target_record_id, p_expected_head_revision_id,
        p_expected_head_generation, p_replacement_record_id,
        p_replacement_revision_id, p_replacement_content_hash,
        p_reason_code, p_reason, p_marker_revision_id,
        p_marker_payload_sha256, p_expected_head_generation + 1,
        p_change_payload,
        p_change_payload_sha256, p_change_auth_version,
        p_change_auth_algorithm, p_change_auth_key_id,
        p_change_auth_signature, p_occurred_at
    );

    UPDATE cortex_data.knowledge_head
    SET current_revision_id = p_marker_revision_id,
        generation = p_expected_head_generation + 1,
        updated_at = GREATEST(transaction_timestamp(), p_occurred_at)
    WHERE tenant_id = v_tenant_id
      AND namespace_name = v_namespace_name
      AND record_id = p_target_record_id
      AND current_revision_id = p_expected_head_revision_id
      AND generation = p_expected_head_generation;
    GET DIAGNOSTICS v_updated = ROW_COUNT;
    IF v_updated <> 1 THEN
        RAISE EXCEPTION 'publication head compare-and-swap failed' USING ERRCODE = '40001';
    END IF;

    RETURN QUERY
        SELECT 'applied'::text, p_marker_revision_id, p_expected_head_generation + 1;
END;
$cortex_apply_publication_change$;

INSERT INTO cortex_meta.privileged_function_contract
    (function_identity, owner_name, security_definer, configuration,
     function_definition)
SELECT
    function_namespace.nspname || '.' || function_value.proname || '(' ||
        pg_catalog.pg_get_function_identity_arguments(function_value.oid) || ')',
    owner_role.rolname,
    function_value.prosecdef,
    COALESCE(function_value.proconfig, ARRAY[]::text[]),
    pg_catalog.pg_get_functiondef(function_value.oid)
FROM pg_catalog.pg_proc function_value
JOIN pg_catalog.pg_namespace function_namespace
  ON function_namespace.oid = function_value.pronamespace
JOIN pg_catalog.pg_roles owner_role
  ON owner_role.oid = function_value.proowner
WHERE function_namespace.nspname = 'cortex_private'
  AND function_value.proname IN
      ('register_knowledge_publication_origin',
       'apply_knowledge_publication_change')
ORDER BY function_value.proname COLLATE "C";

DO $cortex_publication_change_function_contract$
BEGIN
    IF (SELECT COUNT(*) FROM cortex_meta.privileged_function_contract) <> 2 OR
       EXISTS
       (
           SELECT 1
           FROM cortex_meta.privileged_function_contract contract_value
           WHERE NOT contract_value.security_definer OR
                 contract_value.configuration IS DISTINCT FROM
                     ARRAY['search_path=pg_catalog']::text[]
       ) THEN
        RAISE EXCEPTION 'unexpected Cortex privileged-function contract'
            USING ERRCODE = '55000';
    END IF;
END;
$cortex_publication_change_function_contract$;

REVOKE ALL ON FUNCTION cortex_private.register_knowledge_publication_origin
    (text, text, text, text, text, text, text, text, bigint, timestamptz)
    FROM PUBLIC;
REVOKE ALL ON FUNCTION cortex_private.apply_knowledge_publication_change
    (text, text, text, text, text, bigint, text, text, text, text, text,
     timestamptz, text, text, text, text, text, text, text, text, text, text,
     jsonb, bytea, text, integer, text, text, bytea, bytea, text, integer,
     text, text, bytea)
    FROM PUBLIC;
REVOKE ALL ON TABLE cortex_data.knowledge_publication_origin FROM PUBLIC;
REVOKE ALL ON TABLE cortex_data.knowledge_publication_change FROM PUBLIC;

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
        'knowledge_revision_publication_private_read_scope',
        'knowledge_revision_current_head_restrictive_scope',
        'knowledge_revision_publication_change_insert_scope',
        'knowledge_head_publication_private_read_scope',
        'knowledge_head_publication_change_update_scope',
        'knowledge_link_publication_change_insert_scope',
        'knowledge_publication_origin_private_read_scope',
        'knowledge_publication_origin_private_insert_scope',
        'knowledge_publication_change_private_read_scope',
        'knowledge_publication_change_private_insert_scope'
    )
ORDER BY policy_value.tablename COLLATE "C", policy_value.policyname COLLATE "C";

DO $cortex_publication_change_policy_contract$
BEGIN
    IF (SELECT COUNT(*) FROM cortex_meta.rls_policy_contract) <> 39 THEN
        RAISE EXCEPTION 'unexpected Cortex publication-change RLS policy inventory' USING ERRCODE = '55000';
    END IF;
END;
$cortex_publication_change_policy_contract$;
