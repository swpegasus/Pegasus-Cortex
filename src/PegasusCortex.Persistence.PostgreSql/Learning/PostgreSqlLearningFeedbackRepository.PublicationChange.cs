using System.Data;
using System.Data.Common;
using System.Text;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;

namespace Pegasus.Cortex.Persistence.PostgreSql.Learning;

public sealed partial class PostgreSqlLearningFeedbackRepository
{
    private const string PublicationChangeOperation =
        "cortex.learning-feedback.publication-change";
    private const string PublicationChangeEventKind =
        "knowledge-publication-change";

    private const string RegisterPublicationOriginSql = """
        SELECT cortex_private.register_knowledge_publication_origin
        (
            @record_id,
            @revision_id,
            @content_hash,
            @proposal_id,
            @proposal_hash,
            @authority_id,
            @subject_id,
            @service_id,
            @policy_revision,
            @published_at
        );
        """;

    private const string ApplyPublicationChangeSql = """
        SELECT disposition, head_revision_id, head_generation
        FROM cortex_private.apply_knowledge_publication_change
        (
            @change_id,
            @change_hash,
            @action,
            @target_record_id,
            @expected_head_revision_id,
            @expected_head_generation,
            @replacement_record_id,
            @replacement_revision_id,
            @replacement_content_hash,
            @reason_code,
            @reason,
            @occurred_at,
            @marker_revision_id,
            @marker_taxonomy,
            @marker_title,
            @marker_summary,
            @marker_freshness_state,
            @marker_sensitivity,
            @marker_record_status,
            @marker_content_hash,
            @marker_content,
            @marker_keyword_text,
            CAST(@marker_applicability AS jsonb),
            @marker_payload,
            @marker_payload_sha256,
            @marker_auth_version,
            @marker_auth_algorithm,
            @marker_auth_key_id,
            @marker_auth_signature,
            @change_payload,
            @change_payload_sha256,
            @change_auth_version,
            @change_auth_algorithm,
            @change_auth_key_id,
            @change_auth_signature
        );
        """;

    public ValueTask<LearningFeedbackPublicationChangeWriteResult>
        ApplyPublicationChangeAsync(
            LearningFeedbackNamespaceAccess access,
            LearningFeedbackPublicationChangeV1 change,
            CancellationToken cancellationToken)
    {
        ValidatePublicationChange(access, change, cancellationToken);
        var captured = change with
        {
            Replacement = change.Replacement is null
                ? null
                : change.Replacement with { }
        };

        return _transactions.ExecuteAsync(
            PostgreSqlTransactionContext.FromLearning(
                access,
                PublicationChangeOperation),
            IsolationLevel.Serializable,
            async (connection, transaction, token) =>
            {
                var marker = CreatePublicationChangeMarker(captured);
                var markerStored = _codec.Encode(
                    KnowledgeEntityKind,
                    access.TenantId,
                    access.Namespace,
                    marker.RecordId,
                    marker.RevisionId,
                    marker);
                var changeStored = _codec.Encode(
                    PublicationChangeEventKind,
                    access.TenantId,
                    access.Namespace,
                    captured.ChangeId,
                    revisionId: null,
                    captured);
                var applicabilityJson = Encoding.UTF8.GetString(
                    CortexCanonicalJson.Serialize(marker.Applicability));

                await using var command = PostgreSqlCommandFactory.Create(
                    connection,
                    transaction,
                    ApplyPublicationChangeSql,
                    new PostgreSqlParameter("change_id", DbType.String, captured.ChangeId),
                    new PostgreSqlParameter("change_hash", DbType.String, captured.ChangeHash),
                    new PostgreSqlParameter("action", DbType.String, captured.Action),
                    new PostgreSqlParameter("target_record_id", DbType.String, captured.TargetRecordId),
                    new PostgreSqlParameter(
                        "expected_head_revision_id",
                        DbType.String,
                        captured.ExpectedHeadRevisionId),
                    new PostgreSqlParameter(
                        "expected_head_generation",
                        DbType.Int64,
                        captured.ExpectedHeadGeneration),
                    new PostgreSqlParameter(
                        "replacement_record_id",
                        DbType.String,
                        captured.Replacement?.RecordId),
                    new PostgreSqlParameter(
                        "replacement_revision_id",
                        DbType.String,
                        captured.Replacement?.RevisionId),
                    new PostgreSqlParameter(
                        "replacement_content_hash",
                        DbType.String,
                        captured.Replacement?.ContentHash),
                    new PostgreSqlParameter("reason_code", DbType.String, captured.ReasonCode),
                    new PostgreSqlParameter("reason", DbType.String, captured.Reason),
                    new PostgreSqlParameter(
                        "occurred_at",
                        DbType.DateTimeOffset,
                        PostgreSqlTimestamp(captured.OccurredAtUtc)),
                    new PostgreSqlParameter("marker_revision_id", DbType.String, marker.RevisionId),
                    new PostgreSqlParameter("marker_taxonomy", DbType.String, marker.Taxonomy),
                    new PostgreSqlParameter("marker_title", DbType.String, marker.Title),
                    new PostgreSqlParameter("marker_summary", DbType.String, marker.Summary),
                    new PostgreSqlParameter(
                        "marker_freshness_state",
                        DbType.String,
                        marker.Freshness.State),
                    new PostgreSqlParameter("marker_sensitivity", DbType.String, marker.Sensitivity),
                    new PostgreSqlParameter("marker_record_status", DbType.String, marker.RecordStatus),
                    new PostgreSqlParameter("marker_content_hash", DbType.String, marker.ContentHash),
                    new PostgreSqlParameter("marker_content", DbType.String, marker.Content),
                    new PostgreSqlParameter(
                        "marker_keyword_text",
                        DbType.String,
                        string.Join(' ', marker.Keywords)),
                    new PostgreSqlParameter("marker_applicability", DbType.String, applicabilityJson),
                    new PostgreSqlParameter("marker_payload", DbType.Binary, markerStored.Payload),
                    new PostgreSqlParameter(
                        "marker_payload_sha256",
                        DbType.String,
                        markerStored.PayloadSha256),
                    new PostgreSqlParameter(
                        "marker_auth_version",
                        DbType.Int32,
                        markerStored.Seal.Version),
                    new PostgreSqlParameter(
                        "marker_auth_algorithm",
                        DbType.String,
                        markerStored.Seal.Algorithm),
                    new PostgreSqlParameter(
                        "marker_auth_key_id",
                        DbType.String,
                        markerStored.Seal.KeyId),
                    new PostgreSqlParameter(
                        "marker_auth_signature",
                        DbType.Binary,
                        markerStored.Seal.GetSignature()),
                    new PostgreSqlParameter("change_payload", DbType.Binary, changeStored.Payload),
                    new PostgreSqlParameter(
                        "change_payload_sha256",
                        DbType.String,
                        changeStored.PayloadSha256),
                    new PostgreSqlParameter(
                        "change_auth_version",
                        DbType.Int32,
                        changeStored.Seal.Version),
                    new PostgreSqlParameter(
                        "change_auth_algorithm",
                        DbType.String,
                        changeStored.Seal.Algorithm),
                    new PostgreSqlParameter(
                        "change_auth_key_id",
                        DbType.String,
                        changeStored.Seal.KeyId),
                    new PostgreSqlParameter(
                        "change_auth_signature",
                        DbType.Binary,
                        changeStored.Seal.GetSignature()));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw PublicationFailure(
                        "cortex_postgresql_publication_change_result_missing",
                        access,
                        captured);
                }

                var disposition = ParsePublicationChangeDisposition(
                    reader.GetString(0),
                    access,
                    captured);
                var headRevisionId = reader.IsDBNull(1) ? null : reader.GetString(1);
                long? headGeneration = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                if (await reader.ReadAsync(token).ConfigureAwait(false) ||
                    !ValidPublicationChangeStoreResult(
                        disposition,
                        captured,
                        headRevisionId,
                        headGeneration))
                {
                    throw PublicationFailure(
                        "cortex_postgresql_publication_change_result_invalid",
                        access,
                        captured);
                }

                return new LearningFeedbackPublicationChangeWriteResult(
                    disposition,
                    captured.ChangeId,
                    captured.TargetRecordId,
                    headRevisionId,
                    headGeneration,
                    captured.Replacement?.RecordId,
                    captured.Replacement?.RevisionId);
            },
            cancellationToken);
    }

    private async ValueTask RegisterPublicationOriginAsync(
        DbConnection connection,
        DbTransaction transaction,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackProposalV1 proposal,
        CortexKnowledgeRecord record,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken)
    {
        await using var command = PostgreSqlCommandFactory.Create(
            connection,
            transaction,
            RegisterPublicationOriginSql,
            new PostgreSqlParameter("record_id", DbType.String, record.RecordId),
            new PostgreSqlParameter("revision_id", DbType.String, record.RevisionId),
            new PostgreSqlParameter("content_hash", DbType.String, record.ContentHash),
            new PostgreSqlParameter("proposal_id", DbType.String, proposal.ProposalId),
            new PostgreSqlParameter("proposal_hash", DbType.String, proposal.ProposalHash),
            new PostgreSqlParameter("authority_id", DbType.String, proposal.Source.AuthorityId),
            new PostgreSqlParameter("subject_id", DbType.String, proposal.Source.SubjectId),
            new PostgreSqlParameter("service_id", DbType.String, proposal.Source.ServiceId),
            new PostgreSqlParameter(
                "policy_revision",
                DbType.Int64,
                proposal.Screening.PolicyRevision),
            new PostgreSqlParameter(
                "published_at",
                DbType.DateTimeOffset,
                PostgreSqlTimestamp(publishedAt)));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not bool)
        {
            throw Failure(
                "cortex_postgresql_publication_origin_result_invalid",
                ReviewOperation,
                access,
                proposal.ProposalId);
        }
    }

    internal static CortexKnowledgeRecord CreatePublicationChangeMarker(
        LearningFeedbackPublicationChangeV1 change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var status = string.Equals(
            change.Action,
            LearningFeedbackPublicationChangeActions.Supersede,
            StringComparison.Ordinal)
            ? "superseded"
            : "tombstone";
        var title = status == "superseded"
            ? "Superseded knowledge publication"
            : "Withdrawn knowledge publication";
        var content =
            $"Publication action: {change.Action}\n" +
            $"Reason code: {change.ReasonCode}\n" +
            $"Reason: {change.Reason}";
        var contentHash = CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(content));
        var observedAt = PostgreSqlTimestamp(change.OccurredAtUtc);

        return new CortexKnowledgeRecord(
            change.TargetRecordId,
            $"krev_{change.ChangeHash}",
            change.Namespace,
            "publication-change",
            $"publication-change-{change.Action}",
            title,
            $"Author-owned publication change {change.ChangeId}.",
            content,
            Keywords: Array.AsReadOnly(
                new[] { "publication-change", change.Action, change.ReasonCode }),
            Applicability: Array.Empty<CortexKnowledgeApplicability>(),
            Evidence: Array.Empty<CortexKnowledgeEvidence>(),
            new CortexKnowledgeProvenance(
                "pegasus.cortex.learning-feedback-publication-change/1.0",
                SourceReferences: Array.AsReadOnly(new[] { change.ChangeId }),
                SourceRevisions: Array.Empty<CortexKnowledgeSourceRevision>(),
                Transformations: Array.AsReadOnly(
                    new[] { "author-change", "immutable-marker" })),
            new CortexKnowledgeConfidence(
                1.0,
                "authenticated author publication change"),
            new CortexKnowledgeFreshness(
                "current",
                observedAt,
                ReviewAfter: null,
                Basis: "author-owned publication status marker"),
            "internal",
            status,
            TrainingEligibility: false,
            CortexQueryPolicy.ContentTreatment,
            Executable: false,
            contentHash);
    }

    private static void ValidatePublicationChange(
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackPublicationChangeV1 change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(change);
        cancellationToken.ThrowIfCancellationRequested();
        if (access.Capability != LearningFeedbackNamespaceCapability.Submit ||
            !string.Equals(change.TenantId, access.TenantId, StringComparison.Ordinal) ||
            !string.Equals(change.Namespace, access.Namespace, StringComparison.Ordinal) ||
            !string.Equals(change.AuthorityId, access.AuthorityId, StringComparison.Ordinal) ||
            !string.Equals(change.SubjectId, access.SubjectId, StringComparison.Ordinal) ||
            !string.Equals(change.ServiceId, access.ServiceId, StringComparison.Ordinal) ||
            change.PolicyRevision != access.PolicyRevision ||
            !LearningFeedbackPublicationChangePolicy.HasValidIdentity(change) ||
            !ExactPrefixedHash(change.TargetRecordId, "krec_") ||
            !ExactPrefixedHash(change.ExpectedHeadRevisionId, "krev_") ||
            change.ExpectedHeadGeneration is < 1 or long.MaxValue ||
            change.Replacement is not null &&
            (!ExactPrefixedHash(change.Replacement.RecordId, "krec_") ||
             !ExactPrefixedHash(change.Replacement.RevisionId, "krev_") ||
             !ValidHash(change.Replacement.ContentHash)))
        {
            throw PublicationFailure(
                "cortex_postgresql_publication_change_invalid",
                access,
                change);
        }
    }

    private static LearningFeedbackPublicationChangeWriteDisposition
        ParsePublicationChangeDisposition(
            string value,
            LearningFeedbackNamespaceAccess access,
            LearningFeedbackPublicationChangeV1 change) =>
        value switch
        {
            "applied" => LearningFeedbackPublicationChangeWriteDisposition.Applied,
            "existing" => LearningFeedbackPublicationChangeWriteDisposition.Existing,
            "ownership_unavailable" =>
                LearningFeedbackPublicationChangeWriteDisposition.OwnershipUnavailable,
            "not_owner" => LearningFeedbackPublicationChangeWriteDisposition.NotOwner,
            "stale_head" => LearningFeedbackPublicationChangeWriteDisposition.StaleHead,
            "replacement_unavailable" =>
                LearningFeedbackPublicationChangeWriteDisposition.ReplacementUnavailable,
            "conflict" => LearningFeedbackPublicationChangeWriteDisposition.Conflict,
            _ => throw PublicationFailure(
                "cortex_postgresql_publication_change_disposition_invalid",
                access,
                change)
        };

    private static bool ValidPublicationChangeStoreResult(
        LearningFeedbackPublicationChangeWriteDisposition disposition,
        LearningFeedbackPublicationChangeV1 change,
        string? headRevisionId,
        long? headGeneration) =>
        disposition switch
        {
            LearningFeedbackPublicationChangeWriteDisposition.Applied or
            LearningFeedbackPublicationChangeWriteDisposition.Existing =>
                string.Equals(
                    headRevisionId,
                    $"krev_{change.ChangeHash}",
                    StringComparison.Ordinal) &&
                headGeneration == change.ExpectedHeadGeneration + 1,
            LearningFeedbackPublicationChangeWriteDisposition.StaleHead =>
                headRevisionId is null && headGeneration is null ||
                ExactPrefixedHash(headRevisionId, "krev_") && headGeneration is > 0,
            LearningFeedbackPublicationChangeWriteDisposition.ReplacementUnavailable =>
                ExactPrefixedHash(headRevisionId, "krev_") && headGeneration is > 0,
            LearningFeedbackPublicationChangeWriteDisposition.OwnershipUnavailable or
            LearningFeedbackPublicationChangeWriteDisposition.NotOwner or
            LearningFeedbackPublicationChangeWriteDisposition.Conflict =>
                headRevisionId is null && headGeneration is null,
            _ => false
        };

    private static bool ExactPrefixedHash(string? value, string prefix) =>
        value is not null &&
        value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Skip(prefix.Length).All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static PostgreSqlPersistenceException PublicationFailure(
        string code,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackPublicationChangeV1 change) =>
        new(
            code,
            PublicationChangeOperation,
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenantId"] = ValidIdentifier(access.TenantId) ? access.TenantId : null,
                ["namespace"] = ValidNamespace(access.Namespace) ? access.Namespace : null,
                ["changeId"] = change is not null &&
                    ExactPrefixedHash(change.ChangeId, "pchg_")
                        ? change.ChangeId
                        : null,
                ["targetRecordId"] = change is not null &&
                    ExactPrefixedHash(change.TargetRecordId, "krec_")
                        ? change.TargetRecordId
                        : null,
                ["replacementRecordId"] = change?.Replacement is not null &&
                    ExactPrefixedHash(change.Replacement.RecordId, "krec_")
                        ? change.Replacement.RecordId
                        : null,
                ["replacementRevisionId"] = change?.Replacement is not null &&
                    ExactPrefixedHash(change.Replacement.RevisionId, "krev_")
                        ? change.Replacement.RevisionId
                        : null
            });
}
