using Pegasus.Cortex.Service.Tests;

var tests = new (string Name, Func<Task> Run)[]
{
    ("configuration validation fails closed and redacts secrets", CortexPostgreSqlCompositionTests.ValidationAndRedactionAsync),
    ("runtime composition does not retain migration credentials", CortexPostgreSqlCompositionTests.RuntimeWithoutMigrationCredentialsAsync),
    ("runtime and migration sources enforce secure separate defaults", CortexPostgreSqlCompositionTests.ConnectionDefaultsAsync),
    ("startup privilege reconciliation is minimal locked and role-attested", CortexPostgreSqlCompositionTests.RuntimePrivilegeContractAsync),
    ("pinned loopback TLS validates CA host lifetime purpose and SPKI", CortexPostgreSqlCompositionTests.PinnedTlsPolicyAsync),
    ("trusted loopback disables TLS and admits ready runtime and sync", CortexPostgreSqlCompositionTests.TrustedLoopbackAsync),
    ("local bootstrap is loopback-only and readiness-degraded", CortexPostgreSqlCompositionTests.LocalBootstrapAsync),
    ("composition factories and asynchronous ownership are deterministic", CortexPostgreSqlCompositionTests.LifecycleAsync),
    ("poisoned runtime denies admission but preserves readiness", CortexPostgreSqlCompositionTests.PoisonAdmissionAsync),
    ("factory and disposal races remain bounded", CortexPostgreSqlCompositionTests.ConcurrentLifecycleAsync),
    ("executable status remains configuration free", CortexPostgreSqlCompositionTests.ProgramBoundaryAsync),
    ("service lifecycle rehearsal starts and stops without dependencies", CortexServiceLifecycleTests.GracefulShutdownAsync),
    ("service lifecycle rehearsal handles shutdown before readiness", CortexServiceLifecycleTests.ShutdownBeforeReadyAsync),
    ("service lifecycle marker failures retain bounded diagnostics", CortexServiceLifecycleTests.OutputFailuresAsync)
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
