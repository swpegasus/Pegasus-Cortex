using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Core.Learning;

namespace Pegasus.Cortex.Persistence.Testing.Learning;

/// <summary>
/// Deterministic, concurrency-safe test repository. It proves the repository contract but
/// is not a production durability, transaction, retention, or backup implementation.
/// </summary>
public sealed class InMemoryLearningFeedbackRepository : ILearningFeedbackRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<RecordKey, Entry> _entries = [];

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public ValueTask<LearningFeedbackSnapshot?> FindAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = new RecordKey(access.TenantId, access.Namespace, proposalId);
            return ValueTask.FromResult(
                _entries.TryGetValue(key, out var entry)
                    ? Snapshot(entry, cancellationToken)
                    : null);
        }
    }

    public ValueTask<LearningFeedbackProposalWriteResult> AddProposalAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        if (!LearningFeedbackSnapshotBoundary.TryCapture(
                new LearningFeedbackSnapshot(proposal, null),
                out var snapshot,
                cancellationToken))
        {
            throw new ArgumentException("The proposal is not a valid immutable learning-feedback snapshot.", nameof(proposal));
        }
        var captured = snapshot!.Proposal;
        EnsureScope(access, captured.TenantId, captured.Namespace, nameof(proposal));

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new RecordKey(access.TenantId, access.Namespace, captured.ProposalId);
            if (_entries.TryGetValue(key, out var existing))
            {
                var disposition = string.Equals(
                    existing.Proposal.ProposalHash,
                    captured.ProposalHash,
                    StringComparison.Ordinal)
                    ? LearningFeedbackProposalWriteDisposition.Existing
                    : LearningFeedbackProposalWriteDisposition.Conflict;
                return ValueTask.FromResult(
                    new LearningFeedbackProposalWriteResult(
                        disposition,
                        Snapshot(existing, cancellationToken).Proposal));
            }

            _entries.Add(key, new Entry(captured));
            return ValueTask.FromResult(
                new LearningFeedbackProposalWriteResult(
                    LearningFeedbackProposalWriteDisposition.Added,
                    Snapshot(_entries[key], cancellationToken).Proposal));
        }
    }

    public ValueTask<LearningFeedbackDecisionWriteResult> AddDecisionIfPendingAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = decision with { };
        EnsureScope(access, captured.TenantId, captured.Namespace, nameof(decision));

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new RecordKey(access.TenantId, access.Namespace, captured.ProposalId);
            if (!_entries.TryGetValue(key, out var entry))
            {
                return ValueTask.FromResult(
                    new LearningFeedbackDecisionWriteResult(
                        LearningFeedbackDecisionWriteDisposition.MissingProposal,
                        null));
            }

            if (!string.Equals(entry.Proposal.ProposalHash, captured.ProposalHash, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(
                    new LearningFeedbackDecisionWriteResult(
                        LearningFeedbackDecisionWriteDisposition.ProposalHashConflict,
                        Snapshot(entry, cancellationToken)));
            }

            if (entry.Decision is not null)
            {
                var disposition =
                    string.Equals(entry.Decision.DecisionId, captured.DecisionId, StringComparison.Ordinal) &&
                    string.Equals(entry.Decision.DecisionHash, captured.DecisionHash, StringComparison.Ordinal)
                        ? LearningFeedbackDecisionWriteDisposition.Existing
                        : LearningFeedbackDecisionWriteDisposition.AlreadyReviewed;
                return ValueTask.FromResult(
                    new LearningFeedbackDecisionWriteResult(
                        disposition,
                        Snapshot(entry, cancellationToken)));
            }

            if (!LearningFeedbackSnapshotBoundary.TryCapture(
                    new LearningFeedbackSnapshot(entry.Proposal, captured),
                    out _,
                    cancellationToken))
            {
                throw new ArgumentException("The decision is not a valid immutable learning-feedback snapshot.", nameof(decision));
            }

            entry.Decision = captured;
            return ValueTask.FromResult(
                new LearningFeedbackDecisionWriteResult(
                    LearningFeedbackDecisionWriteDisposition.Added,
                    Snapshot(entry, cancellationToken)));
        }
    }

    public ValueTask<LearningFeedbackRepositoryPage> ListAsync(
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var selected = new List<Entry>(limit + 1);
            foreach (var entry in _entries.Values
                         .Where(item =>
                             string.Equals(item.Proposal.TenantId, access.TenantId, StringComparison.Ordinal) &&
                             string.Equals(item.Proposal.Namespace, access.Namespace, StringComparison.Ordinal) &&
                             (afterProposalId is null ||
                              string.CompareOrdinal(item.Proposal.ProposalId, afterProposalId) > 0))
                         .OrderBy(item => item.Proposal.ProposalId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                selected.Add(entry);
                if (selected.Count == limit + 1)
                    break;
            }

            var hasMore = selected.Count > limit;
            var items = new List<LearningFeedbackSnapshot>(Math.Min(limit, selected.Count));
            foreach (var entry in selected.Take(limit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(Snapshot(entry, cancellationToken));
            }
            var next = hasMore && items.Count > 0 ? items[^1].Proposal.ProposalId : null;
            return ValueTask.FromResult(
                new LearningFeedbackRepositoryPage(Array.AsReadOnly(items.ToArray()), next));
        }
    }

    public ValueTask<LearningFeedbackRepositoryStatus> GetStatusAsync(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            long pending = 0;
            long approved = 0;
            long rejected = 0;
            foreach (var entry in _entries.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(entry.Proposal.TenantId, access.TenantId, StringComparison.Ordinal) ||
                    !string.Equals(entry.Proposal.Namespace, access.Namespace, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (entry.Decision?.State)
                {
                    case null:
                        pending++;
                        break;
                    case "approved":
                        approved++;
                        break;
                    case "rejected":
                        rejected++;
                        break;
                    default:
                        throw new InvalidOperationException("The test repository contains an unsupported review state.");
                }
            }

            return ValueTask.FromResult(
                new LearningFeedbackRepositoryStatus(
                    pending + approved + rejected,
                    pending,
                    approved,
                    rejected));
        }
    }

    private static void EnsureScope(
        LearningFeedbackNamespaceAccess access,
        string tenantId,
        string namespaceName,
        string parameterName)
    {
        if (!string.Equals(access.TenantId, tenantId, StringComparison.Ordinal) ||
            !string.Equals(access.Namespace, namespaceName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The learning-feedback record does not match the authorized repository scope.",
                parameterName);
        }
    }

    private static LearningFeedbackSnapshot Snapshot(
        Entry entry,
        CancellationToken cancellationToken)
    {
        if (!LearningFeedbackSnapshotBoundary.TryCapture(
                new LearningFeedbackSnapshot(entry.Proposal, entry.Decision),
                out var snapshot,
                cancellationToken))
        {
            throw new InvalidOperationException("The test repository contains an invalid learning-feedback snapshot.");
        }
        return snapshot!;
    }

    private readonly record struct RecordKey(
        string TenantId,
        string Namespace,
        string ProposalId);

    private sealed class Entry(LearningFeedbackProposalV1 proposal)
    {
        public LearningFeedbackProposalV1 Proposal { get; } = proposal;
        public LearningFeedbackReviewDecisionV1? Decision { get; set; }
    }
}
