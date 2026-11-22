using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Contracts.WorkPackets;

/// <summary>
/// Producer-authored assertion that exact packet bytes were offered to an
/// intended owner. Its hash proves byte linkage, not delivery or observation.
/// It is not a scheduling decision, task claim, or authority grant.
/// </summary>
public sealed record AgentWorkPacketHandoffReceiptV1
{
    [JsonConstructor]
    public AgentWorkPacketHandoffReceiptV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string receiptId,
        string workId,
        string idempotencyKey,
        string packetStage,
        long packetRevision,
        string packetHash,
        AgentWorkOwnerV1 fromOwner,
        AgentWorkOwnerV1 intendedOwner,
        DateTimeOffset handedOffAt,
        DateTimeOffset packetExpiresAt,
        string contentTreatment,
        bool executable,
        bool grantsSchedulingAuthority,
        bool grantsTaskClaim,
        bool grantsAuthority,
        string receiptHash)
    {
        AgentWorkPacketShapes.RequireHeader(
            schemaVersion,
            contractId,
            AgentWorkPacketContractIds.HandoffReceipt,
            canonicalization);
        AgentWorkPacketShapes.RequireIdentifier(receiptId, nameof(receiptId));
        AgentWorkPacketShapes.RequireIdentifier(workId, nameof(workId));
        AgentWorkPacketShapes.RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        if (packetStage is not (
                AgentWorkPacketStageIds.Start or
                AgentWorkPacketStageIds.Continue or
                AgentWorkPacketStageIds.Closeout) ||
            packetRevision is < 1 or > 9_007_199_254_740_991)
        {
            throw new ArgumentException("The handed-off packet identity is invalid.");
        }
        AgentWorkPacketShapes.RequireHash(packetHash, nameof(packetHash));
        FromOwner = fromOwner ?? throw new ArgumentNullException(nameof(fromOwner));
        IntendedOwner = intendedOwner ?? throw new ArgumentNullException(nameof(intendedOwner));
        AgentWorkPacketShapes.RequireUtc(handedOffAt, nameof(handedOffAt));
        AgentWorkPacketShapes.RequireUtc(packetExpiresAt, nameof(packetExpiresAt));
        if (handedOffAt >= packetExpiresAt)
            throw new ArgumentException("A packet cannot be handed off at or after expiry.");
        if (!string.Equals(contentTreatment, AgentWorkPacketContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable || grantsSchedulingAuthority || grantsTaskClaim || grantsAuthority)
        {
            throw new ArgumentException("A handoff receipt is passive untrusted evidence only.");
        }
        AgentWorkPacketShapes.RequireHash(receiptHash, nameof(receiptHash));

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        ReceiptId = receiptId;
        WorkId = workId;
        IdempotencyKey = idempotencyKey;
        PacketStage = packetStage;
        PacketRevision = packetRevision;
        PacketHash = packetHash;
        HandedOffAt = handedOffAt;
        PacketExpiresAt = packetExpiresAt;
        ContentTreatment = contentTreatment;
        Executable = executable;
        GrantsSchedulingAuthority = grantsSchedulingAuthority;
        GrantsTaskClaim = grantsTaskClaim;
        GrantsAuthority = grantsAuthority;
        ReceiptHash = receiptHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string ReceiptId { get; }
    public string WorkId { get; }
    public string IdempotencyKey { get; }
    public string PacketStage { get; }
    public long PacketRevision { get; }
    public string PacketHash { get; }
    public AgentWorkOwnerV1 FromOwner { get; }
    public AgentWorkOwnerV1 IntendedOwner { get; }
    public DateTimeOffset HandedOffAt { get; }
    public DateTimeOffset PacketExpiresAt { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public bool GrantsSchedulingAuthority { get; }
    public bool GrantsTaskClaim { get; }
    public bool GrantsAuthority { get; }
    public string ReceiptHash { get; }

    internal AgentWorkPacketHandoffReceiptV1 CopyWithReceiptHash(string receiptHash) => new(
        SchemaVersion,
        ContractId,
        Canonicalization,
        ReceiptId,
        WorkId,
        IdempotencyKey,
        PacketStage,
        PacketRevision,
        PacketHash,
        FromOwner,
        IntendedOwner,
        HandedOffAt,
        PacketExpiresAt,
        ContentTreatment,
        Executable,
        GrantsSchedulingAuthority,
        GrantsTaskClaim,
        GrantsAuthority,
        receiptHash);
}

/// <summary>
/// Caller-authored assertion that an intended owner accepted one exact handoff.
/// Matching hashes prove byte-level linkage only; without an authenticated
/// identity adapter they do not prove the named receiver observed or accepted
/// the packet. The execution host still revalidates identity and authority.
/// </summary>
public sealed record AgentWorkPacketAcceptanceReceiptV1
{
    [JsonConstructor]
    public AgentWorkPacketAcceptanceReceiptV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string receiptId,
        string handoffReceiptId,
        string handoffReceiptHash,
        string workId,
        string idempotencyKey,
        string packetStage,
        long packetRevision,
        string packetHash,
        string disposition,
        AgentWorkOwnerV1 acceptedBy,
        DateTimeOffset acceptedAt,
        string reason,
        string contentTreatment,
        bool executable,
        bool grantsSchedulingAuthority,
        bool grantsTaskClaim,
        bool grantsAuthority,
        string receiptHash)
    {
        AgentWorkPacketShapes.RequireHeader(
            schemaVersion,
            contractId,
            AgentWorkPacketContractIds.AcceptanceReceipt,
            canonicalization);
        AgentWorkPacketShapes.RequireIdentifier(receiptId, nameof(receiptId));
        AgentWorkPacketShapes.RequireIdentifier(handoffReceiptId, nameof(handoffReceiptId));
        AgentWorkPacketShapes.RequireHash(handoffReceiptHash, nameof(handoffReceiptHash));
        AgentWorkPacketShapes.RequireIdentifier(workId, nameof(workId));
        AgentWorkPacketShapes.RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        if (packetStage is not (
                AgentWorkPacketStageIds.Start or
                AgentWorkPacketStageIds.Continue or
                AgentWorkPacketStageIds.Closeout) ||
            packetRevision is < 1 or > 9_007_199_254_740_991)
        {
            throw new ArgumentException("The accepted packet identity is invalid.");
        }
        AgentWorkPacketShapes.RequireHash(packetHash, nameof(packetHash));
        if (disposition is not (
                AgentWorkAcceptanceDispositionIds.Accepted or
                AgentWorkAcceptanceDispositionIds.Rejected))
        {
            throw new ArgumentException("The acceptance disposition is invalid.", nameof(disposition));
        }
        AcceptedBy = acceptedBy ?? throw new ArgumentNullException(nameof(acceptedBy));
        AgentWorkPacketShapes.RequireUtc(acceptedAt, nameof(acceptedAt));
        AgentWorkPacketShapes.RequireText(
            reason,
            AgentWorkPacketContractLimits.MaximumReceiptReasonCharacters,
            nameof(reason));
        if (!string.Equals(contentTreatment, AgentWorkPacketContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable || grantsSchedulingAuthority || grantsTaskClaim || grantsAuthority)
        {
            throw new ArgumentException("An acceptance receipt is passive untrusted evidence only.");
        }
        AgentWorkPacketShapes.RequireHash(receiptHash, nameof(receiptHash));

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        ReceiptId = receiptId;
        HandoffReceiptId = handoffReceiptId;
        HandoffReceiptHash = handoffReceiptHash;
        WorkId = workId;
        IdempotencyKey = idempotencyKey;
        PacketStage = packetStage;
        PacketRevision = packetRevision;
        PacketHash = packetHash;
        Disposition = disposition;
        AcceptedAt = acceptedAt;
        Reason = reason;
        ContentTreatment = contentTreatment;
        Executable = executable;
        GrantsSchedulingAuthority = grantsSchedulingAuthority;
        GrantsTaskClaim = grantsTaskClaim;
        GrantsAuthority = grantsAuthority;
        ReceiptHash = receiptHash;
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string ReceiptId { get; }
    public string HandoffReceiptId { get; }
    public string HandoffReceiptHash { get; }
    public string WorkId { get; }
    public string IdempotencyKey { get; }
    public string PacketStage { get; }
    public long PacketRevision { get; }
    public string PacketHash { get; }
    public string Disposition { get; }
    public AgentWorkOwnerV1 AcceptedBy { get; }
    public DateTimeOffset AcceptedAt { get; }
    public string Reason { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public bool GrantsSchedulingAuthority { get; }
    public bool GrantsTaskClaim { get; }
    public bool GrantsAuthority { get; }
    public string ReceiptHash { get; }

    internal AgentWorkPacketAcceptanceReceiptV1 CopyWithReceiptHash(string receiptHash) => new(
        SchemaVersion,
        ContractId,
        Canonicalization,
        ReceiptId,
        HandoffReceiptId,
        HandoffReceiptHash,
        WorkId,
        IdempotencyKey,
        PacketStage,
        PacketRevision,
        PacketHash,
        Disposition,
        AcceptedBy,
        AcceptedAt,
        Reason,
        ContentTreatment,
        Executable,
        GrantsSchedulingAuthority,
        GrantsTaskClaim,
        GrantsAuthority,
        receiptHash);
}
