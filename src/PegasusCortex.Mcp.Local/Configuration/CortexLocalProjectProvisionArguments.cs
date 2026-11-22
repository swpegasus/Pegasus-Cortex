namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for the explicit create-only local project provisioner.
/// This parser is never consulted by service, bridge, or stdio startup.
/// </summary>
internal sealed record CortexLocalProjectProvisionArguments(
    string ProfileId,
    CortexMcpLocalStatePaths StatePaths,
    CortexLocalProjectRegistrationRequest Project)
{
    private const string Usage =
        "Usage: provision-project --profile <id> --state-root <dedicated absolute profile directory> --tenant-id <id> --project-link-id <plink_hash> --project-id <id> --project-namespace <id> --project-root <existing absolute directory> --allow-read <true|false> --allow-write <true|false>.";

    public static CortexLocalProjectProvisionArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 19 ||
            !string.Equals(args[0], "provision-project", StringComparison.Ordinal))
        {
            throw new ArgumentException(Usage);
        }

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "--profile",
            "--state-root",
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
                    "Provisioning options must be unique, supported, non-empty pairs.");
            }
        }

        if (!values.TryGetValue("--profile", out var profileId) ||
            !values.TryGetValue("--state-root", out var stateRoot) ||
            !values.TryGetValue("--tenant-id", out var tenantId) ||
            !values.TryGetValue("--project-link-id", out var projectLinkId) ||
            !values.TryGetValue("--project-id", out var projectId) ||
            !values.TryGetValue("--project-namespace", out var projectNamespace) ||
            !values.TryGetValue("--project-root", out var projectRoot) ||
            !values.TryGetValue("--allow-read", out var allowReadText) ||
            !values.TryGetValue("--allow-write", out var allowWriteText) ||
            !TryParseBoolean(allowReadText, out var allowRead) ||
            !TryParseBoolean(allowWriteText, out var allowWrite) ||
            !allowRead && !allowWrite)
        {
            throw new ArgumentException(
                "Provisioning requires every option exactly once, strict boolean grants, and at least one allowed operation.");
        }

        var statePaths = CortexLocalProjectRegistryProvisioner.PortableStatePaths(
            profileId,
            stateRoot);

        // Reuse the registry's root, identifier, owner, reparse-point, and path
        // policy here. The provisioner deliberately revalidates immediately
        // before creation so parsing cannot become an authorization shortcut.
        CortexLocalProjectRegistration validated;
        try
        {
            validated = CortexLocalProjectRegistryFile.CreateRegistration(
                tenantId,
                projectLinkId,
                projectId,
                projectNamespace,
                projectRoot,
                allowRead,
                allowWrite);
            _ = CortexLocalProjectRegistryFile.Create(profileId, [validated]);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                "The project registration identifiers, grants, or protected local root are invalid.",
                nameof(args),
                exception);
        }
        var project = new CortexLocalProjectRegistrationRequest(
            validated.TenantId,
            validated.ProjectLinkId,
            validated.ProjectId,
            validated.ProjectNamespace,
            validated.RootPath,
            validated.AllowRead,
            validated.AllowWrite);
        return new CortexLocalProjectProvisionArguments(
            profileId,
            statePaths,
            project);
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
