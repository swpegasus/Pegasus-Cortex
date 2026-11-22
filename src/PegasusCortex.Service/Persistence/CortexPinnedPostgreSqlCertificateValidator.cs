using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pegasus.Cortex.Service.Configuration;

namespace Pegasus.Cortex.Service.Persistence;

/// <summary>
/// Builds the single-host PostgreSQL validation callback used by the lightweight
/// loopback pinning policy. The callback replaces platform revocation lookup with
/// a protected SPKI pin while retaining private-CA, lifetime, server-purpose, and
/// SAN-only host validation.
/// </summary>
internal static class CortexPinnedPostgreSqlCertificateValidator
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    internal static RemoteCertificateValidationCallback Create(
        string rootCertificatePath,
        string expectedHost,
        string expectedSpkiSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootCertificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSpkiSha256);

        try
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(rootCertificatePath);
            if (roots.Count != 1)
            {
                DisposeAll(roots);
                throw new CryptographicException(
                    "Pinned PostgreSQL validation requires exactly one root certificate.");
            }

            byte[] rootCertificate;
            using (var root = roots[0])
            {
                RequireRootCertificate(root);
                rootCertificate = root.Export(X509ContentType.Cert);
            }
            roots.Clear();

            var expectedPin = Convert.FromHexString(expectedSpkiSha256);
            return (_, certificate, presentedChain, errors) => Validate(
                certificate,
                presentedChain,
                errors,
                rootCertificate,
                expectedPin,
                expectedHost);
        }
        catch (CortexPostgreSqlConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (!Fatal(exception))
        {
            throw new CortexPostgreSqlConfigurationException(
                "cortex_postgresql_pinned_validator_invalid",
                "cortex.postgresql.tls.pinned.configure",
                exception.GetType().FullName);
        }
    }

    private static bool Validate(
        X509Certificate? certificate,
        X509Chain? presentedChain,
        SslPolicyErrors errors,
        byte[] rootCertificate,
        byte[] expectedPin,
        string expectedHost)
    {
        if (certificate is null ||
            (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        try
        {
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            if (leaf.HasPrivateKey ||
                IsCertificateAuthority(leaf) ||
                !HasServerAuthenticationPurpose(leaf) ||
                !leaf.MatchesHostname(
                    expectedHost,
                    allowWildcards: false,
                    allowCommonName: false))
            {
                return false;
            }

            var actualPin = SHA256.HashData(leaf.PublicKey.ExportSubjectPublicKeyInfo());
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actualPin, expectedPin))
                    return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualPin);
            }

            using var root = X509CertificateLoader.LoadCertificate(rootCertificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));

            if (presentedChain is not null)
            {
                foreach (var element in presentedChain.ChainElements)
                {
                    var candidate = element.Certificate;
                    if (!candidate.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span) &&
                        !candidate.RawDataMemory.Span.SequenceEqual(root.RawDataMemory.Span))
                    {
                        chain.ChainPolicy.ExtraStore.Add(candidate);
                    }
                }
            }

            if (!chain.Build(leaf) || chain.ChainElements.Count < 2)
                return false;

            var anchor = chain.ChainElements[^1].Certificate;
            return anchor.RawDataMemory.Span.SequenceEqual(root.RawDataMemory.Span);
        }
        catch (Exception exception) when (!Fatal(exception))
        {
            // Certificate input is untrusted. Any parse, chain, host, or crypto
            // failure rejects the connection without exposing certificate data.
            return false;
        }
    }

    private static void RequireRootCertificate(X509Certificate2 root)
    {
        if (root.HasPrivateKey ||
            !IsCertificateAuthority(root) ||
            !root.SubjectName.RawData.AsSpan().SequenceEqual(root.IssuerName.RawData))
        {
            throw new CryptographicException(
                "Pinned PostgreSQL trust input is not a self-issued public CA certificate.");
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (!chain.Build(root))
            throw new CryptographicException("Pinned PostgreSQL root signature is invalid.");
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate) =>
        certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Any(extension => extension.CertificateAuthority);

    private static bool HasServerAuthenticationPurpose(X509Certificate2 certificate) =>
        certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages
                .OfType<Oid>()
                .Any(oid => string.Equals(
                    oid.Value,
                    ServerAuthenticationOid,
                    StringComparison.Ordinal)));

    private static void DisposeAll(X509Certificate2Collection certificates)
    {
        foreach (X509Certificate2 certificate in certificates)
            certificate.Dispose();
        certificates.Clear();
    }

    private static bool Fatal(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException;
}
