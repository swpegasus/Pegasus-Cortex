using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Windows.Registry;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private RegistryOwnerBinding? _registryOwner;

    // The normal verified owner-host composition calls this once before it
    // exposes its pipes. Public/default/in-process/System engines stay denied.
    internal void BindRegistryOwner(RegistryOwnerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_directOnly || _userHostAdmission is null || !binding.IsCurrentOwner() ||
            Interlocked.CompareExchange(ref _registryOwner, binding, null) is not null)
            throw new AgentOpsException("auth");
    }

    // The central receipt path invokes this inside its existing maintenance and
    // user-host admission leases. No new queue, retry, transport or OS identity.
    private OperationReceipt ExecuteRegistryReceipt(OperationRequest request, string jid, string rid, long sequence,
        CancellationToken cancellationToken, bool persist)
    {
        OperationReceipt receipt;
        if (request.Body is null && request.Arg.ContainsKey(OperationRequest.BodyOmittedKey))
            receipt = RegistryReceiptAdapter.LostBody(request, jid, rid);
        else
        {
            try { receipt = RegistryReceiptAdapter.Execute(RegistryOperationContract.Parse(request), _registryOwner, jid, rid, cancellationToken); }
            catch (RegistryContractException failure) { receipt = RegistryReceiptAdapter.Invalid(request, jid, rid, failure.Field); }
        }
        receipt = receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>()) { ["seq"] = sequence } };
        return Finish(receipt, request, sequence, persist);
    }

    internal static OperationReceipt RegistryInitialReceipt(OperationReceipt receipt, OperationRequest request)
    {
        if (request.Op != "registry") return receipt;
        var invocation = RegistryOperationContract.Parse(request);
        return receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>())
            { ["requestIdentity"] = invocation.RequestIdentity } };
    }

    internal static bool RegistryReplayMatches(OperationRequest request, OperationReceipt receipt)
    {
        if (request.Op != "registry") return false;
        try
        {
            var invocation = RegistryOperationContract.Parse(request);
            if (receipt.Op != RegistryOperationContract.PublicName(invocation.Action)) return false;
            var identity = ScalarString(receipt.Data?.GetValueOrDefault("requestIdentity"));
            // Compact tombstones deliberately retain requestIdentity. A match
            // returns metadata only through the existing replay path; it never
            // rebuilds a result, reloads a body, or executes the native adapter.
            if (IsHistoryPruned(receipt)) return identity == invocation.RequestIdentity;
            return identity == invocation.RequestIdentity;
        }
        catch (RegistryContractException) { return false; }
    }

    // Finish and recovery call this only for the receipt being persisted. The
    // original live response remains separate. Exact JID/RID lookup recovers a
    // missing metadata hash, never registry content or a lost request body.
    private OperationReceipt RegistryDurableReceiptForStore(OperationReceipt receipt, OperationRequest? request)
    {
        request = RegistryMetadataRequest(receipt, request);
        if (request is null) return receipt;
        if (request.Op != "registry" || request.Body is not null ||
            ValidRegistryIdentity(ScalarString(receipt.Data?.GetValueOrDefault("requestIdentity"))))
            return RegistryDurableReceipt(receipt, request);
        var previous = _store.Find(receipt.Jid)?.Receipt;
        return RegistryDurableReceipt(receipt, request, previous);
    }

    internal static OperationReceipt RegistryDurableReceipt(OperationReceipt receipt, OperationRequest? request, OperationReceipt? previous = null)
    {
        request = RegistryMetadataRequest(receipt, request);
        if (request is null) return receipt;
        if (request.Op != "registry" || IsHistoryPruned(receipt)) return receipt;
        var previousMatches = previous is not null && previous.Jid == receipt.Jid && previous.Rid == receipt.Rid &&
            previous.Op == receipt.Op && request.Arg.GetValueOrDefault("op") is { } action &&
            RegistryOperationContract.IsKnownAction(action) && previous.Op == RegistryOperationContract.PublicName(action);
        if (request.Body is null && previousMatches && IsHistoryPruned(previous!)) return previous!;
        var data = new Dictionary<string, object?>();
        foreach (var key in new[] { "seq", "registry_write_invoked", "postcondition_observed", "atomic_compare_and_swap", "native_error" })
            if (RegistryMetadataScalar(receipt.Data?.GetValueOrDefault(key)) is { } scalar)
                data[key] = key == "atomic_compare_and_swap" ? false : scalar;
        foreach (var key in new[] { "registry_status", "registry_code" })
            if (ScalarString(receipt.Data?.GetValueOrDefault(key)) is { } value && RegistryReceiptAdapter.IsSafeCode(value)) data[key] = value;
        var identity = ScalarString(receipt.Data?.GetValueOrDefault("requestIdentity"));
        if (!ValidRegistryIdentity(identity) && request.Body is null && previousMatches)
            identity = ScalarString(previous!.Data?.GetValueOrDefault("requestIdentity"));
        if (!ValidRegistryIdentity(identity) && request.Body is not null)
        {
            try { identity = RegistryOperationContract.Parse(request).RequestIdentity; }
            catch (RegistryContractException) { identity = null; }
        }
        if (ValidRegistryIdentity(identity)) data["requestIdentity"] = identity;
        data["log_available"] = false;
        // No registry target/name/value/generation/cursor/body, output stream or
        // diagnostic payload belongs in durable operation history.
        return receipt with { Data = data, Detail = null, Failure = RegistryReceiptAdapter.DurableFailure(receipt) };
    }

    private static bool ValidRegistryIdentity(string? identity) => identity is { Length: 64 } &&
        identity.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static OperationRequest? RegistryMetadataRequest(OperationReceipt receipt, OperationRequest? request)
    {
        if (request is not null) return request;
        const string prefix = "registry_";
        if (!receipt.Op.StartsWith(prefix, StringComparison.Ordinal) || !RegistryOperationContract.IsKnownAction(receipt.Op[prefix.Length..])) return null;
        // Recovery may have discarded the entire request. This fixed selector
        // skeleton is for metadata projection only: no target, guard or body is
        // recovered, and it is never sent to the execution adapter.
        return new("registry", new Dictionary<string, string>
            { ["op"] = receipt.Op[prefix.Length..], [OperationRequest.BodyOmittedKey] = "1" }, receipt.Rid);
    }
    private static string? ScalarString(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
        _ => null
    };
    private static object? RegistryMetadataScalar(object? value) => value switch
    {
        bool or int or long => value,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt64(out var number) => number,
        _ => null
    };
}

// Trusted composition only: the SID is the installation's verified owner, and
// the effective identity is sampled independently of every request. This is not
// an identity/grant store and has no caller-supplied administrator flag.
internal sealed class RegistryOwnerBinding
{
    private readonly OwnerHostIdentity _identity;
    internal RegistryOperations Operations { get; }

    internal RegistryOwnerBinding(string ownerSid, Func<LocalPeer> current, Func<IRegistryAdmission, RegistryOperations> compose)
    {
        _identity = new OwnerHostIdentity(ownerSid, current);
        ArgumentNullException.ThrowIfNull(compose);
        Operations = compose(new OwnerAdmission(this)) ?? throw new ArgumentException("registry_composition_invalid");
    }

    [SupportedOSPlatform("windows")]
    internal static RegistryOwnerBinding ForOwner(string ownerSid) => new(ownerSid, LocalIdentity.Current, WindowsRegistryOperations.Create);

    internal bool IsCurrentOwner() => _identity.IsCurrentOwner();

    private sealed class OwnerAdmission(RegistryOwnerBinding binding) : IRegistryAdmission
    {
        public bool IsAllowed(RegistryAccessRequest request) => request.ContractVersion == RegistryLimits.ContractVersion &&
            Enum.IsDefined(request.Operation) && binding.IsCurrentOwner();
    }
}

internal static class RegistryReceiptAdapter
{
    private sealed record Outcome(RegistryStatus Status, string Code, object? Value, int? NativeError, RegistryMutationObservation? Mutation);

    internal static OperationReceipt Execute(RegistryInvocation invocation, RegistryOwnerBinding? owner, string jid, string rid, CancellationToken token)
    {
        var op = RegistryOperationContract.PublicName(invocation.Action);
        if (owner is null || !owner.IsCurrentOwner())
            return new(false, jid, rid, "failed", op, "auth", Failure: Evidence("auth", false, false, null));
        var operations = owner.Operations;
        var result = invocation.Operation switch
        {
            RegistryOperation.InspectKey => From(operations.InspectKey(invocation.Key, token)),
            RegistryOperation.Browse => From(operations.Browse(invocation.Browse!, token)),
            RegistryOperation.ReadValue => From(operations.ReadValue(invocation.ValueIdentity!, token)),
            RegistryOperation.CreateKey => From(operations.CreateKey(invocation.Create!, token)),
            RegistryOperation.SetValue => From(operations.SetValue(invocation.Set!, token)),
            RegistryOperation.DeleteValue => From(operations.DeleteValue(invocation.DeleteValue!, token)),
            RegistryOperation.DeleteEmptyKey => From(operations.DeleteEmptyKey(invocation.DeleteKey!, token)),
            _ => throw new InvalidOperationException("registry_operation_invalid")
        };
        var data = new Dictionary<string, object?>
        {
            ["registry_status"] = Status(result.Status), ["registry_code"] = SafeCode(result.Code),
            ["requestIdentity"] = invocation.RequestIdentity, ["resource_identity"] = invocation.ResourceIdentity, ["log_available"] = false
        };
        if (result.NativeError is { } nativeError) data["native_error"] = nativeError;
        if (result.Value is not null) data["registry"] = result.Value;
        if (invocation.IsMutation)
        {
            data["registry_write_invoked"] = result.Mutation?.WriteInvoked ?? false;
            data["postcondition_observed"] = result.Mutation?.PostconditionObserved ?? false;
            data["atomic_compare_and_swap"] = false;
        }
        if (result.Status == RegistryStatus.Ok) return new(true, jid, rid, "done", op, Data: data);
        var code = result.Status switch
        {
            RegistryStatus.InvalidRequest => "invalid", RegistryStatus.Denied => "access", RegistryStatus.NotFound => "not_found",
            RegistryStatus.Stale or RegistryStatus.PreconditionFailed => "precondition", RegistryStatus.LimitExceeded => "registry_limit",
            RegistryStatus.Unsupported => "registry_unsupported", RegistryStatus.Cancelled => "canceled",
            RegistryStatus.Uncertain => "recovery_required", _ => "registry_failed"
        };
        var uncertain = result.Status == RegistryStatus.Uncertain;
        return new(false, jid, rid, uncertain ? "recovery_required" : result.Status == RegistryStatus.Cancelled ? "canceled" : "failed", op, code,
            data, Partial: uncertain, Failure: Evidence(code, result.Mutation?.WriteInvoked == true || !invocation.IsMutation, uncertain, SafeCode(result.Code)));
    }

    internal static OperationReceipt Invalid(OperationRequest request, string jid, string rid, string field) =>
        new(false, jid, rid, "failed", PublicName(request), "invalid", Failure: Evidence("invalid", false, false, null, field));

    internal static OperationReceipt LostBody(OperationRequest request, string jid, string rid) =>
        new(false, jid, rid, "recovery_required", PublicName(request), "recovery_required", Partial: true,
            Failure: Evidence("recovery_required", true, true, "registry_body_unavailable"));

    internal static OperationFailureEvidence? DurableFailure(OperationReceipt receipt)
    {
        if (receipt.Ok) return null;
        var uncertain = receipt.Partial || receipt.State == "recovery_required";
        var execution = receipt.Failure?.Execution.Value;
        if (execution is not (OperationExecutionState.NotDispatched or OperationExecutionState.Started or OperationExecutionState.Partial or OperationExecutionState.Unknown))
            execution = OperationExecutionState.Unknown;
        return Evidence(SafeCode(receipt.Err ?? "registry_failed"), false, uncertain, null) with
        { Execution = new(execution, "registry-adapter"),
            DiagnosticsOmitted = receipt.Failure?.Diagnostics is { Count: > 0 } || receipt.Failure?.DiagnosticsOmitted == true };
    }

    private static string PublicName(OperationRequest request) => request.Arg.GetValueOrDefault("op") is { } action &&
        RegistryOperationContract.IsKnownAction(action) ? RegistryOperationContract.PublicName(action) : "registry";
    private static Outcome From<T>(RegistryResult<T> result) => new(result.Status, result.Code, result.Value, result.NativeError,
        result.Value is RegistryMutationObservation mutation ? mutation : null);

    private static OperationFailureEvidence Evidence(string code, bool invoked, bool uncertain, string? diagnostic, string? field = null) => new(
        code switch
        {
            "auth" => "Registry work requires the verified ordinary signed-in owner host.",
            "access" => "Registry admission or Windows access was denied.",
            "invalid" => "The closed registry request failed validation before registry access.",
            "precondition" => "The registry identity, generation, continuation or preimage changed.",
            "not_found" => "The exact registry key was not found.",
            "registry_limit" => "The exact registry data exceeds this operation's fixed bounds.",
            "registry_unsupported" => "The exact registry data or name is outside the supported contract.",
            "canceled" => "Registry work was canceled before a mutation was invoked.",
            "recovery_required" => "The original registry attempt has an uncertain outcome or its private request body was lost.",
            _ => "The bounded registry operation failed with the reported static cause."
        },
        new(uncertain ? OperationExecutionState.Unknown : invoked ? OperationExecutionState.Started : OperationExecutionState.NotDispatched,
            "registry-adapter"),
        new(uncertain ? OperationRetryDisposition.NotSupported : OperationRetryDisposition.AfterCorrection, "registry-adapter"),
        Field: field is null ? null : new(field),
        Diagnostics: diagnostic is null ? null : [new("The registry boundary returned a static cause code.", Code: diagnostic)],
        Next: uncertain ? "Inspect and reconcile the original target and receipt. Never replay this job; only a deliberately new request may use a fresh preimage and RID."
            : "Correct the typed request or refresh the exact target identity and preimage before a deliberately new request.",
        DetailAvailability: "none", LogAvailability: "not_retained");

    internal static bool IsSafeCode(string code) => code.Length is > 0 and <= 96 &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static string SafeCode(string code) => IsSafeCode(code) ? code : "registry_native_failure";
    private static string Status(RegistryStatus status) => status switch
    {
        RegistryStatus.InvalidRequest => "invalid_request", RegistryStatus.PreconditionFailed => "precondition_failed",
        RegistryStatus.LimitExceeded => "limit_exceeded", _ => status.ToString().ToLowerInvariant()
    };
}
