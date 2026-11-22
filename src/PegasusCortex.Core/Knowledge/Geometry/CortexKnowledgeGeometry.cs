using System.Globalization;
using System.Text;

namespace Pegasus.Cortex.Core.Knowledge.Geometry;

/// <summary>
/// One full-body-unhydrated point in a selected knowledge space. The vector is
/// derived from bounded title, tag, and indexed summary fields; exact record
/// content remains in the canonical store and is never copied into the
/// geometric result.
/// </summary>
public sealed record CortexKnowledgeGeometryPoint(
    string Key,
    string Namespace,
    string RecordId,
    long Revision,
    string Title,
    IReadOnlyList<string> Tags,
    string Summary,
    double LexicalScore,
    bool Direct);

public sealed record CortexKnowledgeGeometryRank(
    CortexKnowledgeGeometryPoint Point,
    double Score,
    double IntentSimilarity,
    double NeighborSimilarity,
    int SharedFeatures);

/// <summary>
/// Deterministic sparse geometry for the shared and user knowledge overlays.
/// It uses the same Unicode-folded coordinate system for every selected space,
/// while callers remain responsible for excluding spaces before supplying
/// points. No model download, external service, or independently authoritative
/// vector database is involved.
/// </summary>
public static partial class CortexKnowledgeGeometry
{
    private const int MaximumVectorTerms = 256;
    private static readonly HashSet<string> StopTerms = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in",
        "is", "it", "knowledge", "of", "on", "or", "shared", "that", "the", "this",
        "to", "user", "with", "general", "generalized", "programming"
    };

    public static IReadOnlyList<string> ExpansionTerms(
        string intent,
        IReadOnlyList<CortexKnowledgeGeometryPoint> directPoints,
        int maximumTerms = 24)
    {
        ArgumentNullException.ThrowIfNull(directPoints);
        if (maximumTerms is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maximumTerms));

        return FrontierExpansionTerms(intent, directPoints.Where(value => value.Direct).ToArray(), maximumTerms);
    }

    // Shared extraction for genuine frontier points; only the public legacy wrapper filters Direct.
    private static IReadOnlyList<string> FrontierExpansionTerms(
        string intent, IReadOnlyList<CortexKnowledgeGeometryPoint> points, int maximumTerms)
    {
        var queryTerms = Tokenize(intent, MaximumVectorTerms).ToHashSet(StringComparer.Ordinal);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var point in points
                     .OrderByDescending(value => value.LexicalScore)
                     .ThenBy(value => value.Namespace, StringComparer.Ordinal)
                     .ThenBy(value => value.RecordId, StringComparer.Ordinal)
                     .Take(8))
        {
            Add(weights, Tokenize(point.Title, 64), 4d);
            foreach (var tag in point.Tags)
                Add(weights, Tokenize(tag, 16), 6d);
            Add(weights, Tokenize(point.Summary, 128), 1d);
        }

        return weights
            .Where(value => !queryTerms.Contains(value.Key) && !StopTerms.Contains(value.Key))
            .OrderByDescending(value => value.Value)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .Take(maximumTerms)
            .Select(value => value.Key)
            .ToArray();
    }

    public static IReadOnlyList<CortexKnowledgeGeometryRank> Rank(
        string intent,
        IReadOnlyList<CortexKnowledgeGeometryPoint> points,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var unique = points
            .GroupBy(value => value.Key, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(value => value.Direct)
                .ThenByDescending(value => value.LexicalScore)
                .First())
            .ToArray();
        var query = UnitVector(BuildVector(intent, [], string.Empty));
        var vectors = unique.ToDictionary(
            value => value.Key,
            value => UnitVector(BuildVector(value.Title, value.Tags, value.Summary)),
            StringComparer.Ordinal);
        var seeds = unique
            .Where(value => value.Direct)
            .OrderByDescending(value => value.LexicalScore)
            .ThenBy(value => value.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.RecordId, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        var ranked = new List<CortexKnowledgeGeometryRank>(unique.Length);
        foreach (var point in unique)
        {
            var vector = vectors[point.Key];
            var intentSimilarity = Dot(query, vector);
            var neighborSimilarity = 0d;
            var sharedFeatures = 0;
            foreach (var seed in seeds)
            {
                if (string.Equals(seed.Key, point.Key, StringComparison.Ordinal))
                    continue;
                var seedVector = vectors[seed.Key];
                var similarity = Dot(seedVector, vector);
                if (similarity <= neighborSimilarity)
                    continue;
                neighborSimilarity = similarity;
                sharedFeatures = vector.Keys.Count(seedVector.ContainsKey);
            }

            // Direct keyword matches remain ahead of geometric neighbors and
            // retain the store's established lexical order. Geometry enriches
            // that direct ranking and orders related non-matches; it does not
            // replace a precise query with a semantically broader guess.
            var lexical = point.Direct
                ? Math.Max(0d, point.LexicalScore) / 1_000d
                : Math.Log10(1d + Math.Max(0d, point.LexicalScore)) / 10d;
            var score = (point.Direct ? 4d : 0d) +
                intentSimilarity * (point.Direct ? 0.5d : 1.25d) +
                neighborSimilarity * (point.Direct ? 0.25d : 0.75d) +
                lexical;
            ranked.Add(new(point, score, intentSimilarity, neighborSimilarity, sharedFeatures));
        }

        return ranked
            .OrderByDescending(value => value.Score)
            .ThenByDescending(value => value.Point.Direct)
            .ThenBy(value => value.Point.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.Point.RecordId, StringComparer.Ordinal)
            .ThenBy(value => value.Point.Revision)
            .Take(limit)
            .ToArray();
    }

    private static Dictionary<string, double> BuildVector(
        string title,
        IReadOnlyList<string> tags,
        string summary)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        Add(result, Tokenize(title, 64), 4d);
        foreach (var tag in tags)
            Add(result, Tokenize(tag, 16), 6d);
        Add(result, Tokenize(summary, 192), 1d);
        if (result.Count <= MaximumVectorTerms)
            return result;
        return result
            .OrderByDescending(value => value.Value)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .Take(MaximumVectorTerms)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, double> UnitVector(Dictionary<string, double> value)
    {
        var magnitude = Math.Sqrt(value.Values.Sum(weight => weight * weight));
        if (magnitude == 0d)
            return value;
        return value.ToDictionary(
            item => item.Key,
            item => item.Value / magnitude,
            StringComparer.Ordinal);
    }

    private static double Dot(
        IReadOnlyDictionary<string, double> left,
        IReadOnlyDictionary<string, double> right)
    {
        if (left.Count > right.Count)
            (left, right) = (right, left);
        var result = 0d;
        foreach (var item in left)
        {
            if (right.TryGetValue(item.Key, out var weight))
                result += item.Value * weight;
        }
        return result;
    }

    private static void Add(
        IDictionary<string, double> target,
        IEnumerable<string> terms,
        double weight)
    {
        foreach (var term in terms.Where(value => !StopTerms.Contains(value)))
        {
            target.TryGetValue(term, out var current);
            target[term] = current + weight;
        }
    }

    private static IReadOnlyList<string> Tokenize(string value, int maximumTerms)
    {
        var terms = new List<string>(maximumTerms);
        var current = new StringBuilder(64);
        foreach (var rune in value.EnumerateRunes())
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
            var normalized = current.ToString().Normalize(NormalizationForm.FormD);
            var folded = new StringBuilder(normalized.Length);
            foreach (var item in normalized.EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(item);
                if (category is not UnicodeCategory.NonSpacingMark and
                    not UnicodeCategory.SpacingCombiningMark and
                    not UnicodeCategory.EnclosingMark)
                {
                    folded.Append(item.ToString().ToLowerInvariant());
                }
            }
            terms.Add(folded.ToString().Normalize(NormalizationForm.FormC));
            current.Clear();
        }
    }
}
