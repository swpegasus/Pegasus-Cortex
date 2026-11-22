using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

/// <summary>Service-owned durable barrier, locally readable without a live service.</summary>
[SupportedOSPlatform("windows")]
internal sealed class MaintenanceModeStore : IMaintenanceModeSource
{
    private const string SystemSid="S-1-5-18",AdminsSid="S-1-5-32-544";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
        {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=8};
    private static readonly Regex Id=new("^[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    private static readonly Regex Sha=new("^[a-f0-9]{64}$",RegexOptions.CultureInvariant);
    private readonly string _directory;
    private readonly bool _protect;
    private readonly bool _requireProvisioned;
    private readonly string? _writerSid;
    private bool _seen;
    private readonly object _gate=new();

    internal MaintenanceModeStore(string stateRoot,string? writerSid=null,bool protect=true,bool requireProvisioned=false)
    {
        // The private service directory is intentionally unreadable by the owner.
        // Put only the nonsecret barrier beside it under the registered data root;
        // protect that new directory without widening any existing private ACL.
        var root=Path.GetFullPath(stateRoot);
        var publicRoot=protect?Path.GetDirectoryName(root)??throw new AgentOpsException("maintenance_state_unavailable"):root;
        _directory=RequireDirectory(Path.Combine(publicRoot,protect?"execution-maintenance":"maintenance"));
        _writerSid=writerSid;_protect=protect;_requireProvisioned=requireProvisioned;
    }

    private MaintenanceModeStore(string publicDirectory,bool requireProvisioned)
    {
        _directory=RequireDirectory(publicDirectory);
        _protect=true;_requireProvisioned=requireProvisioned;
    }

    private static string RequireDirectory(string directory)
    {
        var canonical=Path.GetFullPath(directory);
        if(Path.Combine(canonical,"ack-interval-"+new string('0',32)+".json").Length>240)throw new AgentOpsException("precondition");
        return canonical;
    }

    internal static IMaintenanceModeSource OpenReader(bool requireProvisioned=false,Func<string>? publicDirectoryResolver=null)=>
        new DeferredReader(requireProvisioned,publicDirectoryResolver??CortexProductLayout.ResolveMachinePublicMaintenanceDirectory);

    // Resolve machine layout at Read so configuration failure fences mutations
    // while leaving engine construction and read diagnostics available.
    private sealed class DeferredReader(bool requireProvisioned,Func<string> publicDirectoryResolver):IMaintenanceModeSource
    {
        private readonly object _readerGate=new();
        private MaintenanceModeStore? _store;
        private MaintenanceModeStore Resolve()
        {
            lock(_readerGate)
            {
                // Failed resolution remains retryable. A resolved reader retains
                // observed/provisioned state across subsequent reads.
                try{return _store??=new MaintenanceModeStore(publicDirectoryResolver(),requireProvisioned);}
                catch(Exception ex)when(ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
                {throw MaintenanceReadDiagnostics.Annotate(ex,MaintenanceReadStage.LayoutResolution);}
            }
        }
        public MaintenanceModeRecord? Read()=>Resolve().Read();
        public bool IsAcknowledged(MaintenanceEffect effect)=>Resolve().IsAcknowledged(effect);
    }

    internal void EnsureProvisioned(bool allowFreshOff=true)
    {
        lock(_gate)
        {
            RequireWriter();
            if(!allowFreshOff&&(!Directory.Exists(_directory)||!File.Exists(Path.Combine(_directory,"required.v1"))))throw new AgentOpsException("maintenance_state_unavailable");
            if(!Directory.Exists(_directory))
            {
                PathNormalizer.RejectAbsoluteReparseComponents(Path.GetDirectoryName(_directory)!);
                if(_protect)
                {
                    var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
                    security.SetOwner(new SecurityIdentifier(SystemSid));
                    foreach(var sid in new[]{SystemSid,AdminsSid})security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(_writerSid!),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
                    new DirectoryInfo(_directory).Create(security);
                }
                else Directory.CreateDirectory(_directory);
            }
            PathNormalizer.RejectAbsoluteReparseComponents(_directory);
            var sentinel=Path.Combine(_directory,"required.v1");
            var fresh=!File.Exists(sentinel);
            if(fresh)
            {
                using(var stream=new FileStream(sentinel,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                {stream.Write("maintenance/1\n"u8);stream.Flush(true);}
                // Provision OFF only for a fresh feature. A lost existing record
                // is never interpreted as a completed handback.
                if(!File.Exists(ModePath))WriteAtomic(ModePath,new MaintenanceModeRecord(1,new string('0',32),"off",new string('0',32),new string('0',32),"",Guid.Empty.ToString("D"),_writerSid!,0,"",null,1));
            }
            _seen=true;
        }
    }

    private string ModePath=>Path.Combine(_directory,"mode.v1.json");
    public MaintenanceModeRecord? Read()
    {
        lock(_gate)
        {
            var stage=MaintenanceReadStage.RecordOpen;
            try
            {
                var mode=ReadBounded<MaintenanceModeRecord>(ModePath,4096,value=>stage=value);
                if(mode is null)
                {
                    stage=MaintenanceReadStage.RequiredRecord;
                    if(_seen||_requireProvisioned||File.Exists(Path.Combine(_directory,"required.v1"))||!NamespaceAbsent())throw new AgentOpsException("maintenance_state_unavailable");
                    return null;
                }
                _seen=true;
                stage=MaintenanceReadStage.RecordValidation;
                Validate(mode);
                return mode;
            }
            catch(Exception ex)when(ex is AgentOpsException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException)
            {throw MaintenanceReadDiagnostics.Annotate(ex,stage);}
        }
    }

    private bool NamespaceAbsent()
    {
        // An existing empty/partial namespace is not evidence of a fresh
        // optional feature. Do not hide access errors behind Directory.Exists.
        try { _ = File.GetAttributes(_directory); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    internal MaintenanceModeRecord Publish(MaintenanceModeRecord next,string? expectedEpoch,long expectedRevision)
    {
        lock(_gate)
        {
            RequireWriter();
            var current=Read();
            if(current?.Epoch!=expectedEpoch||(current?.Revision??0)!=expectedRevision)throw new AgentOpsException("maintenance_epoch_conflict");
            next=next with{Revision=checked(expectedRevision+1)};Validate(next);
            WriteAtomic(ModePath,next);_seen=true;return next;
        }
    }

    internal void PublishAcknowledgement(MaintenanceAcknowledgement acknowledgement)
    {
        lock(_gate)
        {
            RequireWriter();Validate(acknowledgement);
            WriteAtomic(AckPath(acknowledgement.Store,acknowledgement.Jid),acknowledgement);
        }
    }

    public bool IsAcknowledged(MaintenanceEffect effect)
    {
        try
        {
            if(!Id.IsMatch(effect.Jid)||!ValidOriginalRid(effect.Rid)||!Sha.IsMatch(effect.ReceiptSha256)||effect.Live||effect.State=="queued"||effect.State=="waiting_user")return false;
            var ack=ReadBounded<MaintenanceAcknowledgement>(AckPath(effect.Store,effect.Jid),4096);
            if(ack is null)return false;
            Validate(ack);
            return ack.Store==effect.Store&&ack.Jid==effect.Jid&&ack.Rid==effect.Rid&&ack.ReceiptSha256==effect.ReceiptSha256;
        }
        catch(Exception ex)when(ex is AgentOpsException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {return false;}
    }

    private string AckPath(string store,string jid)
    {
        if(store is not("user" or "broker" or "interval")||!Id.IsMatch(jid))throw new AgentOpsException("invalid");
        return Path.Combine(_directory,$"ack-{store}-{jid}.json");
    }
    private T? ReadBounded<T>(string path,int maximum,Action<MaintenanceReadStage>? observeStage=null) where T:class
    {
        try
        {
            observeStage?.Invoke(MaintenanceReadStage.PathValidation);
            PathNormalizer.RejectAbsoluteReparseComponents(path);
            observeStage?.Invoke(MaintenanceReadStage.RecordOpen);
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            observeStage?.Invoke(MaintenanceReadStage.RecordSize);
            if(stream.Length is <1||stream.Length>maximum)throw new AgentOpsException("maintenance_state_unavailable");
            observeStage?.Invoke(MaintenanceReadStage.Protection);
            if(_protect){RequireProtectedDirectory();RequireProtectedFile(path);}
            observeStage?.Invoke(MaintenanceReadStage.RecordParse);
            using var document=JsonDocument.Parse(stream,new JsonDocumentOptions{MaxDepth=8});
            RejectDuplicates(document.RootElement);
            return document.RootElement.Deserialize<T>(Json)??throw new AgentOpsException("maintenance_state_unavailable");
        }
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
    }
    private void WriteAtomic<T>(string path,T value)
    {
        if(path.Length>240)throw new AgentOpsException("precondition");
        PathNormalizer.RejectAbsoluteReparseComponents(_directory);
        var temp=Path.Combine(_directory,".publish-"+Guid.NewGuid().ToString("N"));
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,value,Json);stream.Flush(true);}
            if(_protect){RequireProtectedDirectory();RequireProtectedFile(temp);}
            File.Move(temp,path,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private void RequireWriter()
    {
        if(_writerSid is null)throw new AgentOpsException("auth");
        if(!_protect)return;
        using var identity=WindowsIdentity.GetCurrent();
        if(identity.User?.Value!=SystemSid||Process.GetCurrentProcess().SessionId!=0)throw new AgentOpsException("admin_service_identity_required");
    }
    private void RequireProtectedDirectory()
    {
        var directory=new DirectoryInfo(_directory);
        var acl=directory.GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
        if(acl.GetOwner(typeof(SecurityIdentifier))?.Value is not(SystemSid or AdminsSid))throw new AgentOpsException("maintenance_state_unavailable");
        var writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&writes)!=0&&rule.IdentityReference.Value is not(SystemSid or AdminsSid))throw new AgentOpsException("maintenance_state_unavailable");
        // Ancestors may allow unrelated creation, but cannot allow an untrusted
        // writer to replace the protected namespace or alter its security.
        for(var parent=directory.Parent;parent is not null;parent=parent.Parent)
        {
            var ancestor=parent.GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
            if(ancestor.GetOwner(typeof(SecurityIdentifier))?.Value is not(SystemSid or AdminsSid or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"))throw new AgentOpsException("maintenance_state_unavailable");
            var replace=FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
            foreach(FileSystemAccessRule rule in ancestor.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if((rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&replace)!=0&&rule.IdentityReference.Value is not(SystemSid or AdminsSid or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"))throw new AgentOpsException("maintenance_state_unavailable");
        }
    }
    private static void RequireProtectedFile(string path)
    {
        var acl=new FileInfo(path).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
        if(acl.GetOwner(typeof(SecurityIdentifier))?.Value is not(SystemSid or AdminsSid))throw new AgentOpsException("maintenance_state_unavailable");
        var writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&writes)!=0&&rule.IdentityReference.Value is not(SystemSid or AdminsSid))throw new AgentOpsException("maintenance_state_unavailable");
    }
    private static void Validate(MaintenanceModeRecord mode)
    {
        if(mode.Version!=1||!Id.IsMatch(mode.Epoch)||!Id.IsMatch(mode.Jid)||!Id.IsMatch(mode.Rid)||mode.Revision<1||
            mode.Phase is not("off" or "preparing" or "active" or "ending" or "uncertain")||string.IsNullOrWhiteSpace(mode.OwnerSid)||
            mode.Phase!="off"&&(!Id.IsMatch(mode.ScopeId)||!Guid.TryParseExact(mode.TaskId,"D",out _)||mode.OwnerSession<=0||!Id.IsMatch(mode.ServiceInstance)))
            throw new AgentOpsException("maintenance_state_unavailable");
    }
    private static bool ValidOriginalRid(string? rid)=>rid is{Length:>0 and <=1024}&&!rid.Any(char.IsControl);
    private static void Validate(MaintenanceAcknowledgement ack)
    {
        if(ack.Version!=1||ack.Store is not("user" or "broker" or "interval")||!Id.IsMatch(ack.Jid)||!ValidOriginalRid(ack.Rid)||
            !Sha.IsMatch(ack.ReceiptSha256)||!Sha.IsMatch(ack.EvidenceSha256)||!Id.IsMatch(ack.ScopeId)||!Guid.TryParseExact(ack.TaskId,"D",out _)||
            string.IsNullOrWhiteSpace(ack.OwnerSid)||ack.OwnerSession<=0||!ack.EffectsQuiescent)throw new AgentOpsException("invalid");
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var item in element.EnumerateObject()){if(!names.Add(item.Name))throw new JsonException();RejectDuplicates(item.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())RejectDuplicates(item);
    }
}
