using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;

internal static class SyncContractSourceTests
{
    private static readonly string[] CoreFamily =
    [
        CortexSyncContractIds.OfflineEvent,
        CortexSyncContractIds.OfflineBundle,
        CortexSyncContractIds.SyncReceipt,
        CortexSyncContractIds.SyncCapabilities,
        CortexSyncContractIds.CompatibilityRequest,
        CortexSyncContractIds.CompatibilityDecision
    ];

    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
    [
        ("sync schemas are closed bounded and copy first", SchemasAreClosedAndBounded),
        ("local store layout is protected distinct and contained", LocalStoreLayoutIsContained),
        ("offline event and bundle hashes bind decoded immutable bytes", EventAndBundleIntegrity),
        ("sync gateway contracts are distinct sealed and scope bound", GatewayContractsAreSealed),
        ("sync compatibility fails closed on unknown required features", CompatibilityFailsClosed),
        ("sql first clients do not require local or legacy contracts", SqlFirstCompatibility),
        ("receipt proof covers a cycle free semantic body", ReceiptIsAuthenticatedAndReconciled),
        ("legacy migration is copy first path aware and permits empty files", LegacyMigrationIsReversible),
        ("legacy migration pages are hash bound acknowledgment gated and resumable", LegacyMigrationPageIsBounded),
        ("legacy file copies use a typed bounded data-only envelope", LegacyFileCopyPayloadIsTypedAndBounded)
    ];

    private static void LocalStoreLayoutIsContained()
    {
        var value = CortexSyncContractPolicy.Seal(new CortexLocalStoreManifestV1(
            1, CortexSyncContractIds.LocalStoreManifest, CortexSyncContractIds.Canonicalization,
            "store_1", "epoch_1", "project_1", "Project One",
            DateTimeOffset.Parse("2026-07-14T20:00:00Z"), ".cortex/offline",
            ".cortex/offline/b", ".cortex/offline/r", ".cortex/offline/q", true,
            "new", null, CortexSyncContractIds.ContentTreatment, false, [], CortexSyncContractIds.EmptySha256));
        Check(CortexSyncContractPolicy.HasValidHash(value), "Contained local-store manifest hash did not validate.");
        Throws<ArgumentException>(() => new CortexLocalStoreManifestV1(
            1, CortexSyncContractIds.LocalStoreManifest, CortexSyncContractIds.Canonicalization,
            "store_1", "epoch_1", "project_1", "Project One",
            DateTimeOffset.Parse("2026-07-14T20:00:00Z"), ".cortex/offline",
            ".cortex/offline/b", ".cortex/offline/r", ".cortex/other/q", true,
            "new", null, CortexSyncContractIds.ContentTreatment, false, [], CortexSyncContractIds.EmptySha256));
    }

    private static void SchemasAreClosedAndBounded()
    {
        var root = FindProjectRoot();
        var names = new[]
        {
            "cortex-sync-local-store-manifest.v1.schema.json",
            "cortex-sync-offline-bundle.v1.schema.json",
            "cortex-sync-receipt.v1.schema.json",
            "cortex-sync-capabilities.v1.schema.json",
            "cortex-sync-gateway.v1.schema.json",
            "cortex-sync-legacy-migration-manifest.v1.schema.json",
            "cortex-sync-legacy-migration-inventory.v1.schema.json",
            "cortex-sync-legacy-migration-page.v1.schema.json",
            "cortex-sync-legacy-migration-record.v1.schema.json",
            "cortex-sync-legacy-file-copy-payload.v1.schema.json"
        };
        foreach (var name in names)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", name)));
            Check(!document.RootElement.GetRawText().Contains("\"additionalProperties\": true", StringComparison.Ordinal),
                $"{name} must not contain an open object schema.");
        }

        using var bundle = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[1])));
        var eventSchema = bundle.RootElement.GetProperty("$defs").GetProperty("offlineEvent");
        Check(!eventSchema.GetProperty("additionalProperties").GetBoolean(), "Offline events must reject unknown base fields.");
        Check(eventSchema.GetProperty("properties").GetProperty("payloadBytes").GetProperty("maximum").GetInt32() ==
              CortexSyncContractLimits.MaximumEventPayloadBytes, "Event payload limit drifted from source.");
        Check(!eventSchema.GetProperty("properties").GetProperty("source").GetRawText()
            .Contains("exactSourcePath", StringComparison.Ordinal), "Wire event provenance must not expose absolute paths.");

        using var gateway = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[4])));
        var gatewayRaw = gateway.RootElement.GetRawText();
        Check(gatewayRaw.Contains(CortexSyncContractIds.SyncGatewayHeadRequest, StringComparison.Ordinal) &&
              gatewayRaw.Contains(CortexSyncContractIds.SyncGatewayHead, StringComparison.Ordinal) &&
              gatewayRaw.Contains(CortexSyncContractIds.SyncGatewaySubmitRequest, StringComparison.Ordinal) &&
              gatewayRaw.Contains(CortexSyncContractIds.SyncGatewaySubmitResult, StringComparison.Ordinal),
            "The gateway schema does not close all four versioned wire shapes.");

        using var migration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[5])));
        var raw = migration.RootElement.GetRawText();
        Check(raw.Contains("prohibited_until_separate_approval", StringComparison.Ordinal), "Migration cleanup must remain prohibited.");
        Check(raw.Contains("not_approved", StringComparison.Ordinal), "Migration cutover must remain unapproved.");
        Check(raw.Contains("sourcePathCaseSensitive", StringComparison.Ordinal), "Migration path comparison semantics must be explicit.");
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[6])));
        var inventoryRaw = inventory.RootElement.GetRawText();
        Check(inventoryRaw.Contains("sourceManifestHash", StringComparison.Ordinal) &&
              inventoryRaw.Contains("fileIndex", StringComparison.Ordinal),
            "Migration inventory must bind the exact manifest hash and stable ordered file positions.");
        Check(!inventoryRaw.Contains("exactSourcePath", StringComparison.Ordinal),
            "Portable migration inventory must not expose local absolute paths.");
        using var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[7])));
        var pageRaw = page.RootElement.GetRawText();
        Check(pageRaw.Contains("acknowledgmentRequired", StringComparison.Ordinal), "Migration paging must require explicit acknowledgment.");
        Check(pageRaw.Contains("sourceMutationAllowed", StringComparison.Ordinal), "Migration paging must preserve the source-mutation denial.");
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", names[8])));
        var recordRaw = record.RootElement.GetRawText();
        Check(recordRaw.Contains("supersedesRevisionId", StringComparison.Ordinal), "Migration records must carry explicit supersession semantics.");
        Check(recordRaw.Contains("tombstone", StringComparison.Ordinal), "Migration records must carry explicit tombstone semantics.");
        Check(recordRaw.Contains("sourceInventoryHash", StringComparison.Ordinal) &&
              recordRaw.Contains("sourceFileIndex", StringComparison.Ordinal),
            "Migration records must bind exact inventory identity and ordered source position.");
    }

    private static void LegacyFileCopyPayloadIsTypedAndBounded()
    {
        var original = Encoding.UTF8.GetBytes("legacy markdown\n");
        var payload = new CortexLegacyFileCopyPayloadV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyFileCopyPayload,
            CortexSyncContractIds.Canonicalization,
            "source-root-1",
            "archive/STATE.md",
            "state",
            "map-state-1",
            1,
            1,
            0,
            original.Length,
            CortexCanonicalJson.Sha256(original),
            Base64Url(original),
            original.Length,
            CortexCanonicalJson.Sha256(original),
            CortexSyncContractIds.ContentTreatment,
            executable: false);
        var canonical = CortexCanonicalJson.Serialize(payload);
        Check(canonical.Length <= CortexSyncContractLimits.MaximumEventPayloadBytes,
            "The legacy file-copy envelope exceeds the public event payload ceiling.");
        Check(CortexCanonicalJson.Deserialize<CortexLegacyFileCopyPayloadV1>(canonical) == payload,
            "The legacy file-copy envelope did not round-trip canonically.");
        var empty = new CortexLegacyFileCopyPayloadV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyFileCopyPayload,
            CortexSyncContractIds.Canonicalization,
            "source-root-1",
            "archive/.gitkeep",
            "legacy-file",
            "map-empty-1",
            1,
            1,
            0,
            0,
            CortexCanonicalJson.Sha256(Array.Empty<byte>()),
            string.Empty,
            0,
            CortexCanonicalJson.Sha256(Array.Empty<byte>()),
            CortexSyncContractIds.ContentTreatment,
            executable: false);
        Check(empty.SourceBytes == 0, "An empty retained legacy file was rejected.");
        Throws<ArgumentException>(() => new CortexLegacyFileCopyPayloadV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.LegacyFileCopyPayload,
            CortexSyncContractIds.Canonicalization,
            "source-root-1",
            "archive/STATE.md",
            "state",
            "map-state-1",
            1,
            1,
            0,
            original.Length,
            CortexCanonicalJson.Sha256(original),
            Base64Url(original),
            original.Length,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.ContentTreatment,
            executable: false));
    }

    private static void EventAndBundleIntegrity()
    {
        var payload = Encoding.UTF8.GetBytes("{\"contractId\":\"pegasus.cortex.test-payload/1.0\",\"schemaVersion\":1}");
        var sourceEvents = new List<CortexOfflineEventV1>
        {
            Event(payload, 1, CortexSyncContractIds.EmptySha256)
        };
        var sealedEvent = CortexSyncContractPolicy.Seal(sourceEvents[0]);
        sourceEvents[0] = sealedEvent;
        var bundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            "bundle_1",
            "store_1",
            "epoch_1",
            "project_1",
            "server_rev_1",
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            1,
            1,
            DateTimeOffset.Parse("2026-07-14T20:00:01Z"),
            sourceEvents,
            [],
            CortexSyncContractIds.EmptySha256));

        sourceEvents.Clear();
        Check(bundle.Events.Count == 1, "Bundle did not snapshot its caller-owned event collection.");
        Check(CortexSyncContractPolicy.HasValidHash(sealedEvent), "Sealed event hash did not validate.");
        Check(CortexSyncContractPolicy.HasValidHash(bundle), "Sealed bundle hash did not validate.");
        var secondEvent = CortexSyncContractPolicy.Seal(Event(payload, 2, sealedEvent.EventHash));
        var secondBundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1, CortexSyncContractIds.OfflineBundle, CortexSyncContractIds.Canonicalization,
            "bundle_2", "store_1", "epoch_1", "project_1", "server_rev_1",
            bundle.BundleHash, sealedEvent.EventHash, 2, 2,
            DateTimeOffset.Parse("2026-07-14T20:00:02Z"), [secondEvent], [], CortexSyncContractIds.EmptySha256));
        Check(CortexSyncContractPolicy.ValidateBundleContinuation(bundle, secondBundle).Accepted,
            "The exact cross-bundle sequence and hash continuation was rejected.");

        var badHashEvent = Event(payload, 1, CortexSyncContractIds.EmptySha256, payloadHash: CortexSyncContractIds.EmptySha256);
        Throws<ArgumentException>(() => CortexSyncContractPolicy.Seal(badHashEvent));

        var json = JsonNode.Parse(CortexCanonicalJson.Serialize(sealedEvent))!.AsObject();
        json["unsupportedBaseField"] = true;
        Throws<JsonException>(() => CortexCanonicalJson.Deserialize<CortexOfflineEventV1>(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    private static void CompatibilityFailsClosed()
    {
        var server = Capabilities();
        var futureCapability = "future_required_capability";
        var client = Request(
            supportedCapabilities: [.. RequiredCapabilities(), futureCapability],
            requiredServerCapabilities: [futureCapability]);
        var decision = CortexSyncContractPolicy.EvaluateCompatibility(server, client);
        Check(!decision.Accepted, "An unknown required capability was accepted.");
        Check(decision.Issues.Any(item => item.Code == "sync_required_server_capability_unknown"),
            "The required-capability rejection was not explicit.");

        var futureExtension = "pegasus.cortex.future-extension/1.0";
        var extensionBytes = Encoding.UTF8.GetBytes(
            $"{{\"contractId\":\"{futureExtension}\",\"schemaVersion\":1}}");
        var extensionClient = Request(
            supportedExtensions: [futureExtension],
            requiredServerExtensions: [futureExtension],
            extensions:
            [
                new CortexSyncExtensionV1(
                    futureExtension,
                    true,
                    Base64Url(extensionBytes),
                    extensionBytes.Length,
                    CortexCanonicalJson.Sha256(extensionBytes))
            ]);
        var extensionDecision = CortexSyncContractPolicy.EvaluateCompatibility(server, extensionClient);
        Check(!extensionDecision.Accepted, "An unknown required extension was accepted.");
        Check(extensionDecision.Issues.Any(item => item.Code == "sync_required_server_extension_unknown"),
            "The required-extension rejection was not explicit.");

        var corruptExtensionClient = Request(
            supportedExtensions: [futureExtension],
            extensions:
            [
                new CortexSyncExtensionV1(
                    futureExtension, false, Base64Url(extensionBytes), extensionBytes.Length,
                    CortexSyncContractIds.EmptySha256)
            ]);
        var corruptExtensionDecision = CortexSyncContractPolicy.EvaluateCompatibility(server, corruptExtensionClient);
        Check(corruptExtensionDecision.Issues.Any(item => item.Code == "sync_client_extension_integrity_invalid"),
            "Compatibility did not recompute an optional extension payload hash.");
    }

    private static void SqlFirstCompatibility()
    {
        var decision = CortexSyncContractPolicy.EvaluateCompatibility(Capabilities(), Request());
        Check(decision.Accepted, "The core SQL-first synchronization family should negotiate successfully.");
        Check(!CoreFamily.Contains(CortexSyncContractIds.LocalStoreManifest, StringComparer.Ordinal),
            "The local store manifest unexpectedly became a mandatory server wire contract.");
        Check(!CoreFamily.Contains(CortexSyncContractIds.LegacyMigrationManifest, StringComparer.Ordinal),
            "Legacy migration unexpectedly became mandatory for a new SQL-first system.");
    }

    private static void ReceiptIsAuthenticatedAndReconciled()
    {
        var payload = Encoding.UTF8.GetBytes("{\"contractId\":\"pegasus.cortex.test-payload/1.0\",\"schemaVersion\":1}");
        var item = CortexSyncContractPolicy.Seal(Event(payload, 1, CortexSyncContractIds.EmptySha256));
        var bundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1, CortexSyncContractIds.OfflineBundle, CortexSyncContractIds.Canonicalization,
            "bundle_receipt", "store_1", "epoch_1", "project_1", null,
            CortexSyncContractIds.EmptySha256, CortexSyncContractIds.EmptySha256,
            1, 1, DateTimeOffset.Parse("2026-07-14T20:00:01Z"), [item], [], CortexSyncContractIds.EmptySha256));

        var proofOne = Encoding.UTF8.GetBytes("proof-one");
        var proofTwo = Encoding.UTF8.GetBytes("proof-two");
        var provisionalOne = Receipt(bundle, proofOne, CortexSyncContractIds.EmptySha256);
        var provisionalTwo = Receipt(bundle, proofTwo, CortexSyncContractIds.EmptySha256);
        var semanticHash = CortexSyncContractPolicy.ComputeReceiptSemanticHash(provisionalOne);
        Check(string.Equals(semanticHash, CortexSyncContractPolicy.ComputeReceiptSemanticHash(provisionalTwo), StringComparison.Ordinal),
            "Authentication proof bytes leaked into the semantic receipt hash.");

        var receipt = Receipt(bundle, proofOne, semanticHash);
        Check(CortexSyncContractPolicy.HasValidSemanticHash(receipt), "Receipt semantic hash did not validate.");
        var validation = CortexSyncContractPolicy.ValidateReceipt(receipt, bundle, authenticationProofVerified: true);
        Check(validation.Accepted && validation.DurablyReconciled, "Authenticated durable reconciliation was not recognized.");
        var unauthenticated = CortexSyncContractPolicy.ValidateReceipt(receipt, bundle, authenticationProofVerified: false);
        Check(!unauthenticated.Accepted && !unauthenticated.DurablyReconciled,
            "A local receipt file was treated as proof without trusted authentication.");
    }

    private static void GatewayContractsAreSealed()
    {
        Check(CortexSyncContractIds.SyncGatewayHeadRequest != CortexSyncContractIds.SyncGatewayHead &&
              CortexSyncContractIds.SyncGatewaySubmitRequest != CortexSyncContractIds.SyncGatewaySubmitResult,
            "Gateway request and response contracts must remain distinct.");

        var payload = Encoding.UTF8.GetBytes(
            "{\"contractId\":\"pegasus.cortex.test-payload/1.0\",\"schemaVersion\":1}");
        var item = CortexSyncContractPolicy.Seal(
            Event(payload, 1, CortexSyncContractIds.EmptySha256));
        var bundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            "bundle_gateway",
            "store_1",
            "epoch_1",
            "project_1",
            null,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            1,
            1,
            DateTimeOffset.Parse("2026-07-14T20:00:01Z"),
            [item],
            [],
            CortexSyncContractIds.EmptySha256));
        var projectLinkId = $"plink_{new string('a', 64)}";
        var request = CortexSyncContractPolicy.Seal(
            new CortexSyncSubmitRequest(projectLinkId, bundle));
        Check(CortexSyncContractPolicy.HasValidHash(request),
            "The sealed gateway submit request did not validate.");

        var observedAt = DateTimeOffset.Parse("2026-07-14T20:01:03+00:00");
        var head = CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
            1,
            CortexSyncContractIds.SyncGatewayHead,
            CortexSyncContractIds.Canonicalization,
            true,
            projectLinkId,
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId,
            item.Sequence,
            item.EventId,
            item.EventHash,
            bundle.BundleId,
            bundle.BundleHash,
            new string('b', 64),
            item.PayloadSha256,
            1,
            new string('c', 64),
            $"ctx_{new string('d', 64)}",
            "server_rev_2",
            observedAt,
            CortexSyncContractIds.EmptySha256));
        Check(CortexSyncContractPolicy.HasValidHash(head),
            "The sealed gateway head did not validate.");

        var proof = Encoding.UTF8.GetBytes("gateway-proof");
        var provisional = Receipt(bundle, proof, CortexSyncContractIds.EmptySha256);
        var receipt = Receipt(
            bundle,
            proof,
            CortexSyncContractPolicy.ComputeReceiptSemanticHash(provisional));
        var result = CortexSyncContractPolicy.Seal(new CortexSyncSubmitResult(
            1,
            CortexSyncContractIds.SyncGatewaySubmitResult,
            CortexSyncContractIds.Canonicalization,
            receipt,
            head,
            CortexSyncContractIds.EmptySha256));
        Check(CortexSyncContractPolicy.HasValidHash(result),
            "The sealed gateway submit result did not validate.");

        var tamperedHead = new CortexSyncRemoteHead(
            head.SchemaVersion,
            head.ContractId,
            head.Canonicalization,
            head.Exists,
            head.ProjectLinkId,
            head.StoreId,
            head.StoreEpoch,
            head.ProjectId,
            head.HighWaterSequence,
            head.LastEventId,
            head.LastEventHash,
            head.LastBundleId,
            head.LastBundleHash,
            new string('e', 64),
            head.RetainedPayloadHash,
            head.RetainedSnapshotRevision,
            head.RetainedSnapshotHash,
            head.ContextSetId,
            head.ServerRevision,
            head.ObservedAt,
            head.HeadHash);
        Check(!CortexSyncContractPolicy.HasValidHash(tamperedHead),
            "A tampered gateway authority hash retained the original self-hash.");

        var nearMaximumPayload = CortexCanonicalJson.Serialize(new
        {
            contractId = "pegasus.cortex.test-payload/1.0",
            schemaVersion = 1,
            data = new string('x', 32_000)
        });
        var maximumEvents = new List<CortexOfflineEventV1>(
            CortexSyncGatewayLimits.MaximumEventsPerBundle);
        var previousEventHash = CortexSyncContractIds.EmptySha256;
        for (var sequence = 1; sequence <= CortexSyncGatewayLimits.MaximumEventsPerBundle; sequence++)
        {
            var maximumEvent = CortexSyncContractPolicy.Seal(
                Event(nearMaximumPayload, sequence, previousEventHash));
            maximumEvents.Add(maximumEvent);
            previousEventHash = maximumEvent.EventHash;
        }
        var maximumBundle = CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1,
            CortexSyncContractIds.OfflineBundle,
            CortexSyncContractIds.Canonicalization,
            "bundle_gateway_maximum",
            "store_1",
            "epoch_1",
            "project_1",
            null,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            1,
            CortexSyncGatewayLimits.MaximumEventsPerBundle,
            DateTimeOffset.Parse("2026-07-14T20:00:01Z"),
            maximumEvents,
            [],
            CortexSyncContractIds.EmptySha256));
        var maximumRequest = CortexSyncContractPolicy.Seal(
            new CortexSyncSubmitRequest(projectLinkId, maximumBundle));
        Check(CortexCanonicalJson.Serialize(maximumBundle).Length <=
              CortexSyncGatewayLimits.MaximumCanonicalBundleBytes,
            "The initial advertised gateway event profile does not fit its canonical bundle ceiling.");
        Check(CortexCanonicalJson.Serialize(maximumRequest).Length <
              CortexSyncGatewayLimits.NexusMaximumRequestBytes,
            "The initial gateway submit request exceeds the Nexus 1 MiB request ceiling.");

        var maximumProof = Encoding.UTF8.GetBytes("gateway-maximum-proof");
        var maximumProvisionalReceipt = Receipt(
            maximumBundle,
            maximumProof,
            CortexSyncContractIds.EmptySha256);
        var maximumReceipt = Receipt(
            maximumBundle,
            maximumProof,
            CortexSyncContractPolicy.ComputeReceiptSemanticHash(maximumProvisionalReceipt));
        var maximumLastEvent = maximumBundle.Events[^1];
        var maximumHead = CortexSyncContractPolicy.Seal(new CortexSyncRemoteHead(
            1,
            CortexSyncContractIds.SyncGatewayHead,
            CortexSyncContractIds.Canonicalization,
            true,
            projectLinkId,
            maximumBundle.StoreId,
            maximumBundle.StoreEpoch,
            maximumBundle.ProjectId,
            maximumLastEvent.Sequence,
            maximumLastEvent.EventId,
            maximumLastEvent.EventHash,
            maximumBundle.BundleId,
            maximumBundle.BundleHash,
            new string('b', 64),
            maximumLastEvent.PayloadSha256,
            16,
            new string('c', 64),
            $"ctx_{new string('d', 64)}",
            "server_rev_2",
            observedAt,
            CortexSyncContractIds.EmptySha256));
        var maximumResult = CortexSyncContractPolicy.Seal(new CortexSyncSubmitResult(
            1,
            CortexSyncContractIds.SyncGatewaySubmitResult,
            CortexSyncContractIds.Canonicalization,
            maximumReceipt,
            maximumHead,
            CortexSyncContractIds.EmptySha256));
        Check(CortexCanonicalJson.Serialize(maximumResult).Length <
              CortexSyncGatewayLimits.NexusMaximumRequestBytes,
            "The maximum gateway submit result exceeds the Nexus 1 MiB envelope ceiling.");
    }

    private static void LegacyMigrationIsReversible()
    {
        var meta = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"0.2\"}");
        var empty = Array.Empty<byte>();
        var discoveredFiles = new[]
        {
            new CortexLegacyMigrationFileV1(
                "meta", @"D:\Legacy\Project\.cortex\meta.json", ".cortex/meta.json", meta.Length,
                CortexCanonicalJson.Sha256(meta), CortexLegacyFileDispositionIds.DiscoveredUnmapped,
                CortexLegacyFileLifecycleIds.Discovered, "legacy_meta_discovered",
                "The legacy metadata source was inventoried but has not yet been copied.", [], [], [], []),
            new CortexLegacyMigrationFileV1(
                "worklog", @"D:\Legacy\Project\.cortex\EMPTY.md", ".cortex/EMPTY.md", 0,
                CortexCanonicalJson.Sha256(empty), CortexLegacyFileDispositionIds.DiscoveredUnmapped,
                CortexLegacyFileLifecycleIds.Discovered, "empty_file_discovered",
                "The zero-byte source file was inventoried but has not yet been copied.", [], [], [], [])
        };
        var initial = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationManifestV1(
            1, CortexSyncContractIds.LegacyMigrationManifest, CortexSyncContractIds.Canonicalization,
            "migration_1", "store_1", "epoch_1", "project_1", "source_root_1", "0.2",
            "windows", false, @"D:\Legacy\Project", @"D:\Legacy\Project\.cortex\meta.json",
            meta.Length, CortexCanonicalJson.Sha256(meta), DateTimeOffset.Parse("2026-07-14T20:00:00Z"),
            1, CortexSyncContractIds.EmptySha256, discoveredFiles, "copy_first", false,
            "prohibited_until_separate_approval", "not_approved", true, [], CortexSyncContractIds.EmptySha256));
        var files = new[]
        {
            new CortexLegacyMigrationFileV1(
                "meta", @"D:\Legacy\Project\.cortex\meta.json", ".cortex/meta.json", meta.Length,
                CortexCanonicalJson.Sha256(meta), CortexLegacyFileDispositionIds.MappedStructured,
                CortexLegacyFileLifecycleIds.Copied, "legacy_meta_mapped",
                "Mapped from the bounded 0.2 allowlist.", ["event_meta"],
                ["pegasus.cortex.legacy-meta-copy/1.0"], ["evidence_meta_copy"], []),
            new CortexLegacyMigrationFileV1(
                "worklog", @"D:\Legacy\Project\.cortex\EMPTY.md", ".cortex/EMPTY.md", 0,
                CortexCanonicalJson.Sha256(empty), CortexLegacyFileDispositionIds.CopiedVerbatim,
                CortexLegacyFileLifecycleIds.Copied, "empty_file_preserved",
                "The zero-byte source file is preserved exactly.", ["event_empty"],
                ["pegasus.cortex.legacy-file-copy/1.0"], ["evidence_empty_copy"], [])
        };
        var manifest = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationManifestV1(
            1, CortexSyncContractIds.LegacyMigrationManifest, CortexSyncContractIds.Canonicalization,
            "migration_1", "store_1", "epoch_1", "project_1", "source_root_1", "0.2",
            "windows", false, @"D:\Legacy\Project", @"D:\Legacy\Project\.cortex\meta.json",
            meta.Length, CortexCanonicalJson.Sha256(meta), DateTimeOffset.Parse("2026-07-14T20:00:01Z"),
            2, initial.ManifestHash, files, "copy_first", false, "prohibited_until_separate_approval", "not_approved", true,
            [], CortexSyncContractIds.EmptySha256));
        Check(CortexSyncContractPolicy.HasValidHash(manifest), "Copy-first manifest hash did not validate.");
        Check(CortexSyncContractPolicy.ValidateMigrationProgress(initial, manifest).Accepted,
            "A chained discovered-to-copied migration snapshot did not validate.");
        Check(manifest.Files.Single(item => item.Role == "worklog").SourceBytes == 0,
            "A legitimate zero-byte legacy file was not preserved.");
        Check(!manifest.SourceMutationAllowed && manifest.CleanupState == "prohibited_until_separate_approval" &&
              manifest.CutoverState == "not_approved", "Migration safety gates were weakened.");

        Throws<ArgumentException>(() => new CortexLegacyMigrationManifestV1(
            1, CortexSyncContractIds.LegacyMigrationManifest, CortexSyncContractIds.Canonicalization,
            "migration_1", "store_1", "epoch_1", "project_1", "source_root_1", "0.2",
            "windows", false, @"D:\Legacy\Project", @"D:\Legacy\Project\.cortex\meta.json",
            meta.Length, CortexCanonicalJson.Sha256(meta), DateTimeOffset.Parse("2026-07-14T20:00:01Z"),
            2, initial.ManifestHash, files, "copy_first", true, "prohibited_until_separate_approval", "not_approved", true,
            [], CortexSyncContractIds.EmptySha256));
    }

    private static void LegacyMigrationPageIsBounded()
    {
        var firstBytes = Enumerable.Repeat((byte)0x41, CortexLegacyFileCopyPayloadV1.MaximumChunkBytes + 1).ToArray();
        var secondBytes = Encoding.UTF8.GetBytes("legacy tasks\n");
        var manifestHash = new string('a', 64);
        var capturedAt = DateTimeOffset.Parse("2026-07-14T20:00:00Z");
        var inventory = CortexSyncContractPolicy.Seal(new CortexLegacyMigrationInventoryV1(
            1, CortexSyncContractIds.LegacyMigrationInventory, CortexSyncContractIds.Canonicalization,
            "migration_1", manifestHash, "store_1", "epoch_1", "project_1", "source_root_1",
            capturedAt, true,
            [
                new CortexLegacyMigrationInventoryFileV1(
                    0, "state", ".cortex/STATE.md", firstBytes.Length, CortexCanonicalJson.Sha256(firstBytes),
                    "legacy_record_1", "legacy_revision_1", 2),
                new CortexLegacyMigrationInventoryFileV1(
                    1, "tasks", ".cortex/TASKS.md", secondBytes.Length, CortexCanonicalJson.Sha256(secondBytes),
                    "legacy_record_2", "legacy_revision_2", 1)
            ],
            firstBytes.Length + secondBytes.Length, CortexSyncContractIds.ContentTreatment, false, [],
            CortexSyncContractIds.EmptySha256));

        var current = MigrationCursor(
            inventory, 1, 0, 0, 1, CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256, completed: false);
        Check(CortexSyncContractPolicy.IsCanonicalInitialLegacyMigrationCursor(current),
            "The exact file-zero/chunk-zero initial cursor was not recognized.");
        var firstEvent = MigrationEvent(inventory, 0, firstBytes, 0, 1, current.PriorEventHash);
        var firstBundle = MigrationBundle("migration_page_bundle_1", current.PreviousBundleHash, [firstEvent]);
        var continuation = MigrationCursor(
            inventory, 2, 0, 1, 2, firstBundle.BundleHash, firstEvent.EventHash, completed: false);
        Check(!CortexSyncContractPolicy.IsCanonicalInitialLegacyMigrationCursor(continuation),
            "A self-hashed noninitial cursor was incorrectly treated as an unproven initial position.");
        var firstPage = MigrationPage("migration-page-0001", inventory, current, firstBundle, continuation);
        Check(CortexSyncContractPolicy.HasValidHash(firstPage), "The first ordered migration page hash did not validate.");

        var finalFirstFileEvent = MigrationEvent(inventory, 0, firstBytes, 1, 2, continuation.PriorEventHash);
        var secondFileEvent = MigrationEvent(inventory, 1, secondBytes, 0, 3, finalFirstFileEvent.EventHash);
        var secondBundle = MigrationBundle(
            "migration_page_bundle_2", continuation.PreviousBundleHash, [finalFirstFileEvent, secondFileEvent]);
        var completed = MigrationCursor(
            inventory, 3, 2, 0, 4, secondBundle.BundleHash, secondFileEvent.EventHash, completed: true);
        var secondPage = MigrationPage("migration-page-0002", inventory, continuation, secondBundle, completed);
        Check(CortexSyncContractPolicy.ValidateLegacyMigrationPage(secondPage).Accepted,
            "Valid multi-file continuation did not reach the exact inventory terminus.");
        Check(secondPage.NextCursor.Completed && secondPage.NextCursor.NextFileIndex == inventory.Files.Count,
            "Terminal completion did not bind the exact inventory file count.");

        var mismatchedFile = MigrationEvent(inventory, 1, secondBytes, 0, 1, current.PriorEventHash);
        var mismatchedBundle = MigrationBundle("migration_page_bad_file", current.PreviousBundleHash, [mismatchedFile]);
        var mismatchedNext = MigrationCursor(
            inventory, 2, 1, 0, 2, mismatchedBundle.BundleHash, mismatchedFile.EventHash, completed: false);
        ExpectMigrationPageRejected("migration-page-bad-file", inventory, current, mismatchedBundle, mismatchedNext);

        var prematureNext = MigrationCursor(
            inventory, 2, 2, 0, 2, firstBundle.BundleHash, firstEvent.EventHash, completed: true);
        ExpectMigrationPageRejected("migration-page-premature", inventory, current, firstBundle, prematureNext);

        var reorderedFirst = MigrationEvent(inventory, 1, secondBytes, 0, 1, current.PriorEventHash);
        var reorderedSecond = MigrationEvent(inventory, 0, firstBytes, 0, 2, reorderedFirst.EventHash);
        var reorderedBundle = MigrationBundle(
            "migration_page_reordered", current.PreviousBundleHash, [reorderedFirst, reorderedSecond]);
        var reorderedNext = MigrationCursor(
            inventory, 2, 2, 0, 3, reorderedBundle.BundleHash, reorderedSecond.EventHash, completed: true);
        ExpectMigrationPageRejected("migration-page-reordered", inventory, current, reorderedBundle, reorderedNext);

        var duplicatedChunk = MigrationEvent(inventory, 0, firstBytes, 0, 2, firstEvent.EventHash);
        var duplicateBundle = MigrationBundle(
            "migration_page_duplicate", current.PreviousBundleHash, [firstEvent, duplicatedChunk]);
        var duplicateNext = MigrationCursor(
            inventory, 2, 0, 1, 3, duplicateBundle.BundleHash, duplicatedChunk.EventHash, completed: false);
        ExpectMigrationPageRejected("migration-page-duplicate", inventory, current, duplicateBundle, duplicateNext);

        var emptyHash = CortexCanonicalJson.Sha256(Array.Empty<byte>());
        var tombstone = new CortexLegacyMigrationRecordV1(
            1, CortexSyncContractIds.LegacyMigrationRecord, CortexSyncContractIds.Canonicalization,
            "legacy_record_1", "legacy_revision_3", "project.default", "state", "source_root_1",
            manifestHash, inventory.InventoryHash, 0, ".cortex/STATE.md", 0,
            inventory.Files[0].SourceSha256, inventory.Files[0].RevisionId, true,
            0, 0, 0, string.Empty, 0, emptyHash, CortexSyncContractIds.ContentTreatment, false);
        Check(tombstone.Tombstone && tombstone.SupersedesRevisionId == inventory.Files[0].RevisionId,
            "The explicit migration tombstone did not retain its superseded revision.");
        Throws<ArgumentException>(() => new CortexLegacyMigrationRecordV1(
            1, CortexSyncContractIds.LegacyMigrationRecord, CortexSyncContractIds.Canonicalization,
            "legacy_record_1", "legacy_revision_3", "project.default", "state", "source_root_1",
            manifestHash, inventory.InventoryHash, 0, ".cortex/STATE.md", 0,
            inventory.Files[0].SourceSha256, null, true, 0, 0, 0, string.Empty, 0, emptyHash,
            CortexSyncContractIds.ContentTreatment, false));
    }

    private static CortexLegacyMigrationCursorV1 MigrationCursor(
        CortexLegacyMigrationInventoryV1 inventory,
        long pageNumber,
        int fileIndex,
        int chunkIndex,
        long sequence,
        string previousBundleHash,
        string priorEventHash,
        bool completed) =>
        CortexSyncContractPolicy.Seal(new CortexLegacyMigrationCursorV1(
            1, CortexSyncContractIds.LegacyMigrationCursor, CortexSyncContractIds.Canonicalization,
            inventory.MigrationId, inventory.SourceManifestHash, inventory.InventoryHash, inventory.Files.Count,
            inventory.StoreId, inventory.StoreEpoch, inventory.ProjectId, inventory.SourceRootId,
            pageNumber, fileIndex, chunkIndex, sequence, previousBundleHash, priorEventHash, completed,
            CortexSyncContractIds.ContentTreatment, false, [], CortexSyncContractIds.EmptySha256));

    private static CortexOfflineEventV1 MigrationEvent(
        CortexLegacyMigrationInventoryV1 inventory,
        int fileIndex,
        byte[] sourceBytes,
        int zeroBasedChunkIndex,
        long sequence,
        string previousEventHash)
    {
        var file = inventory.Files[fileIndex];
        var offset = zeroBasedChunkIndex * CortexLegacyFileCopyPayloadV1.MaximumChunkBytes;
        var count = Math.Min(CortexLegacyFileCopyPayloadV1.MaximumChunkBytes, sourceBytes.Length - offset);
        var chunk = sourceBytes.AsSpan(offset, count).ToArray();
        var payloadValue = new CortexLegacyMigrationRecordV1(
            1, CortexSyncContractIds.LegacyMigrationRecord, CortexSyncContractIds.Canonicalization,
            file.RecordId, file.RevisionId, "project.default", file.Role, inventory.SourceRootId,
            inventory.SourceManifestHash, inventory.InventoryHash, fileIndex, file.SourceRelativePath,
            file.SourceBytes, file.SourceSha256, null, false, zeroBasedChunkIndex + 1, file.ChunkCount,
            offset, Base64Url(chunk), count, CortexCanonicalJson.Sha256(chunk),
            CortexSyncContractIds.ContentTreatment, false);
        var payload = CortexCanonicalJson.Serialize(payloadValue);
        return CortexSyncContractPolicy.Seal(new CortexOfflineEventV1(
            1, CortexSyncContractIds.OfflineEvent, CortexSyncContractIds.Canonicalization,
            inventory.StoreId, inventory.StoreEpoch, inventory.ProjectId, sequence,
            $"legacy_event_{sequence}", previousEventHash, inventory.CapturedAt, "agent_1",
            "legacy.copy", "tenant_1", "project.default", null,
            CortexSyncContractIds.LegacyMigrationRecord, Base64Url(payload), payload.Length,
            CortexCanonicalJson.Sha256(payload), new CortexSyncSourceProvenanceV1(
                "legacy_file_copy", inventory.SourceRootId, file.SourceRelativePath, file.SourceBytes,
                file.SourceSha256, inventory.CapturedAt),
            CortexSyncContractIds.ContentTreatment, false, [], CortexSyncContractIds.EmptySha256));
    }

    private static CortexOfflineBundleV1 MigrationBundle(
        string bundleId,
        string previousBundleHash,
        IReadOnlyList<CortexOfflineEventV1> events) =>
        CortexSyncContractPolicy.Seal(new CortexOfflineBundleV1(
            1, CortexSyncContractIds.OfflineBundle, CortexSyncContractIds.Canonicalization,
            bundleId, "store_1", "epoch_1", "project_1", null, previousBundleHash,
            events[0].PreviousEventHash, events[0].Sequence, events[^1].Sequence,
            DateTimeOffset.Parse("2026-07-14T20:00:01Z"), events, [], CortexSyncContractIds.EmptySha256));

    private static CortexLegacyMigrationPageV1 MigrationPage(
        string pageId,
        CortexLegacyMigrationInventoryV1 inventory,
        CortexLegacyMigrationCursorV1 current,
        CortexOfflineBundleV1 bundle,
        CortexLegacyMigrationCursorV1 next) =>
        CortexSyncContractPolicy.Seal(new CortexLegacyMigrationPageV1(
            1, CortexSyncContractIds.LegacyMigrationPage, CortexSyncContractIds.Canonicalization,
            pageId, inventory, current, bundle, next, true, false,
            CortexSyncContractIds.ContentTreatment, false, [], CortexSyncContractIds.EmptySha256));

    private static void ExpectMigrationPageRejected(
        string pageId,
        CortexLegacyMigrationInventoryV1 inventory,
        CortexLegacyMigrationCursorV1 current,
        CortexOfflineBundleV1 bundle,
        CortexLegacyMigrationCursorV1 next) =>
        Throws<ArgumentException>(() => MigrationPage(pageId, inventory, current, bundle, next));

    private static CortexOfflineEventV1 Event(byte[] payload, long sequence, string previousHash, string? payloadHash = null) => new(
        1,
        CortexSyncContractIds.OfflineEvent,
        CortexSyncContractIds.Canonicalization,
        "store_1",
        "epoch_1",
        "project_1",
        sequence,
        $"event_{sequence}",
        previousHash,
        DateTimeOffset.Parse("2026-07-14T20:00:00Z"),
        "agent_1",
        $"operation_{sequence}",
        "tenant_1",
        "project.default",
        null,
        "pegasus.cortex.test-payload/1.0",
        Base64Url(payload),
        payload.Length,
        payloadHash ?? CortexCanonicalJson.Sha256(payload),
        new CortexSyncSourceProvenanceV1(
            "client_operation", null, null, payload.Length, CortexCanonicalJson.Sha256(payload),
            DateTimeOffset.Parse("2026-07-14T20:00:00Z")),
        CortexSyncContractIds.ContentTreatment,
        false,
        [],
        CortexSyncContractIds.EmptySha256);

    private static CortexSyncCapabilitiesV1 Capabilities()
    {
        var value = new CortexSyncCapabilitiesV1(
            1,
            CortexSyncContractIds.SyncCapabilities,
            CortexSyncContractIds.Canonicalization,
            "service_1",
            CoreFamily,
            ["pegasus.cortex.test-payload/1.0"],
            RequiredCapabilities(),
            [CortexSyncCapabilityIds.EventHashChain],
            [],
            [],
            ["aes_256_gcm"],
            ["pegasus.cortex.receipt-proof-test/1.0"],
            Limits(),
            [],
            CortexSyncContractIds.EmptySha256);
        return CortexSyncContractPolicy.Seal(value);
    }

    private static CortexSyncCompatibilityRequestV1 Request(
        IReadOnlyList<string>? supportedCapabilities = null,
        IReadOnlyList<string>? requiredServerCapabilities = null,
        IReadOnlyList<string>? supportedExtensions = null,
        IReadOnlyList<string>? requiredServerExtensions = null,
        IReadOnlyList<CortexSyncExtensionV1>? extensions = null) => new(
            1,
            CortexSyncContractIds.CompatibilityRequest,
            CortexSyncContractIds.Canonicalization,
            "request_1",
            "client_1",
            CoreFamily,
            CoreFamily,
            ["pegasus.cortex.test-payload/1.0"],
            supportedCapabilities ?? RequiredCapabilities(),
            requiredServerCapabilities ?? RequiredCapabilities(),
            supportedExtensions ?? [],
            requiredServerExtensions ?? [],
            ["aes_256_gcm"],
            ["pegasus.cortex.receipt-proof-test/1.0"],
            Limits(),
            extensions ?? []);

    private static string[] RequiredCapabilities() =>
    [
        CortexSyncCapabilityIds.DurableReceipts,
        CortexSyncCapabilityIds.EventHashChain,
        CortexSyncCapabilityIds.ServerReadBackReconciliation
    ];

    private static CortexSyncLimitsV1 Limits() => new(
        CortexSyncContractLimits.MaximumEventPayloadBytes,
        CortexSyncContractLimits.MaximumEventsPerBundle,
        CortexSyncContractLimits.MaximumCanonicalBundleBytes,
        CortexSyncContractLimits.MaximumExtensions,
        CortexSyncContractLimits.MaximumExtensionPayloadBytes,
        CortexSyncContractLimits.MaximumReceiptOutcomes,
        CortexSyncContractLimits.MaximumMigrationFiles,
        CortexSyncContractLimits.MaximumJsonDepth);

    private static CortexSyncReceiptV1 Receipt(CortexOfflineBundleV1 bundle, byte[] proof, string receiptHash)
    {
        var receivedAt = DateTimeOffset.Parse("2026-07-14T20:01:00Z");
        return new CortexSyncReceiptV1(
            1,
            CortexSyncContractIds.SyncReceipt,
            CortexSyncContractIds.Canonicalization,
            "receipt_1",
            "server_1",
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId,
            bundle.BundleId,
            bundle.BundleHash,
            receivedAt,
            new CortexSyncReceiptAuthenticationV1(
                "pegasus.cortex.receipt-proof-test/1.0", "key_1", "test_signature", receiptHash,
                Base64Url(proof), proof.Length, CortexCanonicalJson.Sha256(proof)),
            new CortexSyncReceiptDurabilityV1("commit_1", "server_rev_2", receivedAt.AddSeconds(1)),
            bundle.Events.Select(item => new CortexSyncReceiptOutcomeV1(
                item.Sequence, item.EventId, item.EventHash, CortexSyncDispositionIds.Applied, true,
                "entity_1", "server_rev_2", null)).ToArray(),
            new CortexSyncReceiptReconciliationV1(
                "reconciled", bundle.Events.Count, bundle.Events.Count, receivedAt.AddSeconds(2), bundle.BundleHash),
            [],
            receiptHash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PegasusCortex.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
