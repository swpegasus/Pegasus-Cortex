using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private WindowsTaskOperations? _windowsTasks;
    private OwnerScheduledTaskElevation? _scheduledTaskElevation;

    internal void BindScheduledTaskElevation(OwnerScheduledTaskElevation elevation)
    {
        ArgumentNullException.ThrowIfNull(elevation);
        if (_directOnly || _userHostAdmission is null || _windowsTasks?.Authority != WindowsTaskAuthority.Owner ||
            !_windowsTasks.IsCurrentContext() || Interlocked.CompareExchange(ref _scheduledTaskElevation, elevation, null) is not null)
            throw new AgentOpsException("auth");
    }

    internal Task<OperationReceipt> HandleScheduledTaskElevationAsync(OperationRequest request, LocalPeer peer,
        CancellationToken token) => _scheduledTaskElevation is { } elevation && ScheduledTaskElevationContract.IsControlRequest(request)
        ? elevation.HandleAsync(request, peer, token)
        : Task.FromResult(new OperationReceipt(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
            "failed", "windows_task_elevation", "auth"));

    internal void BindWindowsTasks(WindowsTaskOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!OperatingSystem.IsWindows() || !operations.IsCurrentContext() ||
            operations.Authority == WindowsTaskAuthority.OwnerAdmin ||
            _directOnly != (operations.Authority == WindowsTaskAuthority.System) ||
            !_directOnly && _userHostAdmission is null ||
            Interlocked.CompareExchange(ref _windowsTasks, operations, null) is not null)
            throw new AgentOpsException("auth");
    }

    internal void BindOwnerWindowsTasks(string ownerSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var resolver = new WindowsTaskToolResolver(_tools, alias =>
            _workspaces.List().SingleOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Alias, alias))?.Path
                ?? throw new AgentOpsException("not_found"), ownerSid, LocalIdentity.Current);
        BindWindowsTasks(new WindowsTaskOperations(ownerSid, resolver));
    }

    // The service composition supplies the registered owner. Neither the HTTP
    // session nor public request can select the Scheduler identity.
    public void BindSystemWindowsTasks(string registeredOwnerSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        BindWindowsTasks(WindowsTaskOperations.ForSystem(registeredOwnerSid));
    }

    private static OperationReceipt WindowsTaskInitialReceipt(OperationReceipt receipt, OperationRequest request)
    {
        var invocation = WindowsTaskOperationContract.Parse(WindowsServicePublicShape(request));
        return receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>())
            { ["requestIdentity"] = invocation.RequestIdentity } };
    }

    private static OperationRequest WindowsTaskDurableRequest(OperationRequest request)
    {
        var arguments = new Dictionary<string, string> { ["op"] = request.Arg["op"], [OperationRequest.BodyOmittedKey] = "1" };
        // Preserve the route on a body-omitted recovery skeleton: a privileged
        // read must never become an ordinary ephemeral read after restart.
        if (request.Arg.GetValueOrDefault("elevate") == "uac") arguments["elevate"] = "uac";
        return new("windows_task", arguments, request.Rid);
    }

    private static bool WindowsTaskReplayMatches(OperationRequest request, OperationReceipt previous)
    {
        if (request.Op != "windows_task") return false;
        try
        {
            var invocation = WindowsTaskOperationContract.Parse(WindowsServicePublicShape(request));
            return previous.Op == WindowsTaskOperationContract.PublicName(invocation.Action) &&
                Text(previous.Data?.GetValueOrDefault("requestIdentity")) == invocation.RequestIdentity;
        }
        catch (WindowsTaskContractException) { return false; }
    }

    private OperationReceipt WindowsTaskDurableReceipt(OperationReceipt receipt)
    {
        if (IsHistoryPruned(receipt)) return receipt;
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        // Task names, XML, principals, tool argv and observations are live-only.
        // Compact history retains replay identity and effect/uncertainty facts.
        foreach (var key in new[] { "seq", "action_issued", "outcome_unknown", "postcondition_observed", "hresult", "helper_payload_issued", "windows_status" })
        {
            var value = receipt.Data?.GetValueOrDefault(key);
            if (value is JsonElement element)
            {
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) value = element.GetBoolean();
                else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number)) value = number;
            }
            if (value is bool or int or uint or long) data[key] = value;
        }
        foreach (var key in new[] { "requestIdentity", "phase", "task_code", "native_domain" })
            if (Text(receipt.Data?.GetValueOrDefault(key)) is { Length: > 0 and <= 128 } value && !value.Any(char.IsControl))
                data[key] = value;
        if (!WindowsTaskOperationContract.IsDigest(Text(data.GetValueOrDefault("requestIdentity"))) &&
            _store.FindReceipt(receipt.Jid) is { } old && old.Rid == receipt.Rid && old.Op == receipt.Op &&
            WindowsTaskOperationContract.IsDigest(Text(old.Data?.GetValueOrDefault("requestIdentity"))))
            data["requestIdentity"] = Text(old.Data?.GetValueOrDefault("requestIdentity"));
        if (receipt.State == "recovery_required") data["outcome_unknown"] = true;
        PreserveHelperGeneration(data, receipt, receipt.State == "recovery_required" ? _store.FindReceipt(receipt.Jid) : null,
            "helper_pid", "helper_start");
        data["log_available"] = false;
        return receipt with { Data = data, Detail = null, Partial = receipt.Partial || receipt.State == "recovery_required" };
    }

    private async Task<OperationReceipt> ExecuteWindowsTaskReceiptAsync(OperationRequest request, string jid, string rid,
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
                var invocation = WindowsTaskOperationContract.Parse(WindowsServicePublicShape(request));
                var system = request.Arg.GetValueOrDefault("__execution_context_system") == "1";
                if (_windowsTasks?.IsCurrentContext() != true ||
                    system != (_windowsTasks.Authority == WindowsTaskAuthority.System) ||
                    system && !WindowsTaskOperationContract.IsSystemAction(invocation.Action)) throw new AgentOpsException("auth");
                data["requestIdentity"] = invocation.RequestIdentity;
                if (invocation.RequiresOwnerElevation)
                    return await ExecuteElevatedWindowsTaskReceiptAsync(request, invocation, jid, rid, sequence, token, persist).ConfigureAwait(false);
                // Keep the original COM worker, its references and maintenance
                // effect owned until it returns, even after caller cancellation.
                var outcome = await Task.Run(() => _windowsTasks.Execute(invocation, token), CancellationToken.None).ConfigureAwait(false);
                receipt = WindowsTaskOutcomeReceipt(request, jid, rid, outcome, data);
            }
        }
        catch (Exception failure) when (failure is AgentOpsException or WindowsTaskContractException or OperationCanceledException)
        {
            var code = failure is AgentOpsException known ? known.Code : failure is OperationCanceledException ? "canceled" : "invalid";
            if (failure is WindowsTaskContractException invalid) data["field"] = invalid.Field;
            receipt = new(false, jid, rid, code == "canceled" ? "canceled" : "failed", OperationNames.Public(request), code, data,
                Failure: FailureEvidence.Generic(code, false, "windows-task-admission"));
        }
        return Finish(receipt, request, sequence, persist);
    }

    private static OperationReceipt WindowsTaskOutcomeReceipt(OperationRequest request, string jid, string rid,
        WindowsTaskOutcome outcome, Dictionary<string, object?> data)
    {
        data["action_issued"] = outcome.ActionIssued; data["outcome_unknown"] = outcome.OutcomeUnknown;
        data["postcondition_observed"] = outcome.Ok; data["phase"] = outcome.Phase;
        if (outcome.Error is { } code) data["task_code"] = code;
        if (outcome.NativeError is { } status) { data["hresult"] = status; data["native_domain"] = "task_scheduler_com"; }
        if (outcome.Page is { } page) data["page"] = page;
        if (outcome.Current is { } current) data["task"] = current;
        if (outcome.Previous is { } previous) data["previous"] = previous;
        if (outcome.Instances is { } instances) data["instances"] = instances;
        if (outcome.Tool is { } tool)
        { data["tool"] = tool.Tool; data["toolVersion"] = tool.DefinitionVersion; data["toolSha"] = tool.DefinitionSha; }
        if (outcome.Instance is { } instance) data["instance"] = instance;
        if (outcome.Observation is { } observation) data["observation"] = observation;
        if (outcome.Warning is { } warning) data["warning"] = warning;
        var error = outcome.OutcomeUnknown ? "recovery_required" : outcome.Error;
        return new(outcome.Ok, jid, rid, outcome.Ok ? "done" : outcome.OutcomeUnknown ? "recovery_required" :
            error is "canceled" or "cancelled" ? "canceled" : "failed", OperationNames.Public(request), error, data,
            Partial: outcome.OutcomeUnknown || !outcome.Ok && outcome.ActionIssued,
            Failure: outcome.Ok ? null : FailureEvidence.Generic(error ?? "task_failed", outcome.ActionIssued || outcome.OutcomeUnknown, "windows-task"));
    }

    private async Task<OperationReceipt> ExecuteElevatedWindowsTaskReceiptAsync(OperationRequest request, WindowsTaskInvocation invocation,
        string jid, string rid, long sequence, CancellationToken token, bool persist)
    {
        var issued = false;
        var data = new Dictionary<string, object?>
        { ["seq"] = sequence, ["log_available"] = false, ["requestIdentity"] = invocation.RequestIdentity, ["phase"] = "uac" };
        OperationReceipt receipt;
        try
        {
            if (!persist || _directOnly || _windowsTasks?.Authority != WindowsTaskAuthority.Owner ||
                !_windowsTasks.IsCurrentContext() || !invocation.RequiresOwnerElevation) throw new AgentOpsException("auth");
            var elevation = _scheduledTaskElevation ?? throw new AgentOpsException("task_elevation_unavailable");
            token.ThrowIfCancellationRequested();
            var payload = ScheduledTaskElevationContract.Create(WindowsServicePublicShape(request), jid, rid);
            var outcome = await elevation.BeginAsync(payload, PersistIssued, token).ConfigureAwait(false);
            issued = outcome.PayloadIssued;
            data["helper_payload_issued"] = issued;
            if (request.Detail && outcome.Process is { } process)
            { data["helper_pid"] = (long)process.Pid; data["helper_start"] = process.Start; }
            if (request.Detail && outcome.Exit is { } exit) data["helper_exit"] = exit;
            if (outcome.Result is { } result)
            {
                if (!issued || result.Action != invocation.Action) throw new AgentOpsException("invalid");
                // A received report survives a later ACK failure/nonzero exit.
                // The coordinator still awaits this original child's natural exit.
                receipt = WindowsTaskOutcomeReceipt(request, jid, rid, result, data);
            }
            else
            {
                if (outcome.NativeError is { } status)
                { data["windows_status"] = status; data["native_domain"] = "shell_execute"; }
                receipt = Unobserved(outcome.Failure ?? "task_elevation_no_completion");
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
            // Persist the route/uncertainty barrier before any task payload
            // escapes. Recovery retains this RID and never launches another UAC.
            if (!persist) throw new AgentOpsException("precondition");
            data["helper_pid"] = (long)process.Pid; data["helper_start"] = process.Start;
            var pending = new Dictionary<string, object?>(data)
            { ["helper_payload_issued"] = true, ["outcome_unknown"] = true };
            var running = new OperationReceipt(true, jid, rid, "running", OperationNames.Public(request), Data: pending);
            var durable = new DurableJob(WindowsTaskDurableReceipt(running), true, sequence, WindowsTaskDurableRequest(request));
            _store.Update(durable); issued = true; _jobs[jid] = durable;
        }

        OperationReceipt Unobserved(string cause)
        {
            var canceled = cause is "canceled" or "task_elevation_canceled" or "task_elevation_declined" or
                "owner_elevation_canceled" or "owner_elevation_declined" or "certificate_elevation_declined" or "certificate_elevation_canceled";
            var code = issued ? "recovery_required" : canceled ? "canceled" :
                cause is "auth" or "invalid" or "precondition" ? cause : "task_elevation_failed";
            data["helper_payload_issued"] = issued; data["outcome_unknown"] = issued;
            data["postcondition_observed"] = false;
            if (!issued) data["action_issued"] = false;
            data["task_code"] = RegistryReceiptAdapter.IsSafeCode(cause) ? cause : "task_elevation_failed";
            return new(false, jid, rid, issued ? "recovery_required" : canceled ? "canceled" : "failed", OperationNames.Public(request), code,
                data, Partial: issued, Failure: FailureEvidence.Generic(code, issued, "windows-task-uac"));
        }
    }
}
