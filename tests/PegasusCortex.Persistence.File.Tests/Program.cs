using Pegasus.Cortex.Persistence.File.Tests;
using Pegasus.Cortex.Persistence.File.Tests.V2;

if (args is ["--hold-brief-lock", var lockPath, var readyPath])
    return await FileProjectBriefTests.HoldExclusiveLockAsync(lockPath, readyPath)
        .ConfigureAwait(false);

var visualChecks = new (string Name, Func<Task> Run)[]
{
    ("visual library preserves exact replay licensing and bytes", VisualLibraryV2Tests.ExactReplayAndLicensingAsync),
    ("visual library rejects pins paths licenses and foreign heads before writes", VisualLibraryV2Tests.AdmissionFailsBeforeWritesAsync),
    ("visual library bounds manifest ambiguity and cancellation", VisualLibraryV2Tests.BoundedManifestAndCancellationAsync)
};
var confinementChecks = new (string Name, Func<Task> Run)[]
{
    ("file V2 holds directory identities across manifest HOT and exact-read swaps", CortexFileV2ConfinementTests.ParentSwapsAsync),
    ("file V2 publication cleanup replay and cancellation retain original ownership", CortexFileV2ConfinementTests.PublicationAndCancellationAsync)
};
var tests = new (string Name, Func<Task> Run)[]
{
    ("file and SQLite providers retain byte-identical canonical briefs", FileProjectBriefTests.CrossProviderCanonicalBytesAsync),
    ("file provider fails closed on corruption and stale snapshots", FileProjectBriefTests.CorruptionAndFreshnessAsync),
    ("file provider rejects stale chains and returns snapshots without fabricated deltas", FileProjectBriefTests.StaleChainAndSnapshotFallbackAsync),
    ("file provider contains paths rejects links and preserves source files", FileProjectBriefTests.PathLinkAndSourcePreservationAsync),
    ("file provider serializes divergent successors and atomically preserves prior bytes on failure", FileProjectBriefTests.ConcurrentCommitAndFailurePreservationAsync),
    ("file provider honors a cross-process lock and recovers it after process termination", FileProjectBriefTests.CrossProcessCrashReleasedLockAsync),
    ("file Cortex V2 round-trips exact JSON text and atomically refreshes bounded HOT context", FileCortexV2StoreTests.ExactContentAndProjectionAsync),
    ("file Cortex V2 makes atomic finish knowledge searchable and surfaces receipt context", FileCortexV2StoreTests.LifecycleSearchAndAtomicFinishAsync),
    ("file Cortex V2 reference behavior pack is portable and excludes task and machine history", FileCortexV2StoreTests.PortableBehaviorPackAsync),
    ("offline file Cortex preserves exact candidates and atomically retains body-free receipts", CortexOfflineFileStoreTests.ExactCandidateAndReceiptLifecycleAsync),
    ("offline file Cortex enforces bounds concurrency and bounded HOT projection", CortexOfflineFileStoreTests.BoundsConcurrencyAndHotProjectionAsync),
    ("offline file Cortex reports HOT truncation across central pending and prior projection sources", CortexOfflineFileStoreTests.HotMergeTruncationReportsHasMoreAsync),
    ("offline file Cortex directly adapts old manifests and preserves unknown future frames", CortexOfflineFileStoreTests.CompatibilityAndUnknownPreservationAsync),
    ("offline file Cortex isolates task scopes across restart receipt and HOT lifecycles", CortexOfflineFileStoreTests.TaskScopesIsolateRestartReceiptAndHotAsync),
    ("offline file Cortex rejects legacy ambiguity and task binding mismatch without writes", CortexOfflineFileStoreTests.LegacyAmbiguityAndBindingMismatchFailClosedAsync),
    ("offline file Cortex fails closed on task identity path and manifest admission", CortexOfflineFileStoreTests.ScopeIdentityPathAndManifestAdmissionFailClosedAsync)
};
tests = args is ["--directory-confinement-only"] ? confinementChecks
    : args is ["--visual-library"] ? visualChecks : tests.Concat(confinementChecks).Concat(visualChecks).ToArray();

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} passed");
return failures.Count == 0 ? 0 : 1;
