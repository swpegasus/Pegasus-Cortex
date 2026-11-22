using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;

// Owned inert files, an injected ordinary owner, and a fake probe runner only.
// No task registration, process execution, live registry or credential access.
internal static class WindowsTaskToolResolverTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private static int _checks;

    internal static async Task RunAsync()
    {
        _checks = 0;
        await AdmissionAndPinsAsync();
        await DefinitionChangesAsync();
        await WorkingDirectoriesAsync();
        await LeaseAndOwnerAsync();
        Console.WriteLine($"PASS scheduled registered-tool resolution: {_checks} owned-file/fake-probe/owner checks; pins, explicit Scheduler environment, cwd/workspace and creation-time lease cleanup; no task or process execution");
    }

    private static async Task AdmissionAndPinsAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var vector = new[] { "", "two words", "quote\"here", "trailing\\", "a;b&c", "λ字符" };
        var request = new WindowsTaskToolRequest("fixture", vector, "scheduler");
        using (var prepared = fixture.Resolver.Prepare(request, default))
        {
            Check(prepared.Invocation.Arguments.SequenceEqual(vector) && prepared.Invocation.Mode == ToolExecutionMode.User,
                "resolver changed argv or execution owner");
            Check(prepared.Pin is { Tool: "fixture", DefinitionVersion: 1 } && IsSha(prepared.Pin.DefinitionSha), "safe pin was not returned");
            var pin = prepared.Pin;
            using var selected = fixture.Resolver.Prepare(request with { DefinitionVersion = pin.DefinitionVersion, DefinitionSha = pin.DefinitionSha }, default);
            Check(selected.Pin == pin && fixture.Runner.Calls == 1, "resolution executed/reprobed a tool or changed its pin");
            Refused("invalid", () => fixture.Resolver.Prepare(request with { DefinitionVersion = 1 }, default));
            Refused("invalid", () => fixture.Resolver.Prepare(request with { DefinitionSha = pin.DefinitionSha }, default));
            Refused("invalid", () => fixture.Resolver.Prepare(request with { DefinitionVersion = 0, DefinitionSha = pin.DefinitionSha }, default));
            Refused("invalid", () => fixture.Resolver.Prepare(request with { DefinitionVersion = 1, DefinitionSha = new string('A', 64) }, default));
            Refused("precondition", () => fixture.Resolver.Prepare(request with { DefinitionVersion = 2, DefinitionSha = pin.DefinitionSha }, default));
            Refused("precondition", () => fixture.Resolver.Prepare(request with { DefinitionVersion = 1, DefinitionSha = OtherSha(pin.DefinitionSha) }, default));
            Check(!JsonSerializer.Serialize(pin).Contains("fixture-env-marker", StringComparison.Ordinal), "pin exposed a canonical environment body");
        }
        foreach (var policy in new[] { "", "registered", "inherit", "Scheduler" })
            Refused("invalid", () => fixture.Resolver.Prepare(request with { EnvironmentPolicy = policy }, default));
        foreach (var argument in new[] { "%TEMP%", "100%", "$(Arg0)", "prefix$(aRg1)tail", "bad\0value" })
            Refused("invalid", () => fixture.Resolver.Prepare(request with { Argv = [argument] }, default));
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Argv = [new string('x', 129)] }, default));
        Refused("not_found", () => fixture.Resolver.Prepare(request with { Tool = "missing" }, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Canceled(() => fixture.Resolver.Prepare(request, canceled.Token));
        Check(fixture.Runner.Calls == 1, "refusal ran a process or new probe");

        await fixture.ReplaceAsync(fixture.Definition with { DefinitionVersion = 2, VersionProbe = null });
        Refused("precondition", () => fixture.Resolver.Prepare(request, default));
        await fixture.ReplaceAsync(fixture.Definition with { DefinitionVersion = 3, Mode = ToolExecutionMode.System });
        Refused("precondition", () => fixture.Resolver.Prepare(request, default));
        await fixture.Tools.Tools.RegisterInstalledPowerShellAsync(fixture.Definition with
        {
            Name = "pwsh", DefinitionVersion = 1, ExecutablePath = fixture.PowerShell, Mode = ToolExecutionMode.User,
            VersionProbe = new ToolProbeDefinition(["--version"])
        });
        Refused("precondition", () => fixture.Resolver.Prepare(request with { Tool = "pwsh" }, default));
    }

    private static async Task DefinitionChangesAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        WindowsTaskToolPin original;
        using (var first = fixture.Resolver.Prepare(fixture.Request, default)) original = first.Pin;
        var pinned = fixture.Request with { DefinitionVersion = original.DefinitionVersion, DefinitionSha = original.DefinitionSha };
        await fixture.ReplaceAsync(fixture.Definition with { DefinitionVersion = 2 });
        Refused("precondition", () => fixture.Resolver.Prepare(pinned, default));
        using (var updated = fixture.Resolver.Prepare(fixture.Request, default))
            Check(updated.Pin.DefinitionVersion == 2 && updated.Pin.DefinitionSha != original.DefinitionSha, "version changed without digest change");

        Check(fixture.Tools.RemoveTool("fixture"), "owned definition was not removed");
        await fixture.ReplaceAsync(fixture.Definition with
        {
            Environment = new Dictionary<string, string?> { ["B"] = "second", ["A"] = "fixture-env-marker" },
            ExpectedExitCodes = [7, 0]
        });
        using (var reordered = fixture.Resolver.Prepare(pinned, default))
            Check(reordered.Pin == original, "unordered environment/exit sets changed canonical identity");
        Check(fixture.Tools.RemoveTool("fixture"), "owned reordered definition was not removed");
        await fixture.ReplaceAsync(fixture.Definition with
        {
            Environment = new Dictionary<string, string?> { ["A"] = "changed-env-marker", ["B"] = "second" }
        });
        Refused("precondition", () => fixture.Resolver.Prepare(pinned, default));
        using (var changed = fixture.Resolver.Prepare(fixture.Request, default))
            Check(changed.Pin.DefinitionVersion == 1 && changed.Pin.DefinitionSha != original.DefinitionSha, "remove/re-add reused a stale definition pin");
        File.WriteAllText(fixture.Executable, "changed-image", new UTF8Encoding(false));
        Refused("integrity", () => fixture.Resolver.Prepare(fixture.Request, default));
    }

    private static async Task WorkingDirectoriesAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request;
        using (var executableDirectory = fixture.Resolver.Prepare(request, default))
            Check(executableDirectory.Invocation.WorkingDirectory == fixture.Root, "executable directory used an ambient cwd");
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Cwd = fixture.Workspace }, default));
        await fixture.ReplaceAsync(fixture.Definition with { DefinitionVersion = 2, WorkingDirectoryRule = ToolWorkingDirectoryRule.Caller });
        Refused("invalid", () => fixture.Resolver.Prepare(request, default));
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Cwd = "relative" }, default));
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Workspace = "fixture_ws", Cwd = fixture.Workspace }, default));
        using (var caller = fixture.Resolver.Prepare(request with { Cwd = fixture.Workspace }, default))
            Check(caller.Invocation.WorkingDirectory == fixture.Workspace, "explicit caller cwd was not admitted");
        foreach (var bad in new[] { "percent%dir", "event$(Arg0)dir" })
        {
            var path = Path.Combine(fixture.Root, bad); Directory.CreateDirectory(path);
            Refused("invalid", () => fixture.Resolver.Prepare(request with { Cwd = path }, default));
        }
        await fixture.ReplaceAsync(fixture.Definition with { DefinitionVersion = 3, WorkingDirectoryRule = ToolWorkingDirectoryRule.Workspace });
        Refused("invalid", () => fixture.Resolver.Prepare(request, default));
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Workspace = fixture.Workspace }, default));
        Refused("not_found", () => fixture.Resolver.Prepare(request with { Workspace = "unknown" }, default));
        using (var workspace = fixture.Resolver.Prepare(request with { Workspace = "fixture_ws" }, default))
            Check(workspace.Invocation.WorkingDirectory == fixture.Workspace && fixture.LastWorkspace == "fixture_ws", "exact registered workspace was not selected");
        using (var subdirectory = fixture.Resolver.Prepare(request with { Workspace = "fixture_ws", Cwd = Path.Combine(fixture.Workspace, "sub") }, default))
            Check(subdirectory.Invocation.WorkingDirectory == Path.Combine(fixture.Workspace, "sub"), "workspace-contained cwd was not admitted");
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Workspace = "fixture_ws", Cwd = "sub" }, default));
        Refused("precondition", () => fixture.Resolver.Prepare(request with { Workspace = "fixture_ws", Cwd = fixture.Root }, default));
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Workspace = "fixture_ws", Cwd = ".." }, default));
        await fixture.ReplaceAsync(fixture.Definition with
        {
            DefinitionVersion = 4, WorkingDirectoryRule = ToolWorkingDirectoryRule.Fixed, FixedWorkingDirectory = fixture.Workspace
        });
        using (var fixedDirectory = fixture.Resolver.Prepare(request, default))
            Check(fixedDirectory.Invocation.WorkingDirectory == fixture.Workspace, "fixed registered cwd was not preserved");
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Workspace = "fixture_ws" }, default));
        Refused("invalid", () => fixture.Resolver.Prepare(request with { Cwd = fixture.Root }, default));

        foreach (var name in new[] { "percent%tool.exe", "event$(Arg0)tool.exe" })
        {
            var path = Path.Combine(fixture.Root, name); File.WriteAllText(path, "inert-file");
            await fixture.Tools.Tools.RegisterAsync(fixture.Definition with { Name = "literal", ExecutablePath = path });
            Refused("invalid", () => fixture.Resolver.Prepare(request with { Tool = "literal" }, default));
            Check(fixture.Tools.RemoveTool("literal"), "owned expansion-path definition was not removed");
        }
    }

    private static async Task LeaseAndOwnerAsync()
    {
        using var fixture = await Fixture.CreateAsync();
        var prepared = fixture.Resolver.Prepare(fixture.Request, default);
        try
        {
            try
            {
                using var unexpected = new FileStream(fixture.Executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                throw new InvalidOperationException("prepared image was writable during registration lease");
            }
            catch (IOException) { _checks++; }
        }
        finally { prepared.Dispose(); prepared.Dispose(); }
        AssertWritable(fixture.Executable);
        foreach (var denied in new[]
        {
            fixture.Peer with { Elevated = true }, fixture.Peer with { Session = 0 }, fixture.Peer with { Session = 8 },
            fixture.Peer with { Sid = "S-1-5-18" }, fixture.Peer with { Sid = "S-1-5-21-10-20-30-1002" }
        })
        {
            var original = fixture.Peer; fixture.Peer = denied;
            Refused("owner_denied", () => fixture.Resolver.Prepare(fixture.Request, default));
            fixture.Peer = original;
        }
        fixture.OwnerHook = count => { if (count == 4) fixture.Peer = fixture.Peer with { Elevated = true }; };
        Refused("owner_denied", () => fixture.Resolver.Prepare(fixture.Request, default));
        fixture.OwnerHook = null; fixture.Peer = new LocalPeer(Sid, 7, Elevated: false);
        AssertWritable(fixture.Executable);
        using var canceled = new CancellationTokenSource();
        fixture.OwnerHook = count => { if (count == 4) canceled.Cancel(); };
        Canceled(() => fixture.Resolver.Prepare(fixture.Request, canceled.Token));
        fixture.OwnerHook = null; AssertWritable(fixture.Executable);
        fixture.OwnerHook = count =>
        {
            if (count == 4) fixture.ReplaceAsync(fixture.Definition with { DefinitionVersion = 2 }).GetAwaiter().GetResult();
        };
        Refused("precondition", () => fixture.Resolver.Prepare(fixture.Request, default));
        fixture.OwnerHook = null; AssertWritable(fixture.Executable);
        Check(fixture.Runner.Calls == 2, "resolution ran anything beyond the two injected registration probes");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cortex-task-tool-fixture-" + Guid.NewGuid().ToString("N"));
        internal string Executable => Path.Combine(Root, "fixture tool.exe");
        internal string PowerShell => Path.Combine(Root, "pwsh.exe");
        internal string Workspace => Path.Combine(Root, "workspace");
        internal LocalPeer Peer = new(Sid, 7, Elevated: false);
        internal readonly ProbeRunner Runner = new();
        internal ToolRecipeOperations Tools { get; }
        internal WindowsTaskToolResolver Resolver { get; }
        internal RegisteredToolDefinition Definition { get; }
        internal WindowsTaskToolRequest Request => new("fixture", [], "scheduler");
        internal string? LastWorkspace;
        private int _ownerCalls;
        private Action<int>? _ownerHook;
        internal Action<int>? OwnerHook { set { _ownerCalls = 0; _ownerHook = value; } }

        private Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Workspace, "sub"));
            File.WriteAllText(Executable, "inert-owned-image", new UTF8Encoding(false));
            File.WriteAllText(PowerShell, "inert-owned-pwsh", new UTF8Encoding(false));
            Tools = new ToolRecipeOperations(Path.Combine(Root, "state"), Runner, () => PowerShell);
            Definition = new RegisteredToolDefinition("fixture", 1, Executable,
                WorkingDirectoryRule: ToolWorkingDirectoryRule.ExecutableDirectory,
                ExpectedExitCodes: [0, 7], MaxArguments: 8, MaxArgumentUtf8Bytes: 128, MaxTotalArgumentUtf8Bytes: 512,
                VersionProbe: new ToolProbeDefinition(["--version"], [0], "fixture"),
                Environment: new Dictionary<string, string?> { ["A"] = "fixture-env-marker", ["B"] = "second" });
            Resolver = new WindowsTaskToolResolver(Tools, alias =>
            {
                LastWorkspace = alias;
                return alias == "fixture_ws" ? Workspace : throw new AgentOpsException("not_found");
            }, Sid, () => { _ownerHook?.Invoke(++_ownerCalls); return Peer; });
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try { await fixture.ReplaceAsync(fixture.Definition); return fixture; }
            catch { fixture.Dispose(); throw; }
        }

        internal Task<RegisteredToolDefinition> ReplaceAsync(RegisteredToolDefinition definition) => Tools.Tools.RegisterAsync(definition);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ProbeRunner : IToolInvocationRunner
    {
        internal int Calls;
        public Task<ProcessResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(new ProcessResult(0, "fixture PowerShell 7.0", "", false, false, false));
        }
    }

    private static bool IsSha(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string OtherSha(string value) => (value[0] == '0' ? "1" : "0") + value[1..];
    private static void AssertWritable(string path)
    {
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        _checks++;
    }
    private static void Refused(string code, Func<WindowsTaskPreparedTool> action)
    {
        try { using var unexpected = action(); }
        catch (AgentOpsException failure) when (failure.Code == code) { _checks++; return; }
        throw new InvalidOperationException("task resolver accepted an invalid request or changed its failure classification");
    }
    private static void Canceled(Func<WindowsTaskPreparedTool> action)
    {
        try { using var unexpected = action(); }
        catch (OperationCanceledException) { _checks++; return; }
        throw new InvalidOperationException("task resolver cancellation was not preserved");
    }
    private static void Check(bool condition, string code)
    { if (!condition) throw new InvalidOperationException(code); _checks++; }
}
