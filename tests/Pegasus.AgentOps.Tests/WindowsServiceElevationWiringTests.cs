using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Real frontend admission and GUID-owned SQLite; existing injected High children.
// No UAC prompt, OS process, SCM/native service provider or service action.
internal static class WindowsServiceElevationWiringTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        await MetadataAsync();
        await ReadLifetimeAsync();
        await MissingCompletionAsync();
        await DeclinedAsync();
        await LostBodyAsync();
        await OrdinaryAndUnboundAsync();
        Console.WriteLine("PASS service UAC wiring: captured CLI/MCP elevate/wait/context admission, owned SQLite durable read-helper/pre-payload generation, child/observer/Exit ownership, route/preimage replay and lost-body recovery; fake High child and denied SCM boundary, no UAC/SCM/service action");
    }

    private static async Task MetadataAsync()
    {
        await using var capture = new Capture();
        foreach (var action in WindowsServiceOperationContract.Actions)
        {
            var help = PaoProgram.UnifiedMcpOperationHelp("windows_service_" + action)
                ?? throw new Exception("fixture_service_help_missing");
            var example = help["mcp_example"]?.DeepClone().AsObject()
                ?? throw new Exception("fixture_service_example_missing");
            example.Remove("root"); // Select the owner route explicitly.
            example["elevate"] = "uac"; example["rid"] = "service-uac-metadata-" + action;
            example["wait"] = false;
            var before = capture.Calls;
            using (var document = JsonDocument.Parse(example.ToJsonString()))
                await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, capture, default, defaultWs: "must-not-apply");
            if (!WindowsServiceOperationContract.IsOwnerElevationAction(action))
            { Check(capture.Calls == before, "elevated service list reached dispatcher"); continue; }
            var admitted = capture.Last;
            Check(capture.Calls == before + 1 && admitted is { Root: null, Ws: null, Cwd: null } &&
                admitted.Arg.GetValueOrDefault("elevate") == "uac" && admitted.Arg.GetValueOrDefault("wait") == "false" &&
                WindowsServiceOperationContract.Parse(admitted).RequiresOwnerElevation,
                "MCP lost service helper selector or adopted normal workspace defaults");
            using var output = new StringWriter(); using var error = new StringWriter();
            before = capture.Calls;
            var cli = await PaoProgram.RunCliAsync(["windows_service", action, "--stdin", "elevate=uac", "rid=service-uac-cli", "wait=false"],
                capture, output, error, default, stdin: new StringReader(example["text"]!.GetValue<string>()));
            admitted = capture.Last;
            Check(cli == 0 && capture.Calls == before + 1 && admitted is { Root: null, Ws: null, Cwd: null } &&
                admitted.Arg.GetValueOrDefault("elevate") == "uac" && admitted.Arg.GetValueOrDefault("wait") == "false",
                "CLI lost service helper selector or deferred semantics");
            foreach (var kind in new[] { "root", "missing-rid", "unknown-field", "unknown-body" })
            {
                var invalid = example.DeepClone().AsObject();
                if (kind == "root") invalid["root"] = "AB12";
                else if (kind == "missing-rid") invalid.Remove("rid");
                else if (kind == "unknown-field") invalid["callerIdentity"] = null;
                else
                {
                    var body = JsonNode.Parse(invalid["text"]!.GetValue<string>())!.AsObject();
                    body["command"] = "forbidden"; invalid["text"] = body.ToJsonString();
                }
                before = capture.Calls;
                using var document = JsonDocument.Parse(invalid.ToJsonString());
                await PaoProgram.CallUnifiedMcpToolAsync(document.RootElement, capture, default);
                Check(capture.Calls == before, "service frontend admitted conflicting authority, missing RID or unknown input");
            }
        }
    }

    private static async Task ReadLifetimeAsync()
    {
        using var root = new OwnedRoot();
        using var fixture = WindowsServiceElevationTests.CreateEngineFixture();
        var admission = new UserHostAdmission();
        var noNative = new RefusingScm();
        await using var engine = Engine(root.Path, admission, noNative);
        Bind(engine, fixture);
        var request = Request("read", "service-uac-original-read");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var observer = new CancellationTokenSource();
        var operation = engine.ExecuteAsync(request, observer.Token);
        try
        {
            await fixture.TicketReady.WaitAsync(deadline.Token);
            var child = fixture.Launcher.LastProcess ?? throw new Exception("fixture_service_child_missing");
            var redeemed = await fixture.RedeemAsync();
            Check(redeemed.Ok, "fake High child could not redeem service request");
            var payload = Payload(redeemed);
            var running = Stored(root.Path, request.Rid!);
            Check(running.Mutation && running.Request is { Body: null } && running.Request.Arg.GetValueOrDefault("elevate") == "uac" &&
                running.Request.Arg.ContainsKey(OperationRequest.BodyOmittedKey) && Flag(running.Receipt, "helper_payload_issued") &&
                Flag(running.Receipt, "outcome_unknown") && !Has(running.Receipt, "action_issued") &&
                Generation(running.Receipt, child.Identity) && !Has(running.Receipt, "pid") && !Has(running.Receipt, "start"),
                "service read helper/generation uncertainty was not durable before payload or confused with target PID");
            observer.Cancel();
            var canceled = false;
            try { await operation; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && !child.Disposed && noNative.Opens == 0, "observer cancellation abandoned helper or selected ordinary SCM");

            using (admission.TryClose() ?? throw new Exception("fixture_service_admission_close_failed"))
            {
                Check(await engine.UserExitBlockerAsync(deadline.Token) == "work_active", "Exit ignored elevated readonly service helper");
                var changedBody = JsonNode.Parse(request.Body!)!.AsObject(); changedBody["service"] = "OtherFixtureService";
                var stopped = await engine.ExecuteAsync(request with { Body = changedBody.ToJsonString() }, deadline.Token);
                Check(stopped.Err == "host_stopping" && !Has(stopped, "service") && !Has(stopped, "helper_pid") &&
                    fixture.Starts == 1 && noNative.Opens == 0, "closed gate returned durable replay/private data or relaunched changed service body");

                var result = WindowsServiceElevationTests.ValidResult("read");
                var target = result.Current ?? throw new Exception("fixture_service_target_missing");
                Check((await fixture.CompleteAsync(payload, result)).Ok, "parent refused actual provider-shaped service completion");
                Check(!child.Disposed && Stored(root.Path, request.Rid!).Receipt.State == "running" &&
                    await engine.UserExitBlockerAsync(deadline.Token) == "work_active",
                    "completion report released original helper/effect before exit");
                fixture.Exit(0);
                var completed = await TerminalAsync(root.Path, request.Rid!, deadline.Token);
                Check(completed.Ok && completed.State == "done" && Flag(completed, "helper_payload_issued") &&
                    !Flag(completed, "action_issued") && !Flag(completed, "outcome_unknown") &&
                    Generation(completed, child.Identity) && Number(completed, "pid") == target.Pid &&
                    Text(completed, "start") == target.Start && Number(completed, "pid") != child.Identity.Pid &&
                    child.Disposed && noNative.Opens == 0, "read settlement lost separate helper/target generations or used fallback");
            }
        }
        finally { await DrainAsync(root.Path, request.Rid!, fixture, operation, observer.IsCancellationRequested); }

        var original = Stored(root.Path, request.Rid!).Receipt;
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Jid == original.Jid && fixture.Starts == 1, "same service UAC RID relaunched helper");
        var changed = JsonNode.Parse(request.Body!)!.AsObject(); changed["service"] = "OtherFixtureService";
        Check((await engine.ExecuteAsync(request with { Body = changed.ToJsonString() })).Err == "precondition" &&
            fixture.Starts == 1 && noNative.Opens == 0, "same RID changed service preimage or crossed route");
        AssertPrivate(Stored(root.Path, request.Rid!));
    }

    private static async Task MissingCompletionAsync()
    {
        using var root = new OwnedRoot(); using var fixture = WindowsServiceElevationTests.CreateEngineFixture();
        var noNative = new RefusingScm();
        await using var engine = Engine(root.Path, new UserHostAdmission(), noNative); Bind(engine, fixture);
        var request = Request("start", "service-uac-missing-completion");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operation = engine.ExecuteAsync(request);
        try
        {
            await fixture.TicketReady.WaitAsync(deadline.Token);
            Check((await fixture.RedeemAsync()).Ok, "service payload not released");
            fixture.Exit(3);
            var result = await operation.WaitAsync(deadline.Token);
            Check(!result.Ok && result.State == "recovery_required" && result.Partial && Flag(result, "outcome_unknown") &&
                Flag(result, "helper_payload_issued") && !Has(result, "action_issued") && noNative.Opens == 0,
                "missing service completion invented unissued native action or selected fallback");
            Check((await engine.ExecuteAsync(request)).Jid == result.Jid && fixture.Starts == 1, "uncertain service helper relaunched");
            var routeChanged = request with { Arg = new Dictionary<string, string> { ["op"] = "start" } };
            // An unprivileged service mutation is rejected before durable RID lookup.
            // Ordinary task control differs: its owner route is itself admitted.
            var routeRefused = await engine.ExecuteAsync(routeChanged);
            Check(!routeRefused.Ok && routeRefused.State == "failed" && routeRefused.Err == "auth" &&
                noNative.Opens == 0 && fixture.Starts == 1,
                "unadmitted ordinary service mutation reached RID replay/native/helper (error=" +
                (routeRefused.Err ?? "<none>") + ", starts=" + fixture.Starts + ", scm=" + noNative.Opens + ")");
            AssertPrivate(Stored(root.Path, request.Rid!));
        }
        finally { await DrainAsync(root.Path, request.Rid!, fixture, operation); }
    }

    private static async Task DeclinedAsync()
    {
        using var root = new OwnedRoot(); using var fixture = WindowsServiceElevationTests.CreateEngineFixture();
        fixture.Launcher.Decline = true;
        var noNative = new RefusingScm();
        await using var engine = Engine(root.Path, new UserHostAdmission(), noNative); Bind(engine, fixture);
        var request = Request("stop", "service-uac-declined");
        var result = await engine.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        Check(!result.Ok && result.State == "canceled" && !result.Partial && !Flag(result, "helper_payload_issued") &&
            !Flag(result, "action_issued") && fixture.Starts == 1 && noNative.Opens == 0, "declined UAC claimed service effect/fallback");
        Check((await engine.ExecuteAsync(request)).Jid == result.Jid && fixture.Starts == 1, "declined original RID relaunched UAC");
    }

    private static async Task LostBodyAsync()
    {
        using var root = new OwnedRoot(); using var fixture = WindowsServiceElevationTests.CreateEngineFixture();
        var request = Request("read", "service-uac-lost-body");
        var identity = WindowsServiceOperationContract.Parse(request).RequestIdentity;
        var retained = new CertificateElevationProcessIdentity(444, "2026-11-22T00:00:00.0000000Z", fixture.Host);
        using (var store = new SqliteJobStore(root.Path))
            store.Claim(new(true, Guid.NewGuid().ToString("N"), request.Rid!, "queued", "windows_service_read",
                Data: new Dictionary<string, object?> { ["requestIdentity"] = identity, ["helper_payload_issued"] = true,
                    ["helper_pid"] = (long)retained.Pid, ["helper_start"] = retained.Start }),
                new("windows_service", new Dictionary<string, string>
                    { ["op"] = "read", ["elevate"] = "uac", [OperationRequest.BodyOmittedKey] = "1" }, request.Rid), true);
        var noNative = new RefusingScm();
        await using var engine = Engine(root.Path, new UserHostAdmission(), noNative); Bind(engine, fixture);
        var result = await engine.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        Check(result.State == "recovery_required" && result.Partial && Flag(result, "outcome_unknown") &&
            Generation(result, retained) && !Has(result, "action_issued") && fixture.Starts == 0 && noNative.Opens == 0 &&
            Stored(root.Path, request.Rid!).Mutation, "lost helper body became ephemeral/read-native or launched/adopted process");
    }

    private static async Task OrdinaryAndUnboundAsync()
    {
        using var root = new OwnedRoot(); using var fixture = WindowsServiceElevationTests.CreateEngineFixture();
        var noNative = new RefusingScm();
        await using var engine = Engine(root.Path, new UserHostAdmission(), noNative);
        var ordinary = Request("read", "service-ordinary-read") with { Arg = new Dictionary<string, string> { ["op"] = "read" } };
        var result = await engine.ExecuteAsync(ordinary);
        Check(!result.Ok && result.Err == "access" && noNative.Opens == 1 && CountRows(root.Path) == 0 && fixture.Starts == 0,
            "ordinary denied read became durable or selected UAC");
        var unbound = await engine.ExecuteAsync(Request("read", "service-uac-unbound"));
        Check(!unbound.Ok && unbound.Err == "auth" && noNative.Opens == 1 && fixture.Starts == 0,
            "unbound elevated read reached native provider/helper");
    }

    private static AgentOpsEngine Engine(string path, UserHostAdmission admission, RefusingScm noNative) =>
        new(path, null, false, null, false, null, maintenanceMode: new OpenMode(), userHostAdmission: admission,
            windowsServices: new WindowsServiceOperations(noNative));
    private static void Bind(AgentOpsEngine engine, WindowsServiceElevationTests.EngineFixture fixture) =>
        engine.BindWindowsServiceElevation(fixture.Coordinator, fixture.OwnerSid,
            () => new(fixture.OwnerSid, fixture.Session, fixture.Host, Elevated: false));
    private static OperationRequest Request(string action, string rid) =>
        new("windows_service", new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac" }, rid,
            Body: WindowsServiceElevationTests.Payload(action).Body);
    private static WindowsServiceElevationRequest Payload(OperationReceipt receipt) =>
        WindowsServiceElevationContract.ParseRequest(JsonSerializer.Serialize(receipt.Data!["payload"], Json));
    private static DurableJob Stored(string path, string rid)
    {
        using var store = new SqliteJobStore(path);
        return store.FindByRid(rid) ?? throw new Exception("fixture_service_row_missing");
    }
    private static long CountRows(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(path, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM jobs";
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static bool Has(OperationReceipt receipt, string name) => receipt.Data?.ContainsKey(name) == true;
    private static bool Flag(OperationReceipt receipt, string name)
    {
        var data = JsonSerializer.SerializeToElement(receipt.Data, Json);
        return data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
    private static long? Number(OperationReceipt receipt, string name)
    {
        var data = JsonSerializer.SerializeToElement(receipt.Data, Json);
        return data.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
    }
    private static string? Text(OperationReceipt receipt, string name)
    {
        var data = JsonSerializer.SerializeToElement(receipt.Data, Json);
        return data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static bool Generation(OperationReceipt receipt, CertificateElevationProcessIdentity expected) =>
        Number(receipt, "helper_pid") == expected.Pid && Text(receipt, "helper_start") == expected.Start;
    private static async Task<OperationReceipt> TerminalAsync(string path, string rid, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); var receipt = Stored(path, rid).Receipt;
            if (receipt.State is not ("queued" or "running")) return receipt;
            await Task.Delay(10, token);
        }
    }
    private static async Task DrainAsync(string path, string rid, WindowsServiceElevationTests.EngineFixture fixture,
        Task<OperationReceipt> operation, bool observerCanceled = false)
    {
        fixture.Launcher.Dispose(); // Exit every already-owned fake original, never a real process.
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (observerCanceled) { }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await TerminalAsync(path, rid, deadline.Token);
    }
    private static void AssertPrivate(DurableJob row)
    {
        var text = JsonSerializer.Serialize(row, Json);
        Check(row.Request is null && !text.Contains("\"ticket\"", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("\"body\"", StringComparison.OrdinalIgnoreCase) && !text.Contains("\"payload\"", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("binary", StringComparison.OrdinalIgnoreCase) && !text.Contains("account", StringComparison.OrdinalIgnoreCase),
            "durable service helper record exposed private payload/capability/native configuration");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls; internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token)
        {
            Calls++; Last = request;
            return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid ?? "capture", "done", OperationNames.Public(request)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RefusingScm : IWindowsServicePlatform
    {
        internal int Opens;
        public WindowsServicePage List(WindowsServiceInvocation invocation, CancellationToken token) =>
            throw new InvalidOperationException("No service listing is permitted in this wiring fixture.");
        public IWindowsServiceHandle Open(string service, string action)
        { Opens++; throw new Win32Exception(5); } // Injected denial, never invokes SCM.
    }
    private sealed class OwnedRoot : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("cortex-service-uac-wiring-").FullName;
        public void Dispose()
        {
            var canonical = System.IO.Path.GetFullPath(Path);
            var boundary = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) +
                System.IO.Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(canonical).StartsWith("cortex-service-uac-wiring-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("fixture_service_cleanup_boundary_invalid");
            Directory.Delete(canonical, true);
        }
    }
}
