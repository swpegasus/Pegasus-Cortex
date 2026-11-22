using System.Globalization;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for the explicit one-shot recovery import. This parser is
/// not consulted by service, bridge, or stdio startup.
/// </summary>
internal sealed record CortexLocalRecoveryImportArguments(
    string ProfileId,
    CortexMcpLocalStatePaths StatePaths,
    string ProjectLinkId,
    string DestinationRoot,
    string RunId,
    DateTimeOffset CapturedAt)
{
    public static CortexLocalRecoveryImportArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 13 ||
            !string.Equals(args[0], "import-recovery", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: import-recovery --profile <id> --state-root <dedicated absolute profile directory> --project-link <id> --destination-root <existing protected absolute directory> --run-id <id> --captured-at-utc <round-trip UTC timestamp>.");
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile",
            "--state-root",
            "--project-link",
            "--destination-root",
            "--run-id",
            "--captured-at-utc"
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
                    "Recovery import options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue("--state-root", out var stateRoot) ||
            !values.TryGetValue("--project-link", out var projectLinkId) ||
            !values.TryGetValue("--destination-root", out var destinationRoot) ||
            !values.TryGetValue("--run-id", out var runId) ||
            !values.TryGetValue("--captured-at-utc", out var capturedAtText) ||
            !ValidIdentifier(projectLinkId, 96) ||
            !CortexLocalRecoveryRunIdPolicy.IsValid(runId) ||
            !DateTimeOffset.TryParseExact(
                capturedAtText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var capturedAt) ||
            !CortexLocalRecoveryRunIdPolicy.IsNearCurrentUtc(capturedAt))
        {
            throw new ArgumentException(
                "Recovery import requires bounded identifiers, exact paths, and a round-trip UTC capture timestamp.");
        }

        var statePaths = CortexLocalProjectRegistryProvisioner.PortableStatePaths(
            profileId,
            stateRoot);
        return new CortexLocalRecoveryImportArguments(
            profileId,
            statePaths,
            projectLinkId,
            destinationRoot,
            runId,
            capturedAt);
    }

    private static bool ValidIdentifier(string? value, int maximumCharacters) =>
        value is { Length: > 0 } &&
        value.Length <= maximumCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');
}
