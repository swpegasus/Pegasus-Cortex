using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

var options = BenchmarkOptions.Parse(args);
var report = await CortexV2Benchmark.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
var jsonPath = Path.Combine(options.OutputRoot, "benchmark.json");
var markdownPath = Path.Combine(options.OutputRoot, "benchmark.md");
await System.IO.File.WriteAllBytesAsync(
    jsonPath,
    JsonSerializer.SerializeToUtf8Bytes(report, BenchmarkJson.Options)).ConfigureAwait(false);
await System.IO.File.WriteAllTextAsync(
    markdownPath,
    BenchmarkMarkdown.Render(report),
    new UTF8Encoding(false)).ConfigureAwait(false);
Console.WriteLine(JsonSerializer.Serialize(new
{
    report.GeneratedAtUtc,
    report.Corpus,
    report.QualityAnchors,
    fileStorage = report.FileProvider.Storage,
    sqliteStorage = report.SqliteProvider.Storage,
    report.SqliteScale,
    jsonPath,
    markdownPath
}, BenchmarkJson.Options));

internal sealed record BenchmarkOptions(
    string PackRoot,
    string OutputRoot,
    int Iterations,
    int Writes,
    int ScaleRevisions)
{
    public static BenchmarkOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Benchmark arguments must be --name value pairs.");
            values.Add(arguments[index], arguments[index + 1]);
        }
        if (!values.TryGetValue("--pack", out var pack) ||
            !values.TryGetValue("--output", out var output))
        {
            throw new ArgumentException("Required arguments: --pack and --output.");
        }
        var packRoot = RequireExistingAbsoluteDirectory(pack, nameof(pack));
        var outputRoot = Path.GetFullPath(output);
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(outputRoot) || System.IO.File.Exists(outputRoot))
            throw new ArgumentException("The benchmark output must be a new absolute path.", nameof(output));
        var normalized = outputRoot.Replace('/', '\\');
        if (!normalized.Contains("\\artifacts\\validation\\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Benchmark state must be confined under artifacts/validation.", nameof(output));
        var iterations = BoundedInt(values, "--iterations", 100, 10, 10_000);
        var writes = BoundedInt(values, "--writes", 50, 10, 1_000);
        var scale = BoundedInt(values, "--scale-revisions", 10_000, 1_000, 100_000);
        Directory.CreateDirectory(outputRoot);
        return new(packRoot, outputRoot, iterations, writes, scale);
    }

    private static string RequireExistingAbsoluteDirectory(string value, string name)
    {
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("The benchmark pack path must be absolute.", name);
        var path = Path.GetFullPath(value);
        if (!Directory.Exists(path) || (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The benchmark pack path is missing or linked.");
        return path;
    }

    private static int BoundedInt(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(name, out var text))
            return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
        return value;
    }
}

internal static class CortexV2Benchmark
{
    private const string Namespace = "knowledge.shared";
    private const string ReadRecordId = "tenet.core-workspace-artifacts-and-path-safety";
    private const string WriteRecordId = "preference.benchmark-metadata";
    private const string MigrationRecordId = "tenet.disciplines-data-persistence-and-migrations";
    private const string PathSafetyRecordId = "tenet.core-workspace-artifacts-and-path-safety";
    private const string SerializationRecordId = "tenet.disciplines-interfaces-contracts-and-serialization";
    private const string PortabilityRecordId = "tenet.disciplines-portability-time-and-encoding";
    private static readonly CortexProvenanceInputV2 Imported = new(
        CortexProvenanceSourceV2.Imported,
        "generalized-programming-tenets",
        "Portable generalized programming tenet.");

    public static async ValueTask<BenchmarkReport> RunAsync(
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var manifest = await CortexFileBehaviorPackManifestV2.ReadAsync(options.PackRoot, cancellationToken)
            .ConfigureAwait(false);
        var records = await manifest.LoadRecordsAsync(options.PackRoot, cancellationToken)
            .ConfigureAwait(false);
        if (records.Count < 4 ||
            records.Select(value => value.Descriptor.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Count)
        {
            throw new InvalidDataException(
                "The benchmark requires a nontrivial behavior pack with unique record identifiers.");
        }
        var recordIds = records.Select(value => value.Descriptor.RecordId).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     MigrationRecordId,
                     PathSafetyRecordId,
                     SerializationRecordId,
                     PortabilityRecordId
                 })
        {
            if (!recordIds.Contains(required))
                throw new InvalidDataException($"The benchmark pack is missing relevance anchor '{required}'.");
        }
        var corpusBytes = records.Sum(value => Encoding.UTF8.GetByteCount(value.Content));

        var stateRoot = Path.Combine(options.OutputRoot, "state");
        var fileRoot = Path.Combine(stateRoot, "file", "project");
        var sqliteRoot = Path.Combine(stateRoot, "sqlite");
        var scaleRoot = Path.Combine(stateRoot, "sqlite-scale");
        Directory.CreateDirectory(fileRoot);
        Directory.CreateDirectory(sqliteRoot);
        Directory.CreateDirectory(scaleRoot);

        var fileResult = await BenchmarkFileAsync(
            options, records, fileRoot, cancellationToken).ConfigureAwait(false);
        var sqliteResult = await BenchmarkSqliteAsync(
            options, records, sqliteRoot, cancellationToken).ConfigureAwait(false);
        var qualityAnchors = RequireQualityAnchors(fileResult, sqliteResult);
        var scaleResult = await BenchmarkSqliteScaleAsync(
            options.ScaleRevisions, scaleRoot, cancellationToken).ConfigureAwait(false);

        return new BenchmarkReport(
            DateTimeOffset.UtcNow,
            new BenchmarkEnvironment(
                Environment.OSVersion.ToString(),
                Environment.Version.ToString(),
                Environment.ProcessorCount,
                Environment.Is64BitProcess,
                "10.0.400 (latestPatch permitted)",
                "OS filesystem cache not flushed; first reopen is a fresh provider object after seed/dispose."),
            new CorpusResult(manifest.PackId, records.Count, corpusBytes, Namespace),
            qualityAnchors,
            options.Iterations,
            options.Writes,
            fileResult,
            sqliteResult,
            scaleResult);
    }

    private static QualityAnchorResult RequireQualityAnchors(
        params ProviderBenchmarkResult[] providers)
    {
        var results = providers.Select(provider =>
        {
            var migration = Operation(provider, "start:migration").FirstResultIds;
            var pathSafety = Operation(provider, "search:path-safety").FirstResultIds;
            var serialization = Operation(provider, "search:serialization").FirstResultIds;
            var migrationRank = Array.IndexOf(migration.ToArray(), MigrationRecordId);
            var interfaceRank = Array.IndexOf(serialization.ToArray(), SerializationRecordId);
            var portabilityRank = Array.IndexOf(serialization.ToArray(), PortabilityRecordId);
            if (migrationRank < 0 ||
                pathSafety.FirstOrDefault() != PathSafetyRecordId ||
                interfaceRank != 0 || portabilityRank < 0 || interfaceRank >= portabilityRank)
            {
                throw new InvalidDataException(
                    $"The {provider.Provider} benchmark failed a deterministic relevance anchor: " +
                    $"migration=[{string.Join(',', migration)}] " +
                    $"pathSafety=[{string.Join(',', pathSafety)}] " +
                    $"serialization=[{string.Join(',', serialization)}].");
            }
            return new ProviderQualityAnchorResult(
                provider.Provider,
                migrationRank + 1,
                pathSafety[0],
                serialization[0],
                interfaceRank + 1,
                portabilityRank + 1);
        }).ToArray();
        return new QualityAnchorResult(
            Passed: true,
            MigrationRecordId,
            PathSafetyRecordId,
            SerializationRecordId,
            PortabilityRecordId,
            results);

        static OperationMeasurement Operation(ProviderBenchmarkResult provider, string name) =>
            provider.Operations.Single(value => string.Equals(value.Operation, name, StringComparison.Ordinal));
    }

    private static async ValueTask<ProviderBenchmarkResult> BenchmarkFileAsync(
        BenchmarkOptions options,
        IReadOnlyList<CortexFileBehaviorRecordV2> records,
        string root,
        CancellationToken cancellationToken)
    {
        var attachment = new CortexFileProjectManifestV2(
            CortexFileProjectManifestV2.CurrentSchema,
            "benchmark-file",
            Namespace,
            [Namespace],
            4096,
            32,
            64 * 1024);
        await attachment.WriteAsync(root, cancellationToken).ConfigureAwait(false);
        var seeded = await FileCortexV2Store.OpenAsync(root, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var import = await seeded.ImportBehaviorPackAsync(
            options.PackRoot, Namespace, "benchmark-file", cancellationToken).ConfigureAwait(false);
        if (import.Imported != records.Count)
            throw new InvalidDataException("The File benchmark corpus did not seed exactly once.");

        var firstOpen = await TimeOneAsync(async () =>
        {
            var store = await FileCortexV2Store.OpenAsync(root, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return await store.StatusAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var warmStore = await FileCortexV2Store.OpenAsync(root, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var app = new CortexV2Application(warmStore, "benchmark-file");
        var operations = await MeasureOperationsAsync(
            app, options.Iterations, options.Writes, cancellationToken).ConfigureAwait(false);
        var status = await warmStore.StatusAsync(cancellationToken).ConfigureAwait(false);
        var storage = FileStorage(root, status);
        return new ProviderBenchmarkResult("file-v2", firstOpen, operations, storage);
    }

    private static async ValueTask<ProviderBenchmarkResult> BenchmarkSqliteAsync(
        BenchmarkOptions options,
        IReadOnlyList<CortexFileBehaviorRecordV2> records,
        string root,
        CancellationToken cancellationToken)
    {
        var database = Path.Combine(root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        await SeedSqliteAsync(database, records, cancellationToken).ConfigureAwait(false);

        var firstOpen = await TimeOneAsync(async () =>
        {
            await using var store = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(database));
            return await store.QuickCheckAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var low = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(database));
        await using var operational = new SqliteCortexV2OperationalStore(low, "benchmark");
        var app = new CortexV2Application(operational, "benchmark-sqlite");
        var operations = await MeasureOperationsAsync(
            app, options.Iterations, options.Writes, cancellationToken).ConfigureAwait(false);
        var metrics = await low.GetStorageMetricsAsync(cancellationToken).ConfigureAwait(false);
        var status = await operational.StatusAsync(cancellationToken).ConfigureAwait(false);
        var storage = SqliteStorage(database, metrics, status);
        return new ProviderBenchmarkResult("sqlite-v2", firstOpen, operations, storage);
    }

    private static async ValueTask SeedSqliteAsync(
        string database,
        IReadOnlyList<CortexFileBehaviorRecordV2> records,
        CancellationToken cancellationToken)
    {
        var low = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(database));
        await using var operational = new SqliteCortexV2OperationalStore(low, "benchmark-seed");
        var app = new CortexV2Application(operational, "benchmark-seeder");
        foreach (var value in records)
        {
            var descriptor = value.Descriptor;
            var result = await app.ExecuteKnowledgeAsync(new CortexKnowledgeCommandV2(
                Namespace,
                CortexKnowledgeActionV2.Create,
                descriptor.RecordId,
                ExpectedRevision: null,
                IdempotencyKey: $"seed.{descriptor.RecordId}",
                descriptor.Kind,
                descriptor.Tier,
                descriptor.Title,
                value.Content,
                descriptor.Tags,
                Imported,
                ReplacementRecordId: null), cancellationToken).ConfigureAwait(false);
            if (result.Record is not { Revision: 1 })
                throw new InvalidDataException("The SQLite benchmark corpus did not seed exactly once.");
        }
    }

    private static async ValueTask<IReadOnlyList<OperationMeasurement>> MeasureOperationsAsync(
        CortexV2Application app,
        int iterations,
        int writes,
        CancellationToken cancellationToken)
    {
        var startRequest = new CortexStartRequestV2(
            [Namespace], "implement a safe portable persistence migration", null);
        var pathSearch = new CortexSearchRequestV2(
            [Namespace], "path safety links traversal source preservation",
            [CortexKnowledgeKindV2.Tenet], null, 5);
        var serializationSearch = new CortexSearchRequestV2(
            [Namespace], "serialization Unicode controls portable encoding",
            [CortexKnowledgeKindV2.Tenet], null, 5);
        var readRequest = new CortexReadRequestV2(
            Namespace, CortexRecordTypeV2.Knowledge, ReadRecordId, Revision: null);

        for (var index = 0; index < 5; index++)
        {
            _ = await app.StartAsync(startRequest, cancellationToken).ConfigureAwait(false);
            _ = await app.SearchAsync(pathSearch, cancellationToken).ConfigureAwait(false);
            _ = await app.SearchAsync(serializationSearch, cancellationToken).ConfigureAwait(false);
            _ = await app.ReadAsync(readRequest, cancellationToken).ConfigureAwait(false);
        }

        var results = new List<OperationMeasurement>
        {
            await MeasureAsync("start:migration", iterations,
                _ => app.StartAsync(startRequest, cancellationToken)).ConfigureAwait(false),
            await MeasureAsync("search:path-safety", iterations,
                _ => app.SearchAsync(pathSearch, cancellationToken)).ConfigureAwait(false),
            await MeasureAsync("search:serialization", iterations,
                _ => app.SearchAsync(serializationSearch, cancellationToken)).ConfigureAwait(false),
            await MeasureAsync("read:typed-knowledge", iterations,
                _ => app.ReadAsync(readRequest, cancellationToken)).ConfigureAwait(false)
        };

        var initial = await app.ExecuteKnowledgeAsync(new CortexKnowledgeCommandV2(
            Namespace,
            CortexKnowledgeActionV2.Create,
            WriteRecordId,
            ExpectedRevision: null,
            IdempotencyKey: "benchmark-write-create",
            CortexKnowledgeKindV2.Preference,
            CortexKnowledgeTierV2.Explicit,
            "Benchmark metadata preference",
            "Retain this exact body while metadata revisions advance.",
            ["benchmark"],
            new CortexProvenanceInputV2(CortexProvenanceSourceV2.Agent, null, null),
            ReplacementRecordId: null), cancellationToken).ConfigureAwait(false);
        var revision = initial.Record?.Revision ?? throw new InvalidDataException("Benchmark write seed failed.");
        for (var index = 0; index < 3; index++)
        {
            var warm = await ReviseBenchmarkAsync(app, revision, $"warm-{index}", cancellationToken)
                .ConfigureAwait(false);
            revision = warm.Record?.Revision ?? throw new InvalidDataException("Benchmark write warmup failed.");
        }
        results.Add(await MeasureAsync("write:metadata-revision", writes, async index =>
        {
            var result = await ReviseBenchmarkAsync(
                app, revision, $"measure-{index}", cancellationToken).ConfigureAwait(false);
            revision = result.Record?.Revision ?? throw new InvalidDataException("Benchmark write failed.");
            return result;
        }).ConfigureAwait(false));
        return results;
    }

    private static ValueTask<CortexKnowledgeResultV2> ReviseBenchmarkAsync(
        CortexV2Application app,
        long expectedRevision,
        string id,
        CancellationToken cancellationToken) => app.ExecuteKnowledgeAsync(new CortexKnowledgeCommandV2(
            Namespace,
            CortexKnowledgeActionV2.Revise,
            WriteRecordId,
            expectedRevision,
            $"benchmark-write-{id}",
            Kind: null,
            Tier: null,
            Title: $"Benchmark metadata preference {id}",
            Content: null,
            Tags: null,
            new CortexProvenanceInputV2(CortexProvenanceSourceV2.Agent, null, null),
            ReplacementRecordId: null), cancellationToken);

    private static async ValueTask<SqliteScaleResult> BenchmarkSqliteScaleAsync(
        int metadataRevisions,
        string root,
        CancellationToken cancellationToken)
    {
        var database = Path.Combine(root, SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
        var store = new SqliteCortexV2Store(new SqliteCortexV2StoreOptions(database));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var created = await store.AppendAsync(new SqliteCortexV2AppendRequest(
            Namespace,
            "knowledge",
            "scale.metadata",
            ExpectedRevision: 0,
            IdempotencyKey: "scale-create",
            Actor: "benchmark-scale",
            EventType: "create",
            Title: "Scale metadata record",
            Tags: ["scale"],
            Summary: "One retained body with metadata-only successors.",
            ContentUtf8: Encoding.UTF8.GetBytes("Scale content retained exactly once."),
            Searchable: true), cancellationToken).ConfigureAwait(false);
        if (created.Revision != 1)
            throw new InvalidDataException("SQLite scale seed failed.");

        var batchDurations = new List<double>();
        var revision = 1L;
        var total = Stopwatch.StartNew();
        while (revision <= metadataRevisions)
        {
            var count = (int)Math.Min(250, metadataRevisions - revision + 1);
            var requests = new SqliteCortexV2AppendRequest[count];
            for (var index = 0; index < count; index++)
            {
                var next = revision + index + 1;
                requests[index] = new SqliteCortexV2AppendRequest(
                    Namespace,
                    "knowledge",
                    "scale.metadata",
                    ExpectedRevision: next - 1,
                    IdempotencyKey: $"scale-{next:D8}",
                    Actor: "benchmark-scale",
                    EventType: "metadata-update",
                    Title: $"Scale metadata record {next:D8}",
                    Tags: ["scale"],
                    Summary: "Metadata-only successor; exact body is inherited.",
                    ContentUtf8: null,
                    Searchable: true);
            }
            var timer = Stopwatch.StartNew();
            var result = await store.AppendBatchAsync(requests, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            if (!result.Committed || result.Items.Count != count)
                throw new InvalidDataException("A SQLite scale metadata batch did not commit atomically.");
            batchDurations.Add(timer.Elapsed.TotalMilliseconds / count);
            revision += count;
        }
        total.Stop();

        var quickTimer = Stopwatch.StartNew();
        var quick = await store.QuickCheckAsync(cancellationToken).ConfigureAwait(false);
        quickTimer.Stop();
        var doctorTimer = Stopwatch.StartNew();
        var doctor = await store.FullDoctorAsync(cancellationToken).ConfigureAwait(false);
        doctorTimer.Stop();
        var metrics = await store.GetStorageMetricsAsync(cancellationToken).ConfigureAwait(false);
        var openBytes = DatabaseFiles(database);
        if (!quick.Healthy || quick.CorpusRowsRead != 0 || !doctor.Healthy ||
            metrics.RevisionRows != metadataRevisions + 1L || metrics.BlobRows != 1 || metrics.HeadRows != 1)
        {
            throw new InvalidDataException("SQLite scale metrics did not prove metadata-only blob reuse.");
        }
        await store.DisposeAsync().ConfigureAwait(false);
        var disposedBytes = DatabaseFiles(database);
        return new SqliteScaleResult(
            metadataRevisions,
            metrics.RevisionRows,
            metrics.BlobRows,
            metrics.HeadRows,
            metrics.SearchChunkRows,
            metrics.FtsRows,
            total.Elapsed.TotalMilliseconds,
            metadataRevisions / total.Elapsed.TotalSeconds,
            Percentile(batchDurations, 0.50),
            Percentile(batchDurations, 0.95),
            quickTimer.Elapsed.TotalMilliseconds,
            quick.CorpusRowsRead,
            doctorTimer.Elapsed.TotalMilliseconds,
            doctor.RevisionRowsScanned,
            openBytes,
            disposedBytes,
            Ratio(metrics.RevisionRows, metrics.BlobRows),
            Ratio(metrics.SearchChunkRows, metrics.HeadRows),
            Ratio(metrics.FtsRows, metrics.HeadRows),
            metrics.OriginalBlobBytes,
            metrics.CompressedBlobBytes);
    }

    private static async ValueTask<OperationMeasurement> MeasureAsync<T>(
        string name,
        int iterations,
        Func<int, ValueTask<T>> action)
    {
        var times = new List<double>(iterations);
        var bytes = new List<double>(iterations);
        IReadOnlyList<string> firstIds = [];
        for (var index = 0; index < iterations; index++)
        {
            var timer = Stopwatch.StartNew();
            var result = await action(index).ConfigureAwait(false);
            timer.Stop();
            times.Add(timer.Elapsed.TotalMilliseconds);
            bytes.Add(JsonSerializer.SerializeToUtf8Bytes(result, CortexV2Json.Options).Length);
            if (index == 0)
                firstIds = ResultIds(result);
        }
        return new OperationMeasurement(
            name,
            iterations,
            Percentile(times, 0.50),
            Percentile(times, 0.95),
            times.Average(),
            Percentile(bytes, 0.50),
            Percentile(bytes, 0.95),
            firstIds);
    }

    private static async ValueTask<SingleMeasurement> TimeOneAsync<T>(Func<ValueTask<T>> action)
    {
        var timer = Stopwatch.StartNew();
        var result = await action().ConfigureAwait(false);
        timer.Stop();
        return new SingleMeasurement(
            timer.Elapsed.TotalMilliseconds,
            JsonSerializer.SerializeToUtf8Bytes(result, CortexV2Json.Options).Length);
    }

    private static IReadOnlyList<string> ResultIds<T>(T result) => result switch
    {
        CortexStartResultV2 value => value.Items.Select(item => item.RecordId).Take(5).ToArray(),
        CortexSearchResultV2 value => value.Items.Select(item => item.RecordId).Take(5).ToArray(),
        CortexReadResultV2 value => [value.RecordId],
        CortexKnowledgeResultV2 value when value.Record is not null => [value.Record.RecordId],
        _ => []
    };

    private static ProviderStorageResult FileStorage(string root, CortexStatusV2 status)
    {
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        var data = Path.Combine(root, ".cortex", "v2");
        return new ProviderStorageResult(
            files.Sum(path => new FileInfo(path).Length),
            files.Length,
            status.HeadCount,
            status.RevisionCount,
            Directory.GetFiles(Path.Combine(data, "objects"), "*.json").LongLength,
            Directory.GetDirectories(Path.Combine(data, "generations"), "g-*").LongLength,
            Directory.GetFiles(Path.Combine(data, "generations"), "index-*.json", SearchOption.AllDirectories).LongLength,
            BlobRows: null,
            SearchChunkRows: null,
            FtsRows: null,
            RevisionPerBlob: null,
            SearchChunksPerHead: null,
            FtsRowsPerHead: null,
            OriginalBlobBytes: null,
            CompressedBlobBytes: null);
    }

    private static ProviderStorageResult SqliteStorage(
        string database,
        SqliteCortexV2StorageMetrics metrics,
        CortexStatusV2 status) => new(
            DatabaseFiles(database),
            Directory.GetFiles(Path.GetDirectoryName(database)!).Length,
            status.HeadCount,
            status.RevisionCount,
            ObjectFiles: null,
            Generations: null,
            IndexChunks: null,
            metrics.BlobRows,
            metrics.SearchChunkRows,
            metrics.FtsRows,
            Ratio(metrics.RevisionRows, metrics.BlobRows),
            Ratio(metrics.SearchChunkRows, metrics.HeadRows),
            Ratio(metrics.FtsRows, metrics.HeadRows),
            metrics.OriginalBlobBytes,
            metrics.CompressedBlobBytes);

    private static long DatabaseFiles(string database) => new[]
        {
            database,
            database + "-wal",
            database + "-shm"
        }
        .Where(System.IO.File.Exists)
        .Sum(path => new FileInfo(path).Length);

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var ordered = values.OrderBy(value => value).ToArray();
        var rank = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return ordered[lower];
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower);
    }

    private static double Ratio(long numerator, long denominator) => denominator == 0
        ? 0
        : (double)numerator / denominator;
}

internal sealed record BenchmarkReport(
    DateTimeOffset GeneratedAtUtc,
    BenchmarkEnvironment Environment,
    CorpusResult Corpus,
    QualityAnchorResult QualityAnchors,
    int WarmIterations,
    int WriteIterations,
    ProviderBenchmarkResult FileProvider,
    ProviderBenchmarkResult SqliteProvider,
    SqliteScaleResult SqliteScale);

internal sealed record BenchmarkEnvironment(
    string OperatingSystem,
    string Runtime,
    int ProcessorCount,
    bool Is64BitProcess,
    string SourceDeclaredSdk,
    string ColdMeasurementBoundary);

internal sealed record CorpusResult(string PackId, int Records, long Utf8Bytes, string Namespace);

internal sealed record QualityAnchorResult(
    bool Passed,
    string MigrationExpectedRecord,
    string PathSafetyExpectedFirst,
    string SerializationExpectedAhead,
    string SerializationComparedTo,
    IReadOnlyList<ProviderQualityAnchorResult> Providers);

internal sealed record ProviderQualityAnchorResult(
    string Provider,
    int MigrationRank,
    string PathSafetyFirst,
    string SerializationFirst,
    int SerializationRank,
    int PortabilityRank);

internal sealed record ProviderBenchmarkResult(
    string Provider,
    SingleMeasurement FirstReopen,
    IReadOnlyList<OperationMeasurement> Operations,
    ProviderStorageResult Storage);

internal sealed record SingleMeasurement(double Milliseconds, long ResponseUtf8Bytes);

internal sealed record OperationMeasurement(
    string Operation,
    int Iterations,
    double P50Milliseconds,
    double P95Milliseconds,
    double MeanMilliseconds,
    double P50ResponseUtf8Bytes,
    double P95ResponseUtf8Bytes,
    IReadOnlyList<string> FirstResultIds);

internal sealed record ProviderStorageResult(
    long TotalBytes,
    long FileCount,
    long HeadRows,
    long RevisionRows,
    long? ObjectFiles,
    long? Generations,
    long? IndexChunks,
    long? BlobRows,
    long? SearchChunkRows,
    long? FtsRows,
    double? RevisionPerBlob,
    double? SearchChunksPerHead,
    double? FtsRowsPerHead,
    long? OriginalBlobBytes,
    long? CompressedBlobBytes);

internal sealed record SqliteScaleResult(
    int MetadataRevisionsRequested,
    long RevisionRows,
    long BlobRows,
    long HeadRows,
    long SearchChunkRows,
    long FtsRows,
    double TotalAppendMilliseconds,
    double RevisionsPerSecond,
    double P50BatchNormalizedMillisecondsPerRevision,
    double P95BatchNormalizedMillisecondsPerRevision,
    double QuickCheckMilliseconds,
    long QuickCheckCorpusRowsRead,
    double FullDoctorMilliseconds,
    long DoctorRevisionRowsScanned,
    long OpenDatabaseWalShmBytes,
    long DisposedDatabaseWalShmBytes,
    double RevisionPerBlob,
    double SearchChunksPerHead,
    double FtsRowsPerHead,
    long OriginalBlobBytes,
    long CompressedBlobBytes);

internal static class BenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = new(CortexV2Json.Options)
    {
        WriteIndented = true
    };
}

internal static class BenchmarkMarkdown
{
    public static string Render(BenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Pegasus Cortex V2 provider benchmark");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAtUtc:O}`");
        builder.AppendLine();
        builder.AppendLine($"Corpus: {report.Corpus.Records} records / {report.Corpus.Utf8Bytes:N0} UTF-8 bytes in `{report.Corpus.Namespace}`.");
        builder.AppendLine();
        builder.AppendLine($"Boundary: {report.Environment.ColdMeasurementBoundary}");
        builder.AppendLine();
        builder.AppendLine("## Relevance quality gates");
        builder.AppendLine();
        builder.AppendLine("| Provider | migration anchor rank | path-safety first | serialization first | interface rank | portability rank |");
        builder.AppendLine("|---|---|---|---|---:|---:|");
        foreach (var value in report.QualityAnchors.Providers)
        {
            builder.AppendLine(
                $"| {value.Provider} | {value.MigrationRank} | {value.PathSafetyFirst} | {value.SerializationFirst} | {value.SerializationRank} | {value.PortabilityRank} |");
        }
        builder.AppendLine();
        builder.AppendLine("The benchmark fails before writing a report if either provider misses the migration anchor from the bounded startup result, misses a first-ranked path-safety anchor, or ranks portability ahead of interfaces/serialization.");
        builder.AppendLine();
        builder.AppendLine("## First reopen");
        builder.AppendLine();
        builder.AppendLine("| Provider | Operation | ms | response bytes |");
        builder.AppendLine("|---|---:|---:|---:|");
        Row(report.FileProvider, "open + status");
        Row(report.SqliteProvider, "initialize + quick check");
        builder.AppendLine();
        builder.AppendLine("## Warm operations");
        builder.AppendLine();
        builder.AppendLine("| Provider | Operation | n | p50 ms | p95 ms | p50 bytes | First result IDs |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---|");
        Operations(report.FileProvider);
        Operations(report.SqliteProvider);
        builder.AppendLine();
        builder.AppendLine("## Retained storage after A/B workload");
        builder.AppendLine();
        builder.AppendLine("| Provider | bytes | files | heads | revisions | blobs/objects | generations | FTS rows |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        Storage(report.FileProvider);
        Storage(report.SqliteProvider);
        builder.AppendLine();
        builder.AppendLine("## SQLite 10k metadata-revision scale");
        builder.AppendLine();
        builder.AppendLine($"- Requested metadata-only revisions: {report.SqliteScale.MetadataRevisionsRequested:N0}");
        builder.AppendLine($"- Retained rows: {report.SqliteScale.RevisionRows:N0} revisions, {report.SqliteScale.BlobRows:N0} blob, {report.SqliteScale.HeadRows:N0} head");
        builder.AppendLine($"- Append: {report.SqliteScale.TotalAppendMilliseconds:N2} ms total, {report.SqliteScale.RevisionsPerSecond:N0} revisions/s");
        builder.AppendLine($"- Batch-normalized latency: p50 {report.SqliteScale.P50BatchNormalizedMillisecondsPerRevision:N4} ms/revision, p95 {report.SqliteScale.P95BatchNormalizedMillisecondsPerRevision:N4} ms/revision");
        builder.AppendLine($"- Quick check: {report.SqliteScale.QuickCheckMilliseconds:N3} ms, corpus rows read {report.SqliteScale.QuickCheckCorpusRowsRead}");
        builder.AppendLine($"- Full doctor: {report.SqliteScale.FullDoctorMilliseconds:N2} ms, revision rows scanned {report.SqliteScale.DoctorRevisionRowsScanned:N0}");
        builder.AppendLine($"- Amplification: {report.SqliteScale.RevisionPerBlob:N0} revisions/blob, {report.SqliteScale.SearchChunksPerHead:N2} search chunks/head, {report.SqliteScale.FtsRowsPerHead:N2} FTS rows/head");
        builder.AppendLine($"- DB+WAL+SHM bytes: {report.SqliteScale.OpenDatabaseWalShmBytes:N0} open, {report.SqliteScale.DisposedDatabaseWalShmBytes:N0} after dispose");
        builder.AppendLine();
        builder.AppendLine("No installed/active Cortex database was opened. All provider state is disposable and colocated beneath this report's `state/` directory.");
        return builder.ToString();

        void Row(ProviderBenchmarkResult provider, string operation) => builder.AppendLine(
            $"| {provider.Provider} | {operation} | {provider.FirstReopen.Milliseconds:N3} | {provider.FirstReopen.ResponseUtf8Bytes:N0} |");
        void Operations(ProviderBenchmarkResult provider)
        {
            foreach (var value in provider.Operations)
            {
                builder.AppendLine(
                    $"| {provider.Provider} | {value.Operation} | {value.Iterations} | {value.P50Milliseconds:N3} | {value.P95Milliseconds:N3} | {value.P50ResponseUtf8Bytes:N0} | {string.Join(", ", value.FirstResultIds)} |");
            }
        }
        void Storage(ProviderBenchmarkResult provider)
        {
            var value = provider.Storage;
            builder.AppendLine(
                $"| {provider.Provider} | {value.TotalBytes:N0} | {value.FileCount:N0} | {value.HeadRows:N0} | {value.RevisionRows:N0} | {(value.BlobRows ?? value.ObjectFiles ?? 0):N0} | {(value.Generations ?? 0):N0} | {(value.FtsRows ?? 0):N0} |");
        }
    }
}
