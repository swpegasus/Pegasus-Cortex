using System.Data;
using System.Data.Common;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Security;

namespace Pegasus.Cortex.Persistence.PostgreSql.Learning;

public sealed partial class PostgreSqlLearningFeedbackRepository
{
    private const string AuditOperation = "cortex.learning-feedback.audit";
    private const string DelegateReviewOperation = "cortex.learning-feedback.delegate-review";
    private const string AuditAssessmentEventKind = "learning-audit-assessment";
    private const string ReviewGrantEventKind = "learning-review-grant";
    private const string ReviewGrantRevocationEventKind = "learning-review-grant-revocation";
    private const string ReviewGrantUseEventKind = "learning-review-grant-use";

    private const string AuditEventColumns = """
        tenant_id, namespace_name, event_id, event_kind,
        CASE WHEN octet_length(payload) BETWEEN 1 AND 131072
             THEN payload ELSE NULL END AS payload,
        payload_sha256, auth_version, auth_algorithm, auth_key_id,
        auth_signature, occurred_at
        """;

    private const string InsertAuditEventSql = """
        INSERT INTO cortex_data.audit_event
        (
            tenant_id, namespace_name, event_id, event_kind, payload,
            payload_sha256, auth_version, auth_algorithm, auth_key_id,
            auth_signature, occurred_at
        )
        VALUES
        (
            @tenant_id, @namespace, @event_id, @event_kind, @payload,
            @payload_sha256, @auth_version, @auth_algorithm, @auth_key_id,
            @auth_signature, @occurred_at
        )
        ON CONFLICT (tenant_id, namespace_name, event_id) DO NOTHING;
        """;

    private static readonly string SelectAuditEventSql = $$"""
        SELECT {{AuditEventColumns}}
        FROM cortex_data.audit_event
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND event_id = @event_id;
        """;

    private static readonly string SelectLatestAssessmentSql = $$"""
        SELECT {{AuditEventColumns}}
        FROM cortex_data.audit_event
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND event_kind = @event_kind
          AND event_id LIKE @event_prefix
        ORDER BY occurred_at DESC, event_id COLLATE "C" DESC
        LIMIT 1;
        """;

    private const string CountGrantUsesSql = """
        SELECT COUNT(*)
        FROM cortex_data.audit_event
        WHERE tenant_id = @tenant_id
          AND namespace_name = @namespace
          AND event_kind = @event_kind
          AND event_id LIKE @event_prefix;
        """;

    private const string LockGrantSql =
        "SELECT pg_advisory_xact_lock(hashtextextended(@grant_id, 0));";

    public ValueTask<LearningFeedbackReviewGrantWriteResult> IssueReviewGrantAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewGrantV1 grant,
        CancellationToken cancellationToken)
    {
        ValidateAccess(
            access,
            LearningFeedbackNamespaceCapability.DelegateReview,
            null,
            DelegateReviewOperation,
            cancellationToken);
        ArgumentNullException.ThrowIfNull(grant);
        if (!string.Equals(grant.TenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(grant.Namespace, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(grant.AuthorityId, access.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(grant.IssuerSubjectId, access.SubjectId, StringComparison.Ordinal) ||
            grant.PolicyRevision != access.PolicyRevision ||
            !string.Equals(grant.GrantHash, LearningFeedbackAuditPolicy.ComputeGrantHash(grant), StringComparison.Ordinal))
        {
            throw AuditFailure("cortex_postgresql_review_grant_invalid", DelegateReviewOperation, access, grant.GrantId);
        }

        var eventId = GrantIssueEventId(grant.GrantId);
        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(access, DelegateReviewOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var affected = await InsertAuditEventAsync(
                    connection,
                    transaction,
                    access,
                    eventId,
                    ReviewGrantEventKind,
                    grant,
                    grant.CreatedAt,
                    token).ConfigureAwait(false);
                var stored = await ReadAuditEventAsync<LearningFeedbackReviewGrantV1>(
                    connection,
                    transaction,
                    access,
                    eventId,
                    ReviewGrantEventKind,
                    token).ConfigureAwait(false);
                if (stored is null)
                    throw AuditFailure("cortex_postgresql_review_grant_reread_missing", DelegateReviewOperation, access, grant.GrantId);
                var same = string.Equals(stored.GrantHash, grant.GrantHash, StringComparison.Ordinal);
                return new LearningFeedbackReviewGrantWriteResult(
                    affected == 1
                        ? LearningFeedbackAuditWriteDisposition.Added
                        : same
                            ? LearningFeedbackAuditWriteDisposition.Existing
                            : LearningFeedbackAuditWriteDisposition.Conflict,
                    stored,
                    null);
            },
            cancellationToken);
    }

    public ValueTask<LearningFeedbackReviewGrantWriteResult> RevokeReviewGrantAsync(
        LearningFeedbackNamespaceAccess access,
        string reviewGrantId,
        string reason,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        ValidateAccess(
            access,
            LearningFeedbackNamespaceCapability.DelegateReview,
            null,
            DelegateReviewOperation,
            cancellationToken);
        if (!ValidIdentifier(reviewGrantId) ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Length > LearningFeedbackContractLimits.MaximumReviewReasonCharacters)
        {
            throw AuditFailure("cortex_postgresql_review_grant_revocation_invalid", DelegateReviewOperation, access, reviewGrantId);
        }

        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(access, DelegateReviewOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await AcquireGrantLockAsync(
                    connection,
                    transaction,
                    reviewGrantId,
                    token).ConfigureAwait(false);
                var grant = await ReadAuditEventAsync<LearningFeedbackReviewGrantV1>(
                    connection,
                    transaction,
                    access,
                    GrantIssueEventId(reviewGrantId),
                    ReviewGrantEventKind,
                    token).ConfigureAwait(false);
                if (grant is null)
                {
                    return new LearningFeedbackReviewGrantWriteResult(
                        LearningFeedbackAuditWriteDisposition.GrantUnavailable,
                        null,
                        null);
                }

                var revocation = new LearningFeedbackReviewGrantRevocationV1(
                    1,
                    LearningFeedbackContractIds.ReviewGrantRevocation,
                    LearningFeedbackContractIds.Canonicalization,
                    $"revocation_{grant.GrantHash}",
                    grant.GrantId,
                    grant.GrantHash,
                    access.TenantId,
                    access.Namespace,
                    access.AuthorityId,
                    access.SubjectId,
                    access.PolicyRevision,
                    reason.Trim(),
                    revokedAt.ToUniversalTime(),
                    new string('0', 64));
                revocation = revocation with
                {
                    RevocationHash = LearningFeedbackAuditPolicy.ComputeRevocationHash(revocation)
                };
                var eventId = GrantRevocationEventId(reviewGrantId);
                var affected = await InsertAuditEventAsync(
                    connection,
                    transaction,
                    access,
                    eventId,
                    ReviewGrantRevocationEventKind,
                    revocation,
                    revokedAt,
                    token).ConfigureAwait(false);
                var stored = await ReadAuditEventAsync<LearningFeedbackReviewGrantRevocationV1>(
                    connection,
                    transaction,
                    access,
                    eventId,
                    ReviewGrantRevocationEventKind,
                    token).ConfigureAwait(false);
                if (stored is null)
                    throw AuditFailure("cortex_postgresql_review_grant_revocation_reread_missing", DelegateReviewOperation, access, reviewGrantId);
                var same = string.Equals(stored.RevocationHash, revocation.RevocationHash, StringComparison.Ordinal);
                return new LearningFeedbackReviewGrantWriteResult(
                    affected == 1
                        ? LearningFeedbackAuditWriteDisposition.Added
                        : same
                            ? LearningFeedbackAuditWriteDisposition.Existing
                            : LearningFeedbackAuditWriteDisposition.Conflict,
                    grant,
                    stored);
            },
            cancellationToken);
    }

    public ValueTask<LearningFeedbackAuditApplyResult> ApplyAssessmentAsync(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackAuditAssessmentV1 assessment,
        LearningFeedbackReviewDecisionV1? autoApprovalDecision,
        bool collectiveLibrary,
        CancellationToken cancellationToken)
    {
        ValidateAccess(
            access,
            LearningFeedbackNamespaceCapability.Audit,
            null,
            AuditOperation,
            cancellationToken);
        ArgumentNullException.ThrowIfNull(assessment);
        if (!string.Equals(assessment.TenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(assessment.Namespace, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(assessment.AuthorityId, access.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(assessment.AuditorSubjectId, access.SubjectId, StringComparison.Ordinal) ||
            assessment.PolicyRevision != access.PolicyRevision ||
            !string.Equals(
                assessment.AssessmentHash,
                LearningFeedbackAuditPolicy.ComputeAssessmentHash(assessment),
                StringComparison.Ordinal))
        {
            throw AuditFailure("cortex_postgresql_audit_assessment_invalid", AuditOperation, access, assessment.ProposalId);
        }

        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(access, AuditOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                var storedProposal = await ReadProposalAsync(
                    connection,
                    transaction,
                    access,
                    assessment.ProposalId,
                    AuditOperation,
                    token).ConfigureAwait(false);
                if (storedProposal is null)
                    return AuditResult(LearningFeedbackAuditWriteDisposition.MissingProposal, assessment, "missing_proposal");
                if (!string.Equals(storedProposal.Value.ProposalHash, assessment.ProposalHash, StringComparison.Ordinal))
                    return AuditResult(LearningFeedbackAuditWriteDisposition.ProposalHashConflict, assessment, "proposal_hash_conflict");

                var assessmentEventId = AssessmentEventId(assessment);
                var affected = await InsertAuditEventAsync(
                    connection,
                    transaction,
                    access,
                    assessmentEventId,
                    AuditAssessmentEventKind,
                    assessment,
                    assessment.CreatedAt,
                    token).ConfigureAwait(false);
                var storedAssessment = await ReadAuditEventAsync<LearningFeedbackAuditAssessmentV1>(
                    connection,
                    transaction,
                    access,
                    assessmentEventId,
                    AuditAssessmentEventKind,
                    token).ConfigureAwait(false);
                if (storedAssessment is null)
                    throw AuditFailure("cortex_postgresql_audit_assessment_reread_missing", AuditOperation, access, assessment.ProposalId);
                if (!string.Equals(storedAssessment.AssessmentHash, assessment.AssessmentHash, StringComparison.Ordinal))
                    return AuditResult(LearningFeedbackAuditWriteDisposition.Conflict, storedAssessment, "assessment_conflict");
                if (affected == 0)
                    return AuditResult(LearningFeedbackAuditWriteDisposition.Existing, storedAssessment, "assessment_already_recorded");

                var storedDecision = await ReadDecisionAsync(
                    connection,
                    transaction,
                    access,
                    assessment.ProposalId,
                    AuditOperation,
                    token).ConfigureAwait(false);
                if (storedDecision is not null)
                {
                    return new LearningFeedbackAuditApplyResult(
                        LearningFeedbackAuditWriteDisposition.AlreadyReviewed,
                        storedAssessment,
                        null,
                        storedDecision,
                        "proposal_already_reviewed");
                }

                if (!string.Equals(assessment.Recommendation, "approve", StringComparison.Ordinal) ||
                    assessment.ReviewGrantId is null ||
                    autoApprovalDecision is null ||
                    !collectiveLibrary ||
                    !LearningFeedbackAuditPolicy.IsAutoApprovalKind(storedProposal.Value.FeedbackKind))
                {
                    return AuditResult(LearningFeedbackAuditWriteDisposition.Added, storedAssessment, "pending_human_review");
                }

                await AcquireGrantLockAsync(
                    connection,
                    transaction,
                    assessment.ReviewGrantId,
                    token).ConfigureAwait(false);
                var grant = await ReadAuditEventAsync<LearningFeedbackReviewGrantV1>(
                    connection,
                    transaction,
                    access,
                    GrantIssueEventId(assessment.ReviewGrantId),
                    ReviewGrantEventKind,
                    token).ConfigureAwait(false);
                var revoked = await ReadAuditEventAsync<LearningFeedbackReviewGrantRevocationV1>(
                    connection,
                    transaction,
                    access,
                    GrantRevocationEventId(assessment.ReviewGrantId),
                    ReviewGrantRevocationEventKind,
                    token).ConfigureAwait(false);
                var useCount = grant is null
                    ? long.MaxValue
                    : await CountGrantUsesAsync(
                        connection,
                        transaction,
                        access,
                        grant,
                        token).ConfigureAwait(false);
                if (!ValidGrantForAssessment(
                        grant,
                        revoked,
                        useCount,
                        access,
                        storedProposal.Value,
                        storedAssessment,
                        DateTimeOffset.UtcNow))
                {
                    return AuditResult(
                        LearningFeedbackAuditWriteDisposition.GrantUnavailable,
                        storedAssessment,
                        "grant_unavailable");
                }

                var decision = CaptureDecisionScope(access, autoApprovalDecision);
                var decisionPayload = _codec.Encode(
                    DecisionEntityKind,
                    access.TenantId,
                    access.Namespace,
                    decision.ProposalId,
                    decision.DecisionId,
                    decision);
                var decisionAffected = await InsertDecisionAsync(
                    connection,
                    transaction,
                    access,
                    decision,
                    decisionPayload,
                    token).ConfigureAwait(false);
                if (decisionAffected != 1)
                {
                    return AuditResult(
                        LearningFeedbackAuditWriteDisposition.AlreadyReviewed,
                        storedAssessment,
                        "proposal_already_reviewed");
                }

                var use = CreateGrantUse(grant!, storedAssessment, access);
                var useAffected = await InsertAuditEventAsync(
                    connection,
                    transaction,
                    access,
                    GrantUseEventId(grant!, storedAssessment),
                    ReviewGrantUseEventKind,
                    use,
                    use.CreatedAt,
                    token).ConfigureAwait(false);
                if (useAffected != 1)
                    throw AuditFailure("cortex_postgresql_review_grant_use_conflict", AuditOperation, access, assessment.ProposalId);

                await EnsureApprovedRetentionAsync(
                    connection,
                    transaction,
                    access,
                    storedProposal.Value,
                    decision,
                    0.60,
                    "delegated independent audit approval",
                    token).ConfigureAwait(false);
                return new LearningFeedbackAuditApplyResult(
                    LearningFeedbackAuditWriteDisposition.Added,
                    storedAssessment,
                    use,
                    decision,
                    "auto_approved");
            },
            cancellationToken);
    }

    public ValueTask<LearningFeedbackAuditAssessmentV1?> FindLatestAssessmentAsync(
        LearningFeedbackNamespaceAccess access,
        string proposalId,
        CancellationToken cancellationToken)
    {
        ValidateAccess(
            access,
            LearningFeedbackNamespaceCapability.Read,
            LearningFeedbackNamespaceCapability.Audit,
            AuditOperation,
            cancellationToken);
        if (!ValidIdentifier(proposalId) || !proposalId.StartsWith("proposal_", StringComparison.Ordinal))
            throw AuditFailure("cortex_postgresql_audit_proposal_id_invalid", AuditOperation, access, proposalId);

        var prefix = $"audit_{proposalId["proposal_".Length..][..16]}_%";
        return _transactions.ExecuteAsync<LearningFeedbackAuditAssessmentV1?>(
            PostgreSqlTransactionContext.FromLearning(access, AuditOperation),
            IsolationLevel.ReadCommitted,
            async (connection, transaction, token) =>
            {
                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    SelectLatestAssessmentSql,
                    new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
                    new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
                    new PostgreSqlParameter("event_kind", DbType.String, AuditAssessmentEventKind),
                    new PostgreSqlParameter("event_prefix", DbType.String, prefix));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    return null;
                var assessment = ReadAuditEvent<LearningFeedbackAuditAssessmentV1>(
                    reader,
                    access,
                    AuditAssessmentEventKind);
                if (!string.Equals(assessment.ProposalId, proposalId, StringComparison.Ordinal))
                    throw AuditFailure("cortex_postgresql_audit_assessment_scope_mismatch", AuditOperation, access, proposalId);
                return assessment;
            },
            cancellationToken);
    }

    private async ValueTask<int> InsertAuditEventAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        string eventId,
        string eventKind,
        T value,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
        where T : class
    {
        var stored = _codec.Encode(
            eventKind,
            access.TenantId,
            access.Namespace,
            eventId,
            revisionId: null,
            value);
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            InsertAuditEventSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("event_id", DbType.String, eventId),
            new PostgreSqlParameter("event_kind", DbType.String, eventKind),
            new PostgreSqlParameter("payload", DbType.Binary, stored.Payload),
            new PostgreSqlParameter("payload_sha256", DbType.String, stored.PayloadSha256),
            new PostgreSqlParameter("auth_version", DbType.Int32, stored.Seal.Version),
            new PostgreSqlParameter("auth_algorithm", DbType.String, stored.Seal.Algorithm),
            new PostgreSqlParameter("auth_key_id", DbType.String, stored.Seal.KeyId),
            new PostgreSqlParameter("auth_signature", DbType.Binary, stored.Seal.GetSignature()),
            new PostgreSqlParameter("occurred_at", DbType.DateTimeOffset, PostgreSqlTimestamp(occurredAt)));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T?> ReadAuditEventAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        string eventId,
        string eventKind,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            SelectAuditEventSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("event_id", DbType.String, eventId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAuditEvent<T>(reader, access, eventKind)
            : null;
    }

    private T ReadAuditEvent<T>(
        DbDataReader reader,
        LearningFeedbackNamespaceAccess access,
        string expectedEventKind)
        where T : class
    {
        var tenantId = reader.GetString(0);
        var namespaceName = reader.GetString(1);
        var eventId = reader.GetString(2);
        var eventKind = reader.GetString(3);
        if (!string.Equals(tenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(namespaceName, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(eventKind, expectedEventKind, StringComparison.Ordinal) ||
            reader.IsDBNull(4))
        {
            throw AuditFailure("cortex_postgresql_audit_event_scope_mismatch", AuditOperation, access, eventId);
        }
        return _codec.Decode<T>(
            eventKind,
            tenantId,
            namespaceName,
            eventId,
            revisionId: null,
            reader.GetFieldValue<byte[]>(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetFieldValue<byte[]>(9));
    }

    private static async ValueTask AcquireGrantLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        string grantId,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            LockGrantSql,
            new PostgreSqlParameter("grant_id", DbType.String, grantId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> CountGrantUsesAsync(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackReviewGrantV1 grant,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            CountGrantUsesSql,
            new PostgreSqlParameter("tenant_id", DbType.String, access.TenantId),
            new PostgreSqlParameter("namespace", DbType.String, access.Namespace),
            new PostgreSqlParameter("event_kind", DbType.String, ReviewGrantUseEventKind),
            new PostgreSqlParameter("event_prefix", DbType.String, $"guse_{grant.GrantHash[..16]}_%"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ValidGrantForAssessment(
        LearningFeedbackReviewGrantV1? grant,
        LearningFeedbackReviewGrantRevocationV1? revocation,
        long useCount,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackAuditAssessmentV1 assessment,
        DateTimeOffset now) =>
        grant is not null &&
        revocation is null &&
        grant.CollectiveOnly &&
        grant.PolicyRevision == access.PolicyRevision &&
        string.Equals(grant.TenantId, access.TenantId, StringComparison.Ordinal) &&
        string.Equals(grant.Namespace, access.Namespace, StringComparison.Ordinal) &&
        string.Equals(grant.DelegateAuthorityId, access.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(grant.DelegateSubjectId, access.SubjectId, StringComparison.Ordinal) &&
        grant.FeedbackKinds.Contains(proposal.FeedbackKind, StringComparer.Ordinal) &&
        assessment.CreatedAt >= grant.CreatedAt &&
        assessment.CreatedAt <= grant.ExpiresAt &&
        now >= grant.CreatedAt &&
        now <= grant.ExpiresAt &&
        useCount < grant.MaximumUses;

    private static LearningFeedbackReviewGrantUseV1 CreateGrantUse(
        LearningFeedbackReviewGrantV1 grant,
        LearningFeedbackAuditAssessmentV1 assessment,
        LearningFeedbackNamespaceAccess access)
    {
        var use = new LearningFeedbackReviewGrantUseV1(
            1,
            LearningFeedbackContractIds.ReviewGrantUse,
            LearningFeedbackContractIds.Canonicalization,
            $"use_{assessment.AssessmentHash}",
            grant.GrantId,
            grant.GrantHash,
            assessment.AssessmentId,
            assessment.AssessmentHash,
            assessment.ProposalId,
            assessment.ProposalHash,
            access.TenantId,
            access.Namespace,
            access.AuthorityId,
            access.SubjectId,
            assessment.CreatedAt,
            new string('0', 64));
        return use with { UseHash = LearningFeedbackAuditPolicy.ComputeUseHash(use) };
    }

    private static LearningFeedbackAuditApplyResult AuditResult(
        LearningFeedbackAuditWriteDisposition disposition,
        LearningFeedbackAuditAssessmentV1 assessment,
        string outcome) =>
        new(disposition, assessment, null, null, outcome);

    private static string GrantIssueEventId(string grantId) =>
        $"gissue_{grantId["grant_".Length..]}";

    private static string GrantRevocationEventId(string grantId) =>
        $"grevoke_{grantId["grant_".Length..]}";

    private static string AssessmentEventId(LearningFeedbackAuditAssessmentV1 assessment) =>
        $"audit_{assessment.ProposalId["proposal_".Length..][..16]}_{assessment.AssessmentHash}";

    private static string GrantUseEventId(
        LearningFeedbackReviewGrantV1 grant,
        LearningFeedbackAuditAssessmentV1 assessment) =>
        $"guse_{grant.GrantHash[..16]}_{assessment.AssessmentHash}";

    private static PostgreSqlPersistenceException AuditFailure(
        string code,
        string operation,
        LearningFeedbackNamespaceAccess access,
        string? entityId = null) =>
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
                ["entityId"] = ValidIdentifier(entityId) ? entityId : null
            });
}
