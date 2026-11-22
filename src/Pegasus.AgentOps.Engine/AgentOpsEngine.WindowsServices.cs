using System.Globalization;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private readonly WindowsServiceOperations _windowsServices;

    private static OperationRequest WindowsServicePublicShape(OperationRequest request) => request with
    {
        Root = null,
        Arg = request.Arg.Where(pair => pair.Key is not (AuthenticatedFrozenKey or "__execution_context_system" or OperationRequest.BodyOmittedKey))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
    };

    private static OperationReceipt WindowsServiceInitialReceipt(OperationReceipt receipt, OperationRequest request)
    {
        if (request.Op == "windows_task") return WindowsTaskInitialReceipt(receipt, request);
        if (request.Op != "windows_service") return receipt;
        var invocation = WindowsServiceOperationContract.Parse(WindowsServicePublicShape(request));
        return receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>())
            { ["requestIdentity"] = invocation.RequestIdentity } };
    }
    private static OperationRequest WindowsServiceDurableRequest(OperationRequest request)
    {
        if (request.Op == "windows_task") return WindowsTaskDurableRequest(request);
        if (request.Op != "windows_service") return PrivateDurableRequest(request);
        var arguments = new Dictionary<string, string> { ["op"] = request.Arg["op"], [OperationRequest.BodyOmittedKey] = "1" };
        // An elevated read owns a helper. Recovery must retain that route and
        // never reinterpret its omitted payload as an ordinary ephemeral read.
        if (request.Arg.GetValueOrDefault("elevate") == "uac") arguments["elevate"] = "uac";
        return new("windows_service", arguments, request.Rid);
    }

    private static bool WindowsServiceRequiresReplayMatch(OperationRequest request, OperationReceipt previous) =>
        request.Op is "windows_service" or "windows_task" || previous.Op.StartsWith("windows_service_", StringComparison.Ordinal) || previous.Op.StartsWith("windows_task_", StringComparison.Ordinal);
    private static bool WindowsServiceReplayMatches(OperationRequest request, OperationReceipt previous)
    {
        if (request.Op == "windows_task" || previous.Op.StartsWith("windows_task_", StringComparison.Ordinal)) return WindowsTaskReplayMatches(request, previous);
        if (request.Op != "windows_service") return false;
        try
        {
            var invocation = WindowsServiceOperationContract.Parse(WindowsServicePublicShape(request));
            return previous.Op == WindowsServiceOperationContract.PublicName(invocation.Action) &&
                Text(previous.Data?.GetValueOrDefault("requestIdentity")) == invocation.RequestIdentity;
        }
        catch (AgentOpsException) { return false; }
    }

    private OperationReceipt WindowsServiceReceiptForStore(OperationReceipt receipt, OperationRequest? request) =>
        WindowsServiceDurableReceipt(PrivateDurableReceiptForStore(receipt, request), request);

    private OperationReceipt WindowsServiceDurableReceipt(OperationReceipt receipt, OperationRequest? request)
    {
        if (request?.Op == "windows_task" || receipt.Op.StartsWith("windows_task_", StringComparison.Ordinal)) return WindowsTaskDurableReceipt(receipt);
        if (!(request?.Op == "windows_service" || receipt.Op.StartsWith("windows_service_", StringComparison.Ordinal)) || IsHistoryPruned(receipt)) return receipt;
        if (receipt.State == "recovery_required") receipt = receipt with { Partial = true };
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in new[] { "seq", "action_issued", "outcome_unknown", "postcondition_observed", "pid", "previous_pid", "windows_status", "helper_payload_issued" })
        {
            var value = receipt.Data?.GetValueOrDefault(key);
            if (value is JsonElement element)
            {
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) value = element.GetBoolean();
                else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number)) value = number;
            }
            if (value is bool or int or uint or long) result[key] = value;
        }
        foreach (var key in new[] { "service", "action", "service_state", "previous_state", "startup", "previous_startup",
            "configSha", "previous_configSha", "start", "previous_start", "phase", "service_code", "requestIdentity" })
            if (Text(receipt.Data?.GetValueOrDefault(key)) is { Length: > 0 and <= 256 } value && !value.Any(char.IsControl))
                result[key] = value;
        if (!WindowsServiceOperationContract.IsDigest(Text(result.GetValueOrDefault("requestIdentity"))) &&
            _store.FindReceipt(receipt.Jid) is { } old && old.Rid == receipt.Rid && old.Op == receipt.Op &&
            WindowsServiceOperationContract.IsDigest(Text(old.Data?.GetValueOrDefault("requestIdentity"))))
            result["requestIdentity"] = Text(old.Data?.GetValueOrDefault("requestIdentity"));
        if (receipt.State == "recovery_required") result["outcome_unknown"] = true;
        PreserveHelperGeneration(result, receipt, receipt.State == "recovery_required" ? _store.FindReceipt(receipt.Jid) : null,
            "helper_pid", "helper_start");
        result["log_available"] = false;
        return receipt with { Data = result, Detail = null };
    }

    private async Task<OperationReceipt> ExecuteWindowsServiceReceiptAsync(OperationRequest request, string jid, string rid,
        long sequence, CancellationToken token, bool persist)
    {
        var data = new Dictionary<string, object?> { ["seq"] = sequence, ["log_available"] = false };
        OperationReceipt receipt;
        try
        {
            if (request.Body is null && request.Arg.ContainsKey(OperationRequest.BodyOmittedKey))
            {
                data["outcome_unknown"] = true;
                receipt = new(false, jid, rid, "recovery_required", OperationNames.Public(request), "recovery_required", data, Partial: true);
            }
            else
            {
                var invocation = WindowsServiceOperationContract.Parse(WindowsServicePublicShape(request));
                if (invocation.RequiresOwnerElevation)
                    return await ExecuteElevatedWindowsServiceReceiptAsync(request, invocation, jid, rid, sequence, token, persist).ConfigureAwait(false);
                if (invocation.IsMutation && request.Arg.GetValueOrDefault("__execution_context_system") != "1")
                    throw new AgentOpsException("auth");
                data["requestIdentity"] = invocation.RequestIdentity;
                if (invocation.Action == "list")
                {
                    var page = _windowsServices.List(invocation, token);
                    data["services"] = page.Services; data["next"] = page.Next; data["has_more"] = page.HasMore;
                    data["bound_reached"] = page.BoundReached; data["visibility"] = page.Visibility;
                    receipt = new(true, jid, rid, "done", OperationNames.Public(request), Data: data);
                }
                else
                {
                    var outcome = await _windowsServices.ExecuteAsync(invocation, token).ConfigureAwait(false);
                    receipt = WindowsServiceOutcomeReceipt(request, invocation, jid, rid, outcome, data);
                }
            }
        }
        catch (Exception failure) when (failure is AgentOpsException or System.ComponentModel.Win32Exception or IOException or
            UnauthorizedAccessException or OperationCanceledException or ArgumentException)
        {
            var code = failure is AgentOpsException known ? known.Code : failure is OperationCanceledException ? "canceled" :
                failure is UnauthorizedAccessException || failure is System.ComponentModel.Win32Exception { NativeErrorCode: 5 } ? "access" : "service_failed";
            receipt = new(false, jid, rid, code == "canceled" ? "canceled" : "failed", OperationNames.Public(request), code, data,
                Failure: FailureEvidence.Generic(code, false, "windows-service-admission"));
        }
        return Finish(receipt, request, sequence, persist);
    }

    private static OperationReceipt WindowsServiceOutcomeReceipt(OperationRequest request, WindowsServiceInvocation invocation, string jid, string rid,
        WindowsServiceOutcome outcome, Dictionary<string, object?> data)
    {
        data["action"] = invocation.Action; data["service"] = invocation.Service;
        data["action_issued"] = outcome.ActionIssued; data["outcome_unknown"] = outcome.OutcomeUnknown;
        data["postcondition_observed"] = outcome.Ok; data["phase"] = outcome.Phase;
        if (outcome.Error is { } code) data["service_code"] = code;
        if (outcome.NativeError is { } native) data["windows_status"] = native;
        Add(outcome.Current, false); Add(outcome.Previous, true);
        var error = outcome.OutcomeUnknown ? "recovery_required" : outcome.Error;
        return new(outcome.Ok, jid, rid, outcome.Ok ? "done" : outcome.OutcomeUnknown ? "recovery_required" :
            error == "canceled" ? "canceled" : "failed", OperationNames.Public(request), error, data,
            Partial: !outcome.Ok && (outcome.ActionIssued || outcome.OutcomeUnknown),
            Failure: outcome.Ok ? null : FailureEvidence.Generic(error ?? "service_failed", outcome.ActionIssued || outcome.OutcomeUnknown, "windows-service"));

        void Add(WindowsServiceDetails? value, bool previous)
        {
            if (value is null) return;
            var prefix = previous ? "previous_" : "";
            data[previous ? "previous_state" : "service_state"] = value.State;
            data[prefix + "startup"] = value.Startup;
            data[prefix + "configSha"] = value.ConfigSha;
            if (value.Pid is { } pid) data[prefix + "pid"] = pid;
            if (value.Start is { } start) data[prefix + "start"] = start;
            if (!previous)
            {
                data["display_name"] = value.DisplayName; data["service_type"] = value.ServiceType;
                data["delayed_auto_start"] = value.DelayedAutoStart; data["controls_accepted"] = value.ControlsAccepted;
                data["process_identity_available"] = value.Pid is null || value.Start is not null;
            }
        }
    }
    // A shape check only: the broker supplies root/owner/app authority and the
    // trusted entry supplies System context. No public selector confers that right.
    internal static void RequireSystemServiceJobObservation(OperationRequest request)
    {
        if (request.Op != "job" || request.Arg.GetValueOrDefault("op") is not ("stat" or "wait") ||
            request.Root is not null || request.Body is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } ||
            request.Arg.Keys.Any(key => key is not ("op" or "jid" or "rid")) ||
            !request.Arg.TryGetValue("jid", out var jid) || jid.Length != 32 ||
            !jid.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            request.Arg.TryGetValue("rid", out var supplied) && supplied != request.Rid ||
            request.Rid is { } rid && (rid.Length is < 1 or > 128 || string.IsNullOrWhiteSpace(rid) || rid.Any(char.IsControl)))
            throw new AgentOpsException("invalid");
    }

    private static bool IsSystemServiceMutationReceipt(OperationReceipt? receipt)
    {
        const string taskPrefix = "windows_task_";
        if (receipt?.Op.StartsWith(taskPrefix, StringComparison.Ordinal) == true)
        {
            var taskAction = receipt.Op[taskPrefix.Length..];
            return WindowsTaskOperationContract.IsSystemAction(taskAction) && !WindowsTaskOperationContract.IsReadOnly(taskAction);
        }
        const string prefix = "windows_service_";
        if (receipt is null || !receipt.Op.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var action = receipt.Op[prefix.Length..];
        return WindowsServiceOperationContract.IsKnownAction(action) && !WindowsServiceOperationContract.IsReadOnly(action);
    }

    private async Task<OperationReceipt> ObserveSystemServiceJobAsync(OperationRequest trusted, CancellationToken token)
    {
        if (trusted.Root is not null || trusted.Arg.GetValueOrDefault("__execution_context_system") != "1") throw new AgentOpsException("auth");
        var request = WindowsServicePublicShape(trusted);
        RequireSystemServiceJobObservation(request);
        var jid = request.Arg["jid"];
        var action = request.Arg["op"];
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        while (true)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            // Receipt-only lookup: no request/body hydration, queue claim,
            // recovery pumping or cross-store fallback occurs during observation.
            var current = _store.FindReceipt(jid);
            if (current?.Jid != jid || !IsSystemServiceMutationReceipt(current)) return Missing();
            var safe = WindowsServiceDurableReceipt(current!, null);
            if (action == "stat")
                return new(true, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
                    "done", "job_stat", Data: new Dictionary<string, object?> { ["rec"] = safe, ["log_available"] = false });
            if (current!.State is not ("queued" or "running")) return safe;
            if (_completions.TryGetValue(jid, out var completion))
            {
                // Cancel only this observer. The original job keeps its own
                // execution token, RID and effect/uncertainty ownership.
                var completed = await completion.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
                return completed.Jid == jid && IsSystemServiceMutationReceipt(completed)
                    ? WindowsServiceDurableReceipt(completed, null) : Missing();
            }
            // A queued row may not yet have a live completion. Observe its
            // receipt without scheduling it or allocating a retained waiter.
            await Task.Delay(50, lifetime.Token).ConfigureAwait(false);
        }

        OperationReceipt Missing() => new(false, Guid.NewGuid().ToString("N"),
            request.Rid ?? Guid.NewGuid().ToString("N"), "failed", OperationNames.Public(request), "not_found");
    }

    private static string? Text(object? value) => value switch
    { string text => text, JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(), _ => null };
}
