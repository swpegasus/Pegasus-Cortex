using Pegasus.Shared.Diagnostics;
using Pegasus.Shared.Outcomes;

namespace Pegasus.Cortex.Core.Outcomes;

/// <summary>
/// Server-owned caller state. None of these values may be accepted from an outcome payload.
/// </summary>
public sealed record OutcomeCallerContext(
    string TenantId,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    string ConnectionId,
    long PolicyRevision,
    IReadOnlyList<OutcomeNamespaceGrant> NamespaceGrants);

public sealed record OutcomeNamespaceGrant(
    string Namespace,
    bool CanWrite,
    bool CanProposePromotion = false,
    bool CanApprove = false,
    bool CanApplyPromotion = false);

public sealed record StoredOutcome(AgentOutcomeV1 Outcome, DateTimeOffset StoredAt);

public sealed record OutcomeRepositoryWriteResult(bool Accepted, PegasusDiagnostic? Diagnostic);

/// <summary>
/// The storage implementation must make the insert and supersession precondition checks atomic.
/// Existing rows are never updated by this contract.
/// </summary>
public interface IOutcomeRepository
{
    ValueTask<StoredOutcome?> FindAsync(
        string tenantId,
        string outcomeId,
        CancellationToken cancellationToken);

    ValueTask<OutcomeRepositoryWriteResult> AddImmutableAsync(
        AgentOutcomeV1 outcome,
        IReadOnlyList<string> expectedActiveSupersededOutcomeIds,
        CancellationToken cancellationToken);
}

public sealed record OutcomeIngestionResult(
    bool Accepted,
    AgentOutcomeV1? Outcome,
    PegasusDiagnostic? Diagnostic,
    string SubmittedPayloadHash)
{
    public static OutcomeIngestionResult Reject(PegasusDiagnostic diagnostic, string payloadHash) =>
        new(false, null, diagnostic, payloadHash);
}

public sealed class OutcomeIngestionService(IOutcomeRepository repository, TimeProvider timeProvider)
{
    public async ValueTask<OutcomeIngestionResult> IngestAsync(
        AgentOutcomeV1 submitted,
        OutcomeCallerContext caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var payloadHash = AgentOutcomePolicy.ComputeSubmittedPayloadHash(submitted);
        var validation = AgentOutcomePolicy.ValidateSubmission(submitted, caller, timeProvider.GetUtcNow());
        if (validation is not null)
        {
            return OutcomeIngestionResult.Reject(validation, payloadHash);
        }

        var grant = caller.NamespaceGrants.Single(g =>
            string.Equals(g.Namespace, submitted.Namespace, StringComparison.Ordinal));

        foreach (var supersededId in submitted.Supersession.Supersedes)
        {
            var existing = await repository.FindAsync(
                caller.TenantId,
                supersededId,
                cancellationToken).ConfigureAwait(false);

            var diagnostic = ValidateSupersession(existing, submitted, caller, supersededId);
            if (diagnostic is not null)
            {
                return OutcomeIngestionResult.Reject(diagnostic, payloadHash);
            }
        }

        var effective = AgentOutcomePolicy.NormalizeAcceptedOutcome(
            submitted,
            caller,
            grant.Namespace,
            timeProvider.GetUtcNow());

        var write = await repository.AddImmutableAsync(
            effective,
            effective.Supersession.Supersedes,
            cancellationToken).ConfigureAwait(false);

        return write.Accepted
            ? new OutcomeIngestionResult(true, effective, null, payloadHash)
            : OutcomeIngestionResult.Reject(
                write.Diagnostic ?? AgentOutcomePolicy.Error(
                    "outcome_storage_rejected",
                    "The immutable outcome repository rejected the write.",
                    effective.OutcomeId),
                payloadHash);
    }

    private static PegasusDiagnostic? ValidateSupersession(
        StoredOutcome? existing,
        AgentOutcomeV1 submitted,
        OutcomeCallerContext caller,
        string supersededId)
    {
        if (existing is null)
        {
            return AgentOutcomePolicy.Error(
                "outcome_superseded_record_missing",
                "A referenced superseded outcome does not exist.",
                submitted.OutcomeId,
                supersededId);
        }

        if (!string.Equals(existing.Outcome.TenantId, caller.TenantId, StringComparison.Ordinal) ||
            !string.Equals(existing.Outcome.Namespace, submitted.Namespace, StringComparison.Ordinal))
        {
            return AgentOutcomePolicy.Error(
                "outcome_supersession_scope_mismatch",
                "Supersession cannot cross tenant or namespace boundaries.",
                submitted.OutcomeId,
                supersededId);
        }

        return string.Equals(existing.Outcome.RecordStatus, "active", StringComparison.Ordinal)
            ? null
            : AgentOutcomePolicy.Error(
                "outcome_supersession_conflict",
                "Only an active outcome may be superseded.",
                submitted.OutcomeId,
                supersededId);
    }
}
