using Pegasus.Cortex.Contracts.Learning;

namespace Pegasus.Cortex.Core.Learning;

/// <summary>
/// Store-owned outcomes for one immutable publication-change event. Results
/// intentionally carry only bounded identifiers and head coordinates; record
/// content, author metadata, and free-form reasons never cross this boundary.
/// </summary>
public enum LearningFeedbackPublicationChangeWriteDisposition
{
    Applied,
    Existing,
    OwnershipUnavailable,
    NotOwner,
    StaleHead,
    ReplacementUnavailable,
    Conflict
}

public sealed record LearningFeedbackPublicationChangeWriteResult(
    LearningFeedbackPublicationChangeWriteDisposition Disposition,
    string ChangeId,
    string TargetRecordId,
    string? HeadRevisionId,
    long? HeadGeneration,
    string? ReplacementRecordId,
    string? ReplacementRevisionId);

/// <summary>
/// Optional immutable publication-change persistence boundary. Implementations
/// must bind the accepted event to server-derived Submit access, verify exact
/// origin ownership, and perform the expected-head transition atomically.
/// </summary>
public interface ILearningFeedbackPublicationChangeRepository
{
    ValueTask<LearningFeedbackPublicationChangeWriteResult> ApplyPublicationChangeAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackPublicationChangeV1 change,
        CancellationToken cancellationToken);
}
