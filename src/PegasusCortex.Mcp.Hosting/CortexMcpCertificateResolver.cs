using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pegasus.Cortex.Mcp.Hosting;

public sealed class CortexMcpHostingException(string code):Exception("Shared MCP hosting is unavailable: "+code)
{
    public string Code { get; }=code;
}

public interface ICortexMcpCertificateResolver
{
    CortexMcpCertificates Resolve(CortexMcpSettings settings,CortexMcpOwner currentOwner);
}

/// <summary>
/// A certificate lease. Borrowed fixture handles remain caller-owned; the production
/// resolver transfers its duplicated certificate handles to this lease.
/// </summary>
public sealed class CortexMcpCertificates:IDisposable
{
    private readonly bool ownsCertificates;
    private int disposed;
    public X509Certificate2 Server { get; }
    public X509Certificate2 Authority { get; }
    private CortexMcpCertificates(X509Certificate2 server,X509Certificate2 authority,bool ownsCertificates)
    {
        Server=server??throw new ArgumentNullException(nameof(server));
        Authority=authority??throw new ArgumentNullException(nameof(authority));
        this.ownsCertificates=ownsCertificates;
    }
    public static CortexMcpCertificates Borrow(X509Certificate2 server,X509Certificate2 authority)=>new(server,authority,false);
    internal static CortexMcpCertificates Own(X509Certificate2 server,X509Certificate2 authority)=>new(server,authority,true);
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0||!ownsCertificates)return;
        try { Server.Dispose(); }
        finally { Authority.Dispose(); }
    }
}

/// <summary>Exact existing Windows-store selection; never installs trust, exports a key or changes key permissions.</summary>
public sealed class CortexMcpCertificateResolver:ICortexMcpCertificateResolver
{
    public CortexMcpCertificates Resolve(CortexMcpSettings settings,CortexMcpOwner currentOwner)
    {
        settings=CortexMcpSettingsStore.Validate(settings);
        // This check precedes every store open and every private-key observation.
        if(settings.Owner!=currentOwner||!Enum.IsDefined(currentOwner))
            throw new CortexMcpHostingException("certificate_owner_mismatch");
        var leaf=currentOwner==CortexMcpOwner.Application?settings.ApplicationCertificate:settings.ServiceCertificate;
        if(leaf is null||settings.CertificateAuthority is not { } ca)
            throw new CortexMcpHostingException("tls_configuration_missing");
        CortexMcpSettingsStore.RequireCertificateReference(leaf,currentOwner);
        CortexMcpSettingsStore.RequireCertificateReference(ca,null);
        if(currentOwner==CortexMcpOwner.Service&&ca.Location!="LocalMachine")
            throw new CortexMcpHostingException("service_ca_machine_reference_required");
        if(!OperatingSystem.IsWindows())throw new CortexMcpHostingException("certificate_store_windows_required");
        var server=ReadCertificate(leaf,true);
        try { return CortexMcpCertificates.Own(server,ReadCertificate(ca,false)); }
        catch { server.Dispose();throw; }
    }

    private static X509Certificate2 ReadCertificate(CortexMcpCertificateReference reference,bool privateKey)
    {
        try
        {
            using var store=new X509Store(Enum.Parse<StoreName>(reference.Store),Enum.Parse<StoreLocation>(reference.Location));
            store.Open(OpenFlags.ReadOnly|OpenFlags.OpenExistingOnly);
            var certificates=store.Certificates;
            try
            {
                var matches=certificates.Where(c=>c.GetCertHashString(HashAlgorithmName.SHA256)
                    .Equals(reference.CertificateId,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matches.Length!=1)throw new CortexMcpHostingException("certificate_reference_missing_or_ambiguous");
                if(privateKey&&!matches[0].HasPrivateKey)throw new CortexMcpHostingException("certificate_private_key_unavailable");
                // Preserve an owned key handle for the server, strip any key association from a public CA.
                return privateKey?new X509Certificate2(matches[0]):X509CertificateLoader.LoadCertificate(matches[0].RawData);
            }
            finally { foreach(var certificate in certificates)certificate.Dispose(); }
        }
        catch(Exception error)when(error is CryptographicException or UnauthorizedAccessException)
        { throw new CortexMcpHostingException("certificate_store_access_failed"); }
    }
}
