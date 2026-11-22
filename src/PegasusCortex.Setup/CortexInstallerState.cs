using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

internal enum CortexTrustRecommendation
{
    Current,
    Missing,
    Outdated,
    Expiring
}

internal sealed record CortexCertificateWindow(
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

internal sealed record CortexInstallerStateV1(
    bool CodexRegister,
    bool CodexGuidance,
    CortexTrustRecommendation TrustRecommendation)
{
    internal const int SuccessMarker = 128;
    internal const int CodexRegisterBit = 1;
    internal const int CodexGuidanceBit = 2;
    internal const int TrustRecommendedBit = 4;
    internal const int TrustMissingBit = 8;
    internal const int TrustOutdatedBit = 16;
    internal const int TrustExpiringBit = 32;

    internal int ExitCode
    {
        get
        {
            var flags = (CodexRegister ? CodexRegisterBit : 0) |
                        (CodexGuidance ? CodexGuidanceBit : 0);
            flags |= TrustRecommendation switch
            {
                CortexTrustRecommendation.Missing => TrustRecommendedBit | TrustMissingBit,
                CortexTrustRecommendation.Outdated => TrustRecommendedBit | TrustOutdatedBit,
                CortexTrustRecommendation.Expiring => TrustRecommendedBit | TrustExpiringBit,
                _ => 0
            };
            return SuccessMarker | flags;
        }
    }
}

internal static class CortexInstallerState
{
    private const string TrustRegistryPath = @"Software\Pegasus\Trust\ProdAlpha";

    internal static CortexInstallerStateV1 Detect(CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var previous = CortexCodexIntegration.ReadPrevious(layout);
        var trust = DetectTrustRecommendation(DateTimeOffset.UtcNow);
        return new(previous?.Register == true, previous?.Guidance == true, trust);
    }

    internal static CortexTrustRecommendation EvaluateTrust(
        IReadOnlyCollection<CortexCertificateWindow> roots,
        IReadOnlyCollection<CortexCertificateWindow> publishers,
        bool priorPegasusTrustExists,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(publishers);
        var expectedRoot = Matches(roots, CortexProductIdentity.ProdAlphaRootThumbprint);
        var expectedPublisher = Matches(publishers, CortexProductIdentity.ProdAlphaPublisherThumbprint);
        if (expectedRoot.Count != 1 || expectedPublisher.Count != 1)
            return priorPegasusTrustExists || expectedRoot.Count > 0 || expectedPublisher.Count > 0
                ? CortexTrustRecommendation.Outdated
                : CortexTrustRecommendation.Missing;

        foreach (var certificate in expectedRoot.Concat(expectedPublisher))
        {
            if (now < certificate.NotBefore || now >= certificate.NotAfter)
                return CortexTrustRecommendation.Outdated;
            var lifetime = certificate.NotAfter - certificate.NotBefore;
            if (lifetime <= TimeSpan.Zero)
                return CortexTrustRecommendation.Outdated;
            var remaining = certificate.NotAfter - now;
            if (remaining.TotalSeconds / lifetime.TotalSeconds < 0.20d)
                return CortexTrustRecommendation.Expiring;
        }
        return CortexTrustRecommendation.Current;
    }

    private static CortexTrustRecommendation DetectTrustRecommendation(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Pegasus machine trust detection requires Windows.");
        var roots = ReadStore(StoreName.Root);
        var publishers = ReadStore(StoreName.TrustedPublisher);
        using var trust = Registry.LocalMachine.OpenSubKey(TrustRegistryPath, writable: false);
        var priorTrust = trust?.GetSubKeyNames().Length > 0;
        return EvaluateTrust(roots, publishers, priorTrust, now);
    }

    private static List<CortexCertificateWindow> ReadStore(StoreName name)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Select(certificate => new CortexCertificateWindow(
                Normalize(certificate.Thumbprint),
                new DateTimeOffset(certificate.NotBefore),
                new DateTimeOffset(certificate.NotAfter)))
            .ToList();
    }

    private static List<CortexCertificateWindow> Matches(
        IEnumerable<CortexCertificateWindow> certificates,
        string thumbprint)
    {
        var expected = Normalize(thumbprint);
        return certificates.Where(certificate =>
                string.Equals(Normalize(certificate.Thumbprint), expected, StringComparison.Ordinal))
            .ToList();
    }

    private static string Normalize(string value) =>
        new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();
}
