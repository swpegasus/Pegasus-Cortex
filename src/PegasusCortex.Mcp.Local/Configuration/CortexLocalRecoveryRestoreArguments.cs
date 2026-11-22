namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for the explicit one-shot recovery-export restore. Normal
/// service, bridge, and stdio startup never consult this parser.
/// </summary>
internal sealed record CortexLocalRecoveryRestoreArguments(
    string ProfileId,
    CortexMcpLocalStatePaths StatePaths,
    string ProjectLinkId,
    string ExportRoot)
{
    public static CortexLocalRecoveryRestoreArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 9 ||
            !string.Equals(
                args[0],
                "restore-recovery-export",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: restore-recovery-export --profile <id> --state-root <dedicated absolute profile directory> --project-link <id> --export-root <existing protected absolute export run directory>.");
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile",
            "--state-root",
            "--project-link",
            "--export-root"
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (!supported.Contains(name) ||
                string.IsNullOrWhiteSpace(value) ||
                value.Any(char.IsControl) ||
                !values.TryAdd(name, value))
            {
                throw new ArgumentException(
                    "Recovery restore options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue("--state-root", out var stateRoot) ||
            !values.TryGetValue("--project-link", out var projectLinkId) ||
            !values.TryGetValue("--export-root", out var exportRoot) ||
            !ValidIdentifier(projectLinkId, 96) ||
            !Path.IsPathFullyQualified(exportRoot))
        {
            throw new ArgumentException(
                "Recovery restore requires bounded identifiers and exact absolute paths.");
        }

        return new CortexLocalRecoveryRestoreArguments(
            profileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                profileId,
                stateRoot),
            projectLinkId,
            exportRoot);
    }

    private static bool ValidIdentifier(string? value, int maximumCharacters) =>
        value is { Length: > 0 } &&
        value.Length <= maximumCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');
}
