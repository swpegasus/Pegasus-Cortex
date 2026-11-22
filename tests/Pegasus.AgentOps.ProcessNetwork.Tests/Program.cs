using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Pegasus.AgentOps.Engine;

if (args.Length > 0 && args[0].StartsWith("--helper-", StringComparison.Ordinal))
    return await RunHelperAsync(args);

var tests = new (string Name, Func<Task> Run)[]
{
    ("inspect_identity_canonical_utc", InspectIdentityAsync),
    ("wrong_start_never_stops_target", WrongStartAsync),
    ("exact_stop_exits_target", ExactStopAsync),
    ("already_exited_is_honest", AlreadyExitedAsync),
    ("wait_timeout_and_cancellation_safe", WaitTimeoutAsync),
    ("bounded_child_tree_and_exact_tree_stop", ChildTreeAsync),
    ("tcp_udp_listener_exact_port_and_bound", ListenerFilterAsync),
    ("typed_input_bounds", InputBoundsAsync)
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
}

Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static async Task<int> RunHelperAsync(string[] helperArgs)
{
    if (helperArgs[0] == "--helper-sleep")
    {
        await Task.Delay(int.Parse(helperArgs[1], CultureInfo.InvariantCulture));
        return 0;
    }

    if (helperArgs[0] == "--helper-spawn-child")
    {
        var pidFile = helperArgs[1];
        var duration = helperArgs[2];
        using var child = StartSelf("--helper-sleep", duration);
        await File.WriteAllTextAsync(pidFile, child.Id.ToString(CultureInfo.InvariantCulture));
        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    return 2;
}

static Task InspectIdentityAsync()
{
    using var helper = new ProcessLease(StartSelf("--helper-sleep", "10000"));
    var inspected = ProcessOperations.Inspect(helper.Process.Id);
    Check(inspected.Ok && inspected.State == "running" && inspected.Process is not null, "helper was not inspectable");
    Equal(helper.Process.Id, inspected.Process!.Pid);
    Check(ProcessOperations.IsCanonicalStartIdentity(inspected.Process.Start), "start identity was not canonical UTC O format");
    Check(inspected.Process.Start.EndsWith('Z'), "start identity was not UTC");
    Check(!string.IsNullOrWhiteSpace(inspected.Process.Name), "process name missing");
    return Task.CompletedTask;
}

static async Task WrongStartAsync()
{
    using var helper = new ProcessLease(StartSelf("--helper-sleep", "10000"));
    var identity = Identity(helper.Process.Id);
    var parsed = DateTime.ParseExact(identity.Start, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    var wrong = parsed.AddTicks(1).ToString("O", CultureInfo.InvariantCulture);
    var result = await ProcessOperations.StopAsync(helper.Process.Id, wrong, 5);
    Check(!result.Ok && result.State == "reused" && result.Error == "pid_reused", "wrong identity was not rejected as reused");
    Check(!result.ActionIssued && !helper.Process.HasExited, "wrong identity mutated the target");
    Equal(identity.Start, result.ObservedStart);
}

static async Task ExactStopAsync()
{
    using var helper = new ProcessLease(StartSelf("--helper-sleep", "10000"));
    var identity = Identity(helper.Process.Id);
    var result = await ProcessOperations.StopAsync(helper.Process.Id, identity.Start, 5);
    Check(result.Ok && result.State == "stopped" && result.ActionIssued && result.Exited, result.Error ?? "exact stop failed");
    await helper.Process.WaitForExitAsync();
    Check(helper.Process.HasExited, "exact target survived stop");
}

static async Task AlreadyExitedAsync()
{
    using var helper = new ProcessLease(StartSelf("--helper-sleep", "500"));
    var identity = Identity(helper.Process.Id);
    await helper.Process.WaitForExitAsync();
    var waited = await ProcessOperations.WaitAsync(helper.Process.Id, identity.Start, 1);
    Check(waited.Ok && waited.State == "exited" && waited.Exited && !waited.ActionIssued, "wait did not report already exited");
    var stopped = await ProcessOperations.StopAsync(helper.Process.Id, identity.Start, 1);
    Check(stopped.Ok && stopped.State == "exited" && stopped.Exited && !stopped.ActionIssued, "stop did not report already exited");
}

static async Task WaitTimeoutAsync()
{
    using var helper = new ProcessLease(StartSelf("--helper-sleep", "10000"));
    var identity = Identity(helper.Process.Id);
    var timedOut = await ProcessOperations.WaitAsync(helper.Process.Id, identity.Start, 0);
    Check(!timedOut.Ok && timedOut.State == "timeout" && timedOut.Error == "timeout", "bounded wait did not time out");
    Check(!timedOut.ActionIssued && !helper.Process.HasExited, "wait timeout mutated target");

    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await ThrowsCanceled(() => ProcessOperations.WaitAsync(helper.Process.Id, identity.Start, 10, canceled.Token));
    Check(!helper.Process.HasExited, "canceled wait mutated target");

    var cleanup = await ProcessOperations.StopAsync(helper.Process.Id, identity.Start, 5);
    Check(cleanup.Ok, cleanup.Error ?? "timeout test cleanup failed");
}

static async Task ChildTreeAsync()
{
    using var temp = new TempRoot();
    var childPidPath = Path.Combine(temp.Path, "child.pid");
    using var parent = new ProcessLease(StartSelf("--helper-spawn-child", childPidPath, "30000"));
    var childPid = await ReadPidAsync(childPidPath);
    using var child = Process.GetProcessById(childPid);
    var parentIdentity = Identity(parent.Process.Id);

    var tree = ProcessOperations.Tree(parent.Process.Id, 16);
    Check(tree.Ok && tree.State == "running" && !tree.Truncated, tree.Error ?? "tree snapshot failed");
    var root = tree.Nodes.Single(node => node.Pid == parent.Process.Id);
    Equal(0, root.Depth);
    var directChild = tree.Nodes.Single(node => node.Pid == childPid);
    Equal(parent.Process.Id, directChild.ParentPid);
    Equal(1, directChild.Depth);

    var bounded = ProcessOperations.Tree(parent.Process.Id, 1);
    Check(bounded.Ok && bounded.Truncated && bounded.Nodes.Count == 1, "tree max did not produce an honest truncation");

    var stopped = await ProcessOperations.StopAsync(parent.Process.Id, parentIdentity.Start, 5);
    Check(stopped.Ok && stopped.ActionIssued, stopped.Error ?? "tree stop failed");
    await parent.Process.WaitForExitAsync();
    await WaitUntilAsync(() => HasExited(child), TimeSpan.FromSeconds(5));
    Check(HasExited(child), "exact target child survived tree stop");
}

static async Task ListenerFilterAsync()
{
    var tcp = new TcpListener(IPAddress.Loopback, 0);
    tcp.Start();
    try
    {
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var listeners = await WaitForListenersAsync(port, expectedProtocols: 2);
        Check(listeners.Listeners.All(listener => listener.Port == port), "exact port filter returned another port");
        Check(listeners.Listeners.Any(listener => listener.Protocol == "tcp" && listener.Address == IPAddress.Loopback.ToString()), "TCP listener missing");
        Check(listeners.Listeners.Any(listener => listener.Protocol == "udp" && listener.Address == IPAddress.Loopback.ToString()), "UDP listener missing");
        Check(listeners.Listeners.All(listener => listener.Protocol is "tcp" or "udp"), "unexpected network row type");

        var bounded = NetworkOperations.ListListeners(port, max: 1);
        Check(bounded.Truncated && bounded.MatchedCount >= 2 && bounded.Listeners.Count == 1, "listener result did not enforce its bound");
    }
    finally
    {
        tcp.Stop();
    }
}

static Task InputBoundsAsync()
{
    ThrowsCode("invalid", () => ProcessOperations.Inspect(0));
    ThrowsCode("invalid", () => ProcessOperations.Tree(Environment.ProcessId, 0));
    ThrowsCode("invalid", () => NetworkOperations.ListListeners(65536));
    ThrowsCode("invalid", () => NetworkOperations.ListListeners(max: NetworkOperations.MaximumListeners + 1));
    ThrowsCode("invalid", () => ProcessOperations.WaitAsync(Environment.ProcessId, "not-a-start", 1).GetAwaiter().GetResult());
    var currentIdentity = Identity(Environment.ProcessId);
    ThrowsCode(
        "invalid",
        () => ProcessOperations.StopAsync(
            Environment.ProcessId,
            currentIdentity.Start,
            ProcessOperations.MaximumTimeoutSeconds + 1).GetAwaiter().GetResult());
    return Task.CompletedTask;
}

static AgentProcessIdentity Identity(int pid)
{
    var inspected = ProcessOperations.Inspect(pid);
    if (!inspected.Ok || inspected.Process is null) throw new InvalidOperationException($"identity unavailable state={inspected.State}");
    return inspected.Process;
}

static Process StartSelf(params string[] helperArgs)
{
    var host = Environment.ProcessPath ?? throw new InvalidOperationException("process host unavailable");
    var start = new ProcessStartInfo(host)
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };
    if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(host), "dotnet"))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in helperArgs) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("helper failed to start");
}

static async Task<int> ReadPidAsync(string path)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        try
        {
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), CultureInfo.InvariantCulture, out var pid))
                return pid;
        }
        catch (IOException)
        {
            // The helper publishes immediately; retry while its write handle closes.
        }
        await Task.Delay(25);
    }
    throw new TimeoutException("child pid");
}

static async Task<ListenerListResult> WaitForListenersAsync(int port, int expectedProtocols)
{
    var deadline = Stopwatch.StartNew();
    ListenerListResult? last = null;
    while (deadline.Elapsed < TimeSpan.FromSeconds(5))
    {
        last = NetworkOperations.ListListeners(port);
        if (last.Listeners.Select(listener => listener.Protocol).Distinct(StringComparer.Ordinal).Count() >= expectedProtocols)
            return last;
        await Task.Delay(25);
    }
    throw new InvalidOperationException($"listeners unavailable matched={last?.MatchedCount ?? 0}");
}

static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < timeout)
    {
        if (predicate()) return;
        await Task.Delay(25);
    }
}

static bool HasExited(Process process)
{
    try
    {
        process.Refresh();
        return process.HasExited;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

static async Task ThrowsCanceled(Func<Task> action)
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

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected={expected} actual={actual}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class ProcessLease(Process process) : IDisposable
{
    public Process Process { get; } = process;

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            Process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
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
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pao-process-network-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
