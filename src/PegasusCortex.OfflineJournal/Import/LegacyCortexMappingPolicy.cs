using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Import;

internal static class LegacyCortexMappingPolicy
{
    public static string ResolveRole(string relativePath) =>
        (Path.GetFileName(relativePath)?.ToLowerInvariant() ?? "") switch
        {
            "meta.json" => "meta",
            "state.md" => "state",
            "tasks.md" => "tasks",
            "worklog.md" => "worklog",
            "sessions.md" => "sessions",
            "recovery.md" => "recovery",
            "learnings.md" => "learnings",
            "guide.md" => "guide",
            "rules.md" => "rules",
            "readme.md" => "readme",
            "mcp-tools.json" => "mcp-tools",
            _ => "legacy-file"
        };

    public static string MappingId(
        string sourceRootId,
        LegacyCortexImportItemV1 item) =>
        $"legacy-map-{Hash(new MappingIdentity(
            "mapping",
            sourceRootId,
            Normalize(item.RelativePath),
            item.SourceSha256))[..32]}";

    public static string RecordId(
        string sourceRootId,
        LegacyCortexImportItemV1 item) =>
        RecordId(sourceRootId, item.RelativePath);

    public static string RecordId(
        string sourceRootId,
        string sourceRelativePath) =>
        $"legacy-record-{Hash(new RecordIdentity(
            "record-identity",
            sourceRootId,
            Normalize(sourceRelativePath)))[..32]}";

    public static string RevisionId(
        string recordId,
        string sourceSha256,
        string? supersedesRevisionId = null,
        bool tombstone = false) =>
        $"legacy-revision-{Hash(new RevisionIdentity(
            "revision-identity",
            recordId,
            sourceSha256,
            supersedesRevisionId,
            tombstone))[..32]}";

    public static string EvidenceId(
        string sourceRootId,
        LegacyCortexImportManifestV1 manifest,
        LegacyCortexImportItemV1 item) =>
        $"legacy-copy-{Hash(new CopyEvidenceIdentity(
            "copy-evidence",
            sourceRootId,
            Normalize(item.RelativePath),
            manifest.ManifestSha256,
            item.SourceSha256,
            item.StoredBytes,
            item.StoredSha256,
            item.Protection.Algorithm,
            item.Protection.KeyId,
            item.Protection.Nonce,
            item.Protection.AuthenticationTag,
            OfflineJournalContractIds.LegacyFileCopy))[..32]}";

    private static string Normalize(string value) => value.Replace('\\', '/');
    private static string Hash<T>(T value) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(value));

    private sealed record MappingIdentity(
        string Purpose,
        string SourceRootId,
        string RelativePath,
        string SourceSha256);

    private sealed record RecordIdentity(
        string Purpose,
        string SourceRootId,
        string RelativePath);

    private sealed record RevisionIdentity(
        string Purpose,
        string RecordId,
        string SourceSha256,
        string? SupersedesRevisionId,
        bool Tombstone);

    private sealed record CopyEvidenceIdentity(
        string Purpose,
        string SourceRootId,
        string RelativePath,
        string ManifestSha256,
        string SourceSha256,
        long StoredBytes,
        string StoredSha256,
        string ProtectionAlgorithm,
        string ProtectionKeyId,
        string ProtectionNonce,
        string ProtectionAuthenticationTag,
        string PayloadContractId);
}
