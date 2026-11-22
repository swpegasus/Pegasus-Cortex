using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Core.Learning;
using System.Collections;

internal enum FaultyRepositoryMode
{
    CorruptProposalWrite,
    MalformedFind,
    CorruptReviewWrite,
    MutablePage
}

internal sealed class LyingCountLearningFeedbackRepository(
    params LearningFeedbackSnapshot[] pageItems) : ILearningFeedbackRepository
{
    public ValueTask<LearningFeedbackSnapshot?> FindAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<LearningFeedbackProposalWriteResult> AddProposalAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<LearningFeedbackDecisionWriteResult> AddDecisionIfPendingAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<LearningFeedbackRepositoryPage> ListAsync(
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new LearningFeedbackRepositoryPage(
                new LyingCountReadOnlyList<LearningFeedbackSnapshot>(pageItems),
                null));
    }

    public ValueTask<LearningFeedbackRepositoryStatus> GetStatusAsync(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class LyingCountReadOnlyList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
{
    public int Count => Math.Min(1, items.Count);
    public T this[int index] => items[index];
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Deliberately violates adapter contracts so the application fail-closed boundary is tested.
/// </summary>
internal sealed class FaultyLearningFeedbackRepository(
    FaultyRepositoryMode mode,
    LearningFeedbackProposalV1 validProposal,
    CancellationTokenSource? cancelAfterList = null) : ILearningFeedbackRepository
{
    private readonly List<LearningFeedbackSnapshot> _page = [new(validProposal, null)];

    public void ClearMutablePage() => _page.Clear();

    public ValueTask<LearningFeedbackSnapshot?> FindAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proposal = mode == FaultyRepositoryMode.MalformedFind
            ? validProposal with { Payload = null! }
            : validProposal;
        return ValueTask.FromResult<LearningFeedbackSnapshot?>(new LearningFeedbackSnapshot(proposal, null));
    }

    public ValueTask<LearningFeedbackProposalWriteResult> AddProposalAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var corrupt = proposal with { TenantId = "tenant-cross-scope" };
        return ValueTask.FromResult(
            new LearningFeedbackProposalWriteResult(
                LearningFeedbackProposalWriteDisposition.Added,
                corrupt));
    }

    public ValueTask<LearningFeedbackDecisionWriteResult> AddDecisionIfPendingAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var corrupt = decision with { Namespace = "project:cross-scope" };
        return ValueTask.FromResult(
            new LearningFeedbackDecisionWriteResult(
                LearningFeedbackDecisionWriteDisposition.Added,
                new LearningFeedbackSnapshot(validProposal, corrupt)));
    }

    public ValueTask<LearningFeedbackRepositoryPage> ListAsync(
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cancelAfterList?.Cancel();
        return ValueTask.FromResult(
            new LearningFeedbackRepositoryPage(_page, null));
    }

    public ValueTask<LearningFeedbackRepositoryStatus> GetStatusAsync(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LearningFeedbackRepositoryStatus(1, 1, 0, 0));
    }
}
