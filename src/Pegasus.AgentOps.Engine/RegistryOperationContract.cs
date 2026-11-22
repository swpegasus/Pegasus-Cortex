using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Windows.Registry;

namespace Pegasus.AgentOps.Engine;

/// <summary>Closed, local owner-host registry request contract. Parsing never opens the registry.</summary>
public static class RegistryOperationContract
{
    public const int Version = 1;
    public const int MaxRequestBytes = 96 * 1024;
    public const int MaxJsonDepth = 12;
    private static readonly RegistryOperations Validator = new(new NoRegistryAccess());
    private static readonly IReadOnlyDictionary<string, RegistryOperation> Operations = new Dictionary<string, RegistryOperation>(StringComparer.Ordinal)
    {
        ["inspect"] = RegistryOperation.InspectKey, ["browse"] = RegistryOperation.Browse, ["read"] = RegistryOperation.ReadValue,
        ["create"] = RegistryOperation.CreateKey, ["set"] = RegistryOperation.SetValue,
        ["delete_value"] = RegistryOperation.DeleteValue, ["delete_empty_key"] = RegistryOperation.DeleteEmptyKey
    };
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(Operations.Keys.ToArray());

    public static bool IsReadOnly(string? action) => action is "inspect" or "browse" or "read";
    public static bool IsKnownAction(string? action) => action is not null && Operations.ContainsKey(action);
    public static string PublicName(string action) => IsKnownAction(action) ? "registry_" + action : throw Invalid("op");

    public static RegistryInvocation Parse(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != "registry" || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 }) throw Invalid("context");
        if (request.Arg is null || request.Arg.Keys.Any(key => key is not ("op" or "rid") && key != OperationRequest.BodyOmittedKey)) throw Invalid("arguments");
        if (!request.Arg.TryGetValue("op", out var action) || !IsKnownAction(action)) throw Invalid("op");
        if (request.Arg.TryGetValue(OperationRequest.BodyOmittedKey, out var omitted) && (omitted != "1" || request.Body is null))
            throw Invalid("body");
        if (request.Arg.TryGetValue("rid", out var duplicateRid) && duplicateRid != request.Rid) throw Invalid("rid");
        if (request.Rid is not null && !ValidRid(request.Rid) || !IsReadOnly(action) && request.Rid is null) throw Invalid("rid");
        return Parse(action, request.Body ?? throw Invalid("body"));
    }

    public static RegistryInvocation Parse(string action, string body)
    {
        if (action is null || !Operations.TryGetValue(action, out var operation)) throw Invalid("op");
        if (body is null || body.Length > MaxRequestBytes || Encoding.UTF8.GetByteCount(body) > MaxRequestBytes) throw Invalid("body");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            var fields = Object(document.RootElement, Fields(operation), RequiredFields(operation));
            if (Integer(fields["version"], "version") != Version) throw Invalid("version");
            var key = Key(fields["key"]);
            var identity = operation is RegistryOperation.ReadValue or RegistryOperation.SetValue or RegistryOperation.DeleteValue
                ? new RegistryValueIdentity(key, String(fields["valueName"], "valueName")) : null;
            var parsed = new RegistryInvocation(action, operation, key)
            {
                ValueIdentity = identity,
                Browse = operation == RegistryOperation.Browse ? new(key, BrowseKind(fields["kind"]),
                    fields.TryGetValue("limit", out var limit) ? Integer(limit, "limit") : 64,
                    fields.TryGetValue("cursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null ? Cursor(cursor) : null) : null,
                Create = operation == RegistryOperation.CreateKey ? new(key, String(fields["parentGeneration"], "parentGeneration")) : null,
                Set = operation == RegistryOperation.SetValue ? new(identity!, Value(fields["value"]), Preimage(fields["preimage"])) : null,
                DeleteValue = operation == RegistryOperation.DeleteValue ? new(identity!, Preimage(fields["preimage"])) : null,
                DeleteKey = operation == RegistryOperation.DeleteEmptyKey ? new(key, String(fields["keyGeneration"], "keyGeneration")) : null
            };
            // Reuse the module's complete identity/kind/size/guard validation with
            // its default-deny admission. Its native boundary is never reached.
            var valid = operation switch
            {
                RegistryOperation.InspectKey => Validator.InspectKey(key).Status,
                RegistryOperation.ReadValue => Validator.ReadValue(identity!).Status,
                RegistryOperation.Browse => Validator.Browse(parsed.Browse!).Status,
                RegistryOperation.CreateKey => Validator.CreateKey(parsed.Create!).Status,
                RegistryOperation.SetValue => Validator.SetValue(parsed.Set!).Status,
                RegistryOperation.DeleteValue => Validator.DeleteValue(parsed.DeleteValue!).Status,
                RegistryOperation.DeleteEmptyKey => Validator.DeleteEmptyKey(parsed.DeleteKey!).Status,
                _ => RegistryStatus.InvalidRequest
            };
            if (valid != RegistryStatus.Denied) throw Invalid("body");
            parsed.SetIdentities();
            return parsed;
        }
        catch (RegistryContractException) { throw; }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid("body"); }
    }

    private static bool ValidRid(string rid) => rid.Length is > 0 and <= 128 && !string.IsNullOrWhiteSpace(rid) &&
        rid.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static string[] Fields(RegistryOperation operation) => operation switch
    {
        RegistryOperation.InspectKey => ["version", "key"],
        RegistryOperation.Browse => ["version", "key", "kind", "limit", "cursor"],
        RegistryOperation.ReadValue => ["version", "key", "valueName"],
        RegistryOperation.CreateKey => ["version", "key", "parentGeneration"],
        RegistryOperation.SetValue => ["version", "key", "valueName", "value", "preimage"],
        RegistryOperation.DeleteValue => ["version", "key", "valueName", "preimage"],
        RegistryOperation.DeleteEmptyKey => ["version", "key", "keyGeneration"],
        _ => throw Invalid("op")
    };

    private static string[] RequiredFields(RegistryOperation operation) => operation == RegistryOperation.Browse
        ? ["version", "key", "kind"] : Fields(operation);

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] allowed, string[]? required = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("body");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value)) throw Invalid("body");
        if ((required ?? allowed).Any(name => !result.ContainsKey(name))) throw Invalid("body");
        return result;
    }

    private static RegistryKeyIdentity Key(JsonElement element)
    {
        var fields = Object(element, ["hive", "view", "keyPath"]);
        var hive = String(fields["hive"], "hive") switch
        {
            "current_user" => RegistryHiveId.CurrentUser, "local_machine" => RegistryHiveId.LocalMachine, "users" => RegistryHiveId.Users,
            _ => throw Invalid("hive")
        };
        var view = Integer(fields["view"], "view") switch
        {
            32 => RegistryViewId.Registry32, 64 => RegistryViewId.Registry64, _ => throw Invalid("view")
        };
        return new(hive, view, String(fields["keyPath"], "keyPath"));
    }

    private static RegistryBrowseKind BrowseKind(JsonElement element) => String(element, "kind") switch
    {
        "keys" => RegistryBrowseKind.Keys, "values" => RegistryBrowseKind.Values, _ => throw Invalid("kind")
    };

    private static RegistryBrowseCursor Cursor(JsonElement element)
    {
        var fields = Object(element, ["version", "keyFingerprint", "kind", "generation", "nextIndex"]);
        // Continuations are the module's unchanged typed cursor, whose enum
        // serializes as 1/2. Accept only those exact values so a returned cursor
        // can be copied without converting it into a different contract.
        var kind = Integer(fields["kind"], "cursor.kind") switch
        {
            (int)RegistryBrowseKind.Keys => RegistryBrowseKind.Keys,
            (int)RegistryBrowseKind.Values => RegistryBrowseKind.Values,
            _ => throw Invalid("cursor.kind")
        };
        return new(Integer(fields["version"], "cursor.version"), String(fields["keyFingerprint"], "cursor.keyFingerprint"),
            kind, String(fields["generation"], "cursor.generation"), Integer(fields["nextIndex"], "cursor.nextIndex"));
    }

    private static RegistryValueData Value(JsonElement element)
    {
        var fields = Object(element, ["type", "base64"]);
        var type = String(fields["type"], "value.type") switch
        {
            "none" => RegistryValueType.None, "string" => RegistryValueType.String, "expand_string" => RegistryValueType.ExpandString,
            "binary" => RegistryValueType.Binary, "dword" => RegistryValueType.DWord, "dword_big_endian" => RegistryValueType.DWordBigEndian,
            "multi_string" => RegistryValueType.MultiString, "qword" => RegistryValueType.QWord, _ => throw Invalid("value.type")
        };
        return new(type, String(fields["base64"], "value.base64"));
    }

    private static RegistryValuePreimage Preimage(JsonElement element)
    {
        var fields = Object(element, ["keyGeneration", "valueDigest"]);
        return new(String(fields["keyGeneration"], "preimage.keyGeneration"), fields["valueDigest"].ValueKind == JsonValueKind.Null
            ? null : String(fields["valueDigest"], "preimage.valueDigest"));
    }

    private static string String(JsonElement element, string field) => element.ValueKind == JsonValueKind.String
        ? element.GetString()! : throw Invalid(field);
    private static int Integer(JsonElement element, string field) => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
        ? value : throw Invalid(field);
    private static RegistryContractException Invalid(string field) => new(field);

    private sealed class NoRegistryAccess : IRegistryNative
    {
        private static RegistryNativeException Refused() => new(RegistryNativeFailure.AccessDenied, "registry_validation_only");
        public RegistryNativeKeyInfo? InspectKey(RegistryKeyIdentity key) => throw Refused();
        public RegistryNativeValue? ReadValue(RegistryValueIdentity identity, int maxBytes) => throw Refused();
        public RegistryBrowseEntry? ReadName(RegistryKeyIdentity key, RegistryBrowseKind kind, int index, int maxNameChars) => throw Refused();
        public bool CreateKey(RegistryKeyIdentity key) => throw Refused();
        public void SetValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data) => throw Refused();
        public void DeleteValue(RegistryValueIdentity identity) => throw Refused();
        public void DeleteEmptyKey(RegistryKeyIdentity key) => throw Refused();
    }
}

public sealed class RegistryContractException(string field) : ArgumentException("registry_request_invalid")
{
    public string Field { get; } = field;
}

public sealed class RegistryInvocation
{
    internal RegistryInvocation(string action, RegistryOperation operation, RegistryKeyIdentity key)
    { Action = action; Operation = operation; Key = key; }
    public string Action { get; }
    public RegistryOperation Operation { get; }
    public RegistryKeyIdentity Key { get; }
    public bool IsMutation => !RegistryOperationContract.IsReadOnly(Action);
    public string RequestIdentity { get; private set; } = "";
    public string ResourceIdentity { get; private set; } = "";
    internal RegistryValueIdentity? ValueIdentity { get; init; }
    internal RegistryBrowseRequest? Browse { get; init; }
    internal RegistryCreateKeyRequest? Create { get; init; }
    internal RegistrySetValueRequest? Set { get; init; }
    internal RegistryDeleteValueRequest? DeleteValue { get; init; }
    internal RegistryDeleteEmptyKeyRequest? DeleteKey { get; init; }

    internal void SetIdentities()
    {
        // Canonical typed properties, never caller JSON order, define RID
        // identity. Exact names and raw value bytes are not normalized.
        RequestIdentity = Hash(new { Version = RegistryOperationContract.Version, Action, Key, ValueIdentity, Browse, Create, Set, DeleteValue, DeleteKey });
        ResourceIdentity = Hash(new { Version = RegistryOperationContract.Version, Key, ValueName = ValueIdentity?.ValueName });
    }
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public override string ToString() => $"RegistryInvocation(Action={Action}, Content=redacted)";
}
