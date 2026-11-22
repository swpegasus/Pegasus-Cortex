using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

internal static class TrayUiRestoreTests
{
    internal static async Task RunAsync()
    {
        var owner = LocalIdentity.Current();
        var shows = 0;
        var request = new OperationRequest(CortexHostUiControl.Operation, new Dictionary<string, string>(), Guid.NewGuid().ToString("N"));
        Task<bool> Show(CancellationToken _) { shows++; return Task.FromResult(true); }
        var rejected = await CortexHostUiControl.HandleAsync(request, owner,
            owner with { ApplicationPath = Path.Combine(Path.GetTempPath(), "other-image.exe") }, Show, CancellationToken.None);
        Check(!rejected.Ok && shows == 0, "Another same-user image could activate private host controls.");
        var admitted = await CortexHostUiControl.HandleAsync(request, owner, owner, Show, CancellationToken.None);
        Check(admitted.Ok && shows == 1, "Explicit same-image UI activation was refused.");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCalls = 0;
        var running = TrayUiThread.RunAsync(() =>
        {
            var profile = new PersonalProfileAvailability("fixture");
            var context = new TrayApplicationContext(profile,
                _ => { exitCalls++; return Task.FromResult(new CortexRuntimeExitResult(true)); },
                () => new CortexUserPresentationState("running", "not_loaded", null, "unavailable"), automaticRefresh: false);
            EventHandler? ready = null;
            ready = async (_, _) =>
            {
                Application.Idle -= ready;
                try
                {
                    context.CloseUserInterface();
                    Check(!context.UserInterfaceVisible && exitCalls == 0 && profile.Snapshot().IssueCode == CortexPersonalProfilePipe.UiUnavailable,
                        "UI close stopped runtime or left its personal layer available.");
                    Check(await context.ShowUserInterfaceAsync(stop.Token) && context.UserInterfaceVisible && exitCalls == 0,
                        "Explicit re-show could not recover the existing UI without runtime restart.");
                    complete.TrySetResult();
                }
                catch (Exception error) { complete.TrySetException(error); }
                finally { stop.Cancel(); }
            };
            Application.Idle += ready;
            return context;
        }, stop.Token);
        try { await complete.Task.WaitAsync(stop.Token); }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
