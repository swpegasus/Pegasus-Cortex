using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

// Managed closed-contract/coordinator fixtures with owned fake child handles.
// No UAC, SCM, process/token query, service mutation or native call.
internal static class WindowsServiceElevationTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001", Host = @"C:\fixture\PegasusCortex.Host.exe";
    private const string Service = "CortexFixtureService", Jid = "0123456789abcdef0123456789abcdef", Rid = "service-uac-fixture";
    private const string TargetStart = "2026-11-21T00:00:00.0000000Z";
    private const int Session = 7, TargetPid = 710;
    private static readonly string Sha = new('a', 64);
    private static int _checks;

    internal static EngineFixture CreateEngineFixture() => new();
    internal static async Task RunAsync()
    {
        _checks = 0;
        Contracts();
        await CompletionLifetimeAsync();
        await PeerPurposeAndCapacityAsync();
        await MalformedAndMissingAsync();
        await CancellationAndIssuanceAsync();
        await CallbackDrainAsync();
        Console.WriteLine("PASS Windows service UAC: " + _checks +
            " managed closed wire/preimage/startup/unknown, exact-owner child/purpose/shared bounds, persist-generation-before-payload, first-completion, cancellation and original-child/callback drain checks; no actual UAC/SCM/service action");
    }

    private static void Contracts()
    {
        foreach (var action in new[] { "read", "start", "stop", "restart", "startup" })
        {
            var payload = Payload(action);
            var encoded = WindowsServiceElevationContract.SerializeRequest(payload);
            Check(WindowsServiceElevationContract.ParseRequest(encoded) == payload &&
                !payload.ToString().Contains(Service, StringComparison.Ordinal), "closed request round trip without ToString body");
            var invocation = WindowsServiceOperationContract.Parse(WindowsServiceElevationContract.Reconstruct(payload));
            Check(invocation.RequiresOwnerElevation && invocation.RequiresDurableHelper &&
                invocation.RequestIdentity == payload.RequestIdentity, "exact elevated route reconstructs");
            var body = WindowsServiceElevationContract.SerializeCompletion(payload, ValidResult(action));
            Check(WindowsServiceElevationContract.ParseCompletion(Control(WindowsServiceElevationContract.CompleteOperation, Sha, body),
                payload).Result?.Action == action, "strict typed completion round trip");
            WireRefused(() => WindowsServiceElevationContract.ParseRequest(encoded.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
            var extra = JsonNode.Parse(encoded)!.AsObject(); extra["provider"] = null;
            WireRefused(() => WindowsServiceElevationContract.ParseRequest(extra.ToJsonString()));
            WireRefused(() => WindowsServiceElevationContract.ValidateRequest(payload with { RequestIdentity = new string('b', 64) }));
            WireRefused(() => WindowsServiceElevationContract.ParseCompletion(Control(WindowsServiceElevationContract.CompleteOperation, Sha, body),
                payload with { Rid = "other-rid" }));
            var nested = JsonNode.Parse(body)!.AsObject(); nested["result"]!["current"]!["binaryPath"] = "forbidden";
            WireRefused(() => Parse(nested.ToJsonString(), payload));
            WireRefused(() => Parse(body.Replace("\"ok\":true", "\"ok\":true,\"ok\":true", StringComparison.Ordinal), payload));
            var target = JsonNode.Parse(body)!.AsObject(); target["result"]!["current"]!["service"] = "OtherService";
            WireRefused(() => Parse(target.ToJsonString(), payload));
        }
        WireRefused(() => WindowsServiceElevationContract.Create(Request("list"), Jid, Rid));
        WireRefused(() => WindowsServiceElevationContract.Create(Request("read") with { Root = "AB12" }, Jid, Rid));
        WireRefused(() => WindowsServiceElevationContract.Create(Request("read") with { Cwd = @"C:\fixture" }, Jid, Rid));
        WireRefused(() => WindowsServiceElevationContract.Create(Request("read") with { Rid = null }, Jid, Rid));
        WireRefused(() => WindowsServiceElevationContract.Create(Request("stop", CortexProductIdentity.ServiceName), Jid, Rid));
        Check(WindowsServiceElevationContract.Create(Request("read", CortexProductIdentity.ServiceName), Jid, Rid).Action == "read",
            "own Cortex service read allowed while mutation refused");
        var read = Payload("read");
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(read, ValidResult("read") with { ActionIssued = true }));
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(read, ValidResult("read") with
            { Current = Details("running") with { DisplayName = "changed" } }));
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(read, ValidResult("read") with
            { Ok = false, Error = "fixture-private-marker" }));
        var access = new WindowsServiceOutcome(false, "read", null, null, Error: "access", Phase: "open", NativeError: 5);
        Check(Parse(WindowsServiceElevationContract.SerializeCompletion(read, access), read).Result == access,
            "real native failure preserves its fixed code and numeric domain evidence");
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(read, access with { Error = "precondition" }));
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(read, access with { Phase = "startup" }));
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(read, null));
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(read, null, "arbitrary"));
        Check(Parse(WindowsServiceElevationContract.SerializeCompletion(read, null, "windows_service_helper_failed"), read).Result is null,
            "explicit missing evidence stays null");
        var stop = Payload("stop");
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(stop, ValidResult("stop") with
            { Previous = Details("running") with { Pid = TargetPid + 1 } }));
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(stop, ValidResult("stop") with
            { Previous = Details("running") with { ConfigSha = new string('b', 64) } }));
        var startup = Payload("startup");
        var partial = new WindowsServiceOutcome(false, "startup", Details("stopped"), null, true, true,
            "service_outcome_unknown", "startup", 5);
        Check(Parse(WindowsServiceElevationContract.SerializeCompletion(startup, partial), startup).Result == partial,
            "partially issued two-write startup remains unknown");
        var lostContext = partial with { Previous = null, NativeError = null };
        Check(Parse(WindowsServiceElevationContract.SerializeCompletion(startup, lostContext), startup).Result == lostContext,
            "real owner-loss tuple retains issued uncertainty with private snapshots absent");
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(startup, lostContext with { Current = Details("stopped") }));
        var unchangedPayload = WindowsServiceElevationContract.Create(Request("startup", mode: "manual"), Jid, Rid);
        var unchanged = new WindowsServiceOutcome(true, "startup", Details("stopped"), Details("stopped"), Phase: "unchanged");
        Check(Parse(WindowsServiceElevationContract.SerializeCompletion(unchangedPayload, unchanged), unchangedPayload).Result is
            { Ok: true, ActionIssued: false, OutcomeUnknown: false }, "already-desired startup is successful without a write");
        WireRefused(() => WindowsServiceElevationContract.SerializeCompletion(startup, unchanged));
        foreach (var invalid in new[]
        {
            Control(WindowsServiceElevationContract.RedeemOperation, Sha) with { Body = "{}" },
            Control(WindowsServiceElevationContract.RedeemOperation, Sha) with { Root = "AB12" },
            Control(WindowsServiceElevationContract.RedeemOperation, Sha) with { Ws = "other" },
            Control(WindowsServiceElevationContract.RedeemOperation, Sha) with { Detail = true },
            Control(WindowsServiceElevationContract.RedeemOperation, Sha) with
                { Arg = new Dictionary<string, string> { ["ticket"] = Sha, ["op"] = "start" } }
        }) WireRefused(() => WindowsServiceElevationContract.ParseTicket(invalid));
    }

    private static async Task CompletionLifetimeAsync()
    {
        foreach (var action in new[] { "read", "start", "startup" })
        {
            using var fixture = CreateEngineFixture();
            var payload = Payload(action); var persisted = 0;
            CertificateElevationProcessIdentity? generation = null;
            var pending = fixture.Elevation.BeginAsync(payload, (CertificateElevationProcessIdentity process) =>
            {
                generation = process; persisted++;
                var original = fixture.Launcher.LastProcess ?? throw new InvalidOperationException("Original helper missing.");
                Check(process == original.Identity && !ReferenceEquals(process, original.Identity) &&
                    process.Pid != TargetPid && process.Start != TargetStart && !original.Disposed,
                    "immutable exact helper generation persisted, separate from service process preimage");
            }, CancellationToken.None);
            await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
            var redemption = await fixture.RedeemAsync();
            Check(redemption.Ok && persisted == 1 && generation == fixture.Launcher.LastProcess!.Identity &&
                redemption.Data?.GetValueOrDefault("payload") is WindowsServiceElevationRequest released && released == payload &&
                !pending.IsCompleted, "persist before one payload release");
            Check(!(await fixture.RedeemAsync()).Ok && persisted == 1 && fixture.Launcher.ServiceStarts == 1, "no second payload or helper");
            Check((await fixture.CompleteAsync(payload, ValidResult(action))).Ok && !pending.IsCompleted &&
                !fixture.Launcher.LastProcess!.Disposed, "report ACK does not close original helper");
            fixture.Exit(37);
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var originalProcess = fixture.Launcher.LastProcess ?? throw new InvalidOperationException("Original helper missing.");
            Check(outcome.PayloadIssued && outcome.Result == ValidResult(action) && outcome.Failure is null &&
                outcome.Process == generation && outcome.Exit == 37 && originalProcess.Disposed &&
                !originalProcess.DisposedBeforeExit, "typed evidence survives nonzero ACK exit after natural original-child drain");
        }
    }

    private static async Task PeerPurposeAndCapacityAsync()
    {
        using var fixture = CreateEngineFixture();
        var payload = Payload("read"); var issued = 0;
        var pending = fixture.Elevation.BeginAsync(payload, () => issued++, CancellationToken.None);
        var ticket = await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var peer in new[]
        {
            fixture.HighPeer with { Sid = "S-1-5-18" }, fixture.HighPeer with { Session = Session + 1 },
            fixture.HighPeer with { Elevated = false }, fixture.HighPeer with { ProcessId = 999 },
            fixture.HighPeer with { ApplicationPath = @"C:\fixture\other.exe" }
        })
            Check(!(await fixture.Elevation.HandleAsync(Control(WindowsServiceElevationContract.RedeemOperation, ticket),
                peer, CancellationToken.None)).Ok && issued == 0, "foreign owner/session/High/PID/image cannot redeem");
        fixture.Launcher.CaptureOverride = fixture.Launcher.LastProcess!.Identity with { Start = TargetStart };
        Check(!(await fixture.RedeemAsync()).Ok && issued == 0, "reused helper PID with other generation refused");
        fixture.Launcher.CaptureOverride = null;
        foreach (var purpose in new[] { OwnerElevationPurpose.CertificateStore, OwnerElevationPurpose.MachineServerCertificate, OwnerElevationPurpose.ScheduledTaskControl })
            Check(!(await fixture.SharedSession.HandleAsync(purpose, Control(WindowsServiceElevationContract.RedeemOperation, ticket),
                fixture.HighPeer, CancellationToken.None)).Ok && issued == 0, "service capability cannot cross any sibling purpose");
        Check((await fixture.RedeemAsync()).Ok && issued == 1, "rejected peers do not consume valid service payload");
        await fixture.CompleteAsync(payload, ValidResult("read")); fixture.Exit(0); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Elevation.Dispose();
        var sibling = fixture.SharedSession.BeginAsync(new FixedFlow(OwnerElevationPurpose.CertificateStore), () => { }, CancellationToken.None);
        Check(fixture.Launcher.CertificateStarts == 1, "service facade disposal leaves shared certificate purpose usable");
        fixture.Exit(0); await sibling.WaitAsync(TimeSpan.FromSeconds(5));

        using var launcher = new FakeLauncher();
        using var core = new OwnerElevationSession(Sid, Session, Host, launcher);
        var pendingPurposes = Enum.GetValues<OwnerElevationPurpose>().Select(purpose =>
            core.BeginAsync(new FixedFlow(purpose), () => { }, CancellationToken.None)).ToArray();
        Check(launcher.Processes.Count == 4 && launcher.CertificateStarts == 1 && launcher.MachineStarts == 1 &&
            launcher.TaskStarts == 1 && launcher.ServiceStarts == 1, "all fixed purposes share one four-slot original-child registry");
        var full = await core.BeginAsync(new FixedFlow(OwnerElevationPurpose.WindowsServiceControl), () => { }, CancellationToken.None);
        Check(full.Code == "service_elevation_busy" && !full.PayloadIssued && launcher.Processes.Count == 4, "fifth cross-purpose helper refused before launch");
        foreach (var process in launcher.Processes) process.Exit(0);
        await Task.WhenAll(pendingPurposes).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task MalformedAndMissingAsync()
    {
        using (var fixture = CreateEngineFixture())
        {
            var payload = Payload("read");
            var pending = fixture.Elevation.BeginAsync(payload, () => { }, CancellationToken.None);
            await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5)); await fixture.RedeemAsync();
            var body = WindowsServiceElevationContract.SerializeCompletion(payload, ValidResult("read"));
            var bad = await fixture.Elevation.HandleAsync(Control(WindowsServiceElevationContract.CompleteOperation, fixture.Ticket!,
                body.Replace("\"ok\":true", "\"ok\":true,\"ok\":true", StringComparison.Ordinal)), fixture.HighPeer, CancellationToken.None);
            Check(!bad.Ok && bad.Err == "service_elevation_completion_invalid" &&
                !(await fixture.CompleteAsync(payload, ValidResult("read"))).Ok, "first malformed report consumed; replacement refused");
            fixture.Exit(3);
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(outcome.PayloadIssued && outcome.Result is null && outcome.Failure == "service_elevation_completion_invalid",
                "malformed native evidence does not become ActionIssued=false");
        }
        foreach (var reported in new[] { false, true })
        {
            using var fixture = CreateEngineFixture(); var payload = Payload("start");
            var pending = fixture.Elevation.BeginAsync(payload, () => { }, CancellationToken.None);
            await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5)); await fixture.RedeemAsync();
            if (reported) Check((await fixture.CompleteAsync(payload, null, "windows_service_helper_failed")).Ok, "bounded no-result report accepted");
            fixture.Exit(3);
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(outcome.PayloadIssued && outcome.Result is null && outcome.Failure ==
                (reported ? "windows_service_helper_failed" : "service_elevation_no_completion"), "missing result is no native evidence");
        }
    }

    private static async Task CancellationAndIssuanceAsync()
    {
        using (var fixture = CreateEngineFixture())
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var outcome = await fixture.Elevation.BeginAsync(Payload("read"), () => throw new InvalidOperationException("not called"), canceled.Token);
            Check(!outcome.PayloadIssued && outcome.Result is null && fixture.Launcher.ServiceStarts == 0, "prelaunch cancellation has no helper or fabricated result");
        }
        using (var fixture = CreateEngineFixture())
        {
            fixture.Launcher.Decline = true;
            var outcome = await fixture.Elevation.BeginAsync(Payload("read"), () => { }, CancellationToken.None);
            Check(!outcome.PayloadIssued && outcome.Result is null && outcome.Failure == "service_elevation_declined" &&
                outcome.NativeError == 1223 && fixture.Launcher.ServiceStarts == 1, "UAC decline fixed numeric failure without retry");
        }
        using (var fixture = CreateEngineFixture())
        {
            var callbacks = 0;
            var pending = fixture.Elevation.BeginAsync(Payload("read"), () => { callbacks++; throw new IOException("fixture-private-marker"); }, CancellationToken.None);
            await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
            var receipt = await fixture.RedeemAsync();
            Check(!receipt.Ok && receipt.Err == "service_elevation_issue_not_persisted" && receipt.Data?.ContainsKey("payload") != true &&
                !(await fixture.RedeemAsync()).Ok && callbacks == 1, "failed issuance persistence releases no payload and cannot repeat");
            fixture.Exit(3);
            Check(!(await pending.WaitAsync(TimeSpan.FromSeconds(5))).PayloadIssued, "unreleased payload distinct from native ActionIssued");
        }
        using (var fixture = CreateEngineFixture())
        using (var cancellation = new CancellationTokenSource())
        {
            var payload = Payload("read"); var pending = fixture.Elevation.BeginAsync(payload, () => { }, cancellation.Token);
            await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5)); await fixture.RedeemAsync();
            cancellation.Cancel(); fixture.Elevation.Dispose();
            Check(!pending.IsCompleted && !fixture.Launcher.LastProcess!.Disposed, "issued read stays owned through caller cancellation and facade disposal");
            await fixture.CompleteAsync(payload, ValidResult("read")); fixture.Exit(0);
            Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Result?.Ok == true && fixture.Launcher.ServiceStarts == 1,
                "original late completion remains usable without replay or replacement helper");
        }
        using (var fixture = new EngineFixture(inlineExit: true))
        {
            var callbackEntered = false;
            var pending = fixture.Elevation.BeginAsync(Payload("read"), (CertificateElevationProcessIdentity process) =>
            {
                callbackEntered = true; Check(process == fixture.Launcher.LastProcess!.Identity, "generation pinned before reentrant exit");
                fixture.Exit(0);
                Check(!fixture.Launcher.LastProcess!.Disposed, "original child not disposed inside issued callback");
                callbackEntered = false;
            }, CancellationToken.None);
            await fixture.TicketReady.WaitAsync(TimeSpan.FromSeconds(5));
            var receipt = await fixture.RedeemAsync(); var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!callbackEntered && receipt.Ok && outcome.PayloadIssued && outcome.Result is null && fixture.Launcher.LastProcess!.Disposed,
                "reentrant original exit joins issuance before result and cleanup");
        }
    }

    private static async Task CallbackDrainAsync()
    {
        using var launcher = new FakeLauncher { InlineExit = true };
        using var core = new OwnerElevationSession(Sid, Session, Host, launcher);
        var flow = new DrainFlow(() => launcher.LastProcess!.Exit(0));
        var pending = core.BeginAsync(flow, () => { }, CancellationToken.None);
        var ticket = await launcher.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var original = launcher.LastProcess ?? throw new InvalidOperationException("Original helper missing.");
        var peer = new LocalPeer(Sid, Session, Host, original.Identity.Pid, true);
        Check((await core.HandleAsync(OwnerElevationPurpose.WindowsServiceControl, Control(FixedFlow.Redeem, ticket), peer, CancellationToken.None)).Ok,
            "drain fixture payload released");
        var report = core.HandleAsync(OwnerElevationPurpose.WindowsServiceControl, Control(FixedFlow.Complete, ticket), peer, CancellationToken.None);
        await flow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!report.IsCompleted && !pending.IsCompleted && !original.Disposed, "entered domain callback retains child after reentrant exit");
        flow.ReleaseHandler.TrySetResult(); await report.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!pending.IsCompleted && !original.Disposed, "flow idle barrier retained after actual handler settlement");
        flow.ReleaseIdle.TrySetResult();
        Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).PayloadIssued && original.Disposed,
            "original child released only after all entered work drains");
    }

    internal static WindowsServiceElevationRequest Payload(string action = "read") =>
        WindowsServiceElevationContract.Create(Request(action), Jid, Rid);
    internal static WindowsServiceOutcome ValidResult(string action)
    {
        var previous = Details(action is "read" or "stop" or "restart" ? "running" : "stopped");
        var current = action switch
        {
            "stop" => Details("stopped"),
            "start" or "restart" => Details("running"),
            "startup" => Details("stopped") with { Startup = "automatic_delayed", DelayedAutoStart = true, ConfigSha = new string('b', 64) },
            _ => previous
        };
        return new(true, action, previous, current, ActionIssued: action != "read",
            Phase: action == "read" ? "observation" : "postcondition");
    }
    private static WindowsServiceDetails Details(string state) => new(Service, "Fixture service", state, 0x10, "manual", false, Sha,
        state == "stopped" ? null : TargetPid, state == "stopped" ? null : TargetStart, 1);
    private static OperationRequest Request(string action, string service = Service, string mode = "automatic_delayed")
    {
        var body = new JsonObject { ["version"] = 1 };
        if (action != "list") body["service"] = service;
        if (!WindowsServiceOperationContract.IsReadOnly(action))
        {
            body["configSha"] = Sha; body["expect"] = action is "stop" or "restart" ? "running" : "stopped"; body["tmo"] = 2;
            if (action is "stop" or "restart") { body["pid"] = TargetPid; body["start"] = TargetStart; }
            if (action == "startup") body["mode"] = mode;
        }
        return new("windows_service", new Dictionary<string, string> { ["op"] = action, ["elevate"] = "uac" }, Rid, Body: body.ToJsonString());
    }
    private static OperationRequest Control(string op, string ticket, string? body = null) =>
        new(op, new Dictionary<string, string> { ["ticket"] = ticket }, "service-helper-control", Body: body);
    private static WindowsServiceElevationCompletion Parse(string body, WindowsServiceElevationRequest expected) =>
        WindowsServiceElevationContract.ParseCompletion(Control(WindowsServiceElevationContract.CompleteOperation, Sha, body), expected);
    private static void WireRefused(Action action)
    { try { action(); throw new InvalidOperationException("Expected service wire refusal."); } catch (WindowsServiceElevationContractException) { _checks++; } }
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException("Service elevation fixture: " + message); }

    internal sealed class EngineFixture : IDisposable
    {
        internal string OwnerSid => Sid;
        internal int Session => WindowsServiceElevationTests.Session;
        internal string Host => WindowsServiceElevationTests.Host;
        internal FakeLauncher Launcher { get; } = new();
        internal OwnerElevationSession SharedSession { get; }
        internal OwnerWindowsServiceElevation Elevation { get; }
        internal OwnerWindowsServiceElevation Coordinator => Elevation;
        internal int Starts => Launcher.ServiceStarts;
        internal string? Ticket => Launcher.LastTicket;
        internal Task<string> TicketReady => Launcher.Ready.Task;
        internal LocalPeer HighPeer => new(Sid, Session, Host, Launcher.LastProcess!.Identity.Pid, true);
        internal EngineFixture(bool inlineExit = false)
        { Launcher.InlineExit = inlineExit; SharedSession = new(Sid, Session, Host, Launcher); Elevation = new(SharedSession); }
        internal Task<OperationReceipt> RedeemAsync() =>
            Elevation.HandleAsync(Control(WindowsServiceElevationContract.RedeemOperation, Ticket!), HighPeer, CancellationToken.None);
        internal Task<OperationReceipt> CompleteAsync(WindowsServiceElevationRequest payload, WindowsServiceOutcome? result, string? failure = null) =>
            Elevation.HandleAsync(Control(WindowsServiceElevationContract.CompleteOperation, Ticket!,
                WindowsServiceElevationContract.SerializeCompletion(payload, result, failure)), HighPeer, CancellationToken.None);
        internal void Exit(int code) => Launcher.LastProcess!.Exit(code);
        public void Dispose() { Elevation.Dispose(); Launcher.Dispose(); SharedSession.Dispose(); }
    }
    internal sealed class FakeLauncher : IOwnerCertificateElevationLauncher, IOwnerMachineCertificateElevationLauncher,
        IOwnerScheduledTaskElevationLauncher, IOwnerWindowsServiceElevationLauncher, IDisposable
    {
        internal readonly List<FakeProcess> Processes = [];
        internal int CertificateStarts, MachineStarts, TaskStarts, ServiceStarts;
        internal bool Decline, InlineExit;
        internal string? LastTicket;
        internal FakeProcess? LastProcess;
        internal CertificateElevationProcessIdentity? CaptureOverride;
        internal TaskCompletionSource<string> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token)
        { CertificateStarts++; return Start(ticket, token); }
        public Task<IOwnerCertificateElevationProcess> StartMachineServerAsync(string ticket, CancellationToken token)
        { MachineStarts++; return Start(ticket, token); }
        public Task<IOwnerCertificateElevationProcess> StartScheduledTaskAsync(string ticket, CancellationToken token)
        { TaskStarts++; return Start(ticket, token); }
        public Task<IOwnerCertificateElevationProcess> StartWindowsServiceAsync(string ticket, CancellationToken token)
        { ServiceStarts++; if (Decline) throw new Win32Exception(1223); return Start(ticket, token); }
        private Task<IOwnerCertificateElevationProcess> Start(string ticket, CancellationToken token)
        {
            if (token.CanBeCanceled) throw new InvalidOperationException("Original launch cannot be abandoned.");
            LastTicket = ticket; LastProcess = new(new(checked((uint)(810 + Processes.Count)), "2026-11-22T00:00:00.0000000Z", Host), InlineExit);
            Processes.Add(LastProcess); Ready.TrySetResult(ticket);
            return Task.FromResult<IOwnerCertificateElevationProcess>(LastProcess);
        }
        public CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer) =>
            CaptureOverride ?? Processes.FirstOrDefault(item => item.Identity.Pid == peer.ProcessId)?.Identity;
        public void Dispose() { foreach (var process in Processes) process.Exit(3); }
    }
    internal sealed class FakeProcess(CertificateElevationProcessIdentity identity, bool inline) : IOwnerCertificateElevationProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed, DisposedBeforeExit;
        public CertificateElevationProcessIdentity Identity { get; } = identity;
        public Task<int> WaitForExitAsync() => _exit.Task;
        internal void Exit(int code) => _exit.TrySetResult(code);
        public void Dispose() { DisposedBeforeExit |= !_exit.Task.IsCompleted; Disposed = true; }
    }
    private class FixedFlow(OwnerElevationPurpose purpose) : OwnerElevationFlow(purpose)
    {
        internal const string Redeem = "__fixture_service_redeem", Complete = "__fixture_service_complete";
        internal override bool IsControlRequest(OperationRequest request) => request.Op is Redeem or Complete;
        internal override bool IsRedeem(OperationRequest request) => request.Op == Redeem;
        internal override bool TryValidateRedeem(OperationRequest request) => true;
        internal override object SnapshotPayload() => new { fixedPublicOnly = true };
        internal override Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request) => Task.FromResult(
            new OperationReceipt(true, Jid, Rid, "done", "windows_service_elevation"));
    }
    private sealed class DrainFlow(Action exit) : FixedFlow(OwnerElevationPurpose.WindowsServiceControl)
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override Task WhenIdle => ReleaseIdle.Task;
        internal override async Task<OperationReceipt> HandleAuthenticatedAsync(OperationRequest request)
        {
            exit(); Entered.TrySetResult(); await ReleaseHandler.Task.ConfigureAwait(false);
            return new(true, Jid, Rid, "done", "windows_service_elevation");
        }
    }
}
