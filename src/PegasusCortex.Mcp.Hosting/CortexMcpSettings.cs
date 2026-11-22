using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Product;
using Pegasus.SharedMcp;

namespace Pegasus.Cortex.Mcp.Hosting;

public enum CortexMcpOwner { Application, Service }

/// <summary>Public certificate identity only. No password, key bytes or export location is persisted.</summary>
public sealed record CortexMcpCertificateReference(
    [property:JsonRequired] string Location,
    [property:JsonRequired] string Store,
    [property:JsonRequired] string CertificateId);

public sealed record CortexMcpSettings(
    [property:JsonRequired] string Contract,
    [property:JsonRequired] int Version,
    [property:JsonRequired] bool Enabled,
    [property:JsonRequired] CortexMcpOwner Owner,
    [property:JsonRequired] string BindAddress,
    [property:JsonRequired] int Port,
    [property:JsonRequired] bool AllowPrivateNetwork,
    [property:JsonRequired] string[] AllowedPeerCidrs,
    [property:JsonRequired] string ServerName,
    [property:JsonRequired] CortexMcpCertificateReference? CertificateAuthority,
    [property:JsonRequired] CortexMcpCertificateReference? ApplicationCertificate,
    [property:JsonRequired] CortexMcpCertificateReference? ServiceCertificate);

/// <summary>One closed, protected machine setting. Read once at owner startup; saving never changes a running listener.</summary>
public static class CortexMcpSettingsStore
{
    public const string Contract="pegasus-cortex-shared-mcp";
    public const string FileName="shared-mcp.v1.json";
    private const int MaximumBytes=16384;
    private static readonly JsonSerializerOptions Json=CreateJsonOptions();

    public static CortexMcpSettings Default=>new(Contract,1,false,CortexMcpOwner.Application,
        "127.0.0.1",47831,false,["127.0.0.0/8","::1/128"],"localhost",null,null,null);

    public static CortexMcpSettings Validate(CortexMcpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if(settings.Contract!=Contract||settings.Version!=1||!Enum.IsDefined(settings.Owner)||
            !IPAddress.TryParse(settings.BindAddress,out var bind)||bind.IsIPv4MappedToIPv6||
            bind.ToString()!=settings.BindAddress||!PrivateNetworkPolicy.IsPrivateAddress(bind)||
            settings.Port is <1 or >65535||settings.AllowedPeerCidrs is null||
            settings.AllowedPeerCidrs.Length is <1 or >64||
            settings.AllowedPeerCidrs.Any(c=>string.IsNullOrEmpty(c)||c.Length>64||c.Any(char.IsControl))||
            settings.AllowedPeerCidrs.Distinct(StringComparer.Ordinal).Count()!=settings.AllowedPeerCidrs.Length)
            throw new InvalidDataException("Shared MCP settings contain an invalid contract, owner, endpoint or peer policy.");
        try { _=new PrivateNetworkPolicy(settings.AllowedPeerCidrs); }
        catch(ArgumentException error) { throw new InvalidDataException("Shared MCP peer policy is invalid.",error); }
        // The neutral policy already admitted each complete CIDR and its private containment.
        // Inspecting only its admitted network address adds the explicit LAN/VPN opt-in boundary.
        if(!settings.AllowPrivateNetwork&&(!IPAddress.IsLoopback(bind)||
            settings.AllowedPeerCidrs.Any(c=>!IPAddress.IsLoopback(IPAddress.Parse(c.Split('/')[0])))))
            throw new InvalidDataException("LAN or VPN binding and peers require explicit private-network opt-in.");
        if(string.IsNullOrWhiteSpace(settings.ServerName)||settings.ServerName.Length>253||
            settings.ServerName.Any(c=>c>127||char.IsControl(c))||
            settings.ServerName.IndexOfAny(['/','\\','%','*','[',']'])>=0||
            Uri.CheckHostName(settings.ServerName)==UriHostNameType.Unknown)
            throw new InvalidDataException("Shared MCP TLS identity must be a bare ASCII DNS name or IP address.");
        if(settings.CertificateAuthority is { } ca)RequireCertificateReference(ca,null);
        if(settings.ApplicationCertificate is { } application)RequireCertificateReference(application,CortexMcpOwner.Application);
        if(settings.ServiceCertificate is { } service)RequireCertificateReference(service,CortexMcpOwner.Service);
        // Missing references remain representable: startup reports unavailable rather than inventing or falling back to keys.
        return settings with { AllowedPeerCidrs=settings.AllowedPeerCidrs.ToArray() };
    }

    internal static void RequireCertificateReference(CortexMcpCertificateReference reference,CortexMcpOwner? privateOwner)
    {
        var allowed=privateOwner switch
        {
            CortexMcpOwner.Application=>reference.Location=="CurrentUser"&&reference.Store=="My",
            CortexMcpOwner.Service=>reference.Location=="LocalMachine"&&reference.Store=="My",
            null=>reference.Location=="LocalMachine"&&reference.Store=="Root"||
                reference.Location=="CurrentUser"&&reference.Store is ("My" or "Root" or "CertificateAuthority"),
            _=>false
        };
        if(!allowed||reference.CertificateId is null||reference.CertificateId.Length!=64||
            reference.CertificateId.Any(c=>c is not(>='0' and <='9' or >='a' and <='f')))
            throw new InvalidDataException("Shared MCP certificate references require an exact SHA256 identity and an admitted role-specific store.");
    }

    public static CortexMcpSettings ReadForInstallRoot(string installRoot)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        return Read(CortexMachineSettings.ResolvePath(installRoot,FileName),RequireBoundary);
    }

    // The fixed product helper invokes this only after normal UAC; this check is independent.
    public static void SaveForInstallRoot(string installRoot,CortexMcpSettings settings)
        =>UpdateForInstallRoot(installRoot,_=>settings);

    /// <summary>Serializes fixed helper updates and merges against the latest protected selection.
    /// A competing writer is reported as busy; it is never retried or overwritten implicitly.</summary>
    public static void UpdateForInstallRoot(string installRoot,Func<CortexMcpSettings,CortexMcpSettings> update)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Saving machine shared MCP settings requires Windows elevation.");
        Update(CortexMachineSettings.ResolvePath(installRoot,FileName),update,RequireBoundary);
    }

    internal static void Update(string path,Func<CortexMcpSettings,CortexMcpSettings> update,
        Action<string> requireBoundary,Func<string,FileStream>? createTemporary=null)
    {
        ArgumentNullException.ThrowIfNull(update);
        var lockPath=Path.Combine(Path.GetDirectoryName(path)!,".shared-mcp.write.lock");
        CortexProductLayout.RequirePathBudget(lockPath);
        requireBoundary(path);requireBoundary(lockPath);
        // The protected directory admits only trusted writers. DeleteOnClose
        // removes this coordination leaf after its exact original handle closes.
        using var writer=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,
            FileShare.None,1,FileOptions.DeleteOnClose);
        requireBoundary(lockPath);
        var current=Read(path,requireBoundary);
        Save(path,Validate(update(current)),requireBoundary,createTemporary);
    }

    internal static CortexMcpSettings Read(string path,Action<string> requireBoundary)=>
        CortexMachineSettings.Read(path,Default,bytes=>Parse(bytes),requireBoundary,MaximumBytes,"Shared MCP settings");

    internal static void Save(string path,CortexMcpSettings settings,Action<string> requireBoundary,
        Func<string,FileStream>? createTemporary=null)
    {
        var bytes=Serialize(settings);
        CortexMachineSettings.Save(path,bytes,(stream,value)=>stream.Write(value),requireBoundary,".shared-mcp-",createTemporary);
    }

    public static CortexMcpSettings Parse(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is <1 or >MaximumBytes)throw new InvalidDataException("Shared MCP settings exceed their bounded file format.");
        using var document=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions { MaxDepth=8 });
        RequireUniqueProperties(document.RootElement);
        return Validate(document.RootElement.Deserialize<CortexMcpSettings>(Json)??
            throw new InvalidDataException("Shared MCP settings are empty."));
    }

    public static byte[] Serialize(CortexMcpSettings settings)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(Validate(settings),Json);
        if(bytes.Length>MaximumBytes)throw new InvalidDataException("Shared MCP settings exceed their bounded file format.");
        return bytes;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireBoundary(string path)=>CortexMachineSettings.RequireStoreBoundary(path,"Shared MCP settings");

    private static void RequireUniqueProperties(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject())
            {
                if(!names.Add(property.Name))throw new JsonException("Duplicate shared MCP settings property.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if(element.ValueKind==JsonValueKind.Array)
            foreach(var child in element.EnumerateArray())RequireUniqueProperties(child);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive=false,MaxDepth=8
        };
        options.Converters.Add(new OwnerConverter());
        return options;
    }

    private sealed class OwnerConverter:JsonConverter<CortexMcpOwner>
    {
        public override CortexMcpOwner Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>
            reader.TokenType==JsonTokenType.String?reader.GetString() switch
            {
                "Application"=>CortexMcpOwner.Application,"Service"=>CortexMcpOwner.Service,
                _=>throw new JsonException("Shared MCP owner is invalid.")
            }:throw new JsonException("Shared MCP owner must be a named role.");
        public override void Write(Utf8JsonWriter writer,CortexMcpOwner value,JsonSerializerOptions options)=>
            writer.WriteStringValue(value switch
            {
                CortexMcpOwner.Application=>"Application",CortexMcpOwner.Service=>"Service",
                _=>throw new JsonException("Shared MCP owner is invalid.")
            });
    }
}
