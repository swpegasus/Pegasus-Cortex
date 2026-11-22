using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.LearningExports.Contracts;

public static class LearningExportContractIds
{
    public const string InputBundle = "pegasus.cortex.learning-input-bundle/1.0";
    public const string DatasetDefinition = "pegasus.cortex.learning-dataset-definition/1.0";
    public const string DatasetManifest = "pegasus.cortex.learning-dataset-manifest/1.0";
    public const string DatasetItem = "pegasus.cortex.learning-dataset-item/1.0";
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const string ContentTreatment = "untrusted_data";
    public const string CanonicalJsonlAdapter = "canonical-jsonl";
    public const string CanonicalJsonlAdapterVersion = "1.0";
    public const string StableSplitAlgorithm = "sha256-leakage-group-v1";
}

public sealed record LearningExportReviewV1(
    bool Eligible,
    string State,
    string UsageRights,
    string RedactionDecision,
    string PrivacyReview,
    string SecretReview,
    string ReasoningReview,
    string ProviderCredentialReview,
    long PolicyRevision,
    string ApprovalAuthorityId,
    string ApprovalDecisionId,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt);

/// <summary>
/// An explicit, immutable source revision prepared by an upstream review workflow.
/// This contract deliberately has no repository or live-query behavior.
/// </summary>
public sealed record LearningRecordRevisionV1(
    int SchemaVersion,
    string RecordId,
    string RevisionId,
    string Namespace,
    string SourceKind,
    string SourceServiceId,
    string SourceArtifactId,
    string LeakageGroupId,
    IReadOnlyDictionary<string, string> Payload,
    string Sensitivity,
    LearningExportReviewV1 Review,
    DateTimeOffset CreatedAt,
    string ContentTreatment,
    bool Executable,
    string SourceContentHash,
    string ReviewEnvelopeHash);

public sealed record LearningInputBundleV1(
    int SchemaVersion,
    string ContractId,
    string BundleId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<LearningRecordRevisionV1> Records,
    string ContentTreatment,
    bool Executable,
    string BundleHash);

public sealed record LearningDatasetSelectionV1(
    string RecordId,
    string RevisionId,
    string SourceContentHash,
    string ReviewEnvelopeHash);

public sealed record LearningTransformationSpecV1(
    string Id,
    string Version,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record LearningSplitPolicyV1(
    string Algorithm,
    string Salt,
    int TrainBasisPoints,
    int ValidationBasisPoints,
    int TestBasisPoints);

public sealed record LearningDefinitionApprovalV1(
    string State,
    long PolicyRevision,
    string AuthorityId,
    string DecisionId,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt);

public sealed record LearningDatasetDefinitionV1(
    int SchemaVersion,
    string ContractId,
    string DatasetId,
    string DefinitionRevisionId,
    string DisplayName,
    string SourceBundleId,
    IReadOnlyList<LearningDatasetSelectionV1> SelectedRevisions,
    IReadOnlyList<string> AllowedNamespaces,
    IReadOnlyList<string> IntendedUses,
    LearningSplitPolicyV1 SplitPolicy,
    IReadOnlyList<LearningTransformationSpecV1> Transformations,
    string AdapterId,
    string AdapterVersion,
    DateTimeOffset CreatedAt,
    LearningDefinitionApprovalV1 Approval,
    string DefinitionHash);

public sealed record LearningDatasetItemV1(
    int SchemaVersion,
    string ContractId,
    string ItemId,
    string DatasetId,
    string RecordId,
    string RevisionId,
    string Namespace,
    string SourceKind,
    string Split,
    IReadOnlyDictionary<string, string> Payload,
    string ContentTreatment,
    bool Executable);

public sealed record LearningManifestItemV1(
    string ItemId,
    string RecordId,
    string RevisionId,
    string Namespace,
    string Split,
    string LeakageGroupHash,
    string SourceContentHash,
    string ReviewEnvelopeHash,
    string TransformationSetHash,
    string OutputRecordSha256,
    long OutputOffset,
    int OutputBytes,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    long ApprovalPolicyRevision,
    string ApprovalAuthorityId,
    string ApprovalDecisionId,
    string UsageRights,
    string Sensitivity,
    string RedactionDecision);

public sealed record LearningSplitSummaryV1(
    string Name,
    int ItemCount,
    string LineageHash);

public sealed record LearningArtifactV1(
    string RelativePath,
    string MediaType,
    string AdapterId,
    string AdapterVersion,
    string Sha256,
    long Bytes,
    int ItemCount);

/// <summary>
/// Canonical provider-neutral record of exactly what was selected and emitted.
/// It records data preparation only; Cortex does not perform model training.
/// </summary>
public sealed record LearningDatasetManifestV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string DatasetId,
    string DefinitionRevisionId,
    string DefinitionHash,
    string BuildId,
    DateTimeOffset CreatedAt,
    string SourceBundleId,
    string SourceBundleHash,
    LearningDefinitionApprovalV1 ExportApproval,
    IReadOnlyList<string> IntendedUses,
    LearningSplitPolicyV1 SplitPolicy,
    IReadOnlyList<LearningTransformationSpecV1> Transformations,
    IReadOnlyList<LearningManifestItemV1> Items,
    IReadOnlyList<LearningSplitSummaryV1> Splits,
    IReadOnlyList<LearningArtifactV1> Artifacts,
    string ContentTreatment,
    bool Executable,
    bool ModelTrainingPerformed,
    string ManifestHash);

public sealed record LearningExportOptions
{
    public int MaxRecords { get; init; } = 2_000;
    public int MaxPayloadFields { get; init; } = 32;
    public int MaxPayloadCharacters { get; init; } = 64 * 1024;
    public int MaxRecordBytes { get; init; } = 512 * 1024;
    public long MaxInputBundleBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaxArtifactBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaxManifestBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxDiagnostics { get; init; } = 100;

    public void Validate()
    {
        var maximumEncodedPayload = checked((long)MaxPayloadCharacters * 4 + 16 * 1024);
        var maximumArtifactProduct = checked((long)MaxRecords * MaxRecordBytes);
        var minimumManifestBudget = checked(16L * 1024 + (long)MaxRecords * 4096);
        if (MaxRecords is < 1 or > 10_000 ||
            MaxPayloadFields is < 1 or > 256 ||
            MaxPayloadCharacters is < 256 or > 512 * 1024 ||
            MaxRecordBytes is < 32 * 1024 or > 1024 * 1024 ||
            MaxInputBundleBytes is < 64 * 1024 or > 8L * 1024 * 1024 * 1024 ||
            MaxArtifactBytes is < 1024 * 1024 or > 8L * 1024 * 1024 * 1024 ||
            MaxManifestBytes is < 64 * 1024 or > 256 * 1024 * 1024 ||
            MaxDiagnostics is < 1 or > 10_000 ||
            maximumEncodedPayload > MaxRecordBytes ||
            maximumArtifactProduct > MaxArtifactBytes ||
            minimumManifestBudget > MaxManifestBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LearningExportOptions),
                "Learning-export limits are invalid or unreasonably large.");
        }
    }
}

/// <summary>
/// Server/operator-derived authority. Serialized source or definition claims cannot create this grant.
/// </summary>
public sealed record LearningExportAuthorityContext(
    string SubjectId,
    long PolicyRevision,
    bool CanPlan,
    bool CanBuild,
    IReadOnlySet<string> TrustedApprovalAuthorities,
    IReadOnlyDictionary<string, string> ApprovedDecisionHashes,
    IReadOnlySet<string> ExportableNamespaces,
    string? ApprovedOutputRoot);

public sealed record LearningDatasetBuildRequest(
    bool PublishArtifacts,
    string? OutputRoot,
    LearningExportAuthorityContext Authority);

public sealed record LearningDatasetBuildResult(
    bool Accepted,
    bool Published,
    string? ArtifactDirectory,
    LearningDatasetManifestV1? Manifest,
    IReadOnlyList<PegasusDiagnostic> Diagnostics);
