using System.Diagnostics.CodeAnalysis;

namespace Pegasus.Cortex.Service.Tests;

internal static class TestAssert
{
    internal static void True([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static void False([DoesNotReturnIf(true)] bool condition, string message) =>
        True(!condition, message);

    internal static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected '{expected}', received '{actual}'.");
    }

    internal static void Contains(string expected, string actual, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    internal static void DoesNotContain(string forbidden, string actual, string message)
    {
        if (actual.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(message);
    }

    internal static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    internal static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
