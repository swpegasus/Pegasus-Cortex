using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private OwnerCertificateStoreElevation? _certificateElevation;

    // Trusted ordinary-host composition only. A request cannot supply a launcher,
    // administrator identity, service route, executable or elevation capability.
    internal void BindCertificateStoreElevation(OwnerCertificateStoreElevation elevation)
    {
        ArgumentNullException.ThrowIfNull(elevation);
        if (_directOnly || _userHostAdmission is null || _certificateOwner?.IsCurrentOwner() != true ||
            Interlocked.CompareExchange(ref _certificateElevation, elevation, null) is not null)
            throw new AgentOpsException("auth");
    }

    internal Task<OperationReceipt> HandleCertificateElevationAsync(OperationRequest request, LocalPeer peer,
        CancellationToken token) => _certificateElevation is { } elevation &&
            (CertificateStoreElevationContract.IsControlRequest(request) || MachineServerCertificateElevationContract.IsControlRequest(request))
        ? elevation.HandleAsync(request, peer, token)
        : Task.FromResult(CertificateReceiptAdapter.Failed(request, Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), "auth"));

    private async Task<OperationReceipt> ExecuteCertificateAdministrationAsync(OperationRequest request,
        string jid, string rid, long sequence, CancellationToken token, bool persist)
    {
        if (request.Body is null && request.Arg.ContainsKey(OperationRequest.BodyOmittedKey))
            return Finish(CertificateReceiptAdapter.LostBody(request, jid, rid), request, sequence, persist);

        var issued = false;
        var data = new Dictionary<string, object?> { ["seq"] = sequence, ["log_available"] = false };
        OperationReceipt receipt;
        try
        {
            var invocation = CertificateTrustedInvocation(request);
            if (!CertificateOperationContract.IsAdministratorAction(invocation.Action) ||
                invocation.Store is not { Scope: "local_machine" } store)
                throw new AgentOpsException("invalid");
            if (_certificateOwner?.IsCurrentOwner() != true) throw new AgentOpsException("auth");
            var elevation = _certificateElevation ?? throw new AgentOpsException("certificate_elevation_unavailable");
            token.ThrowIfCancellationRequested();
            data["requestIdentity"] = invocation.RequestIdentity;
            data["certificateId"] = invocation.CertificateId;
            data["store_scope"] = store.Scope; data["store_kind"] = store.Store;
            if (invocation.Reference is not null) data["reference"] = invocation.Reference;
            var action = invocation.Action switch
            {
                "store_admin_add" => "store_add", "store_admin_trust" => "store_trust",
                "store_admin_remove" => "store_remove", _ => throw new AgentOpsException("invalid")
            };
            // Inspect is public-only. The elevated helper never sees a vault,
            // private key, password reference, credential or arbitrary filename.
            var der = action == "store_remove" ? null :
                _certificateOwner.Repository().Inspect(invocation.Reference!, invocation.CertificateId).PublicDer;
            var payload = new AdminCertificateStoreRequest(1, action, store.Store, invocation.CertificateId!,
                der, invocation.RequestIdentity, jid, rid);
            var outcome = await elevation.BeginAsync(payload, PersistIssued, token).ConfigureAwait(false);
            issued = outcome.PayloadIssued;
            data["administrator_payload_issued"] = issued;
            if (outcome.Process is { } process)
            {
                data["administrator_pid"] = (long)process.Pid;
                data["administrator_start"] = process.Start;
            }
            if (outcome.Exit is { } exit) data["administrator_exit"] = exit;
            var result = outcome.Result;
            CertificateStoreElevationContract.ValidateResult(result, payload.CertificateId);
            if (result.ActionIssued is { } actionIssued) data["store_action_issued"] = actionIssued;
            data["postcondition_observed"] = result.PostconditionObserved;
            data["outcome_unknown"] = result.OutcomeUnknown;
            data["certificate_status"] = result.State;
            if (result.Code is not null) data["certificate_code"] = result.Code;
            if (result.NativeError is { } status) data["windows_status"] = status;
            if (result.State is "added" or "removed" or "unchanged" or "absent")
            {
                // A received authoritative report survives a later helper ACK
                // failure/nonzero exit. It never turns into a retryable action.
                data["certificate"] = result;
                receipt = new(true, jid, rid, "done", OperationNames.Public(request), Data: data);
            }
            else receipt = Failed(result.Code ?? "certificate_elevation_failed", result.OutcomeUnknown,
                result.ActionIssued == true);
        }
        catch (CertificateContractException failure)
        { receipt = CertificateReceiptAdapter.Invalid(request, jid, rid, failure.Field); }
        catch (Exception failure) when (failure is AgentOpsException or CertificateOperationException or
            OwnerCertificateRepositoryException or OperationCanceledException or System.Security.Cryptography.CryptographicException or
            IOException or UnauthorizedAccessException or ArgumentException)
        {
            var code = failure switch
            {
                AgentOpsException agent => agent.Code,
                CertificateOperationException certificate => certificate.Code,
                OperationCanceledException => "canceled",
                UnauthorizedAccessException => "access",
                _ => "certificate_elevation_failed"
            };
            receipt = Failed(code, issued, false);
        }
        return Finish(receipt, request, sequence, persist);

        void PersistIssued(CertificateElevationProcessIdentity process)
        {
            // Persist uncertainty BEFORE the capability can leave this process.
            // A restart retains its RID barrier and never recreates a UAC prompt.
            if (!persist) throw new AgentOpsException("precondition");
            data["administrator_pid"] = (long)process.Pid; data["administrator_start"] = process.Start;
            var pending = new Dictionary<string, object?>(data)
            { ["administrator_payload_issued"] = true, ["outcome_unknown"] = true };
            var running = new OperationReceipt(true, jid, rid, "running", OperationNames.Public(request), Data: pending);
            var durable = new DurableJob(PrivateDurableReceiptForStore(running, request), true, sequence, PrivateDurableRequest(request));
            _store.Update(durable);
            issued = true;
            _jobs[jid] = durable;
        }

        OperationReceipt Failed(string cause, bool unknown, bool actionIssued)
        {
            var canceled = cause is "canceled" or "certificate_elevation_canceled" or "certificate_elevation_declined";
            var code = unknown ? "recovery_required" : canceled ? "canceled" :
                cause is "auth" or "access" or "invalid" or "precondition" ? cause : "certificate_failed";
            data["administrator_payload_issued"] = issued;
            data["outcome_unknown"] = unknown;
            data["certificate_code"] = RegistryReceiptAdapter.IsSafeCode(cause) ? cause : "certificate_failed";
            return new(false, jid, rid, unknown ? "recovery_required" : actionIssued ? "attention" : canceled ? "canceled" : "failed",
                OperationNames.Public(request), code, data, Partial: unknown || actionIssued,
                Failure: CertificateReceiptAdapter.Evidence(code, actionIssued, unknown));
        }
    }
}
