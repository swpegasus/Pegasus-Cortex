using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Pegasus.AgentOps.Engine;

internal enum MaintenanceInitializationStage
{
    OwnerState, OwnerStore, PublicState, FeatureCheck, Provisioning, Schema, Recovery, Complete
}

/// <summary>Process-local normal-startup observation; never permission to execute or release a fence.</summary>
internal sealed record MaintenanceInitializationObservation
{
    private enum FeatureState { Unknown, Fresh, Existing }
    private enum Failure { NotCompleted, None, AccessDenied, Missing, ProtectionRejected, StorageUnavailable, StateUnavailable, Precondition, IoUnavailable, Unavailable }
    private readonly FeatureState _feature;
    private readonly MaintenanceInitializationStage _stage;
    private readonly Failure _failure;
    private readonly bool _ready;

    private MaintenanceInitializationObservation(FeatureState feature,MaintenanceInitializationStage stage,Failure failure,bool ready)
    { _feature=feature;_stage=stage;_failure=failure;_ready=ready; }

    internal static MaintenanceInitializationObservation Started {get;}=
        new(FeatureState.Unknown,MaintenanceInitializationStage.OwnerState,Failure.NotCompleted,false);

    // The only caller supplying initialized is the existing successful feature query.
    internal MaintenanceInitializationObservation Observe(MaintenanceInitializationStage stage,bool? initialized=null)=>
        new(initialized is{} present?present?FeatureState.Existing:FeatureState.Fresh:_feature,stage,Failure.NotCompleted,false);

    // Called by the service only after the normal constructor has returned.
    internal MaintenanceInitializationObservation Completed()=>new(_feature,MaintenanceInitializationStage.Complete,Failure.None,true);

    internal MaintenanceInitializationObservation Failed(Exception error)=>new(_feature,_stage,error switch
    {
        UnauthorizedAccessException=>Failure.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException=>Failure.Missing,
        CryptographicException=>Failure.ProtectionRejected,
        SqliteException=>Failure.StorageUnavailable,
        AgentOpsException {Code:"maintenance_state_unavailable"}=>Failure.StateUnavailable,
        AgentOpsException {Code:"precondition"}=>Failure.Precondition,
        IOException=>Failure.IoUnavailable,
        _=>Failure.Unavailable
    },false);

    // Closed labels only. Do not retain exception messages, paths, database names or identities.
    internal string Feature=>_feature switch{FeatureState.Fresh=>"fresh",FeatureState.Existing=>"existing",_=>"unknown"};
    internal string Outcome=>_ready?"ready":"held";
    internal string Stage=>_stage switch
    {
        MaintenanceInitializationStage.OwnerState=>"owner-state",MaintenanceInitializationStage.OwnerStore=>"owner-store",
        MaintenanceInitializationStage.PublicState=>"public-state",MaintenanceInitializationStage.FeatureCheck=>"feature-check",
        MaintenanceInitializationStage.Provisioning=>"provisioning",MaintenanceInitializationStage.Schema=>"schema",
        MaintenanceInitializationStage.Recovery=>"recovery",MaintenanceInitializationStage.Complete=>"complete",_=>"unknown"
    };
    internal string Cause=>_failure switch
    {
        Failure.NotCompleted=>"not-completed",Failure.None=>"none",Failure.AccessDenied=>"access-denied",
        Failure.Missing=>"missing",Failure.ProtectionRejected=>"protection-rejected",Failure.StorageUnavailable=>"storage-unavailable",
        Failure.StateUnavailable=>"state-unavailable",Failure.Precondition=>"precondition",Failure.IoUnavailable=>"io-unavailable",_=>"unavailable"
    };
}
