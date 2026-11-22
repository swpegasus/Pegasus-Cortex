using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

internal readonly record struct WindowsSmbLogonIdentity(uint Low, int High);
internal sealed record SmbDriveObservation(bool Occupied, bool Remembered = false, string? RemoteShare = null, int Error = 0);
internal sealed record SmbConnectionResult(bool Ok, string State, string? Error = null, bool ActionIssued = false,
    bool OutcomeUnknown = false, bool MappingObserved = false, int? NativeError = null);

internal interface IWindowsSmbNative
{
    SmbDriveObservation InspectDrive(string localDrive);
    int Connect(SmbConnectionProfile profile, ReadOnlySpan<char> password, Action beforeEntry);
    int Disconnect(string localDrive, Action beforeEntry);
}

/// <summary>
/// Ordinary-owner/logon-scoped worker, not a scheduler or an authentication grant.
/// Invoke inside the Engine's admitted mutation worker. These synchronous native
/// calls cannot be aborted safely: retain the worker/lifetime lease until return.
/// Caller cancellation before entry prevents dispatch; after entry the actual
/// terminal observation wins, with no retry or compensating disconnect.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSmbConnections
{
    private readonly OwnerHostIdentity _owner;
    private readonly Func<WindowsSmbLogonIdentity> _sample;
    private readonly WindowsSmbLogonIdentity _logon;
    private readonly IOwnerSmbPasswordResolver _passwords;
    private readonly IWindowsSmbNative _native;
    private readonly object _gate = new();

    internal WindowsSmbConnections(string ownerSid)
        : this(ownerSid, LocalIdentity.Current, NativeWindowsSmb.SampleLogon,
            new OwnerSmbPasswordReferences(ownerSid), new NativeWindowsSmb()) { }
    internal WindowsSmbConnections(string ownerSid, Func<LocalPeer> current, Func<WindowsSmbLogonIdentity> logon,
        IOwnerSmbPasswordResolver passwords, IWindowsSmbNative native)
    {
        _owner = new(ownerSid, current);
        _sample = logon ?? throw new ArgumentNullException(nameof(logon));
        _passwords = passwords ?? throw new ArgumentNullException(nameof(passwords));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        if (!_owner.IsCurrentOwner()) throw new SmbConnectionException("smb_owner_denied");
        _logon = _sample();
        if (_logon == default) throw new SmbConnectionException("smb_owner_denied");
        RequireOwner();
    }

    internal SmbConnectionResult Inspect(SmbConnectionProfile profile, CancellationToken token = default)
    {
        lock (_gate)
        {
            try
            {
                SmbConnectionProfile.Validate(profile); RequireOwner(); token.ThrowIfCancellationRequested();
                var found = Observe(profile.LocalDrive);
                token.ThrowIfCancellationRequested();
                return found.Error != 0 ? new(false, "unavailable", "smb_observation_unavailable", NativeError: found.Error) :
                    new(true, found.Remembered ? "remembered" : !found.Occupied ? "free" :
                        Matches(found, profile.RemoteShare) ? "mapped" : "occupied", MappingObserved: Matches(found, profile.RemoteShare));
            }
            catch (Exception error) when (Recoverable(error)) { return Failure(error, false); }
        }
    }

    internal SmbConnectionResult Connect(SmbConnectionProfile profile, CancellationToken token = default)
    {
        lock (_gate)
        {
            var issued = false;
            try
            {
                SmbConnectionProfile.Validate(profile); RequireOwner(); token.ThrowIfCancellationRequested();
                var before = Observe(profile.LocalDrive);
                if (before.Error != 0) return new(false, "unavailable", "smb_observation_unavailable", NativeError: before.Error);
                // No adoption, even of a matching mapping; no credential resolution
                // until this exact local device is absent and not remembered.
                if (before.Occupied || before.Remembered) return new(false, "refused", "smb_drive_occupied");
                using var password = profile.IdentityMode == "explicitRemote"
                    ? _passwords.Resolve(profile.PasswordReference!) : null;
                if (profile.IdentityMode == "explicitRemote" && (password is null || password.Value.Length is < 1 or > 1024))
                    throw new SmbConnectionException("smb_password_unavailable");
                RequireOwner(); token.ThrowIfCancellationRequested();
                var code = _native.Connect(profile, password is null ? ReadOnlySpan<char>.Empty : password.Value,
                    () => { RequireOwner(); token.ThrowIfCancellationRequested(); issued = true; });
                if (!issued) throw new SmbConnectionException("smb_action_unobserved");
                var after = Observe(profile.LocalDrive); // Intentionally no late token check.
                if (after.Error != 0) return new(false, "unknown", "smb_observation_unavailable", true, true, NativeError: code != 0 ? code : after.Error);
                if (code == 0 && !after.Remembered && Matches(after, profile.RemoteShare))
                    return new(true, "connected", ActionIssued: true, MappingObserved: true);
                if (code != 0 && !after.Occupied && !after.Remembered)
                    return new(false, "failed", "smb_connect_failed", true, NativeError: code);
                return new(false, "unknown", "smb_action_unobserved", true, true, Matches(after, profile.RemoteShare), code == 0 ? null : code);
            }
            catch (Exception error) when (Recoverable(error)) { return Failure(error, issued); }
        }
    }

    internal SmbConnectionResult Disconnect(SmbConnectionProfile profile, string expectedRemoteShare, CancellationToken token = default)
    {
        lock (_gate)
        {
            var issued = false;
            try
            {
                SmbConnectionProfile.Validate(profile); SmbConnectionProfile.RequireRemoteShare(expectedRemoteShare);
                if (!StringComparer.OrdinalIgnoreCase.Equals(expectedRemoteShare, profile.RemoteShare))
                    throw new SmbConnectionException("smb_mapping_changed");
                RequireOwner(); token.ThrowIfCancellationRequested();
                var before = Observe(profile.LocalDrive);
                if (before.Error != 0) return new(false, "unavailable", "smb_observation_unavailable", NativeError: before.Error);
                if (before.Remembered) return new(false, "refused", "smb_drive_occupied");
                if (!Matches(before, expectedRemoteShare)) return new(false, "refused", "smb_mapping_changed");
                var code = _native.Disconnect(profile.LocalDrive,
                    () => { RequireOwner(); token.ThrowIfCancellationRequested(); issued = true; });
                if (!issued) throw new SmbConnectionException("smb_action_unobserved");
                var after = Observe(profile.LocalDrive);
                if (after.Error != 0) return new(false, "unknown", "smb_observation_unavailable", true, true, NativeError: code != 0 ? code : after.Error);
                if (code == 0 && !after.Occupied && !after.Remembered) return new(true, "disconnected", ActionIssued: true);
                if (code != 0 && !after.Remembered && Matches(after, expectedRemoteShare))
                    return new(false, "failed", "smb_disconnect_failed", true, MappingObserved: true, NativeError: code);
                return new(false, "unknown", "smb_action_unobserved", true, true, Matches(after, expectedRemoteShare), code == 0 ? null : code);
            }
            catch (Exception error) when (Recoverable(error)) { return Failure(error, issued); }
        }
    }

    private SmbDriveObservation Observe(string drive)
    {
        RequireOwner();
        var value = _native.InspectDrive(drive);
        RequireOwner();
        if (value.RemoteShare is not null) SmbConnectionProfile.RequireRemoteShare(value.RemoteShare);
        if (value.RemoteShare is not null && !value.Occupied) throw new SmbConnectionException("smb_observation_unavailable");
        return value;
    }
    private void RequireOwner()
    {
        if (!_owner.IsCurrentOwner() || _sample() != _logon) throw new SmbConnectionException("smb_owner_denied");
    }
    private static bool Matches(SmbDriveObservation value, string remote) =>
        value.Occupied && value.RemoteShare is not null && StringComparer.OrdinalIgnoreCase.Equals(value.RemoteShare, remote);
    private static bool Recoverable(Exception value) => value is not OutOfMemoryException and not StackOverflowException;
    private static SmbConnectionResult Failure(Exception error, bool issued)
    {
        var code = error is OperationCanceledException ? "smb_cancelled" : error is SmbConnectionException smb ? smb.Code : "smb_unavailable";
        if (code is not ("smb_profile_invalid" or "smb_owner_denied" or "smb_password_not_found" or "smb_password_reference_invalid" or
            "smb_password_unavailable" or "smb_action_unobserved" or "smb_mapping_changed" or "smb_observation_unavailable" or "smb_cancelled"))
            code = "smb_unavailable";
        return new(false, issued ? "unknown" : code == "smb_cancelled" ? "cancelled" : "failed", code, issued, issued);
    }
}

// Fixed LANMAN provider and temporary drive mapping only. Never prompt, save
// credentials, update a remembered profile, force-close files, or use remote-name
// cancellation (which would affect unrelated deviceless connections).
[SupportedOSPlatform("windows")]
internal sealed class NativeWindowsSmb : IWindowsSmbNative
{
    internal const uint TemporaryConnection = 4;
    private const int NotConnected = 2250, ConnectionUnavailable = 1201, MoreData = 234, NoMoreItems = 259;

    public SmbDriveObservation InspectDrive(string localDrive)
    {
        RequireDrive(localDrive);
        var remote = new char[512];
        uint length = checked((uint)remote.Length);
        var code = WNetGetConnectionW(localDrive, remote, ref length);
        if (code is not (0 or NotConnected or ConnectionUnavailable)) return new(false, Error: code);
        var remembered = IsRemembered(localDrive, out var enumerationError);
        if (enumerationError != 0) return new(false, Error: enumerationError);
        if (code == ConnectionUnavailable) return new(true, true);
        if (code == 0)
        {
            var count = Array.IndexOf(remote, '\0');
            if (count is < 1 or > 240) return new(false, Error: 87);
            return new(true, remembered, new string(remote, 0, count));
        }
        var device = new char[1024];
        var present = QueryDosDeviceW(localDrive, device, checked((uint)device.Length));
        if (present != 0) return new(true, remembered);
        var error = Marshal.GetLastWin32Error();
        return error == 2 ? new(remembered, remembered) : new(false, Error: error);
    }

    public int Connect(SmbConnectionProfile profile, ReadOnlySpan<char> password, Action beforeEntry)
    {
        SmbConnectionProfile.Validate(profile);
        ArgumentNullException.ThrowIfNull(beforeEntry);
        var explicitRemote = profile.IdentityMode == "explicitRemote";
        if (explicitRemote ? password.Length is < 1 or > 1024 || password.Contains('\0') : !password.IsEmpty)
            throw new SmbConnectionException("smb_password_unavailable");
        var provider = new char[256];
        uint size = checked((uint)provider.Length);
        var code = WNetGetProviderNameW(0x00020000u, provider, ref size); // WNNC_NET_LANMAN.
        if (code != 0) return code;
        var count = Array.IndexOf(provider, '\0');
        if (count is < 1 or >= 256) throw new SmbConnectionException("smb_unavailable");
        var resource = new NetResource { Type = 1, LocalName = profile.LocalDrive, RemoteName = profile.RemoteShare,
            Provider = new string(provider, 0, count) };
        nint buffer = 0;
        try
        {
            if (explicitRemote)
            {
                buffer = Marshal.AllocHGlobal(checked((password.Length + 1) * sizeof(char)));
                for (var index = 0; index < password.Length; index++) Marshal.WriteInt16(buffer, index * 2, unchecked((short)password[index]));
                Marshal.WriteInt16(buffer, password.Length * 2, 0);
            }
            beforeEntry();
            // NULL passwords only for admitted currentUser; explicitRemote must
            // always supply the nonempty resolved buffer, never default fallback.
            return WNetAddConnection2W(ref resource, buffer, explicitRemote ? profile.RemoteUser : null, TemporaryConnection);
        }
        finally
        {
            if (buffer != 0)
            {
                for (var index = 0; index <= password.Length; index++) Marshal.WriteInt16(buffer, index * 2, 0);
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public int Disconnect(string localDrive, Action beforeEntry)
    {
        RequireDrive(localDrive); ArgumentNullException.ThrowIfNull(beforeEntry);
        beforeEntry();
        return WNetCancelConnection2W(localDrive, 0, false);
    }

    private static bool IsRemembered(string drive, out int error)
    {
        error = WNetOpenEnumW(3, 1, 0, 0, out var enumeration); // RESOURCE_REMEMBERED, DISK.
        if (error != 0) return false;
        nint buffer = 0;
        var remembered = false;
        try
        {
            buffer = Marshal.AllocHGlobal(64 * 1024);
            for (var page = 0; page < 16; page++)
            {
                uint count = uint.MaxValue, bytes = 64 * 1024;
                error = WNetEnumResourceW(enumeration, ref count, buffer, ref bytes);
                if (error == NoMoreItems) { error = 0; return remembered; }
                if (error != 0 || count > 512 || bytes > 64 * 1024) { if (error == 0) error = MoreData; return false; }
                var stride = Marshal.SizeOf<NativeResource>();
                if (count == 0 || (ulong)count * (uint)stride > bytes) { error = 87; return false; }
                for (var index = 0; index < count; index++)
                {
                    var resource = Marshal.PtrToStructure<NativeResource>(buffer + checked((int)index * stride));
                    if (resource.LocalName == 0) continue;
                    var offset = resource.LocalName.ToInt64() - buffer.ToInt64();
                    if (offset < 0 || offset > bytes - 6) { error = 87; return false; }
                    if (Marshal.ReadInt16(resource.LocalName) == drive[0] && Marshal.ReadInt16(resource.LocalName, 2) == ':' &&
                        Marshal.ReadInt16(resource.LocalName, 4) == 0) remembered = true;
                }
            }
            error = MoreData; return false;
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            var closed = WNetCloseEnum(enumeration);
            if (closed != 0 && error == 0) error = closed;
        }
    }

    internal static WindowsSmbLogonIdentity SampleLogon()
    {
        // No thread impersonation and no alternate token. Check the effective
        // token against the actual process primary token, then pin its logon LUID.
        using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
        if (impersonated is not null) throw new SmbConnectionException("smb_owner_denied");
        using var effective = WindowsIdentity.GetCurrent();
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var process)) throw new SmbConnectionException("smb_owner_denied");
        using (process)
        {
            var size = checked((uint)Marshal.SizeOf<TokenStatistics>());
            if (!GetTokenInformation(process, 10, out var primary, size, out var length) || length != size ||
                !GetTokenInformation(effective.AccessToken, 10, out var current, size, out length) || length != size ||
                primary.TokenType != 1 || current.TokenType != 1 || primary.TokenId != current.TokenId ||
                primary.AuthenticationId != current.AuthenticationId) throw new SmbConnectionException("smb_owner_denied");
            return new(primary.AuthenticationId.Low, primary.AuthenticationId.High);
        }
    }

    private static void RequireDrive(string value)
    { if (value is not { Length: 2 } || value[0] is < 'A' or > 'Z' || value[1] != ':') throw new SmbConnectionException("smb_profile_invalid"); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Luid(uint Low, int High);
    [StructLayout(LayoutKind.Sequential)] private struct TokenStatistics
    { internal Luid TokenId, AuthenticationId; internal long Expiration; internal int TokenType, Impersonation; internal uint Charged, Available, Groups, Privileges; internal Luid Modified; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NetResource
    { internal uint Scope, Type, DisplayType, Usage; internal string? LocalName, RemoteName, Comment, Provider; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeResource
    { internal uint Scope, Type, DisplayType, Usage; internal nint LocalName, RemoteName, Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetAddConnection2W(ref NetResource resource, nint password, string? userName, uint flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetCancelConnection2W(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetGetConnectionW(string localName, [Out] char[] remoteName, ref uint length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetGetProviderNameW(uint type, [Out] char[] name, ref uint length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetOpenEnumW(uint scope, uint type, uint usage, nint resource, out nint enumeration);
    [DllImport("mpr.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetEnumResourceW(nint enumeration, ref uint count, nint buffer, ref uint size);
    [DllImport("mpr.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetCloseEnum(nint enumeration);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint capacity);
    [DllImport("kernel32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out TokenStatistics value, uint size, out uint needed);
}
