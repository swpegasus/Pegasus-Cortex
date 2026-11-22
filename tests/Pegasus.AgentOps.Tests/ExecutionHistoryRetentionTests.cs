using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Actual owned SQLite + synthetic receipts. The sole Engine case uses only
// owned temporary directories; no installed runtime, service or live DB.
internal static class ExecutionHistoryRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync()
    {
        Check(ExecutionHistoryRetention.Default is { BatchSize: 256, CompletedRowTarget: 10_000, UsedPageTriggerBytes: 64L * 1024 * 1024 },
            "retention defaults match the bounded pass/count/size policy");
        AgeAndSequenceAnchor();
        CountPressureAndRecentWindow();
        SizePressureDoesNotRefuseClaims();
        ActiveAndUncertainPreservation();
        BoundedCursorAndRollback();
        await EngineReplayAndMissingHistoryAsync();
    }

    private static void AgeAndSequenceAnchor()
    {
        using var fixture = new Fixture();
        fixture.Add("expired", "done", Now.AddDays(-31));
        fixture.Add("boundary", "done", Now.AddDays(-30));
        fixture.Add("anchor", "done", Now.AddDays(-31));
        var result = fixture.Store.RetainCompletedHistory(Now, 0, (_, _, _) => false);
        Check(!result.Pressure && result.Deleted == 1 && result.PreservedNewest == 1 && result.PreservedRecent == 1 &&
            fixture.Store.Find("expired") is null && fixture.Store.Find("boundary") is not null && fixture.Store.Find("anchor") is not null,
            "age expires only strictly old completed records and preserves newest sequence anchor");
        var oldSequence = fixture.Store.Find("anchor")!.Sequence;
        var next = fixture.Add("after-expiry", "done", Now);
        Check(next.Sequence > oldSequence, "history expiration never recycles public sequence cursors");
    }

    private static void CountPressureAndRecentWindow()
    {
        using var fixture = new Fixture();
        fixture.Add("older-day", "done", Now.AddHours(-24).AddTicks(-1));
        fixture.Add("day-boundary", "failed", Now.AddHours(-24));
        fixture.Add("fresh", "canceled", Now.AddHours(-3));
        fixture.Add("anchor", "done", Now);
        var policy = ExecutionHistoryRetention.Default with { CompletedRowTarget = 2, UsedPageTriggerBytes = long.MaxValue };
        var result = fixture.Store.RetainCompletedHistory(Now, 0, (_, _, _) => false, policy);
        Check(result.Pressure && result.CompletedRowsAtLeast == 3 && result.Deleted == 1 &&
            fixture.Store.Find("older-day") is null && fixture.Store.Find("day-boundary") is not null && fixture.Store.Find("fresh") is not null,
            "bounded count probe triggers oldest cleanup while protecting the recent 24-hour replay window");
        var recent = fixture.Store.Find("fresh")!;
        var replay = fixture.Store.Claim(new(true, "must-not-replace", recent.Receipt.Rid, "queued", "mkdir"),
            new("mkdir", new Dictionary<string, string> { ["dst"] = fixture.Root }, recent.Receipt.Rid), true);
        Check(replay.Receipt.Jid == recent.Receipt.Jid && replay.Sequence == recent.Sequence,
            "recent immutable RID replay survives size/count pressure");
    }

    private static void SizePressureDoesNotRefuseClaims()
    {
        using var fixture = new Fixture();
        fixture.Add("large-old", "done", Now.AddDays(-2), output: new string('x', 16 * 1024));
        fixture.Add("recent", "done", Now.AddHours(-1));
        fixture.Add("anchor", "done", Now);
        var policy = ExecutionHistoryRetention.Default with { UsedPageTriggerBytes = 1, CompletedRowTarget = 1000 };
        var result = fixture.Store.RetainCompletedHistory(Now, 0, (_, _, _) => false, policy);
        Check(result.Pressure && result.UsedPageBytes > 1 && result.Deleted == 1 && fixture.Store.Find("recent") is not null,
            "used-page size trigger expires eligible old detail without deleting recent history");
        Check(fixture.Add("new-work", "queued", Now).Receipt.State == "queued",
            "soft retention targets never become a new admission quota");
        // SQLite may keep allocated pages for reuse. This fixture deliberately
        // makes no assertion that deleting rows shrinks the database file.
    }

    private static void ActiveAndUncertainPreservation()
    {
        using var fixture = new Fixture();
        foreach (var state in new[] { "queued", "running", "attention", "recovery_required" })
            fixture.Add(state, state, Now.AddDays(-90));
        fixture.Add("active-done", "done", Now.AddDays(-90));
        fixture.Add("partial-failed", "failed", Now.AddDays(-90), partial: true);
        fixture.Add("unknown-failed", "failed", Now.AddDays(-90), unknown: true);
        fixture.Add("discarded", "attention", Now.AddDays(-90));
        fixture.Add("known-failed", "failed", Now.AddDays(-90));
        fixture.Add("anchor", "done", Now);
        fixture.Store.PruneHistory(Now.AddDays(-30), 256, true, (jid, _, _) => jid != "discarded", Now);
        fixture.Age("discarded", Now.AddDays(-31));
        var result = fixture.Store.RetainCompletedHistory(Now, 0, (jid, _, _) => jid == "active-done");
        Check(result.Deleted == 2 && result.PreservedActive == 1 && result.PreservedUncertain == 2 &&
            fixture.Store.Find("discarded") is null && fixture.Store.Find("known-failed") is null,
            "only explicit discard makes unresolved history eligible; inactive known failure may expire");
        foreach (var name in new[] { "queued", "running", "attention", "recovery_required", "active-done", "partial-failed", "unknown-failed" })
            Check(fixture.Store.Find(name) is not null, "retention changed active or unacknowledged uncertain work");
    }

    private static void BoundedCursorAndRollback()
    {
        using (var fixture = new Fixture())
        {
            fixture.Add("active", "done", Now.AddDays(-90));
            fixture.Add("invalid", "failed", Now.AddDays(-90));
            fixture.RawReceipt("invalid", "{bad");
            fixture.Add("old-one", "done", Now.AddDays(-90));
            fixture.Add("old-two", "done", Now.AddDays(-90));
            fixture.Add("anchor", "done", Now);
            var policy = ExecutionHistoryRetention.Default with { BatchSize = 2 };
            var first = fixture.Store.RetainCompletedHistory(Now, 0, (jid, _, _) => jid == "active", policy);
            Check(first.Examined == 2 && first.Deleted == 0 && first.PreservedActive == 1 && first.PreservedInvalid == 1 && first.More,
                "one pass does not hydrate or exceed its bounded metadata page");
            var second = fixture.Store.RetainCompletedHistory(Now, first.NextSequence, (jid, _, _) => jid == "active", policy);
            Check(second.Examined == 2 && second.Deleted == 2 && fixture.Store.Find("old-one") is null && fixture.Store.Find("old-two") is null,
                "sequence cursor gets past protected/malformed old entries instead of starving later cleanup");
            var last = fixture.Store.RetainCompletedHistory(Now, second.NextSequence, (_, _, _) => false, policy);
            Check(last.Examined == 1 && last.PreservedNewest == 1 && !last.More && last.NextSequence == 0,
                "end of bounded history walk resets its cursor");
        }
        using (var fixture = new Fixture())
        {
            fixture.Add("one", "done", Now.AddDays(-90)); fixture.Add("two", "done", Now.AddDays(-90)); fixture.Add("anchor", "done", Now);
            try
            {
                fixture.Store.RetainCompletedHistory(Now, 0, (jid, _, _) => jid == "two" ? throw new IOException("fixture active inspection failed") : false);
                throw new InvalidOperationException("retention fixture expected failure");
            }
            catch (IOException) { }
            Check(fixture.Store.Find("one") is not null && fixture.Store.Find("two") is not null,
                "an incomplete retention pass rolls back its deletions rather than returning partial cleanup success");
        }
    }

    private static async Task EngineReplayAndMissingHistoryAsync()
    {
        using var temp = new TempRoot();
        var root = temp.Dir("retention-engine"); var oldOutput = temp.Dir("old-owned-result");
        File.WriteAllText(Path.Combine(oldOutput, "marker.txt"), "owned result survives history expiry");
        using (var store = new SqliteJobStore(root))
        {
            var old = store.Claim(new(true, "expired-job", "expired-rid", "done", "mkdir"),
                new("mkdir", new Dictionary<string, string> { ["dst"] = oldOutput }, "expired-rid"), true);
            store.Update(old with { Request = null });
            SetAge(root, "expired-job", DateTimeOffset.UtcNow.AddDays(-31));
        }
        await using var engine = new AgentOpsEngine(root, null, false, null, false, null,
            maintenanceMode: new OpenMode(), userHostAdmission: new UserHostAdmission());
        var request = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(temp.Path, "new-owned-result") }, "recent-rid");
        var original = await engine.ExecuteAsync(request);
        Check(original.Ok, "isolated Engine completion admitted its owned directory fixture");
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Jid == original.Jid && replay.Ok, "completion hook preserves recent exact RID replay");
        foreach (var action in new[] { "stat", "retry" })
        {
            var missing = await engine.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = action, ["jid"] = "expired-job" }, "missing-" + action));
            Check(!missing.Ok && missing.Err == "not_found", "expired history keeps existing not_found and never reconstructs or retries old work");
        }
        Check(File.ReadAllText(Path.Combine(oldOutput, "marker.txt")) == "owned result survives history expiry",
            "history expiration never deletes an operation's actual user output");
    }

    private static void SetAge(string root, string jid, DateTimeOffset updated)
    {
        using var connection = Connection(root); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET updated_utc=$utc WHERE jid=$jid";
        command.Parameters.AddWithValue("$utc", updated.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$jid", jid);
        Check(command.ExecuteNonQuery() == 1, "owned retention fixture row missing");
    }
    private static SqliteConnection Connection(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class Fixture : IDisposable
    {
        private readonly TempRoot _temp = new();
        internal string Root { get; }
        internal SqliteJobStore Store { get; }
        internal Fixture() { Root = _temp.Dir("retention"); Store = new(Root); }
        internal DurableJob Add(string name, string state, DateTimeOffset updated, string output = "owned synthetic detail", bool partial = false, bool unknown = false)
        {
            var failure = unknown ? FailureEvidence.Generic("fixture_unknown", false, "fixture") with
                { Execution = new(OperationExecutionState.Unknown, "fixture") } : null;
            var receipt = new OperationReceipt(state == "done", name, "rid-" + name, state, "mkdir", state == "done" ? null : "fixture",
                Data: new Dictionary<string, object?> { ["out"] = output }, Detail: output, Partial: partial, Failure: failure);
            var request = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = Root }, receipt.Rid);
            var saved = Store.Claim(receipt, request, true);
            Store.Update(saved with { Request = state is "queued" or "running" ? request : null });
            Age(name, updated); return saved;
        }
        internal void Age(string jid, DateTimeOffset updated) => SetAge(Root, jid, updated);
        internal void RawReceipt(string jid, string text)
        {
            using var connection = Connection(Root); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET receipt=$receipt WHERE jid=$jid";
            command.Parameters.AddWithValue("$receipt", text); command.Parameters.AddWithValue("$jid", jid); command.ExecuteNonQuery();
        }
        public void Dispose() { Store.Dispose(); _temp.Dispose(); }
    }
}
