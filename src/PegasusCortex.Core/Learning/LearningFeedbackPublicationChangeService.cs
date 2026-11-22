using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Learning;

/// <summary>
/// Authorizes and canonicalizes author-owned publication changes before
/// handing the accepted immutable event to a provider-specific CAS store.
/// </summary>
public sealed class LearningFeedbackPublicationChangeService :
    ILearningFeedbackPublicationChangeService
{
    private const string Operation = "cortex.learning-feedback.publication-change";

    private readonly ILearningFeedbackPublicationChangeRepository _repository;
    private readonly TimeProvider _timeProvider;

    public LearningFeedbackPublicationChangeService(
        ILearningFeedbackPublicationChangeRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<LearningFeedbackPublicationChangeResult> ChangeAsync(
        ChangeLearningFeedbackPublicationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);
        ArgumentNullException.ThrowIfNull(command.ServerAuthority);
        cancellationToken.ThrowIfCancellationRequested();

        var accessResult = LearningFeedbackPolicy.AuthorizeNamespace(
            command.ServerAuthority,
            command.Namespace,
            LearningFeedbackNamespaceCapability.Submit,
            cancellationToken);
        if (!accessResult.Accepted)
            return Rejected(null, command.Request.TargetRecordId, null, null, accessResult.Diagnostics);

        var access = accessResult.Value!;
        var acceptedResult = LearningFeedbackPublicationChangePolicy.Accept(
            command.Request,
            access,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        if (!acceptedResult.Accepted)
        {
            return Rejected(
                null,
                command.Request.TargetRecordId,
                command.Request.Replacement?.RecordId,
                command.Request.Replacement?.RevisionId,
                acceptedResult.Diagnostics);
        }

        var accepted = acceptedResult.Value!;
        try
        {
            var stored = await _repository.ApplyPublicationChangeAsync(
                access,
                accepted,
                cancellationToken).ConfigureAwait(false);
            if (!ValidRepositoryResult(accepted, stored))
            {
                return Rejected(
                    accepted.ChangeId,
                    accepted.TargetRecordId,
                    accepted.Replacement?.RecordId,
                    accepted.Replacement?.RevisionId,
                    [Error(
                        "learning_feedback_publication_change_repository_result_invalid",
                        "The publication-change repository returned an invalid bounded result.",
                        accepted,
                        retryable: false)]);
            }

            return stored.Disposition switch
            {
                LearningFeedbackPublicationChangeWriteDisposition.Applied =>
                    Project(LearningFeedbackMutationDisposition.Accepted, stored, []),
                LearningFeedbackPublicationChangeWriteDisposition.Existing =>
                    Project(LearningFeedbackMutationDisposition.Idempotent, stored, []),
                LearningFeedbackPublicationChangeWriteDisposition.StaleHead =>
                    Project(
                        LearningFeedbackMutationDisposition.Conflict,
                        stored,
                        [Error(
                            "learning_feedback_publication_change_head_stale",
                            "The publication head no longer matches the expected revision and generation.",
                            accepted,
                            retryable: false)]),
                LearningFeedbackPublicationChangeWriteDisposition.ReplacementUnavailable =>
                    Project(
                        LearningFeedbackMutationDisposition.Rejected,
                        stored,
                        [Error(
                            "learning_feedback_publication_change_replacement_unavailable",
                            "The same-namespace replacement is unavailable or is not its active head.",
                            accepted,
                            retryable: false)]),
                LearningFeedbackPublicationChangeWriteDisposition.OwnershipUnavailable or
                LearningFeedbackPublicationChangeWriteDisposition.NotOwner =>
                    Project(
                        LearningFeedbackMutationDisposition.Rejected,
                        stored,
                        [Error(
                            "learning_feedback_publication_change_owner_rejected",
                            "The publication cannot be changed by this authority.",
                            accepted,
                            retryable: false)]),
                LearningFeedbackPublicationChangeWriteDisposition.Conflict =>
                    Project(
                        LearningFeedbackMutationDisposition.Conflict,
                        stored,
                        [Error(
                            "learning_feedback_publication_change_conflict",
                            "The immutable publication-change identity conflicts with stored evidence.",
                            accepted,
                            retryable: false)]),
                _ => Rejected(
                    accepted.ChangeId,
                    accepted.TargetRecordId,
                    accepted.Replacement?.RecordId,
                    accepted.Replacement?.RevisionId,
                    [Error(
                        "learning_feedback_publication_change_repository_result_invalid",
                        "The publication-change repository returned an unsupported disposition.",
                        accepted,
                        retryable: false)])
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Rejected(
                accepted.ChangeId,
                accepted.TargetRecordId,
                accepted.Replacement?.RecordId,
                accepted.Replacement?.RevisionId,
                [Error(
                    "learning_feedback_publication_change_repository_failure",
                    "The publication-change repository failed before it could confirm a durable result.",
                    accepted,
                    retryable: true,
                    exception.GetType().FullName)]);
        }
    }

    private static bool ValidRepositoryResult(
        LearningFeedbackPublicationChangeV1 accepted,
        LearningFeedbackPublicationChangeWriteResult stored)
    {
        if (stored is null ||
            !string.Equals(stored.ChangeId, accepted.ChangeId, StringComparison.Ordinal) ||
            !string.Equals(stored.TargetRecordId, accepted.TargetRecordId, StringComparison.Ordinal) ||
            !string.Equals(
                stored.ReplacementRecordId,
                accepted.Replacement?.RecordId,
                StringComparison.Ordinal) ||
            !string.Equals(
                stored.ReplacementRevisionId,
                accepted.Replacement?.RevisionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        return stored.Disposition switch
        {
            LearningFeedbackPublicationChangeWriteDisposition.Applied or
            LearningFeedbackPublicationChangeWriteDisposition.Existing =>
                string.Equals(
                    stored.HeadRevisionId,
                    $"krev_{accepted.ChangeHash}",
                    StringComparison.Ordinal) &&
                stored.HeadGeneration == accepted.ExpectedHeadGeneration + 1,
            LearningFeedbackPublicationChangeWriteDisposition.StaleHead =>
                stored.HeadRevisionId is null && stored.HeadGeneration is null ||
                ExactRevisionId(stored.HeadRevisionId) && stored.HeadGeneration is > 0,
            LearningFeedbackPublicationChangeWriteDisposition.OwnershipUnavailable or
            LearningFeedbackPublicationChangeWriteDisposition.NotOwner or
            LearningFeedbackPublicationChangeWriteDisposition.Conflict =>
                stored.HeadRevisionId is null && stored.HeadGeneration is null,
            LearningFeedbackPublicationChangeWriteDisposition.ReplacementUnavailable =>
                ExactRevisionId(stored.HeadRevisionId) && stored.HeadGeneration is > 0,
            _ => false
        };
    }

    private static bool ExactRevisionId(string? value) =>
        value is { Length: 69 } &&
        value.StartsWith("krev_", StringComparison.Ordinal) &&
        value.Skip(5).All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static LearningFeedbackPublicationChangeResult Project(
        LearningFeedbackMutationDisposition disposition,
        LearningFeedbackPublicationChangeWriteResult stored,
        IReadOnlyList<PegasusDiagnostic> diagnostics) =>
        new(
            disposition,
            stored.ChangeId,
            stored.TargetRecordId,
            stored.HeadRevisionId,
            stored.HeadGeneration,
            stored.ReplacementRecordId,
            stored.ReplacementRevisionId,
            diagnostics);

    private static LearningFeedbackPublicationChangeResult Rejected(
        string? changeId,
        string? targetRecordId,
        string? replacementRecordId,
        string? replacementRevisionId,
        IReadOnlyList<PegasusDiagnostic> diagnostics) =>
        new(
            LearningFeedbackMutationDisposition.Rejected,
            changeId,
            ExactRecordId(targetRecordId) ? targetRecordId : null,
            null,
            null,
            ExactRecordId(replacementRecordId) ? replacementRecordId : null,
            ExactRevisionId(replacementRevisionId) ? replacementRevisionId : null,
            diagnostics);

    private static bool ExactRecordId(string? value) =>
        value is { Length: 69 } &&
        value.StartsWith("krec_", StringComparison.Ordinal) &&
        value.Skip(5).All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static PegasusDiagnostic Error(
        string code,
        string message,
        LearningFeedbackPublicationChangeV1 accepted,
        bool retryable,
        string? causeType = null) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            Operation,
            retryable,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["changeId"] = accepted.ChangeId,
                ["targetRecordId"] = accepted.TargetRecordId,
                ["replacementRecordId"] = accepted.Replacement?.RecordId,
                ["replacementRevisionId"] = accepted.Replacement?.RevisionId,
                ["causeType"] = causeType
            });
}
