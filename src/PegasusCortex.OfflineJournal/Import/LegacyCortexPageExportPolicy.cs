using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Import;

/// <summary>
/// Builds exactly one deterministic page while retaining only that page's public events.
/// The planner decrypts at most one bounded retained file at a time and zeroes it after use.
/// </summary>
internal sealed class LegacyCortexPageExportBuilder
{
    private readonly OfflineJournalIdentity _journalIdentity;
    private readonly VerifiedLegacyCortexImport _verified;
    private readonly LegacyCortexExportRequest _request;
    private readonly LegacyCortexImportManifestV1 _manifest;
    private readonly CortexLegacyMigrationCursorV1 _current;
    private readonly CortexLegacyMigrationInventoryV1 _inventory;
    private readonly int _maximumEvents;
    private readonly List<CortexOfflineEventV1> _events;
    private int _nextFileIndex;
    private int _nextChunkIndex;
    private long _nextSequence;
    private string _priorEventHash;

    public LegacyCortexPageExportBuilder(
        OfflineJournalIdentity journalIdentity,
        VerifiedLegacyCortexImport verified,
        LegacyCortexExportRequest request,
        CortexLegacyMigrationCursorV1 current,
        CortexLegacyMigrationInventoryV1 inventory,
        int maximumEvents)
    {
        if (maximumEvents is < 1 or > CortexSyncContractLimits.MaximumLegacyMigrationPageEvents)
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        _journalIdentity = journalIdentity ?? throw new ArgumentNullException(nameof(journalIdentity));
        _verified = verified ?? throw new ArgumentNullException(nameof(verified));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _manifest = verified.GetBoundManifest();
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        if (!CortexSyncContractPolicy.HasValidHash(_inventory) ||
            !string.Equals(_inventory.InventoryHash, current.SourceInventoryHash, StringComparison.Ordinal) ||
            _inventory.Files.Count != current.SourceFileCount)
        {
            throw new InvalidDataException("The paged export inventory does not match the exact current cursor.");
        }
        _maximumEvents = maximumEvents;
        _events = new List<CortexOfflineEventV1>(maximumEvents);
        _nextFileIndex = current.NextFileIndex;
        _nextChunkIndex = current.NextChunkIndex;
        _nextSequence = current.NextSequence;
        _priorEventHash = current.PriorEventHash;
    }

    public bool IsFull => _events.Count == _maximumEvents;
    public int NextFileIndex => _nextFileIndex;

    public void AddAvailableChunks(int fileIndex, LegacyCortexImportItemV1 item, ReadOnlySpan<byte> bytes)
    {
        if (IsFull || fileIndex != _nextFileIndex || bytes.Length != item.SourceBytes ||
            !string.Equals(Pegasus.Cortex.Contracts.Serialization.CortexCanonicalJson.Sha256(bytes), item.SourceSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The paged export file does not match its cursor and verified provenance.");
        }
        var chunkCount = Math.Max(1, checked((bytes.Length + CortexLegacyFileCopyPayloadV1.MaximumChunkBytes - 1) /
            CortexLegacyFileCopyPayloadV1.MaximumChunkBytes));
        if (_nextChunkIndex >= chunkCount)
            throw new InvalidDataException("The paged export cursor points beyond the verified source file.");

        while (_nextChunkIndex < chunkCount && !IsFull)
        {
            if (_nextSequence >= CortexSyncContractLimits.MaximumSequence)
                throw new InvalidDataException("The paged legacy export cannot represent a resumable next sequence.");
            var itemEvent = LegacyCortexExportBuilder.CreateMigrationRecordEvent(
                _journalIdentity,
                _verified,
                _request,
                _manifest,
                item,
                bytes,
                _inventory,
                _inventory.Files[fileIndex],
                _nextChunkIndex,
                _nextSequence,
                _priorEventHash);
            _events.Add(itemEvent);
            _priorEventHash = itemEvent.EventHash;
            _nextSequence++;
            _nextChunkIndex++;
        }

        if (_nextChunkIndex == chunkCount)
        {
            _nextFileIndex++;
            _nextChunkIndex = 0;
        }
    }

    public CortexLegacyMigrationPageV1 Complete()
    {
        var totalFiles = _inventory.Files.Count;
        if (_events.Count == 0 || _nextFileIndex > totalFiles ||
            _nextFileIndex == totalFiles && _nextChunkIndex != 0)
        {
            throw new InvalidDataException("The paged legacy export did not produce a valid bounded continuation.");
        }
        var bundle = LegacyCortexExportBuilder.CreateBundle(
            _verified,
            _request,
            $"{_request.BundleIdPrefix}-{_current.PageNumber:D4}",
            _current.PreviousBundleHash,
            _events);
        var next = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyMigrationCursor,
            CortexSyncContractIds.Canonicalization,
            _current.MigrationId,
            _inventory.SourceManifestHash,
            _inventory.InventoryHash,
            totalFiles,
            _current.StoreId,
            _current.StoreEpoch,
            _current.ProjectId,
            _current.SourceRootId,
            _current.PageNumber + 1,
            _nextFileIndex,
            _nextChunkIndex,
            _nextSequence,
            bundle.BundleHash,
            _priorEventHash,
            _nextFileIndex == totalFiles,
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));
        return CortexSyncContractPolicy.Seal(new CortexLegacyMigrationPageV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyMigrationPage,
            CortexSyncContractIds.Canonicalization,
            $"legacy-page-{_inventory.SourceManifestHash[..12]}-{_current.PageNumber:D8}",
            _inventory,
            _current,
            bundle,
            next,
            true,
            false,
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));
    }
}
