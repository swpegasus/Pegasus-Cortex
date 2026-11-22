namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Shared portable run identity policy. Import currently uses the identity in
/// recovery paths; export hashes it for paths but keeps the same cross-profile
/// admission rule so identifiers cannot alias on Windows.
/// </summary>
internal static class CortexLocalRecoveryRunIdPolicy
{
    private static readonly HashSet<string> WindowsReservedBasenames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "con", "prn", "aux", "nul", "clock$",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
        };

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-') &&
        !value.EndsWith('.') &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !WindowsReservedBasenames.Contains(value.Split('.', 2)[0]);

    public static bool IsNearCurrentUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero &&
        (value - TimeProvider.System.GetUtcNow()).Duration() <=
            TimeSpan.FromMinutes(5);
}
