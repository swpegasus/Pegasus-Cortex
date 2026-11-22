using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

public sealed record LocalPeer(string Sid,int Session,string? ApplicationPath=null,uint? ProcessId=null,bool Elevated=false)
{
    public bool SameIdentity(LocalPeer other)=>StringComparer.Ordinal.Equals(Sid,other.Sid)&&Session==other.Session;
}

[SupportedOSPlatform("windows")]
public static class LocalIdentity
{
    private const string LocalSystemSid="S-1-5-18";
    private const string AgentOpsServiceName=CortexProductIdentity.ServiceName;
    public static LocalPeer Current()
    {
        using var identity=WindowsIdentity.GetCurrent();
        return new LocalPeer(identity.User?.Value??throw new AgentOpsException("auth"),Process.GetCurrentProcess().SessionId,Environment.ProcessPath,checked((uint)Environment.ProcessId),new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
    }

    public static LocalPeer Capture(NamedPipeServerStream pipe)
    {
        string? sid=null;var elevated=false;
        pipe.RunAsClient(()=>{using var identity=WindowsIdentity.GetCurrent(true)??throw new AgentOpsException("auth");sid=identity.User?.Value;elevated=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);});
        if(string.IsNullOrWhiteSpace(sid))throw new AgentOpsException("auth_sid");if(!GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(),out var session))throw new AgentOpsException("auth_session");
        var authenticatedSid=sid!;
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out var pid))throw new AgentOpsException("auth_process");
        return new LocalPeer(authenticatedSid,checked((int)session),CaptureProcessApplication(pid),pid,elevated);
    }

    public static LocalPeer CaptureServer(NamedPipeClientStream pipe)
    {
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out var pid)||!GetNamedPipeServerSessionId(pipe.SafePipeHandle.DangerousGetHandle(),out var session))throw new AgentOpsException("auth");
        var process=OpenProcess(0x1000,false,pid);if(process==0)throw new AgentOpsException("auth",inner:new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));try{if(!OpenProcessToken(process,0x0008,out var token))throw new AgentOpsException("auth",inner:new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));try{using var identity=new WindowsIdentity(token);return new LocalPeer(identity.User?.Value??throw new AgentOpsException("auth"),checked((int)session),CaptureProcessApplication(pid),pid);}finally{CloseHandle(token);}}finally{CloseHandle(process);}
    }

    public static LocalPeer CaptureExpectedServer(NamedPipeClientStream pipe,LocalPeer expected)
    {
        if(!StringComparer.Ordinal.Equals(expected.Sid,LocalSystemSid)||expected.Session!=0)return CaptureServer(pipe);
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out var pipePid)||!GetNamedPipeServerSessionId(pipe.SafePipeHandle.DangerousGetHandle(),out var pipeSession)||pipeSession!=0)throw new AgentOpsException("auth");
        var manager=OpenSCManager(null,null,0x0001);if(manager==0)throw new AgentOpsException("auth");try{var service=OpenService(manager,AgentOpsServiceName,0x0004);if(service==0)throw new AgentOpsException("auth");try{var size=Marshal.SizeOf<ServiceStatusProcess>();var buffer=Marshal.AllocHGlobal(size);try{if(!QueryServiceStatusEx(service,0,buffer,(uint)size,out _))throw new AgentOpsException("auth");var status=Marshal.PtrToStructure<ServiceStatusProcess>(buffer);if(status.ProcessId!=pipePid||status.CurrentState!=4)throw new AgentOpsException("auth");return expected;}finally{Marshal.FreeHGlobal(buffer);}}finally{CloseServiceHandle(service);}}finally{CloseServiceHandle(manager);}
    }

    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientSessionId(nint pipe,out uint clientSessionId);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(nint pipe,out uint clientProcessId);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(nint pipe,out uint serverProcessId);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerSessionId(nint pipe,out uint serverSessionId);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint OpenProcess(uint desiredAccess,bool inheritHandle,uint processId);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process,uint flags,StringBuilder path,ref int size);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint OpenSCManager(string? machineName,string? databaseName,uint desiredAccess);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint OpenService(nint manager,string serviceName,uint desiredAccess);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool QueryServiceStatusEx(nint service,uint infoLevel,nint buffer,uint bufferSize,out uint bytesNeeded);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint service);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess{public uint ServiceType;public uint CurrentState;public uint ControlsAccepted;public uint Win32ExitCode;public uint ServiceSpecificExitCode;public uint CheckPoint;public uint WaitHint;public uint ProcessId;public uint ServiceFlags;}

    private static string? CaptureProcessApplication(uint pid)
    {
        var process=OpenProcess(0x1000,false,pid);if(process==0)return null;
        try{var size=32768;var path=new StringBuilder(size);return QueryFullProcessImageName(process,0,path,ref size)?Path.GetFullPath(path.ToString()):null;}
        finally{CloseHandle(process);}
    }
}

[SupportedOSPlatform("windows")]
public static class LocalPipeFactory
{
    public static NamedPipeServerStream Create(string name,string clientSid)
    {
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        Add(security,"S-1-5-7",PipeAccessRights.FullControl,AccessControlType.Deny); // Anonymous
        Add(security,"S-1-5-2",PipeAccessRights.FullControl,AccessControlType.Deny); // Network
        var serverSid=LocalIdentity.Current().Sid;Add(security,serverSid,PipeAccessRights.FullControl,AccessControlType.Allow);
        var clientRights=PipeAccessRights.ReadWrite|PipeAccessRights.Synchronize;
        if(!StringComparer.Ordinal.Equals(clientSid,serverSid))Add(security,clientSid,clientRights,AccessControlType.Allow);
        if(!StringComparer.Ordinal.Equals("S-1-5-18",serverSid)&&!StringComparer.Ordinal.Equals("S-1-5-18",clientSid))Add(security,"S-1-5-18",clientRights,AccessControlType.Allow);
        if(!StringComparer.Ordinal.Equals("S-1-5-32-544",serverSid)&&!StringComparer.Ordinal.Equals("S-1-5-32-544",clientSid))Add(security,"S-1-5-32-544",clientRights,AccessControlType.Allow);
        return NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,32,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,0,security,HandleInheritability.None,PipeAccessRights.ChangePermissions);
    }
    private static void Add(PipeSecurity security,string sid,PipeAccessRights rights,AccessControlType type)=>security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid),rights,type));
}

[SupportedOSPlatform("windows")]
public sealed record ProtectedAccessPolicy(string Mode,IReadOnlyList<string> Values)
{
    public static ProtectedAccessPolicy Off { get; }=new("off",[]);
}

public sealed record ProtectedExecutionGrant(string Label,ProtectedAccessPolicy Applications,ProtectedAccessPolicy Folders);

[SupportedOSPlatform("windows")]
public static class ProtectedAccessRules
{
    public static bool AllowsApplication(ProtectedExecutionGrant definition,string? applicationPath)
    {
        var policy=definition.Applications;if(policy.Mode=="off")return true;if(string.IsNullOrWhiteSpace(applicationPath))return false;
        string canonical;try{canonical=Path.GetFullPath(applicationPath);}catch{return false;}
        var listed=policy.Values.Any(value=>StringComparer.OrdinalIgnoreCase.Equals(value,canonical));return policy.Mode=="allow"?listed:!listed;
    }

    public static bool AllowsFolders(ProtectedExecutionGrant definition,OperationRequest request)
    {
        var policy=definition.Folders;if(policy.Mode=="off")return true;
        var paths=request.Arg.Where(x=>x.Key is "src" or "dst" or "cwd" or "workspace_root").Select(x=>x.Value).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var path in paths)
        {
            var listed=policy.Mode=="allow"?policy.Values.Any(root=>PathNormalizer.Contains(root,path)):
                policy.Values.Any(root=>PathNormalizer.Contains(root,path)||PathNormalizer.Contains(path,root));
            if(policy.Mode=="allow"&&!listed||policy.Mode=="deny"&&listed)return false;
        }
        return true;
    }

    public static ProtectedAccessPolicy NormalizePolicy(ProtectedAccessPolicy? policy,bool application)
    {
        policy??=ProtectedAccessPolicy.Off;var mode=policy.Mode?.Trim().ToLowerInvariant();if(mode=="all")mode="off";
        if(mode is not("off" or "allow" or "deny")||policy.Values is null||policy.Values.Count>32)throw new ArgumentException("protected_policy");
        var values=new List<string>();
        foreach(var raw in policy.Values)
        {
            if(string.IsNullOrWhiteSpace(raw)||raw.Length>1024||!Path.IsPathFullyQualified(raw))throw new ArgumentException("protected_policy_path");
            var canonical=new WindowsPathContextNormalizer().Normalize(Path.GetFullPath(raw)).CanonicalPath;
            if(application&&(!string.Equals(Path.GetExtension(canonical),".exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(canonical)))throw new ArgumentException("protected_policy_app");
            values.Add(canonical);
        }
        values=values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if(mode=="off")values.Clear();else if(values.Count==0)throw new ArgumentException("protected_policy_empty");
        return new(mode,values);
    }
}

internal static class MachineDpapi
{
    public static byte[] Protect(byte[] value)=>Transform(value,true);
    public static byte[] Unprotect(byte[] value)=>Transform(value,false);
    private static byte[] Transform(byte[] value,bool protect)
    {
        var input=Blob(value);DATA_BLOB output=default;
        try
        {
            var ok=protect?CryptProtectData(ref input,null,0,0,0,0x4,out output):CryptUnprotectData(ref input,0,0,0,0,0x4,out output);
            if(!ok)throw new CryptographicException(Marshal.GetLastWin32Error());var result=new byte[output.cbData];Marshal.Copy(output.pbData,result,0,result.Length);return result;
        }
        finally{if(input.pbData!=0)Marshal.FreeHGlobal(input.pbData);if(output.pbData!=0)LocalFree(output.pbData);}
    }
    private static DATA_BLOB Blob(byte[] value){var blob=new DATA_BLOB{cbData=value.Length,pbData=Marshal.AllocHGlobal(value.Length)};Marshal.Copy(value,0,blob.pbData,value.Length);return blob;}
    [StructLayout(LayoutKind.Sequential)] private struct DATA_BLOB{public int cbData;public nint pbData;}
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool CryptProtectData(ref DATA_BLOB input,string? description,nint entropy,nint reserved,nint prompt,uint flags,out DATA_BLOB output);
    [DllImport("crypt32.dll",SetLastError=true)] private static extern bool CryptUnprotectData(ref DATA_BLOB input,nint description,nint entropy,nint reserved,nint prompt,uint flags,out DATA_BLOB output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

internal sealed record RoutedJob(OperationReceipt Receipt,long Sequence,OperationRequest? Request,OperationRequest? RetryRequest,string Sid,long Generation);

internal sealed partial class RoutedJobStore:IDisposable
{
    private static readonly HashSet<string> UiEphemeralKeys=new(StringComparer.Ordinal){"title","window_text","control_class"};
    private readonly SqliteConnection _connection;private readonly object _gate=new();private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public RoutedJobStore(string stateRoot,bool recover=true)
    {
        _connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(stateRoot,"agentops-router.db"),Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());_connection.Open();
        using(var setup=_connection.CreateCommand()){setup.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS routed_jobs(jid TEXT PRIMARY KEY,rid TEXT NOT NULL UNIQUE,seq INTEGER NOT NULL UNIQUE,state TEXT NOT NULL,receipt TEXT NOT NULL,request TEXT NULL,retry_request TEXT NULL,owner_sid TEXT NOT NULL,owner_session INTEGER NOT NULL,generation INTEGER NOT NULL DEFAULT 0); CREATE INDEX IF NOT EXISTS ix_routed_state_seq ON routed_jobs(state,seq); CREATE TABLE IF NOT EXISTS router_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);";setup.ExecuteNonQuery();}
        EnsureColumn("retry_request","TEXT NULL");InitializeMaintenanceAcknowledgements();if(recover)RecoverDispatching();
    }
    public RoutedJob Claim(OperationRequest request,string ownerSid){lock(_gate){using var tx=_connection.BeginTransaction();using var existing=_connection.CreateCommand();existing.Transaction=tx;existing.CommandText="SELECT receipt,seq,request,retry_request,owner_sid,generation FROM routed_jobs WHERE rid=$rid";existing.Parameters.AddWithValue("$rid",request.Rid!);using(var reader=existing.ExecuteReader())if(reader.Read()){var found=Read(reader);tx.Commit();return found;}using var next=_connection.CreateCommand();next.Transaction=tx;next.CommandText="SELECT COALESCE(MAX(seq),0)+1 FROM routed_jobs";var seq=Convert.ToInt64(next.ExecuteScalar());var receipt=new OperationReceipt(true,Guid.NewGuid().ToString("N"),request.Rid!,"waiting_user",OperationNames.Public(request),Data:new Dictionary<string,object?>{{"seq",seq},{"approval_required",true},{"approval_via","tray"}});var durable=DurableRequest(request);var serialized=JsonSerializer.Serialize(durable,Json);var retry=durable.Arg.ContainsKey(OperationRequest.BodyOmittedKey)?null:serialized;using var insert=_connection.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT INTO routed_jobs(jid,rid,seq,state,receipt,request,retry_request,owner_sid,owner_session,generation) VALUES($jid,$rid,$seq,$state,$receipt,$request,$retry,$sid,0,0)";insert.Parameters.AddWithValue("$jid",receipt.Jid);insert.Parameters.AddWithValue("$rid",receipt.Rid);insert.Parameters.AddWithValue("$seq",seq);insert.Parameters.AddWithValue("$state",receipt.State);insert.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt,Json));insert.Parameters.AddWithValue("$request",serialized);insert.Parameters.AddWithValue("$retry",(object?)retry??DBNull.Value);insert.Parameters.AddWithValue("$sid",ownerSid);insert.ExecuteNonQuery();tx.Commit();return new RoutedJob(receipt,seq,request,retry is null?null:request,ownerSid,0);}}
    public IReadOnlyList<RoutedJob> LoadWaiting(){lock(_gate){using var command=_connection.CreateCommand();command.CommandText="SELECT receipt,seq,request,retry_request,owner_sid,generation FROM routed_jobs WHERE state='waiting_user' AND NOT EXISTS(SELECT 1 FROM routed_maintenance_acks a WHERE a.jid=routed_jobs.jid) ORDER BY seq";using var reader=command.ExecuteReader();var result=new List<RoutedJob>();while(reader.Read())result.Add(Read(reader));return result;}}
    // Health queries need a number, never hydrated pending bodies or retry copies.
    public long CountWaiting(){lock(_gate){using var command=_connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM routed_jobs WHERE state='waiting_user' AND NOT EXISTS(SELECT 1 FROM routed_maintenance_acks a WHERE a.jid=routed_jobs.jid)";return Convert.ToInt64(command.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture);}}
    public RoutedJob? TryLease(string jid,long generation){lock(_gate){var found=FindLocked(jid);if(found is null||found.Receipt.State!="waiting_user")return null;var leased=found with{Generation=generation,Receipt=found.Receipt with{State="dispatching"}};using var command=_connection.CreateCommand();command.CommandText="UPDATE routed_jobs SET state='dispatching',receipt=$receipt,generation=$generation WHERE jid=$jid AND state='waiting_user' AND NOT EXISTS(SELECT 1 FROM routed_jobs older WHERE older.seq < $seq AND older.state IN ('waiting_user','dispatching') AND NOT EXISTS(SELECT 1 FROM routed_maintenance_acks a WHERE a.jid=older.jid))";command.Parameters.AddWithValue("$jid",jid);command.Parameters.AddWithValue("$seq",found.Sequence);command.Parameters.AddWithValue("$generation",generation);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(leased.Receipt,Json));return command.ExecuteNonQuery()==1?leased:null;}}
    public RoutedJob? Find(string jid){lock(_gate)return FindLocked(jid);}
    public IReadOnlyList<OperationReceipt> List(int max=100){if(max is <1 or >100)throw new AgentOpsException("invalid");lock(_gate){using var command=_connection.CreateCommand();command.CommandText="SELECT receipt FROM routed_jobs ORDER BY seq DESC LIMIT $max";command.Parameters.AddWithValue("$max",max);using var reader=command.ExecuteReader();var result=new List<OperationReceipt>();while(reader.Read()){var receipt=JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(0),Json)!;result.Add(receipt with{Data=null,Detail=null});}return result;}}
    public bool Complete(RoutedJob job,OperationReceipt inner){var data=new Dictionary<string,object?>(inner.Data??new Dictionary<string,object?>());data["seq"]=job.Sequence;var receipt=inner with{Jid=job.Receipt.Jid,Rid=job.Receipt.Rid,Data=data};lock(_gate){using var command=_connection.CreateCommand();command.CommandText="UPDATE routed_jobs SET state=$state,receipt=$receipt,request=NULL WHERE jid=$jid AND state='dispatching' AND generation=$generation";command.Parameters.AddWithValue("$jid",job.Receipt.Jid);command.Parameters.AddWithValue("$generation",job.Generation);command.Parameters.AddWithValue("$state",receipt.State);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt,Json));return command.ExecuteNonQuery()==1;}}
    public void Release(RoutedJob job){lock(_gate){using var command=_connection.CreateCommand();command.CommandText="UPDATE routed_jobs SET state='waiting_user',receipt=$receipt WHERE jid=$jid AND state='dispatching' AND generation=$generation";command.Parameters.AddWithValue("$jid",job.Receipt.Jid);command.Parameters.AddWithValue("$generation",job.Generation);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(job.Receipt with{State="waiting_user"},Json));command.ExecuteNonQuery();}}
    public void RecoveryRequired(RoutedJob job,string? diagnostic=null){var data=new Dictionary<string,object?>(job.Receipt.Data??new Dictionary<string,object?>());if(diagnostic is not null)data["diagnostic"]=diagnostic;var receipt=job.Receipt with{Ok=false,State="recovery_required",Err="recovery_required",Partial=true,Data=data};lock(_gate){using var command=_connection.CreateCommand();command.CommandText="UPDATE routed_jobs SET state='recovery_required',receipt=$receipt,request=NULL WHERE jid=$jid AND state='dispatching' AND generation=$generation";command.Parameters.AddWithValue("$jid",job.Receipt.Jid);command.Parameters.AddWithValue("$generation",job.Generation);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt,Json));command.ExecuteNonQuery();}}
    public OperationReceipt? CancelQueued(string jid){lock(_gate){var found=FindLocked(jid);if(found is null||found.Receipt.State!="waiting_user")return null;var data=new Dictionary<string,object?>(found.Receipt.Data??new Dictionary<string,object?>()){{"cancel_applied",true}};var receipt=found.Receipt with{Ok=true,State="canceled",Err=null,Data=data};using var command=_connection.CreateCommand();command.CommandText="UPDATE routed_jobs SET state='canceled',receipt=$receipt,request=NULL WHERE jid=$jid AND state='waiting_user'";command.Parameters.AddWithValue("$jid",jid);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt,Json));return command.ExecuteNonQuery()==1?receipt:null;}}
    public RoutedJob Retry(string jid,string rid,string ownerSid,bool system=false)
    {
        lock(_gate)
        {
            using var tx=_connection.BeginTransaction();
            using(var existing=_connection.CreateCommand()){existing.Transaction=tx;existing.CommandText="SELECT receipt,seq,request,retry_request,owner_sid,generation FROM routed_jobs WHERE rid=$rid";existing.Parameters.AddWithValue("$rid",rid);using var existingReader=existing.ExecuteReader();if(existingReader.Read()){var idempotent=Read(existingReader);tx.Commit();return idempotent;}}
            RoutedJob source;using(var target=_connection.CreateCommand()){target.Transaction=tx;target.CommandText="SELECT receipt,seq,request,retry_request,owner_sid,generation FROM routed_jobs WHERE jid=$jid";target.Parameters.AddWithValue("$jid",jid);using var reader=target.ExecuteReader();if(!reader.Read())throw new AgentOpsException("not_found");source=Read(reader);}
            if(!StringComparer.Ordinal.Equals(source.Sid,ownerSid))throw new AgentOpsException("auth");
            if(source.Receipt.State is not("failed" or "canceled" or "recovery_required" or "attention"))throw new AgentOpsException("precondition");
            if(source.RetryRequest is null||source.RetryRequest.Arg.ContainsKey("__execution_context_system")!=system)throw new AgentOpsException("precondition");
            var request=source.RetryRequest with{Rid=rid,Body=null};
            using var next=_connection.CreateCommand();next.Transaction=tx;next.CommandText="SELECT COALESCE(MAX(seq),0)+1 FROM routed_jobs";var seq=Convert.ToInt64(next.ExecuteScalar());
            var receipt=new OperationReceipt(true,Guid.NewGuid().ToString("N"),rid,"waiting_user",OperationNames.Public(request),Data:new Dictionary<string,object?>{{"seq",seq},{"retry_of",jid},{"approval_required",true},{"approval_via","tray"}});var serialized=JsonSerializer.Serialize(request,Json);
            using var insert=_connection.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT INTO routed_jobs(jid,rid,seq,state,receipt,request,retry_request,owner_sid,owner_session,generation) VALUES($jid,$rid,$seq,'waiting_user',$receipt,$request,$retry,$sid,0,0)";insert.Parameters.AddWithValue("$jid",receipt.Jid);insert.Parameters.AddWithValue("$rid",rid);insert.Parameters.AddWithValue("$seq",seq);insert.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt,Json));insert.Parameters.AddWithValue("$request",serialized);insert.Parameters.AddWithValue("$retry",serialized);insert.Parameters.AddWithValue("$sid",ownerSid);insert.ExecuteNonQuery();tx.Commit();return new RoutedJob(receipt,seq,request,request,ownerSid,0);
        }
    }
    internal OperationReceipt? FirstUnresolvedMutation(string ownerSid)
    {
        lock(_gate)
        {
            using var command=_connection.CreateCommand();
            command.CommandText="SELECT receipt,COALESCE(request,retry_request) FROM routed_jobs WHERE owner_sid=$sid AND state IN ('waiting_user','dispatching','recovery_required','attention') AND NOT EXISTS(SELECT 1 FROM routed_maintenance_acks a WHERE a.jid=routed_jobs.jid) ORDER BY seq";
            command.Parameters.AddWithValue("$sid",ownerSid);
            using var reader=command.ExecuteReader();
            // Stream only unresolved indexed rows, never materialize terminal history.
            while(reader.Read())
            {
                var receipt=JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(0),Json)!;
                var request=reader.IsDBNull(1)?null:JsonSerializer.Deserialize<OperationRequest>(reader.GetString(1),Json);
                if(request is null||AgentOpsEngine.IsMutation(request))return receipt;
            }
            return null;
        }
    }
    public bool IsPaused(){lock(_gate){using var command=_connection.CreateCommand();command.CommandText="SELECT value FROM router_settings WHERE key='paused'";return StringComparer.Ordinal.Equals(command.ExecuteScalar()?.ToString(),"1");}}
    public void SetPaused(bool paused){lock(_gate){using var command=_connection.CreateCommand();command.CommandText="INSERT INTO router_settings(key,value) VALUES('paused',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";command.Parameters.AddWithValue("$value",paused?"1":"0");command.ExecuteNonQuery();}}
    public void Canceled(RoutedJob job){var receipt=job.Receipt with{Ok=false,State="canceled",Err="canceled"};lock(_gate){using var command=_connection.CreateCommand();command.CommandText="UPDATE routed_jobs SET state='canceled',receipt=$receipt,request=NULL WHERE jid=$jid AND state='dispatching' AND generation=$generation";command.Parameters.AddWithValue("$jid",job.Receipt.Jid);command.Parameters.AddWithValue("$generation",job.Generation);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt,Json));command.ExecuteNonQuery();}}
    public OperationReceipt? RecoveryRequiredWaiting(string jid){lock(_gate){var found=FindLocked(jid);if(found is null||found.Receipt.State!="waiting_user")return null;var receipt=found.Receipt with{Ok=false,State="recovery_required",Err="recovery_required",Partial=true};using var command=_connection.CreateCommand();command.CommandText="UPDATE routed_jobs SET state='recovery_required',receipt=$receipt,request=NULL WHERE jid=$jid AND state='waiting_user'";command.Parameters.AddWithValue("$jid",jid);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt,Json));return command.ExecuteNonQuery()==1?receipt:null;}}
    public void RecoverEphemeralWaiting(){foreach(var job in LoadWaiting())if(job.Request?.Arg.ContainsKey(OperationRequest.BodyOmittedKey)==true)RecoveryRequiredWaiting(job.Receipt.Jid);}
    public void QuarantineForeignWaiting(string ownerSid){foreach(var job in LoadWaiting())if(!StringComparer.Ordinal.Equals(job.Sid,ownerSid))RecoveryRequiredWaiting(job.Receipt.Jid);}
    private RoutedJob? FindLocked(string jid){using var command=_connection.CreateCommand();command.CommandText="SELECT receipt,seq,request,retry_request,owner_sid,generation FROM routed_jobs WHERE jid=$jid";command.Parameters.AddWithValue("$jid",jid);using var reader=command.ExecuteReader();return reader.Read()?Read(reader):null;}
    private static RoutedJob Read(SqliteDataReader reader)=>new(JsonSerializer.Deserialize<OperationReceipt>(reader.GetString(0),Json)!,reader.GetInt64(1),reader.IsDBNull(2)?null:JsonSerializer.Deserialize<OperationRequest>(reader.GetString(2),Json),reader.IsDBNull(3)?null:JsonSerializer.Deserialize<OperationRequest>(reader.GetString(3),Json),reader.GetString(4),reader.GetInt64(5));
    private static OperationRequest DurableRequest(OperationRequest request)
    {
        var arg=request.Op=="ui"
            ?request.Arg.Where(item=>!UiEphemeralKeys.Contains(item.Key)).ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal)
            :new Dictionary<string,string>(request.Arg,StringComparer.Ordinal);
        return request with{Arg=arg,Body=null};
    }
    private void RecoverDispatching(){foreach(var job in QueryDispatching())RecoveryRequired(job);}
    private IReadOnlyList<RoutedJob> QueryDispatching(){lock(_gate){using var command=_connection.CreateCommand();command.CommandText="SELECT receipt,seq,request,retry_request,owner_sid,generation FROM routed_jobs WHERE state='dispatching' AND NOT EXISTS(SELECT 1 FROM routed_maintenance_acks a WHERE a.jid=routed_jobs.jid) ORDER BY seq";using var reader=command.ExecuteReader();var result=new List<RoutedJob>();while(reader.Read())result.Add(Read(reader));return result;}}
    private void EnsureColumn(string name,string declaration){using var query=_connection.CreateCommand();query.CommandText="PRAGMA table_info(routed_jobs)";using(var reader=query.ExecuteReader()){while(reader.Read())if(StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(1),name))return;}using var alter=_connection.CreateCommand();alter.CommandText=$"ALTER TABLE routed_jobs ADD COLUMN {name} {declaration}";alter.ExecuteNonQuery();}
    public void Dispose()=>_connection.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed partial class AuthenticatedServiceBroker:IAsyncDisposable
{
    private const string SystemContextKey="__execution_context_system";
    private static readonly HashSet<string> SystemAllowed=new(StringComparer.Ordinal){"diag","stat","ls","rd","mkdir","cp","mv","rm","sha","zip","proc","service","windows_service","windows_task","net","job","admin"};
    private static readonly HashSet<string> SystemReadOnly=new(StringComparer.Ordinal){"diag","stat","ls","rd","sha","net"};
    private sealed record TrayRoute(LocalPeer Peer,string PipeName,long Generation,MaintenanceHostIdentity? Identity=null);
    private readonly MaintenanceInitializationObservation _maintenanceInitialization;
    private readonly OwnerAdminExecution? _ownerAdmin;private readonly string _ownerSid;private readonly Func<CortexMcpConnectionAuthority,bool,bool>? _connectionAuthorization;private readonly Func<LocalPeer,bool>? _ownerHostVerifier;private readonly RoutedJobStore _store;private readonly WorkspaceRegistry _workspaces;private readonly ApplicationCredentialRegistry? _applicationCredentials;private readonly SemaphoreSlim _fifo=new(1,1);private readonly SemaphoreSlim _drainGate=new(1,1);private readonly ConcurrentDictionary<string,TaskCompletionSource<OperationReceipt>> _waiters=new();private readonly ConcurrentDictionary<string,CancellationTokenSource> _jobCancellation=new();private readonly ConcurrentDictionary<string,OperationRequest> _ephemeralRequests=new();private readonly ConcurrentDictionary<string,OperationReceipt> _liveReceipts=new();private readonly ConcurrentDictionary<long,Task> _drains=new();private readonly bool _crashAfterTrayExecution;private readonly Func<LocalPeer,string,OperationRequest,CancellationToken,Task<OperationReceipt>>? _trayDispatch;private readonly Func<OperationRequest,CancellationToken,Task<OperationReceipt>>? _systemDispatch;private long _generation;private long _drainId;private volatile TrayRoute? _tray;
    public AuthenticatedServiceBroker(string stateRoot,string ownerSid,Func<CortexMcpConnectionAuthority,bool,bool>? connectionAuthorization,bool crashAfterTrayExecution=false):this(stateRoot,ownerSid,connectionAuthorization,crashAfterTrayExecution,null){}
    internal AuthenticatedServiceBroker(string stateRoot,string ownerSid,Func<CortexMcpConnectionAuthority,bool,bool>? connectionAuthorization,bool crashAfterTrayExecution,Func<LocalPeer,string,OperationRequest,CancellationToken,Task<OperationReceipt>>? trayDispatch,Func<OperationRequest,CancellationToken,Task<OperationReceipt>>? systemDispatch=null,ApplicationCredentialRegistry? applicationCredentials=null,OwnerAdminExecution? ownerAdmin=null,IMaintenanceModeSource? maintenanceMode=null,MaintenanceInitializationObservation? maintenanceInitialization=null,ServiceExitAdmission? serviceExitAdmission=null,Func<LocalPeer,bool>? ownerHostVerifier=null){_ownerHostVerifier=ownerHostVerifier;_serviceExitAdmission=serviceExitAdmission??new ServiceExitAdmission();_maintenanceInitialization=maintenanceInitialization??MaintenanceInitializationObservation.Started;_maintenanceMode=maintenanceMode!;_ownerAdmin=ownerAdmin;_ownerSid=ownerSid;_connectionAuthorization=connectionAuthorization;_crashAfterTrayExecution=crashAfterTrayExecution;_trayDispatch=trayDispatch;_systemDispatch=systemDispatch;_applicationCredentials=applicationCredentials;Directory.CreateDirectory(stateRoot);_store=new RoutedJobStore(stateRoot,recover:false);_workspaces=new WorkspaceRegistry(new AgentOpsPaths(stateRoot));InitializeMaintenanceBroker(stateRoot);}
    public async Task<OperationReceipt> HandleUserAsync(OperationRequest request,LocalPeer peer,CancellationToken ct)
    {
        if(request.Op.StartsWith('_'))return Fail(request,"private_operation");
        if(request.System)return Fail(request,"user_dispatch_required");
        if(request.ConnectionAuthority is { } claimed&&
            (!IsAdmittedOwnerHost(peer)||!ConnectionIsCurrent(claimed,false)))return Fail(request,"auth");
        if(ProcessThreadOperations.IsRequest(request))return Fail(request,"user_dispatch_required");
        if(request.Op is "registry" or "certificate" or "windows_service" or "windows_task" or "installed_program" or "connection_profile" or "windows_device" or "windows_network" or "windows_account")return Fail(request,"user_dispatch_required");
        if(!IsOwner(peer))return Fail(request,"auth");
        if(!_serviceExitAdmission.TryEnter(out var serviceAdmission))return Fail(request,"service_exiting");
        using var serviceLease=serviceAdmission;
        if(request.Op=="admin")return Fail(request,"auth");
        if(request.Op=="app")return Fail(request,"user_dispatch_required");
        if(request.Root is not null)return Fail(request,"auth");
        if(AdminFences(request))return Fail(request,"admin_lane_busy");
        if(!TryEnterOrdinaryControl(request,out var controlTransition))return Fail(request,"maintenance_busy");
        using var controlLease=controlTransition;
        if(request.Op=="application_access")return ApplicationAccess(request);
        if(request.Op=="root")return Fail(request,"root_codes_removed");
        if(request.Op=="queue")
        {
            var control=request.Arg.GetValueOrDefault("op","stat");
            if(control=="stat")return new(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done","queue",Data:new Dictionary<string,object?>{{"paused",_store.IsPaused()},{"queued",_store.CountWaiting()}});
            if(control is not("pause" or "resume"))return Fail(request,"invalid");
            return SetTrayPaused(control=="pause",peer);
        }
        if(request.Op=="job")return await JobAsync(request,ct);
        if(request.Op=="diag")return new OperationReceipt(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done","diag",Data:new Dictionary<string,object?>{{"service","ok"},{"tray",_tray is null?"down":"ready"},{"queued",_store.CountWaiting()},{"paused",_store.IsPaused()},{"product",AgentOpsProductInfo.Product},{"product_version",AgentOpsProductInfo.ProductVersion},{"protocol",$"{ProtocolVersion.Major}.{ProtocolVersion.Minor}"},{"mcp_schema",AgentOpsProductInfo.McpSchemaVersion},{"manifest",AgentOpsProductInfo.InstallManifestVersion},{"maintenance_init_feature",_maintenanceInitialization.Feature},{"maintenance_init_outcome",_maintenanceInitialization.Outcome},{"maintenance_init_stage",_maintenanceInitialization.Stage},{"maintenance_init_cause",_maintenanceInitialization.Cause}});
        if(request.Op=="ctx"&&!request.Arg.ContainsKey("register"))return Context(request);
        if(request.Ws is not null&&request.Cwd is null&&_workspaces.Resolve(request.Ws,Environment.CurrentDirectory) is { } workspaceRoot)request=request with{Cwd=workspaceRoot.Path};
        if(request.Ws is not null&&request.Cwd is not null&&_workspaces.Resolve(request.Ws,request.Cwd) is { } selected&&!PathNormalizer.Contains(selected.Path,request.Cwd))request=request with{Cwd=selected.Path};
        request=request with{Arg=request.Arg.Where(x=>x.Key is not("cwd" or "ws")).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal)};
        OperationRequest clean;try{clean=FreezeUserRequest(request);}catch(AgentOpsException ex){return Fail(request,ex.Code);}catch(UnauthorizedAccessException){return Fail(request,"access");}catch(Exception ex)when(ex is ArgumentException or IOException){return Fail(request,"invalid");}
        var job=ClaimOrdinary(clean);if(job is null)return Fail(request,"maintenance_busy");if(!StringComparer.Ordinal.Equals(job.Sid,_ownerSid))return Fail(request,"auth");if(IsLiveEphemeral(job.Request))_ephemeralRequests.TryAdd(job.Receipt.Jid,job.Request!);if(job.Request is null)return job.Receipt;if((_tray is null||_store.IsPaused())&&clean.Op!="ctx")return job.Receipt;var waiter=Waiter(job.Receipt.Jid);_jobCancellation.GetOrAdd(job.Receipt.Jid,_=>new CancellationTokenSource());ScheduleDrain();var current=_store.Find(job.Receipt.Jid);if(current is not null&&IsTerminal(current.Receipt.State))return SignalTerminal(job.Receipt.Jid);return await waiter.Task.WaitAsync(ct);
    }
    public async Task<OperationReceipt> HandleSystemAsync(OperationRequest request,LocalPeer peer,CancellationToken ct)
    {
        if(request.Op=="certificate")return Fail(request,"auth");
        if(ProcessThreadOperations.IsRequest(request))return Fail(request,"auth");
        if(request.Op==ServiceExitOperation)return await ServiceExitAsync(request,peer,ct);
        if(request.Op.StartsWith('_'))return Fail(request,"private_operation");
        if(!_serviceExitAdmission.TryEnter(out var serviceAdmission))return Fail(request,"service_exiting");
        using var serviceLease=serviceAdmission;
        if (request.Op == "windows_task")
        {
            // Validate before generic authority projection could erase an
            // unexpected field from the original closed task envelope.
            try { _ = WindowsTaskOperationContract.Parse(request); }
            catch (WindowsTaskContractException) { return Fail(request, "invalid"); }
        }
        if(request.Root is not null)return Fail(request,"root_codes_removed");
        if(!request.System||request.ConnectionAuthority is not { } connection||!IsAdmittedOwnerHost(peer)||
            !ConnectionIsCurrent(connection,true)||!SystemAllowed.Contains(request.Op))return Fail(request,"full_control_required");
        var authorization=new ProtectedExecutionGrant(connection.ConnectionId,ProtectedAccessPolicy.Off,ProtectedAccessPolicy.Off);
        request=request with{Root=null,PathRoots=null,Arg=request.Arg.Where(x=>x.Key is not("root" or "stdin")&&!x.Key.StartsWith("env",StringComparison.Ordinal)).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal)};
        if(!ProtectedAccessRules.AllowsApplication(authorization,peer.ApplicationPath))return Fail(request,"root_app_denied");
        if (request.Op == "windows_task")
        {
            if (_systemDispatch is null) return Fail(request, "service_unavailable");
            try
            {
                var invocation = WindowsTaskOperationContract.Parse(request);
                if (!WindowsTaskOperationContract.IsSystemAction(invocation.Action)) return Fail(request, "auth");
                if (AdminFences(request)) return Fail(request, "admin_lane_busy");
                if (!TryEnterOrdinaryControl(request, out var transition)) return Fail(request, "maintenance_busy");
                using (transition)
                {
                    var trusted = request with { Arg = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal) { [SystemContextKey] = "1" } };
                    // One durable RID owner in the System engine. No generic
                    // router persistence, alternate principal or replay.
                    return await _systemDispatch(trusted, ct).ConfigureAwait(false);
                }
            }
            catch (WindowsTaskContractException) { return Fail(request, "invalid"); }
        }
        if (request.Op == "windows_service")
        {
            if (_systemDispatch is null) return Fail(request, "service_unavailable");
            try
            {
                var invocation = WindowsServiceOperationContract.Parse(request);
                if (invocation.RequiresOwnerElevation) return Fail(request, "user_dispatch_required");
                if (!invocation.IsMutation && invocation.Action != "read") return Fail(request, "user_dispatch_required");
                if (AdminFences(request)) return Fail(request, "admin_lane_busy");
                if (!TryEnterOrdinaryControl(request, out var transition)) return Fail(request, "maintenance_busy");
                using (transition)
                {
                    var trusted = request with { Arg = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal) { [SystemContextKey] = "1" } };
                    // The authenticated engine is the sole durable RID owner;
                    // never persist or recover this body in the router as well.
                    return await _systemDispatch(trusted, ct).ConfigureAwait(false);
                }
            }
            catch (AgentOpsException error) { return Fail(request, error.Code); }
        }
        if(request.Op=="admin")
        {
            if(_ownerAdmin is null)return Fail(request,"admin_capability_unavailable");
            if(request.Arg.GetValueOrDefault("op")!="run")
            {
                var result=await _ownerAdmin.HandleAsync(request,peer,authorization,ct,()=>ConnectionIsCurrent(connection,true));
                if(!MaintenanceClosed()){ResumeRouterRecovery();ScheduleDrain();}
                return result;
            }
            // Join the existing service mutation lane for admission/launch. The
            // persistent admin effect fence remains after this observer returns.
            await _fifo.WaitAsync(ct);
            try
            {
                if(!ConnectionIsCurrent(connection,true))return Fail(request,"connection_revoked");
                if(!_ownerAdmin.HasOriginalRid(request.Rid)&&_store.FirstUnresolvedMutation(_ownerSid) is{} original)
                    return Fail(request,"admin_reconciliation_required") with{Data=new Dictionary<string,object?>{{"original_jid",original.Jid},{"original_rid",original.Rid},{"original_state",original.State},{"owner","broker"}}};
                return await _ownerAdmin.HandleAsync(request,peer,authorization,ct,()=>ConnectionIsCurrent(connection,true));
            }
            finally{_fifo.Release();}
        }
        if(AdminFences(request))return Fail(request,"admin_lane_busy");
        if(!TryEnterOrdinaryControl(request,out var controlTransition))return Fail(request,"maintenance_busy");
        using var controlLease=controlTransition;
        if (request.Op == "job")
        {
            if (request.Arg.GetValueOrDefault("op") is "stat" or "wait")
            {
                try
                {
                    AgentOpsEngine.RequireSystemServiceJobObservation(request);
                    if (_systemDispatch is null) return Fail(request, "service_unavailable");
                    var trusted = request with { Arg = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal) { [SystemContextKey] = "1" } };
                    // The same Engine remains the only durable owner; observing
                    // its scoped service receipt creates no routed job or retry.
                    return await _systemDispatch(trusted, ct).ConfigureAwait(false);
                }
                catch (AgentOpsException error) { return Fail(request, error.Code); }
            }
            return RetrySystemJob(request, authorization);
        }
        if(_systemDispatch is null)return request.Op=="diag"?new OperationReceipt(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done","diag",Data:new Dictionary<string,object?>{{"service","ok"}}):Fail(request,"service_unavailable");
        OperationRequest clean;try{clean=FreezeUserRequest(request);}catch(AgentOpsException ex){return Fail(request,ex.Code);}catch(UnauthorizedAccessException){return Fail(request,"access");}catch(Exception ex)when(ex is ArgumentException or IOException){return Fail(request,"invalid");}
        if(!ProtectedAccessRules.AllowsFolders(authorization,clean))return Fail(request,"root_path_denied");
        clean=clean with{Arg=new Dictionary<string,string>(clean.Arg,StringComparer.Ordinal){{SystemContextKey,"1"}}};if(SystemReadOnly.Contains(clean.Op))return await _systemDispatch(clean,ct);
        var job=ClaimOrdinary(clean);if(job is null)return Fail(request,"maintenance_busy");if(job.Request is null)return job.Receipt;var waiter=Waiter(job.Receipt.Jid);_jobCancellation.GetOrAdd(job.Receipt.Jid,_=>new CancellationTokenSource());ScheduleDrain();var current=_store.Find(job.Receipt.Jid);if(current is not null&&IsTerminal(current.Receipt.State))return SignalTerminal(job.Receipt.Jid);return await waiter.Task.WaitAsync(ct);
    }
    internal async Task<OperationReceipt> HandleApplicationAsync(OperationRequest request,CancellationToken ct)
    {
        if(request.Op.StartsWith('_')||request.ConnectionAuthority is not null)return Fail(request,"private_operation");
        if(ProcessThreadOperations.IsRequest(request))return Fail(request,"auth");
        if(request.Op is "registry" or "certificate" or "windows_service" or "windows_task" or "installed_program" or "connection_profile" or "windows_device" or "windows_network" or "windows_account")return Fail(request,"auth");
        if(!_serviceExitAdmission.TryEnter(out var serviceAdmission))return Fail(request,"service_exiting");
        using var serviceLease=serviceAdmission;
        // Application authorization is established once by the private pipe host.
        // Never accept credential/root material inside the reusable PAO contract.
        if(request.Root is not null||request.PathRoots is not null||request.Op is "root" or "queue" or "admin"||
            request.Arg.Keys.Any(key=>key.Equals("root",StringComparison.OrdinalIgnoreCase)||
                key.Equals("root_code",StringComparison.OrdinalIgnoreCase)||
                key.Equals("credential",StringComparison.OrdinalIgnoreCase)))
            return Fail(request,"auth");
        request=request with{Root=null,PathRoots=null,Arg=request.Arg.Where(x=>x.Key is not("root" or "stdin")&&!x.Key.StartsWith("env",StringComparison.Ordinal)).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal)};
        if(request.Op=="service")return Fail(request,"auth");
        if(request.Op is "tool" or "recipe" or "run" or "pwsh" or "git" or "dotnet" or "do" or "nsis")
        {
            // These families resolve declared User tools and owner registries.
            // Full control is permission, not a change to a tool's Windows token.
            var route=_tray;
            if(request.System)return Fail(request,"user_dispatch_required");
            if(route is null||!IsAdmittedOwnerHost(route.Peer))return Fail(request,"user_host_unavailable");
            return await HandleUserAsync(request,route.Peer,ct);
        }
        if(AdminFences(request))return Fail(request,"admin_lane_busy");
        if(!TryEnterOrdinaryControl(request,out var controlTransition))return Fail(request,"maintenance_busy");
        using var controlLease=controlTransition;
        if(request.Op=="job")
        {
            if(request.Arg.GetValueOrDefault("op")=="retry")
            {
                if(request.Arg.GetValueOrDefault("jid") is not{Length:>0} jid)return Fail(request,"invalid");
                try{var retried=_store.Retry(jid,string.IsNullOrWhiteSpace(request.Rid)?Guid.NewGuid().ToString("N"):request.Rid!,_ownerSid,system:true);ScheduleDrain();return retried.Receipt;}
                catch(AgentOpsException ex){return Fail(request,ex.Code);}
            }
            return await JobAsync(request,ct);
        }
        if(_systemDispatch is null)return request.Op=="diag"?new OperationReceipt(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done","diag",Data:new Dictionary<string,object?>{{"service","ok"},{"application_session","ready"}}):Fail(request,"service_unavailable");
        OperationRequest clean;try{clean=FreezeUserRequest(request);}catch(AgentOpsException ex){return Fail(request,ex.Code);}catch(UnauthorizedAccessException){return Fail(request,"access");}catch(Exception ex)when(ex is ArgumentException or IOException){return Fail(request,"invalid");}
        clean=clean with{Arg=new Dictionary<string,string>(clean.Arg,StringComparer.Ordinal){{SystemContextKey,"1"}}};
        if(SystemReadOnly.Contains(clean.Op))return await _systemDispatch(clean,ct);
        var job=ClaimOrdinary(clean);if(job is null)return Fail(request,"maintenance_busy");if(job.Request is null)return job.Receipt;if(IsLiveEphemeral(job.Request))_ephemeralRequests.TryAdd(job.Receipt.Jid,job.Request!);var waiter=Waiter(job.Receipt.Jid);_jobCancellation.GetOrAdd(job.Receipt.Jid,_=>new CancellationTokenSource());ScheduleDrain();var current=_store.Find(job.Receipt.Jid);if(current is not null&&IsTerminal(current.Receipt.State))return SignalTerminal(job.Receipt.Jid);return await waiter.Task.WaitAsync(ct);
    }
    private OperationReceipt RetrySystemJob(OperationRequest request,ProtectedExecutionGrant authorization)
    {
        if(request.Arg.GetValueOrDefault("op")!="retry"||request.Arg.GetValueOrDefault("jid") is not{Length:>0} jid)return Fail(request,"auth");
        var original=_store.Find(jid);if(original?.RetryRequest is not null&&!ProtectedAccessRules.AllowsFolders(authorization,original.RetryRequest))return Fail(request,"root_path_denied");
        try{var retried=_store.Retry(jid,string.IsNullOrWhiteSpace(request.Rid)?Guid.NewGuid().ToString("N"):request.Rid!,_ownerSid,system:true);ScheduleDrain();return retried.Receipt;}
        catch(AgentOpsException ex){return Fail(request,ex.Code);}
    }
    private OperationReceipt ApplicationAccess(OperationRequest request)
    {
        if(_applicationCredentials is null)return Fail(request,"service_unavailable");
        try
        {
            var op=request.Arg.GetValueOrDefault("op","ls");
            if(op=="ls")return new(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done","application_access",Data:new Dictionary<string,object?>{{"applications",_applicationCredentials.OwnerStatus()}});
            if(op!="revoke"||request.Arg.GetValueOrDefault("application_id") is not{Length:>0} applicationId)return Fail(request,"invalid");
            var result=_applicationCredentials.OwnerRevoke(applicationId);
            return result.Ok?new(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done","application_access",Data:new Dictionary<string,object?>{{"application_id",result.ApplicationId},{"state",result.State},{"credential_id",result.CredentialId},{"generation",result.Generation}}):Fail(request,result.Err??"internal");
        }
        catch(UnauthorizedAccessException){return Fail(request,"access");}catch(Exception ex)when(ex is ArgumentException or IOException or CryptographicException){return Fail(request,"integrity");}
    }
    public async Task<TrayRegistrationResult> RegisterTrayAsync(TrayRegistration registration,LocalPeer peer)
    {
        if(!_serviceExitAdmission.TryEnter(out var serviceAdmission))return new(false,"service_exiting");
        using var serviceLease=serviceAdmission;
        if(!IsOwner(peer)||registration.Sid!=peer.Sid||registration.Session!=peer.Session||string.IsNullOrWhiteSpace(registration.PipeName)||registration.PipeName.Length>128)return new(false,"auth");
        MaintenanceHostIdentity? host=null;try{host=OwnerAdminExecution.HostIdentity(peer);}catch(AgentOpsException){}catch(ArgumentException){}catch(System.ComponentModel.Win32Exception){}
        var generation=Interlocked.Increment(ref _generation);_tray=new TrayRoute(peer,registration.PipeName,generation,host);ScheduleDrain();await Task.Yield();return new(true);
    }
    private void ScheduleDrain(){var id=Interlocked.Increment(ref _drainId);var drain=Task.Run(()=>DrainAsync(CancellationToken.None));_drains[id]=drain;_ = drain.ContinueWith(completed=>{_drains.TryRemove(id,out var removed);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}
    private async Task DrainAsync(CancellationToken ct)
    {
        await _drainGate.WaitAsync(ct);
        try
        {
            // A scheduled drain may outlive the request that scheduled it. Join
            // shutdown admission before recovery or leasing any queued work,
            // including reads that are absent from the unresolved-effect probe.
            if(!_serviceExitAdmission.TryEnter(out var serviceAdmission))return;
            using var serviceLease=serviceAdmission;
            if(MaintenanceClosed()||_store.IsPaused())return;
            ResumeRouterRecovery();
            foreach(var job in _store.LoadWaiting().Where(x=>StringComparer.Ordinal.Equals(x.Sid,_ownerSid)))
            {
                if(_store.IsPaused())return;
                await DispatchCandidateAsync(job,ct);
            }
        }
        finally{_drainGate.Release();}
    }
    private async Task<OperationReceipt> DispatchCandidateAsync(RoutedJob candidate,CancellationToken ct)
    {
        await _fifo.WaitAsync(ct);
        try
        {
            if(candidate.Request is null||AdminFences(candidate.Request))return candidate.Receipt;
            var route=_tray;var localContext=candidate.Request?.Op=="ctx"&&candidate.Request.Arg.ContainsKey("register");var systemContext=candidate.Request?.Arg.ContainsKey(SystemContextKey)==true;if(route is null&&!localContext&&!systemContext){if(_jobCancellation.TryRemove(candidate.Receipt.Jid,out var unused))unused.Dispose();return SignalCurrent(candidate.Receipt.Jid);}
            if(candidate.Request?.Arg.ContainsKey(OperationRequest.BodyOmittedKey)==true&&!_ephemeralRequests.ContainsKey(candidate.Receipt.Jid)){_store.RecoveryRequiredWaiting(candidate.Receipt.Jid);return SignalTerminal(candidate.Receipt.Jid);}
            var job=_store.TryLease(candidate.Receipt.Jid,route?.Generation??0);if(job is null)return _store.Find(candidate.Receipt.Jid)?.Receipt??candidate.Receipt;
            if(job.Request?.Arg.ContainsKey(OperationRequest.BodyOmittedKey)==true&&_ephemeralRequests.TryGetValue(job.Receipt.Jid,out var liveRequest))job=job with{Request=liveRequest};
            _activeRouted.TryAdd(job.Receipt.Jid,0);
            var jobCancellation=_jobCancellation.GetOrAdd(job.Receipt.Jid,_=>new CancellationTokenSource());
            try
            {
                OperationReceipt inner;
                // Private HTTP relay messages are never durable/public user jobs.
                // Also fence old queued entries created before this admission rule.
                if(job.Request!.Op.StartsWith('_'))
                    inner=Fail(job.Request!,"private_operation");
                else if(job.Request!.ConnectionAuthority is { } connection&&!ConnectionIsCurrent(connection,systemContext))
                    inner=Fail(job.Request!,"connection_revoked");
                else if(AdminFences(job.Request!)){_store.Release(job);return SignalCurrent(job.Receipt.Jid);}
                else if(localContext){var context=Context(job.Request!);inner=context with{Jid=job.Receipt.Jid,Rid=job.Receipt.Rid};}
                else if(systemContext)inner=_systemDispatch is null?Fail(job.Request!,"service_unavailable"):await _systemDispatch(job.Request!,jobCancellation.Token);
                else if(_trayDispatch is not null)inner=await _trayDispatch(route!.Peer,route.PipeName,job.Request!,jobCancellation.Token);
                else{await using var dispatcher=new NamedPipeDispatcher(route!.PipeName,expectedServer:route.Peer);inner=await dispatcher.DispatchAsync(job.Request!,jobCancellation.Token);}
                if(inner.Err is "maintenance_busy" or "maintenance_state_unavailable"){_store.Release(job);return SignalCurrent(job.Receipt.Jid);}
                if(inner.Err=="svc_down"){_store.Release(job);if(route is not null)Interlocked.CompareExchange(ref _tray,null,route);return SignalCurrent(job.Receipt.Jid);}
                if(inner.Err is "recovery_required" or "auth"){_store.RecoveryRequired(job);RemoveEphemeral(job.Receipt.Jid);return SignalTerminal(job.Receipt.Jid);}
                if(_crashAfterTrayExecution)Environment.Exit(91);
                inner=inner with{Op=job.Receipt.Op};
                if(job.Request?.Op=="ui")_liveReceipts[job.Receipt.Jid]=NormalizeLiveReceipt(job,inner);
                if(!_store.Complete(job,ScrubDurableReceipt(inner))){_store.RecoveryRequired(job);RemoveEphemeral(job.Receipt.Jid);return SignalTerminal(job.Receipt.Jid);}
                _ephemeralRequests.TryRemove(job.Receipt.Jid,out _);
                return SignalTerminal(job.Receipt.Jid);
            }
            catch(OperationCanceledException)when(jobCancellation.IsCancellationRequested){_store.RecoveryRequired(job);RemoveEphemeral(job.Receipt.Jid);return SignalTerminal(job.Receipt.Jid);}
            catch(OperationCanceledException){_store.RecoveryRequired(job);RemoveEphemeral(job.Receipt.Jid);SignalTerminal(job.Receipt.Jid);throw;}
            catch(Exception ex){_store.RecoveryRequired(job,ex.GetType().Name);RemoveEphemeral(job.Receipt.Jid);SignalTerminal(job.Receipt.Jid);throw;}
            finally{_activeRouted.TryRemove(job.Receipt.Jid,out _);if(_jobCancellation.TryRemove(job.Receipt.Jid,out var removed))removed.Dispose();}
        }
        finally{_fifo.Release();}
    }
    private async Task<OperationReceipt> JobAsync(OperationRequest request,CancellationToken ct)
    {
        var op=request.Arg.GetValueOrDefault("op","ls");
        if(op=="ls"){var max=100;if(request.Arg.TryGetValue("max",out var raw)&&(!int.TryParse(raw,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out max)||max is <1 or >100))return Fail(request,"invalid");return new(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done","job_ls",Data:new Dictionary<string,object?>{{"jobs",_store.List(max)},{"paused",_store.IsPaused()}});}
        var jid=request.Arg.GetValueOrDefault("jid");if(string.IsNullOrWhiteSpace(jid))return Fail(request,"invalid");var found=_store.Find(jid);if(found is null)return Fail(request,"not_found");
        if(op=="stat")return found.Receipt;
        if(op=="log")return LogReceipt(request,found);
        if(op=="cancel")
        {
            if(IsTerminal(found.Receipt.State))return found.Receipt;
            var canceled=_store.CancelQueued(jid);if(canceled is not null){RemoveEphemeral(jid);SignalTerminal(jid);ScheduleDrain();return canceled;}
            if(_jobCancellation.TryGetValue(jid,out var active)){active.Cancel();var cancelWaiter=Waiter(jid);return await cancelWaiter.Task.WaitAsync(ct);}
            found=_store.Find(jid);if(found is not null&&found.Receipt.State=="dispatching"){_store.RecoveryRequired(found);RemoveEphemeral(jid);return SignalTerminal(jid);}
            return _store.Find(jid)?.Receipt??Fail(request,"not_found");
        }
        if(op=="retry")
        {
            try{var retried=_store.Retry(jid,string.IsNullOrWhiteSpace(request.Rid)?Guid.NewGuid().ToString("N"):request.Rid!,_ownerSid);ScheduleDrain();return retried.Receipt;}
            catch(AgentOpsException ex){return Fail(request,ex.Code);}
        }
        if(IsTerminal(found.Receipt.State))return found.Receipt;
        if(op!="wait")return Fail(request,"invalid");var waiter=_waiters.GetOrAdd(jid,_=>new TaskCompletionSource<OperationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously));found=_store.Find(jid);if(found is not null&&IsTerminal(found.Receipt.State)){_waiters.TryRemove(jid,out _);return found.Receipt;}return await waiter.Task.WaitAsync(ct);
    }
    internal OperationReceipt SetTrayPaused(bool paused,LocalPeer peer)
    {
        lock(_maintenanceGate)
        {
        if(MaintenanceClosed()||_ownerAdmin?.BlockingJob() is not null)return new(false,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"failed","queue","maintenance_busy");
        var route=_tray;if(route is null||!route.Peer.SameIdentity(peer))return new(false,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"failed","job","auth");_store.SetPaused(paused);if(!paused)ScheduleDrain();return new(true,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"done","job",Data:new Dictionary<string,object?>{{"paused",paused}});
        }
    }
    private static OperationReceipt LogReceipt(OperationRequest request,RoutedJob found)
    {
        var max=16*1024;if(request.Arg.TryGetValue("max",out var raw)&&(!int.TryParse(raw,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out max)||max is <256 or >65536))return Fail(request,"invalid");
        var data=JobLogProjection.Create(found.Receipt,max);
        return new(true,found.Receipt.Jid,request.Rid??Guid.NewGuid().ToString("N"),"done","job",Data:data);
    }
    private OperationReceipt Context(OperationRequest request)
    {
        try
        {
            IReadOnlyDictionary<string,object?> data;if(request.Arg.TryGetValue("register",out var registration)){var split=registration.IndexOf(':');if(split<1)throw new AgentOpsException("invalid");var ws=_workspaces.Register(registration[..split],registration[(split+1)..]);data=new Dictionary<string,object?>{{"ws",ws.Alias},{"path",ws.Path}};}else{var cwd=request.Cwd??Environment.CurrentDirectory;var ws=_workspaces.Resolve(request.Ws,cwd);data=new Dictionary<string,object?>{{"ws",ws?.Alias},{"path",ws?.Path},{"cwd",Path.GetFullPath(cwd)},{"all",_workspaces.List()}};}return new(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done",OperationNames.Public(request),Data:data);
        }
        catch(AgentOpsException ex){return Fail(request,ex.Code);}catch(UnauthorizedAccessException){return Fail(request,"access");}catch(Exception ex)when(ex is ArgumentException or IOException){return Fail(request,"invalid");}
    }
    private OperationRequest FreezeUserRequest(OperationRequest request){var arg=request.Arg.Where(x=>x.Key is not("root" or "stdin" or "cwd" or "ws")&&!x.Key.StartsWith("__",StringComparison.Ordinal)&&!x.Key.StartsWith("env",StringComparison.Ordinal)).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);if(request.Body is not null){if(System.Text.Encoding.UTF8.GetByteCount(request.Body)>OperationRequest.MaxBodyBytes)throw new AgentOpsException("invalid");arg[OperationRequest.BodyOmittedKey]="1";}if(request.Op=="ui"&&arg.Keys.Any(key=>key is "title" or "window_text" or "control_class"))arg[OperationRequest.BodyOmittedKey]="1";var clean=request with{Root=null,PathRoots=null,Arg=arg,Rid=string.IsNullOrWhiteSpace(request.Rid)?Guid.NewGuid().ToString("N"):request.Rid};if(clean.Op=="patch")return FreezePatch(clean,arg);var keys=clean.Op switch{"stat" or "ls" or "find" or "search" or "slice" or "count" or "path" or "lines" or "disk" or "rd" or "read_lines" or "sha" or "pwsh"=>new[]{"src"},"dotnet" when clean.Arg.GetValueOrDefault("op")=="publish"&&clean.Arg.ContainsKey("dst")=>new[]{"src","dst"},"dotnet"=>new[]{"src"},"nsis" when clean.Arg.GetValueOrDefault("op")=="build"=>new[]{"src","dst"},"nsis" when clean.Arg.GetValueOrDefault("op")=="validate"=>new[]{"src"},"encoding" when clean.Arg.GetValueOrDefault("op")=="inspect"=>new[]{"src"},"encoding"=>new[]{"src","dst"},"diff"=>new[]{"src","dst"},"mkdir" or "wr" or "edit" or "rm"=>new[]{"dst"},"ui" when clean.Arg.GetValueOrDefault("op")=="capture"=>new[]{"dst"},"cp" or "mv"=>new[]{"src","dst"},"zip" or "7z" when clean.Arg.GetValueOrDefault("op") is "list" or "test"=>new[]{"src"},"zip" or "7z"=>new[]{"src","dst"},_=>Array.Empty<string>()};var roots=new Dictionary<string,string>(StringComparer.Ordinal);foreach(var key in keys){var admitted=AdmittedRoot(clean,key);arg[key]=Resolve(clean,key);if(admitted is not null)roots[key]=admitted;}if(clean.Op=="do")foreach(var workspace in _workspaces.List())roots["ws:"+workspace.Alias]=workspace.Path;string? cwd=null;if(clean.Cwd is not null){var cwdArg=new Dictionary<string,string>(arg,StringComparer.Ordinal);cwdArg["cwd"]=clean.Cwd;var cwdRequest=clean with{Arg=cwdArg};var admitted=AdmittedRoot(cwdRequest,"cwd");cwd=Resolve(cwdRequest,"cwd");if(admitted is not null)roots["cwd"]=admitted;}return clean with{Arg=arg,Ws=null,Cwd=cwd,PathRoots=roots};}
    private string? AdmittedRoot(OperationRequest request,string key){var value=request.Arg.GetValueOrDefault(key);if(string.IsNullOrWhiteSpace(value))return null;var colon=value.IndexOf(':');if(colon>0&&!(colon==1&&value.Length>2&&value[2] is '\\' or '/'))return _workspaces.Resolve(value[..colon],request.Cwd??Environment.CurrentDirectory)?.Path;if(request.Ws is not null)return _workspaces.Resolve(request.Ws,request.Cwd??Environment.CurrentDirectory)?.Path;if(Path.IsPathFullyQualified(value))return null;return _workspaces.Resolve(null,request.Cwd??Environment.CurrentDirectory)?.Path;}
    private OperationRequest FreezePatch(OperationRequest request,Dictionary<string,string> arg){var selected=_workspaces.Resolve(request.Ws,request.Cwd??Environment.CurrentDirectory)??throw new AgentOpsException("precondition");var body=request.Body??throw new AgentOpsException("invalid");var patch=UnifiedPatch.ParseSingleFile(body);var target=Path.GetFullPath(patch.RelativePath.Replace('/',Path.DirectorySeparatorChar),selected.Path);if(!PathNormalizer.Contains(selected.Path,target))throw new AgentOpsException("precondition");PathNormalizer.RejectReparseComponents(selected.Path,target);arg["dst"]=target;arg["workspace_root"]=selected.Path;return request with{Arg=arg,Ws=null,Cwd=null,PathRoots=new Dictionary<string,string>{{"dst",selected.Path}}};}
    private string Resolve(OperationRequest request,string key){if(!request.Arg.TryGetValue(key,out var value)||string.IsNullOrWhiteSpace(value))throw new AgentOpsException("invalid");var normalizer=new WindowsPathContextNormalizer();Workspace? selected=null;var colon=value.IndexOf(':');if(colon>0&&!(colon==1&&value.Length>2&&value[2] is '\\' or '/')){selected=_workspaces.Resolve(value[..colon],request.Cwd??Environment.CurrentDirectory);if(selected is null)throw new AgentOpsException("precondition");value=value[(colon+1)..].TrimStart(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);}else if(request.Ws is not null)selected=_workspaces.Resolve(request.Ws,request.Cwd??Environment.CurrentDirectory);else if(!Path.IsPathFullyQualified(value))selected=_workspaces.Resolve(null,request.Cwd??Environment.CurrentDirectory);if(request.Ws is not null&&selected is null)throw new AgentOpsException("precondition");WindowsPathContextResult context;if(selected is not null&&!Path.IsPathFullyQualified(value))context=value=="."?normalizer.Normalize(selected.Path):normalizer.ResolveAliasPath(normalizer.NormalizeAliasRoot(selected.Alias,selected.Path),value);else context=normalizer.Normalize(Path.GetFullPath(value,selected?.Path??request.Cwd??Environment.CurrentDirectory));var full=context.CanonicalPath;if(selected is not null&&!PathNormalizer.Contains(selected.Path,full))throw new AgentOpsException("precondition");WindowsPathContextNormalizer.RevalidateLocalNoFollow(context,selected?.Path);return full;}
    private bool AdminFences(OperationRequest request)=>MaintenanceRequestPolicy.IsMutation(request)&&(MaintenanceClosed()||_ownerAdmin?.BlockingJob() is not null);
    private static bool IsTerminal(string state)=>state is "done" or "failed" or "canceled" or "recovery_required" or "attention";
    private bool IsOwner(LocalPeer peer)=>peer.Session>0&&StringComparer.Ordinal.Equals(peer.Sid,_ownerSid);
    private bool ConnectionIsCurrent(CortexMcpConnectionAuthority authority,bool fullControl)
    {
        try{return _connectionAuthorization?.Invoke(authority,fullControl)==true;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException){return false;}
    }
    private bool IsAdmittedOwnerHost(LocalPeer peer)
    {
        if(_ownerHostVerifier is not null)return _ownerHostVerifier(peer);
        var route=_tray;
        if(!IsOwner(peer)||peer.Elevated||route?.Identity is not { } identity||_serviceExitHost is null||
            peer.ProcessId!=identity.Pid||peer.Session!=identity.Session||
            !StringComparer.OrdinalIgnoreCase.Equals(identity.ApplicationPath,_serviceExitHost))return false;
        try{return OwnerAdminExecution.HostIdentity(peer)==identity;}
        catch(Exception error)when(error is AgentOpsException or ArgumentException or System.ComponentModel.Win32Exception){return false;}
    }
    private TaskCompletionSource<OperationReceipt> Waiter(string jid)=>_waiters.GetOrAdd(jid,_=>new TaskCompletionSource<OperationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously));
    private OperationReceipt SignalTerminal(string jid){var receipt=_liveReceipts.TryRemove(jid,out var live)?live:_store.Find(jid)!.Receipt;if(_waiters.TryRemove(jid,out var waiter))waiter.TrySetResult(receipt);return receipt;}
    private OperationReceipt SignalCurrent(string jid){var receipt=_store.Find(jid)!.Receipt;if(_waiters.TryRemove(jid,out var waiter))waiter.TrySetResult(receipt);return receipt;}
    private static OperationReceipt Fail(OperationRequest request,string error)=>new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),error);
    private static bool IsLiveEphemeral(OperationRequest? request)=>request is not null&&request.Arg.ContainsKey(OperationRequest.BodyOmittedKey)&&(request.Body is not null||request.Arg.Keys.Any(key=>key is "title" or "window_text" or "control_class"));
    private void RemoveEphemeral(string jid){_ephemeralRequests.TryRemove(jid,out _);_liveReceipts.TryRemove(jid,out _);}
    private static OperationReceipt NormalizeLiveReceipt(RoutedJob job,OperationReceipt receipt){var data=new Dictionary<string,object?>(receipt.Data??new Dictionary<string,object?>());data["seq"]=job.Sequence;return receipt with{Jid=job.Receipt.Jid,Rid=job.Receipt.Rid,Data=data};}
    private static OperationReceipt ScrubDurableReceipt(OperationReceipt receipt)
    {
        if(!receipt.Op.StartsWith("ui_",StringComparison.Ordinal)||receipt.Data is null)return receipt;
        var data=receipt.Data.Where(item=>item.Key is not("title" or "text" or "class_list")).ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal);
        return receipt with{Data=data,Detail=null};
    }
    public async ValueTask DisposeAsync(){foreach(var cancellation in _jobCancellation.Values)cancellation.Cancel();try{await Task.WhenAll(_drains.Values.ToArray());}catch{}foreach(var cancellation in _jobCancellation.Values)cancellation.Dispose();_jobCancellation.Clear();_ephemeralRequests.Clear();_liveReceipts.Clear();_store.Dispose();_fifo.Dispose();_drainGate.Dispose();}
}
