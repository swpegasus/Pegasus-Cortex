using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

// Only the protected installed Host can redeem a one-shot owner ticket. The
// original ShellExecute process handle remains the identity and wait authority.
[SupportedOSPlatform("windows")]
internal sealed class WindowsOwnerElevationLauncher : IOwnerCertificateElevationLauncher, IOwnerMachineCertificateElevationLauncher,
    IOwnerScheduledTaskElevationLauncher, IOwnerWindowsServiceElevationLauncher
{
    private readonly CortexProductLayout _layout;
    private readonly LocalPeer _owner;
    private readonly Func<LocalPeer> _current;
    private readonly Func<CertificateElevationProcessIdentity> _parent;
    private readonly Action<string, string> _requireProtected;
    private readonly Func<ProcessStartInfo, IOwnerCertificateElevationProcess> _start;

    internal WindowsOwnerElevationLauncher(CortexProductLayout layout, LocalPeer ordinaryOwner)
        : this(layout, ordinaryOwner, LocalIdentity.Current, CurrentIdentity,
            CortexPrivilegedCodePath.RequireProtectedImage, StartNative) { }

    internal WindowsOwnerElevationLauncher(CortexProductLayout layout, LocalPeer ordinaryOwner,
        Func<LocalPeer> current, Func<CertificateElevationProcessIdentity> parent,
        Action<string, string> requireProtected, Func<ProcessStartInfo, IOwnerCertificateElevationProcess> start)
    {
        _layout = layout; _owner = ordinaryOwner; _current = current; _parent = parent;
        _requireProtected = requireProtected; _start = start;
    }

    public Task<IOwnerCertificateElevationProcess> StartAsync(string ticket, CancellationToken token) =>
        StartCoreAsync(ticket, OwnerElevationPurpose.CertificateStore, token);

    public Task<IOwnerCertificateElevationProcess> StartMachineServerAsync(string ticket, CancellationToken token) =>
        StartCoreAsync(ticket, OwnerElevationPurpose.MachineServerCertificate, token);

    public Task<IOwnerCertificateElevationProcess> StartScheduledTaskAsync(string ticket, CancellationToken token) =>
        StartCoreAsync(ticket, OwnerElevationPurpose.ScheduledTaskControl, token);

    public Task<IOwnerCertificateElevationProcess> StartWindowsServiceAsync(string ticket, CancellationToken token) =>
        StartCoreAsync(ticket, OwnerElevationPurpose.WindowsServiceControl, token);

    private Task<IOwnerCertificateElevationProcess> StartCoreAsync(string ticket, OwnerElevationPurpose purpose, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (purpose == OwnerElevationPurpose.ScheduledTaskControl) CortexScheduledTaskElevation.RequireTicket(ticket);
        else if (purpose == OwnerElevationPurpose.WindowsServiceControl) CortexWindowsServiceElevation.RequireTicket(ticket);
        else CortexCertificateStoreElevation.RequireTicket(ticket);
        var current = _current();
        if (_owner.Elevated || _owner.Session <= 0 || _owner.Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" ||
            _layout.OwnerSid != _owner.Sid || current.Elevated || !current.SameIdentity(_owner))
            throw new AgentOpsException("certificate_elevation_owner_denied");
        var host = CortexOwnerElevation.HostPath(_layout);
        var parent = _parent();
        if (current.ProcessId != parent.Pid || !SamePath(current.ApplicationPath, host) || !SamePath(parent.ApplicationPath, host) ||
            !CortexOwnerElevation.CanonicalStart(parent.Start))
            throw new AgentOpsException("certificate_elevation_host_invalid");
        _requireProtected(_layout.InstallRoot, host);
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(host)!
        };
        foreach (var argument in new[] { purpose switch
            {
                OwnerElevationPurpose.CertificateStore => "--certificate-store-once",
                OwnerElevationPurpose.MachineServerCertificate => CortexMachineServerCertificateElevation.HelperFlag,
                OwnerElevationPurpose.ScheduledTaskControl => CortexScheduledTaskElevation.HelperFlag,
                OwnerElevationPurpose.WindowsServiceControl => CortexWindowsServiceElevation.HelperFlag,
                _ => throw new ArgumentOutOfRangeException(nameof(purpose))
            },
            ticket, parent.Pid.ToString(CultureInfo.InvariantCulture), parent.Start })
            start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        // Once ShellExecute starts, cancellation cannot retract its UAC prompt or
        // child. The coordinator revokes an unredeemed ticket and still observes
        // this original process; never detach, kill, or repeat the launch here.
        return Task.Run(() => _start(start), CancellationToken.None);
    }

    public CertificateElevationProcessIdentity? CaptureElevatedPeer(LocalPeer peer)
    {
        if (!peer.SameIdentity(_owner) || !SamePath(peer.ApplicationPath, CortexOwnerElevation.HostPath(_layout))) return null;
        var identity = WindowsOwnerAdminLauncher.CaptureElevatedPeerIdentity(peer);
        return identity is not null && identity.Pid == peer.ProcessId && SamePath(identity.ApplicationPath, CortexOwnerElevation.HostPath(_layout))
            ? identity : null;
    }

    private static bool SamePath(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static CertificateElevationProcessIdentity CurrentIdentity()
    { using var process = Process.GetCurrentProcess(); return Snapshot(process); }
    private static IOwnerCertificateElevationProcess StartNative(ProcessStartInfo start)
    {
        var process = Process.Start(start) ?? throw new AgentOpsException("certificate_elevation_start_failed");
        try { return new RetainedProcess(process, start.FileName); }
        catch { process.Dispose(); throw; }
    }
    private sealed class RetainedProcess : IOwnerCertificateElevationProcess
    {
        private readonly Process _process;
        internal RetainedProcess(Process process, string expectedImage)
        {
            _process = process;
            Identity = Snapshot(process);
            if (!SamePath(Identity.ApplicationPath, expectedImage)) throw new AgentOpsException("certificate_elevation_host_invalid");
        }
        public CertificateElevationProcessIdentity Identity { get; }
        public async Task<int> WaitForExitAsync()
        { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); return _process.ExitCode; }
        public void Dispose() => _process.Dispose();
    }

    private static CertificateElevationProcessIdentity Snapshot(Process process)
    {
        // ShellExecuteEx's original process handle binds both observations and
        // the later wait. No PID lookup/MainModule reopen can switch generations.
        var handle = process.SafeHandle;
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(512);
        var length = path.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref length) || length is < 1 or > 240)
            throw new AgentOpsException("certificate_elevation_host_invalid");
        return new(checked((uint)process.Id), DateTime.FromFileTimeUtc(created).ToString("O", CultureInfo.InvariantCulture), Path.GetFullPath(path.ToString()));
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}


[SupportedOSPlatform("windows")]
internal static class CortexOwnerElevation
{
    internal static string HostPath(CortexProductLayout layout) => Path.GetFullPath(Path.Combine(layout.InstallRoot,
        CortexProductCatalogV1.StableRolePaths["host"].Replace('/', Path.DirectorySeparatorChar)));

    internal static bool CanonicalStart(string start) => DateTime.TryParseExact(start, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var parsed) && parsed.Kind == DateTimeKind.Utc && parsed.ToString("O", CultureInfo.InvariantCulture) == start;

    // The domain helper validates its ticket before opening this transport. This
    // context never initializes an ordinary host, DB, tray, service or payload.
    internal static HelperContext OpenHelperContext(uint parentPid, string parentStart)
    {
        if (parentPid == 0 || !CanonicalStart(parentStart)) throw new AgentOpsException("certificate_elevation_host_invalid");
        var owner = LocalIdentity.Current();
        var layout = CortexProductLayout.ResolveExecution();
        var host = HostPath(layout);
        if (!string.Equals(Environment.ProcessPath, host, StringComparison.OrdinalIgnoreCase) || layout.OwnerSid != owner.Sid)
            throw new AgentOpsException("certificate_elevation_host_invalid");
        CortexPrivilegedCodePath.RequireProtectedImage(layout.InstallRoot, host);
        if (HighIdentity(owner, owner, WindowsOwnerAdminLauncher.CaptureElevatedPeerIdentity) is null)
            throw new AgentOpsException("certificate_elevation_owner_denied");
        // These early helper modes bypass resident startup. Apply only the
        // current process preference after admission, without host/data startup.
        _ = CortexProcessorPolicy.ApplyForInstallRoot(layout.InstallRoot);
        var server = new LocalPeer(owner.Sid, owner.Session, host, parentPid);
        var expected = new MaintenanceHostIdentity(owner.Sid, owner.Session, parentPid, parentStart, host);
        return new(owner, new NamedPipeDispatcher(CortexProductIdentity.UserExecutionPipe(owner.Session), server, expected));
    }

    internal sealed class HelperContext(LocalPeer owner, NamedPipeDispatcher transport) : IAsyncDisposable
    {
        internal LocalPeer Owner { get; } = owner;
        internal NamedPipeDispatcher Transport { get; } = transport;
        public ValueTask DisposeAsync() => Transport.DisposeAsync();
    }

    internal static CertificateElevationProcessIdentity? HighIdentity(LocalPeer peer, LocalPeer owner,
        Func<LocalPeer, CertificateElevationProcessIdentity?> capture)
    {
        if (!peer.SameIdentity(owner) || !peer.Elevated || peer.Session <= 0 || peer.ProcessId is not > 0 ||
            peer.Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" ||
            !string.Equals(peer.ApplicationPath, owner.ApplicationPath, StringComparison.OrdinalIgnoreCase)) return null;
        var identity = capture(peer);
        return identity is not null && identity.Pid == peer.ProcessId && CanonicalStart(identity.Start) &&
            string.Equals(identity.ApplicationPath, owner.ApplicationPath, StringComparison.OrdinalIgnoreCase) ? identity : null;
    }
}

