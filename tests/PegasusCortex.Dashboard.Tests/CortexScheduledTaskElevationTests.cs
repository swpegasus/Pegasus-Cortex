using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

// Injected launcher/identity/transport/provider only. No forms, UAC, COM,
// task inventory, process start, store or installed-state operations.
[SupportedOSPlatform("windows")]
internal static class CortexScheduledTaskElevationTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string Start = "2026-11-21T00:00:00.0000000Z";
    private const string Host = @"C:\fixture\bin\host\PegasusCortex.Host.exe";
    private const string TaskPath = @"\Fixture\Existing";
    private const string Instance = "e884433d-1809-42ee-9cbd-b8c720cb8f29";
    private static readonly string Ticket = new('a', 64);
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        await LauncherAsync().ConfigureAwait(false);
        await HelperAsync().ConfigureAwait(false);
        Console.WriteLine($"PASS scheduled-task elevation helper: {_checks} injected launch/identity/control/provider/lifetime checks; no forms, UAC, COM, task, process or installed-state actions");
    }

    private static async Task LauncherAsync()
    {
        var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(Path.GetTempPath(), "scheduled-launch-fixture"), Sid);
        var host = CortexOwnerElevation.HostPath(layout);
        var owner = new LocalPeer(Sid, 7, host, 123);
        var current = owner;
        var parent = new CertificateElevationProcessIdentity(123, Start, host);
        var child = new ProcessFixture(new(456, Start, host));
        ProcessStartInfo? observed = null;
        var starts = 0; var protections = 0;
        using var canceledDuringLaunch = new CancellationTokenSource();
        var launcher = new WindowsOwnerElevationLauncher(layout, owner, () => current, () => parent,
            (root, image) => { Check(root == layout.InstallRoot && image == host, "launcher protection binds installed Host"); protections++; },
            info => { starts++; observed = info; canceledDuringLaunch.Cancel(); return child; });
        using (var retained = await launcher.StartScheduledTaskAsync(Ticket, canceledDuringLaunch.Token).ConfigureAwait(false))
        {
            Check(ReferenceEquals(retained, child) && canceledDuringLaunch.IsCancellationRequested && starts == 1,
                "post-launch cancellation replaced/repeated/abandoned the original process");
            Check(observed is { UseShellExecute: true, Verb: "runas", WindowStyle: ProcessWindowStyle.Hidden } &&
                observed.FileName == host && observed.WorkingDirectory == Path.GetDirectoryName(host) && observed.Arguments.Length == 0 &&
                observed.ArgumentList.SequenceEqual(new[] { CortexScheduledTaskElevation.HelperFlag, Ticket, "123", Start }),
                "task launch includes an arbitrary target/payload or loses exact parent generation");
            Check(retained.Identity == child.Identity && await retained.WaitForExitAsync().ConfigureAwait(false) == 0 && child.Waits == 1,
                "original process identity/wait was not preserved");
        }
        Check(child.Disposals == 1 && protections == 1, "original process or protection ownership changed");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(await RefusedAsync(() => launcher.StartScheduledTaskAsync(Ticket, canceled.Token)).ConfigureAwait(false) && starts == 1,
            "prelaunch cancellation started a helper");
        Check(await RefusedAsync(() => launcher.StartScheduledTaskAsync("invalid", default)).ConfigureAwait(false) && starts == 1,
            "invalid task ticket started a helper");
        foreach (var denied in new[] { owner with { Elevated = true }, owner with { Session = 0 }, owner with { Session = 8 },
            owner with { Sid = "S-1-5-18" }, owner with { ApplicationPath = host + ".other" }, owner with { ProcessId = 124 } })
        {
            current = denied;
            Check(await RefusedAsync(() => launcher.StartScheduledTaskAsync(Ticket, default)).ConfigureAwait(false) && starts == 1,
                "owner/session/image/process mismatch reached native launch");
        }
        current = owner; parent = parent with { Start = "2026-11-21T00:00:00Z" };
        Check(await RefusedAsync(() => launcher.StartScheduledTaskAsync(Ticket, default)).ConfigureAwait(false), "noncanonical parent generation was accepted");
        parent = parent with { Start = Start };
        var deniedImage = new WindowsOwnerElevationLauncher(layout, owner, () => owner, () => parent,
            (_, _) => throw new UnauthorizedAccessException("fixture-protection-refusal"), _ => throw new InvalidOperationException("unexpected-start"));
        Check(await RefusedAsync(() => deniedImage.StartScheduledTaskAsync(Ticket, default)).ConfigureAwait(false), "unprotected image reached native launch");

        var flags = new List<string>();
        var compatibility = new WindowsCertificateElevationLauncher(layout, owner, () => owner, () => parent,
            (_, _) => { }, info => { flags.Add(info.ArgumentList[0]); return new ProcessFixture(new(456, Start, host)); });
        using (await compatibility.StartAsync(Ticket, default).ConfigureAwait(false)) { }
        using (await compatibility.StartMachineServerAsync(Ticket, default).ConfigureAwait(false)) { }
        Check(flags.SequenceEqual(new[] { "--certificate-store-once", CortexMachineServerCertificateElevation.HelperFlag }),
            "certificate compatibility wrappers changed existing fixed flags");
    }

    private static async Task HelperAsync()
    {
        var owner = new LocalPeer(Sid, 7, Host, 456, Elevated: true);
        CertificateElevationProcessIdentity? Capture(LocalPeer peer) => new(peer.ProcessId!.Value, Start, peer.ApplicationPath!);
        foreach (var action in new[] { "read", "instances", "run", "stop", "enable", "disable", "remove" })
        {
            var payload = Payload(action);
            await using var transport = new Transport(payload);
            var executor = new Executor();
            var exit = await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, default).ConfigureAwait(false);
            var valid = exit == 0 &&
                transport.Redeems == 1 && transport.Completes == 1 && executor.Calls == 1 &&
                transport.Completion is { Result.Ok: true, Failure: null } && transport.Completion.Result.Action == action;
            if (!valid) Console.WriteLine("DIAGNOSTIC scheduled-task-helper " + JsonSerializer.Serialize(new
                { action, exit, redeem = transport.Redeems, execute = executor.Calls, complete = transport.Completes }));
            Check(valid, "one exact task action did not redeem/execute/report once");
            Check(executor.Invocation is { RequiresOwnerElevation: true } && executor.Invocation.Action == action &&
                executor.Invocation.TaskPath == TaskPath && executor.Owner == owner, "task payload changed authority/target/action");
        }
        foreach (var denied in new[] { owner with { Elevated = false }, owner with { Session = 0 }, owner with { Sid = "S-1-5-18" },
            owner with { ApplicationPath = Host + ".other" } })
        {
            await using var transport = new Transport(Payload("read")); var executor = new Executor();
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => denied, Capture, transport, executor, default).ConfigureAwait(false) == 3 &&
                transport.Redeems == 0 && executor.Calls == 0, "nonmatching High owner redeemed a payload");
        }
        foreach (var raw in new[] { "{}", ScheduledTaskElevationContract.SerializeRequest(Payload("read")).Replace("\"action\":\"read\"", "\"action\":\"list\"", StringComparison.Ordinal),
            ScheduledTaskElevationContract.SerializeRequest(Payload("read")).Insert(1, "\"command\":\"forbidden\",") })
        {
            await using var transport = new Transport(Payload("read")) { Raw = raw }; var executor = new Executor();
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, default).ConfigureAwait(false) == 3 &&
                executor.Calls == 0 && transport.Completes == 0, "malformed or expanded task payload reached the executor");
        }
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await using var transport = new Transport(Payload("run")); var executor = new Executor();
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, cancellation.Token).ConfigureAwait(false) == 3 &&
                transport.Redeems == 0 && executor.Calls == 0, "pre-redemption cancellation entered control/provider");
        }
        {
            using var cancellation = new CancellationTokenSource();
            await using var transport = new Transport(Payload("run")) { AfterRedeem = cancellation.Cancel }; var executor = new Executor();
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, cancellation.Token).ConfigureAwait(false) == 0 &&
                cancellation.IsCancellationRequested && executor.Calls == 1 && transport.Completes == 1,
                "post-redemption cancellation abandoned delivered work or report");
        }
        {
            var current = owner;
            await using var transport = new Transport(Payload("run")) { AfterRedeem = () => current = owner with { Elevated = false } };
            var executor = new Executor();
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => current, Capture, transport, executor, default).ConfigureAwait(false) == 4 &&
                executor.Calls == 0 && transport.Completes == 0, "lost High identity executed or reported through unauthorized context");
        }
        {
            await using var transport = new Transport(Payload("run"));
            var executor = new Executor { Work = _ => throw new IOException("unreportable-fixture-detail") };
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, default).ConfigureAwait(false) == 0 &&
                executor.Calls == 1 && transport.Completion is { Result: null, Failure: "scheduled_task_helper_failed" },
                "unobserved provider failure invented a native issued boundary");
            Check(transport.CompletionText is not null && !transport.CompletionText.Contains("unreportable-fixture-detail", StringComparison.Ordinal),
                "raw exception detail crossed completion");
        }
        {
            await using var transport = new Transport(Payload("run"));
            var executor = new Executor { Work = action => Task.FromResult(new WindowsTaskOutcome(false, action, Phase: "run",
                Error: "task_native_failed", ActionIssued: true, OutcomeUnknown: true, NativeError: unchecked((int)0x80004005))) };
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, default).ConfigureAwait(false) == 0 &&
                transport.Completion is { Result: { ActionIssued: true, OutcomeUnknown: true, NativeError: not null } },
                "explicit native uncertainty was dropped or retried");
        }
        {
            await using var transport = new Transport(Payload("read"));
            var executor = new Executor { Work = action => Task.FromResult(new WindowsTaskOutcome(true, action, Current: Details() with { Path = @"\Other" })) };
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, default).ConfigureAwait(false) == 4 &&
                executor.Calls == 1 && transport.Completes == 0, "wrong-target result was reported as a successful exact action");
        }
        {
            await using var transport = new Transport(Payload("run")) { LoseCompletion = true }; var executor = new Executor();
            Check(await CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, default).ConfigureAwait(false) == 4 &&
                executor.Calls == 1 && transport.Completes == 1, "lost acknowledgement repeated invocation or completion");
        }
        {
            using var cancellation = new CancellationTokenSource();
            await using var transport = new Transport(Payload("run"));
            var settlement = new TaskCompletionSource<WindowsTaskOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executor = new Executor { Work = _ => settlement.Task };
            var pending = CortexScheduledTaskElevation.RunOnceAsync(Ticket, owner, () => owner, Capture, transport, executor, cancellation.Token);
            Check(!pending.IsCompleted && executor.Calls == 1 && transport.Completes == 0, "helper reported before the original callback settled");
            cancellation.Cancel();
            Check(!pending.IsCompleted && transport.Completes == 0, "cancellation abandoned the active callback");
            settlement.SetResult(Result("run"));
            Check(await pending.ConfigureAwait(false) == 0 && executor.Calls == 1 && transport.Completes == 1,
                "original callback settlement was not awaited exactly once");
        }
    }

    private static ScheduledTaskElevationRequest Payload(string action)
    {
        var body = new Dictionary<string, object?> { ["version"] = 1, ["task"] = TaskPath };
        if (action is not ("read" or "instances")) body["taskSha"] = new string('b', 64);
        if (action == "stop") body["instance"] = Instance;
        var rid = "scheduled-elevation-fixture-" + action;
        return ScheduledTaskElevationContract.Create(new OperationRequest("windows_task", new Dictionary<string, string>
            { ["op"] = action, ["elevate"] = "uac" }, rid, Body: JsonSerializer.Serialize(body)), new string('c', 32), rid);
    }
    private static WindowsTaskDetails Details() => new(TaskPath, "ready", true, false, true,
        new string('b', 64), new string('d', 64), new string('e', 64), "different_or_noninteractive_identity", "highest", 1, 1, false);
    private static WindowsTaskOutcome Result(string action) => action switch
    {
        "read" => new(true, action, Current: Details()),
        "instances" => new(true, action, Current: Details(), Instances: new(Array.Empty<WindowsTaskInstance>(), 0, false, false)),
        "run" => new(true, action, Phase: action, ActionIssued: true, Previous: Details(), Instance: new(Instance, TaskPath, "running"),
            Observation: "instance_observed_not_completion"),
        "stop" => new(true, action, Phase: action, ActionIssued: true, Previous: Details(), Instance: new(Instance, TaskPath, "running"),
            Observation: "exact_instance_stop_acknowledged_not_process_tree_completion"),
        "enable" or "disable" => new(true, action, Phase: action, ActionIssued: true, Previous: Details(),
            Current: Details() with { Enabled = action == "enable" },
            Observation: action == "enable" ? "enabled_future_triggers_may_run" : "disabled_running_instances_not_stopped"),
        "remove" => new(true, action, Phase: action, ActionIssued: true, Previous: Details(),
            Observation: "registration_absent_running_instances_not_stopped"),
        _ => throw new InvalidOperationException("unexpected-fixture-action")
    };
    private sealed class Executor : ICortexScheduledTaskExecutor
    {
        internal int Calls;
        internal WindowsTaskInvocation? Invocation;
        internal LocalPeer? Owner;
        internal Func<string, Task<WindowsTaskOutcome>> Work = action => Task.FromResult(Result(action));
        public Task<WindowsTaskOutcome> ExecuteAsync(WindowsTaskInvocation invocation, LocalPeer owner, Action requireHigh)
        { Calls++; Invocation = invocation; Owner = owner; requireHigh(); return Work(invocation.Action); }
    }
    private sealed class Transport(ScheduledTaskElevationRequest payload) : IOperationDispatcher
    {
        internal int Redeems, Completes;
        internal string? Raw, CompletionText;
        internal Action? AfterRedeem;
        internal bool LoseCompletion;
        internal ScheduledTaskElevationCompletion? Completion;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken token = default)
        {
            Check(!token.CanBeCanceled && ScheduledTaskElevationContract.ParseTicket(request) == Ticket,
                "helper control changed ticket/context or allowed delivered work to be abandoned");
            if (request.Op == ScheduledTaskElevationContract.RedeemOperation)
            {
                Redeems++; AfterRedeem?.Invoke();
                return Task.FromResult(new OperationReceipt(true, payload.Jid, payload.Rid, "done", request.Op,
                    Data: new Dictionary<string, object?> { ["payload"] = Raw ?? ScheduledTaskElevationContract.SerializeRequest(payload) }));
            }
            Completes++; CompletionText = request.Body;
            Completion = ScheduledTaskElevationContract.ParseCompletion(request, payload);
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
        catch (Exception failure) when (failure is AgentOpsException or ScheduledTaskElevationContractException or OperationCanceledException or UnauthorizedAccessException)
        { return true; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }
}
