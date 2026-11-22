using System.Globalization;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

internal sealed record CortexLocalProjectTransferExportArguments(
    string ProfileId,
    CortexMcpLocalStatePaths StatePaths,
    string ProjectLinkId,
    string DestinationRoot,
    string RunId,
    DateTimeOffset CapturedAt)
{
    public static CortexLocalProjectTransferExportArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 13 ||
            !string.Equals(args[0], "export-project-transfer", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: export-project-transfer --profile <id> --state-root <dedicated absolute profile directory> --project-link <id> --destination-root <existing protected absolute directory> --run-id <id> --captured-at-utc <round-trip UTC timestamp>.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile", "--state-root", "--project-link",
            "--destination-root", "--run-id", "--captured-at-utc"
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index]) || string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].Any(char.IsControl) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Transfer export options must be unique supported pairs.");
            }
        }
        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue("--state-root", out var stateRoot) ||
            !values.TryGetValue("--project-link", out var projectLinkId) ||
            !values.TryGetValue("--destination-root", out var destinationRoot) ||
            !values.TryGetValue("--run-id", out var runId) ||
            !values.TryGetValue("--captured-at-utc", out var capturedText) ||
            projectLinkId.Length != 70 ||
            !projectLinkId.StartsWith("plink_", StringComparison.Ordinal) ||
            projectLinkId.AsSpan(6).ContainsAnyExcept("0123456789abcdef") ||
            !Path.IsPathFullyQualified(destinationRoot) ||
            !CortexLocalRecoveryRunIdPolicy.IsValid(runId) ||
            !DateTimeOffset.TryParseExact(
                capturedText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var capturedAt) ||
            capturedAt.Offset != TimeSpan.Zero ||
            !CortexLocalRecoveryRunIdPolicy.IsNearCurrentUtc(capturedAt))
        {
            throw new ArgumentException("Transfer export requires exact bounded profile, path, scope, run, and UTC inputs.");
        }
        return new CortexLocalProjectTransferExportArguments(
            profileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(profileId, stateRoot),
            projectLinkId,
            destinationRoot,
            runId,
            capturedAt);
    }
}
