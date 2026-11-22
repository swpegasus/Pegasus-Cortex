using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

// Pure contracts, fake SCM handles and GUID-owned SQLite only. No live control.
internal static class GeneralWindowsServiceTests
{
    private const string Service = "Fixture Service_日本";
    private const string StartIdentity = "2026-11-21T12:00:00.0000000Z";
    private static readonly string Config = new('a', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        Contract();
        await ControllerAsync();
        await OwnerAdministratorAsync();
        await EngineAndBrokerAsync();
        await RootObservationsAsync();
        await QueuedObservationAsync();
        await LostBodyAsync();
        await SurfacesAsync();
        Console.WriteLine("PASS general Windows services: six contracts, fake SCM lifecycle/startup, owned SQLite/root admission/replay/lost-body, captured CLI/MCP/help; no live SCM control or UAC");
    }

    private static void Contract()
    {
        Check(WindowsServiceOperationContract.Actions.Count == 6, "closed service actions");
        foreach (var action in WindowsServiceOperationContract.Actions)
        {
            var value = Parse(action);
            Check(value.Action == action && value.IsMutation == !WindowsServiceOperationContract.IsReadOnly(action), "action classification");
            Check(value.RequestIdentity.Length == 64, "opaque request identity");
            var original = JsonNode.Parse(Body(action))!.AsObject();
            var reversed = new JsonObject();
            foreach (var item in original.Reverse()) reversed.Add(item.Key, item.Value?.DeepClone());
            Check(value.RequestIdentity == Parse(action, reversed.ToJsonString()).RequestIdentity, "property ordering changed identity");
            Refuse(() => Parse(action, Body(action).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
            var extra = JsonNode.Parse(Body(action))!.AsObject(); extra["command"] = "forbidden";
            Refuse(() => Parse(action, extra.ToJsonString()));
            Refuse(() => WindowsServiceOperationContract.Parse(Request(action) with { Ws = "forbidden" }));
            Refuse(() => WindowsServiceOperationContract.Parse(Request(action) with { Arg = new Dictionary<string, string> { ["op"] = action, ["service"] = Service } }));
            if (value.IsMutation) Refuse(() => WindowsServiceOperationContract.Parse(Request(action) with { Rid = null }));
        }
        foreach (var bad in new[] { "", "A/B", "A\\B", "A\nB", new string('a', 257) })
        { var body = JsonNode.Parse(Body("read"))!.AsObject(); body["service"] = bad; Refuse(() => Parse("read", body.ToJsonString())); }
        Refuse(() => WindowsServiceOperationContract.RequireName("\ud800"));
        foreach (var mode in new[] { "manual", "automatic", "automatic_delayed", "disabled" })
        {
            var body = JsonNode.Parse(Body("startup"))!.AsObject(); body["mode"] = mode;
            Check(Parse("startup", body.ToJsonString()).Mode == mode, "startup mode");
        }
        foreach (var field in new[] { "binary", "account", "password", "dependencies", "machine", "arguments", "control" })
        { var body = JsonNode.Parse(Body("start"))!.AsObject(); body[field] = "forbidden"; Refuse(() => Parse("start", body.ToJsonString())); }
        var changed = JsonNode.Parse(Body("start"))!.AsObject(); changed["configSha"] = new string('b', 64);
        Check(Parse("start").RequestIdentity != Parse("start", changed.ToJsonString()).RequestIdentity, "config preimage not bound");
        changed["service"] = "Other Service";
        Check(Parse("start").RequestIdentity != Parse("start", changed.ToJsonString()).RequestIdentity, "service target not bound");
        var stop = JsonNode.Parse(Body("stop"))!.AsObject(); stop.Remove("pid"); Refuse(() => Parse("stop", stop.ToJsonString()));
        stop = JsonNode.Parse(Body("stop"))!.AsObject(); stop["start"] = "2026-11-21T12:00:00+00:00"; Refuse(() => Parse("stop", stop.ToJsonString()));
        Check(Parse("list").Maximum == 64 && Parse("start").TimeoutSeconds == 30, "fixed defaults");
    }

    private static async Task ControllerAsync()
    {
        var platform = new FakePlatform();
        var operations = new WindowsServiceOperations(platform);
        var stopped = await operations.ExecuteAsync(Parse("stop"), CancellationToken.None);
        Check(stopped.Ok && platform.State == "stopped" && platform.Stops == 1 && platform.Disposals == 1, "fake stop/handle lifetime");
        var started = await operations.ExecuteAsync(Parse("start"), CancellationToken.None);
        Check(started.Ok && platform.State == "running" && platform.Starts == 1, "fake start");
        platform.Reset();
        Check((await operations.ExecuteAsync(Parse("restart"), CancellationToken.None)).Ok &&
            platform.Stops == 1 && platform.Starts == 1, "fake restart");
        platform.Reset(); platform.FailStep = "start";
        var partial = await operations.ExecuteAsync(Parse("restart"), CancellationToken.None);
        Check(!partial.Ok && partial.ActionIssued && partial.OutcomeUnknown && partial.Current?.State == "stopped",
            "stop/start split lost its partial outcome");
        platform.Reset(); platform.DenyOpen = true;
        var denied = await operations.ExecuteAsync(Parse("stop"), CancellationToken.None);
        Check(!denied.Ok && denied.Error == "access" && !denied.ActionIssued, "access denial fabricated an effect");
        platform.Reset(); platform.ConfigSha = new string('b', 64);
        Check((await operations.ExecuteAsync(Parse("stop"), CancellationToken.None)).Error == "precondition" && platform.Stops == 0,
            "stale config reached stop");
        platform.Reset(); platform.ProcessStart = "2026-11-21T12:01:00.0000000Z";
        Check((await operations.ExecuteAsync(Parse("stop"), CancellationToken.None)).Error == "precondition" && platform.Stops == 0,
            "stale process reached stop");
        platform.Reset(); platform.ReadHook = count => { if (count == 2) platform.ConfigSha = new string('c', 64); };
        Check((await operations.ExecuteAsync(Parse("stop"), CancellationToken.None)).Error == "precondition" && platform.Stops == 0,
            "pre-effect configuration recheck missing");
        foreach (var action in new[] { "start", "stop", "restart", "startup" })
        {
            platform.Reset();
            var body = JsonNode.Parse(Body(action))!.AsObject(); body["service"] = CortexProductIdentity.ServiceName.ToLowerInvariant();
            var protectedResult = await operations.ExecuteAsync(Parse(action, body.ToJsonString()), CancellationToken.None);
            Check(protectedResult.Error == "service_requires_cortex_control" && platform.Opens == 0, "generic Cortex service mutation admitted");
        }
        foreach (var mode in new[] { "manual", "automatic", "automatic_delayed", "disabled" })
        {
            platform.Reset(); var body = JsonNode.Parse(Body("startup"))!.AsObject(); body["mode"] = mode;
            var result = await operations.ExecuteAsync(Parse("startup", body.ToJsonString()), CancellationToken.None);
            Check(result.Ok && result.Current?.Startup == mode && platform.Starts == 0 && platform.Stops == 0,
                "startup changed runtime state");
        }
        platform.Reset(); platform.FailStep = "delay";
        var startup = JsonNode.Parse(Body("startup"))!.AsObject(); startup["mode"] = "automatic_delayed";
        var split = await operations.ExecuteAsync(Parse("startup", startup.ToJsonString()), CancellationToken.None);
        Check(split.OutcomeUnknown && split.ActionIssued && platform.TypeWrites == 1 && split.Current?.Startup == "automatic",
            "two-step startup failure falsely rolled back");
        platform.Reset();
        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            var result = await operations.ExecuteAsync(Parse("stop"), before.Token);
            Check(result.Error == "canceled" && platform.Opens == 0, "pre-cancellation issued SCM work");
        }
        platform.Reset();
        using (var during = new CancellationTokenSource())
        {
            platform.StepHook = step => { if (step == "stop") during.Cancel(); };
            var result = await operations.ExecuteAsync(Parse("restart"), during.Token);
            Check(result.OutcomeUnknown && result.ActionIssued && platform.Starts == 0, "late cancellation reissued or hid stop");
        }
        platform.Reset(); platform.FailStep = "stop";
        var refused = await operations.ExecuteAsync(Parse("stop"), CancellationToken.None);
        Check(refused.OutcomeUnknown && platform.Starts == 0 && platform.Stops == 0, "failed control became retryable");
    }

    private static async Task EngineAndBrokerAsync()
    {
        using var root = new OwnedRoot();
        var platform = new FakePlatform();
        await using var engine = Engine(root.Path, platform);
        var read = await engine.ExecuteAsync(await Wire(Request("read")));
        var page = await engine.ExecuteAsync(Request("list"));
        Check(read.Ok && page.Ok && Rows(root.Path) == 0, "ordinary service reads became durable");
        Check(!JsonSerializer.Serialize(read, Json).Contains("binary", StringComparison.OrdinalIgnoreCase), "raw binary field leaked");
        var denied = await engine.ExecuteAsync(Request("stop"));
        Check(!denied.Ok && denied.Err == "auth" && platform.Stops == 0, "ordinary owner controlled a general service");
        var forged = Request("stop") with { Arg = new Dictionary<string, string> { ["op"] = "stop", ["__execution_context_system"] = "1" } };
        Check(!(await engine.ExecuteAsync(forged)).Ok && platform.Stops == 0, "caller-forged System context admitted");
        var owner = LocalIdentity.Current();
        var verifier = new RootCodeVerifier(System.IO.Path.Combine(root.Path, "root.verifier")); verifier.Enroll("AB12");
        await using var broker = new AuthenticatedServiceBroker(System.IO.Path.Combine(root.Path, "broker"), owner.Sid, verifier,
            false, null, (request, token) => engine.ExecuteAuthenticatedFrozenDirectAsync(request, token));
        Check(!(await broker.HandleSystemAsync(Request("stop") with { Root = "ZZ99" }, owner, CancellationToken.None)).Ok &&
            platform.Stops == 0, "wrong root reached SCM");
        var request = await Wire(Request("stop", rid: "root-stop") with { Root = "AB12" });
        var result = await broker.HandleSystemAsync(request, owner, CancellationToken.None);
        Check(result.Ok && platform.Stops == 1, "root-authenticated fixed service route did not complete");
        await Retired(engine);
        var row = Stored(root.Path, "root-stop");
        Check(row.Receipt.Op == "windows_service_stop" && row.Request is null &&
            !row.Raw.Contains("\"version\"", StringComparison.Ordinal),
            "service body/process expectation entered durable body storage");
        Check(Flag(row.Receipt, "action_issued") && Text(row.Receipt.Data!["requestIdentity"]) == Parse("stop").RequestIdentity,
            "durable service effect/identity missing");
        var calls = platform.Opens;
        var replay = await broker.HandleSystemAsync(request, owner, CancellationToken.None);
        Check(replay.Jid == result.Jid && platform.Opens == calls, "completed service RID reissued");
        var body = JsonNode.Parse(request.Body!)!.AsObject(); body["service"] = "Different Service";
        Check(!(await broker.HandleSystemAsync(request with { Body = body.ToJsonString() }, owner, CancellationToken.None)).Ok &&
            platform.Opens == calls, "changed target reused a RID");
        Check(!(await engine.ExecuteAsync(new("diag", new Dictionary<string, string>(), request.Rid))).Ok,
            "another snapshot family returned a retained service mutation");
        var sameRidRead = await engine.ExecuteAsync(Request("read", rid: request.Rid!));
        Check(sameRidRead.Ok && sameRidRead.Op == "windows_service_read" && Rows(root.Path) == 1,
            "nondurable read returned an unrelated durable service mutation");
    }

    private static async Task RootObservationsAsync()
    {
        using var root = new OwnedRoot();
        var userPath = System.IO.Path.Combine(root.Path, "owner");
        var systemPath = System.IO.Path.Combine(root.Path, "system");
        var userPlatform = new FakePlatform { HideProcessIdentity = true };
        var systemPlatform = new FakePlatform();
        await using var user = Engine(userPath, userPlatform);
        await using var system = Engine(systemPath, systemPlatform);
        var owner = LocalIdentity.Current();
        var image = owner.ApplicationPath ?? throw new InvalidOperationException("fixture application identity unavailable");
        var verifier = new RootCodeVerifier(System.IO.Path.Combine(root.Path, "root.verifier"));
        verifier.Enroll(new RootCodeDefinition("service-observation", "AB12",
            new RootAccessPolicy("allow", [image]), RootAccessPolicy.Off));
        await using var broker = new AuthenticatedServiceBroker(System.IO.Path.Combine(root.Path, "broker"), owner.Sid, verifier,
            false, null, (request, token) => system.ExecuteAuthenticatedFrozenDirectAsync(request, token));

        var ordinary = await user.ExecuteAsync(await Wire(Request("read", rid: "ordinary-limited-read")));
        Check(ordinary.Ok && !Flag(ordinary, "process_identity_available") && ordinary.Data?.ContainsKey("start") != true,
            "ordinary read invented a process identity");
        var privilegedRead = await Wire(Request("read", rid: "root-exact-read") with { Root = "AB12" });
        var privileged = await broker.HandleSystemAsync(privilegedRead, owner, CancellationToken.None);
        Check(privileged.Ok && Text(privileged.Data!["start"]) == StartIdentity &&
            Rows(userPath) == 0 && Rows(systemPath) == 0 && systemPlatform.Starts == 0 && systemPlatform.Stops == 0,
            "explicit root read did not stay query-only and nondurable");
        var opens = systemPlatform.Opens;
        Check(!(await broker.HandleSystemAsync(privilegedRead with { Root = "ZZ99" }, owner, CancellationToken.None)).Ok,
            "wrong root read admitted");
        Check(!(await broker.HandleSystemAsync(privilegedRead, owner with { Sid = "S-1-5-21-100-200-300-1002" }, CancellationToken.None)).Ok,
            "foreign owner read admitted");
        Check(!(await broker.HandleSystemAsync(privilegedRead,
            owner with { ApplicationPath = System.IO.Path.Combine(root.Path, "foreign.exe") }, CancellationToken.None)).Ok,
            "unapproved application read admitted");
        Check(!(await broker.HandleSystemAsync(Request("list") with { Root = "AB12" }, owner, CancellationToken.None)).Ok &&
            systemPlatform.Opens == opens, "privileged list or failed authorization reached the adapter");

        using var entered = new SemaphoreSlim(0, 1);
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        systemPlatform.StepHook = step =>
        {
            if (step != "stop") return;
            entered.Release();
            release.Wait(deadline.Token);
        };
        var mutationRequest = await Wire(Request("stop", rid: "observed-original-stop") with { Root = "AB12" });
        var mutation = Task.Run(() => broker.HandleSystemAsync(mutationRequest, owner, CancellationToken.None));
        try
        {
            await entered.WaitAsync(deadline.Token);
            var original = Stored(systemPath, mutationRequest.Rid!).Receipt;
            var nativeCalls = systemPlatform.Opens;
            var activeRows = Rows(systemPath);
            var stat = await broker.HandleSystemAsync(await Wire(JobObservation("stat", original.Jid)), owner, deadline.Token);
            Check(stat.Ok && Record(stat).Jid == original.Jid && Record(stat).State == "running",
                "root job_stat did not observe the existing service Engine job");
            Check(Rows(systemPath) == activeRows && systemPlatform.Opens == nativeCalls,
                "job_stat claimed another row or entered the adapter");

            var foreign = await system.ExecuteAsync(new("ctx", new Dictionary<string, string>(), "foreign-observation-row"));
            var unknownId = Guid.NewGuid().ToString("N");
            foreach (var action in new[] { "stat", "wait" })
            {
                var foreignResult = await broker.HandleSystemAsync(await Wire(JobObservation(action, foreign.Jid)), owner, deadline.Token);
                var absentResult = await broker.HandleSystemAsync(await Wire(JobObservation(action, unknownId)), owner, deadline.Token);
                Check(!foreignResult.Ok && !absentResult.Ok && foreignResult.Err == "not_found" && absentResult.Err == "not_found" &&
                    foreignResult.Data is null && absentResult.Data is null, "foreign and unknown jobs were distinguishable");
            }
            var completions = system.ResidentCompletionCount;
            var rows = Rows(systemPath);
            using (var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                var waiting = broker.HandleSystemAsync(await Wire(JobObservation("wait", original.Jid)), owner, observation.Token);
                await Task.Delay(20, deadline.Token);
                Check(!waiting.IsCompleted, "root job_wait did not wait for its live original");
                observation.Cancel();
                var canceled = false;
                try { _ = await waiting; } catch (OperationCanceledException) { canceled = true; }
                Check(canceled && !mutation.IsCompleted && system.ResidentCompletionCount == completions &&
                    Rows(systemPath) == rows && systemPlatform.Stops == 0 && systemPlatform.Opens == nativeCalls,
                    "wait cancellation canceled or recreated the original job");
            }
            var deniedJob = await broker.HandleSystemAsync(JobObservation("stat", original.Jid) with { Root = "ZZ99" }, owner, deadline.Token);
            var deniedOwner = await broker.HandleSystemAsync(JobObservation("stat", original.Jid),
                owner with { Sid = "S-1-5-21-100-200-300-1002" }, deadline.Token);
            var deniedApplication = await broker.HandleSystemAsync(JobObservation("stat", original.Jid),
                owner with { ApplicationPath = System.IO.Path.Combine(root.Path, "foreign.exe") }, deadline.Token);
            Check(!deniedJob.Ok && !deniedOwner.Ok && !deniedApplication.Ok, "job observation bypassed root/owner/application policy");
            foreach (var forbidden in new[] { "cancel", "log", "prune", "ls", "retry" })
            {
                var refusedControl = await broker.HandleSystemAsync(JobObservation(forbidden, original.Jid), owner, deadline.Token);
                Check(!refusedControl.Ok && systemPlatform.Stops == 0 && systemPlatform.Opens == nativeCalls,
                    "service job observation broadened into a control or retry");
            }
            var bodyDenied = await broker.HandleSystemAsync(JobObservation("stat", original.Jid) with { Body = "{}" }, owner, deadline.Token);
            Check(!bodyDenied.Ok && bodyDenied.Err == "invalid", "job observer accepted a body");
            var ordinaryJob = await user.ExecuteAsync(JobObservation("stat", original.Jid) with { Root = null });
            Check(!ordinaryJob.Ok && ordinaryJob.Err == "not_found", "ordinary job query crossed into the System Engine");
            release.Set();
            var finished = await mutation.WaitAsync(deadline.Token);
            Check(finished.Ok && finished.Jid == original.Jid && systemPlatform.Stops == 1, "observed original did not complete once");
            await Retired(system);
            var finalRows = Rows(systemPath);
            var waited = await broker.HandleSystemAsync(await Wire(JobObservation("wait", original.Jid)), owner, deadline.Token);
            var terminal = await broker.HandleSystemAsync(await Wire(JobObservation("stat", original.Jid)), owner, deadline.Token);
            Check(waited.Ok && waited.Jid == original.Jid && terminal.Ok && Record(terminal).Jid == original.Jid &&
                systemPlatform.Stops == 1 && systemPlatform.Opens == nativeCalls && Rows(systemPath) == finalRows,
                "terminal observation reissued work or created another durable owner");
            Check(!JsonSerializer.Serialize(Record(terminal), Json).Contains("\"version\"", StringComparison.Ordinal),
                "job observation disclosed the service request body");
        }
        finally
        {
            release.Set();
            await mutation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task QueuedObservationAsync()
    {
        using var root = new OwnedRoot();
        var source = Request("start", rid: "queued-observation-source");
        var jid = Guid.NewGuid().ToString("N");
        using (var store = new SqliteJobStore(root.Path))
            store.Claim(new(true, jid, source.Rid!, "queued", "windows_service_start",
                Data: new Dictionary<string, object?> { ["requestIdentity"] = Parse("start").RequestIdentity }),
                new("windows_service", new Dictionary<string, string> { ["op"] = "start", [OperationRequest.BodyOmittedKey] = "1" }, source.Rid), true);
        var mode = new HeldMode();
        var platform = new FakePlatform();
        await using var engine = new AgentOpsEngine(root.Path, null, false, null, false, null,
            maintenanceMode: mode, windowsServices: new WindowsServiceOperations(platform));
        // Startup deliberately retained this queued skeleton behind maintenance.
        // Opening the fake mode makes any accidental PumpRecovery observable.
        mode.Open = true;
        var reads = mode.Reads;
        var owner = LocalIdentity.Current();
        var verifier = new RootCodeVerifier(System.IO.Path.Combine(root.Path, "root.verifier")); verifier.Enroll("AB12");
        await using var broker = new AuthenticatedServiceBroker(System.IO.Path.Combine(root.Path, "broker"), owner.Sid, verifier,
            false, null, (query, token) => engine.ExecuteAuthenticatedFrozenDirectAsync(query, token));
        var snapshot = await broker.HandleSystemAsync(await Wire(JobObservation("stat", jid)), owner, CancellationToken.None);
        Check(snapshot.Ok && Record(snapshot).State == "queued", "queued receipt observation started recovery");
        using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = broker.HandleSystemAsync(await Wire(JobObservation("wait", jid)), owner, observation.Token);
        await Task.Delay(20, observation.Token);
        Check(!waiting.IsCompleted, "queued observation fabricated a terminal result");
        observation.Cancel();
        var canceled = false;
        try { _ = await waiting; } catch (OperationCanceledException) { canceled = true; }
        Check(canceled && Stored(root.Path, source.Rid!).Receipt.State == "queued" && mode.Reads == reads &&
            platform.Opens == 0 && Rows(root.Path) == 1 && engine.ResidentCompletionCount == 0,
            "queued wait pumped recovery, hydrated/reissued work or retained a new completion");
    }

    private static async Task LostBodyAsync()
    {
        using var root = new OwnedRoot();
        var request = Request("start", rid: "queued-body-lost");
        var invocation = WindowsServiceOperationContract.Parse(request);
        using (var store = new SqliteJobStore(root.Path))
        {
            store.Claim(new(true, Guid.NewGuid().ToString("N"), request.Rid!, "queued", "windows_service_start",
                Data: new Dictionary<string, object?> { ["requestIdentity"] = invocation.RequestIdentity }),
                new("windows_service", new Dictionary<string, string> { ["op"] = "start", [OperationRequest.BodyOmittedKey] = "1" }, request.Rid), true);
        }
        var platform = new FakePlatform();
        await using var engine = Engine(root.Path, platform);
        var frozen = request with { Arg = new Dictionary<string, string> { ["op"] = "start", ["__execution_context_system"] = "1" } };
        var result = await engine.ExecuteAuthenticatedFrozenDirectAsync(frozen);
        Check(result.State == "recovery_required" && result.Partial && Flag(result, "outcome_unknown") &&
            result.Data?.ContainsKey("action_issued") != true && platform.Opens == 0 && platform.Starts == 0,
            "lost body recovery recreated a service action or hid uncertainty");
        var replay = await engine.ExecuteAuthenticatedFrozenDirectAsync(frozen);
        Check(replay.Jid == result.Jid && replay.Partial && Flag(replay, "outcome_unknown") && platform.Opens == 0 && platform.Starts == 0,
            "same-RID recovery reissued a lost service body");
        var owner = LocalIdentity.Current();
        var verifier = new RootCodeVerifier(System.IO.Path.Combine(root.Path, "root.verifier")); verifier.Enroll("AB12");
        await using var broker = new AuthenticatedServiceBroker(System.IO.Path.Combine(root.Path, "broker"), owner.Sid, verifier,
            false, null, (query, token) => engine.ExecuteAuthenticatedFrozenDirectAsync(query, token));
        var observed = await broker.HandleSystemAsync(await Wire(JobObservation("stat", result.Jid)), owner, CancellationToken.None);
        var waited = await broker.HandleSystemAsync(await Wire(JobObservation("wait", result.Jid)), owner, CancellationToken.None);
        Check(observed.Ok && Record(observed).Partial && Flag(Record(observed), "outcome_unknown") &&
            waited.Partial && waited.State == "recovery_required" && Flag(waited, "outcome_unknown") &&
            platform.Opens == 0 && platform.Starts == 0 && Rows(root.Path) == 1,
            "root lost-body observation hid uncertainty or reissued work");
        var retained = Stored(root.Path, request.Rid!);
        Check(retained.Request is null && Text(retained.Receipt.Data!["requestIdentity"]) == invocation.RequestIdentity,
            "lost body recovery erased RID identity");
    }

    private static async Task SurfacesAsync()
    {
        var definition = PaoProgram.UnifiedMcpToolDefinition();
        var advertised = definition["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        await using var capture = new Capture();
        foreach (var action in WindowsServiceOperationContract.Actions)
        {
            var name = WindowsServiceOperationContract.PublicName(action);
            Check(advertised.Contains(name), "service operation missing from discovery");
            var help = PaoProgram.UnifiedMcpOperationHelp(name)!;
            Check(help["availability"]!.GetValue<string>() == "unobserved" &&
                help["executionRoute"]!.GetValue<string>().Contains(WindowsServiceOperationContract.IsReadOnly(action) ? "owner" : "LocalSystem", StringComparison.OrdinalIgnoreCase),
                "help implied readiness or wrong route");
            var arguments = new JsonObject { ["op"] = name, ["text"] = Body(action), ["rid"] = "captured-" + action };
            if (!WindowsServiceOperationContract.IsReadOnly(action)) arguments["root"] = "AB12";
            using var json = JsonDocument.Parse(arguments.ToJsonString());
            var previous = capture.Calls;
            _ = await PaoProgram.CallUnifiedMcpToolAsync(json.RootElement, capture, CancellationToken.None, defaultWs: "must-not-apply");
            Check(capture.Calls == previous + 1 && capture.Last!.Op == "windows_service" &&
                capture.Last.Cwd is null && capture.Last.Ws is null && capture.Last.Body == Body(action),
                "captured unified service shape changed");
        }
        var readHelp = PaoProgram.UnifiedMcpOperationHelp("windows_service_read")!;
        Check(readHelp["allowed"]!.AsArray().Any(value => value!.GetValue<string>() == "root") &&
            !readHelp["required"]!.AsArray().Any(value => value!.GetValue<string>() == "root"),
            "privileged read was missing or made the default");
        using (var rootRead = JsonDocument.Parse(new JsonObject { ["op"] = "windows_service_read", ["text"] = Body("read"), ["root"] = "AB12" }.ToJsonString()))
        {
            var prior = capture.Calls;
            _ = await PaoProgram.CallUnifiedMcpToolAsync(rootRead.RootElement, capture, CancellationToken.None, defaultWs: "must-not-apply");
            Check(capture.Calls == prior + 1 && capture.Last!.Root == "AB12" && capture.Last.Ws is null && capture.Last.Cwd is null,
                "explicit read root was erased or acquired workspace state");
        }
        var serviceJid = new string('1', 32);
        foreach (var action in new[] { "stat", "wait" })
        {
            var help = PaoProgram.UnifiedMcpOperationHelp("job_" + action)!;
            Check(help["allowed"]!.AsArray().Any(value => value!.GetValue<string>() == "root") &&
                help["constraints"]!.GetValue<string>().Contains("Windows-service mutation", StringComparison.Ordinal),
                "scoped root job observation missing from help");
            using var query = JsonDocument.Parse(new JsonObject { ["op"] = "job_" + action, ["jid"] = serviceJid, ["root"] = "AB12" }.ToJsonString());
            var prior = capture.Calls;
            _ = await PaoProgram.CallUnifiedMcpToolAsync(query.RootElement, capture, CancellationToken.None, defaultWs: "must-not-apply");
            Check(capture.Calls == prior + 1 && capture.Last!.Root == "AB12" && capture.Last.Body is null &&
                capture.Last.Ws is null && capture.Last.Cwd is null && capture.Last.Arg["jid"] == serviceJid,
                "root job frontend routed or retained another shape");
            using var cliOut = new StringWriter(); using var cliError = new StringWriter();
            Check(await PaoProgram.RunCliAsync(["job", action, serviceJid, "root=AB12"], capture, cliOut, cliError, CancellationToken.None) == 0 &&
                capture.Last!.Root == "AB12" && capture.Last.Cwd is null, "root job CLI rejected");
        }
        using (var ordinaryJob = JsonDocument.Parse(new JsonObject { ["op"] = "job_stat", ["jid"] = serviceJid }.ToJsonString()))
        {
            _ = await PaoProgram.CallUnifiedMcpToolAsync(ordinaryJob.RootElement, capture, CancellationToken.None, defaultWs: "ordinary-default");
            Check(capture.Last!.Root is null && capture.Last.Ws == "ordinary-default", "ordinary job default route changed");
        }
        var beforeInvalid = capture.Calls;
        foreach (var action in new[] { "ls", "cancel", "log", "prune" })
        {
            using var query = JsonDocument.Parse(new JsonObject { ["op"] = "job_" + action, ["jid"] = serviceJid, ["root"] = "AB12" }.ToJsonString());
            _ = await PaoProgram.CallUnifiedMcpToolAsync(query.RootElement, capture, CancellationToken.None);
        }
        using (var list = JsonDocument.Parse(new JsonObject { ["op"] = "windows_service_list", ["text"] = Body("list"), ["root"] = "AB12" }.ToJsonString()))
            _ = await PaoProgram.CallUnifiedMcpToolAsync(list.RootElement, capture, CancellationToken.None);
        using (var duplicate = JsonDocument.Parse($$"""{"op":"job_stat","jid":"{{serviceJid}}","jid":"{{serviceJid}}","root":"AB12"}"""))
            _ = await PaoProgram.CallUnifiedMcpToolAsync(duplicate.RootElement, capture, CancellationToken.None);
        Check(capture.Calls == beforeInvalid, "root job controls, duplicate observation fields or privileged list were advertised/admitted");

        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await PaoProgram.RunCliAsync(["windows_service", "stop", "--stdin", "root=AB12", "rid=captured-cli"],
            capture, output, error, CancellationToken.None, new StringReader(Body("stop")));
        Check(exit == 0 && capture.Last!.Root == "AB12" && capture.Last.Body == Body("stop"), "typed service CLI rejected");
        var before = capture.Calls;
        using var legacy = JsonDocument.Parse("{\"op\":\"service_status\",\"service\":\"Spooler\",\"root\":\"AB12\"}");
        _ = await PaoProgram.CallUnifiedMcpToolAsync(legacy.RootElement, capture, CancellationToken.None);
        Check(capture.Calls == before, "legacy product-specific service contract broadened");
        using var noRoot = JsonDocument.Parse(new JsonObject { ["op"] = "windows_service_stop", ["text"] = Body("stop"), ["rid"] = "missing-root" }.ToJsonString());
        _ = await PaoProgram.CallUnifiedMcpToolAsync(noRoot.RootElement, capture, CancellationToken.None);
        Check(capture.Calls == before, "general mutation without explicit root dispatched");
    }

    // Optional native read-only qualification; root must select deliberately.
    internal static Task RunNativeReadOnlyAsync()
    {
        var platform = new NativeWindowsServicePlatform();
        var page = platform.List(Parse("list", "{\"version\":1,\"max\":1}"), CancellationToken.None);
        Check(page.Services.Count <= 1 && page.Visibility == "caller_queryable_services_only", "native inventory bound");
        using var handle = platform.Open(CortexProductIdentity.ServiceName, "read");
        var item = handle.Read();
        Check(item.Service == CortexProductIdentity.ServiceName && WindowsServiceOperationContract.IsDigest(item.ConfigSha), "native safe service metadata");
        Console.WriteLine("PASS native local SCM inventory/read only; no service control or configuration writes");
        return Task.CompletedTask;
    }

    private static OperationRequest JobObservation(string action, string jid) => new("job",
        new Dictionary<string, string> { ["op"] = action, ["jid"] = jid }, Guid.NewGuid().ToString("N"), Root: "AB12");
    private static OperationReceipt Record(OperationReceipt observation) =>
        JsonSerializer.Deserialize<OperationReceipt>(JsonSerializer.Serialize(observation.Data!["rec"], Json), Json)!;

    private static async Task<OperationRequest> Wire(OperationRequest request)
    {
        using var frame = new MemoryStream();
        await BinaryProtocol.WriteAsync(frame, request);
        frame.Position = 0;
        var received = await BinaryProtocol.ReadAsync<OperationRequest>(frame);
        Check(received.Body == request.Body && !received.Arg.ContainsKey(OperationRequest.BodyOmittedKey),
            "live framed service request lost its body or acquired a durable-only marker");
        return received;
    }

    private static async Task OwnerAdministratorAsync()
    {
        const string ownerSid = "S-1-5-21-100-200-300-1001";
        const int ownerSession = 7;
        var peer = new LocalPeer(ownerSid, ownerSession, Elevated: true);
        WindowsServiceInvocation Elevated(string action, string? body = null) =>
            WindowsServiceOperationContract.Parse(Request(action, body) with
                { Arg = new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac" } });
        foreach (var action in WindowsServiceOperationContract.Actions)
        {
            var original = Parse(action);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                family = "windows_service", version = 1, action = original.Action, service = original.Service, configSha = original.ConfigSha,
                expectedState = original.ExpectedState, processId = original.ProcessId, processStart = original.ProcessStart,
                mode = original.Mode, timeout = original.TimeoutSeconds, maximum = original.Maximum, after = original.After, pattern = original.Pattern
            });
            try { Check(original.RequestIdentity == Convert.ToHexStringLower(SHA256.HashData(bytes)), "legacy service fingerprint changed without selector"); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            Refuse(() => WindowsServiceOperationContract.Parse(Request(action) with
                { Arg = new Dictionary<string, string> { ["op"] = action, ["wait"] = "false" } }));
            if (action == "list") { Refuse(() => Elevated(action)); continue; }
            var elevated = Elevated(action);
            Check(elevated.RequiresOwnerElevation && elevated.RequiresDurableHelper && elevated.IsMutation == original.IsMutation &&
                elevated.RequestIdentity != original.RequestIdentity, "service UAC route/native mutation classification");
            foreach (var wait in new[] { "true", "false" })
                Check(WindowsServiceOperationContract.Parse(Request(action) with { Arg = new Dictionary<string, string>
                    { ["op"] = action, ["elevate"] = "uac", ["wait"] = wait } }).RequestIdentity == elevated.RequestIdentity,
                    "service helper wait changed request identity");
            Refuse(() => WindowsServiceOperationContract.Parse(Request(action) with
                { Arg = new Dictionary<string, string> { ["op"] = action, ["elevate"] = "UAC" } }));
            Refuse(() => WindowsServiceOperationContract.Parse(Request(action, rid: null) with
                { Arg = new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac" } }));
            Refuse(() => WindowsServiceOperationContract.Parse(Request(action) with
                { Root = "AB12", Arg = new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac" } }));
        }

        var platform = new FakePlatform();
        var legacy = new WindowsServiceOperations(platform);
        var legacyRefusal = await legacy.ExecuteAsync(Elevated("read"), CancellationToken.None);
        Check(!legacyRefusal.Ok && legacyRefusal.Error == "service_owner_elevation_required" && platform.Opens == 0, "legacy provider executed UAC route");

        foreach (var wrong in new[]
        {
            new LocalPeer(ownerSid, ownerSession, Elevated: false), new LocalPeer(ownerSid, ownerSession + 1, Elevated: true),
            new LocalPeer("S-1-5-18", 0, Elevated: true), new LocalPeer("S-1-5-21-100-200-300-9999", ownerSession, Elevated: true)
        })
            Refuse(() => WindowsServiceOperations.ForOwnerAdministrator(ownerSid, ownerSession, () => wrong, new FakePlatform()));

        var provider = WindowsServiceOperations.ForOwnerAdministrator(ownerSid, ownerSession, () => peer, platform);
        var unmarked = await provider.ExecuteAsync(Parse("read"), CancellationToken.None);
        Check(!unmarked.Ok && unmarked.Error == "service_elevation_route_required" && platform.Opens == 0, "High provider adopted unmarked route");
        Refuse(() => provider.List(Parse("list"), CancellationToken.None));
        foreach (var action in new[] { "read", "start", "stop", "restart", "startup" })
        {
            platform.Reset(); peer = new(ownerSid, ownerSession, Elevated: true);
            if (action == "start") platform.State = "stopped";
            var result = await provider.ExecuteAsync(Elevated(action), CancellationToken.None);
            Check(result.Ok && !result.OutcomeUnknown && platform.Opens == 1 && platform.Disposals == 1,
                "injected owner-admin service action/cleanup " + action);
            if (action == "read") Check(!result.ActionIssued && result.Previous == result.Current, "read must remain native readonly");
        }

        platform.Reset();
        var unchanged = JsonNode.Parse(Body("startup"))!.AsObject(); unchanged["mode"] = "manual";
        var noop = await provider.ExecuteAsync(Elevated("startup", unchanged.ToJsonString()), CancellationToken.None);
        Check(noop.Ok && !noop.ActionIssued && !noop.OutcomeUnknown && noop.Phase == "unchanged" &&
            noop.Previous == noop.Current && platform.TypeWrites == 0 && platform.DelayWrites == 0, "valid unchanged startup was forced into issued result");

        platform.Reset();
        foreach (var action in new[] { "start", "stop", "restart", "startup" })
        {
            var own = JsonNode.Parse(Body(action))!.AsObject(); own["service"] = CortexProductIdentity.ServiceName;
            var refusal = await provider.ExecuteAsync(Elevated(action, own.ToJsonString()), CancellationToken.None);
            Check(!refusal.Ok && !refusal.ActionIssued && refusal.Error == "service_requires_cortex_control" && platform.Opens == 0,
                "own Cortex service escaped dedicated control");
        }
        var ownRead = JsonNode.Parse(Body("read"))!.AsObject(); ownRead["service"] = CortexProductIdentity.ServiceName;
        Check((await provider.ExecuteAsync(Elevated("read", ownRead.ToJsonString()), CancellationToken.None)).Ok && platform.Opens == 1,
            "own Cortex service observation unexpectedly refused");

        platform.Reset(); peer = new(ownerSid, ownerSession, Elevated: true);
        platform.ReadHook = count => { if (count == 2) peer = peer with { Session = ownerSession + 1 }; };
        var beforeWrite = await provider.ExecuteAsync(Elevated("stop"), CancellationToken.None);
        Check(!beforeWrite.Ok && !beforeWrite.ActionIssued && beforeWrite.Error == "service_owner_context_required" &&
            beforeWrite.Previous is null && beforeWrite.Current is null && platform.Stops == 0 && platform.Disposals == 1,
            "owner loss before write reached SCM or disclosed snapshot");

        platform.Reset(); peer = new(ownerSid, ownerSession, Elevated: true);
        platform.StepHook = step => { if (step == "type") peer = peer with { Elevated = false }; };
        var afterWrite = await provider.ExecuteAsync(Elevated("startup"), CancellationToken.None);
        Check(!afterWrite.Ok && afterWrite.ActionIssued && afterWrite.OutcomeUnknown && afterWrite.Error == "service_outcome_unknown" &&
            afterWrite.Previous is null && afterWrite.Current is null && platform.TypeWrites == 1 && platform.DelayWrites == 0,
            "owner loss after write erased issuance or continued writes");

        platform.Reset(); peer = new(ownerSid, ownerSession, Elevated: true);
        var automatic = JsonNode.Parse(Body("startup"))!.AsObject(); automatic["mode"] = "automatic_delayed";
        platform.FailStep = "delay";
        var partial = await provider.ExecuteAsync(Elevated("startup", automatic.ToJsonString()), CancellationToken.None);
        Check(!partial.Ok && partial.ActionIssued && partial.OutcomeUnknown && partial.NativeError == 5 &&
            platform.TypeWrites == 1 && platform.Startup == "automatic" && platform.DelayWrites == 0,
            "two-write startup failure rolled back or lost uncertainty");

        platform.Reset(); peer = new(ownerSid, ownerSession, Elevated: true);
        platform.DisposeHook = () => peer = peer with { Sid = "S-1-5-21-100-200-300-9999" };
        var cleanupLoss = await provider.ExecuteAsync(Elevated("read"), CancellationToken.None);
        Check(!cleanupLoss.Ok && cleanupLoss.Current is null && cleanupLoss.Previous is null &&
            cleanupLoss.Error == "service_owner_context_required", "post-handle cleanup owner fence missing");

        platform.Reset(); peer = new(ownerSid, ownerSession, Elevated: true);
        platform.State = "stopped"; platform.PendingReadState = true;
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        platform.StateHook = () => polling.TrySetResult();
        var originalWait = provider.ExecuteAsync(Elevated("start"), CancellationToken.None);
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        peer = peer with { Session = ownerSession + 1 };
        var waitLoss = await originalWait.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!waitLoss.Ok && waitLoss.ActionIssued && waitLoss.OutcomeUnknown && platform.Starts == 1 && platform.Disposals == 1 &&
            waitLoss.Current is null && waitLoss.Previous is null, "owner loss after await abandoned native ownership or payload");

        platform.Reset(); peer = new(ownerSid, ownerSession, Elevated: true);
        using var cancellation = new CancellationTokenSource();
        platform.State = "stopped"; platform.StepHook = step => { if (step == "start") cancellation.Cancel(); };
        var lateCancel = await provider.ExecuteAsync(Elevated("start"), cancellation.Token);
        Check(!lateCancel.Ok && lateCancel.ActionIssued && lateCancel.OutcomeUnknown && platform.Starts == 1 && platform.Stops == 0,
            "post-entry cancellation replayed or compensated service action");
    }

    private static AgentOpsEngine Engine(string root, FakePlatform platform) => new(root, null, false, null, false, null,
        maintenanceMode: new OpenMode(), windowsServices: new WindowsServiceOperations(platform));
    private static OperationRequest Request(string action, string? body = null, string? rid = "fixture-service-rid") =>
        new("windows_service", new Dictionary<string, string> { ["op"] = action }, rid, Body: body ?? Body(action));
    private static WindowsServiceInvocation Parse(string action, string? body = null) => WindowsServiceOperationContract.Parse(Request(action, body));
    private static string Body(string action)
    {
        var body = new JsonObject { ["version"] = 1 };
        if (action == "list") return body.ToJsonString();
        body["service"] = Service;
        if (action == "read") return body.ToJsonString();
        body["configSha"] = Config; body["expect"] = action == "start" ? "stopped" : "running";
        if (action is "stop" or "restart") { body["pid"] = 4242; body["start"] = StartIdentity; }
        if (action == "startup") body["mode"] = "disabled";
        return body.ToJsonString();
    }
    private static async Task Retired(AgentOpsEngine engine)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.ResidentJobCount != 0 || engine.ResidentCompletionCount != 0) await Task.Delay(5, deadline.Token);
    }
    private static SqliteConnection Connection(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(root, "agentops.db"),
            Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); connection.Open(); return connection;
    }
    private static long Rows(string root)
    { using var db = Connection(root); using var sql = db.CreateCommand(); sql.CommandText = "SELECT COUNT(*) FROM jobs"; return Convert.ToInt64(sql.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private sealed record StoredRow(OperationReceipt Receipt, OperationRequest? Request, string Raw);
    private static StoredRow Stored(string root, string rid)
    {
        using var db = Connection(root); using var sql = db.CreateCommand();
        sql.CommandText = "SELECT receipt,request FROM jobs WHERE rid=$rid"; sql.Parameters.AddWithValue("$rid", rid);
        using var reader = sql.ExecuteReader(); Check(reader.Read(), "owned service receipt missing");
        var receipt = reader.GetString(0); var request = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new(JsonSerializer.Deserialize<OperationReceipt>(receipt, Json)!, request is null ? null : JsonSerializer.Deserialize<OperationRequest>(request, Json), receipt + request);
    }
    private sealed class HeldMode : IMaintenanceModeSource
    {
        internal bool Open;
        internal int Reads;
        public MaintenanceModeRecord? Read()
        {
            Interlocked.Increment(ref Reads);
            return Open ? null : new(1, "service-observation-fixture", "active", "fixture-jid", "fixture-rid", "fixture-scope",
                "fixture-task", "S-1-5-21-100-200-300-1001", 7, "fixture-service", null, 1);
        }
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }

    private sealed class OpenMode : IMaintenanceModeSource
    { public MaintenanceModeRecord? Read() => null; public bool IsAcknowledged(MaintenanceEffect effect) => false; }
    private sealed class OwnedRoot : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windows-service-fixture-" + Guid.NewGuid().ToString("N"));
        internal OwnedRoot() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private sealed class Capture : IOperationDispatcher
    {
        internal int Calls; internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        { Calls++; Last = request; return Task.FromResult(new OperationReceipt(true, "captured", request.Rid ?? "read", "done", OperationNames.Public(request))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakePlatform : IWindowsServicePlatform, IWindowsServiceHandle
    {
        internal int Opens, Disposals, Starts, Stops, TypeWrites, DelayWrites, Reads;
        internal string State = "running", Startup = "manual", ConfigSha = Config, ProcessStart = StartIdentity;
        internal bool Delay, DenyOpen, HideProcessIdentity; internal string? FailStep;
        internal Action<int>? ReadHook; internal Action<string>? StepHook;
        internal Action? DisposeHook, StateHook; internal bool PendingReadState;
        public WindowsServicePage List(WindowsServiceInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new([new(Service, "Owned fake service", State, 0x10, 4242)], null, false, false); }
        public IWindowsServiceHandle Open(string service, string action)
        { Opens++; if (DenyOpen) throw new Win32Exception(5); return this; }
        public WindowsServiceDetails Read()
        { ReadHook?.Invoke(++Reads); return new(Service, "Owned fake service", State, 0x10, Startup, Delay, ConfigSha,
            State == "stopped" ? null : 4242, State == "stopped" || HideProcessIdentity ? null : ProcessStart, 1); }
        public string ReadState() { StateHook?.Invoke(); return PendingReadState ? "start_pending" : State; }
        public void Start() { Step("start"); Starts++; State = "running"; }
        public void Stop() { Step("stop"); Stops++; State = "stopped"; }
        public void SetStartType(uint value) { Step("type"); TypeWrites++; Startup = value == 2 ? Delay ? "automatic_delayed" : "automatic" : value == 3 ? "manual" : "disabled"; Rehash(); }
        public void SetDelayedAutoStart(bool value) { Step("delay"); DelayWrites++; Delay = value; if (Startup.StartsWith("automatic", StringComparison.Ordinal)) Startup = value ? "automatic_delayed" : "automatic"; Rehash(); }
        private void Rehash() => ConfigSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Startup + Delay)));
        private void Step(string step) { if (FailStep == step) throw new Win32Exception(step == "stop" ? 1051 : 5); StepHook?.Invoke(step); }
        public void Dispose() { Disposals++; DisposeHook?.Invoke(); }
        internal void Reset()
        { Opens = Disposals = Starts = Stops = TypeWrites = DelayWrites = Reads = 0; State = "running"; Startup = "manual";
            ConfigSha = Config; ProcessStart = StartIdentity; Delay = DenyOpen = HideProcessIdentity = false; FailStep = null; StepHook = null; ReadHook = null; DisposeHook = StateHook = null; PendingReadState = false; }
    }
    private static bool Flag(OperationReceipt receipt, string key) => receipt.Data?.GetValueOrDefault(key) is true or JsonElement { ValueKind: JsonValueKind.True };
    private static string? Text(object? value) => value is JsonElement element ? element.GetString() : value as string;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Refuse(Action action) { try { action(); } catch (AgentOpsException) { return; } throw new InvalidOperationException("invalid general-service input accepted"); }
}
