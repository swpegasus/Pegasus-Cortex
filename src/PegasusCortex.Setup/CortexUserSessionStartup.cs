using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexUserSessionStartupReceiptV1(
    string ReleaseVersion, string Disposition, int ProcessId, int SessionId, bool Elevated, string Service, string Host,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? DashboardProcessId=null);

internal sealed record CortexStartupToken(string Sid, int SessionId, bool Elevated, int Integrity, bool Primary);
internal sealed record CortexStartupHost(string InstallRoot, string ReleaseVersion, string HostComponentVersion, string Executable, string StartupExecutable, string OwnerSid,
    string PaoExecutable, string ServiceExecutable);
internal interface ICortexUserSessionStartupPlatform : IDisposable
{
    CortexStartupToken Caller { get; }
    CortexStartupToken? Linked { get; }
    bool Interactive { get; }
    int? FindExisting(CortexStartupHost host);
    int Launch(CortexStartupHost host, bool linked, bool dashboard);
    int RequireReady(CortexStartupHost host);
}

/// <summary>Starts only the completed installation's host in its owner's existing interactive session.</summary>
public static class CortexUserSessionStartup
{
    private const long MaximumInstalledPeBytes=128L*1024*1024;

    public static CortexUserSessionStartupReceiptV1 Start(string installRoot, bool dashboard=false)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("User-session startup requires Windows.");
        using var platform=AtStage("caller-token",()=>new NativeStartupPlatform());
        RequireInteractiveCaller(platform.Caller,platform.Interactive);
        var host=AtStage("installed-admission",()=>AdmitInstalledHost(installRoot,platform.Caller.Sid));
        return StartAdmitted(host,platform,dashboard);
    }

    internal static CortexStartupToken InspectCurrentTokenReadOnly()=>NativeStartupPlatform.InspectCurrentReadOnly();

    internal static CortexUserSessionStartupReceiptV1 StartAdmitted(
        CortexStartupHost host,ICortexUserSessionStartupPlatform platform,bool dashboard=false)
    {
        var linked=AtStage("select-user-token",()=>SelectToken(host.OwnerSid,platform.Caller,platform.Interactive,
            platform.Caller.Elevated?platform.Linked:null));
        var existing=AtStage("existing-host-identity",()=>platform.FindExisting(host));
        _=existing??AtStage("launch-user-host",()=>platform.Launch(host,linked,dashboard:false));
        var pid=AtStage("host-service-readiness",()=>platform.RequireReady(host));
        int? dashboardPid=dashboard?AtStage("launch-dashboard",()=>platform.Launch(host,linked,dashboard:true)):null;
        return new(host.ReleaseVersion,existing is null?"started":"already-running",pid,platform.Caller.SessionId,false,"optional","ready",dashboardPid);
    }

    internal static bool SelectToken(string ownerSid,CortexStartupToken caller,bool interactive,CortexStartupToken? linked)
    {
        RequireInteractiveCaller(caller,interactive);
        if(caller.Sid!=ownerSid)throw new UnauthorizedAccessException("Startup requires the installation owner's existing user session.");
        if(!caller.Elevated){RequireMedium(caller,ownerSid,caller.SessionId);return false;}
        if(linked is null)throw new UnauthorizedAccessException("The elevated installer has no linked non-elevated user token. Start Cortex from the signed-in user's session.");
        RequireMedium(linked,ownerSid,caller.SessionId);
        return true;
    }

    private static void RequireInteractiveCaller(CortexStartupToken caller,bool interactive)
    {
        if(!interactive||caller.SessionId<=0||!caller.Primary||
            caller.Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            throw new UnauthorizedAccessException("Cortex user startup requires an active signed-in user session, not SYSTEM or a service account.");
        _=new SecurityIdentifier(caller.Sid);
    }

    private static void RequireMedium(CortexStartupToken token,string sid,int session)
    {
        if(token.Sid!=sid||token.SessionId!=session||token.Elevated||token.Integrity!=0x2000||!token.Primary)
            throw new UnauthorizedAccessException("The selected Cortex user token is not the matching non-elevated medium-integrity session token.");
    }

    private static CortexStartupHost AdmitInstalledHost(string installRoot,string ownerSid)
    {
        var root=CortexCodexFileUpdate.RequireCanonical(installRoot);
        CortexCodexFileUpdate.RequireDirectory(root,allowMissing:false);
        using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
        using(var key=machine.OpenSubKey(CortexDataLocation.RegistryKeyPath,writable:false))
        {
            if(key?.GetValue(CortexDataLocation.InstallLocationValueName,null,RegistryValueOptions.DoNotExpandEnvironmentNames) is not string registered||
                key.GetValueKind(CortexDataLocation.InstallLocationValueName)!=RegistryValueKind.String||
                !Equal(root,CortexCodexFileUpdate.RequireCanonical(registered)))
                throw new InvalidDataException("The requested user host does not match the machine-registered Cortex installation.");
        }
        var dataRoot=CortexDataLocation.ResolveMachineDataRoot(root);
        var (catalog,registration)=ReadInstalledMetadata(dataRoot,RequireOwnedFile);
        var role=catalog.Roles.Single(value=>value.Role=="host");
        var launcher=catalog.Roles.Single(value=>value.Role=="launcher");
        var pao=catalog.Roles.Single(value=>value.Role=="pao");
        var service=catalog.Roles.Single(value=>value.Role=="service");
        var expected=CortexProductCatalogV1.StableRolePaths["host"];
        var expectedService=CortexProductCatalogV1.StableRolePaths["service"];
        if(role.RelativePath!=expected||
            pao.RelativePath!="bin/pao.exe"||
            service.RelativePath!=expectedService||
            registration.Contract!=CortexWindowsProductRegistration.ReceiptContract||registration.Version!=1||
            registration.ReleaseVersion!=catalog.ReleaseVersion||registration.RunValueName!="Pegasus Cortex")
            throw new InvalidDataException("The installed catalog and completed owner registration do not agree.");
        var executable=Path.Combine(root,expected.Replace('/',Path.DirectorySeparatorChar));
        var startupExecutable=Path.Combine(root,CortexProductCatalogV1.StableRolePaths["launcher"].Replace('/',Path.DirectorySeparatorChar));
        var paoExecutable=Path.Combine(root,"bin","pao.exe");
        var serviceExecutable=Path.Combine(root,expectedService.Replace('/',Path.DirectorySeparatorChar));
        if(launcher.RelativePath!=CortexProductCatalogV1.StableRolePaths["launcher"]||
            !Equal(registration.RunExecutable,executable)||registration.ServiceName!=CortexProductIdentity.ServiceName||
            !Equal(registration.ServiceExecutable,serviceExecutable))
            throw new InvalidDataException("The registered startup executable does not match the installed host role.");
        using(var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",writable:false))
        {
            var command=run?.GetValue("Pegasus Cortex",null,RegistryValueOptions.DoNotExpandEnvironmentNames);
            if(command is not null && (command is not string text ||
                run!.GetValueKind("Pegasus Cortex")!=RegistryValueKind.String||
                !MatchesUserSessionRegistration(registration.RunExecutable,text,executable)))
                throw new InvalidDataException("The installation owner's Cortex login startup registration conflicts with the admitted host.");
        }
        RequireOwnedFile(root,executable,MaximumInstalledPeBytes);
        RequireOwnedFile(root,startupExecutable,MaximumInstalledPeBytes);
        RequireOwnedFile(root,paoExecutable,MaximumInstalledPeBytes);
        RequireOwnedFile(root,serviceExecutable,MaximumInstalledPeBytes);
        RequireComponentVersion(executable,role.ComponentVersion,"host");
        RequireComponentVersion(startupExecutable,launcher.ComponentVersion,"launcher");
        RequireComponentVersion(paoExecutable,pao.ComponentVersion,"pao");
        RequireComponentVersion(serviceExecutable,service.ComponentVersion,"service");
        var publisherPath=Path.Combine(root,"trust","pegasus-prod-alpha-publisher.cer");
        RequireOwnedFile(root,publisherPath,16*1024);
        using var publisher=X509CertificateLoader.LoadCertificateFromFile(publisherPath);
        if(publisher.Thumbprint!=CortexProductIdentity.ProdAlphaPublisherThumbprint||publisher.HasPrivateKey)
            throw new InvalidDataException("The installed Cortex publisher identity is invalid.");
        CortexSignedProductPackageVerifier.VerifyAuthenticode(executable,publisher);
        CortexSignedProductPackageVerifier.VerifyAuthenticode(startupExecutable,publisher);
        CortexSignedProductPackageVerifier.VerifyAuthenticode(paoExecutable,publisher);
        CortexSignedProductPackageVerifier.VerifyAuthenticode(serviceExecutable,publisher);
        return new(root,catalog.ReleaseVersion,role.ComponentVersion,executable,startupExecutable,ownerSid,paoExecutable,serviceExecutable);
    }

    internal static (CortexProductCatalogV1 Catalog,CortexWindowsRegistrationReceiptV1 Registration)
        ReadInstalledMetadata(string dataRoot,Action<string,string,long> requireOwnedFile)
    {
        ArgumentNullException.ThrowIfNull(requireOwnedFile);
        var root=CortexCodexFileUpdate.RequireCanonical(dataRoot);
        var install=Path.Combine(root,"install");
        // Both records follow the selected machine data root. Optional Codex
        // integration has no part in admitting ordinary owner-host startup.
        var catalog=ReadMetadata<CortexProductCatalogV1>(root,Path.Combine(install,"active.v1.json"),
            32*1024,web:true,requireOwnedFile:requireOwnedFile).Validate();
        var registration=ReadMetadata<CortexWindowsRegistrationReceiptV1>(root,Path.Combine(install,"registration.v1.json"),
            8192,requireOwnedFile:requireOwnedFile);
        if(registration.DataRoot is not null&&!Equal(registration.DataRoot,root))
            throw new InvalidDataException("The completed registration does not match the selected Cortex data root.");
        return (catalog,registration);
    }

    internal static void RequireDiagnostic(string json,string hostComponentVersion,int exitCode)
    {
        if(json.Length>16*1024)throw new InvalidDataException("Cortex startup diagnostic exceeded its bound.");
        using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=16});
        var value=document.RootElement;
        // Recognized scheduler modes share this diagnostic/identity contract;
        // readiness does not imply an empty or globally serialized mutation queue.
        if(exitCode!=0||value.ValueKind!=JsonValueKind.Object||
            !value.TryGetProperty("ok",out var ok)||ok.ValueKind!=JsonValueKind.True||
            !TextIs(value,"state","done")||!TextIs(value,"op","diag")||
            (value.TryGetProperty("partial",out var partial)&&partial.ValueKind!=JsonValueKind.False)||
            (value.TryGetProperty("err",out var error)&&error.ValueKind!=JsonValueKind.Null)||
            !value.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Object||
            !TextIs(data,"product",CortexProductIdentity.Product)||
            !TextIs(data,"store","sqlite-wal")||
            !(TextIs(data,"fifo","single-owner")||TextIs(data,"fifo","ordered-conflicts-bounded"))||
            data.TryGetProperty("service",out _)||data.TryGetProperty("tray",out _)||data.TryGetProperty("queued",out _))
            throw new InvalidOperationException("Cortex user-host diagnostics did not confirm the installed execution engine is ready.");
        RequireUnique(value);RequireUnique(data);
        var actualVersion=RequiredText(data,"product_version");
        if(actualVersion!=hostComponentVersion)
            throw new InvalidDataException($"The admitted host component version is '{hostComponentVersion}', but its diagnostic reported '{actualVersion}'.");
        var actualProtocol=RequiredText(data,"protocol");
        if(actualProtocol!=CortexProductIdentity.ExecutionProtocol)
            throw new CortexProductContractMismatchException("execution","protocol",CortexProductIdentity.ExecutionProtocol,actualProtocol);
        if(!data.TryGetProperty("mcp_schema",out var schema)||schema.ValueKind!=JsonValueKind.Number||!schema.TryGetInt32(out var actualSchema))
            throw new InvalidDataException("The PAO diagnostic omitted its numeric schema contract.");
        if(actualSchema!=CortexProductIdentity.PaoSchema)
            throw new CortexProductContractMismatchException("pao","schema",
                CortexProductIdentity.PaoSchema.ToString(CultureInfo.InvariantCulture),actualSchema.ToString(CultureInfo.InvariantCulture));
    }

    internal static bool MatchesUserSessionRegistration(
        string receiptExecutable,
        string runCommand,
        string hostExecutable) =>
        Equal(receiptExecutable,hostExecutable)&&
        string.Equals(runCommand,'"'+hostExecutable+'"',StringComparison.Ordinal);

    private static bool TextIs(JsonElement value,string property,string expected)=>
        value.TryGetProperty(property,out var text)&&text.ValueKind==JsonValueKind.String&&text.GetString()==expected;
    private static string RequiredText(JsonElement value,string property)=>
        value.TryGetProperty(property,out var text)&&text.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(text.GetString())
            ?text.GetString()!:throw new InvalidDataException($"Cortex startup diagnostic omitted '{property}'.");
    private static void RequireComponentVersion(string path,string expected,string role)
    {
        var actual=FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if(actual!=expected)
            throw new InvalidDataException($"The admitted '{role}' component version is '{expected}', but the executable reports '{actual??"absent"}'.");
    }
    private static void RequireUnique(JsonElement value)
    {
        if(value.EnumerateObject().Select(item=>item.Name).Distinct(StringComparer.Ordinal).Count()!=value.EnumerateObject().Count())
            throw new InvalidDataException("Cortex startup diagnostic contains duplicate fields.");
    }

    private static T ReadMetadata<T>(string root,string path,int maximum,bool web=false,
        Action<string,string,long>? requireOwnedFile=null)
    {
        (requireOwnedFile??RequireOwnedFile)(root,path,maximum);
        var bytes=CortexCodexFileUpdate.ReadBounded(path,maximum);
        using var document=JsonDocument.Parse(bytes);
        if(document.RootElement.ValueKind!=JsonValueKind.Object||
            document.RootElement.EnumerateObject().Select(value=>value.Name).Distinct(StringComparer.Ordinal).Count()!=
            document.RootElement.EnumerateObject().Count())throw new InvalidDataException("Installed startup metadata is malformed.");
        return JsonSerializer.Deserialize<T>(bytes,new JsonSerializerOptions(web?JsonSerializerDefaults.Web:JsonSerializerDefaults.General)
            {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Installed startup metadata is absent.");
    }

    internal static void RequireOwnedFile(string root,string path,long maximum)
    {
        var canonical=CortexCodexFileUpdate.RequireTarget(path,Path.GetFileName(path));
        var relative=Path.GetRelativePath(root,canonical);
        if(Path.IsPathRooted(relative)||relative==".."||relative.StartsWith("..\\",StringComparison.Ordinal))
            throw new InvalidDataException("The installed startup path escaped Cortex.");
        var file=new FileInfo(canonical);
        if(!file.Exists||file.Length<1||file.Length>maximum)throw new InvalidDataException("Installed startup content is absent or exceeds its bound.");
        _=AtStage("immutable-file-acl",()=>
        {RequireImmutableAcl(file.GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access));return true;});
        for(var directory=file.Directory;directory is not null;directory=directory.Parent)
        {
            var inspectedDirectory=directory;
            var stage=Equal(directory.FullName,Path.Combine(root,"state"))?"state-routing-acl":
                Equal(directory.FullName,root)?"install-root-acl":"immutable-parent-acl";
            _=AtStage(stage,()=>
            {RequireImmutableAcl(inspectedDirectory.GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access));return true;});
            if(Equal(directory.FullName,root))return;
        }
        throw new InvalidDataException("The installed startup path has no Cortex boundary.");
    }

    private static void RequireImmutableAcl(FileSystemSecurity security)
    {
        var descriptor=new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0);
        if(descriptor.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544")||descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("Installed Cortex startup content is not administrator-owned.");
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|
            FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&writes)!=0&&
                rule.IdentityReference.Value is not("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("Installed Cortex startup content is writable by an unowned principal.");
    }

    private static bool Equal(string left,string right)=>string.Equals(
        Path.TrimEndingDirectorySeparator(left),Path.TrimEndingDirectorySeparator(right),StringComparison.OrdinalIgnoreCase);

    private static T AtStage<T>(string stage,Func<T> action)
    {
        try{return action();}
        catch(CortexProductContractMismatchException exception)
        {
            exception.Data["CortexStartupStage"]=stage;
            throw;
        }
        catch(Exception exception)when(exception is UnauthorizedAccessException or Win32Exception or IOException or InvalidOperationException)
        {
            if(exception.Data.Contains("CortexStartupStage"))throw;
            var message=$"Cortex startup stage={stage}: "+exception.Message[..Math.Min(exception.Message.Length,256)];
            Exception staged=exception switch
            {
                UnauthorizedAccessException=>new UnauthorizedAccessException(message,exception),
                Win32Exception native=>new Win32Exception(native.NativeErrorCode,message),
                InvalidDataException=>new InvalidDataException(message,exception),
                IOException=>new IOException(message,exception),
                _=>new InvalidOperationException(message,exception)
            };
            staged.Data["CortexStartupStage"]=stage;
            throw staged;
        }
    }

    internal sealed class NativeStartupPlatform:ICortexUserSessionStartupPlatform
    {
        internal static string ReadProcessImage(uint processId)
        {
            // Image-only inspection needs no VM/module access or token rights.
            // Keep this query separate from authenticated host/token admission.
            using var process=OpenProcess(0x1000,false,processId);
            if(process.IsInvalid)throw NativeFailure("The process image handle could not be queried.");
            var image=new StringBuilder(32768);var length=image.Capacity;
            if(!QueryFullProcessImageNameW(process.DangerousGetHandle(),0,image,ref length))
                throw NativeFailure("The process executable path could not be queried.");
            return image.ToString();
        }

        private readonly SafeAccessTokenHandle _current;
        private SafeAccessTokenHandle? _linked;
        public CortexStartupToken Caller{get;}
        public bool Interactive{get;}
        public CortexStartupToken? Linked
        {
            get
            {
                if(!Caller.Elevated)return null;
                if(_linked is null)
                {
                    var pointer=ReadToken(_current,19);
                    try
                    {
                        using var linked=new SafeAccessTokenHandle(Marshal.ReadIntPtr(pointer));
                        // Explicit primary-token rights required by CreateProcessWithTokenW; no privilege changes.
                        if(!DuplicateTokenEx(linked,0x000bu,IntPtr.Zero,2,1,out _linked))
                            throw NativeFailure("The linked non-elevated primary token could not be selected.");
                    }
                    finally{Marshal.FreeHGlobal(pointer);}
                }
                return Inspect(_linked);
            }
        }

        internal NativeStartupPlatform()
        {
            if(!OpenProcessToken(GetCurrentProcess(),0x000a,out _current))throw NativeFailure("The installing user token is unavailable.");
            try
            {
                Caller=Inspect(_current);
                Interactive=Caller.SessionId==Process.GetCurrentProcess().SessionId&&IsActiveSession(Caller.SessionId);
            }
            catch{_current.Dispose();throw;}
        }

        public int? FindExisting(CortexStartupHost host)
        {
            using var pipe=new NamedPipeClientStream(".",CortexProductIdentity.UserExecutionPipe(Caller.SessionId),
                PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            try{pipe.Connect(150);}
            catch(TimeoutException){return null;}
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var serverPid)||serverPid==0)
                throw NativeFailure("The Cortex user-execution endpoint identity could not be established.");
            using var process=OpenProcess(0x1000,false,serverPid); // PROCESS_QUERY_LIMITED_INFORMATION, never PROCESS_ALL_ACCESS.
            if(process.IsInvalid)throw NativeFailure("The Cortex user-execution endpoint process could not be queried.");
            if(!OpenProcessToken(process.DangerousGetHandle(),0x0008,out var token))
                throw NativeFailure("The Cortex user-execution endpoint token could not be inspected.");
            using(token)RequireMedium(Inspect(token),host.OwnerSid,Caller.SessionId);
            var image=new StringBuilder(32768);var imageLength=image.Capacity;
            if(!QueryFullProcessImageNameW(process.DangerousGetHandle(),0,image,ref imageLength))
                throw NativeFailure("The Cortex user-execution endpoint image could not be queried.");
            if(!Equal(image.ToString(),host.Executable))
                throw new InvalidOperationException("The active Cortex user-execution endpoint belongs to a different host. Complete the product update before starting this release.");
            return checked((int)serverPid);
        }

        public int RequireReady(CortexStartupHost host)
        {
            using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try{return RequireReadyAsync(host,bound.Token).GetAwaiter().GetResult();}
            catch(OperationCanceledException)
            {throw new TimeoutException("Cortex did not confirm its user host and execution service ready within 20 seconds.");}
        }

        private async Task<int> RequireReadyAsync(CortexStartupHost host,CancellationToken cancellation)
        {
            int? pid;
            while((pid=FindExisting(host)) is null)
            {
                await Task.Delay(100,cancellation).ConfigureAwait(false);
            }
            // This fixed read-only diagnostic may run elevated, but never starts interactive work.
            // The actual daemon was independently proved to be the matching medium-token pipe server.
            using var diagnostic=new Process{StartInfo=new ProcessStartInfo(host.PaoExecutable)
            {
                UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(host.PaoExecutable)!,
                RedirectStandardOutput=true,RedirectStandardError=true
            }};
            diagnostic.StartInfo.ArgumentList.Add("--json");diagnostic.StartInfo.ArgumentList.Add("diag");
            cancellation.ThrowIfCancellationRequested();
            if(!diagnostic.Start())throw new InvalidOperationException("The admitted Cortex diagnostic could not start.");
            try
            {
                var output=ReadBoundedAsync(diagnostic.StandardOutput,16*1024,cancellation);
                var errors=ReadBoundedAsync(diagnostic.StandardError,4096,cancellation);
                await Task.WhenAll(output,errors,diagnostic.WaitForExitAsync(cancellation)).ConfigureAwait(false);
                if(errors.Result.Length!=0)throw new InvalidOperationException("Cortex startup diagnostics reported an error; consult product diagnostics.");
                RequireDiagnostic(output.Result,host.HostComponentVersion,diagnostic.ExitCode);
                var current=FindExisting(host);
                if(current!=pid)throw new InvalidOperationException("The Cortex user-execution host changed during its startup check.");
                return pid.Value;
            }
            finally
            {
                if(!diagnostic.HasExited)
                {
                    diagnostic.Kill();
                    if(!diagnostic.WaitForExit(2000))
                        throw new InvalidOperationException("The bounded Cortex startup diagnostic did not exit after cancellation.");
                }
            }
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader,int maximum,CancellationToken cancellation)
        {
            var text=new StringBuilder();var chunk=new char[1024];
            while(true)
            {
                var count=await reader.ReadAsync(chunk.AsMemory(),cancellation).ConfigureAwait(false);
                if(count==0)return text.ToString();
                if(text.Length+count>maximum)throw new InvalidDataException("Cortex startup diagnostic exceeded its output bound.");
                text.Append(chunk,0,count);
            }
        }

        public int Launch(CortexStartupHost host,bool linked,bool dashboard)
        {
            var token=linked?_linked??throw new UnauthorizedAccessException("No linked user token was selected."):_current;
            RequireMedium(Inspect(token),host.OwnerSid,Caller.SessionId);
            if(!CreateEnvironmentBlock(out var environment,token,false))throw NativeFailure("The non-elevated user's environment could not be created.");
            ProcessInformation process=default;
            var resumed=false;var shellBroker=false;int? createdPid=null;
            try
            {
                if(!IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out var inJob))throw NativeFailure("Installer process containment could not be checked.");
                var flags=0x00000400u|0x00000004u|0x00000200u; // Unicode environment, suspended, new process group.
                if(inJob)flags|=0x01000000; // Never attach the resident host to installer lifetime.
                var startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfo>(),Desktop=@"winsta0\default"};
                var command=new StringBuilder('"'+host.Executable+'"'+(dashboard?" --dashboard":string.Empty));
                var cwd=Path.GetDirectoryName(host.Executable)!;
                var created=linked
                    ?CreateProcessWithTokenW(token,0,host.Executable,command,flags,environment,cwd,ref startup,out process)
                    :CreateProcessW(host.Executable,command,IntPtr.Zero,IntPtr.Zero,false,flags,environment,cwd,ref startup,out process);
                if(!created)throw NativeFailure("The admitted non-elevated Cortex host could not be created.");
                if(!OpenProcessToken(process.Process,0x0008,out var childToken))throw NativeFailure("The created Cortex host token could not be verified.");
                using(childToken)RequireMedium(Inspect(childToken),host.OwnerSid,Caller.SessionId);
                var image=new StringBuilder(512);var imageLength=image.Capacity;
                if(!QueryFullProcessImageNameW(process.Process,0,image,ref imageLength)||!Equal(image.ToString(),host.Executable))
                    throw new InvalidOperationException("The created process does not match the admitted Cortex host.");
                if(!IsProcessInJob(process.Process,IntPtr.Zero,out var childInJob))
                    throw NativeFailure("The Cortex host containment could not be checked.");
                if(childInJob)
                {
                    if(dashboard)throw new InvalidOperationException("The Cortex dashboard could not be detached from installer containment.");
                    // Some installer hosts forbid BREAKAWAY_FROM_JOB even for an admitted child.
                    // Discard the still-suspended child and ask the signed-in Windows shell to
                    // launch the exact admitted host outside the installer's lifetime. RequireReady
                    // subsequently proves the medium-token pipe owner, exact image, and service.
                    shellBroker=true;
                }
                else
                {
                    if(ResumeThread(process.Thread)==uint.MaxValue)throw NativeFailure("The verified Cortex host could not be resumed.");
                    resumed=true;
                    if(WaitForSingleObject(process.Process,1000)==0&&dashboard)
                        throw new InvalidOperationException("The Cortex user process exited during startup; consult its diagnostics.");
                    // A concurrent daemon may own the session mutex while still initializing.
                    // A duplicate daemon's early exit is not readiness: RequireReady must still
                    // prove the exact medium-token pipe server and its engine within the bound.
                    createdPid=checked((int)process.ProcessId);
                }
            }
            finally
            {
                if(process.Process!=IntPtr.Zero)
                {
                    if(!resumed)_=TerminateProcess(process.Process,8);
                    CloseHandle(process.Process);
                }
                if(process.Thread!=IntPtr.Zero)CloseHandle(process.Thread);
                DestroyEnvironmentBlock(environment);
            }
            return shellBroker?LaunchHostThroughSignedInShell(host):createdPid!.Value;
        }

        private static int LaunchHostThroughSignedInShell(CortexStartupHost host)
        {
            try
            {
                CortexDesktopResidentLauncher.Launch(host.Executable);
                // The broker does not return the child PID. StartAdmitted intentionally ignores
                // this value for the resident host and obtains the authenticated pipe PID instead.
                return 0;
            }
            catch(Exception exception)
            {throw new InvalidOperationException("The signed-in Windows shell could not start the admitted Cortex host.",exception);}
        }

        public void Dispose(){_linked?.Dispose();_current.Dispose();}

        internal static CortexStartupToken InspectCurrentReadOnly()
        {
            if(!OpenProcessToken(GetCurrentProcess(),0x0008,out var token))
                throw NativeFailure("The current process token could not be inspected.");
            using(token)return Inspect(token);
        }

        private static CortexStartupToken Inspect(SafeAccessTokenHandle token)
        {
            string sid;var user=ReadToken(token,1);
            try{sid=new SecurityIdentifier(Marshal.ReadIntPtr(user)).Value;}finally{Marshal.FreeHGlobal(user);}
            int integrity;var label=ReadToken(token,25);
            try{integrity=int.Parse(new SecurityIdentifier(Marshal.ReadIntPtr(label)).Value.Split('-')[^1],CultureInfo.InvariantCulture);}
            finally{Marshal.FreeHGlobal(label);}
            return new(sid,ReadInteger(token,12),ReadInteger(token,20)!=0,integrity,ReadInteger(token,8)==1);
        }

        private static int ReadInteger(SafeAccessTokenHandle token,int kind)
        {var buffer=ReadToken(token,kind);try{return Marshal.ReadInt32(buffer);}finally{Marshal.FreeHGlobal(buffer);}}

        private static IntPtr ReadToken(SafeAccessTokenHandle token,int kind)
        {
            // DWORD and linked-handle structures have fixed ABI sizes. In particular,
            // TokenElevation can return ERROR_BAD_LENGTH, not INSUFFICIENT_BUFFER,
            // for a zero-length sizing probe, even though its exact four-byte read succeeds.
            var bytes=kind switch{8 or 12 or 20=>sizeof(int),19=>IntPtr.Size,_=>0};
            if(bytes==0)
            {
                var queried=GetTokenInformation(token,kind,IntPtr.Zero,0,out bytes);
                var error=Marshal.GetLastWin32Error();
                if(queried||error!=122||bytes<4||bytes>4096)
                    throw new Win32Exception(error,$"User token sizing failed (kind={kind}, win32={error}, bytes={bytes}).");
            }
            var buffer=Marshal.AllocHGlobal(bytes);
            var read=GetTokenInformation(token,kind,buffer,bytes,out var returned);
            var readError=Marshal.GetLastWin32Error();
            if(read&&returned==bytes)return buffer;
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(readError,$"User token read failed (kind={kind}, win32={readError}, bytes={returned}, allocated={bytes}).");
        }

        private static bool IsActiveSession(int session)
        {
            if(session<=0||!WTSQuerySessionInformationW(IntPtr.Zero,session,8,out var state,out var bytes))return false;
            try{return bytes>=4&&Marshal.ReadInt32(state)==0;}finally{WTSFreeMemory(state);}
        }

        private static Win32Exception NativeFailure(string message)=>new(Marshal.GetLastWin32Error(),message);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;public string? Reserved;public string? Desktop;public string? Title;
            public int X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;
            public short ShowWindow,Reserved2;public IntPtr ReservedPointer,Input,Output,Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation{public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
        [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint processId);
        [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,IntPtr buffer,int length,out int returned);
        [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token,uint access,IntPtr attributes,int impersonation,int type,out SafeAccessTokenHandle duplicate);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
        [DllImport("userenv.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateEnvironmentBlock(out IntPtr environment,SafeAccessTokenHandle token,[MarshalAs(UnmanagedType.Bool)]bool inherit);
        [DllImport("userenv.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token,uint logonFlags,string application,StringBuilder command,uint creationFlags,IntPtr environment,string cwd,ref StartupInfo startup,out ProcessInformation process);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint creationFlags,IntPtr environment,string cwd,ref StartupInfo startup,out ProcessInformation process);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsProcessInJob(IntPtr process,IntPtr job,[MarshalAs(UnmanagedType.Bool)]out bool result);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryFullProcessImageNameW(IntPtr process,uint flags,StringBuilder path,ref int length);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool TerminateProcess(IntPtr process,uint exitCode);
        [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
        [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool WTSQuerySessionInformationW(IntPtr server,int session,int kind,out IntPtr buffer,out int bytes);
        [DllImport("wtsapi32.dll")]private static extern void WTSFreeMemory(IntPtr buffer);
    }
}
