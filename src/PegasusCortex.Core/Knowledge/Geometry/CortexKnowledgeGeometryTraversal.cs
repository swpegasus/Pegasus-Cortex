using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pegasus.Cortex.Core.Knowledge.Geometry;

/// <summary>Opt-in, bounded candidate exploration. Similarity edges are not logical evidence.</summary>
public sealed record CortexKnowledgeTraversalOptions(
    bool Enabled = false, int MaximumDepth = 2, int BeamWidth = 4,
    int MaximumQueries = 12, int CandidatesPerQuery = 32, int MaximumVisited = 128,
    int TermsPerParent = 8, int MaximumCandidateOccurrences = 384,
    int MaximumDurationMilliseconds = 5000, double HopDecay = .8,
    double OriginalIntentWeight = .7, double MinimumEdgeSimilarity = .08,
    int MinimumSharedFeatures = 1, int ResultLimit = 4)
{
    public void Validate()
    {
        if (MaximumDepth is < 1 or > 3 || BeamWidth is < 1 or > 8 ||
            MaximumQueries is < 1 or > 32 || CandidatesPerQuery is < 1 or > 64 ||
            MaximumVisited is < 32 or > 256 || TermsPerParent is < 1 or > 24 ||
            MaximumCandidateOccurrences is < 1 or > 2048 ||
            MaximumDurationMilliseconds is < 1 or > 30000 || ResultLimit is < 1 or > 32 ||
            !double.IsFinite(HopDecay) || HopDecay is <= 0 or > 1 ||
            !double.IsFinite(OriginalIntentWeight) || OriginalIntentWeight is < 0 or > 1 ||
            !double.IsFinite(MinimumEdgeSimilarity) || MinimumEdgeSimilarity is < 0 or > 1 ||
            MinimumSharedFeatures is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(CortexKnowledgeTraversalOptions));
    }
}

public sealed record CortexKnowledgeTraversalCoordinate(string Namespace, string Kind, string RecordId, long Revision);
public sealed record CortexKnowledgeTraversalRequest(
    int Ordinal, CortexKnowledgeTraversalCoordinate Parent, int Depth,
    IReadOnlyList<string> Terms, int Limit);
public sealed record CortexKnowledgeTraversalQuery(
    CortexKnowledgeTraversalRequest Request, int Returned, bool AtLimit,
    IReadOnlyList<CortexKnowledgeTraversalCoordinate> Candidates, string Status);
public sealed record CortexKnowledgeTraversalNode(
    CortexKnowledgeTraversalCoordinate Coordinate, CortexKnowledgeGeometryPoint Point,
    int Depth, CortexKnowledgeTraversalCoordinate? Predecessor, int QueryOrdinal,
    double IntentSimilarity, double EdgeSimilarity, int SharedFeatures,
    double PathStrength, double Score, IReadOnlyList<CortexKnowledgeTraversalCoordinate> Path);
public sealed record CortexKnowledgeTraversalResult(
    bool Enabled, string StopReason, bool Truncated,
    IReadOnlyList<CortexKnowledgeTraversalNode> Ranked,
    IReadOnlyList<CortexKnowledgeTraversalNode> Admitted,
    IReadOnlyList<CortexKnowledgeTraversalQuery> Queries, int CandidateOccurrences);

public static partial class CortexKnowledgeGeometry
{
    /// <summary>
    /// Breadth-first bounded exploration. Each admitted node retains the best encountered
    /// parent at its first admitted depth, not a globally optimal path. Incomplete
    /// depth proposals are discarded at an early budget/deadline stop. Later frontier
    /// points remain non-direct. Identity never depends on caller-supplied Point.Key.
    /// The provider must honor cancellation and the per-request result limit.
    /// </summary>
    public static async ValueTask<CortexKnowledgeTraversalResult> TraverseAsync(
        string intent, IReadOnlyList<string> namespaces,
        IReadOnlyList<CortexKnowledgeGeometryPoint> directSeeds,
        Func<CortexKnowledgeTraversalRequest, CancellationToken,
            ValueTask<IReadOnlyList<CortexKnowledgeGeometryPoint>>> findNeighbors,
        CortexKnowledgeTraversalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        ArgumentNullException.ThrowIfNull(directSeeds);
        ArgumentNullException.ThrowIfNull(findNeighbors);
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(intent) || Encoding.UTF8.GetByteCount(intent) > 4096 ||
            namespaces.Count is < 1 or > 4 || namespaces.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) ||
            namespaces.Distinct(StringComparer.Ordinal).Count() != namespaces.Count ||
            directSeeds.Count > 32)
            throw new ArgumentException("Invalid bounded traversal input.");
        if (!options.Enabled)
            return new(false, "disabled", false, [], [], [], 0);

        var selected = namespaces.ToHashSet(StringComparer.Ordinal);
        var admitted = new Dictionary<CortexKnowledgeTraversalCoordinate, CortexKnowledgeTraversalNode>();
        var vectors = new Dictionary<CortexKnowledgeTraversalCoordinate, Dictionary<string, double>>();
        var projections = new Dictionary<CortexKnowledgeTraversalCoordinate, CortexKnowledgeGeometryPoint>();
        var queryVector = UnitVector(BuildVector(intent, [], string.Empty));
        var queries = new List<CortexKnowledgeTraversalQuery>();
        var occurrences = 0;
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(options.MaximumDurationMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
        foreach (var input in directSeeds) { linked.Token.ThrowIfCancellationRequested(); ValidatePoint(input); }
        foreach (var point in directSeeds.Select(value => value with { Tags = value.Tags.ToArray() }).OrderByDescending(value => value.LexicalScore)
                     .ThenBy(value => value.Namespace, StringComparer.Ordinal)
                     .ThenBy(value => value.RecordId, StringComparer.Ordinal).ThenBy(value => value.Revision))
        {
            linked.Token.ThrowIfCancellationRequested();
            ValidatePoint(point);
            if (!point.Direct || !selected.Contains(point.Namespace))
                throw new ArgumentException("Seed is not an admitted direct knowledge projection.");
            var coordinate = Coordinate(point);
            var vector = Vector(point);
            if (admitted.TryGetValue(coordinate, out var prior))
            {
                RequireSameProjection(prior.Point, point);
                continue;
            }
            var similarity = Dot(queryVector, vector);
            // A seed has no record-to-record edge. Lexical scores select initial beams,
            // but do not become fabricated similarity/path evidence.
            admitted.Add(coordinate, new(coordinate, point, 0, null, 0, similarity, 0, 0, 0,
                options.OriginalIntentWeight * similarity, [coordinate]));
        }
        linked.Token.ThrowIfCancellationRequested();
        if (admitted.Count == 0)
            return Finish("zero-seeds", false);
        var frontier = admitted.Values.OrderByDescending(value => value.Point.LexicalScore)
            .ThenBy(value => value.Coordinate.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.Coordinate.RecordId, StringComparer.Ordinal)
            .ThenBy(value => value.Coordinate.Revision).Take(options.BeamWidth).ToArray();
        var beamPruned = admitted.Count > frontier.Length;

            for (var depth = 1; depth <= options.MaximumDepth; depth++)
            {
                var proposals = new Dictionary<CortexKnowledgeTraversalCoordinate, CortexKnowledgeTraversalNode>();
                foreach (var parent in frontier)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested || clock.ElapsedMilliseconds >= options.MaximumDurationMilliseconds)
                        return Finish("time-limit", true);
                    if (queries.Count == options.MaximumQueries) return Finish("query-limit", true);
                    var remaining = options.MaximumCandidateOccurrences - occurrences;
                    if (remaining == 0) return Finish("candidate-limit", true);
                    var terms = FrontierExpansionTerms(intent, [parent.Point], options.TermsPerParent);
                    // Bound the joined query independently of the tokenizer's term count.
                    var boundedTerms = new List<string>();
                    foreach (var term in terms)
                    {
                        if (Encoding.UTF8.GetByteCount(string.Join(' ', boundedTerms.Append(term))) > 512 ||
                            JsonSerializer.SerializeToUtf8Bytes(boundedTerms.Append(term).ToArray()).Length > 512) break;
                        boundedTerms.Add(term);
                    }
                    if (boundedTerms.Count == 0) continue;
                    var request = new CortexKnowledgeTraversalRequest(queries.Count + 1, parent.Coordinate,
                        depth, boundedTerms.ToArray(), Math.Min(options.CandidatesPerQuery, remaining));
                    queries.Add(new(request, 0, false, [], "started"));
                    var candidates = await findNeighbors(request, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested)
                    {
                        queries[^1] = queries[^1] with { Status = "deadline" };
                        return Finish("time-limit", true);
                    }
                    if (candidates is null || candidates.Count > request.Limit)
                        throw new InvalidOperationException("Neighbor provider exceeded its bounded result contract.");
                    candidates = candidates.Select(value => { ValidatePoint(value); return value with { Tags = value.Tags.ToArray() }; }).ToArray();
                    occurrences += candidates.Count;
                    var coordinates = new List<CortexKnowledgeTraversalCoordinate>(candidates.Count);
                    foreach (var candidate in candidates)
                    {
                        ValidatePoint(candidate);
                        var coordinate = Coordinate(candidate);
                        if (!selected.Contains(candidate.Namespace) || candidate.Namespace != parent.Coordinate.Namespace)
                            throw new InvalidOperationException("Neighbor escaped its selected parent namespace.");
                        coordinates.Add(coordinate);
                    }
                    queries[^1] = new(request, candidates.Count, candidates.Count == request.Limit, coordinates, "completed");
                    foreach (var candidate in candidates.OrderBy(value => value.Namespace, StringComparer.Ordinal)
                                 .ThenBy(value => value.RecordId, StringComparer.Ordinal).ThenBy(value => value.Revision))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        var coordinate = Coordinate(candidate);
                        if (admitted.TryGetValue(coordinate, out var visited))
                        {
                            RequireSameProjection(visited.Point, candidate);
                            continue;
                        }
                        var vector = Vector(candidate);
                        var parentVector = vectors[parent.Coordinate];
                        var edge = Dot(parentVector, vector);
                        var shared = vector.Keys.Count(parentVector.ContainsKey);
                        if (edge < options.MinimumEdgeSimilarity || shared < options.MinimumSharedFeatures) continue;
                        var intentSimilarity = Dot(queryVector, vector);
                        var strength = parent.Depth == 0 ? edge : Math.Min(parent.PathStrength, edge);
                        var score = Math.Pow(options.HopDecay, depth) *
                            (options.OriginalIntentWeight * intentSimilarity +
                             (1 - options.OriginalIntentWeight) * strength);
                        var node = new CortexKnowledgeTraversalNode(coordinate, candidate with { Direct = false },
                            depth, parent.Coordinate, request.Ordinal, intentSimilarity, edge, shared, strength, score,
                            parent.Path.Append(coordinate).ToArray());
                        if (!proposals.TryGetValue(coordinate, out var previous) ||
                            node.Score > previous.Score ||
                            node.Score == previous.Score && node.QueryOrdinal < previous.QueryOrdinal)
                        {
                            if (previous is not null) RequireSameProjection(previous.Point, candidate);
                            proposals[coordinate] = node;
                        }
                    }
                }
                var next = Order(proposals.Values).ToArray();
                var capacity = options.MaximumVisited - admitted.Count;
                foreach (var node in next.Take(capacity))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    admitted.Add(node.Coordinate, node);
                }
                if (next.Length > capacity) return Finish("visited-limit", true);
                linked.Token.ThrowIfCancellationRequested();
                if (next.Length == 0) return Finish("frontier-exhausted", beamPruned || queries.Any(value => value.AtLimit));
                frontier = next.Take(options.BeamWidth).ToArray();
                beamPruned |= next.Length > frontier.Length;
            }
            linked.Token.ThrowIfCancellationRequested();
            return Finish("depth-limit", beamPruned || queries.Any(value => value.AtLimit));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            if (queries.Count > 0 && queries[^1].Status == "started")
                queries[^1] = queries[^1] with { Status = "deadline" };
            return Finish("time-limit", true);
        }

        CortexKnowledgeTraversalResult Finish(string reason, bool truncated)
        {
            var nodes = Order(admitted.Values).ToArray();
            return new(true, reason, truncated, nodes.Take(options.ResultLimit).ToArray(), nodes, queries.ToArray(), occurrences);
        }
        Dictionary<string, double> Vector(CortexKnowledgeGeometryPoint point)
        {
            var coordinate = Coordinate(point);
            if (vectors.TryGetValue(coordinate, out var value))
            {
                RequireSameProjection(projections[coordinate], point);
                return value;
            }
            value = UnitVector(BuildVector(point.Title, point.Tags, point.Summary));
            vectors.Add(coordinate, value);
            projections.Add(coordinate, point);
            return value;
        }
    }

    private static IEnumerable<CortexKnowledgeTraversalNode> Order(IEnumerable<CortexKnowledgeTraversalNode> values) =>
        values.OrderByDescending(value => value.Score).ThenBy(value => value.Depth)
            .ThenBy(value => value.Coordinate.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.Coordinate.RecordId, StringComparer.Ordinal).ThenBy(value => value.Coordinate.Revision);

    private static CortexKnowledgeTraversalCoordinate Coordinate(CortexKnowledgeGeometryPoint value) =>
        new(value.Namespace, "knowledge", value.RecordId, value.Revision);

    private static void ValidatePoint(CortexKnowledgeGeometryPoint value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.Namespace) || value.Namespace.Length > 256 || value.Namespace.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(value.RecordId) || value.RecordId.Length > 256 || value.RecordId.Any(char.IsControl) || value.Revision < 1 ||
            value.Title is null || value.Title.Length > 1024 || value.Summary is null || value.Summary.Length > 4096 ||
            value.Tags is null || value.Tags.Count > 32 || value.Tags.Any(tag => tag is null || tag.Length > 256) ||
            !double.IsFinite(value.LexicalScore))
            throw new ArgumentException("Invalid bounded knowledge projection.");
    }

    private static void RequireSameProjection(CortexKnowledgeGeometryPoint left, CortexKnowledgeGeometryPoint right)
    {
        if (left.Title != right.Title || left.Summary != right.Summary || !left.Tags.SequenceEqual(right.Tags))
            throw new InvalidOperationException("One immutable coordinate had inconsistent projection data.");
    }
}
