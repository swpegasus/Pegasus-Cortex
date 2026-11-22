using System.Diagnostics;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>In-memory admission lease for one explicit elevated service stop.</summary>
internal sealed class ServiceExitAdmission
{
    private readonly object _gate = new();
    private int _active;
    private string? _lease;
    private MaintenanceHostIdentity? _owner;
    private bool _prepared;
    private bool _committed;
    private bool _stopIssued;

    internal bool TryEnter(out IDisposable? admission)
    {
        lock (_gate)
        {
            admission = null;
            if (_lease is not null) return false;
            _active++;
            admission = new Release(this);
            return true;
        }
    }

    internal async Task<(string? Lease, string? Issue)> PrepareAsync(MaintenanceHostIdentity owner,
        Func<CancellationToken, Task<string?>> inspect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string lease;
        lock (_gate)
        {
            if (_lease is not null)
            {
                // A second explicit stop may recover a prepare whose elevated
                // helper exited. It cannot steal a still-running helper's lease.
                if (_prepared && SameOwner(owner, _owner!) && !OwnerAlive(_owner!))
                { _owner = owner; return (_lease, null); }
                return (null, "service_exit_pending");
            }
            if (_active != 0) return (null, "service_work_active");
            _lease = lease = Guid.NewGuid().ToString("N");
            _owner = owner;
        }
        try
        {
            var issue = await inspect(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (issue is not null) { Cancel(owner, lease); return (null, issue); }
            lock (_gate) _prepared = true;
            return (lease, null);
        }
        catch { Cancel(owner, lease); throw; }
    }

    internal bool Cancel(MaintenanceHostIdentity owner, string lease)
    {
        lock (_gate)
        {
            if (_lease != lease || _owner != owner || _committed) return false;
            _lease = null; _owner = null; _prepared = false;
            return true;
        }
    }

    internal bool Commit(MaintenanceHostIdentity owner, string lease)
    {
        lock (_gate)
        {
            if (!_prepared || _lease != lease || _owner != owner) return false;
            _committed = true;
            return true;
        }
    }

    internal bool IsCommitted(string? lease)
    {
        lock (_gate) return _committed && _lease is not null && _lease == lease;
    }

    internal bool InvokeStopAfterReply(string? lease, Action stop)
    {
        lock (_gate)
        {
            if (!_committed || _lease is null || _lease != lease) return false;
            if (_stopIssued) return true;
            _stopIssued = true;
        }
        stop();
        return true;
    }

    private static bool SameOwner(MaintenanceHostIdentity left, MaintenanceHostIdentity right) =>
        left.Sid == right.Sid && left.Session == right.Session &&
        string.Equals(left.ApplicationPath, right.ApplicationPath, StringComparison.OrdinalIgnoreCase);

    private static bool OwnerAlive(MaintenanceHostIdentity owner)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)owner.Pid));
            return !process.HasExited && process.StartTime.ToUniversalTime().ToString("O",
                System.Globalization.CultureInfo.InvariantCulture) == owner.Start;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // unknown remains reserved
    }

    private sealed class Release(ServiceExitAdmission owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) owner._active--;
        }
    }
}

public sealed partial class AuthenticatedServiceBroker
{
    internal const string ServiceExitOperation = "_cortex_service_exit";
    private readonly ServiceExitAdmission _serviceExitAdmission;
    private Func<CancellationToken, Task<string?>>? _serviceEngineExitIssue;
    private string? _serviceExitHost;
    private Action? _serviceStopApplication;

    internal void SetServiceExitProbe(Func<CancellationToken, Task<string?>> inspect, string? expectedHost,
        Action? stopApplication = null)
    {
        _serviceEngineExitIssue = inspect ?? throw new ArgumentNullException(nameof(inspect));
        _serviceExitHost = expectedHost is null ? null : Path.GetFullPath(expectedHost);
        _serviceStopApplication = stopApplication;
    }

    internal static bool IsServiceExitPeer(LocalPeer peer, string ownerSid, string expectedHost) =>
        peer.Sid == ownerSid && peer.Session > 0 && peer.Elevated && peer.ProcessId is not null &&
        string.Equals(peer.ApplicationPath, expectedHost, StringComparison.OrdinalIgnoreCase);

    private async Task<OperationReceipt> ServiceExitAsync(OperationRequest request, LocalPeer peer,
        CancellationToken cancellationToken)
    {
        if (_serviceExitHost is null || _serviceStopApplication is null)
            return Fail(request, "service_shutdown_probe_unavailable");
        if (!IsServiceExitPeer(peer, _ownerSid, _serviceExitHost) ||
            request.Root is not null || request.Arg.Keys.Any(key => key is not ("op" or "lease")))
            return Fail(request, "auth");
        MaintenanceHostIdentity owner;
        try { owner = OwnerAdminExecution.HostIdentity(peer); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or AgentOpsException)
        { return Fail(request, "service_shutdown_peer_unavailable"); }
        if (request.Arg.GetValueOrDefault("op") == "cancel")
            return _serviceExitAdmission.Cancel(owner, request.Arg.GetValueOrDefault("lease", ""))
                ? ServiceExitReceipt(request, null, "cancelled") : Fail(request, "service_exit_lease_mismatch");
        if (request.Arg.GetValueOrDefault("op") == "commit")
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = request.Arg.GetValueOrDefault("lease", "");
            return _serviceExitAdmission.Commit(owner, lease)
                ? ServiceExitReceipt(request, lease, "committed") : Fail(request, "service_exit_lease_mismatch");
        }
        if (request.Arg.GetValueOrDefault("op") != "prepare" || request.Arg.ContainsKey("lease"))
            return Fail(request, "invalid");
        try
        {
            var result = await _serviceExitAdmission.PrepareAsync(owner, InspectServiceExitAsync,
                cancellationToken).ConfigureAwait(false);
            return result.Issue is null
                ? ServiceExitReceipt(request, result.Lease, "prepared") : Fail(request, result.Issue);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            AgentOpsException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { return Fail(request, "service_shutdown_state_unavailable"); }
    }

    // Invoked by the SYSTEM pipe host only AFTER its committed response was
    // written. A replacement process has neither this lease nor this callback.
    internal void OnServiceExitReply(OperationReceipt receipt)
    {
        if (receipt.Op != ServiceExitOperation || !receipt.Ok || receipt.State != "done" ||
            receipt.Data is not { } data || data.GetValueOrDefault("state") as string != "committed" ||
            !_serviceExitAdmission.IsCommitted(data.GetValueOrDefault("lease") as string)) return;
        _serviceExitAdmission.InvokeStopAfterReply(data.GetValueOrDefault("lease") as string, _serviceStopApplication!);
    }

    private async Task<string?> InspectServiceExitAsync(CancellationToken cancellationToken)
    {
        lock (_maintenanceGate)
        {
            if (_ordinaryTransitions != 0 || !_activeRouted.IsEmpty ||
                _store.FirstUnresolvedMutation(_ownerSid) is not null) return "service_work_unresolved";
            if (_ownerAdmin is null) return "service_admin_state_unavailable";
            if (_ownerAdmin.BlockingJob() is not null) return "service_admin_work_unresolved";
        }
        return _serviceEngineExitIssue is null ? "service_shutdown_probe_unavailable" :
            await _serviceEngineExitIssue(cancellationToken).ConfigureAwait(false);
    }

    private static OperationReceipt ServiceExitReceipt(OperationRequest request, string? lease, string state)
    {
        using var process = Process.GetCurrentProcess();
        return new(true, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
            "done", ServiceExitOperation, Data: new Dictionary<string, object?>
            {
                ["state"] = state, ["lease"] = lease, ["service_pid"] = Environment.ProcessId,
                ["service_start_ticks"] = process.StartTime.ToUniversalTime().Ticks
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
    }
}
