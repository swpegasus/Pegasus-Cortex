using System.Text.Json;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.GeometryGrowthDiagnostic;

namespace Pegasus.Cortex.GeometryChainDiagnostic;

internal static class ChainProgram
{
    internal const string ManifestSha = "d5c739195a665589c4869a38f1435d72aa00773b6b27075aa02013ddc681d494";
    internal const string PlanSha = "8524fce6239ca5bf0cf1ee0ce790ec8ed93864afdbf2d968bb4aa27e53226582";
    internal const int ReportCap = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = GrowthCorpus.Json;

    internal static CortexKnowledgeTraversalOptions Options(int depth) =>
        new(Enabled: true, MaximumDepth: depth);

    public static async Task<int> Main(string[] args)
    {
        var stage = "arguments";
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] is "--pure" or "--run")
                    values.Add(args[index], "true");
                else
                {
                    if (!new[] { "--case", "--manifest", "--plan", "--fixture-root", "--report-root", "--reference-report" }.Contains(args[index]) ||
                        index + 1 == args.Length) throw new ArgumentException();
                    values.Add(args[index], args[++index]);
                }
            }
            if (values.ContainsKey("--pure") == values.ContainsKey("--run")) throw new ArgumentException();
            stage = "plan-admission";
            var planBytes = Read(values["--plan"], 65536);
            Need(GrowthCorpus.Hash(planBytes) == PlanSha, "plan-pin");
            using var plan = JsonDocument.Parse(planBytes);
            Need(plan.RootElement.GetProperty("sourceManifestSha256").GetString() == ManifestSha &&
                 plan.RootElement.GetProperty("worstCaseLargestReportBytes").GetInt32() ==
                 48 * 2 * (128 * 640 + 12 * (32 * 128 + 1024) + 4 * 4 * 128) + 2 * 1024 * 1024 &&
                 plan.RootElement.GetProperty("worstCaseLargestReportBytes").GetInt32() < ReportCap, "report-worst-case");
            var reportRoot = values["--report-root"];
            NewRoot(reportRoot, "gchain-rp-79fa");
            stage = "pure-checks";
            if (values.ContainsKey("--pure"))
            {
                var checks = await ChainChecks.RunAsync(values["--plan"]).ConfigureAwait(false);
                await WriteAsync(Path.Combine(reportRoot, "pure.v1.json"),
                    new { schema = "pegasus.cortex.geometry-chain-pure/1", passed = true, checks, planSha256 = PlanSha,
                        evidence = "In-memory fake-provider unit checks; SQLite not opened." }).ConfigureAwait(false);
                Console.WriteLine("Chain pure checks passed; SQLite not opened; count=" + checks.Length);
                return 0;
            }

            stage = "manifest-admission";
            var bytes = Read(values["--manifest"], GrowthCorpus.MaximumManifestBytes);
            Need(GrowthCorpus.Hash(bytes) == ManifestSha, "source-manifest-pin");
            var manifest = JsonSerializer.Deserialize<GrowthManifest>(bytes, Json) ?? throw new InvalidDataException();
            GrowthCorpus.Validate(manifest);
            Need(manifest.Records.All(record => record.Payload.Id.Length == 25 &&
                record.Payload.Id[0] == 'r' && record.Payload.Id.Skip(1).All(Uri.IsHexDigit)), "coordinate-byte-bound");
            var cellId = values["--case"];
            Need(new[] { "probe-long-gap", "a-b48-n2048", "near-a-largest", "adapter-contract" }.Contains(cellId), "frozen-case");
            var fixtureRoot = values["--fixture-root"];
            NewRoot(fixtureRoot, "gchain-fx-79fa");
            stage = "isolated-sqlite";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(cellId == "probe-long-gap" ? 90 : 360));
            if (cellId == "adapter-contract")
            {
                var proof = await ChainChecks.AdapterAsync(fixtureRoot, deadline.Token).ConfigureAwait(false);
                await WriteAsync(Path.Combine(reportRoot, "contract.v1.json"), proof).ConfigureAwait(false);
                Console.WriteLine("Actual SQLite adapter admission checks passed.");
                return 0;
            }
            var referenceBytes = Read(values["--reference-report"], 2 * 1024 * 1024);
            var reference = JsonSerializer.Deserialize<GrowthRunReport>(referenceBytes, Json) ?? throw new InvalidDataException();
            Need(reference.ManifestSha256 == ManifestSha && reference.EngineeringStatus == "passed", "reference-admission");
            var result = await ChainEvaluation.RunAsync(manifest, cellId, fixtureRoot, reportRoot, reference,
                deadline.Token).ConfigureAwait(false);
            await WriteAsync(Path.Combine(reportRoot, "report.v1.json"), result).ConfigureAwait(false);
            Console.WriteLine("Actual chain case " + cellId + "; engineering=" + result.EngineeringStatus +
                "; observations=" + result.Observations.Count + "/" + result.PlannedObservations);
            return result.EngineeringStatus == "passed" ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(GrowthEvaluation.DescribeFailure(error, stage), Json));
            return 2;
        }
    }

    internal static byte[] Read(string path, int cap)
    {
        Need(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path && !path.StartsWith(@"\\") &&
            path.Length <= 240, "input-path");
        Need(new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed, "input-fixed-local-volume");
        var info = new FileInfo(path);
        Need(info.Exists && info.LinkTarget is null && info.Length <= cap &&
            (info.Attributes & FileAttributes.ReparsePoint) == 0, "input-file");
        for (var parent = info.Directory; parent is not null; parent = parent.Parent)
            Need((parent.Attributes & FileAttributes.ReparsePoint) == 0, "input-ancestor");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.SequentialScan);
        Need(input.Length <= cap, "input-byte-cap");
        var bytes = new byte[checked(cap + 1)];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = input.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
            Need(total <= cap, "input-growth-byte-cap");
        }
        return bytes.AsSpan(0, total).ToArray();
    }
    internal static void AdmitRoot(string path, string parentName)
    {
        const string validationRoot = @"C:\CodexProjects\Pegasus Cortex\artifacts\validation";
        Need(parentName is "gchain-rp-79fa" or "gchain-fx-79fa", "declared-output-parent");
        var expected = Path.Combine(validationRoot, parentName);
        var leaf = Path.GetFileName(path);
        Need(Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\") && Path.GetFullPath(path) == path &&
            path.Length <= 140 && Path.GetDirectoryName(path) == expected && leaf.Length is >= 1 and <= 48 &&
            leaf.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
            !new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
                "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(leaf),
            "new-root-admission");
        Need(new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed, "output-fixed-local-volume");
    }
    internal static void NewRoot(string path, string parentName)
    {
        AdmitRoot(path, parentName);
        GrowthProgram.CreateFreshDirectory(path);
    }
    internal static async Task WriteAsync(string path, object value)
    {
        Need(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path && path.Length <= 240 &&
            new[] { "pure.v1.json", "baseline.v1.json", "report.v1.json", "contract.v1.json" }.Contains(Path.GetFileName(path)), "generated-report-path");
        var parent = Path.GetDirectoryName(path)!;
        AdmitRoot(parent, "gchain-rp-79fa");
        for (var ancestor = new DirectoryInfo(parent); ancestor is not null; ancestor = ancestor.Parent)
            Need(ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) == 0, "report-ordinary-ancestry");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Need(bytes.Length <= ReportCap, "report-byte-cap");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
    internal static void Need(bool condition, string code)
    {
        if (!condition) throw new GrowthInvariantException(code);
    }
}
