using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Fake native API buffers/SafeHandle release plus injected owner context only.
// Synthetic RFC5737/RFC3849 addresses and PIDs never appear in fixture output.
internal static class WindowsNetworkTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private static int _checks;
    internal static Task RunAsync()
    {
        _checks = 0;
        Contract(); OwnerAndPaging(); DuplicateCoordinates(); AdapterAbiAndLimits(); RouteAbiAndOwnership(); ConnectionAbiAndVisibility();
        Console.WriteLine($"PASS local network inventory: {_checks} pure/injected owner/native-buffer/ABI checks; limits, forward observations, cancellation and owned route frees; no native inventory, DNS/probe/command/configuration");
        return Task.CompletedTask;
    }

    internal static WindowsNetworkOperations CreateEngineFixtureOperations(string ownerSid, Func<LocalPeer> current) =>
        new(ownerSid, current, new Managed { Items = [FixtureItem(0)] });

    [SupportedOSPlatform("windows")]
    internal static Task RunNativeReadOnlyAsync()
    {
        var identity = LocalIdentity.Current(); var provider = new WindowsNetworkOperations(identity.Sid);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var rows = 0; var partial = 0; var details = new List<object>();
        foreach (var view in WindowsNetworkOperationContract.Views)
        {
            var observed = provider.Execute(Parse("list", view, limit: 1), deadline.Token);
            if (!observed.Ok) throw new InvalidOperationException("native_network_" + observed.Code);
            rows += observed.Items.Count; if (observed.Partial) partial++;
            details.Add(new { view, code = observed.Code, returned = observed.Items.Count, partial = observed.Partial, issues = observed.Issues });
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            observation = "native_local_network", firstPageRows = rows, partialViews = partial, views = details,
            aggregateOnly = true, trafficProbes = false, configurationWrites = false, remoteAuthenticationVerified = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return Task.CompletedTask;
    }

    private static void Contract()
    {
        Check(WindowsNetworkOperationContract.Actions.SequenceEqual(new[] { "list", "read" }) &&
            WindowsNetworkOperationContract.Views.SequenceEqual(new[] { "adapters", "routes", "connections" }), "closed views/actions");
        foreach (var action in WindowsNetworkOperationContract.Actions)
        foreach (var view in WindowsNetworkOperationContract.Views)
            Check(Parse(action, view).IsMutation == false && WindowsNetworkOperationContract.IsReadOnly(action), "passive classification");
        foreach (var bad in new[] { "remote", "dns_probe", "listeners", "Adapters" })
            Refused(() => Parse("list", bad));
        foreach (var limit in new[] { 0, 65 }) Refused(() => Parse("list", limit: limit));
        foreach (var bad in new[] { "", "ABC", new string('a', 63), new string('A', 64) })
        { Refused(() => Parse("read", key: bad)); Refused(() => Parse("list", after: bad)); }
        foreach (var extra in new[] { "computer", "host", "credentials", "dnsName", "configure", "address", "wait" })
            Refused(() => WindowsNetworkOperationContract.Parse("list", "{\"version\":1,\"view\":\"adapters\",\"" + extra + "\":null}"));
        Refused(() => WindowsNetworkOperationContract.Parse("list", "{\"version\":1,\"version\":1,\"view\":\"adapters\"}"));
        Refused(() => WindowsNetworkOperationContract.Parse("list", "{\"version\":2,\"view\":\"adapters\"}"));
        Refused(() => WindowsNetworkOperationContract.Parse("read", "{\"version\":1,\"view\":\"adapters\",\"key\":null}"));
        Refused(() => WindowsNetworkOperationContract.Parse("list", new string(' ', 4097)));
        var request = new OperationRequest("windows_network", new Dictionary<string, string> { ["op"] = "list" }, Body: Body("list"));
        foreach (var forbidden in new[] { request with { Root = "root" }, request with { Ws = "workspace" }, request with { Cwd = @"C:\fixture" },
            request with { PathRoots = new Dictionary<string, string> { ["x"] = @"C:\fixture" } },
            request with { Arg = new Dictionary<string, string> { ["op"] = "list", ["wait"] = "false" } }, request with { Body = null } })
            Refused(() => WindowsNetworkOperationContract.Parse(forbidden));
        var reordered = WindowsNetworkOperationContract.Parse("list", "{\"limit\":32,\"view\":\"adapters\",\"version\":1}");
        Check(reordered.RequestIdentity == Parse("list").RequestIdentity, "JSON property order changed request identity");
    }

    private static void OwnerAndPaging()
    {
        var peer = new LocalPeer(Sid, 7, Elevated: false);
        var backend = new Managed { Items = Enumerable.Range(0, 5).Select(FixtureItem).Reverse().ToArray() };
        var provider = new WindowsNetworkOperations(Sid, () => peer, backend);
        var first = provider.Execute(Parse("list", limit: 2));
        Check(first.Ok && first.HasMore && first.NextKey == first.Items[^1].Key && first.Items.Count == 2 &&
            StringComparer.Ordinal.Compare(first.Items[0].Key, first.Items[1].Key) < 0, "bounded deterministic page");
        var next = provider.Execute(Parse("list", limit: 2, after: first.NextKey));
        Check(next.Ok && next.Items.All(item => StringComparer.Ordinal.Compare(item.Key, first.NextKey) > 0) && backend.Calls == 2,
            "forward page did not reobserve or reused state");
        Check(provider.Execute(Parse("read", key: first.Items[0].Key)).Items.Single().Key == first.Items[0].Key, "selected observed key read");
        Check(provider.Execute(Parse("read", key: new string('f', 64))).Code == "not_found", "missing coordinate invented a row");
        backend.Partial = true;
        Check(provider.Execute(Parse("read", key: new string('f', 64))).Code == "network_visibility_incomplete", "partial coverage proved absence");
        backend.Partial = false;
        var original = peer;
        foreach (var denied in new[] { original with { Elevated = true }, original with { Session = 8 }, original with { Session = 0 },
            original with { Sid = "S-1-5-18" }, original with { Sid = "S-1-5-21-10-20-30-1002" } })
        {
            peer = denied; var before = backend.Calls; var deniedResult = provider.Execute(Parse("list"));
            Check(!deniedResult.Ok && deniedResult.Code == "owner_denied" && deniedResult.Items.Count == 0 && backend.Calls == before, "nonowner invoked native collection");
        }
        peer = original; backend.Hook = () => peer = peer with { Elevated = true };
        Check(provider.Execute(Parse("list")).Items.Count == 0, "owner loss disclosed collected metadata");
        peer = original; backend.Hook = null;
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var calls = backend.Calls;
        Canceled(() => provider.Execute(Parse("list"), canceled.Token)); Check(backend.Calls == calls, "canceled request collected");
        using var during = new CancellationTokenSource(); backend.Hook = during.Cancel;
        Canceled(() => provider.Execute(Parse("list"), during.Token)); backend.Hook = null;
        backend.Items = Enumerable.Repeat(FixtureItem(0), 4097).ToArray();
        Check(provider.Execute(Parse("list")).Code == "network_limit", "managed row bound ignored");
        backend.Items = [WindowsNetworkOperations.Item("adapter", new { id = 1 }, new() { ["name"] = new string('x', 270000) })];
        Check(provider.Execute(Parse("list")).Code == "network_output_limit", "oversize metadata silently clipped");
    }

    private static void DuplicateCoordinates()
    {
        var first = WindowsNetworkOperations.Item("tcp_connection", new { id = 1 }, new()
        {
            ["state"] = "established", ["ownerPid"] = 100,
            ["endpoint"] = new Dictionary<string, object?> { ["address"] = "192.0.2.1", ["port"] = 80 },
            ["ordered"] = new[] { 1, 2 }, ["nullable"] = null
        });
        var reordered = first with { Fields = new Dictionary<string, object?>
        {
            ["nullable"] = null, ["ordered"] = new[] { 1, 2 },
            ["endpoint"] = new Dictionary<string, object?> { ["port"] = 80, ["address"] = "192.0.2.1" },
            ["ownerPid"] = 100, ["state"] = "established"
        } };
        var backend = new Managed { Items = [first, reordered, first] };
        var provider = new WindowsNetworkOperations(Sid, () => new LocalPeer(Sid, 7, Elevated: false), backend);
        var identical = provider.Execute(Parse("list", "connections"));
        Check(identical.Ok && !identical.Partial && identical.Issues.Count == 0 && identical.Items.Count == 1 &&
            identical.Items[0].Occurrences == 3, "identical reordered coordinates falsely lost coverage or multiplicity");
        Check(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first.Fields), JsonSerializer.SerializeToElement(identical.Items[0].Fields)),
            "identical coordinates lost observed facts");
        var conflicting = first with { Fields = new Dictionary<string, object?>
        {
            ["state"] = "listening", ["ownerPid"] = 100,
            ["endpoint"] = new Dictionary<string, object?> { ["port"] = 80, ["address"] = "192.0.2.1" },
            ["ordered"] = new[] { 2, 1 }, ["extra"] = true
        } };
        var absent = first with { Fields = new Dictionary<string, object?> { ["ownerPid"] = 100, ["state"] = "established" } };
        backend.Items = [first, conflicting, absent];
        var disputed = provider.Execute(Parse("list", "connections"));
        Check(disputed.Ok && disputed.Partial && disputed.Items.Single().Occurrences == 3 &&
            disputed.Issues.Count == 1 && disputed.Issues[0].Code == "conflicting_coordinates", "conflicting coordinate coverage was not explicit");
        Check(disputed.Items[0].Fields.Count == 1 && disputed.Items[0].Fields["ownerPid"] is 100,
            "conflicting or absent facts were selected from an arbitrary member");
        backend.Items = [conflicting, absent, first];
        Check(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(disputed.Items[0].Fields),
            JsonSerializer.SerializeToElement(provider.Execute(Parse("list", "connections")).Items[0].Fields)), "common facts depended on member order");
        backend.Items = [first, conflicting];
        var arrayConflict = provider.Execute(Parse("list", "connections"));
        Check(arrayConflict.Partial && arrayConflict.Items[0].Fields.ContainsKey("endpoint") &&
            !arrayConflict.Items[0].Fields.ContainsKey("ordered") && !arrayConflict.Items[0].Fields.ContainsKey("nullable") &&
            !arrayConflict.Items[0].Fields.ContainsKey("extra"), "object ordering, array priority or absent/null distinction was lost");
        backend.Items = [first, first with { Kind = "udp_endpoint" }];
        var kindConflict = provider.Execute(Parse("list", "connections"));
        Check(!kindConflict.Ok && kindConflict.Code == "network_invalid_native_data" && kindConflict.Items.Count == 0,
            "different kinds sharing a coordinate were merged");
        backend.Items = [first, first with { Fields = new Dictionary<string, object?> { ["state"] = new string('x', 270000) } }];
        Check(provider.Execute(Parse("list", "connections")).Code == "network_output_limit", "duplicate comparison exceeded the field byte cap");
    }

    private static void AdapterAbiAndLimits()
    {
        Check(Marshal.OffsetOf<WindowsNetworkNative.Adapter>(nameof(WindowsNetworkNative.Adapter.FirstDns)).ToInt32() == (nint.Size == 8 ? 48 : 28) &&
            Marshal.OffsetOf<WindowsNetworkNative.Adapter>(nameof(WindowsNetworkNative.Adapter.FirstGateway)).ToInt32() == (nint.Size == 8 ? 208 : 164), "adapter pointer ABI");
        Check((WindowsNetworkNative.AdapterFlags & 0x180) == 0x180 && (WindowsNetworkNative.AdapterFlags & 0x9) == 0,
            "adapter flags excluded DNS/unicast or unconfigured interfaces");
        var calls = new Calls(); var native = new WindowsNetworkNative(calls);
        var capture = native.Capture("adapters", CancellationToken.None);
        Check(capture.Ok && capture.Items.Count == 1 && !capture.Coverage.DnsResolutionPerformed && !capture.Coverage.Snapshot,
            "adapter configuration was not observed or claimed DNS resolution/snapshot");
        var data = JsonSerializer.SerializeToElement(capture.Items[0].Fields, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(data.GetProperty("unicastAddresses")[0].GetProperty("address").GetString() == "192.0.2.2" &&
            data.GetProperty("unicastAddresses")[0].GetProperty("prefixLength").GetInt32() == 24 &&
            data.GetProperty("dnsServers")[0].GetProperty("address").GetString() == "2001:db8::53%3" &&
            data.GetProperty("gateways")[0].GetProperty("address").GetString() == "192.0.2.1", "adapter address/prefix/DNS/gateway ABI");
        calls.AdapterMode = "grow"; calls.AdapterCalls = 0;
        Check(native.Capture("adapters", CancellationToken.None).Ok && calls.AdapterCalls == 2, "single bounded buffer growth failed");
        foreach (var mode in new[] { "oversize", "twice", "cycle", "outside", "address-cycle", "address-limit", "bad-length", "bad-text" })
        {
            calls.AdapterMode = mode; calls.AdapterCalls = 0;
            var failed = native.Capture("adapters", CancellationToken.None);
            Check(!failed.Ok && failed.Items.Count == 0 && calls.AdapterCalls <= 2, "invalid adapter buffer or third retry admitted");
        }
    }

    private static void RouteAbiAndOwnership()
    {
        Check(Marshal.SizeOf<WindowsNetworkNative.SockaddrInet>() == 28 && Marshal.SizeOf<WindowsNetworkNative.Prefix>() == 32 &&
            Marshal.SizeOf<WindowsNetworkNative.RouteRow>() == 104 && Marshal.OffsetOf<WindowsNetworkNative.RouteLayout>(nameof(WindowsNetworkNative.RouteLayout.First)).ToInt32() == 8 &&
            Marshal.OffsetOf<WindowsNetworkNative.RouteRow>(nameof(WindowsNetworkNative.RouteRow.NextHop)).ToInt32() == 44, "route row/header/socket ABI");
        var calls = new Calls(); var native = new WindowsNetworkNative(calls);
        var captured = native.Capture("routes", CancellationToken.None);
        Check(captured.Ok && captured.Items.Count == 2 && calls.RouteCalls == 1 && calls.RouteFrees == 1 &&
            !captured.Coverage.NativePreallocationCapped && captured.Coverage.MaximumCallerBufferBytes is null &&
            captured.Coverage.Limitations.Contains("windows_allocates_route_table_before_count_admission"), "route allocation ownership/limitation hidden");
        Check(captured.Items.Select(item => item.Fields["family"]).SequenceEqual(new object?[] { "ipv4", "ipv6" }) &&
            captured.Items[0].Fields["destinationPrefix"] is "192.0.2.0/24" && captured.Items[1].Fields["nextHop"] is "fe80::1%3" &&
            Convert.ToUInt32(captured.Items[0].Fields["routeMetricOffset"]) == 7, "both route families/prefix/metric ABI");
        foreach (var mode in new[] { "oversize", "invalid-family", "invalid-prefix", "native-error" })
        {
            calls.RouteMode = mode; var allocated = calls.RouteCalls; var freed = calls.RouteFrees;
            var failed = native.Capture("routes", CancellationToken.None);
            Check(!failed.Ok && failed.Items.Count == 0 && calls.RouteCalls == allocated + 1 && calls.RouteFrees == freed + 1,
                "route failure escaped single-allocation/free boundary");
        }
        calls.RouteMode = "normal"; using var canceled = new CancellationTokenSource();
        calls.AfterRoutes = canceled.Cancel; var before = calls.RouteFrees;
        Canceled(() => native.Capture("routes", canceled.Token)); Check(calls.RouteFrees == before + 1, "post-allocation cancellation leaked route table");
    }

    private static void ConnectionAbiAndVisibility()
    {
        var calls = new Calls(); var native = new WindowsNetworkNative(calls);
        var captured = native.Capture("connections", CancellationToken.None);
        Check(captured.Ok && captured.Items.Count == 4 && !captured.Partial && calls.TcpFamilies.SequenceEqual(new uint[] { 2, 23 }) &&
            calls.UdpFamilies.SequenceEqual(new uint[] { 2, 23 }), "fixed TCP/UDP family tables");
        var tcp = captured.Items.First(item => item.Kind == "tcp_connection");
        var udp = captured.Items.First(item => item.Kind == "udp_endpoint");
        Check(Convert.ToInt32(tcp.Fields["localPort"]) == 50000 && Convert.ToInt32(tcp.Fields["remotePort"]) == 443 &&
            tcp.Fields["localAddress"] is "192.0.2.2" && Convert.ToUInt32(tcp.Fields["ownerPid"]) == 4242,
            "TCP address/port/state/PID ABI");
        Check(udp.Fields["remoteAddress"] is null && udp.Fields["remotePort"] is null && udp.Fields["stateCode"] is null &&
            captured.Coverage.Limitations.Contains("udp_rows_are_local_endpoints_without_remote_peer_or_connection_state"), "UDP endpoint invented a peer/connection state");
        calls.Tcp6Error = 50;
        var partial = native.Capture("connections", CancellationToken.None);
        Check(partial.Ok && partial.Partial && partial.Items.Count == 3 && partial.Issues.Single().Area == "tcp_ipv6" &&
            partial.Issues.Single().NativeError == 50, "one unavailable family became empty complete success");
        calls.Tcp6Error = 0; calls.ConnectionRows = 4097;
        Check(!native.Capture("connections", CancellationToken.None).Ok, "native table count bound ignored");
        calls.ConnectionRows = 1; calls.InvalidPort = true;
        Check(!native.Capture("connections", CancellationToken.None).Ok, "invalid native port accepted");
    }

    private static WindowsNetworkItem FixtureItem(int id) => WindowsNetworkOperations.Item("adapter", new { id },
        new() { ["id"] = "fixture-adapter-" + id, ["name"] = "Fixture adapter", ["interfaceIndex"] = 1u });
    private sealed class Managed : IWindowsNetworkNative
    {
        internal WindowsNetworkItem[] Items = []; internal int Calls; internal bool Partial; internal Action? Hook;
        public WindowsNetworkCapture Capture(string view, CancellationToken token)
        { Calls++; Hook?.Invoke(); return new(true, "observed", Items, Partial, WindowsNetworkOperations.Coverage(view),
            Partial ? new[] { new WindowsNetworkIssue(view, "injected_visibility") } : Array.Empty<WindowsNetworkIssue>()); }
    }
    private sealed class Calls : IWindowsNetworkNativeCalls
    {
        internal string AdapterMode = "normal", RouteMode = "normal";
        internal int AdapterCalls, RouteCalls, RouteFrees, Tcp6Error, ConnectionRows = 1;
        internal bool InvalidPort; internal Action? AfterRoutes;
        internal readonly List<uint> TcpFamilies = new(), UdpFamilies = new();
        public int Adapters(nint buffer, ref uint bytes)
        {
            AdapterCalls++;
            if (AdapterMode == "oversize") { bytes = 1024 * 1024 + 1; return 111; }
            if (AdapterMode is "grow" or "twice" && AdapterCalls == 1) { bytes = 128 * 1024; return 111; }
            if (AdapterMode == "twice") { bytes = 256 * 1024; return 111; }
            bytes = 16384;
            var adapter = new WindowsNetworkNative.Adapter
            {
                Length = (uint)Marshal.SizeOf<WindowsNetworkNative.Adapter>(), IfIndex = 7, Ipv6IfIndex = 8, Luid = 9,
                AdapterName = buffer + 256, FriendlyName = buffer + 320, Description = buffer + 400, DnsSuffix = buffer + 480,
                FirstUnicast = buffer + 640, FirstDns = buffer + 704, FirstGateway = buffer + 848,
                Mtu = 1500, OperStatus = 1, IfType = 6, PhysicalAddress = new byte[8], ZoneIndices = new uint[16]
            };
            Copy(buffer + 256, Encoding.ASCII.GetBytes("fixture-adapter\0"));
            Copy(buffer + 320, Encoding.Unicode.GetBytes("Fixture adapter\0")); Copy(buffer + 400, Encoding.Unicode.GetBytes("Synthetic adapter\0"));
            Copy(buffer + 480, Encoding.Unicode.GetBytes("example.invalid\0"));
            var unicast = new WindowsNetworkNative.Unicast { Length = (uint)Marshal.SizeOf<WindowsNetworkNative.Unicast>(),
                Address = new() { Pointer = buffer + 768, Length = 16 }, PrefixLength = 24 };
            if (AdapterMode == "cycle") adapter.Next = buffer;
            if (AdapterMode == "outside") adapter.AdapterName = buffer + 20000;
            if (AdapterMode == "bad-length") adapter.Length = 4;
            if (AdapterMode == "bad-text") Copy(buffer + 256, Enumerable.Repeat((byte)'x', 257).ToArray());
            if (AdapterMode == "address-cycle") unicast.Next = buffer + 640;
            if (AdapterMode == "address-limit")
            {
                adapter.FirstUnicast = buffer + 2048;
                for (var index = 0; index < 65; index++)
                {
                    var node = unicast; node.Next = index == 64 ? 0 : buffer + 2048 + (index + 1) * 64;
                    Marshal.StructureToPtr(node, buffer + 2048 + index * 64, false);
                }
            }
            else Marshal.StructureToPtr(unicast, buffer + 640, false);
            Marshal.StructureToPtr(new WindowsNetworkNative.AddressNode { Length = (uint)Marshal.SizeOf<WindowsNetworkNative.AddressNode>(),
                Address = new() { Pointer = buffer + 800, Length = 28 } }, buffer + 704, false);
            Marshal.StructureToPtr(new WindowsNetworkNative.AddressNode { Length = (uint)Marshal.SizeOf<WindowsNetworkNative.AddressNode>(),
                Address = new() { Pointer = buffer + 896, Length = 16 } }, buffer + 848, false);
            Socket(buffer + 768, "192.0.2.2"); Socket(buffer + 800, "2001:db8::53", 3); Socket(buffer + 896, "192.0.2.1");
            Marshal.StructureToPtr(adapter, buffer, false); return 0;
        }
        public WindowsNetworkRouteTable Routes()
        {
            RouteCalls++;
            var count = RouteMode == "oversize" ? 4097 : 2;
            var bytes = count > 4096 ? 8 : 8 + count * 104;
            var memory = Marshal.AllocHGlobal(bytes); Marshal.Copy(new byte[bytes], 0, memory, bytes); Marshal.WriteInt32(memory, count);
            var owner = new WindowsNetworkRouteTable(memory, RouteMode == "native-error" ? 5 : 0, pointer => { RouteFrees++; Marshal.FreeHGlobal(pointer); });
            try
            {
                if (count <= 4096)
                for (var index = 0; index < count; index++)
                {
                    var row = new WindowsNetworkNative.RouteRow { InterfaceLuid = (ulong)(index + 1), InterfaceIndex = (uint)(index + 1),
                        Destination = new() { PrefixLength = (byte)(index == 0 ? 24 : RouteMode == "invalid-prefix" ? 129 : 64) },
                        Metric = 7, Protocol = 3, Origin = 0, ValidLifetime = uint.MaxValue, PreferredLifetime = uint.MaxValue };
                    var at = memory + 8 + index * 104; Marshal.StructureToPtr(row, at, false);
                    Socket(at + 12, index == 0 ? "192.0.2.0" : "2001:db8::");
                    Socket(at + 44, index == 0 ? "192.0.2.1" : "fe80::1", index == 0 ? 0u : 3u);
                    if (RouteMode == "invalid-family" && index == 1) Marshal.WriteInt16(at + 12, 99);
                }
                AfterRoutes?.Invoke(); return owner;
            }
            catch { owner.Dispose(); throw; }
        }
        public int Tcp(nint buffer, ref uint bytes, uint family)
        { TcpFamilies.Add(family); if (family == 23 && Tcp6Error != 0) return Tcp6Error; return Table(buffer, ref bytes, family, true); }
        public int Udp(nint buffer, ref uint bytes, uint family)
        { UdpFamilies.Add(family); return Table(buffer, ref bytes, family, false); }
        private int Table(nint buffer, ref uint bytes, uint family, bool tcp)
        {
            var rowBytes = tcp ? family == 2 ? 24 : 56 : family == 2 ? 12 : 28;
            var required = checked(4 + ConnectionRows * rowBytes);
            if (required > bytes) { bytes = (uint)required; return 122; }
            bytes = (uint)required; Marshal.WriteInt32(buffer, ConnectionRows);
            for (var index = 0; index < ConnectionRows; index++)
            {
                var row = buffer + 4 + index * rowBytes; var ipv6 = family == 23;
                Copy(row + (tcp && !ipv6 ? 4 : 0), IPAddress.Parse(ipv6 ? "2001:db8::2" : tcp ? "192.0.2.2" : "0.0.0.0").GetAddressBytes());
                Marshal.WriteInt32(row + (ipv6 ? 20 : tcp ? 8 : 4), unchecked((int)(InvalidPort ? 0x10000u : NetworkPort(tcp ? ipv6 ? 50001 : 50000 : 5353))));
                if (tcp)
                {
                    Copy(row + (ipv6 ? 24 : 12), IPAddress.Parse(ipv6 ? "2001:db8::9" : "203.0.113.9").GetAddressBytes());
                    Marshal.WriteInt32(row + (ipv6 ? 44 : 16), (int)NetworkPort(443)); Marshal.WriteInt32(row + (ipv6 ? 48 : 0), 5);
                    Marshal.WriteInt32(row + (ipv6 ? 52 : 20), ipv6 ? 4243 : 4242);
                }
                else Marshal.WriteInt32(row + (ipv6 ? 24 : 8), ipv6 ? 4245 : 4244);
            }
            return 0;
        }
    }
    private static uint NetworkPort(int port) => (uint)((port & 0xff) << 8 | port >> 8);
    private static void Copy(nint pointer, byte[] bytes) => Marshal.Copy(bytes, 0, pointer, bytes.Length);
    private static void Socket(nint pointer, string address, uint scope = 0)
    {
        var ip = IPAddress.Parse(address); var ipv6 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        Marshal.WriteInt16(pointer, (short)(ipv6 ? 23 : 2)); Copy(pointer + (ipv6 ? 8 : 4), ip.GetAddressBytes());
        if (ipv6) Marshal.WriteInt32(pointer + 24, unchecked((int)scope));
    }
    private static WindowsNetworkInvocation Parse(string action, string view = "adapters", int limit = 32, string? after = null, string? key = null) =>
        WindowsNetworkOperationContract.Parse(action, Body(action, view, limit, after, key));
    private static string Body(string action, string view = "adapters", int limit = 32, string? after = null, string? key = null)
    {
        var fields = new Dictionary<string, object?> { ["version"] = 1, ["view"] = view };
        if (action == "list") { fields["limit"] = limit; if (after is not null) fields["afterKey"] = after; }
        else fields["key"] = key ?? new string('a', 64);
        return JsonSerializer.Serialize(fields);
    }
    private static void Refused(Action action)
    { try { action(); } catch (WindowsNetworkContractException) { _checks++; return; } throw new InvalidOperationException("invalid network contract accepted"); }
    private static void Canceled(Action action)
    { try { action(); } catch (OperationCanceledException) { _checks++; return; } throw new InvalidOperationException("network cancellation not propagated"); }
    private static void Check(bool value, string code) { if (!value) throw new InvalidOperationException(code); _checks++; }
}
