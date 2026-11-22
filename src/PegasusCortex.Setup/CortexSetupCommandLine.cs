namespace Pegasus.Cortex.Setup;

internal enum CortexCodexFeatureAction
{
    Keep,
    Install,
    Remove
}

internal enum CortexCodexFeature
{
    Registration,
    Guidance
}

internal sealed record CortexInstallPackageOptions(
    string PackageRoot,
    string? InstallRoot,
    string? CodexAgentsPath,
    string? DataRoot = null,
    bool? CodexRegister = null,
    bool? CodexGuidance = null,
    string? CodexHome = null,
    CortexCodexFeatureAction? CodexRegisterAction = null,
    CortexCodexFeatureAction? CodexGuidanceAction = null);

internal sealed record CortexCodexFeatureOptions(
    string PackageRoot,
    string InstallRoot,
    CortexCodexFeature Feature,
    CortexCodexFeatureAction Action,
    string? CodexHome,
    string? CodexAgentsPath);

internal static class CortexSetupCommandLine
{
    public static CortexInstallPackageOptions ParseInstallPackage(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], "install-package", StringComparison.Ordinal))
            throw new ArgumentException("The install-package command is incomplete.");
        if (string.IsNullOrWhiteSpace(args[1]))
            throw new ArgumentException("The install-package root is blank.");
        string? installRoot = null;
        string? agentsPath = null;
        string? dataRoot = null;
        string? codexHome = null;
        bool? codexRegister = null;
        bool? codexGuidance = null;
        CortexCodexFeatureAction? codexRegisterAction = null;
        CortexCodexFeatureAction? codexGuidanceAction = null;
        var index = 2;
        while (index < args.Length)
        {
            var name = args[index++];
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A setup argument is blank.");
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                if (agentsPath is not null || index != args.Length)
                    throw new ArgumentException("The legacy Codex AGENTS path must be the final single argument.");
                agentsPath = name;
                continue;
            }
            if (index >= args.Length)
                throw new ArgumentException($"The setup option '{name}' has no value.");
            var value = args[index++];
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"The setup option '{name}' has a blank value.");
            switch (name)
            {
                case "--install-root" when installRoot is null:
                    installRoot = value;
                    break;
                case "--codex-agents" when agentsPath is null:
                    agentsPath = value;
                    break;
                case "--data-root" when dataRoot is null:
                    dataRoot = value;
                    break;
                case "--codex-register" when codexRegister is null:
                    codexRegister = ParseBoolean(name, value);
                    break;
                case "--codex-guidance" when codexGuidance is null:
                    codexGuidance = ParseBoolean(name, value);
                    break;
                case "--codex-home" when codexHome is null:
                    codexHome = value;
                    break;
                case "--codex-register-action" when codexRegisterAction is null:
                    codexRegisterAction = ParseAction(name, value);
                    break;
                case "--codex-guidance-action" when codexGuidanceAction is null:
                    codexGuidanceAction = ParseAction(name, value);
                    break;
                default:
                    throw new ArgumentException($"The setup option '{name}' is unknown or repeated.");
            }
        }
        if (codexRegister is not null && codexRegisterAction is not null ||
            codexGuidance is not null && codexGuidanceAction is not null)
            throw new ArgumentException("A Codex feature cannot use both legacy selection and maintenance action options.");
        return new CortexInstallPackageOptions(args[1], installRoot, agentsPath, dataRoot, codexRegister, codexGuidance,
            codexHome, codexRegisterAction, codexGuidanceAction);
    }

    public static CortexCodexFeatureOptions ParseCodexFeature(string[] args)
    {
        if (args.Length < 8 || !string.Equals(args[0], "configure-codex-feature", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[1]))
            throw new ArgumentException("The configure-codex-feature command is incomplete.");
        string? installRoot = null;
        string? home = null;
        string? agents = null;
        CortexCodexFeature? feature = null;
        CortexCodexFeatureAction? action = null;
        var index = 2;
        while (index < args.Length)
        {
            var name = args[index++];
            if (index >= args.Length)
                throw new ArgumentException($"The setup option '{name}' has no value.");
            var value = args[index++];
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"The setup option '{name}' has a blank value.");
            switch (name)
            {
                case "--install-root" when installRoot is null:
                    installRoot = value;
                    break;
                case "--feature" when feature is null:
                    feature = value switch
                    {
                        "registration" => CortexCodexFeature.Registration,
                        "guidance" => CortexCodexFeature.Guidance,
                        _ => throw new ArgumentException("The Codex feature requires exactly registration or guidance.")
                    };
                    break;
                case "--action" when action is null:
                    action = ParseAction(name, value);
                    if (action == CortexCodexFeatureAction.Keep)
                        throw new ArgumentException("A standalone Codex feature routine requires install or remove.");
                    break;
                case "--codex-home" when home is null:
                    home = value;
                    break;
                case "--codex-agents" when agents is null:
                    agents = value;
                    break;
                default:
                    throw new ArgumentException($"The setup option '{name}' is unknown or repeated.");
            }
        }
        return new CortexCodexFeatureOptions(
            args[1],
            installRoot ?? throw new ArgumentException("The configure-codex-feature command requires --install-root."),
            feature ?? throw new ArgumentException("The configure-codex-feature command requires --feature."),
            action ?? throw new ArgumentException("The configure-codex-feature command requires --action."),
            home,
            agents);
    }

    private static bool ParseBoolean(string option, string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new ArgumentException($"The setup option '{option}' requires exactly true or false.")
    };

    private static CortexCodexFeatureAction ParseAction(string option, string value) => value switch
    {
        "keep" => CortexCodexFeatureAction.Keep,
        "install" => CortexCodexFeatureAction.Install,
        "remove" => CortexCodexFeatureAction.Remove,
        _ => throw new ArgumentException($"The setup option '{option}' requires exactly keep, install, or remove.")
    };
}
