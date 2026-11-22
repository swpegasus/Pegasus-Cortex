using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Certificates;

namespace Pegasus.AgentOps.Engine;

/// <summary>Pure, closed owner-host certificate vocabulary. Parsing confers no key or file authority.</summary>
public static class CertificateOperationContract
{
    public const int Version = 1;
    public const int MaxRequestBytes = 16 * 1024;
    public const int MaxJsonDepth = 8;
    private sealed record Policy(bool Mutation, bool File, int? DefaultDays = null, int? MaximumDays = null);
    private static readonly IReadOnlyDictionary<string, Policy> Operations = new Dictionary<string, Policy>(StringComparer.Ordinal)
    {
        ["create_ca"] = new(true, false, 365, 3650),
        ["issue_code"] = new(true, false, 30, 366),
        ["issue_server"] = new(true, false, 30, 366),
        ["issue_machine_server"] = new(true, false, 30, 366),
        ["store_bind_server"] = new(true, false),
        ["inspect"] = new(false, false),
        ["export_public"] = new(false, false),
        ["sign"] = new(true, true),
        ["verify"] = new(false, true),
        ["import_public"] = new(true, false),
        ["import_private"] = new(true, true),
        ["export_private"] = new(true, true),
        ["store_list"] = new(false, false),
        ["store_add"] = new(true, false),
        ["store_trust"] = new(true, false),
        ["store_remove"] = new(true, false),
        ["store_admin_add"] = new(true, false),
        ["store_admin_trust"] = new(true, false),
        ["store_admin_remove"] = new(true, false)
    };

    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(Operations.Keys.ToArray());
    public static bool IsKnownAction(string? action) => action is not null && Operations.ContainsKey(action);
    public static bool IsReadOnly(string? action) => action is not null && Operations.TryGetValue(action, out var policy) && !policy.Mutation;
    public static bool IsMutation(string? action) => !IsReadOnly(action);
    public static bool IsFileAction(string? action) => action is not null && Operations.TryGetValue(action, out var policy) && policy.File;
    public static bool IsAdministratorAction(string? action) => action is "store_admin_add" or "store_admin_trust" or "store_admin_remove";
    public static bool RequiresOwnerElevation(string? action) => IsAdministratorAction(action) || action == "issue_machine_server";
    public static string[] PathFields(string? action) => action switch
    {
        "sign" or "verify" or "import_private" => ["src"],
        "export_private" => ["dst"],
        _ => []
    };
    public static string PublicName(string action) => IsKnownAction(action) ? "certificate_" + action : throw Invalid("op");

    public static CertificateInvocation Parse(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != "certificate" || request.Arg is null ||
            !request.Arg.TryGetValue("op", out var action) || action is null || !Operations.TryGetValue(action, out var policy)) throw Invalid("op");
        // Always inspect raw authority fields. The owning engine may parse again
        // only after its own freeze/admission and removal of generated metadata.
        if (request.Root is not null || request.PathRoots is not null || !policy.File && request.Ws is not null) throw Invalid("context");
        var sourceFile = PathFields(action).Contains("src", StringComparer.Ordinal);
        var destinationFile = PathFields(action).Contains("dst", StringComparer.Ordinal);
        if (request.Arg.Keys.Any(key => key is not ("op" or "rid" or "detail" or "output") &&
                !(sourceFile && (key is "src" or "sha")) && !(destinationFile && key == "dst"))) throw Invalid("arguments");
        if (request.Arg.TryGetValue("detail", out var detail) && detail is not ("true" or "false") ||
            request.Arg.TryGetValue("output", out var output) && output is not ("agent" or "structured")) throw Invalid("presentation");
        if (request.Rid is not null && !ValidRid(request.Rid) || policy.Mutation && request.Rid is null ||
            request.Arg.TryGetValue("rid", out var argumentRid) && (argumentRid is null || argumentRid != request.Rid)) throw Invalid("rid");

        string? src = null;
        string? sha = null;
        string? dst = null;
        if (sourceFile)
        {
            src = request.Arg.GetValueOrDefault("src");
            sha = request.Arg.GetValueOrDefault("sha");
            if (string.IsNullOrWhiteSpace(src) || src.Length > 240 || src.Any(char.IsControl) || InvalidUnicode(src)) throw Invalid("src");
            if (!Digest(sha)) throw Invalid("sha");
        }
        if (destinationFile)
        {
            dst = request.Arg.GetValueOrDefault("dst");
            if (string.IsNullOrWhiteSpace(dst) || dst.Length > 240 || dst.Any(char.IsControl) || InvalidUnicode(dst)) throw Invalid("dst");
        }
        var body = request.Body;
        if (body is null || body.Length > MaxRequestBytes || InvalidUnicode(body) || Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
            throw Invalid("body");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
                { MaxDepth = MaxJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var fields = Object(document.RootElement, Fields(action), Required(action));
            if (Integer(fields["version"], "version") != Version) throw Invalid("version");
            var reference = fields.TryGetValue("reference", out var referenceElement) ? Reference(referenceElement, "reference") : null;
            var certificateId = fields.TryGetValue("certificateId", out var idElement) ? Id(idElement, "certificateId") : null;
            CertificateIssuerIdentity? issuer = null;
            if (action is "issue_code" or "issue_server" or "issue_machine_server")
            {
                var issuerFields = Object(fields["issuer"], ["reference", "certificateId"]);
                issuer = new(Reference(issuerFields["reference"], "issuer.reference"), Id(issuerFields["certificateId"], "issuer.certificateId"));
            }
            string? subject = null;
            int? days = null;
            int? keyBits = null;
            if (policy.DefaultDays.HasValue)
            {
                subject = String(fields["subject"], "subject");
                // These are the existing CertificateOperations primitive bounds,
                // with a separate bounded DNS/IP-only server SAN selection below.
                if (string.IsNullOrWhiteSpace(subject) || subject.Length > 128 || subject != subject.Trim() || subject.Any(char.IsControl))
                    throw Invalid("subject");
                var requestedDays = fields.TryGetValue("days", out var daysElement) ? Integer(daysElement, "days") : policy.DefaultDays.Value;
                var requestedBits = fields.TryGetValue("keyBits", out var bitsElement) ? Integer(bitsElement, "keyBits") : 3072;
                if (requestedDays < 1 || requestedDays > policy.MaximumDays.GetValueOrDefault()) throw Invalid("days");
                if (requestedBits is not (2048 or 3072 or 4096)) throw Invalid("keyBits");
                days = requestedDays; keyBits = requestedBits;
            }
            ServerCertificateNames? serverNames = null;
            if (action is "issue_server" or "issue_machine_server")
            {
                try { serverNames = new(Names(fields["dnsNames"], "dnsNames"), Names(fields["ipAddresses"], "ipAddresses")); }
                catch (CertificateOperationException) { throw Invalid("serverNames"); }
            }
            string? format = null;
            if (action is "export_public" or "import_public")
            {
                format = String(fields["format"], "format");
                if (format is not ("pem" or "der")) throw Invalid("format");
            }
            var passwordReference = fields.TryGetValue("passwordReference", out var passwordElement)
                ? Reference(passwordElement, "passwordReference") : null;
            string? content = null;
            if (fields.TryGetValue("content", out var contentElement))
            {
                content = String(contentElement, "content");
                if (action == "import_public" && format == "pem") RequirePublicPem(content);
                else RequireBase64(content);
            }
            CertificateStoreIdentity? store = null;
            if (action is "store_list" or "store_add" or "store_trust" or "store_remove" || IsAdministratorAction(action))
            {
                var scope = String(fields["scope"], "scope");
                var storeName = String(fields["store"], "store");
                // Scope names a resource, never an execution identity. The
                // ordinary owner adapter must refuse machine mutations before
                // opening a writable backend; parsing cannot grant that authority.
                if (scope is not ("current_user" or "local_machine")) throw Invalid("scope");
                if (IsAdministratorAction(action) && scope != "local_machine") throw Invalid("scope");
                if (storeName is not ("personal" or "intermediate" or "root" or "trusted_publisher" or "trusted_people")) throw Invalid("store");
                var trust = storeName is "root" or "trusted_publisher" or "trusted_people";
                if ((action is "store_add" or "store_admin_add") && trust || (action is "store_trust" or "store_admin_trust") && !trust) throw Invalid("store");
                store = new(scope, storeName);
            }
            int? max = null;
            string? afterCertificateId = null;
            if (action == "store_list")
            {
                var requestedMax = fields.TryGetValue("max", out var maxElement) ? Integer(maxElement, "max") : 32;
                if (requestedMax is < 1 or > 128) throw Invalid("max");
                max = requestedMax;
                afterCertificateId = fields.TryGetValue("afterCertificateId", out var afterElement) ? Id(afterElement, "afterCertificateId") : null;
            }
            return new(action, policy.Mutation, reference, certificateId, issuer, subject, days, keyBits, format, src, sha,
                content, passwordReference, store, max, afterCertificateId, dst, serverNames);
        }
        catch (CertificateContractException) { throw; }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid("body"); }
    }

    private static string[] Fields(string action) => action switch
    {
        "create_ca" => ["version", "reference", "subject", "days", "keyBits"],
        "issue_code" => ["version", "reference", "subject", "days", "keyBits", "issuer"],
        "issue_server" or "issue_machine_server" => ["version", "reference", "subject", "days", "keyBits", "issuer", "dnsNames", "ipAddresses"],
        "store_bind_server" => ["version", "reference", "certificateId"],
        "inspect" => ["version", "reference", "certificateId"],
        "export_public" => ["version", "reference", "certificateId", "format"],
        "sign" => ["version", "reference", "certificateId"],
        "verify" => ["version", "certificateId"],
        "import_public" => ["version", "reference", "certificateId", "format", "content"],
        "import_private" => ["version", "reference", "certificateId", "passwordReference"],
        "export_private" => ["version", "reference", "certificateId", "passwordReference"],
        "store_list" => ["version", "scope", "store", "max", "afterCertificateId"],
        "store_add" or "store_trust" => ["version", "scope", "store", "reference", "certificateId"],
        "store_remove" => ["version", "scope", "store", "certificateId"],
        "store_admin_add" or "store_admin_trust" => ["version", "scope", "store", "reference", "certificateId"],
        "store_admin_remove" => ["version", "scope", "store", "certificateId"],
        _ => throw Invalid("op")
    };

    private static string[] Required(string action) => action switch
    {
        "create_ca" => ["version", "reference", "subject"],
        "issue_code" => ["version", "reference", "subject", "issuer"],
        "issue_server" or "issue_machine_server" => ["version", "reference", "subject", "issuer", "dnsNames", "ipAddresses"],
        "inspect" => ["version", "reference"],
        "store_list" => ["version", "scope", "store"],
        _ => Fields(action)
    };

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] allowed, string[]? required = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("body");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("body");
        if ((required ?? allowed).Any(field => !fields.ContainsKey(field))) throw Invalid("body");
        return fields;
    }

    private static string Reference(JsonElement element, string field)
    {
        var value = String(element, field);
        if (value.Length is < 1 or > 64 || !LowerAlphanumeric(value[0]) ||
            value.Any(character => !LowerAlphanumeric(character) && character is not ('.' or '_' or '-'))) throw Invalid(field);
        return value;
    }

    private static string[] Names(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > ServerCertificateNames.MaximumNames) throw Invalid(field);
        return value.EnumerateArray().Select(item => String(item, field)).ToArray();
    }

    private static bool LowerAlphanumeric(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Id(JsonElement element, string field)
    {
        var value = String(element, field);
        return Digest(value) ? value : throw Invalid(field);
    }

    private static int Integer(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) ||
            element.GetRawText().Any(character => character is < '0' or > '9')) throw Invalid(field);
        return value;
    }

    private static void RequireBase64(string content)
    {
        // Public DER syntax only: the owning exchange adapter must establish
        // actual X.509 structure and the pinned identity before publication.
        if (content.Length == 0 || content.Length % 4 != 0) throw Invalid("content");
        var bytes = new byte[content.Length / 4 * 3];
        try
        {
            if (!Convert.TryFromBase64String(content, bytes, out var written) || written == 0 ||
                Convert.ToBase64String(bytes, 0, written) != content) throw Invalid("content");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequirePublicPem(string content)
    {
        // Accept exactly one public CERTIFICATE block, preserving every character
        // for request identity. No private-key label, second block or appended text.
        if (!PemEncoding.TryFind(content, out var fields) ||
            !content.AsSpan()[fields.Label].SequenceEqual("CERTIFICATE".AsSpan()) || fields.DecodedDataLength < 1 ||
            !OnlyWhitespace(content.AsSpan()[..fields.Location.Start.GetOffset(content.Length)]) ||
            !OnlyWhitespace(content.AsSpan()[fields.Location.End.GetOffset(content.Length)..])) throw Invalid("content");
    }

    private static bool OnlyWhitespace(ReadOnlySpan<char> value)
    {
        foreach (var character in value) if (!char.IsWhiteSpace(character)) return false;
        return true;
    }

    private static string String(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.String) throw Invalid(field);
        // Reject malformed escaped surrogate sequences before any replacement
        // decoding could turn a submitted identity/subject into different text.
        var raw = element.GetRawText();
        for (var index = 1; index < raw.Length - 1; index++)
        {
            if (raw[index] != '\\') continue;
            index++;
            if (raw[index] != 'u') continue;
            var code = ushort.Parse(raw.AsSpan(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            index += 4;
            if (code is >= 0xdc00 and <= 0xdfff) throw Invalid(field);
            if (code is not (>= 0xd800 and <= 0xdbff)) continue;
            if (index + 6 >= raw.Length || raw[index + 1] != '\\' || raw[index + 2] != 'u') throw Invalid(field);
            var low = ushort.Parse(raw.AsSpan(index + 3, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            if (low is not (>= 0xdc00 and <= 0xdfff)) throw Invalid(field);
            index += 6;
        }
        var value = element.GetString()!;
        if (InvalidUnicode(value)) throw Invalid(field);
        return value;
    }

    private static bool InvalidUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsLowSurrogate(value[index])) return true;
            if (!char.IsHighSurrogate(value[index])) continue;
            if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return true;
        }
        return false;
    }

    private static bool ValidRid(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    private static CertificateContractException Invalid(string field) => new(field);
}

public sealed class CertificateContractException(string field) : ArgumentException("certificate_request_invalid")
{
    public string Field { get; } = field;
}

public sealed record CertificateIssuerIdentity(string Reference, string CertificateId);
public sealed record CertificateStoreIdentity(string Scope, string Store);

public sealed class CertificateInvocation
{
    internal CertificateInvocation(string action, bool isMutation, string? reference, string? certificateId,
        CertificateIssuerIdentity? issuer, string? subject, int? days, int? keyBits, string? format, string? src, string? sha,
        string? content = null, string? passwordReference = null, CertificateStoreIdentity? store = null,
        int? max = null, string? afterCertificateId = null, string? dst = null, ServerCertificateNames? serverNames = null)
    {
        Action = action; IsMutation = isMutation; Reference = reference; CertificateId = certificateId; Issuer = issuer;
        Subject = subject; Days = days; KeyBits = keyBits; Format = format; Src = src; Sha = sha;
        Content = content; PasswordReference = passwordReference; Store = store; Max = max; AfterCertificateId = afterCertificateId; Dst = dst;
        ServerNames = serverNames;
        // Keep the original six-action fingerprint serialization byte-for-byte.
        // Adding nullable properties must not invalidate already retained RIDs.
        RequestIdentity = action is "issue_server" or "issue_machine_server" ? Hash(new
        {
            Family = "certificate", Version = CertificateOperationContract.Version, Action, Reference, CertificateId,
            Issuer, Subject, Days, KeyBits, DnsNames = serverNames!.DnsNames, IpAddresses = serverNames.IpAddresses
        }) : content is null && passwordReference is null && store is null && max is null && afterCertificateId is null && dst is null
            ? Hash(new
        {
            Family = "certificate", Version = CertificateOperationContract.Version, Action, Reference, CertificateId,
            Issuer, Subject, Days, KeyBits, Format, Src, Sha
        }) : Hash(new
        {
            Family = "certificate", Version = CertificateOperationContract.Version, Action, Reference, CertificateId,
            Issuer, Subject, Days, KeyBits, Format, Src, Sha, Dst, Content, PasswordReference, Store, Max, AfterCertificateId
        });
    }

    public string Action { get; }
    public bool IsMutation { get; }
    public string? Reference { get; }
    public string? CertificateId { get; }
    public CertificateIssuerIdentity? Issuer { get; }
    public string? Subject { get; }
    public int? Days { get; }
    public int? KeyBits { get; }
    public string? Format { get; }
    public string? Src { get; }
    public string? Sha { get; }
    public string? Dst { get; }
    public string? Content { get; }
    public string? PasswordReference { get; }
    public CertificateStoreIdentity? Store { get; }
    public int? Max { get; }
    public string? AfterCertificateId { get; }
    public ServerCertificateNames? ServerNames { get; }
    public string RequestIdentity { get; }
    public override string ToString() => $"CertificateInvocation(Action={Action}, Content=redacted)";
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
