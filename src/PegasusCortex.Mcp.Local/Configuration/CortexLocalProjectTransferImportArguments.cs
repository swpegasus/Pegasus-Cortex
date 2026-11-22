namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for the create-only provider-neutral transfer importer.
/// Project identity, root, and grants are supplied independently of the
/// untrusted bundle and are revalidated before any profile state is created.
/// </summary>
internal sealed record CortexLocalProjectTransferImportArguments(
    string ProfileId,
    CortexMcpLocalStatePaths DestinationPaths,
    string BundlePath,
    CortexLocalProjectRegistrationRequest Project)
{
    private const string Usage =
        "Usage: import-project-transfer --profile <id> --destination-state-root <new dedicated absolute profile directory> --bundle-path <canonical sealed bundle.json> --tenant-id <id> --project-link-id <plink_hash> --project-id <id> --project-namespace <id> --project-root <existing absolute directory> --allow-read true --allow-write true.";

    public static CortexLocalProjectTransferImportArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 21 ||
            !string.Equals(args[0], "import-project-transfer", StringComparison.Ordinal))
        {
            throw new ArgumentException(Usage);
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile",
            "--destination-state-root",
            "--bundle-path",
            "--tenant-id",
            "--project-link-id",
            "--project-id",
            "--project-namespace",
            "--project-root",
            "--allow-read",
            "--allow-write"
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
                    "Transfer import options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue(
                "--destination-state-root",
                out var destinationStateRoot) ||
            !values.TryGetValue("--bundle-path", out var bundlePath) ||
            !values.TryGetValue("--tenant-id", out var tenantId) ||
            !values.TryGetValue("--project-link-id", out var projectLinkId) ||
            !values.TryGetValue("--project-id", out var projectId) ||
            !values.TryGetValue("--project-namespace", out var projectNamespace) ||
            !values.TryGetValue("--project-root", out var projectRoot) ||
            !values.TryGetValue("--allow-read", out var allowReadText) ||
            !values.TryGetValue("--allow-write", out var allowWriteText) ||
            !TryParseBoolean(allowReadText, out var allowRead) ||
            !TryParseBoolean(allowWriteText, out var allowWrite) ||
            !allowRead ||
            !allowWrite ||
            !Path.IsPathFullyQualified(bundlePath))
        {
            throw new ArgumentException(
                "Transfer import requires every scope value, a canonical absolute bundle path, and explicit read/write grants set to true.");
        }

        CortexLocalProjectRegistration registration;
        try
        {
            registration = CortexLocalProjectRegistryFile.CreateRegistration(
                tenantId,
                projectLinkId,
                projectId,
                projectNamespace,
                projectRoot,
                allowRead,
                allowWrite);
            _ = CortexLocalProjectRegistryFile.Create(profileId, [registration]);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                "The separately supplied project scope, grants, or existing local root are invalid.",
                nameof(args),
                exception);
        }

        return new CortexLocalProjectTransferImportArguments(
            profileId,
            CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                profileId,
                destinationStateRoot),
            bundlePath,
            new CortexLocalProjectRegistrationRequest(
                registration.TenantId,
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                registration.RootPath,
                registration.AllowRead,
                registration.AllowWrite));
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (string.Equals(value, "true", StringComparison.Ordinal))
        {
            result = true;
            return true;
        }
        if (string.Equals(value, "false", StringComparison.Ordinal))
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }
}
