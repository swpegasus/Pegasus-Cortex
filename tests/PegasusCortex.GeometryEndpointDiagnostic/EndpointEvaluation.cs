using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.GeometryChainDiagnostic;
using Pegasus.Cortex.GeometryGrowthDiagnostic;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.GeometryEndpointDiagnostic;

internal sealed record EndpointKnownBaseline(string Schema, string ManifestSha256, GrowthMeasurement Measurement);
internal sealed record EndpointCachedQuery(string Id, string Family, string Text, GrowthHit[] Lexical,
    CortexSearchHitV2[] Current, SqliteCortexKnowledgeTraversalResult Graph,
    CortexKnowledgeEndpointIdentity[] Identities, string ContextSha256,
    bool ContextParity, bool GraphReplay, bool? C3Reference, bool? ChainReference);
internal sealed record EndpointGraphCache(string Schema, string Dataset, string CaseId,
    string? SourceManifestSha256, string ChainOptionsSha256, int Heads, EndpointCachedQuery[] Queries);
internal sealed record EndpointSelected(CortexKnowledgeTraversalCoordinate Coordinate, double TraversalScore,
    double? SelectionScore, CortexKnowledgeEndpointFeatures? Features,
    IReadOnlyList<CortexKnowledgeTraversalCoordinate> EquivalentCoordinates);
internal sealed record EndpointReachEvidence(int ReachedCount, string[] ReachedPrimaryIds, int UnseenCount,
    string[] UnseenPrimaryIds, string[] ReachedButUnselectedOldChain2, string[] ReachedButUnselectedSelector);
internal sealed record EndpointGroupCoverage(int Required, int Covered, double? Coverage);
internal sealed record EndpointObservation(string QueryId, string Family, string Status, GrowthFailureEvidence? Failure,
    ChainMetrics? Lexical, ChainMetrics? Current, ChainMetrics? OldChain2, ChainMetrics? Selector,
    EndpointSelected[]? Selected, IReadOnlyList<CortexKnowledgeEndpointDecision>? Decisions,
    bool DefaultOffParity, bool GraphUnchanged, bool SelectorReplay, string? StopReason,
    bool? TraversalTruncated, int? SelectorProviderQueriesAdded,
    Dictionary<string, EndpointGroupCoverage>? RequiredGroupCoverage = null, EndpointReachEvidence? Reach = null);
internal sealed record EndpointSelectionReport(string Schema, string Dataset, string CaseId, string PolicyVersion,
    string PolicySha256, string CacheSha256, string EngineeringStatus, int Planned,
    EndpointObservation[] Observations, string[] EvidenceLimits);

internal static class EndpointEvaluation
{
    internal static Dictionary<string, EndpointGoldCase> KnownGold(GrowthManifest manifest, string cellId)
    {
        var eligible = manifest.Cells.Single(value => value.Id == cellId).RecordIds.ToHashSet(StringComparer.Ordinal);
        var questions = cellId == "probe-long-gap" ? manifest.Questions.Where(value => value.Id == "q04") : manifest.Questions;
        return questions.ToDictionary(value => value.Id, value =>
        {
            var targets = value.TargetIds;
            var support = value.UsefulSupport.Where(pair => eligible.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            var noise = eligible.Where(id => !targets.Contains(id) && !support.ContainsKey(id))
                .ToDictionary(id => id, _ => "Closed reviewed C3 relation table.");
            return new EndpointGoldCase(value.Id, targets, support, noise);
        });
    }

    internal static async Task<EndpointGraphCache> CaptureKnownAsync(GrowthManifest manifest, string id,
        string fixture, string report, string c3Path, string chainPath, CancellationToken token, byte[]? retainedBaseline = null)
    {
        var cell = manifest.Cells.Single(value => value.Id == id);
        var ids = id == "probe-long-gap" ? new[] { "q04" } : manifest.Questions.Select(value => value.Id).ToArray();
        GrowthMeasurement baseline;
        if (retainedBaseline is null)
            baseline = await GrowthEvaluation.MeasureAsync(manifest, new("endpoint-baseline", true, [id], ids, 360),
                fixture, token).ConfigureAwait(false);
        else
        {
            var retained = JsonSerializer.Deserialize<EndpointKnownBaseline>(retainedBaseline, EndpointProgram.Json)
                ?? throw new InvalidDataException();
            EndpointProgram.Need(retained.Schema == "pegasus.cortex.endpoint-baseline/1" &&
                retained.ManifestSha256 == EndpointProgram.ManifestSha, "retained-baseline-schema");
            baseline = retained.Measurement;
        }
        await EndpointProgram.WriteAsync(Path.Combine(report, "baseline.v1.json"), new
        {
            schema = "pegasus.cortex.endpoint-baseline/1", manifestSha256 = EndpointProgram.ManifestSha,
            measurement = baseline
        }).ConfigureAwait(false);
        EndpointProgram.Need(!baseline.Cancelled && baseline.CellFailures.Length == 0 &&
            baseline.Observations.Length == ids.Length && baseline.Observations.All(value => value.Status == "engineering-passed"),
            "capture-baseline-engineering");
        var c3 = JsonSerializer.Deserialize<GrowthRunReport>(EndpointProgram.Read(c3Path, 2 * 1024 * 1024), EndpointProgram.Json)
            ?? throw new InvalidDataException();
        var chain = JsonSerializer.Deserialize<ChainCaseReport>(EndpointProgram.Read(chainPath, EndpointProgram.ReportCap),
            EndpointProgram.Json) ?? throw new InvalidDataException();
        EndpointProgram.Need(c3.EngineeringStatus == "passed" && c3.ManifestSha256 == EndpointProgram.ManifestSha &&
            chain.EngineeringStatus == "passed" && chain.ManifestSha256 == EndpointProgram.ManifestSha &&
            chain.PlanSha256 == ChainProgram.PlanSha && chain.CellId == id, "capture-reference-admission");
        var origin = new EndpointOrigin("synthetic-growth", new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));
        var records = manifest.Records.Where(value => cell.RecordIds.Contains(value.Payload.Id))
            .ToDictionary(value => value.Payload.Id, value => new EndpointRecord(value.Payload.Id, value.Payload.Title,
                value.Payload.Content, value.Payload.Tags, origin));
        var queries = manifest.Questions.Where(value => ids.Contains(value.Id))
            .Select(value => new EndpointQuery(value.Id, value.Family, value.Query)).ToArray();
        var low = Store(Path.Combine(fixture, id, SqliteCortexV2StoreOptions.RequiredDatabaseFileName));
        await using var operational = new SqliteCortexV2OperationalStore(low, "endpoint-known-capture");
        await low.InitializeAsync(token).ConfigureAwait(false);
        var output = new List<EndpointCachedQuery>();
        foreach (var query in queries)
        {
            var before = baseline.Observations.Single(value => value.QuestionId == query.Id);
            var original = c3.Measurement.Observations.Single(value => value.CellId == id && value.QuestionId == query.Id);
            EndpointProgram.Need(ChainEvaluation.Same(before.Lexical, original.Lexical) &&
                ChainEvaluation.Same(before.Geometry, original.Geometry) &&
                before.LexicalContextSha256 == original.LexicalContextSha256, "unchanged-C3-baseline");
            var captured = await CaptureQueryAsync(query, records, low, operational, token).ConfigureAwait(false);
            var old = chain.Observations.Single(value => value.QuestionId == query.Id);
            EndpointProgram.Need(ChainEvaluation.Same(ChainEvaluation.Proof(captured.Graph), old.Depth2) &&
                captured.ContextSha256 == before.LexicalContextSha256 &&
                ChainEvaluation.Same(captured.Lexical, before.Lexical), "unchanged-old-chain-graph-and-context");
            output.Add(captured with { C3Reference = true, ChainReference = true });
        }
        return new("pegasus.cortex.endpoint-graph-cache/1", "known-C3", id, EndpointProgram.ManifestSha,
            ChainProgram.PlanSha, cell.RecordIds.Length, output.ToArray());
    }

    internal static void ValidateHoldout(EndpointHoldout fixture, EndpointGold gold)
    {
        EndpointProgram.Need(fixture.Schema == "pegasus.cortex.geometry-ranking-holdout/1" &&
            gold.Schema == "pegasus.cortex.geometry-ranking-holdout-gold/1" &&
            fixture.Namespace == "knowledge.shared" && fixture.Records.Length == 88 && fixture.Queries.Length == 8 &&
            fixture.Queries.Select(query => query.Family).Distinct().Count() == 4 &&
            fixture.Records.Select(record => record.Id).Distinct().Count() == 88 &&
            fixture.Queries.Select(query => query.Id).Distinct().Count() == 8 && gold.Cases.Length == 8,
            "frozen-holdout-schema-counts");
        var ids = fixture.Records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var record in fixture.Records)
            EndpointProgram.Need(record.Id.Length == 25 && record.Id[0] == 'r' && record.Id.Skip(1).All(Uri.IsHexDigit) &&
                System.Text.Encoding.UTF8.GetByteCount(record.Title) <= 160 &&
                System.Text.Encoding.UTF8.GetByteCount(record.Content) <= 512 &&
                record.Tags.Length <= 8 && record.Tags.All(tag => tag.Length <= 256) &&
                !string.IsNullOrWhiteSpace(record.Provenance.SourceId), "holdout-record-bounds");
        foreach (var query in fixture.Queries)
        {
            EndpointProgram.Need(!string.IsNullOrWhiteSpace(query.Text) &&
                System.Text.Encoding.UTF8.GetByteCount(query.Text) <= 4096, "holdout-actual-query");
            var truth = gold.Cases.Single(value => value.QueryId == query.Id);
            var targets = truth.PrimaryTargetIds.ToHashSet(StringComparer.Ordinal);
            var support = truth.UsefulSupport.Keys.ToHashSet(StringComparer.Ordinal);
            var noise = truth.Noise.Keys.ToHashSet(StringComparer.Ordinal);
            EndpointProgram.Need(targets.Count > 0 && targets.Count == truth.PrimaryTargetIds.Length &&
                !targets.Overlaps(support) && !targets.Overlaps(noise) && !support.Overlaps(noise) &&
                targets.Concat(support).Concat(noise).ToHashSet(StringComparer.Ordinal).SetEquals(ids),
                "holdout-complete-disjoint-truth");
            EndpointProgram.Need(truth.AnswerGroups is not null && truth.AnswerGroups.Any(group => group.Required) &&
                truth.AnswerGroups.Select(group => group.Id).Distinct().Count() == truth.AnswerGroups.Length &&
                truth.AnswerGroups.All(group => group.AnyOf.Length > 0 && group.AnyOf.All(recordId => ids.Contains(recordId) && !noise.Contains(recordId))),
                "holdout-answer-groups");
        }
    }

    internal static async Task<EndpointGraphCache> CaptureHoldoutAsync(EndpointHoldout fixture, string root,
        CancellationToken token)
    {
        var low = Store(Path.Combine(root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName));
        await using var operational = new SqliteCortexV2OperationalStore(low, "endpoint-native-holdout");
        await low.InitializeAsync(token).ConfigureAwait(false);
        foreach (var record in fixture.Records)
        {
            var command = new CortexKnowledgeCommandV2("knowledge.shared", CortexKnowledgeActionV2.Create,
                record.Id, null, "holdout-" + record.Id, CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Verified,
                record.Title, record.Content, record.Tags, new(CortexProvenanceSourceV2.Agent, record.Provenance.SourceId,
                    "Authored synthetic holdout; not external or installed knowledge."), null)
            {
                EffectiveProvenance = new(CortexProvenanceSourceV2.Agent, "ranking_holdout", record.Provenance.SourceId,
                    "Independent synthetic holdout.", record.Provenance.CapturedAtUtc)
            };
            var written = await operational.ExecuteKnowledgeAsync(command, token).ConfigureAwait(false);
            EndpointProgram.Need(written.Record?.Revision == 1, "holdout-create-revision");
        }
        var counters = await low.GetCatalogCountersAsync(token).ConfigureAwait(false);
        EndpointProgram.Need(counters.HeadRows == 88, "holdout-head-count");
        var records = fixture.Records.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var output = new List<EndpointCachedQuery>();
        foreach (var query in fixture.Queries)
            output.Add(await CaptureQueryAsync(query, records, low, operational, token).ConfigureAwait(false));
        return new("pegasus.cortex.endpoint-graph-cache/1", "Native-blind-holdout", "native-holdout", null,
            ChainProgram.PlanSha, 88, output.ToArray());
    }

    private static SqliteCortexV2Store Store(string path) => new(new(path, BusyTimeoutMilliseconds: 1000,
        MaximumContentBytes: 4096, MaximumSearchChunksPerHead: 16, MaximumDatabasePages: 4096), TimeProvider.System);

    private static async Task<EndpointCachedQuery> CaptureQueryAsync(EndpointQuery query,
        Dictionary<string, EndpointRecord> records, SqliteCortexV2Store low, SqliteCortexV2OperationalStore operational,
        CancellationToken token)
    {
        var search = new SqliteCortexV2SearchRequest("knowledge.shared", query.Text, "knowledge", 32, 1);
        var lexicalPool = await low.SearchHeadsAsync(search, token).ConfigureAwait(false);
        var lexical = lexicalPool.Take(4).Select(hit => new GrowthHit(new(hit.Namespace, hit.RecordId, hit.Revision), hit.Score)).ToArray();
        var before = await ContextAsync(lexical, records, operational, token).ConfigureAwait(false);
        var request = new CortexSearchRequestV2(["knowledge.shared"], query.Text, [CortexKnowledgeKindV2.Observation],
            null, 4, 4096, CortexResponseFormatV2.Compact);
        var current = await operational.SearchAsync(request, token).ConfigureAwait(false);
        var graph = await operational.TraverseKnowledgeAsync(request, ChainProgram.Options(2), token).ConfigureAwait(false);
        var replay = await operational.TraverseKnowledgeAsync(request, ChainProgram.Options(2), token).ConfigureAwait(false);
        ChainEvaluation.Validate(graph, 2, records.Keys.ToHashSet(StringComparer.Ordinal));
        EndpointProgram.Need(ChainEvaluation.Same(graph, replay) &&
            !graph.Traversal.StopReason.StartsWith("time-limit", StringComparison.Ordinal), "unchanged-chain2-replay");
        var currentAfter = await operational.SearchAsync(request, token).ConfigureAwait(false);
        var lexicalAfter = await low.SearchHeadsAsync(search, token).ConfigureAwait(false);
        var after = await ContextAsync(lexical, records, operational, token).ConfigureAwait(false);
        EndpointProgram.Need(ChainEvaluation.Same(current, currentAfter) &&
            ChainEvaluation.Same(lexicalPool, lexicalAfter) && before == after, "capture-context-and-normal-parity");
        var metadata = graph.AdmittedMetadata.ToDictionary(hit => new CortexKnowledgeTraversalCoordinate(
            hit.Namespace, "knowledge", hit.RecordId, hit.Revision));
        var identities = graph.Traversal.Admitted.Select(node =>
        {
            var record = records[node.Coordinate.RecordId];
            var hit = metadata[node.Coordinate];
            EndpointProgram.Need(hit.Provenance.SourceId == record.Provenance.SourceId &&
                hit.Provenance.CapturedAtUtc == record.Provenance.CapturedAtUtc &&
                node.Point.Title == record.Title && node.Point.Tags.SequenceEqual(record.Tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                "actual-provenance-and-canonical-payload-binding");
            // Full canonical content is already known from the successful immutable
            // fixture creates. It is never fetched from a preview or extra query.
            var contentHash = GrowthCorpus.Hash(System.Text.Encoding.UTF8.GetBytes(record.Content));
            var provenanceHash = hit.Provenance.SourceId is null ? null : GrowthCorpus.Hash(
                JsonSerializer.SerializeToUtf8Bytes(new { hit.RecordType, hit.Kind, hit.Tier, hit.Provenance }));
            return new CortexKnowledgeEndpointIdentity(node.Coordinate, contentHash, provenanceHash);
        }).ToArray();
        return new(query.Id, query.Family, query.Text, lexical, current.Items.ToArray(), graph, identities,
            after, true, true, null, null);
    }

    private static async Task<string> ContextAsync(GrowthHit[] hits, Dictionary<string, EndpointRecord> records,
        SqliteCortexV2OperationalStore operational, CancellationToken token)
    {
        var context = new List<GrowthContext>();
        foreach (var hit in hits)
        {
            var read = await operational.ReadAsync(new(hit.Coordinate.Namespace, CortexRecordTypeV2.Knowledge,
                hit.Coordinate.RecordId, hit.Coordinate.Revision), token).ConfigureAwait(false);
            EndpointProgram.Need(read.Record is CortexKnowledgeV2, "lexical-exact-type");
            var value = (CortexKnowledgeV2)read.Record!;
            var expected = records[hit.Coordinate.RecordId];
            EndpointProgram.Need(value.Namespace == hit.Coordinate.Namespace && value.RecordId == hit.Coordinate.RecordId &&
                value.Revision == hit.Coordinate.Revision && value.Title == expected.Title && value.Content == expected.Content,
                "lexical-canonical-revision");
            context.Add(new(hit.Coordinate, value.Title, value.Content));
        }
        return GrowthCorpus.Hash(JsonSerializer.SerializeToUtf8Bytes(context));
    }

    private static EndpointReachEvidence Reach(EndpointGoldCase truth, SqliteCortexKnowledgeTraversalResult graph,
        string[] selected)
    {
        var targets = truth.PrimaryTargetIds.ToHashSet(StringComparer.Ordinal);
        var reached = graph.Traversal.Admitted.Where(node => targets.Contains(node.Coordinate.RecordId))
            .Select(node => node.Coordinate.RecordId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unseen = targets.Except(reached, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var old = graph.Items.Select(item => item.RecordId).ToHashSet(StringComparer.Ordinal);
        var chosen = selected.ToHashSet(StringComparer.Ordinal);
        return new(reached.Length, reached, unseen.Length, unseen,
            reached.Where(id => !old.Contains(id)).ToArray(), reached.Where(id => !chosen.Contains(id)).ToArray());
    }

    private static Dictionary<string, EndpointGroupCoverage>? GroupCoverage(EndpointGoldCase truth,
        Dictionary<string, string[]> methods)
    {
        if (truth.AnswerGroups is null) return null;
        var required = truth.AnswerGroups.Where(group => group.Required).ToArray();
        return methods.ToDictionary(pair => pair.Key, pair =>
        {
            var selected = pair.Value.ToHashSet(StringComparer.Ordinal);
            var covered = required.Count(group => group.AnyOf.Any(selected.Contains));
            return new EndpointGroupCoverage(required.Length, covered,
                required.Length == 0 ? null : covered / (double)required.Length);
        });
    }

    internal static EndpointSelectionReport Score(EndpointGraphCache cache, Dictionary<string, EndpointGoldCase> gold, string cacheSha)
    {
        var output = new List<EndpointObservation>();
        foreach (var query in cache.Queries)
        {
            try
            {
                var truth = gold[query.Id];
                var targets = truth.PrimaryTargetIds.ToHashSet(StringComparer.Ordinal);
                var support = truth.UsefulSupport.Keys.ToHashSet(StringComparer.Ordinal);
                var before = JsonSerializer.Serialize(query.Graph);
                var off = CortexKnowledgeGeometry.SelectEndpoints(query.Text, query.Graph.Traversal, query.Identities);
                EndpointProgram.Need(!off.Enabled && off.Selected.Select(value => value.Node.Coordinate)
                    .SequenceEqual(query.Graph.Traversal.Ranked.Select(value => value.Coordinate)) &&
                    off.Selected.All(value => value.SelectionScore is null && value.TraversalScore == value.Node.Score),
                    "selector-default-off-parity");
                var selected = CortexKnowledgeGeometry.SelectEndpoints(query.Text, query.Graph.Traversal,
                    query.Identities, new(Enabled: true));
                var replay = CortexKnowledgeGeometry.SelectEndpoints(query.Text, query.Graph.Traversal,
                    query.Identities, new(Enabled: true));
                EndpointProgram.Need(ChainEvaluation.Same(selected, replay) && JsonSerializer.Serialize(query.Graph) == before &&
                    !selected.SelectionWasTruncated && selected.Decisions.Count == query.Graph.Traversal.Admitted.Count &&
                    selected.Selected.Count <= 4, "selector-replay-no-graph-mutation");
                var ids = selected.Selected.Select(value => value.Node.Coordinate.RecordId).ToArray();
                output.Add(new(query.Id, query.Family, "engineering-passed", null,
                    ChainEvaluation.Metrics(query.Lexical.Select(value => value.Coordinate.RecordId), targets, support),
                    ChainEvaluation.Metrics(query.Current.Select(value => value.RecordId), targets, support),
                    ChainEvaluation.Metrics(query.Graph.Items.Select(value => value.RecordId), targets, support),
                    ChainEvaluation.Metrics(ids, targets, support),
                    selected.Selected.Select(value => new EndpointSelected(value.Node.Coordinate, value.TraversalScore,
                        value.SelectionScore, value.Features, value.EquivalentCoordinates)).ToArray(),
                    selected.Decisions, true, true, true, selected.StopReason, selected.TraversalWasTruncated, 0,
                    GroupCoverage(truth, new Dictionary<string, string[]>
                    {
                        ["lexical"] = query.Lexical.Select(value => value.Coordinate.RecordId).ToArray(),
                        ["current"] = query.Current.Select(value => value.RecordId).ToArray(),
                        ["oldChain2"] = query.Graph.Items.Select(value => value.RecordId).ToArray(),
                        ["selector"] = ids
                    }), Reach(truth, query.Graph, ids)));
            }
            catch (Exception error)
            {
                output.Add(new(query.Id, query.Family, "engineering-failed", GrowthEvaluation.DescribeFailure(error, "pure-endpoint-selection"),
                    null, null, null, null, null, null, false, false, false, null, null, null));
            }
        }
        return new("pegasus.cortex.endpoint-selection-report/1", cache.Dataset, cache.CaseId,
            CortexKnowledgeGeometry.EndpointSelectionPolicyVersion, EndpointProgram.PolicySha, cacheSha,
            output.All(value => value.Status == "engineering-passed") ? "passed" : "failed", cache.Queries.Length,
            output.ToArray(), ["Selection consumes the same cached admitted graph and adds0provider queries.",
                "Traversal scores and original coordinates/provenance remain unchanged; selection scores/features are separate.",
                "Representative target/support/noise counts exclude duplicate aliases; aliases remain visible and are not extra returned hits.",
                "Known C3data is development/regression. Native holdout is separately frozen and must never drive retuning.",
                "Similarity/self-support are lexical estimates, not logical proof or model-answer verification.",
                "Default-off/graph/replay checks are pure postprocessing; live context/normal/references are in the capture cache."]);
    }
}
