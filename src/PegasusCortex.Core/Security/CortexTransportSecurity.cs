using System.Net;
using Pegasus.Shared.Diagnostics;
using Pegasus.Shared.Security;

namespace Pegasus.Cortex.Core.Security;

public static class CortexTransportSecurity
{
    public const string OutcomeContentType = "application/vnd.pegasus.agent-outcome+json;version=1";

    public static readonly LocalBootstrapDescriptor CertificateBootstrap = new(
        EnabledWhenCertificateUnavailable: true,
        BindAddresses: ["127.0.0.1", "::1"],
        AllowedOperations: ["status", "csr", "complete"],
        ExposesConfidentialData: false,
        DisableAfterCertificateIssuance: true);

    public static CommunicationSecurityDescriptor CreateServiceDescriptor(
        string localCertificateReference,
        string peerTrustReference,
        bool intermediaryCarriesConfidentialPayload) =>
        new(
            Transport: ConfidentialCommunicationPolicy.RequiredTransport,
            MinimumTlsVersion: ConfidentialCommunicationPolicy.MinimumTlsVersion,
            MutualAuthenticationRequired: true,
            LocalCertificateReference: localCertificateReference,
            PeerTrustReference: peerTrustReference,
            RecipientEncryptionRequired: intermediaryCarriesConfidentialPayload,
            EnvelopeContract: intermediaryCarriesConfidentialPayload
                ? ConfidentialCommunicationPolicy.RequiredEnvelopeContract
                : null,
            KeyRotationRequired: true,
            LocalBootstrap: CertificateBootstrap);

    public static ConfidentialCommunicationEvaluation EvaluateServiceEndpoint(
        Uri endpoint,
        CommunicationSecurityDescriptor descriptor,
        bool intermediaryCarriesConfidentialPayload,
        bool certificatesAvailable = true) =>
        ConfidentialCommunicationPolicy.EvaluateEndpoint(
            endpoint,
            new CommunicationSecurityContext(
                CommunicationPeerKind.PegasusService,
                CommunicationPurpose.ApplicationTraffic,
                intermediaryCarriesConfidentialPayload,
                certificatesAvailable,
                BootstrapWindowEnabled: false),
            descriptor);

    public static ConfidentialCommunicationEvaluation EvaluateBootstrapRequest(
        IPAddress? remoteAddress,
        bool forwardedHeadersPresent,
        bool certificatesAvailable,
        bool bootstrapWindowEnabled,
        string operation,
        bool carriesConfidentialData) =>
        ConfidentialCommunicationPolicy.EvaluateBootstrapRequest(
            remoteAddress,
            forwardedHeadersPresent,
            certificatesAvailable,
            bootstrapWindowEnabled,
            operation,
            carriesConfidentialData,
            CertificateBootstrap);
}

public interface ICertificateAvailability
{
    bool HasUsableCertificate { get; }
}

/// <summary>
/// Tracks only the explicitly opened, unauthenticated loopback bootstrap window.
/// Hosts must still apply the shared remote-address and operation policy per request.
/// </summary>
public sealed class CortexBootstrapWindow(ICertificateAvailability certificateAvailability)
{
    private int _explicitlyEnabled;

    public bool IsOpen =>
        Volatile.Read(ref _explicitlyEnabled) == 1 && !certificateAvailability.HasUsableCertificate;

    public bool TryOpen()
    {
        if (certificateAvailability.HasUsableCertificate)
            return false;

        Interlocked.Exchange(ref _explicitlyEnabled, 1);
        return IsOpen;
    }

    public void Close() => Interlocked.Exchange(ref _explicitlyEnabled, 0);

    public void CertificateIssued() => Close();
}

/// <summary>
/// Cortex cannot issue identity certificates. Pegasus Server/Nexus integration must provide
/// an approved issuer adapter; until then bootstrap fails closed without leaking request data.
/// </summary>
public sealed class CortexUnavailableCertificateBootstrapIssuer : ICertificateBootstrapIssuer
{
    public ValueTask<CertificateBootstrapResult> RequestAsync(
        CertificateBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new CertificateBootstrapResult(
            Accepted: false,
            State: "issuer-unavailable",
            CertificateChainPem: null,
            Diagnostic: new PegasusDiagnostic(
                "certificate_bootstrap_issuer_unavailable",
                "Certificate bootstrap is unavailable until an approved Pegasus Server issuer adapter is configured.",
                DiagnosticSeverity.Critical,
                "cortex.certificate-bootstrap.request",
                retryable: false,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["serviceId"] = request.ServiceId,
                    ["correlationId"] = request.CorrelationId
                })));
    }
}
