using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Learning;

public sealed record ChangeLearningFeedbackPublicationCommand(
    string Namespace,
    LearningFeedbackPublicationChangeRequestV1 Request,
    LearningFeedbackAuthorityContext ServerAuthority);

/// <summary>
/// Content-safe application result. The accepted event's reason and identity
/// stay in immutable storage and audit evidence rather than response metadata.
/// </summary>
public sealed record LearningFeedbackPublicationChangeResult(
    LearningFeedbackMutationDisposition Disposition,
    string? ChangeId,
    string? TargetRecordId,
    string? HeadRevisionId,
    long? HeadGeneration,
    string? ReplacementRecordId,
    string? ReplacementRevisionId,
    IReadOnlyList<PegasusDiagnostic> Diagnostics)
{
    public bool Succeeded =>
        Disposition is LearningFeedbackMutationDisposition.Accepted or
            LearningFeedbackMutationDisposition.Idempotent;
}

public interface ILearningFeedbackPublicationChangeService
{
    ValueTask<LearningFeedbackPublicationChangeResult> ChangeAsync(
        ChangeLearningFeedbackPublicationCommand command,
        CancellationToken cancellationToken);
}
