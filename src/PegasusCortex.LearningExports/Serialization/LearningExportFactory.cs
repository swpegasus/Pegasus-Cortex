using Pegasus.Cortex.LearningExports.Contracts;

namespace Pegasus.Cortex.LearningExports.Serialization;

public static class LearningExportFactory
{
    private static readonly string EmptyHash = new('0', 64);

    public static LearningRecordRevisionV1 CreateRecord(
        string recordId,
        string revisionId,
        string @namespace,
        string sourceKind,
        string sourceServiceId,
        string sourceArtifactId,
        string leakageGroupId,
        IReadOnlyDictionary<string, string> payload,
        string sensitivity,
        LearningExportReviewV1 review,
        DateTimeOffset createdAt,
        string? sourceContentHash = null)
    {
        var record = new LearningRecordRevisionV1(
            1,
            Require(recordId),
            Require(revisionId),
            Require(@namespace),
            Require(sourceKind),
            Require(sourceServiceId),
            Require(sourceArtifactId),
            Require(leakageGroupId),
            CanonicalDictionary(payload),
            Require(sensitivity),
            review ?? throw new ArgumentNullException(nameof(review)),
            createdAt,
            LearningExportContractIds.ContentTreatment,
            false,
            sourceContentHash ?? EmptyHash,
            EmptyHash);

        if (sourceContentHash is null)
            record = record with { SourceContentHash = ComputeSourceContentHash(record) };
        return record with { ReviewEnvelopeHash = ComputeReviewEnvelopeHash(record) };
    }

    public static LearningInputBundleV1 CreateInputBundle(
        DateTimeOffset generatedAt,
        IEnumerable<LearningRecordRevisionV1> records) =>
        CreateInputBundle(generatedAt, records, CancellationToken.None);

    public static LearningInputBundleV1 CreateInputBundle(
        DateTimeOffset generatedAt,
        IEnumerable<LearningRecordRevisionV1> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new List<LearningRecordRevisionV1>();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.Add(RequireRecord(record, nameof(records)));
        }
        var orderedRecords = snapshot
            .OrderBy(item => item.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.RevisionId, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var bundle = new LearningInputBundleV1(
            1,
            LearningExportContractIds.InputBundle,
            string.Empty,
            generatedAt,
            orderedRecords,
            LearningExportContractIds.ContentTreatment,
            false,
            EmptyHash);
        var hash = ComputeBundleHash(bundle, cancellationToken);
        return bundle with { BundleId = $"lbundle_{hash}", BundleHash = hash };
    }

    public static LearningDatasetDefinitionV1 CreateDefinition(
        string datasetId,
        string displayName,
        string sourceBundleId,
        IEnumerable<LearningDatasetSelectionV1> selectedRevisions,
        IEnumerable<string> allowedNamespaces,
        IEnumerable<string> intendedUses,
        LearningSplitPolicyV1 splitPolicy,
        IEnumerable<LearningTransformationSpecV1> transformations,
        string adapterId,
        string adapterVersion,
        DateTimeOffset createdAt,
        LearningDefinitionApprovalV1 approval)
    {
        var definition = new LearningDatasetDefinitionV1(
            1,
            LearningExportContractIds.DatasetDefinition,
            Require(datasetId),
            string.Empty,
            Require(displayName),
            Require(sourceBundleId),
            selectedRevisions.OrderBy(item => item.RecordId, StringComparer.Ordinal)
                .ThenBy(item => item.RevisionId, StringComparer.Ordinal)
                .ToArray(),
            allowedNamespaces.Select(Require).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            intendedUses.Select(Require).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            splitPolicy ?? throw new ArgumentNullException(nameof(splitPolicy)),
            transformations.Select(CanonicalTransformation).ToArray(),
            Require(adapterId),
            Require(adapterVersion),
            createdAt,
            approval ?? throw new ArgumentNullException(nameof(approval)),
            EmptyHash);
        var hash = ComputeDefinitionHash(definition);
        return definition with { DefinitionRevisionId = $"ldef_{hash}", DefinitionHash = hash };
    }

    public static string ComputeSourceContentHash(LearningRecordRevisionV1 record)
    {
        var source = new LearningSourceContent(
            record.SchemaVersion,
            record.RecordId,
            record.RevisionId,
            record.Namespace,
            record.SourceKind,
            record.SourceServiceId,
            record.SourceArtifactId,
            CanonicalDictionary(record.Payload),
            record.ContentTreatment,
            record.Executable);
        return LearningExportJson.Sha256(LearningExportJson.Serialize(source));
    }

    public static string ComputeReviewEnvelopeHash(LearningRecordRevisionV1 record)
    {
        var canonical = record with
        {
            Payload = CanonicalDictionary(record.Payload),
            ReviewEnvelopeHash = EmptyHash
        };
        return LearningExportJson.Sha256(LearningExportJson.Serialize(canonical));
    }

    public static string ComputeRecordHash(LearningRecordRevisionV1 record) =>
        ComputeReviewEnvelopeHash(record);

    public static string ComputeBundleHash(LearningInputBundleV1 bundle) =>
        ComputeBundleHash(bundle, CancellationToken.None);

    public static string ComputeBundleHash(
        LearningInputBundleV1 bundle,
        CancellationToken cancellationToken) =>
        ComputeBundleHash(bundle, long.MaxValue, cancellationToken);

    internal static string ComputeBundleHash(
        LearningInputBundleV1 bundle,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        cancellationToken.ThrowIfCancellationRequested();
        // This public boundary may receive a custom IReadOnlyList. Enumerate it
        // without trusting caller-reported Count as an allocation capacity.
        var records = new List<LearningRecordRevisionV1>();
        foreach (var item in bundle.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = RequireRecord(item, nameof(bundle));
            records.Add(record with
            {
                Payload = CanonicalDictionary(record.Payload)
            });
        }
        var orderedRecords = records
            .OrderBy(item => item.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.RevisionId, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var canonical = bundle with
        {
            BundleId = string.Empty,
            Records = orderedRecords,
            BundleHash = EmptyHash
        };
        return LearningInputBundleSerializer.ComputeHash(
            canonical,
            maximumBytes,
            cancellationToken);
    }

    public static string ComputeDefinitionHash(LearningDatasetDefinitionV1 definition)
    {
        var canonical = definition with
        {
            DefinitionRevisionId = string.Empty,
            SelectedRevisions = definition.SelectedRevisions
                .OrderBy(item => item.RecordId, StringComparer.Ordinal)
                .ThenBy(item => item.RevisionId, StringComparer.Ordinal)
                .ToArray(),
            AllowedNamespaces = definition.AllowedNamespaces.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            IntendedUses = definition.IntendedUses.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            Transformations = definition.Transformations.Select(CanonicalTransformation).ToArray(),
            DefinitionHash = EmptyHash
        };
        return LearningExportJson.Sha256(LearningExportJson.Serialize(canonical));
    }

    public static string ComputeManifestHash(LearningDatasetManifestV1 manifest)
    {
        var canonical = manifest with { ManifestHash = EmptyHash };
        return LearningDatasetManifestSerializer.ComputeHash(
            canonical,
            maximumBytes: int.MaxValue,
            CancellationToken.None).Sha256;
    }

    public static IReadOnlyDictionary<string, string> CanonicalDictionary(
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
            canonical.Add(pair.Key, pair.Value);
        return canonical;
    }

    public static LearningTransformationSpecV1 CanonicalTransformation(
        LearningTransformationSpecV1 transformation) =>
        transformation with
        {
            Id = Require(transformation.Id),
            Version = Require(transformation.Version),
            Parameters = CanonicalDictionary(transformation.Parameters)
        };

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static LearningRecordRevisionV1 RequireRecord(
        LearningRecordRevisionV1? record,
        string parameterName) =>
        record ?? throw new ArgumentException(
            "The record collection contains a null item.",
            parameterName);

    private sealed record LearningSourceContent(
        int SchemaVersion,
        string RecordId,
        string RevisionId,
        string Namespace,
        string SourceKind,
        string SourceServiceId,
        string SourceArtifactId,
        IReadOnlyDictionary<string, string> Payload,
        string ContentTreatment,
        bool Executable);
}
