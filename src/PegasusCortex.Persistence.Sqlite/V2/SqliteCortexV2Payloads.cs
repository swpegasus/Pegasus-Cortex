using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Core.V2;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2Store
{
    private sealed record Payload(byte[] Compressed, byte[] Content, SqliteCortexColdPayload Descriptor, bool Cold);

    private async ValueTask<byte[]> ReadPayloadAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string hash, CancellationToken cancellationToken, int maximumBytes = 16 * 1024 * 1024) =>
        (await ReadPayloadValueAsync(connection, transaction, hash, cancellationToken, maximumBytes).ConfigureAwait(false)).Content;

    private async ValueTask<Payload> ReadPayloadValueAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string hash, CancellationToken cancellationToken, int? maximumBytes = null)
    {
        var admittedMaximum = maximumBytes ?? _options.MaximumContentBytes;
        if (admittedMaximum is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var (version, generation) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (version is not (SchemaVersion or TieredSchemaVersion) || generation != Generation)
            throw new InvalidDataException("The payload schema generation is unsupported.");
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = version == TieredSchemaVersion
            ? "SELECT original_length,compressed_length,compressed_content,storage_tier,cold_format_version,cold_sha256 FROM cortex_v2_content_blob WHERE content_hash=$hash;"
            : "SELECT original_length,compressed_length,compressed_content,'hot',NULL,NULL FROM cortex_v2_content_blob WHERE content_hash=$hash;";
        command.Parameters.AddWithValue("$hash", hash);
        int original, length; bool cold; byte[]? compressed; string? coldHash; int format;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("A retained Cortex payload descriptor is missing.");
            var original64 = reader.GetInt64(0); var length64 = reader.GetInt64(1);
            if (original64 < 0 || original64 > admittedMaximum || length64 < 1 ||
                length64 > admittedMaximum + 65536)
                throw new InvalidDataException("The Cortex payload exceeds its admitted byte bounds.");
            original = (int)original64; length = (int)length64;
            cold = reader.GetString(3) switch { "hot" => false, "cold" => true,
                _ => throw new InvalidDataException("The Cortex payload tier is invalid.") };
            if (original < 0 || original > admittedMaximum || length < 1 ||
                length > admittedMaximum + 65536)
                throw new InvalidDataException("The Cortex payload exceeds its admitted byte bounds.");
            compressed = reader.IsDBNull(2) ? null : (byte[])reader[2];
            format = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            coldHash = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (cold ? compressed is not null || format != 1 || !SqliteCortexV2ColdPayloadFiles.IsHash(coldHash!)
                     : compressed is null || format != 0 || coldHash is not null)
                throw new InvalidDataException("The Cortex payload tier descriptor is inconsistent.");
        }
        var descriptor = new SqliteCortexColdPayload(hash, original, length,
            coldHash ?? Sha256(compressed!), cold ? format : 1);
        try
        {
            if (cold) compressed = await _coldPayloads.ReadAsync(descriptor, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (compressed is null || compressed.Length != length || Sha256(compressed) != descriptor.CompressedSha256)
                throw new InvalidDataException("The Cortex payload failed its compressed identity.");
            var content = Decompress(compressed, original, admittedMaximum);
            if (Sha256(content) != hash) throw new InvalidDataException("The Cortex payload failed its content identity.");
            return new(compressed, content, descriptor, cold);
        }
        catch (Exception error) when (cold && error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { throw new CortexPayloadUnavailableException(error); }
    }

    private async ValueTask<bool> PromotePayloadAsync(SqliteConnection connection, SqliteTransaction transaction,
        string hash, CancellationToken cancellationToken)
    {
        var (version, _) = await ReadGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (version == SchemaVersion) return false;
        await using (var tier = connection.CreateCommand())
        {
            tier.Transaction = transaction;
            tier.CommandText = "SELECT storage_tier FROM cortex_v2_content_blob WHERE content_hash=$hash;";
            tier.Parameters.AddWithValue("$hash", hash);
            var value = await tier.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is "hot") return false; // Prior exact/inherited reader already verified these bytes.
            if (value is not "cold") throw new InvalidDataException("The retained payload tier is unavailable.");
        }
        var payload = await ReadPayloadValueAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false);
        if (!payload.Cold) return false;
        await using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE cortex_v2_content_blob SET compressed_content=$bytes,storage_tier='hot',cold_format_version=NULL,cold_sha256=NULL WHERE content_hash=$hash AND storage_tier='cold';";
        update.Parameters.AddWithValue("$hash", hash);
        update.Parameters.Add("$bytes", SqliteType.Blob).Value = payload.Compressed;
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Cold payload promotion lost its exact descriptor.");
        return true;
    }

    // Backups remain single self-contained databases. The staging copy uses the
    // source's immutable provider, never a guessed sidecar beneath staging.
    private async ValueTask HydrateSnapshotAsync(string staging, CancellationToken cancellationToken)
    {
        await using var snapshot = new SqliteCortexV2Store(new(staging,
            MaximumContentBytes: _options.MaximumContentBytes), _timeProvider, _coldPayloads);
        await snapshot.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.EffectiveSchemaVersion == SchemaVersion) return;
        await using var connection = snapshot.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string? after = null;
        while (true)
        {
            await using var select = connection.CreateCommand(); select.Transaction = transaction;
            select.CommandText = "SELECT content_hash FROM cortex_v2_content_blob WHERE storage_tier='cold' AND ($after IS NULL OR content_hash>$after) ORDER BY content_hash LIMIT 32;";
            select.Parameters.AddWithValue("$after", after is null ? DBNull.Value : after);
            var hashes = new List<string>(32);
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) hashes.Add(reader.GetString(0));
            if (hashes.Count == 0) break;
            foreach (var hash in hashes)
                await snapshot.PromotePayloadAsync(connection, transaction, hash, cancellationToken).ConfigureAwait(false);
            after = hashes[^1];
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask RequireSelfContainedSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (version, _) = await ReadGenerationAsync(connection, null, cancellationToken).ConfigureAwait(false);
        if (version == SchemaVersion) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM cortex_v2_content_blob WHERE storage_tier='cold' LIMIT 1;";
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidDataException("Restore requires a self-contained snapshot with all cold payloads hydrated.");
    }
}
