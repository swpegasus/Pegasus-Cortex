using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Pegasus.AgentOps.Engine;

// Own synthetic allocations and injected calls only. No local account/session
// enumeration, role query, name translation, token read, UAC or configuration.
[SupportedOSPlatform("windows")]
internal static class WindowsAccountNativeTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private static int _checks;

    internal static Task RunAsync()
    {
        _checks = 0;
        AbiAndRole(); SamPagesAndFields(); Membership(); Sessions(); CurrentAndCancellation();
        Console.WriteLine($"PASS Windows account native buffers: {_checks} injected ABI/allocation/free/role/page/field/error/cancellation checks; no native inventory or account/session changes");
        return Task.CompletedTask;
    }

    private static void AbiAndRole()
    {
        Check(Marshal.SizeOf<WindowsAccountNative.User20>() == (IntPtr.Size == 8 ? 32 : 20) &&
            Marshal.OffsetOf<WindowsAccountNative.User20>(nameof(WindowsAccountNative.User20.Flags)).ToInt32() == 3 * IntPtr.Size &&
            Marshal.OffsetOf<WindowsAccountNative.User20>(nameof(WindowsAccountNative.User20.Rid)).ToInt32() == 3 * IntPtr.Size + 4,
            "USER_INFO_20 ABI");
        Check(Marshal.SizeOf<WindowsAccountNative.WtsSession>() == (IntPtr.Size == 8 ? 24 : 12) &&
            Marshal.OffsetOf<WindowsAccountNative.WtsSession>(nameof(WindowsAccountNative.WtsSession.State)).ToInt32() == (IntPtr.Size == 8 ? 16 : 8),
            "WTS_SESSION_INFO ABI");
        var callsType = typeof(WindowsAccountNative).GetNestedType("Calls", BindingFlags.NonPublic)!;
        MethodInfo Method(string name) => callsType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(Method("NetUserEnum").GetParameters()[^1].ParameterType == typeof(uint).MakeByRefType() &&
            Method("NetLocalGroupEnum").GetParameters()[^1].ParameterType == typeof(nuint).MakeByRefType() &&
            Method("NetLocalGroupGetMembers").GetParameters()[^1].ParameterType == typeof(nint), "NetAPI resume widths/null-pointer ABI");
        foreach (var method in callsType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null))
            Check(method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths == DllImportSearchPath.System32,
                "native DLL search is not fixed to System32");
        var untouched = new Calls(); _ = new WindowsAccountNative(untouched);
        Check(untouched.TotalCalls == 0, "constructor invoked native calls");
        foreach (var view in new[] { "users", "groups", "members" })
        foreach (var role in new uint[] { 4, 5, 6, uint.MaxValue })
        {
            var calls = new Calls { Role = role };
            var result = Capture(calls, view);
            Check(!result.Ok && result.Code == (role is 4 or 5 ? "account_domain_controller_refused" : "account_unproven_local_role") &&
                calls.Enumerations == 0 && result.Items.Count == 0, "unproven/DC role reached SAM enumeration");
            Freed(calls);
        }
        foreach (var role in new uint[] { 0, 1, 2, 3 })
        {
            var calls = new Calls { Role = role };
            Check(Capture(calls, "users").Ok && calls.Enumerations == 1, "proven local SAM role was refused");
            Freed(calls);
        }
        var denied = new Calls { RoleError = 5 };
        var refusal = Capture(denied, "groups");
        Check(!refusal.Ok && refusal.Issues.Single() is { Area: "local_role", NativeError: 5, NativeDomain: "dsrole" } &&
            denied.Enumerations == 0, "role error/status was lost or fell back to SAM");
        Freed(denied);
        var missing = new Calls { NullRole = true };
        Check(Capture(missing, "members").Code == "account_unproven_local_role" && missing.Enumerations == 0,
            "missing role proof reached SAM");
        Freed(missing);
    }

    private static void SamPagesAndFields()
    {
        foreach (var view in new[] { "users", "groups" })
        {
            var calls = new Calls { Mode = "two-pages" };
            var result = Capture(calls, view);
            Check(result.Ok && !result.Partial && result.Items.Count == 2 && calls.Enumerations == 2 && calls.Roles == 2 &&
                calls.Requests.All(request => request.Preferred == 8192 && request.View == view && request.Group is null) &&
                calls.Requests.Select(request => request.Resume).SequenceEqual(new nuint[] { 0, 7 }), "fixed bounded page/resume/role flow");
            Check(!result.Coverage.NativePreallocationCapped && !result.Coverage.Snapshot && result.Coverage.PreferredNativePageBytes == 8192,
                "preferred bytes were called an allocation cap/snapshot");
            var fields = result.Items[0].Fields;
            Check(result.Items.All(item => item.Kind == (view == "users" ? "user" : "local_group")) &&
                fields["name"] is "fixture-1" && fields.Count == (view == "users" ? 3 : 1), "account metadata whitelist");
            if (view == "users") Check(fields["rid"] is 1001u && fields["disabled"] is true,
                "RID/disabled ABI or ignored private pointers changed");
            Freed(calls);
        }
        foreach (var mode in new[] { "native-error", "size-error", "oversize", "too-many", "short", "outside", "null-name", "long", "bad-utf16", "null-array" })
        {
            var calls = new Calls { Mode = mode };
            var result = Capture(calls, "users");
            Check(!result.Ok && result.Items.Count == 0 && calls.Enumerations == 1, "invalid/oversize native buffer became complete account data");
            if (mode == "native-error") Check(result.Issues.Single() is { NativeError: 5, NativeDomain: "netapi32" }, "NetAPI native error domain");
            if (mode == "size-error") Check(result.Issues.Single() is { NativeError: 487, NativeDomain: "netapi32" }, "NetAPI buffer-size error domain");
            Freed(calls);
        }
        foreach (var (mode, expected, count) in new[]
        {
            ("stalled", "account_changed_during_read", 1), ("cycle", "account_changed_during_read", 3),
            ("page-limit", "account_limit", 256), ("total-limit", "account_limit", 2)
        })
        {
            var calls = new Calls { Mode = mode };
            var result = Capture(calls, "groups");
            Check(!result.Ok && result.Code == expected && result.Items.Count == 0 && calls.Enumerations == count,
                "enumeration stalled/looped/omitted a page tail or escaped bounds");
            Freed(calls);
        }
        var empty = new Calls { Mode = "empty" };
        Check(Capture(empty, "users") is { Ok: true, Partial: false, Items.Count: 0 }, "native empty result was changed");
        Freed(empty);
    }

    private static void Membership()
    {
        var calls = new Calls();
        var result = Capture(calls, "members");
        Check(result.Ok && result.Items.Single().Kind == "group_member" && result.Items[0].Fields.Count == 2 &&
            result.Items[0].Fields["group"] is "fixture-group" && result.Items[0].Fields["sid"] is "S-1-5-32-544",
            "binary SID/group projection or local SID formatting failed");
        Check(calls.Enumerations == 1 && calls.Requests.Single() is { Preferred: uint.MaxValue, Resume: 0 } &&
            result.Coverage.PreferredNativePageBytes is null && !result.Coverage.NativePreallocationCapped &&
            result.Coverage.Limitations.Contains("windows_allocates_entire_membership_before_count_admission"), "membership whole-result allocation contract");
        Freed(calls);
        foreach (var mode in new[] { "member-more", "sid-count", "sid-revision", "sid-range", "oversize", "too-many" })
        {
            var invalid = new Calls { Mode = mode };
            var failed = Capture(invalid, "members");
            Check(!failed.Ok && failed.Items.Count == 0 && invalid.Enumerations == 1, "membership retry/fallback or malformed SID admitted");
            if (mode == "member-more") Check(failed.Issues.Single() is { NativeError: 234, NativeDomain: "netapi32" }, "unexpected membership continuation was concealed");
            Freed(invalid);
        }
    }

    private static void Sessions()
    {
        var calls = new Calls(); var result = Capture(calls, "sessions");
        Check(result.Ok && !result.Partial && result.Items.Count == 2 && calls.Queries == 4 && calls.Roles == 0 && calls.Enumerations == 0,
            "interactive WTS rows/filtered system-listener rows or query count changed");
        Check(result.Items.All(item => item.Kind == "windows_session" && item.Fields.Count == 4 && !item.Fields.ContainsKey("stationName")) &&
            result.Items[0].Fields["sessionId"] is 1u && result.Items[0].Fields["state"] is "active" &&
            result.Items[0].Fields["userName"] is "fixture-user" && result.Items[0].Fields["domain"] is "FIXTURE",
            "WTS ABI/bounded name projection followed station pointer or exposed extra fields");
        Check(!result.Coverage.NativePreallocationCapped && result.Coverage.PreferredNativePageBytes is null,
            "WTS whole allocation was called prebounded");
        Freed(calls);
        foreach (var mode in new[] { "wts-error", "wts-count", "wts-state" })
        {
            var invalid = new Calls { Mode = mode }; var failed = Capture(invalid, "sessions");
            Check(!failed.Ok && failed.Items.Count == 0 && invalid.Queries == 0, "invalid/unavailable WTS table became complete sessions");
            if (mode == "wts-error") Check(failed.Issues.Single() is { NativeError: 1722, NativeDomain: "win32" }, "WTS native status/domain was lost");
            Freed(invalid);
        }
        foreach (var mode in new[] { "query-denied", "query-large", "query-short", "query-odd", "query-nul", "query-utf16", "query-null" })
        {
            var partialCalls = new Calls { Mode = mode }; var partial = Capture(partialCalls, "sessions");
            Check(partial.Ok && partial.Partial && partial.Items.Count == 2 && partial.Issues.Count == 4 &&
                partial.Items.All(item => item.Fields.Count == 2), "optional WTS failure invented names/false completeness or omitted native rows");
            if (mode == "query-denied") Check(partial.Issues.All(issue => issue.NativeError == 5 && issue.NativeDomain == "win32"), "per-query denied status was discarded");
            Freed(partialCalls);
        }
        foreach (var denied in new[] { false, true })
        {
            var bounded = new Calls { SessionRows = 130, Mode = denied ? "query-denied" : "normal" };
            var partial = Capture(bounded, "sessions");
            Check(partial.Ok && partial.Partial && partial.Items.Count == 130 && bounded.Queries <= 256 &&
                partial.Issues.Count <= WindowsAccountOperations.MaximumIssues && partial.Issues[^1].Code == "session_identity_query_limit",
                "optional identity lookup/issue budgets dropped session tails or grew unbounded");
            Check(partial.Items[^1].Fields.Count == 2 && partial.Issues.Count == (denied ? bounded.Queries + 1 : 1),
                "query budget invented a signed-in name or discarded an observed failure");
            Freed(bounded);
        }
    }

    private static void CurrentAndCancellation()
    {
        var calls = new Calls(); var result = Capture(calls, "current");
        Check(result.Ok && result.Items.Single().Kind == "current_identity" && result.Items[0].Fields.Count == 3 &&
            result.Items[0].Fields["sid"] is Sid && result.Items[0].Fields["sessionId"] is 7u && result.Items[0].Fields["elevated"] is false &&
            calls.TotalCalls == 1 && calls.Currents == 1, "current identity invoked other inventory/projected token or image fields");
        var denied = new Calls { CurrentFailure = new Win32Exception(5) }; var failed = Capture(denied, "current");
        Check(!failed.Ok && failed.Issues.Single() is { NativeError: 5, NativeDomain: "win32" }, "current identity failure became an empty success");
        var invalid = new Calls { Peer = new LocalPeer("invalid", 7) };
        Check(Capture(invalid, "current").Code == "account_invalid_native_data", "invalid current SID was projected");
        using var before = new CancellationTokenSource(); before.Cancel();
        var untouched = new Calls(); Canceled(() => new WindowsAccountNative(untouched).Capture("users", null, before.Token));
        Check(untouched.TotalCalls == 0, "pre-canceled capture called native API");
        foreach (var phase in new[] { "role", "enum", "wts", "query" })
        {
            using var cancellation = new CancellationTokenSource();
            var cancelCalls = new Calls { AfterAllocate = actual => { if (actual == phase) cancellation.Cancel(); } };
            Canceled(() => new WindowsAccountNative(cancelCalls).Capture(phase is "role" or "enum" ? "users" : "sessions", null, cancellation.Token));
            Freed(cancelCalls);
            Check(phase != "role" || cancelCalls.Enumerations == 0, "post-role cancellation entered SAM");
            Check(phase != "wts" || cancelCalls.Queries == 0, "post-table cancellation queried session identity");
        }
    }

    private static WindowsAccountCapture Capture(Calls calls, string view) =>
        new WindowsAccountNative(calls).Capture(view, view == "members" ? "fixture-group" : null, default);
    private static void Freed(Calls calls) => Check(calls.Allocated == calls.Frees, "an original native allocation was not freed exactly once");
    private static void Canceled(Action action)
    { try { action(); } catch (OperationCanceledException) { _checks++; return; } throw new InvalidOperationException("native capture cancellation was not preserved"); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }

    private sealed record Request(string View, string? Group, uint Preferred, nuint Resume);
    private sealed class Calls : IWindowsAccountNativeCalls
    {
        internal string Mode = "normal";
        internal uint Role = 1;
        internal int RoleError, Roles, Enumerations, Tables, Queries, Currents, Allocated, Frees;
        internal int SessionRows = 2;
        internal bool NullRole;
        internal Exception? CurrentFailure;
        internal LocalPeer Peer = new(Sid, 7, @"C:\fixture\not-projected.exe", 123, false);
        internal Action<string>? AfterAllocate;
        internal readonly List<Request> Requests = new();
        internal int TotalCalls => Roles + Enumerations + Tables + Queries + Currents;

        public LocalPeer Current()
        { Currents++; if (CurrentFailure is not null) throw CurrentFailure; return Peer; }
        public WindowsAccountNativeBuffer LocalRole()
        {
            Roles++;
            if (NullRole) return new(0, _ => throw new InvalidOperationException("null-free"), bytes: 4);
            return Allocate("role", 4, pointer => Marshal.WriteInt32(pointer, unchecked((int)Role)), status: RoleError);
        }
        public WindowsAccountNativeBuffer Enumerate(string view, string? group, uint preferredBytes, nuint resume)
        {
            Enumerations++; Requests.Add(new(view, group, preferredBytes, resume));
            var count = Mode switch { "empty" => 0u, "too-many" => 4097u, "total-limit" when Enumerations == 1 => 4096u, _ => 1u };
            var more = Mode is "stalled" or "cycle" or "page-limit" or "member-more" || Mode is "two-pages" or "total-limit" && Enumerations == 1;
            var next = Mode == "stalled" ? 0u : Mode == "cycle" ? Enumerations == 2 ? 9u : 7u : Mode == "page-limit" ? (uint)Enumerations : 7u;
            if (Mode is "empty" or "null-array") return new(0, _ => throw new InvalidOperationException("null-free"), count: count);
            var stride = view == "users" ? Marshal.SizeOf<WindowsAccountNative.User20>() : IntPtr.Size;
            var actualCount = count > 4096 ? 0 : checked((int)count);
            var bytes = Math.Max(1024, actualCount * stride + 1024);
            return Allocate("enum", bytes, pointer =>
            {
                for (var index = 0; index < actualCount; index++)
                {
                    var textAt = pointer + actualCount * stride + 64;
                    var name = "fixture-" + Enumerations;
                    Copy(textAt, Encoding.Unicode.GetBytes(name + "\0"));
                    if (Mode == "outside") textAt = pointer + bytes + 16;
                    if (Mode == "null-name") textAt = 0;
                    if (Mode == "long") Copy(textAt, Encoding.Unicode.GetBytes(new string('x', 257) + "\0"));
                    if (Mode == "bad-utf16") { Marshal.WriteInt16(textAt, unchecked((short)0xD800)); Marshal.WriteInt16(textAt + 2, 0); }
                    if (view == "users")
                        Marshal.StructureToPtr(new WindowsAccountNative.User20
                            { Name = textAt, FullName = -1, Comment = -1, Flags = 2, Rid = (uint)(1000 + Enumerations + index) }, pointer + index * stride, false);
                    else if (view == "groups") Marshal.WriteIntPtr(pointer + index * stride, textAt);
                    else
                    {
                        var sidAt = pointer + actualCount * stride + 64;
                        Copy(sidAt, new byte[] { 1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 32, 2, 0, 0 });
                        if (Mode == "sid-count") Marshal.WriteByte(sidAt + 1, 16);
                        if (Mode == "sid-revision") Marshal.WriteByte(sidAt, 2);
                        if (Mode == "sid-range") { sidAt = pointer + bytes - 8; Marshal.WriteByte(sidAt, 1); Marshal.WriteByte(sidAt + 1, 15); }
                        Marshal.WriteIntPtr(pointer + index * stride, sidAt);
                    }
                }
            }, status: Mode == "native-error" ? 5 : more ? 234 : 0, count: count, resume: next,
                reportedBytes: Mode == "oversize" ? 1024u * 1024 + 1 : Mode == "short" ? (uint)stride - 1 : null,
                sizeStatus: Mode == "size-error" ? 487 : 0);
        }
        public WindowsAccountNativeBuffer Sessions()
        {
            Tables++;
            var stride = Marshal.SizeOf<WindowsAccountNative.WtsSession>();
            var count = Mode == "wts-count" ? 4097 : SessionRows + 2;
            return Allocate("wts", count > 4096 ? 4 : count * stride, pointer =>
            {
                if (count > 4096) return;
                for (var index = 0; index < count; index++)
                    Marshal.StructureToPtr(new WindowsAccountNative.WtsSession
                    {
                        SessionId = index == 0 ? 0u : index == 1 ? 999u : (uint)index - 1,
                        StationName = -1,
                        State = index == 1 ? 6 : Mode == "wts-state" && index == 2 ? 99 : index % 2 == 0 ? 0 : 4
                    }, pointer + index * stride, false);
            }, status: Mode == "wts-error" ? 1722 : 0, count: (uint)count);
        }
        public WindowsAccountNativeBuffer SessionText(uint session, int informationClass)
        {
            Queries++;
            Check(session > 0 && session != 999 && informationClass is 5 or 7, "WTS query reached a filtered session or unsupported class");
            if (Mode == "query-null") return new(0, _ => throw new InvalidOperationException("null-free"), bytes: 2);
            var data = Encoding.Unicode.GetBytes((informationClass == 5 ? "fixture-user" : "FIXTURE") + "\0");
            var bytes = Mode == "query-large" ? 600 : Mode == "query-nul" ? 512 : data.Length;
            return Allocate("query", bytes, pointer =>
            {
                if (Mode == "query-nul") Copy(pointer, Encoding.Unicode.GetBytes(new string('x', 256)));
                else if (Mode == "query-utf16") { Marshal.WriteInt16(pointer, unchecked((short)0xD800)); Marshal.WriteInt16(pointer + 2, 0); }
                else Copy(pointer, data);
            }, status: Mode == "query-denied" ? 5 : 0,
                reportedBytes: Mode == "query-short" ? 1u : Mode == "query-odd" ? 3u : null);
        }
        private WindowsAccountNativeBuffer Allocate(string area, int bytes, Action<nint> fill, int status = 0,
            uint count = 0, nuint resume = 0, uint? reportedBytes = null, int sizeStatus = 0)
        {
            var pointer = Marshal.AllocHGlobal(bytes); Allocated++;
            var buffer = new WindowsAccountNativeBuffer(pointer, address => { Frees++; Marshal.FreeHGlobal(address); }, status,
                reportedBytes ?? (uint)bytes, count, resume, sizeStatus);
            try { Marshal.Copy(new byte[bytes], 0, pointer, bytes); fill(pointer); AfterAllocate?.Invoke(area); return buffer; }
            catch { buffer.Dispose(); throw; }
        }
        private static void Copy(nint pointer, byte[] value) => Marshal.Copy(value, 0, pointer, value.Length);
    }
}
