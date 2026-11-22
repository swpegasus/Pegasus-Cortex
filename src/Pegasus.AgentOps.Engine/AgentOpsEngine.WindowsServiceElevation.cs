using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private sealed record WindowsServiceElevationBinding(OwnerHostIdentity Owner, OwnerWindowsServiceElevation Elevation);
    private WindowsServiceElevationBinding? _windowsServiceElevation;

    internal void BindWindowsServiceElevation(OwnerWindowsServiceElevation elevation, string ownerSid,
        Func<LocalPeer>? current = null)
    {
        ArgumentNullException.ThrowIfNull(elevation);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var binding = new WindowsServiceElevationBinding(new(ownerSid, current ?? LocalIdentity.Current), elevation);
        if (_directOnly || _userHostAdmission is null || !binding.Owner.IsCurrentOwner() ||
            Interlocked.CompareExchange(ref _windowsServiceElevation, binding, null) is not null)
            throw new AgentOpsException("auth");
    }

    internal Task<OperationReceipt> HandleWindowsServiceElevationAsync(OperationRequest request, LocalPeer peer,
        CancellationToken token) => _windowsServiceElevation is { } binding && WindowsServiceElevationContract.IsControlRequest(request)
        ? binding.Elevation.HandleAsync(request, peer, token)
        : Task.FromResult(new OperationReceipt(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
            "failed", "windows_service_elevation", "auth"));

    private async Task<OperationReceipt> ExecuteElevatedWindowsServiceReceiptAsync(OperationRequest request,
        WindowsServiceInvocation invocation, string jid, string rid, long sequence, CancellationToken token, bool persist)
    {
        var issued = false;
        var data = new Dictionary<string, object?>
        { ["seq"] = sequence, ["log_available"] = false, ["requestIdentity"] = invocation.RequestIdentity, ["phase"] = "uac" };
        OperationReceipt receipt;
        try
        {
            var binding = _windowsServiceElevation;
            if (!persist || _directOnly || _userHostAdmission is null || binding?.Owner.IsCurrentOwner() != true ||
                !invocation.RequiresOwnerElevation || request.Arg.GetValueOrDefault("__execution_context_system") == "1")
                throw new AgentOpsException("auth");
            token.ThrowIfCancellationRequested();
            var payload = WindowsServiceElevationContract.Create(WindowsServicePublicShape(request), jid, rid);
            var observed = await binding.Elevation.BeginAsync(payload, PersistIssued, token).ConfigureAwait(false);
            issued = observed.PayloadIssued;
            data["helper_payload_issued"] = issued;
            if (request.Detail && observed.Process is { } process)
            { data["helper_pid"] = (long)process.Pid; data["helper_start"] = process.Start; }
            if (request.Detail && observed.Exit is { } exit) data["helper_exit"] = exit;
            if (observed.Result is { } result)
            {
                if (!issued || result.Action != invocation.Action) throw new AgentOpsException("invalid");
                // A validated report remains evidence even if its final ACK was
                // lost. The shared coordinator still reaps the original child.
                receipt = WindowsServiceOutcomeReceipt(request, invocation, jid, rid, result, data);
            }
            else
            {
                if (observed.NativeError is { } status) data["windows_status"] = status;
                receipt = Unobserved(observed.Failure ?? "service_elevation_no_completion");
            }
        }
        catch (Exception failure) when (failure is AgentOpsException or ArgumentException or OperationCanceledException)
        {
            receipt = Unobserved(failure is AgentOpsException known ? known.Code :
                failure is OperationCanceledException ? "canceled" : "invalid");
        }
        return Finish(receipt, request, sequence, persist);

        void PersistIssued(CertificateElevationProcessIdentity process)
        {
            // Persist the original helper generation and uncertainty barrier
            // before releasing any service payload. These are NOT service PIDs.
            if (!persist) throw new AgentOpsException("precondition");
            data["helper_pid"] = (long)process.Pid; data["helper_start"] = process.Start;
            var pending = new Dictionary<string, object?>(data)
            { ["helper_payload_issued"] = true, ["outcome_unknown"] = true };
            var running = new OperationReceipt(true, jid, rid, "running", OperationNames.Public(request), Data: pending);
            var durable = new DurableJob(WindowsServiceDurableReceipt(running, request), true, sequence,
                WindowsServiceDurableRequest(request));
            _store.Update(durable); issued = true; _jobs[jid] = durable;
        }

        OperationReceipt Unobserved(string cause)
        {
            var canceled = cause is "canceled" or "service_elevation_canceled" or "service_elevation_declined" or
                "owner_elevation_canceled" or "owner_elevation_declined";
            var code = issued ? "recovery_required" : canceled ? "canceled" :
                cause is "auth" or "invalid" or "precondition" ? cause : "service_elevation_failed";
            data["helper_payload_issued"] = issued; data["outcome_unknown"] = issued;
            data["postcondition_observed"] = false;
            // After payload release there is no native evidence yet. Preserve
            // that absence instead of reporting a fictitious unissued action.
            if (!issued) data["action_issued"] = false;
            data["service_code"] = RegistryReceiptAdapter.IsSafeCode(cause) ? cause : "service_elevation_failed";
            return new(false, jid, rid, issued ? "recovery_required" : canceled ? "canceled" : "failed",
                OperationNames.Public(request), code, data, Partial: issued,
                Failure: FailureEvidence.Generic(code, issued, "windows-service-uac"));
        }
    }
}
