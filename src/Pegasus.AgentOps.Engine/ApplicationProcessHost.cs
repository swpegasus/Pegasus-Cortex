using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Pegasus.AgentOps.Internal;

namespace Pegasus.AgentOps.Engine;

// The native handle retains both identity and accounting until observed quiescence.
// This seam also lets deterministic tests exercise the actual wire/state machine.
internal interface IApplicationHostHandle : IDisposable
{
    int ActiveProcesses { get; }
    bool HelperHasExited { get; }
    Stream Responses { get; }
    bool RequestTermination();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class ApplicationProcessHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IApplicationHostHandle _handle;
    private readonly TimeSpan _stopTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;
    private string _state = "running";
    private string _terminalState = "exited";
    private int? _exit;
    private string? _issue;
    private int _disposed;
    private bool _handleReleased;
    private Task<bool>? _retirement;
    internal int Pid { get; }
    internal string Start { get; }

    internal ApplicationProcessHost(IApplicationHostHandle handle, int pid, string start, TimeSpan? stopTimeout = null)
    {
        _handle = handle; Pid = pid; Start = start;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        _monitor = MonitorAsync();
    }

    internal static async Task<ApplicationProcessHost> StartAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        using var lease = ToolRecipeValidation.AcquireInvocationLease(invocation);
        var bytes = ProcessHostWire.CreateRequest(invocation.ExecutablePath, invocation.Arguments,
            invocation.WorkingDirectory, invocation.Environment, cancellationToken);
        var job = WindowsJob.Create(); Process? helper = null;
        try
        {
            var path = ProcessRunner.ResolveProcessHost();
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)!
            };
            start.ArgumentList.Add("--app-stdio-v1");
            start.ArgumentList.Add(invocation.Window == ToolWindowMode.Visible ? "visible" : "hidden");
            helper = Process.Start(start) ?? throw new AgentOpsException("app_launch_failed");
            job.AssignAndVerify(helper);
            _ = DiscardAsync(helper.StandardError.BaseStream);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(10, invocation.Timeout.TotalSeconds)));
            await ProcessHostWire.WriteRequestAsync(helper.StandardInput.BaseStream, bytes, deadline.Token);
            var frame = await ApplicationHostFrame.ReadAsync(helper.StandardOutput.BaseStream, deadline.Token);
            if (frame.Kind != ApplicationHostFrame.Started || frame.Pid <= 0 || frame.ExitCode != 0 ||
                frame.StartTicks <= DateTime.MinValue.Ticks || frame.StartTicks > DateTime.MaxValue.Ticks)
                throw new AgentOpsException("app_launch_failed");
            cancellationToken.ThrowIfCancellationRequested();
            return new ApplicationProcessHost(new NativeApplicationHostHandle(job, helper), frame.Pid,
                new DateTime(frame.StartTicks, DateTimeKind.Utc).ToString("O"));
        }
        catch
        {
            job.Terminate(); job.Dispose();
            if (helper is not null)
            {
                // Before assignment failure this bootstrap is gated and has no target child.
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                helper.Dispose();
            }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal (string State, int? Exit, int Processes, string? Issue) Snapshot()
    {
        lock (_gate)
        {
            if (_disposed != 0) throw new AgentOpsException("app_owner_lost");
            if (_state is "stopped" or "exited") return (_state, _exit, 0, _issue);
            var active = _handle.ActiveProcesses;
            if (_issue == "app_observation_incomplete") _issue = null;
            // Query accounting before helper exit, avoiding subtraction of a helper
            // that exited between the two observations.
            var helperExited = _handle.HelperHasExited;
            var targets = Math.Max(0, active - (helperExited ? 0 : 1));
            if (_state == "stopping")
            {
                if (active == 0) CompleteLocked(_terminalState);
            }
            else if (helperExited) MarkHostFailureLocked("app_host_lost");
            else if (_state == "root_exited_workers_running" && targets == 0)
            { _state = "stopping"; _terminalState = "exited"; RequestTerminationLocked(); }
            return (_state, _exit, targets, _issue);
        }
    }


    // Inventory observation must not drive termination, including helper retirement.
    internal (string State, int? Exit, int? Processes, string? Issue) ObserveOnly()
    {
        lock (_gate)
        {
            if (_disposed != 0) throw new AgentOpsException("app_owner_lost");
            if (_state is "stopped" or "exited") return (_state, _exit, 0, _issue);
            var active = _handle.ActiveProcesses;
            var helperExited = _handle.HelperHasExited;
            return (_state, _exit, Math.Max(0, active - (helperExited ? 0 : 1)),
                helperExited ? "app_host_lost" : _issue);
        }
    }

    // Cached terminal state alone is insufficient while asynchronous retirement is pending.
    internal bool ReleaseReady
    {
        get
        {
            lock (_gate)
                return _disposed == 0 && (_state is "stopped" or "exited") &&
                    _handleReleased && _retirement is { IsCompletedSuccessfully: true } && _retirement.Result;
        }
    }

    internal IReadOnlyDictionary<string, object?> InspectProcesses()
    {
        lock (_gate)
        {
            if (_handle is not NativeApplicationHostHandle native || _state is "stopped" or "exited")
                return new Dictionary<string, object?> { ["state"] = _state };
            return native.InspectProcesses();
        }
    }

    internal (string State, int? Exit) UnobservedSnapshot()
    {
        lock (_gate)
        {
            if (_disposed != 0) throw new AgentOpsException("app_owner_lost");
            return (_state, _exit);
        }
    }

    private void CompleteLocked(string state)
    {
        _state = state;
        _retirement = ReleaseCompletedHandleAsync();
    }

    private async Task<bool> ReleaseCompletedHandleAsync()
    {
        await Task.Yield();
        using var deadline = new CancellationTokenSource(_stopTimeout);
        try
        {
            await _handle.WaitForExitAsync(deadline.Token);
            lock (_gate) { _handle.Dispose(); _handleReleased = true; }
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException || IsHostFailure(ex))
        {
            lock (_gate) { _state = "stopping"; _issue = "app_stop_incomplete"; }
            return false;
        }
    }

    private static bool IsHostFailure(Exception exception) => exception is IOException or InvalidDataException or
        InvalidOperationException or Win32Exception or AgentOpsException;

    private void RequestTerminationLocked()
    {
        try { if (!_handle.RequestTermination()) _issue ??= "app_stop_incomplete"; }
        catch (Exception exception) when (IsHostFailure(exception)) { _issue ??= "app_stop_incomplete"; }
    }

    private void MarkHostFailureLocked(string issue)
    {
        if (_state is "stopped" or "exited") return;
        _issue ??= issue; _state = "stopping";
        RequestTerminationLocked();
    }

    private async Task MonitorAsync()
    {
        try
        {
            var frame = await ApplicationHostFrame.ReadAsync(_handle.Responses, _stop.Token);
            if (frame.Kind != ApplicationHostFrame.RootExited || frame.Pid != 0 || frame.StartTicks != 0)
                throw new InvalidDataException("app_host_protocol");
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) return;
                _exit = frame.ExitCode;
                if (_state != "stopping") _state = "root_exited_workers_running";
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsHostFailure(ex))
        {
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) return;
                MarkHostFailureLocked(ex is InvalidDataException ? "app_host_protocol" : "app_host_lost");
            }
        }
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (Snapshot().State is "stopped" or "exited")
                    { if (_retirement is not null) await _retirement; return; }
                }
                catch (AgentOpsException ex) when (ex.Code == "app_observation_incomplete")
                {
                    // A bounded or temporarily unstable observation is unknown,
                    // not proof of host failure and not authority to kill it.
                    lock (_gate) { _issue ??= ex.Code; }
                }
                catch (Exception ex) when (IsHostFailure(ex))
                {
                    lock (_gate)
                    {
                        if (_stop.IsCancellationRequested) return;
                        MarkHostFailureLocked("app_host_lost");
                    }
                }
                await Task.Delay(100, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal async Task StopAsync()
    {
        Task<bool>? retirement;
        lock (_gate)
        {
            if (_disposed != 0) throw new AgentOpsException("app_owner_lost");
            retirement = _state is "stopped" or "exited" ? _retirement : null;
            if (retirement is null)
            { _state = "stopping"; _terminalState = "stopped"; _stop.Cancel(); RequestTerminationLocked(); }
        }
        if (retirement is not null)
        { if (!await retirement) throw new AgentOpsException("app_stop_incomplete"); return; }
        using var deadline = new CancellationTokenSource(_stopTimeout);
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_state is "stopped" or "exited") break;
                    if (_handle.ActiveProcesses == 0) { CompleteLocked("stopped"); break; }
                }
                await Task.Delay(25, deadline.Token);
            }
            if (_retirement is not null && !await _retirement.WaitAsync(deadline.Token))
                throw new AgentOpsException("app_stop_incomplete");
        }
        catch (Exception ex) when (ex is OperationCanceledException || IsHostFailure(ex))
        {
            // A kill request is not proof of termination. Keep the exact handle
            // queryable and the generation nonterminal until a later observation.
            lock (_gate) { _issue = "app_stop_incomplete"; }
            throw new AgentOpsException("app_stop_incomplete", inner: ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            _stop.Cancel();
            if (!_handleReleased && _state is not ("stopped" or "exited"))
            { _state = "stopping"; _issue = "app_owner_lost"; RequestTerminationLocked(); }
        }
        using var deadline = new CancellationTokenSource(_stopTimeout);
        AgentOpsException? failure = null;
        try
        {
            if (_retirement is not null && !await _retirement.WaitAsync(deadline.Token))
                throw new AgentOpsException("app_stop_incomplete");
            while (true)
            {
                lock (_gate)
                {
                    if (_handleReleased) break;
                    if (_handle.ActiveProcesses == 0) { _state = "stopped"; break; }
                }
                await Task.Delay(25, deadline.Token);
            }
            if (!_handleReleased) await _handle.WaitForExitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException || IsHostFailure(ex))
        {
            failure = new AgentOpsException("app_stop_incomplete", inner: ex);
            lock (_gate)
            {
                _state = "stopping"; _issue = failure.Code;
                try { if (!_handleReleased && _handle is NativeApplicationHostHandle native) failure.Data["observation"] = native.InspectProcesses(); }
                catch (Exception observation) when (IsHostFailure(observation)) { failure.Data["observationIssue"] = observation.GetType().Name; }
            }
        }
        finally { lock (_gate) { _handle.Dispose(); _handleReleased = true; } }
        try { await _monitor.WaitAsync(_stopTimeout); _stop.Dispose(); }
        catch (TimeoutException ex) { failure ??= new AgentOpsException("app_stop_incomplete", inner: ex); }
        if (failure is not null) throw failure;
    }

    private static async Task DiscardAsync(Stream source)
    {
        try { var buffer = new byte[4096]; while (await source.ReadAsync(buffer) != 0) { } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }

    private sealed class NativeApplicationHostHandle(WindowsJob job, Process helper) : IApplicationHostHandle
    {
        public int ActiveProcesses => job.LiveProcessCount();
        public bool HelperHasExited => helper.HasExited;
        public Stream Responses => helper.StandardOutput.BaseStream;
        public bool RequestTermination()
        {
            // Retain exact live member handles before requesting termination,
            // including any members born since the last status observation.
            var terminated = false;
            try { _ = job.LiveProcessCount(); }
            finally { terminated = job.RequestTermination(); }
            return terminated;
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => helper.WaitForExitAsync(cancellationToken);
        public void Dispose() { job.Dispose(); helper.Dispose(); }
        internal IReadOnlyDictionary<string, object?> InspectProcesses()
        {
            var inventory = job.ProcessIdInventory();
            var entries = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var pid in inventory.ProcessIds)
            {
                var entry = new Dictionary<string, object?> { ["pid"] = pid };
                try
                {
                    using var process = Process.GetProcessById(pid);
                    entry["exited"] = process.HasExited;
                    entry["start"] = process.StartTime.ToUniversalTime().ToString("O");
                    entry["exe"] = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception)
                { entry["issue"] = ex.GetType().Name; }
                entries.Add(entry);
            }
            return new Dictionary<string, object?>
            {
                ["accounted"] = job.ActiveProcessCount(), ["assigned"] = inventory.Assigned,
                ["truncated"] = inventory.Truncated, ["helper"] = helper.Id, ["processes"] = entries
            };
        }
    }
}
