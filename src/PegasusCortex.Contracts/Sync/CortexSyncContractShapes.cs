namespace Pegasus.Cortex.Contracts.Sync;

internal static class CortexSyncContractShapes
{
    public static void RequireHeader(int schemaVersion, string? contractId, string expectedContractId, string? canonicalization)
    {
        if (schemaVersion != CortexSyncContractIds.SchemaVersion ||
            !string.Equals(contractId, expectedContractId, StringComparison.Ordinal) ||
            !string.Equals(canonicalization, CortexSyncContractIds.Canonicalization, StringComparison.Ordinal))
        {
            throw new ArgumentException("The synchronization contract header is unsupported.");
        }
    }

    public static void RequireIdentifier(string? value, string parameterName)
    {
        if (!IsIdentifier(value))
            throw new ArgumentException("A bounded synchronization identifier is required.", parameterName);
    }

    public static void RequireContractId(string? value, string parameterName)
    {
        if (!IsContractId(value))
            throw new ArgumentException("A bounded versioned contract identifier is required.", parameterName);
    }

    public static void RequireNamespace(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > CortexSyncContractLimits.MaximumNamespaceCharacters ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not ':' and not '-'))
        {
            throw new ArgumentException("A bounded namespace hint is required.", parameterName);
        }
    }

    public static void RequireHash(string? value, string parameterName)
    {
        if (!IsHash(value))
            throw new ArgumentException("A lowercase SHA-256 value is required.", parameterName);
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
    }

    public static void RequireText(string? value, int maximumCharacters, string parameterName, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximumCharacters ||
            (!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("A bounded text value without unsupported control characters is required.", parameterName);
        }
    }

    public static void RequireRelativePath(string? value, string parameterName)
    {
        RequireText(value, CortexSyncContractLimits.MaximumRelativePathCharacters, parameterName);
        if (value!.StartsWith('/') || value.StartsWith('\\') || value.Contains(':') ||
            value.Split(['/', '\\'], StringSplitOptions.None)
                .Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("A contained relative path is required.", parameterName);
        }
    }

    public static void RequireExactSourcePath(string? value, string parameterName)
    {
        // Exact source paths are opaque provenance on the wire. A Windows path must remain
        // readable on Linux/ARM64 and vice versa; a platform adapter separately validates
        // syntax, containment, and no-follow access before opening a source file.
        RequireText(value, CortexSyncContractLimits.MaximumExactSourcePathCharacters, parameterName);
    }

    public static void RequireEncodedBytes(
        string? base64Url,
        int declaredBytes,
        int maximumBytes,
        string parameterName)
    {
        if (declaredBytes is < 1 || declaredBytes > maximumBytes ||
            string.IsNullOrEmpty(base64Url) || base64Url.Length > EncodedLength(maximumBytes) ||
            base64Url.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The encoded payload is malformed or outside its byte bound.", parameterName);
        }

        try
        {
            if (DecodeBase64Url(base64Url).Length != declaredBytes)
                throw new ArgumentException("The encoded payload length does not match its declared byte count.", parameterName);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The encoded payload is not valid base64url.", parameterName, exception);
        }
    }

    public static byte[] DecodeBase64Url(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(padded);
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

    public static IReadOnlyList<string> SnapshotIdentifierSet(
        IEnumerable<string> source,
        int maximumCount,
        string parameterName,
        bool requireNonEmpty = false) =>
        SnapshotStringSet(source, maximumCount, parameterName, IsIdentifier, requireNonEmpty);

    public static IReadOnlyList<string> SnapshotContractIdSet(
        IEnumerable<string> source,
        int maximumCount,
        string parameterName,
        bool requireNonEmpty = false) =>
        SnapshotStringSet(source, maximumCount, parameterName, IsContractId, requireNonEmpty);

    public static IReadOnlyList<CortexSyncExtensionV1> SnapshotExtensions(
        IEnumerable<CortexSyncExtensionV1> source,
        string parameterName)
    {
        var values = SnapshotList(source, CortexSyncContractLimits.MaximumExtensions, parameterName)
            .OrderBy(item => item.ContractId, StringComparer.Ordinal)
            .ToArray();
        if (values.Select(item => item.ContractId).Distinct(StringComparer.Ordinal).Count() != values.Length ||
            values.Sum(item => (long)item.PayloadBytes) > CortexSyncContractLimits.MaximumTotalExtensionPayloadBytes)
        {
            throw new ArgumentException("Extensions must be unique and remain within the aggregate payload bound.", parameterName);
        }
        return Array.AsReadOnly(values);
    }

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

    public static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<string> SnapshotStringSet(
        IEnumerable<string> source,
        int maximumCount,
        string parameterName,
        Func<string?, bool> validator,
        bool requireNonEmpty)
    {
        var values = SnapshotList(source, maximumCount, parameterName, requireNonEmpty);
        if (values.Any(value => !validator(value)) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("The collection contains an invalid or duplicate value.", parameterName);
        return Array.AsReadOnly(values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static int EncodedLength(int bytes) => checked(((bytes + 2) / 3) * 4);
}

/// <summary>
/// The only additive extension lane in the v1 family. Extension payloads are bounded
/// canonical JSON bytes encoded as base64url. They are data only: no extension can grant
/// identity, namespace access, acknowledgement, cleanup, cutover, or hash-chain authority.
/// Unknown required extensions fail compatibility; unknown optional extensions are preserved
/// byte-for-byte and ignored for decisions.
/// </summary>
public sealed record CortexSyncExtensionV1
{
    public CortexSyncExtensionV1(
        string contractId,
        bool required,
        string payloadBase64Url,
        int payloadBytes,
        string payloadSha256)
    {
        CortexSyncContractShapes.RequireContractId(contractId, nameof(contractId));
        CortexSyncContractShapes.RequireEncodedBytes(
            payloadBase64Url,
            payloadBytes,
            CortexSyncContractLimits.MaximumExtensionPayloadBytes,
            nameof(payloadBase64Url));
        CortexSyncContractShapes.RequireHash(payloadSha256, nameof(payloadSha256));
        ContractId = contractId;
        Required = required;
        PayloadBase64Url = payloadBase64Url;
        PayloadBytes = payloadBytes;
        PayloadSha256 = payloadSha256;
    }

    public string ContractId { get; }
    public bool Required { get; }
    public string PayloadBase64Url { get; }
    public int PayloadBytes { get; }
    public string PayloadSha256 { get; }
}
