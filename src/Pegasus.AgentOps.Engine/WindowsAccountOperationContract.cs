using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>Fixed ordinary-owner local Windows identity inventory, never account administration.</summary>
public static class WindowsAccountOperationContract
{
    public const int Version = 1, MaximumBodyBytes = 4096, MaximumPageEntries = 64;
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[] { "list", "read" });
    public static IReadOnlyList<string> Views { get; } = Array.AsReadOnly(new[] { "users", "groups", "members", "sessions", "current" });
    public static bool IsKnownAction(string? action) => action is "list" or "read";
    public static bool IsReadOnly(string? action) => IsKnownAction(action);
    public static string PublicName(string action) => IsKnownAction(action) ? "windows_account_" + action : throw Invalid("op");
    public static WindowsAccountInvocation Parse(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != "windows_account" || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } || request.Arg is null || request.Arg.Keys.Any(key => key is not ("op" or "rid")))
            throw Invalid("context");
        if (!request.Arg.TryGetValue("op", out var action) || !IsKnownAction(action)) throw Invalid("op");
        if (request.Rid is not null && !Rid(request.Rid) ||
            request.Arg.TryGetValue("rid", out var duplicate) && duplicate != request.Rid) throw Invalid("rid");
        return Parse(action, request.Body ?? throw Invalid("body"));
    }
    public static WindowsAccountInvocation Parse(string action, string body)
    {
        if (!IsKnownAction(action)) throw Invalid("op");
        if (body is null || body.Length > MaximumBodyBytes || !WellFormed(body) || Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes)
            throw Invalid("body");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 3 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("body");
            string[] allowed = action == "list" ? ["version", "view", "group", "limit", "afterKey"] : ["version", "view", "group", "key"];
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("body");
            if (!fields.TryGetValue("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != Version) throw Invalid("version");
            string Text(string field) => fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw Invalid(field);
            var view = Text("view");
            if (!Views.Contains(view, StringComparer.Ordinal)) throw Invalid("view");
            string? group = null, after = null, key = null;
            if (view == "members") { group = Text("group"); RequireGroup(group); }
            else if (fields.ContainsKey("group")) throw Invalid("group"); // Presence, even null, is a selector.
            var limit = 32;
            if (action == "list")
            {
                if (fields.TryGetValue("limit", out var count) &&
                    (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out limit) || limit is < 1 or > MaximumPageEntries))
                    throw Invalid("limit");
                if (fields.ContainsKey("afterKey")) { after = Text("afterKey"); if (!IsKey(after)) throw Invalid("afterKey"); }
            }
            else { key = Text("key"); if (!IsKey(key)) throw Invalid("key"); }
            return new(action, view, group, limit, after, key);
        }
        catch (WindowsAccountContractException) { throw; }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw Invalid("body"); }
    }
    public static bool IsKey(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void RequireGroup(string value)
    {
        if (value is null || value.Length is < 1 or > 256 || !WellFormed(value) || string.IsNullOrWhiteSpace(value) ||
            value != value.Trim() || value is "." or ".." || value.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"'))
            throw Invalid("group");
    }
    internal static bool WellFormed(string value)
    { try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; } catch (EncoderFallbackException) { return false; } }
    private static bool Rid(string value) => value.Length is >= 1 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    private static WindowsAccountContractException Invalid(string field) => new(field);
}
public sealed class WindowsAccountContractException(string field) : ArgumentException("windows_account_request_invalid")
{ public string Field { get; } = field; }
public sealed class WindowsAccountInvocation
{
    internal WindowsAccountInvocation(string action, string view, string? group, int limit, string? afterKey, string? key)
    {
        Action = action; View = view; Group = group; Limit = limit; AfterKey = afterKey; Key = key;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, action, view, group, limit, afterKey, key });
        try { RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Action { get; }
    public string View { get; }
    public string? Group { get; }
    public int Limit { get; }
    public string? AfterKey { get; }
    public string? Key { get; }
    public bool IsMutation => false;
    public string RequestIdentity { get; }
    public override string ToString() => "WindowsAccountInvocation(Action=" + Action + ", View=" + View + ", Content=omitted)";
}
