using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

/// <summary>One owner-process admission boundary shared by execution and semantics.</summary>
internal sealed class UserHostAdmission
{
    private readonly object _gate = new();
    private int _active;
    private bool _closing;
    private TaskCompletionSource _idle = CompletedSignal();

    internal bool Accepting { get { lock (_gate) return !_closing; } }
    internal int ActiveCount { get { lock (_gate) return _active; } }

    internal IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (_closing) return null;
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new AdmissionLease(this);
        }
    }

    internal CloseLease? TryClose()
    {
        lock (_gate)
        {
            if (_closing) return null;
            _closing = true;
            return new CloseLease(this);
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed class AdmissionLease(UserHostAdmission owner) : IDisposable
    {
        private UserHostAdmission? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._gate)
                if (--current._active == 0) current._idle.TrySetResult();
        }
    }

    internal sealed class CloseLease(UserHostAdmission owner) : IDisposable
    {
        private bool _committed;
        private bool _disposed;

        internal Task WhenIdleAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (owner._gate) return owner._idle.Task.WaitAsync(token);
        }

        internal void Commit()
        {
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (owner._active != 0) throw new InvalidOperationException("Owner requests have not drained.");
                _committed = true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                // Refusal/cancelled elevation restores normal admission. A
                // committed exit remains closed until the process terminates.
                if (!_committed) owner._closing = false;
            }
        }
    }
}

internal static class UserHostStartup
{
    internal static AgentOpsEngine CreateOwnerEngine(CortexProductLayout layout,
        IMaintenanceModeSource maintenance, UserHostAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(admission);
        RequireOpen(maintenance);
        using var initialization = admission.TryEnter() ?? throw new AgentOpsException("host_stopping");
        CortexUserStateDirectories.EnsureExecution(layout);
        var engine = new AgentOpsEngine(layout.UserExecutionDirectory, null, false, null, true, null,
            maintenanceMode: maintenance, userHostAdmission: admission,
            connectionAuthorization:(authority,full)=>Pegasus.Cortex.Product.CortexMcpConnections.Find(
                Pegasus.Cortex.Product.CortexMcpConnections.ReadForInstallRoot(layout.InstallRoot),authority,full) is not null);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                engine.BindRegistryOwner(RegistryOwnerBinding.ForOwner(layout.OwnerSid));
                engine.BindCertificateOwner(CertificateOwnerBinding.ForOwner(layout));
                engine.BindSmbOwner(SmbOwnerBinding.ForOwner(layout));
                engine.BindWindowsDevices(new WindowsDeviceOperations(layout.OwnerSid));
                engine.BindWindowsNetwork(new WindowsNetworkOperations(layout.OwnerSid));
                engine.BindWindowsNetworkResolution(new WindowsNetworkResolutionOperations(layout.OwnerSid));
                engine.BindWindowsAccounts(new WindowsAccountOperations(layout.OwnerSid));
                engine.BindOwnerWindowsTasks(layout.OwnerSid);
            }
            return engine;
        }
        catch { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    internal static void RequireOpen(IMaintenanceModeSource maintenance)
    {
        try
        {
            if (maintenance.Read() is { Phase: not "off" })
                throw new AgentOpsException("maintenance_busy");
        }
        catch (AgentOpsException error) when (error.Code == "maintenance_busy") { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        { throw new AgentOpsException("maintenance_state_unavailable", inner: error); }
    }
}
