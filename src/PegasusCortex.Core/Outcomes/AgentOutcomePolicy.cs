using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Shared.Diagnostics;
using Pegasus.Shared.Outcomes;
using Pegasus.Shared.Schemas;

namespace Pegasus.Cortex.Core.Outcomes;

public static class AgentOutcomePolicy
{
    private const int MaximumSummaryLength = 4096;
    private const int MaximumEvidenceCount = 32;
    private const int MaximumResultBytes = 64 * 1024;

    private static readonly HashSet<string> RetentionClasses =
        ["ephemeral", "operational", "knowledge", "decision", "rejected-diagnostic", "audit", "export-artifact"];

    private static readonly HashSet<string> Sensitivities =
        ["public", "internal", "confidential", "restricted"];

    private static readonly HashSet<string> ApprovalStates =
        ["not-required", "pending", "approved", "rejected"];

    private static readonly HashSet<string> PromotionStates =
        ["none", "proposed", "approved", "rejected"];

    private static readonly HashSet<string> ProhibitedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordhash", "sessioncookie", "accesstoken", "refreshtoken",
        "privatekey", "secret", "apikey", "clientsecret", "credential", "authorization", "cookie",
        "rawterminal", "terminaldump", "rawchat", "chatdump",
        "chainofthought", "privatereasoning", "command", "commandline", "script",
        "executableinstruction", "toolarguments"
    };

    private static readonly string[] ProhibitedTextMarkers =
    [
        "-----BEGIN PRIVATE KEY-----", "-----BEGIN RSA PRIVATE KEY-----", "Bearer ", "password=", "access_token=",
        "terminal://", "raw-terminal", "chat://", "raw-chat", "run this command:", "execute the following:"
    ];

    public static PegasusDiagnostic? ValidateSubmission(
        AgentOutcomeV1 outcome,
        OutcomeCallerContext caller,
        DateTimeOffset now)
    {
        if (outcome.SchemaVersion != 1)
            return Error("outcome_schema_unsupported", "Only agent-outcome schema version 1 is supported.", outcome.OutcomeId);
        if (Missing(outcome.OutcomeId, outcome.TenantId, outcome.Namespace, outcome.RecordKind,
                    outcome.OutcomeKind, outcome.PublicSummary, outcome.Source.ServiceId,
                    outcome.Source.AgentId, outcome.Source.OperationId, outcome.Provenance.ProducerContract,
                    outcome.Confidence.Basis, outcome.Retention.Class, outcome.ContentTreatment))
            return Error("outcome_required_value_missing", "The outcome is missing a required value.", outcome.OutcomeId);
        if (!string.Equals(outcome.TenantId, caller.TenantId, StringComparison.Ordinal))
            return Error("outcome_tenant_forbidden", "The submitted tenant does not match server-derived authority.", outcome.OutcomeId);
        if (!string.Equals(outcome.Source.ServiceId, caller.ServiceId, StringComparison.Ordinal))
            return Error("outcome_source_service_forbidden", "The source service does not match the authenticated service.", outcome.OutcomeId);

        var grants = caller.NamespaceGrants.Where(g =>
            g.CanWrite && string.Equals(g.Namespace, outcome.Namespace, StringComparison.Ordinal)).ToArray();
        if (grants.Length != 1)
            return Error("outcome_namespace_forbidden", "Server-derived policy does not grant this namespace write.", outcome.OutcomeId);

        if (!string.IsNullOrWhiteSpace(outcome.Promotion.TargetNamespace) &&
            !caller.NamespaceGrants.Any(g => g.CanProposePromotion &&
                string.Equals(g.Namespace, outcome.Promotion.TargetNamespace, StringComparison.Ordinal)))
            return Error("outcome_promotion_namespace_forbidden", "Server-derived policy does not grant promotion to the target namespace.", outcome.OutcomeId);

        if (!string.Equals(outcome.RecordStatus, "active", StringComparison.Ordinal))
            return Error("outcome_initial_status_invalid", "New outcomes must begin with active status.", outcome.OutcomeId);
        if (outcome.Executable)
            return Error("outcome_executable_forbidden", "Stored outcome content must never be executable.", outcome.OutcomeId);
        if (!string.Equals(outcome.ContentTreatment, "untrusted_data", StringComparison.Ordinal))
            return Error("outcome_content_treatment_invalid", "Outcome content must be explicitly treated as untrusted data.", outcome.OutcomeId);
        if (outcome.PublicSummary.Length > MaximumSummaryLength)
            return Error("outcome_summary_too_large", "The public summary exceeds 4096 characters.", outcome.OutcomeId);
        if (outcome.Evidence.Count > MaximumEvidenceCount)
            return Error("outcome_evidence_too_large", "The outcome has too many evidence entries.", outcome.OutcomeId);
        if (outcome.Confidence.Value is < 0 or > 1)
            return Error("outcome_confidence_invalid", "Confidence must be between zero and one.", outcome.OutcomeId);
        if (!Sensitivities.Contains(outcome.Sensitivity) || !RetentionClasses.Contains(outcome.Retention.Class) ||
            !ApprovalStates.Contains(outcome.Approval.State) || !PromotionStates.Contains(outcome.Promotion.State))
            return Error("outcome_classification_invalid", "Sensitivity, retention, approval, or promotion classification is unsupported.", outcome.OutcomeId);
        if (outcome.Approval.PolicyRevision != caller.PolicyRevision)
            return Error("outcome_policy_revision_stale", "The outcome approval policy revision is not current.", outcome.OutcomeId);
        if (outcome.Approval.State is "approved" or "rejected" && !grants[0].CanApprove)
            return Error("outcome_approval_forbidden", "Server-derived policy does not grant approval authority.", outcome.OutcomeId);
        if (outcome.Promotion.State is "approved" or "rejected" && !grants[0].CanApplyPromotion)
            return Error("outcome_promotion_decision_forbidden", "Server-derived policy does not grant promotion decision authority.", outcome.OutcomeId);
        if (outcome.Retention.LegalHold && !string.Equals(outcome.Approval.State, "approved", StringComparison.Ordinal))
            return Error("outcome_legal_hold_forbidden", "A legal hold requires an approved outcome.", outcome.OutcomeId);
        if (outcome.TrainingEligibility &&
            (!string.Equals(outcome.Approval.State, "approved", StringComparison.Ordinal) ||
             outcome.Sensitivity is not ("public" or "internal")))
            return Error("outcome_training_eligibility_forbidden", "Training export requires approved public or internal content.", outcome.OutcomeId);
        if (outcome.CreatedAt > now.AddMinutes(5) || outcome.Source.OccurredAt > now.AddMinutes(5))
            return Error("outcome_timestamp_invalid", "Outcome timestamps may not be materially in the future.", outcome.OutcomeId);
        if (outcome.Supersession.Supersedes.Distinct(StringComparer.Ordinal).Count() != outcome.Supersession.Supersedes.Count ||
            outcome.Supersession.Supersedes.Contains(outcome.OutcomeId, StringComparer.Ordinal))
            return Error("outcome_supersession_invalid", "Supersession references must be unique and cannot reference the new outcome.", outcome.OutcomeId);

        foreach (var evidence in outcome.Evidence)
        {
            if (Missing(evidence.Reference, evidence.MediaType, evidence.Sha256, evidence.Verification) ||
                !IsLowerHexHash(evidence.Sha256) || ContainsProhibitedText(evidence.Reference))
                return Error("outcome_evidence_invalid", "Evidence is incomplete, unverified, prohibited, or has an invalid hash.", outcome.OutcomeId);
        }

        if (JsonSerializer.SerializeToUtf8Bytes(outcome.Result, PegasusJson.Options).Length > MaximumResultBytes)
            return Error("outcome_result_too_large", "The structured result exceeds 64 KiB.", outcome.OutcomeId);
        if (ContainsProhibitedContent(outcome.Result) || ContainsProhibitedText(outcome.PublicSummary))
            return Error("outcome_prohibited_content", "The outcome contains prohibited credentials, dumps, reasoning, or executable instructions.", outcome.OutcomeId);

        return null;
    }

    public static AgentOutcomeV1 NormalizeAcceptedOutcome(
        AgentOutcomeV1 submitted,
        OutcomeCallerContext caller,
        string effectiveNamespace,
        DateTimeOffset now)
    {
        var normalized = submitted with
        {
            TenantId = caller.TenantId,
            Namespace = effectiveNamespace,
            ContentTreatment = "untrusted_data",
            Executable = false,
            Retention = NormalizeRetention(submitted.Retention, submitted.CreatedAt, now),
            ContentHash = new string('0', 64)
        };

        return normalized with { ContentHash = ComputeContentHash(normalized) };
    }

    public static string ComputeSubmittedPayloadHash(AgentOutcomeV1 submitted) =>
        Sha256(JsonSerializer.SerializeToUtf8Bytes(submitted, PegasusJson.Options));

    public static string ComputeContentHash(AgentOutcomeV1 normalizedWithPlaceholderHash) =>
        Sha256(JsonSerializer.SerializeToUtf8Bytes(normalizedWithPlaceholderHash, PegasusJson.Options));

    public static PegasusDiagnostic Error(string code, string message, string? outcomeId, string? relatedId = null) =>
        new(code, message, DiagnosticSeverity.Error, "cortex.outcome.ingest", false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["outcomeId"] = outcomeId,
                ["relatedOutcomeId"] = relatedId
            });

    private static AgentOutcomeRetention NormalizeRetention(
        AgentOutcomeRetention requested,
        DateTimeOffset createdAt,
        DateTimeOffset now)
    {
        if (requested.LegalHold)
            return requested with { RetainUntil = null };

        var baseline = requested.Class switch
        {
            "ephemeral" => createdAt.AddDays(30),
            "operational" => createdAt.AddDays(180),
            "rejected-diagnostic" => createdAt.AddDays(90),
            "audit" => createdAt.AddDays(730),
            "export-artifact" => createdAt.AddDays(30),
            _ => (DateTimeOffset?)null
        };

        if (baseline is null)
            return requested;

        var requestedFuture = requested.RetainUntil > now ? requested.RetainUntil : null;
        return requested with { RetainUntil = requestedFuture is null || requestedFuture > baseline ? baseline : requestedFuture };
    }

    private static bool ContainsProhibitedContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalizedName = new string(property.Name.Where(char.IsLetterOrDigit).ToArray());
                if (ProhibitedPropertyNames.Contains(normalizedName) || ContainsProhibitedContent(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsProhibitedContent);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            return ContainsProhibitedText(element.GetString());
        }

        return false;
    }

    private static bool ContainsProhibitedText(string? text) =>
        text is not null && ProhibitedTextMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool Missing(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);
    private static bool IsLowerHexHash(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
