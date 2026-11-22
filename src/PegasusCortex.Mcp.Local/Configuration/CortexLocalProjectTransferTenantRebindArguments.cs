using System.Globalization;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict inputs for the explicit copy-first tenant-scope rebind. The command
/// cannot change logical project identity: link, project, and namespace are
/// acknowledged once and must already match the sealed source bundle.
/// </summary>
internal sealed record CortexLocalProjectTransferTenantRebindArguments(
    string BundlePath,
    string ExpectedBundleFileSha256,
    string SourceTenantId,
    string TargetTenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string DestinationRoot,
    string RunId,
    DateTimeOffset CapturedAt)
{
    internal const string Command = "rebind-project-transfer-tenant";
    internal const string Usage =
        "Usage: rebind-project-transfer-tenant --bundle <protected canonical absolute bundle.json> --expected-sha256 <lowercase SHA-256> --source-tenant <id> --target-tenant <id> --project-link <id> --project-id <id> --namespace <id> --destination-root <existing protected absolute directory> --run-id <id> --captured-at-utc <round-trip UTC timestamp>.";

    public static CortexLocalProjectTransferTenantRebindArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 21 ||
            !string.Equals(args[0], Command, StringComparison.Ordinal))
        {
            throw new ArgumentException(Usage);
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--bundle",
            "--expected-sha256",
            "--source-tenant",
            "--target-tenant",
            "--project-link",
            "--project-id",
            "--namespace",
            "--destination-root",
            "--run-id",
            "--captured-at-utc"
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (!supported.Contains(args[index]) ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].Any(char.IsControl) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException(Usage);
            }
        }

        if (!values.TryGetValue("--bundle", out var bundlePath) ||
            !values.TryGetValue("--expected-sha256", out var expectedSha256) ||
            !values.TryGetValue("--source-tenant", out var sourceTenantId) ||
            !values.TryGetValue("--target-tenant", out var targetTenantId) ||
            !values.TryGetValue("--project-link", out var projectLinkId) ||
            !values.TryGetValue("--project-id", out var projectId) ||
            !values.TryGetValue("--namespace", out var projectNamespace) ||
            !values.TryGetValue("--destination-root", out var destinationRoot) ||
            !values.TryGetValue("--run-id", out var runId) ||
            !values.TryGetValue("--captured-at-utc", out var capturedText) ||
            !Path.IsPathFullyQualified(bundlePath) ||
            !Path.IsPathFullyQualified(destinationRoot) ||
            !ValidSha256(expectedSha256) ||
            !ValidIdentifier(sourceTenantId) ||
            !ValidIdentifier(targetTenantId) ||
            string.Equals(sourceTenantId, targetTenantId, StringComparison.Ordinal) ||
            !ValidProjectLinkId(projectLinkId) ||
            !ValidIdentifier(projectId) ||
            !ValidIdentifier(projectNamespace) ||
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
            throw new ArgumentException(Usage);
        }

        return new CortexLocalProjectTransferTenantRebindArguments(
            bundlePath,
            expectedSha256,
            sourceTenantId,
            targetTenantId,
            projectLinkId,
            projectId,
            projectNamespace,
            destinationRoot,
            runId,
            capturedAt);
    }

    private static bool ValidIdentifier(string value) =>
        value.Length is > 0 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');

    private static bool ValidProjectLinkId(string value) =>
        value.Length == 70 &&
        value.StartsWith("plink_", StringComparison.Ordinal) &&
        ValidSha256(value[6..]);

    private static bool ValidSha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
