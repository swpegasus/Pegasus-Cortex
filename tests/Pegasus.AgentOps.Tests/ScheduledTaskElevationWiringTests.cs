using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Captured frontends and owned SQLite with an injected original High child.
// No OS process, UAC, COM, certificate store or task mutation occurs here.
internal static class ScheduledTaskElevationWiringTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        await MetadataAsync();
        await ReadLifetimeAsync();
        await MissingCompletionAsync();
        await DeclinedAsync();
        await LostBodyAsync();
        Console.WriteLine("PASS scheduled-task UAC wiring: actual captured CLI/MCP selection, durable read-helper ownership, issue-before-payload, original child/observer cancellation, route/preimage replay, compact privacy and lost-body recovery; fake child/provider +owned SQLite, no UAC/COM/task effect");
    }

    private static async Task MetadataAsync()
    {
        await using var capture = new Capture();
        foreach (var action in WindowsTaskOperationContract.Actions)
        {
            var help = PaoProgram.UnifiedMcpOperationHelp("windows_task_" + action)!;
            var input = help["mcp_example"]!.DeepClone().AsObject();
            input["elevate"] = "uac"; input["rid"] = "uac-metadata-" + action;
            var before = capture.Calls;
            using (var document = JsonDocument.Parse(input.ToJsonString()))
                await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, capture, default, defaultWs: "must-not-apply");
            if (!WindowsTaskOperationContract.IsOwnerElevationAction(action))
            { Check(capture.Calls == before, "unsupported task action admitted UAC selector"); continue; }
            Check(capture.Calls == before + 1 && capture.Last is { Root: null, Ws: null, Cwd: null } &&
                WindowsTaskOperationContract.Parse(capture.Last).RequiresOwnerElevation, "MCP missed explicit UAC route or applied ambient context");
            using var output = new StringWriter(); using var error = new StringWriter();
            before = capture.Calls;
            var exit = await PaoProgram.RunCliAsync(["windows_task", action, "--stdin", "elevate=uac", "rid=uac-cli", "wait=false"],
                capture, output, error, default, stdin: new StringReader(input["text"]!.GetValue<string>()));
            Check(exit == 0 && capture.Calls == before + 1 && capture.Last!.Arg["elevate"] == "uac" &&
                capture.Last.Arg["wait"] == "false" && capture.Last.Ws is null && capture.Last.Cwd is null,
                "CLI missed explicit UAC route/deferred helper semantics");
            foreach (var key in new[] { "root", "missing-rid" })
            {
                var invalid = input.DeepClone().AsObject();
                if (key == "root") invalid["root"] = "AB12"; else invalid.Remove("rid");
                before = capture.Calls;
                using var document = JsonDocument.Parse(invalid.ToJsonString());
                await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, capture, default);
                Check(capture.Calls == before, "UAC admitted Root or missing helper RID");
            }
        }
    }

    private static async Task ReadLifetimeAsync()
    {
        using var root = new OwnedRoot(); using var fixture = ScheduledTaskElevationTests.CreateEngineFixture();
        var admission = new UserHostAdmission();
        await using var engine = Engine(root.Path, admission);
        var ordinary = Bind(engine, fixture);
        var request = Request("read", "uac-original-read");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var observer = new CancellationTokenSource();
        var operation = engine.ExecuteAsync(request, observer.Token);
        try
        {
            await fixture.TicketReady.WaitAsync(deadline.Token);
            var child = fixture.Launcher.LastProcess ?? throw new Exception("fixture_child_missing");
            var redeemed = await fixture.RedeemAsync(); Check(redeemed.Ok, "fake High child could not redeem task payload");
            var payload = Payload(redeemed);
            var row = Stored(root.Path, request.Rid!);
            Check(row.Mutation && row.Request is { Body: null } && row.Request.Arg.GetValueOrDefault("elevate") == "uac" &&
                row.Request.Arg.ContainsKey(OperationRequest.BodyOmittedKey) && Flag(row.Receipt, "helper_payload_issued") &&
                !row.Receipt.Data!.ContainsKey("action_issued") && Generation(row.Receipt, child.Identity),
                "read-helper route/generation/uncertainty was not durable before payload release");
            observer.Cancel(); var canceled = false;
            try { await operation; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && !child.Disposed && ordinary.Platform.Connects == 0,
                "observer cancellation abandoned original helper or fell back to ordinary provider");
            using var closed = admission.TryClose() ?? throw new Exception("fixture_close_failed");
            Check(await engine.UserExitBlockerAsync(deadline.Token) == "work_active", "Exit ignored elevated readonly helper ownership");
            var high = WindowsTaskTests.CreateEngineFixture(fixture.OwnerSid, () => fixture.HighPeer, WindowsTaskAuthority.OwnerAdmin);
            var native = high.Operations.Execute(WindowsTaskOperationContract.Parse(ScheduledTaskElevationContract.Reconstruct(payload)));
            Check(native.Ok && !native.ActionIssued, "fake High read was not a native read-only observation");
            Check((await fixture.CompleteAsync(payload, native)).Ok, "parent refused exact fake High completion");
            Check(!child.Disposed && Stored(root.Path, request.Rid!).Receipt.State == "running" &&
                await engine.UserExitBlockerAsync(deadline.Token) == "work_active", "completion report released helper ownership before actual child exit");
            fixture.Exit(0);
            var finished = await TerminalAsync(root.Path, request.Rid!, deadline.Token);
            Check(finished.Ok && finished.State == "done" && Flag(finished, "helper_payload_issued") && !Flag(finished, "action_issued") &&
                !Flag(finished, "outcome_unknown") && child.Disposed && ordinary.Platform.Connects == 0,
                "observed child/read completion lost certainty or used ordinary fallback");
        }
        finally
        {
            fixture.Launcher.LastProcess?.Exit(2);
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (observer.IsCancellationRequested) { }
        }
        var replay = await engine.ExecuteAsync(request);
        var original = Stored(root.Path, request.Rid!).Receipt;
        Check(replay.Jid == original.Jid && fixture.Starts == 1 && replay.Data?.ContainsKey("task") != true,
            "same UAC RID relaunched or replayed private read output");
        var changed = await engine.ExecuteAsync(request with { Body = JsonSerializer.Serialize(new { version = 1, task = @"\CortexFixture\Other" }) });
        Check(!changed.Ok && changed.Err == "precondition" && fixture.Starts == 1, "same RID changed elevated task preimage");
        AssertPrivate(Stored(root.Path, request.Rid!));
    }

    private static async Task MissingCompletionAsync()
    {
        using var root = new OwnedRoot(); using var fixture = ScheduledTaskElevationTests.CreateEngineFixture();
        await using var engine = Engine(root.Path, new UserHostAdmission()); var ordinary = Bind(engine, fixture);
        var request = Request("enable", "uac-missing-completion");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operation = engine.ExecuteAsync(request);
        try
        {
            await fixture.TicketReady.WaitAsync(deadline.Token);
            Check((await fixture.RedeemAsync()).Ok, "fake task payload was not issued");
            fixture.Exit(2);
            var result = await operation.WaitAsync(deadline.Token);
            Check(!result.Ok && result.State == "recovery_required" && result.Partial && Flag(result, "outcome_unknown") &&
                !result.Data!.ContainsKey("action_issued") && ordinary.Platform.Connects == 0,
                "missing issued completion invented native nonissuance or used fallback");
            var replay = await engine.ExecuteAsync(request);
            Check(replay.Jid == result.Jid && fixture.Starts == 1, "uncertain task helper was relaunched");
            var changedRoute = request with { Arg = new Dictionary<string, string> { ["op"] = "enable" } };
            Check((await engine.ExecuteAsync(changedRoute)).Err == "precondition" && ordinary.NativeCalls == 0,
                "uncertain UAC mutation retried through ordinary authority");
            AssertPrivate(Stored(root.Path, request.Rid!));
        }
        finally { fixture.Launcher.LastProcess?.Exit(2); await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task DeclinedAsync()
    {
        using var root = new OwnedRoot(); using var fixture = ScheduledTaskElevationTests.CreateEngineFixture();
        fixture.Launcher.Decline = true;
        await using var engine = Engine(root.Path, new UserHostAdmission()); var ordinary = Bind(engine, fixture);
        var request = Request("disable", "uac-declined");
        var result = await engine.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        Check(!result.Ok && result.State == "canceled" && !result.Partial && !Flag(result, "helper_payload_issued") &&
            !Flag(result, "action_issued") && fixture.Starts == 1 && ordinary.Platform.Connects == 0,
            "declined UAC claimed a task effect or selected another route");
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Jid == result.Jid && fixture.Starts == 1, "declined original RID relaunched UAC");
    }

    private static async Task LostBodyAsync()
    {
        using var root = new OwnedRoot(); using var fixture = ScheduledTaskElevationTests.CreateEngineFixture();
        var request = Request("instances", "uac-lost-read-body");
        var identity = WindowsTaskOperationContract.Parse(request).RequestIdentity;
        using (var store = new SqliteJobStore(root.Path))
            store.Claim(new(true, Guid.NewGuid().ToString("N"), request.Rid!, "queued", "windows_task_instances",
                Data: new Dictionary<string, object?>
                { ["requestIdentity"] = identity, ["helper_payload_issued"] = true, ["helper_pid"] = 444L,
                    ["helper_start"] = "2026-11-22T00:00:00.0000000Z" }),
                new("windows_task", new Dictionary<string, string> { ["op"] = "instances", ["elevate"] = "uac", [OperationRequest.BodyOmittedKey] = "1" }, request.Rid), true);
        await using var engine = Engine(root.Path, new UserHostAdmission()); Bind(engine, fixture);
        var result = await engine.ExecuteAsync(request);
        Check(result.State == "recovery_required" && result.Partial && Flag(result, "outcome_unknown") &&
            fixture.Starts == 0 && Stored(root.Path, request.Rid!).Mutation &&
            Generation(result, new(444, "2026-11-22T00:00:00.0000000Z", fixture.Host)),
            "lost elevated read body became an ephemeral read or recreated UAC");
    }

    private static AgentOpsEngine Engine(string path, UserHostAdmission admission) =>
        new(path, null, false, null, false, null, maintenanceMode: new OpenMode(), userHostAdmission: admission);
    private static WindowsTaskTests.EngineFixture Bind(AgentOpsEngine engine, ScheduledTaskElevationTests.EngineFixture fixture)
    {
        var ordinary = WindowsTaskTests.CreateEngineFixture(fixture.OwnerSid, () => new(fixture.OwnerSid, fixture.Session, Elevated: false));
        engine.BindWindowsTasks(ordinary.Operations); engine.BindScheduledTaskElevation(fixture.Elevation); return ordinary;
    }
    private static OperationRequest Request(string action, string rid)
    {
        var body = new JsonObject { ["version"] = 1, ["task"] = WindowsTaskTests.EngineFixtureTaskPath };
        if (!WindowsTaskOperationContract.IsReadOnly(action)) body["taskSha"] = WindowsTaskTests.EngineFixtureTaskSha;
        return new("windows_task", new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac" }, rid, Body: body.ToJsonString());
    }
    private static ScheduledTaskElevationRequest Payload(OperationReceipt receipt) =>
        ScheduledTaskElevationContract.ParseRequest(JsonSerializer.Serialize(receipt.Data!["payload"], Json));
    private static DurableJob Stored(string path, string rid) { using var store = new SqliteJobStore(path); return store.FindByRid(rid)!; }
    private static bool Flag(OperationReceipt receipt, string key) => JsonSerializer.SerializeToElement(receipt.Data, Json).GetProperty(key).GetBoolean();
    private static bool Generation(OperationReceipt receipt, CertificateElevationProcessIdentity expected)
    {
        var data = JsonSerializer.SerializeToElement(receipt.Data, Json);
        return data.TryGetProperty("helper_pid", out var pid) && pid.TryGetUInt32(out var value) && value == expected.Pid &&
            data.TryGetProperty("helper_start", out var start) && start.GetString() == expected.Start;
    }
    private static async Task<OperationReceipt> TerminalAsync(string path, string rid, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); var receipt = Stored(path, rid).Receipt;
            if (receipt.State is not ("queued" or "running")) return receipt;
            await Task.Delay(10, token);
        }
    }
    private static void AssertPrivate(DurableJob row)
    {
        var text = JsonSerializer.Serialize(row, Json);
        Check(row.Request is null && !text.Contains("CortexFixture", StringComparison.Ordinal) &&
            !text.Contains("ticket", StringComparison.OrdinalIgnoreCase) && !text.Contains("owner_interactive_limited", StringComparison.Ordinal),
            "durable helper receipt exposed its payload/capability/native detail");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls; internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token)
        { Calls++; Last = request; return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid ?? "capture", "done", OperationNames.Public(request))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OwnedRoot : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("cortex-task-uac-wiring-").FullName;
        public void Dispose()
        {
            var canonical = System.IO.Path.GetFullPath(Path);
            var boundary = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(canonical).StartsWith("cortex-task-uac-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(canonical, true);
        }
    }
}
