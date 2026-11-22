using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.GeometryChainDiagnostic;
using Pegasus.Cortex.GeometryGrowthDiagnostic;

namespace Pegasus.Cortex.GeometryEndpointDiagnostic;

internal static class EndpointChecks
{
    private static CortexKnowledgeGeometryPoint Point(string id, string title, string summary, long revision = 1) =>
        new("ignored-key", "knowledge.shared", id, revision, title, [], summary, 3, true);
    private static CortexKnowledgeTraversalResult Roots(params CortexKnowledgeGeometryPoint[] points)
    {
        var nodes = points.Select(point =>
        {
            var c = new CortexKnowledgeTraversalCoordinate(point.Namespace, "knowledge", point.RecordId, point.Revision);
            return new CortexKnowledgeTraversalNode(c, point, 0, null, 0, .2, 0, 0, 0, .123, [c]);
        }).ToArray();
        return new(true, "unit-roots", false, nodes.Take(4).ToArray(), nodes, [], 0);
    }
    private static string Hash(string value) => GrowthCorpus.Hash(Encoding.UTF8.GetBytes(value));
    private static CortexKnowledgeEndpointIdentity Identity(CortexKnowledgeTraversalNode node, string body, string provenance) =>
        new(node.Coordinate, Hash(body), Hash(provenance));

    internal static async Task<string[]> RunAsync()
    {
        var passed = new List<string>();
        EndpointProgram.Need(new[] { "alpha", "beta" }.SequenceEqual(new[] { "beta", "alpha", "beta" }
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), "unit-canonical-tag-comparison");
        passed.Add("producer binding uses the store canonical distinct ordinal tag sequence, not input ordering");
        var root = Point("unit-a", "Harbor lease index", "Harbor lease records connect the gateway ledger.");
        var bridge = Point("unit-b", "Gateway ledger", "Gateway ledger binds harbor lease rules.") with { Direct = false };
        var endpoint = Point("unit-c", "Ledger lease rules", "Ledger lease rules bind the owner and its stable lease.") with { Direct = false };
        var calls = 0;
        var graph = await CortexKnowledgeGeometry.TraverseAsync("harbor lease", ["knowledge.shared"], [root],
            (request, token) =>
            {
                calls++;
                IReadOnlyList<CortexKnowledgeGeometryPoint> values = request.Parent.RecordId switch
                { "unit-a" => [bridge], "unit-b" => [endpoint, root], _ => [bridge] };
                return ValueTask.FromResult(values);
            }, ChainProgram.Options(2)).ConfigureAwait(false);
        EndpointProgram.Need(graph.Admitted.Any(node => node.Point.RecordId == "unit-c" && node.Depth == 2), "unit-real-endpoint-path");
        var before = JsonSerializer.Serialize(graph);
        var providerCallsBefore = calls;
        var off = CortexKnowledgeGeometry.SelectEndpoints("harbor lease", graph);
        EndpointProgram.Need(!off.Enabled && off.Selected.Select(value => value.Node.Coordinate)
            .SequenceEqual(graph.Ranked.Select(value => value.Coordinate)) &&
            off.Selected.All(value => value.SelectionScore is null && value.TraversalScore == value.Node.Score),
            "unit-default-off");
        var selected = CortexKnowledgeGeometry.SelectEndpoints("harbor lease", graph, options: new(Enabled: true));
        var replay = CortexKnowledgeGeometry.SelectEndpoints("harbor lease", graph, options: new(Enabled: true));
        EndpointProgram.Need(ChainEvaluation.Same(selected, replay) && JsonSerializer.Serialize(graph) == before &&
            calls == providerCallsBefore && selected.Selected.Any(value => value.Node.Point.RecordId == "unit-c") &&
            selected.Selected.All(value => value.Features?.Root == graph.Admitted.Single(node => node.Depth == 0).Coordinate),
            "unit-pure-root-carry-replay");
        passed.Add("default-off preserves old rank/score; same graph and0provider calls; actual-root carry and deterministic replay");

        var ordinary = Roots(Point("unit-quote", "Notes", "Harbor lease guidance binds the harbor owner before renewing the lease."));
        var quoted = Roots(Point("unit-quote", "Notes", "\"Harbor lease guidance binds the harbor owner before renewing the lease.\""));
        var plainChoice = CortexKnowledgeGeometry.SelectEndpoints("harbor lease", ordinary, options: new(Enabled: true)).Selected.Single();
        var quoteChoice = CortexKnowledgeGeometry.SelectEndpoints("harbor lease", quoted, options: new(Enabled: true)).Selected.Single();
        EndpointProgram.Need(plainChoice.SelectionScore > 0 && plainChoice.SelectionScore == quoteChoice.SelectionScore &&
            ChainEvaluation.Same(plainChoice.Features, quoteChoice.Features), "unit-quoted-guidance");
        passed.Add("legitimate quoted and ordinary token-equivalent guidance have identical features/scores");

        var sparseDense = Roots(
            Point("unit-sparse", "Harbor lease", "Harbor lease."),
            Point("unit-dense", "Harbor lease rules", "Harbor lease rules bind owner identity, renewal time, cancellation, accounting, generation, expiry and recovery."),
            Point("unit-display", "Inventory display", "Inventory display labels show harbor lease on an inventory display inventory panel."));
        var selectedTwo = CortexKnowledgeGeometry.SelectEndpoints("harbor lease", sparseDense,
            options: new(Enabled: true, Limit: 2)).Selected;
        EndpointProgram.Need(selectedTwo.Any(value => value.Node.Point.RecordId == "unit-sparse") &&
            selectedTwo.Any(value => value.Node.Point.RecordId == "unit-dense"), "unit-sparse-dense-endpoints");
        passed.Add("sparse and dense substantive endpoints remain selectable without a body-length or quote gate");

        var twins = Roots(Point("unit-copy-a", "Lease record", "Lease record body."),
            Point("unit-copy-b", "Lease record", "Lease record body."));
        var sameIdentities = twins.Admitted.Select(node => Identity(node, "Complete canonical lease record.", "same source/time")).ToArray();
        var collapsed = CortexKnowledgeGeometry.SelectEndpoints("lease record", twins, sameIdentities, new(Enabled: true));
        EndpointProgram.Need(collapsed.Selected.Count == 1 && collapsed.Selected[0].EquivalentCoordinates.Count == 2 &&
            collapsed.Decisions.Count == 2 && collapsed.Decisions.Count(value => value.EquivalentRepresentative is not null) == 1,
            "unit-exact-equivalence-aliases");
        var unknown = CortexKnowledgeGeometry.SelectEndpoints("lease record", twins, options: new(Enabled: true));
        EndpointProgram.Need(unknown.Selected.Count == 2 && unknown.Decisions.All(value => value.EquivalentRepresentative is null),
            "unit-preview-not-full-equivalence");
        passed.Add("only known full-content+provenance equivalence collapses; all aliases retained; unknown preview identity stays distinct");

        var unequalBodies = new[] { "Enable then validate.", "Validate then enable.", "Lease count=1.", "Lease count=2.",
            "Enable transport.", "Do not enable transport.", "protocol v1", "protocol v2" };
        foreach (var body in unequalBodies.Skip(1))
        {
            var identities = new[] { Identity(twins.Admitted[0], unequalBodies[0], "same source/time"),
                Identity(twins.Admitted[1], body, "same source/time") };
            var result = CortexKnowledgeGeometry.SelectEndpoints("lease record", twins, identities, new(Enabled: true));
            EndpointProgram.Need(result.Selected.Count == 2 && result.Decisions.All(value => value.EquivalentRepresentative is null),
                "unit-non-equivalent-full-facts");
        }
        var differentProvenance = twins.Admitted.Select((node, index) =>
            Identity(node, "Same complete body.", "source/time " + index)).ToArray();
        EndpointProgram.Need(CortexKnowledgeGeometry.SelectEndpoints("lease record", twins, differentProvenance,
            new(Enabled: true)).Selected.Count == 2, "unit-provenance-distinction");
        var versions = Roots(Point("unit-version", "Lease record", "Lease record body."),
            Point("unit-version", "Lease record", "Lease record body.", 2));
        EndpointProgram.Need(CortexKnowledgeGeometry.SelectEndpoints("lease record", versions,
            versions.Admitted.Select(node => Identity(node, "Same complete body.", "same provenance")).ToArray(),
            new(Enabled: true)).Selected.Count == 2, "unit-revision-distinction");
        passed.Add("word order, negation, numeric/code/version, revision and provenance distinctions do not hard-collapse");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Expect<OperationCanceledException>(() => CortexKnowledgeGeometry.SelectEndpoints("lease record", twins,
                options: new(Enabled: true), cancellationToken: cancelled.Token));
        }
        Expect<ArgumentException>(() => CortexKnowledgeGeometry.SelectEndpoints("lease record", twins,
            options: new(Enabled: true, Limit: 1, MaximumCandidates: 1)));
        Expect<ArgumentException>(() => CortexKnowledgeGeometry.SelectEndpoints("lease record", twins,
            [new(twins.Admitted[0].Coordinate, "invalid-hash", null)], new(Enabled: true)));
        var invalid = graph with { Admitted = graph.Admitted.Select(node => node.Depth == 2 ?
            node with { Predecessor = null } : node).ToArray() };
        Expect<ArgumentException>(() => CortexKnowledgeGeometry.SelectEndpoints("harbor lease", invalid, options: new(Enabled: true)));
        EndpointProgram.Need(selected.Decisions.Count <= 128 && selected.Decisions.All(value => value.Reasons.Count <= 2) &&
            selected.Selected.All(value => value.TraversalScore == value.Node.Score), "unit-decision-and-old-score-bounds");
        passed.Add("caller cancellation, candidate/input/path limits, bounded reasons and unchanged traversal scores");
        return passed.ToArray();
    }

    private static void Expect<T>(Action work) where T : Exception
    {
        try { work(); }
        catch (T) { return; }
        throw new GrowthInvariantException("expected-" + typeof(T).Name);
    }
}
