using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.Contracts.Learning;

/// <summary>
/// Stable wire actions for changing the searchable head of one published
/// learning record. The underlying revisions remain immutable.
/// </summary>
public static class LearningFeedbackPublicationChangeActions
{
    public const string Supersede = "supersede";
    public const string Withdraw = "withdraw";
}

/// <summary>
/// Bounded, machine-readable reasons. Free-form reason text is explanatory
/// only and never changes authorization or transition semantics.
/// </summary>
public static class LearningFeedbackPublicationChangeReasonCodes
{
    public const string Corrected = "corrected";
    public const string Obsolete = "obsolete";
    public const string Duplicate = "duplicate";
    public const string UnsafeContent = "unsafe_content";
    public const string InvalidContent = "invalid_content";
}

public static class LearningFeedbackPublicationChangeLimits
{
    public const int MaximumReasonCharacters = 1024;
}

/// <summary>
/// Exact replacement coordinates within the already-authorized namespace.
/// Namespace is deliberately absent so a correction cannot bridge a reserved
/// namespace or turn an identifier into authority.
/// </summary>
public sealed record LearningFeedbackPublicationReplacementV1(
    string RecordId,
    string RevisionId,
    string ContentHash);

/// <summary>
/// Caller-supplied publication-change intent. Identity and namespace fields are
/// deliberately absent; the server supplies them through
/// <see cref="LearningFeedbackNamespaceAccess"/>.
/// </summary>
public sealed record LearningFeedbackPublicationChangeRequestV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string Action,
    string TargetRecordId,
    string ExpectedHeadRevisionId,
    long ExpectedHeadGeneration,
    LearningFeedbackPublicationReplacementV1? Replacement,
    string ReasonCode,
    string Reason,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Canonical immutable publication-change event accepted by the policy. A
/// persistence provider may use the expected head coordinates as a compare-
/// and-swap precondition, but this contract itself never mutates storage.
/// </summary>
public sealed record LearningFeedbackPublicationChangeV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string ChangeId,
    string TenantId,
    string Namespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    string Action,
    string TargetRecordId,
    string ExpectedHeadRevisionId,
    long ExpectedHeadGeneration,
    LearningFeedbackPublicationReplacementV1? Replacement,
    string ReasonCode,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    string ChangeHash);

/// <summary>
/// Pure, provider-neutral acceptance policy for an author-owned publication
/// change. The store remains responsible for verifying that the target's
/// immutable origin matches the accepted authority-qualified subject and for
/// applying the head transition atomically.
/// </summary>
public static class LearningFeedbackPublicationChangePolicy
{
    private static readonly string EmptyHash = new('0', 64);
    private static readonly HashSet<string> Actions =
        [LearningFeedbackPublicationChangeActions.Supersede,
         LearningFeedbackPublicationChangeActions.Withdraw];
    private static readonly HashSet<string> ReasonCodes =
        [LearningFeedbackPublicationChangeReasonCodes.Corrected,
         LearningFeedbackPublicationChangeReasonCodes.Obsolete,
         LearningFeedbackPublicationChangeReasonCodes.Duplicate,
         LearningFeedbackPublicationChangeReasonCodes.UnsafeContent,
         LearningFeedbackPublicationChangeReasonCodes.InvalidContent];
    private static readonly HashSet<string> ReplacementOptionalWithdrawalReasons =
        [LearningFeedbackPublicationChangeReasonCodes.UnsafeContent,
         LearningFeedbackPublicationChangeReasonCodes.InvalidContent];

    public static CortexContractResult<LearningFeedbackPublicationChangeV1> Accept(
        LearningFeedbackPublicationChangeRequestV1 submitted,
        LearningFeedbackNamespaceAccess access,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();

        var hashingBoundary = ValidateHashingBoundary(submitted);
        if (hashingBoundary is not null)
        {
            return new(false, null, null, [hashingBoundary]);
        }

        var submittedBytes = CortexCanonicalJson.Serialize(submitted);
        if (submittedBytes.Length >
            LearningFeedbackContractLimits.MaximumSerializedProposalBytes)
        {
            return Reject(
                "learning_feedback_publication_change_size_invalid",
                "The publication change exceeds its serialized byte limit.",
                submitted.TargetRecordId,
                submittedHash: null);
        }

        var submittedHash = CortexCanonicalJson.Sha256(submittedBytes);
        var diagnostic = Validate(submitted, access, now);
        if (diagnostic is not null)
            return new(false, null, submittedHash, [diagnostic]);

        var normalizedReason = NormalizeReason(submitted.Reason);
        var replacement = submitted.Replacement is null
            ? null
            : submitted.Replacement with { };
        var draft = new LearningFeedbackPublicationChangeV1(
            SchemaVersion: 1,
            ContractId: LearningFeedbackContractIds.PublicationChange,
            Canonicalization: LearningFeedbackContractIds.Canonicalization,
            ChangeId: string.Empty,
            TenantId: access.TenantId,
            Namespace: access.Namespace,
            AuthorityId: access.AuthorityId,
            SubjectId: access.SubjectId,
            ServiceId: access.ServiceId,
            PolicyRevision: access.PolicyRevision,
            Action: submitted.Action,
            TargetRecordId: submitted.TargetRecordId,
            ExpectedHeadRevisionId: submitted.ExpectedHeadRevisionId,
            ExpectedHeadGeneration: submitted.ExpectedHeadGeneration,
            Replacement: replacement,
            ReasonCode: submitted.ReasonCode,
            Reason: normalizedReason,
            OccurredAtUtc: submitted.OccurredAtUtc,
            ChangeHash: EmptyHash);
        var changeHash = ComputeChangeHash(draft);
        var accepted = draft with
        {
            ChangeId = $"pchg_{changeHash}",
            ChangeHash = changeHash
        };
        return new(true, accepted, submittedHash, []);
    }

    /// <summary>
    /// Recomputes the deterministic identity from an accepted event. The
    /// identity and hash fields are excluded through canonical placeholders so
    /// providers can validate exact readback without circular hashing.
    /// </summary>
    public static string ComputeChangeHash(
        LearningFeedbackPublicationChangeV1 change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(
            change with
            {
                ChangeId = string.Empty,
                ChangeHash = EmptyHash,
                Replacement = change.Replacement is null
                    ? null
                    : change.Replacement with { }
            }));
    }

    public static bool HasValidIdentity(
        LearningFeedbackPublicationChangeV1 change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var expectedHash = ComputeChangeHash(change);
        return string.Equals(change.ChangeHash, expectedHash, StringComparison.Ordinal) &&
               string.Equals(
                   change.ChangeId,
                   $"pchg_{expectedHash}",
                   StringComparison.Ordinal);
    }

    private static PegasusDiagnostic? Validate(
        LearningFeedbackPublicationChangeRequestV1 submitted,
        LearningFeedbackNamespaceAccess access,
        DateTimeOffset now)
    {
        if (submitted.SchemaVersion != 1 ||
            !string.Equals(
                submitted.ContractId,
                LearningFeedbackContractIds.PublicationChange,
                StringComparison.Ordinal) ||
            !string.Equals(
                submitted.Canonicalization,
                LearningFeedbackContractIds.Canonicalization,
                StringComparison.Ordinal))
        {
            return Error(
                "learning_feedback_publication_change_contract_invalid",
                "The publication-change contract version or canonicalization is unsupported.",
                submitted.TargetRecordId);
        }

        if (!ValidAccess(access))
        {
            return Error(
                "learning_feedback_publication_change_authority_invalid",
                "Server-derived publication-change authority is invalid or not submit-capable.",
                submitted.TargetRecordId);
        }

        if (!Actions.Contains(submitted.Action) ||
            !ExactPrefixedHash(submitted.TargetRecordId, "krec_") ||
            !ExactPrefixedHash(submitted.ExpectedHeadRevisionId, "krev_") ||
            submitted.ExpectedHeadGeneration < 1)
        {
            return Error(
                "learning_feedback_publication_change_target_invalid",
                "The publication-change action or expected head coordinates are invalid.",
                submitted.TargetRecordId);
        }

        if (!ReasonCodes.Contains(submitted.ReasonCode) ||
            string.IsNullOrWhiteSpace(submitted.Reason) ||
            submitted.Reason.Length >
                LearningFeedbackPublicationChangeLimits.MaximumReasonCharacters ||
            submitted.Reason.Any(char.IsControl) ||
            !string.Equals(
                submitted.Reason,
                submitted.Reason.Trim(),
                StringComparison.Ordinal) ||
            !LearningFeedbackContentScreening.Screen(
                    "publication change",
                    submitted.Reason,
                    "publication change",
                    collective: false)
                .Accepted)
        {
            return Error(
                "learning_feedback_publication_change_reason_invalid",
                "The publication-change reason is missing, unsafe, or outside its bounded contract.",
                submitted.TargetRecordId);
        }

        if (submitted.OccurredAtUtc.Offset != TimeSpan.Zero ||
            submitted.OccurredAtUtc == default ||
            submitted.OccurredAtUtc > now.ToUniversalTime().AddMinutes(5))
        {
            return Error(
                "learning_feedback_publication_change_timestamp_invalid",
                "The publication-change timestamp must be UTC and cannot be materially in the future.",
                submitted.TargetRecordId);
        }

        if (submitted.Replacement is not null &&
            (!ExactPrefixedHash(submitted.Replacement.RecordId, "krec_") ||
             !ExactPrefixedHash(submitted.Replacement.RevisionId, "krev_") ||
             !ExactHash(submitted.Replacement.ContentHash) ||
             string.Equals(
                 submitted.Replacement.RecordId,
                 submitted.TargetRecordId,
                 StringComparison.Ordinal)))
        {
            return Error(
                "learning_feedback_publication_change_replacement_invalid",
                "The replacement coordinates are invalid or self-referential.",
                submitted.TargetRecordId);
        }

        if (string.Equals(
                submitted.Action,
                LearningFeedbackPublicationChangeActions.Supersede,
                StringComparison.Ordinal) &&
            submitted.Replacement is null ||
            string.Equals(
                submitted.Action,
                LearningFeedbackPublicationChangeActions.Withdraw,
                StringComparison.Ordinal) &&
            submitted.Replacement is null &&
            !ReplacementOptionalWithdrawalReasons.Contains(submitted.ReasonCode))
        {
            return Error(
                "learning_feedback_publication_change_replacement_required",
                "A replacement is required unless unsafe or invalid content is being withdrawn.",
                submitted.TargetRecordId);
        }

        return null;
    }

    private static PegasusDiagnostic? ValidateHashingBoundary(
        LearningFeedbackPublicationChangeRequestV1 submitted)
    {
        static bool Oversized(string? value, int maximum) =>
            value is null || value.Length > maximum;

        if (Oversized(submitted.ContractId, LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            Oversized(submitted.Canonicalization, LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            Oversized(submitted.Action, LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            Oversized(submitted.TargetRecordId, LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            Oversized(submitted.ExpectedHeadRevisionId, LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            Oversized(submitted.ReasonCode, LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
            Oversized(
                submitted.Reason,
                LearningFeedbackPublicationChangeLimits.MaximumReasonCharacters) ||
            submitted.Replacement is not null &&
            (Oversized(
                 submitted.Replacement.RecordId,
                 LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
             Oversized(
                 submitted.Replacement.RevisionId,
                 LearningFeedbackContractLimits.MaximumIdentifierCharacters) ||
             Oversized(submitted.Replacement.ContentHash, 64)))
        {
            return Error(
                "learning_feedback_publication_change_hashing_boundary_exceeded",
                "The publication change exceeds a bounded field required before canonical hashing.",
                submitted.TargetRecordId);
        }

        return null;
    }

    private static bool ValidAccess(LearningFeedbackNamespaceAccess access) =>
        access.Capability == LearningFeedbackNamespaceCapability.Submit &&
        ValidIdentifier(access.TenantId) &&
        !string.IsNullOrWhiteSpace(access.Namespace) &&
        access.Namespace.Length <=
            LearningFeedbackContractLimits.MaximumNamespaceCharacters &&
        string.Equals(access.Namespace, access.Namespace.Trim(), StringComparison.Ordinal) &&
        ValidIdentifier(access.AuthorityId) &&
        ValidIdentifier(access.SubjectId) &&
        ValidIdentifier(access.ServiceId) &&
        access.PolicyRevision > 0;

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');

    private static bool ExactPrefixedHash(string? value, string prefix) =>
        value is not null &&
        value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        ExactHash(value[prefix.Length..]);

    private static bool ExactHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string NormalizeReason(string value) => value.Trim();

    private static CortexContractResult<LearningFeedbackPublicationChangeV1> Reject(
        string code,
        string message,
        string? targetRecordId,
        string? submittedHash) =>
        new(false, null, submittedHash, [Error(code, message, targetRecordId)]);

    private static PegasusDiagnostic Error(
        string code,
        string message,
        string? targetRecordId) =>
        new(
            code,
            message,
            DiagnosticSeverity.Error,
            "cortex.learning-feedback.publication-change.validate",
            false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["targetRecordId"] = ExactPrefixedHash(targetRecordId, "krec_")
                    ? targetRecordId
                    : null
            });
}
