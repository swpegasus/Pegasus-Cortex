namespace Pegasus.Cortex.Contracts.WorkPackets;

internal static class AgentWorkPacketShapes
{
    public static void RequireHeader(
        int schemaVersion,
        string? contractId,
        string expectedContractId,
        string? canonicalization)
    {
        if (schemaVersion != AgentWorkPacketContractIds.SchemaVersion ||
            !string.Equals(contractId, expectedContractId, StringComparison.Ordinal) ||
            !string.Equals(canonicalization, AgentWorkPacketContractIds.Canonicalization, StringComparison.Ordinal))
        {
            throw new ArgumentException("The agent work-packet contract header is unsupported.");
        }
    }

    public static void RequireIdentifier(string? value, string parameterName)
    {
        if (!IsIdentifier(value))
            throw new ArgumentException("A bounded stable identifier is required.", parameterName);
    }

    public static void RequireHash(string? value, string parameterName)
    {
        if (!IsHash(value))
            throw new ArgumentException("A lowercase SHA-256 value is required.", parameterName);
    }

    public static void RequirePrefixedHash(
        string? value,
        string prefix,
        string parameterName)
    {
        if (value is null ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !IsHash(value[prefix.Length..]))
        {
            throw new ArgumentException(
                "A supported prefixed lowercase SHA-256 identifier is required.",
                parameterName);
        }
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
    }

    public static void RequireText(
        string? value,
        int maximumCharacters,
        string parameterName,
        bool allowEmpty = false)
    {
        if (value is null || value.Length > maximumCharacters ||
            (!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("A bounded normalized text value is required.", parameterName);
        }
    }

    public static void RequireRelativeProjectPath(string? value, string parameterName)
    {
        RequireText(
            value,
            AgentWorkPacketContractLimits.MaximumRelativeProjectPathCharacters,
            parameterName);
        if (value!.StartsWith('/') || value.Contains('\\') || value.Contains(':'))
            throw new ArgumentException("A relative project path is required.", parameterName);

        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ')))
        {
            throw new ArgumentException("The relative project path contains an unsafe segment.", parameterName);
        }
    }

    public static IReadOnlyList<T> SnapshotList<T>(
        IEnumerable<T> source,
        int maximumCount,
        string parameterName,
        bool requireNonEmpty = false)
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

    public static IReadOnlyList<string> SnapshotTextList(
        IEnumerable<string> source,
        int maximumCount,
        int maximumCharacters,
        string parameterName,
        bool requireNonEmpty = false)
    {
        var values = SnapshotList(source, maximumCount, parameterName, requireNonEmpty);
        if (values.Any(value => !IsText(value, maximumCharacters)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new ArgumentException("The collection contains an invalid or duplicate text value.", parameterName);
        }
        return values;
    }

    public static IReadOnlyList<string> SnapshotIdentifierSet(
        IEnumerable<string> source,
        int maximumCount,
        string parameterName)
    {
        var values = SnapshotList(source, maximumCount, parameterName);
        if (values.Any(value => !IsIdentifier(value)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new ArgumentException("The collection contains an invalid or duplicate identifier.", parameterName);
        }
        return Array.AsReadOnly(values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyList<string> SnapshotRelativeProjectPaths(
        IEnumerable<string> source,
        string parameterName)
    {
        var values = SnapshotList(
            source,
            AgentWorkPacketContractLimits.MaximumProjectPaths,
            parameterName);
        foreach (var value in values)
            RequireRelativeProjectPath(value, parameterName);
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("The collection contains a duplicate project path.", parameterName);
        return Array.AsReadOnly(values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= AgentWorkPacketContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    public static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsText(string? value, int maximumCharacters, bool allowEmpty = false) =>
        value is not null &&
        value.Length <= maximumCharacters &&
        (allowEmpty || !string.IsNullOrWhiteSpace(value)) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(character => !char.IsControl(character) ||
            character is '\r' or '\n' or '\t');

    public static bool SameOwner(AgentWorkOwnerV1? left, AgentWorkOwnerV1? right) =>
        left is not null && right is not null &&
        string.Equals(left.AuthorityId, right.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal) &&
        string.Equals(left.ServiceId, right.ServiceId, StringComparison.Ordinal);
}
