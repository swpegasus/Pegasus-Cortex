using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Task-specific data-only flow, shared capability registry and retained fake
// child processes. No UAC, COM, task/provider, token or native process call.
internal static class ScheduledTaskElevationTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private const int Session = 7;
    private const string Host = @"C:\fixture\PegasusCortex.Host.exe";
    private const string Jid = "0123456789abcdef0123456789abcdef";
    private const string Rid = "scheduled-task-uac-fixture";
    private static readonly string Sha = new('a', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static int _assertions;

    internal static EngineFixture CreateEngineFixture() => new();
    internal static async Task RunAsync()
    {
        _assertions = 0;
        SelectorAndLegacyFingerprint();
        WireContracts();
        await CompletionAndChildLifetimeAsync();
        await PeerAndPurposeAsync();
        await MalformedCompletionAsync();
        await MissingCompletionAndCancellationAsync();
        await BeforePayloadAsync();
        await ReentrantIssuedExitAsync();
        Console.WriteLine("PASS scheduled task UAC: " + _assertions +
            " injected selector/fingerprint, closed nested wire/target, same-owner exact-child/purpose, persist-before-payload, first-completion, cancellation/retained-child and missing-evidence checks; no actual UAC/COM/task action");
    }

    private static void SelectorAndLegacyFingerprint()
    {
        foreach (var action in WindowsTaskOperationContract.Actions)
        {
            var request = Request(action, elevated: false);
            var ordinary = WindowsTaskOperationContract.Parse(request);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                family = "windows_task", version = 1, action = ordinary.Action, task = ordinary.TaskPath,
                folder = ordinary.Folder, kind = ordinary.Kind, maximum = ordinary.Maximum, after = ordinary.AfterPath,
                taskSha = ordinary.TaskSha, instance = ordinary.InstanceGuid, definition = ordinary.Definition, tool = ordinary.Tool
            });
            try { Check(ordinary.RequestIdentity == Convert.ToHexStringLower(SHA256.HashData(bytes)), "legacy route fingerprint unchanged"); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (WindowsTaskOperationContract.IsOwnerElevationAction(action))
            {
                var elevated = WindowsTaskOperationContract.Parse(Request(action));
                Check(elevated.RequiresOwnerElevation && elevated.RequiresDurableHelper && elevated.IsMutation == ordinary.IsMutation &&
                    elevated.RequestIdentity != ordinary.RequestIdentity, "route pinned separately from native mutation");
                var wait = Request(action) with { Arg = new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac", ["wait"] = "false" } };
                Check(!WindowsTaskOperationContract.Parse(wait).Wait, "all helper calls support deferred wait");
                ContractRefused(() => WindowsTaskOperationContract.Parse(Request(action) with { Rid = null }));
                ContractRefused(() => WindowsTaskOperationContract.Parse(Request(action) with { Root = "AB12" }));
            }
            else ContractRefused(() => WindowsTaskOperationContract.Parse(Request(action)));
        }
        foreach (var selector in new[] { "", "UAC", "system", "administrator", "auto" })
            ContractRefused(() => WindowsTaskOperationContract.Parse(Request("read") with
                { Arg = new Dictionary<string, string> { ["op"] = "read", ["elevate"] = selector } }));
        var owner = WindowsTaskTests.CreateEngineFixture(Sid, () => new(Sid, Session));
        var denied = owner.Operations.Execute(WindowsTaskOperationContract.Parse(Request("run")));
        Check(!denied.Ok && !denied.ActionIssued && denied.Error == "task_owner_elevation_required" && owner.NativeCalls == 0, "ordinary provider cannot execute elevated route");
        var system = WindowsTaskTests.CreateEngineFixture(Sid, () => new("S-1-5-18", 0, Elevated: true), WindowsTaskAuthority.System);
        Check(system.Operations.Execute(WindowsTaskOperationContract.Parse(Request("read"))).Error == "task_owner_elevation_required" &&
            system.NativeCalls == 0, "System never substitutes for owner UAC");
        var mismatchedSession = new WindowsTaskOperations(Sid, () => new(Sid, Session + 1, Elevated: true), owner.Platform,
            authority: WindowsTaskAuthority.OwnerAdmin, expectedAdministratorSession: Session);
        Check(!mismatchedSession.IsCurrentContext(), "explicit expected session cannot be replaced by sampler session");
    }

    private static void WireContracts()
    {
        foreach (var action in new[] { "read", "instances", "run", "stop", "enable", "disable", "remove" })
        {
            var payload = Payload(action);
            var encoded = ScheduledTaskElevationContract.SerializeRequest(payload);
            var parsed = ScheduledTaskElevationContract.ParseRequest(encoded);
            Check(parsed == payload && !parsed.ToString().Contains("taskSha", StringComparison.Ordinal), "closed task payload round trip");
            var invocation = WindowsTaskOperationContract.Parse(ScheduledTaskElevationContract.Reconstruct(parsed));
            Check(invocation.RequiresOwnerElevation && invocation.RequestIdentity == payload.RequestIdentity, "both ends reconstruct route");
            var valid = ValidResult(action);
            var body = ScheduledTaskElevationContract.SerializeCompletion(payload, valid);
            var completion = ScheduledTaskElevationContract.ParseCompletion(Control(ScheduledTaskElevationContract.CompleteOperation, Sha, body), payload);
            Check(completion.Result?.Action == action && completion.RequestIdentity == payload.RequestIdentity, "typed bounded completion");
            var extra = JsonNode.Parse(encoded)!.AsObject(); extra["principal"] = null;
            WireRefused(() => ScheduledTaskElevationContract.ParseRequest(extra.ToJsonString()));
            WireRefused(() => ScheduledTaskElevationContract.ParseRequest(encoded.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
            WireRefused(() => ScheduledTaskElevationContract.ValidateRequest(payload with { RequestIdentity = new string('b', 64) }));
            WireRefused(() => ScheduledTaskElevationContract.ParseCompletion(Control(ScheduledTaskElevationContract.CompleteOperation, Sha, body),
                payload with { Jid = "ffffffffffffffffffffffffffffffff" }));
            var target = JsonNode.Parse(body)!.AsObject();
            if (target["result"]!["current"] is JsonObject current) current["path"] = @"\Other\Task";
            else target["result"]!["previous"]!["path"] = @"\Other\Task";
            WireRefused(() => ScheduledTaskElevationContract.ParseCompletion(Control(ScheduledTaskElevationContract.CompleteOperation, Sha, target.ToJsonString()), payload));
            var nested = JsonNode.Parse(body)!.AsObject(); nested["result"]!["command"] = "forbidden";
            WireRefused(() => ScheduledTaskElevationContract.ParseCompletion(Control(ScheduledTaskElevationContract.CompleteOperation, Sha, nested.ToJsonString()), payload));
            var duplicate = body.Replace("\"ok\":true", "\"ok\":true,\"ok\":true", StringComparison.Ordinal);
            WireRefused(() => ScheduledTaskElevationContract.ParseCompletion(Control(ScheduledTaskElevationContract.CompleteOperation, Sha, duplicate), payload));
        }
        var read = Payload("read");
        WireRefused(() => ScheduledTaskElevationContract.ValidateRequest(read with { Body = "{\"version\":1,\"task\":\"\\\\CortexFixture\\\\Task\",\"xml\":null}" }));
        WireRefused(() => ScheduledTaskElevationContract.SerializeCompletion(read, ValidResult("read") with { ActionIssued = true }));
        WireRefused(() => ScheduledTaskElevationContract.SerializeCompletion(read, ValidResult("read") with { Error = "fixture-private-marker", Ok = false }));
        WireRefused(() => ScheduledTaskElevationContract.SerializeCompletion(read, null));
        WireRefused(() => ScheduledTaskElevationContract.SerializeCompletion(read, null, "arbitrary"));
        var missing = ScheduledTaskElevationContract.SerializeCompletion(read, null, "scheduled_task_helper_failed");
        Check(ScheduledTaskElevationContract.ParseCompletion(Control(ScheduledTaskElevationContract.CompleteOperation, Sha, missing), read).Result is null,
            "missing native evidence remains null");
        foreach (var altered in new[]
        {
            Control(ScheduledTaskElevationContract.RedeemOperation, Sha) with { Body = "{}" },
            Control(ScheduledTaskElevationContract.RedeemOperation, Sha) with { Root = "AB12" },
            Control(ScheduledTaskElevationContract.RedeemOperation, Sha) with { Cwd = @"C:\fixture" },
            Control(ScheduledTaskElevationContract.RedeemOperation, Sha) with { Detail = true },
            Control(ScheduledTaskElevationContract.RedeemOperation, Sha) with { Arg = new Dictionary<string, string> { ["ticket"] = Sha, ["op"] = "run" } }
        }) WireRefused(() => ScheduledTaskElevationContract.ParseTicket(altered));
    }

    private static async Task CompletionAndChildLifetimeAsync()
    {
        using var fixture = CreateEngineFixture();
        var payload = Payload("read");
        var issued = 0;
        CertificateElevationProcessIdentity? originalGeneration = null;
        var pending = fixture.Elevation.BeginAsync(payload, (CertificateElevationProcessIdentity process) =>
        {
            originalGeneration = process;
            var original = fixture.Launcher.LastProcess;
            Check(original is not null && process == original.Identity && process.Pid > 0 &&
                !original.Disposed, "exact original PID/start supplied before payload release");
            issued++;
        }, CancellationToken.None);
        await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
        var redeemed = await fixture.RedeemAsync();
        Check(originalGeneration == fixture.Launcher.LastProcess!.Identity && issued == 1,
            "redeemed payload follows generation persistence callback");
        Check(redeemed.Ok && issued == 1 && redeemed.Data?.GetValueOrDefault("payload") is ScheduledTaskElevationRequest p &&
            p.RequestIdentity == payload.RequestIdentity && !pending.IsCompleted, "persist before payload while original child retained");
        Check(!(await fixture.RedeemAsync()).Ok && issued == 1 && fixture.Starts == 1, "one redemption/no relaunch");
        var completed = await fixture.CompleteAsync(payload, ValidResult("read"));
        Check(completed.Ok && !pending.IsCompleted && !fixture.Launcher.LastProcess!.Disposed, "completion receipt does not release original child");
        fixture.Exit(0);
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(outcome.PayloadIssued && outcome.Result is { Ok: true, ActionIssued: false } && outcome.Failure is null &&
            outcome.Exit == 0 && fixture.Launcher.LastProcess is { Disposed: true }, "result only after exact original child exit");

        using var highMutation = CreateEngineFixture();
        var mutationPayload = Payload("run");
        var mutationPending = highMutation.Elevation.BeginAsync(mutationPayload, () => { }, CancellationToken.None);
        await highMutation.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
        Check((await highMutation.RedeemAsync()).Ok, "mutation redeemed");
        Check((await highMutation.CompleteAsync(mutationPayload, ValidResult("run"))).Ok, "mutation completion admitted");
        highMutation.Exit(0);
        Check((await mutationPending.WaitAsync(TimeSpan.FromSeconds(5))).Result is { ActionIssued: true, OutcomeUnknown: false }, "actual mutation evidence preserved");
    }

    private static async Task PeerAndPurposeAsync()
    {
        using var fixture = CreateEngineFixture();
        var payload = Payload("read");
        var issued = 0;
        var pending = fixture.Elevation.BeginAsync(payload, () => issued++, CancellationToken.None);
        var ticket = await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var peer in new[]
        {
            fixture.HighPeer with { Elevated = false }, fixture.HighPeer with { Session = Session + 1 },
            fixture.HighPeer with { Sid = "S-1-5-18" }, fixture.HighPeer with { ProcessId = 999 },
            fixture.HighPeer with { ApplicationPath = @"C:\other\PegasusCortex.Host.exe" }
        })
            Check(!(await fixture.Elevation.HandleAsync(Control(ScheduledTaskElevationContract.RedeemOperation, ticket), peer, CancellationToken.None)).Ok &&
                issued == 0, "wrong owner/session/high/image/generation cannot redeem");
        fixture.Launcher.CaptureOverride = fixture.Launcher.LastProcess!.Identity with { Start = "2026-11-22T00:00:01.0000000Z" };
        Check(!(await fixture.RedeemAsync()).Ok && issued == 0, "same PID wrong start rejected");
        fixture.Launcher.CaptureOverride = null;
        var cross = await fixture.Coordinator.HandleAsync(OwnerElevationPurpose.CertificateStore,
            Control(ScheduledTaskElevationContract.RedeemOperation, ticket), fixture.HighPeer, CancellationToken.None);
        Check(!cross.Ok && issued == 0, "task capability cannot cross certificate purpose");
        Check((await fixture.RedeemAsync()).Ok && issued == 1, "valid peer remains usable after rejected foreign callers");
        await fixture.CompleteAsync(payload, ValidResult("read")); fixture.Exit(0);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Elevation.Dispose();
        var certificate = fixture.Coordinator.BeginAsync(new FixtureCertificateFlow(), () => { }, CancellationToken.None);
        var certTicket = fixture.Launcher.LastTicket!;
        Check(fixture.Launcher.CertificateStarts == 1 && fixture.Launcher.TaskStarts == 1 &&
            !(await fixture.Elevation.HandleAsync(Control(ScheduledTaskElevationContract.RedeemOperation, certTicket), fixture.HighPeer, CancellationToken.None)).Ok,
            "task facade disposal does not dispose shared certificate core or cross-consume ticket");
        fixture.Exit(0); await certificate.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task MalformedCompletionAsync()
    {
        using var fixture = CreateEngineFixture();
        var payload = Payload("read");
        var pending = fixture.Elevation.BeginAsync(payload, () => { }, CancellationToken.None);
        await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5)); await fixture.RedeemAsync();
        var valid = ScheduledTaskElevationContract.SerializeCompletion(payload, ValidResult("read"));
        var malformed = valid.Replace("\"ok\":true", "\"ok\":true,\"ok\":true", StringComparison.Ordinal);
        var rejected = await fixture.Elevation.HandleAsync(Control(ScheduledTaskElevationContract.CompleteOperation, fixture.TaskTicket!, malformed),
            fixture.HighPeer, CancellationToken.None);
        Check(!rejected.Ok && rejected.Err == "task_elevation_completion_invalid", "first malformed completion consumed");
        Check(!(await fixture.CompleteAsync(payload, ValidResult("read"))).Ok, "malformed completion cannot be replaced");
        fixture.Exit(3);
        var unknown = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(unknown.PayloadIssued && unknown.Result is null && unknown.Failure == "task_elevation_completion_invalid",
            "malformed evidence never becomes ActionIssued=false");
    }

    private static async Task MissingCompletionAndCancellationAsync()
    {
        using var fixture = CreateEngineFixture();
        var pending = fixture.Elevation.BeginAsync(Payload("run"), () => { }, CancellationToken.None);
        await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5)); await fixture.RedeemAsync(); fixture.Exit(3);
        var missing = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(missing.PayloadIssued && missing.Result is null && missing.Failure == "task_elevation_no_completion" && missing.Exit == 3,
            "issued without completion unknown task outcome");

        using var held = CreateEngineFixture(); using var cancellation = new CancellationTokenSource();
        var payload = Payload("read");
        var original = held.Elevation.BeginAsync(payload, () => { }, cancellation.Token);
        await held.TicketReady.WaitAsync(TimeSpan.FromSeconds(5)); await held.RedeemAsync();
        cancellation.Cancel();
        Check(!original.IsCompleted && !held.Launcher.LastProcess!.Disposed, "issued read cancellation keeps original helper/effect owned");
        await held.CompleteAsync(payload, ValidResult("read")); held.Exit(0);
        Check((await original.WaitAsync(TimeSpan.FromSeconds(5))).Result?.Ok == true && held.Starts == 1, "late actual read completion/no replay");
    }

    private static async Task BeforePayloadAsync()
    {
        using var canceled = CreateEngineFixture(); using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await canceled.Elevation.BeginAsync(Payload("read"), () => throw new InvalidOperationException("not called"), cancellation.Token);
        Check(!result.PayloadIssued && result.Result is null && canceled.Starts == 0, "cancel before launch/no fabricated native result");
        using var failed = CreateEngineFixture();
        var pending = failed.Elevation.BeginAsync(Payload("read"), () => throw new InvalidOperationException("fixture-private-marker"), CancellationToken.None);
        await failed.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
        var refused = await failed.RedeemAsync();
        Check(!refused.Ok && refused.Data?.ContainsKey("payload") != true && refused.Err == "task_elevation_issue_not_persisted",
            "issued marker failure never releases payload or message");
        failed.Exit(3);
        var notIssued = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!notIssued.PayloadIssued && notIssued.Result is null, "known no payload is distinct from native action flags");
        using var declined = CreateEngineFixture();
        declined.Launcher.Decline = true;
        var decline = await declined.Elevation.BeginAsync(Payload("read"), () => { }, CancellationToken.None);
        Check(decline.Failure == "task_elevation_declined" && !decline.PayloadIssued && decline.NativeError == 1223, "fixed launch refusal numeric/no retry");
    }

    private static async Task ReentrantIssuedExitAsync()
    {
        using var fixture = new EngineFixture(inlineExitContinuations: true);
        var callbackActive = false;
        CertificateElevationProcessIdentity? originalGeneration = null;
        var pending = fixture.Elevation.BeginAsync(Payload("read"), (CertificateElevationProcessIdentity process) =>
        {
            originalGeneration = process;
            Check(process == fixture.Launcher.LastProcess!.Identity, "typed generation pinned before reentrant child exit");
            callbackActive = true;
            fixture.Exit(0);
            Check(!fixture.Launcher.LastProcess!.Disposed, "original process not disposed inside entered issued callback");
            callbackActive = false;
        }, CancellationToken.None);
        await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
        var receipt = await fixture.RedeemAsync();
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!callbackActive && receipt.Ok && outcome.PayloadIssued && outcome.Result is null && outcome.Process == originalGeneration &&
            fixture.Launcher.LastProcess!.Disposed, "issued callback/exit settlement agrees with actual payload release");
    }

    internal static ScheduledTaskElevationRequest Payload(string action = "read") =>
        ScheduledTaskElevationContract.Create(Request(action), Jid, Rid);
    internal static WindowsTaskOutcome ValidResult(string action)
    {
        var details = new WindowsTaskDetails(WindowsTaskTests.EngineFixtureTaskPath, "ready", action != "disable",
            false, true, Sha, Sha, Sha, "owner_interactive_limited", "least", 1, 0, true);
        var previous = details with { Enabled = true };
        return action switch
        {
            "read" => new(true, action, Current: details),
            "instances" => new(true, action, Current: details, Instances: new([], 0, false, false)),
            "run" => new(true, action, "run", ActionIssued: true, Previous: previous,
                Instance: new(WindowsTaskTests.EngineFixtureInstanceGuid, details.Path, "running"), Observation: "instance_observed_not_completion"),
            "stop" => new(true, action, "stop", ActionIssued: true, Previous: previous,
                Instance: new(WindowsTaskTests.EngineFixtureInstanceGuid, details.Path, "running"), Observation: "exact_instance_stop_acknowledged_not_process_tree_completion"),
            "enable" or "disable" => new(true, action, action, ActionIssued: true, Previous: previous, Current: details,
                Observation: action == "enable" ? "enabled_future_triggers_may_run" : "disabled_running_instances_not_stopped"),
            "remove" => new(true, action, "remove", ActionIssued: true, Previous: previous,
                Observation: "registration_absent_running_instances_not_stopped"),
            _ => throw new InvalidOperationException("unsupported fixture action")
        };
    }
    private static OperationRequest Request(string action, bool elevated = true)
    {
        var arg = new Dictionary<string, string> { ["op"] = action };
        if (elevated) arg["elevate"] = "uac";
        var body = new JsonObject { ["version"] = 1 };
        if (action != "list" && action != "prepare") body["task"] = WindowsTaskTests.EngineFixtureTaskPath;
        if (!WindowsTaskOperationContract.IsReadOnly(action) && action != "create") body["taskSha"] = Sha;
        if (action == "stop") body["instance"] = WindowsTaskTests.EngineFixtureInstanceGuid;
        if (action is "prepare" or "create" or "update")
        {
            body["tool"] = "fixture"; body["toolVersion"] = 3; body["toolSha"] = Sha;
            body["argv"] = new JsonArray(); body["environmentPolicy"] = "scheduler";
            if (action != "prepare") body["schedule"] = new JsonObject { ["kind"] = "on_demand" };
        }
        return new("windows_task", arg, Rid, Body: body.ToJsonString());
    }
    private static OperationRequest Control(string op, string ticket, string? body = null) =>
        new(op, new Dictionary<string, string> { ["ticket"] = ticket }, "helper-control", Body: body);
    private static void ContractRefused(Action action)
    {
        try { action(); throw new InvalidOperationException("Expected task route refusal."); }
        catch (WindowsTaskContractException) { _assertions++; }
    }
    private static void WireRefused(Action action)
    {
        try { action(); throw new InvalidOperationException("Expected task wire refusal."); }
        catch (ScheduledTaskElevationContractException) { _assertions++; }
    }
    private static void Check(bool value, string message)
    { _assertions++; if (!value) throw new InvalidOperationException("Task elevation fixture: " + message); }

    internal sealed class EngineFixture : IDisposable
    {
        internal string OwnerSid => Sid;
        internal int Session => ScheduledTaskElevationTests.Session;
        internal string Host => ScheduledTaskElevationTests.Host;
        internal FakeLauncher Launcher { get; } = new();
        internal OwnerElevationSession Coordinator { get; }
        internal OwnerScheduledTaskElevation Elevation { get; }
        internal string? TaskTicket => Launcher.LastTicket;
        internal Task<string> TicketReady => Launcher.Ticket.Task;
        internal int Starts => Launcher.TaskStarts;
        internal LocalPeer HighPeer => new(Sid, Session, Host, Launcher.LastProcess?.Identity.Pid ?? 111, true);
        internal EngineFixture(bool inlineExitContinuations = false)
        {
            Launcher.InlineExitContinuations = inlineExitContinuations;
            Coordinator = new(Sid, Session, Host, Launcher);
            Elevation = new(Coordinator);
        }
        internal Task<OperationReceipt> RedeemAsync() => Elevation.HandleAsync(
            Control(ScheduledTaskElevationContract.RedeemOperation, TaskTicket!), HighPeer, CancellationToken.None);
        internal Task<OperationReceipt> CompleteAsync(ScheduledTaskElevationRequest payload, WindowsTaskOutcome? result, string? failure = null) =>
            Elevation.HandleAsync(Control(ScheduledTaskElevationContract.CompleteOperation, TaskTicket!,
                ScheduledTaskElevationContract.SerializeCompletion(payload, result, failure)), HighPeer, CancellationToken.None);
        internal void Exit(int code) => Launcher.LastProcess!.Exit(code);
        public void Dispose()
        {
            Elevation.Dispose(); Launcher.LastProcess?.Exit(3); Coordinator.Dispose();
        }
    }

    internal sealed class FakeLauncher : IOwnerCertificateElevationLauncher, IOwnerScheduledTaskElevationLauncher
    {
        internal int TaskStarts, CertificateStarts;
        internal string? LastTicket;
        internal FakeProcess? LastProcess;
        internal bool Decline, InlineExitContinuations;
        internal CertificateElevationProcessIdentity? CaptureOverride;
        internal TaskCompletionSource<string> Ticket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private uint _pid = 110;
        public Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token)
        { CertificateStarts++; return Start(ticket, token); }
        public Task<IOwnerCertificateElevationProcess> StartScheduledTaskAsync(string ticket, CancellationToken token)
        {
            TaskStarts++;
            if (Decline) throw new Win32Exception(1223);
            return Start(ticket, token);
        }
        private Task<IOwnerCertificateElevationProcess> Start(string ticket, CancellationToken token)
        {
            if (token.CanBeCanceled) throw new InvalidOperationException("Launch must retain original lifetime.");
            LastTicket = ticket;
            LastProcess = new(new(++_pid, "2026-11-22T00:00:00.0000000Z", Host), InlineExitContinuations);
            Ticket.TrySetResult(ticket);
            return Task.FromResult<IOwnerCertificateElevationProcess>(LastProcess);
        }
        public CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer)
        {
            var process = LastProcess;
            return CaptureOverride ?? (process is not null && peer.ProcessId == process.Identity.Pid ? process.Identity : null);
        }
    }
    internal sealed class FakeProcess(CertificateElevationProcessIdentity identity, bool inlineExitContinuations = false) : IOwnerCertificateElevationProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(inlineExitContinuations ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public CertificateElevationProcessIdentity Identity { get; } = identity;
        public Task<int> WaitForExitAsync() => _exit.Task;
        internal void Exit(int code) => _exit.TrySetResult(code);
        public void Dispose() => Disposed = true;
    }
    private sealed class FixtureCertificateFlow() : OwnerElevationFlow(OwnerElevationPurpose.CertificateStore)
    {
        internal override bool IsControlRequest(OperationRequest request) => request.Op == "__fixture_certificate_redeem";
        internal override bool IsRedeem(OperationRequest request) => true;
        internal override bool TryValidateRedeem(OperationRequest request) => true;
        internal override object SnapshotPayload() => new { publicOnly = true };
        internal override Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request) => throw new InvalidOperationException("not called");
    }
}
