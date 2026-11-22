using Pegasus.Cortex.AgentRetention.Sqlite.Tests;

var tests = new (string Name, Func<Task> Run)[]
{
    ("typed task snapshots preserve bytes, dispositions, chains, and restart",
        SqliteAgentRetentionProviderTests.TaskSnapshotLifecycleAsync),
    ("typed work packets preserve start, continuation, closeout, and restart",
        SqliteAgentRetentionProviderTests.WorkPacketLifecycleAsync),
    ("typed task and work documents enforce 32 KiB and 8 KiB ceilings",
        SqliteAgentRetentionProviderTests.AggregateSizeBoundsAsync)
};

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
