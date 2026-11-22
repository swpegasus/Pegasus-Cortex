using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;

if (args.Length > 0 && args[0] != "--tests")
{
    try
    {
        if (args[0] == "--version")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine("Pegasus AgentOps focused test tool 1.0");
            return 0;
        }
        if (args[0] == "child-echo")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write(string.Join('\u001f', args.Skip(1)));
            return 0;
        }
        if (args[0] == "child-fail")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write("stdout-abcdefgh");
            Console.Error.Write("stderr-abcdefgh");
            return 23;
        }
        if (args[0] == "child-env")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write(JsonSerializer.Serialize(args.Skip(1).Select(Environment.GetEnvironmentVariable).ToArray()));
            return 0;
        }
        if (args[0] == "child-timeout-grandchild" && args.Length == 2)
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            File.WriteAllText(args[1], "survived", new UTF8Encoding(false));
            return 0;
        }
        if (args[0] == "child-timeout-parent" && args.Length == 3)
        {
            using var grandchild = StartSelf("child-timeout-grandchild", args[1]);
            var identity = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{grandchild.Id}|{grandchild.StartTime.ToUniversalTime().Ticks}");
            File.WriteAllText(args[2], identity, new UTF8Encoding(false));
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write("timeout-out");
            Console.Error.Write("timeout-err");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (args[0] == "child-register" && args.Length == 4)
        {
            var childOperations = new ToolRecipeOperations(args[1], new RecordingRunner());
            await childOperations.Tools.RegisterAsync(Tool(args[2], args[3]));
            return 0;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 90;
    }
    return 91;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("registry_probe_persistence_and_difficult_argv", RegistryAndArgumentsAsync),
    ("sha_mismatch_and_unregistered", ShaAndUnregisteredAsync),
    ("probe_fail_closed", ProbeFailureAsync),
    ("typed_adapter_vectors", AdapterVectorsAsync),
    ("typed_recipe_variables_and_ps1", RecipesAsync),
    ("dotnet_release_recipe_admitted_coordinates", DotnetReleaseRecipeAsync),
    ("dotnet_release_recipe_closed_bounds", DotnetReleaseRecipeBoundsAsync),
    ("dotnet_release_recipe_link_boundary", DotnetReleaseRecipeLinksAsync),
    ("dotnet_release_recipe_failure_evidence", DotnetReleaseRecipeFailureAsync),
    ("atomic_registry_publication", AtomicStoreAsync),
    ("system_classification_without_elevation", SystemClassificationAsync),
    ("argv_variable_and_output_bounds", BoundsAsync),
    ("execution_identity_revalidated", ExecutionIdentityAsync),
    ("public_invocation_admission", PublicInvocationAdmissionAsync),
    ("workspace_caller_cwd_confined", WorkspaceCallerCwdAsync),
    ("cross_process_registry_serialization", CrossProcessRegistryAsync),
    ("adapter_recipes_and_7z_extract_fail_closed", UnsafeAdaptersFailClosedAsync),
    ("postcondition_failure_requires_recovery", PostconditionRecoveryAsync),
    ("default_runner_contract_and_real_argv", DefaultRunnerContractAsync),
    ("external_failure_evidence_and_timeout_tree", ExternalFailureEvidenceAsync)
};

var requestedTests = args.Skip(1).ToArray();
if (args.Length > 0 && (requestedTests.Length == 0 ||
    requestedTests.Any(name => !tests.Any(test => test.Name == name))))
{
    Console.Error.WriteLine("Use --tests followed by exact advertised test names, or omit arguments.");
    return 2;
}
var selectedTests = args.Length == 0 ? tests :
    tests.Where(test => requestedTests.Contains(test.Name, StringComparer.Ordinal)).ToArray();
var failed = 0;
foreach (var test in selectedTests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name} {ex}");
    }
}
Console.WriteLine($"RESULT total={selectedTests.Length} passed={selectedTests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static async Task RegistryAndArgumentsAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("fake tool.exe", "fake-v1");
    var runner = new RecordingRunner(new ProcessResult(0, "Fake Tool 1.0", string.Empty, false, false, false));
    var operations = new ToolRecipeOperations(temp.State, runner);
    var definition = Tool(executable, "fake") with
    {
        ExecutableSha256 = Sha(executable),
        ExpectedExitCodes = [0, 7],
        MaxArguments = 16,
        MaxArgumentUtf8Bytes = 1024,
        MaxTotalArgumentUtf8Bytes = 4096,
        VersionProbe = new ToolProbeDefinition(["--version"], [0], "Fake Tool")
    };
    var registered = await operations.Tools.RegisterAsync(definition);
    Check(registered.ProbeVerified, "probe evidence not persisted");
    Equal(1, runner.Calls.Count);
    SequenceEqual(new[] { "--version" }, runner.Calls[0].Arguments);
    await ThrowsCodeAsync("precondition", () => operations.Tools.RegisterAsync(definition));
    Equal(1, runner.Calls.Count);

    var reloaded = new ToolRecipeOperations(temp.State, new RecordingRunner());
    Equal(Path.GetFullPath(executable), reloaded.Tools.Get("fake").ExecutablePath);
    var difficult = new[]
    {
        "space value", "apostrophe's", "[brackets]", "Ω-unicode", "a&b", "\"quoted\"", string.Empty, "trailing\\"
    };
    var invocation = reloaded.PrepareTool("fake", difficult);
    Equal(Path.GetFullPath(executable), invocation.ExecutablePath);
    SequenceEqual(difficult, invocation.Arguments);
}

static async Task ShaAndUnregisteredAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("hash.exe", "known");
    var operations = new ToolRecipeOperations(temp.State, new RecordingRunner());
    await ThrowsCodeAsync("integrity", () => operations.Tools.RegisterAsync(Tool(executable, "bad") with
    {
        ExecutableSha256 = new string('0', 64)
    }));
    ThrowsCode("not_found", () => operations.Tools.Get("bad"));
    ThrowsCode("not_found", () => operations.PrepareTool("missing", []));

    await operations.Tools.RegisterAsync(Tool(executable, "good") with { ExecutableSha256 = Sha(executable) });
    File.WriteAllText(executable, "changed", new UTF8Encoding(false));
    ThrowsCode("integrity", () => operations.PrepareTool("good", []));
}

static async Task ProbeFailureAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("probe.exe", "probe");
    var badExit = new RecordingRunner(new ProcessResult(9, "v1", string.Empty, false, false, false));
    var operations = new ToolRecipeOperations(temp.State, badExit);
    await ThrowsCodeAsync("external_failed", () => operations.Tools.RegisterAsync(Tool(executable, "probe") with
    {
        VersionProbe = new ToolProbeDefinition(["--version"], [0], "v1")
    }));
    ThrowsCode("not_found", () => operations.Tools.Get("probe"));

    var missingText = new ToolRecipeOperations(temp.Dir("state-two"),
        new RecordingRunner(new ProcessResult(0, "different", string.Empty, false, false, false)));
    await ThrowsCodeAsync("precondition", () => missingText.Tools.RegisterAsync(Tool(executable, "probe") with
    {
        VersionProbe = new ToolProbeDefinition(["--version"], [0], "required")
    }));
    ThrowsCode("not_found", () => missingText.Tools.Get("probe"));
}

static async Task AdapterVectorsAsync()
{
    using var temp = new TempRoot();
    var runner = new RecordingRunner(_ => new ProcessResult(0, "PowerShell 7.", string.Empty, false, false, false));
    var operations = new ToolRecipeOperations(temp.State, runner);
    var executable = temp.Executable("adapter host.exe", "adapter");
    foreach (var name in new[] { "7z", "dotnet", "git" })
        await operations.Tools.RegisterAsync(Tool(executable, name) with
        {
            MaxArguments = 32,
            MaxArgumentUtf8Bytes = 4096,
            MaxTotalArgumentUtf8Bytes = 8192
        });
    await operations.Tools.RegisterAsync(Tool(PowerShell7ToolBootstrap.ResolveExecutable(), "pwsh") with
    {
        MaxArguments = 32,
        MaxArgumentUtf8Bytes = 4096,
        MaxTotalArgumentUtf8Bytes = 8192
    });

    var archive = Path.Combine(temp.Path, "archive Ω & one.7z");
    File.WriteAllBytes(archive, [0x37, 0x7A]);
    var sevenTest = operations.PrepareSevenZip("test", archive);
    SequenceEqual(new[] { "t", "-bso0", "-bsp0", "--", Path.GetFullPath(archive) }, sevenTest.Arguments);
    var destination = Path.Combine(temp.Path, "out folder");
    ThrowsCode("precondition", () => operations.PrepareSevenZip("extract", archive, destination, "skip"));

    var dotnet = operations.PrepareDotnet("build", "Release Candidate", ["--", "value with space", "a&b"]);
    SequenceEqual(new[] { "build", "--configuration", "Release Candidate", "--", "value with space", "a&b" }, dotnet.Arguments);
    var git = operations.PrepareGit("status", ["--short", "path with space"]);
    SequenceEqual(new[] { "status", "--short", "path with space" }, git.Arguments);

    var script = temp.Script("invoke me.ps1", "param([string]$Name)");
    var pwsh = operations.PreparePowerShell(script, ["-Name", "O'Brien & Ω"], expectedScriptSha256: Sha(script));
    SequenceEqual(new[] { "-NoProfile", "-NonInteractive", "-File", Path.GetFullPath(script), "-Name", "O'Brien & Ω" }, pwsh.Arguments);

    var workspace = temp.Dir("adapter-workspace");
    var outsideScript = temp.Script("outside.ps1", "'outside'");
    ThrowsCode("precondition", () => operations.PreparePowerShell(outsideScript, workspaceRoot: workspace));
    ThrowsCode("precondition", () => operations.PrepareSevenZip("test", archive, workspaceRoot: workspace));
}

static async Task RecipesAsync()
{
    using var temp = new TempRoot();
    var runner = new RecordingRunner(_ =>
        new ProcessResult(0, "PowerShell 7.", string.Empty, false, false, false));
    var operations = new ToolRecipeOperations(temp.State, runner);
    var executable = temp.Executable("pack.exe", "pack");
    await operations.Tools.RegisterAsync(Tool(executable, "pack") with
    {
        ExecutableSha256 = Sha(executable),
        WorkingDirectoryRule = ToolWorkingDirectoryRule.Workspace,
        TimeoutSeconds = 60,
        MaxArguments = 32,
        MaxArgumentUtf8Bytes = 4096,
        MaxTotalArgumentUtf8Bytes = 8192
    });

    var workspace = temp.Dir("workspace");
    Directory.CreateDirectory(Path.Combine(workspace, "build"));
    var artifact = Path.Combine(workspace, "build", "artifact.txt");
    File.WriteAllText(artifact, "ready", new UTF8Encoding(false));
    var recipe = new OneActionRecipeDefinition(
        "package",
        1,
        RecipeActionKind.Tool,
        "pack",
        ["--configuration", "{{cfg}}", "--version={{ver}}", "{{count}}", "{{enabled}}", "{{rel}}"],
        [
            new RecipeVariableDefinition("cfg", RecipeVariableType.Choice, Choices: ["Debug", "Release"]),
            new RecipeVariableDefinition("ver", MaxUtf8Bytes: 32),
            new RecipeVariableDefinition("count", RecipeVariableType.Integer, Minimum: 1, Maximum: 10),
            new RecipeVariableDefinition("enabled", RecipeVariableType.Boolean),
            new RecipeVariableDefinition("rel", RecipeVariableType.RelativePath)
        ],
        Workspace: "agentops",
        WorkingDirectory: "build",
        TimeoutSeconds: 30,
        ExpectedExitCodes: [0],
        ExecutableSha256: Sha(executable),
        Postcondition: new RecipePostcondition(RecipePostconditionKind.FileExists, "artifact.txt"));
    operations.Recipes.Register(recipe);

    var supplied = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cfg"] = "Release", ["ver"] = "2026.8.11-r1", ["count"] = "+03", ["enabled"] = "TRUE", ["rel"] = "out folder\\file.zip"
    };
    var invocation = operations.PrepareRecipe("package", supplied,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["agentops"] = workspace });
    SequenceEqual(new[] { "--configuration", "Release", "--version=2026.8.11-r1", "3", "true", "out folder\\file.zip" }, invocation.Arguments);
    Equal(Path.Combine(workspace, "build"), invocation.WorkingDirectory);
    Equal(ToolExecutionMode.User, invocation.Mode);
    Equal("package", invocation.RecipeName);
    var result = await operations.RunAsync(invocation);
    Equal(0, result.ExitCode);

    ThrowsCode("precondition", () => operations.PrepareRecipe("package",
        supplied.Where(x => x.Key != "ver").ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        new Dictionary<string, string> { ["agentops"] = workspace }));
    var unknown = new Dictionary<string, string>(supplied, StringComparer.Ordinal) { ["other"] = "x" };
    ThrowsCode("precondition", () => operations.PrepareRecipe("package", unknown,
        new Dictionary<string, string> { ["agentops"] = workspace }));
    Check(!File.ReadAllText(operations.Recipes.StorePath).Contains("2026.8.11-r1", StringComparison.Ordinal), "supplied variable persisted");

    var pwshExecutable = PowerShell7ToolBootstrap.ResolveExecutable();
    await operations.Tools.RegisterAsync(Tool(pwshExecutable, "pwsh"));
    var scripts = Directory.CreateDirectory(Path.Combine(workspace, "scripts")).FullName;
    var script = Path.Combine(scripts, "do.ps1");
    File.WriteAllText(script, "param([string]$Name)", new UTF8Encoding(false));
    operations.Recipes.Register(new OneActionRecipeDefinition(
        "invoke-script", 1, RecipeActionKind.PowerShell, "pwsh", ["-Name", "{{name}}"],
        [new RecipeVariableDefinition("name")], Workspace: "agentops", ScriptPath: "scripts/do.ps1", ScriptSha256: Sha(script)));
    var psRecipe = operations.PrepareRecipe("invoke-script", new Dictionary<string, string> { ["name"] = "O'Brien Ω" },
        new Dictionary<string, string> { ["agentops"] = workspace });
    SequenceEqual(new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Name", "O'Brien Ω" }, psRecipe.Arguments);

    operations.Recipes.Register(new OneActionRecipeDefinition(
        "nonsecret-tokenizer", 1, RecipeActionKind.Tool, "pack", ["--tokenizer={{tokenizer}}"],
        [new RecipeVariableDefinition("tokenizer", MaxUtf8Bytes: 32)]));
    ThrowsCode("precondition", () => operations.Recipes.Register(new OneActionRecipeDefinition(
        "persisted-default", 1, RecipeActionKind.Tool, "pack", ["{{value}}"],
        [new RecipeVariableDefinition("value", DefaultValue: "stored-value")])));
    foreach (var reserved in new[] { "root", "stdin", "env" })
        ThrowsCode("precondition", () => operations.Recipes.Register(new OneActionRecipeDefinition(
            $"reserved-{reserved}", 1, RecipeActionKind.Tool, "pack", ["{{" + reserved + "}}"],
            [new RecipeVariableDefinition(reserved)])));
}

static OneActionRecipeDefinition PluginBuildRecipe() =>
    JsonSerializer.Deserialize<OneActionRecipeDefinition>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "recipes", "plugin-build-release.v1.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new Exception("Missing recipe definition.");

static async Task DotnetReleaseRecipeAsync()
{
    using var temp = new TempRoot();
    var runner = new RecordingRunner();
    var operations = new ToolRecipeOperations(temp.State, runner);
    var tool = temp.Executable("dotnet.exe", "fixture dotnet");
    await operations.Tools.RegisterAsync(Tool(tool, "dotnet") with
    {
        WorkingDirectoryRule = ToolWorkingDirectoryRule.Caller, TimeoutSeconds = 120
    });
    var recipe = PluginBuildRecipe();
    operations.Recipes.Register(recipe);
    runner.Calls.Clear();
    var cwd = temp.Dir("component Ω with spaces");
    var project = Path.Combine(cwd, "-Plugin Ω.csproj");
    File.WriteAllText(project, "<Project />", new UTF8Encoding(false));
    var values = new Dictionary<string, string> { ["project"] = Path.GetFileName(project) };
    var invocation = operations.PrepareRecipe(recipe.Name, values, callerCwd: cwd);
    SequenceEqual(new[] { "build", project, "--configuration", "Release",
        "--disable-build-servers", "--verbosity", "minimal" }, invocation.Arguments);
    Equal(cwd, invocation.WorkingDirectory);
    Equal(TimeSpan.FromSeconds(120), invocation.Timeout);
    Equal(16384, invocation.MaxOutputBytes);
    Equal(ToolExecutionMode.User, invocation.Mode);
    Equal(recipe.Name, invocation.RecipeName);
    Equal(0, runner.Calls.Count);
    Equal(0, (await operations.RunAsync(invocation)).ExitCode);
    Equal(1, runner.Calls.Count);
    Check(!File.ReadAllText(operations.Recipes.StorePath).Contains(cwd, StringComparison.Ordinal),
        "Runtime build coordinates were persisted in the definition.");
    using var stored = JsonDocument.Parse(File.ReadAllText(operations.Recipes.StorePath));
    Equal(1, stored.RootElement.GetProperty("schemaVersion").GetInt32());
    var reopened = new ToolRecipeOperations(temp.State, runner);
    SequenceEqual(invocation.Arguments,
        reopened.PrepareRecipe(recipe.Name, values, callerCwd: cwd).Arguments);
    ThrowsCode("precondition", () => operations.Recipes.Register(recipe));
}

static async Task DotnetReleaseRecipeBoundsAsync()
{
    using var temp = new TempRoot();
    var runner = new RecordingRunner();
    var operations = new ToolRecipeOperations(temp.State, runner);
    var tool = temp.Executable("dotnet.exe", "fixture dotnet");
    await operations.Tools.RegisterAsync(Tool(tool, "dotnet") with
    {
        WorkingDirectoryRule = ToolWorkingDirectoryRule.Caller, TimeoutSeconds = 120
    });
    var recipe = PluginBuildRecipe();
    operations.Recipes.Register(recipe);
    var cwd = temp.Dir("component");
    File.WriteAllText(Path.Combine(cwd, "Plugin.csproj"), "<Project />", new UTF8Encoding(false));
    var values = new Dictionary<string, string> { ["project"] = "Plugin.csproj" };
    foreach (var rejected in new[]
    {
        recipe with { FixedArguments = ["publish", "{{project}}"] },
        recipe with { FixedArguments = recipe.FixedArguments!.Concat(["--output", "somewhere"]).ToArray() },
        recipe with { FixedArguments = ["build {{project}} --configuration Release"] },
        recipe with { Variables = [new("project", RecipeVariableType.Text, MaxUtf8Bytes: 1024)] },
        recipe with { Variables = [new("project", RecipeVariableType.RelativePath, Required: false, MaxUtf8Bytes: 1024)] },
        recipe with { Workspace = "guessed" }, recipe with { WorkingDirectory = "{{cwd}}" },
        recipe with { TimeoutSeconds = null }, recipe with { TimeoutSeconds = 121 },
        recipe with { MaxOutputBytes = null }, recipe with { MaxOutputBytes = 16385 },
        recipe with { ExpectedExitCodes = [0, 1] }, recipe with { OutputBehavior = ToolOutputBehavior.Discard }
    }) ThrowsCode("precondition", () => operations.Recipes.Register(rejected));
    ThrowsCode("precondition", () => operations.PrepareRecipe(recipe.Name, values));
    ThrowsCode("precondition", () => operations.PrepareRecipe(recipe.Name, values, callerCwd: "component"));
    ThrowsCode("not_found", () => operations.PrepareRecipe(recipe.Name, values, callerCwd: Path.Combine(temp.Path, "missing")));
    ThrowsCode("precondition", () => operations.PrepareRecipe(recipe.Name,
        new Dictionary<string, string> { ["project"] = "Plugin.csproj", ["extra"] = "ignored" }, callerCwd: cwd));
    foreach (var project in new[] { "../outside.csproj", "sub/../../outside.csproj", "C:relative.csproj",
        Path.Combine(cwd, "Plugin.csproj"), "Plugin.csproj:stream", "-p:Target=Publish" })
        ThrowsCode("precondition", () => operations.PrepareRecipe(recipe.Name,
            new Dictionary<string, string> { ["project"] = project }, callerCwd: cwd));
    ThrowsCode("precondition", () => operations.PrepareRecipe(recipe.Name,
        new Dictionary<string, string> { ["project"] = "other.slnx" }, callerCwd: cwd));
    ThrowsCode("not_found", () => operations.PrepareRecipe(recipe.Name,
        new Dictionary<string, string> { ["project"] = "missing.csproj" }, callerCwd: cwd));
    ThrowsCode("precondition", () => operations.PrepareRecipe(recipe.Name,
        new Dictionary<string, string> { ["project"] = new string('p', 240) + ".csproj" }, callerCwd: cwd));
    Equal(1, runner.Calls.Count); // Registration probe only; no rejected call ran.
}

static async Task DotnetReleaseRecipeLinksAsync()
{
    using var temp = new TempRoot();
    var runner = new RecordingRunner();
    var operations = new ToolRecipeOperations(temp.State, runner);
    await operations.Tools.RegisterAsync(Tool(temp.Executable("dotnet.exe", "fixture"), "dotnet") with
    {
        WorkingDirectoryRule = ToolWorkingDirectoryRule.Caller, TimeoutSeconds = 120
    });
    var recipe = PluginBuildRecipe();
    operations.Recipes.Register(recipe);
    var cwd = temp.Dir("component");
    var outside = temp.Dir("outside");
    var project = Path.Combine(outside, "Plugin.csproj");
    File.WriteAllText(project, "<Project />", new UTF8Encoding(false));
    var directoryLink = Path.Combine(cwd, "linked-directory");
    var fileLink = Path.Combine(cwd, "linked-project.csproj");
    try
    {
        Directory.CreateSymbolicLink(directoryLink, outside);
        File.CreateSymbolicLink(fileLink, project);
        foreach (var relative in new[] { "linked-directory/Plugin.csproj", "linked-project.csproj" })
            ThrowsCode("reparse_boundary", () => operations.PrepareRecipe(recipe.Name,
                new Dictionary<string, string> { ["project"] = relative }, callerCwd: cwd));
        ThrowsCode("reparse_boundary", () => operations.PrepareRecipe(recipe.Name,
            new Dictionary<string, string> { ["project"] = "Plugin.csproj" }, callerCwd: directoryLink));
        Equal("<Project />", File.ReadAllText(project));
        Equal(1, runner.Calls.Count);
    }
    finally
    {
        // Remove the exact fixture links themselves, not their targets, before
        // TempRoot recursively cleans its task-owned ordinary directories.
        if (File.Exists(fileLink)) File.Delete(fileLink);
        if (Directory.Exists(directoryLink)) Directory.Delete(directoryLink);
    }
}

static async Task DotnetReleaseRecipeFailureAsync()
{
    using var temp = new TempRoot();
    const string diagnostic = "GeneratedDesktopPlugin.cs(8,3): error CS0117: Environment has no DataDirectory";
    foreach (var timedOut in new[] { false, true })
    {
        var runner = new RecordingRunner(new ProcessResult(0, "fixture dotnet", "", false, false, false),
            new ProcessResult(1, diagnostic + new string('x', 17000), "compiler detail",
                TimedOut: timedOut, StdoutTruncated: true, StderrTruncated: false));
        var operations = new ToolRecipeOperations(temp.Dir(timedOut ? "timeout-state" : "failure-state"), runner);
        await operations.Tools.RegisterAsync(Tool(temp.Executable(timedOut ? "timeout.exe" : "failure.exe", "fixture"), "dotnet") with
        {
            WorkingDirectoryRule = ToolWorkingDirectoryRule.Caller, TimeoutSeconds = 120
        });
        var recipe = PluginBuildRecipe();
        operations.Recipes.Register(recipe);
        var cwd = temp.Dir(timedOut ? "timeout-component" : "failure-component");
        File.WriteAllText(Path.Combine(cwd, "Plugin.csproj"), "<Project />", new UTF8Encoding(false));
        var failure = await ThrowsExecutionCodeAsync(timedOut ? "timeout" : "external_failed",
            () => operations.RunAsync(operations.PrepareRecipe(recipe.Name,
                new Dictionary<string, string> { ["project"] = "Plugin.csproj" }, callerCwd: cwd)));
        Equal(1, failure.ExitCode);
        Equal(timedOut, failure.TimedOut);
        Equal(16384, Encoding.UTF8.GetByteCount(failure.Stdout));
        Check(failure.Stdout.StartsWith(diagnostic, StringComparison.Ordinal) && failure.StdoutTruncated,
            "Compiler failure lost bounded diagnostic or truncation evidence.");
        Equal("compiler detail", failure.Stderr);
        Equal(2, runner.Calls.Count); // Probe and one build; never automatic replay.
    }
}

static async Task AtomicStoreAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("atomic.exe", "atomic");
    var operations = new ToolRecipeOperations(temp.State, new RecordingRunner());
    await operations.Tools.RegisterAsync(Tool(executable, "atomic"));
    var before = File.ReadAllBytes(operations.Tools.StorePath);

    using (var held = new FileStream(operations.Tools.StorePath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        await ThrowsAnyAsync<IOException, UnauthorizedAccessException>(() => operations.Tools.RegisterAsync(
            Tool(executable, "atomic") with { DefinitionVersion = 2, TimeoutSeconds = 60 }));
    }
    BytesEqual(before, File.ReadAllBytes(operations.Tools.StorePath));
    Equal(1, operations.Tools.Get("atomic").DefinitionVersion);
    Check(!Directory.EnumerateFiles(temp.State, ".tools.v1.json.*.tmp").Any(), "atomic store temp retained");
}

static async Task SystemClassificationAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("system.exe", "system");
    var runner = new RecordingRunner();
    var operations = new ToolRecipeOperations(temp.State, runner);
    await operations.Tools.RegisterAsync(Tool(executable, "system") with { Mode = ToolExecutionMode.System });
    var invocation = operations.PrepareTool("system", ["--inspect"]);
    Equal(ToolExecutionMode.System, invocation.Mode);
    Equal(0, runner.Calls.Count);
    await ThrowsCodeAsync("service_unavailable", () => new AgentOpsInvocationRunner().RunAsync(invocation, CancellationToken.None));
    await ThrowsCodeAsync("service_unavailable", async () => _ = await operations.RunAsync(invocation));
    Equal(0, runner.Calls.Count);
    await ThrowsCodeAsync("precondition", () => operations.Tools.RegisterAsync(Tool(executable, "bad-visible-system") with
    {
        Mode = ToolExecutionMode.System,
        Window = ToolWindowMode.Visible
    }));
}

static async Task BoundsAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("bounded.exe", "bounded");
    var runner = new RecordingRunner(invocation => invocation.Arguments.SequenceEqual(new[] { "-v" })
        ? new ProcessResult(0, "v1", "", false, false, false)
        : new ProcessResult(0, "😀Ω", "abcdef", false, false, false));
    var operations = new ToolRecipeOperations(temp.State, runner);
    await operations.Tools.RegisterAsync(Tool(executable, "bounded") with
    {
        MaxArguments = 2,
        MaxArgumentUtf8Bytes = 4,
        MaxTotalArgumentUtf8Bytes = 6,
        MaxOutputBytes = 5,
        VersionProbe = new ToolProbeDefinition(["-v"])
    });
    ThrowsCode("invalid", () => operations.PrepareTool("bounded", ["a", "b", "c"]));
    ThrowsCode("invalid", () => operations.PrepareTool("bounded", ["ΩΩΩ"]));
    ThrowsCode("invalid", () => operations.PrepareTool("bounded", ["ΩΩ", "ΩΩ"]));
    var invocation = operations.PrepareTool("bounded", ["ΩΩ", "a"]);
    var result = await operations.RunAsync(invocation);
    Equal("😀", result.Stdout);
    Equal("abcde", result.Stderr);
    Check(result.StdoutTruncated && result.StderrTruncated, "output bounds not reported");

    operations.Recipes.Register(new OneActionRecipeDefinition(
        "bounded-recipe", 1, RecipeActionKind.Tool, "bounded", ["{{value}}"],
        [new RecipeVariableDefinition("value", MaxUtf8Bytes: 2)]));
    ThrowsCode("invalid", () => operations.PrepareRecipe("bounded-recipe", new Dictionary<string, string> { ["value"] = "abc" }));
}

static async Task ExecutionIdentityAsync()
{
    using var temp = new TempRoot();
    var runner = new RecordingRunner();
    var operations = new ToolRecipeOperations(temp.State, runner);
    var executable = temp.Executable("identity.exe", "first identity");
    await operations.Tools.RegisterAsync(Tool(executable, "identity"));
    var invocation = operations.PrepareTool("identity", ["--work"]);
    File.WriteAllText(executable, "replacement identity", new UTF8Encoding(false));
    await ThrowsCodeAsync("integrity", async () => _ = await operations.RunAsync(invocation));
    Equal(0, runner.Calls.Count);

    var pwshExecutable = temp.Executable("identity-pwsh.exe", "pwsh identity");
    await operations.Tools.RegisterAsync(Tool(pwshExecutable, "identity-pwsh"));
    var script = temp.Script("identity.ps1", "'first script'");
    var scriptInvocation = operations.PreparePowerShell(script, toolName: "identity-pwsh");
    File.WriteAllText(script, "'replacement script'", new UTF8Encoding(false));
    await ThrowsCodeAsync("integrity", async () => _ = await operations.RunAsync(scriptInvocation));
    Equal(0, runner.Calls.Count);

    var probeExecutable = temp.Executable("probed.exe", "probed first identity");
    var probeRunner = new RecordingRunner(new ProcessResult(0, "probed v1", string.Empty, false, false, false));
    var probeOperations = new ToolRecipeOperations(temp.Dir("probe-state"), probeRunner);
    await probeOperations.Tools.RegisterAsync(Tool(probeExecutable, "probed") with
    {
        VersionProbe = new ToolProbeDefinition(["--version"], [0], "probed v1")
    });
    File.WriteAllText(probeExecutable, "probed replacement identity", new UTF8Encoding(false));
    ThrowsCode("integrity", () => probeOperations.PrepareTool("probed", []));
    Equal(1, probeRunner.Calls.Count);

    var leasedExecutable = temp.Executable("leased.exe", "leased identity");
    var replacementBlocked = false;
    var leaseRunner = new RecordingRunner(_ =>
    {
        try { File.WriteAllText(leasedExecutable, "replacement during execution", new UTF8Encoding(false)); }
        catch (IOException) { replacementBlocked = true; }
        return new ProcessResult(0, string.Empty, string.Empty, false, false, false);
    });
    var leaseOperations = new ToolRecipeOperations(temp.Dir("lease-state"), leaseRunner);
    await leaseOperations.Tools.RegisterAsync(Tool(leasedExecutable, "leased"));
    _ = await leaseOperations.RunAsync(leaseOperations.PrepareTool("leased", []));
    Check(replacementBlocked, "execution admission did not hold the executable identity lease");
}

static async Task PublicInvocationAdmissionAsync()
{
    Check(typeof(ToolInvocation).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0,
        "ToolInvocation exposes a public constructor");
    Check(typeof(ToolInvocation).GetProperties(BindingFlags.Public | BindingFlags.Instance).All(x => !x.CanWrite),
        "ToolInvocation exposes a writable public property");

    using var temp = new TempRoot();
    var executable = temp.Executable("owner.exe", "owner");
    var first = new ToolRecipeOperations(temp.State, new RecordingRunner());
    await first.Tools.RegisterAsync(Tool(executable, "owner"));
    var invocation = first.PrepareTool("owner", []);
    var secondRunner = new RecordingRunner();
    var second = new ToolRecipeOperations(temp.State, secondRunner);
    await ThrowsCodeAsync("precondition", async () => _ = await second.RunAsync(invocation));
    Equal(0, secondRunner.Calls.Count);
}

static async Task WorkspaceCallerCwdAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("caller.exe", "caller");
    var operations = new ToolRecipeOperations(temp.State, new RecordingRunner());
    await operations.Tools.RegisterAsync(Tool(executable, "caller") with
    {
        WorkingDirectoryRule = ToolWorkingDirectoryRule.Caller
    });
    var workspace = temp.Dir("cwd-workspace");
    var inside = Directory.CreateDirectory(Path.Combine(workspace, "inside")).FullName;
    var outside = temp.Dir("cwd-outside");
    var invocation = operations.PrepareTool("caller", [], workspaceRoot: workspace, cwd: "inside");
    Equal(inside, invocation.WorkingDirectory);
    ThrowsCode("precondition", () => operations.PrepareTool("caller", [], workspaceRoot: workspace, cwd: outside));
    ThrowsCode("precondition", () => operations.PrepareTool("caller", [], workspaceRoot: workspace, cwd: "..\\cwd-outside"));
}

static async Task CrossProcessRegistryAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("cross-process.exe", "cross process");
    var processes = Enumerable.Range(0, 12)
        .Select(i => StartSelf("child-register", temp.State, executable, $"child-{i:D2}"))
        .ToArray();
    var stdout = processes.Select(x => x.StandardOutput.ReadToEndAsync()).ToArray();
    var stderr = processes.Select(x => x.StandardError.ReadToEndAsync()).ToArray();
    await Task.WhenAll(processes.Select(x => x.WaitForExitAsync()));
    await Task.WhenAll(stdout.Concat(stderr));
    var failures = processes.Select((process, index) => new
        {
            process.ExitCode,
            Out = stdout[index].Result,
            Error = stderr[index].Result
        })
        .Where(x => x.ExitCode != 0)
        .ToArray();
    foreach (var process in processes) process.Dispose();
    if (failures.Length > 0)
        throw new InvalidOperationException(string.Join(Environment.NewLine,
            failures.Select(x => $"exit={x.ExitCode} out={x.Out} err={x.Error}")));

    var reloaded = new ToolRecipeOperations(temp.State, new RecordingRunner());
    var names = reloaded.Tools.List().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Equal(12, names.Count);
    for (var i = 0; i < 12; i++) Check(names.Contains($"child-{i:D2}"), $"missing child-{i:D2}");
}

static async Task UnsafeAdaptersFailClosedAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("adapter.exe", "adapter");
    var operations = new ToolRecipeOperations(temp.State, new RecordingRunner());
    foreach (var name in new[] { "7z", "git", "dotnet" })
        await operations.Tools.RegisterAsync(Tool(executable, name));
    var archive = Path.Combine(temp.Path, "unsafe.7z");
    File.WriteAllBytes(archive, [0x37, 0x7A]);
    foreach (var policy in new[] { "fail", "replace", "skip", "clean" })
        ThrowsCode("precondition", () => operations.PrepareSevenZip("extract", archive, temp.Dir($"out-{policy}"), policy));

    var cases = new[]
    {
        ("seven-recipe", RecipeActionKind.SevenZip, "7z", new[] { "x", "-oC:\\outside", "archive.7z" }),
        ("git-recipe", RecipeActionKind.Git, "git", new[] { "status" }),
        ("dotnet-recipe", RecipeActionKind.Dotnet, "dotnet", new[] { "build" })
    };
    foreach (var item in cases)
        ThrowsCode("precondition", () => operations.Recipes.Register(new OneActionRecipeDefinition(
            item.Item1, 1, item.Item2, item.Item3, item.Item4)));
}

static async Task PostconditionRecoveryAsync()
{
    using var temp = new TempRoot();
    var executable = temp.Executable("postcondition.exe", "postcondition");
    var runner = new RecordingRunner(new ProcessResult(0, "ok", string.Empty, false, false, false));
    var operations = new ToolRecipeOperations(temp.State, runner);
    await operations.Tools.RegisterAsync(Tool(executable, "postcondition") with
    {
        WorkingDirectoryRule = ToolWorkingDirectoryRule.Workspace
    });
    var workspace = temp.Dir("postcondition-workspace");
    operations.Recipes.Register(new OneActionRecipeDefinition(
        "missing-output", 1, RecipeActionKind.Tool, "postcondition", Workspace: "ws",
        Postcondition: new RecipePostcondition(RecipePostconditionKind.FileExists, "missing.txt")));
    var invocation = operations.PrepareRecipe("missing-output", workspaceRoots:
        new Dictionary<string, string> { ["ws"] = workspace });
    var missingFailure = await ThrowsExecutionCodeAsync("recovery_required", () => operations.RunAsync(invocation));
    Equal(0, missingFailure.ExitCode);
    Equal("ok", missingFailure.Stdout);
    Check(!missingFailure.TimedOut, "postcondition failure reported timeout");
    Equal(1, runner.Calls.Count);

    var target = temp.Dir("postcondition-target");
    var link = Path.Combine(workspace, "linked-output");
    var linkCreated = false;
    var linkRunner = new RecordingRunner(_ =>
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            linkCreated = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
        }
        return new ProcessResult(0, string.Empty, string.Empty, false, false, false);
    });
    var linkOperations = new ToolRecipeOperations(temp.Dir("link-state"), linkRunner);
    await linkOperations.Tools.RegisterAsync(Tool(executable, "link-postcondition") with
    {
        WorkingDirectoryRule = ToolWorkingDirectoryRule.Workspace
    });
    linkOperations.Recipes.Register(new OneActionRecipeDefinition(
        "linked-output", 1, RecipeActionKind.Tool, "link-postcondition", Workspace: "ws",
        Postcondition: new RecipePostcondition(RecipePostconditionKind.DirectoryExists, "linked-output")));
    var linkInvocation = linkOperations.PrepareRecipe("linked-output", workspaceRoots:
        new Dictionary<string, string> { ["ws"] = workspace });
    _ = await ThrowsExecutionCodeAsync("recovery_required", () => linkOperations.RunAsync(linkInvocation));
    if (!linkCreated) Console.WriteLine("INFO postcondition symlink creation unavailable; missing-path recovery still exercised");
}

static async Task DefaultRunnerContractAsync()
{
    using var temp = new TempRoot();
    var executable = SelfAppHost();
    var operations = new ToolRecipeOperations(temp.State);
    await operations.Tools.RegisterAsync(Tool(executable, "real-tool") with
    {
        VersionProbe = new ToolProbeDefinition(["--version"], [0], "focused test tool"),
        MaxArguments = 16,
        MaxArgumentUtf8Bytes = 4096,
        MaxTotalArgumentUtf8Bytes = 16 * 1024
    });
    var difficult = new[] { "space value", "apostrophe's", "[brackets]", "Ω", "a&b", "\"quoted\"", string.Empty };
    var invocation = operations.PrepareTool("real-tool", ["child-echo", .. difficult]);
    var result = await operations.RunAsync(invocation);
    Equal(string.Join('\u001f', difficult), result.Stdout);
    Equal(string.Empty, result.Stderr);

    const string ambientName = "PAO_TOOL_RECIPE_AMBIENT_FIXTURE";
    const string explicitName = "PAO_TOOL_RECIPE_EXPLICIT_FIXTURE";
    var previousAmbient = Environment.GetEnvironmentVariable(ambientName);
    Environment.SetEnvironmentVariable(ambientName, "ambient-value");
    try
    {
        var replacement = new Dictionary<string, string?>(MinimumToolEnvironment(executable), StringComparer.OrdinalIgnoreCase)
        {
            [explicitName] = "explicit-value"
        };
        await operations.Tools.RegisterAsync(Tool(executable, "replacement-env") with { Environment = replacement });
        var replaced = await operations.RunAsync(operations.PrepareTool("replacement-env", ["child-env", ambientName, explicitName]));
        Check(new string?[] { null, "explicit-value" }.SequenceEqual(JsonSerializer.Deserialize<string?[]>(replaced.Stdout)!),
            "replacement environment leaked ambient state or lost its explicit value");

        await operations.Tools.RegisterAsync(Tool(executable, "overlay-env") with
        {
            EnvironmentMode = ToolEnvironmentMode.InheritWithOverlay,
            Environment = new Dictionary<string, string?> { [explicitName] = "overlay-value" }
        });
        var overlaid = await operations.RunAsync(operations.PrepareTool("overlay-env", ["child-env", ambientName, explicitName]));
        Check(new string?[] { "ambient-value", "overlay-value" }.SequenceEqual(JsonSerializer.Deserialize<string?[]>(overlaid.Stdout)!),
            "explicit inherited-overlay environment was not honored");
    }
    finally
    {
        Environment.SetEnvironmentVariable(ambientName, previousAmbient);
    }

    var fake = temp.Executable("encoding.exe", "encoding");
    await ThrowsCodeAsync("precondition", () => operations.Tools.RegisterAsync(Tool(fake, "ascii-output") with
    {
        StdoutEncoding = "ascii"
    }));
    await ThrowsCodeAsync("precondition", () => operations.Tools.RegisterAsync(Tool(fake, "stdin-enabled") with
    {
        StdinEncoding = "utf8"
    }));
    await ThrowsCodeAsync("invalid", () => operations.Tools.RegisterAsync(Tool(fake, "oversized-output") with
    {
        MaxOutputBytes = 64 * 1024 + 1
    }));
    await ThrowsCodeAsync("precondition", () => operations.Tools.RegisterAsync(Tool(fake, "unsafe-probe") with
    {
        VersionProbe = new ToolProbeDefinition(["--password", "opaque"])
    }));
}

static async Task ExternalFailureEvidenceAsync()
{
    using var temp = new TempRoot();
    var executable = SelfAppHost();
    var operations = new ToolRecipeOperations(temp.State);
    await operations.Tools.RegisterAsync(Tool(executable, "failing-tool") with
    {
        MaxOutputBytes = 8,
        MaxArguments = 8,
        MaxArgumentUtf8Bytes = 4096,
        MaxTotalArgumentUtf8Bytes = 8192
    });

    var failure = await ThrowsExecutionCodeAsync(
        "external_failed",
        () => operations.RunAsync(operations.PrepareTool("failing-tool", ["child-fail"])));
    Equal(23, failure.ExitCode);
    Equal("stdout-a", failure.Stdout);
    Equal("stderr-a", failure.Stderr);
    Check(failure.StdoutTruncated && failure.StderrTruncated, "failed output was not bounded honestly");
    Check(!failure.TimedOut, "unexpected exit reported as timeout");
    Check(typeof(ToolExecutionException).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).All(x => !x.CanWrite),
        "execution failure evidence exposes a writable property");

    operations.Recipes.Register(new OneActionRecipeDefinition(
        "discard-failure",
        1,
        RecipeActionKind.Tool,
        "failing-tool",
        ["child-fail"],
        OutputBehavior: ToolOutputBehavior.Discard));
    var discarded = await ThrowsExecutionCodeAsync(
        "external_failed",
        () => operations.RunAsync(operations.PrepareRecipe("discard-failure")));
    Equal(string.Empty, discarded.Stdout);
    Equal(string.Empty, discarded.Stderr);
    Check(discarded.StdoutTruncated && discarded.StderrTruncated,
        "discarded failure did not retain truncation evidence");

    await operations.Tools.RegisterAsync(Tool(executable, "timeout-tool") with
    {
        TimeoutSeconds = 1,
        MaxArguments = 8,
        MaxArgumentUtf8Bytes = 4096,
        MaxTotalArgumentUtf8Bytes = 8192
    });
    var marker = Path.Combine(temp.Path, "grandchild-survived.txt");
    var identityPath = Path.Combine(temp.Path, "grandchild.identity");
    try
    {
        var timeout = await ThrowsExecutionCodeAsync(
            "timeout",
            () => operations.RunAsync(operations.PrepareTool(
                "timeout-tool",
                ["child-timeout-parent", marker, identityPath])));
        Check(timeout.TimedOut, "timeout evidence omitted timedOut");
        Equal("timeout-out", timeout.Stdout);
        Equal("timeout-err", timeout.Stderr);
        Check(!timeout.StdoutTruncated && !timeout.StderrTruncated, "small timeout evidence reported truncated");
        Check(File.Exists(identityPath), "timeout helper did not publish grandchild identity");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Check(!File.Exists(marker), "owned grandchild survived timeout and wrote its marker");
        Check(!IsOwnedHelperAlive(identityPath), "owned grandchild remained alive after timeout");
    }
    finally
    {
        await StopOwnedHelperAsync(identityPath);
    }
}

static RegisteredToolDefinition Tool(string executable, string name) => new(
    name,
    1,
    executable,
    WorkingDirectoryRule: ToolWorkingDirectoryRule.ExecutableDirectory,
    ExpectedExitCodes: [0],
    VersionProbe: new ToolProbeDefinition(["--version"]),
    Environment: MinimumToolEnvironment(executable));

static IReadOnlyDictionary<string, string?> MinimumToolEnvironment(string executable)
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT" })
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) values[key] = value;
    if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable), "dotnet.exe"))
        values["DOTNET_ROOT"] = Path.GetDirectoryName(Path.GetFullPath(executable));
    return values;
}

static string SelfAppHost()
{
    var path = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
    if (!File.Exists(path)) throw new FileNotFoundException("focused test apphost missing", path);
    return Path.GetFullPath(path);
}

static string Sha(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static Process StartSelf(params string[] arguments)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing process path");
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet"))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("child process did not start");
}

static void ThrowsCode(string code, Action action)
{
    try
    {
        action();
        throw new InvalidOperationException($"expected {code}");
    }
    catch (AgentOpsException ex) when (ex.Code == code)
    {
    }
}

static async Task ThrowsCodeAsync(string code, Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException($"expected {code}");
    }
    catch (AgentOpsException ex) when (ex.Code == code)
    {
    }
}

static async Task<ToolExecutionException> ThrowsExecutionCodeAsync(
    string code,
    Func<Task<ToolExecutionResult>> action)
{
    try
    {
        _ = await action();
        throw new InvalidOperationException($"expected {code}");
    }
    catch (ToolExecutionException ex) when (ex.Code == code)
    {
        return ex;
    }
}

static bool IsOwnedHelperAlive(string identityPath)
{
    if (!TryReadProcessIdentity(identityPath, out var pid, out var startTicks)) return false;
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return false;
    }
}

static async Task StopOwnedHelperAsync(string identityPath)
{
    if (!TryReadProcessIdentity(identityPath, out var pid, out var startTicks)) return;
    try
    {
        using var process = Process.GetProcessById(pid);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
    }
}

static bool TryReadProcessIdentity(string identityPath, out int pid, out long startTicks)
{
    pid = 0;
    startTicks = 0;
    try
    {
        if (!File.Exists(identityPath)) return false;
        var parts = File.ReadAllText(identityPath, Encoding.UTF8).Split('|');
        return parts.Length == 2 &&
               int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid) &&
               long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out startTicks);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return false;
    }
}

static async Task ThrowsAnyAsync<TFirst, TSecond>(Func<Task> action)
    where TFirst : Exception
    where TSecond : Exception
{
    try
    {
        await action();
        throw new InvalidOperationException($"expected {typeof(TFirst).Name} or {typeof(TSecond).Name}");
    }
    catch (Exception ex) when (ex is TFirst or TSecond)
    {
    }
}

static void SequenceEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        throw new InvalidOperationException($"expected=[{string.Join(" | ", expected)}] actual=[{string.Join(" | ", actual)}]");
}

static void BytesEqual(byte[] expected, byte[] actual)
{
    if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("byte mismatch");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected={expected} actual={actual}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class RecordingRunner : IToolInvocationRunner
{
    private readonly Queue<ProcessResult> _results;
    private readonly Func<ToolInvocation, ProcessResult>? _callback;

    public RecordingRunner(params ProcessResult[] results) => _results = new Queue<ProcessResult>(results);

    public RecordingRunner(Func<ToolInvocation, ProcessResult> callback)
    {
        _callback = callback;
        _results = [];
    }

    public List<ToolInvocation> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(invocation);
        var result = _callback is not null
            ? _callback(invocation)
            : _results.Count > 0
            ? _results.Dequeue()
            : new ProcessResult(0, string.Empty, string.Empty, false, false, false);
        return Task.FromResult(result);
    }
}

sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pao-tool-recipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        State = Dir("state");
    }

    public string Path { get; }
    public string State { get; }

    public string Dir(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

    public string Executable(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string Script(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
