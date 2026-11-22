namespace Pegasus.Cortex.Contracts.WorkPackets;

public static class AgentTaskSnapshotContractIds
{
    public const int SchemaVersion = 1;
    public const string Snapshot = "pegasus.agent-task-snapshot/1.0";
    public const string Coverage = "complete";
    public const string SafetyNotice =
        "This snapshot is passive untrusted reference data. It grants no scheduling, task claim, execution, or other authority.";
}

public static class AgentTaskStateIds
{
    public const string Planned = "planned";
    public const string Active = "active";
    public const string Blocked = "blocked";
    public const string Held = "held";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    public const string Unknown = "unknown";
}

/// <summary>
/// Retained assertion kinds that a passive task snapshot may cite as evidence.
/// These identifiers describe data records only and do not grant authority.
/// </summary>
public static class AgentTaskEvidenceKindIds
{
    public const string WorkPacket = "work-packet";
    public const string WorkPacketHandoffReceipt = "work-packet-handoff-receipt";
    public const string WorkPacketAcceptanceReceipt = "work-packet-acceptance-receipt";
}

public static class AgentTaskSnapshotContractLimits
{
    public const int MaximumSerializedSnapshotBytes = 32 * 1024;
    public const int MaximumTasks = 32;
    public const int MaximumDependenciesPerTask = 16;
    public const int MaximumEvidencePerTask = 16;
    public const int MaximumEvidenceReferences = 16;
    public const int MaximumSourcesPerTask = 16;
    public const long MaximumEvidenceRevision = 9_007_199_254_740_991;
}
