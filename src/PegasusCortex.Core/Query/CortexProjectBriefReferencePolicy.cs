using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Core.Query;

/// <summary>
/// Creates and validates bounded project-brief references. Retained locators
/// carry exact repository identity and hashes but confer no read authority.
/// </summary>
public static class CortexProjectBriefReferencePolicy
{
    private const long MaximumRevision = 9_007_199_254_740_991;

    public static CortexProjectBriefReference CreateRetained(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string contentHash,
        string assertionHash)
    {
        var locator = new CortexProjectBriefRetainedRecordLocator(
            projectLinkId,
            projectId,
            projectNamespace,
            kind,
            documentId,
            revision,
            contentHash,
            assertionHash);
        var reference = new CortexProjectBriefReference(
            BuildReference(locator),
            contentHash,
            RecordId: null,
            RevisionId: null,
            locator);
        if (!IsValid(reference))
            throw new ArgumentException("The retained-record locator is invalid.");
        return reference;
    }

    public static bool IsValid(CortexProjectBriefReference? value)
    {
        if (value is null ||
            !ValidReference(value.Reference) ||
            !ValidSha256(value.Sha256))
        {
            return false;
        }

        var retainedScheme = value.Reference.StartsWith(
            "retained://",
            StringComparison.Ordinal);
        if (retainedScheme != (value.RetainedRecord is not null))
            return false;

        if (value.RetainedRecord is null)
        {
            return value.RecordId is null && value.RevisionId is null ||
                   IsPrefixedHash(value.RecordId, "krec_") &&
                   IsPrefixedHash(value.RevisionId, "krev_");
        }

        var locator = value.RetainedRecord;
        return value.RecordId is null &&
               value.RevisionId is null &&
               IsPrefixedHash(locator.ProjectLinkId, "plink_") &&
               ValidIdentifier(locator.ProjectId) &&
               ValidIdentifier(locator.ProjectNamespace) &&
               ValidRetainedKind(locator.Kind) &&
               ValidIdentifier(locator.DocumentId) &&
               locator.Revision is >= 1 and <= MaximumRevision &&
               ValidSha256(locator.ContentHash) &&
               ValidSha256(locator.AssertionHash) &&
               string.Equals(
                   value.Reference,
                   BuildReference(locator),
                   StringComparison.Ordinal) &&
               string.Equals(
                   value.Sha256,
                   locator.ContentHash,
                   StringComparison.Ordinal);
    }

    public static bool IsValidForProject(
        CortexProjectBriefReference? value,
        string projectLinkId,
        string projectId,
        string projectNamespace) =>
        IsValid(value) &&
        (value!.RetainedRecord is null ||
         string.Equals(
             value.RetainedRecord.ProjectLinkId,
             projectLinkId,
             StringComparison.Ordinal) &&
         string.Equals(
             value.RetainedRecord.ProjectId,
             projectId,
             StringComparison.Ordinal) &&
         string.Equals(
             value.RetainedRecord.ProjectNamespace,
             projectNamespace,
             StringComparison.Ordinal));

    public static string BuildReference(
        CortexProjectBriefRetainedRecordLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return $"retained://{locator.Kind}/{locator.ProjectId}/" +
               $"{locator.DocumentId}/{locator.Revision}";
    }

    private static bool ValidReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexProjectBriefContract.MaximumReferenceCharacters &&
        !value.Any(char.IsControl) &&
        (value.StartsWith("checkpoint://", StringComparison.Ordinal) ||
         value.StartsWith("artifact://", StringComparison.Ordinal) ||
         value.StartsWith("record://", StringComparison.Ordinal) ||
         value.StartsWith("revision://", StringComparison.Ordinal) ||
         value.StartsWith("hash://", StringComparison.Ordinal) ||
         value.StartsWith("retained://", StringComparison.Ordinal));

    private static bool ValidRetainedKind(string? value) =>
        value is
            CortexRetainedDocumentKinds.TaskSnapshot or
            CortexRetainedDocumentKinds.WorkPacket or
            CortexRetainedDocumentKinds.WorkPacketHandoffReceipt or
            CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt;

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexAgentRetentionLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');

    private static bool IsPrefixedHash(string? value, string prefix) =>
        value is not null &&
        value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept(
            "0123456789abcdef") < 0;

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
}
