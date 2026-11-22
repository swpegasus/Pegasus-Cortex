using System.Text.RegularExpressions;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.DefaultLibrary.Validation;

public sealed partial class KnowledgeBundleValidator
{
    private const int MaximumRecords = 500;
    private const int MaximumEvidencePerRecord = 16;
    private const int MaximumContentCharacters = 16 * 1024;

    private static readonly HashSet<string> Sensitivities = ["public", "internal", "confidential", "restricted"];
    private static readonly HashSet<string> FreshnessStates = ["current", "stale", "blocked", "unverified", "unknown"];
    private static readonly HashSet<string> RetentionClasses = ["ephemeral", "operational", "knowledge", "decision", "audit"];
    private static readonly HashSet<string> RecordStatuses = ["active", "superseded", "archived"];
    private static readonly HashSet<string> ApprovalStates = ["pending", "approved", "rejected"];
    private static readonly HashSet<string> PromotionStates = ["none", "seeded", "proposed", "approved", "rejected"];
    private static readonly HashSet<string> ApplicabilityStates = ["verified", "blocked", "unverified", "unknown", "stale"];
    private static readonly HashSet<string> EvidenceSchemes = ["source", "schema", "package", "snapshot", "snapshot-source", "coordination-receipt", "artifact", "https", "urn"];
    private static readonly string[] ProhibitedMarkers =
    [
        "-----BEGIN PRIVATE KEY-----",
        "-----BEGIN RSA PRIVATE KEY-----",
        "Bearer ey",
        "password=",
        "access_token=",
        "refresh_token=",
        "raw terminal dump",
        "raw chat transcript",
        "<chain_of_thought>",
        "private reasoning:",
        "run this command:",
        "execute the following:"
    ];

    public KnowledgeBundleValidationResult Validate(KnowledgeSeedBundleV1 bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var diagnostics = new List<PegasusDiagnostic>();

        if (bundle.SchemaVersion != 1 || !string.Equals(bundle.ContractId, KnowledgeContractIds.SeedBundle, StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_bundle_contract_unsupported", "The seed bundle contract or schema version is unsupported."));
        if (bundle.Executable || !string.Equals(bundle.ContentTreatment, KnowledgeContractIds.ContentTreatment, StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_bundle_treatment_invalid", "A seed bundle must be non-executable untrusted data."));
        if (!TryMapNuGetVersion(bundle.ReleaseVersion, out var expectedNuGet) ||
            !string.Equals(expectedNuGet, bundle.NuGetVersion, StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_release_version_invalid", "Canonical and NuGet-safe release versions do not map deterministically."));
        if (bundle.Records.Count is 0 or > MaximumRecords)
            diagnostics.Add(Error("default_library_record_count_invalid", "A seed bundle must contain between 1 and 500 records."));

        var taxonomyCodes = bundle.Taxonomies.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        if (taxonomyCodes.Count != bundle.Taxonomies.Count || taxonomyCodes.Any(string.IsNullOrWhiteSpace))
            diagnostics.Add(Error("default_library_taxonomy_invalid", "Taxonomy codes must be non-empty and unique."));
        if (!bundle.Taxonomies.Select(t => t.Code).SequenceEqual(bundle.Taxonomies.Select(t => t.Code).Order(StringComparer.Ordinal)))
            diagnostics.Add(Error("default_library_canonical_order_invalid", "Taxonomies must use canonical ordinal order."));
        if (!bundle.Records.Select(r => (r.RecordId, r.RevisionId)).SequenceEqual(
                bundle.Records.Select(r => (r.RecordId, r.RevisionId)).OrderBy(r => r.RecordId, StringComparer.Ordinal).ThenBy(r => r.RevisionId, StringComparer.Ordinal)))
            diagnostics.Add(Error("default_library_canonical_order_invalid", "Records must use canonical identity order."));

        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        var revisionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in bundle.Records)
        {
            ValidateRecord(record, taxonomyCodes, recordIds, revisionIds, diagnostics);
            if (diagnostics.Count >= 100)
                break;
        }

        var expectedBundleId = $"kbundle_{ShaForBundleIdentity(bundle.ReleaseVersion)}";
        if (!string.Equals(bundle.BundleId, expectedBundleId, StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_bundle_id_invalid", "The deterministic bundle identifier does not match its release identity."));
        if (!IsHash(bundle.BundleHash) ||
            !string.Equals(bundle.BundleHash, KnowledgeRecordFactory.ComputeBundleHash(bundle), StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_bundle_tampered", "The seed bundle content hash does not match its canonical content."));

        return new KnowledgeBundleValidationResult(diagnostics.Count == 0, diagnostics);
    }

    public static bool ContainsProhibitedContent(params string?[] values) =>
        values.Where(value => value is not null).Any(value =>
            ProhibitedMarkers.Any(marker => value!.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    public static PegasusDiagnostic Error(
        string code,
        string message,
        string? recordId = null,
        string? revisionId = null) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.default-library.validate",
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["recordId"] = recordId,
                ["revisionId"] = revisionId
            });

    private static void ValidateRecord(
        KnowledgeRecordV1 record,
        IReadOnlySet<string> taxonomyCodes,
        ISet<string> recordIds,
        ISet<string> revisionIds,
        ICollection<PegasusDiagnostic> diagnostics)
    {
        if (record.SchemaVersion != 1 || Missing(
                record.RecordId, record.RevisionId, record.Namespace, record.Taxonomy, record.Slug,
                record.Title, record.Summary, record.Content, record.Provenance.ProducerContract,
                record.Confidence.Basis, record.Freshness.Basis, record.Sensitivity,
                record.Retention.Class, record.RecordStatus, record.ContentTreatment, record.ContentHash))
        {
            diagnostics.Add(Error("default_library_record_required_value_missing", "A knowledge record is incomplete.", record.RecordId, record.RevisionId));
            return;
        }

        if (!taxonomyCodes.Contains(record.Taxonomy))
            diagnostics.Add(Error("default_library_record_taxonomy_unknown", "The record references an unknown taxonomy.", record.RecordId, record.RevisionId));
        if (!recordIds.Add(record.RecordId) || !revisionIds.Add(record.RevisionId))
            diagnostics.Add(Error("default_library_duplicate_identity", "Bundle record and revision identifiers must be unique.", record.RecordId, record.RevisionId));
        if (!string.Equals(record.RecordId, KnowledgeRecordFactory.ComputeRecordId(record.Namespace, record.Taxonomy, record.Slug), StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_record_id_invalid", "The deterministic record identifier does not match its namespace, taxonomy, and slug.", record.RecordId, record.RevisionId));

        var expectedHash = KnowledgeRecordFactory.ComputeContentHash(record);
        if (!IsHash(record.ContentHash) || !string.Equals(record.ContentHash, expectedHash, StringComparison.Ordinal) ||
            !string.Equals(record.RevisionId, $"krev_{expectedHash}", StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_record_tampered", "The knowledge revision hash or deterministic revision identifier is invalid.", record.RecordId, record.RevisionId));

        if (record.Executable || !string.Equals(record.ContentTreatment, KnowledgeContractIds.ContentTreatment, StringComparison.Ordinal))
            diagnostics.Add(Error("default_library_executable_content_forbidden", "Knowledge content must remain non-executable untrusted data.", record.RecordId, record.RevisionId));
        if (record.TrainingEligibility)
            diagnostics.Add(Error("default_library_training_eligibility_forbidden", "Default Library records are not training-eligible by default.", record.RecordId, record.RevisionId));
        if (record.Summary.Length > 1024 || record.Content.Length > MaximumContentCharacters ||
            record.Evidence.Count is 0 or > MaximumEvidencePerRecord || record.Applicability.Count is 0 or > 32)
            diagnostics.Add(Error("default_library_record_bounds_exceeded", "Knowledge summary, content, or evidence exceeds the bounded contract.", record.RecordId, record.RevisionId));
        if (ContainsProhibitedContent(record.Title, record.Summary, record.Content) ||
            record.Keywords.Any(value => ContainsProhibitedContent(value)) ||
            record.Evidence.Any(evidence => ContainsProhibitedContent(evidence.Reference)))
            diagnostics.Add(Error("default_library_prohibited_content", "Knowledge contains prohibited credentials, private reasoning, raw dumps, or executable instructions.", record.RecordId, record.RevisionId));
        if (!Sensitivities.Contains(record.Sensitivity) || !FreshnessStates.Contains(record.Freshness.State) ||
            !RetentionClasses.Contains(record.Retention.Class) || !RecordStatuses.Contains(record.RecordStatus) ||
            !ApprovalStates.Contains(record.Approval.State) || !PromotionStates.Contains(record.Promotion.State))
            diagnostics.Add(Error("default_library_classification_invalid", "Knowledge classification, freshness, retention, approval, promotion, or status is unsupported.", record.RecordId, record.RevisionId));
        if (record.Confidence.Value is < 0 or > 1)
            diagnostics.Add(Error("default_library_confidence_invalid", "Knowledge confidence must be between zero and one.", record.RecordId, record.RevisionId));
        if (record.Approval.State == "approved" &&
            (string.IsNullOrWhiteSpace(record.Approval.ApprovedBy) || record.Approval.ApprovedAt is null))
            diagnostics.Add(Error("default_library_approval_invalid", "Approved knowledge requires approver identity and time.", record.RecordId, record.RevisionId));
        if (record.Supersession.SupersedesRevisionIds.Contains(record.RevisionId, StringComparer.Ordinal) ||
            record.Supersession.SupersedesRevisionIds.Distinct(StringComparer.Ordinal).Count() != record.Supersession.SupersedesRevisionIds.Count)
            diagnostics.Add(Error("default_library_supersession_invalid", "Supersession references must be unique and cannot reference the current revision.", record.RecordId, record.RevisionId));
        if (!IsCanonicalOrder(record.Keywords) ||
            !IsCanonicalOrder(record.Evidence.Select(e => e.Reference)) ||
            !IsCanonicalOrder(record.Provenance.SourceRecordIds) ||
            !IsCanonicalOrder(record.Provenance.Transformations) ||
            !IsCanonicalOrder(record.Supersession.SupersedesRevisionIds))
            diagnostics.Add(Error("default_library_record_order_invalid", "Record lists must be unique and use canonical ordinal order.", record.RecordId, record.RevisionId));

        foreach (var applicability in record.Applicability)
        {
            if (Missing(applicability.ProductId, applicability.ComponentId, applicability.State) ||
                !ApplicabilityStates.Contains(applicability.State))
                diagnostics.Add(Error("default_library_applicability_invalid", "Applicability must identify product, component, and supported evidence state.", record.RecordId, record.RevisionId));
        }

        foreach (var evidence in record.Evidence)
        {
            if (Missing(evidence.Reference, evidence.MediaType, evidence.Sha256, evidence.Verification) ||
                !IsHash(evidence.Sha256) || !Uri.TryCreate(evidence.Reference, UriKind.Absolute, out var uri) ||
                !EvidenceSchemes.Contains(uri.Scheme))
                diagnostics.Add(Error("default_library_evidence_invalid", "Evidence must have an attributable reference, media type, verification state, and lowercase SHA-256.", record.RecordId, record.RevisionId));
        }
    }

    private static bool TryMapNuGetVersion(string version, out string mapped)
    {
        mapped = string.Empty;
        var match = CanonicalVersionRegex().Match(version);
        if (!match.Success)
            return false;

        var hourMinute = (int.Parse(match.Groups["hour"].Value) * 100) + int.Parse(match.Groups["minute"].Value);
        mapped = $"{match.Groups["generation"].Value}.{match.Groups["year"].Value}{match.Groups["month"].Value}{match.Groups["day"].Value}.{hourMinute}-{match.Groups["revision"].Value.ToLowerInvariant()}";
        return true;
    }

    private static string ShaForBundleIdentity(string releaseVersion)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{KnowledgeContractIds.SeedBundle}\n{releaseVersion.Trim()}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool Missing(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);
    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsCanonicalOrder(IEnumerable<string> values)
    {
        var items = values.ToArray();
        return items.Distinct(StringComparer.Ordinal).Count() == items.Length &&
            items.SequenceEqual(items.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex("^(?<generation>[1-9][0-9]*)\\.(?<year>[0-9]{4})\\.(?<month>[0-9]{2})\\.(?<day>[0-9]{2})\\.(?<hour>[0-9]{2})\\.(?<minute>[0-9]{2})\\.(?<revision>[A-D])$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalVersionRegex();
}
