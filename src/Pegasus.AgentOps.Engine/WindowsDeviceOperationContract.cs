using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>Fixed local device/registered-driver observations, never device administration.</summary>
public static class WindowsDeviceOperationContract
{
    public const int Version = 1, MaximumBodyBytes = 4096, MaximumPageEntries = 64;
    public const int MaximumInstanceIdCharacters = 199, MaximumScannedEntries = 4096;
    public const int MaximumPageBytes = 256 * 1024;
    internal const int PageEnvelopeReserveBytes = 4096;
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[] { "list", "read" });
    public static IReadOnlyList<string> Scopes { get; } = Array.AsReadOnly(new[] { "present", "all" });
    public static bool IsKnownAction(string? action) => action is "list" or "read";
    public static bool IsReadOnly(string? action) => IsKnownAction(action);
    public static string PublicName(string action) => IsKnownAction(action) ? "windows_device_" + action : throw Invalid("op");

    public static WindowsDeviceInvocation Parse(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != "windows_device" || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 }) throw Invalid("context");
        if (request.Arg is null || request.Arg.Keys.Any(key => key is not ("op" or "rid"))) throw Invalid("arguments");
        if (!request.Arg.TryGetValue("op", out var action) || !IsKnownAction(action)) throw Invalid("op");
        if (request.Rid is not null && !ValidRid(request.Rid) ||
            request.Arg.TryGetValue("rid", out var duplicate) && duplicate != request.Rid) throw Invalid("rid");
        return Parse(action, request.Body ?? throw Invalid("body"));
    }

    public static WindowsDeviceInvocation Parse(string action, string body)
    {
        if (!IsKnownAction(action)) throw Invalid("op");
        if (body is null || body.Length > MaximumBodyBytes || Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes) throw Invalid("body");
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("body");
            string[] allowed = action == "list" ? ["version", "scope", "limit", "afterInstanceId"] : ["version", "instanceId"];
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("body");
            string Text(string field) => fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw Invalid(field);
            if (!fields.TryGetValue("version", out var version) || !version.TryGetInt32(out var number) || number != Version) throw Invalid("version");
            if (action == "read")
            {
                var id = Text("instanceId"); RequireInstanceId(id);
                return new(action, "exact", 1, null, id);
            }
            var scope = fields.ContainsKey("scope") ? Text("scope") : "present";
            if (scope is not ("present" or "all")) throw Invalid("scope");
            var limit = 32;
            if (fields.TryGetValue("limit", out var maximum) && !maximum.TryGetInt32(out limit)) throw Invalid("limit");
            if (limit is < 1 or > MaximumPageEntries) throw Invalid("limit");
            var after = fields.ContainsKey("afterInstanceId") ? Text("afterInstanceId") : null;
            if (after is not null) RequireInstanceId(after);
            return new(action, scope, limit, after, null);
        }
        catch (WindowsDeviceContractException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid("body"); }
    }

    internal static void RequireInstanceId(string value)
    {
        // Microsoft requires opaque string comparison, not path/ID parsing.
        // No trimming, expansion, device opening or wildcard interpretation.
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumInstanceIdCharacters || value.Any(char.IsControl)) throw Invalid("instanceId");
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || ++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Invalid("instanceId");
        }
    }
    private static bool ValidRid(string value) => value.Length is > 0 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    private static WindowsDeviceContractException Invalid(string field) => new(field);
}

public sealed class WindowsDeviceContractException(string field) : ArgumentException("windows_device_request_invalid")
{ public string Field { get; } = field; }

public sealed class WindowsDeviceInvocation
{
    internal WindowsDeviceInvocation(string action, string scope, int limit, string? after, string? id)
    {
        Action = action; Scope = scope; Limit = limit; AfterInstanceId = after; InstanceId = id;
        RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { version = 1, action, scope, limit, afterInstanceId = after, instanceId = id })));
    }
    public string Action { get; }
    public string Scope { get; }
    public int Limit { get; }
    public string? AfterInstanceId { get; }
    public string? InstanceId { get; }
    public string RequestIdentity { get; }
    public bool IsMutation => false;
    public override string ToString() => "WindowsDeviceInvocation(Action=" + Action + ", Scope=" + Scope + ", Content=omitted)";
}
