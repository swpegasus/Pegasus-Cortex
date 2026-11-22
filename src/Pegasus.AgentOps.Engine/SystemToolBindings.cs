using System.Text;

namespace Pegasus.AgentOps.Engine;

/// <summary>
/// Immutable tools admitted by the service composition root. Supplying this
/// object opts the direct System engine out of the mutable user tool registry
/// for the represented tools, including when a binding is unavailable.
/// </summary>
internal sealed record AgentOpsSystemToolBindings(RegisteredToolDefinition? SevenZip);

internal static class SystemSevenZipToolBinding
{
    public static async Task<RegisteredToolDefinition?> TryCreateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var executable = ResolveExecutable();
        if (executable is null) return null;

        var environment = new Dictionary<string, string?>(
            AgentOpsEngine.MinimalToolEnvironment(executable), StringComparer.OrdinalIgnoreCase);
        var before = ToolRecipeValidation.ComputeFileHash(executable);
        var runner = new WindowsSevenZipProcessRunner(ProcessEnvironment.Replace(environment));
        var probe = await runner.RunTextAsync(
            new SevenZipProcessRequest(executable, ["i"], 10, 64 * 1024, 64 * 1024),
            cancellationToken).ConfigureAwait(false);
        var probeText = Encoding.UTF8.GetString(probe.Stdout) + probe.Stderr;
        if (probe.TimedOut || probe.ExitCode != 0 || probe.StdoutTruncated || probe.StderrTruncated ||
            probe.StreamError is not null || !probeText.Contains("7-Zip", StringComparison.Ordinal))
            throw new AgentOpsException("precondition");
        var after = ToolRecipeValidation.ComputeFileHash(executable);
        ToolRecipeValidation.VerifyHashValue(after, before);

        return new RegisteredToolDefinition(
            "7z",
            1,
            executable,
            after,
            Mode: ToolExecutionMode.System,
            WorkingDirectoryRule: ToolWorkingDirectoryRule.Caller,
            TimeoutSeconds: 3600,
            ExpectedExitCodes: [0],
            MaxArguments: 64,
            EnvironmentMode: ToolEnvironmentMode.Replace,
            Environment: environment,
            VersionProbe: new ToolProbeDefinition(["i"], [0], "7-Zip"),
            ProbeVerified: true)
        {
            AdmissionSha256 = after
        };
    }

    private static string? ResolveExecutable()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var canonicalRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, "7-Zip", "7z.exe"));
            if (!File.Exists(candidate)) continue;
            if (!PathNormalizer.Contains(canonicalRoot, candidate)) throw new AgentOpsException("precondition");
            PathNormalizer.RejectAbsoluteReparseComponents(candidate);
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new AgentOpsException("precondition");
            return candidate;
        }
        return null;
    }
}
