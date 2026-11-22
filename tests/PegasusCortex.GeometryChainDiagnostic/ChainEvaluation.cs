using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.GeometryGrowthDiagnostic;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.GeometryChainDiagnostic;

internal sealed record ChainMetrics(int Returned, int TargetHits, int SupportHits, int NoiseHits,
    double TargetPrecisionAt4, double TargetRecallAt4, double FirstTargetRR, double SupportPrecisionAt4);
internal sealed record NodeProof(CortexKnowledgeTraversalCoordinate Id, CortexKnowledgeTraversalCoordinate? Parent,
    int Depth, int Q, bool Direct, double Lexical, double Intent, double Edge, int Shared, double Strength, double Score);
internal sealed record SelectedPath(CortexKnowledgeTraversalCoordinate Id, IReadOnlyList<CortexKnowledgeTraversalCoordinate> Path);
internal sealed record TraceProof(string StopReason, bool Truncated, int SeedQueries, int NeighborQueries,
    int RawSeedCandidates, bool SeedPoolAtLimit, int AcceptedOccurrences, IReadOnlyList<SqliteCortexKnowledgeTraversalNeighborQuery> RawNeighbors,
    IReadOnlyList<CortexSearchHitV2> Ranked, NodeProof[] Visited, IReadOnlyList<CortexKnowledgeTraversalQuery> Queries, SelectedPath[] SelectedPaths);
internal sealed record ChainObservation(string QuestionId, string Status, GrowthFailureEvidence? Failure,
    ChainMetrics? Lexical, ChainMetrics? Current, ChainMetrics? Chain2, ChainMetrics? Chain3,
    TraceProof? Depth2, TraceProof? Depth3, bool DefaultOffNoCalls, bool ContextEqual, bool ReplayEqual,
    bool C3ReferenceEqual, string? LexicalContextSha256, string[]? Depth2DiscoveredTargets, string[]? Depth3DiscoveredTargets,
    int? ActualChainSeedQueriesIncludingReplay, int? ActualChainNeighborQueriesIncludingReplay);
internal sealed record ChainCaseReport(string Schema, string ManifestSha256, string PlanSha256, string CellId,
    string EngineeringStatus, int PlannedObservations, IReadOnlyList<ChainObservation> Observations,
    string Scope, string[] EvidenceLimits);

internal static class ChainEvaluation
{
    internal static async Task<ChainCaseReport> RunAsync(GrowthManifest manifest, string cellId,
        string fixture, string report, GrowthRunReport reference, CancellationToken token)
    {
        var questionIds = cellId == "probe-long-gap" ? new[] { "q04" } : manifest.Questions.Select(value => value.Id).ToArray();
        var cell = manifest.Cells.Single(value => value.Id == cellId);
        var byId = manifest.Records.ToDictionary(value => value.Payload.Id, StringComparer.Ordinal);
        var eligible = cell.RecordIds.ToHashSet(StringComparer.Ordinal);
        var baseline = await GrowthEvaluation.MeasureAsync(manifest,
            new("chain-baseline-" + cellId, true, [cellId], questionIds, 360), fixture, token).ConfigureAwait(false);
        await ChainProgram.WriteAsync(Path.Combine(report, "baseline.v1.json"), new
        {
            schema = "pegasus.cortex.geometry-chain-baseline/1", manifestSha256 = ChainProgram.ManifestSha,
            cellId, measurement = baseline
        }).ConfigureAwait(false);
        ChainProgram.Need(!baseline.Cancelled && baseline.CellFailures.Length == 0 &&
            baseline.Observations.Length == questionIds.Length && baseline.Observations.All(value => value.Status == "engineering-passed"),
            "baseline-engineering");

        var low = new SqliteCortexV2Store(new(Path.Combine(fixture, cellId,
            SqliteCortexV2StoreOptions.RequiredDatabaseFileName), BusyTimeoutMilliseconds: 1000,
            MaximumContentBytes: 4096, MaximumSearchChunksPerHead: 16, MaximumDatabasePages: 4096), TimeProvider.System);
        await using var operational = new SqliteCortexV2OperationalStore(low, "chain-diagnostic-1");
        await low.InitializeAsync(token).ConfigureAwait(false);
        var observations = new List<ChainObservation>();
        foreach (var id in questionIds)
        {
            var stage = "reference-parity";
            try
            {
                token.ThrowIfCancellationRequested();
                var gold = manifest.Questions.Single(value => value.Id == id);
                var old = reference.Measurement.Observations.Single(value => value.CellId == cellId && value.QuestionId == id);
                var currentBaseline = baseline.Observations.Single(value => value.QuestionId == id);
                ChainProgram.Need(Same(old.Lexical, currentBaseline.Lexical) &&
                    Same(old.Geometry, currentBaseline.Geometry) &&
                    old.LexicalContextSha256 == currentBaseline.LexicalContextSha256, "c3-regression-reference");
                var request = new CortexSearchRequestV2(["knowledge.shared"], gold.Query,
                    [CortexKnowledgeKindV2.Observation], null, 4, 4096, CortexResponseFormatV2.Compact);
                var lexicalRequest = new SqliteCortexV2SearchRequest("knowledge.shared", gold.Query, "knowledge", 32, 1);
                var lexicalBefore = await low.SearchHeadsAsync(lexicalRequest, token).ConfigureAwait(false);
                var contextBefore = await ContextAsync(currentBaseline.Lexical!, byId, operational, token).ConfigureAwait(false);
                var normalBefore = await operational.SearchAsync(request, token).ConfigureAwait(false);
                stage = "default-off";
                var off = await operational.TraverseKnowledgeAsync(request, cancellationToken: token).ConfigureAwait(false);
                ChainProgram.Need(!off.Traversal.Enabled && off.Traversal.StopReason == "disabled" &&
                    off.SeedQueries == 0 && off.NeighborQueries == 0 && off.Items.Count == 0, "default-off-no-calls");
                stage = "chain2";
                var two = await operational.TraverseKnowledgeAsync(request, ChainProgram.Options(2), token).ConfigureAwait(false);
                var twoReplay = await operational.TraverseKnowledgeAsync(request, ChainProgram.Options(2), token).ConfigureAwait(false);
                stage = "chain3";
                var three = await operational.TraverseKnowledgeAsync(request, ChainProgram.Options(3), token).ConfigureAwait(false);
                var threeReplay = await operational.TraverseKnowledgeAsync(request, ChainProgram.Options(3), token).ConfigureAwait(false);
                stage = "path-and-replay";
                Validate(two, 2, eligible);
                Validate(three, 3, eligible);
                ChainProgram.Need(Same(two, twoReplay) && Same(three, threeReplay), "chain-deterministic-replay");
                ChainProgram.Need(!two.Traversal.StopReason.StartsWith("time-limit", StringComparison.Ordinal) &&
                    !three.Traversal.StopReason.StartsWith("time-limit", StringComparison.Ordinal), "chain-primary-time-limit");
                stage = "lexical-only-context";
                var normalAfter = await operational.SearchAsync(request, token).ConfigureAwait(false);
                var lexicalAfter = await low.SearchHeadsAsync(lexicalRequest, token).ConfigureAwait(false);
                var contextAfter = await ContextAsync(currentBaseline.Lexical!, byId, operational, token).ConfigureAwait(false);
                ChainProgram.Need(Same(normalBefore, normalAfter) && Same(lexicalBefore, lexicalAfter) &&
                    contextBefore == contextAfter && contextAfter == currentBaseline.LexicalContextSha256,
                    "chain-normal-and-context-parity");
                var targets = gold.TargetIds.ToHashSet(StringComparer.Ordinal);
                var supports = gold.UsefulSupport.Keys.Where(eligible.Contains).ToHashSet(StringComparer.Ordinal);
                observations.Add(new(id, "engineering-passed", null,
                    Metrics(currentBaseline.Lexical!.Select(value => value.Coordinate.RecordId), targets, supports),
                    Metrics(currentBaseline.Geometry!.Select(value => value.Coordinate.RecordId), targets, supports),
                    Metrics(two.Items.Select(value => value.RecordId), targets, supports),
                    Metrics(three.Items.Select(value => value.RecordId), targets, supports),
                    Proof(two), Proof(three), true, true, true, true, contextAfter,
                    two.Traversal.Admitted.Where(value => targets.Contains(value.Coordinate.RecordId))
                        .Select(value => value.Coordinate.RecordId).Order(StringComparer.Ordinal).ToArray(),
                    three.Traversal.Admitted.Where(value => targets.Contains(value.Coordinate.RecordId))
                        .Select(value => value.Coordinate.RecordId).Order(StringComparer.Ordinal).ToArray(),
                    two.SeedQueries + twoReplay.SeedQueries + three.SeedQueries + threeReplay.SeedQueries,
                    two.NeighborQueries + twoReplay.NeighborQueries + three.NeighborQueries + threeReplay.NeighborQueries));
            }
            catch (Exception error)
            {
                observations.Add(new(id, "engineering-failed", GrowthEvaluation.DescribeFailure(error, stage),
                    null, null, null, null, null, null, false, false, false, false, null, null, null, null, null));
                break;
            }
        }
        foreach (var id in questionIds.Where(id => !observations.Any(value => value.QuestionId == id)))
            observations.Add(new(id, "not-executed", null, null, null, null, null, null, null,
                false, false, false, false, null, null, null, null, null));
        return new("pegasus.cortex.geometry-chain-report/1", ChainProgram.ManifestSha, ChainProgram.PlanSha,
            cellId, observations.All(value => value.Status == "engineering-passed") ? "passed" : "failed",
            questionIds.Length, observations, "Actual fresh isolated native SQLite; frozen C3 corpus/query/labels; metadata-only chain.",
            ["97 query units reuse48 semantic questions; depths/replays are not independent semantic tests.",
             "C3 reference scores/coordinates/context are exact regressions for the unchanged normal path.",
             "Similarity candidates and paths do not prove logical entailment.",
             "Only exact lexical revisions supply selected context; chain-only metadata never does.",
             "Budget stops/pruning remain explicit; discovery and top4 quality are separate.",
             "Core counts accepted projections; raw logical neighbor saturation/rejections are recorded separately."]);
    }

    internal static TraceProof Proof(SqliteCortexKnowledgeTraversalResult value)
    {
        var nodes = value.Traversal.Admitted.Select(node => new NodeProof(node.Coordinate, node.Predecessor,
            node.Depth, node.QueryOrdinal, node.Point.Direct, node.Point.LexicalScore, node.IntentSimilarity,
            node.EdgeSimilarity, node.SharedFeatures, node.PathStrength, node.Score)).ToArray();
        ChainProgram.Need(nodes.All(node => JsonSerializer.SerializeToUtf8Bytes(node, ChainProgram.Json).Length <= 640),
            "node-proof-byte-bound");
        ChainProgram.Need(value.Traversal.Queries.All(query =>
            query.Candidates.All(coordinate => JsonSerializer.SerializeToUtf8Bytes(coordinate, ChainProgram.Json).Length <= 128) &&
            JsonSerializer.SerializeToUtf8Bytes(query with { Candidates = [] }, ChainProgram.Json).Length <= 1024),
            "query-proof-byte-bound");
        return new(value.Traversal.StopReason, value.Traversal.Truncated, value.SeedQueries,
            value.NeighborQueries, value.RawSeedCandidates, value.SeedPoolAtLimit,
            value.Traversal.CandidateOccurrences, value.RawNeighborQueries, value.Items, nodes,
            value.Traversal.Queries, value.Traversal.Ranked.Select(node => new SelectedPath(node.Coordinate, node.Path)).ToArray());
    }

    internal static void Validate(SqliteCortexKnowledgeTraversalResult value, int depth, HashSet<string>? eligible = null)
    {
        var result = value.Traversal;
        var options = ChainProgram.Options(depth);
        var visited = result.Admitted.ToDictionary(node => node.Coordinate);
        ChainProgram.Need(result.Enabled && result.Admitted.Count <= options.MaximumVisited &&
            result.Queries.Count <= options.MaximumQueries && result.CandidateOccurrences <= options.MaximumCandidateOccurrences &&
            value.NeighborQueries == result.Queries.Count && value.SeedQueries <= 4, "traversal-caps");
        foreach (var node in result.Admitted)
        {
            ChainProgram.Need(node.Depth <= depth && node.Path.Count == node.Depth + 1 &&
                node.Path.Distinct().Count() == node.Path.Count && node.Path[^1] == node.Coordinate &&
                node.Coordinate.Kind == "knowledge" && node.Coordinate.Revision >= 1 &&
                (eligible is null || eligible.Contains(node.Coordinate.RecordId)), "full-coordinate-path");
            if (node.Depth == 0)
                ChainProgram.Need(node.Point.Direct && node.Predecessor is null && node.QueryOrdinal == 0, "real-seed-depth");
            else
            {
                var parent = visited[node.Predecessor!];
                var query = result.Queries.Single(value => value.Request.Ordinal == node.QueryOrdinal);
                ChainProgram.Need(!node.Point.Direct && parent.Depth == node.Depth - 1 &&
                    parent.Path.SequenceEqual(node.Path.Take(node.Path.Count - 1)) &&
                    query.Request.Parent == node.Predecessor && query.Request.Depth == node.Depth &&
                    query.Candidates.Contains(node.Coordinate) && query.Status == "completed" &&
                    node.EdgeSimilarity >= options.MinimumEdgeSimilarity && node.SharedFeatures >= options.MinimumSharedFeatures,
                    "actual-parent-edge");
            }
        }
        ChainProgram.Need(value.AdmittedMetadata.Count == result.Admitted.Count &&
            value.AdmittedMetadata.All(item => item.RecordType == CortexRecordTypeV2.Knowledge &&
                item.Kind == CortexKnowledgeKindV2.Observation &&
                visited.ContainsKey(new(item.Namespace, "knowledge", item.RecordId, item.Revision))),
            "admitted-kind-provenance");
    }

    private static async Task<string> ContextAsync(GrowthHit[] hits, Dictionary<string, GrowthRecord> records,
        SqliteCortexV2OperationalStore operational, CancellationToken token)
    {
        var context = new List<GrowthContext>();
        foreach (var hit in hits)
        {
            var coordinate = hit.Coordinate;
            var read = await operational.ReadAsync(new(coordinate.Namespace, CortexRecordTypeV2.Knowledge,
                coordinate.RecordId, coordinate.Revision), token).ConfigureAwait(false);
            ChainProgram.Need(read.Record is CortexKnowledgeV2, "lexical-exact-type");
            var knowledge = (CortexKnowledgeV2)read.Record!;
            var expected = records[coordinate.RecordId].Payload;
            ChainProgram.Need(knowledge.Namespace == coordinate.Namespace && knowledge.RecordId == coordinate.RecordId &&
                knowledge.Revision == coordinate.Revision && knowledge.Title == expected.Title && knowledge.Content == expected.Content,
                "lexical-exact-revision");
            context.Add(new(coordinate, knowledge.Title, knowledge.Content));
        }
        return GrowthCorpus.Hash(JsonSerializer.SerializeToUtf8Bytes(context.ToArray()));
    }

    internal static ChainMetrics Metrics(IEnumerable<string> ids, HashSet<string> targets, HashSet<string> support)
    {
        var values = ids.ToArray();
        ChainProgram.Need(values.Length <= 4 && values.Distinct().Count() == values.Length && !targets.Overlaps(support),
            "role-count-admission");
        var targetHits = values.Count(targets.Contains);
        var supportHits = values.Count(support.Contains);
        var first = Array.FindIndex(values, targets.Contains);
        return new(values.Length, targetHits, supportHits, values.Length - targetHits - supportHits,
            targetHits / 4d, targetHits / (double)targets.Count, first < 0 ? 0 : 1d / (first + 1), supportHits / 4d);
    }
    internal static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
