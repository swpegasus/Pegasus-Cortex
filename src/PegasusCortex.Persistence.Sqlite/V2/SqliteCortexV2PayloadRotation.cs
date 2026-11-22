using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2Store
{
    private const int RotationScanPage = 256;

    public ValueTask<SqliteCortexPayloadRotationPlan> PlanPayloadRotationAsync(
        SqliteCortexPayloadRotationRequest request, CancellationToken cancellationToken = default) =>
        PlanPayloadRotationCoreAsync(request, _options.EnablePayloadOffload, cancellationToken);

    private async ValueTask<SqliteCortexPayloadRotationPlan> PlanPayloadRotationCoreAsync(
        SqliteCortexPayloadRotationRequest request, bool admitted, CancellationToken cancellationToken)
    {
        var value = ValidateRotationRequest(request);
        if (!admitted) throw new InvalidOperationException("Payload rotation requires explicit opt-in.");
        await InitializeAsync(requireExisting: true, cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if ((await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false)).Version != TieredSchemaVersion)
            throw new InvalidOperationException("Payload rotation requires the explicit schema-3 upgrade.");
        var hot = await ReadNamespaceHotBytesAsync(connection, transaction, value.Namespace, cancellationToken, value.Budget).ConfigureAwait(false);
        if (value.TargetHotCompressedBytes > 0 && hot <= value.TargetHotCompressedBytes)
            return new(1, value.Namespace, 0, 0, 0, 0, hot, null, null);
        var selected = new List<SqliteCortexTieringCoordinate>(TieringMaximumRecords);
        var scanned = 0; var protectedCount = 0; var overBound = 0; var more = false;
        long charged = 0; var revisions = 0;
        SqliteCortexPayloadRotationCursor? cursor = null;
        await foreach (var head in ReadPayloadLifecycleHeadsAsync(connection, transaction, value.Namespace, cancellationToken, value.After, value.Budget))
        {
            if (scanned == RotationScanPage || selected.Count == TieringMaximumRecords) { more = true; break; }
            scanned++; cursor = new(head.Coordinate.Kind, head.Coordinate.RecordId);
            if (!Eligible(head, value.ArchivedBeforeUtc)) continue;
            value.Budget?.Charge(2); // Fixed namespace and record policy probes.
            if (await IsPayloadProtectedAsync(connection, transaction, head.Coordinate, cancellationToken).ConfigureAwait(false))
            { protectedCount++; continue; }
            // Charge every revision/blob as the existing executor does. This may
            // overestimate shared hashes, which cannot cause an oversized batch.
            var size = await ReadRotationRecordChargeAsync(connection, transaction, head.Coordinate, cancellationToken, value.Budget).ConfigureAwait(false);
            if (size.HotBytes == 0) continue;
            if (size.Revisions > TieringMaximumRevisions || size.Bytes > TieringMaximumBytes ||
                revisions + size.Revisions > TieringMaximumRevisions || charged + size.Bytes > TieringMaximumBytes)
            { overBound++; continue; }
            selected.Add(head.Coordinate); revisions += size.Revisions; charged += size.Bytes;
        }
        var blocked = await FindRotationReferenceBlockersAsync(connection, transaction, value.Namespace, selected,
            cancellationToken, value.Budget).ConfigureAwait(false);
        selected.RemoveAll(record => blocked.Contains(PayloadCoordinateKey(record.Kind, record.RecordId)));
        var selection = selected.Count == 0 ? null : new SqliteCortexTieringRequest(1, Array.AsReadOnly(selected.ToArray()))
            { RotationGuard = value with { After = null } };
        return new(1, value.Namespace, scanned, protectedCount, blocked.Count, overBound, hot, selection, more ? cursor : null);
    }

    public ValueTask<SqliteCortexPayloadRotationReceipt> RotateArchivedPayloadsAsync(
        SqliteCortexPayloadRotationRequest request, CancellationToken cancellationToken = default) =>
        RotatePayloadCoreAsync(request, _options.EnablePayloadOffload, cancellationToken);

    internal async ValueTask<SqliteCortexPayloadRotationReceipt> ExecuteTrustedPayloadRotationAsync(
        SqliteCortexPayloadRotationRequest request, int maximumMetadataHeads, int workMilliseconds,
        CancellationToken cancellationToken)
    {
        if (maximumMetadataHeads is < 256 or > 65536 || workMilliseconds is < 50 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(maximumMetadataHeads));
        _ = ValidateRotationRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        work.CancelAfter(workMilliseconds);
        var budget = new SqliteCortexPayloadMetadataBudget(maximumMetadataHeads, work.Token);
        try
        {
            var result = await RotatePayloadCoreAsync(request with { Budget = budget }, true, work.Token).ConfigureAwait(false);
            return result with { MetadataObservations = budget.Observed };
        }
        catch (Exception error) when (error is SqliteCortexPayloadBudgetExceededException ||
            error is OperationCanceledException && work.IsCancellationRequested)
        {
            // Original cancellation can arrive while the already-failed budget
            // operation is joining rollback/lease cleanup. It still takes
            // precedence over a later automatic-deferral classification.
            cancellationToken.ThrowIfCancellationRequested();
            // The original invocation has returned through its transaction/
            // rollback/lease cleanup. No automatic resubmission is performed.
            return new(1, "deferred_budget", new(1, request.Namespace, null, null, null, null, null, null, request.After),
                null, null, null, "payload_work_budget_exhausted")
                { MetadataObservations = budget.Observed, ImmutableObjectsMayRemain = budget.PublishedObjects != 0 };
        }
    }

    private async ValueTask<SqliteCortexPayloadRotationReceipt> RotatePayloadCoreAsync(
        SqliteCortexPayloadRotationRequest request, bool admitted, CancellationToken cancellationToken)
    {
        var plan = await PlanPayloadRotationCoreAsync(request, admitted, cancellationToken).ConfigureAwait(false);
        SqliteCortexTieringReceipt? tiering = null;
        if (plan.Selection is { } selection)
            tiering = await TierPayloadsAsync(selection, false, admitted, cancellationToken).ConfigureAwait(false);
        // This is an observation after the committed batch, not a disk-shrink or
        // future admission guarantee; concurrent new writes can increase it.
        try
        {
            var observationToken = tiering is null ? cancellationToken : CancellationToken.None;
            await using var connection = CreateConnection();
            await connection.OpenAsync(observationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, observationToken).ConfigureAwait(false);
            var after = await ReadNamespaceHotBytesAsync(connection, null, plan.Namespace, observationToken, request.Budget).ConfigureAwait(false);
            return new(1, RotationState(tiering), plan, tiering, after,
                after <= request.TargetHotCompressedBytes);
        }
        catch (Exception error) when (tiering is not null && error is not OutOfMemoryException and not StackOverflowException)
        {
            // Secondary metrics failure cannot erase the confirmed commit or
            // make it look like a safe-to-retry cancellation/no-op.
            return new(1, RotationState(tiering), plan, tiering, null, null, "payload_rotation_metrics_unavailable");
        }
    }

    private SqliteCortexPayloadRotationRequest ValidateRotationRequest(SqliteCortexPayloadRotationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireIdentifier(request.Namespace, nameof(request.Namespace));
        if (request.Version != 1 || request.ArchivedBeforeUtc.Offset != TimeSpan.Zero ||
            request.ArchivedBeforeUtc > _timeProvider.GetUtcNow() ||
            request.TargetHotCompressedBytes is < 0 or > 16L * 1024 * 1024 * 1024 * 1024)
            throw new ArgumentException("The payload rotation version, age or soft byte target is invalid.");
        if (request.After is { } after)
        {
            if (after.Kind is not ("task" or "knowledge")) throw new ArgumentException("The rotation cursor kind is invalid.");
            RequireIdentifier(after.RecordId, nameof(request.After));
        }
        return request;
    }

    private static bool Eligible(PayloadLifecycleHead head, DateTimeOffset cutoff) =>
        !head.Searchable && head.Lifecycle.Archived && head.Lifecycle.ArchivedAtUtc <= cutoff &&
        head.Lifecycle.UpdatedAtUtc <= cutoff && head.OccurredAtUtc <= cutoff;

    private async ValueTask RequireRotationSelectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        SqliteCortexTieringCoordinate[] records, SqliteCortexPayloadRotationRequest request, CancellationToken token)
    {
        var value = ValidateRotationRequest(request);
        if (records.Any(record => record.Namespace != value.Namespace)) throw new InvalidOperationException("payload_rotation_scope_changed");
        var expected = records.ToDictionary(record => PayloadCoordinateKey(record.Kind, record.RecordId), StringComparer.Ordinal);
        var found = 0;
        await foreach (var head in ReadPayloadLifecycleHeadsAsync(connection, transaction, value.Namespace, token, budget: value.Budget))
            if (expected.TryGetValue(PayloadCoordinateKey(head.Coordinate.Kind, head.Coordinate.RecordId), out var record))
            {
                if (head.Coordinate != record || !Eligible(head, value.ArchivedBeforeUtc))
                    throw new InvalidOperationException("payload_rotation_selection_changed");
                found++;
            }
        if (found != records.Length || (await FindRotationReferenceBlockersAsync(connection, transaction,
            value.Namespace, records, token, value.Budget).ConfigureAwait(false)).Count != 0)
            throw new InvalidOperationException("payload_rotation_references_changed");
    }

    private async ValueTask<HashSet<string>> FindRotationReferenceBlockersAsync(SqliteConnection connection,
        SqliteTransaction transaction, string ns, IReadOnlyList<SqliteCortexTieringCoordinate> records, CancellationToken token,
        SqliteCortexPayloadMetadataBudget? budget = null)
    {
        var selected = records.Select(record => PayloadCoordinateKey(record.Kind, record.RecordId)).ToHashSet(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        if (selected.Count == 0) return blocked;
        // At most 16 selected heads and their bounded typed references are kept
        // in memory. The corpus is streamed; no body or global graph is loaded.
        var unresolved = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var selectedReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await foreach (var head in ReadPayloadLifecycleHeadsAsync(connection, transaction, ns, token, budget: budget))
        {
            var source = PayloadCoordinateKey(head.Coordinate.Kind, head.Coordinate.RecordId);
            if (!selected.Contains(source)) continue;
            foreach (var id in head.Lifecycle.ReferencedIds)
            {
                var target = PayloadCoordinateKey(head.Coordinate.Kind, id);
                if (!unresolved.TryGetValue(target, out var owners)) unresolved.Add(target, owners = []);
                owners.Add(source);
                if (selected.Contains(target))
                {
                    if (!selectedReferences.TryGetValue(source, out var targets)) selectedReferences.Add(source, targets = new(StringComparer.Ordinal));
                    targets.Add(target);
                }
            }
        }
        await foreach (var head in ReadPayloadLifecycleHeadsAsync(connection, transaction, ns, token, budget: budget))
        {
            var source = PayloadCoordinateKey(head.Coordinate.Kind, head.Coordinate.RecordId);
            unresolved.Remove(source);
            if (selected.Contains(source)) continue;
            foreach (var id in head.Lifecycle.ReferencedIds)
            {
                var target = PayloadCoordinateKey(head.Coordinate.Kind, id);
                if (selected.Contains(target)) blocked.Add(target);
            }
        }
        foreach (var owners in unresolved.Values) foreach (var source in owners) blocked.Add(source);
        // A blocked candidate becomes an outside reference owner. Propagate that
        // fact through the small selected graph before publishing a usable plan.
        bool changed;
        do
        {
            changed = false;
            foreach (var source in blocked.ToArray())
                if (selectedReferences.TryGetValue(source, out var targets))
                    foreach (var target in targets) changed |= blocked.Add(target);
        } while (changed);
        return blocked;
    }

    private async IAsyncEnumerable<PayloadLifecycleHead> ReadPayloadLifecycleHeadsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string ns, [EnumeratorCancellation] CancellationToken token,
        SqliteCortexPayloadRotationCursor? after = null, SqliteCortexPayloadMetadataBudget? budget = null)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT h.kind,h.record_id,h.revision,h.revision_hash,r.revision_hash,r.previous_revision_hash,r.content_hash,
              CASE WHEN length(r.title)<=512 THEN r.title END,
              CASE WHEN length(CAST(r.tags_json AS BLOB))<=32768 THEN r.tags_json END,
              CASE WHEN length(r.summary)<=4096 THEN r.summary END,
              CASE WHEN length(CAST(r.attributes_json AS BLOB))<=65536 THEN r.attributes_json END,
              CASE WHEN length(r.event_type)<=128 THEN r.event_type END,
              CASE WHEN length(r.actor)<=128 THEN r.actor END,
              CASE WHEN length(r.idempotency_key)<=128 THEN r.idempotency_key END,
              CASE WHEN length(r.occurred_at_utc)<=40 THEN r.occurred_at_utc END,r.is_searchable,h.content_hash,
              EXISTS(SELECT 1 FROM cortex_v2_record_revision newer WHERE newer.namespace_name=h.namespace_name
                AND newer.kind=h.kind AND newer.record_id=h.record_id AND newer.revision>h.revision),
              EXISTS(SELECT 1 FROM cortex_v2_search_head s WHERE s.namespace_name=h.namespace_name
                AND s.kind=h.kind AND s.record_id=h.record_id)
            FROM cortex_v2_record_head h LEFT JOIN cortex_v2_record_revision r ON r.namespace_name=h.namespace_name
              AND r.kind=h.kind AND r.record_id=h.record_id AND r.revision=h.revision
            WHERE h.namespace_name=$ns AND h.kind IN ('task','knowledge')
              AND ($afterKind IS NULL OR h.kind>$afterKind OR (h.kind=$afterKind AND h.record_id>$afterId))
            ORDER BY h.kind,h.record_id;
            """;
        command.Parameters.AddWithValue("$ns", ns);
        command.Parameters.AddWithValue("$afterKind", (object?)after?.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterId", (object?)after?.RecordId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            budget?.Charge();
            if (Enumerable.Range(7, 8).Any(reader.IsDBNull) || reader.GetString(3) != reader.GetString(4) ||
                reader.GetString(16) != reader.GetString(6) || reader.GetInt64(17) != 0 || reader.GetInt64(2) < 1)
                throw new InvalidDataException("payload_reference_metadata_unavailable");
            var kind = reader.GetString(0); var id = reader.GetString(1); var revision = reader.GetInt64(2);
            ValidateIdentity(ns, kind, id);
            var occurred = ParseUtc(reader.GetString(14)); var attributes = reader.GetString(10);
            var candidate = new SqliteCortexV2AppendRequest(ns, kind, id, revision - 1, reader.GetString(13),
                reader.GetString(12), reader.GetString(11), reader.GetString(7), DeserializeTags(reader.GetString(8)),
                reader.GetString(9), null, reader.GetInt64(15) == 1, attributes);
            if (RevisionHash(candidate, revision, reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
                reader.GetString(8), occurred) != reader.GetString(3)) throw new InvalidDataException("payload_reference_hash_invalid");
            yield return new(new(ns, kind, id, revision, reader.GetString(3)),
                SqliteCortexV2OperationalStore.ReadPayloadLifecycle(kind, attributes), occurred,
                reader.GetInt64(15) != 0 || reader.GetInt64(18) != 0);
        }
    }

    private static async ValueTask<(int Revisions, long Bytes, long HotBytes)> ReadRotationRecordChargeAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteCortexTieringCoordinate record, CancellationToken token,
        SqliteCortexPayloadMetadataBudget? budget = null)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*),coalesce(sum(b.original_length+b.compressed_length),0),
              coalesce(sum(CASE WHEN b.storage_tier='hot' THEN b.compressed_length ELSE 0 END),0),
              coalesce(sum(CASE WHEN b.content_hash IS NULL THEN 1 ELSE 0 END),0)
            FROM (SELECT content_hash FROM cortex_v2_record_revision WHERE namespace_name=$ns AND kind=$kind
              AND record_id=$id ORDER BY revision LIMIT 257) r LEFT JOIN cortex_v2_content_blob b ON b.content_hash=r.content_hash;
            """;
        AddCoordinate(command, record);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt64(3) != 0)
            throw new InvalidDataException("payload_rotation_charge_unavailable");
        budget?.Charge(reader.GetInt32(0));
        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async ValueTask<long> ReadNamespaceHotBytesAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string ns, CancellationToken token, SqliteCortexPayloadMetadataBudget? budget = null)
    {
        // An empty result must still respect the cooperative deadline. After a
        // confirmed commit this becomes unavailable telemetry, never a deferral.
        budget?.Charge(0);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT coalesce(sum(b.compressed_length),0) FROM cortex_v2_content_blob b WHERE b.storage_tier='hot'
              AND EXISTS(SELECT 1 FROM cortex_v2_record_revision r WHERE r.namespace_name=$ns
                AND r.kind IN ('task','knowledge') AND r.content_hash=b.content_hash);
            """;
        command.Parameters.AddWithValue("$ns", ns);
        if (budget is not null)
        {
            command.CommandText = """
                SELECT b.compressed_length FROM cortex_v2_content_blob b WHERE b.storage_tier='hot'
                  AND EXISTS(SELECT 1 FROM cortex_v2_record_revision r WHERE r.namespace_name=$ns
                    AND r.kind IN ('task','knowledge') AND r.content_hash=b.content_hash);
                """;
            long total = 0;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            { budget.Charge(); total = checked(total + reader.GetInt64(0)); }
            return total;
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string PayloadCoordinateKey(string kind, string id) => kind + "\n" + id;
    private static string RotationState(SqliteCortexTieringReceipt? tiering) => tiering is null ? "no_selection" :
        tiering.CleanupIncomplete ? "rotated_cleanup_incomplete" : "rotated";
    private sealed record PayloadLifecycleHead(SqliteCortexTieringCoordinate Coordinate,
        SqliteCortexPayloadLifecycle Lifecycle, DateTimeOffset OccurredAtUtc, bool Searchable);
}
