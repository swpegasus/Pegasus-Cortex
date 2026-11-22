using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Pure coordinator fixtures: fake process/OS peer/clock, public ephemeral DER,
// and callback counters. No process launch, UAC, store, pipe, DB or CredRead.
internal static class CertificateStoreElevationTests
{
    private const string OwnerSid = "S-1-5-21-100-200-300-1001";
    private const string Host = @"C:\fixture\Pegasus.AgentOps.Tray.exe";

    internal static async Task RunAsync()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var certificate = new CertificateOperations().CreateAuthority(new("Elevation public fixture", now.AddMinutes(-1), now.AddDays(1), 2048));
        var der = certificate.RawData;
        await DeclineAndCancellationAsync(der);
        await ExactPeerAndCompletionAsync(der);
        await EarlyRedeemAsync(der);
        await PersistenceFailureAsync(der);
        await LostAndInvalidCompletionAsync(der);
        await DeadlineAndCapacityAsync(der);
    }

    private static async Task DeclineAndCancellationAsync(byte[] der)
    {
        var declined = new FakeLauncher { Failure = new Win32Exception(1223) };
        using (var coordinator = Coordinator(declined))
        {
            var callbacks = 0;
            var result = await coordinator.BeginAsync(Payload(der), () => callbacks++, default);
            Check(!result.PayloadIssued && result.Result is { State: "failed", ActionIssued: false, OutcomeUnknown: false, Code: "certificate_elevation_declined" } &&
                declined.Starts == 1 && callbacks == 0, "UAC decline is clean failure before any payload release");
        }
        var canceled = new FakeLauncher();
        using (var coordinator = Coordinator(canceled))
        using (var token = new CancellationTokenSource())
        {
            token.Cancel();
            var result = await coordinator.BeginAsync(Payload(der), () => throw new InvalidOperationException("canceled callback invoked"), token.Token);
            Check(!result.PayloadIssued && canceled.Starts == 0, "prelaunch cancellation does not launch a helper");
        }
        foreach (var dispose in new[] { false, true })
        {
            var launcher = new FakeLauncher { LaunchGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var coordinator = Coordinator(launcher);
            using var token = new CancellationTokenSource();
            var callbacks = 0;
            var begin = coordinator.BeginAsync(Payload(der), () => callbacks++, token.Token);
            await launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (dispose) coordinator.Dispose(); else token.Cancel();
            var canceledResult = await begin.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!canceledResult.PayloadIssued && callbacks == 0 && launcher.Starts == 1 && !launcher.LaunchTokenCanCancel,
                "prepayload cancel/dispose revokes promptly without abandoning the original pending launch");
            var refused = await coordinator.HandleAsync(Redeem(launcher.Tickets[0]), Peer(launcher.Processes[0]), default);
            Check(!refused.Ok && refused.Data?.ContainsKey("payload") != true, "revoked pending launch cannot redeem");
            launcher.LaunchGate.SetResult(launcher.Processes[0]);
            await UntilAsync(() => launcher.Processes[0].Waits == 1);
            Check(launcher.Processes[0].Disposals == 0, "returned original child handle remains retained while still running");
            launcher.Processes[0].Exit(0);
            await UntilAsync(() => launcher.Processes[0].Disposals == 1);
            Check(!launcher.Processes[0].DisposedBeforeExit && callbacks == 0, "revoked helper is observed naturally then its exact handle is disposed");
        }
    }

    private static async Task ExactPeerAndCompletionAsync(byte[] der)
    {
        var launcher = new FakeLauncher();
        using var coordinator = Coordinator(launcher);
        var payload = Payload(der);
        var original = payload.PublicDer!.ToArray();
        var callbacks = 0;
        var begin = coordinator.BeginAsync(payload, () => callbacks++, default);
        await launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        payload.PublicDer![0] ^= 1;
        var process = launcher.Processes[0]; var peer = Peer(process); var ticket = launcher.Tickets[0];
        foreach (var invalid in new[]
        {
            peer with { Sid = "S-1-5-18" }, peer with { Sid = "S-1-5-21-100-200-300-1002" },
            peer with { Session = 8 }, peer with { Elevated = false }, peer with { ProcessId = peer.ProcessId + 1 },
            peer with { ApplicationPath = @"C:\fixture\other.exe" }
        })
            Check(!(await coordinator.HandleAsync(Redeem(ticket), invalid, default)).Ok && callbacks == 0,
                "wrong SID/session/elevation/PID/image never consumes or receives the legitimate payload");
        launcher.CaptureOverride = process.Identity with { Start = "another-generation" };
        Check(!(await coordinator.HandleAsync(Redeem(ticket), peer, default)).Ok && callbacks == 0,
            "reused PID with a changed process generation cannot redeem");
        launcher.CaptureOverride = null;
        Check(!(await coordinator.HandleAsync(Redeem(new string('f', 64)), peer, default)).Ok && callbacks == 0,
            "unknown ticket does not consume the real phase");
        var redeemed = await coordinator.HandleAsync(Redeem(ticket), peer, default);
        Check(redeemed.Ok && callbacks == 1 && redeemed.Data?.GetValueOrDefault("payload") is AdminCertificateStoreRequest snapshot &&
            snapshot.PublicDer!.SequenceEqual(original), "payload is snapshotted and durable callback runs before first release");
        var released = (AdminCertificateStoreRequest)redeemed.Data!["payload"]!;
        released.PublicDer![0] ^= 1;
        var duplicate = await coordinator.HandleAsync(Redeem(ticket), peer, default);
        Check(!duplicate.Ok && duplicate.Data?.ContainsKey("payload") != true && callbacks == 1,
            "duplicate redemption never resends public material or repeats the durable callback");
        Check(!JsonSerializer.Serialize(redeemed).Contains(ticket, StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(duplicate).Contains(ticket, StringComparison.Ordinal), "private ticket never appears in receipts");
        var completion = new CertificateElevationResult(1, "added", payload.CertificateId, true, true, false);
        var wrongPeer = await coordinator.HandleAsync(Complete(ticket, completion), peer with { Elevated = false }, default);
        Check(!wrongPeer.Ok, "unauthenticated completion cannot consume the legitimate completion phase");
        var ack = await coordinator.HandleAsync(Complete(ticket, completion), peer, default);
        Check(ack.Ok && ack.Data?.GetValueOrDefault("completion_received") is true && !begin.IsCompleted,
            "completion acknowledgement means received, with owner observation still waiting for natural exit");
        var changed = completion with { State = "failed", PostconditionObserved = false, Code = "fixture_changed_completion" };
        Check(!(await coordinator.HandleAsync(Complete(ticket, changed), peer, default)).Ok,
            "changed duplicate completion cannot overwrite the first valid evidence");
        process.Exit(37);
        var result = await begin.WaitAsync(TimeSpan.FromSeconds(3));
        Check(result.PayloadIssued && result.Result == completion && result.Exit == 37 && result.Process == process.Identity &&
            launcher.Starts == 1 && process.Waits == 1 && process.Disposals == 1 && !process.DisposedBeforeExit,
            "valid completion remains authoritative after nonzero acknowledgement-related child exit");
    }

    private static async Task EarlyRedeemAsync(byte[] der)
    {
        var launcher = new FakeLauncher { LaunchGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var coordinator = Coordinator(launcher);
        var payload = Payload(der); var callbacks = 0;
        var begin = coordinator.BeginAsync(payload, () => callbacks++, default);
        await launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var process = launcher.Processes[0]; var ticket = launcher.Tickets[0];
        var redeem = coordinator.HandleAsync(Redeem(ticket), Peer(process), default);
        Check(!redeem.IsCompleted && callbacks == 0, "early child redemption waits for original launch binding without holding ticket gate");
        launcher.LaunchGate.SetResult(process);
        Check((await redeem.WaitAsync(TimeSpan.FromSeconds(3))).Ok && callbacks == 1, "early redemption binds to returned exact child once");
        var known = new CertificateElevationResult(1, "unchanged", payload.CertificateId, false, true, false);
        Check((await coordinator.HandleAsync(Complete(ticket, known), Peer(process), default)).Ok, "bound early child reports typed completion");
        process.Exit(0);
        Check((await begin.WaitAsync(TimeSpan.FromSeconds(3))).Result == known && process.Waits == 1, "early-ready race never launches or waits for a replacement process");
    }

    private static async Task PersistenceFailureAsync(byte[] der)
    {
        var launcher = new FakeLauncher();
        using var coordinator = Coordinator(launcher);
        var callbacks = 0;
        var begin = coordinator.BeginAsync(Payload(der), () => { callbacks++; throw new IOException("fixture persist failed"); }, default);
        await launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var request = Redeem(launcher.Tickets[0]); var process = launcher.Processes[0];
        var first = await coordinator.HandleAsync(request, Peer(process), default);
        var duplicate = await coordinator.HandleAsync(request, Peer(process), default);
        Check(!first.Ok && !duplicate.Ok && first.Data?.ContainsKey("payload") != true && duplicate.Data?.ContainsKey("payload") != true && callbacks == 1,
            "failed durable issue callback releases no payload and cannot be retried");
        process.Exit(0);
        var result = await begin.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!result.PayloadIssued && result.Result is { State: "failed", ActionIssued: false, OutcomeUnknown: false },
            "persistence failure before release is not invented machine-action uncertainty");
    }

    private static async Task LostAndInvalidCompletionAsync(byte[] der)
    {
        foreach (var invalid in new[] { "lost", "wrong-pin", "wrong-action", "malformed", "missing-body" })
        {
            var launcher = new FakeLauncher();
            using var coordinator = Coordinator(launcher);
            using var token = new CancellationTokenSource();
            var payload = Payload(der); var callbacks = 0;
            var begin = coordinator.BeginAsync(payload, () => callbacks++, token.Token);
            await launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var process = launcher.Processes[0]; var peer = Peer(process); var ticket = launcher.Tickets[0];
            Check((await coordinator.HandleAsync(Redeem(ticket), peer, default)).Ok && callbacks == 1, "fixture releases one payload");
            token.Cancel();
            Check(!begin.IsCompleted && process.Disposals == 0, "after-issue cancellation cannot release the owner lease or close the live helper");
            if (invalid != "lost")
            {
                var known = new CertificateElevationResult(1, "added", payload.CertificateId, true, true, false);
                var bad = invalid switch
                {
                    "wrong-pin" => Complete(ticket, known with { CertificateId = new string('0', 64) }),
                    "wrong-action" => Complete(ticket, known with { State = "removed" }),
                    "malformed" => Complete(ticket, known) with { Body = "{bad" },
                    _ => Complete(ticket, known) with { Body = null }
                };
                Check(!(await coordinator.HandleAsync(bad, peer, default)).Ok &&
                    !(await coordinator.HandleAsync(Complete(ticket, known), peer, default)).Ok,
                    "first authenticated invalid completion consumes that phase; no later replacement is accepted");
            }
            process.Exit(4);
            var result = await begin.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result.PayloadIssued && result.Result is { State: "uncertain", ActionIssued: null, OutcomeUnknown: true, PostconditionObserved: false } &&
                result.Exit == 4 && callbacks == 1 && launcher.Starts == 1 && process.Disposals == 1 && !process.DisposedBeforeExit,
                "issued payload with lost or invalid completion remains unknown without false acknowledgement or replay");
        }
    }

    private static async Task DeadlineAndCapacityAsync(byte[] der)
    {
        var launcher = new FakeLauncher(); var clock = new FakeClock();
        using var coordinator = new OwnerCertificateStoreElevation(OwnerSid, 7, Host, launcher, clock);
        var callbacks = 0;
        var begins = Enumerable.Range(0, 4).Select(_ => coordinator.BeginAsync(Payload(der), () => callbacks++, default)).ToArray();
        var full = await coordinator.BeginAsync(Payload(der), () => callbacks++, default);
        Check(!full.PayloadIssued && full.Result.Code == "certificate_elevation_busy" && launcher.Starts == 4,
            "four bounded tickets prevent a fifth launch");
        clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await coordinator.HandleAsync(Redeem(launcher.Tickets[0]), Peer(launcher.Processes[0]), default);
        Check(!expired.Ok && expired.Err == "certificate_elevation_expired" && callbacks == 0,
            "redemption at the two-minute boundary cannot release payload");
        coordinator.Dispose();
        Check((await Task.WhenAll(begins).WaitAsync(TimeSpan.FromSeconds(3))).All(outcome => !outcome.PayloadIssued) &&
            launcher.Processes.All(process => process.Disposals == 0), "dispose revokes unissued tickets without waiting for or killing children");
        foreach (var process in launcher.Processes) process.Exit(0);
        await UntilAsync(() => launcher.Processes.All(process => process.Disposals == 1));
        Check(launcher.Processes.All(process => !process.DisposedBeforeExit && process.Waits == 1),
            "each original child is observed naturally and disposed once after disposal of coordinator");
    }

    private static OwnerCertificateStoreElevation Coordinator(FakeLauncher launcher) => new(OwnerSid, 7, Host, launcher);
    private static AdminCertificateStoreRequest Payload(byte[] der) => new(1, "store_add", "personal",
        Convert.ToHexStringLower(SHA256.HashData(der)), der.ToArray(), new string('a', 64), Guid.NewGuid().ToString("N"), "elevation-fixture");
    private static LocalPeer Peer(FakeProcess process) => new(OwnerSid, 7, Host, process.Identity.Pid, true);
    private static OperationRequest Redeem(string ticket) => new(CertificateStoreElevationContract.RedeemOperation,
        new Dictionary<string, string> { ["ticket"] = ticket });
    private static OperationRequest Complete(string ticket, CertificateElevationResult result) => new(CertificateStoreElevationContract.CompleteOperation,
        new Dictionary<string, string> { ["ticket"] = ticket }, Body: CertificateStoreElevationContract.SerializeResult(result));
    private static async Task UntilAsync(Func<bool> ready)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!ready()) await Task.Delay(10, deadline.Token); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }
    private sealed class FakeLauncher : IOwnerCertificateElevationLauncher
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Tickets = [];
        internal readonly FakeProcess[] Processes = Enumerable.Range(0, 4).Select(index => new FakeProcess(new(checked((uint)(1200 + index)),
            "2026-11-20T12:00:00." + index.ToString("D7") + "Z", Host))).ToArray();
        internal TaskCompletionSource<IOwnerCertificateElevationProcess>? LaunchGate;
        internal Exception? Failure;
        internal CertificateElevationProcessIdentity? CaptureOverride;
        internal int Starts;
        internal bool LaunchTokenCanCancel;
        public async Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token)
        {
            var index = Starts++; Tickets.Add(ticket); LaunchTokenCanCancel = token.CanBeCanceled; Entered.TrySetResult();
            if (Failure is not null) throw Failure;
            return LaunchGate is null ? Processes[index] : await LaunchGate.Task;
        }
        public CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer) =>
            CaptureOverride ?? Processes.FirstOrDefault(process => process.Identity.Pid == peer.ProcessId)?.Identity;
    }
    private sealed class FakeProcess(CertificateElevationProcessIdentity identity) : IOwnerCertificateElevationProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waits, _disposals;
        public CertificateElevationProcessIdentity Identity { get; } = identity;
        internal int Waits => Volatile.Read(ref _waits);
        internal int Disposals => Volatile.Read(ref _disposals);
        internal bool DisposedBeforeExit;
        public Task<int> WaitForExitAsync() { Interlocked.Increment(ref _waits); return _exit.Task; }
        internal void Exit(int code) => _exit.TrySetResult(code);
        public void Dispose() { DisposedBeforeExit |= !_exit.Task.IsCompleted; Interlocked.Increment(ref _disposals); }
    }
}
