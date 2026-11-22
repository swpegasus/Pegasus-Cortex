using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Public input routing and real owned SQLite with a fake Scheduler. No COM,
// installed configuration, native task control, process or credential action.
internal static class WindowsTaskWiringTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private const string TaskPath = WindowsTaskTests.EngineFixtureTaskPath;
    private static readonly string Sha = WindowsTaskTests.EngineFixtureTaskSha;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        await MetadataAsync();
        await OwnerAsync();
        await SystemAsync();
        await LostBodyAsync();
        Console.WriteLine("PASS scheduled-task wiring: captured CLI/MCP all actions, closed fields, owner/System admission, ephemeral reads, original worker/observer cancellation, one durable RID owner, compact unknown recovery/no replay; injected Scheduler +owned SQLite only");
    }

    private static async Task MetadataAsync()
    {
        var capture = new Capture();
        foreach (var action in WindowsTaskOperationContract.Actions)
        {
            var name = WindowsTaskOperationContract.PublicName(action);
            var help = PaoProgram.UnifiedMcpOperationHelp(name) ?? throw new Exception("task_help_missing");
            var example = help["mcp_example"]!.DeepClone().AsObject();
            using var document = JsonDocument.Parse(example.ToJsonString());
            var before = capture.Calls;
            await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, capture, default, defaultWs: "must-not-apply");
            Check(capture.Calls == before + 1 && capture.Last is { Op: "windows_task", Ws: null, Cwd: null, Root: null } &&
                capture.Last.Arg["op"] == action, "MCP did not actually dispatch selected action or leaked ambient context");
            _ = WindowsTaskOperationContract.Parse(capture.Last!);
            using var output = new StringWriter(); using var error = new StringWriter();
            var cli = new List<string> { "windows_task", action, "--stdin" };
            if (!WindowsTaskOperationContract.IsReadOnly(action)) cli.Add("rid=deliberate-task-change");
            before = capture.Calls;
            var exit = await PaoProgram.RunCliAsync(cli.ToArray(), capture, output, error, default,
                stdin: new StringReader(example["text"]!.GetValue<string>()));
            Check(exit == 0 && capture.Calls == before + 1 && capture.Last!.Op == "windows_task" &&
                capture.Last.Arg["op"] == action && capture.Last.Ws is null && capture.Last.Cwd is null,
                "CLI selected wrong action/context");
            foreach (var forbidden in new[] { "server", "principal", "env", "cwd", "ws", "root", "connectionAuthority", "__execution_context_system" })
            {
                var bad = example.DeepClone().AsObject(); bad[forbidden] = "forbidden";
                using var input = JsonDocument.Parse(bad.ToJsonString()); before = capture.Calls;
                await PaoProgram.CallUnifiedMcpToolAsync(input.RootElement, capture, default);
                Check(capture.Calls == before, "MCP admitted a forbidden task envelope field");
            }
            if (action is "prepare" or "create" or "update")
            {
                var bad = example.DeepClone().AsObject(); bad["system"] = true;
                using var input = JsonDocument.Parse(bad.ToJsonString()); before = capture.Calls;
                await PaoProgram.CallUnifiedMcpToolAsync(input.RootElement, capture, default);
                Check(capture.Calls == before, "task authoring admitted System selector");
            }
            else
            {
                var root = example.DeepClone().AsObject(); root["system"] = true;
                using var input = JsonDocument.Parse(root.ToJsonString()); before = capture.Calls;
                await PaoProgram.CallUnifiedMcpToolAsync(input.RootElement, capture, default);
                Check(capture.Calls == before + 1 && capture.Last!.System && capture.Last.Root is null && capture.Last.ConnectionAuthority is null && capture.Last.Cwd is null,
                    "explicit existing-task System route missing");
            }
            if (WindowsTaskOperationContract.IsReadOnly(action))
            {
                var bad = example.DeepClone().AsObject(); bad["wait"] = false;
                using var input = JsonDocument.Parse(bad.ToJsonString()); before = capture.Calls;
                await PaoProgram.CallUnifiedMcpToolAsync(input.RootElement, capture, default);
                Check(capture.Calls == before, "read-only task admitted deferred execution");
            }
        }
        using var duplicate = JsonDocument.Parse("{\"op\":\"windows_task_list\",\"op\":\"windows_task_read\",\"text\":\"{}\"}");
        var calls = capture.Calls;
        await PaoProgram.CallUnifiedMcpToolAsync(duplicate.RootElement, capture, default);
        Check(capture.Calls == calls, "duplicate operation selector admitted");
    }

    private static async Task OwnerAsync()
    {
        using var root = new OwnedRoot();
        var fixture = WindowsTaskTests.CreateEngineFixture(Sid, () => new(Sid, 7, Elevated: false));
        var admission = new UserHostAdmission();
        await using var engine = Engine(root.Path, admission);
        Check(!(await engine.ExecuteAsync(Request("read"))).Ok, "unbound task provider admitted");
        engine.BindWindowsTasks(fixture.Operations);
        foreach (var action in new[] { "list", "read", "instances" })
        {
            var read = await engine.ExecuteAsync(Request(action, "ephemeral"));
            Check(read.Ok && read.Op == "windows_task_" + action && Rows(root.Path) == 0,
                "task read became durable or was misrouted");
        }
        var request = Request("enable", "owner-original");
        foreach (var invalid in new[]
        {
            request with { Rid = null }, request with { Ws = "forbidden" }, request with { Cwd = root.Path },
            request with { PathRoots = new Dictionary<string, string> { ["src"] = root.Path } },
            request with { Arg = new Dictionary<string, string> { ["op"] = "enable", [OperationRequest.BodyOmittedKey] = "1" } },
            request with { Arg = new Dictionary<string, string> { ["op"] = "enable", ["__execution_context_system"] = "1" } }
        }) Check(!(await engine.ExecuteAsync(invalid)).Ok && fixture.NativeCalls == 0, "invalid original task envelope reached native control");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        fixture.HoldCall = true;
        using var caller = new CancellationTokenSource();
        var active = engine.ExecuteAsync(request, caller.Token);
        try
        {
            await fixture.Entered.WaitAsync(deadline.Token); caller.Cancel();
            var canceled = false;
            try { await active; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && fixture.Platform.Outstanding > 0 && Stored(root.Path, request.Rid!).Receipt.State == "running",
                "client cancellation released the original COM worker or fabricated its terminal outcome");
            using var closed = admission.TryClose() ?? throw new Exception("fixture_close_failed");
            Check(await engine.UserExitBlockerAsync(deadline.Token) == "work_active", "Exit ignored active task effect");
            fixture.Release();
            var unknown = await TerminalAsync(root.Path, request.Rid!, deadline.Token);
            Check(!unknown.Ok && unknown.State == "recovery_required" && unknown.Partial && Flag(unknown, "outcome_unknown") &&
                fixture.NativeCalls == 1 && fixture.Platform.Outstanding == 0, "canceled issued effect was not retained as unknown after cleanup");
        }
        finally
        {
            fixture.Release();
            try { await active.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
        }
        var replay = await engine.ExecuteAsync(request);
        Check(replay.State == "recovery_required" && fixture.NativeCalls == 1, "same RID reissued original task effect");
        var changed = await engine.ExecuteAsync(request with { Body = request.Body!.Replace(TaskPath.Replace("\\", "\\\\"), "\\\\Fixture\\\\Other", StringComparison.Ordinal) });
        Check(!changed.Ok && changed.Err == "precondition" && fixture.NativeCalls == 1, "changed RID target was accepted");
        var stored = Stored(root.Path, request.Rid!);
        var retained = JsonSerializer.Serialize(stored, Json);
        Check(stored.Request is null && !retained.Contains("CortexFixture", StringComparison.Ordinal) &&
            !retained.Contains("argv", StringComparison.Ordinal) && !retained.Contains("owner_interactive_limited", StringComparison.Ordinal),
            "durable task history exposed private request/native detail");
        var fresh = await engine.ExecuteAsync(Request("read", request.Rid!));
        Check(fresh.Ok && fresh.Jid != replay.Jid && fresh.Op == "windows_task_read" && Rows(root.Path) == 1,
            "read reused durable mutation RID or created history");
    }

    private static async Task SystemAsync()
    {
        using var root = new OwnedRoot();
        var state = System.IO.Path.Combine(root.Path, "system");
        var owner = LocalIdentity.Current();
        var approval = new ApprovedConnectionFixture(owner);
        await using var engine = Engine(state, null, direct: true, authorization:approval.Authorize);
        var fixture = WindowsTaskTests.CreateEngineFixture(owner.Sid, () => new("S-1-5-18", 0, Elevated: true), WindowsTaskAuthority.System);
        engine.BindWindowsTasks(fixture.Operations);
        await using var broker = new AuthenticatedServiceBroker(System.IO.Path.Combine(root.Path, "router"), owner.Sid,
            approval.Authorize, false, null, (request, token) => engine.ExecuteAuthenticatedFrozenDirectAsync(request, token),
            ownerHostVerifier: approval.IsTrustedHost);
        var request = approval.System(Request("enable", "system-original"));
        foreach (var denied in new[]
        {
            request with { Root = "AB12" }, request with { System = false }, request with { ConnectionAuthority = null },
            request with { ConnectionAuthority = approval.Current with { GrantRevision = 2 } }
        }) Check(!(await broker.HandleSystemAsync(denied, owner, default)).Ok && fixture.NativeCalls == 0,
            "retired or absent/stale approval reached task provider");
        foreach (var invalid in new[]
        {
            request with { PathRoots = new Dictionary<string, string> { ["src"] = root.Path } },
            request with { Arg = new Dictionary<string, string>(request.Arg) { ["env0"] = "forbidden" } },
            request with { Arg = new Dictionary<string, string>(request.Arg) { ["stdin"] = "forbidden" } },
            request with { Arg = new Dictionary<string, string>(request.Arg) { ["__execution_context_system"] = "1" } }
        }) Check(!(await broker.HandleSystemAsync(invalid, owner, default)).Ok && fixture.NativeCalls == 0,
            "System broker erased a forbidden original task field before validation");
        Check(!(await broker.HandleSystemAsync(request, owner with { Sid = Sid }, default)).Ok &&
            !(await broker.HandleSystemAsync(request, owner with { ApplicationPath = System.IO.Path.Combine(root.Path, "foreign.exe") }, default)).Ok &&
            fixture.NativeCalls == 0, "task route bypassed owner or approved application policy");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        fixture.HoldCall = true;
        var active = broker.HandleSystemAsync(request, owner, default);
        try
        {
            await fixture.Entered.WaitAsync(deadline.Token);
            var original = Stored(state, request.Rid!).Receipt;
            var query = new OperationRequest("job", new Dictionary<string, string> { ["op"] = "stat", ["jid"] = original.Jid }, "observer", System: true, ConnectionAuthority: approval.Current);
            var observed = await broker.HandleSystemAsync(query, owner, deadline.Token);
            Check(observed.Ok && Record(observed).Jid == original.Jid && Record(observed).State == "running" &&
                Rows(state) == 1 && Rows(System.IO.Path.Combine(root.Path, "router"), "agentops-router.db", "routed_jobs") == 0,
                "task observation duplicated durable owner or failed original JID");
            using var observer = new CancellationTokenSource();
            var waiting = broker.HandleSystemAsync(query with { Arg = new Dictionary<string, string>(query.Arg) { ["op"] = "wait" } }, owner, observer.Token);
            observer.Cancel(); var canceled = false;
            try { await waiting; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && !active.IsCompleted && fixture.NativeCalls == 1, "observer cancellation affected original task worker");
            fixture.Release(); var result = await active.WaitAsync(deadline.Token);
            Check(result.Ok && result.Jid == original.Jid && fixture.NativeCalls == 1, "System task control did not complete once");
            var replay = await broker.HandleSystemAsync(request, owner, deadline.Token);
            Check(replay.Jid == original.Jid && fixture.NativeCalls == 1, "System RID replay reissued native task control");
            var terminal = await broker.HandleSystemAsync(query, owner, deadline.Token);
            Check(Record(terminal).Jid == original.Jid && !JsonSerializer.Serialize(Record(terminal), Json).Contains("CortexFixture", StringComparison.Ordinal),
                "System job observation disclosed task definition detail");
        }
        finally { fixture.Release(); await active.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task LostBodyAsync()
    {
        using var root = new OwnedRoot();
        var request = Request("enable", "lost-task-body");
        var identity = WindowsTaskOperationContract.Parse(request).RequestIdentity;
        using (var store = new SqliteJobStore(root.Path))
            store.Claim(new(true, Guid.NewGuid().ToString("N"), request.Rid!, "queued", "windows_task_enable",
                Data: new Dictionary<string, object?> { ["requestIdentity"] = identity }),
                new("windows_task", new Dictionary<string, string> { ["op"] = "enable", [OperationRequest.BodyOmittedKey] = "1" }, request.Rid), true);
        var fixture = WindowsTaskTests.CreateEngineFixture(Sid, () => new(Sid, 7, Elevated: false));
        await using var engine = Engine(root.Path, new UserHostAdmission());
        engine.BindWindowsTasks(fixture.Operations);
        var recovered = await engine.ExecuteAsync(request);
        Check(recovered.State == "recovery_required" && recovered.Partial && Flag(recovered, "outcome_unknown") && fixture.NativeCalls == 0,
            "startup recovery reconstructed a task action from its lost body");
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Jid == recovered.Jid && fixture.NativeCalls == 0 && Rows(root.Path) == 1,
            "lost task-body replay created another job or native action");
    }

    private static AgentOpsEngine Engine(string path, UserHostAdmission? admission, bool direct = false,
        Func<Pegasus.Cortex.Product.CortexMcpConnectionAuthority,bool,bool>? authorization = null) =>
        new(path, null, direct, null, false, null, maintenanceMode: new OpenMode(), userHostAdmission: admission,
            connectionAuthorization:authorization);
    private static OperationRequest Request(string action, string? rid = null)
    {
        var body = new JsonObject { ["version"] = 1 };
        if (action != "list") body["task"] = TaskPath;
        else body["folder"] = "\\CortexFixture";
        if (!WindowsTaskOperationContract.IsReadOnly(action)) body["taskSha"] = Sha;
        return new("windows_task", new Dictionary<string, string> { ["op"] = action }, rid ?? "fixture-" + action, Body: body.ToJsonString());
    }
    private static bool Flag(OperationReceipt receipt, string key) => JsonSerializer.SerializeToElement(receipt.Data, Json).GetProperty(key).GetBoolean();
    private static OperationReceipt Record(OperationReceipt receipt) => JsonSerializer.SerializeToElement(receipt.Data, Json).GetProperty("rec").Deserialize<OperationReceipt>(Json)!;
    private static DurableJob Stored(string path, string rid) { using var store = new SqliteJobStore(path); return store.FindByRid(rid)!; }
    private static async Task<OperationReceipt> TerminalAsync(string path, string rid, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var receipt = Stored(path, rid).Receipt;
            if (receipt.State is not ("queued" or "running")) return receipt;
            await Task.Delay(10, token);
        }
    }
    private static long Rows(string root, string file = "agentops.db", string table = "jobs")
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(root, file), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls; internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken)
        { Calls++; Last = request; return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid ?? "capture", "done", OperationNames.Public(request))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class OwnedRoot : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("cortex-task-wiring-").FullName;
        public void Dispose()
        {
            var canonical = System.IO.Path.GetFullPath(Path);
            var boundary = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(canonical).StartsWith("cortex-task-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(canonical, true);
        }
    }
}
