using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.IO;

namespace Pegasus.Cortex.OfflineJournal.Import;

/// <summary>
/// Measures an export before decryption, then converts one authenticated file at a time into
/// bounded typed chunks. Only public events are retained across files; plaintext buffers are not.
/// </summary>
internal sealed class LegacyCortexExportBuilder
{
    internal const int EventsPerBundle = CortexSyncContractLimits.MaximumLegacyMigrationPageEvents;
    private readonly OfflineJournalIdentity _journalIdentity;
    private readonly VerifiedLegacyCortexImport _verified;
    private readonly LegacyCortexExportRequest _request;
    private readonly LegacyCortexImportManifestV1 _manifest;
    private readonly List<CortexOfflineEventV1> _events;
    private string _previousEventHash;
    private long _sequence;
    private int _filesAdded;

    public LegacyCortexExportBuilder(
        OfflineJournalIdentity journalIdentity,
        VerifiedLegacyCortexImport verified,
        LegacyCortexExportRequest request,
        int eventCapacity)
    {
        _journalIdentity = journalIdentity ?? throw new ArgumentNullException(nameof(journalIdentity));
        _verified = verified ?? throw new ArgumentNullException(nameof(verified));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _manifest = verified.GetBoundManifest();
        if (request.CreatedAt.Offset != TimeSpan.Zero || request.CreatedAt < _manifest.CapturedAt)
            throw new ArgumentException("Legacy export time must be UTC and cannot predate capture.", nameof(request));
        _events = new List<CortexOfflineEventV1>(eventCapacity);
        _previousEventHash = request.PriorEventHash;
        _sequence = request.FirstSequence;
    }

    public static LegacyCortexExportMeasure Measure(
        LegacyCortexImportManifestV1 manifest,
        long maximumPlaintextBytes,
        int maximumEvents)
    {
        long plaintextBytes = 0;
        var events = 0;
        foreach (var item in manifest.Items)
        {
            if (item.SourceBytes < 0 || item.SourceBytes > CortexLegacyFileCopyPayloadV1.MaximumSourceBytes)
                throw new JournalLimitExceededException("legacy_export_file_bytes");
            plaintextBytes = checked(plaintextBytes + item.SourceBytes);
            if (plaintextBytes > maximumPlaintextBytes)
                throw new JournalLimitExceededException("legacy_export_plaintext_bytes");
            var chunks = item.SourceBytes == 0
                ? 1
                : checked((int)((item.SourceBytes + CortexLegacyFileCopyPayloadV1.MaximumChunkBytes - 1) /
                    CortexLegacyFileCopyPayloadV1.MaximumChunkBytes));
            events = checked(events + chunks);
            if (events > maximumEvents)
                throw new JournalLimitExceededException("legacy_export_events");
        }
        if (events == 0)
            throw new InvalidDataException("The verified legacy import contains no exportable files.");
        return new LegacyCortexExportMeasure(plaintextBytes, events);
    }

    public void AddFile(LegacyCortexImportItemV1 item, ReadOnlySpan<byte> bytes)
    {
        if (_filesAdded >= _manifest.Items.Count ||
            !string.Equals(_manifest.Items[_filesAdded].RelativePath, item.RelativePath, StringComparison.Ordinal) ||
            bytes.Length != item.SourceBytes ||
            !string.Equals(CortexCanonicalJson.Sha256(bytes), item.SourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A decrypted legacy file does not match its ordered verified provenance.");
        }

        var chunkCount = Math.Max(1, checked((bytes.Length + CortexLegacyFileCopyPayloadV1.MaximumChunkBytes - 1) /
            CortexLegacyFileCopyPayloadV1.MaximumChunkBytes));
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            if (_sequence > CortexSyncContractLimits.MaximumSequence)
                throw new InvalidDataException("The legacy export exceeds the public v1 sequence range.");
            var sealedEvent = CreateEvent(
                _journalIdentity,
                _verified,
                _request,
                _manifest,
                item,
                bytes,
                chunkIndex,
                _sequence,
                _previousEventHash);
            _events.Add(sealedEvent);
            _previousEventHash = sealedEvent.EventHash;
            _sequence = checked(_sequence + 1);
        }
        _filesAdded++;
    }

    public IReadOnlyList<CortexOfflineBundleV1> Complete()
    {
        if (_filesAdded != _manifest.Items.Count)
            throw new InvalidDataException("The authenticated legacy file set is incomplete.");
        var bundles = new List<CortexOfflineBundleV1>((_events.Count + EventsPerBundle - 1) / EventsPerBundle);
        var previousBundleHash = _request.PreviousBundleHash;
        for (var offset = 0; offset < _events.Count; offset += EventsPerBundle)
        {
            var batch = _events.Skip(offset).Take(EventsPerBundle).ToArray();
            var sealedBundle = CreateBundle(
                _verified,
                _request,
                $"{_request.BundleIdPrefix}-{bundles.Count + 1:D4}",
                previousBundleHash,
                batch);
            bundles.Add(sealedBundle);
            previousBundleHash = sealedBundle.BundleHash;
        }
        return bundles.AsReadOnly();
    }

    internal static CortexOfflineEventV1 CreateEvent(
        OfflineJournalIdentity journalIdentity,
        VerifiedLegacyCortexImport verified,
        LegacyCortexExportRequest request,
        LegacyCortexImportManifestV1 manifest,
        LegacyCortexImportItemV1 item,
        ReadOnlySpan<byte> bytes,
        int chunkIndex,
        long sequence,
        string previousEventHash)
    {
        var chunkCount = Math.Max(1, checked((bytes.Length + CortexLegacyFileCopyPayloadV1.MaximumChunkBytes - 1) /
            CortexLegacyFileCopyPayloadV1.MaximumChunkBytes));
        if (chunkIndex < 0 || chunkIndex >= chunkCount)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        var mappingId = LegacyCortexMappingPolicy.MappingId(request.Identity.SourceRootId, item);
        var offset = checked(chunkIndex * CortexLegacyFileCopyPayloadV1.MaximumChunkBytes);
        var count = Math.Min(CortexLegacyFileCopyPayloadV1.MaximumChunkBytes, bytes.Length - offset);
        var chunk = bytes.Slice(offset, count);
        var payload = new CortexLegacyFileCopyPayloadV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyFileCopyPayload,
            CortexSyncContractIds.Canonicalization,
            request.Identity.SourceRootId,
            item.RelativePath,
            LegacyCortexMappingPolicy.ResolveRole(item.RelativePath),
            mappingId,
            chunkIndex + 1,
            chunkCount,
            offset,
            item.SourceBytes,
            item.SourceSha256,
            EncodeBase64Url(chunk),
            count,
            CortexCanonicalJson.Sha256(chunk),
            CortexSyncContractIds.ContentTreatment,
            false);
        var payloadBytes = CortexCanonicalJson.Serialize(payload);
        if (payloadBytes.Length > CortexSyncContractLimits.MaximumEventPayloadBytes)
            throw new InvalidDataException("A typed legacy chunk exceeds the public event payload ceiling.");
        return CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineEvent,
            CortexSyncContractIds.Canonicalization,
            verified.StoreId,
            request.Identity.StoreEpoch,
            verified.ProjectId,
            sequence,
            $"legacy-event-{mappingId[11..]}-{chunkIndex + 1:D3}",
            previousEventHash,
            manifest.CapturedAt,
            journalIdentity.WriterId,
            "legacy.copy",
            request.TenantIdHint,
            request.NamespaceHint,
            request.ObservedPolicyRevision,
            CortexSyncContractIds.LegacyFileCopyPayload,
            EncodeBase64Url(payloadBytes),
            payloadBytes.Length,
            CortexCanonicalJson.Sha256(payloadBytes),
            new CortexSyncSourceProvenanceV1(
                "legacy_file_copy",
                request.Identity.SourceRootId,
                item.RelativePath,
                item.SourceBytes,
                item.SourceSha256,
                manifest.CapturedAt),
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));
    }

    internal static CortexOfflineBundleV1 CreateBundle(
        VerifiedLegacyCortexImport verified,
        LegacyCortexExportRequest request,
        string bundleId,
        string previousBundleHash,
        IReadOnlyList<CortexOfflineEventV1> events)
    {
        if (events.Count is < 1 or > CortexSyncContractLimits.MaximumEventsPerBundle)
            throw new ArgumentOutOfRangeException(nameof(events));
        return CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            bundleId,
            verified.StoreId,
            request.Identity.StoreEpoch,
            verified.ProjectId,
            request.BaseServerRevision,
            previousBundleHash,
            events[0].PreviousEventHash,
            events[0].Sequence,
            events[^1].Sequence,
            request.CreatedAt,
            events,
            [],
            CortexSyncContractIds.EmptySha256));
    }

    internal static CortexOfflineEventV1 CreateMigrationRecordEvent(
        OfflineJournalIdentity journalIdentity,
        VerifiedLegacyCortexImport verified,
        LegacyCortexExportRequest request,
        LegacyCortexImportManifestV1 manifest,
        LegacyCortexImportItemV1 item,
        ReadOnlySpan<byte> bytes,
        CortexLegacyMigrationInventoryV1 inventory,
        CortexLegacyMigrationInventoryFileV1 inventoryFile,
        int chunkIndex,
        long sequence,
        string previousEventHash)
    {
        var chunkCount = Math.Max(1, checked((bytes.Length + CortexLegacyFileCopyPayloadV1.MaximumChunkBytes - 1) /
            CortexLegacyFileCopyPayloadV1.MaximumChunkBytes));
        if (chunkIndex < 0 || chunkIndex >= chunkCount)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        if (!CortexSyncContractPolicy.HasValidHash(inventory) ||
            inventoryFile.FileIndex >= inventory.Files.Count ||
            !Equals(inventory.Files[inventoryFile.FileIndex], inventoryFile) ||
            !string.Equals(inventoryFile.SourceRelativePath, item.RelativePath, StringComparison.Ordinal) ||
            inventoryFile.SourceBytes != item.SourceBytes ||
            !string.Equals(inventoryFile.SourceSha256, item.SourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The migration record is not bound to the exact ordered source inventory.");
        }
        var recordId = inventoryFile.RecordId;
        var revisionId = inventoryFile.RevisionId;
        var offset = checked(chunkIndex * CortexLegacyFileCopyPayloadV1.MaximumChunkBytes);
        var count = Math.Min(CortexLegacyFileCopyPayloadV1.MaximumChunkBytes, bytes.Length - offset);
        var chunk = bytes.Slice(offset, count);
        var payload = new CortexLegacyMigrationRecordV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyMigrationRecord,
            CortexSyncContractIds.Canonicalization,
            recordId,
            revisionId,
            request.NamespaceHint,
            LegacyCortexMappingPolicy.ResolveRole(item.RelativePath),
            request.Identity.SourceRootId,
            inventory.SourceManifestHash,
            inventory.InventoryHash,
            inventoryFile.FileIndex,
            item.RelativePath,
            item.SourceBytes,
            item.SourceSha256,
            null,
            false,
            chunkIndex + 1,
            chunkCount,
            offset,
            EncodeBase64Url(chunk),
            count,
            CortexCanonicalJson.Sha256(chunk),
            CortexSyncContractIds.ContentTreatment,
            false);
        var payloadBytes = CortexCanonicalJson.Serialize(payload);
        if (payloadBytes.Length > CortexSyncContractLimits.MaximumEventPayloadBytes)
            throw new InvalidDataException("A typed migration record exceeds the public event payload ceiling.");
        return CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineEvent,
            CortexSyncContractIds.Canonicalization,
            verified.StoreId,
            request.Identity.StoreEpoch,
            verified.ProjectId,
            sequence,
            $"legacy-event-{revisionId[16..]}-{chunkIndex + 1:D3}",
            previousEventHash,
            manifest.CapturedAt,
            journalIdentity.WriterId,
            "legacy.migrate",
            request.TenantIdHint,
            request.NamespaceHint,
            request.ObservedPolicyRevision,
            CortexSyncContractIds.LegacyMigrationRecord,
            EncodeBase64Url(payloadBytes),
            payloadBytes.Length,
            CortexCanonicalJson.Sha256(payloadBytes),
            new CortexSyncSourceProvenanceV1(
                "legacy_file_copy",
                request.Identity.SourceRootId,
                item.RelativePath,
                item.SourceBytes,
                item.SourceSha256,
                manifest.CapturedAt),
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record LegacyCortexExportMeasure(long PlaintextBytes, int Events);
