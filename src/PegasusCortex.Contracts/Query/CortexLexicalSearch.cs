namespace Pegasus.Cortex.Contracts.Query;

/// <summary>
/// One bounded searchable field and its deterministic contribution to lexical rank.
/// A phrase boost is applied at most once when the complete trimmed query appears in
/// this field; individual term weights are still counted once per field.
/// </summary>
public sealed record CortexLexicalField(
    string Text,
    double Weight,
    double PhraseBoost = 0);

/// <summary>
/// Provider-neutral lexical match evidence. Matched terms are normalized, distinct,
/// ordinally sorted query terms so providers can expose stable explainability metadata.
/// </summary>
public sealed record CortexLexicalMatch(
    bool IsMatch,
    IReadOnlyList<string> MatchedTerms,
    int QueryTermCount,
    double WeightedScore);

/// <summary>
/// Shared lexical normalization and matching used by portable and durable providers.
/// Exact mode is conjunctive across all searchable fields. Broad mode keeps the
/// established minimum-one-term policy and conservative suffix folding.
/// </summary>
public static class CortexLexicalSearch
{
    private const int MaximumFields = 64;

    public static CortexLexicalMatch Match(
        string queryText,
        string mode,
        IReadOnlyList<CortexLexicalField> fields)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentNullException.ThrowIfNull(fields);
        ValidateMode(mode);
        if (fields.Count > MaximumFields)
            throw new ArgumentException("Lexical field count exceeds the bounded contract.", nameof(fields));

        var queryTerms = NormalizeTerms(queryText, mode);
        if (queryTerms.Count == 0)
            return new CortexLexicalMatch(false, Array.Empty<string>(), 0, 0);

        var fieldTerms = new IReadOnlySet<string>[fields.Count];
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index] ??
                throw new ArgumentException("Lexical fields cannot contain null entries.", nameof(fields));
            if (!double.IsFinite(field.Weight) || field.Weight < 0 ||
                !double.IsFinite(field.PhraseBoost) || field.PhraseBoost < 0)
            {
                throw new ArgumentException("Lexical field weights must be finite and non-negative.", nameof(fields));
            }
            fieldTerms[index] = NormalizeTermSet(field.Text, mode);
        }

        var matched = new List<string>(queryTerms.Count);
        double weightedScore = 0;
        foreach (var term in queryTerms)
        {
            var termMatched = false;
            for (var index = 0; index < fields.Count; index++)
            {
                if (!fieldTerms[index].Contains(term))
                    continue;
                termMatched = true;
                weightedScore += fields[index].Weight;
            }
            if (termMatched)
                matched.Add(term);
        }

        var trimmedQuery = queryText.Trim();
        if (trimmedQuery.Length != 0)
        {
            foreach (var field in fields)
            {
                if (field.PhraseBoost > 0 &&
                    field.Text.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    weightedScore += field.PhraseBoost;
                }
            }
        }

        var isMatch = string.Equals(mode, CortexSearchModes.Exact, StringComparison.Ordinal)
            ? matched.Count == queryTerms.Count
            : matched.Count > 0;
        return new CortexLexicalMatch(
            isMatch,
            Array.AsReadOnly(matched.ToArray()),
            queryTerms.Count,
            weightedScore);
    }

    /// <summary>
    /// Returns normalized distinct query terms in ordinal order. Durable providers
    /// may join these terms for a parameterized database candidate query, then use
    /// <see cref="Match"/> as the authoritative provider-neutral match check.
    /// </summary>
    public static IReadOnlyList<string> NormalizeTerms(string value, string mode)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateMode(mode);
        return Array.AsReadOnly(NormalizeTermSet(value, mode)
            .Order(StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>
    /// Returns the exact raw lexemes a durable provider may OR together for its
    /// candidate query. Broad mode expands only lexemes whose single lightweight
    /// fold equals a normalized query term, avoiding over-inclusive prefix matches.
    /// </summary>
    public static IReadOnlyList<string> ProviderTerms(string value, string mode)
    {
        var normalizedTerms = NormalizeTerms(value, mode);
        if (string.Equals(mode, CortexSearchModes.Exact, StringComparison.Ordinal))
            return normalizedTerms;

        var providerTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in normalizedTerms)
        {
            AddIfEquivalent(term);
            AddIfEquivalent(term + "s");
            AddIfEquivalent(term + "ed");
            AddIfEquivalent(term + "es");
            AddIfEquivalent(term + "ing");

            void AddIfEquivalent(string candidate)
            {
                if (string.Equals(FoldBroadTerm(candidate), term, StringComparison.Ordinal))
                    providerTerms.Add(candidate);
            }
        }

        return Array.AsReadOnly(providerTerms
            .Order(StringComparer.Ordinal)
            .ToArray());
    }

    private static HashSet<string> NormalizeTermSet(string value, string mode)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new List<char>();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer.Add(char.ToLowerInvariant(character));
                continue;
            }
            FlushTerm(buffer, terms, mode);
        }
        FlushTerm(buffer, terms, mode);
        return terms;
    }

    private static void FlushTerm(
        ICollection<char> buffer,
        ISet<string> terms,
        string mode)
    {
        if (buffer.Count != 0)
        {
            var term = new string(buffer.ToArray());
            terms.Add(string.Equals(mode, CortexSearchModes.Broad, StringComparison.Ordinal)
                ? FoldBroadTerm(term)
                : term);
        }
        buffer.Clear();
    }

    // This intentionally mirrors the established lightweight broad-search fold.
    // It is not a language stemmer and must remain deterministic across providers.
    private static string FoldBroadTerm(string term)
    {
        if (term.Length > 5 && term.EndsWith("ing", StringComparison.Ordinal))
            return term[..^3];
        if (term.Length > 4 &&
            (term.EndsWith("ed", StringComparison.Ordinal) ||
             term.EndsWith("es", StringComparison.Ordinal)))
        {
            return term[..^2];
        }
        if (term.Length > 3 && term.EndsWith('s'))
            return term[..^1];
        return term;
    }

    private static void ValidateMode(string mode)
    {
        if (!CortexSearchModes.IsSupported(mode))
            throw new ArgumentException("Lexical search mode is unsupported.", nameof(mode));
    }
}
