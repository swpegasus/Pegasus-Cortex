using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Diagnostics;
using Pegasus.Shared.Diagnostics;
using System.Text.RegularExpressions;

namespace Pegasus.Cortex.LearningExports.Policy;

public sealed partial class LearningEligibilityPolicy
{
    private static readonly HashSet<string> AllowedSourceKinds =
    [
        "reviewed_knowledge",
        "verified_outcome",
        "evaluation_case",
        "retrieval_document"
    ];

    private static readonly HashSet<string> AllowedSensitivities = ["public", "internal"];

    private static readonly string[] ProhibitedMarkers =
    [
        "-----BEGIN PRIVATE KEY-----",
        "-----BEGIN RSA PRIVATE KEY-----",
        "authorization: bearer",
        "bearer ey",
        "access_token",
        "refresh_token",
        "api_key",
        "client_secret",
        "password=",
        "<chain_of_thought>",
        "<thinking>",
        "private reasoning:",
        "raw chat transcript",
        "raw terminal stream",
        "raw live query"
    ];

    public IReadOnlyList<PegasusDiagnostic> Evaluate(
        LearningRecordRevisionV1 record,
        IReadOnlySet<string> allowedNamespaces,
        LearningExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(allowedNamespaces);
        ArgumentNullException.ThrowIfNull(options);
        var diagnostics = new List<PegasusDiagnostic>();
        var review = record.Review;

        if (review is null || !review.Eligible ||
            !string.Equals(review.State, "approved", StringComparison.Ordinal) ||
            !string.Equals(review.UsageRights, "training_and_evaluation", StringComparison.Ordinal) ||
            review.PolicyRevision <= 0 || string.IsNullOrWhiteSpace(review.ApprovedBy) ||
            string.IsNullOrWhiteSpace(review.ApprovalAuthorityId) ||
            string.IsNullOrWhiteSpace(review.ApprovalDecisionId) || review.ApprovedAt is null)
        {
            diagnostics.Add(Error(
                "learning_export_record_not_approved",
                "The selected revision lacks explicit current training-and-evaluation approval.",
                record));
        }

        if (review is null ||
            review.RedactionDecision is not ("not_required" or "applied_and_verified") ||
            !string.Equals(review.PrivacyReview, "passed", StringComparison.Ordinal) ||
            !string.Equals(review.SecretReview, "passed", StringComparison.Ordinal) ||
            !string.Equals(review.ReasoningReview, "passed", StringComparison.Ordinal) ||
            !string.Equals(review.ProviderCredentialReview, "passed", StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                "learning_export_record_review_incomplete",
                "Privacy, secret, private-reasoning, and provider-credential reviews must all pass.",
                record));
        }

        if (!allowedNamespaces.Contains(record.Namespace))
        {
            diagnostics.Add(Error(
                "learning_export_namespace_denied",
                "The selected revision is outside the definition's explicitly approved namespaces.",
                record));
        }

        if (!AllowedSourceKinds.Contains(record.SourceKind))
        {
            diagnostics.Add(Error(
                "learning_export_source_kind_denied",
                "Only reviewed knowledge, verified outcomes, evaluation cases, and retrieval documents are exportable.",
                record));
        }

        if (!AllowedSensitivities.Contains(record.Sensitivity))
        {
            diagnostics.Add(Error(
                "learning_export_sensitivity_denied",
                "Confidential and restricted revisions are not eligible for this export contract.",
                record));
        }

        if (record.Executable ||
            !string.Equals(record.ContentTreatment, LearningExportContractIds.ContentTreatment, StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                "learning_export_content_treatment_invalid",
                "Learning source content must remain non-executable untrusted data.",
                record));
        }

        if (string.IsNullOrWhiteSpace(record.LeakageGroupId))
        {
            diagnostics.Add(Error(
                "learning_export_leakage_group_missing",
                "Every selected revision requires a stable leakage group.",
                record));
        }

        diagnostics.AddRange(EvaluatePayload(record, record.Payload, options));

        return diagnostics;
    }

    public IReadOnlyList<PegasusDiagnostic> EvaluateTransformedPayload(
        LearningRecordRevisionV1 record,
        IReadOnlyDictionary<string, string> payload,
        LearningExportOptions options) =>
        EvaluatePayload(record, payload, options);

    private static IReadOnlyList<PegasusDiagnostic> EvaluatePayload(
        LearningRecordRevisionV1 record,
        IReadOnlyDictionary<string, string>? payload,
        LearningExportOptions options)
    {
        if (payload is null || payload.Count is 0 || payload.Count > options.MaxPayloadFields ||
            payload.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) ||
            payload.Sum(pair => (long)pair.Key.Length + pair.Value.Length) > options.MaxPayloadCharacters)
        {
            return
            [
                Error(
                    "learning_export_payload_bounds_exceeded",
                    "The selected revision payload is empty, malformed, or exceeds configured field/character limits.",
                    record)
            ];
        }

        if (ContainsProhibitedContent(record, payload))
        {
            return
            [
                Error(
                    "learning_export_prohibited_content",
                    "The selected revision contains a credential, raw stream/query/chat, or private-reasoning marker.",
                    record)
            ];
        }

        return [];
    }

    private static bool ContainsProhibitedContent(
        LearningRecordRevisionV1 record,
        IReadOnlyDictionary<string, string> payload)
    {
        if (payload.Keys.Any(IsSensitiveFieldName))
            return true;

        var values = payload.SelectMany(pair => new[] { pair.Key, pair.Value })
            .Append(record.SourceArtifactId)
            .Append(record.SourceServiceId)
            .Where(value => value is not null);
        return values.Any(value => ProhibitedMarkers.Any(marker =>
                value.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            CredentialPattern().IsMatch(value));
    }

    private static bool IsSensitiveFieldName(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "password" or "passwd" or "passphrase" or "apikey" or
            "accesstoken" or "refreshtoken" or "clientsecret" or "privatekey" or
            "secretkey" or "credential" or "chainofthought" or "privatereasoning" or
            "internalreasoning" or "thinking";
    }

    private static PegasusDiagnostic Error(string code, string message, LearningRecordRevisionV1 record) =>
        LearningExportDiagnostics.Error(code, message, record.RecordId, record.RevisionId);

    [GeneratedRegex(
        "(?:\\bAKIA[0-9A-Z]{16}\\b|\\b(?:ghp|github_pat)_[A-Za-z0-9_]{20,}\\b|\\bsk-[A-Za-z0-9_-]{20,}\\b|\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
