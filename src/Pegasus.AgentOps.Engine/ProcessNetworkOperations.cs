using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

public sealed record AgentProcessIdentity(int Pid, string Start, string Name);

public sealed record ProcessInspectResult(
    bool Ok,
    string State,
    string? Error,
    AgentProcessIdentity? Process);

public sealed record ProcessActionResult(
    bool Ok,
    string State,
    string? Error,
    int Pid,
    string ExpectedStart,
    string? ObservedStart,
    bool ActionIssued,
    bool Exited,
    int? ExitCode);

public sealed record ProcessTreeNode(int Pid, int ParentPid, int Depth);

public sealed record ProcessTreeResult(
    bool Ok,
    string State,
    string? Error,
    int RootPid,
    IReadOnlyList<ProcessTreeNode> Nodes,
    bool Truncated,
    int SnapshotCount);

public sealed record LocalListener(string Protocol, string Address, AddressFamily AddressFamily, int Port);

public sealed record ListenerListResult(
    IReadOnlyList<LocalListener> Listeners,
    bool Truncated,
    int MatchedCount);

public static class ProcessOperations
{
    public const int MaximumTimeoutSeconds = 3600;
    public const int MaximumTreeNodes = 1024;
    private const int MaximumSnapshotProcesses = 8192;

    public static ProcessInspectResult Inspect(int pid)
    {
        ValidatePid(pid);
        using var process = TryOpen(pid);
        if (process is null) return new(true, "exited", null, null);
        try
        {
            if (process.HasExited) return new(true, "exited", null, null);
            return new(true, "running", null, ReadIdentity(process));
        }
        catch (Win32Exception)
        {
            return new(false, "locked", "access", null);
        }
        catch (InvalidOperationException)
        {
            return new(true, "exited", null, null);
        }
    }

    public static async Task<ProcessActionResult> WaitAsync(
        int pid,
        string start,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(pid, start, timeoutSeconds);
        using var process = TryOpen(pid);
        if (process is null) return Exited(pid, start, actionIssued: false);

        AgentProcessIdentity observed;
        try
        {
            if (process.HasExited) return Exited(pid, start, actionIssued: false);
            observed = ReadIdentity(process);
        }
        catch (Win32Exception)
        {
            return Locked(pid, start, actionIssued: false);
        }
        catch (InvalidOperationException)
        {
            return Exited(pid, start, actionIssued: false);
        }

        if (!StringComparer.Ordinal.Equals(start, observed.Start))
            return Reused(pid, start, observed.Start);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await process.WaitForExitAsync(timeout.Token);
            return Exited(pid, start, actionIssued: false, TryExitCode(process), observed.Start);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (SafeHasExited(process))
                return Exited(pid, start, actionIssued: false, TryExitCode(process), observed.Start);
            return new(false, "timeout", "timeout", pid, start, observed.Start, false, false, null);
        }
        catch (Win32Exception)
        {
            return Locked(pid, start, actionIssued: false, observed.Start);
        }
        catch (InvalidOperationException)
        {
            return Exited(pid, start, actionIssued: false, observedStart: observed.Start);
        }
    }

    public static async Task<ProcessActionResult> StopAsync(
        int pid,
        string start,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(pid, start, timeoutSeconds);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = TryOpen(pid);
        if (process is null) return Exited(pid, start, actionIssued: false);

        AgentProcessIdentity observed;
        try
        {
            if (process.HasExited) return Exited(pid, start, actionIssued: false);
            observed = ReadIdentity(process);
        }
        catch (Win32Exception)
        {
            return Locked(pid, start, actionIssued: false);
        }
        catch (InvalidOperationException)
        {
            return Exited(pid, start, actionIssued: false);
        }

        if (!StringComparer.Ordinal.Equals(start, observed.Start))
            return Reused(pid, start, observed.Start);

        var actionIssued = false;
        try
        {
            // The second identity read is deliberately adjacent to the action.
            // Process keeps the same OS handle, so PID reuse cannot retarget Kill.
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited) return Exited(pid, start, actionIssued: false, observedStart: observed.Start);
            var immediatelyObserved = FormatStart(process.StartTime);
            if (!StringComparer.Ordinal.Equals(start, immediatelyObserved))
                return Reused(pid, start, immediatelyObserved);
            cancellationToken.ThrowIfCancellationRequested();
            process.Kill(entireProcessTree: true);
            actionIssued = true;

            // Once mutation is issued, finish a bounded observation without caller
            // cancellation so a killed process is never mislabeled as untouched.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return new(true, "stopped", null, pid, start, immediatelyObserved, true, true, TryExitCode(process));
            }
            catch (OperationCanceledException)
            {
                if (SafeHasExited(process))
                    return new(true, "stopped", null, pid, start, immediatelyObserved, true, true, TryExitCode(process));
                return new(false, "timeout", "timeout", pid, start, immediatelyObserved, true, false, null);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return Locked(pid, start, actionIssued, observed.Start);
        }
        catch (Win32Exception)
        {
            return new(false, "stop_failed", "external_failed", pid, start, observed.Start, actionIssued, SafeHasExited(process), TryExitCode(process));
        }
        catch (InvalidOperationException)
        {
            return Exited(pid, start, actionIssued, observedStart: observed.Start);
        }
    }

    public static ProcessTreeResult Tree(int pid, int max = 256)
    {
        ValidatePid(pid);
        if (max is < 1 or > MaximumTreeNodes) throw new AgentOpsException("invalid");
        if (!OperatingSystem.IsWindows()) throw new AgentOpsException("platform");

        var snapshot = ProcessSnapshot.Capture(MaximumSnapshotProcesses);
        if (!snapshot.Entries.ContainsKey(pid))
        {
            if (snapshot.Truncated)
                return new(false, "snapshot_truncated", "limit", pid, Array.Empty<ProcessTreeNode>(), true, snapshot.Entries.Count);
            return new(true, "exited", null, pid, Array.Empty<ProcessTreeNode>(), false, snapshot.Entries.Count);
        }

        var children = snapshot.Entries.Values
            .GroupBy(entry => entry.ParentPid)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.Pid).ToArray());
        var result = new List<ProcessTreeNode>(Math.Min(max, 64));
        var queue = new Queue<(int Pid, int ParentPid, int Depth)>();
        var visited = new HashSet<int>();
        var root = snapshot.Entries[pid];
        queue.Enqueue((root.Pid, root.ParentPid, 0));
        var truncated = snapshot.Truncated;

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (!visited.Add(item.Pid)) continue;
            if (result.Count == max)
            {
                truncated = true;
                break;
            }
            result.Add(new ProcessTreeNode(item.Pid, item.ParentPid, item.Depth));
            if (!children.TryGetValue(item.Pid, out var directChildren)) continue;
            foreach (var child in directChildren)
                queue.Enqueue((child.Pid, child.ParentPid, item.Depth + 1));
        }

        return new(true, truncated ? "truncated" : "running", null, pid, result, truncated, snapshot.Entries.Count);
    }

    public static string FormatStart(DateTime startTime) =>
        startTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static bool IsCanonicalStartIdentity(string value) =>
        DateTime.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed) &&
        parsed.Kind == DateTimeKind.Utc &&
        StringComparer.Ordinal.Equals(value, parsed.ToString("O", CultureInfo.InvariantCulture));

    private static AgentProcessIdentity ReadIdentity(Process process) =>
        new(process.Id, FormatStart(process.StartTime), process.ProcessName);

    private static void ValidateTarget(int pid, string start, int timeoutSeconds)
    {
        ValidatePid(pid);
        if (!IsCanonicalStartIdentity(start) || timeoutSeconds is < 0 or > MaximumTimeoutSeconds)
            throw new AgentOpsException("invalid");
    }

    private static void ValidatePid(int pid)
    {
        if (pid <= 0) throw new AgentOpsException("invalid");
    }

    private static Process? TryOpen(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int? TryExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static ProcessActionResult Exited(
        int pid,
        string expected,
        bool actionIssued,
        int? exitCode = null,
        string? observedStart = null) =>
        new(true, "exited", null, pid, expected, observedStart, actionIssued, true, exitCode);

    private static ProcessActionResult Reused(int pid, string expected, string observed) =>
        new(false, "reused", "pid_reused", pid, expected, observed, false, false, null);

    private static ProcessActionResult Locked(int pid, string expected, bool actionIssued, string? observedStart = null) =>
        new(false, "locked", "access", pid, expected, observedStart, actionIssued, false, null);
}

public static class NetworkOperations
{
    public const int MaximumListeners = 4096;

    public static ListenerListResult ListListeners(int? port = null, int max = 512)
    {
        if (port is < 1 or > 65535 || max is < 1 or > MaximumListeners)
            throw new AgentOpsException("invalid");

        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var listeners = properties.GetActiveTcpListeners()
            .Select(endpoint => new LocalListener("tcp", endpoint.Address.ToString(), endpoint.AddressFamily, endpoint.Port))
            .Concat(properties.GetActiveUdpListeners()
                .Select(endpoint => new LocalListener("udp", endpoint.Address.ToString(), endpoint.AddressFamily, endpoint.Port)))
            .Where(listener => port is null || listener.Port == port.Value)
            .OrderBy(listener => listener.Protocol, StringComparer.Ordinal)
            .ThenBy(listener => listener.Port)
            .ThenBy(listener => listener.AddressFamily)
            .ThenBy(listener => listener.Address, StringComparer.Ordinal)
            .ToArray();

        return new(listeners.Take(max).ToArray(), listeners.Length > max, listeners.Length);
    }
}

internal static class ProcessSnapshot
{
    public static SnapshotResult Capture(int maximumProcesses)
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot.IsInvalid) throw new AgentOpsException("snapshot_failed");

        var entries = new Dictionary<int, SnapshotEntry>();
        var item = new NativeMethods.ProcessEntry32 { Size = checked((uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>()) };
        if (!NativeMethods.Process32First(snapshot, ref item))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ErrorNoMoreFiles) return new(entries, false);
            throw new AgentOpsException("snapshot_failed", inner: new Win32Exception(error));
        }

        var truncated = false;
        while (true)
        {
            if (entries.Count == maximumProcesses)
            {
                truncated = true;
                break;
            }
            var pid = checked((int)item.ProcessId);
            entries[pid] = new SnapshotEntry(pid, checked((int)item.ParentProcessId));
            item.Size = checked((uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>());
            if (NativeMethods.Process32Next(snapshot, ref item)) continue;
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorNoMoreFiles)
                throw new AgentOpsException("snapshot_failed", inner: new Win32Exception(error));
            break;
        }

        return new(entries, truncated);
    }

    internal sealed record SnapshotResult(IReadOnlyDictionary<int, SnapshotEntry> Entries, bool Truncated);
    internal sealed record SnapshotEntry(int Pid, int ParentPid);

    private static class NativeMethods
    {
        internal const uint Th32csSnapProcess = 0x00000002;
        internal const int ErrorNoMoreFiles = 18;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry32
        {
            internal uint Size;
            internal uint Usage;
            internal uint ProcessId;
            internal nuint DefaultHeapId;
            internal uint ModuleId;
            internal uint Threads;
            internal uint ParentProcessId;
            internal int PriorityClassBase;
            internal uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string? ExecutableFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);
    }
}
