using System.Text.RegularExpressions;
using Pegasus.Cortex.LearningExports.Adapters;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Diagnostics;
using Pegasus.Cortex.LearningExports.IO;
using Pegasus.Cortex.LearningExports.Policy;
using Pegasus.Cortex.LearningExports.Processing;
using Pegasus.Cortex.LearningExports.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.LearningExports.Validation;

internal sealed record LearningExportValidationResult(
    IReadOnlyList<LearningRecordRevisionV1> Records,
    IReadOnlyList<PegasusDiagnostic> Diagnostics)
{
    public bool Accepted => Diagnostics.All(item => item.Severity < DiagnosticSeverity.Error);
}

internal sealed partial class LearningExportValidator(
    LearningEligibilityPolicy eligibilityPolicy,
    LearningTransformationPipeline transformations)
{
    private static readonly HashSet<string> IntendedUses = ["evaluation", "fine_tuning", "retrieval"];

    public LearningExportValidationResult Validate(
        LearningInputBundleV1 bundle,
        LearningDatasetDefinitionV1 definition,
        LearningDatasetBuildRequest request,
        ILearningDatasetAdapter? adapter,
        LearningExportOptions options,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<PegasusDiagnostic>();
        ValidateBundle(bundle, options, diagnostics, cancellationToken);
        ValidateDefinition(definition, adapter, options, diagnostics, cancellationToken);
        ValidateAuthority(request, definition, options, diagnostics, cancellationToken);
        if (!string.Equals(bundle.BundleId, definition.SourceBundleId, StringComparison.Ordinal))
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_source_bundle_mismatch",
                "The definition does not pin the exact supplied input bundle identity."));
        }
        if (definition.CreatedAt < bundle.GeneratedAt)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_definition_time_invalid",
                "The dataset definition predates its pinned input bundle."));
        }
        if (AtLimit(diagnostics, options))
            return new([], diagnostics);

        var recordsByIdentity = new Dictionary<(string RecordId, string RevisionId), LearningRecordRevisionV1>();
        foreach (var record in bundle.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeField(record.RecordId, 256) || !IsSafeField(record.RevisionId, 256))
                continue;
            if (!recordsByIdentity.TryAdd((record.RecordId, record.RevisionId), record))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_duplicate_source_revision",
                    "The input bundle contains a duplicate record/revision identity.",
                    record.RecordId,
                    record.RevisionId));
            }
        }

        var allowedNamespaces = definition.AllowedNamespaces.ToHashSet(StringComparer.Ordinal);
        var selected = new List<LearningRecordRevisionV1>(definition.SelectedRevisions.Count);
        var seenSelections = new HashSet<(string RecordId, string RevisionId)>();

        foreach (var selection in definition.SelectedRevisions
                     .OrderBy(item => item.RecordId, StringComparer.Ordinal)
                     .ThenBy(item => item.RevisionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeField(selection.RecordId, 256) || !IsSafeField(selection.RevisionId, 256) ||
                !IsHash(selection.SourceContentHash) || !IsHash(selection.ReviewEnvelopeHash))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_selection_invalid",
                    "Every selection must pin bounded record/revision IDs plus source-content and review-envelope SHA-256 hashes."));
                continue;
            }

            var identity = (selection.RecordId, selection.RevisionId);
            if (!seenSelections.Add(identity))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_duplicate_selection",
                    "The dataset definition selects the same immutable revision more than once.",
                    selection.RecordId,
                    selection.RevisionId));
                continue;
            }

            if (!recordsByIdentity.TryGetValue(identity, out var record))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_selected_revision_missing",
                    "The exact selected record revision is absent from the explicit input bundle.",
                    selection.RecordId,
                    selection.RevisionId));
                continue;
            }

            if (!string.Equals(selection.SourceContentHash, record.SourceContentHash, StringComparison.Ordinal) ||
                !string.Equals(selection.ReviewEnvelopeHash, record.ReviewEnvelopeHash, StringComparison.Ordinal))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_selected_hash_mismatch",
                    "The selected revision does not match its pinned source-content and review-envelope hashes.",
                    selection.RecordId,
                    selection.RevisionId));
            }

            if (record.Review.ApprovedAt is null || record.Review.ApprovedAt < record.CreatedAt ||
                record.Review.ApprovedAt > definition.CreatedAt)
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_record_review_time_invalid",
                    "The source review must occur after the immutable revision and no later than definition creation.",
                    record.RecordId,
                    record.RevisionId));
            }

            var authority = request.Authority;
            var review = record.Review;
            if (!authority.TrustedApprovalAuthorities.Contains(review.ApprovalAuthorityId) ||
                !authority.ApprovedDecisionHashes.TryGetValue(review.ApprovalDecisionId, out var approvedHash) ||
                !string.Equals(approvedHash, record.ReviewEnvelopeHash, StringComparison.Ordinal) ||
                !authority.ExportableNamespaces.Contains(record.Namespace) ||
                authority.PolicyRevision != review.PolicyRevision)
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_record_authority_denied",
                    "Current server/operator authority does not affirm this revision's approval decision, namespace, and policy revision.",
                    record.RecordId,
                    record.RevisionId));
            }

            foreach (var diagnostic in eligibilityPolicy.Evaluate(record, allowedNamespaces, options))
                Add(diagnostics, options, diagnostic);

            selected.Add(record);
            if (AtLimit(diagnostics, options))
                break;
        }

        return new(selected.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static void ValidateAuthority(
        LearningDatasetBuildRequest request,
        LearningDatasetDefinitionV1 definition,
        LearningExportOptions options,
        List<PegasusDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authority = request.Authority;
        var authorityShapeInvalid = !IsSafeField(authority.SubjectId, 256) ||
            authority.PolicyRevision <= 0 || !authority.CanPlan ||
            (request.PublishArtifacts && !authority.CanBuild) ||
            authority.TrustedApprovalAuthorities.Count is 0 or > 256 ||
            authority.TrustedApprovalAuthorities.Any(value => !IsSafeField(value, 256)) ||
            authority.ApprovedDecisionHashes.Count is 0 ||
            authority.ApprovedDecisionHashes.Any(pair => !IsSafeField(pair.Key, 256) || !IsHash(pair.Value)) ||
            authority.ExportableNamespaces.Count is 0 or > 256 ||
            authority.ExportableNamespaces.Any(value => !IsSafeField(value, 256));
        if (authorityShapeInvalid)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_authority_denied",
                "A bounded current server/operator planning grant, and build grant when publishing, is required."));
            return;
        }

        if (request.PublishArtifacts)
        {
            if (!LocalOutputRootPolicy.TryAuthorize(
                    request.OutputRoot,
                    authority.ApprovedOutputRoot,
                    out _,
                    out _))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_output_root_denied",
                    "Publication requires one matching host-approved canonical local output root."));
            }
            else if (OperatingSystem.IsWindows())
            {
                Add(diagnostics, options, LearningExportDiagnostics.Information(
                    "learning_export_windows_acl_host_asserted",
                    "The host-approved Windows output root is expected to enforce a private ACL; portable code cannot prove that ACL."));
            }
        }
        else if (request.OutputRoot is not null)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_plan_output_forbidden",
                "A planning request must not carry an unused output root."));
        }

        var approval = definition.Approval;
        if (authority.PolicyRevision != approval.PolicyRevision ||
            !authority.TrustedApprovalAuthorities.Contains(approval.AuthorityId) ||
            !authority.ApprovedDecisionHashes.TryGetValue(approval.DecisionId, out var approvedHash) ||
            !string.Equals(approvedHash, definition.DefinitionHash, StringComparison.Ordinal) ||
            definition.AllowedNamespaces.Any(value => !authority.ExportableNamespaces.Contains(value)))
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_definition_authority_denied",
                "Current server/operator authority does not affirm the definition approval, policy revision, or namespaces."));
        }
    }

    private static void ValidateBundle(
        LearningInputBundleV1 bundle,
        LearningExportOptions options,
        List<PegasusDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (bundle.SchemaVersion != 1 ||
            !string.Equals(bundle.ContractId, LearningExportContractIds.InputBundle, StringComparison.Ordinal) ||
            bundle.Executable ||
            !string.Equals(bundle.ContentTreatment, LearningExportContractIds.ContentTreatment, StringComparison.Ordinal))
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_input_contract_invalid",
                "The input bundle contract is unsupported or is not non-executable untrusted data."));
        }
        if (bundle.GeneratedAt == default)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_input_time_invalid",
                "The input bundle requires an explicit generation time."));
        }
        if (bundle.Records.Count is 0 || bundle.Records.Count > options.MaxRecords)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_input_count_invalid",
                "The input bundle record count is empty or exceeds the configured limit."));
            return;
        }

        var allShapesValid = true;
        foreach (var record in bundle.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRecordShapeValid(record, options))
            {
                allShapesValid = false;
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_source_revision_invalid",
                    "A source revision exceeds a bound or is missing immutable identity, lineage, review, or payload fields.",
                    record.RecordId,
                    record.RevisionId));
                continue;
            }

            string expectedEnvelope;
            try
            {
                expectedEnvelope = LearningExportFactory.ComputeReviewEnvelopeHash(record);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_source_revision_invalid",
                    "A source revision could not be canonicalized.",
                    record.RecordId,
                    record.RevisionId,
                    exception.GetType().Name));
                continue;
            }

            if (!IsHash(record.SourceContentHash) || !IsHash(record.ReviewEnvelopeHash) ||
                !string.Equals(expectedEnvelope, record.ReviewEnvelopeHash, StringComparison.Ordinal))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_source_revision_tampered",
                    "A source revision has an invalid source hash or does not match its canonical review-envelope hash.",
                    record.RecordId,
                    record.RevisionId));
            }
            if (record.CreatedAt > bundle.GeneratedAt || record.Review.ApprovedAt > bundle.GeneratedAt)
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_source_time_invalid",
                    "A source revision or its review occurs after the explicit input bundle was generated.",
                    record.RecordId,
                    record.RevisionId));
            }
        }

        if (!AtLimit(diagnostics, options) && allShapesValid)
        {
            try
            {
                var expectedHash = LearningExportFactory.ComputeBundleHash(
                    bundle,
                    options.MaxInputBundleBytes,
                    cancellationToken);
                if (!IsHash(bundle.BundleHash) ||
                    !string.Equals(expectedHash, bundle.BundleHash, StringComparison.Ordinal) ||
                    !string.Equals(bundle.BundleId, $"lbundle_{expectedHash}", StringComparison.Ordinal))
                {
                    Add(diagnostics, options, LearningExportDiagnostics.Error(
                        "learning_export_input_tampered",
                        "The input bundle identity or canonical hash is invalid."));
                }
            }
            catch (LearningExportLimitExceededException)
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_input_oversize",
                    "The canonical input bundle exceeds its configured byte limit."));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_input_invalid",
                    "The input bundle could not be canonicalized.",
                    cause: exception.GetType().Name));
            }
        }
    }

    private void ValidateDefinition(
        LearningDatasetDefinitionV1 definition,
        ILearningDatasetAdapter? adapter,
        LearningExportOptions options,
        List<PegasusDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (definition.SchemaVersion != 1 ||
            !string.Equals(definition.ContractId, LearningExportContractIds.DatasetDefinition, StringComparison.Ordinal) ||
            !IsSafeField(definition.DisplayName, 256) ||
            !DatasetIdRegex().IsMatch(definition.DatasetId ?? string.Empty) ||
            !LearningExportPathRules.IsSafeDatasetId(definition.DatasetId) ||
            !BundleIdRegex().IsMatch(definition.SourceBundleId ?? string.Empty) ||
            definition.CreatedAt == default)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_definition_contract_invalid",
                "The dataset definition contract, identity, display name, or source bundle reference is invalid."));
        }

        if (definition.SelectedRevisions.Count is 0 || definition.SelectedRevisions.Count > options.MaxRecords ||
            definition.AllowedNamespaces.Count is 0 or > 256 ||
            definition.AllowedNamespaces.Any(value => !IsSafeField(value, 256)) ||
            definition.AllowedNamespaces.Distinct(StringComparer.Ordinal).Count() != definition.AllowedNamespaces.Count)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_definition_selection_invalid",
                "The definition requires bounded exact selections and at least one approved namespace."));
        }

        if (definition.IntendedUses.Count is 0 or > 3 ||
            definition.IntendedUses.Any(value => !IntendedUses.Contains(value)) ||
            definition.IntendedUses.Distinct(StringComparer.Ordinal).Count() != definition.IntendedUses.Count)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_intended_use_invalid",
                "Intended uses must be a unique non-empty subset of retrieval, evaluation, and fine_tuning."));
        }

        var approval = definition.Approval;
        if (!string.Equals(approval.State, "approved", StringComparison.Ordinal) ||
            approval.PolicyRevision <= 0 || !IsSafeField(approval.AuthorityId, 256) ||
            !IsSafeField(approval.DecisionId, 256) || !IsSafeField(approval.ApprovedBy, 256) ||
            approval.ApprovedAt is null || approval.ApprovedAt < definition.CreatedAt)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_definition_not_approved",
                "A separate approval at or after immutable definition creation is required."));
        }

        var split = definition.SplitPolicy;
        if (!string.Equals(split.Algorithm, LearningExportContractIds.StableSplitAlgorithm, StringComparison.Ordinal) ||
            !IsSafeField(split.Salt, 128) || split.Salt.Length < 16 ||
            split.TrainBasisPoints <= 0 || split.ValidationBasisPoints < 0 || split.TestBasisPoints < 0 ||
            (long)split.TrainBasisPoints + split.ValidationBasisPoints + split.TestBasisPoints != 10_000)
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_split_policy_invalid",
                "The stable split policy must use a bounded salt and total exactly 10,000 basis points."));
        }

        if (!IsSafeField(definition.AdapterId, 128) || !IsSafeField(definition.AdapterVersion, 64) ||
            adapter is null || !string.Equals(adapter.Id, definition.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(adapter.Version, definition.AdapterVersion, StringComparison.Ordinal) ||
            !IsSafeField(adapter.Id, 128) || !IsSafeField(adapter.Version, 64) ||
            !IsSafeField(adapter.MediaType, 256) ||
            !LearningExportPathRules.IsSafeArtifactFileName(adapter.RelativeArtifactPath))
        {
            Add(diagnostics, options, LearningExportDiagnostics.Error(
                "learning_export_adapter_unavailable",
                "The exact bounded local dataset adapter is unavailable or declares an unsafe artifact contract."));
        }

        var seen = new HashSet<(string Id, string Version)>();
        foreach (var transformation in definition.Transformations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeField(transformation.Id, 128) || !IsSafeField(transformation.Version, 64) ||
                !seen.Add((transformation.Id, transformation.Version)) ||
                transformation.Parameters.Count > 32 ||
                transformation.Parameters.Any(pair => !IsSafeField(pair.Key, 256) ||
                    pair.Value is null || pair.Value.Length > 4096 || pair.Value.Any(char.IsControl)) ||
                transformation.Parameters.Sum(pair => (long)pair.Key.Length + pair.Value.Length) > 64 * 1024 ||
                !transformations.Supports(transformation))
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_transformation_unsupported",
                    "Every transformation must resolve to one unique, bounded, control-free local implementation."));
            }
        }

        if (!AtLimit(diagnostics, options))
        {
            try
            {
                var expectedHash = LearningExportFactory.ComputeDefinitionHash(definition);
                if (!IsHash(definition.DefinitionHash) ||
                    !string.Equals(definition.DefinitionHash, expectedHash, StringComparison.Ordinal) ||
                    !string.Equals(definition.DefinitionRevisionId, $"ldef_{expectedHash}", StringComparison.Ordinal))
                {
                    Add(diagnostics, options, LearningExportDiagnostics.Error(
                        "learning_export_definition_tampered",
                        "The dataset definition revision or canonical hash is invalid."));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Add(diagnostics, options, LearningExportDiagnostics.Error(
                    "learning_export_definition_invalid",
                    "The dataset definition could not be canonicalized.",
                    cause: exception.GetType().Name));
            }
        }
    }

    private static bool IsRecordShapeValid(LearningRecordRevisionV1 record, LearningExportOptions options)
    {
        var review = record.Review;
        return record.SchemaVersion == 1 &&
            IsSafeField(record.RecordId, 256) && IsSafeField(record.RevisionId, 256) &&
            IsSafeField(record.Namespace, 256) && IsSafeField(record.SourceKind, 256) &&
            IsSafeField(record.SourceServiceId, 256) && IsSafeField(record.SourceArtifactId, 256) &&
            IsSafeField(record.LeakageGroupId, 256) && IsSafeField(record.Sensitivity, 64) &&
            record.CreatedAt != default && record.Payload.Count is > 0 &&
            record.Payload.Count <= options.MaxPayloadFields &&
            record.Payload.All(pair => IsSafeField(pair.Key, 256) && pair.Value is not null) &&
            record.Payload.Sum(pair => (long)pair.Key.Length + pair.Value.Length) <= options.MaxPayloadCharacters &&
            IsSafeField(review.State, 256) && IsSafeField(review.UsageRights, 256) &&
            IsSafeField(review.RedactionDecision, 256) && IsSafeField(review.PrivacyReview, 256) &&
            IsSafeField(review.SecretReview, 256) && IsSafeField(review.ReasoningReview, 256) &&
            IsSafeField(review.ProviderCredentialReview, 256) &&
            IsSafeField(review.ApprovalAuthorityId, 256) && IsSafeField(review.ApprovalDecisionId, 256) &&
            IsSafeField(review.ApprovedBy, 256);
    }

    private static bool IsSafeField(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool AtLimit(IReadOnlyCollection<PegasusDiagnostic> diagnostics, LearningExportOptions options) =>
        diagnostics.Count >= options.MaxDiagnostics;

    private static void Add(
        List<PegasusDiagnostic> diagnostics,
        LearningExportOptions options,
        PegasusDiagnostic diagnostic)
    {
        if (!AtLimit(diagnostics, options))
            diagnostics.Add(diagnostic);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatasetIdRegex();

    [GeneratedRegex("^lbundle_[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex BundleIdRegex();
}
