using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

// Public observations deliberately omit binary command lines, arguments and accounts.
internal sealed record WindowsServiceItem(string Service, string DisplayName, string State, uint ServiceType, uint ProcessId);
internal sealed record WindowsServicePage(IReadOnlyList<WindowsServiceItem> Services, string? Next, bool HasMore, bool BoundReached,
    string Visibility = "caller_queryable_services_only");
internal sealed record WindowsServiceDetails(string Service, string DisplayName, string State, uint ServiceType,
    string Startup, bool DelayedAutoStart, string ConfigSha, int? Pid, string? Start, uint ControlsAccepted);
internal sealed record WindowsServiceOutcome(bool Ok, string Action, WindowsServiceDetails? Previous, WindowsServiceDetails? Current,
    bool ActionIssued = false, bool OutcomeUnknown = false, string? Error = null, string Phase = "observation", int? NativeError = null);
internal interface IWindowsServiceHandle : IDisposable
{
    WindowsServiceDetails Read();
    string ReadState();
    void Start();
    void Stop();
    void SetStartType(uint value);
    void SetDelayedAutoStart(bool value);
}
internal interface IWindowsServicePlatform
{
    WindowsServicePage List(WindowsServiceInvocation invocation, CancellationToken token);
    IWindowsServiceHandle Open(string service, string action);
}

// The handle binds one SCM service object, not an atomic compare-and-swap or
// a generation lease. Preconditions are rechecked before lifecycle effects;
// startup configuration is an explicit, potentially partial two-write change.
internal sealed class WindowsServiceOperations
{
    private readonly IWindowsServicePlatform _platform;
    private readonly string? _administratorSid;
    private readonly int _administratorSession;
    private readonly Func<LocalPeer>? _currentAdministrator;
    internal WindowsServiceOperations(IWindowsServicePlatform? platform = null) =>
        _platform = platform ?? new NativeWindowsServicePlatform();
    private WindowsServiceOperations(string ownerSid, int expectedSession, Func<LocalPeer> current, IWindowsServicePlatform platform)
    {
        _ = new OwnerHostIdentity(ownerSid, () => new(ownerSid, expectedSession));
        if (expectedSession <= 0) throw new ArgumentException("service_elevation_identity_invalid");
        _administratorSid = ownerSid; _administratorSession = expectedSession;
        _currentAdministrator = current ?? throw new ArgumentNullException(nameof(current));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }
    [SupportedOSPlatform("windows")]
    internal static WindowsServiceOperations ForOwnerAdministrator(string ownerSid, int expectedSession) =>
        ForOwnerAdministrator(ownerSid, expectedSession, LocalIdentity.Current, new NativeWindowsServicePlatform());
    internal static WindowsServiceOperations ForOwnerAdministrator(string ownerSid, int expectedSession,
        Func<LocalPeer> current, IWindowsServicePlatform platform)
    {
        var operations = new WindowsServiceOperations(ownerSid, expectedSession, current, platform);
        if (!operations.AdministratorCurrent()) throw new AgentOpsException("auth");
        return operations;
    }

    internal WindowsServicePage List(WindowsServiceInvocation invocation, CancellationToken token)
    {
        if (_currentAdministrator is not null || invocation.RequiresOwnerElevation)
            throw new AgentOpsException("service_elevation_route_required");
        return _platform.List(invocation, token);
    }

    internal async Task<WindowsServiceOutcome> ExecuteAsync(WindowsServiceInvocation invocation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (_currentAdministrator is null && invocation.RequiresOwnerElevation)
            return new(false, invocation.Action, null, null, Error: "service_owner_elevation_required", Phase: "admission");
        if (_currentAdministrator is not null && (!invocation.RequiresOwnerElevation ||
            !WindowsServiceOperationContract.IsOwnerElevationAction(invocation.Action)))
            return new(false, invocation.Action, null, null, Error: "service_elevation_route_required", Phase: "admission");
        var outcome = await ExecuteCoreAsync(invocation, token).ConfigureAwait(false);
        // This fence runs after the retained SCM handle is disposed too. It
        // never rolls back or erases a write that was already issued.
        if (!AdministratorCurrent()) return new(false, invocation.Action, null, null, outcome.ActionIssued,
            outcome.OutcomeUnknown || outcome.ActionIssued, outcome.ActionIssued ? "service_outcome_unknown" : "service_owner_context_required", outcome.Phase);
        return outcome;
    }

    private async Task<WindowsServiceOutcome> ExecuteCoreAsync(WindowsServiceInvocation invocation, CancellationToken token)
    {
        var service = invocation.Service ?? throw new AgentOpsException("invalid");
        if (invocation.IsMutation && string.Equals(service, CortexProductIdentity.ServiceName, StringComparison.OrdinalIgnoreCase))
            return new(false, invocation.Action, null, null, Error: "service_requires_cortex_control");
        IWindowsServiceHandle? handle = null;
        WindowsServiceDetails? previous = null;
        var issued = false;
        var phase = "open";
        try
        {
            token.ThrowIfCancellationRequested(); RequireAdministratorContext();
            handle = _platform.Open(service, invocation.Action); RequireAdministratorContext();
            previous = ReadBound(handle);
            if ((previous.ServiceType & 0x30) == 0) throw new AgentOpsException("service_type_not_supported");
            if (!invocation.IsMutation) return new(true, invocation.Action, previous, previous);
            if (!Matches(previous, invocation, includeProcess: true))
                return new(false, invocation.Action, previous, previous, Error: "precondition");
            token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            if (invocation.Action is "stop" or "restart")
            {
                phase = "stop";
                if (!Matches(ReadBound(handle), invocation, includeProcess: true)) throw new AgentOpsException("precondition");
                token.ThrowIfCancellationRequested();
                RequireAdministratorContext(); issued = true; handle.Stop(); RequireAdministratorContext();
                await WaitAsync(handle, "stopped", watch, invocation.TimeoutSeconds, token).ConfigureAwait(false);
            }
            if (invocation.Action is "start" or "restart")
            {
                phase = "start";
                var current = ReadBound(handle);
                if (current.State != "stopped" || current.ConfigSha != invocation.ConfigSha) throw new AgentOpsException("precondition");
                token.ThrowIfCancellationRequested();
                RequireAdministratorContext(); issued = true; handle.Start(); RequireAdministratorContext();
                await WaitAsync(handle, "running", watch, invocation.TimeoutSeconds, token).ConfigureAwait(false);
            }
            if (invocation.Action == "startup")
            {
                var desiredType = invocation.Mode is "automatic" or "automatic_delayed" ? 2u : invocation.Mode == "manual" ? 3u : 4u;
                var desiredDelay = invocation.Mode == "automatic_delayed";
                if (previous.Startup == invocation.Mode && previous.DelayedAutoStart == desiredDelay)
                    return new(true, invocation.Action, previous, previous, Phase: "unchanged");
                phase = "startup";
                if (!Matches(ReadBound(handle), invocation, includeProcess: false)) throw new AgentOpsException("precondition");
                token.ThrowIfCancellationRequested();
                // This is intentionally two native writes, never a transaction.
                // Clear delayed state before leaving automatic; set it after entering.
                if (desiredType != 2 && previous.DelayedAutoStart)
                { RequireAdministratorContext(); issued = true; handle.SetDelayedAutoStart(false); RequireAdministratorContext(); token.ThrowIfCancellationRequested(); }
                RequireAdministratorContext(); issued = true; handle.SetStartType(desiredType); RequireAdministratorContext();
                token.ThrowIfCancellationRequested();
                if (desiredType == 2) { RequireAdministratorContext(); handle.SetDelayedAutoStart(desiredDelay); RequireAdministratorContext(); }
            }
            phase = "postcondition";
            var observed = ReadBound(handle);
            var matches = invocation.Action switch
            {
                "stop" => observed.State == "stopped" && observed.ConfigSha == invocation.ConfigSha,
                "start" or "restart" => observed.State == "running" && observed.ConfigSha == invocation.ConfigSha,
                "startup" => observed.Startup == invocation.Mode && observed.DelayedAutoStart == (invocation.Mode == "automatic_delayed"),
                _ => true
            };
            return new(matches, invocation.Action, previous, observed, issued, !matches,
                matches ? null : "service_outcome_unknown", phase);
        }
        catch (Exception failure) when (failure is AgentOpsException or Win32Exception or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException)
        {
            if (!AdministratorCurrent()) return new(false, invocation.Action, null, null, issued, issued,
                issued ? "service_outcome_unknown" : "service_owner_context_required", phase);
            var current = TryRead(handle);
            if (!AdministratorCurrent()) return new(false, invocation.Action, null, null, issued, issued,
                issued ? "service_outcome_unknown" : "service_owner_context_required", phase);
            var code = failure switch
            {
                AgentOpsException known => known.Code,
                Win32Exception native when native.NativeErrorCode == 5 => "access",
                Win32Exception native when native.NativeErrorCode == 1060 => "not_found",
                Win32Exception native when native.NativeErrorCode == 1058 => "service_disabled",
                Win32Exception native when native.NativeErrorCode == 1051 => "service_dependents_active",
                OperationCanceledException => "canceled",
                TimeoutException => "timeout",
                _ => "service_failed"
            };
            return new(false, invocation.Action, previous, current, issued, issued,
                issued ? "service_outcome_unknown" : code, phase, failure is Win32Exception error ? error.NativeErrorCode : null);
        }
        finally { handle?.Dispose(); }
    }

    private bool AdministratorCurrent()
    {
        if (_currentAdministrator is null) return true; // Legacy caller-context/System path is unchanged.
        try
        {
            var current = _currentAdministrator();
            return current.Sid == _administratorSid && current.Session == _administratorSession && current.Session > 0 &&
                current.Elevated && current.Sid is not ("S-1-5-18" or "S-1-5-19" or "S-1-5-20");
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    private void RequireAdministratorContext()
    { if (!AdministratorCurrent()) throw new AgentOpsException("service_owner_context_required"); }
    private WindowsServiceDetails ReadBound(IWindowsServiceHandle handle)
    {
        RequireAdministratorContext();
        var result = handle.Read();
        RequireAdministratorContext();
        return result;
    }

    private static bool Matches(WindowsServiceDetails actual, WindowsServiceInvocation expected, bool includeProcess) =>
        actual.ConfigSha == expected.ConfigSha && actual.State == expected.ExpectedState &&
        (!includeProcess || expected.Action is not ("stop" or "restart") ||
            actual.Pid == expected.ProcessId && actual.Start is not null && actual.Start == expected.ProcessStart);
    private static WindowsServiceDetails? TryRead(IWindowsServiceHandle? handle)
    {
        try { return handle?.Read(); }
        catch (Exception error) when (error is Win32Exception or AgentOpsException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return null; }
    }
    private async Task WaitAsync(IWindowsServiceHandle handle, string desired, Stopwatch watch, int seconds, CancellationToken token)
    {
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            token.ThrowIfCancellationRequested();
            RequireAdministratorContext();
            var state = handle.ReadState(); RequireAdministratorContext();
            if (state == desired) return;
            await Task.Delay(100, token).ConfigureAwait(false);
            RequireAdministratorContext();
        }
        throw new TimeoutException();
    }
}

// Local Unicode SCM APIs only. No shell, service executable parsing/execution,
// dependency traversal, arbitrary control, remote machine or driver mutation.
internal sealed class NativeWindowsServicePlatform : IWindowsServicePlatform
{
    private const uint QueryConfig = 1, ChangeConfig = 2, QueryStatus = 4, StartAccess = 0x10, StopAccess = 0x20;
    private const int MaximumInventory = 4096, MaximumBuffer = 256 * 1024;
    public WindowsServicePage List(WindowsServiceInvocation invocation, CancellationToken token)
    {
        RequireWindows();
        using var manager = Manager(4);
        var entries = new List<WindowsServiceItem>();
        var buffer = Marshal.AllocHGlobal(MaximumBuffer);
        uint resume = 0;
        var bound = false;
        try
        {
            for (var page = 0; ; page++)
            {
                token.ThrowIfCancellationRequested();
                var before = resume;
                var ok = EnumServicesStatusExW(manager, 0, 0x30, 3, buffer, MaximumBuffer, out _, out var count, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != 234) throw new Win32Exception(error);
                var size = Marshal.SizeOf<EnumerationEntry>();
                if (count > MaximumBuffer / size) throw new AgentOpsException("service_inventory_invalid");
                for (var index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (entries.Count == MaximumInventory) { bound = true; break; }
                    var item = Marshal.PtrToStructure<EnumerationEntry>(buffer + checked((int)index * size));
                    var name = ReadText(item.Name, buffer, MaximumBuffer, 256);
                    WindowsServiceOperationContract.RequireName(name);
                    entries.Add(new(name, ReadText(item.DisplayName, buffer, MaximumBuffer, 256),
                        State(item.Status.CurrentState), item.Status.ServiceType, item.Status.ProcessId));
                }
                if (bound || ok || resume == 0) break;
                if (page == 63) { bound = true; break; }
                if (count == 0 && resume == before) throw new AgentOpsException("service_inventory_unavailable");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        var selected = entries.OrderBy(item => item.Service, StringComparer.OrdinalIgnoreCase)
            .Where(item => (invocation.After is null || StringComparer.OrdinalIgnoreCase.Compare(item.Service, invocation.After) > 0) &&
                (invocation.Pattern is null || item.Service.Contains(invocation.Pattern, StringComparison.OrdinalIgnoreCase) ||
                    item.DisplayName.Contains(invocation.Pattern, StringComparison.OrdinalIgnoreCase)))
            .Take(invocation.Maximum + 1).ToArray();
        var more = selected.Length > invocation.Maximum;
        var result = selected.Take(invocation.Maximum).ToArray();
        return new(result, more ? result[^1].Service : null, more, bound);
    }

    public IWindowsServiceHandle Open(string service, string action)
    {
        RequireWindows();
        WindowsServiceOperationContract.RequireName(service);
        if (action != "read" && string.Equals(service, CortexProductIdentity.ServiceName, StringComparison.OrdinalIgnoreCase))
            throw new AgentOpsException("service_requires_cortex_control");
        using var manager = Manager(1);
        var access = QueryConfig | QueryStatus |
            (action is "start" or "restart" ? StartAccess : 0) |
            (action is "stop" or "restart" ? StopAccess : 0) | (action == "startup" ? ChangeConfig : 0);
        var handle = OpenServiceW(manager, service, access);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return new NativeHandle(handle, service);
    }

    private sealed class NativeHandle(ServiceHandle handle, string service) : IWindowsServiceHandle
    {
        public WindowsServiceDetails Read()
        {
            var status = Status(handle);
            var configuration = Configuration(handle);
            int? pid = status.ProcessId is > 0 and <= int.MaxValue ? (int)status.ProcessId : null;
            string? start = null;
            if (pid is not null && status.CurrentState != 1)
            {
                using var process = OpenProcess(0x1000, false, checked((uint)pid.Value));
                if (!process.IsInvalid && GetProcessTimes(process, out var creation, out _, out _, out _))
                    start = DateTime.FromFileTimeUtc(creation).ToString("O", CultureInfo.InvariantCulture);
            }
            if (status.CurrentState == 1) pid = null;
            return new(service, configuration.DisplayName, State(status.CurrentState), status.ServiceType,
                Startup(configuration.StartType, configuration.Delayed), configuration.Delayed, configuration.Sha,
                pid, start, status.ControlsAccepted);
        }
        public string ReadState() => State(Status(handle).CurrentState);
        public void Start() { if (!StartServiceW(handle, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void Stop() { if (!ControlService(handle, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void SetStartType(uint value)
        {
            if (!ChangeServiceConfigW(handle, uint.MaxValue, value, uint.MaxValue, null, null, 0, null, null, null, null))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void SetDelayedAutoStart(bool value)
        {
            var info = new DelayedInfo { Delayed = value };
            if (!ChangeServiceConfig2W(handle, 3, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => handle.Dispose();
    }

    private sealed record ConfigurationView(string DisplayName, uint StartType, bool Delayed, string Sha);
    private static ConfigurationView Configuration(ServiceHandle handle)
    {
        _ = QueryServiceConfigW(handle, 0, 0, out var needed);
        if (Marshal.GetLastWin32Error() != 122 || needed < Marshal.SizeOf<ConfigurationNative>() || needed > 65536)
            throw new AgentOpsException("service_configuration_unavailable");
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!QueryServiceConfigW(handle, buffer, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var value = Marshal.PtrToStructure<ConfigurationNative>(buffer);
            if (!QueryServiceConfig2W(handle, 3, out var delay, (uint)Marshal.SizeOf<DelayedInfo>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // Raw configuration is consumed only by the fingerprint. It never
            // enters receipts, exceptions, help, logs or a returned observation.
            var identity = new
            {
                value.ServiceType, value.StartType, value.ErrorControl, value.TagId,
                binary = ReadText(value.BinaryPath, buffer, needed, 32767),
                group = ReadText(value.Group, buffer, needed, 32767),
                dependencies = ReadMulti(value.Dependencies, buffer, needed),
                account = ReadText(value.Account, buffer, needed, 32767),
                displayName = ReadText(value.DisplayName, buffer, needed, 256),
                delayed = delay.Delayed
            };
            var encoded = JsonSerializer.SerializeToUtf8Bytes(identity);
            try { return new(identity.displayName, value.StartType, delay.Delayed, Convert.ToHexStringLower(SHA256.HashData(encoded))); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadText(nint value, nint buffer, long bytes, int maximum)
    {
        if (value == 0) return string.Empty;
        var offset = value.ToInt64() - buffer.ToInt64();
        if (offset < 0 || offset >= bytes || offset % 2 != 0) throw new AgentOpsException("service_native_buffer_invalid");
        var limit = checked((int)Math.Min(maximum + 1L, (bytes - offset) / 2));
        for (var index = 0; index < limit; index++)
            if (Marshal.ReadInt16(value, index * 2) == 0) return Marshal.PtrToStringUni(value, index)!;
        throw new AgentOpsException("service_native_buffer_invalid");
    }
    private static string[] ReadMulti(nint value, nint buffer, uint bytes)
    {
        var values = new List<string>();
        if (value == 0) return [];
        while (true)
        {
            var item = ReadText(value, buffer, bytes, 256);
            if (item.Length == 0) return values.ToArray();
            if (values.Count == 256) throw new AgentOpsException("service_native_buffer_invalid");
            values.Add(item); value += (item.Length + 1) * 2;
        }
    }
    private static ServiceStatusProcess Status(ServiceHandle handle)
    {
        if (!QueryServiceStatusEx(handle, 0, out var value, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }
    private static string Startup(uint type, bool delayed) => type switch
    { 2 => delayed ? "automatic_delayed" : "automatic", 3 => "manual", 4 => "disabled", _ => "unsupported" };
    private static string State(uint state) => state switch
    { 1 => "stopped", 2 => "start_pending", 3 => "stop_pending", 4 => "running", 5 => "continue_pending", 6 => "pause_pending", 7 => "paused", _ => "unknown" };
    private static void RequireWindows() { if (!OperatingSystem.IsWindows()) throw new AgentOpsException("service_unavailable"); }
    private static ServiceHandle Manager(uint access)
    {
        var handle = OpenSCManagerW(null, null, access);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct EnumerationEntry { public nint Name, DisplayName; public ServiceStatusProcess Status; }
    [StructLayout(LayoutKind.Sequential)] private struct ConfigurationNative
    { public uint ServiceType, StartType, ErrorControl; public nint BinaryPath, Group; public uint TagId; public nint Dependencies, Account, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct DelayedInfo { [MarshalAs(UnmanagedType.Bool)] public bool Delayed; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string service, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(ServiceHandle manager, int level, uint type, uint state, nint buffer, int size,
        out uint needed, out uint returned, ref uint resume, string? group);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(ServiceHandle service, nint buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(ServiceHandle service, int level, out DelayedInfo info, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(ServiceHandle service, uint count, nint arguments);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error, string? binary,
        string? group, nint tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(ServiceHandle service, int level, ref DelayedInfo info);
    [DllImport("advapi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
