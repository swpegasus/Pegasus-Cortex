using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2Store
{
    // The normal store-schema admission is separate from this page's work. After
    // admission: <=50 history rows + one cursor + two head proofs + generation
    // and <=2 capacity rows + <=4 helper metadata rows per distinct cold hash
    // (PromotePayloadAsync) fit within 256. No lookahead row is read.
    private const int HistoryRehydrationPageRows = 50;

    public ValueTask<SqliteCortexPayloadHistoryRehydrationReceipt> RehydratePayloadHistoryAsync(
        SqliteCortexPayloadHistoryRehydrationRequest request, CancellationToken cancellationToken = default) =>
        RehydratePayloadHistoryCoreAsync(request, _options.EnablePayloadOffload, cancellationToken);

    // Only the fixed authenticated owner-maintenance controller calls this seam;
    // paths, callbacks and a caller-provided admission bool never enter the wire.
    internal ValueTask<SqliteCortexPayloadHistoryRehydrationReceipt> ExecuteTrustedPayloadHistoryRehydrationAsync(
        SqliteCortexPayloadHistoryRehydrationRequest request, CancellationToken cancellationToken = default) =>
        RehydratePayloadHistoryCoreAsync(request, true, cancellationToken);

    private async ValueTask<SqliteCortexPayloadHistoryRehydrationReceipt> RehydratePayloadHistoryCoreAsync(
        SqliteCortexPayloadHistoryRehydrationRequest request, bool admitted, CancellationToken token)
    {
        var frozen = FreezeHistoryRehydrationRequest(request);
        if (!admitted) throw new InvalidOperationException("Payload history rehydration requires explicit opt-in.");
        await InitializeAsync(requireExisting: true, token).ConfigureAwait(false);
        await _writer.WaitAsync(token).ConfigureAwait(false);
        SqliteCortexPayloadHistoryRehydrationReceipt? committed = null;
        var cleanupIncomplete = false;
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, token).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            if ((await ReadGenerationAsync(connection, transaction, token).ConfigureAwait(false)).Version != TieredSchemaVersion)
                throw new InvalidOperationException("Payload history rehydration requires the explicit schema-3 upgrade.");
            await RequireTieringSelectionAsync(connection, transaction, frozen.Record, token, requireArchived: false)
                .ConfigureAwait(false);
            var inspected = 1;
            var afterRevision = frozen.After?.Revision ?? 0;
            if (frozen.After is { } after)
            {
                var proof = await ReadHistoryRehydrationMetadataAsync(connection, transaction, frozen.Record,
                    after.Revision, cursorOnly: true, token).ConfigureAwait(false);
                inspected += proof.Count;
                if (proof.Count != 1 || proof[0].Revision != after.Revision || proof[0].RevisionHash != after.RevisionHash)
                    throw new InvalidOperationException("payload_history_cursor_changed");
            }
            List<HistoryRehydrationMetadata> page = afterRevision == frozen.Record.ExpectedHeadRevision ? [] :
                await ReadHistoryRehydrationMetadataAsync(connection, transaction, frozen.Record,
                    afterRevision, cursorOnly: false, token).ConfigureAwait(false);
            inspected += page.Count;
            if (afterRevision < frozen.Record.ExpectedHeadRevision && page.Count == 0)
                throw new InvalidDataException("payload_history_metadata_unavailable");
            var priorRevision = afterRevision;
            var priorHash = frozen.After?.RevisionHash;
            foreach (var row in page)
            {
                if (priorRevision == long.MaxValue || row.Revision != priorRevision + 1 || row.PreviousRevisionHash != priorHash)
                    throw new InvalidDataException("payload_history_chain_unavailable");
                priorRevision = row.Revision; priorHash = row.RevisionHash;
            }
            var through = frozen.After;
            var completed = 0; var changed = 0; long charged = 0, restored = 0;
            var handled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in page)
            {
                token.ThrowIfCancellationRequested();
                if (row.Cold && !handled.Contains(row.ContentHash))
                {
                    var cost = checked((long)row.OriginalLength + row.CompressedLength);
                    if (cost > TieringMaximumBytes)
                        throw new InvalidOperationException("payload_history_item_over_bound");
                    if (charged + cost > TieringMaximumBytes) break;
                    // The private promotion helper performs exact compressed and
                    // content hash validation once, inside this same transaction.
                    if (!await PromotePayloadAsync(connection, transaction, row.ContentHash, token).ConfigureAwait(false))
                        throw new InvalidDataException("payload_history_residency_changed");
                    charged += cost; restored += row.CompressedLength; changed++;
                    handled.Add(row.ContentHash);
                }
                else handled.Add(row.ContentHash);
                completed++;
                through = new(row.Revision, row.RevisionHash);
            }
            if (through is null)
                throw new InvalidDataException("payload_history_progress_unavailable");
            await RequireTieringSelectionAsync(connection, transaction, frozen.Record, token, requireArchived: false)
                .ConfigureAwait(false);
            inspected++;
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token).ConfigureAwait(false);
            var reached = through.Revision == frozen.Record.ExpectedHeadRevision;
            committed = new(1, changed == 0 ? "page_already_hot" : "page_rehydrated", frozen.Record,
                afterRevision, through, inspected, completed, charged,
                new(1, "rehydrated", TieredSchemaVersion, 1, changed, 0, 0, restored, true),
                reached ? null : through, reached);
        }
        catch (Exception error) when (committed is not null && error is not OutOfMemoryException and not StackOverflowException)
        {
            // Connection/transaction disposal happens after commit. Its failure
            // cannot erase confirmed progress or suggest replay is effect-free.
            cleanupIncomplete = true;
        }
        finally { _writer.Release(); }
        var result = committed ?? throw new InvalidDataException("payload_history_commit_unavailable");
        return result with { Tiering = result.Tiering with { CleanupIncomplete = cleanupIncomplete } };
    }

    private static SqliteCortexPayloadHistoryRehydrationRequest FreezeHistoryRehydrationRequest(
        SqliteCortexPayloadHistoryRehydrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != 1 || request.Record is null)
            throw new ArgumentException("The payload history request is invalid.", nameof(request));
        var record = FreezeTieringRequest(new(1, [request.Record]))[0];
        if (request.After is { } after && (after.Revision < 1 || after.Revision > record.ExpectedHeadRevision ||
            !SqliteCortexV2ColdPayloadFiles.IsHash(after.RevisionHash)))
            throw new ArgumentException("The payload history cursor is invalid.", nameof(request));
        return request with { Record = record with { }, After = request.After is { } cursor ? cursor with { } : null };
    }

    private async ValueTask<List<HistoryRehydrationMetadata>> ReadHistoryRehydrationMetadataAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteCortexTieringCoordinate record,
        long afterRevision, bool cursorOnly, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT r.revision,r.revision_hash,r.previous_revision_hash,r.content_hash,
              CASE WHEN length(r.title)<=512 THEN r.title END,
              CASE WHEN length(CAST(r.tags_json AS BLOB))<=32768 THEN r.tags_json END,
              CASE WHEN length(r.summary)<=4096 THEN r.summary END,
              CASE WHEN length(CAST(r.attributes_json AS BLOB))<=65536 THEN r.attributes_json END,
              CASE WHEN length(r.event_type)<=128 THEN r.event_type END,
              CASE WHEN length(r.actor)<=128 THEN r.actor END,
              CASE WHEN length(r.idempotency_key)<=128 THEN r.idempotency_key END,r.is_searchable,
              CASE WHEN length(r.occurred_at_utc)<=40 THEN r.occurred_at_utc END,
              b.content_hash,b.compression,b.original_length,b.compressed_length,b.storage_tier,
              length(b.compressed_content),b.cold_format_version,b.cold_sha256
            FROM cortex_v2_record_revision r LEFT JOIN cortex_v2_content_blob b ON b.content_hash=r.content_hash
            WHERE r.namespace_name=$ns AND r.kind=$kind AND r.record_id=$id
            """ + (cursorOnly ? " AND r.revision=$after" : " AND r.revision>$after AND r.revision<=$head") +
            " ORDER BY r.revision LIMIT $limit;";
        AddCoordinate(command, record);
        command.Parameters.AddWithValue("$after", afterRevision);
        if (!cursorOnly) command.Parameters.AddWithValue("$head", record.ExpectedHeadRevision);
        command.Parameters.AddWithValue("$limit", cursorOnly ? 1 : HistoryRehydrationPageRows);
        var rows = new List<HistoryRehydrationMetadata>(cursorOnly ? 1 : HistoryRehydrationPageRows);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (Enumerable.Range(0, 2).Any(reader.IsDBNull) || Enumerable.Range(3, 15).Any(reader.IsDBNull))
                throw new InvalidDataException("payload_history_metadata_unavailable");
            try
            {
                var revision = reader.GetInt64(0); var hash = reader.GetString(1);
                var previous = reader.IsDBNull(2) ? null : reader.GetString(2);
                var contentHash = reader.GetString(3);
                if (revision < 1 || !SqliteCortexV2ColdPayloadFiles.IsHash(hash) ||
                    !SqliteCortexV2ColdPayloadFiles.IsHash(contentHash) || reader.GetString(13) != contentHash ||
                    (revision == 1 ? previous is not null : !SqliteCortexV2ColdPayloadFiles.IsHash(previous!)) ||
                    reader.GetInt64(11) is not (0 or 1) || reader.GetString(14) != "br")
                    throw new InvalidDataException("payload_history_metadata_unavailable");
                var candidate = ValidateAppend(new(record.Namespace, record.Kind, record.RecordId, revision - 1,
                    reader.GetString(10), reader.GetString(9), reader.GetString(8), reader.GetString(4),
                    DeserializeTags(reader.GetString(5)), reader.GetString(6), null, reader.GetInt64(11) != 0, reader.GetString(7)));
                if (RevisionHash(candidate, revision, previous, contentHash, reader.GetString(5), ParseUtc(reader.GetString(12))) != hash)
                    throw new InvalidDataException("payload_history_hash_invalid");
                var original = reader.GetInt64(15); var compressed = reader.GetInt64(16);
                var cold = reader.GetString(17) switch { "cold" => true, "hot" => false,
                    _ => throw new InvalidDataException("payload_history_residency_unavailable") };
                if (original is < 0 or > 16 * 1024 * 1024 || compressed is < 1 or > 16 * 1024 * 1024 + 65536 ||
                    (cold ? !reader.IsDBNull(18) || reader.IsDBNull(19) || reader.GetInt64(19) != 1 ||
                        reader.IsDBNull(20) || !SqliteCortexV2ColdPayloadFiles.IsHash(reader.GetString(20)) ||
                        original > _options.MaximumContentBytes || compressed > _options.MaximumContentBytes + 65536
                      : reader.IsDBNull(18) || reader.GetInt64(18) != compressed || !reader.IsDBNull(19) || !reader.IsDBNull(20)))
                    throw new InvalidDataException("payload_history_residency_unavailable");
                rows.Add(new(revision, hash, previous, contentHash, (int)original, (int)compressed, cold));
            }
            catch (Exception error) when (error is ArgumentException or JsonException or FormatException or InvalidCastException)
            { throw new InvalidDataException("payload_history_metadata_unavailable", error); }
        }
        return rows;
    }

    private sealed record HistoryRehydrationMetadata(long Revision, string RevisionHash,
        string? PreviousRevisionHash, string ContentHash, int OriginalLength, int CompressedLength, bool Cold);
}
