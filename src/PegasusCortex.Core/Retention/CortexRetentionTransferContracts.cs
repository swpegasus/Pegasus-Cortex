using Pegasus.Cortex.Contracts.WorkPackets;

namespace Pegasus.Cortex.Core.Retention;

/// <summary>
/// Provider-neutral bounds for one copy-first project transfer. The bundle is
/// intentionally smaller than a database backup and contains no storage paths,
/// credentials, ACLs, operating-system identity, or mutable provider metadata.
/// </summary>
public static class CortexRetentionTransferContract
{
    public const int SchemaVersion = 1;
    public const string ContractId =
        "pegasus.cortex.retention-transfer-bundle/1.0";
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const string EmptySha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";
    public const int MaximumDocuments = 10_000;
    public const int MaximumDerivedBriefs = 256;
    public const int MaximumEvidenceHashes = 32;
    public const long MaximumCanonicalPayloadBytes = 512L * 1024 * 1024;
}

/// <summary>
/// Exact logical project scope bound by the transfer. Import authorization is
/// still supplied by the host; these values are untrusted data, not grants.
/// </summary>
public sealed record CortexRetentionTransferProjectScope(
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string AuthoritySetHash);

/// <summary>
/// Server-derived authorization scope supplied separately from the untrusted
/// bundle. The import service requires an exact four-field identity match
/// before it reads target heads or performs any write.
/// </summary>
public sealed record CortexRetentionTransferImportScope(
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace);

/// <summary>
/// Sanitized export provenance. Provider and tool identifiers are logical
/// product identifiers rather than machine, account, path, or credential data.
/// </summary>
public sealed record CortexRetentionTransferProvenance(
    string SourceProviderId,
    long SourceStoreEpoch,
    string ExportToolId,
    IReadOnlyList<string> EvidenceHashes);

/// <summary>
/// One exact retained repository revision. Sequence is part of the sealed
/// contract so an importer can prove deterministic dependency ordering.
/// </summary>
public sealed record CortexRetentionTransferDocument(
    int Sequence,
    string RepositoryProjectId,
    string Kind,
    string DocumentId,
    long Revision,
    string? PreviousHash,
    string IdempotencyKey,
    string ProducerId,
    string ContractId,
    byte[] CanonicalUtf8,
    string ContentHash,
    string SearchText,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt)
{
    public static CortexRetentionTransferDocument FromRetainedDocument(
        CortexRetainedDocument document,
        int sequence)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new CortexRetentionTransferDocument(
            sequence,
            document.ProjectId,
            document.Kind,
            document.DocumentId,
            document.Revision,
            document.PreviousHash,
            document.IdempotencyKey,
            document.ProducerId,
            document.ContractId,
            document.CanonicalUtf8.ToArray(),
            document.ContentHash,
            document.SearchText,
            document.CapturedAt,
            document.ExpiresAt);
    }

    public CortexRetentionAppendRequest ToAppendRequest() => new(
        RepositoryProjectId,
        Kind,
        DocumentId,
        Revision,
        PreviousHash,
        IdempotencyKey,
        ProducerId,
        ContractId,
        CanonicalUtf8.ToArray(),
        ContentHash,
        SearchText,
        CapturedAt,
        ExpiresAt);

    public CortexRetentionTransferDocument Snapshot() =>
        this with { CanonicalUtf8 = CanonicalUtf8.ToArray() };
}

/// <summary>
/// A sealed, provider-neutral project transfer. AuthoritativeDocuments contains
/// only typed retained assertions. DerivedBriefEvidence is comparison evidence
/// and must never be appended as authoritative state by the importer.
/// </summary>
public sealed record CortexRetentionTransferBundleV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string BundleId,
    DateTimeOffset CapturedAt,
    CortexRetentionTransferProjectScope Project,
    CortexRetentionTransferProvenance Provenance,
    IReadOnlyList<CortexRetentionTransferDocument> AuthoritativeDocuments,
    IReadOnlyList<CortexRetentionTransferDocument> DerivedBriefEvidence,
    string BundleHash);

public static class CortexRetentionTransferImportDispositions
{
    public const string Imported = "imported";
    public const string Idempotent = "idempotent";
    public const string Rejected = "rejected";
    public const string BriefRegenerationFailed = "brief-regeneration-failed";
}

/// <summary>
/// Result returned by a host-owned project-brief regenerator. It records only
/// hashes and identifiers; historical derived bytes remain comparison evidence.
/// </summary>
public sealed record CortexRetentionTransferBriefRegenerationResult(
    bool Accepted,
    string Disposition,
    string? ContextSetId,
    string? SourceHash,
    string? AuthoritySetHash,
    string? MatchingEvidenceContentHash,
    string? DiagnosticCode);

/// <summary>
/// Host seam for regenerating the current brief from the imported authoritative
/// task snapshot. Implementations must not install a comparison brief directly.
/// </summary>
public interface ICortexRetentionTransferBriefRegenerator
{
    ValueTask<CortexRetentionTransferBriefRegenerationResult> RegenerateAsync(
        CortexRetentionTransferProjectScope project,
        AgentTaskSnapshotV1 authoritativeTaskSnapshot,
        IReadOnlyList<CortexRetentionTransferDocument> comparisonEvidence,
        CancellationToken cancellationToken);
}

public sealed record CortexRetentionTransferImportResult(
    bool Accepted,
    string Disposition,
    string BundleId,
    string BundleHash,
    int DocumentCount,
    int AppendedCount,
    int ExistingCount,
    CortexRetentionTransferBriefRegenerationResult? BriefRegeneration,
    string? DiagnosticCode);

/// <summary>
/// Safe validation failure containing only a stable code. Rejected content is
/// never copied into exception messages or diagnostics.
/// </summary>
public sealed class CortexRetentionTransferValidationException : Exception
{
    public CortexRetentionTransferValidationException(string code)
        : base("The retention transfer bundle failed validation.")
    {
        if (string.IsNullOrWhiteSpace(code) || code.Any(char.IsControl))
            throw new ArgumentException("A stable validation code is required.", nameof(code));
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Unexpected provider or brief-regenerator failure after bundle validation.
/// Counts make a retry decision explicit and the inner exception preserves the
/// provider-owned cause without copying untrusted bundle content into text.
/// </summary>
public sealed class CortexRetentionTransferImportException : Exception
{
    public CortexRetentionTransferImportException(
        string code,
        string bundleId,
        int appendedCount,
        int existingCount,
        Exception innerException)
        : base("The retention transfer import encountered an unexpected dependency failure.", innerException)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Any(char.IsControl))
            throw new ArgumentException("A stable import failure code is required.", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        ArgumentNullException.ThrowIfNull(innerException);
        if (appendedCount < 0 || existingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(appendedCount));
        Code = code;
        BundleId = bundleId;
        AppendedCount = appendedCount;
        ExistingCount = existingCount;
    }

    public string Code { get; }
    public string BundleId { get; }
    public int AppendedCount { get; }
    public int ExistingCount { get; }
}
