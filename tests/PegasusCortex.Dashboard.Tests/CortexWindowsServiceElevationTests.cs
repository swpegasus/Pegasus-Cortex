using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

// Fixed launch/identity/transport/provider fakes. No form, UAC, SCM, process,
// service control, configuration, store or installed-runtime operations.
[SupportedOSPlatform("windows")]
internal static class CortexWindowsServiceElevationTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Start = "2026-11-21T00:00:00.0000000Z";
    private const string ServiceStart = "2026-11-21T01:00:00.0000000Z";
    private const string Host = @"C:\fixture\bin\host\PegasusCortex.Host.exe";
    private const string Service = "FixtureService";
    private static readonly string Ticket = new('a', 64);
    private static readonly string Config = new('b', 64);
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        await LauncherAsync().ConfigureAwait(false);
        await HelperAsync().ConfigureAwait(false);
        // Every owned fake transport/process scope and original callback above
        // has settled and disposed before success can be emitted.
        Console.WriteLine($"PASS Windows service elevation helper: {_checks} injected launch/identity/control/provider/lifetime checks; no forms, UAC, SCM, process, service or installed-state actions");
    }

    private static async Task LauncherAsync()
    {
        var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(Path.GetTempPath(), "service-launch-fixture"), Sid);
        var host = CortexOwnerElevation.HostPath(layout);
        var owner = new LocalPeer(Sid, 7, host, 123);
        var current = owner;
        var parent = new CertificateElevationProcessIdentity(123, Start, host);
        var child = new ProcessFixture(new(456, Start, host));
        ProcessStartInfo? observed = null;
        var starts = 0; var protections = 0;
        using var canceledDuringLaunch = new CancellationTokenSource();
        var launcher = new WindowsOwnerElevationLauncher(layout, owner, () => current, () => parent,
            (root, image) => { Check(root == layout.InstallRoot && image == host, "launch protection binds exact installed Host"); protections++; },
            info => { starts++; observed = info; canceledDuringLaunch.Cancel(); return child; });
        using (var retained = await launcher.StartWindowsServiceAsync(Ticket, canceledDuringLaunch.Token).ConfigureAwait(false))
        {
            Check(ReferenceEquals(retained, child) && canceledDuringLaunch.IsCancellationRequested && starts == 1,
                "post-launch cancellation abandoned/repeated/replaced the original process");
            Check(observed is { UseShellExecute: true, Verb: "runas", WindowStyle: ProcessWindowStyle.Hidden } &&
                observed.FileName == host && observed.WorkingDirectory == Path.GetDirectoryName(host) && observed.Arguments.Length == 0 &&
                observed.ArgumentList.SequenceEqual(new[] { CortexWindowsServiceElevation.HelperFlag, Ticket, "123", Start }),
                "service launch contains a service/command/body or loses parent generation");
            Check(retained.Identity == child.Identity && await retained.WaitForExitAsync().ConfigureAwait(false) == 0 && child.Waits == 1,
                "original retained process identity/wait changed");
        }
        Check(child.Disposals == 1 && protections == 1, "original process/protection ownership changed");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(await RefusedAsync(() => launcher.StartWindowsServiceAsync(Ticket, canceled.Token)).ConfigureAwait(false) && starts == 1,
            "prelaunch cancellation started a helper");
        Check(await RefusedAsync(() => launcher.StartWindowsServiceAsync("invalid", default)).ConfigureAwait(false) && starts == 1,
            "invalid service ticket started a helper");
        foreach (var denied in new[] { owner with { Elevated = true }, owner with { Session = 0 }, owner with { Session = 8 },
            owner with { Sid = "S-1-5-18" }, owner with { ApplicationPath = host + ".other" }, owner with { ProcessId = 124 } })
        {
            current = denied;
            Check(await RefusedAsync(() => launcher.StartWindowsServiceAsync(Ticket, default)).ConfigureAwait(false) && starts == 1,
                "owner/session/image/process mismatch reached launch");
        }
        current = owner;
        var originalParent = parent;
        foreach (var denied in new[] { parent with { Pid = 124 }, parent with { ApplicationPath = host + ".other" },
            parent with { Start = "2026-11-21T00:00:00Z" } })
        {
            parent = denied;
            Check(await RefusedAsync(() => launcher.StartWindowsServiceAsync(Ticket, default)).ConfigureAwait(false) && starts == 1,
                "unbound parent generation reached launch");
        }
        parent = originalParent;
        var unprotected = new WindowsOwnerElevationLauncher(layout, owner, () => owner, () => parent,
            (_, _) => throw new UnauthorizedAccessException("fixture-image-refused"), _ => throw new InvalidOperationException("unexpected-launch"));
        Check(await RefusedAsync(() => unprotected.StartWindowsServiceAsync(Ticket, default)).ConfigureAwait(false),
            "unprotected Host reached launch");
    }

    private static async Task HelperAsync()
    {
        var owner = new LocalPeer(Sid, 7, Host, 456, Elevated: true);
        CertificateElevationProcessIdentity? Capture(LocalPeer peer) => new(peer.ProcessId!.Value, Start, peer.ApplicationPath!);
        foreach (var action in new[] { "read", "start", "stop", "restart", "startup" })
        {
            await using var transport = new Transport(Payload(action));
            var executor = new Executor();
            var exit = await Invoke(transport, executor).ConfigureAwait(false);
            var valid = exit == 0 && transport.Redeems == 1 && transport.Completes == 1 && executor.Calls == 1 &&
                transport.Completion is { Failure: null, Result.Ok: true } && transport.Completion.Result.Action == action;
            if (!valid) Console.WriteLine("DIAGNOSTIC windows-service-helper " + JsonSerializer.Serialize(new
                { action, exit, redeem = transport.Redeems, execute = executor.Calls, complete = transport.Completes }));
            Check(valid, "one exact service action did not redeem/execute/report once");
            Check(executor.Invocation is { RequiresOwnerElevation: true } && executor.Invocation.Action == action &&
                executor.Invocation.Service == Service && executor.Owner == owner, "service request changed exact action/target/owner");
        }
        {
            await using var transport = new Transport(Payload("startup", unchanged: true));
            var executor = new Executor { Work = _ => Task.FromResult(Result("startup", unchanged: true)) };
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 0 && transport.Completion is
                { Result: { Ok: true, Phase: "unchanged", ActionIssued: false, OutcomeUnknown: false } },
                "unchanged startup became a fabricated issued write");
        }
        {
            await using var transport = new Transport(Payload("startup", unchanged: true));
            var before = Details("stopped") with { Startup = "automatic_delayed", DelayedAutoStart = true };
            var afterFirstWrite = before with { Startup = "automatic", DelayedAutoStart = false, ConfigSha = new string('c', 64) };
            var executor = new Executor { Work = _ => Task.FromResult(new WindowsServiceOutcome(false, "startup", before, afterFirstWrite,
                ActionIssued: true, OutcomeUnknown: true, Error: "service_outcome_unknown", Phase: "startup", NativeError: 5)) };
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 0 && transport.Completion is
                { Result: { Ok: false, ActionIssued: true, OutcomeUnknown: true, Phase: "startup", NativeError: 5 } } && executor.Calls == 1,
                "partial two-write startup outcome was called unchanged/rolled back/retried");
        }
        foreach (var denied in new[] { owner with { Elevated = false }, owner with { Session = 0 }, owner with { Sid = "S-1-5-18" },
            owner with { ApplicationPath = Host + ".other" } })
        {
            await using var transport = new Transport(Payload("read")); var executor = new Executor();
            Check(await CortexWindowsServiceElevation.RunOnceAsync(Ticket, owner, () => denied, Capture, transport, executor, default).ConfigureAwait(false) == 3 &&
                transport.Redeems == 0 && executor.Calls == 0, "invalid High owner redeemed or eagerly initialized provider");
        }
        foreach (var raw in new[] { "{}", WindowsServiceElevationContract.SerializeRequest(Payload("read")).Replace("\"action\":\"read\"", "\"action\":\"list\"", StringComparison.Ordinal),
            WindowsServiceElevationContract.SerializeRequest(Payload("read")).Insert(1, "\"command\":\"forbidden\","), OwnServiceMutation() })
        {
            await using var transport = new Transport(Payload("read")) { Raw = raw }; var executor = new Executor();
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 3 && executor.Calls == 0 && transport.Completes == 0,
                "invalid/own-service/expanded payload reached provider or startup");
        }
        {
            await using var transport = new Transport(Payload("read")) { DenyRedemption = true }; var executor = new Executor();
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 3 && executor.Calls == 0 && transport.Completes == 0,
                "revoked/unavailable ticket initialized provider or fell back");
        }
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await using var transport = new Transport(Payload("start")); var executor = new Executor();
            Check(await Invoke(transport, executor, cancellation.Token).ConfigureAwait(false) == 3 && transport.Redeems == 0 && executor.Calls == 0,
                "pre-redemption cancellation initialized control/provider");
        }
        {
            using var cancellation = new CancellationTokenSource();
            await using var transport = new Transport(Payload("start")) { AfterRedeem = cancellation.Cancel }; var executor = new Executor();
            Check(await Invoke(transport, executor, cancellation.Token).ConfigureAwait(false) == 0 && cancellation.IsCancellationRequested &&
                executor.Calls == 1 && transport.Completes == 1, "post-redemption cancellation abandoned the original action/report");
        }
        {
            var current = owner;
            await using var transport = new Transport(Payload("start")) { AfterRedeem = () => current = owner with { Elevated = false } };
            var executor = new Executor();
            Check(await CortexWindowsServiceElevation.RunOnceAsync(Ticket, owner, () => current, Capture, transport, executor, default).ConfigureAwait(false) == 4 &&
                executor.Calls == 0 && transport.Completes == 0, "lost High owner executed/reported through unauthorized context");
        }
        foreach (var cancelled in new[] { false, true })
        {
            await using var transport = new Transport(Payload("start"));
            var executor = new Executor { Work = _ =>
                { if (cancelled) throw new OperationCanceledException(); throw new IOException("unreportable-service-detail"); } };
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 0 && executor.Calls == 1 &&
                transport.Completion is { Result: null } && transport.Completion.Failure ==
                    (cancelled ? "windows_service_helper_cancelled" : "windows_service_helper_failed"),
                "missing native evidence invented false ActionIssued or retried");
            Check(transport.CompletionText is not null && !transport.CompletionText.Contains("unreportable-service-detail", StringComparison.Ordinal),
                "raw provider exception detail crossed completion");
        }
        {
            await using var transport = new Transport(Payload("start"));
            var executor = new Executor { Work = action => Task.FromResult(new WindowsServiceOutcome(false, action, null, null,
                Error: "access", Phase: "open", NativeError: 5)) };
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 0 && executor.Calls == 1 && transport.Completion is
                { Result: { Ok: false, ActionIssued: false, OutcomeUnknown: false, Error: "access", NativeError: 5 } },
                "observed pre-write native refusal became missing evidence or a fabricated issued action");
        }
        {
            await using var transport = new Transport(Payload("read"));
            var wrong = Details("running") with { Service = "OtherService" };
            var executor = new Executor { Work = _ => Task.FromResult(new WindowsServiceOutcome(true, "read", wrong, wrong)) };
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 4 && executor.Calls == 1 && transport.Completes == 0,
                "wrong-service observation was reported as successful exact target");
        }
        {
            await using var transport = new Transport(Payload("start")) { LoseCompletion = true }; var executor = new Executor();
            Check(await Invoke(transport, executor).ConfigureAwait(false) == 4 && executor.Calls == 1 && transport.Completes == 1,
                "lost completion acknowledgement repeated action or report");
        }
        {
            using var cancellation = new CancellationTokenSource();
            await using var transport = new Transport(Payload("start"));
            var settlement = new TaskCompletionSource<WindowsServiceOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executor = new Executor { Work = _ => settlement.Task };
            var pending = Invoke(transport, executor, cancellation.Token);
            Check(!pending.IsCompleted && executor.Calls == 1 && transport.Completes == 0, "helper completed before original provider settled");
            cancellation.Cancel();
            Check(!pending.IsCompleted && transport.Completes == 0, "cancellation abandoned active provider lifetime");
            settlement.SetResult(Result("start"));
            Check(await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) == 0 && executor.Calls == 1 && transport.Completes == 1,
                "original callback did not settle and report exactly once");
        }

        Task<int> Invoke(Transport transport, Executor executor, CancellationToken token = default) =>
            CortexWindowsServiceElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, token);
    }

    private static OperationRequest Request(string action, bool unchanged = false, string service = Service)
    {
        var body = new Dictionary<string, object?> { ["version"] = 1, ["service"] = service };
        if (action != "read")
        {
            body["configSha"] = Config;
            body["expect"] = action is "stop" or "restart" ? "running" : "stopped";
            body["tmo"] = 5;
            if (action is "stop" or "restart") { body["pid"] = 400; body["start"] = ServiceStart; }
            if (action == "startup") body["mode"] = unchanged ? "manual" : "automatic_delayed";
        }
        return new("windows_service", new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac" },
            "service-elevation-fixture-" + action, Body: JsonSerializer.Serialize(body));
    }
    private static WindowsServiceElevationRequest Payload(string action, bool unchanged = false)
    {
        var request = Request(action, unchanged);
        return WindowsServiceElevationContract.Create(request, new string('c', 32), request.Rid!);
    }
    private static string OwnServiceMutation()
    {
        var request = Request("start", service: CortexProductIdentity.ServiceName);
        var invocation = WindowsServiceOperationContract.Parse(request);
        return JsonSerializer.Serialize(new WindowsServiceElevationRequest(1, "start", request.Body!, invocation.RequestIdentity,
            new string('c', 32), request.Rid!), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private static WindowsServiceDetails Details(string state) => new(Service, "Fixture Service", state, 0x10, "manual", false,
        Config, state == "running" ? 400 : null, state == "running" ? ServiceStart : null, 1);
    private static WindowsServiceOutcome Result(string action, bool unchanged = false) => action switch
    {
        "read" => new(true, action, Details("running"), Details("running")),
        "start" => new(true, action, Details("stopped"), Details("running"), ActionIssued: true, Phase: "postcondition"),
        "stop" => new(true, action, Details("running"), Details("stopped"), ActionIssued: true, Phase: "postcondition"),
        "restart" => new(true, action, Details("running"), Details("running") with
            { Pid = 401, Start = "2026-11-21T01:01:00.0000000Z" }, ActionIssued: true, Phase: "postcondition"),
        "startup" when unchanged => new(true, action, Details("stopped"), Details("stopped"), Phase: "unchanged"),
        "startup" => new(true, action, Details("stopped"), Details("stopped") with
            { Startup = "automatic_delayed", DelayedAutoStart = true, ConfigSha = new string('d', 64) }, ActionIssued: true, Phase: "postcondition"),
        _ => throw new InvalidOperationException("unexpected-fixture-action")
    };
    private sealed class Executor : ICortexWindowsServiceExecutor
    {
        internal int Calls;
        internal WindowsServiceInvocation? Invocation;
        internal LocalPeer? Owner;
        internal Func<string, Task<WindowsServiceOutcome>> Work = action => Task.FromResult(Result(action));
        public Task<WindowsServiceOutcome> ExecuteAsync(WindowsServiceInvocation invocation, LocalPeer owner, Action requireHigh)
        { Calls++; Invocation = invocation; Owner = owner; requireHigh(); return Work(invocation.Action); }
    }
    private sealed class Transport(WindowsServiceElevationRequest payload) : IOperationDispatcher
    {
        internal int Redeems, Completes;
        internal string? Raw, CompletionText;
        internal bool DenyRedemption, LoseCompletion;
        internal Action? AfterRedeem;
        internal WindowsServiceElevationCompletion? Completion;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token = default)
        {
            Check(!token.CanBeCanceled && WindowsServiceElevationContract.ParseTicket(request) == Ticket,
                "service control changed ticket/context or made delivered work cancelable");
            if (request.Op == WindowsServiceElevationContract.RedeemOperation)
            {
                Redeems++; AfterRedeem?.Invoke();
                return Task.FromResult(new OperationReceipt(!DenyRedemption, payload.Jid, payload.Rid, DenyRedemption ? "failed" : "done", request.Op,
                    DenyRedemption ? "auth" : null, Data: DenyRedemption ? null : new Dictionary<string, object?>
                        { ["payload"] = Raw ?? WindowsServiceElevationContract.SerializeRequest(payload) }));
            }
            Completes++; CompletionText = request.Body;
            Completion = WindowsServiceElevationContract.ParseCompletion(request, payload);
            if (LoseCompletion) throw new IOException("unreportable-completion-detail");
            return Task.FromResult(new OperationReceipt(true, payload.Jid, payload.Rid, "done", request.Op));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ProcessFixture(CertificateElevationProcessIdentity identity) : IOwnerCertificateElevationProcess
    {
        internal int Waits, Disposals;
        public CertificateElevationProcessIdentity Identity => identity;
        public Task<int> WaitForExitAsync() { Waits++; return Task.FromResult(0); }
        public void Dispose() => Disposals++;
    }
    private static async Task<bool> RefusedAsync(Func<Task<IOwnerCertificateElevationProcess>> action)
    {
        try { using var unexpected = await action().ConfigureAwait(false); return false; }
        catch (Exception failure) when (failure is AgentOpsException or WindowsServiceElevationContractException or OperationCanceledException or UnauthorizedAccessException)
        { return true; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }
}
