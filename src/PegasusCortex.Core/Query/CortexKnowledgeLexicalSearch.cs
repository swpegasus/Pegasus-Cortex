using Pegasus.Cortex.Contracts.Query;

namespace Pegasus.Cortex.Core.Query;

/// <summary>
/// Maps the Core knowledge projection to the single provider-neutral lexical
/// matcher. Both static and durable query sources use this mapping so match
/// metadata cannot drift with provider-specific ranking or storage details.
/// </summary>
public static class CortexKnowledgeLexicalSearch
{
    public static CortexLexicalMatch Match(
        CortexKnowledgeRecord record,
        CortexSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(query);
        return CortexLexicalSearch.Match(
            query.Text,
            ModeName(query.Mode),
            [
                new CortexLexicalField(record.Title, 8, PhraseBoost: 20),
                new CortexLexicalField(record.Taxonomy, 5),
                new CortexLexicalField(string.Join(' ', record.Keywords), 5),
                new CortexLexicalField(record.Summary, 3),
                new CortexLexicalField(record.Content, 1)
            ]);
    }

    /// <summary>
    /// Produces a delimiter-safe list of exact provider lexemes. Broad mode uses
    /// the finite inverse of the shared suffix fold, so a provider does not need
    /// an over-inclusive prefix query before applying <see cref="Match"/>.
    /// </summary>
    public static string ProviderQueryText(CortexSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return string.Join(' ', CortexLexicalSearch.ProviderTerms(
            query.Text,
            ModeName(query.Mode)));
    }

    private static string ModeName(CortexSearchMode mode) => mode switch
    {
        CortexSearchMode.Exact => CortexSearchModes.Exact,
        CortexSearchMode.Broad => CortexSearchModes.Broad,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
