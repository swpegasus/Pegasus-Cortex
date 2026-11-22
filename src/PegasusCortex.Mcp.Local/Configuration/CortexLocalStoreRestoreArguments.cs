namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for create-only restoration of a sealed local-store
/// backup. Restore cannot replace an existing profile or rebind its identity.
/// </summary>
internal sealed record CortexLocalStoreRestoreArguments(
    string ProfileId,
    CortexMcpLocalStatePaths DestinationPaths,
    string BackupRoot)
{
    public static CortexLocalStoreRestoreArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 7 ||
            !string.Equals(
                args[0],
                "restore-local-backup",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: restore-local-backup --profile <id> --destination-state-root <new dedicated absolute profile directory> --backup-root <sealed local backup directory>.");
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile",
            "--destination-state-root",
            "--backup-root"
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
                    "Local restore options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue(
                "--destination-state-root",
                out var destinationStateRoot) ||
            !values.TryGetValue("--backup-root", out var backupRoot) ||
            !Path.IsPathFullyQualified(backupRoot))
        {
            throw new ArgumentException(
                "Local restore requires a bounded profile and exact absolute paths.");
        }

        return new CortexLocalStoreRestoreArguments(
            profileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                profileId,
                destinationStateRoot),
            backupRoot);
    }
}
