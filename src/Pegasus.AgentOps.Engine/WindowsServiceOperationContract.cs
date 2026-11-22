using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Pure closed contract. A selected action never confers SCM or elevation rights.
public static class WindowsServiceOperationContract
{
    public const int Version = 1;
    public const int MaximumBodyBytes = 8192;
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[] { "list", "read", "start", "stop", "restart", "startup" });
    public static bool IsKnownAction(string? action) => action is not null && Actions.Contains(action, StringComparer.Ordinal);
    public static bool IsReadOnly(string? action) => action is "list" or "read";
    public static bool IsOwnerElevationAction(string? action) => action is "read" or "start" or "stop" or "restart" or "startup";
    public static string PublicName(string action) => "windows_service_" + action;

    public static WindowsServiceInvocation Parse(OperationRequest request)
    {
        var action = request.Arg.GetValueOrDefault("op");
        if (request.Op != "windows_service" || !IsKnownAction(action) || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } || request.Arg.Keys.Any(key => key is not ("op" or "rid" or "elevate" or "wait")) ||
            request.Arg.TryGetValue("rid", out var rid) && rid != request.Rid)
            throw Invalid();
        var requiresOwnerElevation = request.Arg.TryGetValue("elevate", out var elevation);
        if (requiresOwnerElevation && (elevation != "uac" || !IsOwnerElevationAction(action) || request.Root is not null))
            throw Invalid("elevate");
        if (request.Arg.TryGetValue("wait", out var wait) &&
            (!requiresOwnerElevation || wait is not ("true" or "false"))) throw Invalid("wait");
        if (request.Rid is { } supplied && !ValidRid(supplied) ||
            (!IsReadOnly(action) || requiresOwnerElevation) && request.Rid is null) throw Invalid("rid");
        if (request.Body is null || Encoding.UTF8.GetByteCount(request.Body) > MaximumBodyBytes) throw Invalid("text");
        try
        {
            using var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 4 });
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object) throw Invalid("text");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var allowed = action switch
            {
                "list" => new[] { "version", "max", "after", "pattern" },
                "read" => ["version", "service"],
                "start" => ["version", "service", "configSha", "expect", "tmo"],
                "stop" or "restart" => ["version", "service", "configSha", "expect", "pid", "start", "tmo"],
                _ => ["version", "service", "configSha", "expect", "mode", "tmo"]
            };
            foreach (var property in body.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid(property.Name);
            if (!fields.TryGetValue("version", out var version) || !version.TryGetInt32(out var number) || number != Version) throw Invalid("version");
            string? service = null, config = null, expected = null, start = null, mode = null, after = null, pattern = null;
            int? pid = null;
            var maximum = 64;
            var timeout = 30;
            string Text(string field) => fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw Invalid(field);
            int Integer(string field) => fields.TryGetValue(field, out var value) && value.TryGetInt32(out var integer)
                ? integer : throw Invalid(field);
            if (action == "list")
            {
                if (fields.ContainsKey("max")) maximum = Integer("max");
                if (maximum is < 1 or > 128) throw Invalid("max");
                if (fields.ContainsKey("after")) { after = Text("after"); RequireName(after); }
                if (fields.ContainsKey("pattern"))
                {
                    pattern = Text("pattern");
                    if (pattern.Length is < 1 or > 256 || pattern.Any(char.IsControl) || !WellFormed(pattern)) throw Invalid("pattern");
                }
            }
            else { service = Text("service"); RequireName(service); }
            if (!IsReadOnly(action))
            {
                config = Text("configSha");
                if (!IsDigest(config)) throw Invalid("configSha");
                expected = Text("expect");
                if (action == "start" ? expected != "stopped" : action is "stop" or "restart" ? expected != "running" : expected is not ("running" or "stopped"))
                    throw Invalid("expect");
                if (fields.ContainsKey("tmo")) timeout = Integer("tmo");
                if (timeout is < 1 or > 120) throw Invalid("tmo");
                if (action is "stop" or "restart")
                {
                    pid = Integer("pid"); start = Text("start");
                    if (pid < 1 || !CanonicalStart(start)) throw Invalid("start");
                }
                if (action == "startup")
                {
                    mode = Text("mode");
                    if (mode is not ("manual" or "automatic" or "automatic_delayed" or "disabled")) throw Invalid("mode");
                }
            }
            return new(action!, service, config, expected, pid, start, mode, timeout, maximum, after, pattern, requiresOwnerElevation);
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid("text"); }
    }

    public static void RequireName(string value)
    {
        if (value.Length is < 1 or > 256 || value.Any(character => char.IsControl(character) || character is '/' or '\\') ||
            !WellFormed(value)) throw Invalid("service");
    }
    public static bool IsDigest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool CanonicalStart(string value) => DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var parsed) && parsed.Kind == DateTimeKind.Utc &&
        parsed.ToString("O", CultureInfo.InvariantCulture) == value;
    private static bool ValidRid(string value) => value.Length is >= 1 and <= 128 && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);
    private static bool WellFormed(string value)
    { try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; } catch (EncoderFallbackException) { return false; } }
    private static AgentOpsException Invalid(string field = "arguments") => new("invalid", inner: new ArgumentException(field));
}

public sealed class WindowsServiceInvocation
{
    public string Action { get; }
    public string? Service { get; }
    public string? ConfigSha { get; }
    public string? ExpectedState { get; }
    public int? ProcessId { get; }
    public string? ProcessStart { get; }
    public string? Mode { get; }
    public int TimeoutSeconds { get; }
    public int Maximum { get; }
    public string? After { get; }
    public string? Pattern { get; }
    public string RequestIdentity { get; }
    public bool RequiresOwnerElevation { get; }
    public bool RequiresDurableHelper => RequiresOwnerElevation;
    public bool IsMutation => !WindowsServiceOperationContract.IsReadOnly(Action);
    internal WindowsServiceInvocation(string action, string? service, string? configSha, string? expectedState,
        int? processId, string? processStart, string? mode, int timeout, int maximum, string? after, string? pattern, bool requiresOwnerElevation = false)
    {
        Action = action; Service = service; ConfigSha = configSha; ExpectedState = expectedState;
        ProcessId = processId; ProcessStart = processStart; Mode = mode; TimeoutSeconds = timeout;
        Maximum = maximum; After = after; Pattern = pattern;
        RequiresOwnerElevation = requiresOwnerElevation;
        // Preserve exact old fingerprint bytes without an explicit route selector.
        var identityBytes = requiresOwnerElevation
            ? JsonSerializer.SerializeToUtf8Bytes(new
                { family = "windows_service", version = 1, action, service, configSha, expectedState, processId, processStart, mode, timeout, maximum, after, pattern, elevate = "uac" })
            : JsonSerializer.SerializeToUtf8Bytes(new
                { family = "windows_service", version = 1, action, service, configSha, expectedState, processId, processStart, mode, timeout, maximum, after, pattern });
        try { RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(identityBytes)); }
        finally { CryptographicOperations.ZeroMemory(identityBytes); }
    }
    public override string ToString() => $"WindowsServiceInvocation(Action={Action}, Body=omitted)";
}
