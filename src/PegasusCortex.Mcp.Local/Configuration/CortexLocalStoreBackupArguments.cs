using System.Globalization;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for the explicit local-store backup operation. The command
/// is never reachable from normal MCP service, bridge, or stdio startup.
/// </summary>
internal sealed record CortexLocalStoreBackupArguments(
    string ProfileId,
    CortexMcpLocalStatePaths StatePaths,
    string DestinationRoot,
    string RunId,
    DateTimeOffset CapturedAt)
{
    public static CortexLocalStoreBackupArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 11 ||
            !string.Equals(args[0], "backup-local", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: backup-local --profile <id> --state-root <dedicated absolute profile directory> --destination-root <existing protected absolute directory> --run-id <id> --captured-at-utc <round-trip UTC timestamp>.");
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile",
            "--state-root",
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
                    "Local backup options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue("--state-root", out var stateRoot) ||
            !values.TryGetValue("--destination-root", out var destinationRoot) ||
            !values.TryGetValue("--run-id", out var runId) ||
            !values.TryGetValue("--captured-at-utc", out var capturedAtText) ||
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
                "Local backup requires bounded identifiers, exact paths, and a round-trip UTC capture timestamp.");
        }

        return new CortexLocalStoreBackupArguments(
            profileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                profileId,
                stateRoot),
            destinationRoot,
            runId,
            capturedAt);
    }
}
