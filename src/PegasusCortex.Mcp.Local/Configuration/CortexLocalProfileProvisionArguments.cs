namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for creating one empty protected local profile. Project
/// registration remains a separate explicit operation.
/// </summary>
internal sealed record CortexLocalProfileProvisionArguments(
    string ProfileId,
    CortexMcpLocalStatePaths StatePaths)
{
    private const string Usage =
        "Usage: provision-profile --profile <id> --state-root <dedicated absolute profile directory>.";

    public static CortexLocalProfileProvisionArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 5 ||
            !string.Equals(args[0], "provision-profile", StringComparison.Ordinal))
        {
            throw new ArgumentException(Usage);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (name is not ("--profile" or "--state-root") ||
                string.IsNullOrWhiteSpace(value) ||
                value.Any(char.IsControl) ||
                !values.TryAdd(name, value))
            {
                throw new ArgumentException(
                    "Profile provisioning options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue("--state-root", out var stateRoot))
        {
            throw new ArgumentException(Usage);
        }

        return new CortexLocalProfileProvisionArguments(
            profileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                profileId,
                stateRoot));
    }
}
