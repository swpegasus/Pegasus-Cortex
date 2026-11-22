using Pegasus.Shared.Diagnostics;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Import;

public enum LegacyCortexImportDisposition
{
    Copied,
    Existing,
    Conflict,
    Rejected
}

public sealed record LegacyCortexImportItemV1(
    string ExactSourcePath,
    string RelativePath,
    long SourceBytes,
    string SourceSha256,
    string StoredFileName,
    long StoredBytes,
    string StoredSha256,
    JournalPayloadProtectionMetadata Protection,
    string Disposition);

public sealed record LegacyCortexPublicManifestIdentity(
    string StoreId,
    string StoreEpoch,
    string ProjectId,
    string SourceRootId);

public sealed record LegacyCortexImportManifestV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string RunId,
    string ExactSourceRoot,
    DateTimeOffset CapturedAt,
    IReadOnlyList<LegacyCortexImportItemV1> Items,
    long SourceBytes,
    string ContentTreatment,
    bool Executable,
    string ManifestSha256);

public sealed record LegacyCortexImportResult
{
    public LegacyCortexImportResult(
        LegacyCortexImportDisposition disposition,
        LegacyCortexImportManifestV1? manifest,
        VerifiedLegacyCortexImport? verified,
        PegasusDiagnostic? diagnostic)
    {
        Disposition = disposition;
        Manifest = manifest is null ? null : LegacyCortexImportSnapshotPolicy.Snapshot(manifest);
        Verified = verified;
        Diagnostic = diagnostic;
    }

    public LegacyCortexImportDisposition Disposition { get; }
    public LegacyCortexImportManifestV1? Manifest { get; }
    public VerifiedLegacyCortexImport? Verified { get; }
    public PegasusDiagnostic? Diagnostic { get; }
}

/// <summary>
/// Opaque proof that the planner re-read and authenticated every retained protected copy.
/// Callers may inspect the immutable manifest, but cannot manufacture this capability.
/// </summary>
public sealed class VerifiedLegacyCortexImport
{
    private readonly LegacyCortexImportManifestV1 _manifest;
    private readonly string _boundManifestSha256;

    internal VerifiedLegacyCortexImport(
        LegacyCortexImportManifestV1 manifest,
        string storeId,
        string projectId)
    {
        _manifest = LegacyCortexImportSnapshotPolicy.Snapshot(manifest);
        _boundManifestSha256 = _manifest.ManifestSha256;
        LegacyCortexImportSnapshotPolicy.RequireBoundHash(_manifest, _boundManifestSha256);
        StoreId = storeId;
        ProjectId = projectId;
    }

    public LegacyCortexImportManifestV1 Manifest => GetBoundManifest();
    internal string StoreId { get; }
    internal string ProjectId { get; }

    internal LegacyCortexImportManifestV1 GetBoundManifest()
    {
        LegacyCortexImportSnapshotPolicy.RequireBoundHash(_manifest, _boundManifestSha256);
        return LegacyCortexImportSnapshotPolicy.Snapshot(_manifest);
    }
}

public sealed record LegacyCortexExportRequest(
    LegacyCortexPublicManifestIdentity Identity,
    string BundleIdPrefix,
    string TenantIdHint,
    string NamespaceHint,
    long? ObservedPolicyRevision,
    string? BaseServerRevision,
    string PreviousBundleHash,
    string PriorEventHash,
    long FirstSequence,
    DateTimeOffset CreatedAt);

public sealed record LegacyCortexExportResult(
    bool Accepted,
    IReadOnlyList<CortexOfflineBundleV1> Bundles,
    PegasusDiagnostic? Diagnostic);

/// <summary>
/// Requests one deterministic page. A null cursor starts at the request's initial chain values;
/// a non-null cursor must match those values exactly and must have a valid canonical hash.
/// </summary>
public sealed record LegacyCortexPageExportRequest(
    LegacyCortexExportRequest Export,
    CortexLegacyMigrationCursorV1? Cursor,
    int MaximumEvents);

public sealed record LegacyCortexPageExportResult(
    bool Accepted,
    CortexLegacyMigrationPageV1? Page,
    PegasusDiagnostic? Diagnostic);
