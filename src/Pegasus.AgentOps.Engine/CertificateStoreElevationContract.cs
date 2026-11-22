using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// Fixed public material and receipt coordinates only. Machine scope is implicit;
// there is no executable, shell, credential, provider or caller-selected identity.
internal sealed record AdminCertificateStoreRequest(int Version, string Action, string Store,
    string CertificateId, byte[]? PublicDer, string RequestIdentity, string Jid, string Rid);

internal sealed record CertificateElevationResult(int Version, string State, string CertificateId,
    bool? ActionIssued, bool PostconditionObserved, bool OutcomeUnknown, string? Code = null, int? NativeError = null);

internal sealed class CertificateElevationContractException(string field) : ArgumentException("certificate_elevation_contract_invalid")
{
    internal string Field { get; } = field;
}

internal static class CertificateStoreElevationContract
{
    internal const int Version = 1;
    internal const int MaximumPublicDerBytes = 65536;
    internal const int MaximumPayloadBytes = 96 * 1024;
    internal const int MaximumCompletionBytes = 4096;
    internal const int MaximumDepth = 4;
    internal const string RedeemOperation = "__certificate_store_redeem";
    internal const string CompleteOperation = "__certificate_store_complete";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaximumDepth,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    internal static bool IsControlRequest(OperationRequest request) => request is not null &&
        request.Op is RedeemOperation or CompleteOperation;

    internal static string ParseTicket(OperationRequest request)
    {
        if (!IsControlRequest(request)) throw Invalid("op");
        if (request.Root is not null || request.Ws is not null || request.Cwd is not null || request.PathRoots is not null ||
            request.Quiet || request.Detail) throw Invalid("context");
        if (request.Arg is null || request.Arg.Count != 1 || request.Arg.Keys.Single() != "ticket" ||
            !request.Arg.TryGetValue("ticket", out var ticket) || !Digest(ticket)) throw Invalid("ticket");
        if (request.Rid is not null && !ValidRid(request.Rid)) throw Invalid("rid");
        if (request.Op == RedeemOperation)
        {
            if (request.Body is not null) throw Invalid("body");
        }
        else RequireJsonSize(request.Body, MaximumCompletionBytes);
        return ticket;
    }

    internal static CertificateElevationResult ParseCompletion(OperationRequest request)
    {
        _ = ParseTicket(request);
        if (request.Op != CompleteOperation) throw Invalid("op");
        try
        {
            using var document = Document(request.Body!, MaximumCompletionBytes);
            var fields = Object(document.RootElement,
                ["version", "state", "certificateId", "actionIssued", "postconditionObserved", "outcomeUnknown", "code", "nativeError"],
                ["version", "state", "certificateId", "actionIssued", "postconditionObserved", "outcomeUnknown"]);
            var result = new CertificateElevationResult(Integer(fields["version"], "version"),
                String(fields["state"], "state"), String(fields["certificateId"], "certificateId"),
                fields["actionIssued"].ValueKind == JsonValueKind.Null ? null : Boolean(fields["actionIssued"], "actionIssued"),
                Boolean(fields["postconditionObserved"], "postconditionObserved"), Boolean(fields["outcomeUnknown"], "outcomeUnknown"),
                fields.TryGetValue("code", out var code) && code.ValueKind != JsonValueKind.Null ? String(code, "code") : null,
                fields.TryGetValue("nativeError", out var native) && native.ValueKind != JsonValueKind.Null
                    ? Integer(native, "nativeError", signed: true) : null);
            ValidateResult(result, result.CertificateId);
            return result;
        }
        catch (CertificateElevationContractException) { throw; }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid("body"); }
    }

    // The ticket owner supplies this expected pin, never a body field override.
    internal static CertificateElevationResult ParseCompletion(OperationRequest request, string expectedCertificateId)
    {
        var result = ParseCompletion(request);
        ValidateResult(result, expectedCertificateId);
        return result;
    }

    internal static void ValidatePayload(AdminCertificateStoreRequest payload)
    {
        if (payload is null || payload.Version != Version) throw Invalid("version");
        if (payload.Action is not ("store_add" or "store_trust" or "store_remove")) throw Invalid("action");
        if (!Store(payload.Store)) throw Invalid("store");
        var trust = payload.Store is "root" or "trusted_publisher" or "trusted_people";
        if (payload.Action == "store_add" && trust || payload.Action == "store_trust" && !trust) throw Invalid("store");
        if (!Digest(payload.CertificateId)) throw Invalid("certificateId");
        if (!Digest(payload.RequestIdentity)) throw Invalid("requestIdentity");
        if (payload.Jid is not { Length: 32 } || !LowerHex(payload.Jid)) throw Invalid("jid");
        if (!ValidRid(payload.Rid)) throw Invalid("rid");
        if (payload.Action == "store_remove")
        {
            if (payload.PublicDer is not null) throw Invalid("publicDer");
            return;
        }
        if (payload.PublicDer is not { Length: >= 1 and <= MaximumPublicDerBytes } ||
            Convert.ToHexStringLower(SHA256.HashData(payload.PublicDer)) != payload.CertificateId) throw Invalid("publicDer");
    }

    internal static void ValidateResult(CertificateElevationResult result, string expectedCertificateId)
    {
        if (!Digest(expectedCertificateId)) throw Invalid("expectedCertificateId");
        if (result is null || result.Version != Version) throw Invalid("version");
        if (!Digest(result.CertificateId) || result.CertificateId != expectedCertificateId) throw Invalid("certificateId");
        if (result.State is not ("added" or "unchanged" or "removed" or "absent" or "failed" or "uncertain")) throw Invalid("state");
        if (result.Code is not null && (result.Code.Length is < 1 or > 96 ||
            result.Code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))) throw Invalid("code");
        if (result.State is "added" or "removed" or "unchanged" or "absent")
        {
            var issued = result.State is "added" or "removed";
            if (result.ActionIssued != issued || !result.PostconditionObserved || result.OutcomeUnknown || result.NativeError is not null)
                throw Invalid("outcome");
        }
        else if (result.State == "failed")
        {
            if (result.ActionIssued is null || result.OutcomeUnknown) throw Invalid("outcome");
        }
        else if (!result.OutcomeUnknown || result.ActionIssued is false) throw Invalid("outcome");
    }

    internal static string SerializePayload(AdminCertificateStoreRequest payload)
    {
        ValidatePayload(payload);
        var json = JsonSerializer.Serialize(payload, Json);
        RequireJsonSize(json, MaximumPayloadBytes);
        return json;
    }

    internal static AdminCertificateStoreRequest ParsePayload(string json)
    {
        byte[]? der = null;
        try
        {
            using var document = Document(json, MaximumPayloadBytes);
            var fields = Object(document.RootElement,
                ["version", "action", "store", "certificateId", "publicDer", "requestIdentity", "jid", "rid"],
                ["version", "action", "store", "certificateId", "requestIdentity", "jid", "rid"]);
            if (fields.TryGetValue("publicDer", out var encoded) && encoded.ValueKind != JsonValueKind.Null)
            {
                var base64 = String(encoded, "publicDer");
                if (base64.Length is < 4 or > ((MaximumPublicDerBytes + 2) / 3 * 4)) throw Invalid("publicDer");
                der = Convert.FromBase64String(base64);
                if (Convert.ToBase64String(der) != base64) throw Invalid("publicDer");
            }
            var result = new AdminCertificateStoreRequest(Integer(fields["version"], "version"),
                String(fields["action"], "action"), String(fields["store"], "store"), String(fields["certificateId"], "certificateId"),
                der, String(fields["requestIdentity"], "requestIdentity"), String(fields["jid"], "jid"), String(fields["rid"], "rid"));
            ValidatePayload(result);
            der = null; // Ownership transfers only after the complete payload validates.
            return result;
        }
        catch (CertificateElevationContractException) { throw; }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid("payload"); }
        finally { if (der is not null) CryptographicOperations.ZeroMemory(der); }
    }

    internal static string SerializeResult(CertificateElevationResult result)
    {
        ValidateResult(result, result.CertificateId);
        var json = JsonSerializer.Serialize(result, Json);
        RequireJsonSize(json, MaximumCompletionBytes);
        return json;
    }

    private static JsonDocument Document(string json, int maximum)
    {
        RequireJsonSize(json, maximum);
        return JsonDocument.Parse(json, new JsonDocumentOptions
            { MaxDepth = MaximumDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
    }

    private static void RequireJsonSize(string? json, int maximum)
    {
        if (json is null || json.Length > maximum || Encoding.UTF8.GetByteCount(json) > maximum) throw Invalid("body");
    }

    private static Dictionary<string, JsonElement> Object(JsonElement value, string[] allowed, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("body");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("body");
        if (required.Any(field => !fields.ContainsKey(field))) throw Invalid("body");
        return fields;
    }

    private static string String(JsonElement value, string field) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(field);
    private static bool Boolean(JsonElement value, string field) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid(field);
    private static int Integer(JsonElement value, string field, bool signed = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw Invalid(field);
        var raw = value.GetRawText();
        if (signed && raw.StartsWith('-')) raw = raw[1..];
        if (raw.Length == 0 || raw.Any(character => character is < '0' or > '9')) throw Invalid(field);
        return number;
    }
    private static bool LowerHex(string value) => value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Digest(string? value) => value is { Length: 64 } && LowerHex(value);
    private static bool ValidRid(string? value) => value is { Length: >= 1 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
    private static bool Store(string? value) => value is "personal" or "intermediate" or "root" or "trusted_publisher" or "trusted_people";
    private static CertificateElevationContractException Invalid(string field) => new(field);
}
