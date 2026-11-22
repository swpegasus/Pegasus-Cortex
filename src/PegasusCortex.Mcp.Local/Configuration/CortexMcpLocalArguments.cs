using Pegasus.Cortex.LocalTransport.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Configuration;

internal enum CortexMcpLocalMode
{
    Service,
    Bridge
}

/// <summary>
/// Exact local-profile selection. Configuration comes only from command-line
/// arguments; environment variables cannot activate or switch a mode.
/// </summary>
internal sealed record CortexMcpLocalArguments(
    CortexMcpLocalMode Mode,
    string ProfileId,
    CortexMcpLocalStatePaths? StatePaths)
{
    public static CortexMcpLocalArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (PegasusCortexMcpLocalProgram.IsRetiredStdioInvocation(args))
            throw new ArgumentException(PegasusCortexMcpLocalProgram.StdioRetirementMessage);
        if (args.Length < 3 || args.Length % 2 == 0)
        {
            throw new ArgumentException(
                "Usage: service|bridge --profile <id>. " + PegasusCortexMcpLocalProgram.StdioRetirementMessage);
        }

        var mode = args[0] switch
        {
            "service" => CortexMcpLocalMode.Service,
            "bridge" => CortexMcpLocalMode.Bridge,
            _ => throw new ArgumentException(
                "The local profile mode must be exactly service or bridge.")
        };

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
                    "Local MCP options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId))
            throw new ArgumentException("The explicit --profile option is required.");
        _ = CortexLocalEndpoint.Create(profileId);

        if (values.Count != 1)
        {
            throw new ArgumentException(
                "Service and bridge modes accept only --profile; service state is derived from the current OS user.");
        }

        return new CortexMcpLocalArguments(
            mode,
            profileId,
            mode == CortexMcpLocalMode.Service
                ? CortexLocalProjectRegistryProvisioner.CurrentUserStatePaths(profileId)
                : null);
    }
}
