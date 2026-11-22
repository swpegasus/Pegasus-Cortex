using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Geometry.Shadow;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Product;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static readonly JsonSerializerOptions ConfigJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); }
    private static async Task Until(Func<bool> predicate, string name)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(10);
        Check(predicate(), name);
    }
    private static async Task<JsonObject> Call(CortexMcpLocalComposition composition, string tool, object arguments,
        CancellationToken token = default)
    {
        var result = await composition.Application.CallToolAsync(tool,
            JsonSerializer.SerializeToElement(arguments, CortexV2Json.Options), token);
        Check(result["isError"]?.GetValue<bool>() == false, tool + " failed: " + result.ToJsonString());
        return (JsonObject)result["structuredContent"]!;
    }
    private static object Query(string ns = "knowledge.shared") => new
        { namespaces = new[] { ns }, text = "quasar", limit = 4, maxUtf8Bytes = 4096 };
    private static void PrivateDirectory(string path) => CortexUserStateDirectories.EnsureCurrentUserDirectory(path);
    private static string WriteConfig(string config, object value, bool protect = true)
    {
        PrivateDirectory(config);
        var path = Path.Combine(config, CortexShadowConfiguration.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(value, ConfigJson));
        if (protect) CortexLocalRegistryAclPolicy.ProtectFile(path);
        return path;
    }
    private static object Enabled(CortexShadowProfile profile, string root) => new
    {
        schema = CortexShadowConfiguration.Schema, enabled = true, profile.TaskId, profile.ProfileId,
        profile.Generation, researchRoot = root, eligibleRecords = profile.EligibleRecords,
        options = new CortexShadowOptions(CollectionEnabled: true, SampleEvery: 1, TickMilliseconds: 10, JournalSlots: 4)
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || Directory.Exists(args[0]) || File.Exists(args[1])) return 2;
        var timer = Stopwatch.StartNew();
        try
        {
            var root = args[0]; Directory.CreateDirectory(root);
            var db = Path.Combine(root, "ordinary", "cortex-v2.db");
            JsonObject baseline; var records = new List<CortexKnowledgeV2>();
            await using (var original = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default))
            {
                Check(original.ShadowResearchDiagnostics?.State == "not-selected", "ordinary opens do not read global configuration");
                foreach (var id in new[] { "composition-alpha", "composition-beta" })
                    await Call(original, "cortex_knowledge", new { @namespace = "knowledge.shared", action = "create",
                        recordId = id, idempotencyKey = "seed-" + id, kind = "observation", tier = "verified",
                        title = "Quasar optics", content = "Quasar composition fixture optical aperture synthetic-body-marker",
                        tags = new[] { "optics" }, provenance = new { source = "agent", sourceId = "composition-fixture" } });
                foreach (var id in new[] { "composition-alpha", "composition-beta" })
                {
                    var read = await Call(original, "cortex_read", new { @namespace = "knowledge.shared",
                        recordType = "knowledge", recordId = id, revision = 1 });
                    records.Add(read["record"]!.Deserialize<CortexKnowledgeV2>(CortexV2Json.Options)!);
                }
                baseline = (await Call(original, "cortex_search", Query())).DeepClone().AsObject();
            }
            Passed.Add("real-composition/default-null-no-ambient-selection");
            var absentConfig = Path.Combine(root, "absent", "config"); PrivateDirectory(absentConfig);
            var absent = CortexShadowConfiguration.ReadOwner(absentConfig);
            Check(absent.State == "absent", "missing config");
            await using (var composition = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default,
                             shadowResearch: absent))
                Check(JsonNode.DeepEquals(baseline, await Call(composition, "cortex_search", Query())), "absent parity");
            Check(!Directory.Exists(Path.Combine(absentConfig, "geometry-shadow")), "missing config creates no research path");
            Check(CortexShadowResearchReadout.Read(absentConfig).Exit == 0, "absent readout");
            var offConfig = Path.Combine(root, "off", "config");
            WriteConfig(offConfig, new { schema = CortexShadowConfiguration.Schema, enabled = false,
                researchRoot = "invalid-root-must-not-be-touched", eligibleRecords = new object?[] { null } });
            var off = CortexShadowConfiguration.ReadOwner(offConfig); Check(off.State == "off", "off ignores inactive research data");
            await using (var composition = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default,
                             shadowResearch: off))
                Check(JsonNode.DeepEquals(baseline, await Call(composition, "cortex_search", Query())), "off parity");
            Check(!Directory.Exists(Path.Combine(offConfig, "geometry-shadow")), "off creates no research path");
            Passed.Add("real-composition/absent-off-parity-no-research-files");
            var profile = new CortexShadowProfile(Guid.Parse("01a12d3b-79fa-7582-aa10-461ab3506979"),
                Guid.NewGuid(), Guid.NewGuid(), records.Select(CortexShadowIdentity.From).ToArray());
            var enabledConfig = Path.Combine(root, "enabled", "config");
            var researchRoot = Path.Combine(enabledConfig, "geometry-shadow", profile.Generation.ToString("N"));
            WriteConfig(enabledConfig, Enabled(profile, researchRoot));
            var selected = CortexShadowConfiguration.ReadOwner(enabledConfig);
            Check(selected.State == "selected", "owner-private profile selected");
            var owner = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default, shadowResearch: selected);
            try
            {
                Check(owner.ShadowResearchDiagnostics?.State == "enabled", "attachment enabled");
                var generation = owner.ShadowResearchDiagnostics!.Collector!.Generation;
                // Two logical clients share this one owner composition; no pipes or installed host are started.
                var clientA = owner.Application; var clientB = owner.Application;
                Check(ReferenceEquals(clientA, clientB), "same owner application across logical clients");
                foreach (var record in records)
                    await Call(owner, "cortex_read", new { @namespace = record.Namespace, recordType = "knowledge",
                        recordId = record.RecordId, revision = record.Revision });
                Check(JsonNode.DeepEquals(baseline, await Call(owner, "cortex_search", Query())), "enabled canonical parity");
                Check(JsonNode.DeepEquals(baseline, await Call(owner, "cortex_search", Query("shared.programming"))),
                    "legacy alias parity through mapper");
                await Until(() => owner.ShadowResearchDiagnostics!.Collector!.Counters["Processed"] == 4, "four observed calls");
                var status = owner.ShadowResearchDiagnostics!.Collector!;
                Check(status.Generation == generation && status.CurrentPoints == 2 && !status.RankingPublished &&
                      status.Counters["ProviderOperations"] == 4 && status.Counters["WorkerFailures"] == 0,
                    "shared bounded collector");
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { await Call(owner, "cortex_search", Query(), cancel.Token); throw new InvalidOperationException("cancel lost"); }
                catch (OperationCanceledException) { }
                Check(owner.ShadowResearchDiagnostics.Collector!.Counters["ForegroundFailures"] == 1,
                    "ordinary cancellation preserved");
            }
            finally { await owner.DisposeAsync(); }
            Check(owner.ShadowResearchDiagnostics?.Collector?.Lifecycle == "stopped", "owner collector stopped");
            using (var unlocked = new FileStream(Path.Combine(researchRoot, "writer.lock"), FileMode.Open,
                       FileAccess.ReadWrite, FileShare.None)) Check(unlocked.Length >= 0, "collector lock released");
            try { await owner.V2Store.GetCatalogCountersAsync(default); throw new InvalidOperationException("DB owner not disposed"); }
            catch (ObjectDisposedException) { }
            Passed.Add("real-composition/enabled-shared-collector-response-and-alias-parity-cancellation-shutdown");
            var readout = CortexShadowResearchReadout.Read(enabledConfig);
            var readoutJson = JsonNode.Parse(readout.Json)!;
            Check(readout.Exit == 0 && readoutJson["state"]!.GetValue<string>() == "stopped-snapshot" &&
                  !readoutJson["hostLivenessVerified"]!.GetValue<bool>() && readoutJson["snapshot"]!["Final"]!.GetValue<bool>(),
                "concrete bounded stopped snapshot readout");
            Check(new FileInfo(Path.Combine(researchRoot, "status.json")).Length <= 16384, "readout bytes bounded");
            Passed.Add("real-filesystem/owner-private-status-command-readout");
            var badCases = new (string Name, object Value, bool Protect)[]
            {
                ("null-entry", new { schema = CortexShadowConfiguration.Schema, enabled = true, profile.TaskId,
                    profile.ProfileId, profile.Generation, researchRoot, eligibleRecords = new object?[] { null } }, true),
                ("missing-namespace", new { schema = CortexShadowConfiguration.Schema, enabled = true, profile.TaskId,
                    profile.ProfileId, profile.Generation, researchRoot,
                    eligibleRecords = new[] { new { recordId = "composition-alpha", revision = 1,
                        provenanceSha256 = new string('a', 64) } } }, true),
                ("unprotected-config", Enabled(profile, researchRoot), false),
                ("outside-root", Enabled(profile, Path.Combine(root, "outside")), true)
            };
            foreach (var bad in badCases)
            {
                var config = Path.Combine(root, bad.Name, "config"); WriteConfig(config, bad.Value, bad.Protect);
                var rejected = CortexShadowConfiguration.ReadOwner(config);
                Check(rejected.State == "disabled" && rejected.Issue is not null, "bad profile disabled: " + bad.Name);
                await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default,
                    shadowResearch: rejected);
                Check(JsonNode.DeepEquals(baseline, await Call(composition, "cortex_search", Query())), "bad profile preserves ordinary: " + bad.Name);
                Check(!Directory.Exists(Path.Combine(config, "geometry-shadow")), "bad profile no research creation");
            }
            var malformedConfig = Path.Combine(root, "malformed", "config"); PrivateDirectory(malformedConfig);
            var malformedPath = Path.Combine(malformedConfig, CortexShadowConfiguration.FileName);
            File.WriteAllText(malformedPath, "{"); CortexLocalRegistryAclPolicy.ProtectFile(malformedPath);
            var malformed = CortexShadowConfiguration.ReadOwner(malformedConfig);
            Check(malformed.State == "disabled", "malformed JSON disabled");
            await using (var composition = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default,
                             shadowResearch: malformed))
                Check(JsonNode.DeepEquals(baseline, await Call(composition, "cortex_search", Query())), "malformed JSON ordinary parity");
            Passed.Add("real-composition/malformed-null-missing-field-ACL-outside-root-degrade-research-only");
            var rootFaultConfig = Path.Combine(root, "root-fault", "config"); PrivateDirectory(rootFaultConfig);
            var blockedRoot = Path.Combine(rootFaultConfig, "geometry-shadow", profile.Generation.ToString("N"));
            PrivateDirectory(Path.GetDirectoryName(blockedRoot)!); File.WriteAllText(blockedRoot, "task-owned-root-collision");
            WriteConfig(rootFaultConfig, Enabled(profile, blockedRoot));
            await using (var composition = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default,
                             shadowResearch: CortexShadowConfiguration.ReadOwner(rootFaultConfig)))
            {
                Check(composition.ShadowResearchDiagnostics?.State == "disabled", "root IO disabled");
                Check(JsonNode.DeepEquals(baseline, await Call(composition, "cortex_search", Query())), "root fault ordinary parity");
            }
            Check(File.ReadAllText(blockedRoot) == "task-owned-root-collision", "root collision preserved");
            var bindingPath = Path.Combine(researchRoot, "binding.json"); var bindingBefore = File.ReadAllText(bindingPath);
            WriteConfig(enabledConfig, Enabled(profile with { ProfileId = Guid.NewGuid() }, researchRoot));
            await using (var composition = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default,
                             shadowResearch: CortexShadowConfiguration.ReadOwner(enabledConfig)))
            {
                Check(composition.ShadowResearchDiagnostics?.State == "disabled", "binding mismatch disabled");
                Check(JsonNode.DeepEquals(baseline, await Call(composition, "cortex_search", Query())), "binding fault ordinary parity");
            }
            Check(bindingBefore == File.ReadAllText(bindingPath), "binding preserved");
            WriteConfig(enabledConfig, Enabled(profile, researchRoot));
            Passed.Add("real-composition/root-and-binding-faults-preserve-ordinary-and-artifacts");
            await using (var ordinary = await CortexMcpLocalComposition.CreateGeneralAsync(db, null, default))
                Check(ordinary.ShadowResearchDiagnostics?.State == "not-selected" &&
                      JsonNode.DeepEquals(baseline, await Call(ordinary, "cortex_search", Query())),
                    "ordinary DB reopen ignores enabled owner configuration unless explicitly selected");
            Passed.Add("real-composition/no-ambient-activation-on-unrelated-opens");
            var summary = JsonSerializer.Deserialize<CortexShadowStudySummary>(
                File.ReadAllText(Path.Combine(researchRoot, "status.json")))!;
            var report = new
            {
                schema = "cortex.shadow.composition-diagnostic/1", passed = Passed, failures = Array.Empty<string>(),
                evidence = "Real source application composition/local MCP calls and actual owner-private fixture ACL/files; no named pipes, installed host, old DB or real-user traffic.",
                fixtureDatabase = db, freshFixtureHeads = 2, researchRoot,
                summary, readout = JsonNode.Parse(readout.Json), elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                originalResponsesPreserved = true, aliasMapperPreserved = true, collectorSharedByOwner = true,
                inactiveOtherOpenPaths = true, sourceOnly = true, installedActivation = false
            };
            Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Shadow composition diagnostic passed; groups={Passed.Count}; installed-activation=0.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
