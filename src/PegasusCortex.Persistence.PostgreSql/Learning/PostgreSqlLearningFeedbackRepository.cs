using System.Data;
using System.Data.Common;
using System.Text;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Learning;

/// <summary>
/// PostgreSQL implementation of the immutable learning-feedback repository. Caller authority
/// is accepted only through a server-derived namespace access and is bound to every transaction.
/// </summary>
public sealed partial class PostgreSqlLearningFeedbackRepository :
    ILearningFeedbackRepository,
    ILearningFeedbackAuditRepository,
    ILearningFeedbackPublicationChangeRepository
{
    private const string ProposalEntityKind = "learning-proposal";
    private const string DecisionEntityKind = "learning-decision";
    private const string SubmitOperation = "cortex.learning-feedback.submit";
    private const string ReviewOperation = "cortex.learning-feedback.review";
    private const string ReviewFindOperation = "cortex.learning-feedback.review.find";
    private const string ReadFindOperation = "cortex.learning-feedback.read.find";
    private const string ListOperation = "cortex.learning-feedback.list";
    private const string StatusOperation = "cortex.learning-feedback.status";
    private const string KnowledgeEntityKind = "knowledge-revision";
    private const string RetentionMarker = "retentionRecord";

    private const string ProposalColumns = """
        p.tenant_id, p.namespace_name, p.proposal_id, p.proposal_hash,
        CASE WHEN octet_length(p.payload) BETWEEN 1 AND 131072
             THEN p.payload ELSE NULL END AS payload,
        p.payload_sha256, p.auth_version, p.auth_algorithm,
        p.auth_key_id, p.auth_signature, p.policy_revision, p.created_at
        """;

    private const string DecisionColumns = """
        d.tenant_id, d.namespace_name, d.proposal_id, d.decision_id,
        d.proposal_hash, d.decision_hash, d.decision_state,
        CASE WHEN octet_length(d.payload) BETWEEN 1 AND 131072
             THEN d.payload ELSE NULL END AS payload,
        d.payload_sha256, d.auth_version, d.auth_algorithm, d.auth_key_id,
        d.auth_signature, d.policy_revision, d.created_at
        """;

    private const string InsertProposalSql = """
        INSERT INTO cortex_data.learning_proposal
        (
            tenant_id, namespace_name, proposal_id, proposal_hash,
            payload, payload_sha256, auth_version, auth_algorithm,
            auth_key_id, auth_signature, policy_revision, created_at
        )
        VALUES
        (
            @tenant_id, @namespace, @proposal_id, @proposal_hash,
            @payload, @payload_sha256, @auth_version, @auth_algorithm,
            @auth_key_id, @auth_signature, @policy_revision, @created_at
        )
        ON CONFLICT (tenant_id, namespace_name, proposal_id) DO NOTHING;
        """;

    private const string InsertDecisionSql = """
        INSERT INTO cortex_data.learning_decision
        (
            tenant_id, namespace_name, proposal_id, decision_id,
            proposal_hash, decision_hash, decision_state, payload,
            payload_sha256, auth_version, auth_algorithm, auth_key_id,
            auth_signature, policy_revision, created_at
        )
        VALUES
        (
            @tenant_id, @namespace, @proposal_id, @decision_id,
            @proposal_hash, @decision_hash, @decision_state, @payload,
            @payload_sha256, @auth_version, @auth_algorithm, @auth_key_id,
            @auth_signature, @policy_revision, @created_at
        )
        ON CONFLICT DO NOTHING;
        """;

    private const string InsertPromotedRevisionSql = """
        INSERT INTO cortex_data.knowledge_revision
        (
            tenant_id, namespace_name, record_id, revision_id, taxonomy,
            title, summary, freshness_state, sensitivity, record_status,
            content_treatment, executable, content_hash, content, keyword_text,
            applicability, payload, payload_sha256, auth_version, auth_algorithm,
            auth_key_id, auth_signature
        )
        VALUES
        (
            @tenant_id, @namespace, @record_id, @revision_id, @taxonomy,
            @title, @summary, @freshness_state, @sensitivity, @record_status,
            @content_treatment, @executable, @content_hash, @content, @keyword_text,
            CAST(@applicability AS jsonb), @payload, @payload_sha256, @auth_version,
            @auth_algorithm, @auth_key_id, @auth_signature
        );
        """;

    private const string InsertPromotedHeadSql = """
        INSERT INTO cortex_data.knowledge_head
        (
            tenant_id, namespace_name, record_id, current_revision_id,
            generation, updated_at
        )
        VALUES
        (
            @tenant_id, @namespace, @record_id, @revision_id, 1, @updated_at
        );
        """;

    private static readonly string FindSql = $$"""
        SELECT {{ProposalColumns}}, {{DecisionColumns}}
        FROM cortex_data.learning_proposal p
        LEFT JOIN cortex_data.learning_decision d
          ON d.tenant_id = p.tenant_id
         AND d.namespace_name = p.namespace_name
         AND d.proposal_id = p.proposal_id
        WHERE p.tenant_id = @tenant_id
          AND p.namespace_name = @namespace
          AND p.proposal_id = @proposal_id;
        """;

    private static readonly string SelectProposalSql = $$"""
        SELECT {{ProposalColumns}}
        FROM cortex_data.learning_proposal p
        WHERE p.tenant_id = @tenant_id
          AND p.namespace_name = @namespace
          AND p.proposal_id = @proposal_id;
        """;

    private static readonly string SelectDecisionSql = $$"""
        SELECT {{DecisionColumns}}
        FROM cortex_data.learning_decision d
        WHERE d.tenant_id = @tenant_id
          AND d.namespace_name = @namespace
          AND d.proposal_id = @proposal_id;
        """;

    private static readonly string ListSql = $$"""
        SELECT {{ProposalColumns}}, {{DecisionColumns}}
        FROM cortex_data.learning_proposal p
        LEFT JOIN cortex_data.learning_decision d
          ON d.tenant_id = p.tenant_id
         AND d.namespace_name = p.namespace_name
         AND d.proposal_id = p.proposal_id
        WHERE p.tenant_id = @tenant_id
          AND p.namespace_name = @namespace
          AND
          (
              @after_proposal_id IS NULL OR
              p.proposal_id COLLATE "C" > @after_proposal_id COLLATE "C"
          )
        ORDER BY p.proposal_id COLLATE "C"
        LIMIT @limit;
        """;

    private const string StatusSql = """
        SELECT
            COUNT(*) AS total,
            COUNT(*) FILTER (WHERE d.proposal_id IS NULL) AS pending,
            COUNT(*) FILTER (WHERE d.decision_state = 'approved') AS approved,
            COUNT(*) FILTER (WHERE d.decision_state = 'rejected') AS rejected
        FROM cortex_data.learning_proposal p
        LEFT JOIN cortex_data.learning_decision d
          ON d.tenant_id = p.tenant_id
         AND d.namespace_name = p.namespace_name
         AND d.proposal_id = p.proposal_id
        WHERE p.tenant_id = @tenant_id
          AND p.namespace_name = @namespace;
        """;

    private readonly PostgreSqlTransactionRunner _transactions;
    private readonly PostgreSqlStoredRowCodec _codec;

    public PostgreSqlLearningFeedbackRepository(
        PostgreSqlTransactionRunner transactions,
        ICortexRowAuthenticator authenticator)
    {
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _codec = new PostgreSqlStoredRowCodec(authenticator);
    }

    public ValueTask<LearningFeedbackSnapshot?> FindAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken)
    {
        var operation = FindOperation(access, cancellationToken);
        ValidateIdentifier(proposalId, "cortex_postgresql_learning_proposal_id_invalid", operation, access);
        return _transactions.ExecuteAsync<LearningFeedbackSnapshot?>(
            PostgreSqlTransactionContext.FromLearning(access, operation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    FindSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
                    new PostgreSqlParameter("proposal_id", DbType.String, proposalId));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    return null;
                var snapshot = ReadSnapshot(reader, access, proposalId, operation, token);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    throw Failure("cortex_postgresql_learning_snapshot_ambiguous", operation, access, proposalId);
                return snapshot;
            },
            cancellationToken);
    }

    public ValueTask<LearningFeedbackProposalWriteResult> AddProposalAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        ValidateAccess(access, LearningFeedbackNamespaceCapability.Submit, null, SubmitOperation, cancellationToken);
        var captured = CaptureProposalForWrite(access, proposal, cancellationToken);

        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(access, SubmitOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var storedPayload = _codec.Encode(
                    ProposalEntityKind,
                    access.TenantId,
                    access.Namespace,
                    captured.ProposalId,
                    revisionId: null,
                    captured);
                var affected = await InsertProposalAsync(
                    connection,
                    transaction,
                    access,
                    captured,
                    storedPayload,
                    token).ConfigureAwait(false);
                if (affected is not (0 or 1))
                {
                    throw Failure(
                        "cortex_postgresql_learning_proposal_insert_count_invalid",
                        SubmitOperation,
                        access,
                        captured.ProposalId);
                }

                var stored = await ReadProposalAsync(
                    connection,
                    transaction,
                    access,
                    captured.ProposalId,
                    SubmitOperation,
                    token).ConfigureAwait(false);
                if (stored is null)
                {
                    throw Failure(
                        "cortex_postgresql_learning_proposal_reread_missing",
                        SubmitOperation,
                        access,
                        captured.ProposalId);
                }

                if (affected == 1)
                {
                    if (stored.PolicyRevision != access.PolicyRevision ||
                        !string.Equals(stored.Value.ProposalHash, captured.ProposalHash, StringComparison.Ordinal))
                    {
                        throw Failure(
                            "cortex_postgresql_learning_proposal_reread_mismatch",
                            SubmitOperation,
                            access,
                            captured.ProposalId);
                    }
                    return new LearningFeedbackProposalWriteResult(
                        LearningFeedbackProposalWriteDisposition.Added,
                        stored.Value);
                }

                var disposition = string.Equals(
                    stored.Value.ProposalHash,
                    captured.ProposalHash,
                    StringComparison.Ordinal)
                    ? LearningFeedbackProposalWriteDisposition.Existing
                    : LearningFeedbackProposalWriteDisposition.Conflict;
                return new LearningFeedbackProposalWriteResult(disposition, stored.Value);
            },
            cancellationToken);
    }

    public ValueTask<LearningFeedbackDecisionWriteResult> AddDecisionIfPendingAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision,
        CancellationToken cancellationToken)
    {
        ValidateAccess(access, LearningFeedbackNamespaceCapability.Review, null, ReviewOperation, cancellationToken);
        var candidate = CaptureDecisionScope(access, decision);

        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(access, ReviewOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                // Proposal rows are immutable and the runtime role deliberately has no UPDATE
                // privilege. The unique decision key plus conflict-safe insert and authenticated
                // reread provide the atomic first-decision transition without a row lock.
                var storedProposal = await ReadProposalAsync(
                    connection,
                    transaction,
                    access,
                    candidate.ProposalId,
                    ReviewOperation,
                    token).ConfigureAwait(false);
                if (storedProposal is null)
                {
                    return new LearningFeedbackDecisionWriteResult(
                        LearningFeedbackDecisionWriteDisposition.MissingProposal,
                        null);
                }

                var storedDecision = await ReadDecisionAsync(
                    connection,
                    transaction,
                    access,
                    candidate.ProposalId,
                    ReviewOperation,
                    token).ConfigureAwait(false);
                var current = CaptureSnapshot(
                    storedProposal.Value,
                    storedDecision,
                    access,
                    candidate.ProposalId,
                    ReviewOperation,
                    "cortex_postgresql_learning_snapshot_invalid",
                    token);

                if (!string.Equals(
                        storedProposal.Value.ProposalHash,
                        candidate.ProposalHash,
                        StringComparison.Ordinal))
                {
                    return new LearningFeedbackDecisionWriteResult(
                        LearningFeedbackDecisionWriteDisposition.ProposalHashConflict,
                        current);
                }

                if (storedDecision is not null)
                    return ExistingDecisionResult(candidate, storedDecision, current);

                var requested = CaptureSnapshot(
                    storedProposal.Value,
                    candidate,
                    access,
                    candidate.ProposalId,
                    ReviewOperation,
                    "cortex_postgresql_learning_decision_invalid",
                    token);
                var capturedDecision = requested.Decision!;
                var storedPayload = _codec.Encode(
                    DecisionEntityKind,
                    access.TenantId,
                    access.Namespace,
                    capturedDecision.ProposalId,
                    capturedDecision.DecisionId,
                    capturedDecision);
                var affected = await InsertDecisionAsync(
                    connection,
                    transaction,
                    access,
                    capturedDecision,
                    storedPayload,
                    token).ConfigureAwait(false);
                if (affected is not (0 or 1))
                {
                    throw Failure(
                        "cortex_postgresql_learning_decision_insert_count_invalid",
                        ReviewOperation,
                        access,
                        capturedDecision.ProposalId,
                        capturedDecision.DecisionId);
                }

                // Only the transaction that stored the immutable decision may promote it.
                // A concurrent loser rereads the committed decision below; it must not rely
                // on ON CONFLICT because that path requires SELECT visibility which the
                // intentionally INSERT-only review capability does not have under RLS.
                if (affected == 1)
                {
                    await EnsureApprovedRetentionAsync(
                        connection,
                        transaction,
                        access,
                        storedProposal.Value,
                        capturedDecision,
                        RetentionConfidence(storedProposal.Value, capturedDecision),
                        RetentionConfidenceBasis(storedProposal.Value, capturedDecision),
                        token).ConfigureAwait(false);
                }

                var reread = await ReadDecisionAsync(
                    connection,
                    transaction,
                    access,
                    capturedDecision.ProposalId,
                    ReviewOperation,
                    token).ConfigureAwait(false);
                if (reread is null)
                {
                    throw Failure(
                        "cortex_postgresql_learning_decision_reread_missing",
                        ReviewOperation,
                        access,
                        capturedDecision.ProposalId,
                        capturedDecision.DecisionId);
                }
                var resultSnapshot = CaptureSnapshot(
                    storedProposal.Value,
                    reread,
                    access,
                    capturedDecision.ProposalId,
                    ReviewOperation,
                    "cortex_postgresql_learning_snapshot_invalid",
                    token);

                if (affected == 1)
                {
                    if (!SameDecision(capturedDecision, reread))
                    {
                        throw Failure(
                            "cortex_postgresql_learning_decision_reread_mismatch",
                            ReviewOperation,
                            access,
                            capturedDecision.ProposalId,
                            capturedDecision.DecisionId);
                    }
                    return new LearningFeedbackDecisionWriteResult(
                        LearningFeedbackDecisionWriteDisposition.Added,
                        resultSnapshot);
                }

                return ExistingDecisionResult(capturedDecision, reread, resultSnapshot);
            },
            cancellationToken);
    }

    public ValueTask<LearningFeedbackRepositoryPage> ListAsync(
        LearningFeedbackNamespaceAccess access,
        string? afterProposalId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        if (access.Capability is not (
                LearningFeedbackNamespaceCapability.Read or
                LearningFeedbackNamespaceCapability.Review or
                LearningFeedbackNamespaceCapability.Audit))
        {
            throw Failure("cortex_postgresql_learning_scope_invalid", ListOperation, access);
        }
        if (limit is < 1 or > LearningFeedbackApplicationLimits.MaximumPageSize ||
            afterProposalId is not null && !ValidIdentifier(afterProposalId))
        {
            throw Failure("cortex_postgresql_learning_page_request_invalid", ListOperation, access, afterProposalId);
        }

        var databaseLimit = checked(limit + 1);
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(access, ListOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    ListSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
                    new PostgreSqlParameter("after_proposal_id", DbType.String, afterProposalId),
                    new PostgreSqlParameter("limit", DbType.Int32, databaseLimit));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var snapshots = new List<LearningFeedbackSnapshot>(databaseLimit);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (snapshots.Count == databaseLimit)
                    {
                        throw Failure(
                            "cortex_postgresql_learning_page_unbounded",
                            ListOperation,
                            access,
                            afterProposalId);
                    }
                    snapshots.Add(ReadSnapshot(reader, access, expectedProposalId: null, ListOperation, token));
                }

                var hasMore = snapshots.Count > limit;
                var items = snapshots.Take(limit).ToArray();
                var next = hasMore && items.Length > 0 ? items[^1].Proposal.ProposalId : null;
                return new LearningFeedbackRepositoryPage(Array.AsReadOnly(items), next);
            },
            cancellationToken);
    }

    public ValueTask<LearningFeedbackRepositoryStatus> GetStatusAsync(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken)
    {
        ValidateAccess(access, LearningFeedbackNamespaceCapability.Read, null, StatusOperation, cancellationToken);
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(access, StatusOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    StatusSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, access.Namespace));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    throw Failure("cortex_postgresql_learning_status_missing", StatusOperation, access);

                var total = reader.GetInt64(0);
                var pending = reader.GetInt64(1);
                var approved = reader.GetInt64(2);
                var rejected = reader.GetInt64(3);
                if (await reader.ReadAsync(token).ConfigureAwait(false) ||
                    total < 0 || pending < 0 || approved < 0 || rejected < 0 ||
                    pending > total || approved > total - pending ||
                    rejected != total - pending - approved)
                {
                    throw Failure("cortex_postgresql_learning_status_invalid", StatusOperation, access);
                }

                return new LearningFeedbackRepositoryStatus(total, pending, approved, rejected);
            },
            cancellationToken);
    }

    private async ValueTask EnsureApprovedRetentionAsync(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackReviewDecisionV1 decision,
        double retentionConfidence,
        string retentionConfidenceBasis,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(decision.State, "approved", StringComparison.Ordinal) ||
            !proposal.Payload.TryGetValue(RetentionMarker, out var retentionValue) ||
            !string.Equals(retentionValue, "true", StringComparison.Ordinal))
        {
            return;
        }

        var record = CreateRetainedKnowledgeRecord(
            access,
            proposal,
            decision,
            retentionConfidence,
            retentionConfidenceBasis);
        var stored = _codec.Encode(
            KnowledgeEntityKind,
            access.TenantId,
            access.Namespace,
            record.RecordId,
            record.RevisionId,
            record);
        var applicabilityJson = Encoding.UTF8.GetString(
            CortexCanonicalJson.Serialize(record.Applicability));

        await using (var revisionCommand = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertPromotedRevisionSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("record_id", DbType.String, record.RecordId),
            new PostgreSqlParameter("revision_id", DbType.String, record.RevisionId),
            new PostgreSqlParameter("taxonomy", DbType.String, record.Taxonomy),
            new PostgreSqlParameter("title", DbType.String, record.Title),
            new PostgreSqlParameter("summary", DbType.String, record.Summary),
            new PostgreSqlParameter("freshness_state", DbType.String, record.Freshness.State),
            new PostgreSqlParameter("sensitivity", DbType.String, record.Sensitivity),
            new PostgreSqlParameter("record_status", DbType.String, record.RecordStatus),
            new PostgreSqlParameter(
                "content_treatment",
                DbType.String,
                record.ContentTreatment),
            new PostgreSqlParameter("executable", DbType.Boolean, false),
            new PostgreSqlParameter("content_hash", DbType.String, record.ContentHash),
            new PostgreSqlParameter("content", DbType.String, record.Content),
            new PostgreSqlParameter(
                "keyword_text",
                DbType.String,
                string.Join(' ', record.Keywords)),
            new PostgreSqlParameter("applicability", DbType.String, applicabilityJson),
            new PostgreSqlParameter("payload", DbType.Binary, stored.Payload),
            new PostgreSqlParameter("payload_sha256", DbType.String, stored.PayloadSha256),
            new PostgreSqlParameter("auth_version", DbType.Int32, stored.Seal.Version),
            new PostgreSqlParameter("auth_algorithm", DbType.String, stored.Seal.Algorithm),
            new PostgreSqlParameter("auth_key_id", DbType.String, stored.Seal.KeyId),
            new PostgreSqlParameter(
                "auth_signature",
                DbType.Binary,
                stored.Seal.GetSignature())))
        {
            var affected = await revisionCommand.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (affected != 1)
                throw Failure(
                    "cortex_postgresql_retention_revision_insert_count_invalid",
                    ReviewOperation,
                    access,
                    proposal.ProposalId,
                    decision.DecisionId);
        }

        await using (var headCommand = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertPromotedHeadSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("record_id", DbType.String, record.RecordId),
            new PostgreSqlParameter("revision_id", DbType.String, record.RevisionId),
            new PostgreSqlParameter(
                "updated_at",
                DbType.DateTimeOffset,
                PostgreSqlTimestamp(decision.CreatedAt))))
        {
            var affected = await headCommand.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (affected != 1)
                throw Failure(
                    "cortex_postgresql_retention_head_insert_count_invalid",
                    ReviewOperation,
                    access,
                    proposal.ProposalId,
                    decision.DecisionId);
        }

        // Ownership is captured from the authenticated, sealed proposal source,
        // not from the reviewer who approved publication. The narrow database
        // function accepts only a brand-new generation-one active head.
        await RegisterPublicationOriginAsync(
            connection,
            transaction,
            access,
            proposal,
            record,
            decision.CreatedAt,
            cancellationToken).ConfigureAwait(false);

    }

    internal static CortexKnowledgeRecord CreateRetainedKnowledgeRecord(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackReviewDecisionV1 decision,
        double retentionConfidence,
        string retentionConfidenceBasis)
    {
        if (!proposal.Payload.TryGetValue("title", out var title) ||
            !proposal.Payload.TryGetValue("content", out var content) ||
            string.IsNullOrWhiteSpace(title) ||
            title.Length > 512 ||
            string.IsNullOrWhiteSpace(content) ||
            content.Length > 32 * 1024 ||
            string.IsNullOrWhiteSpace(proposal.Summary) ||
            proposal.Summary.Length > 2048)
        {
            throw Failure(
                "cortex_postgresql_retention_payload_invalid",
                ReviewOperation,
                access,
                proposal.ProposalId,
                decision.DecisionId);
        }

        var contentHash = CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(content));
        if (!string.Equals(contentHash, proposal.Source.ContentHash, StringComparison.Ordinal))
        {
            throw Failure(
                "cortex_postgresql_retention_content_hash_mismatch",
                ReviewOperation,
                access,
                proposal.ProposalId,
                decision.DecisionId);
        }

        var provisional = LearningFeedbackPublicationPolicy.IsProvisionalDecision(decision);
        var applicabilityState = provisional ? "unverified" : "verified";
        var collectiveProjection = provisional
            ? LearningFeedbackCollectiveProjectionPolicy.Create(proposal)
            : null;
        if (collectiveProjection is not null &&
            (!collectiveProjection.Accepted || collectiveProjection.Value is null))
        {
            throw Failure(
                "cortex_postgresql_collective_projection_rejected",
                ReviewOperation,
                access,
                proposal.ProposalId,
                decision.DecisionId);
        }

        // The proposal and its hash remain exact and project-bound. Only an
        // auto-published collective record uses this deterministic generalized
        // view, so retries create byte-identical searchable knowledge.
        var projection = collectiveProjection?.Value;
        var retainedTitle = projection?.Title ?? title;
        var retainedSummary = projection?.Summary ?? proposal.Summary;
        var retainedContent = projection?.Content ?? content;
        var retainedTaxonomy = projection?.Taxonomy ?? proposal.Taxonomy ?? "continuity";
        var retainedKeywords = projection?.Keywords ?? proposal.Keywords ?? [];
        var retainedApplicability = projection?.Applicability ?? proposal.Applicability ?? [];
        var retainedContentHash = projection?.ContentHash ?? contentHash;
        var transformations = provisional
            ? (projection?.Transformations ?? [])
                .Concat([
                    "sanitized-proposal",
                    "provisional-policy-publication",
                    "deferred-review-required"
                ])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : ["sanitized-proposal", "independent-review"];
        return new CortexKnowledgeRecord(
            $"krec_{proposal.ProposalHash}",
            $"krev_{decision.DecisionHash}",
            access.Namespace,
            retainedTaxonomy,
            provisional ? "generalized-learning" : $"retained-{proposal.ProposalId}",
            retainedTitle,
            retainedSummary,
            retainedContent,
            Keywords: Array.AsReadOnly(retainedKeywords.ToArray()),
            Applicability: Array.AsReadOnly(
                retainedApplicability
                    .Select(item => new CortexKnowledgeApplicability(
                        item.ProductId,
                        item.ComponentId,
                        MinimumVersion: null,
                        MaximumVersion: null,
                        State: applicabilityState))
                    .ToArray()),
            Evidence: [],
            new CortexKnowledgeProvenance(
                string.Equals(
                    proposal.ContractId,
                    LearningFeedbackContractIds.ProposalV1_1,
                    StringComparison.Ordinal)
                    ? "pegasus.cortex.learning-feedback-promotion/1.1"
                    : "pegasus.cortex.learning-feedback-promotion/1.0",
                SourceReferences: [],
                SourceRevisions: [],
                Transformations: Array.AsReadOnly(transformations)),
            new CortexKnowledgeConfidence(retentionConfidence, retentionConfidenceBasis),
            new CortexKnowledgeFreshness(
                "current",
                decision.CreatedAt,
                ReviewAfter: null,
                Basis: provisional
                    ? "provisionally published; deferred independent review required"
                    : "approved retention decision"),
            "internal",
            "active",
            TrainingEligibility: false,
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            retainedContentHash);
    }

    private static double RetentionConfidence(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackReviewDecisionV1 decision) =>
        LearningFeedbackPublicationPolicy.IsProvisionalDecision(decision)
            ? LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidence
            : proposal.Evidence.Count > 0 &&
              proposal.Evidence.All(item =>
                  string.Equals(item.Verification, "verified", StringComparison.Ordinal))
                ? 0.90
                : 0.80;

    private static string RetentionConfidenceBasis(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackReviewDecisionV1 decision) =>
        LearningFeedbackPublicationPolicy.IsProvisionalDecision(decision)
            ? LearningFeedbackPublicationPolicy.ProvisionalRetentionConfidenceBasis
            : RetentionConfidence(proposal, decision) == 0.90
                ? "independent human approval with verified evidence"
                : "independent human approval";

    private async ValueTask<int> InsertProposalAsync(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        PostgreSqlStoredPayload stored,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertProposalSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("proposal_id", DbType.String, proposal.ProposalId),
            new PostgreSqlParameter("proposal_hash", DbType.String, proposal.ProposalHash),
            new PostgreSqlParameter("payload", DbType.Binary, stored.Payload),
            new PostgreSqlParameter("payload_sha256", DbType.String, stored.PayloadSha256),
            new PostgreSqlParameter("auth_version", DbType.Int32, stored.Seal.Version),
            new PostgreSqlParameter("auth_algorithm", DbType.String, stored.Seal.Algorithm),
            new PostgreSqlParameter("auth_key_id", DbType.String, stored.Seal.KeyId),
            new PostgreSqlParameter("auth_signature", DbType.Binary, stored.Seal.GetSignature()),
            new PostgreSqlParameter("policy_revision", DbType.Int64, access.PolicyRevision),
            new PostgreSqlParameter("created_at", DbType.DateTimeOffset, PostgreSqlTimestamp(proposal.CreatedAt)));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> InsertDecisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision,
        PostgreSqlStoredPayload stored,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertDecisionSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("proposal_id", DbType.String, decision.ProposalId),
            new PostgreSqlParameter("decision_id", DbType.String, decision.DecisionId),
            new PostgreSqlParameter("proposal_hash", DbType.String, decision.ProposalHash),
            new PostgreSqlParameter("decision_hash", DbType.String, decision.DecisionHash),
            new PostgreSqlParameter("decision_state", DbType.String, decision.State),
            new PostgreSqlParameter("payload", DbType.Binary, stored.Payload),
            new PostgreSqlParameter("payload_sha256", DbType.String, stored.PayloadSha256),
            new PostgreSqlParameter("auth_version", DbType.Int32, stored.Seal.Version),
            new PostgreSqlParameter("auth_algorithm", DbType.String, stored.Seal.Algorithm),
            new PostgreSqlParameter("auth_key_id", DbType.String, stored.Seal.KeyId),
            new PostgreSqlParameter("auth_signature", DbType.Binary, stored.Seal.GetSignature()),
            new PostgreSqlParameter("policy_revision", DbType.Int64, decision.PolicyRevision),
            new PostgreSqlParameter("created_at", DbType.DateTimeOffset, PostgreSqlTimestamp(decision.CreatedAt)));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<StoredProposal?> ReadProposalAsync(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        string operation,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            SelectProposalSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("proposal_id", DbType.String, proposalId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var proposal = ReadProposal(reader, 0, access, proposalId, operation, cancellationToken);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Failure("cortex_postgresql_learning_proposal_ambiguous", operation, access, proposalId);
        return proposal;
    }

    private async ValueTask<LearningFeedbackReviewDecisionV1?> ReadDecisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        string operation,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            SelectDecisionSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("proposal_id", DbType.String, proposalId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var decision = ReadDecision(reader, 0, access, proposalId, operation);
        if (decision is null)
            throw Failure("cortex_postgresql_learning_decision_projection_missing", operation, access, proposalId);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Failure("cortex_postgresql_learning_decision_ambiguous", operation, access, proposalId);
        return decision;
    }

    private LearningFeedbackSnapshot ReadSnapshot(
        DbDataReader reader,
        LearningFeedbackNamespaceAccess access,
        string? expectedProposalId,
        string operation,
        CancellationToken cancellationToken)
    {
        var proposal = ReadProposal(reader, 0, access, expectedProposalId, operation, cancellationToken);
        var decision = ReadDecision(reader, 12, access, proposal.Value.ProposalId, operation);
        return CaptureSnapshot(
            proposal.Value,
            decision,
            access,
            proposal.Value.ProposalId,
            operation,
            "cortex_postgresql_learning_snapshot_invalid",
            cancellationToken);
    }

    private StoredProposal ReadProposal(
        DbDataReader reader,
        int offset,
        LearningFeedbackNamespaceAccess access,
        string? expectedProposalId,
        string operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenantId = reader.GetString(offset);
        var namespaceName = reader.GetString(offset + 1);
        var proposalId = reader.GetString(offset + 2);
        var proposalHash = reader.GetString(offset + 3);
        var policyRevision = reader.GetInt64(offset + 10);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(offset + 11).ToUniversalTime();
        if (!string.Equals(tenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(namespaceName, access.Namespace, StringComparison.Ordinal) ||
            expectedProposalId is not null &&
                !string.Equals(proposalId, expectedProposalId, StringComparison.Ordinal) ||
            policyRevision < 1 || reader.IsDBNull(offset + 4))
        {
            throw Failure(
                "cortex_postgresql_learning_proposal_scope_mismatch",
                operation,
                access,
                expectedProposalId ?? proposalId);
        }

        var value = _codec.Decode<LearningFeedbackProposalV1>(
            ProposalEntityKind,
            tenantId,
            namespaceName,
            proposalId,
            revisionId: null,
            reader.GetFieldValue<byte[]>(offset + 4),
            reader.GetString(offset + 5),
            reader.GetInt32(offset + 6),
            reader.GetString(offset + 7),
            reader.GetString(offset + 8),
            reader.GetFieldValue<byte[]>(offset + 9));
        if (!string.Equals(value.TenantId, tenantId, StringComparison.Ordinal) ||
            !string.Equals(value.Namespace, namespaceName, StringComparison.Ordinal) ||
            !string.Equals(value.ProposalId, proposalId, StringComparison.Ordinal) ||
            !string.Equals(value.ProposalHash, proposalHash, StringComparison.Ordinal) ||
            value.Screening.PolicyRevision != policyRevision ||
            PostgreSqlTimestamp(value.CreatedAt) != createdAt ||
            !LearningFeedbackSnapshotBoundary.TryCapture(
                new LearningFeedbackSnapshot(value, null),
                out var captured,
                cancellationToken))
        {
            throw Failure(
                "cortex_postgresql_learning_proposal_projection_mismatch",
                operation,
                access,
                proposalId);
        }
        return new StoredProposal(captured!.Proposal, policyRevision);
    }

    private LearningFeedbackReviewDecisionV1? ReadDecision(
        DbDataReader reader,
        int offset,
        LearningFeedbackNamespaceAccess access,
        string expectedProposalId,
        string operation)
    {
        var nullCount = 0;
        for (var index = 0; index < 15; index++)
        {
            if (reader.IsDBNull(offset + index))
                nullCount++;
        }
        if (nullCount == 15)
            return null;
        if (nullCount != 0)
        {
            throw Failure(
                "cortex_postgresql_learning_decision_projection_incomplete",
                operation,
                access,
                expectedProposalId);
        }

        var tenantId = reader.GetString(offset);
        var namespaceName = reader.GetString(offset + 1);
        var proposalId = reader.GetString(offset + 2);
        var decisionId = reader.GetString(offset + 3);
        var proposalHash = reader.GetString(offset + 4);
        var decisionHash = reader.GetString(offset + 5);
        var decisionState = reader.GetString(offset + 6);
        var policyRevision = reader.GetInt64(offset + 13);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(offset + 14).ToUniversalTime();
        if (!string.Equals(tenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(namespaceName, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(proposalId, expectedProposalId, StringComparison.Ordinal) ||
            policyRevision < 1)
        {
            throw Failure(
                "cortex_postgresql_learning_decision_scope_mismatch",
                operation,
                access,
                expectedProposalId,
                decisionId);
        }

        var value = _codec.Decode<LearningFeedbackReviewDecisionV1>(
            DecisionEntityKind,
            tenantId,
            namespaceName,
            proposalId,
            decisionId,
            reader.GetFieldValue<byte[]>(offset + 7),
            reader.GetString(offset + 8),
            reader.GetInt32(offset + 9),
            reader.GetString(offset + 10),
            reader.GetString(offset + 11),
            reader.GetFieldValue<byte[]>(offset + 12));
        if (!string.Equals(value.TenantId, tenantId, StringComparison.Ordinal) ||
            !string.Equals(value.Namespace, namespaceName, StringComparison.Ordinal) ||
            !string.Equals(value.ProposalId, proposalId, StringComparison.Ordinal) ||
            !string.Equals(value.DecisionId, decisionId, StringComparison.Ordinal) ||
            !string.Equals(value.ProposalHash, proposalHash, StringComparison.Ordinal) ||
            !string.Equals(value.DecisionHash, decisionHash, StringComparison.Ordinal) ||
            !string.Equals(value.State, decisionState, StringComparison.Ordinal) ||
            value.PolicyRevision != policyRevision ||
            PostgreSqlTimestamp(value.CreatedAt) != createdAt)
        {
            throw Failure(
                "cortex_postgresql_learning_decision_projection_mismatch",
                operation,
                access,
                expectedProposalId,
                decisionId);
        }
        return value;
    }

    private static LearningFeedbackProposalV1 CaptureProposalForWrite(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!LearningFeedbackSnapshotBoundary.TryCapture(
                new LearningFeedbackSnapshot(proposal, null),
                out var snapshot,
                cancellationToken))
        {
            throw Failure(
                "cortex_postgresql_learning_proposal_invalid",
                SubmitOperation,
                access,
                proposal.ProposalId);
        }

        var captured = snapshot!.Proposal;
        if (!string.Equals(captured.TenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(captured.Namespace, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(captured.Source.AuthorityId, access.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(captured.Source.SubjectId, access.SubjectId, StringComparison.Ordinal) ||
            !string.Equals(captured.Source.ServiceId, access.ServiceId, StringComparison.Ordinal) ||
            captured.Screening.PolicyRevision != access.PolicyRevision)
        {
            throw Failure(
                "cortex_postgresql_learning_proposal_authority_mismatch",
                SubmitOperation,
                access,
                captured.ProposalId);
        }
        return captured;
    }

    private static LearningFeedbackReviewDecisionV1 CaptureDecisionScope(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewDecisionV1 decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var captured = decision with { };
        if (!ValidIdentifier(captured.DecisionId) ||
            !ValidIdentifier(captured.ProposalId) ||
            !ValidHash(captured.ProposalHash) ||
            !ValidHash(captured.DecisionHash) ||
            string.IsNullOrWhiteSpace(captured.Reason) ||
            captured.Reason.Length > LearningFeedbackContractLimits.MaximumReviewReasonCharacters)
        {
            throw Failure(
                "cortex_postgresql_learning_decision_invalid",
                ReviewOperation,
                access,
                captured.ProposalId,
                captured.DecisionId);
        }
        if (!string.Equals(captured.TenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(captured.Namespace, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(captured.AuthorityId, access.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(captured.ReviewerSubjectId, access.SubjectId, StringComparison.Ordinal) ||
            captured.PolicyRevision != access.PolicyRevision)
        {
            throw Failure(
                "cortex_postgresql_learning_decision_authority_mismatch",
                ReviewOperation,
                access,
                captured.ProposalId,
                captured.DecisionId);
        }
        return captured;
    }

    private static LearningFeedbackSnapshot CaptureSnapshot(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackReviewDecisionV1? decision,
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        string operation,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (!LearningFeedbackSnapshotBoundary.TryCapture(
                new LearningFeedbackSnapshot(proposal, decision),
                out var captured,
                cancellationToken) ||
            !string.Equals(captured!.Proposal.TenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(captured.Proposal.Namespace, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(captured.Proposal.ProposalId, proposalId, StringComparison.Ordinal))
        {
            throw Failure(failureCode, operation, access, proposalId, decision?.DecisionId);
        }
        return captured;
    }

    private static LearningFeedbackDecisionWriteResult ExistingDecisionResult(
        LearningFeedbackReviewDecisionV1 requested,
        LearningFeedbackReviewDecisionV1 stored,
        LearningFeedbackSnapshot snapshot) =>
        new(
            SameDecision(requested, stored)
                ? LearningFeedbackDecisionWriteDisposition.Existing
                : LearningFeedbackDecisionWriteDisposition.AlreadyReviewed,
            snapshot);

    private static bool SameDecision(
        LearningFeedbackReviewDecisionV1 first,
        LearningFeedbackReviewDecisionV1 second) =>
        string.Equals(first.DecisionId, second.DecisionId, StringComparison.Ordinal) &&
        string.Equals(first.DecisionHash, second.DecisionHash, StringComparison.Ordinal);

    private static string FindOperation(
        LearningFeedbackNamespaceAccess access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        var operation = access.Capability switch
        {
            LearningFeedbackNamespaceCapability.Review => ReviewFindOperation,
            LearningFeedbackNamespaceCapability.Audit => AuditOperation,
            _ => ReadFindOperation
        };
        if (access.Capability is not (
                LearningFeedbackNamespaceCapability.Read or
                LearningFeedbackNamespaceCapability.Review or
                LearningFeedbackNamespaceCapability.Audit))
        {
            throw Failure("cortex_postgresql_learning_scope_invalid", operation, access);
        }
        return operation;
    }

    private static void ValidateAccess(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackNamespaceCapability expected,
        LearningFeedbackNamespaceCapability? alternate,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        if (access.Capability != expected && access.Capability != alternate)
            throw Failure("cortex_postgresql_learning_scope_invalid", operation, access);
    }

    private static void ValidateIdentifier(
        string value,
        string failureCode,
        string operation,
        LearningFeedbackNamespaceAccess access)
    {
        if (!ValidIdentifier(value))
            throw Failure(failureCode, operation, access, value);
    }

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidNamespace(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= LearningFeedbackContractLimits.MaximumNamespaceCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private static PostgreSqlPersistenceException Failure(
        string code,
        string operation,
        LearningFeedbackNamespaceAccess access,
        string? proposalId = null,
        string? decisionId = null) =>
        new(
            code,
            operation,
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenantId"] = ValidIdentifier(access.TenantId) ? access.TenantId : null,
                ["namespace"] = ValidNamespace(access.Namespace) ? access.Namespace : null,
                ["capability"] = Enum.IsDefined(access.Capability)
                    ? access.Capability.ToString().ToLowerInvariant()
                    : null,
                ["proposalId"] = ValidIdentifier(proposalId) ? proposalId : null,
                ["decisionId"] = ValidIdentifier(decisionId) ? decisionId : null
            });

    private sealed record StoredProposal(
        LearningFeedbackProposalV1 Value,
        long PolicyRevision);
}
