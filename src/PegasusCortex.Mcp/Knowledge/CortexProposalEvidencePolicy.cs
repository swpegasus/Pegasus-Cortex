using Pegasus.Cortex.Contracts.Learning;

namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Shared defensive capture for optional caller-reported proposal evidence.
/// Runtime adapters call this even after MCP parsing so direct callers cannot
/// bypass the same bounds or canonical ordering. This policy validates
/// coordinates and hashes; it does not resolve the referenced artifact.
/// </summary>
public static class CortexProposalEvidencePolicy
{
    public const int MaximumItems = 8;

    public static IReadOnlyList<LearningFeedbackEvidenceV1> Capture(
        IEnumerable<LearningFeedbackEvidenceV1> evidenceReferences)
    {
        ArgumentNullException.ThrowIfNull(evidenceReferences);
        var captured = new List<LearningFeedbackEvidenceV1>(
            MaximumItems);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidenceReferences.Take(
                     MaximumItems + 1))
        {
            if (captured.Count == MaximumItems ||
                item is null ||
                !ValidIdentifier(item.EvidenceId) ||
                !identifiers.Add(item.EvidenceId) ||
                !ValidReference(item.Reference) ||
                !ValidMediaType(item.MediaType) ||
                !ValidSha256(item.Sha256) ||
                !string.Equals(
                    item.Verification,
                    "verified",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Proposal evidence must be unique, bounded, verified, and use exact lowercase SHA-256.",
                    nameof(evidenceReferences));
            }
            captured.Add(item with { });
        }

        return Array.AsReadOnly(
            captured
                .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray());
    }

    public static string ScreeningProjection(
        IReadOnlyList<LearningFeedbackEvidenceV1> evidenceReferences)
    {
        ArgumentNullException.ThrowIfNull(evidenceReferences);
        return string.Join(
            '\n',
            evidenceReferences.Select(item =>
                $"{item.EvidenceId} {item.Reference} {item.MediaType} {item.Verification}"));
    }

    private static bool ValidIdentifier(string? value) =>
        value is { Length: > 0 } &&
        value.Length <= CortexMcpContract.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');

    private static bool ValidReference(string? value) =>
        value is { Length: > 0 } &&
        value.Length <=
            LearningFeedbackContractLimits.MaximumEvidenceReferenceCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl) &&
        (value.StartsWith("artifact://", StringComparison.Ordinal) ||
         value.StartsWith("record://", StringComparison.Ordinal) ||
         value.StartsWith("revision://", StringComparison.Ordinal) ||
         value.StartsWith("hash://", StringComparison.Ordinal));

    private static bool ValidMediaType(string? value) =>
        value is { Length: > 0 } &&
        value.Length <= CortexMcpContract.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '/' or '+' or '.' or '-');

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
}
