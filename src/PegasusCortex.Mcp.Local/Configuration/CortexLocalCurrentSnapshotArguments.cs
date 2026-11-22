using System.Globalization;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for the explicit current-recovery materializer. Project
/// scope and source root are always resolved from the protected registry.
/// </summary>
internal sealed record CortexLocalCurrentSnapshotArguments(
    string ProfileId,
    CortexMcpLocalStatePaths StatePaths,
    string ProjectLinkId,
    DateTimeOffset CapturedAt)
{
    public static CortexLocalCurrentSnapshotArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 9 ||
            !string.Equals(args[0], "materialize-current", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: materialize-current --profile <id> --state-root <dedicated absolute profile directory> --project-link <id> --captured-at-utc <round-trip UTC timestamp>.");
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile",
            "--state-root",
            "--project-link",
            "--captured-at-utc"
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (!supported.Contains(name) || string.IsNullOrWhiteSpace(value) ||
                value.Any(char.IsControl) || !values.TryAdd(name, value))
            {
                throw new ArgumentException(
                    "Current materialization options must be unique supported pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue("--state-root", out var stateRoot) ||
            !values.TryGetValue("--project-link", out var projectLinkId) ||
            !values.TryGetValue("--captured-at-utc", out var capturedText) ||
            projectLinkId.Length != 70 ||
            !projectLinkId.StartsWith("plink_", StringComparison.Ordinal) ||
            projectLinkId.AsSpan(6).ContainsAnyExcept("0123456789abcdef") ||
            !DateTimeOffset.TryParseExact(
                capturedText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var capturedAt) ||
            capturedAt.Offset != TimeSpan.Zero ||
            !CortexLocalRecoveryRunIdPolicy.IsNearCurrentUtc(capturedAt))
        {
            throw new ArgumentException(
                "Current materialization requires exact profile, scope, and UTC inputs.");
        }

        return new CortexLocalCurrentSnapshotArguments(
            profileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                profileId,
                stateRoot),
            projectLinkId,
            capturedAt);
    }
}
