using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class JobHistoryMemoryTests
{
    public static async Task RunAsync()
    {
        using var fixture = new TempRoot();
        var state = fixture.Dir("history");
        var database = Path.Combine(state, "agentops.db");
        using (var schema = new SqliteJobStore(state)) { }
        const int historyCount = 5000;
        var payload = new string('x', 4096) + "\r\nΩ exact retained body";
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO jobs(jid,rid,seq,state,receipt,request,mutation,updated_utc) VALUES($jid,$rid,$seq,$state,$receipt,NULL,0,'2026-10-07T00:00:00Z')";
            insert.Parameters.Add("$jid", SqliteType.Text);
            insert.Parameters.Add("$rid", SqliteType.Text);
            insert.Parameters.Add("$seq", SqliteType.Integer);
            insert.Parameters.Add("$state", SqliteType.Text);
            insert.Parameters.Add("$receipt", SqliteType.Text);
            for (var index = 1; index <= historyCount; index++)
            {
                var jid = $"history-{index}";
                var rid = $"history-rid-{index}";
                var terminalState = index == 1 ? "done" : new[] { "done", "failed", "canceled", "attention", "recovery_required" }[index % 5];
                var receipt = new OperationReceipt(terminalState == "done", jid, rid, terminalState, "rd",
                    Data: new Dictionary<string, object?> { ["out"] = payload, ["seq"] = index });
                insert.Parameters["$jid"].Value = jid;
                insert.Parameters["$rid"].Value = rid;
                insert.Parameters["$seq"].Value = index;
                insert.Parameters["$state"].Value = terminalState;
                insert.Parameters["$receipt"].Value = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        var before = GC.GetTotalMemory(forceFullCollection: true);
        await using (var engine = new AgentOpsEngine(state))
        {
            Check(engine.ResidentJobCount == 0, "Startup hydrated terminal history.");
            for (var index = 0; index < 200; index++)
            {
                var health = await engine.ExecuteAsync(Request("diag"));
                Check(health.Ok && engine.ResidentJobCount == 0, "A health check accumulated resident history.");
            }
            var page = await engine.ExecuteAsync(Request("job", ("op", "ls"), ("max", "7")));
            var summaries = JsonSerializer.SerializeToElement(page.Data!["jobs"], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(page.Ok && summaries.GetArrayLength() == 7 && summaries[0].GetProperty("jid").GetString() == "history-5000",
                "Recent history is not a bounded descending sequence page.");
            Check(!summaries[0].TryGetProperty("data", out _), "A history summary hydrated a full body.");
            var exact = await engine.ExecuteAsync(Request("job", ("op", "stat"), ("jid", "history-1")));
            var saved = (OperationReceipt)exact.Data!["rec"]!;
            Check(exact.Ok && ((JsonElement)saved.Data!["out"]!).GetString() == payload,
                "Rollover lost an exact old receipt body.");
            var waited = await engine.ExecuteAsync(Request("job", ("op", "wait"), ("jid", "history-1")));
            Check(waited.Jid == "history-1" && waited.State == "done", "An old exact wait no longer works.");
            var log = await engine.ExecuteAsync(Request("job", ("op", "log"), ("jid", "history-1"), ("max", "1024")));
            Check(log.Ok && engine.ResidentJobCount == 0, "Log retrieval accumulated history in RAM.");
            Check(Count(database) == historyCount, "Monitoring created durable history rows.");

            var request = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(fixture.Path, "made") }, "durable-rid");
            var completed = await engine.ExecuteAsync(request);
            var replay = await engine.ExecuteAsync(request);
            Check(completed.Ok && replay.Jid == completed.Jid && engine.ResidentJobCount == 0,
                "Terminal rollover lost durable mutation idempotency.");
            Check(Count(database) == historyCount + 1, "Idempotent replay created duplicate history.");
            var growth = GC.GetTotalMemory(forceFullCollection: true) - before;
            Check(growth < 8 * 1024 * 1024, "The generated terminal history accumulated in managed RAM.");
            Console.WriteLine($"METRIC history rows={historyCount} terminal_resident=0 retained_managed_growth_bytes={growth}");
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            Console.WriteLine($"METRIC fixture_process private_bytes={process.PrivateMemorySize64} working_set_bytes={process.WorkingSet64}");
        }
        await using (var reopened = new AgentOpsEngine(state))
        {
            Check(reopened.ResidentJobCount == 0, "Restart rehydrated completed history.");
            var request = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(fixture.Path, "made") }, "durable-rid");
            Check((await reopened.ExecuteAsync(request)).Ok && Count(database) == historyCount + 1,
                "Restart lost durable RID replay.");
        }
        Console.WriteLine("EVIDENCE 5000 generated SQLite history rows, zero terminal resident jobs, exact retained bodies/RID replay, and non-durable health/monitor snapshots. No installed store touched.");
    }

    public static async Task CancellationAndScalarHealthAsync()
    {
        using var fixture = new TempRoot();
        var state = fixture.Dir("cancellation");
        await using (var engine = new AgentOpsEngine(state))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            for (var index = 0; index < 64; index++)
            {
                var target = Path.Combine(fixture.Path, "never-" + index);
                var request = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = target }, "cancel-rid-" + index);
                try { await engine.ExecuteAsync(request, canceled.Token); throw new InvalidOperationException("Canceled admission returned normally."); }
                catch (OperationCanceledException) { }
                Check(engine.ResidentJobCount == 0 && engine.ResidentCompletionCount == 0,
                    "Cancellation before enqueue retained the job or its waiter.");
                var replay = await engine.ExecuteAsync(request);
                Check(replay.State == "canceled" && !Directory.Exists(target), "Canceled RID replay executed a mutation.");
            }
        }
        using (var store = new SqliteJobStore(fixture.Dir("leases")))
        {
            foreach (var cancelFirst in new[] { true, false })
            {
                var id = cancelFirst ? "cancel-first" : "start-first";
                var queued = store.Claim(new OperationReceipt(true, id, id, "queued", "mkdir"),
                    new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(fixture.Path, id) }, id), true);
                var running = queued with { Receipt = queued.Receipt with { State = "running" } };
                var terminal = queued with { Receipt = queued.Receipt with { State = "canceled", Ok = false, Err = "canceled" }, Request = null };
                Check(cancelFirst ? store.TryCancelQueued(terminal) && !store.TryStartQueued(running)
                    : store.TryStartQueued(running) && !store.TryCancelQueued(terminal), "Queued cancellation and start were not exclusive.");
                Check(store.Find(id)!.Receipt.State == (cancelFirst ? "canceled" : "running"), "The losing transition overwrote the durable state.");
            }
        }
        var brokerRoot = fixture.Dir("broker-health");
        var owner = LocalIdentity.Current();
        await using (var broker = new AuthenticatedServiceBroker(brokerRoot, owner.Sid, new RootCodeVerifier(Path.Combine(brokerRoot, "root.verifier"))))
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(brokerRoot, "agentops-router.db")};Pooling=False");
            connection.Open();
            using (var transaction = connection.BeginTransaction())
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                // Poison bodies prove that a status count never reads or deserializes
                // pending contents. No dispatch occurs in this isolated fixture.
                insert.CommandText = "INSERT INTO routed_jobs(jid,rid,seq,state,receipt,request,retry_request,owner_sid,owner_session,generation) VALUES($id,$id,$seq,'waiting_user','not-json','not-json',NULL,$sid,0,0)";
                insert.Parameters.Add("$id", SqliteType.Text);
                insert.Parameters.Add("$seq", SqliteType.Integer);
                insert.Parameters.AddWithValue("$sid", owner.Sid);
                for (var index = 1; index <= 1000; index++)
                {
                    insert.Parameters["$id"].Value = "waiting-" + index;
                    insert.Parameters["$seq"].Value = index;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            foreach (var request in new[] { Request("diag"), Request("queue", ("op", "stat")) })
            {
                var receipt = await broker.HandleUserAsync(request, owner, CancellationToken.None);
                Check(receipt.Ok && Convert.ToInt64(receipt.Data!["queued"]) == 1000, "Service health hydrated waiting job bodies or returned the wrong count.");
            }
        }
    }

    private static OperationRequest Request(string operation, params (string Key, string Value)[] arguments) =>
        new(operation, arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static long Count(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM jobs";
        return (long)query.ExecuteScalar()!;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
