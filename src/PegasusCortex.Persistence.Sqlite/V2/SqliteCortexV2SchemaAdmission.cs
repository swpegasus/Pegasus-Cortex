using Microsoft.Data.Sqlite;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2Store
{
    private sealed record RequiredColumnShape(string Name, string Type, bool? NotNull = true, int PrimaryKey = 0);
    private sealed record RequiredTableShape(string Name, RequiredColumnShape[] Columns, bool Virtual = false);

    private static readonly RequiredTableShape TieredBlobShape = new("cortex_v2_content_blob",
    [
        new("content_hash", "TEXT", NotNull: null, PrimaryKey: 1), new("compression", "TEXT"),
        new("original_length", "INTEGER"), new("compressed_length", "INTEGER"),
        new("compressed_content", "BLOB", NotNull: false), new("created_at_utc", "TEXT"),
        new("storage_tier", "TEXT"), new("cold_format_version", "INTEGER", NotNull: false),
        new("cold_sha256", "TEXT", NotNull: false)
    ]);

    private static async ValueTask VerifyTieringIndexesAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        foreach (var (index, table) in new[]
        {
            ("ix_cortex_v3_revision_content", "cortex_v2_record_revision"),
            ("ix_cortex_v3_head_content", "cortex_v2_record_head")
        })
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT seqno,cid,name FROM pragma_index_info($index) ORDER BY seqno LIMIT 3;";
            command.Parameters.AddWithValue("$index", index);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 0 ||
                    reader.GetInt32(1) < 0 || reader.GetString(2) != "content_hash" ||
                    await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("The Cortex tiering reference index is unsupported.");
            command.CommandText = "SELECT 1 FROM pragma_index_list($table) WHERE name=$index AND partial=0 AND origin='c' LIMIT 2;";
            command.Parameters.AddWithValue("$table", table);
            await using var list = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await list.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                await list.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The Cortex tiering reference index is missing.");
        }
    }

    // These are the required V2 column contracts, not a migration recipe. PK
    // nullability is left implicit only where the DDL does not spell NOT NULL.
    private static readonly RequiredTableShape RequiredMetadataShape = new("cortex_v2_meta",
        [
            new("singleton", "INTEGER", NotNull: null, PrimaryKey: 1),
            new("schema_version", "INTEGER"), new("generation", "TEXT"), new("created_at_utc", "TEXT")
        ]);

    private static readonly RequiredTableShape[] RequiredTableShapes =
    [
        new("cortex_v2_migration_lock",
        [
            new("singleton", "INTEGER", NotNull: null, PrimaryKey: 1),
            new("generation", "TEXT"), new("owner_token", "TEXT"),
            new("acquired_at_utc", "TEXT"), new("completed_at_utc", "TEXT")
        ]),
        new("cortex_v2_namespace",
        [
            new("namespace_name", "TEXT", NotNull: null, PrimaryKey: 1), new("namespace_token", "TEXT"),
            new("revision_count", "INTEGER"), new("head_count", "INTEGER"),
            new("referenced_content_bytes", "INTEGER"), new("head_content_bytes", "INTEGER"),
            new("hot_chunk_count", "INTEGER"), new("created_at_utc", "TEXT"), new("updated_at_utc", "TEXT")
        ]),
        new("cortex_v2_content_blob",
        [
            new("content_hash", "TEXT", NotNull: null, PrimaryKey: 1), new("compression", "TEXT"),
            new("original_length", "INTEGER"), new("compressed_length", "INTEGER"),
            new("compressed_content", "BLOB"), new("created_at_utc", "TEXT")
        ]),
        new("cortex_v2_record_revision",
        [
            new("namespace_name", "TEXT", PrimaryKey: 1), new("kind", "TEXT", PrimaryKey: 2),
            new("record_id", "TEXT", PrimaryKey: 3), new("revision", "INTEGER", PrimaryKey: 4),
            new("previous_revision_hash", "TEXT", NotNull: false), new("revision_hash", "TEXT"),
            new("content_hash", "TEXT"), new("title", "TEXT"), new("tags_json", "TEXT"),
            new("tags_search", "TEXT"), new("summary", "TEXT"), new("attributes_json", "TEXT"),
            new("event_type", "TEXT"), new("actor", "TEXT"), new("idempotency_key", "TEXT"),
            new("request_hash", "TEXT"), new("is_searchable", "INTEGER"), new("occurred_at_utc", "TEXT")
        ]),
        new("cortex_v2_record_head",
        [
            new("namespace_name", "TEXT", PrimaryKey: 1), new("kind", "TEXT", PrimaryKey: 2),
            new("record_id", "TEXT", PrimaryKey: 3), new("revision", "INTEGER"),
            new("revision_hash", "TEXT"), new("content_hash", "TEXT")
        ]),
        new("cortex_v2_search_head",
        [
            new("search_row_id", "INTEGER", NotNull: null, PrimaryKey: 1),
            new("namespace_name", "TEXT"), new("namespace_token", "TEXT"), new("kind", "TEXT"),
            new("record_id", "TEXT"), new("revision", "INTEGER"), new("revision_hash", "TEXT"),
            new("chunk_ordinal", "INTEGER"), new("title", "TEXT"), new("tags", "TEXT"),
            new("summary", "TEXT"), new("body", "TEXT")
        ]),
        new("cortex_v2_search_fts",
        [
            new("namespace_token", "", NotNull: false), new("kind", "", NotNull: false),
            new("title", "", NotNull: false), new("tags", "", NotNull: false),
            new("summary", "", NotNull: false), new("body", "", NotNull: false)
        ], Virtual: true)
    ];

    private static async ValueTask VerifyRequiredTableShapeAsync(
        SqliteConnection connection, SqliteTransaction? transaction,
        RequiredTableShape expected, CancellationToken cancellationToken)
    {
        try
        {
            // Both PRAGMAs expose schema metadata only. Bounds apply even to an
            // incompatible file; no namespace, blob, revision or FTS row is read.
            await using (var table = connection.CreateCommand())
            {
                table.Transaction = transaction;
                table.CommandText = """
                    SELECT type, strict FROM pragma_table_list
                    WHERE "schema" = 'main' AND name = $table LIMIT 2;
                    """;
                table.Parameters.AddWithValue("$table", expected.Name);
                await using var reader = await table.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    reader.GetString(0) != (expected.Virtual ? "virtual" : "table") ||
                    reader.GetInt32(1) != (expected.Virtual ? 0 : 1) ||
                    await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw InvalidTableShape(expected.Name);
            }

            await using var columns = connection.CreateCommand();
            columns.Transaction = transaction;
            columns.CommandText = """
                SELECT substr(name, 1, 129), substr(type, 1, 33), "notnull", pk, hidden
                FROM pragma_table_xinfo($table) ORDER BY cid LIMIT 65;
                """;
            columns.Parameters.AddWithValue("$table", expected.Name);
            await using var fields = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var count = 0;
            var visible = 0;
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            while (await fields.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++count > 64) throw InvalidTableShape(expected.Name);
                var name = fields.GetString(0);
                var type = fields.GetString(1);
                var notNull = fields.GetInt32(2);
                var primaryKey = fields.GetInt32(3);
                var visibility = fields.GetInt32(4);
                if (name.Length > 128 || type.Length > 32) throw InvalidTableShape(expected.Name);
                if (visibility != 0)
                {
                    if (!expected.Virtual || visibility != 1 ||
                        name != expected.Name && name != "rank" ||
                        type.Length != 0 || notNull != 0 || primaryKey != 0 || !hidden.Add(name))
                        throw InvalidTableShape(expected.Name);
                    continue;
                }
                if (visible >= expected.Columns.Length) throw InvalidTableShape(expected.Name);
                var column = expected.Columns[visible++];
                if (!string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(type, column.Type, StringComparison.OrdinalIgnoreCase) ||
                    primaryKey != column.PrimaryKey ||
                    column.NotNull is { } requiredNotNull && notNull != (requiredNotNull ? 1 : 0))
                    throw InvalidTableShape(expected.Name);
            }
            if (visible != expected.Columns.Length ||
                expected.Virtual && !hidden.SetEquals([expected.Name, "rank"]))
                throw InvalidTableShape(expected.Name);
        }
        catch (SqliteException error)
        {
            throw new InvalidDataException(
                $"The Cortex v2 schema metadata for '{expected.Name}' is invalid; automatic migration is prohibited.", error);
        }
    }

    private static InvalidDataException InvalidTableShape(string table) => new(
        $"The Cortex v2 schema for '{table}' has an unsupported column or table shape; automatic migration is prohibited.");
}

