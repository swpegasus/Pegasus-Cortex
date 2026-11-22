using System.Text;
using Pegasus.AgentOps.Engine;

internal static class ToolInvocationContextTests
{
    private const string UserSid = "S-1-5-21-101-202-303-1001";
    private static int _checks;

    public static async Task RunAsync()
    {
        _checks = 0;
        await RefusedProbeContextsAsync();
        await OrdinaryProbeAndExecutionAsync();
        await ContextRecheckedForEachInvocationAsync();
        await ExistingModeAndCancellationRulesAsync();
        await HeaderRequestsRefusedBeforeCallbacksAsync();
        await UnrelatedHeaderArgumentsRemainAllowedAsync();
        await CapturedEnvironmentMarkerWinsAsync();
        Console.WriteLine($"PASS tool invocation context: {_checks} injected primary-identity, credential-header fence and captured-environment checks; no child, token or credential execution");
    }

    private static async Task RefusedProbeContextsAsync()
    {
        var identities = new[]
        {
            new LocalPeer("S-1-5-18", 0, Elevated: true),
            new LocalPeer("S-1-5-18", 7, Elevated: false),
            new LocalPeer("S-1-5-19", 0),
            new LocalPeer("S-1-5-19", 7),
            new LocalPeer("S-1-5-20", 7),
            new LocalPeer(UserSid, 0),
            new LocalPeer(UserSid, -1),
            new LocalPeer(UserSid, 7, Elevated: true),
            new LocalPeer("", 7)
        };
        foreach (var identity in identities)
        {
            using var fixture = new Fixture(() => identity);
            await RefusedAsync("user_dispatch_required", () => fixture.Operations.Tools.RegisterAsync(fixture.Definition));
            Check(fixture.ProcessCalls == 0, "nonordinary probe reached the process callback");
            Check(fixture.IdentityCalls == 1, "probe did not sample its actual execution context");
            Check(fixture.Operations.Tools.List().Count == 0, "refused probe published a tool registration");
        }

        using (var fixture = new Fixture(() => throw new UnauthorizedAccessException("fixture-token-unavailable")))
        {
            await RefusedAsync("user_dispatch_required", () => fixture.Operations.Tools.RegisterAsync(fixture.Definition));
            Check(fixture.ProcessCalls == 0 && fixture.Operations.Tools.List().Count == 0,
                "unavailable identity dispatched or published a registration");
        }
        using (var fixture = new Fixture(() => null!))
        {
            await RefusedAsync("user_dispatch_required", () => fixture.Operations.Tools.RegisterAsync(fixture.Definition));
            Check(fixture.ProcessCalls == 0 && fixture.Operations.Tools.List().Count == 0,
                "missing identity dispatched or published a registration");
        }
    }

    private static async Task OrdinaryProbeAndExecutionAsync()
    {
        using var fixture = new Fixture(() => new(UserSid, 7));
        var admitted = await fixture.Operations.Tools.RegisterAsync(fixture.Definition);
        Check(admitted.ProbeVerified && fixture.ProcessCalls == 1, "ordinary version probe was refused or replayed");
        Check(fixture.LastInvocation is { Mode: ToolExecutionMode.User, Window: ToolWindowMode.Hidden } probe &&
            probe.Arguments.SequenceEqual(["--version"]), "version probe changed its execution mode, visibility or argv");
        Check(fixture.Operations.Tools.Get(fixture.Definition.Name).ProbeVerified, "successful probe did not publish its result");

        var arguments = new[] { "", "two words", "quote\"value", "trailing\\", "λ字符", "a;&b" };
        var invocation = fixture.Operations.PrepareTool(fixture.Definition.Name, arguments);
        var result = await fixture.Operations.RunAsync(invocation);
        Check(result.ExitCode == 0 && fixture.ProcessCalls == 2, "ordinary invocation failed or replayed");
        CheckInvocationPreserved(invocation, fixture.LastInvocation!);
        Check(fixture.LastInvocation!.Arguments.SequenceEqual(arguments), "captured marker changed the argument vector");
        CheckMarker(fixture.LastInvocation);
        Check(!invocation.Environment.Entries.Any(item => item.Key.Equals(
            CapturedCredentialOutputFence.EnvironmentVariable, StringComparison.OrdinalIgnoreCase)),
            "captured marker mutated the caller's admitted environment");
        Check(fixture.IdentityCalls == 2, "execution reused an earlier probe identity observation");

        // Failures after process admission retain their own evidence, rather than
        // becoming misleading context failures or causing another invocation.
        fixture.ProcessError = new IOException("captured-process-failure");
        try
        {
            await fixture.Operations.RunAsync(invocation);
            throw new InvalidOperationException("process callback failure was hidden");
        }
        catch (IOException error)
        {
            Check(ReferenceEquals(error, fixture.ProcessError) && fixture.ProcessCalls == 3,
                "process failure was replaced or replayed");
        }
    }

    private static async Task ContextRecheckedForEachInvocationAsync()
    {
        var current = new LocalPeer(UserSid, 7);
        using var fixture = new Fixture(() => current);
        await fixture.Operations.Tools.RegisterAsync(fixture.Definition);
        var invocation = fixture.Operations.PrepareTool(fixture.Definition.Name, ["inspect"]);
        current = new("S-1-5-18", 0, Elevated: true);
        await RefusedAsync("user_dispatch_required", () => fixture.Operations.RunAsync(invocation));
        Check(fixture.ProcessCalls == 1 && fixture.IdentityCalls == 2,
            "User execution under SYSTEM reused successful registration admission");
        current = new(UserSid, 7);
        await fixture.Operations.RunAsync(invocation);
        Check(fixture.ProcessCalls == 2 && fixture.IdentityCalls == 3,
            "a fresh ordinary invocation could not use the existing immutable registration");
    }

    private static async Task ExistingModeAndCancellationRulesAsync()
    {
        using var fixture = new Fixture(() => new(UserSid, 7));
        await fixture.Operations.Tools.RegisterAsync(fixture.Definition);
        var invocation = fixture.Operations.PrepareTool(fixture.Definition.Name, []);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await fixture.Runner.RunAsync(invocation, canceled.Token);
            throw new InvalidOperationException("canceled invocation reached admission");
        }
        catch (OperationCanceledException)
        {
            Check(fixture.IdentityCalls == 1 && fixture.ProcessCalls == 1,
                "canceled invocation queried identity or dispatched a process");
        }
        await RefusedAsync("service_unavailable", () => fixture.Runner.RunAsync(
            fixture.RawInvocation(ToolExecutionMode.System), CancellationToken.None));
        await RefusedAsync("user_dispatch_required", () => fixture.Runner.RunAsync(
            fixture.RawInvocation(ToolExecutionMode.User, ToolWindowMode.Visible), CancellationToken.None));
        Check(fixture.IdentityCalls == 1 && fixture.ProcessCalls == 1,
            "System or visible classification used the default user process callback");
    }

    private static async Task HeaderRequestsRefusedBeforeCallbacksAsync()
    {
        var cases = new (string Executable, string[] Arguments)[]
        {
            ("PegasusCortex.exe", ["mcp", "headers", "--connection", "fixture-connection"]),
            ("PEGASUSCORTEX.EXE", ["mcp", "headers", "--connection", "fixture-connection"]),
            ("dotnet.exe", [@"C:\Fixture\PegasusCortex.dll", "mcp", "headers", "--connection", "fixture-connection"]),
            ("dotnet.exe", ["exec", @"C:\Fixture\PegasusCortex.dll", "mcp", "headers", "--connection", "fixture-connection"]),
            ("DOTNET.EXE", ["exec", "--runtimeconfig", @"C:\Fixture\runtime config.json", "--depsfile=fixture.deps.json",
                "--roll-forward", "LatestPatch", @"C:\Fixture\PEGASUSCORTEX.DLL", "mcp", "headers", "--connection", "fixture-connection"]),
            ("dotnet.exe", ["--additionalprobingpath", @"C:\Fixture\packages", @"C:\Fixture\PegasusCortex.dll",
                "mcp", "headers", "--connection", "fixture-connection"])
        };
        foreach (var test in cases)
        {
            using var fixture = new Fixture(() => new(UserSid, 7), test.Executable);
            await fixture.Operations.Tools.RegisterAsync(fixture.Definition);
            CheckMarker(fixture.LastInvocation!);
            var invocation = fixture.Operations.PrepareTool(fixture.Definition.Name, test.Arguments);
            await RefusedAsync(CapturedCredentialOutputFence.RefusalCode, () => fixture.Operations.RunAsync(invocation));
            Check(fixture.IdentityCalls == 1 && fixture.ProcessCalls == 1,
                "credential-header helper reached identity or captured-process callback");
        }
        // Registry probes already permit only fixed version arguments; keep that
        // earlier admission rule as well as the execution-time output fence.
        foreach (var test in cases.Take(3))
        {
            using var fixture = new Fixture(() => new(UserSid, 7), test.Executable);
            var definition = fixture.Definition with { VersionProbe = new(test.Arguments, [0]) };
            await RefusedAsync("precondition",
                () => fixture.Operations.Tools.RegisterAsync(definition));
            Check(fixture.IdentityCalls == 0 && fixture.ProcessCalls == 0 && fixture.Operations.Tools.List().Count == 0,
                "credential-header version probe dispatched or published a registration");
        }
    }

    private static async Task UnrelatedHeaderArgumentsRemainAllowedAsync()
    {
        var cases = new (string Executable, string[] Arguments)[]
        {
            ("inert-user-tool.exe", ["mcp", "headers", "--connection", "fixture-connection"]),
            ("PegasusCortex.exe", ["--version"]),
            ("PegasusCortex.exe", ["mcp", "enroll", "--name", "fixture"]),
            ("PegasusCortex.exe", ["mcp", "headers", "--different-option", "fixture"]),
            ("PegasusCortex.exe", ["mcp", "headers", "--connection", "fixture", "extra"]),
            ("dotnet.exe", [@"C:\Fixture\OtherApplication.dll", "mcp", "headers", "--connection", "fixture"]),
            ("dotnet.exe", [@"C:\Fixture\OtherApplication.dll", @"C:\Fixture\PegasusCortex.dll",
                "mcp", "headers", "--connection", "fixture"]),
            ("dotnet.exe", ["exec", @"C:\Fixture\PegasusCortex.dll", "mcp", "enroll", "--name", "fixture"])
        };
        foreach (var test in cases)
        {
            using var fixture = new Fixture(() => new(UserSid, 7), test.Executable);
            await fixture.Operations.Tools.RegisterAsync(fixture.Definition);
            var invocation = fixture.Operations.PrepareTool(fixture.Definition.Name, test.Arguments);
            await fixture.Operations.RunAsync(invocation);
            Check(fixture.IdentityCalls == 2 && fixture.ProcessCalls == 2,
                "unrelated arguments were refused or replayed by the credential-output fence");
            CheckInvocationPreserved(invocation, fixture.LastInvocation!);
            CheckMarker(fixture.LastInvocation!);
        }
    }

    private static async Task CapturedEnvironmentMarkerWinsAsync()
    {
        foreach (var mode in new[] { ToolEnvironmentMode.Replace, ToolEnvironmentMode.InheritWithOverlay })
        foreach (var suppliedMarker in new string?[] { "0", null, "caller-value" })
        {
            using var fixture = new Fixture(() => new(UserSid, 7), environmentMode: mode,
                environment: new Dictionary<string, string?>
                {
                    ["FIXTURE_TOOL_CONTEXT"] = "fixed",
                    ["pegasus_agentops_captured"] = suppliedMarker
                });
            await fixture.Operations.Tools.RegisterAsync(fixture.Definition);
            CheckMarker(fixture.LastInvocation!);
            var original = fixture.Operations.PrepareTool(fixture.Definition.Name, ["inspect"]);
            var originalEnvironment = original.Environment.Entries.ToArray();
            foreach (var output in new[] { ToolOutputBehavior.Capture, ToolOutputBehavior.Discard, ToolOutputBehavior.StatusOnly })
            {
                var invocation = original.WithRecipe(original.Timeout, original.ExpectedExitCodes,
                    original.MaxOutputBytes, output, null, "fixture-marker-policy");
                await fixture.Operations.RunAsync(invocation);
                CheckInvocationPreserved(invocation, fixture.LastInvocation!);
                CheckMarker(fixture.LastInvocation!);
                Check(fixture.LastInvocation!.Environment.Entries.Single(item => item.Key == "FIXTURE_TOOL_CONTEXT").Value == "fixed",
                    "captured marker discarded the final tool environment");
                Check(invocation.Environment.Entries.SequenceEqual(originalEnvironment),
                    "captured marker rewrote the source invocation environment");
            }
            Check(fixture.ProcessCalls == 4, "captured marker caused an extra process callback");
        }
    }

    private static void CheckMarker(ToolInvocation invocation)
    {
        var markers = invocation.Environment.Entries.Where(item => item.Key.Equals(
            CapturedCredentialOutputFence.EnvironmentVariable, StringComparison.OrdinalIgnoreCase)).ToArray();
        Check(markers is [{ Key: CapturedCredentialOutputFence.EnvironmentVariable,
            Value: CapturedCredentialOutputFence.EnvironmentValue }],
            "captured child environment did not receive the single fixed marker");
    }

    private static void CheckInvocationPreserved(ToolInvocation expected, ToolInvocation actual)
    {
        Check(expected.AdmissionOwner == actual.AdmissionOwner && expected.ToolName == actual.ToolName &&
            expected.ExecutablePath == actual.ExecutablePath && expected.Arguments.SequenceEqual(actual.Arguments) &&
            expected.WorkingDirectory == actual.WorkingDirectory && expected.Timeout == actual.Timeout &&
            expected.ExpectedExitCodes.SequenceEqual(actual.ExpectedExitCodes) && expected.Mode == actual.Mode &&
            expected.Window == actual.Window && expected.StdinEncoding == actual.StdinEncoding &&
            expected.StdoutEncoding == actual.StdoutEncoding && expected.StderrEncoding == actual.StderrEncoding &&
            expected.MaxOutputBytes == actual.MaxOutputBytes && expected.FileAdmissions.SequenceEqual(actual.FileAdmissions) &&
            expected.OutputBehavior == actual.OutputBehavior && expected.Postcondition == actual.Postcondition &&
            expected.RecipeName == actual.RecipeName, "captured marker changed admitted invocation properties");
    }

    private static async Task RefusedAsync(string code, Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("expected invocation refusal"); }
        catch (AgentOpsException error) { Check(error.Code == code, "unexpected invocation refusal code"); }
    }

    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempRoot _temp = new();
        internal readonly AgentOpsInvocationRunner Runner;
        internal readonly ToolRecipeOperations Operations;
        internal readonly RegisteredToolDefinition Definition;
        internal int IdentityCalls, ProcessCalls;
        internal ToolInvocation? LastInvocation;
        internal Exception? ProcessError;

        internal Fixture(Func<LocalPeer> identity, string executableName = "inert-user-tool.exe",
            ToolEnvironmentMode environmentMode = ToolEnvironmentMode.Replace,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var executable = Path.Combine(_temp.Path, executableName);
            File.WriteAllText(executable, "inert-fixture-image", new UTF8Encoding(false));
            Runner = new(() => { IdentityCalls++; return identity(); }, (invocation, _) =>
            {
                ProcessCalls++;
                LastInvocation = invocation;
                if (ProcessError is { } error) return Task.FromException<ProcessResult>(error);
                return Task.FromResult(new ProcessResult(0, "fixture-tool 1.0", "", false, false, false));
            });
            Operations = new(_temp.Dir("registry"), Runner);
            Definition = new("fixture-user-tool", 1, executable,
                WorkingDirectoryRule: ToolWorkingDirectoryRule.ExecutableDirectory,
                ExpectedExitCodes: [0], MaxArguments: 16, MaxArgumentUtf8Bytes: 1024,
                MaxTotalArgumentUtf8Bytes: 8192, VersionProbe: new(["--version"], [0], "fixture-tool"),
                EnvironmentMode: environmentMode,
                Environment: environment ?? new Dictionary<string, string?> { ["FIXTURE_TOOL_CONTEXT"] = "fixed" });
        }

        internal ToolInvocation RawInvocation(ToolExecutionMode mode, ToolWindowMode window = ToolWindowMode.Hidden) =>
            new(Guid.Empty, Definition.Name, Definition.ExecutablePath, [], _temp.Path, TimeSpan.FromSeconds(5), [0],
                mode, window, "none", "utf8", "utf8", 1024, ProcessEnvironment.Replace(new Dictionary<string, string?>()), []);

        public void Dispose() => _temp.Dispose();
    }
}
