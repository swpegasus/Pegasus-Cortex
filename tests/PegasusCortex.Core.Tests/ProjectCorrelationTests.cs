using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Hybrid;

internal static class ProjectCorrelationTests
{
    public static Task DeterministicSafeLinkAsync()
    {
        var access = Access(CortexProjectLinkCapability.Correlate);
        var first = CortexProjectCorrelationPolicy.Prepare(
            access,
            "project-pegasus-cortex",
            "store-workstation-a",
            1,
            "pegasus-cortex-arm64",
            "hybrid-v1");
        var second = CortexProjectCorrelationPolicy.Prepare(
            access,
            "project-pegasus-cortex",
            "store-workstation-a",
            1,
            "pegasus-cortex-arm64",
            "hybrid-v1");

        Assert.Equal(first, second);
        Assert.True(first.Metadata.ProjectLinkId.StartsWith("plink_", StringComparison.Ordinal));
        Assert.Equal(70, first.Metadata.ProjectLinkId.Length);
        Assert.True(first.Metadata.ProjectNamespace.StartsWith("project-", StringComparison.Ordinal));
        Assert.Equal(CortexProjectCorrelationContract.SchemaVersion, first.Metadata.SchemaVersion);
        Assert.Equal(CortexProjectCorrelationContract.ContractId, first.Metadata.ContractId);
        Assert.Equal(
            CortexProjectCorrelationContract.CollectiveLibraryAccess,
            CortexProjectCorrelationPolicy.EffectiveLibraryAccess(first.Metadata));
        Assert.MatchesLowerHash(first.CorrelationHash);
        var serialized = System.Text.Json.JsonSerializer.Serialize(first);
        Assert.False(serialized.Contains("path", StringComparison.OrdinalIgnoreCase));
        Assert.False(serialized.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.False(serialized.Contains("authorityId", StringComparison.OrdinalIgnoreCase));

        var confirmed = CortexProjectCorrelationPolicy.Confirm(
            access,
            first,
            first.CorrelationHash);
        CortexProjectCorrelationPolicy.ValidateStoredLink(
            Access(CortexProjectLinkCapability.Read),
            confirmed,
            first.Metadata.ProjectLinkId);

        var reserved = CortexProjectCorrelationPolicy.Prepare(
            access,
            "project-pegasus-cortex",
            "store-workstation-a",
            1,
            "pegasus-cortex-arm64",
            "hybrid-v1",
            CortexProjectCorrelationContract.ReservedLibraryAccess);
        Assert.False(string.Equals(
            first.Metadata.ProjectLinkId,
            reserved.Metadata.ProjectLinkId,
            StringComparison.Ordinal));
        Assert.Equal(
            CortexProjectCorrelationContract.ReservedLibraryAccess,
            CortexProjectCorrelationPolicy.EffectiveLibraryAccess(reserved.Metadata));

        var legacyMetadata = new CortexProjectCorrelationMetadataV1(
            CortexProjectCorrelationContract.LegacySchemaVersion,
            CortexProjectCorrelationContract.LegacyContractId,
            "legacy-project",
            "legacy-store",
            1,
            $"plink_{new string('1', 64)}",
            "project-legacy",
            "pegasus-cortex-arm64",
            "hybrid-v1");
        var legacySerializedMetadata = new LegacyCorrelationMetadata(
            legacyMetadata.SchemaVersion,
            legacyMetadata.ContractId,
            legacyMetadata.ProjectId,
            legacyMetadata.StoreId,
            legacyMetadata.StoreEpoch,
            legacyMetadata.ProjectLinkId,
            legacyMetadata.ProjectNamespace,
            legacyMetadata.EndpointId,
            legacyMetadata.ContractVersion);
        var legacyHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(legacySerializedMetadata));
        var legacy = new CortexProjectLinkV1(legacyMetadata, legacyHash);
        CortexProjectCorrelationPolicy.ValidateStoredLink(
            Access(CortexProjectLinkCapability.Read),
            legacy,
            legacyMetadata.ProjectLinkId);
        Assert.Equal(
            CortexProjectCorrelationContract.CollectiveLibraryAccess,
            CortexProjectCorrelationPolicy.EffectiveLibraryAccess(legacy.Metadata));

        // Existing ARM64 rows were serialized before libraryAccess existed.
        // Prove their exact property set still deserializes and remains collective.
        var legacyPayload = CortexCanonicalJson.Serialize(
            new LegacyProjectLink(legacySerializedMetadata, legacy.CorrelationHash));
        var deserializedLegacy = CortexCanonicalJson.Deserialize<CortexProjectLinkV1>(legacyPayload);
        var reserializedLegacy = CortexCanonicalJson.Serialize(deserializedLegacy);
        Assert.True(legacyPayload.AsSpan().SequenceEqual(reserializedLegacy));
        CortexProjectCorrelationPolicy.ValidateStoredLink(
            Access(CortexProjectLinkCapability.Read),
            deserializedLegacy,
            legacyMetadata.ProjectLinkId);
        Assert.Equal(
            CortexProjectCorrelationContract.CollectiveLibraryAccess,
            CortexProjectCorrelationPolicy.EffectiveLibraryAccess(deserializedLegacy.Metadata));
        return Task.CompletedTask;
    }

    public static Task CorrelationTamperFailsClosedAsync()
    {
        var access = Access(CortexProjectLinkCapability.Correlate);
        var prepared = CortexProjectCorrelationPolicy.Prepare(
            access,
            "project-pegasus-cortex",
            "store-workstation-a",
            1,
            "pegasus-cortex-arm64",
            "hybrid-v1");
        var rejected = false;
        try
        {
            _ = CortexProjectCorrelationPolicy.Confirm(
                access,
                prepared,
                new string('0', 64));
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Assert.True(rejected);
        return Task.CompletedTask;
    }

    private static CortexProjectLinkAccess Access(CortexProjectLinkCapability capability) =>
        new(
            "local",
            CortexProjectCorrelationContract.ControlNamespace,
            "local-policy",
            $"codexsvc-{capability.ToString().ToLowerInvariant()}",
            "pegasus-cortex-mcp",
            1,
            capability);

    private sealed record LegacyCorrelationMetadata(
        int SchemaVersion,
        string ContractId,
        string ProjectId,
        string StoreId,
        long StoreEpoch,
        string ProjectLinkId,
        string ProjectNamespace,
        string EndpointId,
        string ContractVersion);

    private sealed record LegacyProjectLink(
        LegacyCorrelationMetadata Metadata,
        string CorrelationHash);
}
