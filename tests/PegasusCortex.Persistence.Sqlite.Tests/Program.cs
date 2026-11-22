using Pegasus.Cortex.Persistence.Sqlite.Tests;

if (args is ["--hold-uncommitted", var databasePath, var readyPath])
    return await SqliteRetentionTests.HoldUncommittedWriteAsync(databasePath, readyPath)
        .ConfigureAwait(false);

if (args.Length != 0 && args is not ["--package-native-fixture"])
{
    Console.Error.WriteLine("Unknown SQLite test arguments; no fixture was executed.");
    return 2;
}

var tests = new (string Name, Func<Task> Run, bool DeterministicTimeCoverage)[]
{
    ("local store retains exact immutable revision chains", SqliteRetentionTests.RevisionChainAsync, false),
    ("local store rejects divergent replay and stale heads", SqliteRetentionTests.ConflictAndStaleBaseAsync, false),
    ("local store serializes competing writers without merging", SqliteRetentionTests.ConcurrentWriterAsync, false),
    ("local store survives restart and rebuilds bounded search", SqliteRetentionTests.RestartAndSearchAsync, true),
    ("local store produces a restorable integrity-checked backup", SqliteRetentionTests.BackupAndRestoreAsync, false),
    ("local restore rejects corrupt and unsupported backups", SqliteRetentionTests.InvalidBackupAsync, false),
    ("local restore rejects forged schemas and live sidecars", SqliteRetentionTests.ForgedAndLiveBackupAsync, false),
    ("local store rejects unexpected executable schema objects", SqliteRetentionTests.UnexpectedSchemaObjectAsync, false),
    ("local store applies the current-user OS boundary", SqliteRetentionTests.CurrentUserBoundaryAsync, false),
    ("local store rejects altered canonical content and unsafe roots", SqliteRetentionTests.InputAndPathBoundaryAsync, false),
    ("local store rejects prohibited retained content before persistence", SqliteRetentionTests.ContentScreeningBoundaryAsync, false),
    ("local store detects semantic tampering on reads and integrity checks", SqliteRetentionTests.SemanticIntegrityAsync, false),
    ("local store rolls back a simulated disk-full append", SqliteRetentionTests.DiskFullRollbackAsync, false),
    ("local store rolls back an uncommitted writer after process termination", SqliteRetentionTests.CrashRollbackAsync, false),
    ("local store enforces project and aggregate retention quotas", SqliteRetentionTests.QuotaBoundaryAsync, false),
    ("local search omits expired retained heads", SqliteRetentionTests.ExpiredSearchBoundaryAsync, true),
    ("operational tasks enforce namespaces lifecycle history and expected revisions", SqliteOperationalTests.TaskLifecycleNamespaceAndConcurrencyAsync, true),
    ("operational knowledge round-trips content and searches immediately", SqliteOperationalTests.KnowledgeRoundTripSearchAndRevisionAsync, true),
    ("SQLite briefs preserve canonical bytes delta and restart recovery", SqliteProjectBriefTests.CanonicalSnapshotDeltaAndRestartAsync, true),
    ("SQLite briefs reject stale predecessor writes visibly", SqliteProjectBriefTests.StaleBriefWriteFailsVisiblyAsync, true),
    ("SQLite brief replay stays idempotent after the head advances", SqliteProjectBriefTests.ReplayAfterHeadAdvanceAsync, true)
};

if (args is ["--package-native-fixture"])
{
    // Exercise the packaged native dependency with real disposable databases;
    // the separate process-termination crash lane is outside this acceptance.
    var selected = new HashSet<string>(StringComparer.Ordinal)
    {
        "local store retains exact immutable revision chains",
        "local store rejects divergent replay and stale heads",
        "local store serializes competing writers without merging",
        "local store survives restart and rebuilds bounded search",
        "local store produces a restorable integrity-checked backup",
        "operational tasks enforce namespaces lifecycle history and expected revisions",
        "operational knowledge round-trips content and searches immediately"
    };
    tests = tests.Where(test => selected.Contains(test.Name)).ToArray();
    using var probe = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    probe.Open();
    Console.WriteLine("Native SQLite package version: " + probe.ServerVersion);
    if (Version.Parse(probe.ServerVersion) < new Version(3, 50, 2))
        throw new InvalidOperationException("The native SQLite package predates the CVE-2025-6965 fix.");
}

var failures = new List<string>();
var deterministicTimeChecksPassed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
        if (test.DeterministicTimeCoverage)
            deterministicTimeChecksPassed++;
    }
    catch (Exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}");
    }
}

if (failures.Count != 0)
    return 1;

var expectedDeterministicTimeChecks = tests.Count(test => test.DeterministicTimeCoverage);
if (deterministicTimeChecksPassed != expectedDeterministicTimeChecks)
{
    Console.Error.WriteLine("FAIL deterministic-time-evidence");
    return 1;
}

Console.WriteLine("METRIC deterministic-time passed=true");
return 0;
