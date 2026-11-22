using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal enum WindowsServiceLifecycleAction
{
    Status,
    Start,
    Stop,
    Restart
}

internal sealed record WindowsServiceSnapshot(
    string Service,
    string State,
    int? ProcessId = null,
    string? ProcessStart = null);

internal sealed record WindowsServiceLifecycleResult(
    bool Ok,
    WindowsServiceLifecycleAction Action,
    WindowsServiceSnapshot Previous,
    WindowsServiceSnapshot Current,
    bool ActionIssued = false,
    string? Error = null,
    bool Partial = false);

internal interface IWindowsServiceLifecycle
{
    Task<WindowsServiceLifecycleResult> ExecuteAsync(
        string service,
        WindowsServiceLifecycleAction action,
        string? expectedState,
        int? expectedProcessId,
        string? expectedProcessStart,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fixed SCM adapter for the one explicitly admitted external service. It never
/// accepts an executable path, command line, machine name, or arbitrary service
/// identity from the caller.
/// </summary>
internal sealed class WindowsServiceLifecycle : IWindowsServiceLifecycle
{
    public async Task<WindowsServiceLifecycleResult> ExecuteAsync(
        string service,
        WindowsServiceLifecycleAction action,
        string? expectedState,
        int? expectedProcessId,
        string? expectedProcessStart,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Failure(action, service, "service_unavailable");
        if (!StringComparer.Ordinal.Equals(service, ApplicationAuthorizationProtocol.CerebralServiceName))
            return Failure(action, service, "service_not_admitted");
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(120))
            return Failure(action, service, "invalid");

        nint manager = 0;
        nint handle = 0;
        var issued = false;
        WindowsServiceSnapshot? previous = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var access = ServiceQueryStatus |
                (action is WindowsServiceLifecycleAction.Start or WindowsServiceLifecycleAction.Restart ? ServiceStart : 0) |
                (action is WindowsServiceLifecycleAction.Stop or WindowsServiceLifecycleAction.Restart ? ServiceStop : 0);
            handle = OpenService(manager, service, access);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

            previous = ReadSnapshot(handle, service);
            if (action == WindowsServiceLifecycleAction.Status)
                return new(true, action, previous, previous);
            if (!Matches(previous, expectedState, expectedProcessId, expectedProcessStart, out var mismatch))
                return new(false, action, previous, previous, Error: mismatch);

            var watch = Stopwatch.StartNew();
            if (action is WindowsServiceLifecycleAction.Stop or WindowsServiceLifecycleAction.Restart)
            {
                if (!ControlService(handle, ServiceControlStop, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceNotActive) return new(false, action, previous, ReadSnapshot(handle, service), Error: "precondition");
                    throw new Win32Exception(error);
                }
                issued = true;
                await WaitForAsync(handle, service, ServiceStopped, watch, timeout, cancellationToken).ConfigureAwait(false);
            }

            if (action is WindowsServiceLifecycleAction.Start or WindowsServiceLifecycleAction.Restart)
            {
                if (!StartService(handle, 0, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceAlreadyRunning) return new(false, action, previous, ReadSnapshot(handle, service), issued, issued ? "recovery_required" : "precondition", issued);
                    throw new Win32Exception(error);
                }
                issued = true;
                await WaitForAsync(handle, service, ServiceRunning, watch, timeout, cancellationToken).ConfigureAwait(false);
            }

            return new(true, action, previous, ReadSnapshot(handle, service), issued);
        }
        catch (OperationCanceledException) when (issued)
        {
            var current = TryReadSnapshot(handle, service, previous);
            return new(false, action, previous ?? current, current, true, "recovery_required", true);
        }
        catch (TimeoutException)
        {
            var current = TryReadSnapshot(handle, service, previous);
            return new(false, action, previous ?? current, current, issued, issued ? "recovery_required" : "timeout", issued);
        }
        catch (Win32Exception exception)
        {
            var current = TryReadSnapshot(handle, service, previous);
            var error = exception.NativeErrorCode switch
            {
                ErrorServiceDoesNotExist => "not_found",
                ErrorAccessDenied => "access",
                _ when issued => "recovery_required",
                _ => "external_failed"
            };
            return new(false, action, previous ?? current, current, issued, error, issued);
        }
        finally
        {
            if (handle != 0) CloseServiceHandle(handle);
            if (manager != 0) CloseServiceHandle(manager);
        }
    }

    private static bool Matches(
        WindowsServiceSnapshot actual,
        string? expectedState,
        int? expectedProcessId,
        string? expectedProcessStart,
        out string error)
    {
        error = "precondition";
        if (expectedState is null || !StringComparer.Ordinal.Equals(actual.State, expectedState)) return false;
        if (expectedState == "stopped") return expectedProcessId is null && expectedProcessStart is null;
        if (expectedProcessId is null || expectedProcessStart is null ||
            actual.ProcessId != expectedProcessId || actual.ProcessStart is null)
        {
            error = "pid_reused";
            return false;
        }

        if (!DateTimeOffset.TryParseExact(expectedProcessStart, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expected) ||
            !DateTimeOffset.TryParseExact(actual.ProcessStart, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var observed) ||
            expected.UtcTicks != observed.UtcTicks)
        {
            error = "pid_reused";
            return false;
        }
        return true;
    }

    private static async Task WaitForAsync(
        nint serviceHandle,
        string service,
        uint expected,
        Stopwatch watch,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadRawStatus(serviceHandle).CurrentState == expected) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"{service} did not reach the requested state.");
    }

    private static WindowsServiceSnapshot TryReadSnapshot(
        nint handle,
        string service,
        WindowsServiceSnapshot? fallback)
    {
        if (handle == 0) return fallback ?? new(service, "unknown");
        try { return ReadSnapshot(handle, service); }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException or InvalidOperationException)
        { return fallback ?? new(service, "unknown"); }
    }

    private static WindowsServiceSnapshot ReadSnapshot(nint serviceHandle, string service)
    {
        var status = ReadRawStatus(serviceHandle);
        var state = StateName(status.CurrentState);
        if (status.ProcessId == 0)
            return new(service, state);
        try
        {
            using var process = Process.GetProcessById(checked((int)status.ProcessId));
            return new(service, state, process.Id, process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            if (status.CurrentState == ServiceStopped) return new(service, state);
            throw new Win32Exception(ErrorProcessIdentityUnavailable, "The service process identity could not be verified.");
        }
    }

    private static ServiceStatusProcess ReadRawStatus(nint serviceHandle)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(serviceHandle, 0, buffer, (uint)size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string StateName(uint state) => state switch
    {
        ServiceStopped => "stopped",
        ServiceStartPending => "start_pending",
        ServiceStopPending => "stop_pending",
        ServiceRunning => "running",
        ServiceContinuePending => "continue_pending",
        ServicePausePending => "pause_pending",
        ServicePaused => "paused",
        _ => "unknown"
    };

    private static WindowsServiceLifecycleResult Failure(
        WindowsServiceLifecycleAction action,
        string service,
        string error)
    {
        var snapshot = new WindowsServiceSnapshot(service, "unknown");
        return new(false, action, snapshot, snapshot, Error: error);
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceContinuePending = 0x00000005;
    private const uint ServicePausePending = 0x00000006;
    private const uint ServicePaused = 0x00000007;
    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorProcessIdentityUnavailable = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int infoLevel, nint buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(nint service, uint argumentCount, nint arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(nint service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
