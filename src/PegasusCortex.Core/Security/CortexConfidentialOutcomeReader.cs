using System.Text.Json;
using Pegasus.Shared.Diagnostics;
using Pegasus.Shared.Outcomes;
using Pegasus.Shared.Schemas;
using Pegasus.Shared.Security;

namespace Pegasus.Cortex.Core.Security;

public sealed record ConfidentialOutcomeReadResult(
    bool Accepted,
    AgentOutcomeV1? Outcome,
    PegasusDiagnostic? Diagnostic);

/// <summary>
/// Validates recipient-bound envelope metadata before asking an injected provider to decrypt.
/// The default shared provider fails closed; this class never logs or returns plaintext on failure.
/// </summary>
public sealed class CortexConfidentialOutcomeReader(IConfidentialMessageProtector protector)
{
    public async ValueTask<ConfidentialOutcomeReadResult> ReadAsync(
        ConfidentialEnvelopeV1 envelope,
        string expectedRecipientServiceId,
        long minimumRecipientKeyRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRecipientServiceId);

        var metadataDiagnostic = ConfidentialEnvelopeValidator.ValidateMetadata(
            envelope,
            expectedRecipientServiceId,
            minimumRecipientKeyRevision,
            now);
        if (metadataDiagnostic is not null)
            return new ConfidentialOutcomeReadResult(false, null, metadataDiagnostic);

        if (!string.Equals(envelope.ContentType, CortexTransportSecurity.OutcomeContentType, StringComparison.Ordinal))
        {
            return Reject(
                "confidential_outcome_content_type_unsupported",
                "The confidential envelope does not contain an agent-outcome v1 payload.",
                envelope);
        }

        try
        {
            var unprotected = await protector.UnprotectAsync(
                envelope,
                expectedRecipientServiceId,
                minimumRecipientKeyRevision,
                now,
                cancellationToken).ConfigureAwait(false);

            if (!unprotected.Accepted)
                return new ConfidentialOutcomeReadResult(false, null, unprotected.Diagnostic ?? CreateDiagnostic(
                    "confidential_outcome_unprotect_rejected",
                    "The confidential outcome provider rejected the payload.",
                    envelope));

            var outcome = JsonSerializer.Deserialize<AgentOutcomeV1>(unprotected.Plaintext.Span, PegasusJson.Options);
            return outcome is null
                ? Reject("confidential_outcome_payload_empty", "The decrypted outcome payload is empty.", envelope)
                : new ConfidentialOutcomeReadResult(true, outcome, null);
        }
        catch (ConfidentialMessagingUnavailableException exception)
        {
            return new ConfidentialOutcomeReadResult(false, null, exception.Diagnostic);
        }
        catch (JsonException)
        {
            return Reject(
                "confidential_outcome_payload_invalid",
                "The decrypted payload is not a valid agent-outcome v1 document.",
                envelope);
        }
    }

    private static ConfidentialOutcomeReadResult Reject(
        string code,
        string message,
        ConfidentialEnvelopeV1 envelope) =>
        new(false, null, CreateDiagnostic(code, message, envelope));

    private static PegasusDiagnostic CreateDiagnostic(
        string code,
        string message,
        ConfidentialEnvelopeV1 envelope) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.confidential-outcome.read",
            false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["envelopeId"] = envelope.EnvelopeId,
                ["correlationId"] = envelope.CorrelationId,
                ["senderServiceId"] = envelope.SenderServiceId,
                ["recipientServiceId"] = envelope.RecipientServiceId
            });
}
