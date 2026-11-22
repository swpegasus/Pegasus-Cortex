using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>Closed passive observations of fixed local network views; never probes or configuration.</summary>
public static class WindowsNetworkOperationContract
{
    public const int Version = 1, MaximumBodyBytes = 4096, MaximumPageEntries = 64;
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[] { "list", "read" });
    public static IReadOnlyList<string> Views { get; } = Array.AsReadOnly(new[] { "adapters", "routes", "connections" });
    public static bool IsKnownAction(string? action) => action is "list" or "read";
    public static bool IsReadOnly(string? action) => IsKnownAction(action);
    public static string PublicName(string action) => IsKnownAction(action) ? "windows_network_" + action : throw Invalid("op");

    public static WindowsNetworkInvocation Parse(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != "windows_network" || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } || request.Arg is null || request.Arg.Keys.Any(key => key is not ("op" or "rid"))) throw Invalid("context");
        if (!request.Arg.TryGetValue("op", out var action) || !IsKnownAction(action)) throw Invalid("op");
        if (request.Rid is not null && !ValidRid(request.Rid) || request.Arg.TryGetValue("rid", out var duplicate) && duplicate != request.Rid) throw Invalid("rid");
        return Parse(action, request.Body ?? throw Invalid("body"));
    }
    public static WindowsNetworkInvocation Parse(string action, string body)
    {
        if (!IsKnownAction(action)) throw Invalid("op");
        if (body is null || body.Length > MaximumBodyBytes || Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes) throw Invalid("body");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 3 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("body");
            string[] allowed = action == "list" ? ["version", "view", "limit", "afterKey"] : ["version", "view", "key"];
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("body");
            if (!fields.TryGetValue("version", out var version) || !version.TryGetInt32(out var number) || number != Version) throw Invalid("version");
            string Text(string field) => fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(field);
            var view = Text("view"); if (!Views.Contains(view, StringComparer.Ordinal)) throw Invalid("view");
            string? after = null, key = null; var limit = 32;
            if (action == "list")
            {
                if (fields.TryGetValue("limit", out var count) && (!count.TryGetInt32(out limit) || limit is < 1 or > MaximumPageEntries)) throw Invalid("limit");
                if (fields.ContainsKey("afterKey")) { after = Text("afterKey"); if (!IsKey(after)) throw Invalid("afterKey"); }
            }
            else { key = Text("key"); if (!IsKey(key)) throw Invalid("key"); }
            return new(action, view, limit, after, key);
        }
        catch (WindowsNetworkContractException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw Invalid("body"); }
    }
    internal static bool IsKey(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidRid(string value) => value.Length is > 0 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    private static WindowsNetworkContractException Invalid(string field) => new(field);
}

public sealed class WindowsNetworkContractException(string field) : ArgumentException("windows_network_request_invalid")
{ public string Field { get; } = field; }

public sealed class WindowsNetworkInvocation
{
    internal WindowsNetworkInvocation(string action, string view, int limit, string? afterKey, string? key)
    {
        Action = action; View = view; Limit = limit; AfterKey = afterKey; Key = key;
        RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, action, view, limit, afterKey, key })));
    }
    public string Action { get; }
    public string View { get; }
    public int Limit { get; }
    public string? AfterKey { get; }
    public string? Key { get; }
    public bool IsMutation => false;
    public string RequestIdentity { get; }
    public override string ToString() => $"WindowsNetworkInvocation(Action={Action}, View={View}, Content=omitted)";
}
