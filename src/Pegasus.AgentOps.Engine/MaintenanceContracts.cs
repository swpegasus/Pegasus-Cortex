using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal sealed record MaintenanceHostIdentity(string Sid,int Session,uint Pid,string Start,string ApplicationPath);
internal sealed record MaintenanceModeRecord(int Version,string Epoch,string Phase,string Jid,string Rid,string ScopeId,
    string TaskId,string OwnerSid,int OwnerSession,string ServiceInstance,MaintenanceHostIdentity? Host,long Revision);
internal sealed record MaintenanceEffect(string Store,string Jid,string Rid,string State,string Op,string ReceiptSha256,
    long Sequence,bool Live=false);
internal sealed record MaintenanceProbeRequest(int Version,string Epoch,string Action,long After=0,string? Jid=null);
internal sealed record MaintenanceProbeResult(int Version,string Epoch,MaintenanceHostIdentity Host,
    IReadOnlyList<MaintenanceEffect> Effects,long Next,bool HasMore,int ActiveEffects,int AppTransitions,string? Issue=null);
internal sealed record MaintenanceAcknowledgement(int Version,string Store,string Jid,string Rid,string ReceiptSha256,
    string EvidenceSha256,string ScopeId,string TaskId,string OwnerSid,int OwnerSession,DateTimeOffset ReviewedAtUtc,bool EffectsQuiescent);

internal interface IMaintenanceModeSource
{
    MaintenanceModeRecord? Read();
    bool IsAcknowledged(MaintenanceEffect effect);
}

internal sealed record MaintenanceHandback(int Version,string Epoch,string ScopeId,string BeginJid,string BeginRid,string NativeReceiptsSha256,string EvidenceSha256,bool EffectsQuiescent);
