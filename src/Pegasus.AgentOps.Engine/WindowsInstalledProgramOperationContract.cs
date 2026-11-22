using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Windows.Registry;

namespace Pegasus.AgentOps.Engine;

/// <summary>Fixed local Uninstall-registration observations, not product verification or installation.</summary>
public static class WindowsInstalledProgramOperationContract
{
    public const int Version = 1;
    public const int MaximumBodyBytes = 8192;
    public const int MaximumPageEntries = 64;
    internal const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[] { "list", "read" });
    public static IReadOnlyList<string> Sources { get; } = Array.AsReadOnly(new[] { "machine32", "machine64", "owner32", "owner64" });

    public static bool IsKnownAction(string? action) => action is "list" or "read";
    public static bool IsReadOnly(string? action) => IsKnownAction(action);
    public static string PublicName(string action) => IsKnownAction(action) ? "installed_program_" + action : throw Invalid("op");

    public static WindowsInstalledProgramInvocation Parse(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != "installed_program" || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 }) throw Invalid("context");
        if (request.Arg is null || request.Arg.Keys.Any(key => key is not ("op" or "rid"))) throw Invalid("arguments");
        if (!request.Arg.TryGetValue("op", out var action) || !IsKnownAction(action)) throw Invalid("op");
        if (request.Rid is not null && !ValidRid(request.Rid) ||
            request.Arg.TryGetValue("rid", out var duplicateRid) && duplicateRid != request.Rid) throw Invalid("rid");
        return Parse(action, request.Body ?? throw Invalid("body"));
    }

    public static WindowsInstalledProgramInvocation Parse(string action, string body)
    {
        if (!IsKnownAction(action)) throw Invalid("op");
        if (body is null || body.Length > MaximumBodyBytes || Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes) throw Invalid("body");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            string[] allowed = action == "list" ? ["version", "source", "limit", "cursor"] : ["version", "source", "key", "generation"];
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("body");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("body");
            if (!fields.TryGetValue("version", out var version) || Integer(version, "version") != Version) throw Invalid("version");
            if (!fields.TryGetValue("source", out var sourceElement)) throw Invalid("source");
            var source = String(sourceElement, "source");
            var root = RootForSource(source);
            if (action == "list")
            {
                var limit = fields.TryGetValue("limit", out var limitElement) ? Integer(limitElement, "limit") : 32;
                if (limit is < 1 or > MaximumPageEntries) throw Invalid("limit");
                // The existing registry contract validates the complete typed cursor,
                // fixed root/view fingerprint, generation, position and name bounds.
                // Its validation-only native boundary never opens the registry.
                var browse = RegistryOperationContract.Parse("browse", JsonSerializer.Serialize(new
                {
                    version = Version, key = RegistryKey(root), kind = "keys", limit,
                    cursor = fields.TryGetValue("cursor", out var cursor) ? (JsonElement?)cursor : null
                })).Browse!;
                return new(action, source, root, null, null, browse);
            }
            if (!fields.TryGetValue("key", out var keyElement)) throw Invalid("key");
            var keyName = String(keyElement, "key");
            if (keyName.Length is < 1 or > RegistryLimits.MaxKeySegmentChars || keyName is "." or ".." ||
                keyName.Contains('\\') || keyName.Contains('/')) throw Invalid("key");
            var target = root with { KeyPath = root.KeyPath + "\\" + keyName };
            _ = RegistryOperationContract.Parse("inspect", JsonSerializer.Serialize(new { version = Version, key = RegistryKey(target) }));
            var generation = fields.TryGetValue("generation", out var generationElement) ? String(generationElement, "generation") : null;
            if (generation is not null && !Digest(generation)) throw Invalid("generation");
            return new(action, source, root, keyName, generation, null);
        }
        catch (WindowsInstalledProgramContractException) { throw; }
        catch (RegistryContractException failure) { throw Invalid(failure.Field); }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid("body"); }
    }

    internal static RegistryKeyIdentity RootForSource(string source) => source switch
    {
        "machine32" => new(RegistryHiveId.LocalMachine, RegistryViewId.Registry32, UninstallKeyPath),
        "machine64" => new(RegistryHiveId.LocalMachine, RegistryViewId.Registry64, UninstallKeyPath),
        "owner32" => new(RegistryHiveId.CurrentUser, RegistryViewId.Registry32, UninstallKeyPath),
        "owner64" => new(RegistryHiveId.CurrentUser, RegistryViewId.Registry64, UninstallKeyPath),
        _ => throw Invalid("source")
    };

    private static object RegistryKey(RegistryKeyIdentity key) => new
    { hive = key.Hive == RegistryHiveId.LocalMachine ? "local_machine" : "current_user", view = (int)key.View, keyPath = key.KeyPath };
    private static bool ValidRid(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    private static bool Digest(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string String(JsonElement value, string field) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(field);
    private static int Integer(JsonElement value, string field) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Invalid(field);
    private static WindowsInstalledProgramContractException Invalid(string field) => new(field);
}

public sealed class WindowsInstalledProgramContractException(string field) : ArgumentException("installed_program_request_invalid")
{
    public string Field { get; } = field;
}

public sealed class WindowsInstalledProgramInvocation
{
    internal WindowsInstalledProgramInvocation(string action, string source, RegistryKeyIdentity root, string? key,
        string? generation, RegistryBrowseRequest? browse)
    {
        Action = action; Source = source; Root = root; Key = key; Generation = generation; Browse = browse;
        RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { version = 1, action, source, key, generation, limit = browse?.Limit, cursor = browse?.Cursor })));
    }
    public string Action { get; }
    public string Source { get; }
    public string? Key { get; }
    public string? Generation { get; }
    public int Limit => Browse?.Limit ?? 1;
    public RegistryBrowseCursor? Cursor => Browse?.Cursor;
    public string RequestIdentity { get; }
    public bool IsMutation => false;
    internal RegistryKeyIdentity Root { get; }
    internal RegistryBrowseRequest? Browse { get; }
    public override string ToString() => $"WindowsInstalledProgramInvocation(Action={Action}, Source={Source}, Content=omitted)";
}
