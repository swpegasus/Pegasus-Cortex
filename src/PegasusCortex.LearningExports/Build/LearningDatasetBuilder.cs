using System.Runtime.ExceptionServices;
using Pegasus.Cortex.LearningExports.Adapters;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Diagnostics;
using Pegasus.Cortex.LearningExports.IO;
using Pegasus.Cortex.LearningExports.Policy;
using Pegasus.Cortex.LearningExports.Processing;
using Pegasus.Cortex.LearningExports.Serialization;
using Pegasus.Cortex.LearningExports.Validation;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.LearningExports.Build;

public sealed class LearningDatasetBuilder
{
    private const int MaximumAdapterImplementations = 256;

    private readonly LearningExportOptions _options;
    private readonly LearningEligibilityPolicy _eligibilityPolicy;
    private readonly LearningTransformationPipeline _transformations;
    private readonly IReadOnlyDictionary<(string Id, string Version), ILearningDatasetAdapter> _adapters;
    private readonly LearningExportValidator _validator;

    public LearningDatasetBuilder(
        LearningExportOptions? options = null,
        IEnumerable<ILearningDatasetAdapter>? adapters = null,
        IEnumerable<ILearningRecordTransformer>? transformers = null)
    {
        _options = options ?? new LearningExportOptions();
        _options.Validate();
        _eligibilityPolicy = new LearningEligibilityPolicy();
        _transformations = new LearningTransformationPipeline(transformers);
        var implementations = adapters is null
            ? [new CanonicalJsonlDatasetAdapter()]
            : adapters.Take(MaximumAdapterImplementations + 1).ToArray();
        if (implementations.Length > MaximumAdapterImplementations)
            throw new ArgumentException("Too many learning dataset adapters were supplied.", nameof(adapters));
        _adapters = implementations.ToDictionary(AdapterKey);
        _validator = new LearningExportValidator(_eligibilityPolicy, _transformations);
    }

    public async ValueTask<LearningDatasetBuildResult> ExecuteAsync(
        LearningInputBundleV1 bundle,
        LearningDatasetDefinitionV1 definition,
        LearningDatasetBuildRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bundle is null || definition is null || request is null)
        {
            return Reject(LearningExportDiagnostics.Error(
                "learning_export_input_missing",
                "An explicit reviewed input bundle, approved definition, and runtime request are required."));
        }

        LearningExportExecutionSnapshot snapshot;
        try
        {
            snapshot = LearningExportSnapshots.Capture(
                bundle,
                definition,
                request,
                _options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LearningExportSnapshotException exception)
        {
            return Reject(LearningExportDiagnostics.Error(
                "learning_export_snapshot_invalid",
                "A caller-owned input boundary could not be captured within its configured limit.",
                cause: exception.GetType().Name,
                boundary: exception.Boundary));
        }
        catch (Exception exception)
        {
            return Reject(LearningExportDiagnostics.Error(
                "learning_export_snapshot_failed",
                "The explicit inputs could not be copied into an immutable execution snapshot.",
                cause: exception.GetType().Name));
        }

        bundle = snapshot.Bundle;
        definition = snapshot.Definition;
        request = snapshot.Request;

        StableDatasetAdapter? adapter = null;
        try
        {
            if (_adapters.TryGetValue(AdapterKey(definition.AdapterId, definition.AdapterVersion), out var implementation))
                adapter = new StableDatasetAdapter(implementation);
        }
        catch (Exception exception)
        {
            return Reject(LearningExportDiagnostics.Error(
                "learning_export_adapter_contract_invalid",
                "The local adapter contract could not be captured deterministically.",
                cause: exception.GetType().Name));
        }

        LearningExportValidationResult validation;
        try
        {
            validation = _validator.Validate(
                bundle,
                definition,
                request,
                adapter,
                _options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Reject(LearningExportDiagnostics.Error(
                "learning_export_validation_failed",
                "The immutable execution snapshot could not be validated safely.",
                cause: exception.GetType().Name));
        }

        if (!validation.Accepted || adapter is null)
            return new(false, false, null, null, validation.Diagnostics);

        var buildId = ComputeBuildId(definition, bundle, adapter);
        var transformationSetHash = ComputeTransformationSetHash(definition.Transformations);
        AtomicArtifactPublisher? publisher = null;
        LearningDatasetBuildResult? result = null;
        ExceptionDispatchInfo? callerCancellation = null;

        try
        {
            Stream destination;
            if (request.PublishArtifacts)
            {
                publisher = AtomicArtifactPublisher.Create(
                    request.OutputRoot!,
                    request.Authority.ApprovedOutputRoot!,
                    definition.DatasetId,
                    buildId,
                    adapter.RelativeArtifactPath);
                destination = publisher.OpenArtifact();
            }
            else
            {
                destination = Stream.Null;
            }

            var production = await ProduceArtifactAsync(
                destination,
                adapter,
                definition,
                transformationSetHash,
                validation.Records,
                cancellationToken).ConfigureAwait(false);
            if (!production.Accepted)
            {
                result = new(false, false, null, null, production.Diagnostics);
            }
            else
            {
                var manifest = CreateManifest(bundle, definition, buildId, adapter, production);
                var contractHash = LearningDatasetManifestSerializer.ComputeHash(
                    manifest,
                    _options.MaxManifestBytes,
                    cancellationToken);
                manifest = manifest with { ManifestHash = contractHash.Sha256 };

                if (!request.PublishArtifacts)
                {
                    _ = LearningDatasetManifestSerializer.ComputeHash(
                        manifest,
                        _options.MaxManifestBytes,
                        cancellationToken);
                    result = new(true, false, null, manifest, []);
                }
                else
                {
                    CanonicalManifestWriteResult manifestFile;
                    await using (var manifestDestination = publisher!.OpenManifest())
                    {
                        manifestFile = LearningDatasetManifestSerializer.WriteCanonicalAndHash(
                            manifestDestination,
                            manifest,
                            _options.MaxManifestBytes,
                            durableFlush: true,
                            cancellationToken);
                    }

                    var publication = await publisher.PublishAsync(
                        manifestFile.Sha256,
                        manifestFile.Bytes,
                        production.ArtifactSha256,
                        production.ArtifactBytes,
                        cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<PegasusDiagnostic> diagnostics = publication.AlreadyExisted
                        ? [LearningExportDiagnostics.Information(
                            "learning_export_already_published",
                            "The deterministic artifact already existed with identical bytes.")]
                        : [];
                    result = new(true, true, publication.FinalDirectory, manifest, diagnostics);
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            callerCancellation = ExceptionDispatchInfo.Capture(exception);
        }
        catch (OperationCanceledException exception)
        {
            result = Reject(LearningExportDiagnostics.Error(
                "learning_export_unexpected_cancellation",
                "A dependency reported cancellation when the caller token was not canceled.",
                cause: exception.GetType().Name));
        }
        catch (LearningExportLimitExceededException exception)
        {
            result = Reject(LearningExportDiagnostics.Error(
                exception.LimitName == "manifest"
                    ? "learning_export_manifest_oversize"
                    : "learning_export_artifact_oversize",
                "A canonical export output exceeded its configured byte limit."));
        }
        catch (Exception exception)
        {
            result = Reject(LearningExportDiagnostics.Error(
                "learning_export_publication_failed",
                "The export could not be produced or atomically published.",
                cause: exception.GetType().Name));
        }
        finally
        {
            if (publisher is not null)
            {
                await publisher.DisposeAsync().ConfigureAwait(false);
                if (publisher.CleanupFailure is not null)
                {
                    if (callerCancellation is not null)
                    {
                        callerCancellation.SourceException.Data["PegasusLearningExportCleanupFailure"] =
                            publisher.CleanupFailure.GetType().Name;
                    }
                    else if (result is not null)
                    {
                        result = result with
                        {
                            Diagnostics = result.Diagnostics.Append(
                                LearningExportDiagnostics.Warning(
                                    "learning_export_staging_cleanup_failed",
                                    "The primary result is preserved, but a private staging directory may require operator cleanup.",
                                    publisher.CleanupFailure.GetType().Name)).ToArray()
                        };
                    }
                }
            }
        }

        callerCancellation?.Throw();
        if (result is not null && validation.Diagnostics.Count != 0)
        {
            result = result with
            {
                Diagnostics = validation.Diagnostics
                    .Concat(result.Diagnostics)
                    .Take(_options.MaxDiagnostics)
                    .ToArray()
            };
        }
        return result ?? Reject(LearningExportDiagnostics.Error(
            "learning_export_failed",
            "The export ended without a terminal result."));
    }

    private async ValueTask<ArtifactProductionResult> ProduceArtifactAsync(
        Stream destination,
        ILearningDatasetAdapter adapter,
        LearningDatasetDefinitionV1 definition,
        string transformationSetHash,
        IReadOnlyList<LearningRecordRevisionV1> records,
        CancellationToken cancellationToken)
    {
        await using var artifact = new BoundedHashWriteStream(
            destination,
            _options.MaxArtifactBytes,
            "artifact",
            leaveOpen: false);
        var items = new List<LearningManifestItemV1>(records.Count);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<PegasusDiagnostic>();

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, string> payload;
            try
            {
                payload = await _transformations.ApplyAsync(
                    record.Payload,
                    definition.Transformations,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add(LearningExportDiagnostics.Error(
                    "learning_export_transformation_failed",
                    "A selected revision could not be transformed by its exact local transformation set.",
                    record.RecordId,
                    record.RevisionId,
                    exception.GetType().Name));
                break;
            }

            var transformedDiagnostics = _eligibilityPolicy.EvaluateTransformedPayload(record, payload, _options);
            if (transformedDiagnostics.Count != 0)
            {
                diagnostics.AddRange(transformedDiagnostics.Take(_options.MaxDiagnostics - diagnostics.Count));
                if (diagnostics.Count >= _options.MaxDiagnostics)
                    break;
                continue;
            }

            var split = DeterministicSplitAssigner.Assign(
                definition.DatasetId,
                record.LeakageGroupId,
                definition.SplitPolicy);
            var itemId = $"litem_{LearningExportIdentity.ComputeHash(
                "pegasus.cortex.learning-dataset-item/1.0",
                KeyValuePair.Create("contractId", LearningExportContractIds.DatasetItem),
                KeyValuePair.Create("datasetId", definition.DatasetId),
                KeyValuePair.Create("definitionHash", definition.DefinitionHash),
                KeyValuePair.Create("recordId", record.RecordId),
                KeyValuePair.Create("revisionId", record.RevisionId))}";
            if (!itemIds.Add(itemId))
            {
                diagnostics.Add(LearningExportDiagnostics.Error(
                    "learning_export_item_id_collision",
                    "Two selected revisions resolved to the same deterministic item identity.",
                    record.RecordId,
                    record.RevisionId));
                break;
            }

            var item = new LearningDatasetItemV1(
                1,
                LearningExportContractIds.DatasetItem,
                itemId,
                definition.DatasetId,
                record.RecordId,
                record.RevisionId,
                record.Namespace,
                record.SourceKind,
                split,
                payload,
                LearningExportContractIds.ContentTreatment,
                false);

            var offset = artifact.BytesWritten;
            try
            {
                await using var line = new BoundedHashWriteStream(
                    artifact,
                    _options.MaxRecordBytes,
                    "record",
                    leaveOpen: true);
                await adapter.WriteItemAsync(line, item, cancellationToken).ConfigureAwait(false);
                if (line.BytesWritten == 0)
                {
                    diagnostics.Add(LearningExportDiagnostics.Error(
                        "learning_export_adapter_empty_output",
                        "The local adapter returned without emitting a dataset item.",
                        record.RecordId,
                        record.RevisionId));
                    break;
                }
                await line.FlushAsync(cancellationToken).ConfigureAwait(false);
                var lineHash = line.CompleteHash();
                var review = record.Review;
                items.Add(new LearningManifestItemV1(
                    itemId,
                    record.RecordId,
                    record.RevisionId,
                    record.Namespace,
                    split,
                    DeterministicSplitAssigner.ComputeLeakageGroupHash(
                        definition.DatasetId,
                        record.LeakageGroupId,
                        definition.SplitPolicy),
                    record.SourceContentHash,
                    record.ReviewEnvelopeHash,
                    transformationSetHash,
                    lineHash,
                    offset,
                    checked((int)line.BytesWritten),
                    review.ApprovedBy!,
                    review.ApprovedAt!.Value,
                    review.PolicyRevision,
                    review.ApprovalAuthorityId,
                    review.ApprovalDecisionId,
                    review.UsageRights,
                    record.Sensitivity,
                    review.RedactionDecision));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LearningExportLimitExceededException exception)
            {
                diagnostics.Add(LearningExportDiagnostics.Error(
                    exception.LimitName == "record"
                        ? "learning_export_record_oversize"
                        : "learning_export_artifact_oversize",
                    "A transformed output exceeded its configured byte limit.",
                    record.RecordId,
                    record.RevisionId));
                break;
            }
            catch (Exception exception)
            {
                diagnostics.Add(LearningExportDiagnostics.Error(
                    "learning_export_adapter_failed",
                    "The exact local adapter failed while writing a selected revision.",
                    record.RecordId,
                    record.RevisionId,
                    exception.GetType().Name));
                break;
            }
        }

        if (diagnostics.Count != 0)
            return new(false, string.Empty, 0, [], diagnostics.AsReadOnly());

        cancellationToken.ThrowIfCancellationRequested();
        artifact.FlushToDisk();
        var artifactHash = artifact.CompleteHash();
        return new(true, artifactHash, artifact.BytesWritten, items.AsReadOnly(), []);
    }

    private static LearningDatasetManifestV1 CreateManifest(
        LearningInputBundleV1 bundle,
        LearningDatasetDefinitionV1 definition,
        string buildId,
        ILearningDatasetAdapter adapter,
        ArtifactProductionResult production)
    {
        var artifacts = Array.AsReadOnly(new[]
        {
            new LearningArtifactV1(
                adapter.RelativeArtifactPath,
                adapter.MediaType,
                adapter.Id,
                adapter.Version,
                production.ArtifactSha256,
                production.ArtifactBytes,
                production.Items.Count)
        });
        var splitSummaries = new[] { "train", "validation", "test" }
            .Select(split =>
            {
                var entries = production.Items
                    .Where(item => item.Split == split)
                    .Select(item => new SplitLineageEntry(item.ItemId, item.OutputRecordSha256))
                    .ToArray();
                var entriesHash = LearningExportJson.Sha256(LearningExportJson.Serialize(entries));
                return new LearningSplitSummaryV1(
                    split,
                    entries.Length,
                    LearningExportIdentity.ComputeHash(
                        "pegasus.cortex.learning-split-lineage/1.0",
                        KeyValuePair.Create("split", split),
                        KeyValuePair.Create("entriesHash", entriesHash)));
            })
            .ToArray();

        return new LearningDatasetManifestV1(
            1,
            LearningExportContractIds.DatasetManifest,
            LearningExportContractIds.Canonicalization,
            definition.DatasetId,
            definition.DefinitionRevisionId,
            definition.DefinitionHash,
            buildId,
            definition.CreatedAt,
            bundle.BundleId,
            bundle.BundleHash,
            definition.Approval with { },
            Array.AsReadOnly(definition.IntendedUses.Order(StringComparer.Ordinal).ToArray()),
            definition.SplitPolicy with { },
            Array.AsReadOnly(definition.Transformations
                .Select(LearningExportFactory.CanonicalTransformation)
                .ToArray()),
            production.Items,
            Array.AsReadOnly(splitSummaries),
            artifacts,
            LearningExportContractIds.ContentTreatment,
            false,
            false,
            new string('0', 64));
    }

    private static string ComputeBuildId(
        LearningDatasetDefinitionV1 definition,
        LearningInputBundleV1 bundle,
        ILearningDatasetAdapter adapter) =>
        $"lbuild_{LearningExportIdentity.ComputeHash(
            "pegasus.cortex.learning-dataset-build/1.0",
            KeyValuePair.Create("contractId", LearningExportContractIds.DatasetManifest),
            KeyValuePair.Create("datasetId", definition.DatasetId),
            KeyValuePair.Create("definitionHash", definition.DefinitionHash),
            KeyValuePair.Create("bundleHash", bundle.BundleHash),
            KeyValuePair.Create("adapterId", adapter.Id),
            KeyValuePair.Create("adapterVersion", adapter.Version))}";

    private static string ComputeTransformationSetHash(
        IReadOnlyList<LearningTransformationSpecV1> transformations)
    {
        var canonical = transformations.Select(LearningExportFactory.CanonicalTransformation).ToArray();
        return LearningExportIdentity.ComputeHash(
            "pegasus.cortex.learning-transformation-set/1.0",
            KeyValuePair.Create("specificationsHash", LearningExportJson.Sha256(LearningExportJson.Serialize(canonical))));
    }

    private static (string Id, string Version) AdapterKey(ILearningDatasetAdapter adapter) =>
        AdapterKey(adapter.Id, adapter.Version);

    private static (string Id, string Version) AdapterKey(string? id, string? version) => (id!, version!);

    private static LearningDatasetBuildResult Reject(PegasusDiagnostic diagnostic) =>
        new(false, false, null, null, [diagnostic]);

    private sealed record ArtifactProductionResult(
        bool Accepted,
        string ArtifactSha256,
        long ArtifactBytes,
        IReadOnlyList<LearningManifestItemV1> Items,
        IReadOnlyList<PegasusDiagnostic> Diagnostics);

    private sealed record SplitLineageEntry(string ItemId, string OutputRecordSha256);

    private sealed class StableDatasetAdapter(ILearningDatasetAdapter implementation) : ILearningDatasetAdapter
    {
        public string Id { get; } = implementation.Id;
        public string Version { get; } = implementation.Version;
        public string RelativeArtifactPath { get; } = implementation.RelativeArtifactPath;
        public string MediaType { get; } = implementation.MediaType;

        public ValueTask WriteItemAsync(
            Stream destination,
            LearningDatasetItemV1 item,
            CancellationToken cancellationToken) =>
            implementation.WriteItemAsync(destination, item, cancellationToken);
    }
}
