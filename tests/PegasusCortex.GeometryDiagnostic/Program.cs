using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pegasus.Cortex.GeometryDiagnostic;

internal static class DiagnosticProgram
{
    private const int MaximumReportBytes = 262_144;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Options(string FixtureRoot, string ReportRoot, bool GeometryEnabled);

    internal static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Refused: this isolated fixture driver requires Windows.");
            return 2;
        }
        try
        {
            var options = Parse(args);
            var cases = GeometryCases.Create();
            GeometryEvaluation.ValidateManifest(cases);
            GeometryEvaluation.CheckPureChecks();
            var labelBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "pegasus.cortex.geometry-gold/1",
                caseSetVersion = GeometryCases.Version,
                selectedNamespaces = new[] { GeometryCases.Shared },
                cutoff = GeometryEvaluation.K,
                cases
            }, Json);
            var corpusSha256 = Convert.ToHexString(SHA256.HashData(labelBytes)).ToLowerInvariant();
            RequireDistinctRoots(options.FixtureRoot, options.ReportRoot);
            // Exclusive directory creation refuses an existing root; retain either root after failure.
            CreateFreshDirectory(options.FixtureRoot);
            CreateFreshDirectory(options.ReportRoot);
            await WriteNewAsync(Path.Combine(options.ReportRoot, "labels.v1.json"), labelBytes).ConfigureAwait(false);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; deadline.Cancel(); };
            Console.CancelKeyPress += cancel;
            EvaluationResult evaluation;
            try
            {
                evaluation = await GeometryEvaluation.RunAsync(cases, options.FixtureRoot,
                    options.GeometryEnabled, deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
            var completed = evaluation.Cases.Count(value => value.Status == "engineering-passed");
            var passed = !evaluation.Cancelled && completed == cases.Length;
            var report = new
            {
                schema = "pegasus.cortex.geometry-diagnostic/1",
                caseSetVersion = GeometryCases.Version,
                corpusSha256,
                geometryDiagnosticsEnabled = options.GeometryEnabled,
                cutoff = GeometryEvaluation.K,
                lexicalPoolLimit = GeometryEvaluation.LexicalPoolLimit,
                engineeringStatus = passed ? "passed" : evaluation.Cancelled ? "cancelled" : "failed",
                completedCases = completed,
                plannedCases = cases.Length,
                pureChecks = new[] { "hand-calculated-metric-arithmetic", "lexical-only-projection", "noise-only-and-mixed-classification" },
                evidence = new
                {
                    pureChecks = "in-memory checks; no retrieval-quality evidence",
                    retrieval = "actual isolated synthetic SQLite repository calls",
                    geometry = options.GeometryEnabled ? "existing public operational SearchAsync" : "not executed",
                    context = "lexical-selected exact revisions only; geometry candidates never hydrated",
                    installed = "not tested",
                    modelOrTaskQuality = "not tested"
                },
                definitions = new
                {
                    precision = "relevant returned at 4 / 4; missing positions count in denominator",
                    recall = "relevant returned at 4 / eligible gold relevant; null for zero gold",
                    reciprocalRank = "first relevant reciprocal rank at 4; zero with no hit or zero gold; macro mean includes zero-gold cases",
                    noise = "irrelevant returned / returned; null when empty",
                    deltaIrrelevantReturned = "geometry irrelevant returned minus lexical irrelevant returned; its negative is a quality direction",
                    outcome = "P, defined recall, RR and negative delta irrelevant count; all nonnegative with improvement=gain, all nonpositive with worsening=regression, conflicting directions=mixed, all zero=no-measured-gain",
                    aggregates = "completed engineering-passed cases only; macro precision, recall and RR; pooled noise",
                    qualityGate = "none; gain, no-measured-gain, regression and mixed are data",
                    rawScores = "lexical and geometry scores have different scales; do not subtract raw scores"
                },
                comparisonTotals = options.GeometryEnabled ? new
                {
                    comparedCases = evaluation.Cases.Count(value => value.GeometryMetrics is not null),
                    addedRelevant = evaluation.Cases.Sum(value => value.AddedRelevant ?? 0),
                    addedIrrelevant = evaluation.Cases.Sum(value => value.AddedIrrelevant ?? 0),
                    displacedRelevant = evaluation.Cases.Sum(value => value.DisplacedRelevant ?? 0),
                    gainCases = evaluation.Cases.Count(value => value.QualityOutcome == "gain"),
                    noMeasuredGainCases = evaluation.Cases.Count(value => value.QualityOutcome == "no-measured-gain"),
                    regressionCases = evaluation.Cases.Count(value => value.QualityOutcome == "regression"),
                    mixedCases = evaluation.Cases.Count(value => value.QualityOutcome == "mixed")
                } : null,
                evaluation
            };
            await WriteNewAsync(Path.Combine(options.ReportRoot, "report.v1.json"),
                JsonSerializer.SerializeToUtf8Bytes(report, Json)).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(options.ReportRoot, "summary.txt"),
                Encoding.UTF8.GetBytes(Summary(options.GeometryEnabled, corpusSha256, evaluation, passed, cases.Length)))
                .ConfigureAwait(false);
            Console.WriteLine("Engineering " + (passed ? "passed" : "incomplete/failed") +
                "; cases " + completed + "/" + cases.Length + "; geometry " +
                (options.GeometryEnabled ? "diagnostic only" : "off") + "; see explicit report root.");
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Driver stopped: " + exception.GetType().Name +
                ". Any created fixture/report roots are retained; do not replay into them.");
            return 2;
        }
    }

    private static Options Parse(string[] args)
    {
        string? fixture = null;
        string? report = null;
        var geometry = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fixture-root" when fixture is null && index + 1 < args.Length:
                    fixture = args[++index];
                    break;
                case "--report-root" when report is null && index + 1 < args.Length:
                    report = args[++index];
                    break;
                case "--geometry-diagnostics" when !geometry:
                    geometry = true;
                    break;
                default:
                    throw new ArgumentException("Unknown, duplicate or incomplete argument.");
            }
        }
        if (fixture is null || report is null)
            throw new ArgumentException("Both explicit new roots are required.");
        ValidateNewRoot(fixture);
        ValidateNewRoot(report);
        return new(fixture, report, geometry);
    }

    private static void ValidateNewRoot(string requested)
    {
        if (!Path.IsPathFullyQualified(requested) || requested.StartsWith(@"\\", StringComparison.Ordinal) ||
            requested.StartsWith("//", StringComparison.Ordinal) || requested.Length > 140 ||
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested)) != requested)
            throw new ArgumentException("Root must be canonical, bounded, local and without a trailing separator.");
        var volume = Path.GetPathRoot(requested) ?? throw new ArgumentException("Missing volume.");
        if (new DriveInfo(volume).DriveType != DriveType.Fixed)
            throw new ArgumentException("Roots require fixed local storage.");
        var relative = requested[volume.Length..];
        if (relative.Length == 0 || relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None)
            .Any(UnsafeSegment))
            throw new ArgumentException("Unsafe root segment.");
        var parent = Path.GetDirectoryName(requested) ?? throw new ArgumentException("Missing parent.");
        RequireOrdinaryExistingAncestors(parent);
        if (!Directory.Exists(parent) || Directory.Exists(requested) || File.Exists(requested) ||
            new FileInfo(requested).LinkTarget is not null)
            throw new IOException("The parent must exist and the root must be absent.");
        if (Path.Combine(requested, "c12", "cortex-v2.db-shm").Length > 240)
            throw new ArgumentException("Generated path exceeds the Windows path budget.");
    }

    private static bool UnsafeSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment.Any(char.IsControl) || Encoding.UTF8.GetByteCount(segment) > 255)
            return true;
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
            stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789\u00b9\u00b2\u00b3".Contains(stem[3]);
    }

    private static void RequireOrdinaryExistingAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (!current.Exists || current.LinkTarget is not null ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Root ancestry must consist of existing ordinary directories.");
        }
    }

    private static void RequireDistinctRoots(string fixture, string report)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (fixture.Equals(report, comparison) ||
            fixture.StartsWith(report + Path.DirectorySeparatorChar, comparison) ||
            report.StartsWith(fixture + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Fixture and report roots must be separate and non-overlapping.");
    }

    internal static void CreateFreshDirectory(string path)
    {
        if (path.Length > 240)
            throw new ArgumentException("Directory exceeds the path budget.");
        RequireOrdinaryExistingAncestors(Path.GetDirectoryName(path)!);
        if (!CreateDirectoryW(path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Exclusive directory creation failed.");
        RequireOrdinaryExistingAncestors(path);
    }

    private static async Task WriteNewAsync(string path, byte[] bytes)
    {
        if (path.Length > 240 || bytes.Length > MaximumReportBytes)
            throw new InvalidDataException("Bounded report limit exceeded.");
        RequireOrdinaryExistingAncestors(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4_096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Summary(bool geometryEnabled, string fingerprint, EvaluationResult evaluation,
        bool passed, int planned)
    {
        var text = new StringBuilder();
        text.AppendLine("Pegasus Cortex isolated geometry diagnostic v1");
        text.AppendLine("Engineering: " + (passed ? "passed" : evaluation.Cancelled ? "cancelled" : "failed/incomplete"));
        text.AppendLine("Diagnostics: " + (geometryEnabled ? "on; metadata only" : "off; geometry not executed"));
        text.AppendLine("Case manifest SHA256: " + fingerprint);
        text.AppendLine("Completed: " + evaluation.Cases.Count(value => value.Status == "engineering-passed") + "/" + planned);
        text.AppendLine("k=4; P denominator=4; zero-gold recall=null; zero-gold RR=0 included in macro MRR.");
        text.AppendLine("Outcome includes negative net irrelevant-result growth; relevance gains with extra noise are mixed.");
        text.AppendLine("Context: lexical-selected exact revisions only; geometry candidates are never hydrated.");
        text.AppendLine("Evidence: pure metric/projection checks plus actual isolated synthetic SQLite calls.");
        text.AppendLine("No installed, MCP, model, consumer-context or task-quality verification.");
        foreach (var value in evaluation.Cases)
        {
            text.Append(value.Id).Append(": ").Append(value.Status);
            if (value.LexicalMetrics is { } lexical)
                text.Append(" lexical P/R/RR=").Append(Metrics(lexical));
            if (value.GeometryMetrics is { } candidate)
                text.Append(" geometry P/R/RR=").Append(Metrics(candidate))
                    .Append(" outcome=").Append(value.QualityOutcome)
                    .Append(" added relevant/irrelevant=").Append(value.AddedRelevant).Append('/').Append(value.AddedIrrelevant)
                    .Append(" displaced relevant=").Append(value.DisplacedRelevant)
                    .Append(" delta irrelevant returned=").Append(value.Delta?.DeltaIrrelevantReturned);
            if (value.Failure is not null) text.Append(" failure=").Append(value.Failure);
            text.AppendLine();
        }
        AppendAggregate(text, "Lexical", evaluation.LexicalAggregate);
        AppendAggregate(text, "Geometry", evaluation.GeometryAggregate);
        if (geometryEnabled)
            text.AppendLine("Comparison totals: added relevant/irrelevant=" +
                evaluation.Cases.Sum(value => value.AddedRelevant ?? 0) + "/" +
                evaluation.Cases.Sum(value => value.AddedIrrelevant ?? 0) +
                ", displaced relevant=" + evaluation.Cases.Sum(value => value.DisplacedRelevant ?? 0) +
                ", gain/no-gain/regression/mixed=" +
                evaluation.Cases.Count(value => value.QualityOutcome == "gain") + "/" +
                evaluation.Cases.Count(value => value.QualityOutcome == "no-measured-gain") + "/" +
                evaluation.Cases.Count(value => value.QualityOutcome == "regression") + "/" +
                evaluation.Cases.Count(value => value.QualityOutcome == "mixed"));
        text.AppendLine("Quality outcomes are data; there is no automatic promotion or weight/label tuning.");
        text.AppendLine("Fixtures and reports are retained. Re-execution requires different absent roots.");
        return text.ToString();
    }

    private static string Metrics(RankingMetrics value) =>
        Number(value.PrecisionAt4) + "/" + Number(value.RecallAt4) + "/" + Number(value.ReciprocalRankAt4);
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "null";
    private static void AppendAggregate(StringBuilder text, string name, AggregateMetrics? value)
    {
        if (value is null) { text.AppendLine(name + " aggregate: not available."); return; }
        text.AppendLine(name + " aggregate: cases=" + value.Cases + ", zero-gold=" + value.ZeroGoldCases +
            ", recall-cases=" + value.RecallCases + ", mean P/R/RR=" + Number(value.MeanPrecisionAt4) + "/" +
            Number(value.MeanRecallAt4) + "/" + Number(value.MeanReciprocalRankAt4) +
            ", pooled noise=" + Number(value.NoiseFraction));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);
}
