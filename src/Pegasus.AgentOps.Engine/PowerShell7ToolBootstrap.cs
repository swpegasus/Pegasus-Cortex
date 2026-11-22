using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Pegasus.AgentOps.Engine;

[SupportedOSPlatform("windows")]
public static class PowerShell7ToolBootstrap
{
    private const string InstalledVersionsKey = @"SOFTWARE\Microsoft\PowerShellCore\InstalledVersions";

    public static async Task<RegisteredToolDefinition> EnsureAsync(string stateRoot, CancellationToken cancellationToken = default)
    {
        var operations = new ToolRecipeOperations(stateRoot);
        return await EnsureAsync(operations.Tools, ResolveExecutable, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<RegisteredToolDefinition> EnsureAsync(RegisteredToolRegistry tools,
        Func<string> resolveExecutable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RegisteredToolDefinition? existing;
        try { existing = tools.Get("pwsh"); }
        catch (AgentOpsException ex) when (ex.Code == "not_found") { existing = null; }

        if (existing?.UsesInstalledPowerShell == true)
        {
            var resolved = tools.ResolveInstalledPowerShell(existing);
            var environment = BootstrapEnvironment(resolved.ExecutablePath, resolved.Environment);
            if (EnvironmentMatches(resolved.Environment, environment)) return resolved;
            return await tools.RegisterInstalledPowerShellAsync(resolved with
            {
                DefinitionVersion = checked(resolved.DefinitionVersion + 1),
                Environment = environment
            }, cancellationToken).ConfigureAwait(false);
        }

        // The original bootstrap had no ownership marker. Adopt only its exact
        // v1 policy at the current machine-installed location; changed policies
        // and portable/custom registrations retain their explicit pinning.
        if (existing is not null && !IsLegacyBootstrapPolicy(existing))
            return existing;

        string executable;
        try { executable = resolveExecutable(); }
        catch (AgentOpsException ex) when (existing is not null && ex.Code == "precondition")
        {
            return existing;
        }
        if (existing is not null &&
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(existing.ExecutablePath), Path.GetFullPath(executable)))
            return existing;

        var definition = existing is null
            ? CreateDefinition(executable)
            : existing with
            {
                DefinitionVersion = existing.DefinitionVersion + 1,
                Environment = BootstrapEnvironment(executable, existing.Environment)
            };
        return await tools.RegisterInstalledPowerShellAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    private static RegisteredToolDefinition CreateDefinition(string executable)
    {
        return new RegisteredToolDefinition(
            "pwsh",
            1,
            executable,
            WorkingDirectoryRule: ToolWorkingDirectoryRule.Caller,
            TimeoutSeconds: 3600,
            ExpectedExitCodes: [0],
            MaxArguments: 256,
            EnvironmentMode: ToolEnvironmentMode.Replace,
            Environment: BootstrapEnvironment(executable),
            VersionProbe: new ToolProbeDefinition(["-version"], [0], "PowerShell 7."));
    }

    private static IReadOnlyDictionary<string, string?> BootstrapEnvironment(string executable,
        IReadOnlyDictionary<string, string?>? existing = null)
    {
        var environment = new Dictionary<string, string?>(AgentOpsEngine.MinimalToolEnvironment(executable),
            StringComparer.OrdinalIgnoreCase);
        // Older bootstrap records omitted OS profile defaults needed by child SDKs.
        // Preserve explicit values while filling only missing safe defaults.
        if (existing is not null)
            foreach (var pair in existing) environment[pair.Key] = pair.Value;
        return environment;
    }

    private static bool EnvironmentMatches(IReadOnlyDictionary<string, string?>? left,
        IReadOnlyDictionary<string, string?> right)
    {
        if (left is null || left.Count != right.Count) return false;
        foreach (var pair in right)
            if (!left.TryGetValue(pair.Key, out var value) || !StringComparer.Ordinal.Equals(value, pair.Value))
                return false;
        return true;
    }

    private static bool IsLegacyBootstrapPolicy(RegisteredToolDefinition value) =>
        value.DefinitionVersion == 1 && value.Mode == ToolExecutionMode.User &&
        value.WorkingDirectoryRule == ToolWorkingDirectoryRule.Caller && value.FixedWorkingDirectory is null &&
        value.TimeoutSeconds == 3600 && value.ExpectedExitCodes is [0] && value.MaxArguments == 256 &&
        value.MaxArgumentUtf8Bytes == 16 * 1024 && value.MaxTotalArgumentUtf8Bytes == 128 * 1024 &&
        value.StdinEncoding == "none" && value.StdoutEncoding == "utf8" && value.StderrEncoding == "utf8" &&
        value.Window == ToolWindowMode.Hidden && value.MaxOutputBytes == 64 * 1024 &&
        value.EnvironmentMode == ToolEnvironmentMode.Replace && value.ProbeVerified &&
        value.VersionProbe is { Arguments: ["-version"], ExpectedExitCodes: [0], RequiredText: "PowerShell 7." } &&
        value.ExecutableSha256 is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(value.ExecutableSha256, value.AdmissionSha256);

    public static string ResolveExecutable()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var versions = machine.OpenSubKey(InstalledVersionsKey, writable: false)
                ?? throw new AgentOpsException("precondition");
            var candidates = new List<(Version Version, string Executable)>();
            foreach (var name in versions.GetSubKeyNames())
            {
                using var key = versions.OpenSubKey(name, writable: false);
                if (key?.GetValue("SemanticVersion", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string rawVersion ||
                    key.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string rawLocation ||
                    !Version.TryParse(rawVersion, out var version) || version.Major < 7 ||
                    string.IsNullOrWhiteSpace(rawLocation) || !Path.IsPathFullyQualified(rawLocation))
                    continue;
                var executable = Path.Combine(Path.GetFullPath(rawLocation), "pwsh.exe");
                if (!File.Exists(executable)) continue;
                PathNormalizer.RejectAbsoluteReparseComponents(executable);
                if (FileVersionInfo.GetVersionInfo(executable).FileMajorPart < 7) continue;
                candidates.Add((version, executable));
            }
            return candidates.OrderByDescending(candidate => candidate.Version).Select(candidate => candidate.Executable).FirstOrDefault()
                ?? throw new AgentOpsException("precondition");
        }
        catch (AgentOpsException) { throw; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException)
        {
            throw new AgentOpsException("precondition", inner: ex);
        }
    }
}
