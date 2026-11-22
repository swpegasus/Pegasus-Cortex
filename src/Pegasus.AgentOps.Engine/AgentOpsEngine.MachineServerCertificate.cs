using System.Security.Cryptography;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    // This is an ordinary-owner operation with one fixed UAC helper, never a
    // SYSTEM broker operation. Only public CSR/certificate material crosses it.
    private async Task<OperationReceipt> ExecuteMachineServerCertificateAsync(OperationRequest request,
        string jid, string rid, long sequence, CancellationToken token, bool persist)
    {
        if (request.Body is null && request.Arg.ContainsKey(OperationRequest.BodyOmittedKey))
            return Finish(CertificateReceiptAdapter.LostBody(request, jid, rid), request, sequence, persist);

        var issued = false;
        var published = false;
        var publicationUnknown = false;
        var data = new Dictionary<string, object?> { ["seq"] = sequence, ["log_available"] = false };
        OperationReceipt receipt;
        try
        {
            var invocation = CertificateTrustedInvocation(request);
            if (invocation.Action != "issue_machine_server" || invocation.Issuer is not { } issuer)
                throw new AgentOpsException("invalid");
            var owner = _certificateOwner;
            if (owner?.IsCurrentOwner() != true || _directOnly) throw new AgentOpsException("auth");
            var elevation = _certificateElevation ?? throw new AgentOpsException("certificate_elevation_unavailable");
            token.ThrowIfCancellationRequested();
            var repository = owner.Repository();
            using var authority = repository.Resolve(new(issuer.Reference, issuer.CertificateId));
            var now = owner.Clock.GetUtcNow();
            var start = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero).AddMinutes(-1);
            if (start < new DateTimeOffset(authority.NotBefore.ToUniversalTime()))
                start = new DateTimeOffset(authority.NotBefore.ToUniversalTime());
            var specification = new CertificateSpec(invocation.Subject!, start, start.AddDays(invocation.Days!.Value), invocation.KeyBits!.Value);
            owner.Operations.ValidateLeafIssuer(specification, authority);
            data["requestIdentity"] = invocation.RequestIdentity;
            data["reference"] = invocation.Reference;
            data["issuerReference"] = issuer.Reference;
            data["issuerCertificateId"] = issuer.CertificateId;
            data["store_scope"] = "local_machine";
            data["store_kind"] = "personal";
            var names = invocation.ServerNames!;
            var payload = new MachineServerCertificateRequest(1, Guid.ParseExact(jid, "N"), specification,
                names.DnsNames.ToArray(), names.IpAddresses.ToArray(), authority.RawData, issuer.CertificateId,
                invocation.RequestIdentity, jid, rid);
            var outcome = await elevation.BeginMachineServerAsync(payload, SignAndPublishAsync, PersistIssued, token).ConfigureAwait(false);
            issued = outcome.PayloadIssued;
            if (outcome.Process is { } process)
            {
                data["administrator_pid"] = (long)process.Pid;
                data["administrator_start"] = process.Start;
            }
            if (outcome.Exit is { } exit) data["administrator_exit"] = exit;
            var result = outcome.Result;
            data["administrator_payload_issued"] = issued;
            data["published"] = published;
            data["certificate_status"] = result.State;
            if (result.NativeError is { } native) data["windows_status"] = native;
            if (result.Progress is { } progress)
            {
                data["key_creation_issued"] = progress.KeyCreationIssued;
                data["key_finalized"] = progress.KeyFinalized;
                data["key_security_observed"] = progress.SecurityObserved;
                data["store_action_issued"] = progress.StoreActionIssued;
                data["cleanup_issued"] = progress.CleanupIssued;
                data["cleanup_observed"] = progress.CleanupObserved;
                data["postcondition_observed"] = progress.PostconditionObserved;
                if (result.State == "bound" && progress.PostconditionObserved && !progress.OutcomeUnknown &&
                    data.GetValueOrDefault("certificateId") is string expected && progress.CertificateId == expected)
                {
                    data["outcome_unknown"] = false;
                    data["binding"] = new { location = "LocalMachine", store = "My", certificateId = expected };
                    receipt = new(true, jid, rid, "done", OperationNames.Public(request), Data: data);
                }
                else
                    receipt = Failed(result.Code ?? "machine_server_failed", publicationUnknown || progress.OutcomeUnknown || result.State == "uncertain",
                        published || progress.StoreActionIssued || progress.KeyCreationIssued && !progress.CleanupObserved);
            }
            else
                // No helper report is not evidence that no native action ran.
                receipt = Failed(result.Code ?? "machine_server_report_unavailable", issued || publicationUnknown || result.State == "uncertain", published);

            // The coordinator joins any accepted signing callback before returning
            // its outcome, so the held CA cannot be disposed under that callback.
            Task<MachineServerCertificateSignedLeaf> SignAndPublishAsync(ReadOnlyMemory<byte> csr, CancellationToken signingToken)
            {
                signingToken.ThrowIfCancellationRequested();
                if (!owner.IsCurrentOwner()) throw new AgentOpsException("auth");
                using var leaf = owner.Operations.IssueServerFromSigningRequest(specification, authority, names, csr.Span);
                try
                {
                    var entry = repository.Publish(invocation.Reference!, leaf, invocation.RequestIdentity, signingToken);
                    published = true;
                    data["certificateId"] = entry.CertificateId;
                    return Task.FromResult(new MachineServerCertificateSignedLeaf(1, entry.PublicDer, entry.CertificateId));
                }
                catch (OwnerCertificateRepositoryException failure)
                {
                    published |= failure.Published;
                    publicationUnknown |= failure.OutcomeUnknown;
                    throw;
                }
            }
        }
        catch (CertificateContractException failure)
        { receipt = CertificateReceiptAdapter.Invalid(request, jid, rid, failure.Field); }
        catch (Exception failure) when (failure is AgentOpsException or CertificateOperationException or
            OwnerCertificateRepositoryException or OperationCanceledException or CryptographicException or
            IOException or UnauthorizedAccessException or ArgumentException)
        {
            var code = failure switch
            {
                AgentOpsException agent => agent.Code,
                CertificateOperationException certificate => certificate.Code,
                OwnerCertificateRepositoryException repository => repository.Code,
                OperationCanceledException => "canceled",
                UnauthorizedAccessException => "access",
                _ => "machine_server_failed"
            };
            receipt = Failed(code, issued || publicationUnknown, published);
        }
        return Finish(receipt, request, sequence, persist);

        void PersistIssued(CertificateElevationProcessIdentity process)
        {
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

        OperationReceipt Failed(string cause, bool unknown, bool effects)
        {
            var canceled = cause is "canceled" or "certificate_elevation_canceled" or "certificate_elevation_declined";
            var code = unknown ? "recovery_required" : canceled ? "canceled" :
                cause is "auth" or "access" or "invalid" or "precondition" ? cause : "certificate_failed";
            data["administrator_payload_issued"] = issued;
            data["published"] = published;
            data["outcome_unknown"] = unknown;
            data["certificate_code"] = RegistryReceiptAdapter.IsSafeCode(cause) ? cause : "machine_server_failed";
            return new(false, jid, rid, unknown ? "recovery_required" : effects ? "attention" : canceled ? "canceled" : "failed",
                OperationNames.Public(request), code, data, Partial: unknown || effects,
                Failure: CertificateReceiptAdapter.Evidence(code, effects, unknown));
        }
    }
}
