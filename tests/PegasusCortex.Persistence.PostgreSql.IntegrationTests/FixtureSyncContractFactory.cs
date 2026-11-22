using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Produces one coherent server-authorized sync scope and canonical bundle chain for the live
/// fixture. Keeping identifiers here prevents workflow assertions from drifting between layers.
/// </summary>
internal static class FixtureSyncContractFactory
{
    public const string TenantId = "tenant-fixture";
    public const string Namespace = "project:fixture-live";
    public const string StoreId = "store-fixture";
    public const string StoreEpoch = "epoch-fixture";
    public const string ProjectId = "project-fixture";
    public const string PayloadContractId = "pegasus.cortex.fixture-payload/1.0";
    public const long PolicyRevision = 17;

    public static CortexSyncRepositoryScope Scope(
        string? tenantId = null,
        string? @namespace = null) =>
        new(
            tenantId ?? TenantId,
            @namespace ?? Namespace,
            "authority-fixture",
            "subject-fixture",
            "service-fixture",
            PolicyRevision,
            StoreId,
            StoreEpoch,
            ProjectId,
            "cortex.sync.submit");

    public static CortexSyncAuthorityContext Authority() =>
        new(
            TenantId,
            "authority-fixture",
            "subject-fixture",
            "service-fixture",
            PolicyRevision,
            [new CortexSyncNamespaceGrant(Namespace, CanSync: true)],
            [PayloadContractId]);

    public static CortexOfflineBundleV1 Origin(DateTimeOffset observedAt) =>
        CreateBundle(
            "bundle-origin",
            "event-origin",
            sequence: 1,
            baseServerRevision: null,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            observedAt);

    public static CortexOfflineBundleV1 Continue(
        CortexOfflineBundleV1 previous,
        string baseServerRevision,
        string bundleId,
        string eventId,
        DateTimeOffset observedAt) =>
        CreateBundle(
            bundleId,
            eventId,
            previous.LastSequence + 1,
            baseServerRevision,
            previous.BundleHash,
            previous.Events[^1].EventHash,
            observedAt);

    public static CortexOfflineBundleV1 CreateHeadConflict(DateTimeOffset observedAt) =>
        CreateBundle(
            "bundle-stale-origin",
            "event-stale-origin",
            sequence: 1,
            baseServerRevision: null,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            observedAt);

    private static CortexOfflineBundleV1 CreateBundle(
        string bundleId,
        string eventId,
        long sequence,
        string? baseServerRevision,
        string previousBundleHash,
        string priorEventHash,
        DateTimeOffset observedAt)
    {
        var utc = observedAt.ToUniversalTime();
        var payload = Encoding.UTF8.GetBytes(
            "{\"contractId\":\"pegasus.cortex.fixture-payload/1.0\",\"schemaVersion\":1}");
        var payloadHash = CortexCanonicalJson.Sha256(payload);
        var source = new CortexSyncSourceProvenanceV1(
            "client_operation",
            sourceRootId: null,
            sourceRelativePath: null,
            payload.Length,
            payloadHash,
            utc);
        var eventValue = CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineEvent,
            CortexSyncContractIds.Canonicalization,
            StoreId,
            StoreEpoch,
            ProjectId,
            sequence,
            eventId,
            priorEventHash,
            utc,
            "producer-fixture",
            $"operation-{eventId}",
            TenantId,
            Namespace,
            PolicyRevision,
            PayloadContractId,
            Base64Url(payload),
            payload.Length,
            payloadHash,
            source,
            CortexSyncContractIds.ContentTreatment,
            executable: false,
            [],
            CortexSyncContractIds.EmptySha256));

        return CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            bundleId,
            StoreId,
            StoreEpoch,
            ProjectId,
            baseServerRevision,
            previousBundleHash,
            priorEventHash,
            sequence,
            sequence,
            utc,
            [eventValue],
            [],
            CortexSyncContractIds.EmptySha256));
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
