using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Import;

public static class LegacyCortexImportAdapter
{
    public static CortexLegacyMigrationManifestV1 ToPublicManifest(
        VerifiedLegacyCortexImport verified,
        LegacyCortexPublicManifestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.StoreId, verified.StoreId, StringComparison.Ordinal) ||
            !string.Equals(identity.ProjectId, verified.ProjectId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The public migration identity cannot relabel the planner-verified store or project.",
                nameof(identity));
        }
        var local = verified.GetBoundManifest();
        var meta = local.Items.SingleOrDefault(item =>
            string.Equals(item.RelativePath, "meta.json", OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            ?? throw new ArgumentException("A public v1 migration manifest requires the captured legacy meta.json file.", nameof(local));
        if (meta.SourceBytes < 1)
            throw new ArgumentException("The captured legacy meta.json file cannot be empty.", nameof(local));

        var files = local.Items.Select(item =>
        {
            return new CortexLegacyMigrationFileV1(
                LegacyCortexMappingPolicy.ResolveRole(item.RelativePath),
                item.ExactSourcePath,
                item.RelativePath,
                item.SourceBytes,
                item.SourceSha256,
                CortexLegacyFileDispositionIds.CopiedVerbatim,
                CortexLegacyFileLifecycleIds.Validated,
                "copy-first-protected",
                "The source was copied and its retained protected bytes were authenticated without modifying the original.",
                [LegacyCortexMappingPolicy.MappingId(identity.SourceRootId, item)],
                [OfflineJournalContractIds.LegacyFileCopy, OfflineJournalContractIds.LegacyMigrationRecord],
                [LegacyCortexMappingPolicy.EvidenceId(identity.SourceRootId, local, item)],
                []);
        }).ToArray();
        var projectRoot = Directory.GetParent(local.ExactSourceRoot)?.FullName
            ?? throw new ArgumentException("The captured legacy root has no project parent.", nameof(local));
        var candidate = new CortexLegacyMigrationManifestV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyMigrationManifest,
            CortexSyncContractIds.Canonicalization,
            local.RunId,
            identity.StoreId,
            identity.StoreEpoch,
            identity.ProjectId,
            identity.SourceRootId,
            CortexSyncContractIds.LegacyCortexSchemaVersion,
            OperatingSystem.IsWindows() ? "windows" : "posix",
            true,
            projectRoot,
            meta.ExactSourcePath,
            meta.SourceBytes,
            meta.SourceSha256,
            local.CapturedAt,
            1,
            CortexSyncContractIds.EmptySha256,
            files,
            "copy_first",
            false,
            "prohibited_until_separate_approval",
            "not_approved",
            true,
            [],
            CortexSyncContractIds.EmptySha256);
        return CortexSyncContractPolicy.Seal(candidate);
    }

    public static CortexLegacyMigrationInventoryV1 ToPublicInventory(
        CortexLegacyMigrationManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!CortexSyncContractPolicy.HasValidHash(manifest))
            throw new ArgumentException("The portable inventory requires an intact public migration manifest.", nameof(manifest));

        long totalSourceBytes = 0;
        var files = new CortexLegacyMigrationInventoryFileV1[manifest.Files.Count];
        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var file = manifest.Files[index];
            if (file.SourceBytes is null || file.SourceSha256 is null)
                throw new ArgumentException("Every paged migration inventory file requires captured source provenance.", nameof(manifest));
            var sourceBytes = file.SourceBytes.Value;
            totalSourceBytes = checked(totalSourceBytes + sourceBytes);
            var recordId = LegacyCortexMappingPolicy.RecordId(manifest.SourceRootId, file.SourceRelativePath);
            files[index] = new CortexLegacyMigrationInventoryFileV1(
                index,
                file.Role,
                file.SourceRelativePath,
                sourceBytes,
                file.SourceSha256,
                recordId,
                LegacyCortexMappingPolicy.RevisionId(recordId, file.SourceSha256),
                sourceBytes == 0
                    ? 1
                    : checked((int)((sourceBytes + CortexLegacyFileCopyPayloadV1.MaximumChunkBytes - 1) /
                        CortexLegacyFileCopyPayloadV1.MaximumChunkBytes)));
        }

        return CortexSyncContractPolicy.Seal(new CortexLegacyMigrationInventoryV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyMigrationInventory,
            CortexSyncContractIds.Canonicalization,
            manifest.MigrationId,
            manifest.ManifestHash,
            manifest.StoreId,
            manifest.StoreEpoch,
            manifest.ProjectId,
            manifest.SourceRootId,
            manifest.CapturedAt,
            manifest.SourcePathCaseSensitive,
            files,
            totalSourceBytes,
            CortexSyncContractIds.ContentTreatment,
            false,
            [],
            CortexSyncContractIds.EmptySha256));
    }

}
