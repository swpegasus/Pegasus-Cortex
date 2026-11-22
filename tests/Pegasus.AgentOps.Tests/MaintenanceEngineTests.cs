using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

internal static class MaintenanceEngineTests
{
    private static readonly MaintenanceHostIdentity Host = new("fixture-owner",7,123,"fixture-start",@"C:\fixture\user-host.exe");

    internal static async Task AdmissionAndReplayAsync()
    {
        using var fixture=new Fixture();
        var mode=new ModeSource();
        await using var engine=fixture.Engine(mode);
        var request=fixture.Mkdir("original");
        var original=await engine.ExecuteAsync(request);
        Check(original.Ok,"initial ordinary user mutation");
        mode.Set("active");
        Equal(original.Jid,(await engine.ExecuteAsync(request)).Jid,"exact original RID replay");
        var denied=await engine.ExecuteAsync(fixture.Mkdir("denied"));
        Equal("maintenance_busy",denied.Err,"new mutation fenced");
        Check(!Directory.Exists(fixture.Target("denied")),"denied mutation has no effect");
        foreach(var phase in new[]{"preparing","active","ending","uncertain"})
        {
            mode.Set(phase);
            Equal("maintenance_busy",(await engine.ExecuteAsync(fixture.Mkdir("denied-"+phase))).Err,"closed phase "+phase);
            Check(!Directory.Exists(fixture.Target("denied-"+phase)),"closed phase produced no effect");
        }
        Check((await engine.ExecuteAsync(new("diag",new Dictionary<string,string>()))).Ok,"diagnostics remain available");
        Check((await engine.ExecuteAsync(new("stat",new Dictionary<string,string>{{"src",fixture.Root}}))).Ok,"fixed reads remain available");
        foreach(var requestToFence in new OperationRequest[]
        {
            new OperationRequest("job",new Dictionary<string,string>{{"op","cancel"},{"jid",original.Jid}}),
            new("job",new Dictionary<string,string>{{"op","retry"},{"jid",original.Jid}}),
            new("queue",new Dictionary<string,string>{{"op","pause"}}),
            new("queue",new Dictionary<string,string>{{"op","resume"}}),
            new("root",new Dictionary<string,string>{{"op","revoke"}}),
            new("application_access",new Dictionary<string,string>{{"op","revoke"}}),
            new("ui",new Dictionary<string,string>{{"op","activate"},{"hwnd","1"}}),
            new("app",new Dictionary<string,string>{{"op","stop"},{"aid","fixture"}})
        })
            Equal("maintenance_busy",(await engine.ExecuteAsync(requestToFence)).Err,"effect/control fence "+requestToFence.Op);
        var spoof=fixture.Mkdir("spoof") with{Arg=new Dictionary<string,string>{{"dst",fixture.Target("spoof")},{"readonly","true"},{"__maintenance_bypass","1"}}};
        Equal("maintenance_busy",(await engine.ExecuteAsync(spoof)).Err,"no generic readonly/bypass authority");
        Check(!AgentOpsEngine.SupportsDeferredCompletion(new("job",new Dictionary<string,string>{{"op","cancel"}})),"control is not queueable wait contract");
        Check(!AgentOpsEngine.SupportsDeferredCompletion(new("admin",new Dictionary<string,string>{{"op","begin"}})),"admin is not queueable wait contract");
        Equal(2L,fixture.RowCount(),"only admitted original and readonly stat claim durable rows");
        mode.ThrowOnRead=true;
        Equal("maintenance_busy",(await engine.ExecuteAsync(fixture.Mkdir("unavailable"))).Err,"missing/corrupt required mode fails closed");
        var unavailable=await engine.ExecuteAsync(new("diag",new Dictionary<string,string>()));
        Check(unavailable.Ok,"mode failure does not hide diagnostics");
        Equal("maintenance_state_unavailable",unavailable.Data?.GetValueOrDefault("maintenance_issue")?.ToString(),"diagnostics expose closed-mode issue");
        mode.ThrowOnRead=false;mode.Set("off");
        Check((await engine.ExecuteAsync(fixture.Mkdir("off-independent"))).Ok,"mode off ordinary user route remains independent");
    }

    internal static async Task HeldQueuedAndShutdownAsync()
    {
        using var fixture=new Fixture();
        var mode=new ModeSource();
        OperationReceipt queued;
        string before;
        await using(var engine=fixture.Engine(mode))
        {
            // Return OFF to true admission, then publish ACTIVE before the
            // scheduled callback can acquire the same admission gate.
            mode.CloseAfterNextRead();
            queued=await engine.ExecuteAsync(fixture.Mkdir("held") with{Arg=new Dictionary<string,string>{{"dst",fixture.Target("held")},{"wait","false"}}});
            Equal("queued",queued.State,"original was admitted before closure");
            before=fixture.RawReceipt(queued.Jid);
            var probe=engine.ProbeMaintenance(new(1,"fixture-epoch","snapshot"),Host);
            Equal("queued",probe.Effects.Single(effect=>effect.Jid==queued.Jid).State,"probe preserves queued state");
            Equal(0,probe.ActiveEffects,"held callback owns no active effect");
            Equal(before,fixture.RawReceipt(queued.Jid),"snapshot does not rewrite original");
            var cancel=await engine.ExecuteAsync(new("job",new Dictionary<string,string>{{"op","cancel"},{"jid",queued.Jid}}));
            Equal("maintenance_busy",cancel.Err,"cancel cannot rewrite held original");
        }
        Equal(before,fixture.RawReceipt(queued.Jid),"shutdown retires handles without rewriting held original");
        Check(!Directory.Exists(fixture.Target("held")),"held final dispatch never ran");
        mode.Set("off");
        await using(var restarted=fixture.Engine(mode))
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var completed=await restarted.ExecuteAsync(new("job",new Dictionary<string,string>{{"op","wait"},{"jid",queued.Jid}}),timeout.Token);
            Check(completed.Ok,"original recovers after mode off");
            Equal(queued.Jid,completed.Jid,"recovery preserves original JID");
            Equal(queued.Rid,completed.Rid,"recovery preserves original RID");
            Check(Directory.Exists(fixture.Target("held")),"off dispatch performed original effect");
        }
    }

    internal static async Task RecoveryProbeAndAcknowledgementAsync()
    {
        using var fixture=new Fixture();
        var mode=new ModeSource();mode.Set("preparing");
        var originals=new List<DurableJob>();
        using(var store=new SqliteJobStore(fixture.State))
        {
            for(var index=0;index<11;index++)
            {
                var request=fixture.Mkdir("seed-"+index);
                originals.Add(store.Claim(new(true,Guid.NewGuid().ToString("N"),request.Rid!,index==0?"running":"queued","mkdir"),request,true));
            }
            // A known fixed read is omitted; an old misclassified effect is
            // retained even when its historical mutation flag is false.
            store.Claim(new(true,"legacy-read","legacy-read","running","stat"),new("stat",new Dictionary<string,string>{{"src",fixture.Root}}),false);
            store.Claim(new(true,"legacy-effect","legacy-effect","running","job_cancel"),new("job",new Dictionary<string,string>{{"op","cancel"},{"jid","unknown"}}),false);
        }
        var before=originals.ToDictionary(job=>job.Receipt.Jid,job=>fixture.RawReceipt(job.Receipt.Jid));
        await using var engine=fixture.Engine(mode);
        var first=engine.ProbeMaintenance(new(1,"fixture-epoch","snapshot"),Host);
        Equal(8,first.Effects.Count,"bounded first effect page");Check(first.HasMore,"bounded probe cursor has remaining effects");
        var second=engine.ProbeMaintenance(new(1,"fixture-epoch","snapshot",first.Next),Host);
        Check(second.Effects.Count<=8,"bounded second page");
        var all=first.Effects.Concat(second.Effects).ToArray();
        Check(!all.Any(effect=>effect.Jid=="legacy-read"),"fixed read receipt is not an effect");
        Check(all.Any(effect=>effect.Jid=="legacy-effect"),"legacy false mutation label cannot hide effect");
        foreach(var original in originals)
        {
            var raw=before[original.Receipt.Jid];
            Equal(raw,fixture.RawReceipt(original.Receipt.Jid),"closed startup/refill did not rewrite original");
            var effect=all.Single(effect=>effect.Jid==original.Receipt.Jid);
            Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),effect.ReceiptSha256,"original stored bytes hash");
        }
        var unknown=originals[0];
        mode.Acknowledged.Add(all.Single(effect=>effect.Jid==unknown.Receipt.Jid).ReceiptSha256);
        mode.Set("off");
        var exact=engine.ProbeMaintenance(new(1,"prior-epoch","effect",Jid:unknown.Receipt.Jid),Host).Effects.Single();
        Equal(before[unknown.Receipt.Jid],fixture.RawReceipt(unknown.Receipt.Jid),"off exact-effect inspection preserves original");
        Equal("running",exact.State,"acknowledgement does not invent a completion");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var returned=await engine.ExecuteAsync(new("job",new Dictionary<string,string>{{"op","wait"},{"jid",unknown.Receipt.Jid}}),timeout.Token);
        Equal("running",returned.State,"acknowledged unknown wait returns original state");
        Equal(before[unknown.Receipt.Jid],fixture.RawReceipt(unknown.Receipt.Jid),"acknowledged original receipt is immutable");
        // A fresh mutation refills the previously fenced backlog. The acknowledged
        // unknown remains byte-identical while queued originals resume normally.
        Check((await engine.ExecuteAsync(fixture.Mkdir("refill-trigger"))).Ok,"mode off refill trigger accepted");
        Equal(before[unknown.Receipt.Jid],fixture.RawReceipt(unknown.Receipt.Jid),"recovery skips exact acknowledged unknown without update");
    }

    internal static async Task FrozenDispatchAndActiveProbeAsync()
    {
        using var fixture=new Fixture();
        var mode=new ModeSource();
        var service=new BlockingService();
        await using var engine=fixture.Engine(mode,service);
        var request=new OperationRequest("service",new Dictionary<string,string>{{"op","start"},{"service","PegasusCerebral"},{"expect","stopped"},{"__execution_context_system","1"}},"fixture-frozen-service");
        var pending=engine.ExecuteAuthenticatedFrozenDirectAsync(request);
        await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        mode.Set("preparing");
        var probe=engine.ProbeMaintenance(new(1,"fixture-epoch","snapshot"),Host);
        Equal(1,probe.ActiveEffects,"actual callback counted during quiescence snapshot");
        var live=probe.Effects.Single(effect=>effect.Rid==request.Rid);
        Check(live.Live,"undurable private callback reported live");
        Equal("running",live.State,"live state is actual current dispatch");
        Equal("maintenance_busy",(await engine.ExecuteAuthenticatedFrozenDirectAsync(request with{Rid="fixture-frozen-denied"})).Err,"next frozen callback fenced");
        Equal(1,service.Calls,"blocked callback did not enter fake adapter");
        service.Release.TrySetResult();
        Check((await pending).Ok,"already active fake adapter finishes normally");
        Equal(0,engine.ProbeMaintenance(new(1,"fixture-epoch","snapshot"),Host).ActiveEffects,"active permit is retired");
        Equal(0L,fixture.RowCount(),"private callback creates no second durable claim");
    }

    internal static async Task ApplicationReplayFenceAsync()
    {
        using var fixture=new Fixture();
        var mode=new ModeSource();
        await using var engine=fixture.Engine(mode);
        var request=new OperationRequest("app",new Dictionary<string,string>{{"op","stop"},{"aid","fixture-app"}},"fixture-app-original");
        var original=await engine.ExecuteAsync(request);
        Equal("user_dispatch_required",original.Err,"fixture has no application manager");
        Check(original.Data?.ContainsKey("requestIdentity")==true,"mutating app receipt pins request identity");
        var before=fixture.RawReceipt(original.Jid);
        mode.Set("active");
        var replay=await engine.ExecuteAsync(request);
        Equal(original.Jid,replay.Jid,"same app RID metadata replay remains available");
        Equal(original.Err,replay.Err,"replay preserves outcome");
        Equal(before,fixture.RawReceipt(original.Jid),"app metadata replay does not rewrite original");
        Equal("maintenance_busy",(await engine.ExecuteAsync(request with{Arg=new Dictionary<string,string>{{"op","stop"},{"aid","other-app"}}})).Err,"same RID different app identity cannot reenter manager");
        Equal(0,engine.ProbeMaintenance(new(1,"fixture-epoch","snapshot"),Host).AppTransitions,"closed app replay starts no transition");
    }

    internal static Task PublicReaderPathAsync()
    {
        using var fixture=new Fixture();
        var expected=Path.Combine(fixture.Root,"execution-maintenance");
        var inspected=new List<string>();
        var resolved=CortexProductLayout.ResolvePublicMaintenanceDirectory(fixture.Root,path=>
        {
            inspected.Add(path);
            if(path==Path.Combine(fixture.Root,"service"))throw new UnauthorizedAccessException("private leaf must not be inspected");
        });
        Equal(expected,resolved,"public barrier derives directly from data root");
        Equal(1,inspected.Count,"only public target and its ancestry need validation");
        Equal(expected,inspected.Single(),"private service leaf is not a dependency");
        var refused=false;
        try{CortexProductLayout.ResolvePublicMaintenanceDirectory(fixture.Root,path=>throw new IOException("fixture reparse refusal"));}
        catch(IOException){refused=true;}
        Check(refused,"public-path validator refusal is preserved; deterministic policy fixture");
        refused=false;
        try{CortexProductLayout.ResolvePublicMaintenanceDirectory("relative-root",_=>throw new InvalidOperationException("must not reach validator"));}
        catch(ArgumentException){refused=true;}
        Check(refused,"noncanonical data root is rejected before path use");
        return Task.CompletedTask;
    }

    internal static async Task PublicReaderMissingClosedAsync()
    {
        using var fixture=new Fixture();
        var publicDirectory=Path.Combine(fixture.Root,"execution-maintenance");
        var reader=MaintenanceModeStore.OpenReader(requireProvisioned:true,publicDirectoryResolver:()=>publicDirectory);
        await using var engine=fixture.Engine(reader);
        Equal("maintenance_busy",(await engine.ExecuteAsync(fixture.Mkdir("missing-public-denied"))).Err,"required missing public mode stays fenced");
        Check(!Directory.Exists(publicDirectory),"reader never provisions a public barrier");
        var diagnostic=await engine.ExecuteAsync(new("diag",new Dictionary<string,string>()));
        Equal("uncertain",diagnostic.Data!["maintenance_phase"],"missing mode remains uncertain");
        Equal("required-record",diagnostic.Data["maintenance_read_stage"],"missing record stage");
        Equal("missing",diagnostic.Data["maintenance_read_cause"],"missing record cause");
        Check(!Directory.Exists(fixture.Target("missing-public-denied")),"no denied mutation effect");
    }

    internal static async Task ReaderFailureLabelsAsync()
    {
        using var fixture=new Fixture();
        var mode=new ModeSource();
        await using var engine=fixture.Engine(mode);
        const string hidden="do-not-forward-private-path-or-record";
        foreach(var item in new (Exception Error,MaintenanceReadStage Stage,string StageLabel,string CauseLabel)[]
        {
            (new UnauthorizedAccessException(hidden),MaintenanceReadStage.LayoutResolution,"layout-resolution","access-denied"),
            (new FileNotFoundException(hidden),MaintenanceReadStage.RecordOpen,"record-open","missing"),
            (new JsonException(hidden),MaintenanceReadStage.RecordParse,"record-parse","invalid-record"),
            (new AgentOpsException("maintenance_state_unavailable"),MaintenanceReadStage.Protection,"protection","protection-rejected")
        })
        {
            mode.ReadFailure=MaintenanceReadDiagnostics.Annotate(item.Error,item.Stage);
            var diagnostic=await engine.ExecuteAsync(new("diag",new Dictionary<string,string>()));
            Check(diagnostic.Ok,"diagnostics remain available after reader failure");
            Equal("maintenance_state_unavailable",diagnostic.Data!["maintenance_issue"],"existing stable issue preserved");
            Equal(item.StageLabel,diagnostic.Data["maintenance_read_stage"],"bounded stage");
            Equal(item.CauseLabel,diagnostic.Data["maintenance_read_cause"],"bounded cause");
            Check(!diagnostic.Data.Values.Any(value=>value?.ToString()?.Contains(hidden,StringComparison.Ordinal)==true),"public diagnostic does not forward exception messages");
        }
        var unknown=new AgentOpsException("maintenance_state_unavailable");
        unknown.Data["Pegasus.MaintenanceReadStage"]=new string('x',10000);
        unknown.Data["Pegasus.MaintenanceReadCause"]=hidden;
        Equal(("state-read","unavailable"),MaintenanceReadDiagnostics.Describe(unknown),"unknown labels never escape the fixed taxonomy");
        var layoutReader=MaintenanceModeStore.OpenReader(true,()=>throw new UnauthorizedAccessException(hidden));
        try{layoutReader.Read();throw new InvalidOperationException("layout denial was ignored");}
        catch(AgentOpsException ex){Equal(("layout-resolution","access-denied"),MaintenanceReadDiagnostics.Describe(ex),"actual deferred resolution is annotated");}
    }

    internal static async Task ReaderRecordsStayFencedAsync()
    {
        using var fixture=new Fixture();
        var directory=Path.Combine(fixture.Root,"maintenance");
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"mode.v1.json");
        var mode=new MaintenanceModeStore(fixture.Root,protect:false,requireProvisioned:true);
        await using var engine=fixture.Engine(mode);
        File.WriteAllText(path,"{");
        Equal("maintenance_busy",(await engine.ExecuteAsync(fixture.Mkdir("corrupt-denied"))).Err,"corrupt mode stays fenced");
        var diagnostic=await engine.ExecuteAsync(new("diag",new Dictionary<string,string>()));
        Equal("record-parse",diagnostic.Data!["maintenance_read_stage"],"corrupt parse stage");
        Equal("invalid-record",diagnostic.Data["maintenance_read_cause"],"corrupt parse cause");
        var off=new MaintenanceModeRecord(1,new string('0',32),"off",new string('0',32),new string('0',32),"",Guid.Empty.ToString("D"),"fixture-owner",0,"",null,1);
        File.WriteAllText(path,JsonSerializer.Serialize(off,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Check(mode.Read() is{Phase:"off"},"synthetic fixture records can be read normally");
        File.Delete(path);
        Equal("maintenance_busy",(await engine.ExecuteAsync(fixture.Mkdir("seen-loss-denied"))).Err,"previously seen missing record stays fenced");
        File.WriteAllText(Path.Combine(directory,"required.v1"),"maintenance/1\n");
        var required=new MaintenanceModeStore(fixture.Root,protect:false);
        try{required.Read();throw new InvalidOperationException("required marker was ignored");}
        catch(AgentOpsException ex){Equal(("required-record","missing"),MaintenanceReadDiagnostics.Describe(ex),"required marker fences a fresh reader");}
        File.WriteAllText(path,JsonSerializer.Serialize(off,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var protectedReader=MaintenanceModeStore.OpenReader(true,()=>directory);
        try{protectedReader.Read();throw new InvalidOperationException("owner-created fixture was adopted as protected service state");}
        catch(AgentOpsException ex){Equal(("protection","protection-rejected"),MaintenanceReadDiagnostics.Describe(ex),"untrusted protection still refuses without ACL changes");}
        Check(!Directory.Exists(fixture.Target("corrupt-denied"))&&!Directory.Exists(fixture.Target("seen-loss-denied")),"failed reads produced no effect");
    }

    private sealed class ModeSource:IMaintenanceModeSource
    {
        private readonly object _gate=new();
        private MaintenanceModeRecord? _value;
        private bool _closeAfterRead;
        internal bool ThrowOnRead;
        internal Exception? ReadFailure;
        internal HashSet<string> Acknowledged {get;}=new(StringComparer.Ordinal);
        internal void Set(string phase){lock(_gate)_value=Value(phase);}
        internal void CloseAfterNextRead(){lock(_gate)_closeAfterRead=true;}
        public MaintenanceModeRecord? Read()
        {
            lock(_gate)
            {
                if(ReadFailure is not null)throw ReadFailure;
                if(ThrowOnRead)throw new AgentOpsException("maintenance_state_unavailable");
                var result=_value;
                if(_closeAfterRead){_closeAfterRead=false;_value=Value("active");}
                return result;
            }
        }
        public bool IsAcknowledged(MaintenanceEffect effect)=>Acknowledged.Contains(effect.ReceiptSha256);
        private static MaintenanceModeRecord Value(string phase)=>new(1,"fixture-epoch",phase,"interval-jid","interval-rid","scope","task","fixture-owner",7,"service",Host,1);
    }

    private sealed class BlockingService:IWindowsServiceLifecycle
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public async Task<WindowsServiceLifecycleResult> ExecuteAsync(string service,WindowsServiceLifecycleAction action,string? expectedState,int? expectedProcessId,string? expectedProcessStart,TimeSpan timeout,CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(true,action,new(service,"stopped"),new(service,"running",321,"fixture-start"),true);
        }
    }

    private sealed class Fixture:IDisposable
    {
        internal string Root {get;}=Path.Combine(Path.GetTempPath(),"pao-maintenance-"+Guid.NewGuid().ToString("N"));
        internal string State {get;}
        internal Fixture(){Directory.CreateDirectory(Root);State=Path.Combine(Root,"state");Directory.CreateDirectory(State);}
        internal string Target(string name)=>Path.Combine(Root,name);
        internal OperationRequest Mkdir(string name)=>new("mkdir",new Dictionary<string,string>{{"dst",Target(name)}},"fixture-"+name);
        internal AgentOpsEngine Engine(IMaintenanceModeSource mode,IWindowsServiceLifecycle? service=null)=>new(State,null,false,null,false,null,service,mode);
        internal string RawReceipt(string jid)
        {
            using var connection=Connection();using var command=connection.CreateCommand();
            command.CommandText="SELECT receipt FROM jobs WHERE jid=$jid";command.Parameters.AddWithValue("$jid",jid);
            return (string?)command.ExecuteScalar()??throw new InvalidOperationException("missing fixture receipt");
        }
        internal long RowCount(){using var connection=Connection();using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM jobs";return Convert.ToInt64(command.ExecuteScalar());}
        private SqliteConnection Connection(){var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(State,"agentops.db"),Pooling=false}.ToString());connection.Open();return connection;}
        public void Dispose(){try{Directory.Delete(Root,true);}catch(IOException){}}
    }

    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Equal<T>(T expected,T actual,string message)
    {
        if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }
}
