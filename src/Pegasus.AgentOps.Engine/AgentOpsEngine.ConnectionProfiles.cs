using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private SmbOwnerBinding? _smbOwner;
    internal void BindSmbOwner(SmbOwnerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (_directOnly || _userHostAdmission is null || !binding.IsCurrentOwner() ||
            Interlocked.CompareExchange(ref _smbOwner, binding, null) is not null) throw new AgentOpsException("auth");
    }

    private static ConnectionProfileInvocation ConnectionInvocation(OperationRequest request) =>
        ConnectionProfileOperationContract.Parse(request with
        {
            Arg = request.Arg.Where(pair => pair.Key != OperationRequest.BodyOmittedKey)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });

    private async Task<OperationReceipt> ExecuteConnectionProfileReceiptAsync(OperationRequest request, string jid, string rid,
        long sequence, CancellationToken token, bool persist)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, jid, rid, "failed", OperationNames.Public(request), "platform_unsupported");
        var data = new Dictionary<string, object?> { ["seq"] = sequence, ["log_available"] = false };
        OperationReceipt receipt;
        var entered = false;
        bool? returnedActionIssued = null, returnedOutcomeUnknown = null;
        try
        {
            if (request.Body is null && request.Arg.ContainsKey(OperationRequest.BodyOmittedKey))
            {
                data["outcome_unknown"] = true;
                receipt = new(false, jid, rid, "recovery_required", OperationNames.Public(request), "recovery_required", data, Partial: true);
            }
            else
            {
                var invocation = ConnectionInvocation(request);
                data["requestIdentity"] = invocation.RequestIdentity;
                data["action"] = invocation.Action;
                if (_directOnly || _smbOwner?.IsCurrentOwner() != true) throw new AgentOpsException("auth");
                token.ThrowIfCancellationRequested();
                var profiles = _smbOwner.Profiles();
                if (invocation.Action == "list")
                {
                    var page = profiles.List(invocation.AfterId, invocation.Limit);
                    token.ThrowIfCancellationRequested();
                    data["profiles"] = page.Profiles.Select(ProfileProjection).ToArray();
                    data["nextId"] = page.NextId; data["state"] = page.State;
                    receipt = new(page.Ok, jid, rid, page.Ok ? "done" : "failed", OperationNames.Public(request), page.Error, data);
                }
                else if (invocation.Action is "read" or "save" or "remove")
                {
                    SmbProfileStoreResult outcome;
                    if (invocation.Action == "read")
                    { outcome = profiles.Read(invocation.Id!); token.ThrowIfCancellationRequested(); }
                    else
                    {
                        entered = true;
                        outcome = invocation.Action == "save"
                            ? profiles.Save(invocation.Profile!, invocation.Revision, token)
                            : profiles.Remove(invocation.Id!, invocation.Revision, token);
                    }
                    data["state"] = outcome.State; data["revision"] = outcome.Revision;
                    if (outcome.NativeError is { } native) data["windows_status"] = native;
                    if (outcome.Snapshot is { } snapshot) data["snapshot"] = ProfileProjection(snapshot);
                    if (invocation.IsMutation)
                    {
                        returnedActionIssued = outcome.ActionIssued;
                        returnedOutcomeUnknown = outcome.OutcomeUnknown;
                        data["action_issued"] = outcome.ActionIssued;
                        data["outcome_unknown"] = outcome.OutcomeUnknown;
                        data["postcondition_observed"] = outcome.Ok;
                    }
                    var error = outcome.OutcomeUnknown ? "recovery_required" : outcome.Error;
                    receipt = new(outcome.Ok, jid, rid, outcome.Ok ? "done" : outcome.OutcomeUnknown ? "recovery_required" : "failed",
                        OperationNames.Public(request), error, data, Partial: !outcome.Ok && (outcome.ActionIssued || outcome.OutcomeUnknown));
                }
                else
                {
                    var found = profiles.Read(invocation.Id!);
                    if (!found.Ok || found.Snapshot is not { } snapshot || snapshot.Revision != invocation.Revision ||
                        snapshot.ContentHash != invocation.ContentHash) throw new AgentOpsException("precondition");
                    token.ThrowIfCancellationRequested();
                    var connections = _smbOwner.Connections();
                    // Await the ORIGINAL synchronous worker, never WaitAsync-drop it
                    // on observer cancellation. It retains this engine's effect and
                    // scheduler ownership until the actual WNet call returns.
                    entered = true;
                    var outcome = await Task.Run(() =>
                    {
                        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                        return invocation.Action switch
                        {
                            "inspect" => connections.Inspect(snapshot.Profile, token),
                            "connect" => connections.Connect(snapshot.Profile, token),
                            _ => connections.Disconnect(snapshot.Profile, invocation.ExpectedRemoteShare!, token)
                        };
                    }, CancellationToken.None).ConfigureAwait(false);
                    returnedActionIssued = outcome.ActionIssued;
                    returnedOutcomeUnknown = outcome.OutcomeUnknown;
                    data["state"] = outcome.State; data["mapping_observed"] = outcome.MappingObserved;
                    data["remote_authentication_verified"] = false;
                    data["action_issued"] = outcome.ActionIssued; data["outcome_unknown"] = outcome.OutcomeUnknown;
                    data["postcondition_observed"] = outcome.Ok;
                    if (outcome.NativeError is { } native) data["windows_status"] = native;
                    var error = outcome.OutcomeUnknown ? "recovery_required" : outcome.Error;
                    receipt = new(outcome.Ok, jid, rid, outcome.Ok ? "done" : outcome.OutcomeUnknown ? "recovery_required" :
                        outcome.State == "cancelled" ? "canceled" : "failed", OperationNames.Public(request), error, data,
                        Partial: !outcome.Ok && (outcome.ActionIssued || outcome.OutcomeUnknown));
                }
                if (!_smbOwner.IsCurrentOwner())
                {
                    // Redact owner-visible payload without inventing an external
                    // effect after a provider's definite pre-entry refusal. A
                    // returned outcome is stronger evidence than worker entry.
                    var issued = invocation.IsMutation && returnedActionIssued == true;
                    var unknown = invocation.IsMutation && returnedOutcomeUnknown == true;
                    data = new Dictionary<string, object?> { ["seq"] = sequence, ["requestIdentity"] = invocation.RequestIdentity,
                        ["action_issued"] = issued, ["outcome_unknown"] = unknown, ["log_available"] = false };
                    receipt = new(false, jid, rid, unknown ? "recovery_required" : "failed",
                        OperationNames.Public(request), unknown ? "recovery_required" : "auth", data,
                        Partial: issued || unknown);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var unknown = !ConnectionProfileOperationContract.IsReadOnly(request.Arg.GetValueOrDefault("op")) &&
                (returnedOutcomeUnknown ?? entered);
            var code = unknown ? "recovery_required" : error is OperationCanceledException ? "canceled" :
                error is AgentOpsException known ? known.Code : error is ConnectionProfileContractException ? "invalid" : "connection_profile_unavailable";
            data = new Dictionary<string, object?> { ["seq"] = sequence, ["log_available"] = false };
            if (returnedActionIssued.HasValue) data["action_issued"] = returnedActionIssued.Value;
            if (unknown || returnedOutcomeUnknown.HasValue) data["outcome_unknown"] = unknown;
            receipt = new(false, jid, rid, unknown ? "recovery_required" : code == "canceled" ? "canceled" : "failed",
                OperationNames.Public(request), code, data, Partial: unknown);
        }
        return Finish(receipt, request, sequence, persist);
    }

    private static IReadOnlyDictionary<string, object?> ProfileProjection(SmbConnectionProfileSnapshot snapshot) =>
        new Dictionary<string, object?> { ["profile"] = snapshot.Profile, ["revision"] = snapshot.Revision, ["contentHash"] = snapshot.ContentHash };

    private static bool ConnectionProfileReplayMatches(OperationRequest request, OperationReceipt receipt)
    {
        try
        {
            var invocation = ConnectionInvocation(request);
            return receipt.Op == ConnectionProfileOperationContract.PublicName(invocation.Action) &&
                ScalarString(receipt.Data?.GetValueOrDefault("requestIdentity")) == invocation.RequestIdentity;
        }
        catch (ConnectionProfileContractException) { return false; }
    }

    private OperationReceipt ConnectionProfileDurableReceipt(OperationReceipt receipt, OperationRequest? request)
    {
        if (request is not null && request.Op != "connection_profile" || request is null &&
            !receipt.Op.StartsWith("connection_profile_", StringComparison.Ordinal) || IsHistoryPruned(receipt)) return receipt;
        var data = new Dictionary<string, object?>();
        foreach (var name in new[] { "seq", "action_issued", "outcome_unknown", "postcondition_observed", "mapping_observed", "windows_status" })
            if (RegistryMetadataScalar(receipt.Data?.GetValueOrDefault(name)) is { } scalar) data[name] = scalar;
        var identity = ScalarString(receipt.Data?.GetValueOrDefault("requestIdentity"));
        if (!ValidRegistryIdentity(identity) && request?.Body is not null)
        { try { identity = ConnectionInvocation(request).RequestIdentity; } catch (ConnectionProfileContractException) { } }
        if (!ValidRegistryIdentity(identity)) identity = ScalarString(_store.FindReceipt(receipt.Jid)?.Data?.GetValueOrDefault("requestIdentity"));
        if (ValidRegistryIdentity(identity)) data["requestIdentity"] = identity;
        if (receipt.State == "recovery_required") data["outcome_unknown"] = true;
        data["log_available"] = false;
        // No profile, endpoint, remote account, credential reference, body or
        // arbitrary native diagnostic belongs in persisted execution history.
        return receipt with { Data = data, Detail = null, Failure = RegistryReceiptAdapter.DurableFailure(receipt) };
    }
}
