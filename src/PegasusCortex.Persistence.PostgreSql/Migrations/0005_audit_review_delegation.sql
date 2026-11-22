-- Audit and delegation extend existing immutable tables with additive RLS policies.
-- Existing policy contracts and accepted migrations remain unchanged.
CREATE POLICY learning_proposal_audit_read_scope ON cortex_data.learning_proposal
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'audit'
    );

CREATE POLICY learning_decision_audit_read_scope ON cortex_data.learning_decision
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'audit'
    );

CREATE POLICY learning_decision_audit_insert_scope ON cortex_data.learning_decision
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'audit'
    );

CREATE POLICY audit_event_auditor_read_scope ON cortex_data.audit_event
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('audit', 'delegate_review')
    );

CREATE POLICY audit_event_auditor_append_scope ON cortex_data.audit_event
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') IN ('audit', 'delegate_review')
    );

CREATE POLICY knowledge_revision_audit_insert_scope ON cortex_data.knowledge_revision
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'audit' AND
        record_status = 'active' AND
        content_treatment = 'untrusted_data' AND
        executable = false
    );

CREATE POLICY knowledge_head_audit_insert_scope ON cortex_data.knowledge_head
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'audit' AND
        generation = 1
    );

CREATE INDEX audit_event_kind_id_order
    ON cortex_data.audit_event
        (tenant_id, namespace_name, event_kind COLLATE "C", event_id COLLATE "C");

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
        'learning_proposal_audit_read_scope',
        'learning_decision_audit_read_scope',
        'learning_decision_audit_insert_scope',
        'audit_event_auditor_read_scope',
        'audit_event_auditor_append_scope',
        'knowledge_revision_audit_insert_scope',
        'knowledge_head_audit_insert_scope'
    )
ORDER BY policy_value.tablename COLLATE "C", policy_value.policyname COLLATE "C";

DO $cortex_audit_policy_contract$
BEGIN
    IF (SELECT COUNT(*) FROM cortex_meta.rls_policy_contract) <> 23 THEN
        RAISE EXCEPTION 'unexpected Cortex audit RLS policy inventory' USING ERRCODE = '55000';
    END IF;
END;
$cortex_audit_policy_contract$;
