using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Pegasus.AgentOps.Certificates;

/// <summary>Immutable, bounded DNS/IP-only SAN selection. It never resolves a name or opens a connection.</summary>
public sealed class ServerCertificateNames
{
    public const int MaximumNames = 16;
    public IReadOnlyList<string> DnsNames { get; }
    public IReadOnlyList<string> IpAddresses { get; }

    public ServerCertificateNames(IEnumerable<string> dnsNames, IEnumerable<string> ipAddresses)
    {
        ArgumentNullException.ThrowIfNull(dnsNames);
        ArgumentNullException.ThrowIfNull(ipAddresses);
        var dns = new List<string>();
        var ips = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in dnsNames)
        {
            if (dns.Count + ips.Count >= MaximumNames) throw Invalid("server_names_bound");
            if (string.IsNullOrEmpty(value) || value.Length > 253 || value.Any(character => character > 127) ||
                IPAddress.TryParse(value, out _)) throw Invalid();
            var canonical = value.ToLowerInvariant();
            foreach (var label in canonical.Split('.'))
                if (label.Length is < 1 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) ||
                    !char.IsAsciiLetterOrDigit(label[^1]) || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                    throw Invalid();
            if (!seen.Add("dns:" + canonical)) throw Invalid("server_names_duplicate");
            dns.Add(canonical);
        }
        foreach (var value in ipAddresses)
        {
            if (dns.Count + ips.Count >= MaximumNames) throw Invalid("server_names_bound");
            if (string.IsNullOrEmpty(value) || value.Length > 45 || value.Contains('%') ||
                !IPAddress.TryParse(value, out var address) ||
                address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
                address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ||
                address.ToString() != value) throw Invalid();
            if (!seen.Add("ip:" + value)) throw Invalid("server_names_duplicate");
            ips.Add(value);
        }
        if (dns.Count + ips.Count == 0) throw Invalid();
        DnsNames = Array.AsReadOnly(dns.Order(StringComparer.Ordinal).ToArray());
        IpAddresses = Array.AsReadOnly(ips.Order(StringComparer.Ordinal).ToArray());
    }

    internal void AddTo(CertificateRequest request)
    {
        var names = new SubjectAlternativeNameBuilder();
        foreach (var dns in DnsNames) names.AddDnsName(dns);
        foreach (var ip in IpAddresses) names.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(names.Build());
    }

    public static ServerCertificateNames FromCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var extensions = certificate.Extensions.Cast<X509Extension>().Where(value => value.Oid?.Value == "2.5.29.17").Take(2).ToArray();
        if (extensions.Length != 1) throw Invalid();
        try
        {
            var reader = new AsnReader(extensions[0].RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var dns = new List<string>();
            var ips = new List<string>();
            while (sequence.HasData)
            {
                if (dns.Count + ips.Count >= MaximumNames) throw Invalid("server_names_bound");
                var tag = sequence.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    dns.Add(sequence.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                {
                    var encoded = sequence.ReadOctetString(tag);
                    if (encoded.Length is not (4 or 16)) throw Invalid();
                    ips.Add(new IPAddress(encoded).ToString());
                }
                else throw Invalid();
            }
            reader.ThrowIfNotEmpty();
            return new(dns, ips);
        }
        catch (AsnContentException error) { throw new CertificateOperationException("server_names_invalid", error); }
    }

    private static CertificateOperationException Invalid(string code = "server_names_invalid") => new(code);
}
