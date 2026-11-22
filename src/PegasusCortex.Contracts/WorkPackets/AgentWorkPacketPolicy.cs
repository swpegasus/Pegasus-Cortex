using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Contracts.WorkPackets;

public sealed record AgentContractValidationV1
{
    internal AgentContractValidationV1(IEnumerable<string> issueCodes)
    {
        ArgumentNullException.ThrowIfNull(issueCodes);
        var values = issueCodes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (values.Any(value => !AgentWorkPacketShapes.IsIdentifier(value)))
            throw new ArgumentException("Validation issue codes must be stable identifiers.", nameof(issueCodes));
        IssueCodes = Array.AsReadOnly(values);
    }

    public bool Valid => IssueCodes.Count == 0;
    public IReadOnlyList<string> IssueCodes { get; }
}

/// <summary>
/// Passive in-memory integrity and receipt-linkage rules. This boundary performs no
/// filesystem, transport, identity-provider, scheduling, claim, approval, or execution work.
/// Each self-hash is SHA-256 over canonical JSON with only that self-hash replaced by
/// <see cref="AgentWorkPacketContractIds.EmptySha256"/>.
/// </summary>
public static class AgentWorkPacketPolicy
{
    public static AgentWorkPacketV1 Seal(AgentWorkPacketV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var placeholder = value.CopyWithPacketHash(AgentWorkPacketContractIds.EmptySha256);
        return value.CopyWithPacketHash(CanonicalHash(placeholder));
    }

    public static AgentWorkPacketHandoffReceiptV1 Seal(AgentWorkPacketHandoffReceiptV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var placeholder = value.CopyWithReceiptHash(AgentWorkPacketContractIds.EmptySha256);
        return value.CopyWithReceiptHash(CanonicalHash(placeholder));
    }

    public static AgentWorkPacketAcceptanceReceiptV1 Seal(AgentWorkPacketAcceptanceReceiptV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var placeholder = value.CopyWithReceiptHash(AgentWorkPacketContractIds.EmptySha256);
        return value.CopyWithReceiptHash(CanonicalHash(placeholder));
    }

    public static bool HasValidHash(AgentWorkPacketV1? value)
    {
        if (value is null)
            return false;
        try
        {
            var placeholder = value.CopyWithPacketHash(AgentWorkPacketContractIds.EmptySha256);
            var bytes = CortexCanonicalJson.Serialize(placeholder);
            return bytes.Length <= AgentWorkPacketContractLimits.MaximumSerializedPacketBytes &&
                string.Equals(value.PacketHash, CortexCanonicalJson.Sha256(bytes), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool HasValidHash(AgentWorkPacketHandoffReceiptV1? value)
    {
        if (value is null)
            return false;
        try
        {
            var placeholder = value.CopyWithReceiptHash(AgentWorkPacketContractIds.EmptySha256);
            return string.Equals(value.ReceiptHash, CanonicalHash(placeholder), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool HasValidHash(AgentWorkPacketAcceptanceReceiptV1? value)
    {
        if (value is null)
            return false;
        try
        {
            var placeholder = value.CopyWithReceiptHash(AgentWorkPacketContractIds.EmptySha256);
            return string.Equals(value.ReceiptHash, CanonicalHash(placeholder), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static AgentContractValidationV1 ValidateContinuation(
        AgentWorkPacketV1 previous,
        AgentWorkPacketV1 current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var issues = new List<string>();
        if (!HasValidHash(previous) || !HasValidHash(current))
            issues.Add("agent_work_packet_integrity_invalid");
        if (!string.Equals(previous.WorkId, current.WorkId, StringComparison.Ordinal) ||
            !string.Equals(previous.Mode, current.Mode, StringComparison.Ordinal) ||
            !string.Equals(previous.ProjectId, current.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(previous.ProjectLinkId, current.ProjectLinkId, StringComparison.Ordinal))
        {
            issues.Add("agent_work_packet_scope_changed");
        }
        if (!string.Equals(previous.ParentWorkId, current.ParentWorkId, StringComparison.Ordinal) ||
            previous.Depth != current.Depth)
        {
            issues.Add("agent_work_packet_lineage_changed");
        }
        if (current.MaxFanOut > previous.MaxFanOut)
            issues.Add("agent_work_packet_fan_out_increased");
        // ContextSetId identifies the exact bounded recovery context observed by
        // each packet revision. It may advance as the hot project brief changes;
        // WorkId and the packet hash chain remain the stable lineage boundary.
        if (previous.Stage == AgentWorkPacketStageIds.Closeout)
            issues.Add("agent_work_packet_terminal");
        if (current.Stage == AgentWorkPacketStageIds.Start ||
            current.PacketRevision != previous.PacketRevision + 1 ||
            !string.Equals(current.PreviousPacketHash, previous.PacketHash, StringComparison.Ordinal))
        {
            issues.Add("agent_work_packet_revision_conflict");
        }
        if (current.IssuedAt < previous.IssuedAt)
            issues.Add("agent_work_packet_time_regressed");
        return new AgentContractValidationV1(issues);
    }

    public static AgentContractValidationV1 ValidateForHandoff(
        AgentWorkPacketV1 value,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(value);
        AgentWorkPacketShapes.RequireUtc(observedAt, nameof(observedAt));
        var issues = new List<string>();
        if (!HasValidHash(value))
            issues.Add("agent_work_packet_integrity_invalid");
        if (observedAt < value.IssuedAt - AgentWorkPacketContractLimits.MaximumClockSkew ||
            observedAt >= value.ExpiresAt)
        {
            issues.Add("agent_work_packet_expired");
        }
        if (value.CancellationRequested)
            issues.Add("agent_work_packet_cancellation_requested");
        if (value.Freshness.State != AgentWorkFreshnessStateIds.Current ||
            value.Freshness.ObservedAt > observedAt + AgentWorkPacketContractLimits.MaximumClockSkew ||
            value.Freshness.ValidUntil is null ||
            value.Freshness.ValidUntil <= observedAt)
        {
            issues.Add("agent_work_packet_freshness_invalid");
        }
        return new AgentContractValidationV1(issues);
    }

    public static AgentWorkPacketHandoffReceiptV1 CreateHandoffReceipt(
        AgentWorkPacketV1 packet,
        string receiptId,
        AgentWorkOwnerV1 intendedOwner,
        DateTimeOffset handedOffAt)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(intendedOwner);
        var validation = ValidateForHandoff(packet, handedOffAt);
        if (!validation.Valid)
            throw new ArgumentException("The packet is not valid for handoff.", nameof(packet));
        return Seal(new AgentWorkPacketHandoffReceiptV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.HandoffReceipt,
            AgentWorkPacketContractIds.Canonicalization,
            receiptId,
            packet.WorkId,
            packet.IdempotencyKey,
            packet.Stage,
            packet.PacketRevision,
            packet.PacketHash,
            packet.Owner,
            intendedOwner,
            handedOffAt,
            packet.ExpiresAt,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentWorkPacketContractIds.EmptySha256));
    }

    public static AgentWorkPacketAcceptanceReceiptV1 CreateAcceptanceReceipt(
        AgentWorkPacketHandoffReceiptV1 handoff,
        string receiptId,
        string disposition,
        AgentWorkOwnerV1 acceptedBy,
        DateTimeOffset acceptedAt,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(acceptedBy);
        if (!HasValidHash(handoff) ||
            !AgentWorkPacketShapes.SameOwner(handoff.IntendedOwner, acceptedBy) ||
            acceptedAt < handoff.HandedOffAt ||
            acceptedAt >= handoff.PacketExpiresAt)
        {
            throw new ArgumentException("The handoff is not valid for acknowledgement.", nameof(handoff));
        }
        return Seal(new AgentWorkPacketAcceptanceReceiptV1(
            AgentWorkPacketContractIds.SchemaVersion,
            AgentWorkPacketContractIds.AcceptanceReceipt,
            AgentWorkPacketContractIds.Canonicalization,
            receiptId,
            handoff.ReceiptId,
            handoff.ReceiptHash,
            handoff.WorkId,
            handoff.IdempotencyKey,
            handoff.PacketStage,
            handoff.PacketRevision,
            handoff.PacketHash,
            disposition,
            acceptedBy,
            acceptedAt,
            reason,
            AgentWorkPacketContractIds.ContentTreatment,
            executable: false,
            grantsSchedulingAuthority: false,
            grantsTaskClaim: false,
            grantsAuthority: false,
            AgentWorkPacketContractIds.EmptySha256));
    }

    public static AgentContractValidationV1 ValidateHandoff(
        AgentWorkPacketV1 packet,
        AgentWorkPacketHandoffReceiptV1 handoff)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(handoff);
        var issues = new List<string>();
        if (!HasValidHash(packet) || !HasValidHash(handoff))
            issues.Add("agent_work_handoff_integrity_invalid");
        if (!string.Equals(handoff.WorkId, packet.WorkId, StringComparison.Ordinal) ||
            !string.Equals(handoff.IdempotencyKey, packet.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(handoff.PacketStage, packet.Stage, StringComparison.Ordinal) ||
            handoff.PacketRevision != packet.PacketRevision ||
            !string.Equals(handoff.PacketHash, packet.PacketHash, StringComparison.Ordinal) ||
            !AgentWorkPacketShapes.SameOwner(handoff.FromOwner, packet.Owner) ||
            handoff.HandedOffAt < packet.IssuedAt ||
            handoff.HandedOffAt >= packet.ExpiresAt ||
            handoff.PacketExpiresAt != packet.ExpiresAt)
        {
            issues.Add("agent_work_handoff_packet_mismatch");
        }
        return new AgentContractValidationV1(issues);
    }

    public static AgentContractValidationV1 ValidateAcceptance(
        AgentWorkPacketHandoffReceiptV1 handoff,
        AgentWorkPacketAcceptanceReceiptV1 acceptance)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(acceptance);
        var issues = new List<string>();
        if (!HasValidHash(handoff) || !HasValidHash(acceptance))
            issues.Add("agent_work_acceptance_integrity_invalid");
        if (!string.Equals(acceptance.HandoffReceiptId, handoff.ReceiptId, StringComparison.Ordinal) ||
            !string.Equals(acceptance.HandoffReceiptHash, handoff.ReceiptHash, StringComparison.Ordinal) ||
            !string.Equals(acceptance.WorkId, handoff.WorkId, StringComparison.Ordinal) ||
            !string.Equals(acceptance.IdempotencyKey, handoff.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(acceptance.PacketStage, handoff.PacketStage, StringComparison.Ordinal) ||
            acceptance.PacketRevision != handoff.PacketRevision ||
            !string.Equals(acceptance.PacketHash, handoff.PacketHash, StringComparison.Ordinal) ||
            !AgentWorkPacketShapes.SameOwner(acceptance.AcceptedBy, handoff.IntendedOwner) ||
            acceptance.AcceptedAt < handoff.HandedOffAt ||
            acceptance.AcceptedAt >= handoff.PacketExpiresAt)
        {
            issues.Add("agent_work_acceptance_handoff_mismatch");
        }
        return new AgentContractValidationV1(issues);
    }

    public static bool IsIdempotentReplay(AgentWorkPacketV1 existing, AgentWorkPacketV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Equals(existing.WorkId, candidate.WorkId, StringComparison.Ordinal) &&
            string.Equals(existing.IdempotencyKey, candidate.IdempotencyKey, StringComparison.Ordinal) &&
            string.Equals(existing.PacketHash, candidate.PacketHash, StringComparison.Ordinal);
    }

    public static bool HasIdempotencyConflict(AgentWorkPacketV1 existing, AgentWorkPacketV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Equals(existing.WorkId, candidate.WorkId, StringComparison.Ordinal) &&
            string.Equals(existing.IdempotencyKey, candidate.IdempotencyKey, StringComparison.Ordinal) &&
            !string.Equals(existing.PacketHash, candidate.PacketHash, StringComparison.Ordinal);
    }

    public static bool SourceHashesMatch(
        AgentWorkPacketV1 value,
        IReadOnlyDictionary<string, string> currentSourceHashes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(currentSourceHashes);
        return value.SourceRevisions.All(source =>
            currentSourceHashes.TryGetValue(source.SourceId, out var currentHash) &&
            string.Equals(currentHash, source.Sha256, StringComparison.Ordinal));
    }

    private static string CanonicalHash<T>(T value) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(value));
}
