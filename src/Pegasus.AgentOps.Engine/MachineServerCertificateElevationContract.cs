using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal sealed record MachineServerCertificateRequest(
    [property: JsonRequired] int Version, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] CertificateSpec Specification,
    [property: JsonRequired] string[] DnsNames, [property: JsonRequired] string[] IpAddresses,
    [property: JsonRequired] byte[] PublicAuthorityDer, [property: JsonRequired] string AuthorityCertificateId,
    [property: JsonRequired] string RequestIdentity, [property: JsonRequired] string Jid, [property: JsonRequired] string Rid);

internal sealed record MachineServerCertificateSigningRequest(
    [property: JsonRequired] int Version, [property: JsonRequired] byte[] SigningRequest);

internal sealed record MachineServerCertificateSignedLeaf(
    [property: JsonRequired] int Version, [property: JsonRequired] byte[] PublicLeafDer,
    [property: JsonRequired] string CertificateId);

internal sealed record MachineServerCertificateResult(
    [property: JsonRequired] int Version, [property: JsonRequired] string State,
    [property: JsonRequired] MachineServerCertificateProgress? Progress,
    [property: JsonRequired] string? Code = null, [property: JsonRequired] int? NativeError = null);

internal sealed record MachineServerCertificateElevationOutcome(MachineServerCertificateResult Result, bool PayloadIssued,
    CertificateElevationProcessIdentity? Process = null, int? Exit = null);

// One closed public-material conversation; private keys, provider/container/store
// selectors, executable paths and alternative execution identities are absent.
internal static class MachineServerCertificateElevationContract
{
    internal const int Version = 1;
    internal const string RedeemOperation = "__certificate_machine_redeem";
    internal const string CsrOperation = "__certificate_machine_csr";
    internal const string CompleteOperation = "__certificate_machine_complete";
    internal const int MaximumPayloadBytes = 96 * 1024;
    internal const int MaximumCsrBytes = 16 * 1024;
    internal const int MaximumCsrBodyBytes = 24 * 1024;
    internal const int MaximumResultBytes = 8192;
    private const int MaximumPublicDerBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 5, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict
    };

    internal static bool IsControlRequest(OperationRequest request) => request is not null &&
        request.Op is RedeemOperation or CsrOperation or CompleteOperation;

    internal static string ParseTicket(OperationRequest request)
    {
        if (!IsControlRequest(request) || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is not null || request.Quiet || request.Detail || request.Arg is null || request.Arg.Count != 1 ||
            !request.Arg.TryGetValue("ticket", out var ticket) || !Digest(ticket) ||
            request.Rid is not null && !Rid(request.Rid)) throw Invalid();
        if (request.Op == RedeemOperation)
        { if (request.Body is not null) throw Invalid(); }
        else RequireSize(request.Body, request.Op == CsrOperation ? MaximumCsrBodyBytes : MaximumResultBytes);
        return ticket;
    }

    internal static MachineServerCertificateRequest Snapshot(MachineServerCertificateRequest request)
    {
        ValidateRequest(request);
        return request with { Specification = request.Specification with { }, DnsNames = request.DnsNames.ToArray(),
            IpAddresses = request.IpAddresses.ToArray(), PublicAuthorityDer = request.PublicAuthorityDer.ToArray() };
    }

    internal static void ValidateRequest(MachineServerCertificateRequest request)
    {
        if (request is null || request.Version != Version || request.OperationId == Guid.Empty || request.Specification is not { } spec ||
            string.IsNullOrWhiteSpace(spec.CommonName) || spec.CommonName.Length > 128 || spec.CommonName != spec.CommonName.Trim() ||
            spec.CommonName.Any(char.IsControl) || spec.RsaBits is not (2048 or 3072 or 4096) ||
            spec.NotBefore.Offset != TimeSpan.Zero || spec.NotAfter.Offset != TimeSpan.Zero ||
            spec.NotBefore.Ticks % TimeSpan.TicksPerSecond != 0 || spec.NotAfter.Ticks % TimeSpan.TicksPerSecond != 0 ||
            spec.NotAfter <= spec.NotBefore || spec.NotAfter - spec.NotBefore > TimeSpan.FromDays(366) ||
            request.DnsNames is null || request.IpAddresses is null || !Digest(request.AuthorityCertificateId) ||
            !Digest(request.RequestIdentity) || !Hex(request.Jid, 32) || !Rid(request.Rid) ||
            request.PublicAuthorityDer is null || request.PublicAuthorityDer.Length is < 1 or > MaximumPublicDerBytes)
            throw Invalid();
        _ = new UTF8Encoding(false, true).GetByteCount(spec.CommonName);
        var names = new ServerCertificateNames(request.DnsNames, request.IpAddresses);
        if (!names.DnsNames.SequenceEqual(request.DnsNames) || !names.IpAddresses.SequenceEqual(request.IpAddresses)) throw Invalid();
        using var authority = CertificateExchange.ImportPublic(request.PublicAuthorityDer, PublicCertificateFormat.Der);
        var summary = CertificateOperations.Inspect(authority);
        if (summary.CertificateId != request.AuthorityCertificateId || !summary.IsAuthority ||
            spec.NotBefore < summary.NotBefore || spec.NotAfter > summary.NotAfter) throw Invalid();
    }

    internal static string SerializeRequest(MachineServerCertificateRequest request)
    { ValidateRequest(request); return Serialize(request, MaximumPayloadBytes); }

    internal static MachineServerCertificateRequest ParseRequest(string text)
    {
        using var document = Document(text, MaximumPayloadBytes);
        RequireFields(document.RootElement, "version", "operationId", "specification", "dnsNames", "ipAddresses",
            "publicAuthorityDer", "authorityCertificateId", "requestIdentity", "jid", "rid");
        RequireFields(document.RootElement.GetProperty("specification"), "commonName", "notBefore", "notAfter", "rsaBits");
        var request = Deserialize<MachineServerCertificateRequest>(document.RootElement);
        CanonicalBase64(document.RootElement.GetProperty("publicAuthorityDer"), request.PublicAuthorityDer);
        return Snapshot(request);
    }

    internal static string SerializeCsr(byte[] csr)
    {
        if (csr is null || csr.Length is < 1 or > MaximumCsrBytes) throw Invalid();
        return Serialize(new MachineServerCertificateSigningRequest(Version, csr.ToArray()), MaximumCsrBodyBytes);
    }

    internal static byte[] ParseCsr(OperationRequest request)
    {
        _ = ParseTicket(request);
        if (request.Op != CsrOperation) throw Invalid();
        using var document = Document(request.Body!, MaximumCsrBodyBytes);
        RequireFields(document.RootElement, "version", "signingRequest");
        var csr = Deserialize<MachineServerCertificateSigningRequest>(document.RootElement);
        if (csr.Version != Version || csr.SigningRequest is null || csr.SigningRequest.Length is < 1 or > MaximumCsrBytes) throw Invalid();
        CanonicalBase64(document.RootElement.GetProperty("signingRequest"), csr.SigningRequest);
        return csr.SigningRequest.ToArray();
    }

    internal static MachineServerCertificateSignedLeaf SnapshotLeaf(MachineServerCertificateSignedLeaf leaf)
    {
        if (leaf is null || leaf.Version != Version || !Digest(leaf.CertificateId) || leaf.PublicLeafDer is null ||
            leaf.PublicLeafDer.Length is < 1 or > MaximumPublicDerBytes ||
            Convert.ToHexStringLower(SHA256.HashData(leaf.PublicLeafDer)) != leaf.CertificateId) throw Invalid();
        using var certificate = CertificateExchange.ImportPublic(leaf.PublicLeafDer, PublicCertificateFormat.Der);
        if (certificate.HasPrivateKey) throw Invalid();
        return leaf with { PublicLeafDer = leaf.PublicLeafDer.ToArray() };
    }

    internal static void ValidateLeaf(MachineServerCertificateSignedLeaf leaf, MachineServerCertificateRequest request,
        DateTimeOffset now)
    {
        var copy = SnapshotLeaf(leaf);
        using var certificate = CertificateExchange.ImportPublic(copy.PublicLeafDer, PublicCertificateFormat.Der);
        var names = ServerCertificateMaterial.Require(certificate, copy.CertificateId, now, privateKey: false);
        using var key = certificate.GetRSAPublicKey();
        if (key?.KeySize != request.Specification.RsaBits || certificate.GetNameInfo(X509NameType.SimpleName, false) != request.Specification.CommonName ||
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime()) != request.Specification.NotBefore ||
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime()) != request.Specification.NotAfter ||
            !names.DnsNames.SequenceEqual(request.DnsNames) || !names.IpAddresses.SequenceEqual(request.IpAddresses) ||
            !CertificateChainOperations.VerifyServerAuthentication(certificate, copy.CertificateId,
                new(CertificateTrustSelection.CustomRoots, CertificateRevocationSelection.NotChecked, now,
                    [request.PublicAuthorityDer])).Trusted) throw Invalid();
    }

    internal static string SerializeLeaf(MachineServerCertificateSignedLeaf leaf) =>
        Serialize(SnapshotLeaf(leaf), MaximumPayloadBytes);

    internal static MachineServerCertificateSignedLeaf ParseLeaf(string text)
    {
        using var document = Document(text, MaximumPayloadBytes);
        RequireFields(document.RootElement, "version", "publicLeafDer", "certificateId");
        var leaf = Deserialize<MachineServerCertificateSignedLeaf>(document.RootElement);
        CanonicalBase64(document.RootElement.GetProperty("publicLeafDer"), leaf.PublicLeafDer);
        return SnapshotLeaf(leaf);
    }

    internal static string SerializeResult(MachineServerCertificateResult result)
    { ValidateResult(result); return Serialize(result, MaximumResultBytes); }

    internal static MachineServerCertificateResult ParseResult(OperationRequest request)
    {
        _ = ParseTicket(request);
        if (request.Op != CompleteOperation) throw Invalid();
        using var document = Document(request.Body!, MaximumResultBytes);
        RequireFields(document.RootElement, "version", "state", "progress", "code", "nativeError");
        if (document.RootElement.GetProperty("progress").ValueKind != JsonValueKind.Null)
            RequireFields(document.RootElement.GetProperty("progress"), "phase", "keyCreationIssued", "keyFinalized",
                "securityObserved", "storeActionIssued", "postconditionObserved", "cleanupIssued", "cleanupObserved",
                "outcomeUnknown", "certificateId", "code");
        var result = Deserialize<MachineServerCertificateResult>(document.RootElement);
        ValidateResult(result);
        return result;
    }

    internal static void ValidateResult(MachineServerCertificateResult result)
    {
        if (result is null || result.Version != Version || result.State is not ("bound" or "failed" or "uncertain") ||
            result.Code is not null && !Code(result.Code, 96)) throw Invalid();
        // Lost helper evidence is represented by absent progress, not fabricated
        // false action flags. Only an explicitly uncertain result may omit it.
        if (result.Progress is not { } p)
        { if (result.State != "uncertain") throw Invalid(); return; }
        if (!Code(p.Phase, 48) || p.Code is not null && !Code(p.Code, 96) || p.CertificateId is not null && !Digest(p.CertificateId) ||
            p.KeyFinalized && !p.KeyCreationIssued || p.SecurityObserved && !p.KeyFinalized ||
            p.StoreActionIssued && (!p.KeyFinalized || !p.SecurityObserved || p.CleanupIssued) ||
            p.PostconditionObserved && !p.StoreActionIssued || p.CleanupObserved && !p.CleanupIssued ||
            p.CleanupIssued && !p.KeyCreationIssued || p.CleanupObserved && p.OutcomeUnknown)
            throw Invalid();
        if (result.State == "bound")
        {
            if (!p.PostconditionObserved || p.OutcomeUnknown || p.CertificateId is null || p.CleanupIssued ||
                result.Code is not null || result.NativeError is not null) throw Invalid();
        }
        else if (p.PostconditionObserved || (result.State == "uncertain") != p.OutcomeUnknown) throw Invalid();
    }

    internal static MachineServerCertificateResult Failed(string code, bool payloadIssued = false) =>
        new(Version, payloadIssued ? "uncertain" : "failed", payloadIssued ? null :
            new("not-started", false, false, false, false, false, false, false, false), code);

    internal static OperationRequest Control(string operation, string ticket, string? body = null) =>
        new(operation, new Dictionary<string, string>(StringComparer.Ordinal) { ["ticket"] = ticket }, Body: body);

    internal static bool Digest(string? value) => Hex(value, 64);
    private static bool Hex(string? value, int count) => value is not null && value.Length == count &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Rid(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);
    private static bool Code(string value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static ArgumentException Invalid() => new("certificate_machine_contract_invalid");

    private static string Serialize<T>(T value, int maximum)
    { var text = JsonSerializer.Serialize(value, Json); RequireSize(text, maximum); return text; }
    private static T Deserialize<T>(JsonElement value) where T : class => value.Deserialize<T>(Json) ?? throw Invalid();
    private static void RequireSize(string? text, int maximum)
    { if (text is null || text.Length > maximum || Encoding.UTF8.GetByteCount(text) > maximum) throw Invalid(); }
    private static JsonDocument Document(string text, int maximum)
    {
        RequireSize(text, maximum);
        var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 5 });
        try { RequireUnique(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); RequireUnique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) RequireUnique(child);
    }
    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            value.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw Invalid();
    }
    private static void CanonicalBase64(JsonElement encoded, byte[]? bytes)
    { if (bytes is null || encoded.ValueKind != JsonValueKind.String || encoded.GetString() != Convert.ToBase64String(bytes)) throw Invalid(); }
}
