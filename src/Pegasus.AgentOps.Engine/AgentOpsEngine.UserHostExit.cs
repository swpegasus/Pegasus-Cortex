using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private OperationReceipt HostStopping(OperationRequest request, bool replayDurable = true)
    {
        if (replayDurable && request.Op is not ("terminal" or "certificate" or "installed_program" or "connection_profile" or "windows_device" or "windows_network" or "windows_account" or "windows_task" or "windows_service") && !ProcessThreadOperations.IsRequest(request) && !string.IsNullOrWhiteSpace(request.Rid))
        {
            lock (_admissionGate)
                if (!_stopping && _store.FindByRid(request.Rid) is { } original &&
                    (!RequiresPrivateReplayMatch(request, original.Receipt) ||
                        PrivateReplayMatches(request, original.Receipt))) return original.Receipt;
        }
        // This rejects only this submission. It never overwrites the durable
        // original, or establishes the outcome of an earlier lost terminal input.
        return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
            "failed", OperationNames.Public(request), "host_stopping",
            Data: new Dictionary<string, object?> { ["execution"] = OperationExecutionState.NotDispatched });
    }

    // Called only after shared admission closes. Inspect ownership; never stop
    // an application/terminal or acknowledge an uncertain effect on the user's behalf.
    internal Task<string?> UserExitBlockerAsync(CancellationToken cancellationToken)
    {
        if (_userHostAdmission is null || _userHostAdmission.Accepting) return Task.FromResult<string?>("exit_not_prepared");
        // Stopping an idle user host neither resolves an old external effect
        // nor acknowledges its receipt. Historical uncertainty remains durable.
        return InspectExitIssueCoreAsync(cancellationToken, includeRetainedUncertainty: false);
    }

    // The caller must own its admission fence. This read-only inspection does
    // not close a user/service route or authorize stopping either process.
    internal Task<string?> InspectExitIssueAsync(CancellationToken cancellationToken) =>
        InspectExitIssueCoreAsync(cancellationToken, includeRetainedUncertainty: true);

    private async Task<string?> InspectExitIssueCoreAsync(CancellationToken cancellationToken,
        bool includeRetainedUncertainty)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_admissionGate)
        {
            if (_stopping) return "host_stopping";
            if (_activeEffects.Count != 0 || _applicationTransitions.Count != 0 ||
                !_scheduler.IsIdle || !_queued.IsEmpty || !_jobs.IsEmpty || _recoveryPending)
                return "work_active";

            // The service/maintenance caller retains the conservative durable
            // uncertainty check. Ordinary user Exit still requires every live
            // owner, queued job and recovery transition above to be idle.
            if (includeRetainedUncertainty)
            {
                long after = 0;
                for (var page = 0; page < 128; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var effects = _store.ListMaintenanceEffects(after);
                    foreach (var effect in effects.Effects)
                        if (!_maintenanceMode.IsAcknowledged(effect)) return "work_unresolved";
                    if (!effects.HasMore) break;
                    if (effects.Next <= after || page == 127) return "work_unresolved";
                    after = effects.Next;
                }
            }
        }
        return _applications is not null && await _applications.HasLiveApplicationsAsync(cancellationToken)
            ? "applications_active" : null;
    }
}
