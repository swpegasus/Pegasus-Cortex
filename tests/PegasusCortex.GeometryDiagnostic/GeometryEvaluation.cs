using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.GeometryDiagnostic;

internal sealed record RecordCoordinate(string Namespace, string RecordId, long Revision);
internal sealed record RankedRecord(RecordCoordinate Coordinate, double Score);
internal sealed record SelectedContext(RecordCoordinate Coordinate, string Title, string Content);
internal sealed record CandidateProjection(SelectedContext[] Context, RankedRecord[]? GeometryDiagnostics);
internal sealed record RankingMetrics(int Returned, int RelevantReturned, int EligibleGoldRelevant,
    double PrecisionAt4, double? RecallAt4, double ReciprocalRankAt4, double? NoiseFraction);
internal sealed record MetricDelta(double PrecisionAt4, double? RecallAt4, double ReciprocalRankAt4,
    int DeltaIrrelevantReturned);
internal sealed record RankMovement(RecordCoordinate Coordinate, bool Relevant, int? LexicalRank, int? GeometryRank);
internal sealed record CaseMeasurement(string Id, string Status, string? Failure,
    int EligibleRecords, int ExcludedRecords, int? LexicalSeedCount, bool? GeometryHasMore,
    RankedRecord[]? Lexical, RankedRecord[]? Geometry, RankingMetrics? LexicalMetrics,
    RankingMetrics? GeometryMetrics, MetricDelta? Delta, int? AddedRelevant, int? AddedIrrelevant,
    int? DisplacedRelevant, string? QualityOutcome, RankMovement[]? RankMovements,
    string? LexicalContextSha256, bool? ContextIdentical, bool? DeterministicReplay);
internal sealed record AggregateMetrics(int Cases, int ZeroGoldCases, int RecallCases,
    double MeanPrecisionAt4, double? MeanRecallAt4, double MeanReciprocalRankAt4,
    int Returned, int RelevantReturned, double? NoiseFraction);
internal sealed record EvaluationResult(CaseMeasurement[] Cases, bool Cancelled,
    AggregateMetrics? LexicalAggregate, AggregateMetrics? GeometryAggregate);

internal static class GeometryEvaluation
{
    internal const int K = 4;
    internal const int LexicalPoolLimit = 32;
    private static readonly DateTimeOffset CapturedAt = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    internal static void ValidateManifest(GeometryCase[] cases)
    {
        Require(cases.Length == 12 && cases.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() == 12,
            "The fixed manifest requires twelve unique cases.");
        foreach (var value in cases)
        {
            Require(value.Records.Length is >= 1 and <= 12 && value.Query.Length is > 0 and <= 128,
                "Case bounds exceeded.");
            Require(value.Records.Select(record => record.Namespace + "/" + record.RecordId)
                .Distinct(StringComparer.Ordinal).Count() == value.Records.Length, "Duplicate fixture coordinate.");
            foreach (var record in value.Records)
            {
                Require(record.Namespace is GeometryCases.Shared or GeometryCases.Excluded,
                    "Unexpected fixture namespace.");
                Require(record.RecordId.Length is > 0 and <= 32 && record.Title.Length <= 128 &&
                    Encoding.UTF8.GetByteCount(record.Content) <= 1_024 && record.Tags.Length <= 8 &&
                    !string.IsNullOrWhiteSpace(record.Rationale), "Invalid bounded fixture or missing gold rationale.");
            }
        }
    }

    // Hand-calculated arithmetic checks are pure in-memory evidence, not retrieval evidence.
    internal static void CheckPureChecks()
    {
        var gold = new HashSet<string>(["a", "b"], StringComparer.Ordinal);
        var partial = Measure(["x", "b"], gold);
        Require(partial.PrecisionAt4 == 0.25 && partial.RecallAt4 == 0.5 &&
            partial.ReciprocalRankAt4 == 0.5 && partial.NoiseFraction == 0.5,
            "Partial-ranking metric arithmetic failed.");
        var empty = Measure([], gold);
        Require(empty.PrecisionAt4 == 0 && empty.RecallAt4 == 0 &&
            empty.ReciprocalRankAt4 == 0 && empty.NoiseFraction is null,
            "Empty-ranking metric arithmetic failed.");
        var noGold = Measure(["x"], new HashSet<string>(StringComparer.Ordinal));
        Require(noGold.RecallAt4 is null && noGold.ReciprocalRankAt4 == 0 &&
            noGold.PrecisionAt4 == 0 && noGold.NoiseFraction == 1,
            "Zero-gold metric arithmetic failed.");
        SelectedContext[] selected = [new(new(GeometryCases.Shared, "lexical", 1), "Exact title", "Exact body")];
        RankedRecord[] candidates = [new(new(GeometryCases.Shared, "geometry-only", 1), 42)];
        var off = Project(selected, candidates, geometryEnabled: false);
        var on = Project(selected, candidates, geometryEnabled: true);
        Require(off.GeometryDiagnostics is null && on.GeometryDiagnostics?.Length == 1 &&
            JsonSerializer.Serialize(off.Context) == JsonSerializer.Serialize(on.Context) &&
            on.Context.Single().Coordinate.RecordId == "lexical",
            "Pure projection admitted a geometry-only context record.");
        var singleGold = new HashSet<string>(["a"], StringComparer.Ordinal);
        var noiseOnly = CompareMetrics(Measure(["a"], singleGold), Measure(["a", "x"], singleGold));
        Require(noiseOnly.PrecisionAt4 == 0 && noiseOnly.RecallAt4 == 0 &&
            noiseOnly.ReciprocalRankAt4 == 0 && noiseOnly.DeltaIrrelevantReturned == 1 &&
            Outcome(noiseOnly) == "regression", "Noise-only arithmetic/classification failed.");
        var mixed = CompareMetrics(Measure(["x"], singleGold), Measure(["a", "x", "y"], singleGold));
        Require(mixed.PrecisionAt4 == 0.25 && mixed.RecallAt4 == 1 &&
            mixed.ReciprocalRankAt4 == 1 && mixed.DeltaIrrelevantReturned == 1 &&
            Outcome(mixed) == "mixed", "Relevant-gain-plus-noise arithmetic/classification failed.");
    }

    internal static async Task<EvaluationResult> RunAsync(
        GeometryCase[] cases, string fixtureRoot, bool geometryEnabled, CancellationToken cancellationToken)
    {
        var measurements = new List<CaseMeasurement>(cases.Length);
        var cancelled = false;
        foreach (var value in cases)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                measurements.Add(await RunCaseAsync(value, fixtureRoot, geometryEnabled, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                measurements.Add(Failed(value, "cancelled", "OperationCanceledException"));
                break;
            }
            catch (Exception exception)
            {
                // Persist bounded failure identity, never arbitrary provider messages or terminal output.
                measurements.Add(Failed(value, "engineering-failed",
                    exception.GetType().Name + ":" + exception.HResult.ToString("X8")));
            }
        }
        if (cancelled)
            measurements.AddRange(cases.Skip(measurements.Count)
                .Select(value => Failed(value, "not-executed", "CancellationBeforeCase")));
        return new(measurements.ToArray(), cancelled,
            Aggregate(measurements.Where(value => value.Status == "engineering-passed")
                .Select(value => value.LexicalMetrics!).ToArray()),
            geometryEnabled ? Aggregate(measurements.Where(value => value.Status == "engineering-passed")
                .Select(value => value.GeometryMetrics!).ToArray()) : null);
    }

    private static async Task<CaseMeasurement> RunCaseAsync(
        GeometryCase value, string fixtureRoot, bool geometryEnabled, CancellationToken cancellationToken)
    {
        var caseRoot = Path.Combine(fixtureRoot, value.Id);
        DiagnosticProgram.CreateFreshDirectory(caseRoot);
        var lowLevel = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(
            Path.Combine(caseRoot, SqliteCortexV2StoreOptions.RequiredDatabaseFileName),
            BusyTimeoutMilliseconds: 1_000, MaximumContentBytes: 4_096,
            MaximumSearchChunksPerHead: 16, MaximumDatabasePages: 256), new FixtureClock());
        await using var operational = new SqliteCortexV2OperationalStore(lowLevel, "geometry-diagnostic-1");
        await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in value.Records)
        {
            var seed = new CortexKnowledgeCommandV2(record.Namespace, CortexKnowledgeActionV2.Create,
                record.RecordId, ExpectedRevision: null, IdempotencyKey: value.Id + "-" + record.RecordId,
                Kind: CortexKnowledgeKindV2.Observation, Tier: CortexKnowledgeTierV2.Verified,
                Title: record.Title, Content: record.Content, Tags: record.Tags,
                Provenance: new(CortexProvenanceSourceV2.Agent, "synthetic-geometry-fixture", "Synthetic fixture only."),
                ReplacementRecordId: null)
            {
                EffectiveProvenance = new(CortexProvenanceSourceV2.Agent, "geometry_fixture",
                    "synthetic-geometry-fixture", "Synthetic fixture only; not live evidence.", CapturedAt)
            };
            var seeded = await operational.ExecuteKnowledgeAsync(seed, cancellationToken).ConfigureAwait(false);
            Require(seeded.Record is { Revision: 1 }, "Fixture seed did not create revision one.");
        }

        var lexicalRequest = new SqliteCortexV2SearchRequest(GeometryCases.Shared, value.Query,
            "knowledge", LexicalPoolLimit, MinimumMatch: 1);
        var pool = await lowLevel.SearchHeadsAsync(lexicalRequest, cancellationToken).ConfigureAwait(false);
        var lexical = pool.Take(K).Select(hit => new RankedRecord(
            new(hit.Namespace, hit.RecordId, hit.Revision), hit.Score)).ToArray();
        ValidateRanking(value, lexical);
        if (value.RequireZeroLexicalSeeds)
            Require(pool.Count == 0, "Zero-seed fixture accidentally matched a query token.");
        if (value.RequireLexicallyAbsentRelevantId is { } absentId)
            Require(pool.All(hit => hit.RecordId != absentId), "Bridge fixture accidentally matched the lexical query.");

        // Only lexical-selected exact revisions may be hydrated, even when diagnostics are enabled.
        var before = await ReadLexicalContextAsync(value, lexical, operational, cancellationToken).ConfigureAwait(false);
        var off = Project(before, null, geometryEnabled: false);
        RankedRecord[]? geometry = null;
        bool? geometryHasMore = null;
        if (geometryEnabled)
        {
            var request = new CortexSearchRequestV2([GeometryCases.Shared], value.Query,
                [CortexKnowledgeKindV2.Observation], AfterCursor: null, Limit: K,
                MaxUtf8Bytes: 4_096, Format: CortexResponseFormatV2.Compact);
            var first = await operational.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            var replay = await operational.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            Require(first.EffectiveNamespaces.SequenceEqual([GeometryCases.Shared]),
                "Geometry result changed the selected namespace.");
            Require(first.Items.All(hit => hit.RecordType == CortexRecordTypeV2.Knowledge),
                "Geometry returned an unexpected record type.");
            geometry = first.Items.Select(hit => new RankedRecord(
                new(hit.Namespace, hit.RecordId, hit.Revision), hit.Score)).ToArray();
            ValidateRanking(value, geometry);
            Require(JsonSerializer.Serialize(first) == JsonSerializer.Serialize(replay),
                "Actual repository geometry replay changed.");
            geometryHasMore = first.HasMore;
        }

        var poolReplay = await lowLevel.SearchHeadsAsync(lexicalRequest, cancellationToken).ConfigureAwait(false);
        Require(JsonSerializer.Serialize(pool) == JsonSerializer.Serialize(poolReplay),
            "Lexical search changed after optional diagnostic retrieval.");
        var after = await ReadLexicalContextAsync(value, lexical, operational, cancellationToken).ConfigureAwait(false);
        var on = Project(after, geometry, geometryEnabled);
        var contextBefore = JsonSerializer.SerializeToUtf8Bytes(off.Context);
        var contextAfter = JsonSerializer.SerializeToUtf8Bytes(on.Context);
        Require(contextBefore.AsSpan().SequenceEqual(contextAfter),
            "Optional diagnostics changed lexical-selected exact-revision context.");
        Require(off.GeometryDiagnostics is null && (geometryEnabled || on.GeometryDiagnostics is null),
            "Disabled diagnostics leaked candidate metadata.");

        var gold = value.Records.Where(record => record.Namespace == GeometryCases.Shared && record.Relevant)
            .Select(record => Key(record.Namespace, record.RecordId)).ToHashSet(StringComparer.Ordinal);
        var lexicalMetrics = Measure(lexical.Select(hit => Key(hit.Coordinate)), gold);
        RankingMetrics? geometryMetrics = geometry is null ? null :
            Measure(geometry.Select(hit => Key(hit.Coordinate)), gold);
        var delta = geometryMetrics is null ? null : CompareMetrics(lexicalMetrics, geometryMetrics);
        var lexicalKeys = lexical.Select(hit => Key(hit.Coordinate)).ToHashSet(StringComparer.Ordinal);
        var geometryKeys = geometry?.Select(hit => Key(hit.Coordinate)).ToHashSet(StringComparer.Ordinal);
        var added = geometryKeys?.Except(lexicalKeys).ToArray();
        var movements = geometry is null ? null : lexical.Concat(geometry)
            .DistinctBy(hit => Key(hit.Coordinate)).Select(hit => new RankMovement(hit.Coordinate,
                gold.Contains(Key(hit.Coordinate)), Position(lexical, hit.Coordinate),
                Position(geometry, hit.Coordinate))).ToArray();
        return new(value.Id, "engineering-passed", null,
            value.Records.Count(record => record.Namespace == GeometryCases.Shared),
            value.Records.Count(record => record.Namespace != GeometryCases.Shared), pool.Count, geometryHasMore,
            lexical, geometry, lexicalMetrics, geometryMetrics, delta,
            added?.Count(gold.Contains), added?.Count(key => !gold.Contains(key)),
            geometryKeys is null ? null : lexicalKeys.Except(geometryKeys).Count(gold.Contains),
            delta is null ? null : Outcome(delta), movements,
            Convert.ToHexString(SHA256.HashData(contextBefore)).ToLowerInvariant(), true, true);
    }

    private static CandidateProjection Project(SelectedContext[] lexicalContext,
        RankedRecord[]? geometry, bool geometryEnabled) =>
        new(lexicalContext, geometryEnabled ? geometry : null);

    private static async Task<SelectedContext[]> ReadLexicalContextAsync(GeometryCase value,
        RankedRecord[] lexical, SqliteCortexV2OperationalStore operational, CancellationToken cancellationToken)
    {
        var result = new List<SelectedContext>(lexical.Length);
        foreach (var hit in lexical)
        {
            var coordinate = hit.Coordinate;
            var exact = await operational.ReadAsync(new(coordinate.Namespace, CortexRecordTypeV2.Knowledge,
                coordinate.RecordId, coordinate.Revision), cancellationToken).ConfigureAwait(false);
            Require(exact.Record is CortexKnowledgeV2, "Exact lexical read was not typed knowledge.");
            var record = (CortexKnowledgeV2)exact.Record!;
            var fixture = value.Records.Single(item => item.Namespace == coordinate.Namespace &&
                item.RecordId == coordinate.RecordId);
            Require(exact.Revision == coordinate.Revision && record.Revision == coordinate.Revision &&
                record.Namespace == coordinate.Namespace && record.RecordId == coordinate.RecordId &&
                record.Content == fixture.Content && record.Title == fixture.Title,
                "Exact lexical-selected revision differed from the fixture.");
            result.Add(new(coordinate, record.Title, record.Content));
        }
        return result.ToArray();
    }

    private static void ValidateRanking(GeometryCase value, RankedRecord[] ranking)
    {
        Require(ranking.Length <= K && ranking.Select(hit => Key(hit.Coordinate))
            .Distinct(StringComparer.Ordinal).Count() == ranking.Length, "Invalid bounded ranking.");
        foreach (var hit in ranking)
            Require(hit.Coordinate.Namespace == GeometryCases.Shared && hit.Coordinate.Revision == 1 &&
                double.IsFinite(hit.Score) && value.Records.Any(record =>
                    record.Namespace == hit.Coordinate.Namespace && record.RecordId == hit.Coordinate.RecordId),
                "Ranking escaped its selected fixture namespace or revision.");
    }

    private static RankingMetrics Measure(IEnumerable<string> ranking, HashSet<string> gold)
    {
        var keys = ranking.Take(K).ToArray();
        Require(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length, "Duplicate metric coordinate.");
        var relevant = keys.Count(gold.Contains);
        var first = Array.FindIndex(keys, gold.Contains);
        // Zero-gold RR is deliberately zero and is included in macro MRR.
        return new(keys.Length, relevant, gold.Count, relevant / (double)K,
            gold.Count == 0 ? null : relevant / (double)gold.Count,
            first < 0 ? 0 : 1d / (first + 1),
            keys.Length == 0 ? null : (keys.Length - relevant) / (double)keys.Length);
    }

    private static AggregateMetrics? Aggregate(RankingMetrics[] metrics)
    {
        if (metrics.Length == 0)
            return null;
        var recall = metrics.Where(value => value.RecallAt4.HasValue).ToArray();
        var returned = metrics.Sum(value => value.Returned);
        var relevant = metrics.Sum(value => value.RelevantReturned);
        return new(metrics.Length, metrics.Count(value => value.EligibleGoldRelevant == 0), recall.Length,
            metrics.Average(value => value.PrecisionAt4),
            recall.Length == 0 ? null : recall.Average(value => value.RecallAt4!.Value),
            metrics.Average(value => value.ReciprocalRankAt4), returned, relevant,
            returned == 0 ? null : (returned - relevant) / (double)returned);
    }

    private static MetricDelta CompareMetrics(RankingMetrics lexical, RankingMetrics geometry) =>
        new(geometry.PrecisionAt4 - lexical.PrecisionAt4,
            geometry.RecallAt4 - lexical.RecallAt4,
            geometry.ReciprocalRankAt4 - lexical.ReciprocalRankAt4,
            (geometry.Returned - geometry.RelevantReturned) - (lexical.Returned - lexical.RelevantReturned));

    private static string Outcome(MetricDelta delta)
    {
        // More irrelevant returned records is a negative quality direction, even with unchanged relevance.
        var values = new[] { delta.PrecisionAt4, delta.RecallAt4 ?? 0, delta.ReciprocalRankAt4,
            -delta.DeltaIrrelevantReturned };
        if (values.All(value => value == 0)) return "no-measured-gain";
        if (values.All(value => value >= 0)) return "gain";
        if (values.All(value => value <= 0)) return "regression";
        return "mixed";
    }

    private static int? Position(RankedRecord[] ranking, RecordCoordinate coordinate)
    {
        var index = Array.FindIndex(ranking, hit => hit.Coordinate == coordinate);
        return index < 0 ? null : index + 1;
    }

    private static string Key(RecordCoordinate value) => Key(value.Namespace, value.RecordId);
    private static string Key(string ns, string id) => ns + "/" + id;
    private static CaseMeasurement Failed(GeometryCase value, string status, string failure) =>
        new(Id: value.Id, Status: status, Failure: failure,
            EligibleRecords: value.Records.Count(record => record.Namespace == GeometryCases.Shared),
            ExcludedRecords: value.Records.Count(record => record.Namespace != GeometryCases.Shared),
            LexicalSeedCount: null, GeometryHasMore: null, Lexical: null, Geometry: null,
            LexicalMetrics: null, GeometryMetrics: null, Delta: null, AddedRelevant: null,
            AddedIrrelevant: null, DisplacedRelevant: null, QualityOutcome: null, RankMovements: null,
            LexicalContextSha256: null, ContextIdentical: null, DeterministicReplay: null);

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => CapturedAt;
    }
}
