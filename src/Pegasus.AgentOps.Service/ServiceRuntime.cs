using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pegasus.AgentOps.Engine;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Pegasus.Cortex.Product;
using Microsoft.Extensions.Hosting.WindowsServices;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.AgentOps.Service;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Pegasus.AgentOps.Tests")]

internal sealed record InstalledServiceContract(string? Contract,int Version,string? OwnerSid);

internal sealed record ServiceConfiguration(string OwnerSid,string StateRoot,bool IsFixture=false,string? UserHostExecutable=null);

internal static class ServiceStartupTrace
{
    private static readonly object Gate=new();
    // Keep early host/SCM diagnostics in the same private service log. Failure
    // to resolve that domain (including ordinary-user execution) has no fallback.
    public static string? TryResolveRoot()
    {
        try{return CortexProductLayout.ResolveMachineServiceStateDirectory();}
        catch{return null;}
    }

    public static void Write(string? root,string stage,string detail)
    {
        if(root is null)return;
        try
        {
            var now=DateTimeOffset.UtcNow;
            var line=$"{now:O} stage={Token(stage)} detail={Token(detail)}{Environment.NewLine}";
            lock(Gate)
            {
                var serviceSid=LocalIdentity.Current().Sid;
                ServiceStateSecurity.ValidateTraceRoot(root,serviceSid);
                var path=Path.Combine(root,"startup.log");
                var previous=Path.Combine(root,"startup.previous.log");
                ServiceStateSecurity.SealExistingTraceFile(root,path,serviceSid);
                ServiceStateSecurity.SealExistingTraceFile(root,previous,serviceSid);
                CortexLogRotation.RotateIfNeeded(path,previous,64*1024,now);
                using var stream=File.Exists(path)
                    ? new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read)
                    : new FileInfo(path).Create(FileMode.CreateNew,FileSystemRights.FullControl,FileShare.Read,
                        4096,FileOptions.None,ServiceStateSecurity.TraceFileSecurity(serviceSid));
                using var writer=new StreamWriter(stream);
                writer.Write(line);
            }
        }
        catch
        {
            // Startup diagnostics must never replace the actual service failure.
        }
    }
    private static string Token(string value)=>new(value.Where(character=>char.IsAsciiLetterOrDigit(character)||character is '-' or '_' or '.').Take(96).ToArray());
}

[SupportedOSPlatform("windows")]
internal static class ServiceConfigurationLoader
{
    public const string ContractName="pegasus-cortex-execution-service";
    public const int CurrentVersion=1;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive=false,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow
    };

    public static ServiceConfiguration Load(string fixedRoot,string configPath)
    {
        try
        {
            var canonicalRoot=Path.GetFullPath(fixedRoot);
            var canonicalConfig=Path.GetFullPath(configPath);
            if(!Path.IsPathFullyQualified(fixedRoot)||!Path.IsPathFullyQualified(configPath))throw new InvalidDataException();
            if(!StringComparer.OrdinalIgnoreCase.Equals(canonicalRoot,fixedRoot.TrimEnd(Path.DirectorySeparatorChar)))throw new InvalidDataException();
            if(!StringComparer.OrdinalIgnoreCase.Equals(canonicalConfig,Path.Combine(canonicalRoot,"service.v1.json")))throw new InvalidDataException();
            if(!Directory.Exists(canonicalRoot)||!File.Exists(canonicalConfig))throw new FileNotFoundException();
            PathNormalizer.RejectAbsoluteReparseComponents(canonicalRoot);
            PathNormalizer.RejectAbsoluteReparseComponents(canonicalConfig);
            if((File.GetAttributes(canonicalRoot)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(canonicalConfig)&FileAttributes.ReparsePoint)!=0)throw new AgentOpsException("reparse_boundary");
            var serviceSid=LocalIdentity.Current().Sid;
            // Administrative ownership with a private service DACL is valid;
            // ordinary startup must not rewrite retained workspace/DB owners.
            ValidateStateTree(canonicalRoot);
            ServiceStateSecurity.Validate(canonicalRoot,serviceSid);
            var value=JsonSerializer.Deserialize<InstalledServiceContract>(File.ReadAllBytes(canonicalConfig),Json);
            if(value is null||!StringComparer.Ordinal.Equals(value.Contract,ContractName)||value.Version!=CurrentVersion||string.IsNullOrWhiteSpace(value.OwnerSid))throw new InvalidDataException();
            var sid=new SecurityIdentifier(value.OwnerSid);
            if(sid.IsWellKnown(WellKnownSidType.LocalSystemSid)||sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)||sid.IsWellKnown(WellKnownSidType.AnonymousSid)||sid.IsWellKnown(WellKnownSidType.NetworkSid))throw new InvalidDataException();
            return new ServiceConfiguration(sid.Value,canonicalRoot);
        }
        catch(ServiceConfigurationException){throw;}
        catch(Exception ex)when(ex is ArgumentException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException or AgentOpsException)
        {
            throw new ServiceConfigurationException("service_config_invalid",inner:ex);
        }
    }

    private static void ValidateStateTree(string root)
    {
        var pending=new Stack<string>();pending.Push(root);var count=0;
        while(pending.TryPop(out var directory))
        {
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if(++count>100_000)throw new ServiceConfigurationException("service_state_limit");
                var attributes=File.GetAttributes(entry);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new ServiceConfigurationException("service_state_reparse",Relative(root,entry));
                if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);
            }
        }
    }
    private static string Relative(string root,string path){var relative=Path.GetRelativePath(root,path);return relative=="."?".":relative.Replace(Path.DirectorySeparatorChar,'/');}
}

[SupportedOSPlatform("windows")]
internal static class ServiceStateSecurity
{
    private static readonly string Administrators="S-1-5-32-544";

    public static void RestrictFixture(string root)
    {
        var serviceSid=LocalIdentity.Current().Sid;
        RestrictDirectory(root,serviceSid);
        foreach(var entry in EnumerateNoFollow(root))
        {
            var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new ServiceConfigurationException("service_config_invalid");
            if((attributes&FileAttributes.Directory)!=0)RestrictDirectory(entry,serviceSid);else RestrictFile(entry,serviceSid);
        }
    }

    public static void Validate(string root,string serviceSid)
    {
        ValidateSecurity(new DirectoryInfo(root).GetAccessControl(),serviceSid,true,root,root);
        foreach(var entry in EnumerateNoFollow(root))
        {
            var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new ServiceConfigurationException("service_state_reparse",Relative(root,entry));
            FileSystemSecurity security=(attributes&FileAttributes.Directory)!=0?new DirectoryInfo(entry).GetAccessControl():new FileInfo(entry).GetAccessControl();
            ValidateSecurity(security,serviceSid,false,root,entry);
        }
    }

    internal static void ValidateTraceRoot(string root,string serviceSid)
    {
        if(!Directory.Exists(root))throw new ServiceConfigurationException("service_state_acl_invalid");
        PathNormalizer.RejectAbsoluteReparseComponents(root);
        var security=new DirectoryInfo(root).GetAccessControl();
        ValidateSecurity(security,serviceSid,true,root,root);
        RequirePrivateTraceRules(security,serviceSid);
    }

    internal static void SealExistingTraceFile(string root,string path,string serviceSid)
    {
        var name=Path.GetFileName(path);
        if(name is not("startup.log" or "startup.previous.log")||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path),Path.Combine(Path.GetFullPath(root),name)))
            throw new ServiceConfigurationException("service_state_acl_invalid");
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        if(Directory.Exists(path))throw new ServiceConfigurationException("service_state_acl_invalid");
        if(!File.Exists(path))return;
        var file=new FileInfo(path);
        var security=file.GetAccessControl();
        RequireAdoptableTraceSecurity(security,serviceSid);
        // The private parent and allowed service/administrator grants establish
        // the boundary. Safe inherited/Admin-owned diagnostics need no repair.
        ValidateSecurity(security,serviceSid,false,root,path);
    }

    internal static void RequireAdoptableTraceSecurity(FileSecurity security,string serviceSid)
    {
        var owner=security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if(owner!=serviceSid&&owner!=Administrators)throw new ServiceConfigurationException("service_state_acl_invalid");
        RequirePrivateTraceRules(security,serviceSid);
    }

    internal static bool RepairLegacyWorkspaceOwner(string root,string serviceSid)
    {
        ValidateTraceRoot(root,serviceSid);
        var path=Path.Combine(root,"workspaces.json");
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        if(Directory.Exists(path))throw new ServiceConfigurationException("service_state_acl_invalid");
        if(!File.Exists(path))return false;
        var file=new FileInfo(path);
        var security=file.GetAccessControl();
        RequireAdoptableTraceSecurity(security,serviceSid);
        if(security.GetOwner(typeof(SecurityIdentifier))?.Value==serviceSid)return false;
        var before=new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0);
        // Only this historical atomic-workspace file is admitted. Persist the
        // owner section alone: no content read/write, DACL change or tree repair.
        security.SetOwner(new SecurityIdentifier(serviceSid));
        file.SetAccessControl(security);
        var after=file.GetAccessControl();
        var afterDescriptor=new RawSecurityDescriptor(after.GetSecurityDescriptorBinaryForm(),0);
        const ControlFlags daclFlags=ControlFlags.DiscretionaryAclPresent|ControlFlags.DiscretionaryAclDefaulted|
            ControlFlags.DiscretionaryAclAutoInheritRequired|ControlFlags.DiscretionaryAclAutoInherited|ControlFlags.DiscretionaryAclProtected;
        if(after.GetOwner(typeof(SecurityIdentifier))?.Value!=serviceSid||
            (before.ControlFlags&daclFlags)!=(afterDescriptor.ControlFlags&daclFlags)||
            !AclBytes(before.DiscretionaryAcl).SequenceEqual(AclBytes(afterDescriptor.DiscretionaryAcl)))
            throw new ServiceConfigurationException("service_state_acl_invalid");
        return true;
    }

    private static byte[] AclBytes(RawAcl? acl)
    {
        if(acl is null)return [];
        var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);return bytes;
    }

    private static void RequirePrivateTraceRules(FileSystemSecurity security,string serviceSid)
    {
        if(new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0).DiscretionaryAcl is null)
            throw new ServiceConfigurationException("service_state_acl_invalid");
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType!=AccessControlType.Allow||
                rule.IdentityReference.Value!=serviceSid&&rule.IdentityReference.Value!=Administrators)
                throw new ServiceConfigurationException("service_state_acl_invalid");
    }

    internal static FileSecurity TraceFileSecurity(string serviceSid)
    {
        var security=new FileSecurity();
        Configure(security,serviceSid,InheritanceFlags.None);
        return security;
    }

    private static IEnumerable<string> EnumerateNoFollow(string root)
    {
        var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            yield return entry;var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)==0&&(attributes&FileAttributes.Directory)!=0)pending.Push(entry);
        }
    }

    internal static void ValidateSecurity(FileSystemSecurity security,string serviceSid,bool requireProtected,string root,string path)
    {
        if(requireProtected&&!security.AreAccessRulesProtected)throw Invalid(root,path);
        var owner=security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if(!StringComparer.Ordinal.Equals(owner,serviceSid)&&owner!=Administrators)throw Invalid(root,path);
        if(new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0).DiscretionaryAcl is null)throw Invalid(root,path);
        var allowed=new HashSet<string>(StringComparer.Ordinal){serviceSid,Administrators};
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&!allowed.Contains(rule.IdentityReference.Value))throw Invalid(root,path);
    }
    private static ServiceConfigurationException Invalid(string root,string path)=>new("service_state_acl_invalid",Relative(root,path));
    private static string Relative(string root,string path){var relative=Path.GetRelativePath(root,path);return relative=="."?".":relative.Replace(Path.DirectorySeparatorChar,'/');}

    private static void RestrictDirectory(string path,string serviceSid)
    {
        var security=new DirectorySecurity();Configure(security,serviceSid,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit);new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void RestrictFile(string path,string serviceSid)
    {
        var security=new FileSecurity();Configure(security,serviceSid,InheritanceFlags.None);new FileInfo(path).SetAccessControl(security);
    }
    private static void Configure(FileSystemSecurity security,string serviceSid,InheritanceFlags inheritance)
    {
        var service=new SecurityIdentifier(serviceSid);security.SetOwner(service);security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new FileSystemAccessRule(service,FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Administrators),FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
    }
}

internal sealed class ServiceConfigurationException(string code,string? entry=null,Exception? inner=null):Exception(code,inner)
{
    public string Code{get;}=code;
    public string? Entry{get;}=entry;
}

internal static class ServiceRuntime
{
    internal static string? ResolveServiceExitHostExecutable(ServiceConfiguration configuration) =>
        ResolveServiceExitHostExecutable(configuration,AppContext.BaseDirectory,
            CortexDataLocation.ResolveMachineDataRoot,CortexPrivilegedCodePath.RequireProtectedImage);

    // The service needs a code identity, not an owner's storage layout. In
    // particular, constructing a legacy user layout before applying the machine
    // data binding can walk private leaves that LocalSystem must not inspect.
    internal static string? ResolveServiceExitHostExecutable(ServiceConfiguration configuration,
        string serviceDirectory,Func<string,string> resolveMachineDataRoot,
        Action<string,string> requireProtectedImage)
    {
        if(configuration.IsFixture)return configuration.UserHostExecutable;
        ArgumentNullException.ThrowIfNull(resolveMachineDataRoot);
        ArgumentNullException.ThrowIfNull(requireProtectedImage);
        if(!Path.IsPathFullyQualified(serviceDirectory))throw new InvalidDataException("The executing service directory is not absolute.");
        var directory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(serviceDirectory));
        var installRoot=Path.GetFullPath(Path.Combine(directory,"..",".."));
        var serviceRelative=CortexProductCatalogV1.StableRolePaths["service"].Replace('/',Path.DirectorySeparatorChar);
        if(!StringComparer.OrdinalIgnoreCase.Equals(directory,Path.GetDirectoryName(Path.Combine(installRoot,serviceRelative))))
            throw new InvalidDataException("The executing service is outside its stable product role.");

        // This fixed machine resolver validates the executing install binding and
        // selected data root without creating or probing any owner-user paths.
        var dataRoot=resolveMachineDataRoot(installRoot);
        if(!StringComparer.OrdinalIgnoreCase.Equals(configuration.StateRoot,Path.Combine(dataRoot,"service")))
            throw new InvalidDataException("The service state does not match the registered Cortex data root.");
        var host=Path.GetFullPath(Path.Combine(installRoot,
            CortexProductCatalogV1.StableRolePaths["host"].Replace('/',Path.DirectorySeparatorChar)));
        if(host.Length>CortexProductLayout.MaximumWindowsPathCharacters)
            throw new PathTooLongException("The installed Cortex Host path exceeds the product path budget.");
        // Reuse protected code ownership, containment and no-reparse admission;
        // selecting external mutable data never relaxes the installed image.
        requireProtectedImage(installRoot,host);
        return host;
    }

    public static async Task RunAsync(ServiceConfiguration configuration,bool crashAfterTray,CancellationToken cancellationToken,
        Action? stopApplication=null)
    {
        // The LocalSystem service executes only the bounded privileged surface.
        // Interactive application lifetime belongs to the signed-in user host;
        // constructing its manager in session 0 is both an authority-boundary
        // violation and a deterministic startup failure.
        if(!configuration.IsFixture)
        {
            var processors=CortexProcessorPolicy.ApplyForInstallRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..")));
            ServiceStartupTrace.Write(configuration.StateRoot,"processors",processors.Applied?"applied":
                processors.Supported?"not-applied":"unavailable");
        }
        RegisteredToolDefinition? applicationSevenZip=null;
        try
        {
            applicationSevenZip=await SystemSevenZipToolBinding.TryCreateAsync(cancellationToken);
            ServiceStartupTrace.Write(configuration.StateRoot,"application-7z",applicationSevenZip is null?"unavailable":"ready");
        }
        catch(AgentOpsException ex)
        {
            // Optional archive execution fails closed as not_found while Cortex's
            // ordinary semantic and PAO surfaces remain available.
            ServiceStartupTrace.Write(configuration.StateRoot,"application-7z","unavailable."+ex.Code);
        }
        var maintenanceMode=configuration.IsFixture?new MaintenanceModeStore(configuration.StateRoot,protect:false):MaintenanceModeStore.OpenReader(requireProvisioned:true);
        var installedRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..",".."));
        bool ConnectionIsCurrent(CortexMcpConnectionAuthority authority,bool full) =>
            !configuration.IsFixture&&Pegasus.Cortex.Product.CortexMcpConnections.Find(
                Pegasus.Cortex.Product.CortexMcpConnections.ReadForInstallRoot(installedRoot),authority,full) is not null;
        await using var systemEngine=new AgentOpsEngine(
            Path.Combine(configuration.StateRoot,"system-direct"),
            pathNormalizer:null,
            directOnly:true,
            bundledNsis:null,
            enableUserApplications:false,
            systemTools:new AgentOpsSystemToolBindings(applicationSevenZip),maintenanceMode:maintenanceMode,
            connectionAuthorization:ConnectionIsCurrent);
        if (!configuration.IsFixture) systemEngine.BindSystemWindowsTasks(configuration.OwnerSid);
        var applicationCredentials=new ApplicationCredentialRegistry(configuration.StateRoot);
        // Optional owner-admin capability fails closed without changing ordinary execution.
        OwnerAdminExecution? ownerAdmin=null;
        var maintenanceInitialization=MaintenanceInitializationObservation.Started;
        try
        {
            ownerAdmin=new OwnerAdminExecution(configuration.StateRoot,configuration.OwnerSid,new WindowsOwnerAdminLauncher(configuration.StateRoot),
                initializationProgress:(stage,initialized)=>maintenanceInitialization=maintenanceInitialization.Observe(stage,initialized));
            maintenanceInitialization=maintenanceInitialization.Completed();
        }
        catch(Exception ex)when(ex is AgentOpsException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or Microsoft.Data.Sqlite.SqliteException)
        { maintenanceInitialization=maintenanceInitialization.Failed(ex);ServiceStartupTrace.Write(configuration.StateRoot,"owner-admin","unavailable."+(ex is AgentOpsException failure?failure.Code:ex.GetType().Name)); }
        using var ownerAdminLifetime=ownerAdmin;
        await using var broker=new AuthenticatedServiceBroker(configuration.StateRoot,configuration.OwnerSid,ConnectionIsCurrent,crashAfterTray,null,(request,ct)=>systemEngine.ExecuteAuthenticatedFrozenDirectAsync(request,ct),applicationCredentials,ownerAdmin,maintenanceMode,maintenanceInitialization);
        broker.SetServiceExitProbe(systemEngine.InspectExitIssueAsync,
            ResolveServiceExitHostExecutable(configuration),stopApplication);
        // HTTPS belongs to this service only when the persisted startup owner
        // explicitly selects it. The dispatcher relays to the ordinary Host;
        // this direct-only Engine never acquires semantic storage or user keys.
        await using var sharedMcp = configuration.IsFixture ? null :
            await CortexMcpHostController.StartForInstallRootAsync(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..")),
                CortexMcpOwner.Service,
                _ => Task.FromResult<Pegasus.SharedMcp.IMcpDispatcher>(new CortexServiceMcpDispatcher(broker)),
                cancellationToken);
        if (sharedMcp is not null)
            ServiceStartupTrace.Write(configuration.StateRoot,"shared-mcp",
                sharedMcp.State.Status.ToString().ToLowerInvariant() +
                (sharedMcp.State.Issue is { } sharedIssue ? "." + sharedIssue : string.Empty));
        var applicationAuthorization=new ApplicationAuthorizationCoordinator(applicationCredentials,new WindowsApplicationPeerVerifier());
        var user=AuthenticatedPipeHost.RunOperationsAsync(NamedPipeDispatcher.UserPipeName,configuration.OwnerSid,broker.HandleUserAsync,cancellationToken);
        var system=AuthenticatedPipeHost.RunOperationsAsync(NamedPipeDispatcher.SystemPipeName,configuration.OwnerSid,broker.HandleSystemAsync,cancellationToken,broker.OnServiceExitReply);
        var registration=AuthenticatedPipeHost.RunRegistrationsAsync(NamedPipeDispatcher.TrayRegistrationPipeName,configuration.OwnerSid,(value,peer,_)=>broker.RegisterTrayAsync(value,peer),cancellationToken);
        var applications=ApplicationAuthorizationPipeHost.RunAsync(CortexProductIdentity.ApplicationAuthorizationPipe,applicationAuthorization,broker,cancellationToken);
        await Task.WhenAll(user,system,registration,applications);
    }
}

internal sealed class AgentOpsWindowsWorker(ILogger<AgentOpsWindowsWorker> logger,IHostApplicationLifetime lifetime,IHostLifetime hostLifetime):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? fixedRoot=null;
        try
        {
            fixedRoot=CortexProductLayout.ResolveMachineServiceStateDirectory();
            // BackgroundService execution begins only after WindowsServiceLifetime
            // has connected to SCM. Token-default repair and private trace I/O
            // therefore cannot turn a slow cold launch into SCM error 1053.
            var ownerChanged=ServiceProcessOwnership.EnsureCurrentProcess();
            ServiceStartupTrace.Write(fixedRoot,"process-owner",ownerChanged?"system-selected":"unchanged");
            ServiceStartupTrace.Write(fixedRoot,"lifetime",hostLifetime.GetType().Name);
            ServiceStartupTrace.Write(fixedRoot,"worker","validating-config");
            var configPath=Path.GetFullPath(Path.Combine(fixedRoot,"service.v1.json"));
            var configuration=ServiceConfigurationLoader.Load(fixedRoot,configPath);
            ServiceStartupTrace.Write(fixedRoot,"worker","starting-pipes");
            await ServiceRuntime.RunAsync(configuration,false,stoppingToken,lifetime.StopApplication);
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested)
        {
            // An SCM-requested stop is not a startup failure or recovery trigger.
        }
        catch(ServiceConfigurationException ex)
        {
            if(fixedRoot is not null)ServiceStartupTrace.Write(fixedRoot,"configuration-failed",ex.Code+(ex.Entry is null?string.Empty:"."+ex.Entry));
            logger.LogCritical("AgentOps service startup failed. code={Code} entry={Entry}",ex.Code,ex.Entry??"none");
            Fail(4);
        }
        catch(Exception ex)
        {
            if(fixedRoot is not null)ServiceStartupTrace.Write(fixedRoot,"startup-failed",ex.GetType().Name);
            logger.LogCritical(ex,"AgentOps service startup failed before the execution pipes became ready.");
            Fail(8);
        }
    }

    private void Fail(int exitCode)
    {
        Environment.ExitCode=exitCode;
        if(hostLifetime is WindowsServiceLifetime serviceLifetime)serviceLifetime.ExitCode=WindowsServiceFailureExitCode(exitCode);
        lifetime.StopApplication();
    }

    // ServiceBase.ExitCode is a Win32 status, not the process's product code.
    // Preserve process codes and detailed logs without misreporting code 8 as
    // ERROR_NOT_ENOUGH_MEMORY. A true service-specific status would also require
    // dwServiceSpecificExitCode, which WindowsServiceLifetime does not expose.
    internal static int WindowsServiceFailureExitCode(int processExitCode) => processExitCode==4
        ? 13   // ERROR_INVALID_DATA: rejected service configuration.
        : 1064; // ERROR_EXCEPTION_IN_SERVICE: unexpected startup exception.
}
