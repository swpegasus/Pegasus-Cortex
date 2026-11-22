using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>One explicit active DNS resolution. Inventory list/read parsing is separate and unchanged.</summary>
public static class WindowsNetworkResolutionContract
{
    public const string Action = "resolve";
    public const int Version = 1, MaximumBodyBytes = 2048, MinimumTimeoutMilliseconds = 100,
        MaximumTimeoutMilliseconds = 30000, DefaultTimeoutMilliseconds = 5000;
    public static bool IsKnownAction(string? action) => action == Action;
    public static bool IsRequest(OperationRequest request) =>
        request.Op == "windows_network" && request.Arg?.GetValueOrDefault("op") == Action;

    public static WindowsNetworkResolveInvocation Parse(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != "windows_network" || request.Arg is null ||
            request.Arg.GetValueOrDefault("op") != Action) throw Invalid("op");
        if (request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.PathRoots is { Count: > 0 } || request.Arg.Keys.Any(key => key is not ("op" or "rid")))
            throw Invalid("context");
        if (request.Rid is { } rid && !ValidRid(rid) ||
            request.Arg.TryGetValue("rid", out var supplied) && supplied != request.Rid) throw Invalid("rid");
        return Parse(request.Body ?? throw Invalid("body"));
    }

    public static WindowsNetworkResolveInvocation Parse(string body)
    {
        if (body is null || body.Length > MaximumBodyBytes || Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes)
            throw Invalid("body");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 3 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("body");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name is not ("version" or "name" or "recordType" or "timeoutMs") ||
                    !fields.TryAdd(property.Name, property.Value)) throw Invalid("body");
            if (!fields.TryGetValue("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != Version) throw Invalid("version");
            string Text(string field) => fields.TryGetValue(field, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid(field);
            var name = CanonicalName(Text("name"));
            var type = Text("recordType");
            if (type is not ("A" or "AAAA")) throw Invalid("recordType");
            var timeout = DefaultTimeoutMilliseconds;
            if (fields.TryGetValue("timeoutMs", out var time) &&
                (time.ValueKind != JsonValueKind.Number || !time.TryGetInt32(out timeout) ||
                 timeout is < MinimumTimeoutMilliseconds or > MaximumTimeoutMilliseconds)) throw Invalid("timeoutMs");
            return new(name, type, timeout);
        }
        catch (WindowsNetworkResolutionContractException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw Invalid("body"); }
    }

    private static string CanonicalName(string name)
    {
        if (name.Length is < 1 or > 254 || name.Any(c => !char.IsAscii(c))) throw Invalid("name");
        var labelsText = name.EndsWith('.') ? name[..^1] : name;
        if (labelsText.Length is < 1 or > 253 || IPAddress.TryParse(labelsText, out _)) throw Invalid("name");
        foreach (var label in labelsText.Split('.'))
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw Invalid("name");
        // An absolute query deliberately never invokes suffix-search behavior.
        return labelsText.ToLowerInvariant() + ".";
    }
    private static bool ValidRid(string value) => value.Length is > 0 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    private static WindowsNetworkResolutionContractException Invalid(string field) => new(field);
}

public sealed class WindowsNetworkResolutionContractException(string field) : ArgumentException("windows_network_resolution_invalid")
{ public string Field { get; } = field; }

public sealed class WindowsNetworkResolveInvocation
{
    internal WindowsNetworkResolveInvocation(string name, string recordType, int timeoutMilliseconds)
    {
        Name = name; RecordType = recordType; TimeoutMilliseconds = timeoutMilliseconds;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, action = WindowsNetworkResolutionContract.Action,
            name, recordType, timeoutMs = timeoutMilliseconds });
        try { RequestIdentity = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Name { get; }
    public string RecordType { get; }
    public int TimeoutMilliseconds { get; }
    public string RequestIdentity { get; }
    public bool IsMutation => false;
    public bool IsActiveProbe => true;
    public override string ToString() => "WindowsNetworkResolveInvocation(Content=omitted)";
}
