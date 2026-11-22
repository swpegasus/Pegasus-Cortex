using System.IO.Pipes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;

internal static class CortexHostExitControlTests
{
    internal static async Task RunAsync()
    {
        await PolicyAsync();
        await ClientOutcomesAsync();
        await PipeAsync(loseReply: false);
        await PipeAsync(loseReply: true);
        await CanceledWriteAsync();
        Console.WriteLine("METRIC host-exit-control policy-and-fake-client=true owned-pipe-ordering=true held-own-process=true installed-host=false");
    }

    private static readonly LocalPeer Owner = new("S-1-5-21-100-100-100-1001", 1,
        @"C:\Program Files\Pegasus\Cortex\bin\host\PegasusCortex.Host.exe", 42);

    private static OperationRequest Request() => new(CortexHostExitControl.Operation,
        new Dictionary<string, string>(), Guid.NewGuid().ToString("N"));

    private static async Task PolicyAsync()
    {
        var gate = new UserHostAdmission();
        var serviceCalls = 0;
        var stops = 0;
        var runtime = new CortexRuntimeExit(gate, _ => Task.FromResult<string?>(null),
            _ => { serviceCalls++; return Task.FromResult<string?>(null); }, () => stops++);
        Check(!runtime.SignalStop().Accepted && stops == 0 && gate.Accepting, "An unprepared Exit could signal stop.");
        var control = new CortexHostExitControl(Owner, runtime);
        foreach (var peer in new[]
        {
            Owner with { Sid = "S-1-5-21-100-100-100-1002" }, Owner with { Session = 2 },
            Owner with { Elevated = true }, Owner with { ApplicationPath = @"C:\Other\Host.exe" },
            Owner with { ApplicationPath = null }
        })
        {
            var denied = await control.HandleAsync(Request(), peer, CancellationToken.None);
            control.ReplyAttemptSettled(denied);
            Check(!denied.Ok && denied.Err == "auth" && gate.Accepting && serviceCalls == 0 && stops == 0,
                "A wrong peer reached shutdown admission.");
        }
        var request = Request();
        foreach (var invalid in new[]
        {
            request with { Op = "other" }, request with { Arg = new Dictionary<string, string> { ["action"] = "exit" } },
            request with { Rid = "bad" }, request with { Root = "x" }, request with { Ws = "x" },
            request with { Cwd = @"C:\" }, request with { Body = "x" }, request with { Quiet = true },
            request with { Detail = true }, request with { PathRoots = new Dictionary<string, string>() }
        })
        {
            var denied = await control.HandleAsync(invalid, Owner, CancellationToken.None);
            control.ReplyAttemptSettled(denied);
            Check(!denied.Ok && denied.Err == "invalid" && gate.Accepting && serviceCalls == 0 && stops == 0,
                "An extended request reached shutdown admission.");
        }
        var receipt = await control.HandleAsync(request, Owner, CancellationToken.None);
        Check(receipt.Ok && !gate.Accepting && serviceCalls == 1 && stops == 0,
            "Exit stopped before its reply attempt or bypassed service policy.");
        control.ReplyAttemptSettled(receipt with { });
        Check(stops == 0, "A different receipt could signal the committed Exit.");
        control.ReplyAttemptSettled(receipt);
        control.ReplyAttemptSettled(receipt);
        Check(stops == 1, "Reply settlement failed to signal exactly once.");
        var duplicate = await control.HandleAsync(Request(), Owner, CancellationToken.None);
        Check(!duplicate.Ok && duplicate.Err == "exit_in_progress" && stops == 1, "Exit was replayed after commit.");

        foreach (var issue in new[] { "applications_active", "terminals_active", "exit_state_unavailable" })
        {
            var refusalGate = new UserHostAdmission();
            var refused = new CortexHostExitControl(Owner, new CortexRuntimeExit(refusalGate,
                _ => Task.FromResult<string?>(issue), _ => throw new Exception("Blocked work reached service stop."),
                () => throw new Exception("Blocked work stopped Host.")));
            var result = await refused.HandleAsync(Request(), Owner, CancellationToken.None);
            refused.ReplyAttemptSettled(result);
            Check(!result.Ok && result.Err == issue && refusalGate.Accepting, "Refusal left admission closed.");
        }
        var serviceGate = new UserHostAdmission();
        var serviceRefusal = new CortexHostExitControl(Owner, new CortexRuntimeExit(serviceGate,
            _ => Task.FromResult<string?>(null), _ => Task.FromResult<string?>("service_work_unresolved"),
            () => throw new Exception("Refused optional service still stopped Host.")));
        var serviceResult = await serviceRefusal.HandleAsync(Request(), Owner, CancellationToken.None);
        Check(!serviceResult.Ok && serviceResult.Err == "service_work_unresolved" && serviceGate.Accepting,
            "Optional-service refusal was bypassed.");
        using var active = serviceGate.TryEnter();
        var busy = new CortexHostExitControl(Owner, new CortexRuntimeExit(serviceGate,
            _ => Task.FromResult<string?>(null), _ => throw new Exception("Active lease reached service stop."),
            () => throw new Exception("Active lease was stopped."), TimeSpan.FromMilliseconds(25)));
        var busyResult = await busy.HandleAsync(Request(), Owner, CancellationToken.None);
        Check(!busyResult.Ok && busyResult.Err == "work_active" && serviceGate.Accepting, "Active work was canceled to exit.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledResult = await serviceRefusal.HandleAsync(Request(), Owner, canceled.Token);
        Check(!canceledResult.Ok && canceledResult.Err == "exit_canceled" && serviceGate.Accepting,
            "Canceled Exit changed admission.");
    }

    private static async Task ClientOutcomesAsync()
    {
        var absentCalls = 0;
        var absent = await CortexHostExitControl.RequestAsync(_ =>
        { absentCalls++; return Task.FromResult<ICortexHostExitConnection?>(null); }, CancellationToken.None);
        Check(absent.State == "stopped" && absent.HostStopped && absent.ProcessId is null && absentCalls == 1,
            "Absent Host was started or reported as a connected generation.");
        foreach (var scenario in new[] { "stopped", "refused", "timeout", "lost", "invalid" })
        {
            var connection = new FakeConnection(scenario);
            var connects = 0;
            var result = await CortexHostExitControl.RequestAsync(_ =>
            { connects++; return Task.FromResult<ICortexHostExitConnection?>(connection); }, CancellationToken.None,
                TimeSpan.FromMilliseconds(25));
            Check(connects == 1 && connection.Sends == 1 && connection.Disposals == 1 &&
                result.ProcessId == 42 && result.CreationFileTime == "1234", "Exit retried or lost the original generation.");
            Check(scenario switch
            {
                "stopped" => result.State == "stopped" && result.HostStopped && result.ExitCode == 0 && connection.Waits == 1,
                "refused" => result.State == "refused" && result.Error == "work_active" && connection.Waits == 0,
                "timeout" => result.State == "uncertain" && result.Error == "host_stop_unconfirmed" && !result.HostStopped,
                "lost" => result.State == "uncertain" && result.Error == "exit_acknowledgement_unavailable" && result.HostStopped,
                _ => result.State == "uncertain" && result.Error == "exit_reply_invalid" && connection.Waits == 0
            }, "Client mislabeled refusal, lost acknowledgement, or actual stop observation.");
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var connected = false;
        var canceledResult = await CortexHostExitControl.RequestAsync(_ =>
        { connected = true; throw new Exception("Canceled client connected."); }, canceled.Token);
        Check(canceledResult.Error == "exit_canceled" && !connected, "A canceled client connected or sent work.");
    }

    private static async Task PipeAsync(bool loseReply)
    {
        var owner = LocalIdentity.Current();
        Check(CortexHostExitControl.IsOrdinaryPeer(owner), "Owned pipe fixture requires the ordinary signed-in USER route.");
        var pipeName = "cortex-exit-fixture-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var serverStop = new CancellationTokenSource();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new UserHostAdmission();
        var control = new CortexHostExitControl(owner, new CortexRuntimeExit(gate,
            _ => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null), () => stopped.TrySetResult()));
        var writes = 0;
        var settled = 0;
        var server = AuthenticatedPipeHost.RunOperationsAsync(pipeName, owner.Sid, async (request, peer, token) =>
        {
            var receipt = await control.HandleAsync(request, peer, token);
            prepared.TrySetResult();
            if (loseReply) await releaseReply.Task.WaitAsync(deadline.Token);
            return receipt;
        }, serverStop.Token, _ => Interlocked.Increment(ref writes), receipt =>
        {
            Check(loseReply || Volatile.Read(ref writes) == 1, "Stop preceded successful reply write.");
            Interlocked.Increment(ref settled);
            control.ReplyAttemptSettled(receipt);
        });
        try
        {
            await using var pipe = await CortexMcpPipe.ConnectAsync(pipeName, owner, owner.ApplicationPath!, deadline.Token);
            if (!loseReply)
            {
                // Production identity capture and wait observe this OWNED test
                // process, which is deliberately never stopped by the fixture.
                await using var captured = CortexHostExitControl.WindowsExitConnection.Capture(pipe, owner, owner.ApplicationPath!);
                Check(captured.ProcessId == Environment.ProcessId && captured.CreationFileTime > 0 &&
                    !await captured.WaitForStopAsync(TimeSpan.FromMilliseconds(25), deadline.Token),
                    "Held-generation observation reported a live owned process stopped.");
                var receipt = await captured.SendAsync(Request(), deadline.Token);
                Check(receipt.Ok && receipt.Op == CortexHostExitControl.Operation, "Owned pipe Exit acknowledgement failed.");
            }
            else
            {
                await BinaryProtocol.WriteAsync(pipe, Request(), deadline.Token);
                await prepared.Task.WaitAsync(deadline.Token);
                Check(!stopped.Task.IsCompleted && !gate.Accepting, "Committed Exit stopped before reply attempt.");
                await pipe.DisposeAsync();
                releaseReply.TrySetResult();
            }
            await stopped.Task.WaitAsync(deadline.Token);
            Check(Volatile.Read(ref settled) == 1 && !gate.Accepting, "Lost reply stranded committed admission.");
        }
        finally
        {
            releaseReply.TrySetResult();
            serverStop.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
        }
    }

    private static async Task CanceledWriteAsync()
    {
        var owner = LocalIdentity.Current();
        var name = "cortex-exit-cancel-" + Guid.NewGuid().ToString("N");
        using var serverStop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var server = AuthenticatedPipeHost.RunOperationsAsync(name, owner.Sid, (request, _, _) =>
        {
            serverStop.Cancel(); // deterministic failed write, not a shutdown action
            return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid!, "done", request.Op));
        }, serverStop.Token, _ => Interlocked.Increment(ref writes), _ => settled.TrySetResult());
        try
        {
            await using var pipe = await CortexMcpPipe.ConnectAsync(name, owner, owner.ApplicationPath!, deadline.Token);
            await BinaryProtocol.WriteAsync(pipe, Request(), deadline.Token);
            await settled.Task.WaitAsync(deadline.Token);
            Check(writes == 0, "Successful-write callback fired after a canceled response attempt.");
        }
        finally
        {
            serverStop.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
        }
    }

    private sealed class FakeConnection(string scenario) : ICortexHostExitConnection
    {
        public uint ProcessId => 42;
        public long CreationFileTime => 1234;
        internal int Sends, Waits, Disposals;
        public Task<OperationReceipt> SendAsync(OperationRequest request, CancellationToken token)
        {
            Sends++;
            if (scenario == "lost") throw new IOException("owned fixture lost acknowledgement");
            return Task.FromResult(new OperationReceipt(scenario != "refused", "fixture", scenario == "invalid" ? "wrong" : request.Rid!,
                scenario == "refused" ? "failed" : "done", request.Op, scenario == "refused" ? "work_active" : null));
        }
        public Task<bool> WaitForStopAsync(TimeSpan timeout, CancellationToken token)
        { Waits++; return Task.FromResult(scenario != "timeout"); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
