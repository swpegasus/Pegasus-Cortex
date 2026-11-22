using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.AgentOps.Engine;

// Nonsecret configuration only. The fixed provider kind is never caller-selected.
internal sealed record SmbConnectionProfile(int Version, string Id, string Label, string RemoteShare,
    string LocalDrive, string IdentityMode, string? RemoteUser = null, string? PasswordReference = null)
{
    public string Kind => "smb-drive";
    internal const int MaximumEncodedBytes = 4096;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 4, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static SmbConnectionProfile Parse(JsonElement value)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object) throw Invalid();
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string[] allowed = ["version", "kind", "id", "label", "remoteShare", "localDrive", "identityMode", "remoteUser", "passwordReference"];
            foreach (var property in value.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                    throw Invalid();
            if (!fields.TryGetValue("version", out var version) || !version.TryGetInt32(out var number) ||
                Required("kind") != "smb-drive") throw Invalid();
            var mode = Required("identityMode");
            if (mode == "currentUser" && (fields.ContainsKey("remoteUser") || fields.ContainsKey("passwordReference"))) throw Invalid();
            return Validate(new(number, Required("id"), Required("label"), Required("remoteShare"),
                Required("localDrive"), mode, Optional("remoteUser"), Optional("passwordReference")));
            string Required(string name) => fields.TryGetValue(name, out var item) && item.ValueKind == JsonValueKind.String
                ? item.GetString()! : throw Invalid();
            string? Optional(string name) => !fields.TryGetValue(name, out var item) || item.ValueKind == JsonValueKind.Null
                ? null : item.ValueKind == JsonValueKind.String ? item.GetString() : throw Invalid();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    internal static SmbConnectionProfile Validate(SmbConnectionProfile value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireReference(value.Id);
        if (value.Version != 1 || !Text(value.Label, 1, 80) || value.Label != value.Label.Trim() ||
            value.LocalDrive is not { Length: 2 } || value.LocalDrive[0] is < 'A' or > 'Z' || value.LocalDrive[1] != ':' ||
            value.IdentityMode is not ("currentUser" or "explicitRemote")) throw Invalid();
        RequireRemoteShare(value.RemoteShare);
        if (value.IdentityMode == "currentUser")
        {
            if (value.RemoteUser is not null || value.PasswordReference is not null) throw Invalid();
        }
        else
        {
            if (!Text(value.RemoteUser, 1, 256) || value.RemoteUser != value.RemoteUser!.Trim()) throw Invalid();
            RequireReference(value.PasswordReference!);
        }
        if (JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > MaximumEncodedBytes) throw Invalid();
        return value;
    }

    internal static void RequireReference(string value)
    {
        if (value is not { Length: >= 1 and <= 64 } || value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            value.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))) throw Invalid();
    }

    internal static void RequireRemoteShare(string value)
    {
        if (value is not { Length: >= 5 and <= 240 } || !value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.EndsWith('\\') || !Text(value, 5, 240)) throw Invalid();
        var parts = value[2..].Split('\\');
        if (parts.Length != 2 || parts[0].Length is < 1 or > 253 || parts[1].Length is < 1 or > 80 ||
            parts[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            parts[0] is "." or ".." || parts[0].StartsWith('.') || parts[0].EndsWith('.') ||
            parts[1] is "." or ".." || parts[1].EndsWith('.') || parts[1].EndsWith(' ') ||
            parts[1].Any(c => c is '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')) throw Invalid();
    }

    private static bool Text(string? value, int minimum, int maximum)
    {
        if (value is null || value.Length < minimum || value.Length > maximum || value.Any(char.IsControl)) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    private static SmbConnectionException Invalid() => new("smb_profile_invalid");
}

internal sealed record SmbConnectionProfileSnapshot(SmbConnectionProfile Profile, long Revision, string ContentHash);
internal sealed record SmbProfileStoreResult(bool Ok, string State, string? Error = null,
    SmbConnectionProfileSnapshot? Snapshot = null, bool ActionIssued = false, bool OutcomeUnknown = false, long Revision = 0, int? NativeError = null);
internal sealed class SmbConnectionException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed record SmbProfileListResult(bool Ok, string State, IReadOnlyList<SmbConnectionProfileSnapshot> Profiles,
    string? NextId = null, string? Error = null);
