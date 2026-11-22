using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal enum CortexServiceControlAction
{
    Start,
    Stop,
    Restart,
    Install,
    EnableManualStartup,
    EnableAutomaticStartup,
    DisableStartup
}

internal sealed record CortexServiceControlResult(
    bool Ok,
    CortexServiceControlAction Action,
    string State,
    string? Error = null,
    string? Startup = null);

internal sealed record CortexServiceStatus(bool Installed, string State, string Startup,
    int ProcessId, string? Error = null);

internal interface ICortexServiceController
{
    Task<CortexServiceControlResult> ApplyAsync(
        CortexServiceControlAction action,
        CancellationToken cancellationToken);
}

/// <summary>
/// The resident host stays unelevated. An explicit service-control click runs
/// this same protected installed product binary once with UAC and one fixed action.
/// </summary>
internal sealed class ElevatedCortexServiceController(
    Func<string?>? executableResolver = null) : ICortexServiceController
{
    private readonly Func<string?> _executableResolver = executableResolver ?? (() => Environment.ProcessPath);

    public async Task<CortexServiceControlResult> ApplyAsync(
        CortexServiceControlAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executableValue = _executableResolver();
        if (string.IsNullOrWhiteSpace(executableValue))
            return new(false, action, "unknown", "host_path_unavailable");

        try
        {
            var executable = Path.GetFullPath(executableValue);
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
                (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
                return new(false, action, "unknown", "host_path_invalid");
            PathNormalizer.RejectAbsoluteReparseComponents(executable);
            var layout = CortexProductLayout.ResolveCurrent();
            var expectedHost = Path.Combine(layout.InstallRoot,
                CortexProductCatalogV1.StableRolePaths["host"].Replace('/', Path.DirectorySeparatorChar));
            if (!string.Equals(executable, expectedHost, StringComparison.OrdinalIgnoreCase))
                return new(false, action, "unknown", "host_path_mismatch");
            RequireProtectedImage(layout.InstallRoot, executable);
            string[] arguments;
            if (action == CortexServiceControlAction.Install)
            {
                executable = Path.Combine(layout.InstallRoot,
                    CortexProductCatalogV1.StableRolePaths["setup"].Replace('/', Path.DirectorySeparatorChar));
                RequireProtectedImage(layout.InstallRoot, executable);
                arguments = ["install-execution-service", "--install-root", layout.InstallRoot];
            }
            else arguments = ["--service-control", ActionName(action)];
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return new(false, action, "unknown", "elevated_helper_unavailable");
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken)
                .ConfigureAwait(false);
            var observed = WindowsCortexServiceControl.ReadStatus();
            var expected = action switch
            {
                CortexServiceControlAction.Start or CortexServiceControlAction.Restart => observed.State == "Running",
                CortexServiceControlAction.Stop => !observed.Installed || observed.State == "Stopped",
                CortexServiceControlAction.Install => observed.Installed,
                CortexServiceControlAction.EnableManualStartup => observed.Startup == "manual",
                CortexServiceControlAction.EnableAutomaticStartup => observed.Startup == "automatic",
                CortexServiceControlAction.DisableStartup => observed.Startup == "disabled",
                _ => false
            };
            var ok = process.ExitCode == 0 && observed.Error is null && expected;
            return new(ok, action, observed.State.ToLowerInvariant(), ok ? null :
                process.ExitCode switch
                {
                    10 => "service_work_unresolved",
                    11 => "service_shutdown_state_unavailable",
                    12 => "service_identity_conflict",
                    13 => "service_disabled",
                    0 => observed.Error ?? "service_postcondition_unconfirmed",
                    _ => $"service_control_exit_{process.ExitCode}"
                }, observed.Startup);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return new(false, action, "unchanged", "elevation_cancelled");
        }
        catch (TimeoutException)
        {
            return new(false, action, "unknown", "service_control_timeout");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or
            UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return new(false, action, "unknown", exception is Win32Exception native
                ? $"win32_{native.NativeErrorCode}"
                : exception.GetType().Name);
        }
    }

    internal static string ActionName(CortexServiceControlAction action) => action switch
    {
        CortexServiceControlAction.Start => "start",
        CortexServiceControlAction.Stop => "stop",
        CortexServiceControlAction.Restart => "restart",
        CortexServiceControlAction.Install => "install",
        CortexServiceControlAction.EnableManualStartup => "enable-manual",
        CortexServiceControlAction.EnableAutomaticStartup => "enable-automatic",
        CortexServiceControlAction.DisableStartup => "disable-startup",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private const int ErrorCancelled = 1223;

    internal static void RequireProtectedImage(string root, string executable)
    {
        // Preserve the existing absolute-path gate while sharing the same
        // image/ancestor policy with other fixed installed-host UAC actions.
        PathNormalizer.RejectAbsoluteReparseComponents(executable);
        CortexPrivilegedCodePath.RequireProtectedImage(root, executable);
    }
}

internal static class CortexServiceExit
{
    internal static Task<CortexServiceControlResult> StopIfRunningAsync(CancellationToken cancellationToken) =>
        StopIfRunningAsync(WindowsCortexServiceControl.ReadStatus, new ElevatedCortexServiceController(), cancellationToken);

    internal static async Task<CortexServiceControlResult> StopIfRunningAsync(
        Func<CortexServiceStatus> read, ICortexServiceController controller, CancellationToken cancellationToken)
    {
        const CortexServiceControlAction action = CortexServiceControlAction.Stop;
        var before = read();
        if (before.Error is not null) return new(false, action, "unknown", before.Error, before.Startup);
        if (!before.Installed || before.State == "Stopped") return new(true, action, "stopped", Startup: before.Startup);
        try
        {
            var result = await controller.ApplyAsync(action, cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return result;
            var after = read();
            return after.Error is null && (!after.Installed || after.State == "Stopped")
                ? new(true, action, "stopped", Startup: after.Startup)
                : new(false, action, "unknown", after.Error ?? "service_stop_unconfirmed", after.Startup);
        }
        catch (OperationCanceledException)
        { return new(false, action, "unknown", "service_stop_outcome_unknown", before.Startup); }
    }
}

internal static class CortexServiceExitGate
{
    internal static async Task<string?> StopForExitAsync(CancellationToken cancellationToken)
    {
        var result = await CortexServiceExit.StopIfRunningAsync(cancellationToken).ConfigureAwait(false);
        return result.Ok ? null : result.Error ?? "service_shutdown_unconfirmed";
    }
}

internal static class WindowsCortexServiceControl
{
    internal static CortexServiceStatus ReadStatus()
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Unknown", "unknown", 0, "windows_required");
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0) return new(false, "Unknown", "unknown", 0, "scm_unavailable");
        try
        {
            var service = OpenService(manager, CortexProductIdentity.ServiceName, ServiceQueryStatus | ServiceQueryConfig);
            if (service == 0) return Marshal.GetLastWin32Error() == 1060
                ? new(false, "Absent", "none", 0) : new(false, "Unknown", "unknown", 0, "service_query_denied");
            try
            {
                var configuration = ReadConfiguration(service);
                RequireOwnedConfiguration(configuration);
                var status = ReadStatus(service);
                return new(true, status.CurrentState switch
                {
                    1 => "Stopped", 2 => "Start pending", 3 => "Stop pending", 4 => "Running",
                    5 => "Continue pending", 6 => "Pause pending", 7 => "Paused", _ => "Unknown"
                }, configuration.StartType switch { 2 => "automatic", 3 => "manual", 4 => "disabled", _ => "unknown" },
                    checked((int)status.ProcessId));
            }
            finally { CloseServiceHandle(service); }
        }
        catch (Win32Exception) { return new(true, "Unknown", "unknown", 0, "service_query_failed"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or System.Security.SecurityException)
        { return new(true, "Unknown", "unknown", 0, "service_identity_conflict"); }
        finally { CloseServiceHandle(manager); }
    }

    internal static int RunElevated(string actionValue)
    {
        if (!TryParse(actionValue, out var action)) return 2;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 4;
            var layout = CortexProductLayout.ResolveCurrent();
            var expectedHost = Path.Combine(layout.InstallRoot,
                CortexProductCatalogV1.StableRolePaths["host"].Replace('/', Path.DirectorySeparatorChar));
            if (!string.Equals(Environment.ProcessPath, expectedHost, StringComparison.OrdinalIgnoreCase)) return 12;
            ElevatedCortexServiceController.RequireProtectedImage(layout.InstallRoot, expectedHost);
            _ = CortexProcessorPolicy.ApplyForInstallRoot(layout.InstallRoot);
            ApplyAsync(action).GetAwaiter().GetResult();
            return 0;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1058) { return 13; }
        catch (ServiceStopPreparationException exception) { return exception.Uncertain ? 11 : 10; }
        catch (InvalidDataException) { return 12; }
        catch (UnauthorizedAccessException) { return 12; }
        catch (Win32Exception) { return 8; }
        catch (TimeoutException) { return 8; }
        catch (OperationCanceledException) { return 11; }
    }

    internal static bool TryParse(string value, out CortexServiceControlAction action)
    {
        action = value switch
        {
            "start" => CortexServiceControlAction.Start,
            "stop" => CortexServiceControlAction.Stop,
            "restart" => CortexServiceControlAction.Restart,
            "enable-manual" => CortexServiceControlAction.EnableManualStartup,
            "enable-automatic" => CortexServiceControlAction.EnableAutomaticStartup,
            "disable-startup" => CortexServiceControlAction.DisableStartup,
            _ => (CortexServiceControlAction)(-1)
        };
        return Enum.IsDefined(action);
    }

    private static async Task ApplyAsync(CortexServiceControlAction action)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, CortexProductIdentity.ServiceName,
                ServiceQueryStatus | ServiceQueryConfig | ServiceStart | ServiceStop |
                (action is CortexServiceControlAction.EnableManualStartup or CortexServiceControlAction.EnableAutomaticStartup or
                    CortexServiceControlAction.DisableStartup ? ServiceChangeConfig : 0));
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                RequireOwnedConfiguration(ReadConfiguration(service));
                if (action is CortexServiceControlAction.EnableManualStartup or CortexServiceControlAction.EnableAutomaticStartup or CortexServiceControlAction.DisableStartup)
                {
                    var startup = action == CortexServiceControlAction.EnableManualStartup ? 3u :
                        action == CortexServiceControlAction.EnableAutomaticStartup ? 2u : 4u;
                    if (!ChangeServiceConfig(service, uint.MaxValue, startup, uint.MaxValue,
                            null, null, 0, null, null, null, null))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (ReadConfiguration(service).StartType != startup)
                        throw new InvalidDataException("Service startup policy was not confirmed.");
                    return;
                }
                if (action is CortexServiceControlAction.Stop or CortexServiceControlAction.Restart)
                    await StopSafelyAsync(service).ConfigureAwait(false);
                if (action is CortexServiceControlAction.Start or CortexServiceControlAction.Restart)
                {
                    RequireOwnedConfiguration(ReadConfiguration(service));
                    Start(service);
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static void Start(nint service)
    {
        var state = ReadState(service);
        if (state == ServiceRunning) return;
        if (state == ServiceStartPending)
        {
            WaitFor(service, ServiceRunning);
            return;
        }
        if (!StartService(service, 0, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning) throw new Win32Exception(error);
        }
        WaitFor(service, ServiceRunning);
    }

    private static async Task StopSafelyAsync(nint service)
    {
        var before = ReadStatus(service);
        if (before.CurrentState == ServiceStopped) return;
        if (before.CurrentState == ServiceStopPending) { WaitFor(service, ServiceStopped); return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var dispatcher = new NamedPipeDispatcher(NamedPipeDispatcher.SystemPipeName,
            expectedServer: new LocalPeer("S-1-5-18", 0));
        var request = new OperationRequest(AuthenticatedServiceBroker.ServiceExitOperation,
            new Dictionary<string, string> { ["op"] = "prepare" }, Rid: Guid.NewGuid().ToString("N"));
        var prepared = await dispatcher.DispatchAsync(request, timeout.Token).ConfigureAwait(false);
        var lease = DataText(prepared, "lease");
        var preparedStart = DataText(prepared, "service_start_ticks");
        if (!prepared.Ok || prepared.Partial || prepared.Op != request.Op || prepared.Rid != request.Rid ||
            prepared.State != "done" || DataText(prepared, "state") != "prepared" ||
            !Guid.TryParseExact(lease, "N", out _) || !uint.TryParse(DataText(prepared, "service_pid"), out var preparedPid) ||
            !long.TryParse(preparedStart, out _))
            throw new ServiceStopPreparationException(prepared.Err is not
                ("service_work_active" or "service_work_unresolved" or "service_admin_work_unresolved" or
                 "work_active" or "work_unresolved" or "applications_active"));
        try
        {
            var current = ReadStatus(service);
            if (current.ProcessId != before.ProcessId || current.ProcessId != preparedPid)
                throw new ServiceStopPreparationException(true);
            var commitRequest = new OperationRequest(request.Op,
                new Dictionary<string, string> { ["op"] = "commit", ["lease"] = lease! },
                Rid: Guid.NewGuid().ToString("N"));
            var committed = await dispatcher.DispatchAsync(commitRequest, timeout.Token).ConfigureAwait(false);
            if (!committed.Ok || committed.Partial || committed.Op != request.Op ||
                committed.Rid != commitRequest.Rid || committed.State != "done" ||
                DataText(committed, "state") != "committed" || DataText(committed, "lease") != lease ||
                DataText(committed, "service_pid") != preparedPid.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                DataText(committed, "service_start_ticks") != preparedStart)
                throw new ServiceStopPreparationException(true);
            // The prepared instance commits its own stop. A replacement never
            // receives a name-based SCM stop from this control helper.
            WaitForStoppedInstance(service, preparedPid, preparedStart!);
        }
        catch
        {
            // Reopen admission only after a known refusal with the SAME service
            // still running. Pending/unknown shutdown retains its reservation.
            try
            {
                var current = ReadStatus(service);
                if (current.CurrentState == ServiceRunning && current.ProcessId == preparedPid)
                {
                    using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    _ = await dispatcher.DispatchAsync(new OperationRequest(request.Op,
                        new Dictionary<string, string> { ["op"] = "cancel", ["lease"] = lease! },
                        Rid: Guid.NewGuid().ToString("N")), cancelTimeout.Token).ConfigureAwait(false);
                }
            }
            catch { /* The primary stop failure remains; no implicit retry or force. */ }
            throw;
        }
    }

    private static string? DataText(OperationReceipt receipt, string key) =>
        receipt.Data is not null && receipt.Data.TryGetValue(key, out var value)
            ? value is JsonElement json ? json.ToString() : value?.ToString() : null;

    private sealed class ServiceStopPreparationException(bool uncertain) : Exception
    { internal bool Uncertain { get; } = uncertain; }

    private static void WaitForStoppedInstance(nint service, uint expectedPid, string expectedStart)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var status = ReadStatus(service);
            if (status.CurrentState == ServiceStopped) return;
            if (status.ProcessId != 0)
            {
                if (status.ProcessId != expectedPid) throw new ServiceStopPreparationException(true);
                try
                {
                    using var process = Process.GetProcessById(checked((int)status.ProcessId));
                    if (process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) != expectedStart)
                        throw new ServiceStopPreparationException(true);
                }
                catch (ArgumentException) { /* Observe SCM again after natural process exit. */ }
            }
            Thread.Sleep(100);
        }
        throw new ServiceStopPreparationException(true);
    }

    private static void WaitFor(nint service, uint expected)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (ReadState(service) == expected) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("The Cortex service did not reach the requested state.");
    }

    private static uint ReadState(nint service) => ReadStatus(service).CurrentState;

    private static ServiceStatusProcess ReadStatus(nint service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, (uint)size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;

    private sealed record ServiceConfiguration(string BinaryPath, string Account, uint StartType);

    private static ServiceConfiguration ReadConfiguration(nint service)
    {
        _ = QueryServiceConfig(service, 0, 0, out var bytes);
        if (bytes < Marshal.SizeOf<ServiceConfigurationNative>() || bytes > 65536 || Marshal.GetLastWin32Error() != 122)
            throw new InvalidDataException("The service configuration is unavailable or oversized.");
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!QueryServiceConfig(service, buffer, bytes, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var value = Marshal.PtrToStructure<ServiceConfigurationNative>(buffer);
            return new(Marshal.PtrToStringUni(value.BinaryPathName) ?? "",
                Marshal.PtrToStringUni(value.ServiceStartName) ?? "", value.StartType);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void RequireOwnedConfiguration(ServiceConfiguration configuration)
    {
        var layout = CortexProductLayout.ResolveCurrent();
        var expected = Path.Combine(layout.InstallRoot,
            CortexProductCatalogV1.StableRolePaths["service"].Replace('/', Path.DirectorySeparatorChar));
        if (!string.Equals(configuration.BinaryPath, '"' + expected + '"', StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(configuration.Account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The service name belongs to a different image or account.");
        PathNormalizer.RejectAbsoluteReparseComponents(expected);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfigurationNative
    {
        public uint ServiceType, StartType, ErrorControl;
        public nint BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public nint Dependencies, ServiceStartName, DisplayName;
    }
    private const int ErrorServiceAlreadyRunning = 1056;

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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(nint service, nint configuration, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(nint service, uint type, uint startup, uint errorControl,
        string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int infoLevel, nint buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(nint service, uint argumentCount, nint arguments);


    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
