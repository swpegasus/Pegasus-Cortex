using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Only an already admitted existing-task request crosses the fixed helper lane.
// Body contains the closed original task JSON, never XML, command, argv, principal,
// credentials, Root, or an arbitrary helper operation.
internal sealed record ScheduledTaskElevationRequest(int Version, string Action, string Body,
    string RequestIdentity, string Jid, string Rid)
{ public override string ToString() => "ScheduledTaskElevationRequest(Body=omitted)"; }
internal sealed record ScheduledTaskElevationCompletion(int Version, string RequestIdentity, string Jid, string Rid,
    WindowsTaskOutcome? Result, string? Failure = null);
internal sealed record ScheduledTaskElevationOutcome(WindowsTaskOutcome? Result, bool PayloadIssued,
    string? Failure = null, CertificateElevationProcessIdentity? Process = null, int? Exit = null, int? NativeError = null);
internal sealed class ScheduledTaskElevationContractException(string field) : ArgumentException("scheduled_task_elevation_contract_invalid")
{ internal string Field { get; } = field; }

internal static class ScheduledTaskElevationContract
{
    internal const int Version = 1;
    internal const string RedeemOperation = "__windows_task_uac_redeem";
    internal const string CompleteOperation = "__windows_task_uac_complete";
    internal const int MaximumPayloadBytes = 128 * 1024;
    internal const int MaximumCompletionBytes = WindowsTaskOperationContract.MaximumPageBytes + 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 10, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false
    };
    private static readonly string[] OutcomeFields = ["ok", "action", "phase", "error", "actionIssued", "outcomeUnknown",
        "previous", "current", "page", "instances", "tool", "nativeError", "warning", "instance", "observation"];
    private static readonly string[] DetailFields = ["path", "state", "enabled", "hidden", "allowDemandStart", "taskSha",
        "definitionSha", "securitySha", "principalKind", "runLevel", "actionCount", "triggerCount", "authoringSupported", "lastRun", "nextRun", "lastTaskResult"];
    private static readonly string[] InstanceFields = ["instanceGuid", "taskPath", "state"];
    private static readonly HashSet<string> Errors = new(StringComparer.Ordinal)
    {
        "owner_denied", "cancelled", "precondition", "not_found", "instance_not_found_in_caller_context",
        "task_access_denied", "task_account_unavailable", "task_disabled", "task_scheduler_unavailable", "task_native_failed",
        "task_unavailable", "task_platform_unavailable", "task_worker_context_required", "task_property_too_large",
        "task_property_invalid", "task_fingerprint_unavailable", "task_time_unavailable", "task_collection_changed",
        "task_instance_unavailable", "task_enumeration_bound", "task_result_too_large", "task_postcondition_unavailable",
        "task_not_demand_startable", "task_identity_requires_explicit_authority", "task_owner_elevation_required",
        "task_authoring_requires_ordinary_owner", "task_tool_precondition"
    };

    internal static bool IsControlRequest(OperationRequest request) => request is not null &&
        request.Op is RedeemOperation or CompleteOperation;
    internal static ScheduledTaskElevationRequest Create(OperationRequest request, string jid, string rid)
    {
        var invocation = WindowsTaskOperationContract.Parse(request);
        if (!invocation.RequiresOwnerElevation || request.Rid != rid) throw Invalid("route");
        var payload = new ScheduledTaskElevationRequest(Version, invocation.Action, request.Body!, invocation.RequestIdentity, jid, rid);
        ValidateRequest(payload); return payload;
    }
    internal static OperationRequest Reconstruct(ScheduledTaskElevationRequest request)
    {
        if (request is null) throw Invalid("payload");
        return new("windows_task", new Dictionary<string, string>
            { ["op"] = request.Action, ["elevate"] = "uac" }, request.Rid, Body: request.Body);
    }
    internal static void ValidateRequest(ScheduledTaskElevationRequest request)
    {
        if (request is null || request.Version != Version || !WindowsTaskOperationContract.IsOwnerElevationAction(request.Action) ||
            !WindowsTaskOperationContract.IsDigest(request.RequestIdentity) || !Hex(request.Jid, 32) || !Rid(request.Rid))
            throw Invalid("payload");
        WindowsTaskInvocation parsed;
        try { parsed = WindowsTaskOperationContract.Parse(Reconstruct(request)); }
        catch (WindowsTaskContractException) { throw Invalid("body"); }
        if (!parsed.RequiresOwnerElevation || parsed.Action != request.Action || parsed.RequestIdentity != request.RequestIdentity)
            throw Invalid("requestIdentity");
    }
    internal static ScheduledTaskElevationRequest Snapshot(ScheduledTaskElevationRequest request)
    { ValidateRequest(request); return request with { }; }
    internal static string SerializeRequest(ScheduledTaskElevationRequest request)
    { ValidateRequest(request); return Serialize(request, MaximumPayloadBytes); }
    internal static ScheduledTaskElevationRequest ParseRequest(string text)
    {
        using var document = Document(text, MaximumPayloadBytes);
        Fields(document.RootElement, ["version", "action", "body", "requestIdentity", "jid", "rid"]);
        var request = Deserialize<ScheduledTaskElevationRequest>(document.RootElement);
        ValidateRequest(request); return request;
    }
    internal static string ParseTicket(OperationRequest request)
    {
        if (!IsControlRequest(request) || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is not null || request.Quiet || request.Detail || request.Arg is null || request.Arg.Count != 1 ||
            !request.Arg.TryGetValue("ticket", out var ticket) || !WindowsTaskOperationContract.IsDigest(ticket) ||
            request.Rid is not null && !Rid(request.Rid)) throw Invalid("ticket");
        if (request.Op == RedeemOperation)
        { if (request.Body is not null) throw Invalid("body"); }
        else Size(request.Body, MaximumCompletionBytes);
        return ticket;
    }
    internal static string SerializeCompletion(ScheduledTaskElevationRequest expected, WindowsTaskOutcome? result, string? failure = null)
    {
        ValidateRequest(expected);
        var completion = new ScheduledTaskElevationCompletion(Version, expected.RequestIdentity, expected.Jid, expected.Rid, result, failure);
        ValidateCompletion(completion, expected);
        return Serialize(completion, MaximumCompletionBytes);
    }
    internal static ScheduledTaskElevationCompletion ParseCompletion(OperationRequest request, ScheduledTaskElevationRequest expected)
    {
        ValidateRequest(expected);
        _ = ParseTicket(request);
        if (request.Op != CompleteOperation) throw Invalid("op");
        using var document = Document(request.Body!, MaximumCompletionBytes);
        var fields = Fields(document.RootElement, ["version", "requestIdentity", "jid", "rid", "result", "failure"]);
        var result = fields["result"];
        if (result.ValueKind != JsonValueKind.Null) ValidateResultShape(result);
        var completion = Deserialize<ScheduledTaskElevationCompletion>(document.RootElement);
        ValidateCompletion(completion, expected); return completion;
    }
    internal static void ValidateCompletion(ScheduledTaskElevationCompletion completion, ScheduledTaskElevationRequest expected)
    {
        ValidateRequest(expected);
        if (completion is null || completion.Version != Version || completion.RequestIdentity != expected.RequestIdentity ||
            completion.Jid != expected.Jid || completion.Rid != expected.Rid) throw Invalid("completionIdentity");
        if (completion.Failure is not null && completion.Failure is not
            ("scheduled_task_helper_failed" or "scheduled_task_helper_cancelled" or "scheduled_task_helper_unavailable"))
            throw Invalid("failure");
        if (completion.Result is null)
        {
            if (completion.Failure is null) throw Invalid("result");
            return; // No evidence is never translated into a false ActionIssued.
        }
        if (completion.Failure is not null) throw Invalid("failure");
        var result = completion.Result;
        var invocation = WindowsTaskOperationContract.Parse(Reconstruct(expected));
        if (result.Action != expected.Action || result.Page is not null || result.Tool is not null ||
            result.Phase is not ("admission" or "open" or "precondition" or "observation" or "run" or "stop" or "enable" or "disable" or "remove") ||
            result.Error is not null && !Errors.Contains(result.Error) || result.Ok && result.Error is not null ||
            !result.Ok && result.Error is null || !invocation.IsMutation && (result.ActionIssued || result.OutcomeUnknown) ||
            result.OutcomeUnknown && !result.ActionIssued || result.Ok && invocation.IsMutation && !result.ActionIssued ||
            result.Ok && result.OutcomeUnknown || result.Warning is not (null or "scheduler_warning") ||
            result.Warning is not null && result.NativeError is null) throw Invalid("outcome");
        if (result.Observation is not (null or "run_request_acknowledged_instance_not_observed" or "instance_observed_not_completion" or
            "registration_absent_running_instances_not_stopped" or "disabled_running_instances_not_stopped" or
            "enabled_future_triggers_may_run" or "exact_instance_stop_acknowledged_not_process_tree_completion"))
            throw Invalid("observation");
        if (result.Ok && result.NativeError is < 0 || result.Warning is not null && result.NativeError is not > 0 ||
            result.Ok && invocation.IsMutation && (result.Previous is null || result.Previous.TaskSha != invocation.TaskSha) ||
            result.ActionIssued && result.Previous is not null && result.Previous.TaskSha != invocation.TaskSha)
            throw Invalid("preimage");
        if (result.Ok && expected.Action == "run" && (result.Instance is null
                ? result.Observation != "run_request_acknowledged_instance_not_observed"
                : result.Observation != "instance_observed_not_completion") ||
            result.Ok && expected.Action == "stop" && (result.Instance is null ||
                result.Observation != "exact_instance_stop_acknowledged_not_process_tree_completion") ||
            result.Ok && expected.Action == "enable" && result.Observation != "enabled_future_triggers_may_run" ||
            result.Ok && expected.Action == "disable" && result.Observation != "disabled_running_instances_not_stopped" ||
            result.Ok && expected.Action == "remove" && result.Observation != "registration_absent_running_instances_not_stopped")
            throw Invalid("observation");
        ValidateDetails(result.Previous, invocation.TaskPath!); ValidateDetails(result.Current, invocation.TaskPath!);
        if (result.Instance is { } instance)
        {
            ValidateInstance(instance, invocation.TaskPath!);
            if (expected.Action is not ("run" or "stop") || expected.Action == "stop" && instance.InstanceGuid != invocation.InstanceGuid)
                throw Invalid("instance");
        }
        if (result.Instances is { } instances)
        {
            if (expected.Action != "instances" || instances.Items is null || instances.Items.Count > WindowsTaskOperationContract.MaximumPageSize ||
                instances.ObservedCount < instances.Items.Count || instances.Visibility != "caller_context_visible_instances_only" ||
                instances.HasMore != (instances.ObservedCount > instances.Items.Count) ||
                instances.BoundReached != (instances.ObservedCount > WindowsTaskOperationContract.MaximumEntries))
                throw Invalid("instances");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in instances.Items)
            { ValidateInstance(item, invocation.TaskPath!); if (!ids.Add(item.InstanceGuid)) throw Invalid("instances"); }
        }
        if (result.Ok && (expected.Action is "read" or "instances" or "enable" or "disable") && result.Current is null ||
            result.Ok && expected.Action == "instances" && result.Instances is null ||
            result.Ok && expected.Action == "remove" && result.Current is not null ||
            result.Ok && expected.Action == "enable" && result.Current?.Enabled != true ||
            result.Ok && expected.Action == "disable" && result.Current?.Enabled != false) throw Invalid("postcondition");
    }

    private static void ValidateDetails(WindowsTaskDetails? details, string expectedPath)
    {
        if (details is null) return;
        if (!StringComparer.OrdinalIgnoreCase.Equals(details.Path, expectedPath) || !State(details.State) ||
            !WindowsTaskOperationContract.IsDigest(details.TaskSha) || !WindowsTaskOperationContract.IsDigest(details.DefinitionSha) ||
            !WindowsTaskOperationContract.IsDigest(details.SecuritySha) ||
            details.PrincipalKind is not ("owner_interactive_limited" or "different_or_noninteractive_identity" or "unverified_principal") ||
            details.RunLevel is not ("least" or "highest" or "unknown") || details.ActionCount < 0 || details.TriggerCount < 0)
            throw Invalid("target");
        ValidateTime(details.LastRun); ValidateTime(details.NextRun);
    }
    private static void ValidateInstance(WindowsTaskInstance instance, string expectedPath)
    {
        if (instance is null || !StringComparer.OrdinalIgnoreCase.Equals(instance.TaskPath, expectedPath) || !State(instance.State) ||
            !Guid.TryParseExact(instance.InstanceGuid, "D", out var id) || id == Guid.Empty || id.ToString("D") != instance.InstanceGuid)
            throw Invalid("instance");
    }
    private static void ValidateTime(WindowsTaskLocalTime? time)
    {
        if (time is null) return;
        if (time.TimeBasis != "scheduler_local_ole_date" || time.TimeZoneId is not { Length: >= 1 and <= 128 } ||
            time.TimeZoneId.Any(char.IsControl) || !DateTime.TryParseExact(time.Value, "yyyy-MM-dd'T'HH:mm:ss.fffffff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            (time.Ambiguous || time.Invalid) && time.UtcOffset is not null ||
            !(time.Ambiguous || time.Invalid) && (time.UtcOffset is null || !TimeSpan.TryParseExact(time.UtcOffset, "c", CultureInfo.InvariantCulture, out _)))
            throw Invalid("time");
    }
    private static bool State(string? state) => state is "unknown" or "disabled" or "queued" or "ready" or "running";
    private static void ValidateResultShape(JsonElement value)
    {
        var fields = Fields(value, OutcomeFields);
        foreach (var name in new[] { "previous", "current" })
            if (fields[name].ValueKind != JsonValueKind.Null)
            {
                var details = Fields(fields[name], DetailFields);
                foreach (var timeName in new[] { "lastRun", "nextRun" })
                    if (details[timeName].ValueKind != JsonValueKind.Null)
                        Fields(details[timeName], ["value", "timeBasis", "timeZoneId", "ambiguous", "invalid", "utcOffset"]);
            }
        if (fields["instance"].ValueKind != JsonValueKind.Null) Fields(fields["instance"], InstanceFields);
        if (fields["instances"].ValueKind != JsonValueKind.Null)
        {
            var instances = Fields(fields["instances"], ["items", "observedCount", "hasMore", "boundReached", "visibility"]);
            if (instances["items"].ValueKind != JsonValueKind.Array || instances["items"].GetArrayLength() > WindowsTaskOperationContract.MaximumPageSize)
                throw Invalid("instances");
            foreach (var item in instances["items"].EnumerateArray()) Fields(item, InstanceFields);
        }
        if (fields["page"].ValueKind != JsonValueKind.Null || fields["tool"].ValueKind != JsonValueKind.Null) throw Invalid("outcome");
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("fields");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("fields");
        if (fields.Count != names.Length) throw Invalid("fields");
        return fields;
    }
    private static JsonDocument Document(string text, int maximum)
    {
        Size(text, maximum);
        try { return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 10, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException) { throw Invalid("json"); }
    }
    private static T Deserialize<T>(JsonElement value) where T : class
    {
        try { return value.Deserialize<T>(Json) ?? throw Invalid("json"); }
        catch (JsonException) { throw Invalid("json"); }
        catch (NotSupportedException) { throw Invalid("json"); }
    }
    private static string Serialize<T>(T value, int maximum)
    { var text = JsonSerializer.Serialize(value, Json); Size(text, maximum); return text; }
    private static void Size(string? value, int maximum)
    {
        if (value is null || value.Length > maximum || !WindowsTaskOperationContract.WellFormed(value) || Encoding.UTF8.GetByteCount(value) > maximum)
            throw Invalid("size");
    }
    private static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Rid(string? value) => value is { Length: >= 1 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) && WindowsTaskOperationContract.WellFormed(value);
    private static ScheduledTaskElevationContractException Invalid(string field) => new(field);
}
