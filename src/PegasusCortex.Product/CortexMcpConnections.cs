using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Product;

/// <summary>Public connection coordinates. Neither a chat label nor a session ID is a credential.</summary>
public sealed record CortexMcpConnectionRequest(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] string ConnectionId,
    [property: JsonRequired] string CredentialSha256,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Application,
    [property: JsonRequired] string? ChatLabel);

public sealed record CortexMcpConnectionGrant(
    [property: JsonRequired] CortexMcpConnectionRequest Request,
    [property: JsonRequired] string CredentialGeneration,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] bool FullControl);

public sealed record CortexMcpConnectionStore(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] string Authority,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] CortexMcpConnectionGrant[] Connections);

/// <summary>Nonsecret authority forwarded only by an authenticated Cortex Host. Never a public PAO argument.</summary>
public sealed record CortexMcpConnectionAuthority(string Authority, string ConnectionId,
    string CredentialGeneration, long GrantRevision);

/// <summary>
/// Admin/SYSTEM-writable machine metadata holds only random-credential digests and approval state.
/// Missing metadata denies access. Client bearer material lives separately in the user's Windows vault.
/// Every dispatch reads current approval; old root-code files are never read or migrated into grants.
/// </summary>
public static class CortexMcpConnections
{
    public const string RequestSchema = "pegasus.cortex.mcp-connection-request/1";
    public const string StoreSchema = "pegasus.cortex.mcp-connections/1";
    public const int MaximumConnections = 128;
    public const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    public static CortexMcpConnectionRequest Validate(CortexMcpConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Schema != RequestSchema || !IsId(request.ConnectionId) ||
            !IsHash(request.CredentialSha256) || !ValidLabel(request.Name, 96) ||
            !ValidLabel(request.Application, 128) ||
            request.ChatLabel is not null && !ValidLabel(request.ChatLabel, 128))
            throw new InvalidDataException("Invalid MCP connection request.");
        return request;
    }

    public static CortexMcpConnectionStore Validate(CortexMcpConnectionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Schema != StoreSchema || !IsId(store.Authority) || store.Revision < 1 ||
            store.Connections is null || store.Connections.Length > MaximumConnections)
            throw new InvalidDataException("Invalid MCP connection registry.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in store.Connections)
        {
            if (grant is null || !IsId(grant.CredentialGeneration) || grant.Revision < 1 ||
                !ids.Add(Validate(grant.Request).ConnectionId) || !grant.Enabled && grant.FullControl)
                throw new InvalidDataException("Invalid MCP connection approval.");
        }
        return store;
    }

    public static CortexMcpConnectionStore? ReadForInstallRoot(string installRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Read(ResolvePath(installRoot), RequireBoundary);
    }

    public static CortexMcpConnectionGrant? Find(CortexMcpConnectionStore? store,
        CortexMcpConnectionAuthority authority, bool requireFullControl = false)
    {
        if (store is null || authority.Authority != store.Authority) return null;
        var grant = store.Connections.SingleOrDefault(item => item.Request.ConnectionId == authority.ConnectionId);
        return grant is { Enabled: true } && grant.CredentialGeneration == authority.CredentialGeneration &&
            grant.Revision == authority.GrantRevision && (!requireFullControl || grant.FullControl) ? grant : null;
    }

    public static CortexMcpConnectionAuthority? Authenticate(CortexMcpConnectionStore? store, string? authorization)
    {
        if (store is null || authorization is null || authorization.Length != 83 ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization[39] != '.')
            return null;
        var id = authorization.Substring(7, 32);
        if (!IsId(id)) return null;
        var encoded = authorization[40..];
        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=");
            if (secret.Length != 32 || Base64Url(secret) != encoded)
            { CryptographicOperations.ZeroMemory(secret); return null; }
        }
        catch (FormatException) { return null; }
        try
        {
            var digest = SHA256.HashData(secret);
            try
            {
                var grant = store.Connections.SingleOrDefault(item => item.Request.ConnectionId == id);
                if (grant is not { Enabled: true } ||
                    !CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(grant.Request.CredentialSha256)))
                    return null;
                return new(store.Authority, id, grant.CredentialGeneration, grant.Revision);
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public static CortexMcpConnectionRequest ReadRequest(string path) => ReadPinnedRequest(path).Request;

    public static (CortexMcpConnectionRequest Request, string Sha256) ReadPinnedRequest(string path)
    {
        CortexProductLayout.RequirePathBudget(Path.GetFullPath(path));
        CortexProductLayout.RejectExistingReparseComponents(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 2 or > 4096) throw new InvalidDataException("Connection request exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Connection request changed while reading.");
        RejectDuplicateProperties(bytes);
        var request = Validate(JsonSerializer.Deserialize<CortexMcpConnectionRequest>(bytes, Json) ??
            throw new InvalidDataException("Connection request is empty."));
        return (request, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>Explicit UAC action; expected revision prevents applying stale UI selections.</summary>
    public static CortexMcpConnectionGrant ApproveForInstallRoot(string installRoot,
        CortexMcpConnectionRequest request, bool fullControl, long expectedRevision)
    {
        RequireAdministrator();
        request = Validate(request);
        CortexMcpConnectionGrant? result = null;
        Update(installRoot, store =>
        {
            var current = store.Connections.SingleOrDefault(item => item.Request.ConnectionId == request.ConnectionId);
            if ((current?.Revision ?? 0) != expectedRevision) throw new InvalidDataException("Connection approval changed; refresh.");
            if (current is not null && current.Request.CredentialSha256 != request.CredentialSha256)
                throw new InvalidDataException("Connection ID is already bound to another credential.");
            result = new(request, current?.CredentialGeneration ?? Guid.NewGuid().ToString("N"),
                checked((current?.Revision ?? 0) + 1), true, fullControl);
            var entries = store.Connections.Where(item => item.Request.ConnectionId != request.ConnectionId).Append(result).ToArray();
            return store with { Revision = checked(store.Revision + 1), Connections = entries };
        });
        return result!;
    }

    public static void RevokeForInstallRoot(string installRoot, string connectionId, long expectedRevision, bool fullControlOnly)
    {
        RequireAdministrator();
        if (!IsId(connectionId)) throw new InvalidDataException("Invalid connection ID.");
        Update(installRoot, store =>
        {
            var current = store.Connections.SingleOrDefault(item => item.Request.ConnectionId == connectionId) ??
                throw new InvalidDataException("Connection is not registered.");
            if (current.Revision != expectedRevision) throw new InvalidDataException("Connection approval changed; refresh.");
            var changed = current with { Revision = checked(current.Revision + 1), FullControl = false,
                Enabled = fullControlOnly && current.Enabled };
            return store with { Revision = checked(store.Revision + 1),
                Connections = store.Connections.Select(item => item == current ? changed : item).ToArray() };
        });
    }

    internal static CortexMcpConnectionStore? Read(string path, Action<string> boundary) =>
        CortexMachineSettings.Read<CortexMcpConnectionStore?>(path, null, bytes =>
        {
            RejectDuplicateProperties(bytes);
            return Validate(JsonSerializer.Deserialize<CortexMcpConnectionStore>(bytes, Json) ??
                throw new InvalidDataException("Connection registry is empty."));
        }, boundary, MaximumBytes, "MCP connection approvals", FileShare.Read | FileShare.Delete);

    private static void Update(string installRoot, Func<CortexMcpConnectionStore, CortexMcpConnectionStore> change)
    {
        var path = ResolvePath(installRoot);
        RequireBoundary(path);
        var lockPath = Path.Combine(Path.GetDirectoryName(path)!, ".mcp-connections.lock");
        RequireBoundary(lockPath);
        using var held = CreateWriterLock(lockPath);
        RequireBoundary(lockPath);
        var store = Read(path, RequireBoundary) ?? new(StoreSchema, Guid.NewGuid().ToString("N"), 1, []);
        var changed = Validate(change(store));
        CortexMachineSettings.Save(path, changed,
            (stream, value) => JsonSerializer.Serialize(stream, value, Json), RequireBoundary, ".mcp-grants-");
    }

    private static FileStream CreateWriterLock(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new(new SecurityIdentifier(sid), System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
        return System.IO.FileSystemAclExtensions.Create(new FileInfo(path), FileMode.OpenOrCreate,
            System.Security.AccessControl.FileSystemRights.Read | System.Security.AccessControl.FileSystemRights.Write |
            System.Security.AccessControl.FileSystemRights.ReadPermissions | System.Security.AccessControl.FileSystemRights.Synchronize,
            FileShare.None, 4096, FileOptions.None, security);
    }

    private static string ResolvePath(string root) => CortexMachineSettings.ResolvePath(root, "mcp-connections.v1.json");
    private static void RequireBoundary(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        CortexMachineSettings.RequireStoreBoundary(path, "MCP connection approvals");
    }
    private static void RequireAdministrator()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Changing MCP approvals requires explicit Windows elevation.");
    }

    internal static void RejectDuplicateProperties(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate connection metadata property.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Visit(item);
        }
        Visit(document.RootElement);
    }
    public static bool IsId(string? value) => value is { Length: 32 } && value.Any(c => c != '0') &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsAuthority(CortexMcpConnectionAuthority? value) => value is not null &&
        IsId(value.Authority)&&IsId(value.ConnectionId)&&IsId(value.CredentialGeneration)&&value.GrantRevision>=1;
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidLabel(string? value, int maximum) => value is { Length: > 0 } &&
        value.Length <= maximum && value.Trim() == value && !value.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(value) <= maximum * 4;
    internal static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
