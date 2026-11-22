using System.Net;

namespace Pegasus.SharedMcp;

/// <summary>Checks the actual TCP peer. Proxy headers and caller-provided identity are never trusted.</summary>
public sealed class PrivateNetworkPolicy
{
    private readonly Cidr[] allowed;
    private static readonly Cidr[] PrivateRanges = [Cidr.Parse("127.0.0.0/8"), Cidr.Parse("::1/128"),
        Cidr.Parse("10.0.0.0/8"), Cidr.Parse("172.16.0.0/12"), Cidr.Parse("192.168.0.0/16"),
        Cidr.Parse("100.64.0.0/10"), Cidr.Parse("fc00::/7")];

    public PrivateNetworkPolicy(IEnumerable<string> allowedPeerCidrs)
    {
        allowed = allowedPeerCidrs.Select(Cidr.Parse).ToArray();
        if (allowed.Length is < 1 or > 64 || allowed.Any(c => !PrivateRanges.Any(p => p.Contains(c))))
            throw new ArgumentException("Allow only explicit loopback, RFC1918, private IPv6 or CGNAT/VPN ranges; public and wildcard networks are refused.");
    }

    public bool Allows(IPAddress? peer) => peer is not null && allowed.Any(c => c.Contains(Normalize(peer)));
    public static bool IsPrivateAddress(IPAddress address) => PrivateRanges.Any(c => c.Contains(Normalize(address)));
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed record Cidr(byte[] Network, int Prefix)
    {
        public static Cidr Parse(string value)
        {
            var parts = value.Split('/');
            if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var address) || address.IsIPv4MappedToIPv6)
                throw new ArgumentException("An explicit IP address or CIDR is required.");
            var bytes = address.GetAddressBytes();
            var prefix = bytes.Length * 8;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > bytes.Length * 8))
                throw new ArgumentException("Invalid CIDR prefix.");
            var network = bytes.ToArray();
            for (var bit = prefix; bit < bytes.Length * 8; bit++) network[bit / 8] &= (byte)~(1 << (7 - bit % 8));
            if (!network.SequenceEqual(bytes)) throw new ArgumentException("CIDR must use its canonical network address.");
            return new(network, prefix);
        }
        public bool Contains(IPAddress address) => Contains(new Cidr(address.GetAddressBytes(), address.GetAddressBytes().Length * 8));
        public bool Contains(Cidr other)
        {
            if (Network.Length != other.Network.Length || Prefix > other.Prefix) return false;
            for (var bit = 0; bit < Prefix; bit++)
                if ((Network[bit / 8] & (1 << (7 - bit % 8))) != (other.Network[bit / 8] & (1 << (7 - bit % 8)))) return false;
            return true;
        }
    }
}
