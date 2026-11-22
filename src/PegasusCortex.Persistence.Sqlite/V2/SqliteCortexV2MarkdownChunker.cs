using System.Globalization;
using System.Text;

namespace Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class SqliteCortexV2MarkdownChunker
{
    public const int MaximumChunkCharacters = 2_048;

    public static IReadOnlyList<string> Chunk(string content, int maximumChunks)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumChunks < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumChunks));

        var blocks = SemanticBlocks(content);
        var chunks = new List<string>(Math.Min(blocks.Count, maximumChunks));
        var packed = new StringBuilder(MaximumChunkCharacters);
        foreach (var block in blocks)
        {
            foreach (var piece in SplitBounded(block))
            {
                if (packed.Length == 0)
                {
                    packed.Append(piece);
                    continue;
                }

                // Semantic blocks remain visibly separated in normalized index
                // text, but small headings and paragraphs share one bounded FTS
                // row. Exact Markdown remains independently stored in the blob.
                if (packed.Length + 2 + piece.Length <= MaximumChunkCharacters)
                {
                    packed.Append("\n\n");
                    packed.Append(piece);
                    continue;
                }

                FlushPacked();
                packed.Append(piece);
            }
        }

        FlushPacked();

        if (chunks.Count == 0)
            chunks.Add(string.Empty);
        return chunks.AsReadOnly();

        void FlushPacked()
        {
            if (packed.Length == 0)
                return;
            if (chunks.Count == maximumChunks)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(content), "The document exceeds the bounded search-chunk count.");
            }
            chunks.Add(packed.ToString());
            packed.Clear();
        }
    }

    public static IReadOnlyList<string> QueryTerms(string text, int maximumTerms = 32)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumTerms < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumTerms));
        var terms = new List<string>(maximumTerms);
        var current = new StringBuilder(64);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or
                UnicodeCategory.OtherNumber or
                UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark)
            {
                if (current.Length < 128)
                    current.Append(rune.ToString().ToLowerInvariant());
                continue;
            }
            Flush();
            if (terms.Count == maximumTerms)
                break;
        }
        Flush();
        return terms.Distinct(StringComparer.Ordinal).ToArray();

        void Flush()
        {
            if (current.Length == 0 || terms.Count == maximumTerms)
                return;
            terms.Add(Fold(current.ToString()));
            current.Clear();
        }
    }

    public static int CountMatches(IEnumerable<string> queryTerms, params string?[] fields)
        => MatchingTerms(queryTerms, fields).Count;

    /// <summary>
    /// Tokenizes each candidate field once and returns the query terms present
    /// in that candidate. Search calls this once per FTS row; re-tokenizing the
    /// same Markdown once per query term makes latency grow as rows x terms x
    /// document length even though FTS already bounded the candidate set.
    /// </summary>
    public static IReadOnlyList<string> MatchingTerms(
        IEnumerable<string> queryTerms,
        params string?[] fields)
    {
        var searchable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields.Where(value => !string.IsNullOrEmpty(value)))
        {
            foreach (var token in QueryTerms(field!, 16_384))
                searchable.Add(token);
        }
        return queryTerms.Where(searchable.Contains).ToArray();
    }

    private static List<string> SemanticBlocks(string content)
    {
        var blocks = new List<string>();
        var block = new StringBuilder();
        var inFence = false;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            var fence = trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal);
            var heading = !inFence && IsHeading(trimmed);
            var blank = !inFence && string.IsNullOrWhiteSpace(line);

            if ((heading || blank) && block.Length > 0)
                Flush();
            if (!blank || inFence)
            {
                if (block.Length > 0)
                    block.Append('\n');
                block.Append(line);
            }
            if (fence)
            {
                inFence = !inFence;
                if (!inFence)
                    Flush();
            }
            else if (heading)
            {
                Flush();
            }
        }
        Flush();
        return blocks;

        void Flush()
        {
            if (block.Length == 0)
                return;
            blocks.Add(block.ToString());
            block.Clear();
        }
    }

    private static IEnumerable<string> SplitBounded(string block)
    {
        var remaining = block;
        while (remaining.Length > MaximumChunkCharacters)
        {
            var boundary = MaximumChunkCharacters;
            if (boundary < remaining.Length && char.IsLowSurrogate(remaining[boundary]))
                boundary--;
            var floor = Math.Max(1, boundary / 2);
            var candidate = remaining.AsSpan(0, boundary).LastIndexOfAny('\n', ' ', '\t');
            if (candidate >= floor)
                boundary = candidate;
            yield return remaining[..boundary].TrimEnd();
            remaining = remaining[boundary..].TrimStart();
        }
        if (remaining.Length > 0)
            yield return remaining;
    }

    private static bool IsHeading(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && hashes < 6 && line[hashes] == '#')
            hashes++;
        return hashes > 0 && hashes < line.Length && char.IsWhiteSpace(line[hashes]);
    }

    private static string Fold(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not UnicodeCategory.NonSpacingMark and
                not UnicodeCategory.SpacingCombiningMark and
                not UnicodeCategory.EnclosingMark)
            {
                result.Append(rune.ToString().ToLowerInvariant());
            }
        }
        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}
