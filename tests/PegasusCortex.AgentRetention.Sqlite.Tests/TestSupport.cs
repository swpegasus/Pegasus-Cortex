namespace Pegasus.Cortex.AgentRetention.Sqlite.Tests;

internal static class ProviderAssert
{
    public static void True(bool value, string? message = null)
    {
        if (!value)
            throw new InvalidOperationException(message ?? "Expected true.");
    }

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

    public static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Expected exact canonical byte equality.");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
    }

    public static TException Throws<TException>(Func<object?> action)
        where TException : Exception
    {
        try
        {
            _ = action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected an exception of type {typeof(TException).Name}.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
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

        throw new InvalidOperationException(
            $"Expected an exception of type {typeof(TException).Name}.");
    }
}

/// <summary>
/// Owns one direct GUID child beneath a fixed test-only directory in the OS
/// temporary root. Disposal revalidates that exact relationship before deletion.
/// </summary>
internal sealed class TemporarySqliteRoot : IDisposable
{
    private static readonly string ParentPath = Path.GetFullPath(
        Path.Combine(
            Path.GetTempPath(),
            "pegasus-cortex-agent-retention-sqlite-tests"));
    private bool _disposed;

    private TemporarySqliteRoot(string rootPath)
    {
        RootPath = rootPath;
        DatabasePath = Path.Combine(rootPath, "profile", "cortex.db");
    }

    public string RootPath { get; }
    public string DatabasePath { get; }

    public static TemporarySqliteRoot Create()
    {
        Directory.CreateDirectory(ParentPath);
        var rootPath = Path.GetFullPath(
            Path.Combine(ParentPath, Guid.NewGuid().ToString("N")));
        ValidateOwnedRoot(rootPath);
        Directory.CreateDirectory(rootPath);
        return new TemporarySqliteRoot(rootPath);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ValidateOwnedRoot(RootPath);
        if (Directory.Exists(RootPath))
            Directory.Delete(RootPath, recursive: true);
    }

    private static void ValidateOwnedRoot(string rootPath)
    {
        var canonical = Path.GetFullPath(rootPath);
        var parent = Path.GetDirectoryName(canonical);
        var leaf = Path.GetFileName(canonical);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(parent, ParentPath, comparison) ||
            !Guid.TryParseExact(leaf, "N", out _))
        {
            throw new InvalidOperationException(
                "The temporary SQLite test root is outside its owned GUID boundary.");
        }
    }
}
