using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class DeferredSubmissionEngineTests
{
    // Real current-user executable fixtures against isolated state. The exact
    // file gate proves a job remains active; elapsed time is only a test deadline.
    internal static async Task RunAsync()
    {
        using var fixture = await GateFixture.CreateAsync();
        await using var engine = new AgentOpsEngine(fixture.State);
        var work = fixture.Work("admitted");
        var request = fixture.Request("accepted-owner", work);
        using var caller = new CancellationTokenSource();
        var accepted = await engine.ExecuteAsync(request, caller.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Check(accepted.Ok && accepted.State is "queued" or "running", "Deferred submission did not return admission.");
        await fixture.EnteredAsync(work);
        caller.Cancel();
        var replay = await engine.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(3));
        Check(replay.Jid == accepted.Jid && Rows(fixture.State, request.Rid!) == 1 && fixture.Launches(work) == 1,
            "Same-RID admission started another process or created another row.");
        Check(await StateAsync(engine, accepted.Jid) == "running", "Caller cancellation canceled accepted engine-owned work.");

        using (var canceledWait = new CancellationTokenSource())
        {
            var waiting = engine.ExecuteAsync(Job("wait", accepted.Jid), canceledWait.Token);
            Check(!waiting.IsCompleted, "job_wait returned before the gated process completed.");
            Check((await engine.ExecuteAsync(Request("diag")).WaitAsync(TimeSpan.FromSeconds(3))).Ok,
                "Diagnostics waited behind an unrelated long process.");
            Check((await engine.ExecuteAsync(Job("log", accepted.Jid)).WaitAsync(TimeSpan.FromSeconds(3))).Ok,
                "A bounded exact log request waited behind its running process.");
            canceledWait.Cancel();
            await ExpectCancellationAsync(waiting);
            Check(await StateAsync(engine, accepted.Jid) == "running", "Canceling a wait canceled the job being observed.");
        }
        fixture.Release(work);
        var done = await engine.ExecuteAsync(Job("wait", accepted.Jid)).WaitAsync(TimeSpan.FromSeconds(10));
        Check(done.Ok && done.State == "done" && done.Jid == accepted.Jid, "Accepted job did not complete under the same identity.");
        var terminalLog = await engine.ExecuteAsync(Job("log", accepted.Jid));
        Check(terminalLog.Ok && terminalLog.Data!.GetValueOrDefault("out") is string output &&
            output.Contains("fixture gate released", StringComparison.Ordinal), "Terminal output was not retained for exact job_log.");
        var terminalReplay = await engine.ExecuteAsync(request);
        Check(terminalReplay.Jid == accepted.Jid && terminalReplay.State == "done" && fixture.Launches(work) == 1,
            "Terminal RID replay re-executed work.");

        await DisconnectAsync(fixture, engine);
        await CancelExactAsync(fixture, engine);
        await RejectRawCompletionModeAsync(fixture, engine);
        Console.WriteLine("EVIDENCE isolated real PowerShell file-gated engine admission, caller/pipe disconnect independence, RID replay, exact job controls, raw completion-mode rejection; no installed state touched.");
    }

    internal static async Task CapacityAsync()
    {
        using var fixture = await GateFixture.CreateAsync();
        await using var engine = new AgentOpsEngine(fixture.State);
        var work = fixture.Work("capacity-gate");
        var gate = await engine.ExecuteAsync(fixture.Request("capacity-owner", work));
        await fixture.EnteredAsync(work);
        var health = await engine.ExecuteAsync(Request("diag"));
        var maximum = Convert.ToInt32(health.Data!["queue_max_pending"]);
        Check(maximum is >= 1 and <= 128, "Unexpected bounded admission window.");
        var requests = new List<OperationRequest>();
        for (var index = 0; index < maximum; index++)
        {
            var request = Write(fixture, "capacity-" + index, "bounded fixture " + index);
            var admitted = await engine.ExecuteAsync(request);
            Check(admitted.Ok && admitted.State == "queued", "Admission failed before the bounded window filled.");
            requests.Add(request);
        }
        var overflow = Write(fixture, "capacity-overflow", "admit only after capacity");
        var rejected = await engine.ExecuteAsync(overflow);
        Check(!rejected.Ok && rejected.Err == "queue_full" && rejected.ExitCode == 3 &&
            Rows(fixture.State, overflow.Rid!) == 0 && !File.Exists(overflow.Arg["dst"]),
            "Saturation left an orphan durable row or executed rejected work.");
        var sameRid = await engine.ExecuteAsync(requests[0]);
        Check(sameRid.Ok && Rows(fixture.State, requests[0].Rid!) == 1, "A full queue rejected an already accepted RID.");
        var restoredCapacity = await engine.ExecuteAsync(Job("cancel", sameRid.Jid));
        Check(restoredCapacity.Ok, "Exact queued cancellation failed during saturation.");
        await WaitUntilAsync(async () => Convert.ToInt32((await engine.ExecuteAsync(Request("diag"))).Data!["queue_pending"]) < maximum,
            "Canceled pending job did not release admission capacity.");
        var retried = await engine.ExecuteAsync(overflow);
        Check(retried.Ok && retried.State == "queued" && Rows(fixture.State, overflow.Rid!) == 1,
            "Rejected RID could not be safely admitted after capacity was available.");
        fixture.Release(work);
        Check((await engine.ExecuteAsync(Job("wait", gate.Jid)).WaitAsync(TimeSpan.FromSeconds(10))).Ok,
            "Capacity fixture gate did not complete.");
        Check((await engine.ExecuteAsync(Job("wait", retried.Jid)).WaitAsync(TimeSpan.FromSeconds(10))).Ok &&
            File.ReadAllText(overflow.Arg["dst"]) == overflow.Body, "Accepted capacity retry lost exact body or completion.");
        Console.WriteLine($"EVIDENCE bounded pending window={maximum}, no row for rejected RID, identical replay while full, exact cancel/capacity reuse.");
    }

    internal static async Task RecoveryAsync()
    {
        using var fixture = await GateFixture.CreateAsync();
        var engine = new AgentOpsEngine(fixture.State);
        var work = fixture.Work("restart-gate");
        var pendingPath = Path.Combine(fixture.Root, "durable-after-restart");
        var durable = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = pendingPath }, "legacy-sync-replay");
        var ephemeral = Write(fixture, "ephemeral-recovery", "unique ephemeral Ω body " + Guid.NewGuid().ToString("N"));
        Task<OperationReceipt>? pending = null;
        string acceptedBodyJid = string.Empty;
        try
        {
            await engine.ExecuteAsync(fixture.Request("restart-running", work));
            await fixture.EnteredAsync(work);
            pending = engine.ExecuteAsync(durable);
            await WaitUntilAsync(() => Task.FromResult(Rows(fixture.State, durable.Rid!) == 1), "Synchronous queued job was not durable.");
            var bodyReceipt = await engine.ExecuteAsync(ephemeral);
            acceptedBodyJid = bodyReceipt.Jid;
            Check(bodyReceipt.Ok && bodyReceipt.State == "queued" && !pending.IsCompleted,
                "Legacy synchronous job or ephemeral write escaped the known conflict.");
            Check(EphemeralWasOmitted(fixture.State, ephemeral),
                "An accepted ephemeral body was persisted in queued request content.");
        }
        finally
        {
            await engine.DisposeAsync();
            if (pending is not null) await ExpectCancellationAsync(pending);
        }
        Check(!Directory.Exists(pendingPath) && !File.Exists(ephemeral.Arg["dst"]),
            "Disposal executed never-started queued work.");
        Check(StoredState(fixture.State, durable.Rid!) == "queued" &&
            StoredState(fixture.State, ephemeral.Rid!) == "queued", "Disposal destroyed durable replay eligibility.");

        await using var reopened = new AgentOpsEngine(fixture.State);
        var restored = await reopened.ExecuteAsync(durable).WaitAsync(TimeSpan.FromSeconds(10));
        Check(restored.Ok && restored.State == "done" && Directory.Exists(pendingPath) && Rows(fixture.State, durable.Rid!) == 1,
            "Legacy synchronous queued request failed next-start replay.");
        var omitted = await reopened.ExecuteAsync(ephemeral).WaitAsync(TimeSpan.FromSeconds(3));
        Check(!omitted.Ok && omitted.State == "recovery_required" && omitted.Jid == acceptedBodyJid &&
            !File.Exists(ephemeral.Arg["dst"]), "Restart replayed an omitted ephemeral body or lost its exact identity.");
        Console.WriteLine("EVIDENCE disposal preserves never-started durable queue, cancels owned running process, restart replays safe request and quarantines omitted ephemeral body.");
    }

    private static async Task DisconnectAsync(GateFixture fixture, AgentOpsEngine engine)
    {
        var work = fixture.Work("disconnect");
        var request = fixture.Request("pipe-disconnect-owner", work);
        var owner = LocalIdentity.Current();
        var pipeName = "pao-engine-deferred-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var host = PipeHost.RunAsync(pipeName, engine, stop.Token);
        try
        {
            OperationReceipt accepted;
            // The request pipe closes immediately after receipt admission.
            await using (var dispatcher = new NamedPipeDispatcher(pipeName, expectedServer: owner))
                accepted = await dispatcher.DispatchAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            Check(accepted.Ok, "Isolated pipe admission failed.");
            await fixture.EnteredAsync(work);
            Check(await StateAsync(engine, accepted.Jid) == "running", "Accepted work died with its submission pipe.");
            fixture.Release(work);
            Check((await engine.ExecuteAsync(Job("wait", accepted.Jid)).WaitAsync(TimeSpan.FromSeconds(10))).Ok &&
                fixture.Launches(work) == 1, "Disconnected accepted job did not complete exactly once.");
        }
        finally
        {
            fixture.Release(work);
            stop.Cancel();
            try { await host; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task CancelExactAsync(GateFixture fixture, AgentOpsEngine engine)
    {
        var work = fixture.Work("cancel-running");
        var running = await engine.ExecuteAsync(fixture.Request("exact-cancel-running", work));
        await fixture.EnteredAsync(work);
        var queued = Write(fixture, "exact-cancel-queued", "must never execute");
        var queuedReceipt = await engine.ExecuteAsync(queued);
        Check(queuedReceipt.State == "queued", "Conflicting fixture unexpectedly executed before its parent gate.");
        var queuedCancel = await engine.ExecuteAsync(Job("cancel", queuedReceipt.Jid));
        Check(queuedCancel.Ok && await StateAsync(engine, queuedReceipt.Jid) == "canceled" && !File.Exists(queued.Arg["dst"]),
            "Exact queued cancellation executed or failed to cancel its target.");
        Check(await StateAsync(engine, running.Jid) == "running", "Canceling a queued job canceled another job.");
        var foreign = await engine.ExecuteAsync(Job("cancel", "missing-jid"));
        Check(!foreign.Ok && foreign.Err == "not_found", "Unowned/unknown job identity was adopted for cancellation.");
        var runningCancel = await engine.ExecuteAsync(Job("cancel", running.Jid));
        Check(runningCancel.Ok && runningCancel.Data!.GetValueOrDefault("cancel_requested") is true,
            "Running cancellation did not report the request disposition.");
        var terminal = await engine.ExecuteAsync(Job("wait", running.Jid)).WaitAsync(TimeSpan.FromSeconds(10));
        Check(!terminal.Ok && terminal.State is "canceled" or "failed" or "attention" or "recovery_required",
            "Running cancel did not release its owned process or preserve terminal failure disposition.");
        Check(!File.Exists(queued.Arg["dst"]) && fixture.Launches(work) == 1,
            "Cancellation executed queued work or duplicated the running process.");
    }

    private static async Task RejectRawCompletionModeAsync(GateFixture fixture, AgentOpsEngine engine)
    {
        foreach (var value in new[] { "False", "0", "null", "", "false ", "oops" })
        {
            var request = Write(fixture, "rawwait-" + Guid.NewGuid().ToString("N"), "rejected") with
            { Arg = new Dictionary<string, string> { ["dst"] = Path.Combine(fixture.Root, "rawwait-never"), ["wait"] = value } };
            var rejected = await engine.ExecuteAsync(request);
            Check(!rejected.Ok && rejected.Err == "invalid" && Rows(fixture.State, request.Rid!) == 0,
                "Malformed raw completion mode was admitted or persisted.");
        }
        var readOnly = new OperationRequest("stat", new Dictionary<string, string>
        { ["src"] = fixture.Script, ["wait"] = "false" }, "readonly-deferred-rejected");
        var result = await engine.ExecuteAsync(readOnly);
        Check(!result.Ok && result.Err == "invalid" && Rows(fixture.State, readOnly.Rid!) == 0,
            "Read-only raw request accepted a deferred completion field.");
    }

    private static OperationRequest Write(GateFixture fixture, string rid, string body) =>
        new("wr", new Dictionary<string, string> { ["dst"] = Path.Combine(fixture.Root, rid + ".txt"), ["wait"] = "false" }, rid, Body: body);
    private static OperationRequest Request(string op) => new(op, new Dictionary<string, string>());
    private static OperationRequest Job(string op, string jid) =>
        new("job", new Dictionary<string, string> { ["op"] = op, ["jid"] = jid });
    private static async Task<string> StateAsync(AgentOpsEngine engine, string jid)
    {
        var receipt = await engine.ExecuteAsync(Job("stat", jid)).WaitAsync(TimeSpan.FromSeconds(3));
        Check(receipt.Ok && receipt.Data!["rec"] is OperationReceipt, "Exact job_stat failed.");
        return ((OperationReceipt)receipt.Data!["rec"]!).State;
    }

    private static long Rows(string root, string rid) => Convert.ToInt64(Query(root,
        "SELECT COUNT(*) FROM jobs WHERE rid=$rid", rid));
    private static string StoredRequest(string root, string rid) => Convert.ToString(Query(root,
        "SELECT request FROM jobs WHERE rid=$rid", rid)) ?? string.Empty;
    private static string StoredState(string root, string rid) => Convert.ToString(Query(root,
        "SELECT state FROM jobs WHERE rid=$rid", rid)) ?? string.Empty;
    private static bool EphemeralWasOmitted(string root, OperationRequest request)
    {
        using var document = JsonDocument.Parse(StoredRequest(root, request.Rid!));
        var record = document.RootElement;
        var arguments = record.GetProperty("arg");
        return !record.TryGetProperty("body", out _) &&
            arguments.GetProperty(OperationRequest.BodyOmittedKey).GetString() == "1" &&
            arguments.EnumerateObject().All(field => field.Value.GetString() != request.Body);
    }
    private static object? Query(string root, string sql, string rid)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "agentops.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        query.Parameters.AddWithValue("$rid", rid);
        return query.ExecuteScalar();
    }

    private static async Task ExpectCancellationAsync(Task<OperationReceipt> task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Canceled waiter returned normally."); }
        catch (OperationCanceledException) { }
    }
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(failure);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class GateFixture : IDisposable
    {
        private readonly TempRoot _root = new();
        internal string Root => _root.Path;
        internal string State { get; }
        internal string Script { get; }
        private GateFixture()
        {
            State = _root.Dir("engine");
            Script = Path.Combine(Root, "gate.ps1");
        }
        internal static async Task<GateFixture> CreateAsync()
        {
            var fixture = new GateFixture();
            try
            {
                await File.WriteAllTextAsync(fixture.Script, """
                    param([string]$Directory)
                    $ErrorActionPreference = 'Stop'
                    $FixtureDirectory = [System.IO.Path]::GetFullPath($Directory)
                    if (-not [System.IO.File]::Exists([System.IO.Path]::Combine($FixtureDirectory, 'fixture.marker'))) { throw 'Wrong fixture directory' }
                    [System.IO.File]::AppendAllText([System.IO.Path]::Combine($FixtureDirectory, 'launches.txt'), "1`n")
                    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($FixtureDirectory, 'entered.marker'), 'entered')
                    [Console]::Out.WriteLine('fixture gate entered')
                    while (-not [System.IO.File]::Exists([System.IO.Path]::Combine($FixtureDirectory, 'release.marker'))) { [System.Threading.Thread]::Sleep(20) }
                    [Console]::Out.WriteLine('fixture gate released')
                    """, new UTF8Encoding(false));
                await PowerShell7ToolBootstrap.EnsureAsync(fixture.State);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal string Work(string name)
        {
            var directory = _root.Dir(name);
            File.WriteAllText(Path.Combine(directory, "fixture.marker"), "owned fixture");
            return directory;
        }
        internal OperationRequest Request(string rid, string work) => new("pwsh", new Dictionary<string, string>
        { ["src"] = Script, ["arg0"] = work, ["wait"] = "false", ["tmo"] = "30" }, rid, Cwd: work);
        internal Task EnteredAsync(string work) => WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Combine(work, "entered.marker"))),
            "The admitted exact fixture did not publish its entered marker.");
        internal void Release(string work) => File.WriteAllText(Path.Combine(work, "release.marker"), "release");
        internal int Launches(string work) => File.Exists(Path.Combine(work, "launches.txt"))
            ? File.ReadAllLines(Path.Combine(work, "launches.txt")).Length : 0;
        public void Dispose() => _root.Dispose();
    }
}
