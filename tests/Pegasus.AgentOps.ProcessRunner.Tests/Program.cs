using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;

if (Environment.GetEnvironmentVariable("DOTNET_ROOT_X64") is not { Length: > 0 } &&
    Environment.GetEnvironmentVariable("PAO_TEST_DOTNET") is { Length: > 0 } testDotnet &&
    File.Exists(testDotnet))
    Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", Path.GetDirectoryName(Path.GetFullPath(testDotnet)));

if (args.Length > 0 && args[0].StartsWith("--helper-", StringComparison.Ordinal))
    return await RunHelperAsync(args);

var tests = new (string Name, Func<Task> Run)[]
{
    ("exact_argv_cwd_environment_and_unicode_output", ExactArgvAsync),
    ("explicit_environment_isolated_and_concurrent", ExplicitEnvironmentAsync),
    ("request_admission_is_incrementally_bounded", RequestAdmissionBoundsAsync),
    ("zero_timeout_and_slow_admission_launch_nothing", PrelaunchTimeoutAsync),
    ("partial_request_send_obeys_timeout", PartialSendTimeoutAsync),
    ("bounded_stdout_and_stderr", BoundedOutputAsync),
    ("timeout_kills_assigned_immediate_child_tree", TimeoutTreeAsync),
    ("cancellation_kills_assigned_immediate_child_tree", CancellationTreeAsync),
    ("powershell_native_exit_and_hidden_window", NativeShellExitAsync),
    ("powershell_native_timeout_cleans_owned_tree", NativeShellTimeoutAsync),
    ("powershell_native_cancellation_cleans_owned_tree", NativeShellCancellationAsync),
    ("registered_native_environment_preserves_explicit_policy", RegisteredNativeEnvironmentAsync),
    ("root_exit_with_inherited_stdout_cannot_deadlock", RootExitDescendantAsync),
    ("protocol_fault_is_bounded_and_mapped", ProtocolFaultAsync),
    ("response_bounds_are_exact", ResponseBoundsAsync),
    ("noncanonical_executable_is_rejected_prelaunch", CanonicalExecutableAsync),
    ("assignment_failure_cleans_blocked_host", () => FailureCleanupAsync(ProcessLaunchFailurePoint.Assign)),
    ("transport_failure_cleans_assigned_blocked_host", () => FailureCleanupAsync(ProcessLaunchFailurePoint.Send))
};

var failed = 0;
foreach (var test in tests)
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
    finally
    {
        ProcessRunnerTestHooks.Reset();
    }
}

Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static async Task<int> RunHelperAsync(string[] helperArgs)
{
    if (helperArgs[0] == "--helper-native-shell")
    {
        Console.OutputEncoding = Encoding.UTF8;
        var console = GetConsoleWindow();
        await WriteJsonAsync(helperArgs[1], new NativeShellEvidence(
            Environment.ProcessId, IsCurrentProcessInJob(), console.ToInt64(), IsWindowVisible(console)));
        Console.Out.Write("nested-stdout-😀");
        Console.Error.Write("nested-stderr-Ω");
        await Task.Delay(int.Parse(helperArgs[3], CultureInfo.InvariantCulture));
        return int.Parse(helperArgs[2], CultureInfo.InvariantCulture);
    }

    if (helperArgs[0] == "--helper-capture")
    {
        var payload = new CapturePayload(
            helperArgs.Skip(2).ToArray(),
            Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable("PAO_PROCESS_RUNNER_TEST_ENV"),
            Environment.GetEnvironmentVariable("PAO_UNRELATED_SECRET"),
            Environment.GetEnvironmentVariable("PAO_EMPTY_VALUE"));
        await WriteJsonAsync(helperArgs[1], payload);
        Console.OutputEncoding = Encoding.UTF8;
        Console.Error.Write("stderr-Ω");
        Console.Write("stdout-😀");
        return 0;
    }

    if (helperArgs[0] == "--helper-output")
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Out.Write(new string('o', 70_000));
        Console.Error.Write(new string('e', 70_000));
        return 0;
    }

    if (helperArgs[0] == "--helper-tree")
    {
        var parentInJob = IsCurrentProcessInJob();
        Console.Write("tree-started");
        Console.Out.Flush();
        using var child = StartSelf("--helper-grandchild", helperArgs[2], helperArgs[3]);
        await WriteJsonAsync(helperArgs[1], new TreeEvidence(Environment.ProcessId, parentInJob, child.Id));
        await Task.Delay(int.Parse(helperArgs[3], CultureInfo.InvariantCulture));
        return 0;
    }

    if (helperArgs[0] == "--helper-grandchild")
    {
        if (helperArgs.Length > 3 && helperArgs[3] == "close-stderr") CloseCurrentStandardError();
        await WriteJsonAsync(helperArgs[1], new TreeEvidence(Environment.ProcessId, IsCurrentProcessInJob(), null));
        await Task.Delay(int.Parse(helperArgs[2], CultureInfo.InvariantCulture));
        return 0;
    }

    if (helperArgs[0] == "--helper-touch")
    {
        await File.WriteAllTextAsync(helperArgs[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        await Task.Delay(30_000);
        return 0;
    }

    if (helperArgs[0] == "--helper-sleep")
    {
        await Task.Delay(int.Parse(helperArgs[1], CultureInfo.InvariantCulture));
        return 0;
    }

    if (helperArgs[0] == "--helper-root-exit")
    {
        using var child = StartSelfWithOnlyStdoutInherited(
            "--helper-grandchild",
            helperArgs[1],
            helperArgs[2],
            "close-stderr");
        Console.Write("root-exit");
        Console.Out.Flush();
        return 0;
    }

    return 2;
}

static async Task ExactArgvAsync()
{
    using var temp = new TempRoot();
    var workingDirectory = Path.Combine(temp.Path, "cwd Ω & space");
    Directory.CreateDirectory(workingDirectory);
    var capture = Path.Combine(temp.Path, "capture.json");
    string[] expected =
    [
        "plain",
        "two words",
        "apostrophe's",
        "[brackets]",
        "Unicode-😀-Ω",
        "ampersand&value",
        "quoted \"value\"",
        string.Empty,
        "trailing\\",
        "slashes\\\\before\"quote"
    ];

    var prior = Environment.GetEnvironmentVariable("PAO_PROCESS_RUNNER_TEST_ENV");
    var priorSecret = Environment.GetEnvironmentVariable("PAO_UNRELATED_SECRET");
    try
    {
        Environment.SetEnvironmentVariable("PAO_PROCESS_RUNNER_TEST_ENV", "env Ω & exact");
        Environment.SetEnvironmentVariable("PAO_UNRELATED_SECRET", "must-not-cross");
        var result = await RunSelfAsync(
            ["--helper-capture", capture, .. expected],
            TimeSpan.FromSeconds(10),
            workingDirectory,
            CancellationToken.None);
        Equal(0, result.ExitCode);
        Check(!result.TimedOut, "capture timed out");
        Equal("stdout-😀", result.Stdout);
        Equal("stderr-Ω", result.Stderr);

        var payload = await ReadJsonAsync<CapturePayload>(capture);
        SequenceEqual(expected, payload.Arguments);
        Equal(Path.GetFullPath(workingDirectory), Path.GetFullPath(payload.WorkingDirectory));
        Equal("env Ω & exact", payload.EnvironmentValue);
        Equal<string?>(null, payload.UnrelatedSecret);
        Equal(string.Empty, payload.EmptyValue);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PAO_PROCESS_RUNNER_TEST_ENV", prior);
        Environment.SetEnvironmentVariable("PAO_UNRELATED_SECRET", priorSecret);
    }
}

static async Task ExplicitEnvironmentAsync()
{
    using var temp = new TempRoot();
    var priorSecret = Environment.GetEnvironmentVariable("PAO_UNRELATED_SECRET");
    try
    {
        Environment.SetEnvironmentVariable("PAO_UNRELATED_SECRET", "ambient-secret");
        var firstPath = Path.Combine(temp.Path, "first.json");
        var secondPath = Path.Combine(temp.Path, "second.json");
        var first = RunSelfWithEnvironmentAsync(
            ["--helper-capture", firstPath],
            TimeSpan.FromSeconds(10),
            temp.Path,
            ReplacementEnvironment("first"),
            CancellationToken.None);
        var second = RunSelfWithEnvironmentAsync(
            ["--helper-capture", secondPath],
            TimeSpan.FromSeconds(10),
            temp.Path,
            ReplacementEnvironment("second"),
            CancellationToken.None);
        await Task.WhenAll(first, second);
        Equal(0, first.Result.ExitCode);
        Equal(0, second.Result.ExitCode);

        var firstPayload = await ReadJsonAsync<CapturePayload>(firstPath);
        var secondPayload = await ReadJsonAsync<CapturePayload>(secondPath);
        Equal("first", firstPayload.EnvironmentValue);
        Equal("second", secondPayload.EnvironmentValue);
        Equal<string?>(null, firstPayload.UnrelatedSecret);
        Equal<string?>(null, secondPayload.UnrelatedSecret);
        Equal(string.Empty, firstPayload.EmptyValue);
        Equal(string.Empty, secondPayload.EmptyValue);

        var overlayPath = Path.Combine(temp.Path, "overlay.json");
        var overlay = new Dictionary<string, string?>
        {
            ["PAO_UNRELATED_SECRET"] = null,
            ["PAO_PROCESS_RUNNER_TEST_ENV"] = "overlay",
            ["PAO_EMPTY_VALUE"] = string.Empty
        };
        var overlayResult = await RunSelfWithEnvironmentAsync(
            ["--helper-capture", overlayPath],
            TimeSpan.FromSeconds(10),
            temp.Path,
            ProcessEnvironment.InheritCurrent(overlay),
            CancellationToken.None);
        Equal(0, overlayResult.ExitCode);
        var overlayPayload = await ReadJsonAsync<CapturePayload>(overlayPath);
        Equal("overlay", overlayPayload.EnvironmentValue);
        Equal<string?>(null, overlayPayload.UnrelatedSecret);

        ThrowsCode(
            "invalid",
            () => ProcessEnvironment.Replace(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PAO_CASE"] = "one",
                ["pao_case"] = "two"
            }));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PAO_UNRELATED_SECRET", priorSecret);
    }
}

static Task RequestAdmissionBoundsAsync()
{
    using var temp = new TempRoot();
    var invocation = SelfInvocation([]);
    var singleRead = new SingleReadArguments(["one", "two"]);
    var frozen = ProcessHostWire.CreateRequest(
        invocation.File,
        singleRead,
        temp.Path,
        ProcessEnvironment.Empty,
        CancellationToken.None);
    Array.Clear(frozen);
    Check(singleRead.ReadCounts.All(count => count == 1), "mutable argument source was read after its admission snapshot");

    var exact = ExactRequestArguments(invocation.File, temp.Path);
    var request = ProcessHostWire.CreateRequest(
        invocation.File,
        exact,
        temp.Path,
        ProcessEnvironment.Empty,
        CancellationToken.None);
    try
    {
        Equal(ProcessHostWire.MaxRequestBytes, request.Length);
    }
    finally
    {
        Array.Clear(request);
    }

    var over = exact.ToArray();
    over[^1] += "x";
    ThrowsCode(
        "invalid",
        () => ProcessHostWire.CreateRequest(
            invocation.File,
            over,
            temp.Path,
            ProcessEnvironment.Empty,
            CancellationToken.None));
    return Task.CompletedTask;
}

static async Task PrelaunchTimeoutAsync()
{
    using var temp = new TempRoot();
    var invocation = SelfInvocation([]);
    var created = 0;
    ProcessRunnerTestHooks.ProcessCreated = _ => created++;
    var zero = await ProcessRunner.RunAsync(
        invocation.File,
        invocation.Arguments,
        TimeSpan.Zero,
        temp.Path,
        TestEnvironment(),
        CancellationToken.None);
    Check(zero.TimedOut && created == 0, "zero timeout launched the process host");

    var slow = await ProcessRunner.RunAsync(
        invocation.File,
        new SlowArguments(32, TimeSpan.FromMilliseconds(25)),
        TimeSpan.FromMilliseconds(10),
        temp.Path,
        TestEnvironment(),
        CancellationToken.None);
    Check(slow.TimedOut && created == 0, "admission timeout launched the process host");
}

static async Task PartialSendTimeoutAsync()
{
    using var temp = new TempRoot();
    var marker = Path.Combine(temp.Path, "partial-send.marker");
    var hostPid = 0;
    ProcessRunnerTestHooks.ProcessCreated = pid => hostPid = pid;
    ProcessRunnerTestHooks.WriteRequestAsync = async (stream, request, token) =>
    {
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, request.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(request.AsMemory(0, Math.Min(16, request.Length)), token);
        await stream.FlushAsync(token);
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    };

    var stopwatch = Stopwatch.StartNew();
    var result = await RunSelfAsync(
        ["--helper-touch", marker],
        TimeSpan.FromMilliseconds(200),
        temp.Path,
        CancellationToken.None);
    Check(result.TimedOut, "partial send did not time out");
    Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "partial send exceeded its bound");
    Check(hostPid > 0, "partial send did not create the blocked host");
    await WaitForExitAsync(hostPid);
    Check(!File.Exists(marker), "partial request launched target user code");
}

static async Task BoundedOutputAsync()
{
    using var temp = new TempRoot();
    var result = await RunSelfAsync(
        ["--helper-output"],
        TimeSpan.FromSeconds(10),
        temp.Path,
        CancellationToken.None);
    Equal(0, result.ExitCode);
    Equal(65_536, result.Stdout.Length);
    Equal(65_536, result.Stderr.Length);
    Check(result.StdoutTruncated && result.StderrTruncated, "capture limits were not reported");
}

static async Task TimeoutTreeAsync()
{
    using var temp = new TempRoot();
    using var unrelated = new ProcessLease(StartSelf("--helper-sleep", "30000"));
    var parentPath = Path.Combine(temp.Path, "timeout-parent.json");
    var childPath = Path.Combine(temp.Path, "timeout-child.json");
    var result = await RunSelfAsync(
        ["--helper-tree", parentPath, childPath, "30000"],
        TimeSpan.FromSeconds(2),
        temp.Path,
        CancellationToken.None);

    Check(result.TimedOut, "tree operation did not time out");
    Equal("tree-started", result.Stdout);
    var parent = await ReadJsonAsync<TreeEvidence>(parentPath);
    var child = await ReadJsonAsync<TreeEvidence>(childPath);
    Check(parent.InJob, "parent user code ran before job assignment");
    Check(child.InJob, "immediate grandchild escaped the job");
    Equal(parent.ChildPid, child.Pid);
    await WaitForExitAsync(parent.Pid);
    await WaitForExitAsync(child.Pid);
    Check(!unrelated.Process.HasExited, "timeout killed a process outside the owned job");
}

static async Task CancellationTreeAsync()
{
    using var temp = new TempRoot();
    var parentPath = Path.Combine(temp.Path, "cancel-parent.json");
    var childPath = Path.Combine(temp.Path, "cancel-child.json");
    using var canceled = new CancellationTokenSource();
    var running = RunSelfAsync(
        ["--helper-tree", parentPath, childPath, "30000"],
        TimeSpan.FromSeconds(30),
        temp.Path,
        canceled.Token);

    var parent = await ReadJsonAsync<TreeEvidence>(parentPath);
    var child = await ReadJsonAsync<TreeEvidence>(childPath);
    Check(parent.InJob && child.InJob, "cancellation tree was not contained before user code");
    canceled.Cancel();
    await ThrowsCanceledAsync(() => running);
    await WaitForExitAsync(parent.Pid);
    await WaitForExitAsync(child.Pid);
}

static async Task NativeShellExitAsync()
{
    foreach (var exitCode in new[] { 0, 23 })
    {
        using var temp = new TempRoot();
        using var canceled = new CancellationTokenSource();
        await using var windows = new OwnedWindowObservation();
        ProcessRunnerTestHooks.ProcessCreated = windows.AddProcess;
        var running = RunNativeShellAsync(temp.Path, exitCode, 400, TimeSpan.FromSeconds(15),
            canceled.Token, legacyEnvironment: exitCode == 23);
        try
        {
            var shell = await ReadExecutionEvidenceAsync<PowerShellEvidence>(Path.Combine(temp.Path, "shell.json"), running);
            windows.AddProcess(shell.Pid);
            var native = await ReadExecutionEvidenceAsync<NativeShellEvidence>(Path.Combine(temp.Path, "native.json"), running);
            windows.AddProcess(native.Pid);
            windows.AddConsole(native.ConsoleWindow);
            var result = await running;
            Equal(exitCode, result.ExitCode);
            Check(!result.TimedOut, "nested native execution timed out");
            Equal("nested-stdout-😀", result.Stdout);
            Equal("nested-stderr-Ω", result.Stderr);
            Check(shell.Major >= 7, "the fixture did not use PowerShell 7 or newer");
            Check(native.InJob, "PowerShell native child escaped the owned job");
            await WaitForExitAsync(shell.Pid);
            await WaitForExitAsync(native.Pid);
            Check(!native.ConsoleVisible && !await windows.CompleteAsync(), "nested native execution exposed an owned window");
        }
        finally
        {
            canceled.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}

static async Task NativeShellTimeoutAsync()
{
    using var temp = new TempRoot();
    using var canceled = new CancellationTokenSource();
    await using var windows = new OwnedWindowObservation();
    ProcessRunnerTestHooks.ProcessCreated = windows.AddProcess;
    var running = RunNativeShellAsync(temp.Path, 0, 30_000, TimeSpan.FromSeconds(5), canceled.Token);
    try
    {
        var shell = await ReadExecutionEvidenceAsync<PowerShellEvidence>(Path.Combine(temp.Path, "shell.json"), running);
        windows.AddProcess(shell.Pid);
        var native = await ReadExecutionEvidenceAsync<NativeShellEvidence>(Path.Combine(temp.Path, "native.json"), running);
        windows.AddProcess(native.Pid);
        windows.AddConsole(native.ConsoleWindow);
        var result = await running;
        Check(result.TimedOut, "nested native execution did not time out");
        Check(native.InJob, "timed nested native child escaped the owned job");
        await WaitForExitAsync(shell.Pid);
        await WaitForExitAsync(native.Pid);
        Check(!native.ConsoleVisible && !await windows.CompleteAsync(), "timed native execution exposed an owned window");
    }
    finally
    {
        canceled.Cancel();
        try { await running; } catch (OperationCanceledException) { }
    }
}

static async Task NativeShellCancellationAsync()
{
    using var temp = new TempRoot();
    using var canceled = new CancellationTokenSource();
    await using var windows = new OwnedWindowObservation();
    ProcessRunnerTestHooks.ProcessCreated = windows.AddProcess;
    var running = RunNativeShellAsync(temp.Path, 0, 30_000, TimeSpan.FromSeconds(15), canceled.Token);
    try
    {
        var shell = await ReadExecutionEvidenceAsync<PowerShellEvidence>(Path.Combine(temp.Path, "shell.json"), running);
        windows.AddProcess(shell.Pid);
        var native = await ReadExecutionEvidenceAsync<NativeShellEvidence>(Path.Combine(temp.Path, "native.json"), running);
        windows.AddProcess(native.Pid);
        windows.AddConsole(native.ConsoleWindow);
        canceled.Cancel();
        await ThrowsCanceledAsync(() => running);
        Check(native.InJob, "canceled native child escaped the owned job");
        await WaitForExitAsync(shell.Pid);
        await WaitForExitAsync(native.Pid);
        Check(!native.ConsoleVisible && !await windows.CompleteAsync(), "canceled native execution exposed an owned window");
    }
    finally
    {
        canceled.Cancel();
        try { await running; } catch (OperationCanceledException) { }
    }
}

static Task RegisteredNativeEnvironmentAsync()
{
    var definition = new RegisteredToolDefinition("native-environment", 1, SelfInvocation([]).File);
    var defaultEnvironment = RegisteredToolRegistry.CreateEnvironment(definition);
    Equal(AgentOpsEngine.NativeExecutableExtensions,
        defaultEnvironment.Entries.Single(item => item.Key == "PATHEXT").Value);

    foreach (var value in new string?[] { ".EXE;.CUSTOM", string.Empty, null })
    {
        var policy = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATHEXT"] = value };
        var configured = definition with { Environment = policy };
        var environment = RegisteredToolRegistry.CreateEnvironment(configured);
        if (value is null) Check(!environment.Entries.Any(item => item.Key == "PATHEXT"), "explicit PATHEXT deletion was replaced");
        else Equal(value, environment.Entries.Single(item => item.Key == "PATHEXT").Value);
        Equal(value, policy["PATHEXT"]);
    }

    var overlay = RegisteredToolRegistry.CreateEnvironment(definition with
    {
        EnvironmentMode = ToolEnvironmentMode.InheritWithOverlay,
        Environment = new Dictionary<string, string?> { ["PATHEXT"] = null }
    });
    Check(!overlay.Entries.Any(item => item.Key == "PATHEXT"), "overlay PATHEXT deletion was replaced");
    return Task.CompletedTask;
}

static async Task<ProcessResult> RunNativeShellAsync(string directory, int exitCode, int delayMilliseconds,
    TimeSpan timeout, CancellationToken token, bool legacyEnvironment = false)
{
    string powerShell;
    try { powerShell = PowerShell7ToolBootstrap.ResolveExecutable(); }
    catch (AgentOpsException exception)
    {
        throw new InvalidOperationException("Native launch coverage requires registry-discovered PowerShell 7 or newer.", exception);
    }
    var invocation = SelfInvocation(["--helper-native-shell", Path.Combine(directory, "native.json"),
        exitCode.ToString(CultureInfo.InvariantCulture), delayMilliseconds.ToString(CultureInfo.InvariantCulture)]);
    var requestPath = Path.Combine(directory, "invocation.json");
    await WriteJsonAsync(requestPath, new NativeInvocation(invocation.File, invocation.Arguments));
    var scriptPath = Path.Combine(directory, "native.ps1");
    await File.WriteAllTextAsync(scriptPath, """
        param([string]$RequestPath, [string]$ShellPath)
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        $PSNativeCommandUseErrorActionPreference = $false
        $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json -AsHashtable
        $file = [string]$request.File
        $nativeArguments = @($request.Arguments)
        $shellEvidence = @{Pid=$PID; Version=$PSVersionTable.PSVersion.ToString(); Major=$PSVersionTable.PSVersion.Major}
        $shellEvidence | ConvertTo-Json -Compress | Set-Content -LiteralPath $ShellPath -Encoding utf8NoBOM
        & $file @nativeArguments
        exit $LASTEXITCODE
        """, new UTF8Encoding(false), token);
    string[] arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath,
        "-RequestPath", requestPath, "-ShellPath", Path.Combine(directory, "shell.json")];
    var environment = new Dictionary<string, string?>(AgentOpsEngine.MinimalToolEnvironment(powerShell), StringComparer.OrdinalIgnoreCase);
    foreach (var key in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64" })
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) environment[key] = value;
    ProcessEnvironment processEnvironment;
    if (legacyEnvironment)
    {
        environment.Remove("PATHEXT");
        processEnvironment = RegisteredToolRegistry.CreateEnvironment(new RegisteredToolDefinition(
            "legacy-native-pwsh", 1, powerShell, Environment: environment));
    }
    else processEnvironment = ProcessEnvironment.Replace(environment);
    return await ProcessRunner.RunAsync(powerShell, arguments, timeout, directory, processEnvironment, token);
}

static async Task<T> ReadExecutionEvidenceAsync<T>(string path, Task<ProcessResult> running)
{
    var deadline = Stopwatch.StartNew();
    while (!File.Exists(path))
    {
        if (running.IsCompleted)
            throw new InvalidOperationException("Native execution ended before evidence was created: " + ProcessOutcomeBrief(await running));
        if (deadline.Elapsed > TimeSpan.FromSeconds(8))
            throw new TimeoutException("Native execution evidence was not created within eight seconds.");
        await Task.Delay(20);
    }
    return await ReadJsonAsync<T>(path);
}

static string ProcessOutcomeBrief(ProcessResult result) => JsonSerializer.Serialize(new
{
    result.ExitCode, result.TimedOut, result.StdoutTruncated, result.StderrTruncated,
    stdout = result.Stdout[..Math.Min(result.Stdout.Length, 4000)],
    stderr = result.Stderr[..Math.Min(result.Stderr.Length, 4000)]
});

static async Task RootExitDescendantAsync()
{
    using var temp = new TempRoot();
    var childPath = Path.Combine(temp.Path, "root-exit-child.json");
    var stopwatch = Stopwatch.StartNew();
    var result = await RunSelfAsync(
        ["--helper-root-exit", childPath, "30000"],
        Timeout.InfiniteTimeSpan,
        temp.Path,
        CancellationToken.None);

    Check(!result.TimedOut, "exited root was reported as timed out");
    Equal(0, result.ExitCode);
    Equal("root-exit", result.Stdout);
    Check(result.StdoutTruncated, "retained stdout handle was not reported as truncated");
    Check(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "descendant-held stream deadlocked the runner");
    var child = await ReadJsonAsync<TreeEvidence>(childPath);
    Check(child.InJob, "root-exit descendant escaped the owned job");
    await WaitForExitAsync(child.Pid);
}

static async Task ProtocolFaultAsync()
{
    using var temp = new TempRoot();
    var marker = Path.Combine(temp.Path, "protocol.marker");
    var hostPid = 0;
    ProcessRunnerTestHooks.ProcessCreated = pid => hostPid = pid;
    ProcessRunnerTestHooks.ReadResponseAsync = (_, _) =>
        Task.FromException<ProcessHostResponse>(new InvalidDataException("malformed-test-frame"));

    await ThrowsCodeAsync(
        "internal",
        () => RunSelfAsync(
            ["--helper-touch", marker],
            Timeout.InfiniteTimeSpan,
            temp.Path,
            CancellationToken.None));
    Check(hostPid > 0, "protocol fault did not create a host");
    await WaitForExitAsync(hostPid);
}

static async Task ResponseBoundsAsync()
{
    var exact = BuildResponse(stdoutBytes: ProcessHostWire.MaxStreamBytes, extraStdoutByte: false);
    var exactResponse = await ProcessHostWire.ReadResponseAsync(new MemoryStream(exact));
    Equal((long)ProcessHostWire.MaxStreamBytes, exactResponse.Stdout.Length);
    Check(!exactResponse.StdoutTruncated, "exact response boundary was marked truncated");
    Check(exactResponse.Completed, "exact response did not complete");

    var over = BuildResponse(stdoutBytes: ProcessHostWire.MaxStreamBytes, extraStdoutByte: true);
    var overResponse = await ProcessHostWire.ReadResponseAsync(new MemoryStream(over));
    Equal((long)ProcessHostWire.MaxStreamBytes, overResponse.Stdout.Length);
    Check(overResponse.StdoutTruncated, "over-bound response was not marked truncated");

    await ThrowsInvalidDataAsync(() => ProcessHostWire.ReadResponseAsync(new MemoryStream([1, 2, 3, 4, 5, 6])));
    await ThrowsInvalidDataAsync(() => ProcessHostWire.ReadResponseAsync(new MemoryStream(BuildOversizedFrame())));
}

static async Task CanonicalExecutableAsync()
{
    using var temp = new TempRoot();
    var created = 0;
    ProcessRunnerTestHooks.ProcessCreated = _ => created++;
    await ThrowsCodeAsync(
        "invalid",
        () => ProcessRunner.RunAsync(
            "pwsh.exe",
            [],
            TimeSpan.FromSeconds(1),
            temp.Path,
            ProcessEnvironment.Empty,
            CancellationToken.None));
    Equal(0, created);
}

static async Task FailureCleanupAsync(ProcessLaunchFailurePoint point)
{
    using var temp = new TempRoot();
    var marker = Path.Combine(temp.Path, $"{point}.marker");
    var createdPid = 0;
    ProcessRunnerTestHooks.FailurePoint = point;
    ProcessRunnerTestHooks.ProcessCreated = pid => createdPid = pid;

    await ThrowsCodeAsync(
        "internal",
        () => RunSelfAsync(
            ["--helper-touch", marker],
            TimeSpan.FromSeconds(10),
            temp.Path,
            CancellationToken.None));

    Check(createdPid > 0, "failure injection did not observe the suspended process");
    await WaitForExitAsync(createdPid);
    Check(!File.Exists(marker), $"{point} failure allowed child user code to run");
}

static Task<ProcessResult> RunSelfAsync(
    IReadOnlyList<string> arguments,
    TimeSpan timeout,
    string cwd,
    CancellationToken cancellationToken)
{
    var invocation = SelfInvocation(arguments);
    return ProcessRunner.RunAsync(
        invocation.File,
        invocation.Arguments,
        timeout,
        cwd,
        TestEnvironment(),
        cancellationToken);
}

static Task<ProcessResult> RunSelfWithEnvironmentAsync(
    IReadOnlyList<string> arguments,
    TimeSpan timeout,
    string cwd,
    ProcessEnvironment environment,
    CancellationToken cancellationToken)
{
    var invocation = SelfInvocation(arguments);
    return ProcessRunner.RunAsync(
        invocation.File,
        invocation.Arguments,
        timeout,
        cwd,
        environment,
        cancellationToken);
}

static ProcessEnvironment TestEnvironment()
{
    var values = new Dictionary<string, string?>();
    if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
        values["DOTNET_ROOT"] = dotnetRoot;
    if (Environment.GetEnvironmentVariable("PAO_PROCESS_RUNNER_TEST_ENV") is { } testValue)
        values["PAO_PROCESS_RUNNER_TEST_ENV"] = testValue;
    values["PAO_EMPTY_VALUE"] = string.Empty;
    return ProcessEnvironment.Replace(values);
}

static ProcessEnvironment ReplacementEnvironment(string value)
{
    var values = new Dictionary<string, string?>
    {
        ["PAO_PROCESS_RUNNER_TEST_ENV"] = value,
        ["PAO_EMPTY_VALUE"] = string.Empty
    };
    if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
        values["DOTNET_ROOT"] = dotnetRoot;
    return ProcessEnvironment.Replace(values);
}

static (string File, IReadOnlyList<string> Arguments) SelfInvocation(IReadOnlyList<string> arguments)
{
    var host = Environment.ProcessPath ?? throw new InvalidOperationException("process host unavailable");
    if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(host), "dotnet"))
        return (host, arguments);

    return (host, [Assembly.GetExecutingAssembly().Location, .. arguments]);
}

static Process StartSelf(params string[] arguments)
{
    var invocation = SelfInvocation(arguments);
    var start = new ProcessStartInfo(invocation.File)
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var argument in invocation.Arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("helper failed to start");
}

static Process StartSelfWithOnlyStdoutInherited(params string[] arguments)
{
    var invocation = SelfInvocation(arguments);
    var start = new ProcessStartInfo(invocation.File)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true
    };
    foreach (var argument in invocation.Arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("helper failed to start");
}

static IReadOnlyList<string> ExactRequestArguments(string file, string cwd)
{
    const int argumentCount = 8;
    const int maximumStringBytes = 256 * 1024;
    var fixedBytes =
        sizeof(uint) + sizeof(ushort) +
        sizeof(int) + Encoding.UTF8.GetByteCount(file) +
        sizeof(int) + Encoding.UTF8.GetByteCount(cwd) +
        sizeof(int) +
        (argumentCount * sizeof(int)) +
        sizeof(int);
    var remaining = ProcessHostWire.MaxRequestBytes - fixedBytes;
    var result = new string[argumentCount];
    for (var index = 0; index < result.Length; index++)
    {
        var length = Math.Min(maximumStringBytes, remaining);
        result[index] = new string('a', length);
        remaining -= length;
    }
    Equal(0, remaining);
    Check(result[^1].Length < maximumStringBytes, "exact request calculation left no one-byte over case");
    return result;
}

static byte[] BuildResponse(int stdoutBytes, bool extraStdoutByte)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    writer.Write(0x31524850u);
    writer.Write((ushort)1);
    var remaining = stdoutBytes;
    var chunk = new byte[16 * 1024];
    while (remaining > 0)
    {
        var length = Math.Min(chunk.Length, remaining);
        writer.Write((byte)1);
        writer.Write(length);
        writer.Write(chunk, 0, length);
        remaining -= length;
    }
    if (extraStdoutByte)
    {
        writer.Write((byte)1);
        writer.Write(1);
        writer.Write((byte)0);
    }
    writer.Write((byte)3);
    writer.Write(sizeof(int) + 2);
    writer.Write(0);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Flush();
    return stream.ToArray();
}

static byte[] BuildOversizedFrame()
{
    var bytes = new byte[sizeof(uint) + sizeof(ushort) + sizeof(byte) + sizeof(int)];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x31524850u);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(sizeof(uint)), 1);
    bytes[sizeof(uint) + sizeof(ushort)] = 1;
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(uint) + sizeof(ushort) + sizeof(byte)), (16 * 1024) + 1);
    return bytes;
}

static async Task<T> ReadJsonAsync<T>(string path)
{
    var deadline = Stopwatch.StartNew();
    Exception? last = null;
    while (deadline.Elapsed < TimeSpan.FromSeconds(8))
    {
        try
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var value = await JsonSerializer.DeserializeAsync<T>(stream);
                if (value is not null) return value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            last = ex;
        }
        await Task.Delay(20);
    }
    throw new TimeoutException($"evidence unavailable: {path}", last);
}

static async Task WriteJsonAsync<T>(string path, T value)
{
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    await JsonSerializer.SerializeAsync(stream, value);
    await stream.FlushAsync();
}

static bool IsCurrentProcessInJob()
{
    if (!IsProcessInJob(GetCurrentProcess(), 0, out var inJob))
        throw new Win32Exception(Marshal.GetLastWin32Error());
    return inJob;
}

static void CloseCurrentStandardError()
{
    var handle = GetStdHandle(-12);
    if (handle is 0 or -1) return;
    SetStdHandle(-12, 0);
    CloseHandle(handle);
}

static async Task WaitForExitAsync(int pid)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(8))
    {
        if (!IsRunning(pid)) return;
        await Task.Delay(20);
    }
    throw new InvalidOperationException($"process survived cleanup pid={pid}");
}

static bool IsRunning(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

static async Task ThrowsCanceledAsync(Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException("expected cancellation");
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task ThrowsInvalidDataAsync(Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException("expected invalid protocol data");
    }
    catch (InvalidDataException)
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

static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"sequence mismatch expected={JsonSerializer.Serialize(expected)} actual={JsonSerializer.Serialize(actual)}");
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

[DllImport("kernel32.dll")]
static extern nint GetCurrentProcess();

[DllImport("kernel32.dll")]
static extern nint GetConsoleWindow();

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool IsWindowVisible(nint window);

[DllImport("kernel32.dll")]
static extern nint GetStdHandle(int standardHandle);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool SetStdHandle(int standardHandle, nint handle);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool CloseHandle(nint handle);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

sealed record CapturePayload(
    string[] Arguments,
    string WorkingDirectory,
    string? EnvironmentValue,
    string? UnrelatedSecret,
    string? EmptyValue);
sealed record TreeEvidence(int Pid, bool InJob, int? ChildPid);
sealed record NativeInvocation(string File, IReadOnlyList<string> Arguments);
sealed record PowerShellEvidence(int Pid, string Version, int Major);
sealed record NativeShellEvidence(int Pid, bool InJob, long ConsoleWindow, bool ConsoleVisible);

sealed class OwnedWindowObservation : IAsyncDisposable
{
    private const int MaximumSnapshots = 4096;
    private readonly object _gate = new();
    private readonly HashSet<(long Window, int Pid)> _visible = [];
    private readonly HashSet<int> _processIds = [];
    private readonly HashSet<long> _consoleWindows = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampling;
    private bool _overflow, _failed;

    public OwnedWindowObservation()
    {
        // Retain startup observations before process identity evidence is available.
        Sample();
        _sampling = SampleAsync();
    }

    public void AddProcess(int pid)
    {
        lock (_gate) _processIds.Add(pid);
    }

    public void AddConsole(long window)
    {
        if (window == 0) return;
        lock (_gate) _consoleWindows.Add(window);
    }

    public async Task<bool> CompleteAsync()
    {
        _stop.Cancel();
        await _sampling;
        Sample();
        lock (_gate)
        {
            if (_failed || _overflow) throw new InvalidOperationException("Owned-window observation failed or exceeded its bound.");
            return _visible.Any(item => _processIds.Contains(item.Pid) || _consoleWindows.Contains(item.Window));
        }
    }

    private async Task SampleAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Sample();
                await Task.Delay(10, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Sample()
    {
        lock (_gate)
        {
            var enumerated = EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window))
                {
                    GetWindowThreadProcessId(window, out var pid);
                    Retain(window.ToInt64(), (int)pid);
                }
                return true;
            }, 0);
            _failed |= !enumerated;
            foreach (var window in _consoleWindows)
                if (IsWindowVisible((nint)window)) Retain(window, 0);
        }
    }

    private void Retain(long window, int pid)
    {
        if (_visible.Contains((window, pid))) return;
        if (_visible.Count == MaximumSnapshots) _overflow = true;
        else _visible.Add((window, pid));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _sampling;
        _stop.Dispose();
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

sealed class SlowArguments(int count, TimeSpan delay) : IReadOnlyList<string>
{
    public int Count { get; } = count;

    public string this[int index]
    {
        get
        {
            Thread.Sleep(delay);
            return $"slow-{index}";
        }
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var index = 0; index < Count; index++) yield return this[index];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

sealed class SingleReadArguments(IReadOnlyList<string> values) : IReadOnlyList<string>
{
    private readonly int[] _reads = new int[values.Count];
    public int Count => values.Count;
    public IReadOnlyList<int> ReadCounts => _reads;

    public string this[int index]
    {
        get
        {
            if (++_reads[index] != 1) throw new InvalidOperationException("argument reread");
            return values[index];
        }
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var index = 0; index < Count; index++) yield return this[index];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

sealed class ProcessLease(Process process) : IDisposable
{
    internal Process Process { get; } = process;

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            Process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }
        finally
        {
            Process.Dispose();
        }
    }
}

sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pao-process-runner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        Directory.Delete(Path, recursive: true);
    }
}
