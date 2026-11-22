using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class JobHistoryRetentionTests
{
    internal static async Task RunAsync()
    {
        SchemaAndBoundedPreview();
        DiscardPreservesOutcomeAndReplay();
        FailedBatchRollsBack();
        AutomaticPolicyIsBoundedAndDoesNotDiscard();
        ExactUtcCutoff();
        await OwnerHostAndApplicationReplayAsync();
        Console.WriteLine("PASS job history pruning (isolated SQLite, synthetic outcomes, no installed or business runtime effects)");
    }

    private static void SchemaAndBoundedPreview()
    {
        using var fixture = new TempRoot();
        var root = fixture.Dir("history");
        using (var old = new SqliteConnection("Data Source=" + Path.Combine(root, "agentops.db") + ";Pooling=False"))
        {
            old.Open();
            using var schema = old.CreateCommand();
            schema.CommandText = "CREATE TABLE jobs(jid TEXT PRIMARY KEY,rid TEXT NOT NULL UNIQUE,seq INTEGER NOT NULL UNIQUE,state TEXT NOT NULL,receipt TEXT NOT NULL,request TEXT NULL,mutation INTEGER NOT NULL,updated_utc TEXT NOT NULL)";
            schema.ExecuteNonQuery();
        }
        var now = DateTimeOffset.UtcNow;
        using (var store = new SqliteJobStore(root))
        {
            for (var index = 0; index < 4; index++) Add(store, root, "old-" + index, "attention", now.AddDays(-60), "private-history-output");
            for (var index = 0; index < 140; index++) Add(store, root, "new-" + index, "done", now, "new-output");
            var page = store.ListHistory("attention", now.AddDays(-30), 0, 2);
            Check(page.Jobs.Count == 2 && page.More && page.Jobs.All(job => job.Jid.StartsWith("old-", StringComparison.Ordinal)),
                "old attention preview was hidden behind newer history");
            Check(!JsonSerializer.Serialize(page).Contains("private-history-output", StringComparison.Ordinal),
                "preview exposed receipt output");
            var next = store.ListHistory("attention", now.AddDays(-30), page.Next, 2);
            Check(next.Jobs.Count == 2 && !next.More && !next.Jobs.Select(job => job.Jid).Intersect(page.Jobs.Select(job => job.Jid)).Any(),
                "history keyset repeated or skipped the selected old page");
        }
        using var reopened = new SqliteJobStore(root);
        Check(reopened.FindByRid("rid-old-0") is not null, "idempotent column migration lost an old RID");
    }

    private static void DiscardPreservesOutcomeAndReplay()
    {
        using var fixture = new TempRoot();
        var root = fixture.Dir("discard");
        var now = DateTimeOffset.UtcNow;
        using var store = new SqliteJobStore(root);
        var original = Add(store, root, "discarded", "attention", now.AddDays(-80), "sensitive-output");
        Add(store, root, "active", "recovery_required", now.AddDays(-80), "active-output");
        Add(store, root, "fresh", "attention", now, "fresh-output");
        Add(store, root, "queued", "queued", now.AddDays(-80), "queued-output");
        var result = store.PruneHistory(now.AddDays(-30), 256, true,
            (jid, _, _) => jid == "active", now);
        Check(result.Pruned == 1 && result.SkippedActive == 1, "discard changed active or fresh work");
        var saved = store.Find("discarded")!;
        Check(saved.Request is null && AgentOpsEngine.IsHistoryPruned(saved.Receipt) &&
            saved.Receipt.Ok == original.Receipt.Ok && saved.Receipt.State == original.Receipt.State &&
            saved.Receipt.Err == original.Receipt.Err && saved.Receipt.Partial == original.Receipt.Partial,
            "discard changed an original outcome or retained its request");
        Check(saved.Receipt.Failure?.Execution == original.Receipt.Failure?.Execution &&
            saved.Receipt.Failure?.Retry == original.Receipt.Failure?.Retry &&
            saved.Receipt.Data?.GetValueOrDefault("requestIdentity")?.ToString() == new string('a', 64) &&
            !JsonSerializer.Serialize(saved.Receipt).Contains("sensitive-output", StringComparison.Ordinal),
            "pruning lost no-replay/uncertainty metadata or retained payloads");
        Check(store.FindMaintenanceEffect("discarded") is null && store.FindMaintenanceEffect("active") is not null &&
            store.FindMaintenanceEffect("fresh") is not null && store.Find("queued")!.Receipt.State == "queued",
            "discard suppressed another blocker or changed queued work");
        var replay = store.Claim(new(true, "replacement", original.Receipt.Rid, "queued", "mkdir"),
            new("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(root, "must-not-run") }), true);
        Check(replay.Receipt.Jid == original.Receipt.Jid && AgentOpsEngine.IsHistoryPruned(replay.Receipt),
            "pruning removed the unique RID replay barrier");
        store.Update(original);
        Check(AgentOpsEngine.IsHistoryPruned(store.Find("discarded")!.Receipt), "a stale completion resurrected a pruned body");
        Check(store.PruneHistory(now.AddDays(-30), 256, true, (jid, _, _) => jid == "active", now).Pruned == 0,
            "repeated discard consumed fresh or previously discarded work");
    }

    private static void AutomaticPolicyIsBoundedAndDoesNotDiscard()
    {
        using var fixture = new TempRoot();
        var root = fixture.Dir("automatic");
        var now = DateTimeOffset.UtcNow;
        using var store = new SqliteJobStore(root);
        for (var index = 0; index < 132; index++) Add(store, root, "terminal-" + index, "done", now.AddDays(-31), "old-output");
        Add(store, root, "unknown", "recovery_required", now.AddDays(-100), "unresolved-output");
        Add(store, root, "new-terminal", "done", now.AddDays(-2), "recent-output");
        var failed = Add(store, root, "old-failed", "failed", now.AddDays(-31), "failure-output");
        var result = store.PruneHistory(now.AddDays(-30), 128, false, (_, _, _) => false, now);
        Check(result.Pruned == 128 && result.More, "automatic pass exceeded or failed its 128-row bound");
        var unknown = store.Find("unknown")!;
        Check(!AgentOpsEngine.IsHistoryPruned(unknown.Receipt) && store.FindMaintenanceEffect("unknown") is not null &&
            !AgentOpsEngine.IsHistoryPruned(store.Find("new-terminal")!.Receipt), "age automatically discarded uncertainty or recent history");
        var page = store.ListHistory("done", now.AddDays(-30), 0, 256);
        Check(page.Jobs.Count(job => job.Pruned) == 128 && page.Jobs.All(job => !job.Discarded),
            "automatic compaction forged an explicit discard marker");
        store.PruneHistory(now.AddDays(-30), 128, false, (_, _, _) => false, now);
        var retainedFailure = store.Find("old-failed")!.Receipt;
        Check(AgentOpsEngine.IsHistoryPruned(retainedFailure) && retainedFailure.Failure?.Execution == failed.Receipt.Failure?.Execution &&
            retainedFailure.Failure?.Retry == failed.Receipt.Failure?.Retry && retainedFailure.State == "failed",
            "terminal body retention reclassified failure uncertainty");
    }

    private static void FailedBatchRollsBack()
    {
        using var fixture = new TempRoot();
        var root = fixture.Dir("rollback");
        var now = DateTimeOffset.UtcNow;
        using var store = new SqliteJobStore(root);
        Add(store, root, "rollback-first", "attention", now.AddDays(-60), "first-output");
        Add(store, root, "rollback-second", "attention", now.AddDays(-60), "second-output");
        var observations = 0;
        try
        {
            store.PruneHistory(now.AddDays(-30), 10, true, (_, _, _) =>
                ++observations == 2 ? throw new InvalidOperationException("fixture observation failed") : false, now);
            throw new Exception("failed batch was accepted");
        }
        catch (InvalidOperationException error) when (error.Message == "fixture observation failed") { }
        Check(!AgentOpsEngine.IsHistoryPruned(store.Find("rollback-first")!.Receipt) &&
            !AgentOpsEngine.IsHistoryPruned(store.Find("rollback-second")!.Receipt) &&
            store.FindMaintenanceEffect("rollback-first") is not null,
            "a failed batch partially discarded original records");
    }

    private static void ExactUtcCutoff()
    {
        using var fixture = new TempRoot();
        var root = fixture.Dir("cutoff");
        using var store = new SqliteJobStore(root);
        var cutoff = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(500);
        Add(store, root, "older-z", "attention", cutoff.AddMilliseconds(-500), "old");
        SetAge(root, "older-z", "2020-01-02T00:00:00Z");
        Add(store, root, "exact", "attention", cutoff, "boundary");
        var page = store.ListHistory("attention", cutoff, 0, 10);
        Check(page.Jobs.Count == 1 && page.Jobs[0].Jid == "older-z", "UTC-Z/offset representation changed the strict cutoff");
        var result = store.PruneHistory(cutoff, 10, true, (_, _, _) => false, DateTimeOffset.UtcNow);
        Check(result.Pruned == 1 && !AgentOpsEngine.IsHistoryPruned(store.Find("exact")!.Receipt),
            "prune crossed the exact UTC cutoff");
    }

    private static async Task OwnerHostAndApplicationReplayAsync()
    {
        using var fixture = new TempRoot();
        var unboundRoot = fixture.Dir("unbound");
        await using (var unbound = Engine(unboundRoot, bound: false))
        {
            var refused = await unbound.ExecuteAsync(Prune("details"));
            Check(!refused.Ok && refused.Err == "auth", "an unbound/in-process engine gained owner history cleanup");
        }
        var root = fixture.Dir("app-history");
        var request = new OperationRequest("app", new Dictionary<string, string>
            { ["op"] = "start", ["cfg"] = "unused-fixture", ["name"] = "history-fixture" }, "app-history-rid");
        OperationReceipt original;
        await using (var engine = Engine(root, bound: true))
            original = await engine.ExecuteAsync(request); // No application manager or launch provider exists.
        using (var store = new SqliteJobStore(root))
        {
            var known = store.FindByRid(request.Rid!)!;
            var data = new Dictionary<string, object?>(known.Receipt.Data!)
            { ["aid"] = new string('b', 32), ["name"] = "history-fixture", ["out"] = "fixture-output" };
            store.Update(known with { Receipt = known.Receipt with { Ok = true, State = "done", Err = null, Data = data } });
            SetAge(root, known.Receipt.Jid, DateTimeOffset.UtcNow.AddDays(-60).ToString("O", CultureInfo.InvariantCulture));
        }
        await using (var engine = Engine(root, bound: true))
        {
            var prune = await engine.ExecuteAsync(Prune("details"));
            Check(prune.Ok, "bound owner history pruning failed");
            var replay = await engine.ExecuteAsync(request);
            Check(replay.Jid == original.Jid && replay.Ok && AgentOpsEngine.IsHistoryPruned(replay),
                "pruned successful application replay invoked its absent manager or lost identity");
            var invalid = await engine.ExecuteAsync(Prune("discard_unresolved") with
            { Arg = new Dictionary<string, string> { ["op"] = "prune", ["mode"] = "discard_unresolved", ["before"] = "2020-01-01T00:00:00+00:00", ["max"] = "1" } });
            Check(!invalid.Ok && invalid.Err == "invalid", "cleanup accepted a cutoff outside the fixed UTC-Z contract");
        }
    }

    private static AgentOpsEngine Engine(string root, bool bound) => new(root, null, false, null, false, null,
        maintenanceMode: new NoMaintenanceBarrier(), userHostAdmission: bound ? new UserHostAdmission() : null);

    private static OperationRequest Prune(string mode) => new("job", new Dictionary<string, string>
    {
        ["op"] = "prune", ["mode"] = mode, ["before"] = DateTimeOffset.UtcNow.AddDays(-30).UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ["max"] = "256"
    }, Guid.NewGuid().ToString("N"));

    private static DurableJob Add(SqliteJobStore store, string root, string jid, string state, DateTimeOffset updated, string payload)
    {
        var request = new OperationRequest("run", new Dictionary<string, string> { ["cfg"] = "never-executed-fixture" }, "rid-" + jid);
        var receipt = new OperationReceipt(state == "done", jid, request.Rid!, state, "run", state == "done" ? null : "fixture_uncertain",
            new Dictionary<string, object?> { ["out"] = payload, ["requestIdentity"] = new string('a', 64) },
            Detail: payload, Partial: state is "attention" or "recovery_required",
            Failure: state == "done" ? null : FailureEvidence.Generic("fixture_uncertain", false, "fixture"));
        var claimed = store.Claim(receipt, request, true);
        var saved = claimed with { Receipt = receipt with { Data = new Dictionary<string, object?>(receipt.Data!) { ["seq"] = claimed.Sequence } } };
        store.Update(saved);
        SetAge(root, jid, updated.ToString("O", CultureInfo.InvariantCulture));
        return saved;
    }

    private static void SetAge(string root, string jid, string updated)
    {
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "agentops.db") + ";Pooling=False");
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE jobs SET updated_utc=$utc WHERE jid=$jid";
        update.Parameters.AddWithValue("$utc", updated);
        update.Parameters.AddWithValue("$jid", jid);
        update.ExecuteNonQuery();
    }

    private sealed class NoMaintenanceBarrier : IMaintenanceModeSource
    {
        public MaintenanceModeRecord? Read() => null;
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }

    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
