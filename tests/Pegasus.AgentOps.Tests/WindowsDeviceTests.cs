using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Injected info-set/node boundary and owner identities. No device, registry,
// service, file, driver, process or credential provider is consulted by RunAsync.
internal static class WindowsDeviceTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    internal const string EngineFixtureInstanceId = @"ROOT\CORTEX_FIXTURE\0000";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static int _assertions;

    internal static WindowsDeviceOperations CreateEngineFixtureOperations(string ownerSid, Func<LocalPeer> current) =>
        new(ownerSid, current, new FakeNative { Count = 1, CustomFirstId = EngineFixtureInstanceId });

    internal static Task RunAsync()
    {
        _assertions = 0;
        Contract();
        MetadataAndExactRead();
        PagingAndBounds();
        MissingChangesAndErrors();
        OwnerCancellationAndCleanup();
        if (OperatingSystem.IsWindows()) NativeLayoutOnly();
        Console.WriteLine("PASS Windows devices: " + _assertions +
            " injected owner/SetupAPI-boundary checks; exact IDs, selected device/driver metadata, bounded live paging/bytes, typed partial failures and handle/buffer cleanup; no real inventory or device administration");
        return Task.CompletedTask;
    }

    private static void Contract()
    {
        Check(WindowsDeviceOperationContract.Actions.SequenceEqual(new[] { "list", "read" }), "closed read actions");
        var first = List();
        Check(first.Scope == "present" && first.Limit == 32 && !first.IsMutation && first.RequestIdentity.Length == 64 &&
            WindowsDeviceOperationContract.IsReadOnly(first.Action), "default read metadata");
        foreach (var extra in new[] { "computer", "credentials", "root", "provider", "classInstaller", "properties", "enable", "command", "filter" })
            Refuse(() => WindowsDeviceOperationContract.Parse("list", "{\"version\":1,\"" + extra + "\":null}"));
        foreach (var body in new[]
        {
            "{\"version\":1,\"version\":1}", "{\"version\":2}", "{\"version\":1,\"scope\":\"remote\"}",
            "{\"version\":1,\"scope\":null}", "{\"version\":1,\"limit\":0}", "{\"version\":1,\"limit\":65}",
            "{\"version\":1,\"afterInstanceId\":null}", "{\"version\":1,\"instanceId\":\"x\"}", new string(' ', 4097)
        }) Refuse(() => WindowsDeviceOperationContract.Parse("list", body));
        foreach (var id in new[] { "", " ", "x\0", "x\n", new string('x', 200) })
            Refuse(() => Read(id));
        Refuse(() => WindowsDeviceOperationContract.RequireInstanceId("\uD800"));
        Refuse(() => WindowsDeviceOperationContract.Parse("read", "{\"version\":1,\"instanceId\":\"\\uD800\"}"));
        // IDs are opaque: do not parse as a filesystem path or expand a pattern.
        Check(Read(@"ROOT\*LITERAL\0000").InstanceId == @"ROOT\*LITERAL\0000", "opaque ID was rewritten/expanded");
        var request = new OperationRequest("windows_device", new Dictionary<string, string> { ["op"] = "list" },
            "read-id", Body: "{\"version\":1}");
        foreach (var invalid in new[]
        {
            request with { Root = "AB12" }, request with { Ws = "alias" }, request with { Cwd = @"C:\fixture" },
            request with { PathRoots = new Dictionary<string, string> { ["x"] = @"C:\fixture" } },
            request with { Body = null }, request with { Rid = "bad\n" },
            request with { Arg = new Dictionary<string, string> { ["op"] = "list", ["wait"] = "false" } },
            request with { Arg = new Dictionary<string, string> { ["op"] = "list", ["rid"] = "different" } }
        }) Refuse(() => WindowsDeviceOperationContract.Parse(invalid));
        Check(WindowsDeviceOperationContract.Parse("list", "{\"limit\":32,\"scope\":\"present\",\"version\":1}").RequestIdentity ==
            first.RequestIdentity, "property ordering changed read identity");
    }

    private static void MetadataAndExactRead()
    {
        using var fixture = new Fixture();
        Check(fixture.Native.OpenCalls == 0 && fixture.Operations.IsCurrentOwner(), "constructor enumerated devices");
        var result = fixture.Operations.Execute(List(limit: 1));
        Check(result.Ok && result.Devices.Count == 1 && result.HasMore && !result.Partial && fixture.Native.LastPresent,
            "present device page");
        var item = result.Devices[0];
        Check(item.InstanceId == Id(0) && item.Name == "Synthetic friendly name" && item.Class == "Synthetic class" &&
            item.Manufacturer == "Synthetic manufacturer" && item.Service == "SyntheticService" &&
            item.DriverInf == "oem-fixture.inf" && item.DriverVersion == "1.2.3.4" && item.DriverProvider == "Synthetic provider" &&
            item.DriverDateUtc == new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero), "selected registered metadata");
        Check(item.StatusFlags == 8 && item.ProblemCode is null, "undefined problem output was invented as meaningful");
        fixture.Native.StatusValue = new(0x408, 42);
        var exact = fixture.Operations.Execute(Read(Id(0).ToLowerInvariant()));
        Check(exact.Ok && exact.Scope == "exact" && exact.Devices.Single().StatusFlags == 0x408 &&
            exact.Devices.Single().ProblemCode == 42 && fixture.Native.LastExact == Id(0).ToLowerInvariant(),
            "exact ID read/status pair");
        Check(fixture.Native.ActiveSets == 0 && fixture.Native.Disposals == 2 && fixture.Native.BuffersCleared,
            "exact retained info sets/buffers were not disposed");
        Check(fixture.Native.PropertyFields.All(field => Enum.IsDefined(field)) &&
            !fixture.Native.PropertyFields.Contains(WindowsDeviceField.Description), "unexpected property/discretionary fallback");
        _ = fixture.Operations.Execute(List(scope: "all", limit: 1));
        Check(!fixture.Native.LastPresent, "all-scope silently kept present-only filter");
        fixture.Native.Fields[WindowsDeviceField.FriendlyName] = new(0x12, null, 1168);
        var fallback = fixture.Operations.Execute(Read(Id(0))).Devices.Single();
        Check(fallback.Name == "Synthetic description" && fallback.Issues.Any(issue => issue.Code == "not_found"),
            "missing friendly-name metadata was hidden/invented");
    }

    private static void PagingAndBounds()
    {
        using var fixture = new Fixture();
        fixture.Native.Count = 1_000_000;
        var first = fixture.Operations.Execute(List(limit: 64));
        Check(first.Ok && first.Devices.Count == 64 && first.Scanned == 65 && first.HasMore &&
            first.NextAfterInstanceId == Id(63), "million-entry source did not stop at page plus lookahead");
        Check(fixture.Native.Enumerations == 65 && fixture.Native.Properties == 64 * 8, "page native work was not bounded");
        var next = fixture.Operations.Execute(List(limit: 2, after: first.NextAfterInstanceId));
        Check(next.Ok && next.Devices[0].InstanceId == Id(64) && next.NextAfterInstanceId == Id(65), "exact anchor forward page");
        var calls = fixture.Native.Enumerations;
        var limited = fixture.Operations.Execute(List(limit: 1, after: Id(5000)));
        Check(!limited.Ok && limited.Code == "enumeration_limit" && limited.Partial && limited.HasMore &&
            limited.NextAfterInstanceId is null && fixture.Native.Enumerations - calls == 4096,
            "seek ceiling became unbounded or a complete empty page");
        fixture.Native.Count = 3;
        var missing = fixture.Operations.Execute(List(after: "ROOT\\NOT_FOUND\\0"));
        Check(!missing.Ok && missing.Code == "precondition" && missing.Devices.Count == 0, "missing anchor restarted first page");

        using var large = new Fixture();
        large.Native.Count = 1000;
        foreach (var field in large.Native.Fields.Keys.ToArray())
        {
            if (field == WindowsDeviceField.DriverDate) continue;
            var length = field switch
            {
                WindowsDeviceField.Class or WindowsDeviceField.DriverVersion => 128,
                WindowsDeviceField.Manufacturer or WindowsDeviceField.Service or WindowsDeviceField.DriverProvider => 256,
                WindowsDeviceField.DriverInf => 260, _ => 512
            };
            large.Native.Fields[field] = Text(new string('\u4E2D', length));
        }
        var bytePage = large.Operations.Execute(List(limit: 64));
        Check(bytePage.Ok && bytePage.HasMore && bytePage.Devices.Count is > 0 and < 64 &&
            bytePage.NextAfterInstanceId == bytePage.Devices[^1].InstanceId &&
            JsonSerializer.SerializeToUtf8Bytes(bytePage, Json).Length <=
                WindowsDeviceOperationContract.MaximumPageBytes - WindowsDeviceOperationContract.PageEnvelopeReserveBytes,
            "worst-case encoded fields exceeded the page serialization budget");
        var byteNext = large.Operations.Execute(List(limit: 64, after: bytePage.NextAfterInstanceId));
        Check(byteNext.Ok && byteNext.Devices[0].InstanceId == Id(bytePage.Devices.Count),
            "byte-shortened page dropped the inspected but unreturned next item");
    }

    private static void MissingChangesAndErrors()
    {
        using var fixture = new Fixture();
        fixture.Native.Fields[WindowsDeviceField.Class] = new(7, new byte[4]);
        fixture.Native.Fields[WindowsDeviceField.Manufacturer] = new(0x12, new byte[70_000]);
        fixture.Native.Fields[WindowsDeviceField.Service] = new(0x12, [65, 0, 0]);
        fixture.Native.Fields[WindowsDeviceField.DriverInf] = new(0x19, Encoding.Unicode.GetBytes("must-not-load.dll\0"));
        fixture.Native.Fields[WindowsDeviceField.DriverVersion] = new(0x12, null, 5);
        fixture.Native.Fields[WindowsDeviceField.DriverProvider] = new(0x12, [0, 216, 0, 0]);
        var date = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(date, long.MaxValue);
        fixture.Native.Fields[WindowsDeviceField.DriverDate] = new(0x10, date);
        fixture.Native.StatusValue = new(0, 0, 13);
        var broken = fixture.Operations.Execute(Read(Id(0)));
        Check(broken.Ok && broken.Partial && broken.Devices.Single().Issues.Count == 8, "typed per-field failures were hidden");
        var item = broken.Devices.Single();
        Check(item.Class is null && item.Manufacturer is null && item.Service is null && item.DriverInf is null &&
            item.DriverVersion is null && item.DriverProvider is null && item.DriverDateUtc is null && item.StatusFlags is null,
            "malformed/unsupported metadata was decoded or expanded");
        Check(item.Issues.Any(issue => issue.Code == "field_limit") && item.Issues.Any(issue => issue.Code == "unsupported_type") &&
            item.Issues.Any(issue => issue.NativeDomain == "configmgr32" && issue.NativeCode == 13) &&
            !JsonSerializer.Serialize(broken, Json).Contains("must-not-load", StringComparison.Ordinal) && fixture.Native.BuffersCleared,
            "native domain/bounds/raw diagnostics cleanup");
        fixture.Native.IdentityOverride = (index, read) => read > 1 ? new("ROOT\\CHANGED\\0") : new(Id(index));
        var changed = fixture.Operations.Execute(Read(Id(0)));
        Check(!changed.Ok && changed.Code == "precondition" && changed.Devices.Single().Name is null &&
            changed.Devices.Single().Issues.Single().Code == "device_changed", "mixed metadata survived observed identity change");
        fixture.Native.IdentityOverride = (index, read) => new(null, 1168);
        var removed = fixture.Operations.Execute(Read(Id(0)));
        Check(!removed.Ok && removed.Devices.Count == 0, "removed exact device became empty success");
        fixture.Native.IdentityOverride = (index, read) => index == 1 ? new(null, 1168) : new(Id(index));
        var boundary = fixture.Operations.Execute(List(limit: 2));
        Check(!boundary.Ok && boundary.Code == "continuation_unavailable" && boundary.HasMore &&
            boundary.NextAfterInstanceId is null && boundary.Devices.Count == 2, "unidentified boundary guessed a forward cursor");
        fixture.Native.IdentityOverride = null;
        fixture.Native.EnumerationErrorAt = 1;
        var nativeError = fixture.Operations.Execute(List(limit: 2));
        Check(!nativeError.Ok && nativeError.Code == "access" && nativeError.NextAfterInstanceId is null,
            "enumeration error became a complete partial page");
        fixture.Native.EnumerationErrorAt = null;
        fixture.Native.OpenError = 5;
        Check(fixture.Operations.Execute(List()) is { Code: "access", NativeError: 5, NativeDomain: "setupapi" }, "open denial became absence/lost native status");
        fixture.Native.OpenError = 12345;
        Check(fixture.Operations.Execute(List()) is { Ok: false, Code: "inventory_unavailable", NativeError: 12345, NativeDomain: "setupapi" },
            "unknown observed native error was erased");
        fixture.Native.OpenError = null;
        fixture.Native.IdentityOverride = (_, _) => new(null, 12346);
        Check(fixture.Operations.Execute(Read(Id(0))) is { Ok: false, NativeError: 12346, NativeDomain: "setupapi" },
            "native identity failure lost actual status");
        fixture.Native.IdentityOverride = (_, _) => new(new string('x', 200));
        Check(fixture.Operations.Execute(Read(Id(0))) is { Ok: false, NativeError: null, NativeDomain: null },
            "managed identity validation invented native status");
        fixture.Native.IdentityOverride = null;
        fixture.Native.ThrowStatus = true;
        var arbitrary = fixture.Operations.Execute(Read(Id(0)));
        Check(!arbitrary.Ok && !JsonSerializer.Serialize(arbitrary, Json).Contains("synthetic-diagnostic", StringComparison.Ordinal) &&
            fixture.Native.ActiveSets == 0 && arbitrary.NativeError is null && arbitrary.NativeDomain is null,
            "arbitrary provider diagnostic/HResult escaped or leaked an info set");
    }

    private static void OwnerCancellationAndCleanup()
    {
        using var fixture = new Fixture();
        var ordinary = fixture.Peer;
        foreach (var denied in new[] { ordinary with { Elevated = true }, ordinary with { Session = 0 },
            ordinary with { Session = 8 }, ordinary with { Sid = "S-1-5-18" }, ordinary with { Sid = "S-1-5-21-10-20-30-1002" } })
        {
            fixture.Peer = denied;
            var calls = fixture.Native.OpenCalls;
            var result = fixture.Operations.Execute(List());
            Check(!result.Ok && result.Code == "owner_denied" && result.Devices.Count == 0 && fixture.Native.OpenCalls == calls,
                "wrong owner/session/elevation opened native inventory");
        }
        fixture.Peer = ordinary;
        fixture.Native.Hook = stage => { if (stage == "property") fixture.Peer = ordinary with { Session = 8 }; };
        var lost = fixture.Operations.Execute(List(limit: 1));
        Check(!lost.Ok && lost.Code == "owner_denied" && lost.Devices.Count == 0 && fixture.Native.ActiveSets == 0 &&
            fixture.Native.BuffersCleared, "owner loss retained payload or raw buffers");
        fixture.Peer = ordinary;
        fixture.Native.Hook = null;
        using var before = new CancellationTokenSource(); before.Cancel();
        var opens = fixture.Native.OpenCalls;
        ExpectCancelled(() => fixture.Operations.Execute(List(), before.Token));
        Check(fixture.Native.OpenCalls == opens, "pre-entry cancellation opened inventory");
        using var during = new CancellationTokenSource();
        fixture.Native.Hook = stage => { if (stage == "property") during.Cancel(); };
        ExpectCancelled(() => fixture.Operations.Execute(List(), during.Token));
        Check(fixture.Native.ActiveSets == 0 && fixture.Native.BuffersCleared, "cancellation leaked native set/returned property bytes");
        fixture.Native.Hook = stage => { if (stage == "dispose") fixture.Peer = ordinary with { Session = 8 }; };
        var disposalLoss = fixture.Operations.Execute(List(limit: 1));
        Check(disposalLoss.Code == "owner_denied" && disposalLoss.Devices.Count == 0, "disposal-time identity loss retained payload");
        fixture.Peer = ordinary; fixture.Native.Hook = null;
        fixture.Native.DisposeError = true;
        var cleanup = fixture.Operations.Execute(List(limit: 1));
        Check(!cleanup.Ok && cleanup.Code == "cleanup_failed" && cleanup.NativeError == 5 && cleanup.NativeDomain == "setupapi" &&
            cleanup.Devices.Count == 0 && fixture.Native.ActiveSets == 0, "cleanup failure was a successful observation");
    }

    [SupportedOSPlatform("windows")]
    private static void NativeLayoutOnly()
    {
        Check(NativeWindowsDevices.DeviceInfoDataSize == (IntPtr.Size == 8 ? 32 : 28) && NativeWindowsDevices.PropertyKeySize == 20,
            "native ABI structure sizes");
        Check(NativeWindowsDevices.KeyFor(WindowsDeviceField.DriverInf).FormatId ==
            new Guid(unchecked((int)0xa8b865dd), 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6) &&
            NativeWindowsDevices.KeyFor(WindowsDeviceField.DriverInf).PropertyId == 5 &&
            NativeWindowsDevices.KeyFor(WindowsDeviceField.DriverProvider).PropertyId == 9, "fixed registered driver property keys");
    }

    // Separate opt-in selector only; never invoked by injected RunAsync.
    internal static Task<int> RunNativeReadOnlyAsync() => Task.FromResult(!OperatingSystem.IsWindows() ? 77 : NativeReadOnlyWindows());
    [SupportedOSPlatform("windows")]
    private static int NativeReadOnlyWindows()
    {
        var owner = LocalIdentity.Current();
        var operations = new WindowsDeviceOperations(owner.Sid);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = operations.Execute(List(limit: 2), deadline.Token);
        if (!result.Ok) { Console.Error.WriteLine("FAIL native device inventory: code=" + result.Code); return 1; }
        var exact = result.Devices.FirstOrDefault(device => device.InstanceId is not null);
        if (exact is not null && !operations.Execute(Read(exact.InstanceId!), deadline.Token).Ok)
        { Console.Error.WriteLine("FAIL native device exact observation changed/unavailable."); return 1; }
        var issues = result.Devices.SelectMany(device => device.Issues)
            .GroupBy(issue => new { issue.Field, issue.Code, issue.NativeCode, issue.NativeDomain })
            .Select(group => new { group.Key.Field, group.Key.Code, group.Key.NativeCode, group.Key.NativeDomain, count = group.Count() });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            observation = "native_device_read", returned = result.Devices.Count, partial = result.Partial,
            more = result.HasMore, issues, aggregateOnly = true, deviceAdministration = false, driverExecution = false
        }, Json));
        return 0;
    }

    private static WindowsDeviceInvocation List(string scope = "present", int limit = 32, string? after = null) =>
        WindowsDeviceOperationContract.Parse("list", JsonSerializer.Serialize(new { version = 1, scope, limit, afterInstanceId = after },
            new JsonSerializerOptions(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    private static WindowsDeviceInvocation Read(string id) =>
        WindowsDeviceOperationContract.Parse("read", JsonSerializer.Serialize(new { version = 1, instanceId = id }, Json));
    private static string Id(int index) => @"ROOT\FIXTURE\D" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    private static WindowsDeviceProperty Text(string value) => new(0x12, Encoding.Unicode.GetBytes(value + '\0'));
    private static void Check(bool value, string code) { _assertions++; if (!value) throw new InvalidOperationException(code); }
    private static void Refuse(Action action)
    {
        _assertions++;
        try { action(); } catch (WindowsDeviceContractException) { return; }
        throw new InvalidOperationException("invalid_device_contract_admitted");
    }
    private static void ExpectCancelled(Action action)
    {
        _assertions++;
        try { action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("device_cancellation_lost");
    }
    private sealed class Fixture : IDisposable
    {
        internal LocalPeer Peer = new(Sid, 7);
        internal readonly FakeNative Native = new();
        internal readonly WindowsDeviceOperations Operations;
        internal Fixture() => Operations = new(Sid, () => Peer, Native);
        public void Dispose() => Native.Dispose();
    }

    private sealed class FakeNative : IWindowsDeviceNative, IDisposable
    {
        internal int Count = 3, OpenCalls, Enumerations, Properties, Disposals, ActiveSets;
        internal bool LastPresent, ThrowStatus, DisposeError;
        internal int? OpenError, EnumerationErrorAt;
        internal string? LastExact, CustomFirstId;
        internal Action<string>? Hook;
        internal Func<int, int, WindowsDeviceIdentity>? IdentityOverride;
        internal WindowsDeviceStatus StatusValue = new(8, 999);
        internal readonly HashSet<WindowsDeviceField> PropertyFields = [];
        internal readonly List<byte[]> ReturnedBuffers = [];
        internal readonly Dictionary<WindowsDeviceField, WindowsDeviceProperty> Fields = new()
        {
            [WindowsDeviceField.FriendlyName] = Text("Synthetic friendly name"),
            [WindowsDeviceField.Description] = Text("Synthetic description"),
            [WindowsDeviceField.Class] = Text("Synthetic class"),
            [WindowsDeviceField.Manufacturer] = Text("Synthetic manufacturer"),
            [WindowsDeviceField.Service] = Text("SyntheticService"),
            [WindowsDeviceField.DriverInf] = Text("oem-fixture.inf"),
            [WindowsDeviceField.DriverVersion] = Text("1.2.3.4"),
            [WindowsDeviceField.DriverDate] = new(0x10, DateBytes()),
            [WindowsDeviceField.DriverProvider] = Text("Synthetic provider")
        };
        internal bool BuffersCleared => ReturnedBuffers.All(bytes => bytes.All(value => value == 0));
        public IWindowsDeviceSet OpenList(bool presentOnly)
        { OpenCalls++; LastPresent = presentOnly; Hook?.Invoke("open"); if (OpenError is { } error) throw new WindowsDeviceNativeException(error); return new Set(this, null); }
        public IWindowsDeviceSet OpenExact(string instanceId)
        {
            OpenCalls++; LastExact = instanceId; Hook?.Invoke("open");
            if (OpenError is { } error) throw new WindowsDeviceNativeException(error);
            var index = -1;
            if (StringComparer.OrdinalIgnoreCase.Equals(instanceId, InstanceId(0))) index = 0;
            else if (instanceId.Length >= 6 && int.TryParse(instanceId[^6..], out var parsed) && parsed < Count &&
                StringComparer.OrdinalIgnoreCase.Equals(instanceId, InstanceId(parsed))) index = parsed;
            if (index < 0) throw new WindowsDeviceNativeException(2);
            return new Set(this, index);
        }
        private string InstanceId(int index) => index == 0 && CustomFirstId is not null ? CustomFirstId : Id(index);
        private static byte[] DateBytes()
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()); return bytes;
        }
        private sealed class Set : IWindowsDeviceSet
        {
            private readonly FakeNative _owner;
            private readonly int? _exact;
            private bool _disposed;
            internal Set(FakeNative owner, int? exact) { _owner = owner; _exact = exact; owner.ActiveSets++; }
            internal void RequireOpen() { if (_disposed) throw new InvalidOperationException("native_set_used_after_disposal"); }
            public WindowsDeviceEntry At(uint index)
            {
                RequireOpen(); _owner.Enumerations++; _owner.Hook?.Invoke("enumerate");
                if (_exact.HasValue) return index == 0 ? new(new Node(this, _owner, _exact.Value)) : new(End: true);
                if (index >= _owner.Count) return new(End: true);
                if (_owner.EnumerationErrorAt == checked((int)index)) return new(Error: 5);
                return new(new Node(this, _owner, checked((int)index)));
            }
            public void Dispose()
            {
                if (_disposed) return; _disposed = true; _owner.Disposals++; _owner.ActiveSets--;
                _owner.Hook?.Invoke("dispose");
                if (_owner.DisposeError) throw new WindowsDeviceNativeException(5, cleanup: true);
            }
        }
        private sealed class Node(Set set, FakeNative owner, int index) : IWindowsDeviceNode
        {
            private int _identities;
            public Guid ClassGuid => new("4d36e97d-e325-11ce-bfc1-08002be10318");
            public WindowsDeviceIdentity Identity()
            { set.RequireOpen(); owner.Hook?.Invoke("identity"); return owner.IdentityOverride?.Invoke(index, ++_identities) ?? new(owner.InstanceId(index)); }
            public WindowsDeviceProperty Property(WindowsDeviceField field, int maximumBytes)
            {
                set.RequireOpen(); owner.Properties++; owner.PropertyFields.Add(field);
                var template = owner.Fields[field];
                var bytes = template.Bytes?.ToArray();
                if (bytes is not null) owner.ReturnedBuffers.Add(bytes);
                owner.Hook?.Invoke("property");
                return template with { Bytes = bytes };
            }
            public WindowsDeviceStatus Status()
            {
                set.RequireOpen(); owner.Hook?.Invoke("status");
                if (owner.ThrowStatus) throw new IOException("synthetic-diagnostic-must-not-escape");
                return owner.StatusValue;
            }
        }
        public void Dispose()
        {
            foreach (var raw in Fields.Values) if (raw.Bytes is not null) Array.Clear(raw.Bytes);
            foreach (var bytes in ReturnedBuffers) Array.Clear(bytes);
            Fields.Clear(); ReturnedBuffers.Clear();
            if (ActiveSets != 0) throw new InvalidOperationException("injected_native_set_leaked");
        }
    }
}
