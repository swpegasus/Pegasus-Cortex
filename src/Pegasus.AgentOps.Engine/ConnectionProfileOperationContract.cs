using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>Nonsecret v1 profile coordinates only; parsing never resolves credentials or connects.</summary>
public static class ConnectionProfileOperationContract
{
    public const int Version = 1, MaximumBodyBytes = 8192;
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[]
        { "list", "read", "save", "remove", "inspect", "connect", "disconnect" });
    public static bool IsKnownAction(string? action) => action is not null && Actions.Contains(action, StringComparer.Ordinal);
    public static bool IsReadOnly(string? action) => action is "list" or "read" or "inspect";
    public static string PublicName(string action) => IsKnownAction(action) ? "connection_profile_" + action : throw Invalid("op");

    public static ConnectionProfileInvocation Parse(OperationRequest request)
    {
        if (request.Op != "connection_profile" || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } || request.Arg.Keys.Any(key => key is not ("op" or "rid" or "wait"))) throw Invalid("context");
        var action = request.Arg.GetValueOrDefault("op");
        if (!IsKnownAction(action)) throw Invalid("op");
        if (request.Rid is { } rid && (rid.Length is < 1 or > 128 || string.IsNullOrWhiteSpace(rid) || rid.Any(char.IsControl)) ||
            !IsReadOnly(action) && request.Rid is null ||
            request.Arg.TryGetValue("rid", out var duplicated) && duplicated != request.Rid) throw Invalid("rid");
        if (request.Arg.TryGetValue("wait", out var wait) && (IsReadOnly(action) || wait is not ("true" or "false"))) throw Invalid("wait");
        return Parse(action!, request.Body ?? throw Invalid("text"));
    }

    public static ConnectionProfileInvocation Parse(string action, string body)
    {
        if (!IsKnownAction(action)) throw Invalid("op");
        if (body is null || body.Length > MaximumBodyBytes || Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes) throw Invalid("text");
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 6 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("text");
            string[] allowed = action switch
            {
                "list" => ["version", "kind", "afterId", "limit"],
                "read" => ["version", "kind", "id"],
                "save" => ["version", "kind", "profile", "expectedRevision"],
                "remove" => ["version", "kind", "id", "expectedRevision"],
                "disconnect" => ["version", "kind", "id", "revision", "contentHash", "expectedRemoteShare"],
                _ => ["version", "kind", "id", "revision", "contentHash"]
            };
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in json.RootElement.EnumerateObject())
                if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value)) throw Invalid("text");
            string Text(string field) => fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw Invalid(field);
            long Number(string field) => fields.TryGetValue(field, out var value) && value.TryGetInt64(out var number)
                ? number : throw Invalid(field);
            if (Number("version") != Version || Text("kind") != "smb-drive") throw Invalid("version");
            string? id = null, after = null, hash = null, expectedRemote = null;
            var limit = 32;
            long revision = 0;
            SmbConnectionProfile? profile = null;
            if (action == "list")
            {
                if (fields.ContainsKey("afterId")) { after = Text("afterId"); SmbConnectionProfile.RequireReference(after); }
                if (fields.ContainsKey("limit")) limit = checked((int)Number("limit"));
                if (limit is < 1 or > 64) throw Invalid("limit");
            }
            else if (action == "save")
            {
                if (!fields.TryGetValue("profile", out var value)) throw Invalid("profile");
                profile = SmbConnectionProfile.Parse(value);
                id = profile.Id;
                revision = Number("expectedRevision");
                if (revision is < 0 or long.MaxValue) throw Invalid("expectedRevision");
            }
            else
            {
                id = Text("id"); SmbConnectionProfile.RequireReference(id);
                if (action != "read")
                {
                    revision = Number(action == "remove" ? "expectedRevision" : "revision");
                    if (revision is < 1 or long.MaxValue) throw Invalid("revision");
                }
                if (action is "inspect" or "connect" or "disconnect")
                {
                    hash = Text("contentHash");
                    if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid("contentHash");
                }
                if (action == "disconnect")
                { expectedRemote = Text("expectedRemoteShare"); SmbConnectionProfile.RequireRemoteShare(expectedRemote); }
            }
            return new(action, id, revision, hash, after, limit, expectedRemote, profile);
        }
        catch (ConnectionProfileContractException) { throw; }
        catch (Exception error) when (error is JsonException or SmbConnectionException or ArgumentException or InvalidOperationException or OverflowException)
        { throw Invalid("text"); }
    }

    private static ConnectionProfileContractException Invalid(string field) => new(field);
}

public sealed class ConnectionProfileContractException(string field) : ArgumentException("connection_profile_request_invalid")
{ public string Field { get; } = field; }

public sealed class ConnectionProfileInvocation
{
    internal ConnectionProfileInvocation(string action, string? id, long revision, string? hash, string? after, int limit,
        string? expectedRemote, SmbConnectionProfile? profile)
    {
        Action = action; Id = id; Revision = revision; ContentHash = hash; AfterId = after; Limit = limit;
        ExpectedRemoteShare = expectedRemote; Profile = profile;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("kind", "smb-drive");
            writer.WriteString("action", action); writer.WriteString("id", id); writer.WriteNumber("revision", revision);
            writer.WriteString("contentHash", hash); writer.WriteString("afterId", after); writer.WriteNumber("limit", limit);
            writer.WriteString("expectedRemoteShare", expectedRemote); writer.WritePropertyName("profile");
            JsonSerializer.Serialize(writer, profile, SmbConnectionProfile.Json); writer.WriteEndObject();
        }
        RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
    public string Action { get; }
    public string? Id { get; }
    public long Revision { get; }
    public string? ContentHash { get; }
    public string? AfterId { get; }
    public int Limit { get; }
    public string? ExpectedRemoteShare { get; }
    public bool IsMutation => !ConnectionProfileOperationContract.IsReadOnly(Action);
    public string RequestIdentity { get; }
    internal SmbConnectionProfile? Profile { get; }
}
