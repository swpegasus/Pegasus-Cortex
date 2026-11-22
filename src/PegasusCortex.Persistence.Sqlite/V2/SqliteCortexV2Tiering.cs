using Microsoft.Data.Sqlite;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2Store
{
    private const int TieringMaximumRecords = 16, TieringMaximumRevisions = 256;
    private const long TieringMaximumBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Explicit operator upgrade, requiring the product owner to have quiesced
    /// all older readers. Ordinary open/create never calls this method. The
    /// table rebuild is transactional and bounded in managed memory; total disk
    /// space/work scales with the blob table. No record/replay metadata is erased.
    /// </summary>
    public ValueTask<SqliteCortexTieringReceipt> UpgradePayloadStorageAsync(int expectedSchemaVersion,
        CancellationToken cancellationToken = default) =>
        UpgradePayloadStorageCoreAsync(expectedSchemaVersion, _options.EnablePayloadOffload, cancellationToken);

    private async ValueTask<SqliteCortexTieringReceipt> UpgradePayloadStorageCoreAsync(int expectedSchemaVersion,
        bool admitted, CancellationToken cancellationToken)
    {
        if (!admitted || expectedSchemaVersion != SchemaVersion)
            throw new InvalidOperationException("Explicit schema-2 payload-tiering upgrade admission is required.");
        await InitializeAsync(requireExisting: true, cancellationToken).ConfigureAwait(false);
        if (!(await FullDoctorAsync(cancellationToken).ConfigureAwait(false)).Healthy)
            throw new InvalidDataException("The payload upgrade requires a verified source store.");
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            // A private migration connection only. SQLite's table rebuild needs
            // FK enforcement disabled outside its transaction; foreign_key_check
            // and exact schema admission must pass BEFORE committing the rebuild.
            await ExecuteTieringSqlAsync(connection, null, "PRAGMA foreign_keys=OFF;", cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var (version, sourceGeneration) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (version == TieredSchemaVersion)
                return new(1, "already_upgraded", version, 0, 0, 0, 0, 0, true);
            if (version != expectedSchemaVersion)
                throw new InvalidOperationException("The source schema changed before payload upgrade.");
            var sourceCreatedAt = await ReadScalarStringAsync(connection, transaction,
                "SELECT created_at_utc FROM cortex_v2_meta WHERE singleton=1;", cancellationToken).ConfigureAwait(false);
            await ExecuteTieringSqlAsync(connection, transaction, TieredBlobDdl, cancellationToken).ConfigureAwait(false);
            await ExecuteTieringSqlAsync(connection, transaction, """
                INSERT INTO cortex_v2_content_blob_next
                    (content_hash,compression,original_length,compressed_length,compressed_content,created_at_utc,storage_tier)
                SELECT content_hash,compression,original_length,compressed_length,compressed_content,created_at_utc,'hot'
                    FROM cortex_v2_content_blob;
                DROP TABLE cortex_v2_content_blob;
                ALTER TABLE cortex_v2_content_blob_next RENAME TO cortex_v2_content_blob;
                CREATE TABLE cortex_v2_meta_next(
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                    schema_version INTEGER NOT NULL CHECK(schema_version=3),
                    generation TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                ) STRICT;
                INSERT INTO cortex_v2_meta_next(singleton,schema_version,generation,created_at_utc)
                SELECT singleton,3,generation,created_at_utc FROM cortex_v2_meta WHERE singleton=1 AND schema_version=2;
                DROP TABLE cortex_v2_meta;
                ALTER TABLE cortex_v2_meta_next RENAME TO cortex_v2_meta;
                """, cancellationToken).ConfigureAwait(false);
            // The old table has CHECK2, so updating its version in place is not
            // a migration. Its replacement has CHECK3 and preserves the exact
            // singleton/generation/creation identity in the same rollback unit.
            await VerifyGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var (actualVersion, actualGeneration) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var actualCreatedAt = await ReadScalarStringAsync(connection, transaction,
                "SELECT created_at_utc FROM cortex_v2_meta WHERE singleton=1;", cancellationToken).ConfigureAwait(false);
            if (actualVersion != TieredSchemaVersion || actualGeneration != sourceGeneration || actualCreatedAt != sourceCreatedAt)
                throw new InvalidDataException("The payload upgrade did not preserve its source store identity.");
            // Index failure also rolls back the already-observed, uncommitted
            // metadata replacement. Observed schema is published only after commit.
            await ExecuteTieringSqlAsync(connection, transaction, """
                CREATE INDEX ix_cortex_v3_revision_content ON cortex_v2_record_revision(content_hash);
                CREATE INDEX ix_cortex_v3_head_content ON cortex_v2_record_head(content_hash);
                """, cancellationToken).ConfigureAwait(false);
            await VerifyRequiredObjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNoRowsAsync(connection, transaction, "PRAGMA foreign_key_check;",
                "The payload upgrade failed foreign-key validation.", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _openedSchemaVersion, TieredSchemaVersion);
            return new(1, "upgraded", TieredSchemaVersion, 0, 0, 0, 0, 0, true);
        }
        finally { _writer.Release(); }
    }

    public ValueTask<SqliteCortexTieringReceipt> OffloadArchivedPayloadsAsync(SqliteCortexTieringRequest request,
        CancellationToken cancellationToken = default) => TierPayloadsAsync(request, false, _options.EnablePayloadOffload, cancellationToken);

    public ValueTask<SqliteCortexTieringReceipt> RehydratePayloadsAsync(SqliteCortexTieringRequest request,
        CancellationToken cancellationToken = default) => TierPayloadsAsync(request, true, _options.EnablePayloadOffload, cancellationToken);

    // This entry is trusted product machinery: its caller owns real ingress /
    // worker quiescence and the maintenance lifetime. It does not infer either
    // from paths, process absence, a user-supplied bool or a second lock.
    public ValueTask<SqliteCortexTieringReceipt> ExecuteTrustedPayloadMaintenanceAsync(
        SqliteCortexPayloadMaintenanceAction action, int expectedSchemaVersion,
        SqliteCortexTieringRequest? selection, CancellationToken cancellationToken = default) => action switch
    {
        SqliteCortexPayloadMaintenanceAction.Upgrade when expectedSchemaVersion == SchemaVersion && selection is null =>
            UpgradePayloadStorageCoreAsync(expectedSchemaVersion, true, cancellationToken),
        SqliteCortexPayloadMaintenanceAction.Offload when expectedSchemaVersion == TieredSchemaVersion && selection is not null =>
            TierPayloadsAsync(selection, false, true, cancellationToken),
        SqliteCortexPayloadMaintenanceAction.Rehydrate when expectedSchemaVersion == TieredSchemaVersion && selection is not null =>
            TierPayloadsAsync(selection, true, true, cancellationToken),
        _ => throw new ArgumentException("The fixed payload maintenance action or schema precondition is invalid.")
    };

    public async ValueTask<int> RevalidatePayloadSchemaAdmissionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("Payload schema revalidation requires an initialized existing store.");
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: true);
                await VerifyGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await VerifyRequiredObjectsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await RequireWalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                var (version, _) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _openedSchemaVersion, version);
                return version;
            }
            finally { _writer.Release(); }
        }
        finally { _initialization.Release(); }
    }

    private async ValueTask<SqliteCortexTieringReceipt> TierPayloadsAsync(SqliteCortexTieringRequest request,
        bool rehydrate, bool admitted, CancellationToken cancellationToken)
    {
        var records = FreezeTieringRequest(request);
        var budget = request.RotationGuard?.Budget;
        if (!admitted)
            throw new InvalidOperationException("Payload tier mutations require explicit opt-in.");
        await InitializeAsync(requireExisting: true, cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        var leases = new List<IAsyncDisposable>();
        var cleanup = new List<Exception>();
        Exception? primary = null;
        SqliteCortexTieringReceipt? committed = null;
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var (version, _) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (version != TieredSchemaVersion)
                throw new InvalidOperationException("Payload offload requires the explicit schema-3 upgrade.");
            if (!rehydrate && request.RotationGuard is { } rotation)
                await RequireRotationSelectionAsync(connection, transaction, records, rotation, cancellationToken).ConfigureAwait(false);
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            var revisions = 0;
            foreach (var record in records)
            {
                budget?.Charge();
                await RequireTieringSelectionAsync(connection, transaction, record, cancellationToken,
                    requireArchived: !rehydrate).ConfigureAwait(false);
                // Rehydration is also how a caller prepares already-cold data
                // for a new hot pin. Only eviction is fenced by retained holds.
                if (!rehydrate)
                {
                    budget?.Charge(2);
                    await RequirePayloadUnprotectedAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
                }
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT content_hash FROM cortex_v2_record_revision WHERE namespace_name=$ns AND kind=$kind AND record_id=$id ORDER BY revision LIMIT 257;";
                AddCoordinate(command, record);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    budget?.Charge();
                    if (++revisions > TieringMaximumRevisions)
                        throw new InvalidOperationException("The explicit tiering selection exceeds its revision bound.");
                    hashes.Add(reader.GetString(0));
                }
            }
            var changed = 0; var shared = 0; long evicted = 0, restored = 0, charged = 0;
            foreach (var hash in hashes.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = await ReadPayloadValueAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false);
                charged = checked(charged + payload.Content.Length + payload.Compressed.Length);
                if (charged > TieringMaximumBytes)
                    throw new InvalidOperationException("The explicit tiering selection exceeds its aggregate payload bound.");
                if (rehydrate)
                {
                    if (await PromotePayloadAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false))
                    { changed++; restored += payload.Compressed.Length; }
                    continue;
                }
                if (payload.Cold) continue; // Repeated explicit selection is verification, not another eviction.
                if (await HasOutsideReferenceAsync(connection, transaction, hash, records, cancellationToken).ConfigureAwait(false))
                { shared++; continue; }
                budget?.Published(); // A failed provider may still have published an immutable object.
                var lease = await _coldPayloads.PublishVerifiedAsync(payload.Descriptor, payload.Compressed, cancellationToken)
                    .ConfigureAwait(false);
                if (lease is null) throw new InvalidDataException("Cold publication did not retain its verified object.");
                leases.Add(lease);
                // Do not trust a provider's publication claim: read its exact object
                // through the same verification rules before removing hot bytes.
                var verified = await _coldPayloads.ReadAsync(payload.Descriptor, cancellationToken).ConfigureAwait(false);
                if (verified is null || verified.Length != payload.Compressed.Length || Sha256(verified) != payload.Descriptor.CompressedSha256 ||
                    Sha256(Decompress(verified, payload.Content.Length, _options.MaximumContentBytes)) != hash)
                    throw new InvalidDataException("Cold publication failed immutable readback.");
                foreach (var record in records)
                {
                    budget?.Charge(3); // Exact semantic head plus both protection policies.
                    await RequireTieringSelectionAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
                    await RequirePayloadUnprotectedAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
                }
                if (await HasOutsideReferenceAsync(connection, transaction, hash, records, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Payload references changed during publication.");
                await using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE cortex_v2_content_blob SET compressed_content=NULL,storage_tier='cold',cold_format_version=1,cold_sha256=$sha WHERE content_hash=$hash AND storage_tier='hot' AND compressed_length=$length;";
                update.Parameters.AddWithValue("$sha", payload.Descriptor.CompressedSha256);
                update.Parameters.AddWithValue("$hash", hash); update.Parameters.AddWithValue("$length", payload.Compressed.Length);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("Payload eviction lost its exact hot row.");
                changed++; evicted += payload.Compressed.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            // No cancellation check after a confirmed commit may relabel it as no-op.
            committed = new(1, rehydrate ? "rehydrated" : "offloaded", version, records.Length, changed, shared,
                evicted, restored, true);
        }
        catch (Exception error) when (committed is not null && error is not OutOfMemoryException and not StackOverflowException)
        {
            // Async transaction/connection disposal is also cleanup. Once commit
            // was acknowledged, retain that result even if a later close fails.
            cleanup.Add(error);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            {
                // Published objects are intentionally retained on rollback. No orphan
                // sweeping, source deletion, VACUUM or physical reclaim is implicit.
                foreach (var lease in leases)
                    try { await lease.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                    { cleanup.Add(error); }
            }
            finally { _writer.Release(); }
            if (cleanup.Count != 0 && committed is null)
            {
                if (primary is not null) cleanup.Insert(0, primary);
                throw new AggregateException("Tiering cleanup failed; inspect the retained payload tier before another mutation.",
                    cleanup);
            }
        }
        return (committed ?? throw new InvalidDataException("The payload commit result is unavailable.")) with
            { CleanupIncomplete = cleanup.Count != 0 };
    }

    private static SqliteCortexTieringCoordinate[] FreezeTieringRequest(SqliteCortexTieringRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != 1 || request.Records is null || request.Records.Count is < 1 or > TieringMaximumRecords)
            throw new ArgumentException("The tiering request version or selection count is invalid.");
        var records = request.Records.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record is null) throw new ArgumentException("The tiering coordinate is missing.");
            ValidateIdentity(record.Namespace, record.Kind, record.RecordId);
            if (record.Kind is not ("task" or "knowledge") || record.ExpectedHeadRevision < 1 ||
                !SqliteCortexV2ColdPayloadFiles.IsHash(record.ExpectedHeadHash) ||
                !seen.Add(record.Namespace + "\n" + record.Kind + "\n" + record.RecordId))
                throw new ArgumentException("The tiering coordinate or expected head is invalid.");
        }
        return records;
    }

    private static async ValueTask RequireTieringSelectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        SqliteCortexTieringCoordinate record, CancellationToken cancellationToken, bool requireArchived = true)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT h.revision,h.revision_hash,
              CASE WHEN length(CAST(r.attributes_json AS BLOB))<=65536 THEN r.attributes_json END,r.is_searchable,
              r.previous_revision_hash,r.content_hash,
              CASE WHEN length(r.title)<=512 THEN r.title END,
              CASE WHEN length(CAST(r.tags_json AS BLOB))<=32768 THEN r.tags_json END,
              CASE WHEN length(r.summary)<=4096 THEN r.summary END,
              CASE WHEN length(r.event_type)<=128 THEN r.event_type END,
              CASE WHEN length(r.actor)<=128 THEN r.actor END,
              CASE WHEN length(r.idempotency_key)<=128 THEN r.idempotency_key END,
              CASE WHEN length(r.occurred_at_utc)<=40 THEN r.occurred_at_utc END,r.revision_hash,h.content_hash,
              EXISTS(SELECT 1 FROM cortex_v2_record_revision newer WHERE newer.namespace_name=h.namespace_name
                AND newer.kind=h.kind AND newer.record_id=h.record_id AND newer.revision>h.revision)
            FROM cortex_v2_record_head h JOIN cortex_v2_record_revision r
              ON r.namespace_name=h.namespace_name AND r.kind=h.kind AND r.record_id=h.record_id AND r.revision=h.revision
            WHERE h.namespace_name=$ns AND h.kind=$kind AND h.record_id=$id
              AND ($archivedOnly=0 OR NOT EXISTS(SELECT 1 FROM cortex_v2_search_head s WHERE
                  s.namespace_name=h.namespace_name AND s.kind=h.kind AND s.record_id=h.record_id));
            """;
        AddCoordinate(command, record);
        command.Parameters.AddWithValue("$archivedOnly", requireArchived ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) != record.ExpectedHeadRevision ||
            reader.GetString(1) != record.ExpectedHeadHash || reader.GetString(13) != record.ExpectedHeadHash ||
            reader.GetString(14) != reader.GetString(5) || reader.GetInt64(15) != 0 ||
            reader.IsDBNull(2) || Enumerable.Range(6, 7).Any(reader.IsDBNull) ||
            requireArchived && (reader.GetInt64(3) != 0 ||
                !SqliteCortexV2OperationalStore.IsArchivedForPayloadTiering(record.Kind, reader.GetString(2))))
            throw new InvalidOperationException("The exact tiering precondition is unavailable or changed.");
        if (!requireArchived)
            _ = SqliteCortexV2OperationalStore.ReadPayloadLifecycle(record.Kind, reader.GetString(2));
        var tags = reader.GetString(7);
        var candidate = new SqliteCortexV2AppendRequest(record.Namespace, record.Kind, record.RecordId,
            record.ExpectedHeadRevision - 1, reader.GetString(11), reader.GetString(10), reader.GetString(9),
            reader.GetString(6), DeserializeTags(tags), reader.GetString(8), null, reader.GetInt64(3) != 0, reader.GetString(2));
        if (RevisionHash(candidate, record.ExpectedHeadRevision, reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), tags, ParseUtc(reader.GetString(12))) != record.ExpectedHeadHash)
            throw new InvalidDataException("The archived head failed its immutable metadata hash.");
    }

    private static async ValueTask<bool> HasOutsideReferenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        string hash, SqliteCortexTieringCoordinate[] records, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        var included = new List<string>(records.Length);
        for (var index = 0; index < records.Length; index++)
        {
            included.Add($"(namespace_name=$n{index} AND kind=$k{index} AND record_id=$i{index})");
            command.Parameters.AddWithValue("$n" + index, records[index].Namespace);
            command.Parameters.AddWithValue("$k" + index, records[index].Kind);
            command.Parameters.AddWithValue("$i" + index, records[index].RecordId);
        }
        command.CommandText = "SELECT 1 FROM cortex_v2_record_revision INDEXED BY ix_cortex_v3_revision_content WHERE content_hash=$hash AND NOT (" + string.Join(" OR ", included) + ") LIMIT 1;";
        command.Parameters.AddWithValue("$hash", hash);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static void AddCoordinate(SqliteCommand command, SqliteCortexTieringCoordinate record)
    {
        command.Parameters.AddWithValue("$ns", record.Namespace);
        command.Parameters.AddWithValue("$kind", record.Kind);
        command.Parameters.AddWithValue("$id", record.RecordId);
    }

    private static async ValueTask ExecuteTieringSqlAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string TieredBlobDdl = """
        CREATE TABLE cortex_v2_content_blob_next(
            content_hash TEXT PRIMARY KEY CHECK(length(content_hash)=64),
            compression TEXT NOT NULL CHECK(compression='br'),
            original_length INTEGER NOT NULL CHECK(original_length>=0),
            compressed_length INTEGER NOT NULL CHECK(compressed_length>0),
            compressed_content BLOB NULL,
            created_at_utc TEXT NOT NULL,
            storage_tier TEXT NOT NULL CHECK(storage_tier IN ('hot','cold')),
            cold_format_version INTEGER NULL,
            cold_sha256 TEXT NULL,
            CHECK((storage_tier='hot' AND compressed_content IS NOT NULL AND
                   length(compressed_content)=compressed_length AND cold_format_version IS NULL AND cold_sha256 IS NULL)
               OR (storage_tier='cold' AND compressed_content IS NULL AND cold_format_version IS NOT NULL AND
                   cold_format_version=1 AND cold_sha256 IS NOT NULL AND length(cold_sha256)=64 AND
                   cold_sha256 NOT GLOB '*[^0-9a-f]*'))
        ) STRICT;
        """;
}
