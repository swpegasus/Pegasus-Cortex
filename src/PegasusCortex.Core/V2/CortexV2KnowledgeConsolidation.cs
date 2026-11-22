using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Core.V2;

/// <summary>
/// Produces deterministic, read-only duplicate candidates. It never merges,
/// supersedes, archives, or otherwise mutates retained records.
/// </summary>
public static class CortexV2KnowledgeConsolidation
{
    public static IReadOnlyList<CortexConsolidationSuggestionV2> Analyze(
        IReadOnlyList<CortexKnowledgeV2> heads)
    {
        ArgumentNullException.ThrowIfNull(heads);
        var values = new List<CortexConsolidationSuggestionV2>();
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        void AddGroup(IEnumerable<IGrouping<string, CortexKnowledgeV2>> groups, string reason)
        {
            foreach (var group in groups.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(value => value.RecordId, StringComparer.Ordinal).ToArray();
                if (ordered.Length < 2)
                    continue;
                var canonical = ordered[0];
                foreach (var candidate in ordered.Skip(1))
                {
                    var pair = $"{canonical.Namespace}\n{canonical.RecordId}\n{candidate.RecordId}";
                    if (!pairs.Add(pair))
                        continue;
                    values.Add(new(
                        reason,
                        new(canonical.Namespace, CortexRecordTypeV2.Knowledge, canonical.RecordId, canonical.Revision),
                        new(candidate.Namespace, CortexRecordTypeV2.Knowledge, candidate.RecordId, candidate.Revision),
                        $"sha256:{Hash(group.Key)}"));
                }
            }
        }

        AddGroup(
            heads.GroupBy(
                value => $"{value.Kind}\n{value.Tier}\n{Normalize(value.Title)}\n{value.Content}",
                StringComparer.Ordinal),
            "exact-content");
        AddGroup(
            heads.GroupBy(
                value => $"{value.Kind}\n{value.Tier}\n{Normalize(value.Title)}",
                StringComparer.Ordinal),
            "normalized-title-review");
        return values
            .OrderBy(value => value.Reason, StringComparer.Ordinal)
            .ThenBy(value => value.Canonical.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.Canonical.RecordId, StringComparer.Ordinal)
            .ThenBy(value => value.Candidate.RecordId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Normalize(string value) => string.Join(' ', value
        .Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
