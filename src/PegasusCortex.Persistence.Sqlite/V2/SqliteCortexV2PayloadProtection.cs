using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2Store
{
    // Product-owned metadata shares the normal revision/replay/backup machinery;
    // it is never searchable knowledge or a caller-selected generic record kind.
    private const string PayloadProtectionKind = "payload-protection";
    private const string PayloadProtectionEvent = "payload.protection.updated";
    private static readonly JsonSerializerOptions PayloadProtectionJson = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<SqliteCortexV2WriteResult> SetPayloadProtectionAsync(
        SqliteCortexPayloadProtectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = FreezePayloadProtectionScope(request.Scope);
        if (request.Version != 1 || request.ExpectedRevision < 0)
            throw new ArgumentException("The payload protection version or expected revision is invalid.", nameof(request));
        var metadata = new PayloadProtectionMetadata(1, scope.Namespace, scope.Kind, scope.RecordId,
            request.Pinned, request.Hold);
        var append = ValidateAppend(new(scope.Namespace, PayloadProtectionKind, PayloadProtectionId(scope),
            request.ExpectedRevision, request.IdempotencyKey, request.Actor, PayloadProtectionEvent,
            "Payload protection", [], "", [], Searchable: false,
            AttributesJson: JsonSerializer.Serialize(metadata, PayloadProtectionJson)));
        await InitializeAsync(requireExisting: true, cancellationToken).ConfigureAwait(false);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await RequirePayloadProtectionTargetAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
            // A corrupt/unknown existing policy is not an implicit permission to
            // replace it. This also verifies the exact current immutable head.
            _ = await ReadPayloadProtectionInTransactionAsync(connection, transaction, scope, cancellationToken)
                .ConfigureAwait(false);
            var result = await AppendInTransactionAsync(connection, transaction, append, cancellationToken).ConfigureAwait(false);
            if (result.Disposition is SqliteCortexV2WriteDispositions.Conflict or SqliteCortexV2WriteDispositions.StaleRevision)
                return result;
            if (result.Disposition == SqliteCortexV2WriteDispositions.Appended && (request.Pinned || request.Hold))
                await RequireProtectedPayloadsHotAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            // A confirmed commit wins late cancellation, just like ordinary appends.
            return result;
        }
        finally { _writer.Release(); }
    }

    public async ValueTask<SqliteCortexPayloadProtectionState> ReadPayloadProtectionAsync(
        SqliteCortexPayloadProtectionScope scope, CancellationToken cancellationToken = default)
    {
        var frozen = FreezePayloadProtectionScope(scope);
        await InitializeAsync(requireExisting: true, cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await RequirePayloadProtectionTargetAsync(connection, transaction, frozen, cancellationToken).ConfigureAwait(false);
        var result = await ReadPayloadProtectionInTransactionAsync(connection, transaction, frozen, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async ValueTask<bool> IsPayloadProtectedAsync(SqliteConnection connection,
        SqliteTransaction transaction, SqliteCortexTieringCoordinate coordinate, CancellationToken cancellationToken)
    {
        var scope = FreezePayloadProtectionScope(new(coordinate.Namespace, coordinate.Kind, coordinate.RecordId));
        var ns = await ReadPayloadProtectionInTransactionAsync(connection, transaction,
            new(scope.Namespace), cancellationToken).ConfigureAwait(false);
        // Validate BOTH exact policies, even when the first already protects;
        // a malformed record policy must remain observable rather than be hidden.
        var record = await ReadPayloadProtectionInTransactionAsync(connection, transaction, scope, cancellationToken)
            .ConfigureAwait(false);
        return ns.Pinned || ns.Hold || record.Pinned || record.Hold;
    }

    internal async ValueTask RequirePayloadUnprotectedAsync(SqliteConnection connection,
        SqliteTransaction transaction, SqliteCortexTieringCoordinate coordinate, CancellationToken cancellationToken)
    {
        if (await IsPayloadProtectedAsync(connection, transaction, coordinate, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("payload_protected");
    }

    private static SqliteCortexPayloadProtectionScope FreezePayloadProtectionScope(SqliteCortexPayloadProtectionScope? scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIdentifier(scope.Namespace, nameof(scope.Namespace));
        if ((scope.Kind is null) != (scope.RecordId is null) ||
            scope.Kind is not (null or "task" or "knowledge"))
            throw new ArgumentException("Payload protection selects a namespace or one exact task/knowledge record.", nameof(scope));
        if (scope.RecordId is not null) RequireIdentifier(scope.RecordId, nameof(scope.RecordId));
        return scope with { };
    }

    private static string PayloadProtectionId(SqliteCortexPayloadProtectionScope scope) =>
        "protection-" + Sha256(StrictUtf8.GetBytes("payload-protection/1\n" + scope.Namespace + "\n" +
            (scope.Kind ?? "namespace") + "\n" + (scope.RecordId ?? "")));

    private static async ValueTask RequirePayloadProtectionTargetAsync(SqliteConnection connection,
        SqliteTransaction transaction, SqliteCortexPayloadProtectionScope scope, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = scope.Kind is null
            ? "SELECT 1 FROM cortex_v2_namespace WHERE namespace_name=$ns;"
            : "SELECT 1 FROM cortex_v2_record_head WHERE namespace_name=$ns AND kind=$kind AND record_id=$id;";
        command.Parameters.AddWithValue("$ns", scope.Namespace);
        if (scope.Kind is not null)
        {
            command.Parameters.AddWithValue("$kind", scope.Kind);
            command.Parameters.AddWithValue("$id", scope.RecordId!);
        }
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("payload_protection_target_unavailable");
    }

    private async ValueTask RequireProtectedPayloadsHotAsync(SqliteConnection connection,
        SqliteTransaction transaction, SqliteCortexPayloadProtectionScope scope, CancellationToken cancellationToken)
    {
        var (version, _) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        // A missing payload must not disappear through an inner join and then
        // become a successful hot-residency claim, even in schema2. SQL length
        // checks inspect descriptors/residency without hydrating record bodies.
        var tierFailure = version == TieredSchemaVersion ? " OR b.storage_tier IS NOT 'hot'" : "";
        var failureExpression = version == TieredSchemaVersion
            ? "CASE WHEN b.content_hash IS NOT NULL AND b.storage_tier='cold' THEN 'payload_protection_requires_rehydration' ELSE 'payload_protection_payload_unavailable' END"
            : "'payload_protection_payload_unavailable'";
        command.CommandText = $$"""
            SELECT {{failureExpression}}
            FROM cortex_v2_record_revision r
            LEFT JOIN cortex_v2_content_blob b ON b.content_hash=r.content_hash
            WHERE r.namespace_name=$ns AND r.kind IN ('task','knowledge')
              AND ($kind IS NULL OR (r.kind=$kind AND r.record_id=$id))
              AND (b.content_hash IS NULL OR b.compressed_content IS NULL
                OR length(b.compressed_content)!=b.compressed_length{{tierFailure}}) LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ns", scope.Namespace);
        command.Parameters.AddWithValue("$kind", (object?)scope.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)scope.RecordId ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string failure)
            throw new InvalidOperationException(failure);
    }

    private async ValueTask<SqliteCortexPayloadProtectionState> ReadPayloadProtectionInTransactionAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteCortexPayloadProtectionScope scope,
        CancellationToken cancellationToken)
    {
        var id = PayloadProtectionId(scope);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT h.revision,h.revision_hash,h.content_hash,r.revision,r.revision_hash,r.content_hash,
              r.previous_revision_hash,
              CASE WHEN length(r.title)<=512 THEN r.title END,
              CASE WHEN length(CAST(r.tags_json AS BLOB))<=32768 THEN r.tags_json END,
              CASE WHEN length(r.summary)<=4096 THEN r.summary END,
              CASE WHEN length(CAST(r.attributes_json AS BLOB))<=4096 THEN r.attributes_json END,
              CASE WHEN length(r.event_type)<=128 THEN r.event_type END,
              CASE WHEN length(r.actor)<=128 THEN r.actor END,
              CASE WHEN length(r.idempotency_key)<=128 THEN r.idempotency_key END,
              r.is_searchable,
              CASE WHEN length(r.occurred_at_utc)<=40 THEN r.occurred_at_utc END,
              EXISTS(SELECT 1 FROM cortex_v2_search_head s WHERE s.namespace_name=h.namespace_name
                AND s.kind=h.kind AND s.record_id=h.record_id),
              EXISTS(SELECT 1 FROM cortex_v2_record_revision any_revision WHERE
                any_revision.namespace_name=$ns AND any_revision.kind=$kind AND any_revision.record_id=$id),
              EXISTS(SELECT 1 FROM cortex_v2_record_revision newer WHERE newer.namespace_name=h.namespace_name
                AND newer.kind=h.kind AND newer.record_id=h.record_id AND newer.revision>h.revision)
            FROM (SELECT 1) singleton LEFT JOIN cortex_v2_record_head h
              ON h.namespace_name=$ns AND h.kind=$kind AND h.record_id=$id
            LEFT JOIN cortex_v2_record_revision r
              ON r.namespace_name=h.namespace_name AND r.kind=h.kind
              AND r.record_id=h.record_id AND r.revision=h.revision;
            """;
        command.Parameters.AddWithValue("$ns", scope.Namespace);
        command.Parameters.AddWithValue("$kind", PayloadProtectionKind);
        command.Parameters.AddWithValue("$id", id);
        PayloadProtectionMetadata metadata;
        long revision; string revisionHash; string contentHash;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("payload_protection_invalid");
            if (reader.IsDBNull(0))
            {
                // A lost head must never reinterpret retained protection history
                // as a policy that was never written.
                if (reader.GetInt64(17) != 0) throw new InvalidDataException("payload_protection_invalid");
                return new(scope, 0, null, false, false);
            }
            if (Enumerable.Range(0, 6).Any(reader.IsDBNull) || Enumerable.Range(7, 10).Any(reader.IsDBNull))
                throw new InvalidDataException("payload_protection_invalid");
            revision = reader.GetInt64(0); revisionHash = reader.GetString(1); contentHash = reader.GetString(2);
            if (revision < 1 || revision != reader.GetInt64(3) || revisionHash != reader.GetString(4) ||
                contentHash != reader.GetString(5) || contentHash != Sha256([]) || reader.GetInt64(14) != 0 ||
                reader.GetInt64(16) != 0 || reader.GetInt64(18) != 0 || reader.GetString(7) != "Payload protection" ||
                reader.GetString(8) != "[]" || reader.GetString(9) != "" || reader.GetString(11) != PayloadProtectionEvent ||
                (revision == 1 ? !reader.IsDBNull(6) : reader.IsDBNull(6) || !SqliteCortexV2ColdPayloadFiles.IsHash(reader.GetString(6))))
                throw new InvalidDataException("payload_protection_invalid");
            var attributes = reader.GetString(10);
            metadata = ParsePayloadProtectionMetadata(attributes, scope);
            var candidate = new SqliteCortexV2AppendRequest(scope.Namespace, PayloadProtectionKind, id,
                revision - 1, reader.GetString(13), reader.GetString(12), reader.GetString(11), reader.GetString(7),
                [], "", null, false, attributes);
            DateTimeOffset occurredAt;
            try { occurredAt = ParseUtc(reader.GetString(15)); }
            catch (FormatException error) { throw new InvalidDataException("payload_protection_invalid", error); }
            var actual = RevisionHash(candidate, revision, reader.IsDBNull(6) ? null : reader.GetString(6),
                contentHash, reader.GetString(8), occurredAt);
            if (actual != revisionHash || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("payload_protection_invalid");
        }
        var content = await ReadPayloadAsync(connection, transaction, contentHash, cancellationToken).ConfigureAwait(false);
        if (content.Length != 0) throw new InvalidDataException("payload_protection_invalid");
        return new(scope, revision, revisionHash, metadata.Pinned, metadata.Hold);
    }

    private static PayloadProtectionMetadata ParsePayloadProtectionMetadata(string attributes,
        SqliteCortexPayloadProtectionScope scope)
    {
        try
        {
            using var document = JsonDocument.Parse(attributes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var properties = root.EnumerateObject().ToArray();
            string[] names = ["version", "namespace", "kind", "recordId", "pinned", "hold"];
            if (properties.Length != names.Length || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length ||
                properties.Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw new JsonException();
            if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
                root.GetProperty("namespace").ValueKind != JsonValueKind.String ||
                root.GetProperty("namespace").GetString() != scope.Namespace ||
                root.GetProperty("kind").ValueKind != (scope.Kind is null ? JsonValueKind.Null : JsonValueKind.String) ||
                root.GetProperty("kind").GetString() != scope.Kind ||
                root.GetProperty("recordId").ValueKind != (scope.RecordId is null ? JsonValueKind.Null : JsonValueKind.String) ||
                root.GetProperty("recordId").GetString() != scope.RecordId ||
                root.GetProperty("pinned").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                root.GetProperty("hold").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            return new(1, scope.Namespace, scope.Kind, scope.RecordId,
                root.GetProperty("pinned").GetBoolean(), root.GetProperty("hold").GetBoolean());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("payload_protection_invalid", error); }
    }

    private sealed record PayloadProtectionMetadata(int Version, string Namespace, string? Kind,
        string? RecordId, bool Pinned, bool Hold);
}
