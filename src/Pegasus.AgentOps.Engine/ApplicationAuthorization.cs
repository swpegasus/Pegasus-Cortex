using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

internal sealed record ApplicationPeerEvidence(string ApplicationId,string ServiceName,string ExecutablePath,string PublisherThumbprint);
internal sealed record ApplicationSessionLease(string ApplicationId,string CredentialId,long Generation);

internal interface IApplicationPeerVerifier
{
    ApplicationPeerEvidence Verify(LocalPeer peer,string applicationId);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsApplicationPeerVerifier:IApplicationPeerVerifier
{
    private const string LocalSystemSid="S-1-5-18";
    private const string LocalServiceSid="S-1-5-19";
    public ApplicationPeerEvidence Verify(LocalPeer peer,string applicationId)
    {
        if(!StringComparer.Ordinal.Equals(applicationId,ApplicationAuthorizationProtocol.CerebralApplicationId)||
            !StringComparer.Ordinal.Equals(peer.Sid,LocalServiceSid)||peer.Session!=0||peer.ProcessId is null||
            string.IsNullOrWhiteSpace(peer.ApplicationPath))throw new AgentOpsException("application_auth");

        var server=ServiceProcessIdentity.Load(CortexProductIdentity.ServiceName);
        var current=LocalIdentity.Current();
        if(!StringComparer.Ordinal.Equals(current.Sid,LocalSystemSid)||current.Session!=0||
            current.ProcessId is null||server.ProcessId!=current.ProcessId||
            !SamePath(server.ExecutablePath,Environment.ProcessPath))throw new AgentOpsException("application_auth");

        var client=ServiceProcessIdentity.Load(ApplicationAuthorizationProtocol.CerebralServiceName);
        if(client.ProcessId!=peer.ProcessId||!SamePath(client.ExecutablePath,peer.ApplicationPath))
            throw new AgentOpsException("application_auth");

        RequireOrdinaryExecutable(server.ExecutablePath);
        RequireOrdinaryExecutable(client.ExecutablePath);
        var serverSigner=AuthenticodePublisher.ReadValidPublisher(server.ExecutablePath);
        var clientSigner=AuthenticodePublisher.ReadValidPublisher(client.ExecutablePath);
        if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(serverSigner),Convert.FromHexString(clientSigner)))
            throw new AgentOpsException("application_signer");
        return new(applicationId,ApplicationAuthorizationProtocol.CerebralServiceName,client.ExecutablePath,clientSigner);
    }

    private static bool SamePath(string expected,string? actual)
    {
        if(string.IsNullOrWhiteSpace(actual))return false;
        try{return StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(expected),Path.GetFullPath(actual));}
        catch(Exception ex)when(ex is ArgumentException or IOException or UnauthorizedAccessException){return false;}
    }

    private static void RequireOrdinaryExecutable(string path)
    {
        try
        {
            var canonical=Path.GetFullPath(path);
            if(!Path.IsPathFullyQualified(path)||!StringComparer.OrdinalIgnoreCase.Equals(canonical,path)||!File.Exists(canonical)||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(canonical),".exe"))throw new AgentOpsException("application_identity");
            PathNormalizer.RejectAbsoluteReparseComponents(canonical);
            if((File.GetAttributes(canonical)&FileAttributes.ReparsePoint)!=0)throw new AgentOpsException("application_identity");
        }
        catch(AgentOpsException){throw;}
        catch(Exception ex)when(ex is ArgumentException or IOException or UnauthorizedAccessException){throw new AgentOpsException("application_identity",inner:ex);}
    }
}

[SupportedOSPlatform("windows")]
internal sealed record ServiceProcessIdentity(string ServiceName,uint ProcessId,string ExecutablePath)
{
    public static ServiceProcessIdentity Load(string serviceName)
    {
        var manager=OpenSCManager(null,null,0x0001);if(manager==0)throw new AgentOpsException("application_identity");
        try
        {
            var service=OpenService(manager,serviceName,0x0001|0x0004);if(service==0)throw new AgentOpsException("application_identity");
            try
            {
                var statusSize=Marshal.SizeOf<ServiceStatusProcess>();var statusBuffer=Marshal.AllocHGlobal(statusSize);
                try
                {
                    if(!QueryServiceStatusEx(service,0,statusBuffer,(uint)statusSize,out _))throw new AgentOpsException("application_identity");
                    var status=Marshal.PtrToStructure<ServiceStatusProcess>(statusBuffer);
                    if(status.CurrentState!=4||status.ProcessId==0)throw new AgentOpsException("application_service_down");
                    QueryServiceConfig(service,0,0,out var required);
                    if(required==0||required>64*1024)throw new AgentOpsException("application_identity");
                    var configBuffer=Marshal.AllocHGlobal(checked((int)required));
                    try
                    {
                        if(!QueryServiceConfig(service,configBuffer,required,out _))throw new AgentOpsException("application_identity");
                        var config=Marshal.PtrToStructure<ServiceConfig>(configBuffer);
                        var commandLine=Marshal.PtrToStringUni(config.BinaryPathName);
                        var path=FirstArgument(commandLine);
                        return new(serviceName,status.ProcessId,Path.GetFullPath(path));
                    }
                    finally{Marshal.FreeHGlobal(configBuffer);}
                }
                finally{Marshal.FreeHGlobal(statusBuffer);}
            }
            finally{CloseServiceHandle(service);}
        }
        finally{CloseServiceHandle(manager);}
    }

    private static string FirstArgument(string? commandLine)
    {
        if(string.IsNullOrWhiteSpace(commandLine))throw new AgentOpsException("application_identity");
        var argv=CommandLineToArgvW(commandLine,out var count);if(argv==0||count<1)throw new AgentOpsException("application_identity");
        try{return Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv))??throw new AgentOpsException("application_identity");}
        finally{LocalFree(argv);}
    }

    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint OpenSCManager(string? machineName,string? databaseName,uint desiredAccess);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint OpenService(nint manager,string serviceName,uint desiredAccess);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool QueryServiceStatusEx(nint service,uint infoLevel,nint buffer,uint bufferSize,out uint bytesNeeded);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryServiceConfig(nint service,nint serviceConfig,uint bufferSize,out uint bytesNeeded);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint service);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CommandLineToArgvW(string commandLine,out int argumentCount);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess{public uint ServiceType;public uint CurrentState;public uint ControlsAccepted;public uint Win32ExitCode;public uint ServiceSpecificExitCode;public uint CheckPoint;public uint WaitHint;public uint ProcessId;public uint ServiceFlags;}
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig{public uint ServiceType;public uint StartType;public uint ErrorControl;public nint BinaryPathName;public nint LoadOrderGroup;public uint TagId;public nint Dependencies;public nint ServiceStartName;public nint DisplayName;}
}

[SupportedOSPlatform("windows")]
internal static class AuthenticodePublisher
{
    private const string CodeSigningOid="1.3.6.1.5.5.7.3.3";
    private const int TrustSuccess=0;
    private const int CertEUntrustedRoot=unchecked((int)0x800B0109);
    private const int CertEChaining=unchecked((int)0x800B010A);

    public static string ReadValidPublisher(string path)
    {
        X509Certificate2 signer;
        try
        {
#pragma warning disable SYSLIB0057
            signer=new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        }
        catch(CryptographicException ex){throw new AgentOpsException("application_signature",inner:ex);}
        using(signer)
        {
            var now=DateTimeOffset.UtcNow;
            if(now<signer.NotBefore.ToUniversalTime()||now>signer.NotAfter.ToUniversalTime())throw new AgentOpsException("application_signature");
            var eku=signer.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            if(eku is null||!eku.EnhancedKeyUsages.Cast<Oid>().Any(oid=>StringComparer.Ordinal.Equals(oid.Value,CodeSigningOid)))throw new AgentOpsException("application_signature");
            var usage=signer.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            if(usage is not null&&(usage.KeyUsages&X509KeyUsageFlags.DigitalSignature)==0)throw new AgentOpsException("application_signature");
            var status=WinTrust.Verify(path);
            if(status is not(TrustSuccess or CertEUntrustedRoot or CertEChaining))throw new AgentOpsException("application_signature");
            var thumbprint=signer.Thumbprint?.Replace(" ",string.Empty,StringComparison.Ordinal).ToUpperInvariant();
            if(string.IsNullOrWhiteSpace(thumbprint)||thumbprint.Length%2!=0)throw new AgentOpsException("application_signature");
            return thumbprint;
        }
    }

    private static class WinTrust
    {
        private static readonly Guid Action=new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        public static int Verify(string path){using var file=new FileInfoHandle(path);using var data=new TrustDataHandle(file.Pointer);var action=Action;return WinVerifyTrust(0,ref action,data.Pointer);}
        [DllImport("wintrust.dll",ExactSpelling=true,PreserveSig=true)] private static extern int WinVerifyTrust(nint window,ref Guid actionId,nint trustData);
        private sealed class FileInfoHandle:IDisposable
        {
            public FileInfoHandle(string path){var text=Marshal.StringToCoTaskMemUni(path);Pointer=Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());Marshal.StructureToPtr(new WinTrustFileInfo{Size=(uint)Marshal.SizeOf<WinTrustFileInfo>(),FilePath=text},Pointer,false);}
            public nint Pointer{get;}
            public void Dispose(){if(Pointer==0)return;var value=Marshal.PtrToStructure<WinTrustFileInfo>(Pointer);Marshal.FreeCoTaskMem(value.FilePath);Marshal.FreeCoTaskMem(Pointer);}
        }
        private sealed class TrustDataHandle:IDisposable
        {
            public TrustDataHandle(nint file){Pointer=Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());Marshal.StructureToPtr(new WinTrustData{Size=(uint)Marshal.SizeOf<WinTrustData>(),UiChoice=2,UnionChoice=1,FileInfo=file,ProviderFlags=0x00001000},Pointer,false);}
            public nint Pointer{get;}
            public void Dispose(){if(Pointer!=0)Marshal.FreeCoTaskMem(Pointer);}
        }
        [StructLayout(LayoutKind.Sequential)] private struct WinTrustFileInfo{public uint Size;public nint FilePath;public nint FileHandle;public nint KnownSubject;}
        [StructLayout(LayoutKind.Sequential)] private struct WinTrustData{public uint Size;public nint PolicyCallbackData;public nint SipClientData;public uint UiChoice;public uint RevocationChecks;public uint UnionChoice;public nint FileInfo;public uint StateAction;public nint StateData;public nint UrlReference;public uint ProviderFlags;public uint UiContext;public nint SignatureSettings;}
    }
}

internal sealed class ApplicationCredentialRegistry
{
    private const int Iterations=240_000;
    private sealed record Entry(string ApplicationId,string Label,string CredentialId,long Generation,string State,byte[]? Salt,byte[]? Verifier,DateTimeOffset IssuedAt,DateTimeOffset UpdatedAt);
    private sealed record Store(int Version,List<Entry> Entries);
    internal sealed record Change(ApplicationAuthorizationResult Result,ApplicationSessionLease? Lease=null)
    {
        internal bool Connected=>Lease is not null;
    }
    private readonly string _path;private readonly object _gate=new();private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public ApplicationCredentialRegistry(string stateRoot)=>_path=Path.Combine(Path.GetFullPath(stateRoot),"application-authorizations.v1.json");

    public Change Apply(ApplicationAuthorizationRequest request)
    {
        if(request.Version!=ApplicationAuthorizationProtocol.Version||!StringComparer.Ordinal.Equals(request.ApplicationId,ApplicationAuthorizationProtocol.CerebralApplicationId))return Fail("application_protocol");
        return request.Action switch
        {
            "issue" when request.Credential is null=>Issue(request.ApplicationId,false),
            "rotate" when request.Credential is null=>Issue(request.ApplicationId,true),
            "status" when request.Credential is null=>Status(request.ApplicationId),
            "revoke" when request.Credential is null=>Revoke(request.ApplicationId),
            "connect" when request.Credential is not null=>Connect(request.ApplicationId,request.Credential),
            _=>Fail("invalid")
        };
    }

    public IReadOnlyList<ApplicationAuthorizationResult> OwnerStatus()
    {
        lock(_gate)
        {
            var entry=Read().Entries.SingleOrDefault(value=>value.ApplicationId==ApplicationAuthorizationProtocol.CerebralApplicationId);
            return entry is null
                ?[new(true,"absent",ApplicationId:ApplicationAuthorizationProtocol.CerebralApplicationId,Label:"Pegasus Cerebral")]
                :[new(true,entry.State,ApplicationId:entry.ApplicationId,Label:entry.Label,CredentialId:entry.CredentialId,Generation:entry.Generation)];
        }
    }

    public ApplicationAuthorizationResult OwnerRevoke(string applicationId)=>Revoke(applicationId).Result;

    internal void RequireActive(ApplicationSessionLease lease)
    {
        lock(_gate)
        {
            var entry=Read().Entries.SingleOrDefault(value=>value.ApplicationId==lease.ApplicationId);
            if(lease.ApplicationId!=ApplicationAuthorizationProtocol.CerebralApplicationId||
                entry is null||entry.State!="active"||entry.CredentialId!=lease.CredentialId||entry.Generation!=lease.Generation)
                throw new AgentOpsException("application_auth");
            // This is admission of one operation, serialized with revoke/rotate.
            // Revocation cannot undo work that passed this check before it completed.
        }
    }

    private Change Issue(string applicationId,bool rotate)
    {
        lock(_gate)
        {
            var store=Read();var existing=store.Entries.SingleOrDefault(entry=>entry.ApplicationId==applicationId);
            if(!rotate&&existing is not null&&existing.State=="active")return Fail("precondition");
            if(rotate&&(existing is null||existing.State!="active"))return Fail("precondition");
            var secretBytes=RandomNumberGenerator.GetBytes(32);var credential=Base64Url(secretBytes);var salt=RandomNumberGenerator.GetBytes(32);
            var verifier=Rfc2898DeriveBytes.Pbkdf2(secretBytes,salt,Iterations,HashAlgorithmName.SHA256,32);
            CryptographicOperations.ZeroMemory(secretBytes);
            var generation=(existing?.Generation??0)+1;var credentialId="cac_"+Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));var now=DateTimeOffset.UtcNow;
            var entry=new Entry(applicationId,"Pegasus Cerebral",credentialId,generation,"active",salt,verifier,existing?.IssuedAt??now,now);
            store.Entries.RemoveAll(value=>value.ApplicationId==applicationId);store.Entries.Add(entry);Write(store);
            return new(new(true,rotate?"rotated":"issued",ApplicationId:applicationId,Label:entry.Label,CredentialId:credentialId,Generation:generation,Credential:credential));
        }
    }

    private Change Status(string applicationId)
    {
        lock(_gate)
        {
            var entry=Read().Entries.SingleOrDefault(value=>value.ApplicationId==applicationId);
            return entry is null?new(new(true,"absent",ApplicationId:applicationId,Label:"Pegasus Cerebral")):
                new(new(true,entry.State,ApplicationId:applicationId,Label:entry.Label,CredentialId:entry.CredentialId,Generation:entry.Generation));
        }
    }

    private Change Revoke(string applicationId)
    {
        if(!StringComparer.Ordinal.Equals(applicationId,ApplicationAuthorizationProtocol.CerebralApplicationId))return Fail("invalid");
        lock(_gate)
        {
            var store=Read();var index=store.Entries.FindIndex(value=>value.ApplicationId==applicationId);
            if(index<0)return new(new(true,"absent",ApplicationId:applicationId,Label:"Pegasus Cerebral"));
            var current=store.Entries[index];if(current.State=="revoked")return new(new(true,"revoked",ApplicationId:applicationId,Label:current.Label,CredentialId:current.CredentialId,Generation:current.Generation));
            var revoked=current with{State="revoked",Salt=null,Verifier=null,UpdatedAt=DateTimeOffset.UtcNow};store.Entries[index]=revoked;Write(store);
            return new(new(true,"revoked",ApplicationId:applicationId,Label:revoked.Label,CredentialId:revoked.CredentialId,Generation:revoked.Generation));
        }
    }

    private Change Connect(string applicationId,string credential)
    {
        if(credential.Length is <40 or >128)return Fail("application_auth");
        lock(_gate)
        {
            var entry=Read().Entries.SingleOrDefault(value=>value.ApplicationId==applicationId);
            if(entry is null||entry.State!="active"||entry.Salt is null||entry.Verifier is null)return Fail("application_auth");
            byte[] supplied;try{supplied=FromBase64Url(credential);}catch(FormatException){return Fail("application_auth");}
            try
            {
                if(supplied.Length!=32)return Fail("application_auth");
                var verifier=Rfc2898DeriveBytes.Pbkdf2(supplied,entry.Salt,Iterations,HashAlgorithmName.SHA256,32);
                try{if(!CryptographicOperations.FixedTimeEquals(verifier,entry.Verifier))return Fail("application_auth");}
                finally{CryptographicOperations.ZeroMemory(verifier);}
            }
            finally{CryptographicOperations.ZeroMemory(supplied);}
            return new(new(true,"ready",ApplicationId:applicationId,Label:entry.Label,CredentialId:entry.CredentialId,Generation:entry.Generation,SessionId:"cas_"+Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))),new(applicationId,entry.CredentialId,entry.Generation));
        }
    }

    private Store Read()
    {
        if(!File.Exists(_path))return new(1,[]);
        PathNormalizer.RejectAbsoluteReparseComponents(_path);if((File.GetAttributes(_path)&FileAttributes.ReparsePoint)!=0)throw new AgentOpsException("integrity");
        var store=JsonSerializer.Deserialize<Store>(File.ReadAllBytes(_path),Json)??throw new AgentOpsException("integrity");
        if(store.Version!=1||store.Entries is null||store.Entries.Count>8||
            store.Entries.Any(entry=>entry is null||entry.CredentialId is null)||
            store.Entries.GroupBy(entry=>entry.ApplicationId,StringComparer.Ordinal).Any(group=>group.Count()!=1)||
            store.Entries.Any(entry=>entry.ApplicationId!=ApplicationAuthorizationProtocol.CerebralApplicationId||entry.Label!="Pegasus Cerebral"||
                entry.Generation<1||entry.CredentialId.Length!=28||!entry.CredentialId.StartsWith("cac_",StringComparison.Ordinal)||
                entry.CredentialId[4..].Any(character=>character is not(>='0' and <='9') and not(>='a' and <='f'))||
                entry.State is not("active" or "revoked")||
                entry.State=="active"&&(entry.Salt?.Length!=32||entry.Verifier?.Length!=32)||
                entry.State=="revoked"&&(entry.Salt is not null||entry.Verifier is not null)))throw new AgentOpsException("integrity");
        return store;
    }

    private void Write(Store store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var temp=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(store,Json);using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}File.Move(temp,_path,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    private static Change Fail(string error)=>new(new(false,"failed",error));
    private static string Base64Url(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] FromBase64Url(string value){var padded=value.Replace('-','+').Replace('_','/');padded+=new string('=',(4-padded.Length%4)%4);return Convert.FromBase64String(padded);}
}

internal sealed class ApplicationAuthorizationCoordinator
{
    private readonly ApplicationCredentialRegistry _registry;private readonly IApplicationPeerVerifier _peerVerifier;
    [SupportedOSPlatform("windows")]
    public ApplicationAuthorizationCoordinator(string stateRoot,IApplicationPeerVerifier? peerVerifier=null){_registry=new(stateRoot);_peerVerifier=peerVerifier??new WindowsApplicationPeerVerifier();}
    internal ApplicationAuthorizationCoordinator(ApplicationCredentialRegistry registry,IApplicationPeerVerifier peerVerifier){_registry=registry;_peerVerifier=peerVerifier;}
    public ApplicationCredentialRegistry.Change Apply(ApplicationAuthorizationRequest request,LocalPeer peer)
    {
        try{_peerVerifier.Verify(peer,request.ApplicationId);return _registry.Apply(request);}
        catch(AgentOpsException){return new(new(false,"failed","application_auth"));}
        catch(Exception ex)when(ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException){return new(new(false,"failed","application_auth"));}
    }

    internal ApplicationAuthorizedSession OpenSession(ApplicationCredentialRegistry.Change change,
        Func<OperationRequest,CancellationToken,Task<OperationReceipt>> dispatch)
    {
        if(!change.Result.Ok||change.Lease is not { } lease)throw new AgentOpsException("application_auth");
        return new(_registry,lease,dispatch);
    }
}

// A connection keeps its original authenticated generation. Checking it before
// every new dispatch never reconnects, renews or replays a submitted operation.
internal sealed class ApplicationAuthorizedSession(ApplicationCredentialRegistry registry,ApplicationSessionLease lease,
    Func<OperationRequest,CancellationToken,Task<OperationReceipt>> dispatch)
{
    internal Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try{registry.RequireActive(lease);}
        catch(Exception ex)when(ex is AgentOpsException or ArgumentException or IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or System.Security.SecurityException)
        {
            return Task.FromResult(new OperationReceipt(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),
                "failed",OperationNames.Public(request),"application_auth"));
        }
        // Callback failures retain the broker's own evidence; they are not
        // misreported as an authentication failure and are never retried here.
        return dispatch(request,cancellationToken);
    }
}

[SupportedOSPlatform("windows")]
internal static class ApplicationAuthorizationPipeHost
{
    private const string LocalServiceSid="S-1-5-19";
    public static async Task RunAsync(string pipeName,ApplicationAuthorizationCoordinator coordinator,AuthenticatedServiceBroker broker,CancellationToken cancellationToken)
    {
        using var connections=new SemaphoreSlim(8,8);var handlers=new HashSet<Task>();
        while(!cancellationToken.IsCancellationRequested)
        {
            await connections.WaitAsync(cancellationToken);var pipe=LocalPipeFactory.Create(pipeName,LocalServiceSid);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);var handler=HandleAsync(pipe);lock(handlers)handlers.Add(handler);
                _=handler.ContinueWith(task=>{lock(handlers)handlers.Remove(task);connections.Release();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
            catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){await pipe.DisposeAsync();connections.Release();break;}
            catch{await pipe.DisposeAsync();connections.Release();throw;}
        }
        Task[] remaining;lock(handlers)remaining=handlers.ToArray();await Task.WhenAll(remaining);

        async Task HandleAsync(NamedPipeServerStream pipe)
        {
            await using(pipe)
            {
                try
                {
                    var peer=LocalIdentity.Capture(pipe);var request=await BinaryProtocol.ReadAsync<ApplicationAuthorizationRequest>(pipe,cancellationToken);var change=coordinator.Apply(request,peer);
                    await BinaryProtocol.WriteAsync(pipe,change.Result,cancellationToken);if(!change.Connected)return;
                    var session=coordinator.OpenSession(change,broker.HandleApplicationAsync);
                    while(pipe.IsConnected)
                    {
                        OperationRequest operation;try{operation=await BinaryProtocol.ReadAsync<OperationRequest>(pipe,cancellationToken);}catch(EndOfStreamException){return;}
                        var receipt=await session.DispatchAsync(operation,cancellationToken);await BinaryProtocol.WriteAsync(pipe,receipt,cancellationToken);
                    }
                }
                catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){}
                catch(Exception ex)when(ex is IOException or InvalidDataException or EndOfStreamException or AgentOpsException){}
            }
        }
    }
}
