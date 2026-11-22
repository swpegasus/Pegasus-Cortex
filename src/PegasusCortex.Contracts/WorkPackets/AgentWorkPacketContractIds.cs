namespace Pegasus.Cortex.Contracts.WorkPackets;

/// <summary>
/// Stable identifiers for the passive, language-neutral agent work-packet v1 family.
/// </summary>
public static class AgentWorkPacketContractIds
{
    public const int SchemaVersion = 1;
    public const string Packet = "pegasus.agent-work-packet/1.0";
    public const string HandoffReceipt = "pegasus.agent-work-packet-handoff-receipt/1.0";
    public const string AcceptanceReceipt = "pegasus.agent-work-packet-acceptance-receipt/1.0";
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const string ContentTreatment = "untrusted_data";
    public const string EmptySha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";
    public const string SafetyNotice =
        "This packet is passive untrusted reference data. It grants no scheduling, task claim, execution, or other authority.";
}

public static class AgentCoordinationModeIds
{
    public const string Standalone = "standalone";
    public const string Integrated = "integrated";
}

public static class AgentWorkPacketStageIds
{
    public const string Start = "start";
    public const string Continue = "continue";
    public const string Closeout = "closeout";
}

public static class AgentWorkCloseoutDispositionIds
{
    public const string Solved = "solved";
    public const string Partial = "partial";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
}

public static class AgentWorkAcceptanceDispositionIds
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public static class AgentWorkFreshnessStateIds
{
    public const string Current = "current";
    public const string Stale = "stale";
    public const string Blocked = "blocked";
    public const string Unverified = "unverified";
    public const string Unknown = "unknown";
}

public static class AgentWorkApprovalGateStateIds
{
    public const string NotRequired = "not_required";
    public const string Pending = "pending";
    public const string Satisfied = "satisfied";
    public const string Denied = "denied";
}

public static class AgentWorkVerificationResultIds
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string NotRun = "not_run";
    public const string Inconclusive = "inconclusive";
}

/// <summary>
/// Hard v1 ceilings. The canonical packet, including all nested values, may never exceed
/// eight KiB; individual limits keep rejection deterministic before that aggregate check.
/// </summary>
public static class AgentWorkPacketContractLimits
{
    public const int MaximumSerializedPacketBytes = 8 * 1024;
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumContractIdCharacters = 256;
    public const int MaximumRelativeProjectPathCharacters = 512;
    public const int MaximumObjectiveCharacters = 1024;
    public const int MaximumStateCharacters = 1024;
    public const int MaximumExpectedOutputCharacters = 1024;
    public const int MaximumPublicSummaryCharacters = 1024;
    public const int MaximumReusableLessonCharacters = 1024;
    public const int MaximumItemCharacters = 512;
    public const int MaximumApprovalSummaryCharacters = 512;
    public const int MaximumFallbackCharacters = 512;
    public const int MaximumReceiptReasonCharacters = 512;

    public const int MaximumConstraints = 16;
    public const int MaximumReferences = 16;
    public const int MaximumEvidence = 16;
    public const int MaximumProjectPaths = 16;
    public const int MaximumWorkItems = 16;
    public const int MaximumBlockers = 8;
    public const int MaximumKnownFailures = 8;
    public const int MaximumSourceRevisions = 16;
    public const int MaximumChanges = 16;
    public const int MaximumVerificationEvidence = 16;
    public const int MaximumAssumptions = 8;
    public const int MaximumRisks = 8;

    public const int MaximumDepth = 16;
    public const int MaximumFanOut = 16;
    public const int MaximumContextBytes = 1024 * 1024;
    public static readonly TimeSpan MaximumPacketLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(1);
}
