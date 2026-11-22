using Pegasus.Cortex.Contracts.Learning;

namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Captures optional proposal 1.1 metadata before it participates in screening
/// or idempotency identity. Contract policy performs the authoritative recheck.
/// </summary>
public static class CortexProposalMetadataPolicy
{
    public static bool TryCapture(
        CortexProposalMetadataV1? source,
        out CortexProposalMetadataV1? captured)
    {
        captured = null;
        if (source is null)
            return true;
        if (source.Keywords is null || source.Applicability is null ||
            source.Taxonomy is not null && !ValidIdentifier(source.Taxonomy))
        {
            return false;
        }

        var keywords = source.Keywords
            .Take(LearningFeedbackContractLimits.MaximumProposalKeywords + 1)
            .ToArray();
        if (keywords.Length > LearningFeedbackContractLimits.MaximumProposalKeywords ||
            keywords.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > LearningFeedbackContractLimits.MaximumProposalKeywordCharacters ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.Any(char.IsControl)) ||
            keywords.Distinct(StringComparer.Ordinal).Count() != keywords.Length)
        {
            return false;
        }

        var applicability = source.Applicability
            .Take(LearningFeedbackContractLimits.MaximumProposalApplicabilityItems + 1)
            .ToArray();
        if (applicability.Length >
                LearningFeedbackContractLimits.MaximumProposalApplicabilityItems ||
            applicability.Any(item =>
                item is null ||
                !ValidIdentifier(item.ProductId) ||
                !ValidIdentifier(item.ComponentId)) ||
            applicability
                .Select(item => (item.ProductId, item.ComponentId))
                .Distinct()
                .Count() != applicability.Length)
        {
            return false;
        }

        captured = new CortexProposalMetadataV1(
            source.Taxonomy,
            Array.AsReadOnly(keywords.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(
                applicability
                    .OrderBy(item => item.ProductId, StringComparer.Ordinal)
                    .ThenBy(item => item.ComponentId, StringComparer.Ordinal)
                    .Select(item => item with { })
                    .ToArray()));
        return true;
    }

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= LearningFeedbackContractLimits.MaximumIdentifierCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}
