using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Persistence.PostgreSql.Retention;

internal sealed record PostgreSqlRetentionChainRecord(
    string ProjectId,
    string Kind,
    string DocumentId,
    DateTimeOffset CreatedAt);

internal sealed record PostgreSqlRetentionAuditRecord(
    string AuditId,
    string ProjectId,
    string Kind,
    string DocumentId,
    long Revision,
    string ContentHash,
    string ProducerId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Complete physical document row. Every denormalized projection is treated as
/// untrusted until the canonical payload and row seal are authenticated and the
/// projection is compared byte-for-value with the decoded document.
/// </summary>
internal sealed record PostgreSqlRetentionProjectedDocument(
    string ProjectId,
    string Kind,
    string DocumentId,
    long Revision,
    string? PreviousHash,
    string IdempotencyKey,
    string ProducerId,
    string ContractId,
    string ContentHash,
    string SearchText,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    byte[] Payload,
    string PayloadSha256,
    int AuthVersion,
    string AuthAlgorithm,
    string AuthKeyId,
    byte[] AuthSignature);

internal static class PostgreSqlRetentionRecordIdentity
{
    public const string ChainEntityKind = "retained-chain";
    public const string DocumentEntityKind = "retained-document";
    public const string AuditEntityKind = "retention-audit";

    public static string ChainPrimaryId(
        string projectId,
        string kind,
        string documentId) =>
        CortexCanonicalJson.Sha256(
            $"{projectId}\n{kind}\n{documentId}");

    public static string DocumentRevisionId(long revision) =>
        revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string AuditId(CortexRetentionAppendRequest request) =>
        CortexCanonicalJson.Sha256(
            $"{request.ProjectId}\n{request.Kind}\n{request.DocumentId}\n" +
            $"{request.Revision}\n{request.ContentHash}");
}
