using System.Text.Json;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Core.Learning;

/// <summary>
/// Provider-neutral coordinates for retaining one private pending proposal.
/// The scope is supplied by an authenticated host; proposal content cannot
/// select its project, idempotency domain, producer, or retention lifetime.
/// </summary>
public sealed record RetainedLearningFeedbackProposalScope(
    string ProjectId,
    string IdempotencyKey,
    string ProducerId,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Adapts the existing learning-feedback application workflow to the generic
/// immutable-retention port. This intentionally supports proposal submission
/// exact readback, and bounded private head listing only: it cannot review,
/// publish, promote, or place a proposal into full-text search.
/// Use one instance per submission so its host-derived scope cannot be reused
/// for a different project or idempotency key.
/// </summary>
public sealed class RetainedLearningFeedbackProposalRepository :
    ILearningFeedbackRepository
{
    private readonly ICortexAgentRetentionRepository _retention;
    private readonly RetainedLearningFeedbackProposalScope _scope;

    public RetainedLearningFeedbackProposalRepository(
        ICortexAgentRetentionRepository retention,
        RetainedLearningFeedbackProposalScope scope)
    {
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _scope = (scope ?? throw new ArgumentNullException(nameof(scope))) with { };
        ValidateScope(_scope);
    }

    public async ValueTask<LearningFeedbackSnapshot?> FindAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        var retained = await _retention.FindRevisionAsync(
            _scope.ProjectId,
            CortexRetainedDocumentKinds.LearningFeedbackProposal,
            proposalId,
            revision: 1,
            cancellationToken).ConfigureAwait(false);
        return TryCapture(
                retained,
                access,
                expectedProposalId: proposalId,
                expectedProposalHash: null,
                requireSubmissionCoordinates: false,
                cancellationToken,
                out var snapshot)
            ? snapshot
            : null;
    }

    public async ValueTask<LearningFeedbackProposalWriteResult> AddProposalAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();

        if (!LearningFeedbackSnapshotBoundary.TryCapture(
                new LearningFeedbackSnapshot(proposal, Decision: null),
                out var captured,
                cancellationToken) ||
            !MatchesAccess(captured!.Proposal, access) ||
            captured.Proposal.CreatedAt >= _scope.ExpiresAt)
        {
            throw new ArgumentException(
                "The pending proposal does not match its authorized immutable-retention scope.",
                nameof(proposal));
        }

        var immutable = captured.Proposal;
        var canonical = CortexCanonicalJson.Serialize(immutable);
        if (canonical.Length > CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(proposal),
                "The pending proposal exceeds the local generic-retention byte boundary.");
        }

        var request = new CortexRetentionAppendRequest(
            _scope.ProjectId,
            CortexRetainedDocumentKinds.LearningFeedbackProposal,
            immutable.ProposalId,
            Revision: 1,
            ExpectedPreviousHash: null,
            _scope.IdempotencyKey,
            _scope.ProducerId,
            immutable.ContractId,
            canonical,
            CortexCanonicalJson.Sha256(canonical),
            // Pending proposals are exact-readback records, not knowledge
            // search results. An empty projection keeps them out of the
            // generic FTS path even when another caller can search the project.
            string.Empty,
            immutable.CreatedAt,
            _scope.ExpiresAt).Validate();
        var result = await _retention.AppendAsync(
            request,
            cancellationToken).ConfigureAwait(false);

        if (result.Disposition is CortexRetentionWriteDispositions.Conflict or
            CortexRetentionWriteDispositions.StaleBase)
        {
            var existing = result.Document;
            if (existing is null &&
                result.Disposition == CortexRetentionWriteDispositions.StaleBase)
            {
                existing = await _retention.FindRevisionAsync(
                    _scope.ProjectId,
                    CortexRetainedDocumentKinds.LearningFeedbackProposal,
                    immutable.ProposalId,
                    revision: 1,
                    cancellationToken).ConfigureAwait(false);
            }
            _ = TryCapture(
                existing,
                access,
                expectedProposalId: null,
                expectedProposalHash: null,
                requireSubmissionCoordinates: false,
                cancellationToken,
                out var conflict);
            return new LearningFeedbackProposalWriteResult(
                LearningFeedbackProposalWriteDisposition.Conflict,
                conflict?.Proposal);
        }

        if (result.Disposition is not (
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent))
        {
            throw new InvalidDataException(
                "The immutable-retention provider returned an unsupported proposal disposition.");
        }

        // Read through the repository after every write result. This verifies
        // restart-safe exact bytes instead of trusting a provider-owned return
        // object as proof that the proposal is durably readable.
        var readback = await _retention.FindRevisionAsync(
            _scope.ProjectId,
            CortexRetainedDocumentKinds.LearningFeedbackProposal,
            immutable.ProposalId,
            revision: 1,
            cancellationToken).ConfigureAwait(false);
        if (!TryCapture(
                readback,
                access,
                immutable.ProposalId,
                immutable.ProposalHash,
                requireSubmissionCoordinates: true,
                cancellationToken,
                out var stored))
        {
            throw new InvalidDataException(
                "The retained pending proposal failed exact readback validation.");
        }

        return new LearningFeedbackProposalWriteResult(
            result.Disposition == CortexRetentionWriteDispositions.Appended
                ? LearningFeedbackProposalWriteDisposition.Added
                : LearningFeedbackProposalWriteDisposition.Existing,
            stored!.Proposal);
    }

    public ValueTask<LearningFeedbackDecisionWriteResult>
        AddDecisionIfPendingAsync(
            LearningFeedbackNamespaceAccess access,
            LearningFeedbackReviewDecisionV1 decision,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Private local proposal retention cannot store review or publication decisions.");
    }

    public async ValueTask<LearningFeedbackRepositoryPage> ListAsync(
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        if (afterProposalId is not null &&
            (afterProposalId.Length >
                 CortexAgentRetentionLimits.MaximumIdentifierCharacters ||
             !afterProposalId.StartsWith("proposal_", StringComparison.Ordinal)) ||
            limit is < 1 or >
                CortexAgentRetentionLimits.MaximumKindHeadPageSize)
        {
            throw new ArgumentException(
                "The private proposal page request is invalid.");
        }

        var retainedPage = await _retention.ListHeadsByKindAsync(
            _scope.ProjectId,
            CortexRetainedDocumentKinds.LearningFeedbackProposal,
            afterProposalId,
            limit,
            cancellationToken).ConfigureAwait(false);
        var snapshots = new List<LearningFeedbackSnapshot>(
            retainedPage.Items.Count);
        foreach (var retained in retainedPage.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCapture(
                    retained,
                    access,
                    expectedProposalId: null,
                    expectedProposalHash: null,
                    requireSubmissionCoordinates: false,
                    cancellationToken,
                    out var snapshot))
            {
                throw new InvalidDataException(
                    "A retained private proposal head failed exact read validation.");
            }
            snapshots.Add(snapshot!);
        }
        return new LearningFeedbackRepositoryPage(
            Array.AsReadOnly(snapshots.ToArray()),
            retainedPage.NextAfterDocumentId);
    }

    public ValueTask<LearningFeedbackRepositoryStatus> GetStatusAsync(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Private local proposal retention exposes no review-status workflow.");
    }

    private bool TryCapture(
        CortexRetainedDocument? retained,
        LearningFeedbackNamespaceAccess access,
        string? expectedProposalId,
        string? expectedProposalHash,
        bool requireSubmissionCoordinates,
        CancellationToken cancellationToken,
        out LearningFeedbackSnapshot? snapshot)
    {
        snapshot = null;
        if (retained is null ||
            !string.Equals(retained.ProjectId, _scope.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(
                retained.Kind,
                CortexRetainedDocumentKinds.LearningFeedbackProposal,
                StringComparison.Ordinal) ||
            retained.Revision != 1 ||
            retained.PreviousHash is not null ||
            !LearningFeedbackContractIds.IsSupportedProposal(retained.ContractId) ||
            retained.CanonicalUtf8 is null ||
            !string.Equals(
                retained.ContentHash,
                CortexCanonicalJson.Sha256(retained.CanonicalUtf8),
                StringComparison.Ordinal) ||
            requireSubmissionCoordinates &&
            (!string.Equals(
                    retained.IdempotencyKey,
                    _scope.IdempotencyKey,
                    StringComparison.Ordinal) ||
             !string.Equals(
                    retained.ProducerId,
                    _scope.ProducerId,
                    StringComparison.Ordinal) ||
             retained.ExpiresAt != _scope.ExpiresAt))
        {
            return false;
        }

        try
        {
            var proposal =
                CortexCanonicalJson.Deserialize<LearningFeedbackProposalV1>(
                    retained.CanonicalUtf8);
            if (!CortexCanonicalJson.Serialize(proposal)
                    .AsSpan().SequenceEqual(retained.CanonicalUtf8) ||
                !string.Equals(
                    proposal.ProposalId,
                    retained.DocumentId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    proposal.ContractId,
                    retained.ContractId,
                    StringComparison.Ordinal) ||
                expectedProposalId is not null &&
                !string.Equals(
                    proposal.ProposalId,
                    expectedProposalId,
                    StringComparison.Ordinal) ||
                expectedProposalHash is not null &&
                !string.Equals(
                    proposal.ProposalHash,
                    expectedProposalHash,
                    StringComparison.Ordinal) ||
                !MatchesAccess(proposal, access) ||
                !LearningFeedbackSnapshotBoundary.TryCapture(
                    new LearningFeedbackSnapshot(proposal, Decision: null),
                    out var captured,
                    cancellationToken))
            {
                return false;
            }
            snapshot = captured;
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool MatchesAccess(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackNamespaceAccess access) =>
        string.Equals(proposal.TenantId, access.TenantId, StringComparison.Ordinal) &&
        string.Equals(proposal.Namespace, access.Namespace, StringComparison.Ordinal);

    private static void ValidateScope(
        RetainedLearningFeedbackProposalScope scope)
    {
        static bool ValidIdentifier(string? value) =>
            value is { Length: > 0 } &&
            value.Length <= CortexAgentRetentionLimits.MaximumIdentifierCharacters &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.Skip(1).All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or ':' or '-');

        if (!ValidIdentifier(scope.ProjectId) ||
            !ValidIdentifier(scope.IdempotencyKey) ||
            !ValidIdentifier(scope.ProducerId) ||
            scope.ExpiresAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The private pending-proposal retention scope is invalid.",
                nameof(scope));
        }
    }
}
