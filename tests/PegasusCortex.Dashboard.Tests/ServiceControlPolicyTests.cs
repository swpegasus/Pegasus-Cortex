using Pegasus.AgentOps.Tray;

internal static class ServiceControlPolicyTests
{
    internal static async Task RunAsync()
    {
        var absent = new CortexServiceStatus(false, "Absent", "none", 0);
        var stopped = new CortexServiceStatus(true, "Stopped", "disabled", 0);
        var running = new CortexServiceStatus(true, "Running", "manual", 123);
        var control = new FakeControl(new(true, CortexServiceControlAction.Stop, "stopped"));
        Check((await CortexServiceExit.StopIfRunningAsync(() => absent, control, default)).Ok && control.Calls == 0, "absent service invoked a control");
        Check((await CortexServiceExit.StopIfRunningAsync(() => stopped, control, default)).Ok && control.Calls == 0, "stopped/disabled service invoked a control");
        var unknown = new CortexServiceStatus(true, "Unknown", "unknown", 0, "observation_unavailable");
        Check(!(await CortexServiceExit.StopIfRunningAsync(() => unknown, control, default)).Ok && control.Calls == 0, "unknown service was treated as stopped");
        var reads = 0;
        Check((await CortexServiceExit.StopIfRunningAsync(() => reads++ == 0 ? running : stopped,
            control, default)).Ok && control.Calls == 1, "confirmed stop was not joined exactly once");
        var refused = new FakeControl(new(false, CortexServiceControlAction.Stop, "running", "service_work_unresolved"));
        var result = await CortexServiceExit.StopIfRunningAsync(() => running, refused, default);
        Check(!result.Ok && result.Error == "service_work_unresolved" && refused.Calls == 1, "unresolved work was stopped or retried");
        var unconfirmed = new FakeControl(new(true, CortexServiceControlAction.Stop, "stopped"));
        Check(!(await CortexServiceExit.StopIfRunningAsync(() => running, unconfirmed, default)).Ok,
            "helper success without observed stop was accepted");
        foreach (var name in new[] { "start", "stop", "restart", "enable-manual", "enable-automatic", "disable-startup" })
            Check(WindowsCortexServiceControl.TryParse(name, out _), "fixed action missing: " + name);
        Check(!WindowsCortexServiceControl.TryParse("start & arbitrary-command", out _), "generic command accepted");
        Console.WriteLine("PASS optional-service Exit policy (fake status/controller, no SCM/UAC/installed access)");
    }

    private sealed class FakeControl(CortexServiceControlResult result) : ICortexServiceController
    {
        internal int Calls { get; private set; }
        public Task<CortexServiceControlResult> ApplyAsync(CortexServiceControlAction action, CancellationToken cancellationToken)
        {
            Check(action == CortexServiceControlAction.Stop, "Exit changed startup policy or requested start");
            Calls++;
            return Task.FromResult(result);
        }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
