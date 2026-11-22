using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

// Only the closed original service request crosses this fixed helper lane.
// It cannot select a remote SCM, executable, account, control code or provider.
internal sealed record WindowsServiceElevationRequest(int Version, string Action, string Body,
    string RequestIdentity, string Jid, string Rid)
{ public override string ToString() => "WindowsServiceElevationRequest(Body=omitted)"; }
internal sealed record WindowsServiceElevationCompletion(int Version, string RequestIdentity, string Jid, string Rid,
    WindowsServiceOutcome? Result, string? Failure = null);
internal sealed record WindowsServiceElevationOutcome(WindowsServiceOutcome? Result, bool PayloadIssued,
    string? Failure = null, CertificateElevationProcessIdentity? Process = null, int? Exit = null, int? NativeError = null);
internal sealed class WindowsServiceElevationContractException(string field) : ArgumentException("windows_service_elevation_contract_invalid")
{ internal string Field { get; } = field; }

internal static class WindowsServiceElevationContract
{
    internal const int Version = 1, MaximumPayloadBytes = 64 * 1024, MaximumCompletionBytes = 64 * 1024;
    internal const string RedeemOperation = "__windows_service_uac_redeem";
    internal const string CompleteOperation = "__windows_service_uac_complete";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false
    };
    private static readonly string[] ResultFields =
        ["ok", "action", "previous", "current", "actionIssued", "outcomeUnknown", "error", "phase", "nativeError"];
    private static readonly string[] DetailFields =
        ["service", "displayName", "state", "serviceType", "startup", "delayedAutoStart", "configSha", "pid", "start", "controlsAccepted"];
    private static readonly HashSet<string> Errors = new(StringComparer.Ordinal)
    {
        "access", "not_found", "service_disabled", "service_dependents_active", "canceled", "timeout", "service_failed",
        "precondition", "service_type_not_supported", "service_configuration_unavailable", "service_native_buffer_invalid",
        "service_requires_cortex_control", "service_unavailable", "service_outcome_unknown",
        "service_owner_context_required", "service_owner_elevation_required", "service_elevation_route_required"
    };

    internal static bool IsControlRequest(OperationRequest request) => request is not null &&
        request.Op is RedeemOperation or CompleteOperation;
    internal static WindowsServiceElevationRequest Create(OperationRequest request, string jid, string rid)
    {
        if (request is null) throw Invalid("request");
        WindowsServiceInvocation invocation;
        try { invocation = WindowsServiceOperationContract.Parse(request); }
        catch (AgentOpsException) { throw Invalid("request"); }
        if (!invocation.RequiresOwnerElevation || request.Rid != rid) throw Invalid("route");
        var payload = new WindowsServiceElevationRequest(Version, invocation.Action, request.Body!, invocation.RequestIdentity, jid, rid);
        ValidateRequest(payload); return payload;
    }
    internal static OperationRequest Reconstruct(WindowsServiceElevationRequest request)
    {
        if (request is null) throw Invalid("payload");
        return new("windows_service", new Dictionary<string, string>
            { ["op"] = request.Action, ["elevate"] = "uac" }, request.Rid, Body: request.Body);
    }
    internal static void ValidateRequest(WindowsServiceElevationRequest request)
    {
        if (request is null || request.Version != Version || !WindowsServiceOperationContract.IsOwnerElevationAction(request.Action) ||
            !WindowsServiceOperationContract.IsDigest(request.RequestIdentity) || !Hex(request.Jid, 32) || !Rid(request.Rid))
            throw Invalid("payload");
        WindowsServiceInvocation invocation;
        try { invocation = WindowsServiceOperationContract.Parse(Reconstruct(request)); }
        catch (AgentOpsException) { throw Invalid("body"); }
        if (!invocation.RequiresOwnerElevation || invocation.RequestIdentity != request.RequestIdentity) throw Invalid("requestIdentity");
        // Cortex has its own coordinated lifecycle contract. The generic helper
        // must not stop/reconfigure its own transport or privileged owner.
        if (invocation.IsMutation && StringComparer.OrdinalIgnoreCase.Equals(invocation.Service, CortexProductIdentity.ServiceName))
            throw Invalid("service");
    }
    internal static WindowsServiceElevationRequest Snapshot(WindowsServiceElevationRequest request)
    { ValidateRequest(request); return request with { }; }
    internal static string SerializeRequest(WindowsServiceElevationRequest request)
    { ValidateRequest(request); return Serialize(request, MaximumPayloadBytes); }
    internal static WindowsServiceElevationRequest ParseRequest(string text)
    {
        using var document = Document(text, MaximumPayloadBytes);
        Fields(document.RootElement, ["version", "action", "body", "requestIdentity", "jid", "rid"]);
        var request = Deserialize<WindowsServiceElevationRequest>(document.RootElement);
        ValidateRequest(request); return request;
    }
    internal static string ParseTicket(OperationRequest request)
    {
        if (!IsControlRequest(request) || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is not null || request.Quiet || request.Detail || request.Arg is null || request.Arg.Count != 1 ||
            !request.Arg.TryGetValue("ticket", out var ticket) || !WindowsServiceOperationContract.IsDigest(ticket) ||
            request.Rid is not null && !Rid(request.Rid)) throw Invalid("ticket");
        if (request.Op == RedeemOperation)
        { if (request.Body is not null) throw Invalid("body"); }
        else Size(request.Body, MaximumCompletionBytes);
        return ticket;
    }
    internal static string SerializeCompletion(WindowsServiceElevationRequest expected, WindowsServiceOutcome? result, string? failure = null)
    {
        ValidateRequest(expected);
        var completion = new WindowsServiceElevationCompletion(Version, expected.RequestIdentity, expected.Jid, expected.Rid, result, failure);
        ValidateCompletion(completion, expected); return Serialize(completion, MaximumCompletionBytes);
    }
    internal static WindowsServiceElevationCompletion ParseCompletion(OperationRequest request, WindowsServiceElevationRequest expected)
    {
        ValidateRequest(expected); _ = ParseTicket(request);
        if (request.Op != CompleteOperation) throw Invalid("op");
        using var document = Document(request.Body!, MaximumCompletionBytes);
        var fields = Fields(document.RootElement, ["version", "requestIdentity", "jid", "rid", "result", "failure"]);
        if (fields["result"].ValueKind != JsonValueKind.Null)
        {
            var result = Fields(fields["result"], ResultFields);
            foreach (var name in new[] { "previous", "current" })
                if (result[name].ValueKind != JsonValueKind.Null) Fields(result[name], DetailFields);
        }
        var completion = Deserialize<WindowsServiceElevationCompletion>(document.RootElement);
        ValidateCompletion(completion, expected); return completion;
    }
    internal static void ValidateCompletion(WindowsServiceElevationCompletion completion, WindowsServiceElevationRequest expected)
    {
        ValidateRequest(expected);
        if (completion is null || completion.Version != Version || completion.RequestIdentity != expected.RequestIdentity ||
            completion.Jid != expected.Jid || completion.Rid != expected.Rid) throw Invalid("completionIdentity");
        if (completion.Failure is not null && completion.Failure is not
            ("windows_service_helper_failed" or "windows_service_helper_cancelled" or "windows_service_helper_unavailable"))
            throw Invalid("failure");
        if (completion.Result is null)
        {
            if (completion.Failure is null) throw Invalid("result");
            return; // No report is no evidence, never an unissued native outcome.
        }
        if (completion.Failure is not null) throw Invalid("failure");
        var result = completion.Result;
        var invocation = WindowsServiceOperationContract.Parse(Reconstruct(expected));
        if (result.Action != expected.Action || !Phase(expected.Action, result.Phase) ||
            result.Error is not null && !Errors.Contains(result.Error) || result.Ok && result.Error is not null ||
            !result.Ok && result.Error is null || result.NativeError is < 0 || result.Ok && result.NativeError is not null ||
            result.OutcomeUnknown != (!result.Ok && result.ActionIssued) ||
            result.OutcomeUnknown != (result.Error == "service_outcome_unknown") ||
            result.ActionIssued && result.Phase is ("admission" or "open" or "observation" or "unchanged"))
            throw Invalid("outcome");
        if (result.NativeError is { } native && result.Error != (result.ActionIssued ? "service_outcome_unknown" : native switch
            {
                5 => "access", 1060 => "not_found", 1058 => "service_disabled", 1051 => "service_dependents_active", _ => "service_failed"
            })) throw Invalid("nativeError");
        ValidateDetails(result.Previous, invocation.Service!); ValidateDetails(result.Current, invocation.Service!);
        if (!invocation.IsMutation)
        {
            if (result.ActionIssued || result.OutcomeUnknown || result.Ok &&
                (result.Previous is null || result.Current is null || result.Previous != result.Current || result.Phase != "observation"))
                throw Invalid("observation");
            if (result.Ok && (result.Current!.ServiceType & 0x30) == 0) throw Invalid("serviceType");
            return;
        }
        if (result.ActionIssued || result.Ok)
        {
            var previous = result.Previous;
            // The provider discards both snapshots after losing its owner-High
            // context. Preserve its issued/unknown fact without pretending that
            // absent private metadata is an observed or matching preimage.
            if (previous is null)
            {
                if (result.Ok || !result.OutcomeUnknown || result.Current is not null || result.NativeError is not null)
                    throw Invalid("preimage");
            }
            else if (previous.ConfigSha != invocation.ConfigSha || previous.State != invocation.ExpectedState ||
                (previous.ServiceType & 0x30) == 0 || invocation.Action is ("stop" or "restart") &&
                (previous.Pid != invocation.ProcessId || previous.Start != invocation.ProcessStart))
                throw Invalid("preimage");
        }
        if (result.Phase == "unchanged" && (!result.Ok || invocation.Action != "startup" || result.ActionIssued ||
            result.Current != result.Previous)) throw Invalid("unchanged");
        if (!result.Ok) return;
        if (result.Current is not { } current || (current.ServiceType & 0x30) == 0) throw Invalid("postcondition");
        if (invocation.Action == "startup")
        {
            if (current.Startup != invocation.Mode || current.DelayedAutoStart != (invocation.Mode == "automatic_delayed") ||
                (result.ActionIssued ? result.Phase != "postcondition" : result.Phase != "unchanged")) throw Invalid("postcondition");
        }
        else if (!result.ActionIssued || result.Phase != "postcondition" || current.ConfigSha != invocation.ConfigSha ||
            current.State != (invocation.Action == "stop" ? "stopped" : "running")) throw Invalid("postcondition");
    }

    private static void ValidateDetails(WindowsServiceDetails? details, string service)
    {
        if (details is null) return;
        if (!StringComparer.OrdinalIgnoreCase.Equals(details.Service, service) || !Text(details.Service, 256, false) ||
            !Text(details.DisplayName, 256, true) || details.State is not
                ("stopped" or "start_pending" or "stop_pending" or "running" or "continue_pending" or "pause_pending" or "paused" or "unknown") ||
            details.Startup is not ("automatic" or "automatic_delayed" or "manual" or "disabled" or "unsupported") ||
            !WindowsServiceOperationContract.IsDigest(details.ConfigSha) || details.Pid is < 1 ||
            details.Start is not null && (details.Pid is null || !WindowsServiceOperationContract.CanonicalStart(details.Start)) ||
            details.State == "stopped" && (details.Pid is not null || details.Start is not null))
            throw Invalid("target");
    }
    private static bool Phase(string action, string? phase) => phase is "admission" or "open" or "observation" || (action switch
    {
        "start" => phase is "start" or "postcondition",
        "stop" => phase is "stop" or "postcondition",
        "restart" => phase is "stop" or "start" or "postcondition",
        "startup" => phase is "startup" or "unchanged" or "postcondition",
        _ => false
    });
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
        try { return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException) { throw Invalid("json"); }
    }
    private static T Deserialize<T>(JsonElement value) where T : class
    {
        try { return value.Deserialize<T>(Json) ?? throw Invalid("json"); }
        catch (Exception failure) when (failure is JsonException or NotSupportedException) { throw Invalid("json"); }
    }
    private static string Serialize<T>(T value, int maximum)
    { var text = JsonSerializer.Serialize(value, Json); Size(text, maximum); return text; }
    private static void Size(string? text, int maximum)
    {
        if (text is null || text.Length > maximum || !WellFormed(text) || Encoding.UTF8.GetByteCount(text) > maximum) throw Invalid("size");
    }
    private static bool Text(string? text, int maximum, bool allowEmpty) => text is not null &&
        text.Length <= maximum && (allowEmpty || !string.IsNullOrWhiteSpace(text)) && !text.Any(char.IsControl) && WellFormed(text);
    private static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Rid(string? value) => value is { Length: >= 1 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) && WellFormed(value);
    private static bool WellFormed(string text)
    { try { _ = new UTF8Encoding(false, true).GetByteCount(text); return true; } catch (EncoderFallbackException) { return false; } }
    private static WindowsServiceElevationContractException Invalid(string field) => new(field);
}
