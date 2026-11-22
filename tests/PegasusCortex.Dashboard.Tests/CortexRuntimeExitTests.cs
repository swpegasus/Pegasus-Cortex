using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Tray;

internal static class CortexRuntimeExitTests
{
    internal static async Task RunAsync()
    {
        var gate = new UserHostAdmission();
        var active = gate.TryEnter()!;
        var serviceCalls = 0;
        var stops = 0;
        var exit = new CortexRuntimeExit(gate, _ => Task.FromResult<string?>(null),
            _ => { serviceCalls++; return Task.FromResult<string?>(null); }, () => stops++);
        var pending = exit.RequestAsync(CancellationToken.None);
        Check(!pending.IsCompleted && gate.TryEnter() is null && serviceCalls == 0 && stops == 0,
            "Exit failed to close admission or stopped an owner before draining.");
        active.Dispose();
        Check((await pending).Accepted && serviceCalls == 1 && stops == 1 && !gate.Accepting,
            "Drained Exit did not commit exactly once.");
        Check(!(await exit.RequestAsync(CancellationToken.None)).Accepted && stops == 1,
            "Committed Exit was issued twice.");

        foreach (var issue in new[] { "work_unresolved", "applications_active" })
        {
            var refused = new UserHostAdmission();
            var callback = new CortexRuntimeExit(refused, _ => Task.FromResult<string?>(issue),
                _ => throw new Exception("Refused work reached service stop."), () => throw new Exception("Refused work stopped runtime."));
            var result = await callback.RequestAsync(CancellationToken.None);
            Check(!result.Accepted && result.Issue == issue && refused.Accepting, "Refusal did not preserve runtime admission.");
        }

        var serviceGate = new UserHostAdmission();
        var denied = true;
        var committed = false;
        var serviceExit = new CortexRuntimeExit(serviceGate, _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(denied ? "service_stop_denied" : null), () => committed = true);
        var refusal = await serviceExit.RequestAsync(CancellationToken.None);
        Check(!refusal.Accepted && refusal.Issue == "service_stop_denied" && serviceGate.Accepting && !committed,
            "Denied elevation was reported as shutdown.");
        denied = false;
        Check((await serviceExit.RequestAsync(CancellationToken.None)).Accepted && committed,
            "A later explicit Exit could not proceed after service refusal.");

        var busyGate = new UserHostAdmission();
        using (busyGate.TryEnter())
        {
            var busy = new CortexRuntimeExit(busyGate, _ => Task.FromResult<string?>(null),
                _ => throw new Exception("Busy owner reached service stop."), () => throw new Exception("Busy owner was stopped."),
                TimeSpan.FromMilliseconds(100));
            var result = await busy.RequestAsync(CancellationToken.None);
            Check(!result.Accepted && result.Issue == "work_active" && busyGate.Accepting,
                "A drain timeout canceled work or claimed OFF.");
        }

        var canceledGate = new UserHostAdmission();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledExit = new CortexRuntimeExit(canceledGate, _ => Task.FromResult<string?>(null),
            _ => throw new Exception("Canceled Exit reached service stop."), () => throw new Exception("Canceled Exit stopped runtime."));
        var canceledResult = await canceledExit.RequestAsync(canceled.Token);
        Check(!canceledResult.Accepted && canceledResult.Issue == "exit_canceled" && canceledGate.Accepting,
            "Canceled Exit left the owner closed.");

        var committedGate = new UserHostAdmission();
        var signalCalls = 0;
        var signalFailure = new CortexRuntimeExit(committedGate, _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null), () => { signalCalls++; throw new AggregateException("fixture callback failure"); });
        var uncertain = await signalFailure.RequestAsync(CancellationToken.None);
        Check(uncertain.Accepted && uncertain.Issue == "exit_signal_incomplete" && !committedGate.Accepting && signalCalls == 1,
            "Post-commit signal failure was reported as reversible refusal.");
        Check(!(await signalFailure.RequestAsync(CancellationToken.None)).Accepted && signalCalls == 1,
            "An uncertain committed shutdown automatically reissued the stop signal.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
