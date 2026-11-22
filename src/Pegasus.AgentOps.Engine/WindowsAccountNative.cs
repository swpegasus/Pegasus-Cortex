using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

internal interface IWindowsAccountNativeCalls
{
    WindowsAccountNativeBuffer LocalRole();
    WindowsAccountNativeBuffer Enumerate(string view, string? group, uint preferredBytes, nuint resume);
    WindowsAccountNativeBuffer Sessions();
    WindowsAccountNativeBuffer SessionText(uint session, int informationClass);
    LocalPeer Current();
}

// The exact original allocation is owned even on native errors. Bytes is actual
// NetAPI/WTS-query size; WTS's session table exposes only its declared row extent.
internal sealed class WindowsAccountNativeBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<nint> _free;
    internal int Status { get; }
    internal uint Bytes { get; }
    internal uint Count { get; }
    internal nuint Resume { get; }
    internal int SizeStatus { get; }
    internal bool Success { get; }
    internal WindowsAccountNativeBuffer(nint pointer, Action<nint> free, int status = 0, uint bytes = 0,
        uint count = 0, nuint resume = 0, int sizeStatus = 0, bool? success = null) : base(true)
    {
        SetHandle(pointer); _free = free; Status = status; Bytes = bytes; Count = count;
        Resume = resume; SizeStatus = sizeStatus; Success = success ?? status is 0 or 234;
    }
    protected override bool ReleaseHandle()
    { try { _free(handle); return true; } catch { return false; } }
}

internal sealed class WindowsAccountNative : IWindowsAccountNative
{
    internal const uint PreferredPageBytes = WindowsAccountOperations.PreferredPageBytes;
    internal const int MaximumRows = WindowsAccountOperations.MaximumRows, MaximumPages = WindowsAccountOperations.MaximumNativePages;
    internal const int MaximumBufferBytes = 1024 * 1024;
    internal const int MaximumSessionQueries = 256, UserNameClass = 5, DomainNameClass = 7;
    private const int MoreData = 234;
    private readonly IWindowsAccountNativeCalls _calls;

    [SupportedOSPlatform("windows")]
    internal WindowsAccountNative() : this(new Calls()) { }
    internal WindowsAccountNative(IWindowsAccountNativeCalls calls) => _calls = calls ?? throw new ArgumentNullException(nameof(calls));

    public WindowsAccountCapture Capture(string view, string? group, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var items = new List<WindowsAccountItem>(); var issues = new List<WindowsAccountIssue>();
        try
        {
            if (view != "members" && group is not null || view == "members" && string.IsNullOrWhiteSpace(group))
                throw Fault(view, "account_invalid_native_data");
            switch (view)
            {
                case "users": case "groups": case "members": Sam(view, group, items, token); break;
                case "sessions": Sessions(items, issues, token); break;
                case "current":
                    var peer = _calls.Current(); token.ThrowIfCancellationRequested();
                    if (peer.Session < 0 || !WindowsAccountOperations.IsSid(peer.Sid)) throw Fault("current", "account_invalid_native_data");
                    items.Add(WindowsAccountOperations.Item("current_identity", new { peer.Sid, peer.Session }, new()
                        { ["sid"] = peer.Sid, ["sessionId"] = checked((uint)peer.Session), ["elevated"] = peer.Elevated }));
                    break;
                default: throw Fault("current", "account_invalid_native_data");
            }
            token.ThrowIfCancellationRequested();
            return new(true, "observed", items.AsReadOnly(), issues.Count > 0, WindowsAccountOperations.Coverage(view), issues.AsReadOnly());
        }
        catch (NativeFault failure)
        {
            return new(false, failure.Code, Array.Empty<WindowsAccountItem>(), false, WindowsAccountOperations.Coverage(view),
                Array.AsReadOnly(new[] { new WindowsAccountIssue(failure.Area, failure.Code, failure.NativeError, failure.Domain) }));
        }
        catch (Win32Exception failure)
        {
            return new(false, "account_unavailable", Array.Empty<WindowsAccountItem>(), false, WindowsAccountOperations.Coverage(view),
                Array.AsReadOnly(new[] { new WindowsAccountIssue(view, "account_unavailable", failure.NativeErrorCode, "win32") }));
        }
        catch (UnauthorizedAccessException)
        {
            return new(false, "account_unavailable", Array.Empty<WindowsAccountItem>(), false, WindowsAccountOperations.Coverage(view),
                Array.AsReadOnly(new[] { new WindowsAccountIssue(view, "account_unavailable") }));
        }
        catch (Exception failure) when (failure is ArgumentException or OverflowException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new(false, "account_invalid_native_data", Array.Empty<WindowsAccountItem>(), false, WindowsAccountOperations.Coverage(view),
                Array.AsReadOnly(new[] { new WindowsAccountIssue(view, "account_invalid_native_data") }));
        }
    }

    private void RequireLocalSam(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var role = _calls.LocalRole();
        token.ThrowIfCancellationRequested();
        if (!role.Success || role.Status != 0)
            throw Fault("local_role", "account_unproven_local_role", role.Status, "dsrole");
        if (role.IsInvalid || role.Bytes < sizeof(uint)) throw Fault("local_role", "account_unproven_local_role");
        // Only the role prefix is read. Domain names, forest and GUID are ignored.
        var value = unchecked((uint)Marshal.ReadInt32(role.DangerousGetHandle()));
        if (value is 4 or 5) throw Fault("local_role", "account_domain_controller_refused");
        if (value > 3) throw Fault("local_role", "account_unproven_local_role");
    }

    private void Sam(string view, string? group, List<WindowsAccountItem> items, CancellationToken token)
    {
        nuint resume = 0; var seen = new HashSet<nuint> { 0 };
        for (var page = 0; page < (view == "members" ? 1 : MaximumPages); page++)
        {
            // A NULL server on a DC addresses AD. Reprove the local SAM role
            // before every fixed local enumeration call; never fall back to AD.
            RequireLocalSam(token);
            using var buffer = _calls.Enumerate(view, group, view == "members" ? uint.MaxValue : PreferredPageBytes, resume);
            token.ThrowIfCancellationRequested();
            if (!buffer.Success || buffer.Status is not (0 or MoreData) || view == "members" && buffer.Status == MoreData)
                throw Fault(view, "account_unavailable", buffer.Status, "netapi32");
            if (buffer.SizeStatus != 0) throw Fault(view, "account_unavailable", buffer.SizeStatus, "netapi32");
            if (buffer.Bytes > MaximumBufferBytes || buffer.Count > MaximumRows - items.Count)
                throw Fault(view, "account_limit");
            var memory = new Memory(buffer.DangerousGetHandle(), buffer.Bytes, view);
            var stride = view == "users" ? Marshal.SizeOf<User20>() : IntPtr.Size;
            memory.RequireArray(buffer.Count, stride);
            for (var index = 0u; index < buffer.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var pointer = memory.Start + checked((int)index * stride);
                if (view == "users")
                {
                    var row = Marshal.PtrToStructure<User20>(pointer);
                    var name = memory.Unicode(row.Name, 256);
                    items.Add(WindowsAccountOperations.Item("user", new { name = name.ToUpperInvariant(), row.Rid }, new()
                        { ["name"] = name, ["rid"] = row.Rid, ["disabled"] = (row.Flags & 2) != 0 }));
                }
                else if (view == "groups")
                {
                    var name = memory.Unicode(Marshal.ReadIntPtr(pointer), 256);
                    items.Add(WindowsAccountOperations.Item("local_group", new { name = name.ToUpperInvariant() }, new() { ["name"] = name }));
                }
                else
                {
                    var sid = memory.Sid(Marshal.ReadIntPtr(pointer));
                    items.Add(WindowsAccountOperations.Item("group_member", new { group = group!.ToUpperInvariant(), sid },
                        new() { ["group"] = group, ["sid"] = sid }));
                }
            }
            if (buffer.Status == 0) return;
            if (buffer.Resume == resume || !seen.Add(buffer.Resume))
                throw Fault(view, "account_changed_during_read", MoreData, "netapi32");
            resume = buffer.Resume;
        }
        throw Fault(view, "account_limit");
    }

    private void Sessions(List<WindowsAccountItem> items, List<WindowsAccountIssue> issues, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var table = _calls.Sessions();
        token.ThrowIfCancellationRequested();
        if (!table.Success || table.Status != 0) throw Fault("sessions", "account_unavailable", table.Status, "win32");
        if (table.Count > MaximumRows) throw Fault("sessions", "account_limit");
        var stride = Marshal.SizeOf<WtsSession>();
        // WTS provides count, not allocation bytes. Admit the declared table
        // extent only; never follow its station-name pointers into unknown memory.
        var memory = new Memory(table.DangerousGetHandle(), checked(table.Count * (uint)stride), "sessions");
        memory.RequireArray(table.Count, stride);
        var queries = 0; var limited = false;
        for (var index = 0u; index < table.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = Marshal.PtrToStructure<WtsSession>(memory.Start + checked((int)index * stride));
            if (row.SessionId == 0 || row.State == 6) continue;
            var state = row.State switch
            {
                0 => "active", 1 => "connected", 2 => "connect_query", 3 => "shadow", 4 => "disconnected",
                5 => "idle", 7 => "reset", 8 => "down", 9 => "init", _ => throw Fault("sessions", "account_invalid_native_data")
            };
            var fields = new Dictionary<string, object?> { ["sessionId"] = row.SessionId, ["state"] = state };
            foreach (var (info, key, area) in new[] { (UserNameClass, "userName", "session_user"), (DomainNameClass, "domain", "session_domain") })
            {
                // Reserve one bounded issue slot for the omitted optional fields;
                // never drop an actual observed native error to make room for it.
                if (queries == MaximumSessionQueries || issues.Count >= WindowsAccountOperations.MaximumIssues - 1)
                { limited = true; continue; }
                queries++;
                try
                {
                    token.ThrowIfCancellationRequested();
                    using var text = _calls.SessionText(row.SessionId, info);
                    token.ThrowIfCancellationRequested();
                    if (!text.Success || text.Status != 0) throw Fault(area, "account_unavailable", text.Status, "win32");
                    if (text.Bytes > 514) throw Fault(area, "account_limit");
                    if (text.Bytes < 2 || (text.Bytes & 1) != 0) throw Fault(area, "account_invalid_native_data");
                    var value = new Memory(text.DangerousGetHandle(), text.Bytes, area).Unicode(text.DangerousGetHandle(), 256, allowEmpty: true);
                    if (value.Length > 0) fields[key] = value;
                }
                catch (NativeFault failure) { issues.Add(new(area, failure.Code, failure.NativeError, failure.Domain)); }
            }
            items.Add(WindowsAccountOperations.Item("windows_session", new { row.SessionId }, fields));
        }
        if (limited) issues.Add(new("sessions", "session_identity_query_limit"));
    }

    [StructLayout(LayoutKind.Sequential)] internal struct User20
    { internal nint Name, FullName, Comment; internal uint Flags, Rid; }
    [StructLayout(LayoutKind.Sequential)] internal struct WtsSession
    { internal uint SessionId; internal nint StationName; internal int State; }

    private readonly struct Memory(nint start, uint length, string area)
    {
        internal nint Start => start;
        private void Require(nint pointer, uint size)
        {
            var offset = unchecked((nuint)pointer - (nuint)start);
            if (pointer == 0 || offset > length || size > length - offset) throw Fault(area, "account_invalid_native_data");
        }
        internal void RequireArray(uint count, int stride)
        { if (count > 0) Require(start, checked(count * (uint)stride)); }
        internal string Unicode(nint pointer, int maximum, bool allowEmpty = false)
        {
            var text = new StringBuilder();
            for (var index = 0; index <= maximum; index++)
            {
                var at = pointer + index * 2; Require(at, 2);
                var value = (char)Marshal.ReadInt16(at);
                if (value == '\0')
                {
                    var result = text.ToString();
                    if (!allowEmpty && result.Length == 0) throw Fault(area, "account_invalid_native_data");
                    for (var i = 0; i < result.Length; i++)
                    {
                        if (char.IsHighSurrogate(result[i]))
                        { if (++i == result.Length || !char.IsLowSurrogate(result[i])) throw Fault(area, "account_invalid_native_data"); }
                        else if (char.IsLowSurrogate(result[i]) || char.IsControl(result[i])) throw Fault(area, "account_invalid_native_data");
                    }
                    return result;
                }
                if (index == maximum) throw Fault(area, "account_limit");
                text.Append(value);
            }
            throw Fault(area, "account_limit");
        }
        internal string Sid(nint pointer)
        {
            Require(pointer, 8);
            var count = Marshal.ReadByte(pointer, 1);
            if (Marshal.ReadByte(pointer) != 1 || count > 15) throw Fault("members", "account_invalid_native_data");
            var bytes = new byte[8 + count * 4]; Require(pointer, (uint)bytes.Length); Marshal.Copy(pointer, bytes, 0, bytes.Length);
            // Binary constructor and Value format locally; never translate to an
            // account name or query a domain controller.
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return new SecurityIdentifier(bytes, 0).Value;
        }
    }

    private sealed class NativeFault(string area, string code, int? nativeError, string? domain) : Exception(code)
    { internal string Area = area, Code = code; internal int? NativeError = nativeError; internal string? Domain = domain; }
    private static NativeFault Fault(string area, string code, int? nativeError = null, string? domain = null) => new(area, code, nativeError, domain);

    [SupportedOSPlatform("windows")]
    private sealed class Calls : IWindowsAccountNativeCalls
    {
        public LocalPeer Current() => LocalIdentity.Current();
        public WindowsAccountNativeBuffer LocalRole()
        {
            var status = unchecked((int)DsRoleGetPrimaryDomainInformation(null, 1, out var pointer));
            return new(pointer, DsRoleFreeMemory, status, bytes: sizeof(uint));
        }
        public WindowsAccountNativeBuffer Enumerate(string view, string? group, uint preferredBytes, nuint resume)
        {
            nint pointer; uint count, total; int status;
            if (view == "users")
            {
                var userResume = checked((uint)resume);
                status = unchecked((int)NetUserEnum(null, 20, 2, out pointer, preferredBytes, out count, out total, ref userResume));
                resume = userResume;
            }
            else if (view == "groups")
                status = unchecked((int)NetLocalGroupEnum(null, 0, out pointer, preferredBytes, out count, out total, ref resume));
            else if (view == "members")
                // One whole-result request, with no retained resume continuation.
                // Unexpected MORE_DATA is refused; no failure cleanup guarantee
                // beyond releasing the exact returned buffer is inferred.
                status = unchecked((int)NetLocalGroupGetMembers(null, group!, 0, out pointer, uint.MaxValue, out count, out total, 0));
            else throw new ArgumentException("account_view_invalid");
            try
            {
                uint bytes = 0;
                var sizeStatus = pointer != 0 && status is 0 or MoreData ? unchecked((int)NetApiBufferSize(pointer, out bytes)) : 0;
                return new(pointer, FreeNet, status, bytes, count, resume, sizeStatus);
            }
            catch { if (pointer != 0) FreeNet(pointer); throw; }
        }
        public WindowsAccountNativeBuffer Sessions()
        {
            var success = WTSEnumerateSessionsW(0, 0, 1, out var pointer, out var count);
            var status = success ? 0 : Marshal.GetLastWin32Error();
            return new(pointer, WTSFreeMemory, status, count: count, success: success);
        }
        public WindowsAccountNativeBuffer SessionText(uint session, int informationClass)
        {
            if (informationClass is not (UserNameClass or DomainNameClass)) throw new ArgumentException("account_information_class_invalid");
            var success = WTSQuerySessionInformationW(0, session, informationClass, out var pointer, out var bytes);
            var status = success ? 0 : Marshal.GetLastWin32Error();
            return new(pointer, WTSFreeMemory, status, bytes, success: success);
        }
        private static void FreeNet(nint pointer) => _ = NetApiBufferFree(pointer);

        [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint NetUserEnum(string? server, uint level, uint filter, out nint buffer, uint preferred,
            out uint entries, out uint total, ref uint resume);
        [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint NetLocalGroupEnum(string? server, uint level, out nint buffer, uint preferred,
            out uint entries, out uint total, ref nuint resume);
        [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint NetLocalGroupGetMembers(string? server, string group, uint level, out nint buffer,
            uint preferred, out uint entries, out uint total, nint resume);
        [DllImport("netapi32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint NetApiBufferSize(nint buffer, out uint bytes);
        [DllImport("netapi32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint NetApiBufferFree(nint buffer);
        [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint DsRoleGetPrimaryDomainInformation(string? server, int level, out nint buffer);
        [DllImport("netapi32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void DsRoleFreeMemory(nint buffer);
        [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSEnumerateSessionsW(nint server, uint reserved, uint version, out nint buffer, out uint count);
        [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformationW(nint server, uint session, int informationClass, out nint buffer, out uint bytes);
        [DllImport("wtsapi32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void WTSFreeMemory(nint buffer);
    }
}
