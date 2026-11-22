using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pegasus.Cortex.Persistence.Sqlite.Storage;

/// <summary>
/// Verifies that an existing database is the complete Cortex schema before any
/// initializer is allowed to create or alter objects in it.
/// </summary>
internal static class SqliteCortexSchemaVerifier
{
    public const int SupportedSchemaVersion = 1;
    private const string TaskSnapshotHeadIndexName =
        "ux_retained_head_single_task_snapshot";
    private const string TaskSnapshotHeadIndexSql = """
        CREATE UNIQUE INDEX ux_retained_head_single_task_snapshot
            ON retained_head(project_id)
            WHERE kind = 'task-snapshot'
        """;

    private static readonly IReadOnlyDictionary<string, ColumnShape[]> RequiredColumns =
        new Dictionary<string, ColumnShape[]>(StringComparer.Ordinal)
        {
            ["cortex_schema"] =
            [
                new("singleton", "INTEGER", NotNull: false, PrimaryKeyOrdinal: 1),
                new("version", "INTEGER", NotNull: true, PrimaryKeyOrdinal: 0)
            ],
            ["retained_document"] =
            [
                new("project_id", "TEXT", true, 1),
                new("kind", "TEXT", true, 2),
                new("document_id", "TEXT", true, 3),
                new("revision", "INTEGER", true, 4),
                new("previous_hash", "TEXT", false, 0),
                new("idempotency_key", "TEXT", true, 0),
                new("producer_id", "TEXT", true, 0),
                new("contract_id", "TEXT", true, 0),
                new("content_hash", "TEXT", true, 0),
                new("canonical_json", "BLOB", true, 0),
                new("search_text", "TEXT", true, 0),
                new("captured_at_utc", "TEXT", true, 0),
                new("expires_at_utc", "TEXT", true, 0)
            ],
            ["retained_head"] =
            [
                new("project_id", "TEXT", true, 1),
                new("kind", "TEXT", true, 2),
                new("document_id", "TEXT", true, 3),
                new("revision", "INTEGER", true, 0),
                new("content_hash", "TEXT", true, 0)
            ],
            ["retention_audit"] =
            [
                new("audit_id", "TEXT", true, 1),
                new("project_id", "TEXT", true, 0),
                new("kind", "TEXT", true, 0),
                new("document_id", "TEXT", true, 0),
                new("revision", "INTEGER", true, 0),
                new("content_hash", "TEXT", true, 0),
                new("producer_id", "TEXT", true, 0),
                new("occurred_at_utc", "TEXT", true, 0)
            ],
            ["retained_document_fts"] =
            [
                new("project_id", string.Empty, false, 0),
                new("kind", string.Empty, false, 0),
                new("document_id", string.Empty, false, 0),
                new("revision", string.Empty, false, 0),
                new("content", string.Empty, false, 0),
                new("retained_document_fts", string.Empty, false, 0, Hidden: 1),
                new("rank", string.Empty, false, 0, Hidden: 1)
            ]
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredCreateSql =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cortex_schema"] = """
                CREATE TABLE cortex_schema(
                    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                    version INTEGER NOT NULL CHECK(version > 0)
                ) STRICT
                """,
            ["retained_document"] = """
                CREATE TABLE retained_document(
                    project_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    previous_hash TEXT NULL,
                    idempotency_key TEXT NOT NULL,
                    producer_id TEXT NOT NULL,
                    contract_id TEXT NOT NULL,
                    content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
                    canonical_json BLOB NOT NULL CHECK(length(canonical_json) BETWEEN 2 AND 32768),
                    search_text TEXT NOT NULL CHECK(length(search_text) <= 4096),
                    captured_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    PRIMARY KEY(project_id, kind, document_id, revision),
                    UNIQUE(project_id, kind, producer_id, idempotency_key),
                    CHECK(
                        (revision = 1 AND previous_hash IS NULL) OR
                        (revision > 1 AND length(previous_hash) = 64))
                ) STRICT
                """,
            ["retained_head"] = """
                CREATE TABLE retained_head(
                    project_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
                    PRIMARY KEY(project_id, kind, document_id),
                    FOREIGN KEY(project_id, kind, document_id, revision)
                        REFERENCES retained_document(project_id, kind, document_id, revision)
                        ON DELETE RESTRICT
                ) STRICT
                """,
            ["retention_audit"] = """
                CREATE TABLE retention_audit(
                    audit_id TEXT PRIMARY KEY CHECK(length(audit_id) = 64),
                    project_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    content_hash TEXT NOT NULL CHECK(length(content_hash) = 64),
                    producer_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    FOREIGN KEY(project_id, kind, document_id, revision)
                        REFERENCES retained_document(project_id, kind, document_id, revision)
                        ON DELETE RESTRICT
                ) STRICT
                """,
            ["retained_document_fts"] = """
                CREATE VIRTUAL TABLE retained_document_fts USING fts5(
                    project_id UNINDEXED,
                    kind UNINDEXED,
                    document_id UNINDEXED,
                    revision UNINDEXED,
                    content,
                    tokenize = 'unicode61'
                )
                """
        };

    private static readonly IReadOnlyDictionary<string, string[]> RequiredIndexes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["cortex_schema"] = [],
            ["retained_document"] =
            [
                "pk:project_id,kind,document_id,revision",
                "u:project_id,kind,producer_id,idempotency_key"
            ],
            ["retained_head"] = ["pk:project_id,kind,document_id"],
            ["retention_audit"] = ["pk:audit_id"],
            ["retained_document_fts"] = []
        };

    private static readonly IReadOnlySet<string> RequiredTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "cortex_schema",
            "retained_document",
            "retained_head",
            "retention_audit",
            "retained_document_fts",
            "retained_document_fts_config",
            "retained_document_fts_content",
            "retained_document_fts_data",
            "retained_document_fts_docsize",
            "retained_document_fts_idx"
        };

    public static async ValueTask VerifyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken,
        bool requireTaskSnapshotHeadIndex = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await VerifyVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var entry in RequiredColumns)
        {
            await VerifyTableAsync(
                connection,
                transaction,
                entry.Key,
                entry.Value,
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var entry in RequiredIndexes)
        {
            await VerifyIndexesAsync(
                connection,
                transaction,
                entry.Key,
                entry.Value,
                requireTaskSnapshotHeadIndex,
                cancellationToken).ConfigureAwait(false);
        }
        await VerifyObjectSetAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await VerifyForeignKeyAsync(
            connection,
            transaction,
            "retained_head",
            cancellationToken).ConfigureAwait(false);
        await VerifyForeignKeyAsync(
            connection,
            transaction,
            "retention_audit",
            cancellationToken).ConfigureAwait(false);
        foreach (var table in new[]
                 {
                     "cortex_schema",
                     "retained_document",
                     "retained_document_fts"
                 })
        {
            await VerifyNoForeignKeysAsync(
                connection,
                transaction,
                table,
                cancellationToken).ConfigureAwait(false);
        }
        await VerifyForeignKeyRowsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await VerifyPageSizeAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask VerifyVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COALESCE(MAX(version), 0)
              FROM cortex_schema
             WHERE singleton = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != 1 ||
            reader.GetInt64(1) != SupportedSchemaVersion)
        {
            throw new InvalidDataException("The local Cortex schema version is unsupported.");
        }
    }

    private static async ValueTask VerifyTableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<ColumnShape> requiredColumns,
        CancellationToken cancellationToken)
    {
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                SELECT sql
                  FROM sqlite_schema
                 WHERE type = 'table' AND name = $table;
                """;
            schema.Parameters.AddWithValue("$table", table);
            var sql = Convert.ToString(
                await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(sql) ||
                !string.Equals(
                    NormalizeSql(sql),
                    NormalizeSql(RequiredCreateSql[table]),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The required local Cortex table '{table}' is absent or incompatible.");
            }
        }

        await using var columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = """
            SELECT name, type, "notnull", dflt_value, pk, hidden
              FROM pragma_table_xinfo($table)
             ORDER BY cid;
            """;
        columns.Parameters.AddWithValue("$table", table);
        var observed = new List<ColumnShape>(requiredColumns.Count);
        await using var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(3))
            {
                throw new InvalidDataException(
                    $"The required local Cortex table '{table}' has an unexpected column default.");
            }
            observed.Add(new ColumnShape(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2) == 1,
                checked((int)reader.GetInt64(4)),
                checked((int)reader.GetInt64(5))));
        }
        if (!observed.SequenceEqual(requiredColumns))
            throw new InvalidDataException($"The required local Cortex table '{table}' has an incompatible shape.");
    }

    private static async ValueTask VerifyIndexesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<string> requiredIndexes,
        bool requireTaskSnapshotHeadIndex,
        CancellationToken cancellationToken)
    {
        var entries = new List<(string Name, string Origin)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT name, "unique", origin, partial
                  FROM pragma_index_list($table)
                 ORDER BY name;
                """;
            command.Parameters.AddWithValue("$table", table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var unique = reader.GetInt64(1) == 1;
                var origin = reader.GetString(2);
                var partial = reader.GetInt64(3) == 1;
                if (string.Equals(
                        table,
                        "retained_head",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        name,
                        TaskSnapshotHeadIndexName,
                        StringComparison.Ordinal))
                {
                    if (!unique || !partial ||
                        !string.Equals(origin, "c", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The local Cortex task-set head index is incompatible.");
                    }
                    entries.Add((name, "p"));
                    continue;
                }
                if (!unique || partial || origin is not ("pk" or "u"))
                {
                    throw new InvalidDataException(
                        $"The local Cortex table '{table}' has an unsupported index.");
                }
                entries.Add((name, origin));
            }
        }

        var observed = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            var columns = new List<string>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT cid, name, "desc", coll, "key"
                  FROM pragma_index_xinfo($index)
                 ORDER BY seqno;
                """;
            command.Parameters.AddWithValue("$index", entry.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(4) == 0)
                    continue;
                if (reader.GetInt64(0) < 0 ||
                    reader.IsDBNull(1) ||
                    reader.GetInt64(2) != 0 ||
                    !string.Equals(reader.GetString(3), "BINARY", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The local Cortex table '{table}' has an incompatible index key.");
                }
                columns.Add(reader.GetString(1));
            }
            observed.Add($"{entry.Origin}:{string.Join(',', columns)}");

            if (entry.Origin == "p")
            {
                await using var definition = connection.CreateCommand();
                definition.Transaction = transaction;
                definition.CommandText = """
                    SELECT sql
                      FROM sqlite_schema
                     WHERE type = 'index' AND name = $index;
                    """;
                definition.Parameters.AddWithValue("$index", entry.Name);
                var sql = Convert.ToString(
                    await definition.ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (!string.Equals(
                        NormalizeSql(sql ?? string.Empty),
                        NormalizeSql(TaskSnapshotHeadIndexSql),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The local Cortex task-set head index definition is incompatible.");
                }
            }
        }

        var expectedIndexes = requiredIndexes.ToList();
        if (string.Equals(table, "retained_head", StringComparison.Ordinal) &&
            (requireTaskSnapshotHeadIndex ||
             observed.Contains("p:project_id", StringComparer.Ordinal)))
        {
            expectedIndexes.Add("p:project_id");
        }
        if (!observed.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    expectedIndexes.OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The local Cortex table '{table}' has incompatible key or uniqueness constraints.");
        }
    }

    private static async ValueTask VerifyForeignKeyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        var expected = new[]
        {
            "0:project_id:project_id",
            "1:kind:kind",
            "2:document_id:document_id",
            "3:revision:revision"
        };
        var observed = new List<string>(expected.Length);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, seq, "table", "from", "to", on_update, on_delete, "match"
              FROM pragma_foreign_key_list($table)
             ORDER BY id, seq;
            """;
        command.Parameters.AddWithValue("$table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt64(0) != 0 ||
                !string.Equals(reader.GetString(2), "retained_document", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(5), "NO ACTION", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(6), "RESTRICT", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(7), "NONE", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The local Cortex table '{table}' has an incompatible revision foreign key.");
            }
            observed.Add(
                $"{reader.GetInt64(1)}:{reader.GetString(3)}:{reader.GetString(4)}");
        }
        if (!observed.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException($"The local Cortex table '{table}' has incomplete revision linkage.");
    }

    private static async ValueTask VerifyNoForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list($table);";
        command.Parameters.AddWithValue("$table", table);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 0)
            throw new InvalidDataException($"The local Cortex table '{table}' has an unexpected foreign key.");
    }

    private static async ValueTask VerifyObjectSetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                tables.Add(reader.GetString(0));
        }
        if (!tables.SetEquals(RequiredTables))
        {
            throw new InvalidDataException(
                "The local Cortex schema contains a missing or unexpected table.");
        }

        await using var unexpected = connection.CreateCommand();
        unexpected.Transaction = transaction;
        unexpected.CommandText = """
            SELECT COUNT(*)
              FROM sqlite_schema
             WHERE type IN ('trigger', 'view')
                OR type = 'index' AND sql IS NOT NULL
                   AND name <> $taskSnapshotHeadIndex;
            """;
        unexpected.Parameters.AddWithValue(
            "$taskSnapshotHeadIndex",
            TaskSnapshotHeadIndexName);
        var count = Convert.ToInt64(
            await unexpected.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 0)
        {
            throw new InvalidDataException(
                "The local Cortex schema contains an unexpected executable or mutable object.");
        }
    }

    private static async ValueTask VerifyForeignKeyRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The local Cortex database contains invalid foreign-key references.");
    }

    private static async ValueTask VerifyPageSizeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA page_size;";
        var pageSize = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (pageSize != 4096)
            throw new InvalidDataException("The local Cortex database page size is unsupported.");
    }

    private static string NormalizeSql(string? value) =>
        value is null
            ? string.Empty
            : new string(value
                .Where(character => !char.IsWhiteSpace(character))
                .Select(char.ToLowerInvariant)
                .ToArray());

    private sealed record ColumnShape(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrdinal,
        int Hidden = 0);
}
