using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Uses the real Engine, scheduler and an owned SQLite store. File completion is
// injected so cancellation can be held at an exact boundary without live data.
internal static class CopyJobWiringTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync()
    {
        await DeferredCancellationAsync();
        await CancellationBeforeEffectsAsync();
        await DisposalJoinsOriginalAsync();
        await PartialFailureAsync();
        Console.WriteLine("PASS copy job wiring: deferred client disconnect, explicit cancellation, live progress, retained ownership/Exit fence, partial failure, durable RID replay and disposal join (owned SQLite/injected file completion)");
    }

    private static async Task DeferredCancellationAsync()
    {
        await using var fixture = new Fixture(effects: true);
        using var caller = new CancellationTokenSource();
        var accepted = await fixture.Engine.ExecuteAsync(fixture.Request(), caller.Token);
        Check(accepted.Ok && accepted.State is "queued" or "running", "deferred copy was not admitted");
        await fixture.Transfer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        Check(!fixture.Transfer.CancelRequested.Task.IsCompleted,
            "client cancellation owned the admitted background copy");

        var before = await StatAsync(fixture.Engine, accepted.Jid);
        Check(before.GetProperty("rec").GetProperty("state").GetString() == "running" &&
            before.GetProperty("progress").GetProperty("bytes_transferred").GetInt64() == 64 &&
            before.GetProperty("progress").GetProperty("cp").GetInt64() == 0,
            "job_stat lost live byte progress or fabricated a completed file");
        var original = before.GetProperty("rec").GetRawText();
        Check(!before.GetProperty("rec").GetProperty("data").TryGetProperty("bytes_transferred", out _),
            "advisory chunk observations overwrote the retained receipt");

        var cancel = await fixture.Engine.ExecuteAsync(Job("cancel", accepted.Jid));
        await fixture.Transfer.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(cancel.Ok && JsonSerializer.SerializeToElement(cancel.Data).GetProperty("cancel_requested").GetBoolean(),
            "job_cancel did not request cancellation of the existing worker");
        var canceled = await StatAsync(fixture.Engine, accepted.Jid);
        Check(canceled.GetProperty("rec").GetRawText() == original &&
            canceled.GetProperty("progress").GetProperty("cancellation_requested").GetBoolean(),
            "cancellation fabricated a terminal receipt before the original copy returned");
        var waiting = fixture.Engine.ExecuteAsync(Job("wait", accepted.Jid));
        using (fixture.Admission.TryClose() ?? throw new InvalidOperationException("admission fence failed"))
        {
            Check(!waiting.IsCompleted && await fixture.Engine.UserExitBlockerAsync(CancellationToken.None) == "work_active",
                "copy cancellation released the original callback or Exit guard");
            fixture.Transfer.Release();
            var terminal = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!terminal.Ok && terminal.State == "attention" && terminal.Partial && terminal.Err == "canceled",
                "an interrupted destination was reported as untouched or successful");
            var data = JsonSerializer.SerializeToElement(terminal.Data);
            Check(data.GetProperty("bytes_transferred").GetInt64() == 64 &&
                data.GetProperty("destination_may_have_changed").GetBoolean() &&
                !data.GetProperty("source_deletion_issued").GetBoolean(),
                "terminal receipt lost observed progress or source-deletion semantics");
        }
        var final = await StatAsync(fixture.Engine, accepted.Jid);
        Check(!final.TryGetProperty("progress", out _) &&
            final.GetProperty("rec").GetProperty("partial").GetBoolean(),
            "terminal copy retained a live progress slot or lost partial status");
        var replay = await fixture.Engine.ExecuteAsync(fixture.Request());
        Check(replay.Jid == accepted.Jid && fixture.Transfer.Calls == 1,
            "same-RID observation executed the interrupted copy again");
        Check(File.ReadAllText(fixture.Source) == "owned copy fixture", "copy deleted or changed its source");
        using var db = fixture.OpenDatabase();
        using var count = db.CreateCommand();
        // The actual schema stores operations inside receipts. Count all rows:
        // passive stat/cancel/wait must not append a durable row for each poll.
        count.CommandText = "SELECT COUNT(*) FROM jobs";
        Check(Convert.ToInt64(count.ExecuteScalar()) == 1, "progress observation created durable history rows");
    }

    private static async Task CancellationBeforeEffectsAsync()
    {
        await using var fixture = new Fixture(effects: false);
        var accepted = await fixture.Engine.ExecuteAsync(fixture.Request());
        await fixture.Transfer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Engine.ExecuteAsync(Job("cancel", accepted.Jid));
        await fixture.Transfer.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = fixture.Engine.ExecuteAsync(Job("wait", accepted.Jid));
        Check(!waiting.IsCompleted, "unissued cancellation completed before the provider returned");
        fixture.Transfer.Release();
        var terminal = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!terminal.Ok && terminal.Err == "canceled" && terminal.State == "canceled" && !terminal.Partial &&
            JsonSerializer.SerializeToElement(terminal.Data).GetProperty("bytes_transferred").GetInt64() == 0 &&
            !File.Exists(fixture.Destination), "pre-effect cancellation reported destination effects");
    }

    private static async Task DisposalJoinsOriginalAsync()
    {
        await using var fixture = new Fixture(effects: true);
        var accepted = await fixture.Engine.ExecuteAsync(fixture.Request());
        await fixture.Transfer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = fixture.StopEngineAsync();
        await fixture.Transfer.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!disposal.IsCompleted, "Engine disposal closed resources beneath the original copy");
        fixture.Transfer.Release();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        using var db = fixture.OpenDatabase();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT receipt FROM jobs WHERE jid=$jid"; read.Parameters.AddWithValue("$jid", accepted.Jid);
        var terminal = JsonSerializer.Deserialize<OperationReceipt>((string)read.ExecuteScalar()!, WireJson)!;
        Check(!terminal.Ok && terminal.State == "attention" && terminal.Partial && fixture.Transfer.Calls == 1,
            "shutdown lost the interrupted copy's actual terminal receipt");
    }

    private static async Task PartialFailureAsync()
    {
        await using var fixture = new Fixture(effects: true, fail: true);
        var accepted = await fixture.Engine.ExecuteAsync(fixture.Request());
        await fixture.Transfer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = fixture.Engine.ExecuteAsync(Job("wait", accepted.Jid));
        fixture.Transfer.Release();
        var terminal = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!terminal.Ok && terminal.State == "attention" && terminal.Partial && terminal.Err == "access" &&
            JsonSerializer.SerializeToElement(terminal.Data).GetProperty("native_error").GetInt32() == 5,
            "native partial failure lost its failure code/effect evidence");
        Check((await fixture.Engine.ExecuteAsync(fixture.Request())).Jid == accepted.Jid && fixture.Transfer.Calls == 1,
            "failed background copy was automatically retried");
    }

    private static OperationRequest Job(string op, string jid) => new("job",
        new Dictionary<string, string> { ["op"] = op, ["jid"] = jid });
    private static async Task<JsonElement> StatAsync(AgentOpsEngine engine, string jid)
    {
        var result = await engine.ExecuteAsync(Job("stat", jid));
        Check(result.Ok, "job_stat failed");
        return JsonSerializer.SerializeToElement(result.Data, WireJson);
    }

    private sealed class ControlledTransfer(bool effects, bool fail) : ICopyFileTransfer
    {
        internal int Calls;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancelRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public CopyFileTransferOutcome Copy(string source, string destination, bool overwrite,
            CancellationToken token, Action<long, long>? progress)
        {
            Interlocked.Increment(ref Calls);
            using var registration = token.Register(() => CancelRequested.TrySetResult());
            if (effects) progress?.Invoke(128, 64);
            Entered.TrySetResult();
            _release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (fail) return new(false, false, effects, effects ? 64 : 0, 128, 5, "access", effects);
            return new(false, token.IsCancellationRequested, effects, effects ? 64 : 0, 128,
                token.IsCancellationRequested ? 1235 : null, token.IsCancellationRequested ? "canceled" : "copy_failed", effects);
        }
    }

    private sealed class Mode : IMaintenanceModeSource
    {
        public MaintenanceModeRecord? Read() => null;
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("cortex-copy-wiring-").FullName;
        internal readonly UserHostAdmission Admission = new();
        internal readonly ControlledTransfer Transfer;
        internal readonly AgentOpsEngine Engine;
        internal readonly string Source;
        internal readonly string Destination;
        private Task? _disposal;
        internal Fixture(bool effects, bool fail = false)
        {
            Source = Path.Combine(Root, "source.txt"); File.WriteAllText(Source, "owned copy fixture");
            Destination = Path.Combine(Root, "destination.txt");
            Transfer = new(effects, fail);
            Engine = new(Path.Combine(Root, "state"), null, false, null, false, null,
                maintenanceMode: new Mode(), userHostAdmission: Admission, copyFileTransfer: Transfer);
        }
        internal OperationRequest Request() => new("cp", new Dictionary<string, string>
            { ["src"] = Source, ["dst"] = Destination, ["pol"] = "replace", ["wait"] = "false" }, "owned-copy-rid");
        internal SqliteConnection OpenDatabase()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Root, "state", "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open(); return db;
        }
        internal Task StopEngineAsync() => _disposal ??= Engine.DisposeAsync().AsTask();
        public async ValueTask DisposeAsync()
        {
            Transfer.Release();
            await StopEngineAsync();
            var root = Path.GetFullPath(Root);
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cortex-copy-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(root, recursive: true);
        }
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
}
