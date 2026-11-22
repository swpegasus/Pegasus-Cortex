using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexProductInstallLifecycleReceiptV1(
    string Contract,
    int Version,
    bool ServiceStopped,
    int ProductProcessesStopped,
    bool RuntimeKeptStopped = false);

public static class CortexProductInstallLifecycle
{
    public const string Contract = "pegasus-cortex-install-lifecycle";
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

    public static CortexProductInstallLifecycleReceiptV1 Prepare(CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        CortexWindowsProductRegistration.RequireOwnedServiceStopped(layout);
        RequireNoProductProcesses(layout.InstallRoot, Environment.ProcessId);
        // Exit/drain is owned by Cortex itself. Setup never kills a host or
        // races reconnecting clients to manufacture an update window.
        return new(Contract, 1, ServiceStopped: false, ProductProcessesStopped: 0, RuntimeKeptStopped: true);
    }

    internal static IDisposable HoldUserHostAdmission(CortexProductLayout layout)
    {
        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        if (process.SessionId <= 0 || identity.User?.Value != layout.OwnerSid)
            throw new UnauthorizedAccessException("Cortex maintenance requires the registered owner's interactive session.");
        // Existing tray contract: named-object existence, not mutex ownership,
        // blocks a second host before it resolves or initializes any data path.
        var handle = new Mutex(false, "Local\\Pegasus.Cortex.Host." + process.SessionId, out var created);
        if (created) return handle;
        handle.Dispose();
        throw new IOException("The Cortex user-host admission object is already active; no process was stopped.");
    }

    internal static void RequireNoProductProcesses(string installRoot, int excludedProcessId)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // Idle (0) and kernel System (4) are not user-mode product
                // images. System may return ERROR_GEN_FAILURE to image queries.
                if (process.Id is 0 or 4 || process.Id == excludedProcessId) continue;
                try
                {
                    var executable = CortexUserSessionStartup.NativeStartupPlatform.ReadProcessImage(checked((uint)process.Id));
                    if (IsContained(root, executable))
                        throw new IOException("A Cortex program image is still loaded. Exit Cortex and disconnect the affected client before updating; no process was stopped.");
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode is 5 or 87 ||
                    IsKernelImageQueryFailure(exception.NativeErrorCode, process.ProcessName))
                {
                    // Protected system images may deny even a limited query;
                    // an exited PID may disappear between enumeration and open.
                    // Other native failures remain errors. The service retains
                    // its separate exact SCM identity check.
                }
            }
        }
    }

    internal static bool IsKernelImageQueryFailure(int nativeError, string processName) =>
        // These Windows kernel-backed pseudo-processes have no user-mode
        // executable. Classify only a failed query, never a successful image.
        nativeError == 31 && processName is "Secure System" or "Registry" or "Memory Compression";

    internal static CortexProductInstallLifecycleReceiptV1 PrepareCore(
        Func<bool> stopService, Func<int> stopProcesses)
    {
        var serviceStopped = stopService();
        var processesStopped = stopProcesses();
        return new CortexProductInstallLifecycleReceiptV1(
            Contract, 1, serviceStopped, processesStopped);
    }

    internal static void PrepareCodexFeatureChange()
    {
        RequireCodexClosed();
    }

    internal static int StopProductProcessesForTest(string installRoot)
    {
        return StopProductProcesses(installRoot, Environment.ProcessId);
    }

    internal static void RequireCodexClosed()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { names.Add(process.ProcessName); }
                catch (InvalidOperationException) { }
            }
        }
        RequireCodexClosed(names);
    }

    internal static void RequireCodexClosed(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        var count = processNames.Count(name =>
            string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase) ||
            // The packaged Windows Codex desktop currently uses ChatGPT.exe
            // for its UI processes. Both identities must be absent before a
            // selected feature changes Codex configuration or guidance.
            string.Equals(name, "chatgpt", StringComparison.OrdinalIgnoreCase));
        if (count != 0)
            throw new CortexCodexRunningException(count);
    }

    private static int StopProductProcesses(string installRoot, int excludedProcessId)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var candidates = new List<ProductProcessIdentity>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == excludedProcessId)
                    continue;
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is null || !IsContained(root, executable))
                        continue;
                    candidates.Add(new ProductProcessIdentity(
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks,
                        Path.GetFullPath(executable)));
                }
                catch (ArgumentException)
                {
                    // The process exited during the bounded identity snapshot.
                }
                catch (InvalidOperationException)
                {
                    // The process exited during the bounded identity snapshot.
                }
                catch (Win32Exception)
                {
                    // A protected unrelated process did not expose its image path.
                }
            }
        }

        var stopped = 0;
        foreach (var candidate in candidates.OrderByDescending(value => value.Executable.Length))
        {
            using var process = TryOpenExact(candidate);
            if (process is null)
                continue;
            // Give owned interactive windows a normal close opportunity first.
            // Headless MCP/host processes have no close window; the explicit
            // upgrade interruption warning covers their bounded termination.
            try
            {
                if (!process.CloseMainWindow() || !process.WaitForExit(3000))
                    process.Kill(entireProcessTree: true);
                if (!process.WaitForExit((int)ProcessExitTimeout.TotalMilliseconds))
                    throw new TimeoutException("A verified Cortex product process did not stop within 10 seconds.");
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // An already admitted process can exit between the close and
                // termination calls. Its exit satisfies the stop requirement.
            }
            stopped++;
        }
        return stopped;
    }

    private static Process? TryOpenExact(ProductProcessIdentity expected)
    {
        try
        {
            var process = Process.GetProcessById(expected.ProcessId);
            var executable = process.MainModule?.FileName;
            if (executable is null ||
                process.StartTime.ToUniversalTime().Ticks != expected.StartTimeUtcTicks ||
                !string.Equals(Path.GetFullPath(executable), expected.Executable, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    internal static bool IsContained(string root, string candidate)
    {
        var full = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(root, full);
        return relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed record ProductProcessIdentity(
        int ProcessId,
        long StartTimeUtcTicks,
        string Executable);

}

internal sealed class CortexCodexRunningException(int processCount) : InvalidOperationException(
    $"Codex is still running ({processCount} related process(es)). Close every Codex window completely, then run setup again.");
