namespace Pegasus.Cortex.Mcp.Integrated.Recovery;

/// <summary>
/// Exact operator-supplied import boundary. Scope values are claims only; the
/// command independently resolves the protected server project link before it
/// constructs the authorized Core import scope.
/// </summary>
internal sealed record CortexIntegratedProjectTransferImportArguments(
    string BundlePath,
    string ExpectedBundleFileSha256,
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace)
{
    internal const string Command = "import-project-transfer";
    internal const string Usage =
        "Usage: import-project-transfer --bundle <protected canonical absolute file> --expected-sha256 <lowercase SHA-256> --tenant <id> --project-link <id> --project-id <id> --namespace <id>.";

    public static CortexIntegratedProjectTransferImportArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 13 ||
            !string.Equals(args[0], Command, StringComparison.Ordinal))
        {
            throw new ArgumentException(Usage);
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--bundle",
            "--expected-sha256",
            "--tenant",
            "--project-link",
            "--project-id",
            "--namespace"
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
            !values.TryGetValue("--tenant", out var tenantId) ||
            !values.TryGetValue("--project-link", out var projectLinkId) ||
            !values.TryGetValue("--project-id", out var projectId) ||
            !values.TryGetValue("--namespace", out var projectNamespace) ||
            !ValidCanonicalAbsolutePath(bundlePath) ||
            !ValidSha256(expectedSha256) ||
            !ValidIdentifier(tenantId) ||
            !ValidProjectLinkId(projectLinkId) ||
            !ValidIdentifier(projectId) ||
            !ValidIdentifier(projectNamespace))
        {
            throw new ArgumentException(Usage);
        }

        return new CortexIntegratedProjectTransferImportArguments(
            bundlePath,
            expectedSha256,
            tenantId,
            projectLinkId,
            projectId,
            projectNamespace);
    }

    private static bool ValidCanonicalAbsolutePath(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.Length > 240)
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(value),
                value,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');

    internal static bool ValidProjectLinkId(string value) =>
        value.Length == 70 &&
        value.StartsWith("plink_", StringComparison.Ordinal) &&
        ValidSha256(value[6..]);

    internal static bool ValidSha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
