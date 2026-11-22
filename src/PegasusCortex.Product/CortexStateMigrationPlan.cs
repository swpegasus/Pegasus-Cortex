namespace Pegasus.Cortex.Product;

public enum CortexStateMigrationStrategy
{
    SqliteOnlineBackup,
    ExactFileCopy
}

public sealed record CortexStateMigrationItemV1(
    string Role,
    string SourcePath,
    string DestinationPath,
    CortexStateMigrationStrategy Strategy,
    bool Required);

public sealed record CortexLegacyStateRootsV1(
    string CortexUserRoot,
    string AgentOpsServiceRoot,
    string AgentOpsUserRoot);

public sealed record CortexStateMigrationPlanV1(
    string Contract,
    int Version,
    string OwnerSid,
    string TargetRoot,
    IReadOnlyList<CortexStateMigrationItemV1> Items)
{
    public const string ContractId = "pegasus-cortex-state-migration-plan";

    public static CortexStateMigrationPlanV1 Create(
        CortexProductLayout target,
        CortexLegacyStateRootsV1 legacy)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(legacy);
        target.RequireRuntimePathsWithinInstallRoot();

        var items = new[]
        {
            Database(
                "cortex-v2",
                Path.Combine(RequireLegacyRoot(legacy.CortexUserRoot), "data", "cortex-v2.db"),
                target.CortexDatabasePath,
                required: false),
            Database(
                "cortex-v1-preserved",
                Path.Combine(RequireLegacyRoot(legacy.CortexUserRoot), "data", "cortex.db"),
                target.LegacyCortexDatabasePath,
                required: false),
            Database(
                "execution-jobs",
                Path.Combine(RequireLegacyRoot(legacy.AgentOpsServiceRoot), "agentops.db"),
                Path.Combine(target.ServiceStateDirectory, "databases", "agentops.db"),
                required: false),
            Database(
                "execution-router",
                Path.Combine(RequireLegacyRoot(legacy.AgentOpsServiceRoot), "agentops-router.db"),
                Path.Combine(target.ServiceStateDirectory, "databases", "agentops-router.db"),
                required: false),
            Database(
                "execution-feedback",
                Path.Combine(RequireLegacyRoot(legacy.AgentOpsUserRoot), "feedback.db"),
                Path.Combine(target.UserExecutionDirectory, "feedback", "feedback.db"),
                required: false),
            File(
                "execution-root-verifier",
                Path.Combine(RequireLegacyRoot(legacy.AgentOpsServiceRoot), "root.verifier"),
                Path.Combine(target.ServiceStateDirectory, "policy", "root.verifier")),
            File(
                "execution-workspaces",
                Path.Combine(RequireLegacyRoot(legacy.AgentOpsUserRoot), "workspaces.json"),
                Path.Combine(target.UserExecutionDirectory, "workspaces.json")),
            File(
                "execution-tools",
                Path.Combine(RequireLegacyRoot(legacy.AgentOpsUserRoot), "tools.json"),
                Path.Combine(target.UserExecutionDirectory, "tools.json")),
            File(
                "execution-recipes",
                Path.Combine(RequireLegacyRoot(legacy.AgentOpsUserRoot), "recipes.json"),
                Path.Combine(target.UserExecutionDirectory, "recipes.json"))
        };

        foreach (var item in items)
        {
            CortexProductLayout.ForIsolatedRoot(target.InstallRoot, target.OwnerSid)
                .RequireRuntimePathsWithinInstallRoot();
            RequireDestination(target.DataRoot, item.DestinationPath);
        }

        return new CortexStateMigrationPlanV1(
            ContractId,
            1,
            target.OwnerSid,
            target.DataRoot,
            items);
    }

    private static CortexStateMigrationItemV1 Database(
        string role,
        string source,
        string destination,
        bool required) =>
        new(
            role,
            Path.GetFullPath(source),
            Path.GetFullPath(destination),
            CortexStateMigrationStrategy.SqliteOnlineBackup,
            required);

    private static CortexStateMigrationItemV1 File(
        string role,
        string source,
        string destination) =>
        new(
            role,
            Path.GetFullPath(source),
            Path.GetFullPath(destination),
            CortexStateMigrationStrategy.ExactFileCopy,
            Required: false);

    private static string RequireLegacyRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical local legacy state root is required.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static void RequireDestination(string targetRoot, string destination)
    {
        var relative = Path.GetRelativePath(targetRoot, destination);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A state migration destination escaped the unified Cortex root.");
        }
    }
}
