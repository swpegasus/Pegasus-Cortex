using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Pegasus.SharedMcp;

public sealed record McpTlsQualificationResult(bool Success, string Code)
{
    public bool ValidUnderSelectedCa => Success;
    /// <summary>Read-only local process/system chain observation, never proof that Codex or another environment trusts the CA.</summary>
    public string ClientTrustObservation { get; init; } = "not_observed";
}

public sealed class McpTlsQualificationException(string code) : Exception("HTTPS loopback qualification failed: " + code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Read-only HTTPS qualification against one selected custom root CA. It changes no machine trust,
/// exports no keys, performs no DNS lookup, and never treats a correlation label as identity.
/// </summary>
public static class McpTlsQualification
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    public static async Task<McpTlsQualificationResult> ProbeAsync(IPEndPoint loopbackEndpoint, string serverName,
        X509Certificate2 selectedCa, X509Certificate2 expectedServerCertificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loopbackEndpoint);
        ArgumentNullException.ThrowIfNull(selectedCa);
        ArgumentNullException.ThrowIfNull(expectedServerCertificate);
        if (!IPAddress.IsLoopback(loopbackEndpoint.Address) || loopbackEndpoint.Port is < 1 or > 65535)
            throw new ArgumentException("Certificate qualification connects only to an explicit loopback endpoint.");
        RequireServerName(serverName);
        var rootIssue = RootIssue(selectedCa);
        if (rootIssue is not null) return new(false, rootIssue);
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            VerificationFlags = X509VerificationFlags.NoFlag,
            DisableCertificateDownloads = true,
            // Offline qualification proves identity/chain/usage/time, not revocation status.
            RevocationMode = X509RevocationMode.NoCheck,
            VerificationTime = DateTime.Now
        };
        policy.CustomTrustStore.Add(selectedCa);
        policy.ApplicationPolicy.Add(new Oid(ServerAuth));
        var issue = "tls_handshake_failed";
        var validated = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient(loopbackEndpoint.AddressFamily);
        try
        {
            await tcp.ConnectAsync(loopbackEndpoint.Address, loopbackEndpoint.Port, deadline.Token);
            using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                CertificateChainPolicy = policy,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    try
                    {
                        if (certificate is null) { issue = "certificate_missing"; return false; }
                        using var leaf = new X509Certificate2(certificate);
                        var leafIssue = LeafIssue(leaf, serverName);
                        if (leafIssue is not null) { issue = leafIssue; return false; }
                        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                        { issue = "certificate_name_mismatch"; return false; }
                        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                        { issue = "certificate_missing"; return false; }
                        if (!leaf.RawData.AsSpan().SequenceEqual(expectedServerCertificate.RawData))
                        { issue = "certificate_unexpected_leaf"; return false; }
                        if (errors != SslPolicyErrors.None || chain is null || chain.ChainElements.Count == 0 ||
                            chain.ChainStatus.Any(status => status.Status != X509ChainStatusFlags.NoError) ||
                            !chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(selectedCa.RawData))
                        { issue = "certificate_chain_rejected"; return false; }
                        validated = true;
                        return true;
                    }
                    catch (Exception error) when (error is CryptographicException or ArgumentException or InvalidOperationException)
                    { issue = "certificate_invalid"; return false; }
                }
            }, deadline.Token);
            if (!validated || !tls.IsAuthenticated || !tls.IsEncrypted) return new(false, "tls_validation_incomplete");
            var authority = IPAddress.TryParse(serverName, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                ? "[" + serverName + "]" : serverName;
            var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {authority}:{loopbackEndpoint.Port}\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(request, deadline.Token);
            await tls.FlushAsync(deadline.Token);
            var firstLine = new List<byte>(128);
            var one = new byte[1];
            while (firstLine.Count < 256)
            {
                if (await tls.ReadAsync(one, deadline.Token) == 0) break;
                firstLine.Add(one[0]);
                if (one[0] == (byte)'\n') break;
            }
            var status = Encoding.ASCII.GetString(firstLine.ToArray());
            return status.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && status.Length >= 12 &&
                int.TryParse(status.AsSpan(9, 3), out var code) && code is >= 100 and <= 599
                ? new(true, "https_loopback_qualified") { ClientTrustObservation = ObserveSystemTrust(expectedServerCertificate, selectedCa) }
                : new(false, "https_response_invalid");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "https_probe_timeout"); }
        catch (AuthenticationException) { return new(false, issue); }
        catch (Exception error) when (error is IOException or SocketException or CryptographicException)
        { return new(false, validated ? "https_transport_failed" : issue); }
    }

    internal static void RequireServerCertificate(X509Certificate2 certificate, string serverName)
    {
        RequireServerName(serverName);
        if (!certificate.HasPrivateKey) throw new ArgumentException("The configured TLS server certificate needs an accessible private key.");
        var issue = LeafIssue(certificate, serverName);
        if (issue is not null) throw new ArgumentException("The configured TLS server certificate was rejected: " + issue);
    }

    internal static void RequireServerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 253 || name.Any(c => c > 127 || char.IsControl(c)) ||
            name.IndexOfAny(['/', '\\', '%', '*', '[', ']']) >= 0 || Uri.CheckHostName(name) == UriHostNameType.Unknown)
            throw new ArgumentException("TLS serverName must be one bare ASCII DNS name or IP address, without a port or wildcard.");
    }

    private static string? LeafIssue(X509Certificate2 certificate, string serverName)
    {
        if (!Current(certificate)) return "certificate_expired_or_not_yet_valid";
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => x.CertificateAuthority)) return "certificate_leaf_required";
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (eku.Length != 1 || !eku[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ServerAuth))
            return "certificate_server_auth_required";
        if (certificate.Extensions.Count(x => x.Oid?.Value == "2.5.29.17") != 1 ||
            !certificate.MatchesHostname(serverName, allowWildcards: false, allowCommonName: false)) return "certificate_name_mismatch";
        return null;
    }

    private static string? RootIssue(X509Certificate2 certificate)
    {
        if (!Current(certificate)) return "certificate_ca_expired_or_not_yet_valid";
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().ToArray();
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().ToArray();
        if (constraints.Length != 1 || !constraints[0].CertificateAuthority || usage.Length != 1 ||
            (usage[0].KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0 ||
            !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            return "certificate_ca_root_required";
        return null;
    }
    private static bool Current(X509Certificate2 certificate) =>
        DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime();

    private static string ObserveSystemTrust(X509Certificate2 leaf, X509Certificate2 selectedCa)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuth));
            // ExtraStore helps chain discovery but grants no trust. CustomTrustStore is deliberately unused here.
            chain.ChainPolicy.ExtraStore.Add(selectedCa);
            return chain.Build(leaf) ? "trusted_by_this_process_system_policy" :
                chain.ChainStatus.Any(s => (s.Status & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
                    ? "selected_ca_not_normally_trusted_here" : "system_policy_rejected";
        }
        catch (CryptographicException) { return "system_trust_observation_unavailable"; }
    }
}
