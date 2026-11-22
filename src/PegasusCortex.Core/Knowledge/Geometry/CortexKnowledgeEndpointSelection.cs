using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pegasus.Cortex.Core.Knowledge.Geometry;

/// <summary>Only already-known canonical/provenance fingerprints may establish equivalence.
/// No preview/vector fingerprint is a canonical-content fingerprint.</summary>
public sealed record CortexKnowledgeEndpointIdentity(CortexKnowledgeTraversalCoordinate Coordinate,
    string? CanonicalContentSha256, string? ProvenanceSha256);

public sealed record CortexKnowledgeEndpointSelectionOptions(bool Enabled = false, int Limit = 4,
    int MaximumCandidates = 128, double DiversityPenalty = .04, int MaximumMilliseconds = 5000)
{
    public void Validate()
    {
        if (Limit is < 1 or > 16 || MaximumCandidates is < 1 or > 256 || Limit > MaximumCandidates ||
            !double.IsFinite(DiversityPenalty) || DiversityPenalty is < 0 or > .25 ||
            MaximumMilliseconds is < 1 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(CortexKnowledgeEndpointSelectionOptions));
    }
}

public sealed record CortexKnowledgeEndpointFeatures(
    CortexKnowledgeTraversalCoordinate Root, double RootHeaderIntent, double RootFullIntent,
    double RootAnchor, double RetainedAnchor, double HeaderIntent, double FullIntent,
    double HeaderCoverage, double HeaderBodyAgreement, double SelfSupport,
    double IncomingHeaderAgreement, double EndpointEvidence, double UncorroboratedMention);

public sealed record CortexKnowledgeEndpointChoice(CortexKnowledgeTraversalNode Node,
    double TraversalScore, double? SelectionScore, CortexKnowledgeEndpointFeatures? Features,
    IReadOnlyList<CortexKnowledgeTraversalCoordinate> EquivalentCoordinates);
public sealed record CortexKnowledgeEndpointDecision(CortexKnowledgeTraversalCoordinate Coordinate,
    double TraversalScore, double? BaseSelectionScore, double? DiversityDiscount, double? SelectionScore,
    bool Selected, CortexKnowledgeTraversalCoordinate? EquivalentRepresentative, IReadOnlyList<string> Reasons);
public sealed record CortexKnowledgeEndpointSelectionResult(string PolicyVersion, bool Enabled,
    string StopReason, bool TraversalWasTruncated, bool SelectionWasTruncated,
    IReadOnlyList<CortexKnowledgeEndpointChoice> Selected,
    IReadOnlyList<CortexKnowledgeEndpointDecision> Decisions);

public static partial class CortexKnowledgeGeometry
{
    public const string EndpointSelectionPolicyVersion = "root-anchor-endpoint/1";

    /// <summary>
    /// Pure post-traversal selection: no provider, canonical body read or graph mutation.
    /// Relevance is carried only through each node's recorded root/path. Self-support
    /// and incoming-header agreement are lexical endpoint estimates, not entailment.
    /// No depth bonus, query-specific vocabulary, quote rule or model is used.
    /// Unknown canonical/provenance identity prevents hard duplicate collapse.
    /// </summary>
    public static CortexKnowledgeEndpointSelectionResult SelectEndpoints(
        string intent, CortexKnowledgeTraversalResult traversal,
        IReadOnlyList<CortexKnowledgeEndpointIdentity>? identities = null,
        CortexKnowledgeEndpointSelectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traversal);
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(intent) || Encoding.UTF8.GetByteCount(intent) > 4096 ||
            traversal.Admitted.Count > options.MaximumCandidates || traversal.Ranked.Count > 32 ||
            traversal.Queries.Count > 32 || traversal.Queries.Any(query => query.Candidates.Count > 64 || query.Request.Terms.Count > 24))
            throw new ArgumentException("Invalid bounded endpoint input.");
        if (!options.Enabled)
            return new(EndpointSelectionPolicyVersion, false, "disabled", traversal.Truncated, false,
                traversal.Ranked.Take(options.Limit).Select(node => new CortexKnowledgeEndpointChoice(
                    node, node.Score, null, null, [node.Coordinate])).ToArray(), []);

        var nodes = traversal.Admitted.ToDictionary(node => node.Coordinate);
        identities ??= [];
        if (identities.Count > options.MaximumCandidates ||
            identities.Select(value => value.Coordinate).Distinct().Count() != identities.Count ||
            identities.Any(value => !nodes.ContainsKey(value.Coordinate) ||
                !OptionalHash(value.CanonicalContentSha256) || !OptionalHash(value.ProvenanceSha256)))
            throw new ArgumentException("Invalid bounded endpoint identities.");
        var identityMap = identities.ToDictionary(value => value.Coordinate);
        var clock = Stopwatch.StartNew();
        var query = UnitVector(BuildVector(intent, [], string.Empty));
        var headers = new Dictionary<CortexKnowledgeTraversalCoordinate, Dictionary<string, double>>();
        var full = new Dictionary<CortexKnowledgeTraversalCoordinate, Dictionary<string, double>>();
        foreach (var node in nodes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= options.MaximumMilliseconds) return Timeout("time-limit-validation");
            ValidatePoint(node.Point);
            if (node.Coordinate != Coordinate(node.Point) || node.Depth is < 0 or > 3 ||
                node.Path.Count != node.Depth + 1 || node.Path.Distinct().Count() != node.Path.Count ||
                node.Path[^1] != node.Coordinate || !double.IsFinite(node.Score) ||
                !double.IsFinite(node.PathStrength) || node.PathStrength is < 0 or > 1.0000000001)
                throw new ArgumentException("Invalid traversal coordinate/path evidence.");
            if (node.Depth == 0)
            {
                if (!node.Point.Direct || node.Predecessor is not null || node.QueryOrdinal != 0)
                    throw new ArgumentException("Invalid traversal root.");
            }
            else
            {
                if (node.Point.Direct || node.Predecessor is null ||
                    !nodes.TryGetValue(node.Predecessor, out var parent) || parent.Depth != node.Depth - 1 ||
                    !parent.Path.SequenceEqual(node.Path.Take(node.Path.Count - 1)) ||
                    !traversal.Queries.Any(value => value.Status == "completed" &&
                        value.Request.Ordinal == node.QueryOrdinal && value.Request.Parent == node.Predecessor &&
                        value.Request.Depth == node.Depth && value.Candidates.Contains(node.Coordinate)))
                    throw new ArgumentException("Unproven traversal predecessor.");
            }
            headers.Add(node.Coordinate, UnitVector(BuildVector(node.Point.Title, node.Point.Tags, string.Empty)));
            full.Add(node.Coordinate, UnitVector(BuildVector(node.Point.Title, node.Point.Tags, node.Point.Summary)));
        }

        var scored = new List<EndpointWork>(nodes.Count);
        foreach (var node in nodes.Values.OrderBy(value => value.Coordinate.Namespace, StringComparer.Ordinal)
                     .ThenBy(value => value.Coordinate.RecordId, StringComparer.Ordinal).ThenBy(value => value.Coordinate.Revision))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= options.MaximumMilliseconds) return Timeout("time-limit-scoring");
            var root = nodes[node.Path[0]];
            if (root.Depth != 0) throw new ArgumentException("Path did not start at an actual direct root.");
            var header = headers[node.Coordinate];
            var content = UnitVector(BuildVector(string.Empty, [], node.Point.Summary));
            var rootHeaderIntent = Dot(query, headers[root.Coordinate]);
            var rootFullIntent = Dot(query, full[root.Coordinate]);
            var rootAnchor = .6 * rootHeaderIntent + .4 * rootFullIntent;
            // Identical root identity needs no edge. Other nodes retain only their
            // actually recorded bottleneck coherence, without a depth-only promotion.
            var retained = rootAnchor * (node.Depth == 0 ? 1 : .55 + .45 * node.PathStrength);
            var headerIntent = Dot(query, header);
            var intentSimilarity = Dot(query, full[node.Coordinate]);
            var coverage = header.Count == 0 ? 0 : header.Keys.Count(content.ContainsKey) / (double)header.Count;
            var bodyAgreement = Dot(header, content);
            // Coverage preserves sparse/dense support; no minimum body length or
            // quote detection is applied. This does not certify factual claims.
            var selfSupport = .7 * coverage + .3 * bodyAgreement;
            var incomingHeader = node.Predecessor is null ? 0 : Dot(headers[node.Predecessor], header);
            var endpoint = Math.Max(selfSupport, incomingHeader);
            var mention = Math.Max(0, intentSimilarity - headerIntent) * (1 - selfSupport);
            var score = .4 * retained + .3 * rootAnchor * endpoint + .3 * intentSimilarity - .08 * mention;
            var features = new CortexKnowledgeEndpointFeatures(root.Coordinate, rootHeaderIntent, rootFullIntent,
                rootAnchor, retained, headerIntent, intentSimilarity, coverage, bodyAgreement, selfSupport,
                incomingHeader, endpoint, mention);
            var equivalence = identityMap.TryGetValue(node.Coordinate, out var identity) &&
                identity.CanonicalContentSha256 is not null && identity.ProvenanceSha256 is not null
                ? JsonSerializer.Serialize(new
                {
                    node.Coordinate.Namespace, node.Coordinate.Kind, node.Coordinate.Revision,
                    identity.CanonicalContentSha256, identity.ProvenanceSha256,
                    node.Point.Title, Tags = node.Point.Tags.ToArray()
                }) : null;
            scored.Add(new(node, features, score, equivalence));
        }

        var representatives = new List<EndpointWork>();
        var groups = new Dictionary<string, List<EndpointWork>>(StringComparer.Ordinal);
        foreach (var work in EndpointOrder(scored))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= options.MaximumMilliseconds) return Timeout("time-limit-grouping");
            if (work.Equivalence is null)
                representatives.Add(work);
            else if (groups.TryGetValue(work.Equivalence, out var group))
                group.Add(work);
            else
            {
                groups.Add(work.Equivalence, [work]);
                representatives.Add(work);
            }
        }
        var chosen = new List<EndpointWork>();
        var finalScores = new Dictionary<CortexKnowledgeTraversalCoordinate, (double Discount, double Score)>();
        while (chosen.Count < options.Limit && chosen.Count < representatives.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= options.MaximumMilliseconds) return Timeout("time-limit-selection");
            var best = representatives.Where(work => !chosen.Contains(work)).Select(work =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var redundancy = chosen.Count == 0 ? 0 : chosen.Max(previous =>
                    Dot(full[work.Node.Coordinate], full[previous.Node.Coordinate]));
                return (Work: work, Discount: options.DiversityPenalty * redundancy,
                    Score: work.Base - options.DiversityPenalty * redundancy);
            }).OrderByDescending(value => value.Score)
                .ThenBy(value => value.Work.Node.Coordinate.Namespace, StringComparer.Ordinal)
                .ThenBy(value => value.Work.Node.Coordinate.RecordId, StringComparer.Ordinal)
                .ThenBy(value => value.Work.Node.Coordinate.Revision).First();
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= options.MaximumMilliseconds) return Timeout("time-limit-selection");
            chosen.Add(best.Work);
            finalScores.Add(best.Work.Node.Coordinate, (best.Discount, best.Score));
        }
        var selected = chosen.Select(work => new CortexKnowledgeEndpointChoice(work.Node, work.Node.Score,
            finalScores[work.Node.Coordinate].Score, work.Features, Aliases(work))).ToArray();
        var decisions = new List<CortexKnowledgeEndpointDecision>(nodes.Count);
        foreach (var work in EndpointOrder(scored))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= options.MaximumMilliseconds) return Timeout("time-limit-decisions");
            var representative = work.Equivalence is null ? work : groups[work.Equivalence][0];
            if (!ReferenceEquals(representative, work))
            {
                decisions.Add(new(work.Node.Coordinate, work.Node.Score, work.Base, null, null, false,
                    representative.Node.Coordinate, ["exact-canonical-duplicate-alias"]));
                continue;
            }
            var isSelected = chosen.Contains(work);
            var discount = finalScores.TryGetValue(work.Node.Coordinate, out var score) ? score.Discount :
                chosen.Count == 0 ? 0 : options.DiversityPenalty * chosen.Max(previous =>
                    Dot(full[work.Node.Coordinate], full[previous.Node.Coordinate]));
            decisions.Add(new(work.Node.Coordinate, work.Node.Score, work.Base, discount, work.Base - discount,
                isSelected, null, discount > 0 ? [isSelected ? "selected" : "below-topk", "similar-preview-discount"] :
                [isSelected ? "selected" : "below-topk"]));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.ElapsedMilliseconds >= options.MaximumMilliseconds) return Timeout("time-limit-terminal");
        return new(EndpointSelectionPolicyVersion, true,
            chosen.Count == options.Limit ? "selected-limit" : "exhausted-distinct-candidates",
            traversal.Truncated, false, selected, decisions);

        IReadOnlyList<CortexKnowledgeTraversalCoordinate> Aliases(EndpointWork work) =>
            work.Equivalence is null ? [work.Node.Coordinate] :
            groups[work.Equivalence].Select(value => value.Node.Coordinate).ToArray();
        CortexKnowledgeEndpointSelectionResult Timeout(string reason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unknown = new List<CortexKnowledgeEndpointDecision>(nodes.Count);
            foreach (var node in nodes.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                unknown.Add(new(node.Coordinate, node.Score, null, null, null, false, null, ["selection-time-budget"]));
            }
            return new(EndpointSelectionPolicyVersion, true, reason, traversal.Truncated, true, [], unknown);
        }
    }

    private sealed record EndpointWork(CortexKnowledgeTraversalNode Node, CortexKnowledgeEndpointFeatures Features,
        double Base, string? Equivalence);
    private static IEnumerable<EndpointWork> EndpointOrder(IEnumerable<EndpointWork> work) =>
        work.OrderByDescending(value => value.Base)
            .ThenBy(value => value.Node.Coordinate.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.Node.Coordinate.RecordId, StringComparer.Ordinal).ThenBy(value => value.Node.Coordinate.Revision);
    private static bool OptionalHash(string? value) =>
        value is null || value.Length == 64 && value.All(Uri.IsHexDigit);
}
