using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal sealed record MaintenanceInterval(string Epoch,string Jid,string Rid,string ScopeId,string ScopeSha,string RootLabel,
    string TaskId,string OwnerSid,int OwnerSession,string ClientPath,string Fingerprint,string Phase,string ServiceInstance,
    MaintenanceHostIdentity? Host=null,long UserCursor=0,long BrokerCursor=0,string? EndJid=null,string? EndRid=null,
    string? EndFingerprint=null,string? Issue=null,IReadOnlyList<MaintenanceEffect>? Blockers=null);

[SupportedOSPlatform("windows")]
internal sealed partial class OwnerAdminExecution
{
    private MaintenanceModeStore _maintenanceMode=null!;
    private readonly string _serviceInstance=Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _maintenanceRequests=new(1,1);
    private Func<MaintenanceProbeRequest,CancellationToken,Task<MaintenanceProbeResult>>? _userMaintenanceProbe;
    private Func<MaintenanceProbeRequest,MaintenanceProbeResult>? _brokerMaintenanceProbe;
    private Action<MaintenanceAcknowledgement>? _brokerMaintenanceAck;
    internal IMaintenanceModeSource MaintenanceMode=>_maintenanceMode;

    private void InitializeMaintenance(string stateRoot,bool protectState)
    {
        _initializationProgress?.Invoke(MaintenanceInitializationStage.PublicState,null);
        _maintenanceMode=new MaintenanceModeStore(stateRoot,_ownerSid,protectState,requireProvisioned:true);
        _initializationProgress?.Invoke(MaintenanceInitializationStage.FeatureCheck,null);
        using var knownFeature=_db.CreateCommand();knownFeature.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='maintenance_intervals'";
        var initialized=Convert.ToInt64(knownFeature.ExecuteScalar(),CultureInfo.InvariantCulture)!=0;
        _initializationProgress?.Invoke(MaintenanceInitializationStage.Provisioning,initialized);
        _maintenanceMode.EnsureProvisioned(allowFreshOff:!initialized);
        _initializationProgress?.Invoke(MaintenanceInitializationStage.Schema,null);
        using var schema=_db.CreateCommand();
        schema.CommandText="""
            CREATE TABLE IF NOT EXISTS maintenance_intervals(epoch TEXT PRIMARY KEY,jid TEXT NOT NULL UNIQUE,rid TEXT NOT NULL UNIQUE,phase TEXT NOT NULL,body TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_maintenance_open ON maintenance_intervals((1)) WHERE phase IN ('preparing','active','ending','uncertain','handback_ready');
            CREATE TABLE IF NOT EXISTS maintenance_native_jobs(epoch TEXT NOT NULL,jid TEXT NOT NULL UNIQUE,rid TEXT NOT NULL,definition_sha TEXT NOT NULL,PRIMARY KEY(epoch,jid));
            CREATE TABLE IF NOT EXISTS maintenance_acknowledgements(store TEXT NOT NULL,jid TEXT NOT NULL,rid TEXT NOT NULL,receipt_sha TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(store,jid,rid,receipt_sha));
            """;
        schema.ExecuteNonQuery();
        _initializationProgress?.Invoke(MaintenanceInitializationStage.Recovery,null);
        var current=FindOpenInterval();
        if(current is not null&&current.ServiceInstance!=_serviceInstance)
        {
            current=current with{Phase="uncertain",Issue="maintenance_service_restarted"};SaveInterval(current);
            PublishInterval(current,"uncertain");
        }
        else if(current is null&&_maintenanceMode.Read() is{Phase:not "off"})
        {
            // Missing private ownership never makes an existing public fence OFF.
            // Status exposes it; only an explicitly reviewed recovery may repair it.
        }
    }

    internal void SetMaintenanceProbes(
        Func<MaintenanceProbeRequest,CancellationToken,Task<MaintenanceProbeResult>> user,
        Func<MaintenanceProbeRequest,MaintenanceProbeResult> broker,
        Action<MaintenanceAcknowledgement> brokerAck)
    { _userMaintenanceProbe=user;_brokerMaintenanceProbe=broker;_brokerMaintenanceAck=brokerAck; }

    internal static MaintenanceHostIdentity HostIdentity(LocalPeer peer)
    {
        if(peer.ProcessId is not{ } pid||peer.ApplicationPath is null)throw new AgentOpsException("maintenance_host_unavailable");
        using var process=Process.GetProcessById(checked((int)pid));
        return new(peer.Sid,peer.Session,pid,process.StartTime.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),peer.ApplicationPath);
    }

    private async Task<OperationReceipt> HandleMaintenanceAsync(OperationRequest request,LocalPeer peer,ProtectedExecutionGrant authorization,CancellationToken ct)
    {
        await _maintenanceRequests.WaitAsync(ct);
        try{return await HandleMaintenanceCoreAsync(request,peer,authorization,ct);}
        finally{_maintenanceRequests.Release();}
    }
    private async Task<OperationReceipt> HandleMaintenanceCoreAsync(OperationRequest request,LocalPeer peer,
        ProtectedExecutionGrant authorization,CancellationToken ct)
    {
        var operation=request.Arg["op"];
        try
        {
            var stored=FindAdmission(request.Arg["cfg"])??throw new AgentOpsException("not_found");
            var scope=stored.Scope;
            var elevatedControl=operation=="ack"&&_launcher.IsElevatedOwner(peer);
            if(scope.OwnerSid!=peer.Sid||scope.OwnerSid!=_ownerSid||scope.OwnerSession!=peer.Session||stored.RootLabel!=authorization.Label)
                throw new AgentOpsException("admin_admission_mismatch");
            if(!elevatedControl)ValidateOwner(scope.OwnerSid,scope.OwnerSession,scope.ClientApplicationPath,peer);
            ValidatePolicy(scope.ReadRoots.Concat(scope.WriteRoots),authorization);
            if(operation=="interval")
            {
                var current=FindOpenInterval();
                if(current?.ScopeId!=scope.Id)current=FindLatestInterval(scope.Id);
                if(current is not null)ValidateIntervalOwner(current,scope,authorization);
                return current is null?new(true,Guid.NewGuid().ToString("N"),request.Rid!,"done","admin_interval",Data:new Dictionary<string,object?>{{"phase",_maintenanceMode.Read()?.Phase??"off"}}):IntervalReceipt(current);
            }
            if(!scope.AllowMaintenanceIntervals||request.Arg.GetValueOrDefault("sha")!=stored.Sha)throw new AgentOpsException("admin_scope_mismatch");
            if(operation=="ack")return await AcknowledgeMaintenanceAsync(request,peer,scope,ct);
            if(operation=="begin")
            {
                var fingerprint=MaintenanceFingerprint(request,peer,stored.Sha);
                var previous=FindIntervalByRid(request.Rid!);
                if(previous is not null)
                {
                    if(previous.Fingerprint!=fingerprint)throw new AgentOpsException("admin_admission_conflict");
                    if(previous.Phase!="preparing")return IntervalReceipt(previous);
                    if(stored.Revoked||scope.ExpiresAtUtc<=_clock.GetUtcNow())throw new AgentOpsException("admin_admission_expired");
                    return await CompletePreparationAsync(previous,ct);
                }
                if(stored.Revoked)throw new AgentOpsException("admin_admission_revoked");
                if(scope.ExpiresAtUtc<=_clock.GetUtcNow())throw new AgentOpsException("admin_admission_expired");
                if(FindOpenInterval() is{} active)return IntervalReceipt(active) with{Ok=false,Err="maintenance_busy"};
                if(_maintenanceMode.Read() is{Phase:not "off"})throw new AgentOpsException("maintenance_state_unavailable");
                if(BlockingJob() is not null)throw new AgentOpsException("admin_reconciliation_required");
                var interval=new MaintenanceInterval(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),request.Rid!,scope.Id,stored.Sha,
                    authorization.Label,scope.TaskId,scope.OwnerSid,scope.OwnerSession,peer.ApplicationPath!,fingerprint,"preparing",_serviceInstance);
                InsertInterval(interval);
                try{PublishInterval(interval,"preparing");}
                catch{SaveInterval(interval with{Phase="uncertain",Issue="maintenance_publication_unknown"});throw;}
                return await CompletePreparationAsync(interval,ct);
            }
            if(operation!="end")throw new AgentOpsException("invalid");
            var ending=FindInterval(request.Arg["epoch"])??throw new AgentOpsException("not_found");
            ValidateIntervalOwner(ending,scope,authorization);
            var endFingerprint=MaintenanceFingerprint(request,peer,stored.Sha);
            if(ending.EndRid is not null)
            {
                if(ending.EndRid!=request.Rid||ending.EndFingerprint!=endFingerprint)throw new AgentOpsException("admin_admission_conflict");
                if(ending.Phase=="off")return IntervalReceipt(ending,end:true);
            }
            if(ending.Phase is "refused" or "off")return IntervalReceipt(ending,end:true);
            if(BlockingJob() is not null)return IntervalReceipt(ending) with{Ok=false,Err="admin_reconciliation_required"};
            var reviewedOriginal=ending.Phase=="uncertain"&&IntervalAcknowledged(ending);
            if(ending.Phase=="uncertain"&&!reviewedOriginal)return IntervalReceipt(ending) with{Ok=false,Err="maintenance_reconciliation_required"};
            lock(_gate)
            {
                if(BlockingJob() is not null)return IntervalReceipt(ending) with{Ok=false,Err="admin_reconciliation_required"};
                ValidateHandback(request,ending);
                ending=ending with{Phase="ending",EndRid=request.Rid,EndJid=ending.EndJid??Guid.NewGuid().ToString("N"),EndFingerprint=endFingerprint,Issue=null};
                SaveInterval(ending);PublishInterval(ending,"ending");
            }
            var result=await ProbeAllAsync(ending,forEnd:true,ct,allowReplacementHost:reviewedOriginal);
            if(result.Issue is not null||result.Blockers.Count!=0)
            {
                ending=ending with{Phase="uncertain",Issue=result.Issue??"maintenance_unresolved_effects",Blockers=result.Blockers};
                SaveInterval(ending);PublishInterval(ending,"uncertain");return IntervalReceipt(ending,end:true) with{Ok=false,Err=ending.Issue};
            }
            // A confirmed, same-epoch quiescent handback is persisted before the
            // OFF publication. Lost response after commit retrieves this original.
            ending=ending with{Phase="handback_ready",Host=result.Host,Issue=null,Blockers=[]};SaveInterval(ending);
            PublishInterval(ending,"off");
            ending=ending with{Phase="off"};SaveInterval(ending);
            return IntervalReceipt(ending,end:true);
        }
        catch(OperationCanceledException)
        {
            var interval=FindIntervalByRid(request.Rid!)??FindOpenInterval();
            if(interval is not null&&interval.Phase is not("off" or "refused"))
            {interval=interval with{Phase="uncertain",Issue="maintenance_observation_lost"};SaveInterval(interval);TryPublishUncertain(interval);return IntervalReceipt(interval) with{Ok=false,Err=interval.Issue};}
            throw;
        }
        catch(AgentOpsException ex){return MaintenanceFailure(request,ex.Code);}
        catch(Exception ex)when(ex is IOException or SqliteException or JsonException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {return MaintenanceFailure(request,"maintenance_state_unavailable");}
    }

    private async Task<OperationReceipt> CompletePreparationAsync(MaintenanceInterval interval,CancellationToken ct)
    {
        try
        {
            var result=await ProbeAllAsync(interval,forEnd:false,ct);
            if(result.Issue=="maintenance_busy_known")
            {
                interval=interval with{Phase="refused",Host=result.Host,Issue="maintenance_busy",Blockers=result.Blockers};SaveInterval(interval);PublishInterval(interval,"off");
                return IntervalReceipt(interval) with{Ok=false,Err="maintenance_busy"};
            }
            if(result.Issue is not null)
            {
                interval=interval with{Phase="uncertain",Issue=result.Issue,Blockers=result.Blockers};SaveInterval(interval);PublishInterval(interval,"uncertain");
                return IntervalReceipt(interval) with{Ok=false,Err=result.Issue};
            }
            if(result.Blockers.Count!=0)
            {
                // Known busy rejection admits no administrator effect and does
                // not alter the original blocker. Ambiguous rejection never clears.
                interval=interval with{Phase="refused",Host=result.Host,Issue="maintenance_busy",Blockers=result.Blockers};
                SaveInterval(interval);PublishInterval(interval,"off");return IntervalReceipt(interval) with{Ok=false,Err="maintenance_busy"};
            }
            interval=interval with{Phase="active",Host=result.Host,Issue=null,Blockers=[]};SaveInterval(interval);PublishInterval(interval,"active");
            return IntervalReceipt(interval);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)when(ex is AgentOpsException or IOException or SqliteException or UnauthorizedAccessException)
        {
            interval=interval with{Phase="uncertain",Issue=ex is AgentOpsException reason?reason.Code:"maintenance_probe_unknown"};
            SaveInterval(interval);TryPublishUncertain(interval);return IntervalReceipt(interval) with{Ok=false,Err=interval.Issue};
        }
    }

    private sealed record ProbeOutcome(MaintenanceHostIdentity? Host,IReadOnlyList<MaintenanceEffect> Blockers,string? Issue);
    private async Task<ProbeOutcome> ProbeAllAsync(MaintenanceInterval interval,bool forEnd,CancellationToken ct,bool allowReplacementHost=false)
    {
        if(_userMaintenanceProbe is null||_brokerMaintenanceProbe is null)return new(null,[],"maintenance_host_unavailable");
        MaintenanceHostIdentity? host=null;var blockers=new List<MaintenanceEffect>();
        using var total=CancellationTokenSource.CreateLinkedTokenSource(ct);total.CancelAfter(TimeSpan.FromSeconds(10));
        ct=total.Token;
        foreach(var store in new[]{"user","broker"})
        {
            long cursor=0;
            // Only unresolved rows are streamed. The bounded operation fails
            // closed rather than hydrating unbounded history or declaring zero.
            for(var page=0;page<128;page++)
            {
                var probe=new MaintenanceProbeRequest(1,interval.Epoch,"snapshot",cursor);
                using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(5));
                var result=store=="user"?await _userMaintenanceProbe(probe,budget.Token):_brokerMaintenanceProbe(probe);
                if(result is null||result.Version!=1||result.Epoch!=interval.Epoch||result.Host is null||result.Effects is null||result.Effects.Count>8||result.Next<cursor||result.ActiveEffects<0||result.AppTransitions<0)return new(host,blockers,"maintenance_probe_invalid");
                if(result.Issue is not null)return new(host,blockers,result.Issue);
                if(store=="user")
                {
                    if(result.Host.Sid!=interval.OwnerSid||result.Host.Session!=interval.OwnerSession||result.Host.Pid==0||string.IsNullOrEmpty(result.Host.Start))return new(host,blockers,"maintenance_host_mismatch");
                    host??=result.Host;
                    if(host!=result.Host)return new(host,blockers,"maintenance_host_changed");
                    if(interval.Host is not null&&interval.Host!=host&&!allowReplacementHost)return new(host,blockers,"maintenance_host_changed");
                }
                if(result.ActiveEffects!=0||result.AppTransitions!=0)return new(host,result.Effects,"maintenance_busy_known");
                foreach(var effect in result.Effects)
                {
                    if(effect is null||effect.Store!=store||!IsId(effect.Jid)||!ValidOriginalRid(effect.Rid)||effect.ReceiptSha256 is null||!Sha.IsMatch(effect.ReceiptSha256))return new(host,blockers,"maintenance_probe_invalid");
                    if(_maintenanceMode.IsAcknowledged(effect))continue;
                    // Held, never-started requests may resume only AFTER a proved
                    // handback. They still block initial activation.
                    if(forEnd&&effect.State is "queued" or "waiting_user")continue;
                    blockers.Add(effect);
                }
                if(blockers.Count!=0)return new(host,blockers,null);
                if(!result.HasMore)break;
                if(result.Next<=cursor||page==127)return new(host,[],"maintenance_snapshot_bound");
                cursor=result.Next;
            }
        }
        return new(host,[],null);
    }

    private async Task<OperationReceipt> AcknowledgeMaintenanceAsync(OperationRequest request,LocalPeer peer,OwnerAdminAuthorization scope,CancellationToken ct)
    {
        if(!scope.AllowOrdinaryQuiescenceAcknowledgements||!_launcher.IsElevatedOwner(peer))throw new AgentOpsException("admin_review_required");
        using(var document=JsonDocument.Parse(request.Body!))RejectDuplicates(document.RootElement);
        var supplied=JsonSerializer.Deserialize<MaintenanceAcknowledgement>(request.Body!,Json)??throw new AgentOpsException("invalid");
        if(supplied.Version!=1||!supplied.EffectsQuiescent||supplied.ScopeId!=scope.Id||supplied.TaskId!=scope.TaskId||supplied.OwnerSid!=peer.Sid||supplied.OwnerSession!=peer.Session||!IsId(supplied.Jid)||!ValidOriginalRid(supplied.Rid))throw new AgentOpsException("admin_admission_mismatch");
        RequireSha(supplied.ReceiptSha256);RequireSha(supplied.EvidenceSha256);
        supplied=supplied with{ReviewedAtUtc=_clock.GetUtcNow()};
        if(supplied.Store=="interval")
        {
            var original=FindIntervalByJid(supplied.Jid)??throw new AgentOpsException("not_found");
            if(original.Rid!=supplied.Rid||IntervalDigest(original)!=supplied.ReceiptSha256||original.Phase!="uncertain")throw new AgentOpsException("admin_admission_mismatch");
            ValidateIntervalOwner(original,scope,new ProtectedExecutionGrant(original.RootLabel,ProtectedAccessPolicy.Off,ProtectedAccessPolicy.Off));
            if(BlockingJob() is not null)throw new AgentOpsException("admin_reconciliation_required");
        }
        else
        {
            if(supplied.Store is not("user" or "broker"))throw new AgentOpsException("invalid");
            var epoch=_maintenanceMode.Read()?.Epoch??new string('0',32);
            var query=new MaintenanceProbeRequest(1,epoch,"effect",Jid:supplied.Jid);
            var observed=supplied.Store=="user"?(_userMaintenanceProbe is null?throw new AgentOpsException("maintenance_host_unavailable"):await _userMaintenanceProbe(query,ct)):
                (_brokerMaintenanceProbe?.Invoke(query)??throw new AgentOpsException("maintenance_host_unavailable"));
            var effect=observed.Effects.SingleOrDefault()??throw new AgentOpsException("not_found");
            if(observed.Issue is not null||effect.Live||effect.State is "queued" or "waiting_user"||effect.Rid!=supplied.Rid||effect.ReceiptSha256!=supplied.ReceiptSha256)
                throw new AgentOpsException("maintenance_effects_active");
            if(supplied.Store=="broker")_brokerMaintenanceAck?.Invoke(supplied);
        }
        lock(_gate)
        {
        using var insert=_db.CreateCommand();
        insert.CommandText="INSERT OR IGNORE INTO maintenance_acknowledgements(store,jid,rid,receipt_sha,body) VALUES($store,$jid,$rid,$sha,$body)";
        insert.Parameters.AddWithValue("$store",supplied.Store);insert.Parameters.AddWithValue("$jid",supplied.Jid);insert.Parameters.AddWithValue("$rid",supplied.Rid);insert.Parameters.AddWithValue("$sha",supplied.ReceiptSha256);insert.Parameters.AddWithValue("$body",JsonSerializer.Serialize(supplied,Json));insert.ExecuteNonQuery();
        }
        _maintenanceMode.PublishAcknowledgement(supplied);
        return new(true,Guid.NewGuid().ToString("N"),request.Rid!,"done","admin_ack",Data:new Dictionary<string,object?>{{"original_jid",supplied.Jid},{"original_rid",supplied.Rid},{"original_receipt_sha",supplied.ReceiptSha256},{"evidence_sha",supplied.EvidenceSha256},{"receipt_rewritten",false},{"exit_invented",false}});
    }

    private bool IntervalAcknowledged(MaintenanceInterval interval)=>_maintenanceMode.IsAcknowledged(new("interval",interval.Jid,interval.Rid,interval.Phase,"admin_begin",IntervalDigest(interval),0));
    private static bool ValidOriginalRid(string? rid)=>rid is{Length:>0 and <=1024}&&!rid.Any(char.IsControl);
    private static string IntervalDigest(MaintenanceInterval interval)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(interval,Json)))).ToLowerInvariant();
    private static string MaintenanceFingerprint(OperationRequest request,LocalPeer peer,string sha)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{operation=request.Arg["op"],cfg=request.Arg.GetValueOrDefault("cfg"),epoch=request.Arg.GetValueOrDefault("epoch"),sha,bodySha=request.Body is null?null:Hash(request.Body),peer.Sid,peer.Session,peer.ApplicationPath},Json)))).ToLowerInvariant();
    private void ValidateIntervalOwner(MaintenanceInterval interval,OwnerAdminAuthorization scope,ProtectedExecutionGrant authorization)
    {
        if(interval.ScopeId!=scope.Id||interval.ScopeSha!=Hash(JsonSerializer.Serialize(scope,Json))||interval.TaskId!=scope.TaskId||interval.OwnerSid!=scope.OwnerSid||interval.OwnerSession!=scope.OwnerSession||interval.RootLabel!=authorization.Label)throw new AgentOpsException("admin_scope_mismatch");
    }
    private void RequireActiveMaintenance(OwnerAdminDefinition definition,OwnerAdminAuthorization scope,ProtectedExecutionGrant authorization)
    {
        var current=FindOpenInterval()??throw new AgentOpsException("maintenance_interval_required");
        var admission=FindAdmission(current.ScopeId)??throw new AgentOpsException("admin_admission_revoked");
        if(admission.Revoked||admission.Scope.ExpiresAtUtc<=_clock.GetUtcNow())throw new AgentOpsException("admin_admission_expired");
        ValidateIntervalOwner(current,scope,authorization);
        var publicMode=_maintenanceMode.Read();
        if(current.Phase!="active"||current.ServiceInstance!=_serviceInstance||publicMode?.Phase!="active"||publicMode.Epoch!=current.Epoch||definition.MaintenanceEpoch!=current.Epoch)
            throw new AgentOpsException("maintenance_epoch_conflict");
    }
    private void PublishInterval(MaintenanceInterval interval,string phase)
    {
        var mode=_maintenanceMode.Read();
        if(mode is not null&&mode.Phase!="off"&&mode.Epoch!=interval.Epoch)throw new AgentOpsException("maintenance_epoch_conflict");
        _maintenanceMode.Publish(new(1,interval.Epoch,phase,interval.Jid,interval.Rid,interval.ScopeId,interval.TaskId,interval.OwnerSid,interval.OwnerSession,_serviceInstance,interval.Host,0),mode?.Epoch,mode?.Revision??0);
    }
    private void TryPublishUncertain(MaintenanceInterval interval){try{PublishInterval(interval,"uncertain");}catch(AgentOpsException){}catch(IOException){}catch(UnauthorizedAccessException){}}
    private void InsertInterval(MaintenanceInterval interval)
    {
        lock(_gate)
        {
        using var command=_db.CreateCommand();command.CommandText="INSERT INTO maintenance_intervals(epoch,jid,rid,phase,body) VALUES($epoch,$jid,$rid,$phase,$body)";
        command.Parameters.AddWithValue("$epoch",interval.Epoch);command.Parameters.AddWithValue("$jid",interval.Jid);command.Parameters.AddWithValue("$rid",interval.Rid);command.Parameters.AddWithValue("$phase",interval.Phase);command.Parameters.AddWithValue("$body",JsonSerializer.Serialize(interval,Json));command.ExecuteNonQuery();
        }
    }
    private void SaveInterval(MaintenanceInterval interval)
    {
        lock(_gate)
        {
        using var command=_db.CreateCommand();command.CommandText="UPDATE maintenance_intervals SET phase=$phase,body=$body WHERE epoch=$epoch";
        command.Parameters.AddWithValue("$epoch",interval.Epoch);command.Parameters.AddWithValue("$phase",interval.Phase);command.Parameters.AddWithValue("$body",JsonSerializer.Serialize(interval,Json));command.ExecuteNonQuery();
        }
    }
    private MaintenanceInterval? FindOpenInterval()=>ReadInterval("SELECT body FROM maintenance_intervals WHERE phase IN ('preparing','active','ending','uncertain','handback_ready') LIMIT 1");
    private MaintenanceInterval? FindInterval(string epoch)=>ReadInterval("SELECT body FROM maintenance_intervals WHERE epoch=$value",epoch);
    private MaintenanceInterval? FindIntervalByRid(string rid)=>ReadInterval("SELECT body FROM maintenance_intervals WHERE rid=$value",rid);
    private MaintenanceInterval? FindIntervalByJid(string jid)=>ReadInterval("SELECT body FROM maintenance_intervals WHERE jid=$value",jid);
    private MaintenanceInterval? FindLatestInterval(string scope)=>ReadInterval("SELECT body FROM maintenance_intervals WHERE json_extract(body,'$.scopeId')=$value ORDER BY rowid DESC LIMIT 1",scope);
    private MaintenanceInterval? ReadInterval(string sql,string? value=null)
    {
        lock(_gate){using var command=_db.CreateCommand();command.CommandText=sql;if(value is not null)command.Parameters.AddWithValue("$value",value);return command.ExecuteScalar() is string body?JsonSerializer.Deserialize<MaintenanceInterval>(body,Json):null;}
    }
    private OperationReceipt IntervalReceipt(MaintenanceInterval interval,bool end=false)=>
        new(interval.Issue is null,end?interval.EndJid??interval.Jid:interval.Jid,end?interval.EndRid??interval.Rid:interval.Rid,
            interval.Phase is "active" or "off"?"done":interval.Phase,"admin_"+(end?"end":"begin"),interval.Issue,
            new Dictionary<string,object?>{{"epoch",interval.Epoch},{"phase",interval.Phase},{"cfg",interval.ScopeId},{"task",interval.TaskId},{"owner_sid",interval.OwnerSid},{"owner_session",interval.OwnerSession},{"host",interval.Host},{"receipt_sha",IntervalDigest(interval)},{"native_receipts_sha",NativeReceiptsDigest(interval.Epoch)},{"blockers",interval.Blockers??[]},{"ordinary_mutations_closed",interval.Phase is not("off" or "refused")},{"replay",false}},Partial:interval.Phase=="uncertain");
    private string NativeReceiptsDigest(string epoch)
    {
        lock(_gate)
        {
            using var command=_db.CreateCommand();command.CommandText="SELECT j.body FROM maintenance_native_jobs n JOIN jobs j ON j.jid=n.jid WHERE n.epoch=$epoch ORDER BY n.jid";command.Parameters.AddWithValue("$epoch",epoch);
            using var reader=command.ExecuteReader();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Stream the full indexed epoch in deterministic order. One receipt
            // is resident at a time; terminal history never exhausts admission.
            while(reader.Read())
            {
                var bytes=Encoding.UTF8.GetBytes(reader.GetString(0));hash.AppendData(bytes);hash.AppendData(new byte[]{10});
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }
    private void ValidateHandback(OperationRequest request,MaintenanceInterval interval)
    {
        using(var document=JsonDocument.Parse(request.Body!))RejectDuplicates(document.RootElement);
        var proof=JsonSerializer.Deserialize<MaintenanceHandback>(request.Body!,Json)??throw new AgentOpsException("invalid");
        RequireSha(proof.NativeReceiptsSha256);RequireSha(proof.EvidenceSha256);
        if(proof.Version!=1||!proof.EffectsQuiescent||proof.Epoch!=interval.Epoch||proof.ScopeId!=interval.ScopeId||proof.BeginJid!=interval.Jid||proof.BeginRid!=interval.Rid||proof.NativeReceiptsSha256!=NativeReceiptsDigest(interval.Epoch))throw new AgentOpsException("maintenance_handback_mismatch");
        // Root-process exit is observation only. This separate, pinned assertion
        // records the caller-reviewed domain quiescence proof for descendants
        // or effects the generic process observer cannot establish.
    }
    private OperationReceipt MaintenanceFailure(OperationRequest request,string code)
    {
        var original=FindIntervalByRid(request.Rid!)??FindOpenInterval();
        return original is null?Failure(request,code):IntervalReceipt(original) with{Ok=false,Err=code};
    }
}
