namespace Pegasus.Cortex.Persistence.PostgreSql.Sync;

public sealed partial class PostgreSqlCortexSyncRepository
{
    private const string AdvisoryLockSql = """
        SELECT pg_advisory_xact_lock(hashtextextended(@lock_scope, 0));
        """;

    private const string FindAttemptByCommitSql = """
        SELECT attempt_id
        FROM cortex_sync.journal_commit
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND store_id = @store_id
          AND store_epoch = @store_epoch
          AND project_id = @project_id
          AND bundle_id = @bundle_id
          AND bundle_hash = @bundle_hash
          AND commit_id = @commit_id
          AND storage_revision = @storage_revision
        LIMIT 2;
        """;

    private const string StoredCommitSql = """
        SELECT
            b.attempt_id, b.advances_source_head, b.bundle_id, b.bundle_schema_version,
            b.bundle_contract_id, b.canonicalization, b.base_server_revision,
            b.previous_bundle_hash, b.prior_event_hash, b.first_source_sequence,
            b.last_source_sequence, b.entry_count, b.bundle_hash, b.created_at,
            b.received_at, b.authority_id, b.subject_id, b.service_id, b.policy_revision,
            b.extensions_canonical, b.bundle_canonical, b.bundle_canonical_sha256,
            b.migration_id, b.migration_page_number, b.migration_page_id,
            b.migration_page_hash, b.migration_current_cursor_hash,
            b.migration_next_cursor_hash, b.migration_inventory_hash,
            b.migration_page_canonical, b.migration_page_canonical_sha256,
            c.commit_id, c.storage_revision, c.disposition, c.received_at,
            c.committed_at, c.durable_state_hash, c.authority_id, c.subject_id,
            c.service_id, c.policy_revision
        FROM cortex_sync.journal_batch AS b
        JOIN cortex_sync.journal_commit AS c
          ON c.tenant_id = b.tenant_id
         AND c.namespace_name = b.namespace_name
         AND c.store_id = b.store_id
         AND c.store_epoch = b.store_epoch
         AND c.project_id = b.project_id
         AND c.attempt_id = b.attempt_id
         AND c.bundle_id = b.bundle_id
        WHERE b.tenant_id = @tenant_id
          AND b.namespace_name = @namespace
          AND b.store_id = @store_id
          AND b.store_epoch = @store_epoch
          AND b.project_id = @project_id
          AND b.attempt_id = @attempt_id
        LIMIT 2;
        """;

    private const string StoredEntriesSql = """
        SELECT
            attempt_id, advances_source_head, bundle_id, bundle_first_source_sequence,
            bundle_last_source_sequence, source_sequence, event_id, event_schema_version,
            event_contract_id, canonicalization, previous_event_hash, event_hash,
            occurred_at, producer_instance_id, operation_id, tenant_id_hint,
            namespace_hint, observed_policy_revision, payload_contract_id, payload,
            payload_bytes, payload_sha256, source_kind, source_root_id,
            source_relative_path, source_bytes, source_sha256, source_captured_at,
            source_provenance_canonical, content_treatment, executable,
            extensions_canonical, event_canonical, event_canonical_sha256
        FROM cortex_sync.journal_entry
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND store_id = @store_id
          AND store_epoch = @store_epoch
          AND project_id = @project_id
          AND attempt_id = @attempt_id
          AND bundle_id = @bundle_id
        ORDER BY source_sequence
        LIMIT 257;
        """;

    private const string StoredOutcomesSql = """
        SELECT source_sequence, event_id, event_hash, disposition, durable,
               server_entity_id, server_revision_id, diagnostic_code
        FROM cortex_sync.journal_outcome
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND store_id = @store_id
          AND store_epoch = @store_epoch
          AND project_id = @project_id
          AND attempt_id = @attempt_id
          AND bundle_id = @bundle_id
          AND commit_id = @commit_id
        ORDER BY source_sequence
        LIMIT 257;
        """;

    private const string StoredHeadSql = """
        SELECT base_server_revision, bundle_id, storage_revision,
               committed_source_sequence, committed_event_id, committed_chain_hash,
               recorded_at
        FROM cortex_sync.journal_source_head
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND store_id = @store_id
          AND store_epoch = @store_epoch
          AND project_id = @project_id
          AND attempt_id = @attempt_id
          AND commit_id = @commit_id
        LIMIT 2;
        """;

    private const string CurrentLeafSql = """
        SELECT head_value.storage_revision, head_value.bundle_id,
               head_value.committed_bundle_hash, head_value.committed_source_sequence,
               head_value.committed_event_id, head_value.committed_chain_hash,
               head_value.attempt_id, head_value.commit_id
        FROM cortex_sync.journal_source_head AS head_value
        WHERE head_value.tenant_id = @tenant_id
          AND head_value.namespace_name = @namespace
          AND head_value.store_id = @store_id
          AND head_value.store_epoch = @store_epoch
          AND head_value.project_id = @project_id
          AND NOT EXISTS
          (
              SELECT 1
              FROM cortex_sync.journal_source_head AS child
              WHERE child.tenant_id = head_value.tenant_id
                AND child.namespace_name = head_value.namespace_name
                AND child.store_id = head_value.store_id
                AND child.store_epoch = head_value.store_epoch
                AND child.project_id = head_value.project_id
                AND child.base_server_revision = head_value.storage_revision
          )
        ORDER BY head_value.committed_source_sequence DESC
        LIMIT 2;
        """;

    private const string LatestPublishedAttemptSql = """
        SELECT head_value.attempt_id
        FROM cortex_sync.journal_source_head AS head_value
        JOIN cortex_sync.journal_ack AS ack
          ON ack.tenant_id = head_value.tenant_id
         AND ack.namespace_name = head_value.namespace_name
         AND ack.store_id = head_value.store_id
         AND ack.store_epoch = head_value.store_epoch
         AND ack.project_id = head_value.project_id
         AND ack.attempt_id = head_value.attempt_id
         AND ack.bundle_id = head_value.bundle_id
         AND ack.commit_id = head_value.commit_id
         AND ack.storage_revision = head_value.storage_revision
        WHERE head_value.tenant_id = @tenant_id
          AND head_value.namespace_name = @namespace
          AND head_value.store_id = @store_id
          AND head_value.store_epoch = @store_epoch
          AND head_value.project_id = @project_id
          AND ack.reconciliation_state = 'reconciled'
          AND ack.matched_event_count = ack.source_event_count
        ORDER BY head_value.committed_source_sequence DESC
        LIMIT 1;
        """;

    private const string ExactPublishedAttemptSql = """
        SELECT head_value.attempt_id
        FROM cortex_sync.journal_source_head AS head_value
        JOIN cortex_sync.journal_ack AS ack
          ON ack.tenant_id = head_value.tenant_id
         AND ack.namespace_name = head_value.namespace_name
         AND ack.store_id = head_value.store_id
         AND ack.store_epoch = head_value.store_epoch
         AND ack.project_id = head_value.project_id
         AND ack.attempt_id = head_value.attempt_id
         AND ack.bundle_id = head_value.bundle_id
         AND ack.commit_id = head_value.commit_id
         AND ack.storage_revision = head_value.storage_revision
        WHERE head_value.tenant_id = @tenant_id
          AND head_value.namespace_name = @namespace
          AND head_value.store_id = @store_id
          AND head_value.store_epoch = @store_epoch
          AND head_value.project_id = @project_id
          AND head_value.bundle_id = @bundle_id
          AND head_value.committed_bundle_hash = @bundle_hash
          AND ack.bundle_hash = @bundle_hash
          AND ack.reconciliation_state = 'reconciled'
          AND ack.matched_event_count = ack.source_event_count
        LIMIT 2;
        """;

    private const string InsertBatchSql = """
        INSERT INTO cortex_sync.journal_batch
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id,
            attempt_id, advances_source_head, bundle_id, bundle_schema_version,
            bundle_contract_id, canonicalization, base_server_revision,
            previous_bundle_hash, prior_event_hash, first_source_sequence,
            last_source_sequence, entry_count, bundle_hash, created_at, received_at,
            authority_id, subject_id, service_id, policy_revision,
            extensions_canonical, bundle_canonical, bundle_canonical_sha256,
            migration_id, migration_page_number, migration_page_id,
            migration_page_hash, migration_current_cursor_hash,
            migration_next_cursor_hash, migration_inventory_hash,
            migration_page_canonical, migration_page_canonical_sha256
        )
        VALUES
        (
            @tenant_id, @namespace, @store_id, @store_epoch, @project_id,
            @attempt_id, @advances_source_head, @bundle_id, @bundle_schema_version,
            @bundle_contract_id, @canonicalization, @base_server_revision,
            @previous_bundle_hash, @prior_event_hash, @first_source_sequence,
            @last_source_sequence, @entry_count, @bundle_hash, @created_at, @received_at,
            @authority_id, @subject_id, @service_id, @policy_revision,
            @extensions_canonical, @bundle_canonical, @bundle_canonical_sha256,
            @migration_id, @migration_page_number, @migration_page_id,
            @migration_page_hash, @migration_current_cursor_hash,
            @migration_next_cursor_hash, @migration_inventory_hash,
            @migration_page_canonical, @migration_page_canonical_sha256
        );
        """;

    private const string HasAcceptedMigrationRegistrationSql = """
        SELECT 1
        FROM cortex_sync.journal_batch
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND store_id = @store_id
          AND store_epoch = @store_epoch
          AND project_id = @project_id
          AND migration_id = @migration_id
          AND advances_source_head
        LIMIT 1;
        """;

    private const string CurrentMigrationPredecessorSql = """
        SELECT b.migration_page_id, b.migration_page_canonical,
               b.migration_page_canonical_sha256, c.storage_revision,
               a.receipt_id, a.receipt_hash, a.receipt_canonical_sha256,
               a.receipt_canonical
        FROM cortex_sync.journal_batch AS b
        JOIN cortex_sync.journal_commit AS c
          ON c.tenant_id = b.tenant_id
         AND c.namespace_name = b.namespace_name
         AND c.store_id = b.store_id
         AND c.store_epoch = b.store_epoch
         AND c.project_id = b.project_id
         AND c.attempt_id = b.attempt_id
         AND c.bundle_id = b.bundle_id
        JOIN cortex_sync.journal_ack AS a
          ON a.tenant_id = c.tenant_id
         AND a.namespace_name = c.namespace_name
         AND a.store_id = c.store_id
         AND a.store_epoch = c.store_epoch
         AND a.project_id = c.project_id
         AND a.attempt_id = c.attempt_id
         AND a.bundle_id = c.bundle_id
         AND a.commit_id = c.commit_id
         AND a.storage_revision = c.storage_revision
        WHERE b.tenant_id = @tenant_id
          AND b.namespace_name = @namespace
          AND b.store_id = @store_id
          AND b.store_epoch = @store_epoch
          AND b.project_id = @project_id
          AND b.attempt_id = @attempt_id
          AND c.commit_id = @commit_id
          AND b.advances_source_head
          AND b.migration_page_canonical IS NOT NULL
          AND a.reconciliation_state = 'reconciled'
        LIMIT 2;
        """;

    private const string InsertCommitSql = """
        INSERT INTO cortex_sync.journal_commit
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id,
            attempt_id, advances_source_head, bundle_id, bundle_hash,
            first_source_sequence, last_source_sequence, source_event_count,
            commit_id, storage_revision, disposition, received_at, committed_at,
            durable_state_hash, authority_id, subject_id, service_id, policy_revision
        )
        VALUES
        (
            @tenant_id, @namespace, @store_id, @store_epoch, @project_id,
            @attempt_id, @advances_source_head, @bundle_id, @bundle_hash,
            @first_source_sequence, @last_source_sequence, @source_event_count,
            @commit_id, @storage_revision, @disposition, @received_at, @committed_at,
            @durable_state_hash, @authority_id, @subject_id, @service_id, @policy_revision
        );
        """;

    private const string InsertHeadSql = """
        INSERT INTO cortex_sync.journal_source_head
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id,
            attempt_id, storage_revision, base_server_revision, bundle_id,
            commit_id, commit_disposition, committed_event_id,
            committed_bundle_hash, committed_source_sequence, committed_chain_hash,
            recorded_at
        )
        VALUES
        (
            @tenant_id, @namespace, @store_id, @store_epoch, @project_id,
            @attempt_id, @storage_revision, @base_server_revision, @bundle_id,
            @commit_id, 'committed', @committed_event_id,
            @committed_bundle_hash, @committed_source_sequence, @committed_chain_hash,
            @recorded_at
        );
        """;

    private const string InsertReceiptSql = """
        INSERT INTO cortex_sync.journal_ack
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id,
            attempt_id, bundle_id, receipt_id, receipt_schema_version,
            receipt_contract_id, canonicalization, server_instance_id, bundle_hash,
            first_source_sequence, last_source_sequence, source_event_count,
            received_at, commit_id, storage_revision, committed_at,
            reconciliation_state, matched_event_count, read_back_at, read_back_hash,
            receipt_hash, proof_contract_id, proof_key_id, proof_algorithm_id,
            covered_receipt_hash, proof, proof_sha256, extensions_canonical,
            receipt_canonical, receipt_canonical_sha256, recorded_at
        )
        VALUES
        (
            @tenant_id, @namespace, @store_id, @store_epoch, @project_id,
            @attempt_id, @bundle_id, @receipt_id, @receipt_schema_version,
            @receipt_contract_id, @canonicalization, @server_instance_id, @bundle_hash,
            @first_source_sequence, @last_source_sequence, @source_event_count,
            @received_at, @commit_id, @storage_revision, @committed_at,
            @reconciliation_state, @matched_event_count, @read_back_at, @read_back_hash,
            @receipt_hash, @proof_contract_id, @proof_key_id, @proof_algorithm_id,
            @covered_receipt_hash, @proof, @proof_sha256, @extensions_canonical,
            @receipt_canonical, @receipt_canonical_sha256, @recorded_at
        )
        ON CONFLICT DO NOTHING;
        """;

    private const string FindReceiptByCommitSql = """
        SELECT receipt_id, receipt_hash, receipt_canonical_sha256,
               recorded_at, receipt_canonical
        FROM cortex_sync.journal_ack
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND store_id = @store_id
          AND store_epoch = @store_epoch
          AND project_id = @project_id
          AND bundle_id = @bundle_id
          AND bundle_hash = @bundle_hash
          AND commit_id = @commit_id
          AND storage_revision = @storage_revision
        LIMIT 2;
        """;

    private const string ReadReceiptSql = """
        SELECT receipt_id, receipt_hash, receipt_canonical_sha256,
               recorded_at, receipt_canonical
        FROM cortex_sync.journal_ack
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND store_id = @store_id
          AND store_epoch = @store_epoch
          AND project_id = @project_id
          AND bundle_id = @bundle_id
          AND receipt_id = @receipt_id
          AND receipt_hash = @receipt_hash
          AND receipt_canonical_sha256 = @receipt_canonical_sha256
        LIMIT 2;
        """;

    private const string ForceDeferredConstraintsSql = "SET CONSTRAINTS ALL IMMEDIATE;";
}
