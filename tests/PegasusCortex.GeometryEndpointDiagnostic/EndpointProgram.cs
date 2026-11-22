using System.Text.Json;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.GeometryChainDiagnostic;
using Pegasus.Cortex.GeometryGrowthDiagnostic;

namespace Pegasus.Cortex.GeometryEndpointDiagnostic;

internal sealed record EndpointOrigin(string SourceId, DateTimeOffset CapturedAtUtc);
internal sealed record EndpointRecord(string Id, string Title, string Content, string[] Tags, EndpointOrigin Provenance);
internal sealed record EndpointQuery(string Id, string Family, string Text);
internal sealed record EndpointHoldout(string Schema, string Namespace, EndpointRecord[] Records, EndpointQuery[] Queries);
internal sealed record EndpointAnswerGroup(string Id, bool Required, string[] AnyOf, string Rationale);
internal sealed record EndpointGoldCase(string QueryId, string[] PrimaryTargetIds,
    Dictionary<string, string> UsefulSupport, Dictionary<string, string> Noise,
    Dictionary<string, string>? PrimaryRationales = null, EndpointAnswerGroup[]? AnswerGroups = null, string? EvaluationNote = null);
internal sealed record EndpointGold(string Schema, EndpointGoldCase[] Cases);

internal static class EndpointProgram
{
    internal const string ManifestSha = ChainProgram.ManifestSha;
    internal const string PolicySha = "66cdccfdfb9b4b31b0ab925fd739d01e9176be9eec02759d47e2d27221d011d7";
    internal const int ReportCap = 16 * 1024 * 1024;
    internal static JsonSerializerOptions Json => GrowthCorpus.Json;

    public static async Task<int> Main(string[] args)
    {
        var stage = "arguments";
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var modes = new[] { "--pure", "--capture-known", "--recover-known", "--score-cache", "--holdout" };
            var names = new[] { "--policy", "--report-root", "--fixture-root", "--case", "--manifest", "--reference-report",
                "--chain-reference", "--cache", "--payload", "--gold", "--source-freeze", "--expected-source-freeze-sha256",
                "--baseline", "--expected-baseline-sha256", "--expected-fixture-sha256" };
            for (var index = 0; index < args.Length; index++)
            {
                if (modes.Contains(args[index])) values.Add(args[index], "true");
                else
                {
                    if (!names.Contains(args[index]) || index + 1 == args.Length) throw new ArgumentException();
                    values.Add(args[index], args[++index]);
                }
            }
            Need(modes.Count(values.ContainsKey) == 1, "one-mode");
            stage = "policy-admission";
            var policyBytes = Read(values["--policy"], 65536);
            Need(GrowthCorpus.Hash(policyBytes) == PolicySha &&
                CortexKnowledgeGeometry.EndpointSelectionPolicyVersion == "root-anchor-endpoint/1", "policy-pin");
            var report = values["--report-root"];
            NewRoot(report, "gend-rp-79fa");
            if (values.ContainsKey("--pure"))
            {
                stage = "pure-selector";
                var checks = await EndpointChecks.RunAsync().ConfigureAwait(false);
                await WriteAsync(Path.Combine(report, "pure.v1.json"), new
                {
                    schema = "pegasus.cortex.endpoint-pure/1", passed = true, checks,
                    policySha256 = PolicySha, evidence = "Pure/fake-graph unit evidence; SQLite not opened."
                }).ConfigureAwait(false);
                Console.WriteLine("Endpoint pure checks passed; SQLite not opened; groups=" + checks.Length);
                return 0;
            }
            if (values.ContainsKey("--capture-known") || values.ContainsKey("--recover-known"))
            {
                stage = "known-manifest";
                var manifestBytes = Read(values["--manifest"], GrowthCorpus.MaximumManifestBytes);
                Need(GrowthCorpus.Hash(manifestBytes) == ManifestSha, "known-manifest-pin");
                var manifest = JsonSerializer.Deserialize<GrowthManifest>(manifestBytes, Json) ?? throw new InvalidDataException();
                GrowthCorpus.Validate(manifest);
                var id = values["--case"];
                Need(new[] { "probe-long-gap", "a-b48-n2048", "near-a-largest" }.Contains(id), "known-case");
                byte[]? retainedBaseline = null;
                string? databaseBefore = null;
                var databasePath = Path.Combine(values["--fixture-root"], id, Pegasus.Cortex.Persistence.Sqlite.V2.SqliteCortexV2StoreOptions.RequiredDatabaseFileName);
                if (values.ContainsKey("--recover-known"))
                {
                    AdmitRoot(values["--fixture-root"], "gend-fx-79fa");
                    Need(id == "probe-long-gap" && Path.GetFileName(values["--fixture-root"]) == id, "exact-retained-capture-root");
                    databaseBefore = GrowthCorpus.Hash(Read(databasePath, ReportCap));
                    retainedBaseline = Read(values["--baseline"], ReportCap);
                    Need(databaseBefore == values["--expected-fixture-sha256"] &&
                        GrowthCorpus.Hash(retainedBaseline) == values["--expected-baseline-sha256"], "retained-fixture-and-baseline-pins");
                }
                else NewRoot(values["--fixture-root"], "gend-fx-79fa");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(id == "probe-long-gap" ? 90 : 360));
                stage = "known-capture";
                var cache = await EndpointEvaluation.CaptureKnownAsync(manifest, id, values["--fixture-root"],
                    report, values["--reference-report"], values["--chain-reference"], deadline.Token, retainedBaseline).ConfigureAwait(false);
                if (databaseBefore is not null)
                    Need(databaseBefore == GrowthCorpus.Hash(Read(databasePath, ReportCap)), "read-only-producer-recovery-database-unchanged");
                await WriteAsync(Path.Combine(report, "graph-cache.v1.json"), cache).ConfigureAwait(false);
                Console.WriteLine("Known graph capture passed; case=" + id + "; queries=" + cache.Queries.Length);
                return 0;
            }
            if (values.ContainsKey("--score-cache"))
            {
                stage = "known-cache-selection";
                var cacheBytes = Read(values["--cache"], ReportCap);
                var cache = JsonSerializer.Deserialize<EndpointGraphCache>(cacheBytes, Json) ?? throw new InvalidDataException();
                Need(cache.Schema == "pegasus.cortex.endpoint-graph-cache/1" && cache.Dataset == "known-C3" &&
                    cache.SourceManifestSha256 == ManifestSha, "known-cache-admission");
                var truthBytes = Read(values["--manifest"], GrowthCorpus.MaximumManifestBytes);
                Need(GrowthCorpus.Hash(truthBytes) == ManifestSha, "known-truth-pin");
                var manifest = JsonSerializer.Deserialize<GrowthManifest>(truthBytes, Json) ?? throw new InvalidDataException();
                var gold = EndpointEvaluation.KnownGold(manifest, cache.CaseId);
                var result = EndpointEvaluation.Score(cache, gold, GrowthCorpus.Hash(cacheBytes));
                await WriteAsync(Path.Combine(report, "selection.v1.json"), result).ConfigureAwait(false);
                Console.WriteLine("Pure known-cache selection " + result.EngineeringStatus + "; queries=" + result.Observations.Length +
                    "; selector provider calls=0");
                return result.EngineeringStatus == "passed" ? 0 : 1;
            }

            // The only holdout branch must validate final source/policy/image binding
            // BEFORE touching either payload or gold bytes.
            stage = "blind-freeze-admission";
            var freezeBytes = Read(values["--source-freeze"], 256 * 1024);
            Need(GrowthCorpus.Hash(freezeBytes) == values["--expected-source-freeze-sha256"], "final-freeze-pin");
            using var freeze = JsonDocument.Parse(freezeBytes);
            var f = freeze.RootElement;
            Need(f.GetProperty("finalBeforeHoldout").GetBoolean() &&
                f.GetProperty("policySha256").GetString() == PolicySha &&
                f.GetProperty("driverSha256").GetString() == GrowthCorpus.Hash(Read(typeof(EndpointProgram).Assembly.Location, ReportCap)) &&
                f.GetProperty("coreSha256").GetString() == GrowthCorpus.Hash(Read(typeof(CortexKnowledgeGeometry).Assembly.Location, ReportCap)),
                "final-source-policy-image-binding");
            stage = "holdout-open-after-freeze";
            var payloadBytes = Read(values["--payload"], 131072);
            var goldBytes = Read(values["--gold"], 262144);
            Need(GrowthCorpus.Hash(payloadBytes) == f.GetProperty("holdoutPayloadSha256").GetString() &&
                GrowthCorpus.Hash(goldBytes) == f.GetProperty("holdoutGoldSha256").GetString(), "holdout-pins");
            var holdout = JsonSerializer.Deserialize<EndpointHoldout>(payloadBytes, Json) ?? throw new InvalidDataException();
            var holdoutGold = JsonSerializer.Deserialize<EndpointGold>(goldBytes, Json) ?? throw new InvalidDataException();
            EndpointEvaluation.ValidateHoldout(holdout, holdoutGold);
            NewRoot(values["--fixture-root"], "gend-fx-79fa");
            using var holdoutDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            stage = "holdout-sqlite";
            var holdoutCache = await EndpointEvaluation.CaptureHoldoutAsync(holdout, values["--fixture-root"], holdoutDeadline.Token)
                .ConfigureAwait(false);
            await WriteAsync(Path.Combine(report, "graph-cache.v1.json"), holdoutCache).ConfigureAwait(false);
            var resultHoldout = EndpointEvaluation.Score(holdoutCache, holdoutGold.Cases.ToDictionary(value => value.QueryId),
                GrowthCorpus.Hash(JsonSerializer.SerializeToUtf8Bytes(holdoutCache, Json)));
            await WriteAsync(Path.Combine(report, "selection.v1.json"), resultHoldout).ConfigureAwait(false);
            Console.WriteLine("Frozen holdout selection " + resultHoldout.EngineeringStatus + "; queries=" +
                resultHoldout.Observations.Length + "; selector provider calls=0");
            return resultHoldout.EngineeringStatus == "passed" ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(GrowthEvaluation.DescribeFailure(error, stage), Json));
            return 2;
        }
    }

    internal static byte[] Read(string path, int cap) => ChainProgram.Read(path, cap);
    internal static void AdmitRoot(string path, string parentName)
    {
        const string basePath = @"C:\CodexProjects\Pegasus Cortex\artifacts\validation";
        Need(parentName is "gend-fx-79fa" or "gend-rp-79fa", "output-parent");
        var leaf = Path.GetFileName(path);
        Need(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path && path.Length <= 140 &&
            Path.GetDirectoryName(path) == Path.Combine(basePath, parentName) &&
            leaf.Length is >= 1 and <= 48 && leaf.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
            !new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
                "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(leaf) &&
            new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed, "output-root");
    }
    internal static void NewRoot(string path, string parentName)
    {
        AdmitRoot(path, parentName);
        GrowthProgram.CreateFreshDirectory(path);
    }
    internal static async Task WriteAsync(string path, object value)
    {
        Need(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path && path.Length <= 240 &&
            new[] { "pure.v1.json", "baseline.v1.json", "graph-cache.v1.json", "selection.v1.json" }.Contains(Path.GetFileName(path)),
            "generated-output-path");
        var parent = Path.GetDirectoryName(path)!;
        AdmitRoot(parent, "gend-rp-79fa");
        for (var ancestor = new DirectoryInfo(parent); ancestor is not null; ancestor = ancestor.Parent)
            Need(ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) == 0, "ordinary-output-ancestor");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Need(bytes.Length <= ReportCap, "report-byte-cap");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
    internal static void Need(bool value, string code) => ChainProgram.Need(value, code);
}
