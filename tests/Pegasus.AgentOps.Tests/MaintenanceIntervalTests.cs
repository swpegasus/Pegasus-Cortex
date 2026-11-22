using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Microsoft.Data.Sqlite;

internal static class MaintenanceIntervalTests
{
    private const string TaskId="01a12d3b-7157-7352-ac1f-a5f549ceb103";
    private const string Code="T7Q9"; // Private fixture verifier; never a live grant.

    internal static async Task InitializationFreshAsync()
    {
        using var temp=new TempRoot();await using var f=new Fixture(temp.Path);await f.Ready();
        Equal("fresh",f.BeforeInitializationCompletion.Feature);Equal("held",f.BeforeInitializationCompletion.Outcome);
        Equal("recovery",f.BeforeInitializationCompletion.Stage);Equal("not-completed",f.BeforeInitializationCompletion.Cause);
        Equal(1,f.InitializationProgress.Count(x=>x.Initialized.HasValue));
        True(f.InitializationProgress.Single(x=>x.Initialized.HasValue).Initialized==false,"existing feature query observed fresh");
        AssertInitialization(await f.Broker.HandleUserAsync(Diagnostic(),f.Peer,default),"fresh","ready","complete","none");
        Equal("off",f.Manager.MaintenanceMode.Read()!.Phase);
    }

    internal static async Task InitializationExistingFenceAsync()
    {
        using var temp=new TempRoot();await using var f=new Fixture(temp.Path);await f.Ready();
        var begin=await f.Begin();True(begin.Ok,"synthetic maintenance active");
        AssertInitialization(await f.Broker.HandleUserAsync(Diagnostic(),f.Peer,default),"fresh","ready","complete","none");
        Equal("active",f.Manager.MaintenanceMode.Read()!.Phase);
        await f.Restart();
        True(f.InitializationProgress.Single(x=>x.Initialized.HasValue).Initialized==true,"same feature query observed existing");
        AssertInitialization(await f.Broker.HandleUserAsync(Diagnostic(),f.Peer,default),"existing","ready","complete","none");
        var mode=f.Manager.MaintenanceMode.Read()!;Equal("uncertain",mode.Phase);Equal(begin.Jid,mode.Jid);
        var mutation=new OperationRequest("mkdir",new Dictionary<string,string>(){["dst"]=Path.Combine(f.Root,"must-stay-blocked")});
        Equal("maintenance_busy",(await f.User.ExecuteAsync(mutation)).Err);
        True(!(await f.Broker.HandleUserAsync(mutation,f.Peer,default)).Ok,"ready initialization leaves broker fence closed");
        True(!Directory.Exists(Path.Combine(f.Root,"must-stay-blocked")),"no ordinary effect after readiness diagnostic");
    }

    internal static async Task InitializationBeforeQueryFailureAsync()
    {
        using var temp=new TempRoot();var peer=LocalIdentity.Current();var service=Path.Combine(temp.Path,"service");
        // A directory in place of a synthetic SQLite file fails normal Open,
        // before the feature query, without changing any OS permissions.
        Directory.CreateDirectory(Path.Combine(service,"owner-admin","execution.db"));
        var observation=MaintenanceInitializationObservation.Started;OwnerAdminExecution? owner=null;
        try
        {
            owner=new OwnerAdminExecution(service,peer.Sid,new Launcher(),protectState:false,
                initializationProgress:(stage,initialized)=>observation=observation.Observe(stage,initialized));
            observation=observation.Completed();
        }
        catch(SqliteException ex){observation=observation.Failed(ex);}
        finally{owner?.Dispose();}
        True(owner is null,"failed constructor leaves owner capability absent");
        True(!Directory.Exists(Path.Combine(service,"maintenance")),"failure before query never provisions public state");
        await using var broker=ObservationBroker(temp.Path,peer,service,observation);
        AssertInitialization(await broker.HandleUserAsync(Diagnostic(),peer,default),"unknown","held","owner-store","storage-unavailable");
    }

    internal static async Task InitializationAfterQueryFailureAsync()
    {
        using var temp=new TempRoot();await using var f=new Fixture(temp.Path);await f.Ready();
        f.Manager.Dispose();var service=Path.Combine(f.Root,"service");
        var publicDirectory=Path.Combine(service,"maintenance");Directory.Move(publicDirectory,publicDirectory+"-retained");
        var observation=MaintenanceInitializationObservation.Started;OwnerAdminExecution? owner=null;
        try
        {
            owner=new OwnerAdminExecution(service,f.Peer.Sid,f.Launcher,protectState:false,
                initializationProgress:(stage,initialized)=>observation=observation.Observe(stage,initialized));
            observation=observation.Completed();
        }
        catch(AgentOpsException ex)when(ex.Code=="maintenance_state_unavailable"){observation=observation.Failed(ex);}
        finally{owner?.Dispose();}
        True(owner is null,"existing feature with missing public state remains unavailable");
        await using var broker=ObservationBroker(Path.Combine(f.Root,"failed-observer"),f.Peer,service,observation);
        AssertInitialization(await broker.HandleUserAsync(Diagnostic(),f.Peer,default),"existing","held","provisioning","state-unavailable");
        True(!Directory.Exists(publicDirectory),"observation and diagnostic create no replacement OFF state");
    }

    internal static async Task InitializationPrivacyAndAuthenticationAsync()
    {
        using var temp=new TempRoot();var peer=LocalIdentity.Current();
        const string privateText="PRIVATE_SENTINEL C:\\private-fixture\\store.db owner-secret";
        var fresh=MaintenanceInitializationObservation.Started.Observe(MaintenanceInitializationStage.Provisioning,false)
            .Failed(new UnauthorizedAccessException(privateText));
        Equal("fresh",fresh.Feature);Equal("held",fresh.Outcome);Equal("provisioning",fresh.Stage);Equal("access-denied",fresh.Cause);
        var unknown=MaintenanceInitializationObservation.Started.Observe(MaintenanceInitializationStage.FeatureCheck)
            .Failed(new AgentOpsException(privateText));
        await using var broker=ObservationBroker(temp.Path,peer,Path.Combine(temp.Path,"service"),unknown);
        var result=await broker.HandleUserAsync(Diagnostic(),peer,default);
        AssertInitialization(result,"unknown","held","feature-check","unavailable");
        var data=result.Data!;
        var originalKeys=new[]{"service","tray","queued","paused","product","product_version","protocol","mcp_schema","manifest"};
        Equal(originalKeys.Length+4,data.Count);True(originalKeys.All(data.ContainsKey),"original diagnostic fields retained");
        Equal("ok",data["service"]!.ToString());Equal("down",data["tray"]!.ToString());
        Equal(0,Convert.ToInt32(data["queued"]));Equal(false,Convert.ToBoolean(data["paused"]));
        Equal(AgentOpsProductInfo.Product,data["product"]!.ToString());Equal(AgentOpsProductInfo.ProductVersion,data["product_version"]!.ToString());
        Equal($"{ProtocolVersion.Major}.{ProtocolVersion.Minor}",data["protocol"]!.ToString());
        Equal(AgentOpsProductInfo.McpSchemaVersion,Convert.ToInt32(data["mcp_schema"]));Equal(AgentOpsProductInfo.InstallManifestVersion,Convert.ToInt32(data["manifest"]));
        var serialized=JsonSerializer.Serialize(result);
        True(!serialized.Contains("PRIVATE_SENTINEL",StringComparison.Ordinal)&&!serialized.Contains("owner-secret",StringComparison.Ordinal),"exception content never leaves fixed diagnostic labels");
        Equal("auth",(await broker.HandleUserAsync(Diagnostic(),peer with{Sid="S-1-5-21-0-0-0-9999"},default)).Err);
        True(!Directory.Exists(Path.Combine(temp.Path,"service","maintenance")),"diag without owner does not provision");
    }

    private static OperationRequest Diagnostic()=>new("diag",new Dictionary<string,string>());
    private static AuthenticatedServiceBroker ObservationBroker(string root,LocalPeer peer,string service,MaintenanceInitializationObservation observation)=>
        new(Path.Combine(root,"observation-router"),peer.Sid,new RootCodeVerifier(Path.Combine(root,"observation.verifier")),false,null,
            ownerAdmin:null,maintenanceMode:new MaintenanceModeStore(service,protect:false,requireProvisioned:true),maintenanceInitialization:observation);
    private static void AssertInitialization(OperationReceipt result,string feature,string outcome,string stage,string cause)
    {
        True(result.Ok,"owner-authenticated initialization diagnostic available");Equal("diag",result.Op);Equal("done",result.State);
        var data=result.Data!;Equal(4,data.Keys.Count(key=>key.StartsWith("maintenance_init_",StringComparison.Ordinal)));
        Equal(feature,data["maintenance_init_feature"]!.ToString());Equal(outcome,data["maintenance_init_outcome"]!.ToString());
        Equal(stage,data["maintenance_init_stage"]!.ToString());Equal(cause,data["maintenance_init_cause"]!.ToString());
    }

    internal static async Task MissingPublicStateAsync()
    {
        await using var f=new Fixture();await f.Ready();var begin=await f.Begin();True(begin.Ok,"loss fixture active");
        f.Manager.Dispose();
        var publicDirectory=Path.Combine(f.Root,"service","maintenance");var retained=publicDirectory+"-retained";
        Directory.Move(publicDirectory,retained);
        var refused=false;
        try{using var unavailable=new OwnerAdminExecution(Path.Combine(f.Root,"service"),f.Peer.Sid,f.Launcher,protectState:false);}
        catch(AgentOpsException ex)when(ex.Code=="maintenance_state_unavailable"){refused=true;}
        True(refused,"initialized feature cannot recreate OFF after public state loss");True(!Directory.Exists(publicDirectory),"failed initialization creates no replacement OFF state");
        Equal("maintenance_busy",(await f.User.ExecuteAsync(new("mkdir",new Dictionary<string,string>(){["dst"]=Path.Combine(f.Root,"must-stay-blocked")}))).Err);
        True((await f.User.ExecuteAsync(new("diag",new Dictionary<string,string>()))).Ok,"diagnostics survive lost public state");
        Directory.Move(retained,publicDirectory);
        using var restored=new OwnerAdminExecution(Path.Combine(f.Root,"service"),f.Peer.Sid,f.Launcher,protectState:false);
        Equal("uncertain",restored.MaintenanceMode.Read()!.Phase);Equal(begin.Jid,restored.MaintenanceMode.Read()!.Jid);
    }

    internal static async Task ManyAttemptsHandbackAsync()
    {
        await using var f=new Fixture();await f.Ready();True((await f.Begin()).Ok,"long interval");
        // These terminal records are deterministic fixtures, not 640 real or
        // administrator process executions. The next launch uses the fake native
        // adapter and exercises the real controller admission/receipt path.
        var originals=new SortedDictionary<string,string>(StringComparer.Ordinal);
        var databasePath=Path.Combine(f.Root,"service","owner-admin","execution.db");
        using(var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Pooling=false}.ToString()))
        {
            database.Open();using var transaction=database.BeginTransaction();
            for(var index=1;index<=640;index++)
            {
                var definition=f.Definition();var jid=index.ToString("x32",System.Globalization.CultureInfo.InvariantCulture);var sha=OwnerAdminExecution.Digest(definition);
                var original=new OwnerAdminJob(jid,definition.ExpectedRid,f.Scope.Id,sha,"done",Exit:0);
                var body=JsonSerializer.Serialize(original,OwnerAdminExecution.Json);originals.Add(jid,body);
                using var command=database.CreateCommand();command.Transaction=transaction;
                command.CommandText="INSERT INTO jobs(jid,rid,admission_id,definition,state,body) VALUES($jid,$rid,$scope,$definition,'done',$body); INSERT INTO maintenance_native_jobs(epoch,jid,rid,definition_sha) VALUES($epoch,$jid,$rid,$sha);";
                command.Parameters.AddWithValue("$jid",jid);command.Parameters.AddWithValue("$rid",definition.ExpectedRid);command.Parameters.AddWithValue("$scope",f.Scope.Id);command.Parameters.AddWithValue("$definition",JsonSerializer.Serialize(definition,OwnerAdminExecution.Json));command.Parameters.AddWithValue("$body",body);command.Parameters.AddWithValue("$epoch",f.Epoch);command.Parameters.AddWithValue("$sha",sha);command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        static string ExpectedDigest(IEnumerable<string> bodies)=>Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",bodies)+"\n")));
        var earlier=await f.Handback();Equal(ExpectedDigest(originals.Values),earlier.NativeReceiptsSha256);
        var definitionAfterHistory=f.Definition();var launched=await f.Run(definitionAfterHistory);
        True(launched.Ok,"eligible launch after more than 512 terminal originals");Equal("running",launched.State);Equal(1,f.Launcher.Created);
        Equal("admin_reconciliation_required",(await f.End()).Err);
        f.Launcher.Last!.Exit=0;await f.Wait(launched.Jid);
        Equal("maintenance_handback_mismatch",(await f.Control("end",new Dictionary<string,string>(){["cfg"]=f.Scope.Id,["sha"]=f.ScopeSha,["epoch"]=f.Epoch},earlier)).Err);
        Equal("active",f.Manager.MaintenanceMode.Read()!.Phase);
        using(var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Pooling=false}.ToString()))
        {
            database.Open();using var command=database.CreateCommand();command.CommandText="SELECT body FROM jobs WHERE jid=$jid";command.Parameters.AddWithValue("$jid",launched.Jid);
            originals.Add(launched.Jid,(string)command.ExecuteScalar()!);
        }
        var fresh=await f.Handback();Equal(ExpectedDigest(originals.Values),fresh.NativeReceiptsSha256);True(fresh.NativeReceiptsSha256!=earlier.NativeReceiptsSha256,"new original changes full-epoch proof");
        True((await f.End()).Ok,"fresh full-epoch handback after 641 terminal originals");
        Equal("off",f.Manager.MaintenanceMode.Read()!.Phase);
    }
    internal static async Task StartupEffectsAsync()
    {
        await using var f=new Fixture();await f.Ready();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup=f.User.TryRunStartupEffectAsync(async _=>{entered.SetResult();await finish.Task;return null;},default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal("maintenance_busy",(await f.Begin()).Err);Equal("off",f.Manager.MaintenanceMode.Read()!.Phase);
        finish.SetResult();True(await startup,"startup guarded effect completed");
        True((await f.Begin(fresh:true)).Ok,"activation after startup completes");
        var called=false;
        True(!await f.User.TryRunStartupEffectAsync(_=>{called=true;return Task.FromResult<RegisteredToolDefinition?>(null);},default),"startup deferred during maintenance");
        True(!called,"deferred startup has no effect");
        True((await f.User.ExecuteAsync(new("stat",new Dictionary<string,string>(){["src"]=f.Root}))).Ok,"read while bootstrap deferred");
        True((await f.End()).Ok,"startup handback");
    }

    internal static async Task LifecycleAsync()
    {
        await using var f=new Fixture();await f.Ready();
        f.BeforeProbe=async _=>{await f.Broker.RegisterTrayAsync(new(f.Peer.Sid,f.Peer.Session,"private-fixture"),f.Peer);};
        var begin=await f.Begin();True(begin.Ok,"activation tolerates exact-host registration renewal");var epoch=f.Epoch;f.BeforeProbe=null;
        var otherScope=f.Scope with{Id=Guid.NewGuid().ToString("N")};
        var otherAdmission=await f.Control("admit",new(),otherScope);True(otherAdmission.Ok,"second reviewed scope");
        var crossScope=f.Definition() with{Id=otherScope.Id};
        Equal("admin_scope_mismatch",(await f.Control("run",new Dictionary<string,string>(){["cfg"]=otherScope.Id,["sha"]=OwnerAdminExecution.Digest(crossScope)},crossScope,crossScope.ExpectedRid)).Err);
        Equal("admin_scope_mismatch",(await f.Control("end",new Dictionary<string,string>(){["cfg"]=otherScope.Id,["sha"]=otherAdmission.Data!["sha"]!.ToString()!,["epoch"]=epoch},await f.Handback())).Err);
        f.Launcher.Elevated=false;
        var mutation=new OperationRequest("mkdir",new Dictionary<string,string>(){["dst"]=Path.Combine(f.Root,"blocked")},Rid:"ordinary-original-rid");
        Equal("maintenance_busy",(await f.User.ExecuteAsync(mutation)).Err);
        True(!(await f.Broker.HandleUserAsync(mutation,f.Peer,default)).Ok,"broker admission fenced");
        foreach(var control in new[]{new OperationRequest("queue",new Dictionary<string,string>(){["op"]="pause"}),new("job",new Dictionary<string,string>(){["op"]="cancel",["jid"]=Guid.NewGuid().ToString("N")}),new("job",new Dictionary<string,string>(){["op"]="retry",["jid"]=Guid.NewGuid().ToString("N")})})
            True(!(await f.Broker.HandleUserAsync(control,f.Peer,default)).Ok,"control fenced");
        True((await f.User.ExecuteAsync(new("stat",new Dictionary<string,string>(){["src"]=f.Root}))).Ok,"true read remains available");
        var definition=f.Definition();var running=await f.Run(definition);True(running.Ok,"reusable scope allows run without another elevated admission");
        Equal("admin_reconciliation_required",(await f.End()).Err);
        f.Launcher.Last!.Exit=0;await f.Wait(running.Jid);
        Equal("invalid",(await f.Control("end",new Dictionary<string,string>(){["cfg"]=f.Scope.Id,["sha"]=f.ScopeSha,["epoch"]=epoch})).Err);
        var wrongProof=(await f.Handback()) with{NativeReceiptsSha256=new string('0',64)};
        Equal("maintenance_handback_mismatch",(await f.Control("end",new Dictionary<string,string>(){["cfg"]=f.Scope.Id,["sha"]=f.ScopeSha,["epoch"]=epoch},wrongProof)).Err);
        Equal("active",f.Manager.MaintenanceMode.Read()!.Phase);
        var end=await f.End();True(end.Ok,"quiescent end");Equal("off",f.Manager.MaintenanceMode.Read()!.Phase);
        var same=await f.End();Equal(end.Jid,same.Jid);Equal(end.Rid,same.Rid);
        True((await f.User.ExecuteAsync(mutation)).Ok,"ordinary route restored");
        File.WriteAllText(f.Executable,"fixture revision two");
        True((await f.Begin(fresh:true)).Ok,"new interval after ordinary staging");True(epoch!=f.Epoch,"new epoch");
        var second=await f.Run(f.Definition());True(second.Ok,"revised pinned invocation");Equal(2,f.Launcher.Created);
        f.Launcher.Last!.Exit=0;await f.Wait(second.Jid);True((await f.End()).Ok,"second handback");
    }

    internal static async Task BrokerRaceAndAcknowledgementAsync()
    {
        await using var f=new Fixture();await f.Ready();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeDispatch=async()=>{entered.TrySetResult();await release.Task;};
        var request=new OperationRequest("mkdir",new Dictionary<string,string>(){["dst"]=Path.Combine(f.Root,"ordinary")},Rid:"caller-chosen-ordinary-rid");
        var ordinary=f.Broker.HandleUserAsync(request,f.Peer,default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy=await f.Begin();Equal("maintenance_busy",busy.Err);Equal("off",f.Manager.MaintenanceMode.Read()!.Phase);
        release.SetResult();True((await ordinary.WaitAsync(TimeSpan.FromSeconds(5))).Ok,"original ordinary effect finishes");f.BeforeDispatch=null;
        True((await f.Begin(fresh:true)).Ok,"opposite order active");
        True(!(await f.Broker.HandleUserAsync(request with{Rid="second-ordinary-rid"},f.Peer,default)).Ok,"ordinary blocked after begin");
        True((await f.End()).Ok,"end before unknown original");
        f.ReturnUnknown=true;
        var uncertain=await f.Broker.HandleUserAsync(request with{Rid="opaque-original-unknown-rid"},f.Peer,default);
        Equal("recovery_required",uncertain.State);f.ReturnUnknown=false;
        var denied=await f.Begin(fresh:true);Equal("maintenance_busy",denied.Err);
        var blockers=JsonSerializer.Deserialize<MaintenanceEffect[]>(JsonSerializer.Serialize(denied.Data!["blockers"]),OwnerAdminExecution.Json)!;
        var original=blockers.Single();Equal(uncertain.Jid,original.Jid);Equal(uncertain.Rid,original.Rid);
        var before=JsonSerializer.Serialize(uncertain);
        f.Launcher.Elevated=false;
        Equal("admin_review_required",(await f.Ack(original)).Err);
        f.Launcher.Elevated=true;
        True((await f.Ack(original)).Ok,"exact unknown acknowledgement");
        var current=await f.Broker.HandleUserAsync(new("job",new Dictionary<string,string>(){["op"]="stat",["jid"]=uncertain.Jid}),f.Peer,default);
        Equal(before,JsonSerializer.Serialize(current));
        True((await f.Begin(fresh:true)).Ok,"separate acknowledgement admits interval");
        True((await f.End()).Ok,"handback after acknowledged original");
    }

    internal static async Task RestartAndLossAsync()
    {
        await using var f=new Fixture();await f.Ready();
        var original=await f.Begin();True(original.Ok,"first interval");var epoch=f.Epoch;
        await f.Restart();
        var changedHost=OwnerAdminExecution.HostIdentity(f.Peer) with{Start="replacement-host-fixture"};
        f.Manager.SetMaintenanceProbes((request,_)=>Task.FromResult(new MaintenanceProbeResult(1,request.Epoch,changedHost,[],request.After,false,0,0)),
            request=>new(1,request.Epoch,changedHost,[],request.After,false,0,0),_=>{});
        var status=await f.Control("interval",new Dictionary<string,string>(){["cfg"]=f.Scope.Id});Equal(original.Jid,status.Jid);Equal(original.Rid,status.Rid);
        Equal("uncertain",status.Data!["phase"]?.ToString());Equal("uncertain",f.Manager.MaintenanceMode.Read()!.Phase);
        Equal("maintenance_reconciliation_required",(await f.End()).Err);
        var acknowledgement=new MaintenanceEffect("interval",status.Jid,status.Rid,"uncertain","admin_begin",status.Data!["receipt_sha"]!.ToString()!,0);
        True((await f.Ack(acknowledgement)).Ok,"explicit exact-interval reconciliation");
        True((await f.End()).Ok,"same-epoch confirmed handback to explicitly reviewed replacement host");Equal(epoch,f.Epoch);
        // Recreate the fixture broker to restore its actual in-process probes.
        await f.Restart();
        var probeEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeProbe=async ct=>{probeEntered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);};
        using var cancellation=new CancellationTokenSource();var pending=f.Begin(fresh:true,cancellation.Token);
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancellation.Cancel();
        var lost=await pending;Equal("uncertain",lost.Data!["phase"]!.ToString());
        f.BeforeProbe=null;
        var replay=await f.Begin();Equal(lost.Jid,replay.Jid);Equal(lost.Rid,replay.Rid);Equal("uncertain",f.Manager.MaintenanceMode.Read()!.Phase);
        Equal("maintenance_reconciliation_required",(await f.End()).Err);
        True((await f.User.ExecuteAsync(new("stat",new Dictionary<string,string>(){["src"]=f.Root}))).Ok,"read available after lost observation");
    }

    private sealed class Fixture:IAsyncDisposable
    {
        internal string Root {get;}=Path.Combine(Path.GetTempPath(),"pao-interval-"+Guid.NewGuid().ToString("N"));
        internal Fixture(string? root=null){if(root is not null)Root=root;}
        internal LocalPeer Peer {get;}=LocalIdentity.Current();
        internal Launcher Launcher {get;}=new();
        internal OwnerAdminExecution Manager=null!;internal AgentOpsEngine User=null!;internal AuthenticatedServiceBroker Broker=null!;
        internal OwnerAdminAuthorization Scope=null!;internal string ScopeSha="";internal string Epoch="";internal string Executable="";
        private RootCodeVerifier _verifier=null!;private string _beginRid=Guid.NewGuid().ToString("N"),_endRid=Guid.NewGuid().ToString("N");
        internal Func<Task>? BeforeDispatch;internal Func<CancellationToken,Task>? BeforeProbe;internal bool ReturnUnknown;
        internal MaintenanceInitializationObservation Initialization=MaintenanceInitializationObservation.Started;
        internal MaintenanceInitializationObservation BeforeInitializationCompletion=null!;
        internal readonly List<(MaintenanceInitializationStage Stage,bool? Initialized)> InitializationProgress=new();
        internal async Task Ready()
        {
            Directory.CreateDirectory(Root);Executable=Path.Combine(Root,"worker.exe");File.WriteAllText(Executable,"private executable fixture");
            _verifier=new RootCodeVerifier(Path.Combine(Root,"fixture.verifier"));_verifier.Enroll("fixture",Code);
            Open();
            Scope=new(1,Guid.NewGuid().ToString("N"),TaskId,Peer.Sid,Peer.Session,Peer.ApplicationPath!,Hash(Peer.ApplicationPath!),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),DateTimeOffset.UtcNow.AddHours(1),
                [new(Executable,Hash(Executable),true)],[],[Root],[Root,Path.GetDirectoryName(Peer.ApplicationPath!)!],[Root],["filesystem"],true,true,true);
            var admitted=await Control("admit",new(),Scope);True(admitted.Ok,"scope admit");ScopeSha=admitted.Data!["sha"]!.ToString()!;
            True((await Broker.RegisterTrayAsync(new(Peer.Sid,Peer.Session,"private-fixture"),Peer)).Ok,"host registration");
        }
        private void Open()
        {
            Initialization=MaintenanceInitializationObservation.Started;InitializationProgress.Clear();
            Manager=new OwnerAdminExecution(Path.Combine(Root,"service"),Peer.Sid,Launcher,protectState:false,
                initializationProgress:(stage,initialized)=>{Initialization=Initialization.Observe(stage,initialized);InitializationProgress.Add((stage,initialized));});
            BeforeInitializationCompletion=Initialization;Initialization=Initialization.Completed();
            User=new AgentOpsEngine(Path.Combine(Root,"user"),null,false,null,false,null,maintenanceMode:Manager.MaintenanceMode);
            Broker=new AuthenticatedServiceBroker(Path.Combine(Root,"router"),Peer.Sid,_verifier,false,Dispatch,null,null,Manager,maintenanceInitialization:Initialization);
        }
        private async Task<OperationReceipt> Dispatch(LocalPeer peer,string pipe,OperationRequest request,CancellationToken ct)
        {
            if(request.Op==MaintenanceProbeProtocol.Operation)
            {
                if(BeforeProbe is not null)await BeforeProbe(ct);
                // Round-trip the actual wire serializer's object projection too.
                var receipt=MaintenanceProbeProtocol.Handle(User,OwnerAdminExecution.HostIdentity(Peer),request);
                return JsonSerializer.Deserialize<OperationReceipt>(JsonSerializer.Serialize(receipt,OwnerAdminExecution.Json),OwnerAdminExecution.Json)!;
            }
            if(BeforeDispatch is not null)await BeforeDispatch();
            if(ReturnUnknown)return new(false,Guid.NewGuid().ToString("N"),request.Rid!,"recovery_required",OperationNames.Public(request),"recovery_required",Partial:true);
            return await User.ExecuteAuthenticatedFrozenDirectAsync(request,ct);
        }
        internal async Task Restart()
        {
            await Broker.DisposeAsync();await User.DisposeAsync();Manager.Dispose();Open();
            True((await Broker.RegisterTrayAsync(new(Peer.Sid,Peer.Session,"private-fixture"),Peer)).Ok,"new host registration");
        }
        internal async Task<OperationReceipt> Begin(bool fresh=false,CancellationToken cancellation=default)
        {
            if(fresh){_beginRid=Guid.NewGuid().ToString("N");_endRid=Guid.NewGuid().ToString("N");}
            var receipt=await Control("begin",new Dictionary<string,string>(){["cfg"]=Scope.Id,["sha"]=ScopeSha},rid:_beginRid,cancellation:cancellation);
            if(receipt.Data?.GetValueOrDefault("epoch") is{} epoch)Epoch=epoch.ToString()!;
            return receipt;
        }
        internal async Task<MaintenanceHandback> Handback()
        {
            var original=await Control("interval",new Dictionary<string,string>(){["cfg"]=Scope.Id});
            return new(1,Epoch,Scope.Id,original.Jid,original.Rid,original.Data!["native_receipts_sha"]!.ToString()!,new string('c',64),true);
        }
        internal async Task<OperationReceipt> End()=>await Control("end",new Dictionary<string,string>(){["cfg"]=Scope.Id,["sha"]=ScopeSha,["epoch"]=Epoch},await Handback(),rid:_endRid);
        internal Task<OperationReceipt> Ack(MaintenanceEffect original)=>Control("ack",new Dictionary<string,string>(){["cfg"]=Scope.Id,["sha"]=ScopeSha},new MaintenanceAcknowledgement(1,original.Store,original.Jid,original.Rid,original.ReceiptSha256,new string('a',64),Scope.Id,TaskId,Peer.Sid,Peer.Session,DateTimeOffset.UtcNow,true));
        internal OwnerAdminDefinition Definition()=>new(1,Scope.Id,TaskId,Peer.Sid,Peer.Session,Peer.ApplicationPath!,Scope.ClientSha256,Scope.OwnerProfile,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddMinutes(30),"exe",Executable,Hash(Executable),Root,[],[],Scope.ReadRoots,Scope.WriteRoots,["filesystem"],true,MaintenanceEpoch:Epoch);
        internal Task<OperationReceipt> Run(OwnerAdminDefinition definition)=>Control("run",new Dictionary<string,string>(){["cfg"]=Scope.Id,["sha"]=OwnerAdminExecution.Digest(definition)},definition,definition.ExpectedRid);
        internal Task<OperationReceipt> Control(string action,Dictionary<string,string> args,object? body=null,string? rid=null,CancellationToken cancellation=default)
        {args["op"]=action;return Broker.HandleSystemAsync(new("admin",args,rid??Guid.NewGuid().ToString("N"),Root:Code,Body:body is null?null:JsonSerializer.Serialize(body,OwnerAdminExecution.Json)),Peer,cancellation);}
        internal async Task Wait(string jid)
        {for(var attempt=0;attempt<100;attempt++){var result=await Control("stat",new Dictionary<string,string>(){["jid"]=jid});if(result.State is not("starting" or "running"))return;await Task.Delay(20);}throw new TimeoutException("fake process monitor");}
        public async ValueTask DisposeAsync(){if(Launcher.Last is{} process)process.Exit??=0;if(Broker is not null)await Broker.DisposeAsync();if(User is not null)await User.DisposeAsync();Manager?.Dispose();}
    }
    private sealed class Launcher:IOwnerAdminLauncher
    {
        internal bool Elevated=true;internal int Created;internal FakeProcess? Last;
        public bool IsElevatedOwner(LocalPeer peer)=>Elevated;
        public bool IsOriginalProcessAlive(int pid,string start)=>Last is{Exit:null} process&&process.Pid==pid&&process.Start==start;
        public void ValidateProtectedInputs(OwnerAdminDefinition definition){}
        public IOwnerAdminProcess CreateSuspended(OwnerAdminDefinition definition){Created++;return Last=new();}
    }
    private sealed class FakeProcess:IOwnerAdminProcess
    {internal int? Exit;public int Pid=>42001;public string Start{get;}=DateTime.UtcNow.ToString("O");public void Resume(){}public bool TryGetExit(out int exit){exit=Exit??0;return Exit.HasValue;}public void Dispose(){}}
    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
    private static void True(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"expected={expected} actual={actual}");}
}
