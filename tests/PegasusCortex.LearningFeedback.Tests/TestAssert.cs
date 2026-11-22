using Pegasus.Shared.Diagnostics;

internal static class TestAssert
{
    public static void True(bool value, string? message = null)
    {
        if (!value)
            throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("The sequences were not equal.");
    }

    public static void Code(string expected, IReadOnlyList<PegasusDiagnostic> diagnostics)
    {
        Equal(1, diagnostics.Count);
        Equal(expected, diagnostics[0].Code);
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
    }

    public static void DoesNotContain(string prohibited, string actual)
    {
        if (actual.Contains(prohibited, StringComparison.Ordinal))
            throw new InvalidOperationException($"Text unexpectedly contained '{prohibited}'.");
    }

    public static void Hash(string value)
    {
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException("Expected a lowercase SHA-256 hash.");
        }
    }

    public static void Throws<TException>(Action action) where TException : Exception
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

    public static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
