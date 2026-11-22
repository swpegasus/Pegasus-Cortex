using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class DotnetSdkEnvironmentTests
{
    private const string Sentinel = "PAO_SDK_UNRELATED_SENTINEL";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string executable)
    {
        executable = Path.GetFullPath(executable);
        Check(File.Exists(executable) && Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase),
            "The real installed dotnet.exe is required; this test must not use the fake tool.");
        using var fixture = new TempRoot();
        Check(!fixture.Path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !fixture.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)),
            "The SDK integration fixture requires a local, non-OneDrive temporary root.");
        var priorSentinel = Environment.GetEnvironmentVariable(Sentinel);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            Environment.SetEnvironmentVariable(Sentinel, "must-not-enter-msbuild");
            await VerifyAsync(fixture, executable, timeout.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Sentinel, priorSentinel);
        }
    }

    private static async Task VerifyAsync(TempRoot fixture, string executable, CancellationToken cancellationToken)
    {
        var state = fixture.Dir("sdk-state");
        var workspace = fixture.Dir("sdk-project");
        var localFeed = fixture.Dir("empty-feed");
        var packages = fixture.Dir("packages");
        var cliHome = fixture.Dir("cli-home");
        var childTemp = fixture.Dir("child-temp");
        var httpCache = fixture.Dir("http-cache");
        var nugetScratch = fixture.Dir("nuget-scratch");
        var pluginsCache = fixture.Dir("plugins-cache");
        await using var engine = new AgentOpsEngine(state);
        const string workspaceAlias = "sdkfixture";
        var workspaceResponse = await PaoProgram.CallUnifiedMcpToolAsync(
            JsonSerializer.SerializeToElement(new { op = "ctx_add", name = workspaceAlias, src = workspace }),
            new EngineDispatcher(engine), cancellationToken);
        var workspaceReceipt = workspaceResponse["structuredContent"]?.Deserialize<OperationReceipt>(Json);
        Check(workspaceReceipt is { Ok: true, ExitCode: 0 } &&
            workspaceResponse["structuredContent"]?["data"]?["ws"]?.GetValue<string>() == workspaceAlias &&
            StringComparer.OrdinalIgnoreCase.Equals(workspace,
                workspaceResponse["structuredContent"]?["data"]?["path"]?.GetValue<string>()),
            "Task-local workspace registration failed: " + Bounded(workspaceResponse));
        using var executableStream = File.OpenRead(executable);
        var executableHash = Convert.ToHexString(SHA256.HashData(executableStream)).ToLowerInvariant();

        // No public-registration probe runs against the real user profile. Only the
        // explicit test-local replacement below may execute the installed SDK.
        var definition = new RegisteredToolDefinition("dotnet", 1, executable, executableHash,
            WorkingDirectoryRule: ToolWorkingDirectoryRule.Workspace, TimeoutSeconds: 60,
            ExpectedExitCodes: [0], MaxArguments: 64, MaxOutputBytes: 16 * 1024);
        var added = await engine.ExecuteAsync(new OperationRequest("tool",
            new Dictionary<string, string> { ["op"] = "add" },
            Body: JsonSerializer.Serialize(definition, Json)), cancellationToken);
        Check(added.Ok && added.ExitCode == 0, "Fresh public tool registration failed: " + added.Err);
        var registry = new ToolRecipeOperations(state).Tools;
        var original = registry.Get("dotnet");
        Check(original.DefinitionVersion == 1 && !original.ProbeVerified &&
            original.EnvironmentMode == ToolEnvironmentMode.Replace, "Unexpected original registration state.");
        var originalEnvironment = original.Environment ?? throw new InvalidOperationException("Public registration omitted its environment.");
        foreach (var key in new[]
                 {
                     "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)",
                     "ProgramW6432", "ProgramData", "CommonProgramFiles", "CommonProgramFiles(x86)",
                     "CommonProgramW6432", "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES"
                 })
            Check(originalEnvironment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value),
                "Public registration omitted a canonical runtime directory: " + key);
        Check(!originalEnvironment.ContainsKey(Sentinel), "Public registration inherited an unrelated environment variable.");

        var originalBytes = await File.ReadAllBytesAsync(registry.StorePath, cancellationToken);
        Check(new ToolRecipeOperations(state).Tools.Get("dotnet").DefinitionVersion == 1 &&
            registry.List().Single(tool => tool.Name == "dotnet").DefinitionVersion == 1,
            "Reading an existing tool unexpectedly replaced its registration.");
        var afterReads = await File.ReadAllBytesAsync(registry.StorePath, cancellationToken);
        Check(originalBytes.SequenceEqual(afterReads),
            "Reading an existing registration changed its stored definition.");

        // Preserve the production-generated standard Windows paths. These narrowly
        // scoped overlays prevent telemetry, workload updates and SDK/cache writes
        // outside the exact disposable fixture. This is not installed acceptance.
        var isolatedEnvironment = new Dictionary<string, string?>(originalEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_CLI_HOME"] = cliHome,
            ["NUGET_PACKAGES"] = packages,
            ["NUGET_HTTP_CACHE_PATH"] = httpCache,
            ["NUGET_SCRATCH"] = nugetScratch,
            ["NUGET_PLUGINS_CACHE_PATH"] = pluginsCache,
            ["TEMP"] = childTemp,
            ["TMP"] = childTemp,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true",
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en-US"
        };
        var replacement = original with
        {
            Environment = isolatedEnvironment,
            VersionProbe = new ToolProbeDefinition(["--version"], [0], "10.0.")
        };
        try
        {
            await registry.RegisterAsync(replacement, cancellationToken);
            throw new InvalidOperationException("A same-version registration replacement was admitted.");
        }
        catch (AgentOpsException exception) when (exception.Code == "precondition") { }
        var afterRejection = await File.ReadAllBytesAsync(registry.StorePath, cancellationToken);
        Check(originalBytes.SequenceEqual(afterRejection),
            "Rejected same-version replacement changed the original stored registration.");
        var admitted = await registry.RegisterAsync(replacement with { DefinitionVersion = 2 }, cancellationToken);
        Check(admitted.DefinitionVersion == 2 && admitted.ProbeVerified,
            "The explicit higher-version test-local SDK registration was not probed and admitted.");

        var project = Path.Combine(workspace, "OfflineSdkFixture.csproj");
        var nugetConfig = Path.Combine(workspace, "NuGet.Config");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Library</OutputType>
                <UseSharedCompilation>false</UseSharedCompilation>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
              <Target Name="RejectInheritedSentinel" BeforeTargets="CoreCompile">
                <Error Condition="'$(PAO_SDK_UNRELATED_SENTINEL)' != ''" Text="Unrelated environment entered the SDK child." />
              </Target>
              <Target Name="RecordActualCompile" AfterTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/compiled.marker" Lines="core-compile-completed" Overwrite="true" />
              </Target>
            </Project>
            """, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Arithmetic.cs"),
            "namespace OfflineSdkFixture; public static class Arithmetic { public static int Add(int left, int right) => left + right; }",
            new UTF8Encoding(false), cancellationToken);
        var config = new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "task-local-empty"), new XAttribute("value", localFeed))),
            new XElement("fallbackPackageFolders", new XElement("clear")),
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"), new XAttribute("value", packages)))));
        await File.WriteAllTextAsync(nugetConfig, config.ToString(), new UTF8Encoding(false), cancellationToken);

        string[] isolationArguments =
        [
            "--disable-build-servers", "--verbosity", "minimal", "--tl:off", "-m:1", "-nr:false",
            "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false",
            "-p:NuGetAudit=false", "-p:UseSharedCompilation=false"
        ];
        await ExecuteTypedAsync(engine, new
        {
            op = "dotnet_restore", src = Path.GetFileName(project), ws = workspaceAlias,
            arg = isolationArguments.Concat(new[] { "--configfile", nugetConfig, "--source", localFeed, "--packages", packages, "--disable-parallel" }).ToArray()
        }, "offline restore", cancellationToken);
        var assets = Path.Combine(workspace, "obj", "project.assets.json");
        var marker = Path.Combine(workspace, "compiled.marker");
        var assembly = Path.Combine(workspace, "bin", "Debug", "net10.0", "OfflineSdkFixture.dll");
        Check(File.Exists(assets) && !File.Exists(marker) && !File.Exists(assembly),
            "Restore did not produce assets alone for the subsequent real compilation.");
        var assetsBytes = await File.ReadAllBytesAsync(assets, cancellationToken);
        using (var parsedAssets = JsonDocument.Parse(assetsBytes))
        {
            var sources = parsedAssets.RootElement.GetProperty("project").GetProperty("restore").GetProperty("sources");
            Check(sources.EnumerateObject().Count() == 1 && sources.TryGetProperty(localFeed, out _),
                "Restored assets admitted a source outside the task-owned empty local feed.");
        }

        await ExecuteTypedAsync(engine, new
        {
            op = "dotnet_build", src = Path.GetFileName(project), ws = workspaceAlias, cfg = "Debug",
            arg = isolationArguments.Append("--no-restore").ToArray()
        }, "existing-assets compilation", cancellationToken);
        Check(File.Exists(assembly) && new FileInfo(assembly).Length > 0 && File.Exists(marker) &&
            (await File.ReadAllTextAsync(marker, cancellationToken)).Trim() == "core-compile-completed",
            "The real SDK did not reach compilation and produce the fixture assembly.");
        var afterBuild = await File.ReadAllBytesAsync(assets, cancellationToken);
        Check(assetsBytes.SequenceEqual(afterBuild),
            "The --no-restore build rewrote the existing assets instead of consuming them unchanged.");
        Console.WriteLine("EVIDENCE real installed SDK typed-adapter integration fixture: local-only restore; unchanged existing assets; actual compilation; unrelated sentinel absent; explicit test-local hygiene overlay, not installed registration acceptance.");
    }

    private static async Task ExecuteTypedAsync(AgentOpsEngine engine, object arguments, string phase, CancellationToken cancellationToken)
    {
        var response = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(arguments),
            new EngineDispatcher(engine), cancellationToken);
        var receipt = response["structuredContent"]?.Deserialize<OperationReceipt>(Json);
        Check(receipt is { Ok: true, ExitCode: 0, Partial: false }, phase + " failed: " + Bounded(response));
        Check(receipt!.Data is not null && receipt.Data.TryGetValue("exit", out var exit) &&
            Convert.ToString(exit, System.Globalization.CultureInfo.InvariantCulture) == "0",
            phase + " did not report a successful real child exit.");
    }

    private static string Bounded(JsonObject response)
    {
        var text = response["structuredContent"]?.ToJsonString(Json) ?? response.ToJsonString(Json);
        return text.Length <= 4096 ? text : text[..4096] + " [truncated]";
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
