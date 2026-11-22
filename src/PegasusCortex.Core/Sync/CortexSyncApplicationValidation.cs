using Pegasus.Cortex.Contracts.Sync;

namespace Pegasus.Cortex.Core.Sync;

/// <summary>
/// Core-local boundary checks. The wire package owns serialization; Core independently
/// validates authority and provider values without exposing Contracts implementation helpers.
/// </summary>
internal static class CortexSyncApplicationValidation
{
    public static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    public static bool IsContractId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumContractIdCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Contains('/', StringComparison.Ordinal) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-' or '/');

    public static bool IsNamespace(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumNamespaceCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    public static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static IReadOnlyList<T> SnapshotList<T>(
        IEnumerable<T> source,
        int maximumCount,
        string parameterName,
        bool requireNonEmpty)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var values = new List<T>(Math.Min(maximumCount, 16));
        using var enumerator = source.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (values.Count == maximumCount)
                throw new ArgumentOutOfRangeException(parameterName, $"The collection exceeds {maximumCount} items.");
            if (enumerator.Current is null)
                throw new ArgumentException("The collection contains a null item.", parameterName);
            values.Add(enumerator.Current);
        }
        if (requireNonEmpty && values.Count == 0)
            throw new ArgumentException("The collection must not be empty.", parameterName);
        return Array.AsReadOnly(values.ToArray());
    }

    public static IReadOnlyList<string> SnapshotContractIds(
        IEnumerable<string> source,
        int maximumCount,
        string parameterName)
    {
        var values = SnapshotList(source, maximumCount, parameterName, requireNonEmpty: true);
        if (values.Any(value => !IsContractId(value)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new ArgumentException("Payload contract identifiers are invalid or duplicated.", parameterName);
        }
        return Array.AsReadOnly(values.Order(StringComparer.Ordinal).ToArray());
    }
}
