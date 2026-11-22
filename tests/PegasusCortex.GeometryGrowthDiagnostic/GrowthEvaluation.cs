using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.GeometryGrowthDiagnostic;

internal sealed record GrowthCoordinate(string Namespace, string RecordId, long Revision);
internal sealed record GrowthHit(GrowthCoordinate Coordinate, double Score);
internal sealed record GrowthContext(GrowthCoordinate Coordinate, string Title, string Content);
internal sealed record RoleMetrics(int Returned, int TargetHits, int SupportHits, int NoiseHits,
    int TargetAvailable, int SupportAvailable, double TargetPrecisionAt4, double? TargetRecallAt4,
    double TargetReciprocalRankAt4, double SupportPrecisionAt4, double? SupportCoverage,
    double? NoiseFraction, double? HelpfulFraction, double TargetRecallCeiling);
internal sealed record UniformMath(string Kind, int Eligible, int Draws, double ExpectedTargetHits,
    double? ExpectedTargetRecall, double ExpectedSupportHits, double ExpectedNoiseHits, double ProbabilityAnyTarget);
internal sealed record GrowthDelta(int TargetHits, double? TargetRecall, double TargetReciprocalRank,
    int SupportHits, int NoiseHits, string TargetOutcome);
internal sealed record GrowthFailureEvidence(string Stage, string Invariant, long? Expected, long? Observed,
    bool ObservedIsLowerBound, string ExceptionType, int HResult, int? SqliteCode, int? SqliteExtendedCode, int? NativeCode);
internal sealed class GrowthInvariantException(string invariant, long? expected = null, long? observed = null,
    bool observedIsLowerBound = false) : Exception("Bounded diagnostic invariant failed.")
{
    internal string Invariant { get; } = invariant;
    internal long? Expected { get; } = expected;
    internal long? Observed { get; } = observed;
    internal bool ObservedIsLowerBound { get; } = observedIsLowerBound;
}
internal sealed record GrowthObservation(string CellId, string QuestionId, string Family, string Status,
    GrowthFailureEvidence? Failure, int Eligible, int? DirectPool32, int? ProbeCount256, bool? ProbeAtLimit,
    bool? DirectTruncationObserved, bool? ExpansionEligibleBySourceRule,
    GrowthHit[]? Lexical, GrowthHit[]? Geometry, RoleMetrics? LexicalMetrics,
    RoleMetrics? GeometryMetrics, GrowthDelta? Delta, UniformMath? LexicalMatchedMath,
    UniformMath? GeometryMatchedMath, UniformMath? FixedFourMath,
    string? LexicalContextSha256, string? LexicalRankingSha256, bool? ContextIdentical,
    bool? DeterministicReplay);
internal sealed record CellAggregate(string CellId, int CompletedQuestions, int DistinctFamilies,
    double MeanLexicalTargetRecall, double? MeanGeometryTargetRecall,
    double MeanLexicalTargetRR, double? MeanGeometryTargetRR, int? ParetoTargetGainQuestions,
    int? ParetoTargetRegressionQuestions, int? MixedTargetMetricQuestions,
    int? TargetHitLossQuestions, int? FirstTargetRankLossQuestions, int? AnyTargetMetricLossQuestions,
    int? SupportDelta, int? NoiseDelta);
internal sealed record TargetChangeCounts(int ParetoGains, int ParetoRegressions, int Mixed,
    int TargetHitLosses, int FirstTargetRankLosses, int AnyTargetMetricLosses);
internal sealed record GrowthMeasurement(GrowthObservation[] Observations, CellAggregate[] Aggregates,
    bool Cancelled, int PlannedObservations, GrowthFailureEvidence[] CellFailures, string Evidence);

internal static class GrowthEvaluation
{
    private const int DirectLimit = 32;
    private const int ProbeLimit = 256;
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    internal static void PureChecks()
    {
        var metric = Measure(["t", "s", "n"], new HashSet<string>(["t", "u"], StringComparer.Ordinal),
            new HashSet<string>(["s"], StringComparer.Ordinal));
        Require(metric.TargetPrecisionAt4 == .25 && metric.TargetRecallAt4 == .5 &&
            metric.SupportHits == 1 && metric.NoiseHits == 1 && metric.SupportCoverage == 1,
            "pure-role-arithmetic");
        var empty = Measure([], new HashSet<string>(["t"], StringComparer.Ordinal), []);
        Require(empty.TargetRecallAt4 == 0 && empty.NoiseFraction is null && empty.Returned == 0,
            "pure-empty-ranking");
        var lostTarget = Delta(metric, Measure(["s", "n"], new HashSet<string>(["t", "u"], StringComparer.Ordinal),
            new HashSet<string>(["s"], StringComparer.Ordinal)));
        Require(lostTarget.TargetOutcome == "regression" && lostTarget.TargetHits == -1,
            "pure-target-loss");
        var moreSupport = Measure(["t", "s"], new HashSet<string>(["t"], StringComparer.Ordinal),
            new HashSet<string>(["s", "v", "w"], StringComparer.Ordinal));
        Require(moreSupport.TargetRecallAt4 == 1 && moreSupport.SupportCoverage == 1d / 3,
            "pure-fixed-target-denominator");
        var tradeoffTargets = new HashSet<string>(["t", "u"], StringComparer.Ordinal);
        var tradeoff = Delta(Measure(["n1", "n2", "t", "u"], tradeoffTargets, []),
            Measure(["t"], tradeoffTargets, []));
        var tradeoffCounts = CountTargetChanges([tradeoff]);
        Require(tradeoff.TargetOutcome == "mixed-target-metrics" && tradeoff.TargetHits == -1 &&
            tradeoff.TargetRecall == -.5 && tradeoff.TargetReciprocalRank > 0 &&
            tradeoffCounts.TargetHitLosses == 1 && tradeoffCounts.FirstTargetRankLosses == 0 &&
            tradeoffCounts.AnyTargetMetricLosses == 1 && tradeoffCounts.Mixed == 1 &&
            tradeoffCounts.ParetoRegressions == 0, "pure-mixed-target-loss-accounting");
        var math = Uniform(10, 2, 3, 4);
        Require(Math.Abs(math.ExpectedTargetHits - .8) < 1e-12 &&
            Math.Abs(math.ProbabilityAnyTarget - 2d / 3) < 1e-12 &&
            math.Kind == "analytical-not-provider", "pure-uniform-reference");
        var zero = Uniform(10, 0, 3, 0);
        Require(zero.ExpectedTargetRecall is null && zero.ProbabilityAnyTarget == 0,
            "pure-zero-reference");
        GrowthContext[] selected = [new(new("knowledge.shared", "lexical", 1), "Title", "Exact body")];
        GrowthHit[] candidate = [new(new("knowledge.shared", "candidate-only", 1), 4)];
        var off = Project(selected, candidate, false);
        var on = Project(selected, candidate, true);
        Require(off.Diagnostics is null && on.Diagnostics?.Length == 1 &&
            JsonSerializer.Serialize(off.Context) == JsonSerializer.Serialize(on.Context),
            "pure-lexical-projection");
        GrowthFailureEvidence? captured = null;
        try { CheckCount("frozen-direct-hit-count", 3, 5); }
        catch (GrowthInvariantException error) { captured = Evidence(error, "lexical-probe"); }
        Require(captured is { Stage: "lexical-probe", Invariant: "frozen-direct-hit-count", Expected: 3, Observed: 5 } &&
            !captured.ObservedIsLowerBound, "pure-count-failure-envelope");
        var sqlite = Evidence(new SqliteException("Pure fixture text is excluded from evidence.", 19), "sqlite-initialize");
        Require(sqlite.SqliteCode == 19 && sqlite.SqliteExtendedCode == 19 &&
            sqlite.Stage == "sqlite-initialize" && sqlite.Invariant == "unexpected-exception",
            "pure-sqlite-code-envelope");
    }

    internal static async Task<GrowthMeasurement> MeasureAsync(GrowthManifest manifest, GrowthRun run,
        string fixtureRoot, CancellationToken cancellationToken)
    {
        GrowthCorpus.Validate(manifest);
        var records = manifest.Records.ToDictionary(value => value.Payload.Id, StringComparer.Ordinal);
        var questions = manifest.Questions.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var observations = new List<GrowthObservation>();
        var cancelled = false;
        var cellFailures = new List<GrowthFailureEvidence>();
        var planned = run.CellIds.Length * run.QuestionIds.Length;
        for (var cellIndex = 0; cellIndex < run.CellIds.Length; cellIndex++)
        {
            var cell = manifest.Cells.Single(value => value.Id == run.CellIds[cellIndex]);
            var cellResults = new List<GrowthObservation>();
            var setupStage = "cell-directory";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cellRoot = Path.Combine(fixtureRoot, cell.Id);
                GrowthProgram.CreateFreshDirectory(cellRoot);
                setupStage = "sqlite-construction";
                var lowLevel = new SqliteCortexV2Store(new(
                    Path.Combine(cellRoot, SqliteCortexV2StoreOptions.RequiredDatabaseFileName),
                    BusyTimeoutMilliseconds: 1_000, MaximumContentBytes: 4_096,
                    MaximumSearchChunksPerHead: 16, MaximumDatabasePages: 4_096), new FixtureClock());
                await using var operational = new SqliteCortexV2OperationalStore(lowLevel, "growth-diagnostic-1");
                setupStage = "sqlite-initialize";
                await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
                setupStage = "seed-records";
                foreach (var id in cell.RecordIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var payload = records[id].Payload;
                    // Only payload fields enter SQLite. Gold roles, bank labels, fact keys
                    // and rationales never become searchable attributes.
                    var command = new CortexKnowledgeCommandV2("knowledge.shared", CortexKnowledgeActionV2.Create,
                        payload.Id, null, "seed-" + payload.Id, CortexKnowledgeKindV2.Observation,
                        CortexKnowledgeTierV2.Verified, payload.Title, payload.Content, payload.Tags,
                        new(CortexProvenanceSourceV2.Agent, "synthetic-growth", "Synthetic fixture only."), null)
                    {
                        EffectiveProvenance = new(CortexProvenanceSourceV2.Agent, "growth_fixture",
                            "synthetic-growth", "Synthetic fixture only; not live evidence.", FixedTime)
                    };
                    var written = await operational.ExecuteKnowledgeAsync(command, cancellationToken).ConfigureAwait(false);
                    CheckCount("seed-revision-one", 1, written.Record?.Revision ?? -1);
                }
                setupStage = "shared-universe";
                var counters = await lowLevel.GetCatalogCountersAsync(cancellationToken).ConfigureAwait(false);
                CheckCount("eligible-row-count", cell.RecordIds.Length, counters.HeadRows);
                foreach (var questionId in run.QuestionIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var queryStage = new StageTracker();
                    try
                    {
                        cellResults.Add(await QueryAsync(cell, questions[questionId], records,
                            run.GeometryEnabled, lowLevel, operational, queryStage, cancellationToken).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        cellResults.Add(Failure(cell, questions[questionId], "engineering-failed", Evidence(error, queryStage.Value)));
                    }
                }
                observations.AddRange(cellResults);
                setupStage = "fixture-byte-budget";
                var observedBytes = Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
                if (observedBytes > manifest.Budget.MaximumFixtureBytes)
                    throw new GrowthInvariantException("fixture-byte-budget", manifest.Budget.MaximumFixtureBytes, observedBytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                observations.AddRange(cellResults.Where(value => !observations.Any(old =>
                    old.CellId == value.CellId && old.QuestionId == value.QuestionId)));
                foreach (var questionId in run.QuestionIds.Where(id => !observations.Any(value =>
                             value.CellId == cell.Id && value.QuestionId == id)))
                    observations.Add(Failure(cell, questions[questionId], "cancelled", Synthetic("cell-cancellation", "cancelled")));
                cancelled = true;
                foreach (var remaining in run.CellIds.Skip(cellIndex + 1))
                {
                    var next = manifest.Cells.Single(value => value.Id == remaining);
                    observations.AddRange(run.QuestionIds.Select(id => Failure(next, questions[id],
                        "not-executed", Synthetic("not-started", "cancelled-before-cell"))));
                }
                break;
            }
            catch (Exception error)
            {
                cellFailures.Add(Evidence(error, setupStage));
                observations.AddRange(cellResults.Where(value => !observations.Any(old =>
                    old.CellId == value.CellId && old.QuestionId == value.QuestionId)));
                foreach (var questionId in run.QuestionIds.Where(id => !observations.Any(value =>
                             value.CellId == cell.Id && value.QuestionId == id)))
                    observations.Add(Failure(cell, questions[questionId], "engineering-failed", Evidence(error, setupStage)));
                // A cell-level budget/setup error stops further cells; no automatic repair.
                foreach (var remaining in run.CellIds.Skip(cellIndex + 1))
                {
                    var next = manifest.Cells.Single(value => value.Id == remaining);
                    observations.AddRange(run.QuestionIds.Select(id => Failure(next, questions[id],
                        "not-executed", Synthetic("not-started", "prior-cell-failure"))));
                }
                break;
            }
        }
        var aggregates = observations.Where(value => value.Status == "engineering-passed")
            .GroupBy(value => value.CellId).Select(group => AggregateCell(group, run.GeometryEnabled)).ToArray();
        return new(observations.ToArray(), aggregates, cancelled, planned, cellFailures.ToArray(),
            "Actual isolated shared SQLite and existing public providers only when this measurement mode executes.");
    }

    private static TargetChangeCounts CountTargetChanges(IEnumerable<GrowthDelta> values)
    {
        var changes = values.ToArray();
        return new(changes.Count(value => value.TargetOutcome == "gain"),
            changes.Count(value => value.TargetOutcome == "regression"),
            changes.Count(value => value.TargetOutcome == "mixed-target-metrics"),
            changes.Count(value => value.TargetHits < 0),
            changes.Count(value => value.TargetReciprocalRank < 0),
            changes.Count(value => value.TargetHits < 0 || (value.TargetRecall ?? 0) < 0 || value.TargetReciprocalRank < 0));
    }

    private static CellAggregate AggregateCell(IGrouping<string, GrowthObservation> group, bool enabled)
    {
        var changes = enabled ? CountTargetChanges(group.Select(value => value.Delta!)) : null;
        return new(group.Key, group.Count(), group.Select(value => value.Family).Distinct().Count(),
            group.Average(value => value.LexicalMetrics!.TargetRecallAt4!.Value),
            enabled ? group.Average(value => value.GeometryMetrics!.TargetRecallAt4!.Value) : null,
            group.Average(value => value.LexicalMetrics!.TargetReciprocalRankAt4),
            enabled ? group.Average(value => value.GeometryMetrics!.TargetReciprocalRankAt4) : null,
            changes?.ParetoGains, changes?.ParetoRegressions, changes?.Mixed,
            changes?.TargetHitLosses, changes?.FirstTargetRankLosses, changes?.AnyTargetMetricLosses,
            enabled ? group.Sum(value => value.Delta!.SupportHits) : null,
            enabled ? group.Sum(value => value.Delta!.NoiseHits) : null);
    }

    private static async Task<GrowthObservation> QueryAsync(CorpusCell cell, QuestionGold question,
        Dictionary<string, GrowthRecord> records, bool geometryEnabled, SqliteCortexV2Store lowLevel,
        SqliteCortexV2OperationalStore operational, StageTracker stage, CancellationToken cancellationToken)
    {
        stage.Value = "lexical-direct";
        var request = new SqliteCortexV2SearchRequest("knowledge.shared", question.Query, "knowledge", DirectLimit, 1);
        var pool = await lowLevel.SearchHeadsAsync(request, cancellationToken).ConfigureAwait(false);
        stage.Value = "lexical-probe";
        var probe = await lowLevel.SearchHeadsAsync(request with { Limit = ProbeLimit }, cancellationToken).ConfigureAwait(false);
        if (cell.ExpectedDirectHits.TryGetValue(question.Id, out var expected))
            CheckCount("frozen-direct-hit-count", expected, probe.Count, probe.Count == ProbeLimit);
        var lexical = pool.Take(GrowthCorpus.K).Select(value => new GrowthHit(
            new(value.Namespace, value.RecordId, value.Revision), value.Score)).ToArray();
        ValidateRanking(cell, lexical);
        stage.Value = "lexical-hydration-before";
        var before = await HydrateLexicalAsync(lexical, records, operational, cancellationToken).ConfigureAwait(false);
        GrowthHit[]? geometry = null;
        if (geometryEnabled)
        {
            var search = new CortexSearchRequestV2(["knowledge.shared"], question.Query,
                [CortexKnowledgeKindV2.Observation], null, GrowthCorpus.K, 4_096, CortexResponseFormatV2.Compact);
            stage.Value = "geometry-search";
            var first = await operational.SearchAsync(search, cancellationToken).ConfigureAwait(false);
            stage.Value = "geometry-replay";
            var repeat = await operational.SearchAsync(search, cancellationToken).ConfigureAwait(false);
            Require(first.EffectiveNamespaces.SequenceEqual(["knowledge.shared"]) &&
                first.Items.All(value => value.RecordType == CortexRecordTypeV2.Knowledge) &&
                JsonSerializer.Serialize(first) == JsonSerializer.Serialize(repeat), "candidate-replay-or-scope");
            geometry = first.Items.Select(value => new GrowthHit(
                new(value.Namespace, value.RecordId, value.Revision), value.Score)).ToArray();
            ValidateRanking(cell, geometry);
        }
        stage.Value = "lexical-replay";
        var repeated = await lowLevel.SearchHeadsAsync(request, cancellationToken).ConfigureAwait(false);
        Require(JsonSerializer.Serialize(pool) == JsonSerializer.Serialize(repeated), "lexical-replay");
        stage.Value = "lexical-hydration-after";
        var after = await HydrateLexicalAsync(lexical, records, operational, cancellationToken).ConfigureAwait(false);
        var off = Project(before, geometry, false);
        var on = Project(after, geometry, geometryEnabled);
        stage.Value = "context-parity";
        var contextBytes = JsonSerializer.SerializeToUtf8Bytes(off.Context);
        Require(contextBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(on.Context)) &&
            off.Diagnostics is null, "lexical-context-parity");
        stage.Value = "query-role-metrics";
        var eligible = cell.RecordIds.ToHashSet(StringComparer.Ordinal);
        var targets = question.TargetIds.ToHashSet(StringComparer.Ordinal);
        var support = question.UsefulSupport.Keys.Where(eligible.Contains).ToHashSet(StringComparer.Ordinal);
        Require(targets.All(eligible.Contains) && !targets.Overlaps(support), "eligible-query-roles");
        var left = Measure(lexical.Select(value => value.Coordinate.RecordId), targets, support);
        var right = geometry is null ? null : Measure(geometry.Select(value => value.Coordinate.RecordId), targets, support);
        return new(cell.Id, question.Id, question.Family, "engineering-passed", null, eligible.Count,
            pool.Count, probe.Count, probe.Count == ProbeLimit, probe.Count > DirectLimit,
            pool.Count < GrowthCorpus.K, lexical, geometry, left, right,
            right is null ? null : Delta(left, right),
            Uniform(eligible.Count, targets.Count, support.Count, left.Returned),
            right is null ? null : Uniform(eligible.Count, targets.Count, support.Count, right.Returned),
            Uniform(eligible.Count, targets.Count, support.Count, Math.Min(GrowthCorpus.K, eligible.Count)),
            GrowthCorpus.Hash(contextBytes), GrowthCorpus.Hash(JsonSerializer.SerializeToUtf8Bytes(lexical)), true, true);
    }

    private static async Task<GrowthContext[]> HydrateLexicalAsync(GrowthHit[] lexical,
        Dictionary<string, GrowthRecord> records, SqliteCortexV2OperationalStore operational, CancellationToken token)
    {
        var result = new List<GrowthContext>();
        foreach (var hit in lexical)
        {
            var coordinate = hit.Coordinate;
            var read = await operational.ReadAsync(new("knowledge.shared", CortexRecordTypeV2.Knowledge,
                coordinate.RecordId, coordinate.Revision), token).ConfigureAwait(false);
            Require(read.Record is CortexKnowledgeV2, "exact-knowledge-type");
            var value = (CortexKnowledgeV2)read.Record!;
            var expected = records[coordinate.RecordId].Payload;
            Require(value.Namespace == coordinate.Namespace && value.RecordId == coordinate.RecordId &&
                value.Revision == coordinate.Revision && value.Content == expected.Content && value.Title == expected.Title,
                "exact-lexical-payload");
            result.Add(new(coordinate, value.Title, value.Content));
        }
        return result.ToArray();
    }

    private static (GrowthContext[] Context, GrowthHit[]? Diagnostics) Project(
        GrowthContext[] lexical, GrowthHit[]? candidates, bool enabled) => (lexical, enabled ? candidates : null);

    private static RoleMetrics Measure(IEnumerable<string> ranking, HashSet<string> targets, HashSet<string> support)
    {
        Require(!targets.Overlaps(support), "role-overlap");
        var ids = ranking.Take(GrowthCorpus.K).ToArray();
        Require(ids.Distinct().Count() == ids.Length, "duplicate-result");
        var targetHits = ids.Count(targets.Contains);
        var supportHits = ids.Count(support.Contains);
        var noise = ids.Length - targetHits - supportHits;
        var first = Array.FindIndex(ids, targets.Contains);
        return new(ids.Length, targetHits, supportHits, noise, targets.Count, support.Count,
            targetHits / (double)GrowthCorpus.K, targets.Count == 0 ? null : targetHits / (double)targets.Count,
            first < 0 ? 0 : 1d / (first + 1), supportHits / (double)GrowthCorpus.K,
            support.Count == 0 ? null : supportHits / (double)support.Count,
            ids.Length == 0 ? null : noise / (double)ids.Length,
            ids.Length == 0 ? null : (targetHits + supportHits) / (double)ids.Length,
            targets.Count == 0 ? 0 : Math.Min(GrowthCorpus.K, targets.Count) / (double)targets.Count);
    }

    private static UniformMath Uniform(int eligible, int targets, int support, int draws)
    {
        Require(eligible > 0 && targets >= 0 && support >= 0 && targets + support <= eligible &&
            draws >= 0 && draws <= Math.Min(GrowthCorpus.K, eligible), "analytical-universe");
        var noTarget = 1d;
        for (var index = 0; index < draws; index++)
            noTarget *= Math.Max(0, eligible - targets - index) / (double)(eligible - index);
        return new("analytical-not-provider", eligible, draws, draws * targets / (double)eligible,
            targets == 0 ? null : draws / (double)eligible,
            draws * support / (double)eligible, draws * (eligible - targets - support) / (double)eligible,
            1 - noTarget);
    }

    private static GrowthDelta Delta(RoleMetrics lexical, RoleMetrics geometry)
    {
        var recall = geometry.TargetRecallAt4 - lexical.TargetRecallAt4;
        var rank = geometry.TargetReciprocalRankAt4 - lexical.TargetReciprocalRankAt4;
        var directions = new[] { recall ?? 0, rank };
        var outcome = directions.All(value => value == 0) ? "no-measured-target-change" :
            directions.All(value => value >= 0) ? "gain" :
            directions.All(value => value <= 0) ? "regression" : "mixed-target-metrics";
        return new(geometry.TargetHits - lexical.TargetHits, recall, rank,
            geometry.SupportHits - lexical.SupportHits, geometry.NoiseHits - lexical.NoiseHits, outcome);
    }

    private static void ValidateRanking(CorpusCell cell, GrowthHit[] hits) =>
        Require(hits.Length <= GrowthCorpus.K && hits.Select(value => value.Coordinate.RecordId).Distinct().Count() == hits.Length &&
            hits.All(value => value.Coordinate.Namespace == "knowledge.shared" && value.Coordinate.Revision == 1 &&
                double.IsFinite(value.Score) && cell.RecordIds.Contains(value.Coordinate.RecordId)), "ranking-scope");
    private static GrowthObservation Failure(CorpusCell cell, QuestionGold question, string status, GrowthFailureEvidence evidence) =>
        new(cell.Id, question.Id, question.Family, status, evidence, cell.RecordIds.Length,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
    internal static GrowthFailureEvidence DescribeFailure(Exception error, string stage) => Evidence(error, stage);

    private static GrowthFailureEvidence Evidence(Exception error, string stage)
    {
        var invariant = error as GrowthInvariantException;
        var sqlite = error as SqliteException;
        var native = error as System.ComponentModel.Win32Exception;
        return new(stage, invariant?.Invariant ?? "unexpected-exception",
            invariant?.Expected, invariant?.Observed, invariant?.ObservedIsLowerBound ?? false,
            error.GetType().Name, error.HResult, sqlite?.SqliteErrorCode, sqlite?.SqliteExtendedErrorCode,
            native?.NativeErrorCode);
    }
    private static GrowthFailureEvidence Synthetic(string stage, string invariant) =>
        new(stage, invariant, null, null, false, "NotExecutedOrCancelled", 0, null, null, null);
    private static void CheckCount(string invariant, long expected, long observed, bool lowerBound = false)
    {
        if (expected != observed || lowerBound)
            throw new GrowthInvariantException(invariant, expected, observed, lowerBound);
    }
    private static void Require(bool condition, string invariant)
    {
        if (!condition) throw new GrowthInvariantException(invariant);
    }
    private sealed class StageTracker { internal string Value { get; set; } = "query-start"; }
    private sealed class FixtureClock : TimeProvider { public override DateTimeOffset GetUtcNow() => FixedTime; }
}
