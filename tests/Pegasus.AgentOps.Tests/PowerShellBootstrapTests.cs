using Pegasus.AgentOps.Engine;

internal static class PowerShellBootstrapTests
{
    // These are isolated registry/probe fixtures, not installed PowerShell or
    // machine-registration acceptance. No fake executable is ever launched.
    internal static async Task RunAsync()
    {
        using var temp = new TempRoot();
        await FreshAndServicedAsync(temp);
        await InstalledEnvironmentUpgradeAsync(temp);
        await LegacyAdoptionAsync(temp);
        await CustomPreservationAsync(temp);
        await FailedProbePreservesStoreAsync(temp);
    }

    private static async Task InstalledEnvironmentUpgradeAsync(TempRoot temp)
    {
        var executable = FixtureExecutable(temp, "environment-upgrade-installed");
        var runner = new ProbeRunner();
        var tools = new RegisteredToolRegistry(temp.Dir("environment-upgrade-state"), runner, () => executable);
        var current = await PowerShell7ToolBootstrap.EnsureAsync(tools, () => executable);
        var reduced = new Dictionary<string, string?>(current.Environment!, StringComparer.OrdinalIgnoreCase);
        reduced.Remove("ComSpec");
        var stale = await tools.RegisterInstalledPowerShellAsync(current with
        {
            DefinitionVersion = current.DefinitionVersion + 1,
            Environment = reduced
        });
        Check(!stale.Environment!.ContainsKey("ComSpec"), "The stale fixture retained ComSpec.");
        var upgraded = await PowerShell7ToolBootstrap.EnsureAsync(tools, () => executable);
        var expected = Path.GetFullPath(Environment.GetEnvironmentVariable("ComSpec")!);
        Check(upgraded.DefinitionVersion == stale.DefinitionVersion + 1 &&
            upgraded.Environment!.TryGetValue("ComSpec", out var actual) && actual == expected,
            "Installed PowerShell bootstrap did not restore the required Windows command processor.");
        var bytes = File.ReadAllBytes(tools.StorePath);
        var count = runner.Count;
        var repeated = await PowerShell7ToolBootstrap.EnsureAsync(tools, () => executable);
        Check(repeated.DefinitionVersion == upgraded.DefinitionVersion && runner.Count == count &&
            bytes.SequenceEqual(File.ReadAllBytes(tools.StorePath)),
            "The repaired installed PowerShell environment was not idempotent.");
    }

    private static async Task FreshAndServicedAsync(TempRoot temp)
    {
        var executable = FixtureExecutable(temp, "installed-one");
        var current = executable;
        var runner = new ProbeRunner();
        var operations = new ToolRecipeOperations(temp.Dir("fresh-state"), runner, () => current);
        var first = await PowerShell7ToolBootstrap.EnsureAsync(operations.Tools, () => current);
        Check(first.UsesInstalledPowerShell && first.ExecutableSha256 is null && first.AdmissionSha256 is null &&
            first.ProbeVerified && first.DefinitionVersion == 1 && first.ExecutablePath == executable,
            "Fresh installed PowerShell was not registered with the shared servicing policy.");
        Check(runner.Count == 1 && runner.Last?.Arguments.SequenceEqual(["-version"]) == true,
            "Fresh registration did not perform one bounded version probe.");
        var original = File.ReadAllBytes(operations.Tools.StorePath);
        var repeated = await PowerShell7ToolBootstrap.EnsureAsync(operations.Tools, () => current);
        Check(repeated.DefinitionVersion == 1 && runner.Count == 1 &&
            original.SequenceEqual(File.ReadAllBytes(operations.Tools.StorePath)), "Unchanged bootstrap rewrote or reprobed the tool.");

        File.AppendAllText(executable, "serviced bytes");
        var samePath = operations.PrepareTool("pwsh", ["-version"], cwd: temp.Path);
        Check(samePath.ExecutablePath == executable, "In-place installed servicing retained a stale executable pin.");
        current = FixtureExecutable(temp, "installed-two");
        File.Delete(executable);
        var resolved = await PowerShell7ToolBootstrap.EnsureAsync(operations.Tools, () => current);
        var invocation = operations.PrepareTool("pwsh", ["-version"], cwd: temp.Path);
        Check(resolved.ExecutablePath == current && invocation.ExecutablePath == current && runner.Count == 1 &&
            original.SequenceEqual(File.ReadAllBytes(operations.Tools.StorePath)),
            "Current installed path was not selected without registration churn after the former path disappeared.");
        Check(operations.Tools.List().Single().ExecutablePath == current, "Tool listing exposed the obsolete installed path.");
    }

    private static async Task LegacyAdoptionAsync(TempRoot temp)
    {
        var executable = CopyInstalledPowerShell(temp, "legacy-installed");
        var runner = new ProbeRunner();
        var tools = new RegisteredToolRegistry(temp.Dir("legacy-state"), runner, () => executable);
        var old = await tools.RegisterAsync(LegacyDefinition(executable));
        File.AppendAllText(executable, "serviced bytes");
        var adopted = await PowerShell7ToolBootstrap.EnsureAsync(tools, () => executable);
        Check(adopted.UsesInstalledPowerShell && adopted.DefinitionVersion == 2 && adopted.ExecutableSha256 is null &&
            adopted.AdmissionSha256 is null && adopted.ProbeVerified && runner.Count == 2,
            "Exact legacy bootstrap was not adopted after installed bytes changed.");
        Check(old.Environment!.All(pair => adopted.Environment!.TryGetValue(pair.Key, out var value) && value == pair.Value) &&
            AgentOpsEngine.MinimalToolEnvironment(executable).All(pair =>
                adopted.Environment!.TryGetValue(pair.Key, out var value) &&
                value == (old.Environment!.TryGetValue(pair.Key, out var original) ? original : pair.Value)) &&
            adopted.MaxArguments == old.MaxArguments && adopted.TimeoutSeconds == old.TimeoutSeconds &&
            adopted.MaxOutputBytes == old.MaxOutputBytes, "Adoption lost explicit environment values, safe defaults, or execution limits.");
        var after = File.ReadAllBytes(tools.StorePath);
        await PowerShell7ToolBootstrap.EnsureAsync(tools, () => executable);
        Check(runner.Count == 2 && after.SequenceEqual(File.ReadAllBytes(tools.StorePath)), "Legacy adoption was not idempotent.");
    }

    private static async Task CustomPreservationAsync(TempRoot temp)
    {
        var installed = CopyInstalledPowerShell(temp, "custom-installed");
        var portable = CopyInstalledPowerShell(temp, "portable");
        var cases = new[]
        {
            LegacyDefinition(portable),
            LegacyDefinition(installed) with { DefinitionVersion = 2 },
            LegacyDefinition(installed) with { MaxArguments = 42 },
            LegacyDefinition(installed) with { TimeoutSeconds = 120 },
            LegacyDefinition(installed) with { ExecutableSha256 = null }
        };
        for (var index = 0; index < cases.Length; index++)
        {
            var runner = new ProbeRunner();
            var tools = new RegisteredToolRegistry(temp.Dir("custom-state-" + index), runner, () => installed);
            var original = await tools.RegisterAsync(cases[index]);
            var before = File.ReadAllBytes(tools.StorePath);
            var preserved = await PowerShell7ToolBootstrap.EnsureAsync(tools, () => installed);
            Check(!preserved.UsesInstalledPowerShell && preserved.DefinitionVersion == original.DefinitionVersion &&
                preserved.ExecutablePath == original.ExecutablePath && runner.Count == 1 &&
                before.SequenceEqual(File.ReadAllBytes(tools.StorePath)), "A custom PowerShell registration was adopted or rewritten.");
        }

        var unavailableRunner = new ProbeRunner();
        var portableTools = new RegisteredToolRegistry(temp.Dir("portable-absent-install"), unavailableRunner,
            () => throw new AgentOpsException("precondition"));
        await portableTools.RegisterAsync(LegacyDefinition(portable));
        var kept = await PowerShell7ToolBootstrap.EnsureAsync(portableTools, () => throw new AgentOpsException("precondition"));
        Check(!kept.UsesInstalledPowerShell && kept.ExecutablePath == portable && unavailableRunner.Count == 1,
            "Unavailable installed PowerShell displaced a portable registration.");

        // The generic registry must not let public registration opt into the
        // internal installed-PowerShell marker, even when called with a clone.
        var publicTools = new RegisteredToolRegistry(temp.Dir("public-marker"), new ProbeRunner(), () => installed);
        var supplied = LegacyDefinition(installed) with { UsesInstalledPowerShell = true };
        var pinned = await publicTools.RegisterAsync(supplied);
        Check(!pinned.UsesInstalledPowerShell && pinned.AdmissionSha256 is not null,
            "Public registration bypassed arbitrary-tool executable integrity.");
    }

    private static async Task FailedProbePreservesStoreAsync(TempRoot temp)
    {
        var executable = CopyInstalledPowerShell(temp, "failed-probe");
        var runner = new ProbeRunner();
        var tools = new RegisteredToolRegistry(temp.Dir("failure-state"), runner, () => executable);
        await tools.RegisterAsync(LegacyDefinition(executable));
        var before = File.ReadAllBytes(tools.StorePath);
        runner.Output = "unrelated runtime";
        try
        {
            await PowerShell7ToolBootstrap.EnsureAsync(tools, () => executable);
            throw new InvalidOperationException("Wrong version probe was accepted.");
        }
        catch (AgentOpsException ex) when (ex.Code == "precondition") { }
        Check(before.SequenceEqual(File.ReadAllBytes(tools.StorePath)) && !tools.Get("pwsh").UsesInstalledPowerShell,
            "Failed bootstrap adoption mutated the previous registration.");
    }

    private static RegisteredToolDefinition LegacyDefinition(string executable) => new(
        "pwsh", 1, executable, ToolRecipeValidation.ComputeFileHash(executable),
        WorkingDirectoryRule: ToolWorkingDirectoryRule.Caller, TimeoutSeconds: 3600,
        ExpectedExitCodes: [0], MaxArguments: 256,
        EnvironmentMode: ToolEnvironmentMode.Replace,
        Environment: new Dictionary<string, string?> { ["PAO_BOOTSTRAP_FIXTURE"] = "preserve-this-overlay" },
        VersionProbe: new ToolProbeDefinition(["-version"], [0], "PowerShell 7."));

    private static string FixtureExecutable(TempRoot temp, string name)
    {
        var path = Path.Combine(temp.Dir(name), "pwsh.exe");
        File.WriteAllText(path, "fixture bytes, never executed");
        return path;
    }

    private static string CopyInstalledPowerShell(TempRoot temp, string name)
    {
        var path = Path.Combine(temp.Dir(name), "pwsh.exe");
        File.Copy(PowerShell7ToolBootstrap.ResolveExecutable(), path);
        return path;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class ProbeRunner : IToolInvocationRunner
    {
        internal int Count { get; private set; }
        internal string Output { get; set; } = "PowerShell 7.6.0";
        internal ToolInvocation? Last { get; private set; }

        public Task<ProcessResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            Last = invocation;
            return Task.FromResult(new ProcessResult(0, Output, string.Empty, false, false, false));
        }
    }
}
