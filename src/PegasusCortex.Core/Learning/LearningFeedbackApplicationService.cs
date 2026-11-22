using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Core.Learning;

public sealed class LearningFeedbackApplicationService : ILearningFeedbackApplicationService
{
    private readonly ILearningFeedbackRepository _repository;
    private readonly TimeProvider _timeProvider;

    public LearningFeedbackApplicationService(
        ILearningFeedbackRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<LearningFeedbackSubmissionResult> SubmitAsync(
        SubmitLearningFeedbackCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Proposal);
        ArgumentNullException.ThrowIfNull(command.ServerAuthority);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            command.ServerAuthority,
            command.Proposal.Namespace,
            LearningFeedbackNamespaceCapability.Submit,
            cancellationToken);
        if (!access.Accepted)
            return RejectedSubmission(null, access.Diagnostics);
        var authorizedAccess = access.Value!;

        var accepted = LearningFeedbackPolicy.AcceptProposal(
            command.Proposal,
            command.ServerAuthority,
            now,
            cancellationToken);
        if (!accepted.Accepted)
            return RejectedSubmission(accepted.SubmittedPayloadHash, accepted.Diagnostics);

        var proposal = accepted.Value!;
        try
        {
            var write = await _repository.AddProposalAsync(
                authorizedAccess,
                proposal,
                cancellationToken).ConfigureAwait(false);
            if (write.Disposition is LearningFeedbackProposalWriteDisposition.Added or
                LearningFeedbackProposalWriteDisposition.Existing)
            {
                if (!TryCaptureStoredSnapshot(
                        write.Proposal is null ? null : new LearningFeedbackSnapshot(write.Proposal, null),
                        authorizedAccess,
                        proposal.ProposalId,
                        proposal.ProposalHash,
                        null,
                        null,
                        cancellationToken,
                        out var stored))
                {
                    return InvalidSubmissionRepositoryResult(
                        proposal,
                        accepted.SubmittedPayloadHash,
                        write.Disposition);
                }

                var disposition = write.Disposition == LearningFeedbackProposalWriteDisposition.Added
                    ? LearningFeedbackMutationDisposition.Accepted
                    : LearningFeedbackMutationDisposition.Idempotent;
                return SuccessfulSubmission(
                    disposition,
                    stored!.Proposal,
                    accepted.SubmittedPayloadHash!,
                    authorizedAccess,
                    now);
            }

            if (write.Disposition == LearningFeedbackProposalWriteDisposition.Conflict)
            {
                return ConflictingSubmission(
                    proposal,
                    write.Proposal,
                    accepted.SubmittedPayloadHash!,
                    authorizedAccess,
                    now);
            }

            return InvalidSubmissionRepositoryResult(
                proposal,
                accepted.SubmittedPayloadHash,
                write.Disposition);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RejectedSubmission(
                accepted.SubmittedPayloadHash,
                [RepositoryError(
                    "learning_feedback_repository_failure",
                    "The proposal repository failed while storing immutable feedback.",
                    "cortex.learning-feedback.submit",
                    proposal.Namespace,
                    proposal.ProposalId,
                    null,
                    proposal.ProposalHash,
                    null,
                    exception.GetType().FullName,
                    retryable: true)]);
        }
    }

    public async ValueTask<LearningFeedbackReviewResult> ReviewAsync(
        ReviewLearningFeedbackCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Decision);
        ArgumentNullException.ThrowIfNull(command.ServerAuthority);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            command.ServerAuthority,
            command.Decision.Namespace,
            LearningFeedbackNamespaceCapability.Review,
            cancellationToken);
        if (!access.Accepted)
            return RejectedReview(null, access.Diagnostics);
        var authorizedAccess = access.Value!;

        if (!ValidReviewLookup(command.Decision, authorizedAccess))
        {
            return RejectedReview(
                null,
                [RepositoryError(
                    "learning_feedback_review_lookup_invalid",
                    "The review lookup does not match server-derived authority or bounded identifiers.",
                    "cortex.learning-feedback.review.authorize",
                    authorizedAccess.Namespace,
                    command.Decision.ProposalId,
                    command.Decision.DecisionId,
                    command.Decision.ProposalHash,
                    null)]);
        }

        LearningFeedbackSnapshot? stored;
        try
        {
            var repositoryValue = await _repository.FindAsync(
                authorizedAccess,
                command.Decision.ProposalId,
                cancellationToken).ConfigureAwait(false);

            if (repositoryValue is null)
            {
                return RejectedReview(
                    null,
                    [RepositoryError(
                        "learning_feedback_proposal_missing",
                        "The proposal does not exist in the authorized namespace.",
                        "cortex.learning-feedback.review",
                        authorizedAccess.Namespace,
                        command.Decision.ProposalId,
                        command.Decision.DecisionId,
                        command.Decision.ProposalHash,
                        null)]);
            }

            if (!TryCaptureStoredSnapshot(
                    repositoryValue,
                    authorizedAccess,
                    command.Decision.ProposalId,
                    null,
                    null,
                    null,
                    cancellationToken,
                    out stored))
            {
                return RejectedReview(
                    null,
                    [RepositoryError(
                        "learning_feedback_repository_integrity_invalid",
                        "The proposal repository returned an invalid or cross-scope record.",
                        "cortex.learning-feedback.review",
                        authorizedAccess.Namespace,
                        command.Decision.ProposalId,
                        command.Decision.DecisionId,
                        command.Decision.ProposalHash,
                        null)]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RejectedReview(
                null,
                [RepositoryError(
                    "learning_feedback_repository_failure",
                    "The proposal repository failed while locating feedback for review.",
                    "cortex.learning-feedback.review.find",
                    authorizedAccess.Namespace,
                    command.Decision.ProposalId,
                    command.Decision.DecisionId,
                    command.Decision.ProposalHash,
                    null,
                    exception.GetType().FullName,
                    retryable: true)]);
        }

        var storedProposal = stored!.Proposal;
        var accepted = LearningFeedbackPolicy.AcceptReviewDecision(
            command.Decision,
            storedProposal,
            command.ServerAuthority,
            now,
            cancellationToken);
        if (!accepted.Accepted)
            return RejectedReview(accepted.SubmittedPayloadHash, accepted.Diagnostics);

        var decision = accepted.Value!;
        try
        {
            var write = await _repository.AddDecisionIfPendingAsync(
                authorizedAccess,
                decision,
                cancellationToken).ConfigureAwait(false);
            if (write.Disposition is LearningFeedbackDecisionWriteDisposition.Added or
                LearningFeedbackDecisionWriteDisposition.Existing)
            {
                if (!TryCaptureStoredSnapshot(
                        write.Snapshot,
                        authorizedAccess,
                        decision.ProposalId,
                        decision.ProposalHash,
                        decision.DecisionId,
                        decision.DecisionHash,
                        cancellationToken,
                        out var reviewed))
                {
                    return InvalidReviewRepositoryResult(
                        decision,
                        accepted.SubmittedPayloadHash,
                        write.Disposition);
                }

                var disposition = write.Disposition == LearningFeedbackDecisionWriteDisposition.Added
                    ? LearningFeedbackMutationDisposition.Accepted
                    : LearningFeedbackMutationDisposition.Idempotent;
                return SuccessfulReview(
                    disposition,
                    reviewed!,
                    accepted.SubmittedPayloadHash!,
                    authorizedAccess,
                    now);
            }

            return write.Disposition switch
            {
                LearningFeedbackDecisionWriteDisposition.AlreadyReviewed =>
                    ConflictingReview(
                        "learning_feedback_already_reviewed",
                        "The proposal already has an immutable review decision.",
                        storedProposal,
                        decision,
                        accepted.SubmittedPayloadHash!,
                        authorizedAccess,
                        now,
                        write.Disposition),
                LearningFeedbackDecisionWriteDisposition.ProposalHashConflict =>
                    ConflictingReview(
                        "learning_feedback_proposal_hash_conflict",
                        "The proposal hash changed before the review decision was stored.",
                        storedProposal,
                        decision,
                        accepted.SubmittedPayloadHash!,
                        authorizedAccess,
                        now,
                        write.Disposition),
                LearningFeedbackDecisionWriteDisposition.MissingProposal =>
                    ConflictingReview(
                        "learning_feedback_proposal_missing",
                        "The proposal no longer exists in the authorized namespace.",
                        storedProposal,
                        decision,
                        accepted.SubmittedPayloadHash!,
                        authorizedAccess,
                        now,
                        write.Disposition),
                _ => InvalidReviewRepositoryResult(
                    decision,
                    accepted.SubmittedPayloadHash,
                    write.Disposition)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RejectedReview(
                accepted.SubmittedPayloadHash,
                [RepositoryError(
                    "learning_feedback_repository_failure",
                    "The proposal repository failed while storing the immutable review decision.",
                    "cortex.learning-feedback.review",
                    decision.Namespace,
                    decision.ProposalId,
                    decision.DecisionId,
                    decision.ProposalHash,
                    decision.DecisionHash,
                    exception.GetType().FullName,
                    retryable: true)]);
        }
    }

    public async ValueTask<LearningFeedbackPageResult> ListAsync(
        ListLearningFeedbackQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.ServerAuthority);
        cancellationToken.ThrowIfCancellationRequested();

        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            query.ServerAuthority,
            query.Namespace,
            query.Capability,
            cancellationToken);
        if (!access.Accepted)
            return new LearningFeedbackPageResult(false, null, access.Diagnostics);
        var authorizedAccess = access.Value!;

        if (query.Limit is < 1 or > LearningFeedbackApplicationLimits.MaximumPageSize ||
            !ValidContinuation(query.AfterProposalId))
        {
            return new LearningFeedbackPageResult(
                false,
                null,
                [RepositoryError(
                    "learning_feedback_page_request_invalid",
                    "The page limit or continuation proposal identifier is invalid.",
                    "cortex.learning-feedback.list",
                    authorizedAccess.Namespace,
                    query.AfterProposalId,
                    null,
                    null,
                    null)]);
        }

        try
        {
            var page = await _repository.ListAsync(
                authorizedAccess,
                query.AfterProposalId,
                query.Limit,
                cancellationToken).ConfigureAwait(false);
            if (!TryCaptureRepositoryPage(
                    page,
                    authorizedAccess,
                    query.AfterProposalId,
                    query.Limit,
                    cancellationToken,
                    out var capturedPage))
            {
                return new LearningFeedbackPageResult(
                    false,
                    null,
                    [RepositoryError(
                        "learning_feedback_repository_page_invalid",
                        "The proposal repository returned an invalid, unbounded, or cross-scope page.",
                        "cortex.learning-feedback.list",
                        authorizedAccess.Namespace,
                        query.AfterProposalId,
                        null,
                        null,
                        null)]);
            }

            return new LearningFeedbackPageResult(
                true,
                capturedPage,
                []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LearningFeedbackPageResult(
                false,
                null,
                [RepositoryError(
                    "learning_feedback_repository_failure",
                    "The proposal repository failed while listing feedback.",
                    "cortex.learning-feedback.list",
                    authorizedAccess.Namespace,
                    query.AfterProposalId,
                    null,
                    null,
                    null,
                    exception.GetType().FullName,
                    retryable: true)]);
        }
    }

    public async ValueTask<LearningFeedbackStatusResult> GetStatusAsync(
        GetLearningFeedbackStatusQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.ServerAuthority);
        cancellationToken.ThrowIfCancellationRequested();

        var access = LearningFeedbackPolicy.AuthorizeNamespace(
            query.ServerAuthority,
            query.Namespace,
            LearningFeedbackNamespaceCapability.Read,
            cancellationToken);
        if (!access.Accepted)
            return new LearningFeedbackStatusResult(false, null, access.Diagnostics);
        var authorizedAccess = access.Value!;

        try
        {
            var status = await _repository.GetStatusAsync(
                authorizedAccess,
                cancellationToken).ConfigureAwait(false);
            if (!ValidStatus(status))
            {
                return new LearningFeedbackStatusResult(
                    false,
                    null,
                    [RepositoryError(
                        "learning_feedback_repository_status_invalid",
                        "The proposal repository returned an inconsistent status projection.",
                        "cortex.learning-feedback.status",
                        authorizedAccess.Namespace,
                        null,
                        null,
                        null,
                        null)]);
            }

            return new LearningFeedbackStatusResult(
                true,
                new LearningFeedbackStatusProjection(
                    authorizedAccess.TenantId,
                    authorizedAccess.Namespace,
                    status.Total,
                    status.Pending,
                    status.Approved,
                    status.Rejected),
                []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LearningFeedbackStatusResult(
                false,
                null,
                [RepositoryError(
                    "learning_feedback_repository_failure",
                    "The proposal repository failed while reading feedback status.",
                    "cortex.learning-feedback.status",
                    authorizedAccess.Namespace,
                    null,
                    null,
                    null,
                    null,
                    exception.GetType().FullName,
                    retryable: true)]);
        }
    }

    private static LearningFeedbackSubmissionResult SuccessfulSubmission(
        LearningFeedbackMutationDisposition disposition,
        LearningFeedbackProposalV1 proposal,
        string submittedPayloadHash,
        LearningFeedbackNamespaceAccess access,
        DateTimeOffset now) =>
        new(
            disposition,
            proposal,
            submittedPayloadHash,
            Audit("submit", disposition, access, proposal, null, now),
            []);

    private static LearningFeedbackSubmissionResult ConflictingSubmission(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackProposalV1? existing,
        string submittedPayloadHash,
        LearningFeedbackNamespaceAccess access,
        DateTimeOffset now) =>
        new(
            LearningFeedbackMutationDisposition.Conflict,
            null,
            submittedPayloadHash,
            Audit("submit", LearningFeedbackMutationDisposition.Conflict, access, proposal, null, now),
            [RepositoryError(
                "learning_feedback_proposal_id_conflict",
                "The proposal identifier already exists with a different canonical hash.",
                "cortex.learning-feedback.submit",
                proposal.Namespace,
                proposal.ProposalId,
                null,
                proposal.ProposalHash,
                null,
                cause: LearningFeedbackProposalWriteDisposition.Conflict.ToString(),
                existingProposalHash: existing?.ProposalHash)]);

    private static LearningFeedbackSubmissionResult InvalidSubmissionRepositoryResult(
        LearningFeedbackProposalV1 proposal,
        string? submittedPayloadHash,
        LearningFeedbackProposalWriteDisposition disposition) =>
        RejectedSubmission(
            submittedPayloadHash,
            [RepositoryError(
                "learning_feedback_repository_result_invalid",
                "The proposal repository returned an invalid, mutable, or cross-scope write result.",
                "cortex.learning-feedback.submit",
                proposal.Namespace,
                proposal.ProposalId,
                null,
                proposal.ProposalHash,
                null,
                disposition.ToString())]);

    private static LearningFeedbackSubmissionResult RejectedSubmission(
        string? submittedPayloadHash,
        IReadOnlyList<PegasusDiagnostic> diagnostics) =>
        new(LearningFeedbackMutationDisposition.Rejected, null, submittedPayloadHash, null, diagnostics);

    private static LearningFeedbackReviewResult SuccessfulReview(
        LearningFeedbackMutationDisposition disposition,
        LearningFeedbackSnapshot snapshot,
        string submittedPayloadHash,
        LearningFeedbackNamespaceAccess access,
        DateTimeOffset now) =>
        new(
            disposition,
            snapshot.Decision,
            submittedPayloadHash,
            Audit("review", disposition, access, snapshot.Proposal, snapshot.Decision, now),
            []);

    private static LearningFeedbackReviewResult ConflictingReview(
        string code,
        string message,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackReviewDecisionV1 decision,
        string submittedPayloadHash,
        LearningFeedbackNamespaceAccess access,
        DateTimeOffset now,
        LearningFeedbackDecisionWriteDisposition repositoryDisposition) =>
        new(
            LearningFeedbackMutationDisposition.Conflict,
            null,
            submittedPayloadHash,
            Audit(
                "review",
                LearningFeedbackMutationDisposition.Conflict,
                access,
                proposal,
                decision,
                now),
            [RepositoryError(
                code,
                message,
                "cortex.learning-feedback.review",
                decision.Namespace,
                decision.ProposalId,
                decision.DecisionId,
                decision.ProposalHash,
                decision.DecisionHash,
                repositoryDisposition.ToString())]);

    private static LearningFeedbackReviewResult InvalidReviewRepositoryResult(
        LearningFeedbackReviewDecisionV1 decision,
        string? submittedPayloadHash,
        LearningFeedbackDecisionWriteDisposition disposition) =>
        RejectedReview(
            submittedPayloadHash,
            [RepositoryError(
                "learning_feedback_repository_result_invalid",
                "The proposal repository returned an invalid, mutable, or cross-scope review result.",
                "cortex.learning-feedback.review",
                decision.Namespace,
                decision.ProposalId,
                decision.DecisionId,
                decision.ProposalHash,
                decision.DecisionHash,
                disposition.ToString())]);

    private static LearningFeedbackReviewResult RejectedReview(
        string? submittedPayloadHash,
        IReadOnlyList<PegasusDiagnostic> diagnostics) =>
        new(LearningFeedbackMutationDisposition.Rejected, null, submittedPayloadHash, null, diagnostics);

    private static LearningFeedbackAuditMetadata Audit(
        string operation,
        LearningFeedbackMutationDisposition disposition,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackReviewDecisionV1? decision,
        DateTimeOffset now) =>
        new(
            operation,
            disposition,
            access.TenantId,
            access.Namespace,
            access.AuthorityId,
            access.SubjectId,
            access.ServiceId,
            access.PolicyRevision,
            proposal.ProposalId,
            proposal.ProposalHash,
            decision?.DecisionId,
            decision?.DecisionHash,
            proposal.Source.CorrelationId,
            now.ToUniversalTime());

    private static bool TryCaptureRepositoryPage(
        LearningFeedbackRepositoryPage? page,
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken,
        out LearningFeedbackPage? capturedPage)
    {
        capturedPage = null;
        if (page?.Items is null || page.Items.Count > limit || !ValidContinuation(page.NextAfterProposalId))
            return false;

        string? previous = afterProposalId;
        var captured = new List<LearningFeedbackSnapshot>(page.Items.Count);
        foreach (var item in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (captured.Count == limit)
                return false;
            if (!TryCaptureStoredSnapshot(
                    item,
                    access,
                    null,
                    null,
                    null,
                    null,
                    cancellationToken,
                    out var snapshot) ||
                previous is not null && string.CompareOrdinal(snapshot!.Proposal.ProposalId, previous) <= 0)
            {
                return false;
            }
            captured.Add(snapshot!);
            previous = snapshot!.Proposal.ProposalId;
        }

        if (page.NextAfterProposalId is not null &&
            (captured.Count == 0 ||
             !string.Equals(page.NextAfterProposalId, captured[^1].Proposal.ProposalId, StringComparison.Ordinal)))
        {
            return false;
        }

        capturedPage = new LearningFeedbackPage(
            Array.AsReadOnly(captured.ToArray()),
            page.NextAfterProposalId);
        return true;
    }

    private static bool TryCaptureStoredSnapshot(
        LearningFeedbackSnapshot? snapshot,
        LearningFeedbackNamespaceAccess access,
        string? expectedProposalId,
        string? expectedProposalHash,
        string? expectedDecisionId,
        string? expectedDecisionHash,
        CancellationToken cancellationToken,
        out LearningFeedbackSnapshot? captured)
    {
        captured = null;
        if (!LearningFeedbackSnapshotBoundary.TryCapture(snapshot, out var immutable, cancellationToken) ||
            !string.Equals(immutable!.Proposal.TenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(immutable.Proposal.Namespace, access.Namespace, StringComparison.Ordinal) ||
            expectedProposalId is not null &&
            !string.Equals(immutable.Proposal.ProposalId, expectedProposalId, StringComparison.Ordinal) ||
            expectedProposalHash is not null &&
            !string.Equals(immutable.Proposal.ProposalHash, expectedProposalHash, StringComparison.Ordinal) ||
            expectedDecisionId is not null &&
            !string.Equals(immutable.Decision?.DecisionId, expectedDecisionId, StringComparison.Ordinal) ||
            expectedDecisionHash is not null &&
            !string.Equals(immutable.Decision?.DecisionHash, expectedDecisionHash, StringComparison.Ordinal))
        {
            return false;
        }

        captured = immutable;
        return true;
    }

    private static bool ValidContinuation(string? proposalId) =>
        proposalId is null ||
        proposalId.Length is > 0 and <= LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(proposalId[0]) &&
        proposalId.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidReviewLookup(
        LearningFeedbackReviewDecisionV1 decision,
        LearningFeedbackNamespaceAccess access) =>
        ValidRequiredIdentifier(decision.ProposalId) &&
        ValidRequiredIdentifier(decision.DecisionId) &&
        SafeHash(decision.ProposalHash) is not null &&
        string.Equals(decision.TenantId, access.TenantId, StringComparison.Ordinal) &&
        string.Equals(decision.Namespace, access.Namespace, StringComparison.Ordinal) &&
        string.Equals(decision.AuthorityId, access.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(decision.ReviewerSubjectId, access.SubjectId, StringComparison.Ordinal) &&
        decision.PolicyRevision == access.PolicyRevision;

    private static bool ValidRequiredIdentifier(string? value) =>
        value is not null && ValidContinuation(value);

    private static bool ValidStatus(LearningFeedbackRepositoryStatus status)
    {
        if (status.Total < 0 || status.Pending < 0 || status.Approved < 0 || status.Rejected < 0 ||
            status.Pending > status.Total || status.Approved > status.Total || status.Rejected > status.Total)
        {
            return false;
        }

        try
        {
            return status.Total == checked(status.Pending + status.Approved + status.Rejected);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static PegasusDiagnostic RepositoryError(
        string code,
        string message,
        string operation,
        string? namespaceName,
        string? proposalId,
        string? decisionId,
        string? proposalHash,
        string? decisionHash,
        string? cause = null,
        bool retryable = false,
        string? existingProposalHash = null) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            operation,
            retryable,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["namespace"] = SafeNamespace(namespaceName),
                ["proposalId"] = SafeIdentifier(proposalId),
                ["decisionId"] = SafeIdentifier(decisionId),
                ["proposalHash"] = SafeHash(proposalHash),
                ["decisionHash"] = SafeHash(decisionHash),
                ["existingProposalHash"] = SafeHash(existingProposalHash)
            },
            cause);

    private static string? SafeNamespace(string? value) =>
        value is { Length: > 0 and <= LearningFeedbackContractLimits.MaximumNamespaceCharacters }
            ? value
            : null;

    private static string? SafeIdentifier(string? value) =>
        value is { Length: > 0 and <= LearningFeedbackContractLimits.MaximumIdentifierCharacters }
            ? value
            : null;

    private static string? SafeHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : null;
}
