using System.Collections.ObjectModel;

namespace Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;

/// <summary>
/// Safe persistence failure surfaced to application adapters. Raw SQL, parameter values,
/// connection strings, and provider messages are intentionally excluded.
/// </summary>
public sealed class PostgreSqlPersistenceException : Exception
{
    public PostgreSqlPersistenceException(
        string code,
        string operation,
        bool retryable,
        IReadOnlyDictionary<string, string?>? context = null,
        string? sqlState = null,
        Exception? innerException = null,
        Exception? cleanupFailure = null)
        : base($"PostgreSQL persistence operation '{operation}' failed with code '{code}'.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Code = code;
        Operation = operation;
        Retryable = retryable;
        SqlState = SafeSqlState(sqlState);
        CleanupFailureType = cleanupFailure?.GetType().FullName;
        Context = new ReadOnlyDictionary<string, string?>(
            context is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : context.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
    }

    public string Code { get; }
    public string Operation { get; }
    public bool Retryable { get; }
    public string? SqlState { get; }
    public string? CleanupFailureType { get; }
    public IReadOnlyDictionary<string, string?> Context { get; }

    private static string? SafeSqlState(string? value) =>
        value is { Length: 5 } && value.All(char.IsAsciiLetterOrDigit) ? value : null;
}
