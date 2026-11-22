using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class UserHostLifecycleTests
{
    internal static AgentOpsEngine CreateOpenEngine(string root) =>
        new(root, null, false, null, false, null, maintenanceMode: new Mode("off"));

    internal static async Task AdmissionAndStartupAsync()
    {
        var gate = new UserHostAdmission();
        var request = gate.TryEnter() ?? throw new Exception("initial admission");
        using (var closing = gate.TryClose() ?? throw new Exception("exit admission"))
        {
            Check(gate.TryEnter() is null, "Exit accepted new work.");
            Check(!closing.WhenIdleAsync(CancellationToken.None).IsCompleted, "Active work was declared drained.");
            request.Dispose();
            await closing.WhenIdleAsync(CancellationToken.None);
        }
        Check(gate.Accepting, "A refused Exit failed to reopen admission.");
        using (var closing = gate.TryClose()!) closing.Commit();
        Check(!gate.Accepting && gate.TryEnter() is null, "Committed Exit reopened admission.");

        var root = Path.Combine(Path.GetTempPath(), "cortex-start-" + Guid.NewGuid().ToString("N"));
        foreach (var phase in new[] { "preparing", "active", "ending", "uncertain" })
        {
            var mode = new Mode(phase);
            try { UserHostStartup.RequireOpen(mode); Directory.CreateDirectory(root); throw new Exception("closed startup admitted"); }
            catch (AgentOpsException error) when (error.Code == "maintenance_busy") { }
            Check(!Directory.Exists(root), "Closed startup initialized owner state.");
        }
        try { UserHostStartup.RequireOpen(new Mode("off") { Unavailable = true }); throw new Exception("unavailable startup admitted"); }
        catch (AgentOpsException error) when (error.Code == "maintenance_state_unavailable") { }
        UserHostStartup.RequireOpen(new Mode("off"));
    }

    internal static async Task EngineExitAdmissionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-exit-" + Guid.NewGuid().ToString("N"));
        var gate = new UserHostAdmission();
        try
        {
            await using var engine = new AgentOpsEngine(root, null, false, null, true, null,
                maintenanceMode: new Mode("off"), userHostAdmission: gate);
            var diag = new OperationRequest("diag", new Dictionary<string, string>());
            Check((await engine.ExecuteAsync(diag)).Ok, "Ordinary owner needs a privileged service.");
            var status = new OperationRequest("stat", new Dictionary<string, string> { ["src"] = root }, "exit-original-status");
            var originalStatus = await engine.ExecuteAsync(status);
            Check(originalStatus.Ok, "Fixture durable status was not admitted.");
            using (var closing = gate.TryClose()!)
            {
                await closing.WhenIdleAsync(CancellationToken.None);
                Check(await engine.UserExitBlockerAsync(CancellationToken.None) is null, "Fresh owner cannot exit.");
                Check((await engine.ExecuteAsync(status)).Jid == originalStatus.Jid,
                    "Closing admission replaced an existing durable receipt.");
                Check(!(await engine.ExecuteAsync(status with { Root = "invalid-credential" })).Ok,
                    "Closing replay rescued a rejected presented credential.");
                var denied = await engine.ExecuteAsync(new("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(root, "denied") }));
                Check(denied.Err == "host_stopping" && !denied.Partial && !Directory.Exists(Path.Combine(root, "denied")), "Exit admission produced an effect.");
                var frozen = await engine.ExecuteAuthenticatedFrozenDirectAsync(diag);
                Check(frozen.Err == "host_stopping", "Service callback bypassed owner Exit admission.");
                var invoked = false;
                Check(!await engine.TryRunStartupEffectAsync(_ => { invoked = true; return Task.FromResult<RegisteredToolDefinition?>(null); }, CancellationToken.None) && !invoked,
                    "Optional startup effects bypassed Exit admission.");
            }
            Check((await engine.ExecuteAsync(diag)).Ok, "Refused Exit did not preserve owner operation.");
        }
        finally { if (Directory.Exists(root)) Console.WriteLine("lifecycle fixture retained: " + root); }
    }

    internal static async Task UnknownWorkRefusesExitAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-unknown-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var jid = Guid.NewGuid().ToString("N");
        var rid = Guid.NewGuid().ToString("N");
        try
        {
            string originalReceipt;
            string originalRequest;
            string originalEffect;
            long originalSequence;
            using (var store = new SqliteJobStore(root))
            {
                store.Claim(new(false, jid, rid, "attention", "run", "recovery_required", Partial: true),
                    new("run", new Dictionary<string, string> { ["cfg"] = "unexecuted-fixture" }), true);
                var retained = store.FindByRid(rid)!;
                originalReceipt = System.Text.Json.JsonSerializer.Serialize(retained.Receipt);
                originalRequest = System.Text.Json.JsonSerializer.Serialize(retained.Request);
                originalSequence = retained.Sequence;
                originalEffect = System.Text.Json.JsonSerializer.Serialize(store.FindMaintenanceEffect(jid));
            }
            var gate = new UserHostAdmission();
            await using (var engine = new AgentOpsEngine(root, null, false, null, false, null,
                maintenanceMode: new Mode("off"), userHostAdmission: gate))
            {
                using var closing = gate.TryClose()!;
                await closing.WhenIdleAsync(CancellationToken.None);
                Check(await engine.InspectExitIssueAsync(CancellationToken.None) == "work_unresolved",
                    "The conservative service probe stopped protecting unresolved history.");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    var refused = false;
                    try { await engine.UserExitBlockerAsync(canceled.Token); }
                    catch (OperationCanceledException) { refused = true; }
                    Check(refused, "Canceled user Exit was declared eligible.");
                }
                Check(await engine.UserExitBlockerAsync(CancellationToken.None) is null,
                    "Nonlive retained uncertainty blocked idle ordinary-user Exit.");
                closing.Commit();
            }
            Check(!gate.Accepting, "Graceful user Exit reopened committed admission.");
            using var observed = new SqliteJobStore(root);
            var original = observed.FindByRid(rid);
            Check(original is not null && original.Receipt.Jid == jid && original.Receipt.State == "attention" && original.Receipt.Partial,
                "Exit inspection acknowledged, replayed or rewrote the original uncertainty.");
            Check(original!.Mutation && original.Sequence == originalSequence &&
                System.Text.Json.JsonSerializer.Serialize(original.Receipt) == originalReceipt &&
                System.Text.Json.JsonSerializer.Serialize(original.Request) == originalRequest &&
                System.Text.Json.JsonSerializer.Serialize(observed.FindMaintenanceEffect(jid)) == originalEffect,
                "Graceful user Exit changed the original receipt, request, sequence or effect digest.");
        }
        finally { if (Directory.Exists(root)) Console.WriteLine("lifecycle fixture retained: " + root); }
    }

    private sealed class Mode(string phase) : IMaintenanceModeSource
    {
        internal bool Unavailable;
        public MaintenanceModeRecord? Read() => Unavailable ? throw new AgentOpsException("maintenance_state_unavailable") :
            new(1, new string('1', 32), phase, new string('2', 32), new string('3', 32), "scope", Guid.Empty.ToString("D"), "fixture", 1, "service", null, 1);
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
