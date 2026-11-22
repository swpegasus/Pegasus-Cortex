using System.Globalization;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed partial class SqliteCortexV2Store : ICortexUsageStore
{
    private const int UsageRowLimit = 100_000;
    private static readonly string[] UsageOperations =
    ["status", "namespaces", "start", "search", "read", "batch-read", "task", "knowledge", "finish",
     "init", "migrate-legacy", "capture", "ingest", "refresh", "warmup", "unknown"];

    public async ValueTask RecordUsageAsync(CortexUsageSample sample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Channel is not ("mcp" or "offline") || !UsageOperations.Contains(sample.Operation) ||
            !Guid.TryParseExact(sample.SessionId, "N", out _) ||
            (sample.TaskId is not null && CortexUsageRecorder.CanonicalTaskId(sample.TaskId) is null) ||
            sample.Namespaces.Count > 16 || sample.Results < 0 || sample.ExactRecords < 0 ||
            sample.ResponseBytes is < 0 or > 4 * 1024 * 1024 || sample.DurationMicroseconds is < 0 or > 86_400_000_000)
            throw new ArgumentException("Invalid bounded usage sample.");
        foreach (var ns in sample.Namespaces) RequireIdentifier(ns, nameof(sample.Namespaces));
        // Never create a semantic store just to record offline diagnostics.
        if (!File.Exists(DatabasePath)) throw new FileNotFoundException("Central store is unavailable.");
        _options.Validate();
        await using var connection = CreateUsageConnection(readOnly: false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyGenerationAsync(connection, null, cancellationToken).ConfigureAwait(false);
        await RequireWalAsync(connection, null, cancellationToken).ConfigureAwait(false);
        // Optional metrics obey the same capacity ceiling as semantic writes.
        await ApplyPageCeilingAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cutoff = now.AddDays(-89).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandText = UsageSchema + """
                INSERT OR IGNORE INTO cortex_usage_meta_v1 VALUES(1, $now);
                DELETE FROM cortex_usage_day_v1 WHERE day < $cutoff;
                DELETE FROM cortex_usage_process_v1 WHERE day < $cutoff;
                """;
            setup.Parameters.AddWithValue("$now", now.ToString("O"));
            setup.Parameters.AddWithValue("$cutoff", cutoff);
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var rows = await ReadScalarInt64Async(connection, transaction,
            "SELECT count(*) FROM cortex_usage_day_v1;", cancellationToken).ConfigureAwait(false);
        var processes = await ReadScalarInt64Async(connection, transaction,
            "SELECT count(*) FROM cortex_usage_process_v1;", cancellationToken).ConfigureAwait(false);
        // Bound total cardinality, not just report size. A refused sample is exposed by the caller.
        if (rows + sample.Namespaces.Count + 2 > UsageRowLimit || processes >= UsageRowLimit)
            throw new InvalidOperationException("Utilization retention capacity reached.");
        var coordinates = new List<(string Dimension, string Key)> { ("total", "all") };
        coordinates.AddRange(sample.Namespaces.Distinct(StringComparer.Ordinal).Select(ns => ("namespace", ns)));
        if (sample.TaskId is not null) coordinates.Add(("task", sample.TaskId));
        foreach (var (dimension, key) in coordinates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cortex_usage_day_v1
                    (day,dimension,key,channel,operation,calls,failures,results,exact_records,response_bytes,total_us,max_us,last_utc)
                VALUES($day,$dimension,$key,$channel,$operation,1,$failures,$results,$exact,$bytes,$us,$us,$now)
                ON CONFLICT(day,dimension,key,channel,operation) DO UPDATE SET
                    calls=calls+1, failures=failures+excluded.failures,
                    results=results+excluded.results, exact_records=exact_records+excluded.exact_records,
                    response_bytes=response_bytes+excluded.response_bytes,
                    total_us=total_us+excluded.total_us,max_us=max(max_us,excluded.max_us),last_utc=max(last_utc,excluded.last_utc);
                """;
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$dimension", dimension);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$channel", sample.Channel);
            command.Parameters.AddWithValue("$operation", sample.Operation);
            command.Parameters.AddWithValue("$failures", sample.Failed ? 1 : 0);
            // Multi-namespace calls count one access per namespace. Payload/timing belongs
            // to the operation, not each namespace; do not multiply bytes or results.
            command.Parameters.AddWithValue("$results", dimension == "namespace" ? 0 : sample.Results);
            command.Parameters.AddWithValue("$exact", dimension == "namespace" ? 0 : sample.ExactRecords);
            command.Parameters.AddWithValue("$bytes", dimension == "namespace" ? 0 : sample.ResponseBytes);
            command.Parameters.AddWithValue("$us", dimension == "namespace" ? 0 : sample.DurationMicroseconds);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var process = connection.CreateCommand())
        {
            process.Transaction = transaction;
            process.CommandText = "INSERT OR IGNORE INTO cortex_usage_process_v1 VALUES($day,$id);";
            process.Parameters.AddWithValue("$day", day);
            process.Parameters.AddWithValue("$id", sample.SessionId);
            await process.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CortexUsageReport> ReadUsageAsync(int days = 7, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days));
        _options.Validate();
        if (!File.Exists(DatabasePath)) return CortexUsageReport.Empty(days);
        // Dashboard/report reads do not initialize, migrate, prune, or change pragmas.
        await using var connection = CreateUsageConnection(readOnly: true);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyGenerationAsync(connection, null, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (!await TableExistsAsync(connection, "cortex_usage_meta_v1", transaction, cancellationToken).ConfigureAwait(false))
            return CortexUsageReport.Empty(days);
        var cutoff = _timeProvider.GetUtcNow().ToUniversalTime().AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? started;
        await using (var meta = connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = "SELECT started_utc FROM cortex_usage_meta_v1 WHERE singleton=1;";
            started = (string?)await meta.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        var rows = new List<CortexUsageRow>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT dimension,key,channel,operation,sum(calls),sum(failures),sum(results),sum(exact_records),
                    sum(response_bytes),sum(total_us),max(max_us),max(last_utc)
                FROM cortex_usage_day_v1 WHERE day >= $cutoff
                GROUP BY dimension,key,channel,operation
                ORDER BY CASE dimension WHEN 'total' THEN 0 WHEN 'namespace' THEN 1 ELSE 2 END,
                    sum(calls) DESC,key,channel,operation LIMIT 513;
                """;
            query.Parameters.AddWithValue("$cutoff", cutoff);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
                    reader.GetInt64(9), reader.GetInt64(10), reader.GetString(11)));
        }
        long processes, tasks;
        await using (var counts = connection.CreateCommand())
        {
            counts.Transaction = transaction;
            counts.Parameters.AddWithValue("$cutoff", cutoff);
            counts.CommandText = "SELECT count(DISTINCT session_id) FROM cortex_usage_process_v1 WHERE day >= $cutoff;";
            processes = (long)(await counts.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            counts.CommandText = "SELECT count(DISTINCT key) FROM cortex_usage_day_v1 WHERE day >= $cutoff AND dimension='task';";
            tasks = (long)(await counts.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new("cortex.utilization/1", "available", days, 90, started,
            rows.Where(r => r.Dimension == "total").Select(r => r.LastSeenUtc).OrderDescending().FirstOrDefault(),
            processes, tasks, rows.Count > 512, rows.Take(512).ToArray());
    }

    private SqliteConnection CreateUsageConnection(bool readOnly) => new(new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1
    }.ToString());

    private const string UsageSchema = """
        CREATE TABLE IF NOT EXISTS cortex_usage_meta_v1(
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),started_utc TEXT NOT NULL) STRICT;
        CREATE TABLE IF NOT EXISTS cortex_usage_day_v1(
            day TEXT NOT NULL,dimension TEXT NOT NULL CHECK(dimension IN ('total','namespace','task')),
            key TEXT NOT NULL,channel TEXT NOT NULL CHECK(channel IN ('mcp','offline')),operation TEXT NOT NULL,
            calls INTEGER NOT NULL,failures INTEGER NOT NULL,results INTEGER NOT NULL,exact_records INTEGER NOT NULL,
            response_bytes INTEGER NOT NULL,total_us INTEGER NOT NULL,max_us INTEGER NOT NULL,last_utc TEXT NOT NULL,
            PRIMARY KEY(day,dimension,key,channel,operation)) STRICT;
        CREATE TABLE IF NOT EXISTS cortex_usage_process_v1(
            day TEXT NOT NULL,session_id TEXT NOT NULL,PRIMARY KEY(day,session_id)) STRICT;
        """;
}
