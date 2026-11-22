using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Pegasus.Cortex.GeometryGrowthDiagnostic;

internal sealed record GrowthRunReport(string Schema, string ManifestSha256, string RunId,
    bool GeometryDiagnosticsEnabled, string EngineeringStatus, int DistinctSemanticQuestions,
    int DistinctFamilies, string[] PureChecks, string[] EvidenceLimits, GrowthMeasurement Measurement);

internal static class GrowthProgram
{
    private const int MaximumReportBytes = 2 * 1024 * 1024;

    internal static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Refused: this bounded fixture driver requires Windows.");
            return 2;
        }
        var stage = "argument-admission";
        try
        {
            var options = Arguments(args);
            stage = "pure-checks";
            GrowthEvaluation.PureChecks();
            if (options.ContainsKey("--prepare-manifest"))
            {
                stage = "manifest-preparation";
                return await PrepareAsync(options).ConfigureAwait(false);
            }
            if (options.ContainsKey("--compare"))
            {
                stage = "report-comparison";
                return await CompareAsync(options).ConfigureAwait(false);
            }
            stage = "measurement-admission-or-report";
            return await MeasureAsync(options).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Driver stopped; created roots are retained; do not replay into them.");
            Console.Error.WriteLine(JsonSerializer.Serialize(
                GrowthEvaluation.DescribeFailure(error, stage), GrowthCorpus.Json));
            return 2;
        }
    }

    private static Dictionary<string, string> Arguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(["--prepare-manifest", "--measure", "--compare", "--geometry-diagnostics"],
            StringComparer.Ordinal);
        var values = new HashSet<string>(["--report-root", "--fixture-root", "--manifest",
            "--expected-manifest-sha256", "--run", "--on-report", "--off-report"], StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (result.ContainsKey(key)) throw new ArgumentException("Duplicate option.");
            if (flags.Contains(key)) result.Add(key, "true");
            else if (values.Contains(key) && index + 1 < args.Length) result.Add(key, args[++index]);
            else throw new ArgumentException("Unknown or incomplete option.");
        }
        if (new[] { "--prepare-manifest", "--measure", "--compare" }.Count(result.ContainsKey) != 1)
            throw new ArgumentException("Choose exactly one fixed action.");
        string[] allowed = result.ContainsKey("--prepare-manifest")
            ? ["--prepare-manifest", "--report-root"]
            : result.ContainsKey("--compare")
                ? ["--compare", "--report-root", "--on-report", "--off-report"]
                : ["--measure", "--report-root", "--fixture-root", "--manifest", "--expected-manifest-sha256", "--run", "--geometry-diagnostics"];
        if (result.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Action option mismatch.");
        return result;
    }

    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing required option.");

    private static async Task<int> PrepareAsync(Dictionary<string, string> options)
    {
        var root = Required(options, "--report-root");
        ValidateNewRoot(root);
        var manifest = GrowthCorpus.Create();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, GrowthCorpus.Json);
        if (bytes.Length > GrowthCorpus.MaximumManifestBytes)
            throw new GrowthInvariantException("manifest-byte-budget", GrowthCorpus.MaximumManifestBytes, bytes.Length);
        var hash = GrowthCorpus.Hash(bytes);
        CreateFreshDirectory(root);
        await WriteNewAsync(Path.Combine(root, "manifest.v1.json"), bytes, GrowthCorpus.MaximumManifestBytes)
            .ConfigureAwait(false);
        var review = new
        {
            schema = "pegasus.cortex.geometry-growth-preparation/1",
            status = "prepared-not-measured",
            manifestSha256 = hash,
            manifestBytes = bytes.Length,
            pureChecksPassed = true,
            sqliteOpened = false,
            benchmarkMeasured = false,
            distinctQuestions = manifest.Questions.Length,
            distinctFamilies = manifest.Questions.Select(value => value.Family).Distinct().Count(),
            variableTargetCounts = manifest.Questions.GroupBy(value => value.TargetIds.Length)
                .Select(group => new { targetsPerQuestion = group.Key, questions = group.Count() }).ToArray(),
            explicitSupportRelations = manifest.Records.Sum(record => record.UsefulFor.Count),
            crossQuestionSupportRelations = manifest.Records.Sum(record =>
                record.UsefulFor.Keys.Count(id => !record.FactKey.StartsWith(id + "/", StringComparison.Ordinal))),
            manifest.Budget,
            mainCells = manifest.Cells.Where(value => value.Kind == "main")
                .Select(value => new { value.Id, value.Variant, value.BridgeOwnerSlots, value.AddedNoise, rows = value.RecordIds.Length,
                    actualBeneficiaryCount = value.AddedSupportBeneficiaryQuestionIds?.Length,
                    value.AddedSupportBeneficiaryQuestionIds, value.BeneficiariesByFamily }).ToArray(),
            nearNoiseProfile = manifest.NearNoise,
            nearNoiseControl = manifest.Cells.Where(value => value.Kind == "near-noise")
                .Select(value => new { value.Id, rows = value.RecordIds.Length, value.Purpose }).ToArray(),
            probes = manifest.Cells.Where(value => value.Kind == "probe")
                .Select(value => new { value.Id, rows = value.RecordIds.Length, value.Purpose, value.ExpectedDirectHits }).ToArray(),
            questions = manifest.Questions.Select(value => new
            {
                value.Id, value.Family, value.Query, value.InformationNeed,
                value.TargetIds, value.TargetRationales, usefulSupport = value.UsefulSupport
            }).ToArray(),
            variants = manifest.Variants,
            runs = manifest.Runs,
            reviewRequiredBeforeMeasurement = true
        };
        await WriteNewAsync(Path.Combine(root, "preparation.v1.json"),
            JsonSerializer.SerializeToUtf8Bytes(review, GrowthCorpus.Json), MaximumReportBytes).ConfigureAwait(false);
        var text = new StringBuilder();
        text.AppendLine("Shared geometry growth preparation v1 - NOT MEASURED");
        text.AppendLine("Manifest SHA256: " + hash);
        text.AppendLine("48 distinct questions, 12 families; 256 fixed shared base records.");
        text.AppendLine("Owner slots0/24/48 are distinct from actual useful beneficiaries; see per-cell sets.");
        text.AppendLine("Near-noise:256 rows,48 unique indexed payloads repeated5/6 times; artificial density stress.");
        text.AppendLine("Main: 18 databases, 18864 rows, 864 paired query measurements planned.");
        text.AppendLine("OFF: one largest-cell replica, 2352 rows, 48 comparisons planned.");
        text.AppendLine("Probes: " + manifest.Budget.ProbeDatabases + " databases, " + manifest.Budget.ProbeRows + " rows planned.");
        text.AppendLine("Near-noise: " + manifest.Budget.NearNoiseDatabases + " database, " +
            manifest.Budget.NearNoiseRows + " rows, " + manifest.Budget.NearNoiseQueryMeasurements + " questions planned.");
        text.AppendLine("Total planned: " + manifest.Budget.TotalDatabases + " databases / " + manifest.Budget.TotalRows + " rows.");
        text.AppendLine("Pure arithmetic/projection/manifest checks passed. No SQLite connection or retrieval ran.");
        text.AppendLine("Question-conditioned target/support sets and full payloads are in manifest.v1.json.");
        text.AppendLine("Root/Native/Audit review and exact manifest pin precede measurement.");
        foreach (var question in manifest.Questions)
            text.AppendLine(question.Id + " [" + question.Family + "] targets=" + question.TargetIds.Length +
                " query=" + question.Query + " need=" + question.InformationNeed);
        await WriteNewAsync(Path.Combine(root, "review-summary.txt"), Encoding.UTF8.GetBytes(text.ToString()),
            MaximumReportBytes).ConfigureAwait(false);
        Console.WriteLine("Prepared 48-question shared manifest; pure checks passed; SQLite not opened. SHA256=" + hash);
        return 0;
    }

    private static async Task<int> MeasureAsync(Dictionary<string, string> options)
    {
        var manifestPath = Required(options, "--manifest");
        ValidateExistingFile(manifestPath);
        var expected = Required(options, "--expected-manifest-sha256");
        GrowthCorpus.Require(expected.Length == 64 && expected.All(Uri.IsHexDigit), "Invalid expected manifest pin.");
        var bytes = await ReadBoundedAsync(manifestPath, GrowthCorpus.MaximumManifestBytes).ConfigureAwait(false);
        var hash = GrowthCorpus.Hash(bytes);
        GrowthCorpus.Require(hash.Equals(expected, StringComparison.OrdinalIgnoreCase), "Manifest pin mismatch.");
        var manifest = JsonSerializer.Deserialize<GrowthManifest>(bytes, GrowthCorpus.Json) ??
            throw new InvalidDataException("Missing manifest.");
        GrowthCorpus.Validate(manifest);
        var runId = Required(options, "--run");
        var run = manifest.Runs.SingleOrDefault(value => value.Id == runId) ??
            throw new ArgumentException("Run is not in the frozen plan.");
        var geometryEnabled = options.ContainsKey("--geometry-diagnostics");
        GrowthCorpus.Require(geometryEnabled == run.GeometryEnabled,
            "Diagnostics default off; this frozen run requires an explicit matching flag.");
        var fixture = Required(options, "--fixture-root");
        var report = Required(options, "--report-root");
        ValidateNewRoot(fixture);
        ValidateNewRoot(report);
        RequireSeparate(fixture, report);
        GrowthCorpus.Require(!manifestPath.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !manifestPath.StartsWith(report + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Input manifest must remain outside new output roots.");
        CreateFreshDirectory(fixture);
        CreateFreshDirectory(report);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(run.CooperativeSeconds));
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += handler;
        GrowthMeasurement measurement;
        try
        {
            measurement = await GrowthEvaluation.MeasureAsync(manifest, run, fixture, deadline.Token).ConfigureAwait(false);
        }
        finally { Console.CancelKeyPress -= handler; }
        var passed = !measurement.Cancelled && measurement.CellFailures.Length == 0 &&
            measurement.Observations.Length == measurement.PlannedObservations &&
            measurement.Observations.All(value => value.Status == "engineering-passed");
        var result = new GrowthRunReport("pegasus.cortex.geometry-growth-report/1", hash, run.Id,
            geometryEnabled, passed ? "passed" : measurement.Cancelled ? "cancelled" : "failed",
            run.QuestionIds.Length, run.QuestionIds.Select(id => manifest.Questions.Single(value => value.Id == id).Family).Distinct().Count(),
            ["role arithmetic", "fixed target denominator", "target loss visibility", "uniform math", "lexical-only projection", "bounded failure envelopes", "mixed target-loss accounting"],
            [
                "Pure assertions are in-memory checks, not retrieval evidence.",
                "Provider results are actual isolated SQLite only when this measurement action is executed.",
                "Variants/cells do not add independent semantic questions.",
                "Analytical references are not executed providers.",
                "Support gains and noise deltas do not erase primary-target losses.",
                "No installed, old-store, model-answer, personal-profile or production-task proof."
            ], measurement);
        await WriteNewAsync(Path.Combine(report, "report.v1.json"),
            JsonSerializer.SerializeToUtf8Bytes(result, GrowthCorpus.Json), MaximumReportBytes).ConfigureAwait(false);
        var summary = new StringBuilder();
        summary.AppendLine("Shared geometry growth run: " + run.Id + "; engineering=" + result.EngineeringStatus);
        summary.AppendLine("Manifest SHA256: " + hash);
        summary.AppendLine("Geometry: " + (geometryEnabled ? "diagnostic metadata only" : "off"));
        summary.AppendLine("Distinct questions: " + result.DistinctSemanticQuestions + "; planned observations: " + measurement.PlannedObservations);
        summary.AppendLine("All selected context remains exact lexical-selected revisions.");
        foreach (var aggregate in measurement.Aggregates)
            summary.AppendLine(aggregate.CellId + ": completed=" + aggregate.CompletedQuestions +
                " target recall lexical/geometry=" + Number(aggregate.MeanLexicalTargetRecall) + "/" +
                Number(aggregate.MeanGeometryTargetRecall) + " Pareto gains/regressions=" + aggregate.ParetoTargetGainQuestions + "/" + aggregate.ParetoTargetRegressionQuestions +
                " mixed=" + aggregate.MixedTargetMetricQuestions +
                " target-hit-loss=" + aggregate.TargetHitLossQuestions +
                " first-rank-loss=" + aggregate.FirstTargetRankLossQuestions +
                " any-target-metric-loss=" + aggregate.AnyTargetMetricLossQuestions +
                " support/noise delta=" + aggregate.SupportDelta + "/" + aggregate.NoiseDelta);
        foreach (var failure in measurement.CellFailures) summary.AppendLine("CELL FAILURE " + FailureText(failure));
        foreach (var failure in measurement.Observations.Where(value => value.Status != "engineering-passed"))
            summary.AppendLine(failure.CellId + "/" + failure.QuestionId + " " + failure.Status + " " + (failure.Failure is null ? "none" : FailureText(failure.Failure)));
        await WriteNewAsync(Path.Combine(report, "summary.txt"), Encoding.UTF8.GetBytes(summary.ToString()), MaximumReportBytes)
            .ConfigureAwait(false);
        Console.WriteLine("Growth engineering " + result.EngineeringStatus + "; observations=" +
            measurement.Observations.Count(value => value.Status == "engineering-passed") + "/" + measurement.PlannedObservations);
        return passed ? 0 : 1;
    }

    private static async Task<int> CompareAsync(Dictionary<string, string> options)
    {
        var onPath = Required(options, "--on-report");
        var offPath = Required(options, "--off-report");
        ValidateExistingFile(onPath); ValidateExistingFile(offPath);
        var on = JsonSerializer.Deserialize<GrowthRunReport>(await ReadBoundedAsync(onPath, MaximumReportBytes).ConfigureAwait(false),
            GrowthCorpus.Json) ?? throw new InvalidDataException("Missing ON report.");
        var off = JsonSerializer.Deserialize<GrowthRunReport>(await ReadBoundedAsync(offPath, MaximumReportBytes).ConfigureAwait(false),
            GrowthCorpus.Json) ?? throw new InvalidDataException("Missing OFF report.");
        GrowthCorpus.Require(on.Schema == "pegasus.cortex.geometry-growth-report/1" && off.Schema == on.Schema &&
            on.ManifestSha256 == off.ManifestSha256 && on.EngineeringStatus == "passed" && off.EngineeringStatus == "passed" &&
            on.GeometryDiagnosticsEnabled && !off.GeometryDiagnosticsEnabled, "Incompatible paired reports.");
        var pairs = off.Measurement.Observations.Select(left =>
        {
            var right = on.Measurement.Observations.Single(value => value.CellId == left.CellId && value.QuestionId == left.QuestionId);
            return new
            {
                left.CellId, left.QuestionId,
                contextEqual = left.LexicalContextSha256 == right.LexicalContextSha256,
                rankingEqual = left.LexicalRankingSha256 == right.LexicalRankingSha256,
                right.ContextIdentical
            };
        }).ToArray();
        GrowthCorpus.Require(pairs.Length == 48 && pairs.All(value => value.contextEqual && value.rankingEqual && value.ContextIdentical == true),
            "Representative actual OFF/ON parity failed.");
        var root = Required(options, "--report-root");
        ValidateNewRoot(root); CreateFreshDirectory(root);
        await WriteNewAsync(Path.Combine(root, "parity.v1.json"),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "pegasus.cortex.geometry-growth-parity/1",
                on.ManifestSha256, pairedQueries = pairs.Length, allEqual = true, pairs,
                scope = "Representative largest-cell actual OFF/ON only; other ON cells check internal before/after parity."
            }, GrowthCorpus.Json), MaximumReportBytes).ConfigureAwait(false);
        Console.WriteLine("Actual representative OFF/ON lexical context and ranking parity:48/48.");
        return 0;
    }

    private static string FailureText(GrowthFailureEvidence value) =>
        value.Stage + "/" + value.Invariant + " expected=" + value.Expected + " observed=" + value.Observed +
        (value.ObservedIsLowerBound ? " (lower bound)" : "") +
        " type=" + value.ExceptionType + " hresult=" + value.HResult +
        " sqlite=" + value.SqliteCode + "/" + value.SqliteExtendedCode + " native=" + value.NativeCode;

    private static string Number(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "null";

    private static void ValidateNewRoot(string path)
    {
        ValidateCanonicalLocal(path, 140);
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("Missing parent.");
        OrdinaryAncestors(parent);
        if (Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("Output root must be absent.");
    }

    private static void ValidateExistingFile(string path)
    {
        ValidateCanonicalLocal(path, 240);
        OrdinaryAncestors(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Input must be an ordinary existing file.");
    }

    private static void ValidateCanonicalLocal(string path, int maximum)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) || path.Length > maximum ||
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != path)
            throw new ArgumentException("Path must be canonical, bounded and local without a trailing separator.");
        var volume = Path.GetPathRoot(path) ?? throw new ArgumentException("Missing volume.");
        if (new DriveInfo(volume).DriveType != DriveType.Fixed) throw new IOException("Fixed local storage required.");
        var parts = path[volume.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (parts.Length == 0 || parts.Any(UnsafeSegment)) throw new ArgumentException("Unsafe component.");
    }

    private static bool UnsafeSegment(string value)
    {
        if (value.Length == 0 || value is "." or ".." || value.EndsWith(' ') || value.EndsWith('.') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) > 255) return true;
        var stem = value.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789\u00b9\u00b2\u00b3".Contains(stem[3]);
    }

    private static void OrdinaryAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (!current.Exists || current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Input/output ancestry must be ordinary and stable.");
    }

    private static void RequireSeparate(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
            left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output roots overlap.");
    }

    internal static void CreateFreshDirectory(string path)
    {
        if (path.Length > 240) throw new IOException("Generated directory exceeds the path budget.");
        OrdinaryAncestors(Path.GetDirectoryName(path)!);
        if (!CreateDirectoryW(path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Exclusive directory creation failed.");
        OrdinaryAncestors(path);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximum) throw new InvalidDataException("Input byte cap exceeded.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        if (buffer.Length > maximum) throw new InvalidDataException("Input grew beyond cap.");
        return buffer.ToArray();
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, int maximum)
    {
        if (path.Length > 240 || bytes.Length > maximum) throw new InvalidDataException("Output byte cap exceeded.");
        OrdinaryAncestors(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16_384, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
}
