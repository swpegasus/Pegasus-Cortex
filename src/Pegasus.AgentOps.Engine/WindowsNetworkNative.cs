using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

internal interface IWindowsNetworkNativeCalls
{
    int Adapters(nint buffer, ref uint bytes);
    int Tcp(nint buffer, ref uint bytes, uint family);
    int Udp(nint buffer, ref uint bytes, uint family);
    WindowsNetworkRouteTable Routes();
}

// Ownership is transferred with the result, including nonzero native status.
// GetIpForwardTable2 allocates before the count is observable. No claimed byte
// cap applies to that OS allocation; managed traversal is admitted immediately.
internal sealed class WindowsNetworkRouteTable : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<nint> _free;
    internal int Status { get; }
    internal WindowsNetworkRouteTable(nint pointer, int status, Action<nint> free) : base(true)
    { SetHandle(pointer); Status = status; _free = free; }
    protected override bool ReleaseHandle()
    { try { _free(handle); return true; } catch { return false; } }
}

internal sealed class WindowsNetworkNative : IWindowsNetworkNative
{
    internal const int MaximumBufferBytes = 1024 * 1024, MaximumAdapters = 256;
    internal const int MaximumAddresses = 64, MaximumDnsServers = 16, MaximumGateways = 16;
    internal const int RouteRowBytes = 104, RouteTableOffset = 8;
    internal const uint AdapterFlags = 0x186; // All interfaces/gateways; skip only anycast/multicast, never DNS or unicast.
    private const int NoError = 0, InsufficientBuffer = 122, BufferOverflow = 111, NoData = 232, NotFound = 1168;
    private readonly IWindowsNetworkNativeCalls _calls;
    [SupportedOSPlatform("windows")]
    internal WindowsNetworkNative() : this(new Calls()) { }
    internal WindowsNetworkNative(IWindowsNetworkNativeCalls calls) => _calls = calls ?? throw new ArgumentNullException(nameof(calls));

    public WindowsNetworkCapture Capture(string view, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = new List<WindowsNetworkItem>(); var issues = new List<WindowsNetworkIssue>();
        try
        {
            switch (view)
            {
                case "adapters": Adapters(rows, token); break;
                case "routes": Routes(rows, token); break;
                case "connections": Connections(rows, issues, token); break;
                default: throw Fault("network_invalid_view");
            }
            token.ThrowIfCancellationRequested();
            return new(true, "observed", rows.AsReadOnly(), issues.Count > 0, WindowsNetworkOperations.Coverage(view), issues.AsReadOnly());
        }
        catch (NativeFault error)
        {
            return new(false, error.Code, Array.Empty<WindowsNetworkItem>(), false, WindowsNetworkOperations.Coverage(view),
                Array.AsReadOnly(new[] { new WindowsNetworkIssue(view, error.Code, error.NativeError) }));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or InvalidOperationException)
        {
            return new(false, "network_invalid_native_data", Array.Empty<WindowsNetworkItem>(), false,
                WindowsNetworkOperations.Coverage(view), Array.AsReadOnly(new[] { new WindowsNetworkIssue(view, "network_invalid_native_data") }));
        }
    }

    private delegate int BufferCall(nint pointer, ref uint bytes);
    private static OwnedBuffer ReadBuffer(BufferCall read, CancellationToken token)
    {
        var size = 64 * 1024;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var buffer = new OwnedBuffer(size);
            try
            {
                var bytes = checked((uint)size); var status = read(buffer.DangerousGetHandle(), ref bytes);
                token.ThrowIfCancellationRequested();
                if (status == NoError)
                {
                    if (bytes > size) throw Fault("network_buffer_limit");
                    buffer.AdmitLength(bytes);
                    return buffer;
                }
                if (status == NoData) { buffer.Dispose(); return new OwnedBuffer(0); }
                if (status is InsufficientBuffer or BufferOverflow)
                {
                    if (bytes <= size || bytes > MaximumBufferBytes) throw Fault("network_buffer_limit", status);
                    if (attempt != 0) throw Fault("network_changed_during_read", status);
                    size = checked((int)bytes);
                }
                else throw Fault("network_unavailable", status);
            }
            catch { buffer.Dispose(); throw; }
            buffer.Dispose();
        }
        throw Fault("network_changed_during_read");
    }

    private void Adapters(List<WindowsNetworkItem> rows, CancellationToken token)
    {
        using var buffer = ReadBuffer(_calls.Adapters, token);
        if (buffer.Length == 0) return;
        var memory = new Memory(buffer.DangerousGetHandle(), buffer.Length);
        var pointer = memory.Start; var visited = new HashSet<nint>();
        while (pointer != 0)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count == MaximumAdapters || !visited.Add(pointer)) throw Fault("network_adapter_or_cycle_limit");
            var header = memory.Node<Adapter>(pointer);
            var id = memory.Ansi(header.AdapterName, 256, required: true)!;
            var name = memory.Unicode(header.FriendlyName, 256);
            var description = memory.Unicode(header.Description, 512);
            var suffix = memory.Unicode(header.DnsSuffix, 256);
            var unicast = Addresses(memory, header.FirstUnicast, MaximumAddresses, true, token);
            var dns = Addresses(memory, header.FirstDns, MaximumDnsServers, false, token);
            var gateways = Addresses(memory, header.FirstGateway, MaximumGateways, false, token);
            rows.Add(WindowsNetworkOperations.Item("adapter", new { id }, new Dictionary<string, object?>
            {
                ["id"] = id, ["name"] = name, ["description"] = description,
                ["interfaceIndex"] = header.IfIndex, ["ipv6InterfaceIndex"] = header.Ipv6IfIndex,
                ["interfaceLuid"] = header.Luid.ToString("x16"), ["operationalStatusCode"] = header.OperStatus,
                ["interfaceTypeCode"] = header.IfType, ["mtu"] = header.Mtu,
                ["dnsSuffix"] = suffix, ["dnsServers"] = dns, ["gateways"] = gateways, ["unicastAddresses"] = unicast
            }));
            pointer = header.Next;
        }
    }

    private static IReadOnlyList<NetworkAddress> Addresses(Memory memory, nint pointer, int maximum, bool unicast, CancellationToken token)
    {
        var entries = new List<NetworkAddress>(); var visited = new HashSet<nint>();
        while (pointer != 0)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count == maximum || !visited.Add(pointer)) throw Fault("network_address_or_cycle_limit");
            if (unicast)
            {
                var item = memory.Node<Unicast>(pointer);
                var address = memory.Socket(item.Address.Pointer, item.Address.Length);
                if (item.PrefixLength > (address.Family == "ipv4" ? 32 : 128)) throw Fault("network_invalid_native_data");
                entries.Add(new(address.Family, address.Address, item.PrefixLength)); pointer = item.Next;
            }
            else
            {
                var item = memory.Node<AddressNode>(pointer);
                var address = memory.Socket(item.Address.Pointer, item.Address.Length);
                entries.Add(new(address.Family, address.Address, null)); pointer = item.Next;
            }
        }
        return entries.AsReadOnly();
    }

    private void Routes(List<WindowsNetworkItem> rows, CancellationToken token)
    {
        if (Marshal.SizeOf<RouteRow>() != RouteRowBytes || Marshal.OffsetOf<RouteLayout>(nameof(RouteLayout.First)).ToInt32() != RouteTableOffset)
            throw Fault("network_abi_unsupported");
        token.ThrowIfCancellationRequested();
        using var table = _calls.Routes(); // Exactly one native allocation; never a second table retry.
        token.ThrowIfCancellationRequested();
        if (table.Status is NoData or NotFound) return;
        if (table.Status != NoError) throw Fault("network_unavailable", table.Status);
        if (table.IsInvalid) throw Fault("network_invalid_native_data");
        var pointer = table.DangerousGetHandle();
        var count = unchecked((uint)Marshal.ReadInt32(pointer));
        if (count > WindowsNetworkOperations.MaximumRows) throw Fault("network_row_limit");
        // The API owns allocation size. Its admitted count and documented ABI
        // define the range; this is not an independently observed byte length.
        var memory = new Memory(pointer, checked(RouteTableOffset + (int)count * RouteRowBytes));
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var address = pointer + checked(RouteTableOffset + (int)index * RouteRowBytes);
            var row = memory.Read<RouteRow>(address);
            var destination = memory.Socket(address + Marshal.OffsetOf<RouteRow>(nameof(RouteRow.Destination)).ToInt32(), 28);
            var nextHop = memory.Socket(address + Marshal.OffsetOf<RouteRow>(nameof(RouteRow.NextHop)).ToInt32(), 28);
            if (row.Destination.PrefixLength > (destination.Family == "ipv4" ? 32 : 128) || destination.Family != nextHop.Family)
                throw Fault("network_invalid_native_data");
            var prefix = destination.Address + "/" + row.Destination.PrefixLength;
            rows.Add(WindowsNetworkOperations.Item("route", new { row.InterfaceLuid, row.InterfaceIndex, destination.Family, prefix, nextHop = nextHop.Address, row.Protocol },
                new Dictionary<string, object?>
                {
                    ["family"] = destination.Family, ["interfaceIndex"] = row.InterfaceIndex, ["interfaceLuid"] = row.InterfaceLuid.ToString("x16"),
                    ["destinationPrefix"] = prefix, ["nextHop"] = nextHop.Address, ["routeMetricOffset"] = row.Metric,
                    ["protocolCode"] = row.Protocol, ["originCode"] = row.Origin, ["loopback"] = row.Loopback != 0,
                    ["autoconfigured"] = row.Autoconfigure != 0, ["published"] = row.Publish != 0, ["immortal"] = row.Immortal != 0,
                    ["validLifetimeSeconds"] = row.ValidLifetime, ["preferredLifetimeSeconds"] = row.PreferredLifetime, ["ageSeconds"] = row.Age
                }));
        }
    }

    private void Connections(List<WindowsNetworkItem> rows, List<WindowsNetworkIssue> issues, CancellationToken token)
    {
        var successful = 0; var totalRows = 0;
        foreach (var protocol in new[] { "tcp", "udp" })
        foreach (var family in new uint[] { 2, 23 })
        {
            token.ThrowIfCancellationRequested();
            var area = protocol + (family == 2 ? "_ipv4" : "_ipv6");
            try
            {
                using var buffer = ReadBuffer((nint pointer, ref uint size) => protocol == "tcp"
                    ? _calls.Tcp(pointer, ref size, family) : _calls.Udp(pointer, ref size, family), token);
                if (buffer.Length == 0) { successful++; continue; }
                var memory = new Memory(buffer.DangerousGetHandle(), buffer.Length);
                var count = memory.UInt32(memory.Start);
                if (count > WindowsNetworkOperations.MaximumRows || totalRows + count > WindowsNetworkOperations.MaximumRows) throw Fault("network_row_limit");
                totalRows += checked((int)count);
                var rowSize = protocol == "tcp" ? family == 2 ? 24 : 56 : family == 2 ? 12 : 28;
                memory.Check(memory.Start, checked(4 + (int)count * rowSize));
                for (var index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var pointer = memory.Start + checked(4 + (int)index * rowSize);
                    var ipv6 = family == 23;
                    var localAddress = memory.Ip(pointer + (protocol == "tcp" && !ipv6 ? 4 : 0), ipv6,
                        ipv6 ? memory.UInt32(pointer + 16) : 0);
                    var localPort = Port(memory.UInt32(pointer + (ipv6 ? 20 : protocol == "tcp" ? 8 : 4)));
                    string? remoteAddress = null; int? remotePort = null; uint? state = null; uint pid;
                    if (protocol == "tcp")
                    {
                        remoteAddress = memory.Ip(pointer + (ipv6 ? 24 : 12), ipv6, ipv6 ? memory.UInt32(pointer + 40) : 0);
                        remotePort = Port(memory.UInt32(pointer + (ipv6 ? 44 : 16)));
                        state = memory.UInt32(pointer + (ipv6 ? 48 : 0)); pid = memory.UInt32(pointer + (ipv6 ? 52 : 20));
                        if (state is < 1 or > 12 or 2) throw Fault("network_invalid_native_data"); // CONNECTIONS excludes LISTEN.
                    }
                    else pid = memory.UInt32(pointer + (ipv6 ? 24 : 8));
                    var kind = protocol == "tcp" ? "tcp_connection" : "udp_endpoint";
                    var familyName = ipv6 ? "ipv6" : "ipv4";
                    rows.Add(WindowsNetworkOperations.Item(kind, new { family = familyName, localAddress, localPort, remoteAddress, remotePort, pid },
                        new Dictionary<string, object?>
                        {
                            ["family"] = familyName, ["protocol"] = protocol, ["localAddress"] = localAddress, ["localPort"] = localPort,
                            ["remoteAddress"] = remoteAddress, ["remotePort"] = remotePort, ["stateCode"] = state, ["ownerPid"] = pid
                        }));
                }
                successful++;
            }
            catch (NativeFault error) when (error.Code is "network_unavailable" or "network_changed_during_read")
            { issues.Add(new(area, error.Code, error.NativeError)); }
        }
        if (successful == 0) throw Fault("network_unavailable");
    }

    private static int Port(uint value)
    {
        if ((value & 0xffff0000) != 0) throw Fault("network_invalid_native_data");
        return checked((int)((value & 0xff) << 8 | value >> 8));
    }
    private sealed class NativeFault(string code, int? nativeError) : Exception(code)
    { internal string Code { get; } = code; internal int? NativeError { get; } = nativeError; }
    private static NativeFault Fault(string code, int? nativeError = null) => new(code, nativeError);
    private sealed class OwnedBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal int Length { get; private set; }
        internal OwnedBuffer(int length) : base(true)
        {
            if (length is < 0 or > MaximumBufferBytes) throw Fault("network_buffer_limit");
            Length = length; SetHandle(length == 0 ? 0 : Marshal.AllocHGlobal(length));
            if (length != 0) Marshal.Copy(new byte[length], 0, handle, length);
        }
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
        internal void AdmitLength(uint bytes)
        { if (bytes < 4 || bytes > Length) throw Fault("network_invalid_native_data"); Length = checked((int)bytes); }
    }
    private sealed class Memory(nint start, int length)
    {
        internal nint Start { get; } = start;
        internal void Check(nint pointer, int count)
        {
            if (pointer == 0 || count < 0 || count > length || (nuint)pointer < (nuint)Start ||
                (nuint)pointer - (nuint)Start > (nuint)(length - count)) throw Fault("network_invalid_native_pointer");
        }
        internal T Read<T>(nint pointer) where T : struct
        { Check(pointer, Marshal.SizeOf<T>()); return Marshal.PtrToStructure<T>(pointer); }
        internal T Node<T>(nint pointer) where T : struct
        { var size = UInt32(pointer); if (size < Marshal.SizeOf<T>() || size > MaximumBufferBytes) throw Fault("network_abi_unsupported"); Check(pointer, (int)size); return Read<T>(pointer); }
        internal uint UInt32(nint pointer) { Check(pointer, 4); return unchecked((uint)Marshal.ReadInt32(pointer)); }
        internal byte[] Bytes(nint pointer, int count) { Check(pointer, count); var bytes = new byte[count]; Marshal.Copy(pointer, bytes, 0, count); return bytes; }
        internal string Ip(nint pointer, bool ipv6, uint scope) => ipv6 ? new IPAddress(Bytes(pointer, 16), scope).ToString() : new IPAddress(Bytes(pointer, 4)).ToString();
        internal NetworkAddress Socket(nint pointer, int size)
        {
            if (size is < 2 or > 128) throw Fault("network_invalid_native_data"); Check(pointer, size);
            var family = unchecked((ushort)Marshal.ReadInt16(pointer));
            return family switch
            {
                2 when size >= 16 => new("ipv4", Ip(pointer + 4, false, 0), null),
                23 when size >= 28 => new("ipv6", Ip(pointer + 8, true, UInt32(pointer + 24)), null),
                _ => throw Fault("network_address_family_unsupported")
            };
        }
        internal string? Unicode(nint pointer, int maximum) => Text(pointer, maximum, true, false);
        internal string? Ansi(nint pointer, int maximum, bool required) => Text(pointer, maximum, false, required);
        private string? Text(nint pointer, int maximum, bool unicode, bool required)
        {
            if (pointer == 0) { if (required) throw Fault("network_invalid_native_data"); return null; }
            var chars = new char[maximum];
            for (var index = 0; index <= maximum; index++)
            {
                var at = pointer + checked(index * (unicode ? 2 : 1)); Check(at, unicode ? 2 : 1);
                var value = unicode ? unchecked((ushort)Marshal.ReadInt16(at)) : Marshal.ReadByte(at);
                if (value == 0)
                {
                    if (required && index == 0) throw Fault("network_invalid_native_data");
                    var text = new string(chars, 0, index);
                    try { _ = new UTF8Encoding(false, true).GetByteCount(text); } catch (EncoderFallbackException) { throw Fault("network_invalid_native_data"); }
                    return text;
                }
                if (index == maximum || char.IsControl((char)value) || !unicode && value > 127) throw Fault("network_field_limit");
                chars[index] = (char)value;
            }
            throw Fault("network_field_limit");
        }
    }
    private sealed record NetworkAddress(string Family, string Address, int? PrefixLength);
    [StructLayout(LayoutKind.Sequential)] internal struct SocketRef { public nint Pointer; public int Length; }
    [StructLayout(LayoutKind.Sequential)] internal struct AddressNode { public uint Length, Reserved; public nint Next; public SocketRef Address; }
    [StructLayout(LayoutKind.Sequential)] internal struct Unicast
    {
        public uint Length, Flags; public nint Next; public SocketRef Address;
        public uint PrefixOrigin, SuffixOrigin, DadState, ValidLifetime, PreferredLifetime, LeaseLifetime; public byte PrefixLength;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Adapter
    {
        public uint Length, IfIndex; public nint Next, AdapterName, FirstUnicast, FirstAnycast, FirstMulticast, FirstDns, DnsSuffix, Description, FriendlyName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[]? PhysicalAddress;
        public uint PhysicalAddressLength, Flags, Mtu, IfType, OperStatus, Ipv6IfIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public uint[]? ZoneIndices;
        public nint FirstPrefix; public ulong TransmitLinkSpeed, ReceiveLinkSpeed; public nint FirstWins, FirstGateway;
        public uint Ipv4Metric, Ipv6Metric; public ulong Luid;
    }
    [StructLayout(LayoutKind.Explicit, Size = 28)] internal struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Word0;
        [FieldOffset(8)] public uint Word1;
        [FieldOffset(12)] public uint Word2;
        [FieldOffset(16)] public uint Word3;
        [FieldOffset(20)] public uint Word4;
        [FieldOffset(24)] public uint Word5;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Prefix { public SockaddrInet Address; public byte PrefixLength; }
    [StructLayout(LayoutKind.Sequential)] internal struct RouteRow
    {
        public ulong InterfaceLuid; public uint InterfaceIndex; public Prefix Destination; public SockaddrInet NextHop;
        public byte SitePrefixLength; public uint ValidLifetime, PreferredLifetime, Metric, Protocol;
        public byte Loopback, Autoconfigure, Publish, Immortal; public uint Age, Origin;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RouteLayout { public uint Count; public RouteRow First; }

    // Windows SDK ABI: MIB_IPFORWARD_TABLE2 explicitly permits alignment padding.
    // Use computed layout plus asserted row/first-entry offsets, not count+4.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_ipforward_table2

    [SupportedOSPlatform("windows")]
    private sealed class Calls : IWindowsNetworkNativeCalls
    {
        public int Adapters(nint buffer, ref uint bytes) => GetAdaptersAddresses(0, AdapterFlags, 0, buffer, ref bytes);
        public int Tcp(nint buffer, ref uint bytes, uint family) => GetExtendedTcpTable(buffer, ref bytes, false, family, 4 /* OWNER_PID_CONNECTIONS */, 0);
        public int Udp(nint buffer, ref uint bytes, uint family) => GetExtendedUdpTable(buffer, ref bytes, false, family, 1 /* OWNER_PID */, 0);
        public WindowsNetworkRouteTable Routes()
        {
            nint pointer = 0;
            try { var status = GetIpForwardTable2(0, out pointer); return new(pointer, status, FreeMibTable); }
            catch { if (pointer != 0) FreeMibTable(pointer); throw; }
        }
        [DllImport("iphlpapi.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetAdaptersAddresses(uint family, uint flags, nint reserved, nint buffer, ref uint bytes);
        [DllImport("iphlpapi.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetExtendedTcpTable(nint buffer, ref uint bytes, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetExtendedUdpTable(nint buffer, ref uint bytes, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetIpForwardTable2(ushort family, out nint table);
        [DllImport("iphlpapi.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void FreeMibTable(nint memory);
    }
}
