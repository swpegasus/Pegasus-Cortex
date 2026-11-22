var tests = new (string Name, Func<Task> Run)[]
{
    ("task snapshots map exact canonical retention fields", AgentRetentionApplicationTests.TaskSnapshotMappingAsync),
    ("task revisions translate typed hashes and preserve native CAS outcomes", AgentRetentionApplicationTests.TaskRevisionTranslationAsync),
    ("work packets remain passive retainable evidence", AgentRetentionApplicationTests.WorkPacketRetentionAsync),
    ("repository append results are returned unchanged", AgentRetentionApplicationTests.RepositoryResultPreservationAsync),
    ("caller cancellation stops before repository access", AgentRetentionApplicationTests.CancellationAsync),
    ("corrupt retained predecessors fail closed", AgentRetentionApplicationTests.CorruptPredecessorAsync),
    ("prohibited nested content fails closed without retention", AgentRetentionApplicationTests.ContentScreeningAsync),
    ("search projections are deterministic and bounded", AgentRetentionApplicationTests.SearchProjectionBoundsAsync),
    ("transfer bundles are sealed canonical and portable", RetentionTransferTests.BundleSealingAndPortabilityAsync),
    ("transfer import preserves exact chains and regenerates briefs", RetentionTransferTests.ImportPreservesChainsAndRegeneratesBriefAsync),
    ("transfer import requires a separate authorized project scope", RetentionTransferTests.AuthorizedScopeMismatchFailsBeforeRepositoryAccessAsync),
    ("exact transfer reimport is idempotent", RetentionTransferTests.ExactReimportIsIdempotentAsync),
    ("divergent target transfer fails before writes", RetentionTransferTests.DivergentTargetFailsBeforeWritesAsync),
    ("gapped target transfer fails before writes", RetentionTransferTests.GappedTargetFailsBeforeWritesAsync),
    ("extra target head transfer fails before writes", RetentionTransferTests.ExtraTargetHeadFailsBeforeWritesAsync),
    ("absolute machine paths are rejected from transfer", RetentionTransferTests.AbsoluteMachinePathsAreRejectedAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
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

internal static class Assert
{
    public static void True(bool value, string? message = null)
    {
        if (!value)
            throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool value, string? message = null) =>
        True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected '{expected}', received '{actual}'.");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new InvalidOperationException($"Did not expect '{actual}'.");
    }

    public static void Same(object expected, object actual)
    {
        if (!ReferenceEquals(expected, actual))
            throw new InvalidOperationException("Expected the same object instance.");
    }

    public static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Expected byte sequences to be identical.");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
    }

    public static void DoesNotContain(string unexpected, string actual)
    {
        if (actual.Contains(unexpected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Did not expect text to contain '{unexpected}'.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected an exception of type {typeof(TException).Name}.");
    }
}
