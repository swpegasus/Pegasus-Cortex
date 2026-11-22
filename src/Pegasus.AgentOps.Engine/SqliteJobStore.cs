using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal sealed record DurableJob(OperationReceipt Receipt, bool Mutation, long Sequence, OperationRequest? Request);

internal sealed class SqliteJobStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SqliteJobStore(string stateRoot)
    {
        var path = Path.Combine(stateRoot, "agentops.db");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try
        {
            _connection.Open();
            using var setup = _connection.CreateCommand();
            setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS jobs (jid TEXT PRIMARY KEY, rid TEXT NOT NULL UNIQUE, seq INTEGER NOT NULL UNIQUE, state TEXT NOT NULL, receipt TEXT NOT NULL, request TEXT NULL, mutation INTEGER NOT NULL, updated_utc TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_jobs_state_seq ON jobs(state,seq);";
            setup.ExecuteNonQuery();
            EnsureHistoryColumn("pruned_utc");
            EnsureHistoryColumn("discarded_utc");
            using var historyIndex = _connection.CreateCommand();
            historyIndex.CommandText = "CREATE INDEX IF NOT EXISTS ix_jobs_history_pruned ON jobs(state,pruned_utc,updated_utc,seq); CREATE INDEX IF NOT EXISTS ix_jobs_history_discarded ON jobs(state,discarded_utc,updated_utc,seq); CREATE INDEX IF NOT EXISTS ix_jobs_maintenance_state_seq ON jobs(state,discarded_utc,seq);";
            historyIndex.ExecuteNonQuery();
        }
        catch { _connection.Dispose(); throw; }
    }

    private void EnsureHistoryColumn(string name)
    {
        using var columns = _connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(jobs)";
        using (var reader = columns.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1) == name) return;
        // Names are fixed literals owned by this schema migration, never caller input.
        using var add = _connection.CreateCommand();
        add.CommandText = "ALTER TABLE jobs ADD COLUMN " + name + " TEXT NULL";
        add.ExecuteNonQuery();
    }

    // Recovery is streamed into the bounded admission window. Even a legacy queue
    // larger than that window must not be materialized at startup.
    public long HighestRecoverableSequence()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(seq),0) FROM jobs WHERE state IN ('queued','running')";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public DurableJob? LoadNextRecoverable(long afterSequence, long throughSequence, long maximumRequestBytes)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT receipt, mutation, seq, CASE WHEN length(CAST(request AS BLOB))<=$maximum THEN request ELSE NULL END FROM jobs WHERE state IN ('queued','running') AND seq>$after AND seq<=$through ORDER BY seq LIMIT 1";
            command.Parameters.AddWithValue("$after", afterSequence);
            command.Parameters.AddWithValue("$through", throughSequence);
            command.Parameters.AddWithValue("$maximum", maximumRequestBytes);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var receipt = JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(0), Json) ?? throw new InvalidDataException("job_receipt");
            var request = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<OperationRequest>(reader.GetString(3), Json);
            return new DurableJob(receipt, reader.GetBoolean(1), reader.GetInt64(2), request);
        }
    }

    public DurableJob? Find(string jid)
        => FindExact("jid", jid);

    public DurableJob? FindByRid(string rid)
        => FindExact("rid", rid);

    public OperationReceipt? FindReceipt(string jid)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT receipt FROM jobs WHERE jid=$jid";
            command.Parameters.AddWithValue("$jid", jid);
            var json = command.ExecuteScalar() as string;
            return json is null ? null : JsonSerializer.Deserialize<OperationReceipt>(json, Json)
                ?? throw new InvalidDataException("job_receipt");
        }
    }

    private DurableJob? FindExact(string column, string value)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT receipt,mutation,seq,request FROM jobs WHERE {column}=$value";
            command.Parameters.AddWithValue("$value", value);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var receipt = JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(0), Json)
                ?? throw new InvalidDataException("job_receipt");
            var request = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<OperationRequest>(reader.GetString(3), Json);
            return new DurableJob(receipt, reader.GetBoolean(1), reader.GetInt64(2), request);
        }
    }

    // The sequence index bounds the page before JSON projection. No historical body
    // is deserialized into an OperationReceipt merely to display a recent-job list.
    public IReadOnlyList<JobSummary> ListRecent(int maximum)
    {
        if (maximum is < 1 or > 100) throw new AgentOpsException("invalid");
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT jid,rid,state,json_extract(receipt,'$.op'),json_extract(receipt,'$.err'),COALESCE(json_extract(receipt,'$.partial'),0),seq FROM jobs ORDER BY seq DESC LIMIT $maximum";
            command.Parameters.AddWithValue("$maximum", maximum);
            using var reader = command.ExecuteReader();
            var result = new List<JobSummary>(maximum);
            while (reader.Read())
                result.Add(new JobSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetInt64(6)));
            return result;
        }
    }

    internal sealed record JobSummary(string Jid, string Rid, string State, string Op, string? Err, bool Partial, long Seq);

    internal sealed record HistorySummary(string Jid, string Rid, string State, string Op, string? Err,
        bool Partial, long Seq, string UpdatedUtc, long RetainedBytes, bool Pruned, bool Discarded);
    internal sealed record HistoryPage(IReadOnlyList<HistorySummary> Jobs, long Next, bool More);
    internal sealed record HistoryPruneResult(int Selected, int Pruned, int SkippedActive,
        int SkippedInvalid, long Next, bool More);

    internal HistoryPage ListHistory(string? state, DateTimeOffset? before, long after, int maximum)
    {
        if (maximum is < 1 or > 256 || after < 0) throw new AgentOpsException("invalid");
        var states = HistoryStates(state);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            // Each indexed state walk is capped before the cross-state ordering.
            // JSON projection runs only over the bounded selected page.
            var parts = states.Select((_, index) => "SELECT * FROM (SELECT jid,rid,state,receipt,seq,updated_utc,pruned_utc,discarded_utc FROM jobs WHERE state=$state" + index +
                " AND seq>$after" + (before is null ? "" : " AND updated_utc<$before") + " ORDER BY seq LIMIT $maximum)");
            command.CommandText = "WITH selected AS (" + string.Join(" UNION ALL ", parts) +
                ") SELECT jid,rid,state,json_extract(receipt,'$.op'),json_extract(receipt,'$.err'),COALESCE(json_extract(receipt,'$.partial'),0),seq,updated_utc,length(CAST(receipt AS BLOB)),pruned_utc,discarded_utc FROM selected ORDER BY seq LIMIT $maximum";
            for (var index = 0; index < states.Length; index++) command.Parameters.AddWithValue("$state" + index, states[index]);
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$maximum", maximum + 1);
            if (before is not null) command.Parameters.AddWithValue("$before", HistoryCutoffUpperBound(before.Value));
            using var reader = command.ExecuteReader();
            var jobs = new List<HistorySummary>(maximum);
            var next = after;
            var more = false;
            var scanned = 0;
            while (reader.Read())
            {
                if (scanned++ == maximum) { more = true; break; }
                next = reader.GetInt64(6);
                if (before is not null && (!DateTimeOffset.TryParse(reader.GetString(7),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
                    out var updated) || updated >= before.Value)) continue;
                jobs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), next,
                    reader.GetString(7), reader.GetInt64(8), !reader.IsDBNull(9), !reader.IsDBNull(10)));
            }
            return new(jobs, next, more);
        }
    }

    private static string[] HistoryStates(string? state) => state switch
    {
        null or "all" => ["queued", "running", "done", "failed", "canceled", "attention", "recovery_required"],
        "unresolved" => ["attention", "recovery_required"],
        "queued" or "running" or "done" or "failed" or "canceled" or "attention" or "recovery_required" => [state],
        _ => throw new AgentOpsException("invalid")
    };

    internal HistoryPruneResult PruneHistory(DateTimeOffset before, int maximum, bool discardUnresolved,
        Func<string, string, string, bool> isActive, DateTimeOffset now)
    {
        if (maximum is < 1 or > 256 || before.Offset != TimeSpan.Zero || now.Offset != TimeSpan.Zero)
            throw new AgentOpsException("invalid");
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            string[] states = discardUnresolved ? ["attention", "recovery_required"] : ["done", "failed", "canceled"];
            using var selected = _connection.CreateCommand();
            selected.Transaction = transaction;
            var marker = discardUnresolved ? "discarded_utc" : "pruned_utc";
            var parts = states.Select((_, index) => "SELECT * FROM (SELECT jid,rid,seq FROM jobs WHERE state=$state" + index +
                " AND " + marker + " IS NULL AND updated_utc<$before ORDER BY updated_utc,seq LIMIT $maximum)");
            selected.CommandText = string.Join(" UNION ALL ", parts) + " ORDER BY seq LIMIT $maximum";
            for (var index = 0; index < states.Length; index++) selected.Parameters.AddWithValue("$state" + index, states[index]);
            selected.Parameters.AddWithValue("$before", HistoryCutoffUpperBound(before));
            selected.Parameters.AddWithValue("$maximum", maximum + 1);
            var candidates = new List<(string Jid, string Rid, long Sequence)>(maximum);
            var more = false;
            using (var reader = selected.ExecuteReader())
                while (reader.Read())
                {
                    if (candidates.Count == maximum) { more = true; break; }
                    candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
                }
            var pruned = 0;
            var active = 0;
            var invalid = 0;
            foreach (var candidate in candidates)
            {
                // The engine holds its admission lock before entering this store
                // transaction, so no selected dormant job can become active here.
                using var read = _connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT CASE WHEN length(CAST(receipt AS BLOB))<=$maximum THEN receipt ELSE NULL END,updated_utc FROM jobs WHERE jid=$jid";
                read.Parameters.AddWithValue("$jid", candidate.Jid);
                read.Parameters.AddWithValue("$maximum", ProtocolVersion.MaxFrameBytes);
                string? original;
                string updated;
                using (var reader = read.ExecuteReader())
                {
                    if (!reader.Read()) { invalid++; continue; }
                    original = reader.IsDBNull(0) ? null : reader.GetString(0);
                    updated = reader.GetString(1);
                }
                if (original is null || !DateTimeOffset.TryParse(updated, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var updatedUtc))
                { invalid++; continue; }
                if (updatedUtc >= before) continue;
                OperationReceipt? receipt;
                try { receipt = JsonSerializer.Deserialize<OperationReceipt>(original, Json); }
                catch (JsonException) { invalid++; continue; }
                if (receipt is null || receipt.Jid != candidate.Jid || receipt.Rid != candidate.Rid ||
                    receipt.Jid is null || receipt.Rid is null || receipt.Op is null ||
                    receipt.Jid.Length > 256 || receipt.Rid.Length > 256 || receipt.Op.Length > 128 ||
                    receipt.Failure is { } failure && (failure.Execution is null || failure.Retry is null ||
                        failure.Execution.Value is null || failure.Execution.Source is null || failure.Retry.Value is null || failure.Retry.Source is null))
                { invalid++; continue; }
                if (isActive(candidate.Jid, candidate.Rid, receipt.Op)) { active++; continue; }
                var compact = CompactHistoryReceipt(receipt, original, candidate.Sequence, now, discardUnresolved);
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE jobs SET receipt=$receipt,request=NULL,pruned_utc=COALESCE(pruned_utc,$utc),discarded_utc=CASE WHEN $discard=1 THEN $utc ELSE discarded_utc END WHERE jid=$jid AND state=$state AND " + marker + " IS NULL";
                update.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(compact, Json));
                update.Parameters.AddWithValue("$utc", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$discard", discardUnresolved ? 1 : 0);
                update.Parameters.AddWithValue("$jid", candidate.Jid);
                update.Parameters.AddWithValue("$state", receipt.State);
                pruned += update.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(candidates.Count, pruned, active, invalid, candidates.Count == 0 ? 0 : candidates[^1].Sequence, more);
        }
    }

    private static OperationReceipt CompactHistoryReceipt(OperationReceipt receipt, string original, long sequence,
        DateTimeOffset now, bool discarded)
    {
        var data = new Dictionary<string, object?>
        {
            ["seq"] = sequence, ["history_pruned"] = true, ["history_discarded"] = discarded,
            ["pruned_utc"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ["log_available"] = false
        };
        // These bounded scalar identities support metadata-only replay. No path,
        // request, diagnostic text or process output survives payload pruning.
        foreach (var key in new[] { "requestIdentity", "aid", "name", "epoch", "retry_of", "original_receipt_sha256", "pruned_utc" })
            if (receipt.Data?.GetValueOrDefault(key) is { } value)
            {
                var text = value is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : value as string;
                if (text is { Length: <= 128 }) data[key] = text;
            }
        if (!data.ContainsKey("original_receipt_sha256"))
            data["original_receipt_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
        if (discarded) data["discarded_utc"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var failure = receipt.Failure is null ? null : new OperationFailureEvidence(
            "Historical failure details were pruned; the original outcome is unchanged.",
            new(BoundHistoryFact(receipt.Failure.Execution.Value), BoundHistoryFact(receipt.Failure.Execution.Source)),
            new(BoundHistoryFact(receipt.Failure.Retry.Value), BoundHistoryFact(receipt.Failure.Retry.Source)),
            DiagnosticsOmitted: true, DetailAvailability: "not_retained", LogAvailability: "not_retained");
        return receipt with { Data = data, Detail = null, Failure = failure };
    }

    private static string BoundHistoryFact(string value) => value.Length <= 128 ? value : value[..128];

    // DateTimeOffset's old round-trip representation and legacy UTC-Z values
    // sort differently within the same second. Seek conservatively through that
    // second, then enforce the exact timestamp in the bounded selected page.
    private static string HistoryCutoffUpperBound(DateTimeOffset before) =>
        before.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "~";

    // Call with the owner's existing admission fence held. Sequence-keyset
    // metadata pages use the existing state/sequence index and never hydrate a
    // whole history or build a new startup index. Each pass examines <=BatchSize
    // rows; a cursor lets later passes get past protected old entries.
    internal ExecutionHistoryRetentionResult RetainCompletedHistory(DateTimeOffset now, long afterSequence,
        Func<string, string, string, bool> isActive, ExecutionHistoryRetention? policy = null)
    {
        policy ??= ExecutionHistoryRetention.Default;
        policy.Validate(); ArgumentNullException.ThrowIfNull(isActive);
        if (now.Offset != TimeSpan.Zero || afterSequence < 0) throw new ArgumentException("history_retention_context");
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            long Scalar(string sql)
            {
                using var command = _connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            var used = checked(Math.Max(0, Scalar("PRAGMA page_count;") - Scalar("PRAGMA freelist_count;")) * Scalar("PRAGMA page_size;"));
            // A threshold probe, not a full COUNT/SUM over all retained history.
            var count = checked((int)Scalar("SELECT COUNT(*) FROM (SELECT 1 FROM jobs WHERE state IN ('done','failed','canceled') LIMIT " +
                (policy.CompletedRowTarget + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"));
            var pressure = used >= policy.UsedPageTriggerBytes || count > policy.CompletedRowTarget;
            var before = now - (pressure ? policy.RecentReplayWindow : policy.MaximumAge);
            // MAX(seq)+1 is the existing allocator. Keep its newest row so age
            // cleanup cannot recycle public cursors after an otherwise idle
            // history is entirely expired. This needs no new ledger/schema.
            var newest = Scalar("SELECT COALESCE(MAX(seq),0) FROM jobs");
            var states = new[] { "done", "failed", "canceled", "attention", "recovery_required" };
            using var select = _connection.CreateCommand(); select.Transaction = transaction;
            var branches = states.Select((state, index) =>
                "SELECT * FROM (SELECT seq,CASE WHEN length(CAST(jid AS BLOB))<=128 THEN jid END jid," +
                "CASE WHEN length(CAST(rid AS BLOB))<=128 THEN rid END rid,state," +
                "CASE WHEN length(updated_utc)<=40 THEN updated_utc END updated_utc,discarded_utc FROM jobs WHERE state=$s" + index +
                " AND seq>$after" + (index >= 3 ? " AND discarded_utc IS NOT NULL" : "") + " ORDER BY seq LIMIT $maximum)");
            select.CommandText = string.Join(" UNION ALL ", branches) + " ORDER BY seq LIMIT $maximum";
            for (var index = 0; index < states.Length; index++) select.Parameters.AddWithValue("$s" + index, states[index]);
            select.Parameters.AddWithValue("$after", afterSequence); select.Parameters.AddWithValue("$maximum", policy.BatchSize + 1);
            var candidates = new List<(long Seq, string? Jid, string? Rid, string State, string? Updated, bool Discarded)>(policy.BatchSize);
            var more = false;
            using (var reader = select.ExecuteReader())
                while (reader.Read())
                {
                    if (candidates.Count == policy.BatchSize) { more = true; break; }
                    candidates.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), !reader.IsDBNull(5)));
                }
            var deleted = 0; var active = 0; var recent = 0; var uncertain = 0; var invalid = 0; var anchor = 0;
            foreach (var row in candidates)
            {
                if (row.Seq == newest) { anchor++; continue; }
                if (row.Jid is null || row.Rid is null || !DateTimeOffset.TryParse(row.Updated, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var updated) || updated.Offset != TimeSpan.Zero)
                { invalid++; continue; }
                if (updated >= before) { recent++; continue; }
                if (isActive(row.Jid, row.Rid, "")) { active++; continue; }
                using var read = _connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = "SELECT CASE WHEN length(CAST(receipt AS BLOB))<=$maximum THEN receipt END FROM jobs WHERE jid=$jid AND seq=$seq AND state=$state";
                read.Parameters.AddWithValue("$maximum", ProtocolVersion.MaxFrameBytes); read.Parameters.AddWithValue("$jid", row.Jid);
                read.Parameters.AddWithValue("$seq", row.Seq); read.Parameters.AddWithValue("$state", row.State);
                if (read.ExecuteScalar() is not string serialized) { invalid++; continue; }
                OperationReceipt? receipt;
                try { receipt = JsonSerializer.Deserialize<OperationReceipt>(serialized, Json); }
                catch (JsonException) { invalid++; continue; }
                if (receipt is null || receipt.Jid != row.Jid || receipt.Rid != row.Rid || receipt.State != row.State)
                { invalid++; continue; }
                var discarded = row.Discarded && Truth(receipt.Data?.GetValueOrDefault("history_discarded"));
                if (!discarded && (row.State is "attention" or "recovery_required" || receipt.Partial ||
                    receipt.Failure?.Execution.Value == OperationExecutionState.Unknown || Truth(receipt.Data?.GetValueOrDefault("outcome_unknown"))))
                { uncertain++; continue; }
                if (isActive(row.Jid, row.Rid, receipt.Op)) { active++; continue; }
                using var remove = _connection.CreateCommand(); remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM jobs WHERE jid=$jid AND rid=$rid AND seq=$seq AND state=$state AND updated_utc=$updated" +
                    (row.State is "attention" or "recovery_required" ? " AND discarded_utc IS NOT NULL" : "");
                remove.Parameters.AddWithValue("$jid", row.Jid); remove.Parameters.AddWithValue("$rid", row.Rid);
                remove.Parameters.AddWithValue("$seq", row.Seq); remove.Parameters.AddWithValue("$state", row.State); remove.Parameters.AddWithValue("$updated", row.Updated!);
                deleted += remove.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(candidates.Count, deleted, active, recent, uncertain, invalid, anchor,
                more && candidates.Count > 0 ? candidates[^1].Seq : 0, more, used, count, pressure);
        }

        static bool Truth(object? value) => value is true or JsonElement { ValueKind: JsonValueKind.True };
    }

    public DurableJob Claim(OperationReceipt receipt, OperationRequest request, bool mutation)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            using var existing = _connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT receipt,mutation,seq,request FROM jobs WHERE rid=$rid";
            existing.Parameters.AddWithValue("$rid", receipt.Rid);
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    var foundReceipt = JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(0), Json)!;
                    var foundRequest = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<OperationRequest>(reader.GetString(3), Json);
                    var found = new DurableJob(foundReceipt, reader.GetBoolean(1), reader.GetInt64(2), foundRequest);
                    transaction.Commit(); return found;
                }
            }
            using var next = _connection.CreateCommand(); next.Transaction = transaction; next.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM jobs";
            var sequence = Convert.ToInt64(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            var withSequence = receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>()) { ["seq"] = sequence } };
            using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO jobs(jid,rid,seq,state,receipt,request,mutation,updated_utc) VALUES($jid,$rid,$seq,$state,$receipt,$request,$mutation,$utc)";
            insert.Parameters.AddWithValue("$jid", withSequence.Jid); insert.Parameters.AddWithValue("$rid", withSequence.Rid); insert.Parameters.AddWithValue("$seq", sequence); insert.Parameters.AddWithValue("$state", withSequence.State);
            insert.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(withSequence, Json)); insert.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, Json)); insert.Parameters.AddWithValue("$mutation", mutation ? 1 : 0); insert.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery(); transaction.Commit(); return new DurableJob(withSequence, mutation, sequence, request);
        }
    }

    public void Update(DurableJob job)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET state=$state,receipt=$receipt,request=$request,updated_utc=$utc WHERE jid=$jid AND pruned_utc IS NULL";
            command.Parameters.AddWithValue("$jid", job.Receipt.Jid); command.Parameters.AddWithValue("$state", job.Receipt.State); command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(job.Receipt, Json));
            command.Parameters.AddWithValue("$request", job.Request is null ? DBNull.Value : JsonSerializer.Serialize(job.Request, Json)); command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        }
    }

    // Starting and queued cancellation share one SQL state precondition. A
    // canceled job cannot be revived by the worker's later resident snapshot.
    public bool TryStartQueued(DurableJob job)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET state='running',receipt=$receipt,request=$request,updated_utc=$utc WHERE jid=$jid AND state='queued'";
            command.Parameters.AddWithValue("$jid", job.Receipt.Jid);
            command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(job.Receipt, Json));
            command.Parameters.AddWithValue("$request", job.Request is null ? DBNull.Value : JsonSerializer.Serialize(job.Request, Json));
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            return command.ExecuteNonQuery() == 1;
        }
    }

    public bool TryCancelQueued(DurableJob job)
    {
        lock(_gate){using var command=_connection.CreateCommand();command.CommandText="UPDATE jobs SET state='canceled',receipt=$receipt,request=NULL,updated_utc=$utc WHERE jid=$jid AND state='queued'";command.Parameters.AddWithValue("$jid",job.Receipt.Jid);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(job.Receipt,Json));command.Parameters.AddWithValue("$utc",DateTimeOffset.UtcNow.ToString("O"));return command.ExecuteNonQuery()==1;}
    }

    internal sealed record MaintenancePage(IReadOnlyList<MaintenanceEffect> Effects,long Next,bool HasMore);

    // The state/sequence index bounds work independently of terminal history.
    // Scan at most 32 unresolved metadata rows and return at most eight effects.
    internal MaintenancePage ListMaintenanceEffects(long after)
    {
        lock(_gate)
        {
            using var command=_connection.CreateCommand();
            // Bound each state-index walk before the cross-state ordering; an
            // IN + ORDER BY seq alone can sort every unresolved legacy row.
            command.CommandText="SELECT jid,rid,state,json_extract(receipt,'$.op'),CASE WHEN length(CAST(receipt AS BLOB))<=$maximum THEN receipt ELSE NULL END,seq,mutation FROM jobs WHERE seq IN ("+
                "SELECT seq FROM (SELECT seq FROM jobs WHERE state='queued' AND discarded_utc IS NULL AND seq>$after ORDER BY seq LIMIT 32) UNION ALL "+
                "SELECT seq FROM (SELECT seq FROM jobs WHERE state='running' AND discarded_utc IS NULL AND seq>$after ORDER BY seq LIMIT 32) UNION ALL "+
                "SELECT seq FROM (SELECT seq FROM jobs WHERE state='recovery_required' AND discarded_utc IS NULL AND seq>$after ORDER BY seq LIMIT 32) UNION ALL "+
                "SELECT seq FROM (SELECT seq FROM jobs WHERE state='attention' AND discarded_utc IS NULL AND seq>$after ORDER BY seq LIMIT 32)) ORDER BY seq LIMIT 32";
            command.Parameters.AddWithValue("$after",after);
            command.Parameters.AddWithValue("$maximum",ProtocolVersion.MaxFrameBytes);
            var effects=new List<MaintenanceEffect>(8);
            var next=after;
            using(var reader=command.ExecuteReader())
                while(reader.Read())
                {
                    next=reader.GetInt64(5);
                    var op=reader.GetString(3);
                    if(!reader.GetBoolean(6)&&MaintenanceRequestPolicy.IsReadOnlyReceipt(op))continue;
                    if(reader.IsDBNull(4))throw new AgentOpsException("maintenance_effect_unavailable");
                    effects.Add(Effect(reader.GetString(0),reader.GetString(1),reader.GetString(2),op,reader.GetString(4),next));
                    if(effects.Count==8)break;
                }
            using var remaining=_connection.CreateCommand();
            remaining.CommandText="SELECT EXISTS(SELECT 1 FROM jobs WHERE state IN ('queued','running','recovery_required','attention') AND discarded_utc IS NULL AND seq>$after LIMIT 1)";
            remaining.Parameters.AddWithValue("$after",next);
            return new(effects,next,Convert.ToInt64(remaining.ExecuteScalar())!=0);
        }
    }

    internal MaintenanceEffect? FindMaintenanceEffect(string jid)
    {
        lock(_gate)
        {
            using var command=_connection.CreateCommand();
            command.CommandText="SELECT jid,rid,state,json_extract(receipt,'$.op'),CASE WHEN length(CAST(receipt AS BLOB))<=$maximum THEN receipt ELSE NULL END,seq FROM jobs WHERE jid=$jid AND discarded_utc IS NULL";
            command.Parameters.AddWithValue("$jid",jid);
            command.Parameters.AddWithValue("$maximum",ProtocolVersion.MaxFrameBytes);
            using var reader=command.ExecuteReader();
            if(!reader.Read())return null;
            if(reader.IsDBNull(4))throw new AgentOpsException("maintenance_effect_unavailable");
            return Effect(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5));
        }
    }

    private static MaintenanceEffect Effect(string jid,string rid,string state,string op,string original,long sequence)
    {
        // Hash the exact original stored UTF-8 receipt, never a projected status
        // or a deserialized/reserialized object. The original row stays untouched.
        var bytes=Encoding.UTF8.GetBytes(original);
        try { return new("user",jid,rid,state,op,Convert.ToHexStringLower(SHA256.HashData(bytes)),sequence); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose() => _connection.Dispose();
}
