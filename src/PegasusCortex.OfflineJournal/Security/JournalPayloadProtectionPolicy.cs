using System.Security.Cryptography;
using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Security;

internal static class JournalPayloadProtectionPolicy
{
    private const int MaximumIdentifierCharacters = 128;
    private const int MaximumNonceCharacters = 1024;
    private const int MaximumAuthenticationTagCharacters = 2048;

    public static void ValidateMetadata(
        JournalPayloadProtectionMetadata? metadata,
        int maximumIdentifierCharacters)
    {
        if (maximumIdentifierCharacters is < 1 or > MaximumIdentifierCharacters ||
            metadata is null ||
            !IsIdentifier(metadata.Algorithm, maximumIdentifierCharacters) ||
            !IsIdentifier(metadata.KeyId, maximumIdentifierCharacters) ||
            !IsOpaqueValue(metadata.Nonce, MaximumNonceCharacters) ||
            !IsOpaqueValue(metadata.AuthenticationTag, MaximumAuthenticationTagCharacters))
        {
            throw new InvalidDataException("Journal payload protection metadata is missing, invalid, or unbounded.");
        }
    }

    public static bool IsIdentifier(string? value, int maximumCharacters = MaximumIdentifierCharacters) =>
        maximumCharacters is > 0 and <= MaximumIdentifierCharacters &&
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    public static bool IsDiagnosticCode(string? value) => IsIdentifier(value);

    public static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecodeCanonicalBase64Url(
        string? value,
        int expectedBytes,
        out byte[] decoded)
    {
        decoded = [];
        if (value is null || expectedBytes < 0 || value.Length == 0 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            var remainder = normalized.Length % 4;
            if (remainder == 1)
                return false;
            if (remainder != 0)
                normalized = normalized.PadRight(normalized.Length + 4 - remainder, '=');
            decoded = Convert.FromBase64String(normalized);
            if (decoded.Length != expectedBytes ||
                !string.Equals(value, EncodeBase64Url(decoded), StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
                decoded = [];
                return false;
            }
            return true;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }

    private static bool IsOpaqueValue(string? value, int maximumCharacters) =>
        value is { Length: > 0 } && value.Length <= maximumCharacters &&
        !value.Any(char.IsControl) && string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
