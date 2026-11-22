using System.Diagnostics;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ServiceExitAdmissionTests
{
    internal static async Task RunAsync()
    {
        var host = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fixture-host.exe"));
        using var process = Process.GetCurrentProcess();
        var owner = new MaintenanceHostIdentity("fixture-owner", 1, checked((uint)process.Id),
            process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture), host);
        var gate = new ServiceExitAdmission();
        Check(gate.TryEnter(out var active), "initial request refused");
        var calls = 0;
        var blocked = await gate.PrepareAsync(owner, _ => { calls++; return Task.FromResult<string?>(null); }, default);
        Check(blocked.Issue == "service_work_active" && calls == 0, "active request was ignored");
        active!.Dispose();
        var inspect = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparing = gate.PrepareAsync(owner, _ => inspect.Task, default);
        Check(!gate.TryEnter(out _), "admission reopened during preparation");
        inspect.SetResult(null);
        var prepared = await preparing;
        Check(prepared.Lease is not null && prepared.Issue is null && !gate.TryEnter(out _), "prepared lease did not fence admission");
        Check(!gate.Cancel(owner with { Session = 2 }, prepared.Lease!), "different owner cancelled lease");
        Check((await gate.PrepareAsync(owner, _ => Task.FromResult<string?>(null), default)).Issue == "service_exit_pending",
            "a live helper lease was stolen");
        Check(gate.Cancel(owner, prepared.Lease!), "known cancellation was refused");
        Check(!gate.Commit(owner, prepared.Lease!), "cancelled lease was committed later");
        Check(gate.TryEnter(out var resumed), "known cancellation did not reopen admission");
        resumed!.Dispose();
        var unresolved = await gate.PrepareAsync(owner, _ => Task.FromResult<string?>("work_unresolved"), default);
        Check(unresolved.Issue == "work_unresolved" && gate.TryEnter(out resumed), "unresolved work was accepted");
        resumed!.Dispose();
        try { await gate.PrepareAsync(owner, _ => throw new IOException("fixture"), default); throw new Exception("observation failure accepted"); }
        catch (IOException) { }
        Check(gate.TryEnter(out resumed), "failed preflight left a false successful reservation");
        resumed!.Dispose();
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelledGate = new ServiceExitAdmission();
            var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = cancelledGate.PrepareAsync(owner, _ => observed.Task, cancellation.Token);
            Check(!cancelledGate.TryEnter(out _), "preflight did not close admission");
            cancellation.Cancel();
            observed.SetResult(null);
            try { await pending; throw new Exception("cancelled preflight committed"); }
            catch (OperationCanceledException) { }
            Check(cancelledGate.TryEnter(out var released), "cancelled preflight retained a false reservation");
            released!.Dispose();
            try { await cancelledGate.PrepareAsync(owner, _ => throw new Exception("cancelled token inspected"), cancellation.Token); throw new Exception("cancelled token accepted"); }
            catch (OperationCanceledException) { }
        }
        var peer = new LocalPeer(owner.Sid, owner.Session, host, owner.Pid, Elevated: true);
        Check(AuthenticatedServiceBroker.IsServiceExitPeer(peer, owner.Sid, host), "correct synthetic elevated peer refused");
        Check(!AuthenticatedServiceBroker.IsServiceExitPeer(peer with { Elevated = false }, owner.Sid, host) &&
            !AuthenticatedServiceBroker.IsServiceExitPeer(peer with { Sid = "other" }, owner.Sid, host) &&
            !AuthenticatedServiceBroker.IsServiceExitPeer(peer with { Session = 0 }, owner.Sid, host) &&
            !AuthenticatedServiceBroker.IsServiceExitPeer(peer with { ApplicationPath = host + ".other" }, owner.Sid, host),
            "wrong privilege/owner/session/image admitted");
        var instance = new ServiceExitAdmission();
        var stopSignals = 0;
        var preparedInstance = await instance.PrepareAsync(owner, _ => Task.FromResult<string?>(null), default);
        Check(!instance.Commit(owner with { Session = 2 }, preparedInstance.Lease!), "wrong caller committed another lease");
        Check(instance.Commit(owner, preparedInstance.Lease!), "prepared lease did not commit");
        Check(!instance.Cancel(owner, preparedInstance.Lease!) && !instance.TryEnter(out _),
            "unknown committed acknowledgement reopened admission");
        Check(stopSignals == 0, "commit invoked stop before response completion");
        var replacement = new ServiceExitAdmission();
        Check(!replacement.Commit(owner, preparedInstance.Lease!) &&
            !replacement.InvokeStopAfterReply(preparedInstance.Lease, () => stopSignals++),
            "replacement instance accepted a previous instance lease");
        Check(!instance.InvokeStopAfterReply("wrong-lease", () => stopSignals++), "unrelated response stopped the service");
        Check(instance.InvokeStopAfterReply(preparedInstance.Lease, () => stopSignals++) && stopSignals == 1,
            "successful committed response did not signal self-stop exactly once");
        Check(instance.InvokeStopAfterReply(preparedInstance.Lease, () => stopSignals++) && stopSignals == 1,
            "committed response replay repeated the stop signal");
        await ScheduledReadRemainsQueuedAsync(owner);
        Console.WriteLine("PASS service-exit admission and peer policy (unit/synthetic identities; no pipe/SCM/admin/runtime action)");
    }

    private static async Task ScheduledReadRemainsQueuedAsync(MaintenanceHostIdentity owner)
    {
        using var temp = new TempRoot();
        var state = temp.Dir("router");
        var file = Path.Combine(temp.Path, "read.txt");
        await File.WriteAllTextAsync(file, "fixture");
        using (var store = new RoutedJobStore(state, recover: false)) store.SetPaused(true);
        var admission = new ServiceExitAdmission();
        var peer = new LocalPeer(owner.Sid, owner.Session, owner.ApplicationPath, owner.Pid);
        var dispatched = 0;
        OperationReceipt queued;
        await using (var broker = new AuthenticatedServiceBroker(state, owner.Sid,
            new RootCodeVerifier(Path.Combine(temp.Path, "root.verifier")), false,
            (_, _, request, _) =>
            {
                Interlocked.Increment(ref dispatched);
                return Task.FromResult(new OperationReceipt(true, "fixture-inner", request.Rid!, "done", request.Op));
            }, maintenanceMode: new NoMaintenanceBarrier(), serviceExitAdmission: admission))
        {
            Check((await broker.RegisterTrayAsync(new TrayRegistration(owner.Sid, owner.Session, "fixture-tray"), peer)).Ok,
                "fixture tray registration failed");
            queued = await broker.HandleUserAsync(new OperationRequest("stat", new Dictionary<string, string>
                { ["src"] = file }, "queued-read-before-exit"), peer, default);
            Check(queued.State == "waiting_user", "paused read was not queued");
            // The earlier registration drain may still be finishing. Wait only
            // for that owned idle task; never inspect installed service state.
            var deadline = Stopwatch.StartNew();
            (string? Lease, string? Issue) prepared;
            do
            {
                prepared = await admission.PrepareAsync(owner, _ => Task.FromResult<string?>(null), default);
                if (prepared.Issue != "service_work_active") break;
                await Task.Delay(10);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(5));
            Check(prepared.Lease is not null && prepared.Issue is null, "fixture idle preparation failed");
            // Schedules the background task after PREPARE, through the existing
            // internal tray control, independently of inbound request admission.
            Check(broker.SetTrayPaused(false, peer).Ok, "fixture queue resume failed");
        } // Disposal joins every scheduled drain before examining its result.
        Check(dispatched == 0, "a scheduled read started after shutdown preparation");
        using var retained = new RoutedJobStore(state, recover: false);
        var waiting = retained.Find(queued.Jid);
        Check(waiting?.Receipt.State == "waiting_user" && waiting.Receipt.Rid == queued.Rid &&
            waiting.Request?.Op == "stat" && retained.FirstUnresolvedMutation(owner.Sid) is null,
            "shutdown changed or consumed the queued read");
    }

    private sealed class NoMaintenanceBarrier : IMaintenanceModeSource
    {
        public MaintenanceModeRecord? Read() => null;
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
