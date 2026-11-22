-- Preserve the exact validated migration page beside its bundle attempt. These nullable columns
-- leave ordinary synchronization unchanged while making a migration retry distinguishable from
-- a caller that rewraps the same bundle with different cursor evidence.
ALTER TABLE cortex_sync.journal_batch
    ADD COLUMN migration_id text COLLATE "C" CHECK
        (migration_id IS NULL OR char_length(migration_id) BETWEEN 1 AND 128),
    ADD COLUMN migration_page_number bigint CHECK
        (migration_page_number IS NULL OR migration_page_number BETWEEN 1 AND 9007199254740991),
    ADD COLUMN migration_page_id text COLLATE "C" CHECK
        (migration_page_id IS NULL OR char_length(migration_page_id) BETWEEN 1 AND 128),
    ADD COLUMN migration_page_hash text COLLATE "C" CHECK
        (migration_page_hash IS NULL OR migration_page_hash ~ '^[0-9a-f]{64}$'),
    ADD COLUMN migration_current_cursor_hash text COLLATE "C" CHECK
        (migration_current_cursor_hash IS NULL OR migration_current_cursor_hash ~ '^[0-9a-f]{64}$'),
    ADD COLUMN migration_next_cursor_hash text COLLATE "C" CHECK
        (migration_next_cursor_hash IS NULL OR migration_next_cursor_hash ~ '^[0-9a-f]{64}$'),
    ADD COLUMN migration_inventory_hash text COLLATE "C" CHECK
        (migration_inventory_hash IS NULL OR migration_inventory_hash ~ '^[0-9a-f]{64}$'),
    ADD COLUMN migration_page_canonical bytea CHECK
        (migration_page_canonical IS NULL OR octet_length(migration_page_canonical) BETWEEN 1 AND 33554432),
    ADD COLUMN migration_page_canonical_sha256 text COLLATE "C" CHECK
        (migration_page_canonical_sha256 IS NULL OR migration_page_canonical_sha256 ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT journal_batch_migration_page_complete CHECK
    (
        num_nonnulls(
            migration_id,
            migration_page_number,
            migration_page_id,
            migration_page_hash,
            migration_current_cursor_hash,
            migration_next_cursor_hash,
            migration_inventory_hash,
            migration_page_canonical,
            migration_page_canonical_sha256) IN (0, 9)
    );

-- One accepted page number/id/hash is permitted per registered migration and server scope.
-- Exact retries reuse the original attempt; stale or forked successors are rejected before insert.
CREATE UNIQUE INDEX journal_batch_accepted_migration_page_number_unique
    ON cortex_sync.journal_batch
        (tenant_id, namespace_name, store_id, store_epoch, project_id, migration_id, migration_page_number)
    WHERE advances_source_head AND migration_id IS NOT NULL;
CREATE UNIQUE INDEX journal_batch_accepted_migration_page_id_unique
    ON cortex_sync.journal_batch
        (tenant_id, namespace_name, store_id, store_epoch, project_id, migration_id, migration_page_id)
    WHERE advances_source_head AND migration_id IS NOT NULL;
CREATE UNIQUE INDEX journal_batch_accepted_migration_page_hash_unique
    ON cortex_sync.journal_batch
        (tenant_id, namespace_name, store_id, store_epoch, project_id, migration_id, migration_page_hash)
    WHERE advances_source_head AND migration_id IS NOT NULL;

-- Extend the immutable sync schema contract with the new columns, constraint, and indexes. The
-- readiness probe still performs a complete bidirectional catalog comparison.
WITH installed_schema_object AS
(
    SELECT
        'column'::text AS object_kind,
        namespace_value.nspname || '.' || relation_value.relname || '.' || attribute_value.attname
            AS object_identity,
        owner_role.rolname::text AS owner_name,
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
            AS object_definition
    FROM pg_catalog.pg_attribute AS attribute_value
    JOIN pg_catalog.pg_class AS relation_value ON relation_value.oid = attribute_value.attrelid
    JOIN pg_catalog.pg_namespace AS namespace_value ON namespace_value.oid = relation_value.relnamespace
    JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = relation_value.relowner
    LEFT JOIN pg_catalog.pg_attrdef AS default_value
      ON default_value.adrelid = attribute_value.attrelid
     AND default_value.adnum = attribute_value.attnum
    LEFT JOIN pg_catalog.pg_collation AS collation_value
      ON collation_value.oid = attribute_value.attcollation
    LEFT JOIN pg_catalog.pg_namespace AS collation_namespace
      ON collation_namespace.oid = collation_value.collnamespace
    WHERE namespace_value.nspname = 'cortex_sync'
      AND relation_value.relname = 'journal_batch'
      AND attribute_value.attname IN
      (
          'migration_id',
          'migration_page_number',
          'migration_page_id',
          'migration_page_hash',
          'migration_current_cursor_hash',
          'migration_next_cursor_hash',
          'migration_inventory_hash',
          'migration_page_canonical',
          'migration_page_canonical_sha256'
      )

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
    JOIN pg_catalog.pg_namespace AS namespace_value ON namespace_value.oid = relation_value.relnamespace
    JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = relation_value.relowner
    WHERE namespace_value.nspname = 'cortex_sync'
      AND relation_value.relname = 'journal_batch'
      AND
      (
          constraint_value.conname = 'journal_batch_migration_page_complete' OR
          constraint_value.conname LIKE 'journal_batch_migration_%_check'
      )

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
      AND table_value.relname = 'journal_batch'
      AND index_value.relname IN
      (
          'journal_batch_accepted_migration_page_number_unique',
          'journal_batch_accepted_migration_page_id_unique',
          'journal_batch_accepted_migration_page_hash_unique'
      )
)
INSERT INTO cortex_sync.schema_object_contract
    (object_kind, object_identity, owner_name, object_definition)
SELECT object_kind, object_identity, owner_name, object_definition
FROM installed_schema_object
ORDER BY object_kind COLLATE "C", object_identity COLLATE "C";

DO $cortex_sync_migration_page_contract$
BEGIN
    IF
    (
        SELECT COUNT(*)
        FROM cortex_sync.schema_object_contract
        WHERE object_identity LIKE 'cortex_sync.journal_batch.migration_%' OR
              object_identity LIKE 'cortex_sync.journal_batch.journal_batch_migration_%' OR
              object_identity LIKE 'cortex_sync.journal_batch.journal_batch_accepted_migration_%'
    ) <> 22 THEN
        RAISE EXCEPTION 'unexpected legacy migration cursor-authority schema inventory'
            USING ERRCODE = '55000';
    END IF;
END;
$cortex_sync_migration_page_contract$;
