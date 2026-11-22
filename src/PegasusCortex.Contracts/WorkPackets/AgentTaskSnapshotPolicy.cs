using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Contracts.WorkPackets;

/// <summary>
/// Immutable revision and integrity rules for retained task assertions. Conflict results
/// require an external producer to reconcile; Cortex never selects or merges a winning list.
/// SnapshotHash is SHA-256 over canonical JSON with SnapshotHash replaced by 64 zeroes.
/// </summary>
public static class AgentTaskSnapshotPolicy
{
    public static AgentTaskSnapshotV1 Seal(AgentTaskSnapshotV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var placeholder = value.CopyWithSnapshotHash(AgentWorkPacketContractIds.EmptySha256);
        return value.CopyWithSnapshotHash(CanonicalHash(placeholder));
    }

    public static bool HasValidHash(AgentTaskSnapshotV1? value)
    {
        if (value is null)
            return false;
        try
        {
            var placeholder = value.CopyWithSnapshotHash(AgentWorkPacketContractIds.EmptySha256);
            var bytes = CortexCanonicalJson.Serialize(placeholder);
            return bytes.Length <= AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes &&
                string.Equals(value.SnapshotHash, CortexCanonicalJson.Sha256(bytes), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static AgentContractValidationV1 ValidateRevision(
        AgentTaskSnapshotV1 previous,
        AgentTaskSnapshotV1 current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var issues = new List<string>();
        if (!HasValidHash(previous) || !HasValidHash(current))
            issues.Add("agent_task_snapshot_integrity_invalid");
        if (!string.Equals(previous.TaskSetId, current.TaskSetId, StringComparison.Ordinal) ||
            !string.Equals(previous.Mode, current.Mode, StringComparison.Ordinal) ||
            !string.Equals(previous.ProjectId, current.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(previous.ProjectLinkId, current.ProjectLinkId, StringComparison.Ordinal))
        {
            issues.Add("agent_task_snapshot_scope_changed");
        }
        // ContextSetId identifies the exact bounded recovery context observed by
        // each revision. It may advance as the hot project brief changes; the
        // task-set identity and hash chain remain the stable lineage boundary.
        if (current.SnapshotRevision != previous.SnapshotRevision + 1 ||
            !string.Equals(current.PreviousSnapshotHash, previous.SnapshotHash, StringComparison.Ordinal))
        {
            issues.Add("agent_task_snapshot_revision_conflict");
        }
        if (string.Equals(current.SnapshotId, previous.SnapshotId, StringComparison.Ordinal))
            issues.Add("agent_task_snapshot_identity_reused");
        if (current.CapturedAt < previous.CapturedAt)
            issues.Add("agent_task_snapshot_time_regressed");
        return new AgentContractValidationV1(issues);
    }

    public static bool IsFreshAt(AgentTaskSnapshotV1 value, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(value);
        AgentWorkPacketShapes.RequireUtc(observedAt, nameof(observedAt));
        return HasValidHash(value) &&
            observedAt >= value.CapturedAt - AgentWorkPacketContractLimits.MaximumClockSkew &&
            observedAt < value.ExpiresAt &&
            value.Freshness.State == AgentWorkFreshnessStateIds.Current &&
            value.Freshness.ObservedAt <= observedAt + AgentWorkPacketContractLimits.MaximumClockSkew &&
            value.Freshness.ValidUntil is not null &&
            value.Freshness.ValidUntil > observedAt;
    }

    public static bool IsIdempotentReplay(AgentTaskSnapshotV1 existing, AgentTaskSnapshotV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Equals(existing.TaskSetId, candidate.TaskSetId, StringComparison.Ordinal) &&
            string.Equals(existing.IdempotencyKey, candidate.IdempotencyKey, StringComparison.Ordinal) &&
            string.Equals(existing.SnapshotHash, candidate.SnapshotHash, StringComparison.Ordinal);
    }

    public static bool HasIdempotencyConflict(AgentTaskSnapshotV1 existing, AgentTaskSnapshotV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Equals(existing.TaskSetId, candidate.TaskSetId, StringComparison.Ordinal) &&
            string.Equals(existing.IdempotencyKey, candidate.IdempotencyKey, StringComparison.Ordinal) &&
            !string.Equals(existing.SnapshotHash, candidate.SnapshotHash, StringComparison.Ordinal);
    }

    public static bool SourceHashesMatch(
        AgentTaskSnapshotV1 value,
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
