using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal sealed partial class RoutedJobStore
{
    internal void InitializeMaintenanceAcknowledgements()
    {
        lock(_gate)
        {
            using var command=_connection.CreateCommand();
            command.CommandText="""
                CREATE TABLE IF NOT EXISTS routed_maintenance_acks(jid TEXT PRIMARY KEY,rid TEXT NOT NULL,receipt_sha TEXT NOT NULL,evidence_sha TEXT NOT NULL,body TEXT NOT NULL);
                CREATE TRIGGER IF NOT EXISTS routed_maintenance_ack_invalidate AFTER UPDATE OF receipt ON routed_jobs
                BEGIN DELETE FROM routed_maintenance_acks WHERE jid=NEW.jid; END;
                """;
            command.ExecuteNonQuery();
        }
    }
    internal void ResumeRecovery(string ownerSid)
    {RecoverDispatching();RecoverEphemeralWaiting();QuarantineForeignWaiting(ownerSid);}

    internal RoutedJob? FindRid(string? rid)
    {
        if(rid is null)return null;
        lock(_gate){using var command=_connection.CreateCommand();command.CommandText="SELECT receipt,seq,request,retry_request,owner_sid,generation FROM routed_jobs WHERE rid=$rid";command.Parameters.AddWithValue("$rid",rid);using var reader=command.ExecuteReader();return reader.Read()?Read(reader):null;}
    }
    internal MaintenanceProbeResult ProbeMaintenance(MaintenanceProbeRequest request,MaintenanceHostIdentity host,Func<string,bool> live)
    {
        lock(_gate)
        {
            using var command=_connection.CreateCommand();
            command.CommandText=request.Action=="effect"
                ?"SELECT receipt,seq,COALESCE(request,retry_request) FROM routed_jobs WHERE jid=$jid LIMIT 1"
                :"SELECT receipt,seq,COALESCE(request,retry_request) FROM routed_jobs WHERE seq>$after AND state IN ('waiting_user','dispatching','running','recovery_required','attention') AND NOT EXISTS(SELECT 1 FROM routed_maintenance_acks a WHERE a.jid=routed_jobs.jid) ORDER BY seq LIMIT 33";
            if(request.Action=="effect")command.Parameters.AddWithValue("$jid",request.Jid??"");else command.Parameters.AddWithValue("$after",request.After);
            using var reader=command.ExecuteReader();var result=new List<MaintenanceEffect>();long next=request.After;var scanned=0;var more=false;
            while(reader.Read())
            {
                if(scanned++==32||result.Count==8){more=true;break;}
                var body=reader.GetString(0);var receipt=JsonSerializer.Deserialize<OperationReceipt>(body,Json)!;
                next=reader.GetInt64(1);
                var original=reader.IsDBNull(2)?null:JsonSerializer.Deserialize<OperationRequest>(reader.GetString(2),Json);
                if(original is not null?!MaintenanceRequestPolicy.IsMutation(original):MaintenanceRequestPolicy.IsReadOnlyReceipt(receipt.Op))continue;
                result.Add(new("broker",receipt.Jid,receipt.Rid,receipt.State,receipt.Op,Digest(body),next,live(receipt.Jid)));
            }
            return new(1,request.Epoch,host,result,next,more,result.Count(x=>x.Live),0);
        }
    }
    internal void AcknowledgeMaintenance(MaintenanceAcknowledgement acknowledgement)
    {
        lock(_gate)
        {
            using var read=_connection.CreateCommand();read.CommandText="SELECT receipt,state,rid FROM routed_jobs WHERE jid=$jid";read.Parameters.AddWithValue("$jid",acknowledgement.Jid);
            using(var reader=read.ExecuteReader())
            {
                if(!reader.Read()||reader.GetString(2)!=acknowledgement.Rid||Digest(reader.GetString(0))!=acknowledgement.ReceiptSha256||reader.GetString(1) is "waiting_user" or "queued")throw new AgentOpsException("admin_admission_mismatch");
            }
            using var command=_connection.CreateCommand();command.CommandText="INSERT INTO routed_maintenance_acks(jid,rid,receipt_sha,evidence_sha,body) VALUES($jid,$rid,$sha,$evidence,$body) ON CONFLICT(jid) DO UPDATE SET rid=excluded.rid,receipt_sha=excluded.receipt_sha,evidence_sha=excluded.evidence_sha,body=excluded.body";
            command.Parameters.AddWithValue("$jid",acknowledgement.Jid);command.Parameters.AddWithValue("$rid",acknowledgement.Rid);command.Parameters.AddWithValue("$sha",acknowledgement.ReceiptSha256);command.Parameters.AddWithValue("$evidence",acknowledgement.EvidenceSha256);command.Parameters.AddWithValue("$body",JsonSerializer.Serialize(acknowledgement,Json));command.ExecuteNonQuery();
        }
    }
    private static string Digest(string body)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}

[SupportedOSPlatform("windows")]
public sealed partial class AuthenticatedServiceBroker
{
    private readonly object _maintenanceGate=new();
    private IMaintenanceModeSource _maintenanceMode=null!;
    private int _ordinaryTransitions;
    private bool _routerRecoveryPending;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> _activeRouted=new();
    private void InitializeMaintenanceBroker(string stateRoot)
    {
        _maintenanceMode=_maintenanceMode??_ownerAdmin?.MaintenanceMode??MaintenanceModeStore.OpenReader();
        _store.InitializeMaintenanceAcknowledgements();
        _routerRecoveryPending=true;
        if(!MaintenanceClosed())ResumeRouterRecovery();
        _ownerAdmin?.SetMaintenanceProbes(ProbeUserMaintenanceAsync,ProbeBrokerMaintenance,AcknowledgeBrokerMaintenance);
    }
    private bool MaintenanceClosed()
    {try{return _maintenanceMode.Read() is{Phase:not "off"};}catch{return true;}}
    private void ResumeRouterRecovery()
    {
        lock(_maintenanceGate)
        {
            if(!_routerRecoveryPending||MaintenanceClosed())return;
            _store.ResumeRecovery(_ownerSid);_routerRecoveryPending=false;
        }
    }
    private sealed class OrdinaryTransition(AuthenticatedServiceBroker owner):IDisposable
    {public void Dispose(){lock(owner._maintenanceGate)owner._ordinaryTransitions--;}}
    // A probe observes controls that crossed the barrier before PREPARING.
    // Controls arriving after publication are refused before their first effect.
    private bool TryEnterOrdinaryControl(OperationRequest request,out IDisposable? transition)
    {
        transition=null;
        if(request.Op is not("root" or "queue" or "job" or "application_access")||!MaintenanceRequestPolicy.IsMutation(request))return true;
        lock(_maintenanceGate){if(AdminFences(request))return false;_ordinaryTransitions++;transition=new OrdinaryTransition(this);return true;}
    }
    private RoutedJob? ClaimOrdinary(OperationRequest request)
    {
        lock(_maintenanceGate)
        {
            var original=_store.FindRid(request.Rid);
            if(original is not null)return original;
            if(AdminFences(request))return null;
            return _store.Claim(request,_ownerSid);
        }
    }
    private MaintenanceProbeResult ProbeBrokerMaintenance(MaintenanceProbeRequest request)
    {
        var host=OwnerAdminExecution.HostIdentity(LocalIdentity.Current());
        lock(_maintenanceGate)
        {
            var mode=_maintenanceMode.Read();
            if(request.Action=="snapshot"&&(mode?.Epoch!=request.Epoch||mode.Phase=="off"))return new(1,request.Epoch,host,[],request.After,false,0,0,"maintenance_epoch_conflict");
            var acquired=_fifo.Wait(0);
            try
            {
                var result=_store.ProbeMaintenance(request,host,jid=>_activeRouted.ContainsKey(jid));
                return result with{ActiveEffects=Math.Max(result.ActiveEffects,(!acquired?1:0)+_ordinaryTransitions)};
            }
            finally{if(acquired)_fifo.Release();}
        }
    }
    private void AcknowledgeBrokerMaintenance(MaintenanceAcknowledgement acknowledgement)
    {
        lock(_maintenanceGate)
        {
            if(_ordinaryTransitions!=0||_activeRouted.ContainsKey(acknowledgement.Jid)||!_fifo.Wait(0))throw new AgentOpsException("maintenance_effects_active");
            try{_store.AcknowledgeMaintenance(acknowledgement);}finally{_fifo.Release();}
        }
    }
    private async Task<MaintenanceProbeResult> ProbeUserMaintenanceAsync(MaintenanceProbeRequest probe,CancellationToken ct)
    {
        var route=_tray??throw new AgentOpsException("maintenance_host_unavailable");
        var host=route.Identity??throw new AgentOpsException("maintenance_host_unavailable");
        var request=new OperationRequest("__maintenance_probe",new Dictionary<string,string>(),Rid:Guid.NewGuid().ToString("N"),Body:JsonSerializer.Serialize(probe));
        OperationReceipt receipt;
        if(_trayDispatch is not null)receipt=await _trayDispatch(route.Peer,route.PipeName,request,ct);
        else
        {
            await using var dispatcher=new NamedPipeDispatcher(route.PipeName,expectedServer:route.Peer,expectedProcess:host);
            receipt=await dispatcher.DispatchAsync(request,ct);
        }
        if(!receipt.Ok||receipt.Data?.GetValueOrDefault("maintenance") is not{} raw)throw new AgentOpsException(receipt.Err??"maintenance_probe_invalid");
        var result=JsonSerializer.Deserialize<MaintenanceProbeResult>(JsonSerializer.Serialize(raw),OwnerAdminExecution.Json)??throw new AgentOpsException("maintenance_probe_invalid");
        var current=_tray;
        if(result.Host!=host||current?.Identity!=host||current.PipeName!=route.PipeName)throw new AgentOpsException("maintenance_host_changed");
        return result;
    }
}
