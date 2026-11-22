namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// Stable, language-neutral identifiers for the Cortex offline/synchronization v1 family.
/// Contract identifiers carry the wire major/minor version; <c>SchemaVersion</c> remains
/// the integer major used by the existing Cortex contract conventions.
/// </summary>
public static class CortexSyncContractIds
{
    public const int SchemaVersion = 1;
    public const string Canonicalization = "pegasus-canonical-json-v1";
    public const string ContentTreatment = "untrusted_data";

    public const string LocalStoreManifest = "pegasus.cortex.local-store-manifest/1.0";
    public const string OfflineEvent = "pegasus.cortex.offline-event/1.0";
    public const string OfflineBundle = "pegasus.cortex.offline-bundle/1.0";
    public const string SyncReceipt = "pegasus.cortex.sync-receipt/1.0";
    public const string SyncCapabilities = "pegasus.cortex.sync-capabilities/1.0";
    public const string SyncGatewayHeadRequest = "pegasus.cortex.sync-gateway-head-request/1.0";
    public const string SyncGatewayHead = "pegasus.cortex.sync-gateway-head/1.0";
    public const string SyncGatewaySubmitRequest = "pegasus.cortex.sync-gateway-submit-request/1.0";
    public const string SyncGatewaySubmitResult = "pegasus.cortex.sync-gateway-submit-result/1.0";
    public const string CompatibilityRequest = "pegasus.cortex.sync-compatibility-request/1.0";
    public const string CompatibilityDecision = "pegasus.cortex.sync-compatibility-decision/1.0";
    public const string LegacyMigrationManifest = "pegasus.cortex.legacy-migration-manifest/1.0";
    public const string LegacyMigrationInventory = "pegasus.cortex.legacy-migration-inventory/1.0";
    public const string LegacyMigrationCursor = "pegasus.cortex.legacy-migration-cursor/1.0";
    public const string LegacyMigrationPage = "pegasus.cortex.legacy-migration-page/1.0";
    public const string LegacyMigrationRecord = "pegasus.cortex.legacy-migration-record/1.0";
    public const string LegacyFileCopyPayload = "pegasus.cortex.legacy-file-copy/1.0";

    public const string LegacyCortexSchemaVersion = "0.2";
    public const string EmptySha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";
}

/// <summary>
/// Hard v1 safety ceilings. A negotiated endpoint may advertise smaller effective limits,
/// but neither side may use capability negotiation to exceed these wire-contract bounds.
/// </summary>
public static class CortexSyncContractLimits
{
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumContractIdCharacters = 256;
    public const int MaximumDisplayNameCharacters = 256;
    public const int MaximumNamespaceCharacters = 256;
    public const int MaximumRelativePathCharacters = 512;
    public const int MaximumExactSourcePathCharacters = 1024;
    public const int MaximumReasonCharacters = 1024;
    public const int MaximumDiagnosticMessageCharacters = 1024;

    public const int MaximumExtensions = 16;
    public const int MaximumExtensionPayloadBytes = 16 * 1024;
    public const int MaximumTotalExtensionPayloadBytes = 64 * 1024;
    public const int MaximumContractIds = 64;
    public const int MaximumCapabilityIds = 64;
    public const int MaximumProtectionSuites = 16;
    public const int MaximumCompatibilityIssues = 32;

    public const int MaximumEventPayloadBytes = 512 * 1024;
    public const int MaximumEventsPerBundle = 256;
    public const int MaximumCanonicalBundleBytes = 32 * 1024 * 1024;
    public const int MaximumReceiptOutcomes = MaximumEventsPerBundle;
    public const int MaximumMigrationFiles = 4096;
    public const int MaximumMappedIdsPerFile = 64;
    public const int MaximumMigrationEvidenceIdsPerFile = 32;
    public const int MaximumMigrationReceiptIdsPerFile = 16;
    public const int MaximumLegacyMigrationPageEvents = 32;
    public const int MaximumJsonDepth = 32;

    // This bound is exactly representable by JavaScript and other common JSON consumers.
    public const long MaximumSequence = 9_007_199_254_740_991;
}

/// <summary>
/// Initial reconnect-gateway profile sized to fit beneath the Nexus 1 MiB
/// request ceiling after the gateway JSON envelope is added. These are
/// advertised limits, not wider MCP frame limits.
/// </summary>
public static class CortexSyncGatewayLimits
{
    public const int MaximumEventPayloadBytes = 32 * 1024;
    public const int MaximumEventsPerBundle = 16;
    public const int MaximumCanonicalBundleBytes = 768 * 1024;
    public const int MaximumReceiptOutcomes = MaximumEventsPerBundle;
    public const int NexusMaximumRequestBytes = 1024 * 1024;
}

public static class CortexSyncCapabilityIds
{
    public const string CopyFirstMigration = "copy_first_migration";
    public const string DurableReceipts = "durable_receipts";
    public const string EventHashChain = "event_hash_chain";
    public const string ExactSourceProvenance = "exact_source_provenance";
    public const string PagedLegacyMigration = "paged_legacy_migration";
    public const string ServerReadBackReconciliation = "server_read_back_reconciliation";
    public const string CurrentRecoveryMaterialization = "current_recovery_materialization";
}

public static class CortexSyncDispositionIds
{
    public const string Applied = "applied";
    public const string Duplicate = "duplicate";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";
    public const string Quarantined = "quarantined";
}

public static class CortexLegacyFileDispositionIds
{
    public const string DiscoveredUnmapped = "discovered_unmapped";
    public const string MappedStructured = "mapped_structured";
    public const string CopiedVerbatim = "copied_verbatim";
    public const string PreservedUnsupported = "preserved_unsupported";
    public const string ExcludedSensitive = "excluded_sensitive";
    public const string RejectedUnsafe = "rejected_unsafe";
    public const string MissingDeclared = "missing_declared";
}

public static class CortexLegacyFileLifecycleIds
{
    public const string Discovered = "discovered";
    public const string Copied = "copied";
    public const string Validated = "validated";
    public const string Uploaded = "uploaded";
    public const string ReadBack = "read_back";
    public const string Reconciled = "reconciled";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";
}
