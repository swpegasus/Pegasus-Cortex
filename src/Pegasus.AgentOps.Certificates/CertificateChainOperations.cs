using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pegasus.AgentOps.Certificates;

public enum CertificateTrustSelection { System, CustomRoots }
public enum CertificateRevocationSelection { NotChecked, Offline }
public sealed record CertificateChainRequest(CertificateTrustSelection Trust, CertificateRevocationSelection Revocation,
    DateTimeOffset VerificationTime, IReadOnlyList<byte[]> CustomRootCertificates);
public sealed record CertificateChainResult(bool Trusted, string TrustSelection, string Revocation,
    IReadOnlyList<string> Status);

/// <summary>Offline-only verification. A successful custom-root check never installs that root or claims OS trust.</summary>
public static class CertificateChainOperations
{
    public static CertificateChainResult VerifyCodeSigning(X509Certificate2 certificate, string expectedCertificateId,
        CertificateChainRequest request) =>
        Verify(certificate, expectedCertificateId, request, serverAuthentication: false);

    public static CertificateChainResult VerifyServerAuthentication(X509Certificate2 certificate, string expectedCertificateId,
        CertificateChainRequest request) =>
        Verify(certificate, expectedCertificateId, request, serverAuthentication: true);

    private static CertificateChainResult Verify(X509Certificate2 certificate, string expectedCertificateId,
        CertificateChainRequest request, bool serverAuthentication)
    {
        ArgumentNullException.ThrowIfNull(certificate); ArgumentNullException.ThrowIfNull(request);
        CertificateMaterial.RequireIdentity(certificate, expectedCertificateId);
        if (!Enum.IsDefined(request.Trust) || !Enum.IsDefined(request.Revocation) ||
            request.VerificationTime.Offset != TimeSpan.Zero || request.CustomRootCertificates is null ||
            request.CustomRootCertificates.Count > 8 ||
            request.CustomRootCertificates.Sum(value => (long)(value?.Length ?? 0)) > CertificateMaterial.MaximumCertificateBytes ||
            (request.Trust == CertificateTrustSelection.System ? request.CustomRootCertificates.Count != 0 : request.CustomRootCertificates.Count == 0))
            throw new CertificateOperationException("chain_policy_invalid");
        try
        {
            if (serverAuthentication) ServerCertificateMaterial.Require(certificate, expectedCertificateId, request.VerificationTime, privateKey: false);
            else CertificateMaterial.RequireSigning(certificate, request.VerificationTime, privateKey: false);
        }
        catch (CertificateOperationException error)
        { return new(false, request.Trust.ToString(), "not_checked", [error.Code]); }
        using var chain = new X509Chain();
        var roots = new List<X509Certificate2>();
        try
        {
            foreach (var bytes in request.CustomRootCertificates)
            {
                if (bytes is null) throw new CertificateOperationException("chain_policy_invalid");
                var root = CertificateExchange.ImportPublic(bytes, PublicCertificateFormat.Der);
                roots.Add(root);
                if (!CertificateOperations.Inspect(root).IsAuthority) throw new CertificateOperationException("authority_certificate_required");
                chain.ChainPolicy.CustomTrustStore.Add(root);
            }
            chain.ChainPolicy.TrustMode = request.Trust == CertificateTrustSelection.CustomRoots ? X509ChainTrustMode.CustomRootTrust : X509ChainTrustMode.System;
            chain.ChainPolicy.VerificationTime = request.VerificationTime.UtcDateTime;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(serverAuthentication ? CertificateOperations.ServerAuthenticationOid : CertificateOperations.CodeSigningOid));
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.Zero;
            chain.ChainPolicy.RevocationMode = request.Revocation == CertificateRevocationSelection.Offline ? X509RevocationMode.Offline : X509RevocationMode.NoCheck;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            var trusted = chain.Build(certificate);
            var statuses = chain.ChainStatus.Select(value => value.Status.ToString()).Distinct(StringComparer.Ordinal).Take(32).ToArray();
            var indeterminate = chain.ChainStatus.Any(value => (value.Status & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0);
            return new(trusted, request.Trust.ToString(), DescribeRevocation(request.Revocation,
                Math.Max(0, chain.ChainElements.Count - 1), indeterminate), statuses);
        }
        catch (CryptographicException) { throw new CertificateOperationException("chain_verification_failed"); }
        finally { foreach (var root in roots) root.Dispose(); }
    }

    // ExcludeRoot can leave a one-element trusted chain with no eligible revocation target.
    internal static string DescribeRevocation(CertificateRevocationSelection selection, int eligibleCertificates, bool indeterminate) =>
        selection == CertificateRevocationSelection.NotChecked || eligibleCertificates == 0
            ? "not_checked" : indeterminate ? "offline_indeterminate" : "offline_checked";
}
