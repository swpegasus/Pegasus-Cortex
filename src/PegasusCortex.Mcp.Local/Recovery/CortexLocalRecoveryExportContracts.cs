using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal static class CortexLocalRecoveryExportContractIds
{
    public const string EmptySha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const string Manifest =
        "pegasus.cortex.local-recovery-export-manifest/1.0";
    public const string Receipt =
        "pegasus.cortex.local-recovery-export-receipt/1.0";
    public const string Result =
        "pegasus.cortex.local-recovery-export-result/1.0";
}

internal sealed record CortexLocalRecoveryExportEntry(
    string RepositoryProjectId,
    string Kind,
    string DocumentId,
    long Revision,
    string? PreviousHash,
    string IdempotencyKey,
    string ProducerId,
    string ContractId,
    string SearchText,
    string Sha256,
    int ByteCount,
    string RelativePath,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt);

internal sealed record CortexLocalRecoveryExportManifest(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string RunId,
    DateTimeOffset CapturedAt,
    string ProfileId,
    string RegistryHash,
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    IReadOnlyList<CortexLocalRecoveryExportEntry> Entries,
    int EntryCount,
    long ExportedBytes);

internal sealed record CortexLocalRecoveryExportReceipt(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string RunId,
    DateTimeOffset CapturedAt,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string ManifestRelativePath,
    string ManifestSha256,
    int ManifestByteCount,
    int EntryCount,
    long ExportedBytes,
    string ReceiptHash);

internal sealed record CortexLocalRecoveryExportResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string ProjectLinkId,
    string? ProjectId,
    string? ProjectNamespace,
    string RunId,
    string? ExportRelativePath,
    string? ManifestSha256,
    string? ReceiptSha256,
    int EntryCount,
    long ExportedBytes,
    bool SourceMutationAllowed,
    bool FallbackRequired,
    string FallbackInstructions,
    string? DiagnosticCode);

internal sealed record CortexLocalRecoveryExportReadback(
    CortexLocalRecoveryExportManifest Manifest,
    CortexLocalRecoveryExportReceipt Receipt,
    byte[] ProjectBriefUtf8,
    IReadOnlyList<CortexRetainedDocument> RetainedDocuments);

internal sealed record CortexLocalRecoveryExportLimits(
    int MaximumEntries = 10_000,
    long MaximumExportedBytes = 512L * 1024 * 1024,
    int MaximumManifestBytes = 8 * 1024 * 1024)
{
    public CortexLocalRecoveryExportLimits Validate()
    {
        if (MaximumEntries is < 1 or > 100_000 ||
            MaximumExportedBytes is < 1024 or > 2L * 1024 * 1024 * 1024 ||
            MaximumManifestBytes is < 1024 or > 32 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CortexLocalRecoveryExportLimits));
        }
        return this;
    }
}
