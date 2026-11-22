using Pegasus.Cortex.Contracts.Learning;

namespace Pegasus.Cortex.Core.Learning;

public enum LearningFeedbackProposalWriteDisposition
{
    Added,
    Existing,
    Conflict
}

public sealed record LearningFeedbackProposalWriteResult(
    LearningFeedbackProposalWriteDisposition Disposition,
    LearningFeedbackProposalV1? Proposal);

public enum LearningFeedbackDecisionWriteDisposition
{
    Added,
    Existing,
    MissingProposal,
    ProposalHashConflict,
    AlreadyReviewed
}

public sealed record LearningFeedbackDecisionWriteResult(
    LearningFeedbackDecisionWriteDisposition Disposition,
    LearningFeedbackSnapshot? Snapshot);

public sealed record LearningFeedbackRepositoryPage(
    IReadOnlyList<LearningFeedbackSnapshot> Items,
    string? NextAfterProposalId);

public sealed record LearningFeedbackRepositoryStatus(
    long Total,
    long Pending,
    long Approved,
    long Rejected);

/// <summary>
/// Immutable storage boundary. Implementations must make proposal idempotency and the
/// pending-to-reviewed transition atomic. Every operation receives the server-derived
/// namespace access that authorized it; implementations must never derive authority from records.
/// </summary>
public interface ILearningFeedbackRepository
{
    ValueTask<LearningFeedbackSnapshot?> FindAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackProposalWriteResult> AddProposalAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackDecisionWriteResult> AddDecisionIfPendingAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackRepositoryPage> ListAsync(
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<LearningFeedbackRepositoryStatus> GetStatusAsync(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken);
}
