-- Attempt-first durable storage contract. A host may enable synchronization only after both the
-- established base readiness boundary and the isolated sync readiness probe attest this schema.
CREATE SCHEMA cortex_sync;

REVOKE ALL ON SCHEMA cortex_sync FROM PUBLIC;

-- One immutable copy of the exact public bundle plus bounded relational projections used for
-- transaction-end integrity checks. Canonical bytes preserve extensions and future optional data.
CREATE TABLE cortex_sync.journal_batch
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    store_id text COLLATE "C" NOT NULL CHECK (char_length(store_id) BETWEEN 1 AND 128),
    store_epoch text COLLATE "C" NOT NULL CHECK (char_length(store_epoch) BETWEEN 1 AND 128),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    attempt_id text COLLATE "C" NOT NULL CHECK (char_length(attempt_id) BETWEEN 1 AND 128),
    advances_source_head boolean NOT NULL,
    bundle_id text COLLATE "C" NOT NULL CHECK (char_length(bundle_id) BETWEEN 1 AND 128),
    bundle_schema_version integer NOT NULL CHECK (bundle_schema_version > 0),
    bundle_contract_id text COLLATE "C" NOT NULL CHECK (char_length(bundle_contract_id) BETWEEN 1 AND 256),
    canonicalization text COLLATE "C" NOT NULL CHECK (char_length(canonicalization) BETWEEN 1 AND 128),
    base_server_revision text COLLATE "C" CHECK
        (base_server_revision IS NULL OR char_length(base_server_revision) BETWEEN 1 AND 128),
    previous_bundle_hash text COLLATE "C" NOT NULL CHECK (previous_bundle_hash ~ '^[0-9a-f]{64}$'),
    prior_event_hash text COLLATE "C" NOT NULL CHECK (prior_event_hash ~ '^[0-9a-f]{64}$'),
    first_source_sequence bigint NOT NULL CHECK
        (first_source_sequence BETWEEN 1 AND 9007199254740991),
    last_source_sequence bigint NOT NULL CHECK
        (last_source_sequence BETWEEN first_source_sequence AND 9007199254740991),
    entry_count integer NOT NULL CHECK (entry_count BETWEEN 1 AND 256),
    bundle_hash text COLLATE "C" NOT NULL CHECK (bundle_hash ~ '^[0-9a-f]{64}$'),
    created_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL,
    authority_id text COLLATE "C" NOT NULL CHECK (char_length(authority_id) BETWEEN 1 AND 128),
    subject_id text COLLATE "C" NOT NULL CHECK (char_length(subject_id) BETWEEN 1 AND 128),
    service_id text COLLATE "C" NOT NULL CHECK (char_length(service_id) BETWEEN 1 AND 128),
    policy_revision bigint NOT NULL CHECK (policy_revision > 0),
    extensions_canonical bytea NOT NULL CHECK
        (octet_length(extensions_canonical) BETWEEN 2 AND 131072),
    bundle_canonical bytea NOT NULL CHECK
        (octet_length(bundle_canonical) BETWEEN 1 AND 33554432),
    bundle_canonical_sha256 text COLLATE "C" NOT NULL CHECK
        (bundle_canonical_sha256 ~ '^[0-9a-f]{64}$'),
    PRIMARY KEY (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id),
    UNIQUE
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
        bundle_hash, first_source_sequence, last_source_sequence, entry_count
    ),
    UNIQUE
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id,
        attempt_id, bundle_id, first_source_sequence, last_source_sequence
    ),
    CHECK (last_source_sequence - first_source_sequence + 1 = entry_count)
);

-- Conflicting submissions remain durable attempts, while accepted source-chain identities stay
-- globally unique. The deferred commit trigger proves that this projection agrees with disposition.
CREATE UNIQUE INDEX journal_batch_accepted_bundle_id_unique
    ON cortex_sync.journal_batch
        (tenant_id, namespace_name, store_id, store_epoch, project_id, bundle_id)
    WHERE advances_source_head;
CREATE UNIQUE INDEX journal_batch_accepted_bundle_hash_unique
    ON cortex_sync.journal_batch
        (tenant_id, namespace_name, store_id, store_epoch, project_id, bundle_hash)
    WHERE advances_source_head;
CREATE UNIQUE INDEX journal_batch_accepted_canonical_unique
    ON cortex_sync.journal_batch
        (tenant_id, namespace_name, store_id, store_epoch, project_id, bundle_canonical_sha256)
    WHERE advances_source_head;

-- Event projections retain decoded payload bytes for bounded processing while canonical event,
-- provenance, and extension bytes preserve the exact public evidence received from the client.
CREATE TABLE cortex_sync.journal_entry
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    store_id text COLLATE "C" NOT NULL CHECK (char_length(store_id) BETWEEN 1 AND 128),
    store_epoch text COLLATE "C" NOT NULL CHECK (char_length(store_epoch) BETWEEN 1 AND 128),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    attempt_id text COLLATE "C" NOT NULL CHECK (char_length(attempt_id) BETWEEN 1 AND 128),
    advances_source_head boolean NOT NULL,
    bundle_id text COLLATE "C" NOT NULL CHECK (char_length(bundle_id) BETWEEN 1 AND 128),
    bundle_first_source_sequence bigint NOT NULL CHECK
        (bundle_first_source_sequence BETWEEN 1 AND 9007199254740991),
    bundle_last_source_sequence bigint NOT NULL CHECK
        (bundle_last_source_sequence BETWEEN bundle_first_source_sequence AND 9007199254740991),
    source_sequence bigint NOT NULL CHECK (source_sequence BETWEEN 1 AND 9007199254740991),
    event_id text COLLATE "C" NOT NULL CHECK (char_length(event_id) BETWEEN 1 AND 128),
    event_schema_version integer NOT NULL CHECK (event_schema_version > 0),
    event_contract_id text COLLATE "C" NOT NULL CHECK (char_length(event_contract_id) BETWEEN 1 AND 256),
    canonicalization text COLLATE "C" NOT NULL CHECK (char_length(canonicalization) BETWEEN 1 AND 128),
    previous_event_hash text COLLATE "C" NOT NULL CHECK (previous_event_hash ~ '^[0-9a-f]{64}$'),
    event_hash text COLLATE "C" NOT NULL CHECK (event_hash ~ '^[0-9a-f]{64}$'),
    occurred_at timestamptz NOT NULL,
    producer_instance_id text COLLATE "C" NOT NULL CHECK (char_length(producer_instance_id) BETWEEN 1 AND 128),
    operation_id text COLLATE "C" NOT NULL CHECK (char_length(operation_id) BETWEEN 1 AND 128),
    tenant_id_hint text COLLATE "C" NOT NULL CHECK (char_length(tenant_id_hint) BETWEEN 1 AND 128),
    namespace_hint text COLLATE "C" NOT NULL CHECK (char_length(namespace_hint) BETWEEN 1 AND 256),
    observed_policy_revision bigint CHECK (observed_policy_revision IS NULL OR observed_policy_revision > 0),
    payload_contract_id text COLLATE "C" NOT NULL CHECK (char_length(payload_contract_id) BETWEEN 1 AND 256),
    payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 1 AND 524288),
    payload_bytes integer NOT NULL CHECK (payload_bytes BETWEEN 1 AND 524288),
    payload_sha256 text COLLATE "C" NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    source_kind text COLLATE "C" NOT NULL CHECK
        (source_kind IN ('client_operation', 'legacy_file_copy')),
    source_root_id text COLLATE "C" CHECK
        (source_root_id IS NULL OR char_length(source_root_id) BETWEEN 1 AND 128),
    source_relative_path text COLLATE "C" CHECK
        (source_relative_path IS NULL OR char_length(source_relative_path) BETWEEN 1 AND 512),
    source_bytes bigint NOT NULL CHECK (source_bytes BETWEEN 0 AND 9007199254740991),
    source_sha256 text COLLATE "C" NOT NULL CHECK (source_sha256 ~ '^[0-9a-f]{64}$'),
    source_captured_at timestamptz NOT NULL,
    source_provenance_canonical bytea NOT NULL CHECK
        (octet_length(source_provenance_canonical) BETWEEN 1 AND 8192),
    content_treatment text COLLATE "C" NOT NULL CHECK (content_treatment = 'untrusted_data'),
    executable boolean NOT NULL CHECK (executable = false),
    extensions_canonical bytea NOT NULL CHECK
        (octet_length(extensions_canonical) BETWEEN 2 AND 131072),
    event_canonical bytea NOT NULL CHECK
        (octet_length(event_canonical) BETWEEN 1 AND 1048576),
    event_canonical_sha256 text COLLATE "C" NOT NULL CHECK
        (event_canonical_sha256 ~ '^[0-9a-f]{64}$'),
    PRIMARY KEY
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, event_id),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, source_sequence),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, event_hash),
    UNIQUE
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
        event_id, source_sequence, event_hash
    ),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, event_canonical_sha256),
    FOREIGN KEY
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id,
        attempt_id, bundle_id, bundle_first_source_sequence, bundle_last_source_sequence
    )
        REFERENCES cortex_sync.journal_batch
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id,
            attempt_id, bundle_id, first_source_sequence, last_source_sequence
        )
        DEFERRABLE INITIALLY DEFERRED,
    CHECK (source_sequence BETWEEN bundle_first_source_sequence AND bundle_last_source_sequence),
    CHECK (payload_bytes = octet_length(payload)),
    CHECK (source_captured_at <= occurred_at),
    CHECK
    (
        source_kind = 'client_operation' AND source_root_id IS NULL AND source_relative_path IS NULL OR
        source_kind = 'legacy_file_copy' AND source_root_id IS NOT NULL AND source_relative_path IS NOT NULL
    )
);

CREATE UNIQUE INDEX journal_entry_accepted_sequence_unique
    ON cortex_sync.journal_entry
        (tenant_id, namespace_name, store_id, store_epoch, project_id, source_sequence)
    WHERE advances_source_head;
CREATE UNIQUE INDEX journal_entry_accepted_event_id_unique
    ON cortex_sync.journal_entry
        (tenant_id, namespace_name, store_id, store_epoch, project_id, event_id)
    WHERE advances_source_head;
CREATE UNIQUE INDEX journal_entry_accepted_event_hash_unique
    ON cortex_sync.journal_entry
        (tenant_id, namespace_name, store_id, store_epoch, project_id, event_hash)
    WHERE advances_source_head;

-- A commit is one immutable processing attempt for a bundle. Multiple attempts support durable
-- conflict evidence and response-loss recovery without rewriting the canonical bundle or events.
CREATE TABLE cortex_sync.journal_commit
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    store_id text COLLATE "C" NOT NULL CHECK (char_length(store_id) BETWEEN 1 AND 128),
    store_epoch text COLLATE "C" NOT NULL CHECK (char_length(store_epoch) BETWEEN 1 AND 128),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    attempt_id text COLLATE "C" NOT NULL CHECK (char_length(attempt_id) BETWEEN 1 AND 128),
    advances_source_head boolean NOT NULL,
    bundle_id text COLLATE "C" NOT NULL CHECK (char_length(bundle_id) BETWEEN 1 AND 128),
    bundle_hash text COLLATE "C" NOT NULL CHECK (bundle_hash ~ '^[0-9a-f]{64}$'),
    first_source_sequence bigint NOT NULL CHECK
        (first_source_sequence BETWEEN 1 AND 9007199254740991),
    last_source_sequence bigint NOT NULL CHECK
        (last_source_sequence BETWEEN first_source_sequence AND 9007199254740991),
    source_event_count integer NOT NULL CHECK (source_event_count BETWEEN 1 AND 256),
    commit_id text COLLATE "C" NOT NULL CHECK (char_length(commit_id) BETWEEN 1 AND 128),
    storage_revision text COLLATE "C" NOT NULL CHECK (char_length(storage_revision) BETWEEN 1 AND 128),
    disposition text COLLATE "C" NOT NULL CHECK
        (disposition IN ('committed', 'existing', 'conflict')),
    received_at timestamptz NOT NULL,
    committed_at timestamptz NOT NULL,
    durable_state_hash text COLLATE "C" NOT NULL CHECK (durable_state_hash ~ '^[0-9a-f]{64}$'),
    authority_id text COLLATE "C" NOT NULL CHECK (char_length(authority_id) BETWEEN 1 AND 128),
    subject_id text COLLATE "C" NOT NULL CHECK (char_length(subject_id) BETWEEN 1 AND 128),
    service_id text COLLATE "C" NOT NULL CHECK (char_length(service_id) BETWEEN 1 AND 128),
    policy_revision bigint NOT NULL CHECK (policy_revision > 0),
    PRIMARY KEY
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, commit_id),
    UNIQUE (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id, commit_id),
    UNIQUE
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id,
        attempt_id, bundle_id, commit_id, storage_revision
    ),
    UNIQUE
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id,
        attempt_id, bundle_id, commit_id, storage_revision, disposition
    ),
    FOREIGN KEY
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
        bundle_hash, first_source_sequence, last_source_sequence, source_event_count
    )
        REFERENCES cortex_sync.journal_batch
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
            bundle_hash, first_source_sequence, last_source_sequence, entry_count
        )
        DEFERRABLE INITIALLY DEFERRED,
    CHECK (last_source_sequence - first_source_sequence + 1 = source_event_count),
    CHECK (committed_at >= received_at),
    CHECK (advances_source_head = (disposition = 'committed'))
);

-- Outcomes bind the exact attempt to the exact event inside the exact bundle. The bundle_id in
-- both foreign keys prevents an outcome from borrowing an event with the same identity elsewhere.
CREATE TABLE cortex_sync.journal_outcome
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    store_id text COLLATE "C" NOT NULL CHECK (char_length(store_id) BETWEEN 1 AND 128),
    store_epoch text COLLATE "C" NOT NULL CHECK (char_length(store_epoch) BETWEEN 1 AND 128),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    attempt_id text COLLATE "C" NOT NULL CHECK (char_length(attempt_id) BETWEEN 1 AND 128),
    bundle_id text COLLATE "C" NOT NULL CHECK (char_length(bundle_id) BETWEEN 1 AND 128),
    commit_id text COLLATE "C" NOT NULL CHECK (char_length(commit_id) BETWEEN 1 AND 128),
    source_sequence bigint NOT NULL CHECK (source_sequence BETWEEN 1 AND 9007199254740991),
    event_id text COLLATE "C" NOT NULL CHECK (char_length(event_id) BETWEEN 1 AND 128),
    event_hash text COLLATE "C" NOT NULL CHECK (event_hash ~ '^[0-9a-f]{64}$'),
    disposition text COLLATE "C" NOT NULL CHECK
        (disposition IN ('applied', 'duplicate', 'conflict', 'rejected', 'quarantined')),
    durable boolean NOT NULL CHECK (durable = true),
    server_entity_id text COLLATE "C" CHECK
        (server_entity_id IS NULL OR char_length(server_entity_id) BETWEEN 1 AND 128),
    server_revision_id text COLLATE "C" CHECK
        (server_revision_id IS NULL OR char_length(server_revision_id) BETWEEN 1 AND 128),
    diagnostic_code text COLLATE "C" CHECK
        (diagnostic_code IS NULL OR char_length(diagnostic_code) BETWEEN 1 AND 128),
    PRIMARY KEY
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, commit_id, source_sequence),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, commit_id, event_id),
    FOREIGN KEY
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id, commit_id)
        REFERENCES cortex_sync.journal_commit
            (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id, commit_id)
        DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
        event_id, source_sequence, event_hash
    )
        REFERENCES cortex_sync.journal_entry
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
            event_id, source_sequence, event_hash
        )
        DEFERRABLE INITIALLY DEFERRED
);

-- Only a successful commit attempt advances the immutable source-head chain. The terminal event,
-- bundle hash, sequence, and prior server revision are retained for exact continuation checks.
CREATE TABLE cortex_sync.journal_source_head
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    store_id text COLLATE "C" NOT NULL CHECK (char_length(store_id) BETWEEN 1 AND 128),
    store_epoch text COLLATE "C" NOT NULL CHECK (char_length(store_epoch) BETWEEN 1 AND 128),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    attempt_id text COLLATE "C" NOT NULL CHECK (char_length(attempt_id) BETWEEN 1 AND 128),
    storage_revision text COLLATE "C" NOT NULL CHECK (char_length(storage_revision) BETWEEN 1 AND 128),
    base_server_revision text COLLATE "C" CHECK
        (base_server_revision IS NULL OR char_length(base_server_revision) BETWEEN 1 AND 128),
    bundle_id text COLLATE "C" NOT NULL CHECK (char_length(bundle_id) BETWEEN 1 AND 128),
    commit_id text COLLATE "C" NOT NULL CHECK (char_length(commit_id) BETWEEN 1 AND 128),
    commit_disposition text COLLATE "C" NOT NULL CHECK (commit_disposition = 'committed'),
    committed_event_id text COLLATE "C" NOT NULL CHECK (char_length(committed_event_id) BETWEEN 1 AND 128),
    committed_bundle_hash text COLLATE "C" NOT NULL CHECK (committed_bundle_hash ~ '^[0-9a-f]{64}$'),
    committed_source_sequence bigint NOT NULL CHECK
        (committed_source_sequence BETWEEN 1 AND 9007199254740991),
    committed_chain_hash text COLLATE "C" NOT NULL CHECK (committed_chain_hash ~ '^[0-9a-f]{64}$'),
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    PRIMARY KEY
        (tenant_id, namespace_name, store_id, store_epoch, project_id, storage_revision),
    UNIQUE (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id),
    UNIQUE (tenant_id, namespace_name, store_id, store_epoch, project_id, bundle_id),
    UNIQUE (tenant_id, namespace_name, store_id, store_epoch, project_id, commit_id),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, committed_source_sequence),
    FOREIGN KEY
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id,
        bundle_id, commit_id, storage_revision, commit_disposition
    )
        REFERENCES cortex_sync.journal_commit
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id,
            bundle_id, commit_id, storage_revision, disposition
        )
        DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
        committed_event_id, committed_source_sequence, committed_chain_hash
    )
        REFERENCES cortex_sync.journal_entry
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id,
            event_id, source_sequence, event_hash
        )
        DEFERRABLE INITIALLY DEFERRED,
    FOREIGN KEY
        (tenant_id, namespace_name, store_id, store_epoch, project_id, base_server_revision)
        REFERENCES cortex_sync.journal_source_head
            (tenant_id, namespace_name, store_id, store_epoch, project_id, storage_revision)
        DEFERRABLE INITIALLY DEFERRED,
    CHECK (base_server_revision IS NULL OR base_server_revision <> storage_revision)
);

CREATE UNIQUE INDEX journal_source_head_origin_unique
    ON cortex_sync.journal_source_head
        (tenant_id, namespace_name, store_id, store_epoch, project_id)
    WHERE base_server_revision IS NULL;
CREATE UNIQUE INDEX journal_source_head_child_unique
    ON cortex_sync.journal_source_head
        (tenant_id, namespace_name, store_id, store_epoch, project_id, base_server_revision)
    WHERE base_server_revision IS NOT NULL;

-- One append-only row contains the authenticated terminal public receipt and reconciliation for
-- an exact commit attempt. There is no mutable pending row or unauthenticated follow-up evidence.
CREATE TABLE cortex_sync.journal_ack
(
    tenant_id text COLLATE "C" NOT NULL CHECK (char_length(tenant_id) BETWEEN 1 AND 128),
    namespace_name text COLLATE "C" NOT NULL CHECK (char_length(namespace_name) BETWEEN 1 AND 256),
    store_id text COLLATE "C" NOT NULL CHECK (char_length(store_id) BETWEEN 1 AND 128),
    store_epoch text COLLATE "C" NOT NULL CHECK (char_length(store_epoch) BETWEEN 1 AND 128),
    project_id text COLLATE "C" NOT NULL CHECK (char_length(project_id) BETWEEN 1 AND 128),
    attempt_id text COLLATE "C" NOT NULL CHECK (char_length(attempt_id) BETWEEN 1 AND 128),
    bundle_id text COLLATE "C" NOT NULL CHECK (char_length(bundle_id) BETWEEN 1 AND 128),
    receipt_id text COLLATE "C" NOT NULL CHECK (char_length(receipt_id) BETWEEN 1 AND 128),
    receipt_schema_version integer NOT NULL CHECK (receipt_schema_version > 0),
    receipt_contract_id text COLLATE "C" NOT NULL CHECK (char_length(receipt_contract_id) BETWEEN 1 AND 256),
    canonicalization text COLLATE "C" NOT NULL CHECK (char_length(canonicalization) BETWEEN 1 AND 128),
    server_instance_id text COLLATE "C" NOT NULL CHECK (char_length(server_instance_id) BETWEEN 1 AND 128),
    bundle_hash text COLLATE "C" NOT NULL CHECK (bundle_hash ~ '^[0-9a-f]{64}$'),
    first_source_sequence bigint NOT NULL CHECK
        (first_source_sequence BETWEEN 1 AND 9007199254740991),
    last_source_sequence bigint NOT NULL CHECK
        (last_source_sequence BETWEEN first_source_sequence AND 9007199254740991),
    source_event_count integer NOT NULL CHECK (source_event_count BETWEEN 1 AND 256),
    received_at timestamptz NOT NULL,
    commit_id text COLLATE "C" NOT NULL CHECK (char_length(commit_id) BETWEEN 1 AND 128),
    storage_revision text COLLATE "C" NOT NULL CHECK (char_length(storage_revision) BETWEEN 1 AND 128),
    committed_at timestamptz NOT NULL,
    reconciliation_state text COLLATE "C" NOT NULL CHECK
        (reconciliation_state IN ('reconciled', 'mismatch')),
    matched_event_count integer NOT NULL CHECK
        (matched_event_count BETWEEN 0 AND source_event_count),
    read_back_at timestamptz NOT NULL,
    read_back_hash text COLLATE "C" NOT NULL CHECK (read_back_hash ~ '^[0-9a-f]{64}$'),
    receipt_hash text COLLATE "C" NOT NULL CHECK (receipt_hash ~ '^[0-9a-f]{64}$'),
    proof_contract_id text COLLATE "C" NOT NULL CHECK (char_length(proof_contract_id) BETWEEN 1 AND 256),
    proof_key_id text COLLATE "C" NOT NULL CHECK (char_length(proof_key_id) BETWEEN 1 AND 128),
    proof_algorithm_id text COLLATE "C" NOT NULL CHECK (char_length(proof_algorithm_id) BETWEEN 1 AND 128),
    covered_receipt_hash text COLLATE "C" NOT NULL CHECK (covered_receipt_hash ~ '^[0-9a-f]{64}$'),
    proof bytea NOT NULL CHECK (octet_length(proof) BETWEEN 1 AND 16384),
    proof_sha256 text COLLATE "C" NOT NULL CHECK (proof_sha256 ~ '^[0-9a-f]{64}$'),
    extensions_canonical bytea NOT NULL CHECK
        (octet_length(extensions_canonical) BETWEEN 2 AND 131072),
    receipt_canonical bytea NOT NULL CHECK
        (octet_length(receipt_canonical) BETWEEN 1 AND 33554432),
    receipt_canonical_sha256 text COLLATE "C" NOT NULL CHECK
        (receipt_canonical_sha256 ~ '^[0-9a-f]{64}$'),
    recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    PRIMARY KEY
        (tenant_id, namespace_name, store_id, store_epoch, project_id, receipt_id),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, receipt_hash),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id, bundle_id, commit_id),
    UNIQUE
        (tenant_id, namespace_name, store_id, store_epoch, project_id, receipt_canonical_sha256),
    FOREIGN KEY
    (
        tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id,
        bundle_id, commit_id, storage_revision
    )
        REFERENCES cortex_sync.journal_commit
        (
            tenant_id, namespace_name, store_id, store_epoch, project_id, attempt_id,
            bundle_id, commit_id, storage_revision
        )
        DEFERRABLE INITIALLY DEFERRED,
    CHECK (last_source_sequence - first_source_sequence + 1 = source_event_count),
    CHECK (committed_at >= received_at),
    CHECK (read_back_at >= committed_at),
    CHECK (recorded_at >= read_back_at),
    CHECK (covered_receipt_hash = receipt_hash),
    CHECK (reconciliation_state <> 'reconciled' OR matched_event_count = source_event_count)
);

-- Validate bundle entry count, order, hash continuation, prior bundle continuation, and the
-- presence of a commit attempt at transaction end. All referenced rows remain immutable.
CREATE FUNCTION cortex_private.validate_sync_bundle_integrity()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $cortex_sync_bundle_integrity$
DECLARE
    batch_value cortex_sync.journal_batch%ROWTYPE;
    actual_entry_count bigint;
    minimum_sequence bigint;
    maximum_sequence bigint;
    base_bundle_hash text;
    base_chain_hash text;
    base_sequence bigint;
BEGIN
    SELECT batch_row.*
    INTO batch_value
    FROM cortex_sync.journal_batch AS batch_row
    WHERE batch_row.tenant_id = NEW.tenant_id
      AND batch_row.namespace_name = NEW.namespace_name
      AND batch_row.store_id = NEW.store_id
      AND batch_row.store_epoch = NEW.store_epoch
      AND batch_row.project_id = NEW.project_id
      AND batch_row.attempt_id = NEW.attempt_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'offline bundle row is unavailable at integrity boundary'
            USING ERRCODE = '23503';
    END IF;

    SELECT COUNT(*), MIN(entry_row.source_sequence), MAX(entry_row.source_sequence)
    INTO actual_entry_count, minimum_sequence, maximum_sequence
    FROM cortex_sync.journal_entry AS entry_row
    WHERE entry_row.tenant_id = batch_value.tenant_id
      AND entry_row.namespace_name = batch_value.namespace_name
      AND entry_row.store_id = batch_value.store_id
      AND entry_row.store_epoch = batch_value.store_epoch
      AND entry_row.project_id = batch_value.project_id
      AND entry_row.attempt_id = batch_value.attempt_id
      AND entry_row.bundle_id = batch_value.bundle_id;

    IF actual_entry_count <> batch_value.entry_count OR
       minimum_sequence IS DISTINCT FROM batch_value.first_source_sequence OR
       maximum_sequence IS DISTINCT FROM batch_value.last_source_sequence THEN
        RAISE EXCEPTION 'offline bundle entry count or sequence range is incomplete'
            USING ERRCODE = '23514';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM cortex_sync.journal_entry AS entry_row
        WHERE entry_row.tenant_id = batch_value.tenant_id
          AND entry_row.namespace_name = batch_value.namespace_name
          AND entry_row.store_id = batch_value.store_id
          AND entry_row.store_epoch = batch_value.store_epoch
          AND entry_row.project_id = batch_value.project_id
          AND entry_row.attempt_id = batch_value.attempt_id
          AND entry_row.bundle_id = batch_value.bundle_id
          AND entry_row.advances_source_head IS DISTINCT FROM batch_value.advances_source_head
    ) THEN
        RAISE EXCEPTION 'offline event head projection disagrees with its bundle attempt'
            USING ERRCODE = '23514';
    END IF;

    IF EXISTS
    (
        SELECT 1
        FROM
        (
            SELECT
                entry_row.source_sequence,
                entry_row.previous_event_hash,
                LAG(entry_row.event_hash) OVER (ORDER BY entry_row.source_sequence) AS preceding_event_hash
            FROM cortex_sync.journal_entry AS entry_row
            WHERE entry_row.tenant_id = batch_value.tenant_id
              AND entry_row.namespace_name = batch_value.namespace_name
              AND entry_row.store_id = batch_value.store_id
              AND entry_row.store_epoch = batch_value.store_epoch
              AND entry_row.project_id = batch_value.project_id
              AND entry_row.attempt_id = batch_value.attempt_id
              AND entry_row.bundle_id = batch_value.bundle_id
        ) AS ordered_entry
        WHERE ordered_entry.previous_event_hash IS DISTINCT FROM
            CASE
                WHEN ordered_entry.source_sequence = batch_value.first_source_sequence
                    THEN batch_value.prior_event_hash
                ELSE ordered_entry.preceding_event_hash
            END
    ) THEN
        RAISE EXCEPTION 'offline bundle event hash chain is discontinuous'
            USING ERRCODE = '23514';
    END IF;

    IF batch_value.advances_source_head AND batch_value.base_server_revision IS NULL THEN
        IF batch_value.first_source_sequence <> 1 OR
           batch_value.previous_bundle_hash <> repeat('0', 64) OR
           batch_value.prior_event_hash <> repeat('0', 64) THEN
            RAISE EXCEPTION 'initial offline bundle does not start at the canonical origin'
                USING ERRCODE = '23514';
        END IF;
    ELSIF batch_value.advances_source_head THEN
        SELECT
            head_row.committed_bundle_hash,
            head_row.committed_chain_hash,
            head_row.committed_source_sequence
        INTO base_bundle_hash, base_chain_hash, base_sequence
        FROM cortex_sync.journal_source_head AS head_row
        WHERE head_row.tenant_id = batch_value.tenant_id
          AND head_row.namespace_name = batch_value.namespace_name
          AND head_row.store_id = batch_value.store_id
          AND head_row.store_epoch = batch_value.store_epoch
          AND head_row.project_id = batch_value.project_id
          AND head_row.storage_revision = batch_value.base_server_revision;

        IF NOT FOUND OR
           batch_value.first_source_sequence <> base_sequence + 1 OR
           batch_value.previous_bundle_hash IS DISTINCT FROM base_bundle_hash OR
           batch_value.prior_event_hash IS DISTINCT FROM base_chain_hash THEN
            RAISE EXCEPTION 'offline bundle does not exactly continue its claimed source head'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    IF NOT EXISTS
    (
        SELECT 1
        FROM cortex_sync.journal_commit AS commit_row
        WHERE commit_row.tenant_id = batch_value.tenant_id
          AND commit_row.namespace_name = batch_value.namespace_name
          AND commit_row.store_id = batch_value.store_id
          AND commit_row.store_epoch = batch_value.store_epoch
          AND commit_row.project_id = batch_value.project_id
          AND commit_row.attempt_id = batch_value.attempt_id
          AND commit_row.bundle_id = batch_value.bundle_id
    ) THEN
        RAISE EXCEPTION 'offline bundle has no durable commit attempt'
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$cortex_sync_bundle_integrity$;

-- Validate exact outcome cardinality and disposition coherence. A successful commit must advance
-- one source head whose bundle, event, sequence, and prior revision equal the committed bundle.
CREATE FUNCTION cortex_private.validate_sync_commit_integrity()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $cortex_sync_commit_integrity$
DECLARE
    commit_value cortex_sync.journal_commit%ROWTYPE;
    batch_value cortex_sync.journal_batch%ROWTYPE;
    head_value cortex_sync.journal_source_head%ROWTYPE;
    actual_outcome_count bigint;
    minimum_sequence bigint;
    maximum_sequence bigint;
    head_count bigint;
    terminal_event_id text;
    terminal_event_hash text;
BEGIN
    SELECT commit_row.*
    INTO commit_value
    FROM cortex_sync.journal_commit AS commit_row
    WHERE commit_row.tenant_id = NEW.tenant_id
      AND commit_row.namespace_name = NEW.namespace_name
      AND commit_row.store_id = NEW.store_id
      AND commit_row.store_epoch = NEW.store_epoch
      AND commit_row.project_id = NEW.project_id
      AND commit_row.attempt_id = NEW.attempt_id
      AND commit_row.commit_id = NEW.commit_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'offline commit row is unavailable at integrity boundary'
            USING ERRCODE = '23503';
    END IF;

    SELECT batch_row.*
    INTO STRICT batch_value
    FROM cortex_sync.journal_batch AS batch_row
    WHERE batch_row.tenant_id = commit_value.tenant_id
      AND batch_row.namespace_name = commit_value.namespace_name
      AND batch_row.store_id = commit_value.store_id
      AND batch_row.store_epoch = commit_value.store_epoch
      AND batch_row.project_id = commit_value.project_id
      AND batch_row.attempt_id = commit_value.attempt_id
      AND batch_row.bundle_id = commit_value.bundle_id;

    IF batch_value.advances_source_head IS DISTINCT FROM commit_value.advances_source_head OR
       batch_value.received_at IS DISTINCT FROM commit_value.received_at OR
       batch_value.authority_id IS DISTINCT FROM commit_value.authority_id OR
       batch_value.subject_id IS DISTINCT FROM commit_value.subject_id OR
       batch_value.service_id IS DISTINCT FROM commit_value.service_id OR
       batch_value.policy_revision IS DISTINCT FROM commit_value.policy_revision THEN
        RAISE EXCEPTION 'offline commit provenance disagrees with its bundle attempt'
            USING ERRCODE = '23514';
    END IF;

    SELECT COUNT(*), MIN(outcome_row.source_sequence), MAX(outcome_row.source_sequence)
    INTO actual_outcome_count, minimum_sequence, maximum_sequence
    FROM cortex_sync.journal_outcome AS outcome_row
    WHERE outcome_row.tenant_id = commit_value.tenant_id
      AND outcome_row.namespace_name = commit_value.namespace_name
      AND outcome_row.store_id = commit_value.store_id
      AND outcome_row.store_epoch = commit_value.store_epoch
      AND outcome_row.project_id = commit_value.project_id
      AND outcome_row.attempt_id = commit_value.attempt_id
      AND outcome_row.commit_id = commit_value.commit_id;

    IF actual_outcome_count <> commit_value.source_event_count OR
       minimum_sequence IS DISTINCT FROM commit_value.first_source_sequence OR
       maximum_sequence IS DISTINCT FROM commit_value.last_source_sequence THEN
        RAISE EXCEPTION 'offline commit outcome set is incomplete'
            USING ERRCODE = '23514';
    END IF;

    IF commit_value.disposition = 'committed' AND
       (
           EXISTS
           (
               SELECT 1
               FROM cortex_sync.journal_outcome AS outcome_row
               WHERE outcome_row.tenant_id = commit_value.tenant_id
                 AND outcome_row.namespace_name = commit_value.namespace_name
                  AND outcome_row.store_id = commit_value.store_id
                  AND outcome_row.store_epoch = commit_value.store_epoch
                  AND outcome_row.project_id = commit_value.project_id
                  AND outcome_row.attempt_id = commit_value.attempt_id
                  AND outcome_row.commit_id = commit_value.commit_id
                 AND outcome_row.disposition NOT IN ('applied', 'duplicate')
           ) OR
           NOT EXISTS
           (
               SELECT 1
               FROM cortex_sync.journal_outcome AS outcome_row
               WHERE outcome_row.tenant_id = commit_value.tenant_id
                 AND outcome_row.namespace_name = commit_value.namespace_name
                  AND outcome_row.store_id = commit_value.store_id
                  AND outcome_row.store_epoch = commit_value.store_epoch
                  AND outcome_row.project_id = commit_value.project_id
                  AND outcome_row.attempt_id = commit_value.attempt_id
                  AND outcome_row.commit_id = commit_value.commit_id
                 AND outcome_row.disposition = 'applied'
           )
       ) THEN
        RAISE EXCEPTION 'committed offline attempt has incoherent event dispositions'
            USING ERRCODE = '23514';
    END IF;

    IF commit_value.disposition = 'existing' AND EXISTS
    (
        SELECT 1
        FROM cortex_sync.journal_outcome AS outcome_row
        WHERE outcome_row.tenant_id = commit_value.tenant_id
          AND outcome_row.namespace_name = commit_value.namespace_name
          AND outcome_row.store_id = commit_value.store_id
          AND outcome_row.store_epoch = commit_value.store_epoch
          AND outcome_row.project_id = commit_value.project_id
          AND outcome_row.attempt_id = commit_value.attempt_id
          AND outcome_row.commit_id = commit_value.commit_id
          AND outcome_row.disposition <> 'duplicate'
    ) THEN
        RAISE EXCEPTION 'existing offline attempt has a non-duplicate disposition'
            USING ERRCODE = '23514';
    END IF;

    IF commit_value.disposition = 'conflict' AND
       (
           EXISTS
           (
               SELECT 1
               FROM cortex_sync.journal_outcome AS outcome_row
               WHERE outcome_row.tenant_id = commit_value.tenant_id
                 AND outcome_row.namespace_name = commit_value.namespace_name
                  AND outcome_row.store_id = commit_value.store_id
                  AND outcome_row.store_epoch = commit_value.store_epoch
                  AND outcome_row.project_id = commit_value.project_id
                  AND outcome_row.attempt_id = commit_value.attempt_id
                  AND outcome_row.commit_id = commit_value.commit_id
                 AND outcome_row.disposition = 'applied'
           ) OR
           NOT EXISTS
           (
               SELECT 1
               FROM cortex_sync.journal_outcome AS outcome_row
               WHERE outcome_row.tenant_id = commit_value.tenant_id
                 AND outcome_row.namespace_name = commit_value.namespace_name
                  AND outcome_row.store_id = commit_value.store_id
                  AND outcome_row.store_epoch = commit_value.store_epoch
                  AND outcome_row.project_id = commit_value.project_id
                  AND outcome_row.attempt_id = commit_value.attempt_id
                  AND outcome_row.commit_id = commit_value.commit_id
                 AND outcome_row.disposition IN ('conflict', 'rejected', 'quarantined')
           )
       ) THEN
        RAISE EXCEPTION 'conflicting offline attempt has incoherent event dispositions'
            USING ERRCODE = '23514';
    END IF;

    SELECT COUNT(*)
    INTO head_count
    FROM cortex_sync.journal_source_head AS head_row
    WHERE head_row.tenant_id = commit_value.tenant_id
      AND head_row.namespace_name = commit_value.namespace_name
      AND head_row.store_id = commit_value.store_id
      AND head_row.store_epoch = commit_value.store_epoch
      AND head_row.project_id = commit_value.project_id
      AND head_row.attempt_id = commit_value.attempt_id
      AND head_row.commit_id = commit_value.commit_id;

    IF commit_value.disposition = 'committed' THEN
        IF head_count <> 1 THEN
            RAISE EXCEPTION 'successful offline commit did not advance exactly one source head'
                USING ERRCODE = '23514';
        END IF;

        SELECT head_row.*
        INTO STRICT head_value
        FROM cortex_sync.journal_source_head AS head_row
        WHERE head_row.tenant_id = commit_value.tenant_id
          AND head_row.namespace_name = commit_value.namespace_name
          AND head_row.store_id = commit_value.store_id
          AND head_row.store_epoch = commit_value.store_epoch
          AND head_row.project_id = commit_value.project_id
          AND head_row.attempt_id = commit_value.attempt_id
          AND head_row.commit_id = commit_value.commit_id;

        SELECT entry_row.event_id, entry_row.event_hash
        INTO STRICT terminal_event_id, terminal_event_hash
        FROM cortex_sync.journal_entry AS entry_row
        WHERE entry_row.tenant_id = commit_value.tenant_id
          AND entry_row.namespace_name = commit_value.namespace_name
          AND entry_row.store_id = commit_value.store_id
          AND entry_row.store_epoch = commit_value.store_epoch
          AND entry_row.project_id = commit_value.project_id
          AND entry_row.attempt_id = commit_value.attempt_id
          AND entry_row.bundle_id = commit_value.bundle_id
          AND entry_row.source_sequence = commit_value.last_source_sequence;

        IF head_value.storage_revision IS DISTINCT FROM commit_value.storage_revision OR
           head_value.base_server_revision IS DISTINCT FROM batch_value.base_server_revision OR
           head_value.bundle_id IS DISTINCT FROM commit_value.bundle_id OR
           head_value.committed_event_id IS DISTINCT FROM terminal_event_id OR
           head_value.committed_bundle_hash IS DISTINCT FROM commit_value.bundle_hash OR
           head_value.committed_source_sequence IS DISTINCT FROM commit_value.last_source_sequence OR
           head_value.committed_chain_hash IS DISTINCT FROM terminal_event_hash OR
           head_value.recorded_at < commit_value.committed_at THEN
            RAISE EXCEPTION 'source head does not equal the exact successful offline commit'
                USING ERRCODE = '23514';
        END IF;
    ELSIF head_count <> 0 THEN
        RAISE EXCEPTION 'non-successful offline attempt advanced a source head'
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$cortex_sync_commit_integrity$;

-- Validate that the authenticated terminal receipt projections equal the exact immutable commit.
-- Canonical JSON and proof digests are verified by Core before append and after repository read-back.
CREATE FUNCTION cortex_private.validate_sync_receipt_integrity()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $cortex_sync_receipt_integrity$
DECLARE
    receipt_value cortex_sync.journal_ack%ROWTYPE;
    commit_value cortex_sync.journal_commit%ROWTYPE;
BEGIN
    SELECT receipt_row.*
    INTO receipt_value
    FROM cortex_sync.journal_ack AS receipt_row
    WHERE receipt_row.tenant_id = NEW.tenant_id
      AND receipt_row.namespace_name = NEW.namespace_name
      AND receipt_row.store_id = NEW.store_id
      AND receipt_row.store_epoch = NEW.store_epoch
      AND receipt_row.project_id = NEW.project_id
      AND receipt_row.attempt_id = NEW.attempt_id
      AND receipt_row.receipt_id = NEW.receipt_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'offline receipt row is unavailable at integrity boundary'
            USING ERRCODE = '23503';
    END IF;

    SELECT commit_row.*
    INTO STRICT commit_value
    FROM cortex_sync.journal_commit AS commit_row
    WHERE commit_row.tenant_id = receipt_value.tenant_id
      AND commit_row.namespace_name = receipt_value.namespace_name
      AND commit_row.store_id = receipt_value.store_id
      AND commit_row.store_epoch = receipt_value.store_epoch
      AND commit_row.project_id = receipt_value.project_id
      AND commit_row.attempt_id = receipt_value.attempt_id
      AND commit_row.bundle_id = receipt_value.bundle_id
      AND commit_row.commit_id = receipt_value.commit_id;

    IF receipt_value.bundle_hash IS DISTINCT FROM commit_value.bundle_hash OR
       receipt_value.first_source_sequence IS DISTINCT FROM commit_value.first_source_sequence OR
       receipt_value.last_source_sequence IS DISTINCT FROM commit_value.last_source_sequence OR
       receipt_value.source_event_count IS DISTINCT FROM commit_value.source_event_count OR
       receipt_value.received_at IS DISTINCT FROM commit_value.received_at OR
       receipt_value.storage_revision IS DISTINCT FROM commit_value.storage_revision OR
       receipt_value.committed_at IS DISTINCT FROM commit_value.committed_at THEN
        RAISE EXCEPTION 'terminal receipt does not equal its exact offline commit'
            USING ERRCODE = '23514';
    END IF;

    IF receipt_value.reconciliation_state = 'reconciled' AND
       (
           receipt_value.matched_event_count <> receipt_value.source_event_count OR
           receipt_value.read_back_hash IS DISTINCT FROM commit_value.durable_state_hash
       ) THEN
        RAISE EXCEPTION 'reconciled terminal receipt does not match durable read-back state'
            USING ERRCODE = '23514';
    END IF;

    IF receipt_value.reconciliation_state = 'mismatch' AND
       receipt_value.matched_event_count = receipt_value.source_event_count AND
       receipt_value.read_back_hash = commit_value.durable_state_hash THEN
        RAISE EXCEPTION 'mismatch terminal receipt contains matching durable read-back state'
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$cortex_sync_receipt_integrity$;

REVOKE ALL ON FUNCTION cortex_private.validate_sync_bundle_integrity() FROM PUBLIC;
REVOKE ALL ON FUNCTION cortex_private.validate_sync_commit_integrity() FROM PUBLIC;
REVOKE ALL ON FUNCTION cortex_private.validate_sync_receipt_integrity() FROM PUBLIC;

CREATE TRIGGER journal_batch_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.journal_batch
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER journal_entry_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.journal_entry
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER journal_commit_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.journal_commit
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER journal_outcome_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.journal_outcome
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER journal_source_head_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.journal_source_head
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();
CREATE TRIGGER journal_ack_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.journal_ack
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

CREATE CONSTRAINT TRIGGER journal_batch_integrity
    AFTER INSERT ON cortex_sync.journal_batch
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION cortex_private.validate_sync_bundle_integrity();
CREATE CONSTRAINT TRIGGER journal_commit_integrity
    AFTER INSERT ON cortex_sync.journal_commit
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION cortex_private.validate_sync_commit_integrity();
CREATE CONSTRAINT TRIGGER journal_ack_integrity
    AFTER INSERT ON cortex_sync.journal_ack
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION cortex_private.validate_sync_receipt_integrity();

ALTER TABLE cortex_sync.journal_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_batch FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_entry FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_commit ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_commit FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_outcome ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_outcome FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_source_head ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_source_head FORCE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_ack ENABLE ROW LEVEL SECURITY;
ALTER TABLE cortex_sync.journal_ack FORCE ROW LEVEL SECURITY;

CREATE POLICY journal_batch_select_scope ON cortex_sync.journal_batch
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );
CREATE POLICY journal_batch_append_scope ON cortex_sync.journal_batch
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        authority_id = NULLIF(current_setting('pegasus.cortex.authority_id', true), '') AND
        subject_id = NULLIF(current_setting('pegasus.cortex.subject_id', true), '') AND
        service_id = NULLIF(current_setting('pegasus.cortex.service_id', true), '') AND
        policy_revision::text = NULLIF(current_setting('pegasus.cortex.policy_revision', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );

CREATE POLICY journal_entry_select_scope ON cortex_sync.journal_entry
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );
CREATE POLICY journal_entry_append_scope ON cortex_sync.journal_entry
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );

CREATE POLICY journal_commit_select_scope ON cortex_sync.journal_commit
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );
CREATE POLICY journal_commit_append_scope ON cortex_sync.journal_commit
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        authority_id = NULLIF(current_setting('pegasus.cortex.authority_id', true), '') AND
        subject_id = NULLIF(current_setting('pegasus.cortex.subject_id', true), '') AND
        service_id = NULLIF(current_setting('pegasus.cortex.service_id', true), '') AND
        policy_revision::text = NULLIF(current_setting('pegasus.cortex.policy_revision', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );

CREATE POLICY journal_outcome_select_scope ON cortex_sync.journal_outcome
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );
CREATE POLICY journal_outcome_append_scope ON cortex_sync.journal_outcome
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );

CREATE POLICY journal_source_head_select_scope ON cortex_sync.journal_source_head
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );
CREATE POLICY journal_source_head_append_scope ON cortex_sync.journal_source_head
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );

CREATE POLICY journal_ack_select_scope ON cortex_sync.journal_ack
    FOR SELECT
    USING
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );
CREATE POLICY journal_ack_append_scope ON cortex_sync.journal_ack
    FOR INSERT
    WITH CHECK
    (
        tenant_id = NULLIF(current_setting('pegasus.cortex.tenant_id', true), '') AND
        namespace_name = NULLIF(current_setting('pegasus.cortex.namespace', true), '') AND
        NULLIF(current_setting('pegasus.cortex.capability', true), '') = 'sync'
    );

-- Keep the sync inventory separate so the established 0001 readiness policy inventory remains
-- exact while the sync-specific readiness boundary independently attests these policies.
CREATE TABLE cortex_sync.rls_policy_contract
(
    table_name text COLLATE "C" NOT NULL CHECK (char_length(table_name) BETWEEN 1 AND 63),
    policy_name text COLLATE "C" NOT NULL CHECK (char_length(policy_name) BETWEEN 1 AND 63),
    permissive text COLLATE "C" NOT NULL CHECK (permissive IN ('PERMISSIVE', 'RESTRICTIVE')),
    role_names text[] NOT NULL CHECK (cardinality(role_names) BETWEEN 1 AND 64),
    command_name text COLLATE "C" NOT NULL CHECK (command_name IN ('SELECT', 'INSERT')),
    using_expression text CHECK (using_expression IS NULL OR char_length(using_expression) <= 32768),
    check_expression text CHECK (check_expression IS NULL OR char_length(check_expression) <= 32768),
    PRIMARY KEY (table_name, policy_name),
    CHECK
    (
        command_name = 'SELECT' AND using_expression IS NOT NULL AND check_expression IS NULL OR
        command_name = 'INSERT' AND using_expression IS NULL AND check_expression IS NOT NULL
    )
);

INSERT INTO cortex_sync.rls_policy_contract
    (table_name, policy_name, permissive, role_names, command_name,
     using_expression, check_expression)
SELECT
    policy_value.tablename,
    policy_value.policyname,
    policy_value.permissive,
    policy_value.roles::text[],
    policy_value.cmd,
    policy_value.qual,
    policy_value.with_check
FROM pg_policies AS policy_value
WHERE policy_value.schemaname = 'cortex_sync'
  AND policy_value.tablename IN
      ('journal_source_head', 'journal_batch', 'journal_entry', 'journal_commit',
       'journal_outcome', 'journal_ack')
ORDER BY
    policy_value.tablename COLLATE "C",
    policy_value.policyname COLLATE "C";

DO $cortex_sync_policy_contract$
BEGIN
    IF (SELECT COUNT(*) FROM cortex_sync.rls_policy_contract) <> 12 THEN
        RAISE EXCEPTION 'unexpected Cortex offline-sync RLS policy inventory' USING ERRCODE = '55000';
    END IF;
END;
$cortex_sync_policy_contract$;

CREATE TRIGGER sync_rls_policy_contract_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.rls_policy_contract
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

-- Snapshot the exact catalog shape installed by this migration. Runtime readiness compares the
-- live catalog to these immutable rows so a retained migration checksum cannot hide later DDL,
-- owner, trigger, constraint, index, or function drift.
CREATE TABLE cortex_sync.schema_object_contract
(
    object_kind text COLLATE "C" NOT NULL CHECK
        (object_kind IN ('schema', 'relation', 'column', 'constraint', 'index', 'trigger', 'function')),
    object_identity text COLLATE "C" NOT NULL CHECK
        (char_length(object_identity) BETWEEN 1 AND 512),
    owner_name text COLLATE "C" NOT NULL CHECK (char_length(owner_name) BETWEEN 1 AND 128),
    object_definition text COLLATE "C" NOT NULL CHECK
        (char_length(object_definition) BETWEEN 1 AND 262144),
    PRIMARY KEY (object_kind, object_identity)
);

CREATE TRIGGER sync_schema_object_contract_immutable
    BEFORE UPDATE OR DELETE ON cortex_sync.schema_object_contract
    FOR EACH ROW EXECUTE FUNCTION cortex_private.reject_immutable_change();

WITH installed_schema_object AS
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
    JOIN pg_catalog.pg_namespace AS namespace_value ON namespace_value.oid = table_value.relnamespace
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
)
INSERT INTO cortex_sync.schema_object_contract
    (object_kind, object_identity, owner_name, object_definition)
SELECT object_kind, object_identity, owner_name, object_definition
FROM installed_schema_object
ORDER BY object_kind COLLATE "C", object_identity COLLATE "C";

REVOKE ALL ON ALL TABLES IN SCHEMA cortex_sync FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA cortex_private FROM PUBLIC;
