using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Geometry.Shadow;
using Pegasus.Cortex.Persistence.Sqlite.V2;

internal static class Program
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
    private static readonly CortexKnowledgeV2 Record = new("knowledge.shared", "shadow-alpha", 1,
        CortexKnowledgeStateV2.Active, CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Verified,
        "Quasar optical guide", "Quasar synthetic-body-sentinel photon aperture guidance.", ["optics"],
        new(CortexProvenanceSourceV2.Agent, "shadow_fixture", "synthetic-only", null, At),
        null, At, At, null);
    private static readonly CortexReadRequestV2 Read = new("knowledge.shared", CortexRecordTypeV2.Knowledge, "shadow-alpha", 1);
    private static readonly CortexSearchRequestV2 Search = new(["knowledge.shared"], "quasar query-sentinel", null, null, 4);
    private static readonly List<string> Passed = [];
    private static void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); }
    private static long Count(CortexGeometryShadow shadow, string metric) => shadow.GetStatus().Counters[metric];
    private static CortexShadowProfile Profile(IEnumerable<CortexKnowledgeV2>? records = null) => new(
        Guid.Parse("01a12d3b-79fa-7582-aa10-461ab3506979"), Guid.NewGuid(), Guid.NewGuid(),
        (records ?? [Record]).Select(CortexShadowIdentity.From).ToArray());
    private static async Task Until(Func<bool> predicate, string name, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.ElapsedMilliseconds < milliseconds) await Task.Delay(10);
        Check(predicate(), name);
    }
    private static async Task UnitAsync(string root)
    {
        var fake = new FakeStore(Record);
        var disabledRoot = Path.Combine(root, "disabled-must-not-exist");
        await using (var shadow = new CortexGeometryShadow(Profile(), disabledRoot))
        {
            var wrapper = new CortexObservedV2Store(fake, shadow);
            var result = await wrapper.ReadAsync(Read, default);
            Check(ReferenceEquals(result, fake.LastRead), "disabled reference parity");
            Check(!Directory.Exists(disabledRoot) && Count(shadow, "ProviderOperations") == 0, "disabled no work");
        }
        Passed.Add("unit/default-OFF-no-IO-reference-parity");
        var profile = Profile();
        var lane = Path.Combine(root, "unit-flow");
        await using (var shadow = new CortexGeometryShadow(profile, lane,
                         new(CollectionEnabled: true, SampleEvery: 1, TickMilliseconds: 10, JournalSlots: 4)))
        {
            var wrapper = new CortexObservedV2Store(fake, shadow);
            await wrapper.ReadAsync(Read, default);
            await Until(() => shadow.GetStatus().CurrentPoints == 1, "read point registration");
            var result = await wrapper.SearchAsync(Search, default);
            Check(ReferenceEquals(result, fake.LastSearch), "enabled same result object");
            await Until(() => Count(shadow, "Processed") >= 2, "query worker");
            var observation = Directory.EnumerateFiles(lane, "n???.json").Select(File.ReadAllText)
                .Select(text => JsonSerializer.Deserialize<CortexShadowObservation>(text)!)
                .First(x => x.Operation == "search");
            Check(observation.OrdinaryEligible == 1 && observation.Shadow.Count == 1 &&
                  observation.Shadow[0].Coordinate.RecordId == "shadow-alpha", "shadow comparison");
            File.WriteAllText(Path.Combine(lane, "pending.json"), "task-owned-feedback-collision");
            Check(shadow.TrySubmitFeedback(new(observation.ObservationId, profile.Generation,
                Guid.NewGuid().ToString(), [CortexShadowIdentity.From(Record).Coordinate])), "feedback queued");
            await Until(() => Count(shadow, "WorkerFailures") >= 1, "feedback failure observed");
            Check(Count(shadow, "LabeledSamples") == 0, "failed feedback not labeled");
            Check(File.ReadAllText(Path.Combine(lane, "pending.json")) == "task-owned-feedback-collision", "feedback scratch preserved");
            File.Delete(Path.Combine(lane, "pending.json")); // Exact task-created fixture, after preserved collision proof.
            Check(shadow.TrySubmitFeedback(new(observation.ObservationId, profile.Generation,
                Guid.NewGuid().ToString(), [CortexShadowIdentity.From(Record).Coordinate])), "feedback retry queued");
            await Until(() => Count(shadow, "LabeledSamples") == 1, "explicit feedback retry processed");
            fake.Value = Record with { Revision = 2 };
            await wrapper.ReadAsync(Read with { Revision = 2 }, default);
            await Until(() => shadow.GetStatus().CurrentPoints == 0, "unreviewed revision invalidates point");
            fake.Value = Record;
            await wrapper.ReadAsync(Read, default);
            await Until(() => Count(shadow, "StaleRevisions") >= 1, "stale revision rejected");
            fake.Value = Record with { Revision = 3, State = CortexKnowledgeStateV2.Archived };
            await wrapper.ReadAsync(Read with { Revision = 3 }, default);
            await Until(() => Count(shadow, "Tombstones") >= 2, "archive observed");
            Check(shadow.GetStatus().CurrentPoints == 0 && !shadow.GetStatus().RankingPublished, "no promotion");
            Check(Directory.EnumerateFiles(lane, "n???.json").Count() <= 4 &&
                  shadow.GetStatus().JournalBytes <= 4 * 32768, "journal cap");
            foreach (var file in Directory.EnumerateFiles(lane, "*.json"))
            {
                var text = File.ReadAllText(file);
                Check(!text.Contains("synthetic-body-sentinel") && !text.Contains("query-sentinel") &&
                      !text.Contains("Quasar optical guide"), "no raw body/query/title retention");
            }
        }
        Passed.Add("unit/eligible-read-search-feedback-revision-tombstone-retention-content-boundary");
        fake.Value = Record;
        await using (var shadow = new CortexGeometryShadow(Profile(), Path.Combine(root, "queue"),
                         new(CollectionEnabled: true, QueueCapacity: 1, TickMilliseconds: 1000)))
        {
            var wrapper = new CortexObservedV2Store(fake, shadow);
            for (var i = 0; i < 12; i++) await wrapper.ReadAsync(Read, default);
            Check(Count(shadow, "QueueDropped") >= 10 && Count(shadow, "PeakQueuedBytes") <= 1024 * 1024,
                "bounded queue saturation");
        }
        Passed.Add("unit/backpressure-drops-research-only-cancel-cleanup");
        var droppedRoot = Path.Combine(root, "dropped-revision");
        await using (var shadow = new CortexGeometryShadow(Profile(), droppedRoot,
                         new(CollectionEnabled: true, QueueCapacity: 1, SampleEvery: 1, TickMilliseconds: 100)))
        {
            var wrapper = new CortexObservedV2Store(fake, shadow);
            await wrapper.ReadAsync(Read, default);
            await Until(() => shadow.GetStatus().CurrentPoints == 1, "drop initial point");
            await wrapper.SearchAsync(Search, default); // Occupy the only queue slot.
            fake.Value = Record with { Revision = 2 };
            await wrapper.ReadAsync(Read with { Revision = 2 }, default); // Its queued body work must drop.
            Check(Count(shadow, "QueueDropped") > 0, "revision work dropped");
            await Until(() => shadow.GetStatus().CurrentPoints == 0, "dropped invalidation still applied");
            Check(shadow.GetStatus().ObservedRevisionEpoch >= 2, "observed revision epoch");
        }
        fake.Value = Record;
        Passed.Add("unit/dropped-invalidation-cannot-preserve-stale-vector");
        var seed = Record with { RecordId = "shadow-root" };
        var other = Record with { Namespace = "project.shadow" };
        var otherSeed = seed with { Namespace = other.Namespace };
        var crossProfile = Profile([seed, otherSeed, Record, other]);
        var crossRoot = Path.Combine(root, "cross-namespace");
        await using (var shadow = new CortexGeometryShadow(crossProfile, crossRoot,
                         new(CollectionEnabled: true, SampleEvery: 1, TickMilliseconds: 10)))
        {
            var wrapper = new CortexObservedV2Store(fake, shadow);
            foreach (var record in new[] { seed, otherSeed, Record, other })
            {
                fake.Value = record;
                await wrapper.ReadAsync(Read with { Namespace = record.Namespace, RecordId = record.RecordId }, default);
            }
            await Until(() => shadow.GetStatus().CurrentPoints == 4, "four cross-space points");
            fake.SearchValues = [seed, otherSeed];
            await wrapper.SearchAsync(Search with { Namespaces = ["knowledge.shared", "project.shadow"] }, default);
            await Until(() => Count(shadow, "Processed") >= 5, "cross-space evaluation");
            var observation = Directory.EnumerateFiles(crossRoot, "n???.json").Select(File.ReadAllText)
                .Select(text => JsonSerializer.Deserialize<CortexShadowObservation>(text)!).Single(x => x.Operation == "search");
            Check(observation.Reached.Where(c => c.RecordId == "shadow-alpha").Select(c => c.Namespace).Distinct().Count() == 2,
                "namespace collisions do not collapse neighbor candidates");
        }
        fake.SearchValues = null; fake.Value = Record;
        Passed.Add("unit/full-coordinate-cross-namespace-neighbor-identity");
        var pendingRoot = Path.Combine(root, "pending-fault");
        await using (var shadow = new CortexGeometryShadow(Profile(), pendingRoot,
                         new(CollectionEnabled: true, TickMilliseconds: 20)))
        {
            File.WriteAllText(Path.Combine(pendingRoot, "pending.json"), "task-owned-collision-sentinel");
            var wrapper = new CortexObservedV2Store(fake, shadow);
            var result = await wrapper.ReadAsync(Read, default);
            await Until(() => Count(shadow, "WorkerFailures") >= 1, "storage fault visible");
            Check(ReferenceEquals(result, fake.LastRead), "storage fault ordinary result parity");
            Check(File.ReadAllText(Path.Combine(pendingRoot, "pending.json")) == "task-owned-collision-sentinel",
                "preexisting pending preserved");
        }
        Passed.Add("unit/real-filesystem-collision-failure-isolation");
        var foreignRoot = Path.Combine(root, "foreign-retention");
        await using (var shadow = new CortexGeometryShadow(Profile(), foreignRoot,
                         new(CollectionEnabled: true, TickMilliseconds: 10, RetentionSeconds: 1)))
        {
            var foreign = Path.Combine(foreignRoot, "nABC.json");
            File.WriteAllText(foreign, "task-owned-foreign-sentinel");
            File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-3));
            await Until(() => Count(shadow, "WorkerFailures") > 0, "foreign retention fault visible");
            Check(File.Exists(foreign), "foreign file not deleted");
        }
        Passed.Add("unit/retention-preserves-unknown-entry");
        var expiry = Path.Combine(root, "idle-expiry");
        await using (var shadow = new CortexGeometryShadow(Profile(), expiry,
                         new(CollectionEnabled: true, TickMilliseconds: 10, RetentionSeconds: 1)))
        {
            var wrapper = new CortexObservedV2Store(fake, shadow);
            await wrapper.ReadAsync(Read, default);
            await Until(() => shadow.GetStatus().CurrentPoints == 1, "expiry initial point");
            await Until(() => shadow.GetStatus().CurrentPoints == 0 && shadow.GetStatus().JournalBytes == 0,
                "idle expiry", 4000);
        }
        Passed.Add("unit/idle-time-retention");
        var bind = Profile(); var bindRoot = Path.Combine(root, "binding");
        await using (var shadow = new CortexGeometryShadow(bind, bindRoot, new(CollectionEnabled: true))) { }
        var before = File.ReadAllText(Path.Combine(bindRoot, "binding.json"));
        try
        {
            await using var mismatch = new CortexGeometryShadow(bind with { Generation = Guid.NewGuid() },
                bindRoot, new(CollectionEnabled: true));
            throw new InvalidOperationException("generation mismatch accepted");
        }
        catch (IOException) { }
        try
        {
            await using var changedOptions = new CortexGeometryShadow(bind, bindRoot,
                new(CollectionEnabled: true, SampleEvery: 2));
            throw new InvalidOperationException("same generation changed options accepted");
        }
        catch (IOException) { }
        Check(before == File.ReadAllText(Path.Combine(bindRoot, "binding.json")), "binding preserved");
        try
        {
            await using var invalid = new CortexGeometryShadow(Profile() with
                { EligibleRecords = [CortexShadowIdentity.From(Record) with { Namespace = "knowledge.user" }] },
                Path.Combine(root, "personal-invalid"), new(CollectionEnabled: true));
            throw new InvalidOperationException("personal scope accepted");
        }
        catch (ArgumentException) { }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await using (var shadow = new CortexGeometryShadow(Profile(), Path.Combine(root, "cancel"),
                         new(CollectionEnabled: true)))
        {
            var wrapper = new CortexObservedV2Store(fake, shadow);
            try { await wrapper.ReadAsync(Read, canceled.Token); throw new InvalidOperationException("cancellation lost"); }
            catch (OperationCanceledException) { }
            Check(Count(shadow, "ForegroundFailures") == 1, "foreground cancel visible");
        }
        var recoveryProfile = Profile(); var recoveryRoot = Path.Combine(root, "summary-recovery");
        var recoveryOptions = new CortexShadowOptions(CollectionEnabled: true, TickMilliseconds: 10);
        await using (var first = new CortexGeometryShadow(recoveryProfile, recoveryRoot, recoveryOptions))
        {
            var wrapper = new CortexObservedV2Store(fake, first);
            await wrapper.ReadAsync(Read, default);
            await Until(() => first.GetStatus().CurrentPoints == 1, "summary initial point");
            fake.Value = Record with { Revision = 2 };
            await wrapper.ReadAsync(Read with { Revision = 2 }, default);
            await Until(() => first.GetStatus().CurrentPoints == 0, "summary invalidation");
        }
        var saved = JsonSerializer.Deserialize<CortexShadowStudySummary>(
            File.ReadAllText(Path.Combine(recoveryRoot, "status.json")))!;
        Check(saved.Final && saved.Status.Lifecycle == "stopped" && saved.Status.Counters["ProviderOperations"] == 2,
            "final counters persisted");
        fake.Value = Record;
        await using (var resumed = new CortexGeometryShadow(recoveryProfile, recoveryRoot, recoveryOptions))
        {
            Check(Count(resumed, "ProviderOperations") == 2 && resumed.GetStatus().CurrentPoints == 0,
                "counters restored without body or vector hydration");
            var resumedStatus = resumed.GetStatus();
            Check(resumedStatus.EligiblePointOccurrencesCumulative == 1 &&
                resumedStatus.DistinctAdmittedIdentitiesThisRun == 0,
                "restored occurrences are distinct from this-run identity admission");
            CheckBuckets(resumedStatus, "ForegroundObserverSection", 0);
            CheckBuckets(resumedStatus, "Worker", 0);
            CheckBuckets(resumedStatus, "QueueDelay", 0);
            var wrapper = new CortexObservedV2Store(fake, resumed);
            var processedBefore = Count(resumed, "Processed");
            await wrapper.ReadAsync(Read, default);
            await Until(() => Count(resumed, "Processed") > processedBefore, "resumed historical read");
            Check(resumed.GetStatus().CurrentPoints == 0 && Count(resumed, "StaleRevisions") >= 1,
                "dropped/stored revision frontier survives restart");
        }
        Passed.Add("unit/periodic-final-summary-and-revision-frontier-recovery");
        Passed.Add("unit/generation-personal-scope-and-caller-cancellation");
    }

    private static async Task<object> RuntimeAsync(string root)
    {
        var db = Path.Combine(root, "real", "cortex-v2.db");
        var low = new SqliteCortexV2Store(new(db, MaximumContentBytes: 4096, MaximumDatabasePages: 4096));
        await using var store = new SqliteCortexV2OperationalStore(low, "shadow-diagnostic-1");
        await low.InitializeAsync();
        foreach (var record in new[] { Record, Record with { RecordId = "shadow-beta", Title = "Photon aperture",
                     Content = "Photon aperture has a bounded optical alignment." } })
        {
            var command = new CortexKnowledgeCommandV2(record.Namespace, CortexKnowledgeActionV2.Create,
                record.RecordId, null, "seed-" + record.RecordId, record.Kind, record.Tier, record.Title,
                record.Content, record.Tags, new(record.Provenance.Source, record.Provenance.SourceId, null), null)
                { EffectiveProvenance = record.Provenance };
            await store.ExecuteKnowledgeAsync(command, default);
        }
        var records = new List<CortexKnowledgeV2>();
        foreach (var id in new[] { "shadow-alpha", "shadow-beta" })
            records.Add((CortexKnowledgeV2)(await store.ReadAsync(Read with { RecordId = id }, default)).Record!);
        var before = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(db)));
        long expectedTrace, expectedConnections;
        using (var trace = CortexSqliteTraceMeasurement.Begin())
        {
            await store.SearchAsync(Search, default);
            expectedTrace = trace.StatementTraceEvents; expectedConnections = trace.Connections;
            Check(trace.AttachmentFailures == 0 && expectedTrace > 0 && expectedConnections > 0,
                "actual SQLite trace coverage");
        }
        var profile = Profile(records); var lane = Path.Combine(root, "real-shadow");
        var off = new List<long>(); var on = new List<long>(); CortexShadowStatus final;
        await using (var shadow = new CortexGeometryShadow(profile, lane,
                         new(CollectionEnabled: true, SampleEvery: 2, TickMilliseconds: 10, JournalSlots: 8)))
        {
            var wrapper = new CortexObservedV2Store(store, shadow);
            foreach (var record in records)
                await wrapper.ReadAsync(Read with { RecordId = record.RecordId }, default);
            await Until(() => shadow.GetStatus().CurrentPoints == 2, "real points");
            for (var i = 0; i < 20; i++)
            {
                var watch = Stopwatch.StartNew(); var ordinary = await store.SearchAsync(Search, default);
                off.Add(watch.ElapsedTicks);
                var sqlBefore = Count(shadow, "SqliteStatementTraceEvents");
                watch.Restart(); var observed = await wrapper.SearchAsync(Search, default); on.Add(watch.ElapsedTicks);
                Check(JsonSerializer.Serialize(ordinary) == JsonSerializer.Serialize(observed), "real ordinary payload parity");
                Check(Count(shadow, "SqliteStatementTraceEvents") - sqlBefore == expectedTrace,
                    "same actual foreground trace count");
                await Task.Delay(15);
            }
            await Until(() => Count(shadow, "Processed") == Count(shadow, "Queued"), "real queue drained");
            final = shadow.GetStatus();
            Check(final.Counters["ProviderOperations"] == 22 && final.Counters["TraceAttachmentFailures"] == 0 &&
                  final.Counters["Sampled"] == 10 && final.Counters["LabeledSamples"] == 0 &&
                  final.Counters["WorkerFailures"] == 0 && !final.RankingPublished, "real metrics");
            Check(final.JournalBytes <= 8 * 32768 && final.CurrentPoints == 2, "real caps");
        }
        var after = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(db)));
        Check(before == after, "background changed no ordinary DB bytes");
        var catalog = await low.GetCatalogCountersAsync(default);
        Check(catalog.HeadRows == 2 && catalog.RevisionRows == 2, "only two fresh fixture records");
        Passed.Add("real-executable/local-SQLite-2-records-22-observed-operations-payload-parity-actual-trace");
        return new { db, databaseBeforeSha256 = before, databaseAfterSha256 = after, heads = catalog.HeadRows,
            expectedTraceEventsPerSearch = expectedTrace, expectedConnectionsPerSearch = expectedConnections,
            status = final, stopwatchFrequency = Stopwatch.Frequency, offTicks = off, onTicks = on,
            costLimit = "Paired small-fixture elapsed observations, not a causal per-request CPU/memory benchmark." };
    }

    private static async Task MetricsModesAsync(string root)
    {
        foreach (var mode in new[] { "off", "collect-only", "collect-and-score" })
        {
            var collect = mode != "off";
            var evaluate = mode == "collect-and-score";
            var fake = new FakeStore(Record);
            var lane = Path.Combine(root, "metrics-" + mode);
            var profile = Profile();
            await using var shadow = new CortexGeometryShadow(profile, lane,
                new(CollectionEnabled: collect, EvaluationEnabled: mode != "collect-only", SampleEvery: 1,
                    TickMilliseconds: 10, JournalSlots: 8));
            var wrapper = new CortexObservedV2Store(fake, shadow);
            var read = await wrapper.ReadAsync(Read, default);
            Check(ReferenceEquals(read, fake.LastRead), mode + " read reference parity");
            if (collect) await Until(() => shadow.GetStatus().CurrentPoints == 1, mode + " registered point");
            var search = await wrapper.SearchAsync(Search, default);
            Check(ReferenceEquals(search, fake.LastSearch), mode + " search reference parity");
            await wrapper.ReadAsync(Read, default); // One identity, multiple observed occurrences.
            if (collect) await Until(() => Count(shadow, "Processed") == 3 &&
                shadow.GetStatus().LatenciesThisRun!["Worker"].Samples == 3, mode + " measured drain");
            var status = shadow.GetStatus();
            Check(status.EffectiveMode == mode && status.CollectionEnabled == collect &&
                status.EvaluationEnabled == evaluate && !status.RankingPublished, mode + " effective mode");
            Check(status.DistinctAdmittedIdentitiesThisRun == (collect ? 1 : 0) &&
                status.EligiblePointOccurrencesCumulative == (collect ? 3 : 0) &&
                status.DistinctPointIdentitiesInWindow == (collect ? 1 : 0) &&
                status.DistinctAdmittedIdentitiesThisRun.GetValueOrDefault() <= status.ReviewedRecordRevisions,
                mode + " occurrences versus bounded distinct identities");
            CheckBuckets(status, "ForegroundObserverSection", collect ? 3 : 0);
            CheckBuckets(status, "Worker", collect ? 3 : 0);
            CheckBuckets(status, "QueueDelay", collect ? 3 : 0);
            Check(status.LatenciesThisRun!["ForegroundObserverSection"].TotalStopwatchTicks == status.Counters["ObserverTicks"] &&
                status.LatenciesThisRun["Worker"].TotalStopwatchTicks == status.Counters["WorkerTicks"],
                mode + " measured-section cumulative ticks in a fresh run");
            if (!collect)
            {
                Check(!Directory.Exists(lane) && status.Counters["ProviderOperations"] == 0,
                    "off metrics created instrumentation or research IO");
                continue;
            }
            var observed = Directory.EnumerateFiles(lane, "n???.json").Select(File.ReadAllText)
                .Select(text => JsonSerializer.Deserialize<CortexShadowObservation>(text)!)
                .Single(value => value.Operation == "search");
            Check(evaluate ? (observed.Status is "evaluated" or "evaluation-partial") && observed.Shadow.Count == 1 :
                observed.Status == "collected" && observed.Shadow.Count == 0 && observed.Reached.Count == 0 && observed.MemoryLookups == 0,
                mode + " independent scoring gate");
            foreach (var file in Directory.EnumerateFiles(lane, "*.json"))
            {
                var text = File.ReadAllText(file);
                Check(!text.Contains("query-sentinel", StringComparison.Ordinal) &&
                    !text.Contains("synthetic-body-sentinel", StringComparison.Ordinal), mode + " metrics retained raw content");
            }
        }
        Passed.Add("unit/research-off-collection-only-scoring-reference-parity-bounded-latencies-distinct-identities");
    }

    private static void CheckBuckets(CortexShadowStatus status, string name, long expectedSamples)
    {
        var bucket = status.LatenciesThisRun![name];
        Check(bucket.UpperBoundsMicroseconds.SequenceEqual(new long[] { 50, 100, 500, 1000, 5000, 10000, 50000, 250000 }) &&
            bucket.Counts.Count == bucket.UpperBoundsMicroseconds.Count + 1 && bucket.Counts.All(value => value >= 0) &&
            bucket.Counts.Sum() == expectedSamples && bucket.Samples == expectedSamples &&
            bucket.TotalStopwatchTicks >= bucket.MaximumStopwatchTicks && bucket.MaximumStopwatchTicks >= 0 &&
            (expectedSamples != 0 || bucket.TotalStopwatchTicks == 0 && bucket.MaximumStopwatchTicks == 0),
            name + " fixed bucket totals/bounds");
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || Directory.Exists(args[0]) || File.Exists(args[1])) return 2;
        var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(args[0]);
            await UnitAsync(args[0]);
            await MetricsModesAsync(args[0]);
            var runtime = await RuntimeAsync(args[0]);
            process.Refresh();
            var report = new { schema = "cortex.shadow.diagnostic/1", passed = Passed, failures = Array.Empty<string>(),
                runtime, elapsedTicks = watch.ElapsedTicks, stopwatchFrequency = Stopwatch.Frequency,
                processAggregateCpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                processAggregatePeakWorkingSetBytes = process.PeakWorkingSet64,
                limits = "Synthetic focused tests and one fresh local fixture only; no real-user collection, installed integration, cloud or accuracy claim." };
            Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Shadow diagnostic passed; groups={Passed.Count}; live-user-data=0.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private sealed class FakeStore(CortexKnowledgeV2 value) : ICortexV2Store
    {
        public CortexKnowledgeV2 Value = value;
        public IReadOnlyList<CortexKnowledgeV2>? SearchValues;
        public CortexReadResultV2? LastRead; public CortexSearchResultV2? LastSearch;
        public ValueTask<CortexReadResultV2> ReadAsync(CortexReadRequestV2 r, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); LastRead = new([Value.Namespace], CortexRecordTypeV2.Knowledge,
            Value.RecordId, Value.Revision, Value); return ValueTask.FromResult(LastRead); }
        public ValueTask<CortexSearchResultV2> SearchAsync(CortexSearchRequestV2 r, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var records = SearchValues ?? [Value];
            LastSearch = new(r.Namespaces, records.Select(record => new CortexSearchHitV2(record.Namespace,
                CortexRecordTypeV2.Knowledge, record.RecordId, record.Revision, record.Title, record.Content,
                record.Kind, record.Tier, new(record.Provenance.Source, record.Provenance.Actor,
                    record.Provenance.SourceId, record.Provenance.CapturedAtUtc), 1)).ToArray(), null, false);
            return ValueTask.FromResult(LastSearch);
        }
        public ValueTask<CortexStatusV2> StatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(CortexNamespacesRequestV2 r, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<CortexStartResultV2> StartAsync(CortexStartRequestV2 r, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<CortexTaskResultV2> ExecuteTaskAsync(CortexTaskCommandV2 r, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(CortexKnowledgeCommandV2 r, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<CortexFinishResultV2> FinishAsync(CortexFinishRequestV2 r, CancellationToken ct) => throw new NotSupportedException();
    }
}
