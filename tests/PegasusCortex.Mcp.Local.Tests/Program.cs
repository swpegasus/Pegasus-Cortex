if(args is ["--metadata-read-bounds-only"])
{
    await MetadataReadBoundsTests.RunAsync();
    Console.WriteLine("PASS local metadata opened-handle bounds.");
    return 0;
}
if (args is ["--owned-profile-protocol-child", var ownedProfile, var ownedStateRoot])
    return await OwnedProfileProtocolChild.RunAsync(ownedProfile, ownedStateRoot);
if (args is ["--direct-stdio-retirement-only"])
{
    await DirectStdioRetirementTests.RunAsync();
    return 0;
}
if (args is ["--payload-tiering-only"])
{
    await CortexPayloadTieringTests.RunAsync();
    return 0;
}
if (args is ["--payload-protection-only"])
{
    await CortexPayloadProtectionTests.RunAsync();
    return 0;
}
if (args is ["--payload-rotation-only"])
{
    await CortexPayloadRotationTests.RunAsync();
    return 0;
}
if (args is ["--payload-budget-only"])
{
    await CortexPayloadBudgetTests.RunAsync();
    return 0;
}
if (args is ["--payload-history-rehydration-only"])
{
    await CortexPayloadHistoryRehydrationTests.RunAsync();
    return 0;
}
if (args is ["--payload-lifecycle-settings-only"])
{
    await CortexPayloadLifecycleSettingsTests.RunAsync();
    return 0;
}
if (args is ["--shared-mcp-embedding-only"])
{
    await SharedMcpEmbeddingTests.RunAsync();
    return 0;
}
if (args is ["--shared-mcp-configured-product-only", var sharedProductConfiguration])
{
    await SharedMcpConfiguredProductTests.RunAsync(sharedProductConfiguration);
    return 0;
}
if (args.Length > 0 && args[0] == "--registered-fresh-start-only")
    return args is [_, var fixtureRoot]
        ? await RegisteredFreshStartTests.RunAsync(fixtureRoot)
        : 2;
if (args.Length > 0 && args[0] == "--registered-user-state-permissions-only")
    return args is [_, var permissionFixtureRoot]
        ? await RegisteredUserStatePermissionTests.RunAsync(permissionFixtureRoot)
        : 2;

if (args.Length > 0 && args[0] == "native-interop")
    return await NativeInteropHarness.RunAsync(args[1..]);

if (args is ["lifecycle-relay-child", var lifecyclePipe, var lifecyclePeer])
    return await LifecycleProcessTests.RunAdapterChildAsync(lifecyclePipe, lifecyclePeer);
if (args is ["launcher-stdio-echo-child"])
    return await LauncherConsoleTests.RunEchoChildAsync();
if (args is ["--launcher-console-only", var consoleFixtureRoot])
    return await LauncherConsoleTests.RunAsync(consoleFixtureRoot);
if (args is ["--lifecycle-process-only", var lifecycleLauncher, var lifecycleRoot])
{
    await LifecycleProcessTests.RunAsync(lifecycleLauncher, lifecycleRoot);
    return 0;
}
if (args is ["fixture-launcher-child", var fixtureDataRoot, .. var forwarded])
    return await LauncherSelectorFixtureTests.RunLauncherChildAsync(fixtureDataRoot, forwarded);
if (args is ["--lifecycle-fixture-process-only", var fixtureProcessRoot])
{
    await LifecycleProcessTests.RunFixtureAsync(fixtureProcessRoot);
    return 0;
}
if (args is ["--launcher-selection-only", var selectedLauncher, var selectorRoot])
    return await LauncherSelectorFixtureTests.RunAsync(selectedLauncher, selectorRoot);

var allChecks = new (string Name, Func<Task> Run)[]
{
    ("lifecycle semantic admission drains without replay and checks selected instance", LifecycleAdmissionTests.SemanticDrainAndSelectionAsync),
    ("lifecycle closed resident hello and ping preserve authentication without ready claims", LifecycleAdmissionTests.ClosedResidentIdentityAsync),
    ("private read cache arguments are explicit and legacy grammar is isolated", PrivateReadCacheArgumentsTests.StrictGrammarAndLegacyIsolationAsync),
    ("private read cache fresh preview rejects foreign scope and omits cursors", PrivateReadCacheCommandTests.FreshPreviewRejectsForeignScopeAndOmitsCursorsAsync),
    ("private read cache commands remain passive fresh bounded and isolated", PrivateReadCacheCommandTests.CommandsArePassiveFreshAndIsolatedAsync),
    ("owner-private read cache store preserves binding leases and passive snapshots", Pegasus.Cortex.Mcp.Local.Tests.PrivateReadCacheStoreTests.RunAllAsync),
    ("resident client keys enforce identity rotation expiry not namespace permissions", ResidentTransportTests.RegisteredKeysAndDefaultsAsync),
    ("resident native and MCP share SQLite and isolate sessions", ResidentTransportTests.NativeAndMcpShareRuntimeAsync),
    ("resident framing EOF and cancellation are bounded", ResidentTransportTests.FramingAndCancellationAsync),
    ("resident direct adapter checks exact selected role and version", ResidentTransportTests.DirectAdapterSelectionAsync),
    ("resident ONE native2 pipe overlaps routes frames and retains uncertain SQLite commit", ResidentTransportTests.SinglePipeMultiplexingAsync),
    ("file assets preserve pins metadata and namespaces", AssetV2Tests.ExactFilesAndMetadataAsync),
    ("file assets bound search and verified ranges", AssetV2Tests.BoundedSearchAndByteRangesAsync),
    ("frozen icons and raw MCP resources share one surface", AssetV2Tests.ResourceProtocolAndFrozenIconsAsync),
    ("frozen icon metadata and provenance are exact", IconPackV2Tests.FrozenPackMetadataAsync),
    ("icon pack paths active content and tampering fail closed", IconPackV2Tests.RejectUnsafeAndTamperedPackAsync),
    ("icon pack metadata and cancellation are bounded", IconPackV2Tests.BoundedMetadataAndCancellationAsync),
    ("registry and read/write tool allowlists are exact", LocalCompositionTests.RegistryAndToolAllowlistsAsync),
    ("user-state access accepts safe Windows descriptors without reseal", UserStateAccessPolicyTests.DescriptorAdmissionAsync),
    ("user-state access preserves owned filesystem permissions and data", UserStateAccessPolicyTests.OwnedFilesystemAdmissionAsync),
    ("user-state protection rejects foreign owners and access before mutation", ProductUserStateOwnershipTests.OwnerAdmissionPrecedesProtectionAsync),
    ("general MCP tools require and echo explicit namespaces", GeneralOperationalTests.ExplicitNamespaceToolRoundTripAsync),
    ("optional personal profile pipe recovers without interrupting SQLite core", PersonalProfileIntegrationTests.OptionalProfilePipeAndSqliteAsync),
    ("empty personal HOT removes previews without changing task-bound frames", PersonalProfileOfflineTests.EmptyPersonalHotPreservesBoundStateAsync),
    ("one MCP host exposes Cortex plus one closed PAO tool", UnifiedMcpSurfaceTests.CortexAndPaoShareOneClosedSurfaceAsync),
    ("offline catalog survives restart and registration replay", OfflineOrchestrationTests.CatalogRestartAndIdempotentRegistrationAsync),
    ("offline CLI parsing is explicit and content-file only", OfflineOrchestrationTests.DeterministicCliParsingAsync),
    ("explicit legacy migration is scoped preserved and idempotent", OfflineOrchestrationTests.ExplicitLegacyMigrationIsScopedAndIdempotentAsync),
    ("legacy migration conflicts and unsafe roots fail closed", OfflineOrchestrationTests.LegacyMigrationConflictsFailClosedAsync),
    ("legacy migration rejects full catalog before scope initialization", OfflineOrchestrationTests.LegacyMigrationCapacityPreflightCreatesNoScopeAsync),
    ("ambient warmup does not enumerate task scope details", OfflineOrchestrationTests.WarmupDoesNotEnumerateTaskScopesAsync),
    ("offline capture search read and ingest preserve exact content", OfflineOrchestrationTests.OfflineCaptureSearchReadAndIngestAsync),
    ("active-store capacity thresholds classify exact boundaries", OfflineOrchestrationTests.CapacityThresholdClassificationAsync),
    ("product backup is timestamped canonical create-only and leaves uninitialized state absent", ProductBackupCommandTests.TimestampedCentralBackupIsCanonicalAndCreateOnlyAsync),
    ("product knowledge conversion is backup-first and leaves project state exact", ProductKnowledgeSpaceMigrationTests.BackupFirstConversionPreservesLegacyAndProjectStateAsync),
    ("starter corpus seeds fresh stores atomically and preserves existing stores", ProductStarterCorpusTests.FreshStoreIsSeededAtomicallyAndExistingStoreIsPreservedAsync),
    ("single behavior-pack product route is atomic scoped and bounded", ProductStarterCorpusTests.SingleBehaviorPackRouteIsAtomicScopedAndBoundedAsync),
    ("shared starter corpus retrieval is relevant bounded and deterministic", ProductStarterCorpusTests.SharedCorpusRetrievalIsRelevantBoundedAndDeterministicAsync),
    ("Cerebral guides are bounded versioned and contract accurate", ProductStarterCorpusTests.CerebralGuidesAreBoundedAndContractAccurateAsync),
    ("explicit project provisioning is strict and create-only", LocalProvisioningCommandTests.ExplicitCreateOnlyProvisioningAsync),
    ("retired direct STDIO entry refuses without initializing state", DirectStdioRetirementTests.RunAsync),
    ("owned profile protocol child retains and reads typed local assertions", LocalProcessTests.DirectStdioRoundTripAsync),
    ("handoff and acceptance receipts retain exact passive links", LocalProcessTests.ReceiptLinkageAsync),
    ("cold brief locators recover exact task and continuation bytes", LocalProcessTests.ColdBriefRecoveryAsync),
    ("evidence locators resolve exactly and ignore omitted-task evidence", LocalProcessTests.EvidenceProjectionBoundaryAsync),
    ("valid retention survives bounded brief projection limits", LocalProcessTests.ProjectionLimitsDoNotVetoRetentionAsync),
    ("representable task heads recover across one exact skipped source", LocalProcessTests.ProjectionGapRecoveryAsync),
    ("prohibited task and work content fails with bounded tool errors", LocalProcessTests.ProhibitedContentFailsBoundedlyAsync),
    ("private proposals remain pending exact and non-searchable", LocalProposalProcessTests.PrivatePendingProposalRetentionAsync),
    ("proposal 1.1 metadata is identity-bound pending and exact", LocalProposalProcessTests.ProposalMetadataV11Async),
    ("explicit recovery import preserves exact source bytes and fails closed", LocalRecoveryImportTests.ExplicitCopyFirstImportAsync),
    ("explicit recovery export retains exact passive history offline", LocalRecoveryExportTests.ExplicitExactExportAsync),
    ("transfer export binds provenance to exact project correlation", LocalProjectTransferExportTests.CorrelatedStoreProvenanceAndFailuresAsync),
    ("tenant rebind copies authoritative transfer bytes exactly", LocalProjectTransferTenantRebindTests.ExactAuthoritativeCopyAndFailuresAsync),
    ("provider-neutral transfer import is create-only and scope-bound", LocalProjectTransferImportTests.CreateOnlyRoundTripAndFailClosedAsync),
    ("explicit recovery restore rebuilds current state from exact passive history", LocalRecoveryRestoreTests.ExplicitCurrentStateRestoreAsync),
    ("exact local backup restores only into a new protected profile", LocalStoreBackupTests.ExactBackupAndCreateOnlyRestoreAsync),
    ("verified CURRENT extends an independent retained recovery chain", LocalCurrentMaterializationTests.VerifiedCurrentExtendsIndependentRetainedChainAsync),
    ("expired legacy CURRENT chain advances without relabeling", LocalCurrentMaterializationTests.ExpiredLegacyTaskSetContinuesWithoutRelabelingAsync),
    ("continuity refresh is explicit, idempotent, and one-shot", LocalContinuityRefreshTests.ExplicitOneShotRefreshAndHorizonAsync),
    ("continuity watch recovers missed hints and cancels boundedly", LocalContinuityRefreshTests.WatchHintsPollCancellationAndInvalidAuthorityAsync),
    ("pipe bridge recovers retained state across restart", LocalProcessTests.PipeBridgeRestartAsync),
    ("service sessions do not share MCP lifecycle state", LocalProcessTests.ServiceSessionIsolationAsync),
    ("explicit modes fail closed without silent switching", LocalProcessTests.ExplicitModeFailureAsync),
    ("local output has no remote or integrated dependency", LocalCompositionTests.DependencyClosureAsync)
};

var checks = args is ["--lifecycle-only"]
    ? allChecks.Where(check => check.Name.StartsWith("lifecycle", StringComparison.Ordinal) ||
        check.Run == PersonalProfileIntegrationTests.OptionalProfilePipeAndSqliteAsync ||
        check.Run == ResidentTransportTests.FramingAndCancellationAsync).ToArray()
    : args is ["--profile-protocol-only"]
    ? allChecks.Where(check =>
        check.Run == LocalProcessTests.DirectStdioRoundTripAsync ||
        check.Run == LocalProcessTests.PipeBridgeRestartAsync ||
        check.Run == LocalProcessTests.ServiceSessionIsolationAsync ||
        check.Run == LocalProcessTests.ExplicitModeFailureAsync).ToArray()
    : args is ["--user-state-ownership-only"]
    ? allChecks.Where(check =>
        check.Run == ProductUserStateOwnershipTests.OwnerAdmissionPrecedesProtectionAsync).ToArray()
    : args is ["--user-state-access-only"]
    ? allChecks.Where(check =>
        check.Run == UserStateAccessPolicyTests.DescriptorAdmissionAsync ||
        check.Run == UserStateAccessPolicyTests.OwnedFilesystemAdmissionAsync).ToArray()
    : args is ["--private-read-cache-only"]
    ? allChecks.Where(check => check.Name.StartsWith("private read cache", StringComparison.Ordinal) ||
        check.Run == Pegasus.Cortex.Mcp.Local.Tests.PrivateReadCacheStoreTests.RunAllAsync).ToArray()
    : args is ["--owner-admin-only"]
    ? allChecks.Where(check=>check.Run==UnifiedMcpSurfaceTests.CortexAndPaoShareOneClosedSurfaceAsync||check.Run==ResidentTransportTests.NativeAndMcpShareRuntimeAsync).ToArray()
    : args is ["--resident-only"]
    ? allChecks.Where(check => check.Name.StartsWith("resident", StringComparison.Ordinal) ||
        check.Run == UnifiedMcpSurfaceTests.CortexAndPaoShareOneClosedSurfaceAsync ||
        check.Run == GeneralOperationalTests.ExplicitNamespaceToolRoundTripAsync).ToArray()
    : args is ["--assets-only"]
    ? allChecks.Where(check => check.Name.StartsWith("file assets", StringComparison.Ordinal) ||
        check.Name.StartsWith("frozen icon", StringComparison.Ordinal) ||
        check.Name.StartsWith("icon pack", StringComparison.Ordinal) ||
        check.Run == UnifiedMcpSurfaceTests.CortexAndPaoShareOneClosedSurfaceAsync ||
        check.Run == GeneralOperationalTests.ExplicitNamespaceToolRoundTripAsync).ToArray()
    : args is ["--personal-profile-only"]
    ? allChecks.Where(check =>
        check.Run == PersonalProfileIntegrationTests.OptionalProfilePipeAndSqliteAsync ||
        check.Run == PersonalProfileOfflineTests.EmptyPersonalHotPreservesBoundStateAsync ||
        check.Run == GeneralOperationalTests.ExplicitNamespaceToolRoundTripAsync ||
        check.Run == UnifiedMcpSurfaceTests.CortexAndPaoShareOneClosedSurfaceAsync ||
        check.Run == OfflineOrchestrationTests.OfflineCaptureSearchReadAndIngestAsync).ToArray()
    : args is ["--offline-only"]
    ? allChecks.Where(check =>
        check.Run == OfflineOrchestrationTests.CatalogRestartAndIdempotentRegistrationAsync ||
        check.Run == OfflineOrchestrationTests.DeterministicCliParsingAsync ||
        check.Run == OfflineOrchestrationTests.ExplicitLegacyMigrationIsScopedAndIdempotentAsync ||
        check.Run == OfflineOrchestrationTests.LegacyMigrationConflictsFailClosedAsync ||
        check.Run == OfflineOrchestrationTests.LegacyMigrationCapacityPreflightCreatesNoScopeAsync ||
        check.Run == OfflineOrchestrationTests.WarmupDoesNotEnumerateTaskScopesAsync ||
        check.Run == OfflineOrchestrationTests.OfflineCaptureSearchReadAndIngestAsync ||
        check.Run == OfflineOrchestrationTests.CapacityThresholdClassificationAsync).ToArray()
    : args is ["--data-location-only"]
        ? allChecks.Where(check =>
            check.Run == ProductUserStateOwnershipTests.OwnerAdmissionPrecedesProtectionAsync ||
            check.Run == LocalCompositionTests.RegistryAndToolAllowlistsAsync ||
            check.Run == GeneralOperationalTests.ExplicitNamespaceToolRoundTripAsync ||
            check.Run == ProductBackupCommandTests.TimestampedCentralBackupIsCanonicalAndCreateOnlyAsync ||
            check.Run == ProductStarterCorpusTests.FreshStoreIsSeededAtomicallyAndExistingStoreIsPreservedAsync ||
            check.Run == OfflineOrchestrationTests.CatalogRestartAndIdempotentRegistrationAsync).ToArray()
    : args is ["--cerebral-guides-only"]
        ? allChecks.Where(check =>
            check.Run == ProductStarterCorpusTests.SingleBehaviorPackRouteIsAtomicScopedAndBoundedAsync ||
            check.Run == ProductStarterCorpusTests.SharedCorpusRetrievalIsRelevantBoundedAndDeterministicAsync ||
            check.Run == ProductStarterCorpusTests.CerebralGuidesAreBoundedAndContractAccurateAsync).ToArray()
    : args is ["--starter-corpus-only"]
        ? allChecks.Where(check =>
            check.Run == ProductStarterCorpusTests.FreshStoreIsSeededAtomicallyAndExistingStoreIsPreservedAsync ||
            check.Run == ProductStarterCorpusTests.SingleBehaviorPackRouteIsAtomicScopedAndBoundedAsync ||
            check.Run == ProductStarterCorpusTests.SharedCorpusRetrievalIsRelevantBoundedAndDeterministicAsync ||
            check.Run == ProductStarterCorpusTests.CerebralGuidesAreBoundedAndContractAccurateAsync).ToArray()
    : allChecks;

var passed = 0;
var authorityFallbackPassed = false;
var projectBriefAuthorityPassed = false;
var offlineRecoveryChecksPassed = 0;
foreach (var check in checks)
{
    var originalOutput = Console.Out;
    using var nestedOutput = new StringWriter();
    try
    {
        // Helpers may report a bounded platform skip. Capture it so this
        // harness emits only the sealed evidence grammar below.
        Console.SetOut(nestedOutput);
        await check.Run().ConfigureAwait(false);
        Console.SetOut(originalOutput);
        EmitBoundedNestedOutput(nestedOutput.ToString());
        Console.WriteLine($"PASS {check.Name}");
        passed++;

        // The cold-brief check directly exercises the required authority hash,
        // missing/mismatched hash fallback, and omission of stale task claims.
        if (check.Run == LocalProcessTests.ColdBriefRecoveryAsync)
        {
            authorityFallbackPassed = true;
            projectBriefAuthorityPassed = true;
        }

        // Track every bounded operator path needed for complete offline recovery.
        if (check.Run == LocalRecoveryImportTests.ExplicitCopyFirstImportAsync ||
            check.Run == LocalRecoveryExportTests.ExplicitExactExportAsync ||
            check.Run == LocalProjectTransferExportTests.CorrelatedStoreProvenanceAndFailuresAsync ||
            check.Run == LocalProjectTransferTenantRebindTests.ExactAuthoritativeCopyAndFailuresAsync ||
            check.Run == LocalProjectTransferImportTests.CreateOnlyRoundTripAndFailClosedAsync ||
            check.Run == LocalRecoveryRestoreTests.ExplicitCurrentStateRestoreAsync ||
            check.Run == LocalStoreBackupTests.ExactBackupAndCreateOnlyRestoreAsync ||
            check.Run == LocalCurrentMaterializationTests.VerifiedCurrentExtendsIndependentRetainedChainAsync ||
            check.Run == LocalCurrentMaterializationTests.ExpiredLegacyTaskSetContinuesWithoutRelabelingAsync ||
            check.Run == LocalContinuityRefreshTests.ExplicitOneShotRefreshAndHorizonAsync ||
            check.Run == LocalContinuityRefreshTests.WatchHintsPollCancellationAndInvalidAuthorityAsync)
        {
            offlineRecoveryChecksPassed++;
        }
    }
    catch (Exception exception)
    {
        Console.SetOut(originalOutput);
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 240)
            message = message[..240];
        Console.Error.WriteLine(
            $"FAIL {check.Name} :: {exception.GetType().Name}: {message}");
        return 1;
    }
}

if (args is ["--user-state-ownership-only"])
{
    if (checks.Length != 1 || passed != 1) return 1;
    Console.WriteLine("METRIC user-state-ownership passed=1 total=1 scratch-only=true");
    return 0;
}

if (args is ["--private-read-cache-only"])
{
    if (checks.Length != 4 || passed != checks.Length) return 1;
    Console.WriteLine("METRIC private-read-cache passed=4 total=4 fixture=true");
    return 0;
}

if (args is ["--owner-admin-only"])
{
    if (checks.Length != 2 || passed != checks.Length) return 1;
    Console.WriteLine("METRIC owner-admin-runtime-boundary passed=2 total=2");
    return 0;
}

if (args is ["--resident-only"])
{
    if (checks.Length != 7 || passed != checks.Length) return 1;
    Console.WriteLine($"METRIC resident-transports passed={checks.Length} total={checks.Length}");
    return 0;
}

if (args is ["--personal-profile-only"])
{
    if (checks.Length != 5 || passed != checks.Length)
        return 1;
    Console.WriteLine("METRIC optional-personal-profile passed=5 total=5");
    return 0;
}

if (args is ["--assets-only"])
{
    if (checks.Length != 8 || passed != checks.Length) return 1;
    Console.WriteLine("METRIC file-assets passed=8 total=8 " + AssetV2Tests.BoundedFixtureMetric);
    return 0;
}

if (args is ["--offline-only"])
{
    Console.WriteLine("METRIC task-scoped-offline passed=true");
    return 0;
}

if (args is ["--data-location-only"])
{
    if (checks.Length != 6 || passed != checks.Length)
    {
        Console.Error.WriteLine("FAIL data-location-evidence-output");
        return 1;
    }
    Console.WriteLine("METRIC data-location passed=true");
    return 0;
}

if (args is ["--starter-corpus-only"])
{
    if (checks.Length != 4 || passed != checks.Length)
        return 1;
    Console.WriteLine("METRIC starter-corpus passed=4 total=4");
    return 0;
}

if (args is ["--lifecycle-only"])
{
    if (checks.Length != 4 || passed != checks.Length)
        return 1;
    Console.WriteLine("METRIC lifecycle passed=4 total=4 fixture-only=true");
    return 0;
}

if (args is ["--profile-protocol-only"])
{
    if (checks.Length != 4 || passed != checks.Length) return 1;
    Console.WriteLine("METRIC owned-profile-protocol passed=4 total=4 fixture-only=true");
    return 0;
}

if (args is ["--user-state-access-only"])
{
    if (checks.Length != 2 || passed != checks.Length)
        return 1;
    Console.WriteLine("METRIC user-state-access passed=2 total=2 evidence=descriptor-and-owned-filesystem-fixture");
    return 0;
}

if (args is ["--cerebral-guides-only"])
{
    if (checks.Length != 3 || passed != checks.Length)
        return 1;
    Console.WriteLine("METRIC cerebral-guides passed=3 total=3");
    return 0;
}

if (passed != checks.Length ||
    !authorityFallbackPassed ||
    !projectBriefAuthorityPassed ||
    offlineRecoveryChecksPassed != 11)
{
    Console.Error.WriteLine("FAIL evidence-output");
    return 1;
}

Console.WriteLine("METRIC authority-fallback passed=true");
Console.WriteLine("METRIC offline-recovery passed=true");
Console.WriteLine("METRIC project-brief-authority passed=true");
return 0;

static void EmitBoundedNestedOutput(string output)
{
    foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        switch (line)
        {
            case "SKIP symbolic-link fixture: OS link creation unavailable.":
                Console.WriteLine("SKIP private-read-cache reparse probe :: OS symbolic-link creation is unavailable.");
                break;
            case "SKIP assets reparse probe: symbolic-link creation is unavailable.":
                Console.WriteLine("SKIP assets reparse probe :: Symbolic-link creation is unavailable.");
                break;
            case "SKIP recovery reparse probe: symbolic-link creation is unavailable.":
                Console.WriteLine(
                    "SKIP recovery reparse probe :: Symbolic-link creation is unavailable.");
                break;
            case "SKIP export reparse probe: symbolic-link creation is unavailable.":
                Console.WriteLine(
                    "SKIP export reparse probe :: Symbolic-link creation is unavailable.");
                break;
            default:
                throw new InvalidOperationException("A helper emitted unsupported evidence output.");
        }
    }
}
