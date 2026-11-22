using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class BoundedRecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int QueuedCount = 1024;
    private const int QuarantinedCount = 4;
    private const int ResidentMaximum = 131;

    internal static async Task RunAsync()
    {
        using var fixture = new TempRoot();
        var state = fixture.Dir("bounded-recovery");
        var database = Path.Combine(state, "agentops.db");
        using (var schema = new SqliteJobStore(state)) { }
        Seed(database, fixture.Path);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var peakResident = 0;
        var peakWaiters = 0;
        var healthCalls = 0;
        await using (var engine = new AgentOpsEngine(state))
        {
            AssertResidentWindow(engine, ref peakResident, ref peakWaiters);
            // The exact last queued row begins outside the resident window. A
            // disk-only wait must reach its terminal result without hydrating a
            // waiter/history dictionary entry for every row in front of it.
            var final = engine.ExecuteAsync(Request("job", ("op", "wait"), ("jid", Id(QueuedCount))), deadline.Token);
            while (!final.IsCompleted)
            {
                var health = await engine.ExecuteAsync(Request("diag"), deadline.Token);
                Check(health.Ok, "health during recovery failed");
                healthCalls++;
                AssertResidentWindow(engine, ref peakResident, ref peakWaiters);
                Check(Convert.ToInt64(health.Data!["queue_payload_bytes"]) <= 16 * 1024 * 1024,
                    "recovery exceeded resident payload bound");
                await Task.WhenAny(final, Task.Delay(10, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
            }
            var lastReceipt = await final;
            Check(lastReceipt.Jid == Id(QueuedCount) && lastReceipt.State == "failed" && lastReceipt.Err == "not_found",
                "disk-only last-row wait did not yield its missing-tool terminal receipt");

            // A receipt can become terminal slightly before its execution handle
            // retires. Check actual counters rather than assume terminal==idle.
            while (true)
            {
                var health = await engine.ExecuteAsync(Request("diag"), deadline.Token);
                healthCalls++;
                AssertResidentWindow(engine, ref peakResident, ref peakWaiters);
                if (engine.ResidentJobCount == 0 && engine.ResidentCompletionCount == 0 &&
                    Convert.ToInt32(health.Data!["queue_active"]) == 0 &&
                    Convert.ToInt32(health.Data["queue_pending"]) == 0 &&
                    Convert.ToInt64(health.Data["queue_payload_bytes"]) == 0 &&
                    !Convert.ToBoolean(health.Data["queue_recovery_pending"])) break;
                await Task.Delay(10, deadline.Token);
            }
            for (var index = 0; index < 100; index++)
            {
                var health = await engine.ExecuteAsync(Request("diag"), deadline.Token);
                healthCalls++;
                Check(health.Ok && engine.ResidentJobCount == 0 && engine.ResidentCompletionCount == 0,
                    "health snapshots accumulated resident history after recovery");
            }
            AssertDatabase(database);

            foreach (var jid in new[] { "recovery-running", "recovery-ephemeral", "recovery-app", "recovery-oversize" })
            {
                var read = await engine.ExecuteAsync(Request("job", ("op", "stat"), ("jid", jid)), deadline.Token);
                Check(read.Ok && read.Data!["rec"] is OperationReceipt { State: "recovery_required", Ok: false },
                    "unsafe recovery row did not retain truthful quarantine: " + jid);
            }
            // Quarantining one oversized row must not permanently close admission.
            var fresh = await engine.ExecuteAsync(new OperationRequest("run",
                new Dictionary<string, string> { ["cfg"] = "fixture-missing-tool" }, "fresh-after-recovery"), deadline.Token);
            Check(fresh.State == "failed" && fresh.Err == "not_found", "oversized legacy row poisoned future admission");
        }
        Check(!Directory.Exists(Path.Combine(fixture.Path, "ephemeral-must-not-run")), "omitted body was replayed");
        await using (var reopened = new AgentOpsEngine(state))
        {
            Check(reopened.ResidentJobCount == 0 && reopened.ResidentCompletionCount == 0,
                "restart hydrated recovered terminal history");
        }
        Console.WriteLine($"EVIDENCE generated recovery rows={QueuedCount + QuarantinedCount} replay_failed={QueuedCount} " +
            $"quarantined={QuarantinedCount} peak_resident={peakResident} peak_waiters={peakWaiters} health_calls={healthCalls}; " +
            "disk-only terminal wait, bounded resident counters, no child/tool launch and no installed state touched.");
    }

    private static void Seed(string database, string fixtureRoot)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO jobs(jid,rid,seq,state,receipt,request,mutation,updated_utc) " +
            "VALUES($jid,$jid,$seq,$state,$receipt,$request,1,'2026-10-08T00:00:00Z')";
        insert.Parameters.Add("$jid", SqliteType.Text);
        insert.Parameters.Add("$seq", SqliteType.Integer);
        insert.Parameters.Add("$state", SqliteType.Text);
        insert.Parameters.Add("$receipt", SqliteType.Text);
        insert.Parameters.Add("$request", SqliteType.Text);

        Add("recovery-running", 1, "running", MissingTool());
        Add("recovery-ephemeral", 2, "queued", new("wr", new Dictionary<string, string>
        {
            ["dst"] = Path.Combine(fixtureRoot, "ephemeral-must-not-run"), [OperationRequest.BodyOmittedKey] = "1"
        }));
        Add("recovery-app", 3, "queued", new("app", new Dictionary<string, string> { ["op"] = "start", ["name"] = "fixture" }));
        Add("recovery-oversize", 4, "queued", new("run", new Dictionary<string, string>
        {
            ["cfg"] = "fixture-missing-tool", ["arg0"] = new string('x', 17 * 1024 * 1024)
        }));
        for (var index = 1; index <= QueuedCount; index++) Add(Id(index), index + QuarantinedCount, "queued", MissingTool());
        transaction.Commit();

        void Add(string jid, int sequence, string state, OperationRequest request)
        {
            var receipt = new OperationReceipt(true, jid, jid, state, OperationNames.Public(request),
                Data: new Dictionary<string, object?> { ["seq"] = sequence });
            insert.Parameters["$jid"].Value = jid;
            insert.Parameters["$seq"].Value = sequence;
            insert.Parameters["$state"].Value = state;
            insert.Parameters["$receipt"].Value = JsonSerializer.Serialize(receipt, Json);
            insert.Parameters["$request"].Value = JsonSerializer.Serialize(request with { Rid = jid }, Json);
            insert.ExecuteNonQuery();
        }
    }

    private static void AssertDatabase(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*), SUM(CASE WHEN state='failed' THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN state='recovery_required' THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN request IS NOT NULL THEN 1 ELSE 0 END) FROM jobs";
        using var reader = query.ExecuteReader();
        Check(reader.Read() && reader.GetInt64(0) == QueuedCount + QuarantinedCount &&
            reader.GetInt64(1) == QueuedCount && reader.GetInt64(2) == QuarantinedCount && reader.GetInt64(3) == 0,
            "recovery lost rows, replayed unsafe work, retained requests, or health created history");
    }

    private static void AssertResidentWindow(AgentOpsEngine engine, ref int peakResident, ref int peakWaiters)
    {
        var resident = engine.ResidentJobCount;
        var waiters = engine.ResidentCompletionCount;
        peakResident = Math.Max(peakResident, resident);
        peakWaiters = Math.Max(peakWaiters, waiters);
        Check(resident <= ResidentMaximum && waiters <= ResidentMaximum, "recovery hydrated beyond bounded scheduling window");
    }
    private static OperationRequest MissingTool() => new("run", new Dictionary<string, string> { ["cfg"] = "fixture-missing-tool" });
    private static string Id(int index) => "recovery-queued-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    private static OperationRequest Request(string operation, params (string Key, string Value)[] arguments) =>
        new(operation, arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("bounded_recovery: " + message);
    }
}
