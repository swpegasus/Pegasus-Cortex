using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Protocol;

public static class ProtocolVersion
{
    public const ushort Major = 1;
    public const ushort Minor = 0;
    public const int MaxFrameBytes = 4 * 1024 * 1024;
}

public static class AgentOpsProductInfo
{
    public const string Product = CortexProductIdentity.Product;
    public const string Component = "execution";
    public static string ProductVersion { get; } =
        typeof(AgentOpsProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
    public const int McpSchemaVersion = CortexProductIdentity.PaoSchema;
    public const int InstallManifestVersion = 1;
}

public sealed record OperationRequest(
    string Op,
    IReadOnlyDictionary<string, string> Arg,
    string? Rid = null,
    string? Ws = null,
    string? Cwd = null,
    string? Root = null,
    bool Quiet = false,
    bool Detail = false,
    IReadOnlyDictionary<string, string>? PathRoots = null,
    [property: JsonIgnore] string? Body = null,
    bool System = false,
    CortexMcpConnectionAuthority? ConnectionAuthority = null)
{
    public const int MaxBodyBytes = 1024 * 1024;
    public const string BodyOmittedKey = "__body_omitted";
}

public static class OperationNames
{
    public static string Public(OperationRequest request)
    {
        var sub = request.Arg.GetValueOrDefault("op");
        return request.Op switch
        {
            "ctx" when request.Arg.ContainsKey("register") => "ctx_add",
            "ctx" when sub is "ls" or "cur" => "ctx_" + sub,
            "slice" when request.Arg.GetValueOrDefault("mode") is { Length: > 0 } mode => mode,
            "path" when sub is "realpath" or "basename" or "dirname" => sub,
            "lines" when sub is "sort" or "uniq" => sub,
            "disk" when sub is "du" or "df" => sub,
            "encoding" when sub is "inspect" or "convert" => "encoding_" + sub,
            "wr" when sub == "replace" => "replace",
            "zip" when sub is not null => "zip_" + sub,
            "7z" when sub is not null => "7z_" + sub,
            "git" when sub is not null => "git_" + sub.Replace('-', '_'),
            "dotnet" when sub is not null => "dotnet_" + sub,
            "proc" when sub is not null => "proc_" + sub,
            "windows_service" when sub is not null => "windows_service_" + sub,
            "windows_task" when sub is not null => "windows_task_" + sub,
            "installed_program" when sub is not null => "installed_program_" + sub,
            "windows_device" when sub is not null => "windows_device_" + sub,
            "windows_network" when sub is not null => "windows_network_" + sub,
            "windows_account" when sub is not null => "windows_account_" + sub,
            "connection_profile" when sub is not null => "connection_profile_" + sub,
            "service" when sub is not null => "service_" + sub,
            "net" when sub is not null => "net_" + sub,
            "job" when sub is not null => "job_" + sub,
            "feedback" when sub is not null => "feedback_" + sub,
            "tool" when sub is not null => "tool_" + sub,
            "recipe" when sub is not null => "recipe_" + sub,
            "ui" when sub is not null => "ui_" + sub,
            "nsis" when sub is not null => "nsis_" + sub,
            "app" when sub is not null => "app_" + sub,
            "terminal" when sub is not null => "terminal_" + sub,
            "registry" when sub is not null => "registry_" + sub,
            "certificate" when sub is not null => "certificate_" + sub,
            "admin" when sub is not null => "admin_" + sub,
            _ => request.Op
        };
    }
}

public static class OperationExecutionState
{
    public const string NotDispatched = "not_dispatched";
    public const string Started = "started";
    public const string Partial = "partial";
    public const string Unknown = "unknown";
}

public static class OperationRetryDisposition
{
    public const string Safe = "safe";
    public const string AfterCorrection = "after_correction";
    public const string ReconcileFirst = "reconcile_first";
    public const string NotSupported = "not_supported";
}

public sealed record OperationFailureFact(string Value, string Source);

public sealed record OperationFailureField(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null);

public sealed record OperationFailureBound(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Actual,
    long Maximum,
    string Unit);

public sealed record OperationFailureDiagnostic(
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Column = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);

/// <summary>
/// Bounded, presentation-safe failure facts. Full process output remains in
/// <see cref="OperationReceipt.Data"/> and is never inferred from these fields.
/// </summary>
public sealed record OperationFailureEvidence(
    string Message,
    OperationFailureFact Execution,
    OperationFailureFact Retry,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OperationFailureField? Field = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OperationFailureBound? Bound = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OperationFailureDiagnostic>? Diagnostics = null,
    bool DiagnosticsOmitted = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Next = null,
    string DetailAvailability = "none",
    string LogAvailability = "not_retained");

public sealed record OperationReceipt(
    bool Ok,
    string Jid,
    string Rid,
    string State,
    string Op,
    string? Err = null,
    IReadOnlyDictionary<string, object?>? Data = null,
    string? Detail = null,
    bool Partial = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OperationFailureEvidence? Failure = null)
{
    public string? Remedy => Ok ? null : Op.StartsWith("registry_", StringComparison.Ordinal) && Err == "recovery_required"
        ? "Inspect and reconcile the original registry target and receipt. Never replay this job; a deliberate new change requires a fresh preimage and RID."
        : Op.StartsWith("certificate_", StringComparison.Ordinal) && (Partial || Err == "recovery_required")
        ? "Inspect the original certificate reference or exact file and receipt. Never replay an issued or uncertain attempt; a deliberate new change requires a fresh preimage and RID."
        : Err switch
    {
        "invalid" => "Correct the typed fields, value formats, and documented bounds; unknown or duplicate inputs are rejected.",
        "queue_full" => "The bounded user queue is full or still admitting an older recovery backlog. Inspect job stat, wait, or cancel; retry the same RID after capacity is available. No new job was accepted.",
        "unsupported_command" or "unsupported_option" => "Use the documented fixed pao ux subset or an equivalent typed pao_* operation; no shell fallback is attempted.",
        "path_style" => "Use a Windows path, workspace-relative path, alias-qualified path, or explicit /c/... drive form.",
        "encoding_required" => "Retry with an explicit recognized enc value such as utf-8 or codepage:N after identifying the source encoding.",
        "not_found" => "Verify the exact admitted path or registered tool name, then retry with the corrected typed value.",
        "job_pruned" => "This historical job retains only its outcome and request identity. Its payload cannot be retried; submit a deliberate new request if needed.",
        "pwsh_unconfigured" => "Install PowerShell 7 for all users so HKLM PowerShellCore identifies pwsh.exe, or register an exact portable PowerShell 7 path with pao tool add --stdin; Windows PowerShell 5.1 is not accepted.",
        "git_unconfigured" => "Register and probe the exact canonical Git executable with pao tool add --stdin, then retry the typed Git operation.",
        "nsis_unconfigured" => "Repair the admitted product-local NSIS provider or register and probe the exact canonical makensis.exe as tool name nsis, then retry the fixed typed operation.",
        "nsis_unsafe_directive" => "Remove compile-time execution or filesystem directives; AgentOps accepts only side-effect-free preprocessed NSIS input.",
        "nsis_output_contract" => "Use exactly one OutFile directive bound to ${PAO_OUT}; AgentOps supplies and verifies the exact admitted destination.",
        "collision" => "Choose a different exact destination or an operation-supported pol such as replace or skip.",
        // Keep app admission guidance visible in legacy CLI and structured remedies.
        "precondition" when Op == "app_start" && Failure is
            { Field.Name: "ws", Execution: { Value: OperationExecutionState.NotDispatched, Source: "application-workspace-resolution" } } failure
            => failure.Message + " " + failure.Next,
        "precondition" or "root_target" or "pid_reused" => "Refresh the target identity and operation preconditions; do not force or blindly retry the previous request.",
        "service_not_admitted" => "Use only an explicitly admitted Pegasus service identity; arbitrary Windows service control is not available.",
        "reentrant_mutation" => "Run the parent application directly outside PAO when it must call PAO mutations; a PAO-owned process cannot enqueue another mutation behind itself.",
        "reparse_boundary" => "Use a path with no reparse-point ancestor; exact rm or mv may act on an admitted link leaf without traversing it.",
        "access" => "Use the signed-in owner context. For mapped SMB drives, register the user-visible path so it is normalized to UNC before service submission; verify that the owner session can access that UNC path.",
        "auth" => "Use the authenticated owner session; only fixed System-capable actions accept a valid transient enrolled root code.",
        "root_app_denied" => "Use the code only through an application allowed by that code's owner policy, or update the code policy in the authenticated tray.",
        "root_path_denied" => "Choose a path allowed by that code's folder policy, or update the code policy in the authenticated tray.",
        "admin_capability_unavailable" => "Deploy the coherently tested owner-admin capability through the reviewed owner route; ordinary User operations remain available.",
        "admin_review_required" => "One elevated owner setup must admit a reusable explicit task/tool execution scope. User tool registration cannot admit administrator execution.",
        "admin_admission_revoked" or "admin_admission_expired" => "The reusable execution scope is revoked or expired; do not bypass its owner review.",
        "admin_scope_mismatch" or "admin_admission_mismatch" or "admin_admission_conflict" => "Correct the exact task, owner, session, client, scope and per-request pins. Never reuse a RID for different work.",
        "admin_protected_input_required" => "Stage reviewed executable, script and code/config inputs in an administrator-protected task directory; preserve the ACL and ownership of declared user data.",
        "admin_input_changed" => "Recheck the exact input hashes and immutable definition before a new explicitly intended attempt; this request did not launch.",
        "admin_owner_token_unavailable" or "admin_owner_token_mismatch" or "admin_owner_profile_unavailable" or "admin_service_identity_required" => "The same owner elevated token, active session, loaded profile or authenticated service is unavailable. No System-token fallback or automatic UAC action is allowed.",
        "admin_outcome_unknown" => "Reconcile the original RID, JID, PID and start identity. The child was not killed and this request will never replay.",
        "admin_reconciliation_required" or "admin_lane_busy" => "Inspect the original admin_stat JID and domain evidence; a running or unknown original effect fences new maintenance. After verified quiescence, use elevated-owner admin_reconcile for that original identity. Never kill or replay it implicitly.",
        "admin_persistence_unavailable" => "Preserve this original RID/JID/PID/start receipt. Storage failed, but the original monitor remains attached; reconcile this job, never submit a successor blindly.",
        "admin_capacity" => "Observe or reconcile existing active owner-admin processes; terminal history is not an admission limit.",
        "timeout" => "Inspect bounded output, then increase tmo only if the operation is expected to take longer; the owned process tree was terminated.",
        "cancelled" or "canceled" => "Submit a new request only if cancellation was intentional and the prior receipt confirms no recovery attention is required.",
        "external_failed" => "Inspect exit, out, err_tail, and truncation fields; correct the registered executable, exact argv, cwd, or intentional environment.",
        "process_host_runtime" => "Run the self-contained package, or set the architecture-specific DOTNET_ROOT_X64 to a compatible .NET 10 runtime for developer builds.",
        "user_host_unavailable" => "Start or repair the signed-in Cortex user execution host. Ordinary work never falls back to the privileged service, and the tray UI is not an execution dependency.",
        "svc_down" or "service_unavailable" => "Start or repair the Cortex user execution host for ordinary work, or the privileged execution service for a root-authorized operation. The tray UI is not an execution dependency.",
        "copy_incomplete" or "clean_failed" => "Inspect del, cp, changed, and partial; reconcile the exact destination before submitting another mutation.",
        "recovery_required" => "Inspect job stat and bounded job log, verify the external outcome, then use explicit retry only when replay is safe.",
        "ui_unavailable" => "Keep the signed-in owner tray running or repair the product-local UI provider payload; do not register COM or route UI work through LocalSystem.",
        "ui_not_found" => "Resolve the visible window or standard control again with bounded exact criteria.",
        "ui_ambiguous" => "Narrow the window title or text criteria until exactly one target matches.",
        "stale_target" => "Resolve the window again and use its newly returned HWND, PID, and exact start identity.",
        "ui_unsupported" => "Use the application's native API, unattended mode, or a supported standard control instead of raw/global automation.",
        "postcondition_failed" => "Inspect the exact target state before deciding whether to retry; the UI action was issued but its result was not proven.",
        "ui_protocol" => "Repair the product-local UI host payload and inspect the target before retrying any uncertain mutation.",
        "tool_output_limit" => "Narrow the operation or output; use bounded detail or log retrieval instead of requesting unbounded child output.",
        "user_dispatch_required" => "Use the signed-in Cortex user host and app_start for registered User-mode application lifetime; no service fallback or elevation is available.",
        "app_capacity" => "Inspect app_inventory for the exact host capacity. Wait for owned active work or explicitly release only a known terminal, fully retired aid; never evict unknown generations.",
        "app_not_releasable" => "The exact generation is active, retiring or unconfirmed. Inspect it without stopping unrelated work; release never requests termination.",
        "app_inventory_changed" => "The host epoch or generation catalog changed. Restart bounded inventory pagination; never apply a stale release selection.",
        "app_owner_lost" => "The original user host no longer owns this application generation. Do not replay the launch implicitly; inspect the current owner and issue a deliberate new start if needed.",
        "app_launch_failed" => "The bounded application bootstrap did not acknowledge an admitted root; its owned job was closed. Inspect the registered tool, working directory, and source-candidate helper compatibility.",
        "app_stop_incomplete" => "Termination of this owned application was requested, but job quiescence is unconfirmed. Inspect app_status and retry app_stop for the same aid; do not start a replacement until termination is confirmed.",
        "app_observation_incomplete" => "Exact owned-process observation was unavailable or exceeded its bounded inventory. Retry app_status; do not infer that the application exited or start a replacement.",
        "internal" => "Record jid and rid, run pao diag detail=true, and report the bounded receipt so the implementation can be corrected.",
        _ => "Inspect the stable err code, jid, rid, partial flag, and bounded detail before correcting or retrying the typed request."
    };

    public int ExitCode => Ok ? 0 : Partial ? 7 : Err switch
    {
        "invalid" or "unsupported_command" or "unsupported_option" or "path_style" or "encoding_required" => 2,
        "app_not_releasable" or "app_inventory_changed" or "queue_full" or "not_found" or "service_not_admitted" or "pwsh_unconfigured" or "git_unconfigured" or "nsis_unconfigured" or "nsis_unsafe_directive" or "nsis_output_contract" or "collision" or "precondition" or "reentrant_mutation" or "reparse_boundary" or "clean_failed" or "root_target" or "pid_reused" or "ui_not_found" or "ui_ambiguous" or "stale_target" or "ui_unsupported" or "postcondition_failed" => 3,
        "access" or "auth" or "root_app_denied" or "root_path_denied" => 4,
        "timeout" or "cancelled" or "canceled" => 5,
        "external_failed" or "process_host_runtime" or "svc_down" or "service_unavailable" or "user_host_unavailable" or "ui_unavailable" => 6,
        "copy_incomplete" or "recovery_required" => 7,
        "job_pruned" or "registry_limit" or "registry_unsupported" => 3,
        "ui_protocol" => 8,
        _ => 8
    };
}

public sealed record TrayRegistration(string Sid, int Session, string PipeName);
public sealed record TrayRegistrationResult(bool Ok, string? Err = null);

public static class ApplicationAuthorizationProtocol
{
    public const int Version = 1;
    public const string CerebralApplicationId = "pegasus-cerebral";
    public const string CerebralServiceName = "PegasusCerebral";
}

// This contract is used only on the private service-to-service pipe. Credential
// material is never part of an OperationRequest, OperationReceipt, CLI or MCP
// schema. The server returns it exactly once for issue/rotate so the requesting
// service can protect it beneath its own non-agent boundary.
public sealed record ApplicationAuthorizationRequest(
    int Version,
    string ApplicationId,
    string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Credential = null);

public sealed record ApplicationAuthorizationResult(
    bool Ok,
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Err = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ApplicationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CredentialId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Generation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Credential = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionId = null);

public static class BinaryProtocol
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        // Local UTF-8 protocol, never HTML: preserve readable Unicode while keeping required JSON/control escapes.
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};options.Converters.Add(new WireOperationRequestConverter());return options;
    }

    public static Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, value, cancellationToken, ProtocolVersion.MaxFrameBytes);

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken, int maximumFrameBytes)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (maximumFrameBytes is < 1 or > ProtocolVersion.MaxFrameBytes || payload.Length > maximumFrameBytes) throw new InvalidDataException("frame_too_large");
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), ProtocolVersion.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), ProtocolVersion.Minor);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default) =>
        ReadAsync<T>(stream, cancellationToken, ProtocolVersion.MaxFrameBytes);

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken, int maximumFrameBytes)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (major != ProtocolVersion.Major) throw new InvalidDataException("protocol_version");
        // Minor releases are backward-compatible: this reader accepts older peers and
        // rejects newer peers whose added semantics it cannot safely understand.
        if (minor > ProtocolVersion.Minor) throw new InvalidDataException("protocol_minor_unsupported");
        if (maximumFrameBytes is < 1 or > ProtocolVersion.MaxFrameBytes || length < 1 || length > maximumFrameBytes) throw new InvalidDataException("frame_size");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("frame_json");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }
}

internal sealed class WireOperationRequestConverter:JsonConverter<OperationRequest>
{
    public override OperationRequest Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
    {
        using var document=JsonDocument.ParseValue(ref reader);var root=document.RootElement;
        var op=root.GetProperty("op").GetString()??throw new JsonException();
        var arg=root.TryGetProperty("arg",out var argElement)?JsonSerializer.Deserialize<Dictionary<string,string>>(argElement.GetRawText(),options)??new():new();arg.Remove(OperationRequest.BodyOmittedKey);
        // The live wire body is not omitted. Durable admission adds its own
        // marker after validating public arguments; never inject it here.
        var body=String(root,"body");ValidateBody(body);
        return new OperationRequest(op,arg,String(root,"rid"),String(root,"ws"),String(root,"cwd"),String(root,"root"),Bool(root,"quiet"),Bool(root,"detail"),
            root.TryGetProperty("pathRoots",out var roots)&&roots.ValueKind!=JsonValueKind.Null?JsonSerializer.Deserialize<Dictionary<string,string>>(roots.GetRawText(),options):null,body,
            StrictBoolean(root,"system"),ReadConnectionAuthority(root,options));
    }

    public override void Write(Utf8JsonWriter writer,OperationRequest value,JsonSerializerOptions options)
    {
        ValidateBody(value.Body);writer.WriteStartObject();writer.WriteString("op",value.Op);writer.WritePropertyName("arg");JsonSerializer.Serialize(writer,value.Arg,options);
        Write(writer,"rid",value.Rid);Write(writer,"ws",value.Ws);Write(writer,"cwd",value.Cwd);Write(writer,"root",value.Root);writer.WriteBoolean("quiet",value.Quiet);writer.WriteBoolean("detail",value.Detail);
        writer.WritePropertyName("pathRoots");JsonSerializer.Serialize(writer,value.PathRoots,options);Write(writer,"body",value.Body);
        writer.WriteBoolean("system",value.System);writer.WritePropertyName("connectionAuthority");
        JsonSerializer.Serialize(writer,value.ConnectionAuthority,options);writer.WriteEndObject();
    }

    private static string? String(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind!=JsonValueKind.Null?value.GetString():null;
    private static bool Bool(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.True;
    private static bool StrictBoolean(JsonElement root,string name)
    {
        if(!root.TryGetProperty(name,out var value))return false;
        if(value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new JsonException("invalid_system_intent");
        return value.GetBoolean();
    }
    private static CortexMcpConnectionAuthority? ReadConnectionAuthority(JsonElement root,JsonSerializerOptions options)
    {
        if(!root.TryGetProperty("connectionAuthority",out var value)||value.ValueKind==JsonValueKind.Null)return null;
        if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=4||
            value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=4||
            value.EnumerateObject().Any(p=>p.Name is not("authority" or "connectionId" or "credentialGeneration" or "grantRevision")))
            throw new JsonException("invalid_connection_authority");
        var authority=JsonSerializer.Deserialize<CortexMcpConnectionAuthority>(value.GetRawText(),options);
        if(!CortexMcpConnections.IsAuthority(authority))throw new JsonException("invalid_connection_authority");
        return authority;
    }
    private static void Write(Utf8JsonWriter writer,string name,string? value){if(value is null)writer.WriteNull(name);else writer.WriteString(name,value);}
    private static void ValidateBody(string? body){if(body is not null&&Encoding.UTF8.GetByteCount(body)>OperationRequest.MaxBodyBytes)throw new JsonException("body_too_large");}
}
