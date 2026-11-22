using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;
using Pegasus.Cortex.Product;

internal static class ProductStarterCorpusTests
{
    private const int CerebralGuidePackRevision = 9;

    public static async Task FreshStoreIsSeededAtomicallyAndExistingStoreIsPreservedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cx-seed", Guid.NewGuid().ToString("N"));
        var corpus = Path.Combine(root, "corpus");
        var time = new FixedSeedTimeProvider(new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero));
        try
        {
            WriteCorpus(corpus, generalContent: "general Ω\r\nexact", includeLateRecord: false);
            var database = Path.Combine(root, "fresh", "cortex-v2.db");
            var first = await CortexLocalProductCommands.ImportStarterCorpusAsync(
                corpus, database, onlyIfEmpty: true, "os_seed_test", time, CancellationToken.None);
            using (var json = JsonDocument.Parse(first.Json))
            {
                LocalAssert.True(
                    first.ExitCode == 0 &&
                    json.RootElement.GetProperty("state").GetString() == "starter-seeded" &&
                    json.RootElement.GetProperty("imported").GetInt32() == 2 &&
                    json.RootElement.GetProperty("total").GetInt32() == 2,
                    $"A fresh store was not seeded exactly: {first.Json}");
            }
            await using (var store = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(database), time))
            {
                var general = await store.ReadHeadAsync("knowledge.shared", "knowledge", "tenet.general.fixture");
                var programming = await store.ReadHeadAsync(
                    "knowledge.shared", "knowledge", "workaround.programming.fixture");
                var counters = await store.GetCatalogCountersAsync();
                LocalAssert.True(
                    general is not null && Encoding.UTF8.GetString(general.ContentUtf8) == "general Ω\r\nexact" &&
                    programming is not null && Encoding.UTF8.GetString(programming.ContentUtf8) == "programming exact" &&
                    counters.NamespaceRows == 1 && counters.HeadRows == 2 && counters.RevisionRows == 2,
                    "The starter corpus did not preserve exact content in one shared knowledge space.");
            }

            var replay = await CortexLocalProductCommands.ImportStarterCorpusAsync(
                corpus, database, onlyIfEmpty: false, "os_seed_test", time, CancellationToken.None);
            using (var json = JsonDocument.Parse(replay.Json))
            {
                LocalAssert.True(
                    replay.ExitCode == 0 &&
                    json.RootElement.GetProperty("state").GetString() == "starter-current" &&
                    json.RootElement.GetProperty("imported").GetInt32() == 0 &&
                    json.RootElement.GetProperty("unchanged").GetInt32() == 2,
                    $"Starter corpus replay was not idempotent: {replay.Json}");
            }

            var existingDatabase = Path.Combine(root, "existing", "cortex-v2.db");
            await using (var existing = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(existingDatabase), time))
            {
                await existing.AppendAsync(new SqliteCortexV2AppendRequest(
                    "project.existing", "knowledge", "existing-record", 0,
                    "existing-create", "os_seed_test", "Create", "Existing", [], "existing",
                    Encoding.UTF8.GetBytes("existing exact"), Searchable: false, AttributesJson: "{}"));
            }
            var preserved = await CortexLocalProductCommands.ImportStarterCorpusAsync(
                corpus, existingDatabase, onlyIfEmpty: true, "os_seed_test", time, CancellationToken.None);
            using (var json = JsonDocument.Parse(preserved.Json))
            {
                LocalAssert.True(
                    preserved.ExitCode == 0 &&
                    json.RootElement.GetProperty("state").GetString() == "existing-store-unchanged",
                    "Automatic starter seeding changed a nonempty existing store.");
            }
            await using (var existing = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(existingDatabase), time))
            {
                var absent = await existing.ReadHeadAsync(
                    "knowledge.shared", "knowledge", "tenet.general.fixture");
                var counters = await existing.GetCatalogCountersAsync();
                LocalAssert.True(
                    absent is null && counters.HeadRows == 1 && counters.RevisionRows == 1,
                    "Automatic starter seeding appended into a nonempty store.");
            }

            WriteCorpus(corpus, generalContent: "conflicting general", includeLateRecord: true);
            var conflict = await CortexLocalProductCommands.ImportStarterCorpusAsync(
                corpus, database, onlyIfEmpty: false, "os_seed_test", time, CancellationToken.None);
            LocalAssert.True(conflict.ExitCode == 1, "A divergent starter head was not rejected.");
            await using (var store = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(database), time))
            {
                var original = await store.ReadHeadAsync("knowledge.shared", "knowledge", "tenet.general.fixture");
                var late = await store.ReadHeadAsync("knowledge.shared", "knowledge", "workaround.late.fixture");
                var counters = await store.GetCatalogCountersAsync();
                LocalAssert.True(
                    original is not null && Encoding.UTF8.GetString(original.ContentUtf8) == "general Ω\r\nexact" &&
                    late is null && counters.HeadRows == 2 && counters.RevisionRows == 2,
                    "A conflicting starter corpus partially changed the existing store.");
            }

            var sourceRoot = FindSourceRoot();
            var sourceManifest = await CortexFileStarterCorpusManifestV1.ReadAsync(
                Path.Combine(sourceRoot, "integrations"));
            var generalRoot = Path.Combine(sourceRoot, "integrations", "general-knowledge-v1");
            var programmingRoot = Path.Combine(sourceRoot, "integrations", "file-cortex-v2");
            var cerebralRoot = Path.Combine(sourceRoot, "integrations", "cerebral-guides-v1");
            var windowsRoot = Path.Combine(sourceRoot, "integrations", "windows-operational-knowledge-v1");
            var generalPack = await CortexFileBehaviorPackManifestV2.ReadAsync(generalRoot);
            var programmingPack = await CortexFileBehaviorPackManifestV2.ReadAsync(programmingRoot);
            var cerebralPack = await CortexFileBehaviorPackManifestV2.ReadAsync(cerebralRoot);
            var windowsPack = await CortexFileBehaviorPackManifestV2.ReadAsync(windowsRoot);
            var sourceTotal = (await generalPack.LoadRecordsAsync(generalRoot)).Count +
                (await programmingPack.LoadRecordsAsync(programmingRoot)).Count +
                (await cerebralPack.LoadRecordsAsync(cerebralRoot)).Count +
                (await windowsPack.LoadRecordsAsync(windowsRoot)).Count;
            var managedGuidancePath = Path.Combine(
                sourceRoot,
                "integrations",
                "codex",
                "pegasus-cortex-client",
                "guidance",
                "pegasus-cortex-agents.v2.md");
            var managedGuidanceBytes = await File.ReadAllBytesAsync(managedGuidancePath);
            var managedGuidance = new UTF8Encoding(false, true).GetString(managedGuidanceBytes);
            var managedPaoPath = Path.Combine(
                sourceRoot,
                "integrations",
                "codex",
                "pegasus-cortex-client",
                "guidance",
                "pegasus-cortex-pao.v1.md");
            var managedPaoBytes = await File.ReadAllBytesAsync(managedPaoPath);
            var managedPao = new UTF8Encoding(false, true).GetString(managedPaoBytes);
            var pluginRoot = Path.Combine(
                sourceRoot, "integrations", "codex", "pegasus-cortex-client");
            var clientSkill = await File.ReadAllTextAsync(Path.Combine(
                pluginRoot, "skills", "pegasus-cortex-client", "SKILL.md"));
            var capabilities = await File.ReadAllTextAsync(Path.Combine(
                pluginRoot, "skills", "pegasus-cortex-client", "references", "capabilities.md"));
            var agentPrompt = await File.ReadAllTextAsync(Path.Combine(
                pluginRoot, "skills", "pegasus-cortex-client", "agents", "openai.yaml"));
            using var pluginManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
                pluginRoot, ".codex-plugin", "plugin.json")));
            using var mcpManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
                pluginRoot, ".mcp.json")));
            LocalAssert.True(
                sourceManifest.Packs.Select(value => value.Namespace).SequenceEqual(
                    new[] { "knowledge.shared", "knowledge.shared", "knowledge.shared", "knowledge.shared" }, StringComparer.Ordinal) &&
                sourceManifest.Packs.Select(value => value.Path).SequenceEqual(
                    new[] { "general-knowledge", "generalized-programming-tenets", "cerebral-guides", "windows-operational-knowledge" }, StringComparer.Ordinal) &&
                sourceTotal == 94 &&
                generalPack.PackRevision == 2 &&
                programmingPack.PackRevision == 1 &&
                cerebralPack.PackRevision == CerebralGuidePackRevision &&
                windowsPack.PackRevision == 1 &&
                generalPack.Records.Any(value =>
                    value.RecordId == "tenet.bounded-geometric-knowledge-retrieval.20260828" &&
                    value.Tags.Contains("geometry", StringComparer.Ordinal) &&
                    value.Tags.Contains("exclusion", StringComparer.Ordinal)) &&
                generalPack.Records.Any(value =>
                    value.RecordId == "tenet.meaningful-cross-agent-continuity.20260816") &&
                generalPack.Records.All(value =>
                    value.RecordId != "preference.pao-first-local-developer-operations.20260815") &&
                cerebralPack.Records.Count == 17 &&
                cerebralPack.Records.Any(value => value.RecordId == "Knowledge.Cerebral.Guide") &&
                cerebralPack.Records.Any(value => value.RecordId == "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions") &&
                new[]
                {
                    "Knowledge.Cerebral.Guide.ContainerBuildAdmission",
                    "Knowledge.Cerebral.Guide.ContainerRuntimeQualification",
                    "Knowledge.Cerebral.Guide.SshAccessRouting",
                    "Knowledge.Cerebral.Guide.TlsCertificatesIngress",
                    "Knowledge.Cerebral.Guide.MultiHomedNetworking",
                    "Knowledge.Cerebral.Guide.DependencyNativeBuilds",
                    "Knowledge.Cerebral.Guide.SecretProvisioning",
                    "Knowledge.Cerebral.Guide.FailureEvidenceRetry",
                    "Knowledge.Cerebral.Guide.PrivateServiceMonitoring"
                }.All(id => cerebralPack.Records.Any(value => value.RecordId == id)) &&
                cerebralPack.Records.Any(value => value.RecordId == "Knowledge.Cerebral.Guide.BoundedWork") &&
                windowsPack.Records.Count == 8 &&
                windowsPack.Records.Any(value => value.RecordId == "Knowledge.Windows") &&
                windowsPack.Records.Any(value => value.RecordId == "Knowledge.Windows.FileTransfer.Robocopy") &&
                windowsPack.Records.Any(value => value.RecordId == "Knowledge.Windows.IPC.NamedPipes") &&
                cerebralPack.Records.Concat(windowsPack.Records).All(value =>
                    !value.RecordId.StartsWith("guide.cerebral.", StringComparison.Ordinal)),
                "The source starter corpus is not the expected one shared space and 94 portable non-personal records.");
            LocalAssert.True(
                managedGuidanceBytes.Length < 1024 &&
                managedGuidance.StartsWith("## Pegasus Cortex continuity\n", StringComparison.Ordinal) &&
                managedGuidance.TrimEnd().EndsWith("End of Pegasus Cortex continuity", StringComparison.Ordinal) &&
                !managedGuidance.Contains("PEGASUS:CORTEX:MANAGED", StringComparison.Ordinal) &&
                managedGuidance.Contains("CODEX_THREAD_ID", StringComparison.Ordinal) &&
                managedGuidance.Contains("explicit relevant namespaces", StringComparison.Ordinal) &&
                managedGuidance.Contains("Search compact previews", StringComparison.Ordinal) &&
                managedGuidance.Contains("exact-read only selected record IDs", StringComparison.Ordinal) &&
                managedGuidance.Contains("knowledge.shared", StringComparison.Ordinal) &&
                managedGuidance.Contains("Save only durable", StringComparison.Ordinal) &&
                managedGuidance.Contains("cortex_finish", StringComparison.Ordinal) &&
                managedGuidance.Contains("never expands authority", StringComparison.Ordinal),
                "The suite-owned managed Cortex guidance is missing, oversized, or contract-inaccurate.");
            LocalAssert.True(
                managedPaoBytes.Length < 1024 &&
                managedPao.Contains("PEGASUS:CORTEX:PAO:MANAGED:v1", StringComparison.Ordinal) &&
                managedPao.Contains("live `pao` tool schema exposed by `pegasus_cortex`", StringComparison.Ordinal) &&
                managedPao.Contains("Select one fixed operation", StringComparison.Ordinal) &&
                managedPao.Contains("Ordinary operations use the user host", StringComparison.Ordinal) &&
                managedPao.Contains("root-authorized operations require the service, not the tray", StringComparison.Ordinal) &&
                !managedPao.Contains("`pegasus_agentops` MCP tool", StringComparison.Ordinal),
                "The suite-owned unified PAO guidance is missing, oversized, or routes to the retired MCP server.");
            var pluginJson = pluginManifest.RootElement.GetRawText();
            var mcpServer = mcpManifest.RootElement.GetProperty("mcpServers")
                .GetProperty("pegasus_cortex");
            LocalAssert.True(
                pluginManifest.RootElement.GetProperty("version").GetString() ==
                    "0.3.0+codex.20260828001800" &&
                mcpServer.GetProperty("command").GetString() ==
                    @"C:\Program Files\Pegasus\Cortex\PegasusCortex.exe" &&
                mcpServer.GetProperty("args").GetArrayLength() == 0 &&
                clientSkill.Contains("`format=compact`", StringComparison.Ordinal) &&
                clientSkill.Contains("one through eight unique typed coordinates", StringComparison.Ordinal) &&
                clientSkill.Contains("Cortex does not keep read receipts", StringComparison.Ordinal) &&
                capabilities.Contains("content-free retrieval counters", StringComparison.Ordinal) &&
                capabilities.Contains("search exclusion list", StringComparison.Ordinal) &&
                capabilities.Contains("suggestions. Analysis is read-only", StringComparison.Ordinal) &&
                agentPrompt.Contains("track loaded revisions in context", StringComparison.Ordinal) &&
                pluginJson.Contains("preview-first geometric retrieval", StringComparison.Ordinal) &&
                pluginJson.Contains("reuse loaded revisions only while their full bodies remain", StringComparison.Ordinal) &&
                !pluginJson.Contains("Retain my durable interaction preference", StringComparison.Ordinal),
                "The source client plugin is not versioned or aligned with compact preview, exact hydration, and one-route guidance.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task SingleBehaviorPackRouteIsAtomicScopedAndBoundedAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cx-pack-route", Guid.NewGuid().ToString("N"));
        var time = new FixedSeedTimeProvider(new DateTimeOffset(2026, 9, 27, 23, 30, 0, TimeSpan.Zero));
        try
        {
            Directory.CreateDirectory(root);
            var sourceRoot = FindSourceRoot();
            var packRoot = Path.Combine(sourceRoot, "integrations", "cerebral-guides-v1");
            var database = Path.Combine(root, "current", "cortex-v2.db");
            var first = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                packRoot,
                database,
                CortexKnowledgeSpaces.Shared,
                "os_pack_route_test",
                time,
                CancellationToken.None);
            using (var json = JsonDocument.Parse(first.Json))
            {
                LocalAssert.True(
                    first.ExitCode == 0 &&
                    Encoding.UTF8.GetByteCount(first.Json) <= 2048 &&
                    json.RootElement.GetProperty("schema").GetString() ==
                        "pegasus.cortex.behavior-pack-import/1.0" &&
                    json.RootElement.GetProperty("state").GetString() == "pack-imported" &&
                    json.RootElement.GetProperty("packId").GetString() == "pegasus-cerebral-guides-v1" &&
                    json.RootElement.GetProperty("effectiveNamespaces")[0].GetString() ==
                        CortexKnowledgeSpaces.Shared &&
                    json.RootElement.GetProperty("imported").GetInt32() == 17 &&
                    json.RootElement.GetProperty("revised").GetInt32() == 0 &&
                    json.RootElement.GetProperty("unchanged").GetInt32() == 0 &&
                    json.RootElement.GetProperty("total").GetInt32() == 17 &&
                    !json.RootElement.TryGetProperty("store", out _),
                    $"The single-pack route did not return its bounded receipt: {first.Json}");
            }
            await using (var store = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(database), time))
            {
                var exact = await store.ReadHeadAsync(
                    CortexKnowledgeSpaces.Shared,
                    "knowledge",
                    "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions");
                var counters = await store.GetCatalogCountersAsync();
                LocalAssert.True(
                    exact is not null &&
                    Encoding.UTF8.GetString(exact.ContentUtf8).Contains(
                        "virtual environment", StringComparison.OrdinalIgnoreCase) &&
                    counters.HeadRows == 17 && counters.RevisionRows == 17,
                    "The single-pack route did not retain the exact seventeen-record module.");
            }

            var replay = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                packRoot,
                database,
                CortexKnowledgeSpaces.Shared,
                "os_pack_route_test",
                time,
                CancellationToken.None);
            using (var json = JsonDocument.Parse(replay.Json))
            {
                LocalAssert.True(
                    replay.ExitCode == 0 &&
                    json.RootElement.GetProperty("state").GetString() == "pack-current" &&
                    json.RootElement.GetProperty("imported").GetInt32() == 0 &&
                    json.RootElement.GetProperty("revised").GetInt32() == 0 &&
                    json.RootElement.GetProperty("unchanged").GetInt32() == 17,
                    $"The single-pack route was not idempotent: {replay.Json}");
            }

            var managedDatabase = Path.Combine(root, "managed-old", "cortex-v2.db");
            var managedLowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(managedDatabase), time);
            await using (var managedStore = new SqliteCortexV2OperationalStore(
                             managedLowLevel, "pack-route-test"))
            {
                await managedLowLevel.InitializeAsync();
                await managedStore.ImportKnowledgeSeedsAsync(
                    [new SqliteCortexV2KnowledgeSeed(
                        "pegasus-cerebral-guides-v1",
                        4,
                        CortexKnowledgeSpaces.Shared,
                        "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions",
                        CortexKnowledgeKindV2.Tenet,
                        CortexKnowledgeTierV2.Explicit,
                        "Outdated managed fixture",
                        "outdated managed content",
                        ["generalized"],
                        CortexProvenanceSourceV2.Agent,
                        "fixture/managed-old")],
                    "os_pack_route_test",
                    time.GetUtcNow(),
                    CancellationToken.None);
            }
            var managedUpdate = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                packRoot,
                managedDatabase,
                CortexKnowledgeSpaces.Shared,
                "os_pack_route_test",
                time,
                CancellationToken.None);
            using (var json = JsonDocument.Parse(managedUpdate.Json))
            {
                LocalAssert.True(
                    managedUpdate.ExitCode == 0 &&
                    json.RootElement.GetProperty("state").GetString() == "pack-updated" &&
                    json.RootElement.GetProperty("imported").GetInt32() == 16 &&
                    json.RootElement.GetProperty("revised").GetInt32() == 1 &&
                    json.RootElement.GetProperty("unchanged").GetInt32() == 0,
                    $"The newer managed pack did not replace its older owned record: {managedUpdate.Json}");
            }
            await using (var store = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(managedDatabase), time))
            {
                var revised = await store.ReadHeadAsync(
                    CortexKnowledgeSpaces.Shared,
                    "knowledge",
                    "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions");
                var counters = await store.GetCatalogCountersAsync();
                LocalAssert.True(
                    revised is { Revision: 2 } &&
                    Encoding.UTF8.GetString(revised.ContentUtf8).Contains(
                        "virtual environment", StringComparison.OrdinalIgnoreCase) &&
                    counters.HeadRows == 17 && counters.RevisionRows == 18,
                    "The managed pack replacement did not retain one auditable revision.");
            }

            var adoptionDatabase = Path.Combine(root, "adoption", "cortex-v2.db");
            var adoptionLowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(adoptionDatabase), time);
            const string predecessorSource = "codex-task-019ff269-3c07-7082-87cf-d9325d7e24ba";
            await using (var adoptionStore = new SqliteCortexV2OperationalStore(
                             adoptionLowLevel, "pack-route-test"))
            {
                await adoptionLowLevel.InitializeAsync();
                await adoptionStore.ImportKnowledgeSeedsAsync(
                    [new SqliteCortexV2KnowledgeSeed(
                        predecessorSource,
                        0,
                        CortexKnowledgeSpaces.Shared,
                        "Knowledge.Cerebral.Guide.ToolRouting",
                        CortexKnowledgeKindV2.Tenet,
                        CortexKnowledgeTierV2.Explicit,
                        "Local predecessor",
                        "locally created predecessor content",
                        ["generalized"],
                        CortexProvenanceSourceV2.User,
                        "local/predecessor")],
                    "os_pack_route_test",
                    time.GetUtcNow(),
                    CancellationToken.None);
            }
            var adoptionManifest = Path.Combine(root, "cerebral-adoption.v1.json");
            await File.WriteAllTextAsync(adoptionManifest, JsonSerializer.Serialize(new
            {
                schema = "pegasus.cortex.behavior-pack-adoption/1.0",
                packId = "pegasus-cerebral-guides-v1",
                packRevision = CerebralGuidePackRevision,
                priorSourceId = predecessorSource,
                priorSource = "user",
                records = new[]
                {
                    new { recordId = "Knowledge.Cerebral.Guide.ToolRouting", expectedRevision = 1 }
                }
            }));
            var adopted = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                packRoot,
                adoptionDatabase,
                CortexKnowledgeSpaces.Shared,
                "os_pack_route_test",
                time,
                CancellationToken.None,
                adoptionManifest);
            using (var json = JsonDocument.Parse(adopted.Json))
            await using (var store = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(adoptionDatabase), time))
            {
                var revised = await store.ReadHeadAsync(
                    CortexKnowledgeSpaces.Shared, "knowledge", "Knowledge.Cerebral.Guide.ToolRouting");
                var counters = await store.GetCatalogCountersAsync();
                LocalAssert.True(
                    adopted.ExitCode == 0 && json.RootElement.GetProperty("adopted").GetInt32() == 1 &&
                    revised is { Revision: 2 } && counters.HeadRows == 17 && counters.RevisionRows == 18,
                    $"The explicit local-head adoption did not preserve history atomically: {adopted.Json}");
            }

            var staleManifest = Path.Combine(root, "cerebral-adoption-stale.v1.json");
            await File.WriteAllTextAsync(staleManifest, (await File.ReadAllTextAsync(adoptionManifest))
                .Replace("\"expectedRevision\":1", "\"expectedRevision\":2", StringComparison.Ordinal));
            var rejectedAdoption = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                packRoot,
                adoptionDatabase,
                CortexKnowledgeSpaces.Shared,
                "os_pack_route_test",
                time,
                CancellationToken.None,
                staleManifest);
            LocalAssert.True(rejectedAdoption.ExitCode == 1,
                "A stale adoption manifest was accepted after the predecessor had already been adopted.");

            var conflictDatabase = Path.Combine(root, "conflict", "cortex-v2.db");
            var conflictLowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(conflictDatabase), time);
            await using (var conflictStore = new SqliteCortexV2OperationalStore(
                             conflictLowLevel, "pack-route-test"))
            {
                await conflictLowLevel.InitializeAsync();
                await conflictStore.ImportKnowledgeSeedsAsync(
                    [new SqliteCortexV2KnowledgeSeed(
                        "conflict-fixture",
                        1,
                        CortexKnowledgeSpaces.Shared,
                        "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions",
                        CortexKnowledgeKindV2.Tenet,
                        CortexKnowledgeTierV2.Explicit,
                        "Divergent fixture",
                        "divergent exact content",
                        ["generalized"],
                        CortexProvenanceSourceV2.Agent,
                        "fixture/conflict")],
                    "os_pack_route_test",
                    time.GetUtcNow(),
                    CancellationToken.None);
            }
            var conflict = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                packRoot,
                conflictDatabase,
                CortexKnowledgeSpaces.Shared,
                "os_pack_route_test",
                time,
                CancellationToken.None);
            await using (var store = new SqliteCortexV2Store(
                             new SqliteCortexV2StoreOptions(conflictDatabase), time))
            {
                var missing = await store.ReadHeadAsync(
                    CortexKnowledgeSpaces.Shared,
                    "knowledge",
                    "Knowledge.Cerebral.Guide");
                var divergent = await store.ReadHeadAsync(
                    CortexKnowledgeSpaces.Shared,
                    "knowledge",
                    "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions");
                var counters = await store.GetCatalogCountersAsync();
                LocalAssert.True(
                    conflict.ExitCode == 1 &&
                    missing is null &&
                    divergent is not null &&
                    Encoding.UTF8.GetString(divergent.ContentUtf8) == "divergent exact content" &&
                    counters.HeadRows == 1 && counters.RevisionRows == 1,
                    "A divergent single-pack head caused a partial import.");
            }

            var wrongNamespaceDatabase = Path.Combine(root, "wrong-space", "cortex-v2.db");
            var wrongNamespace = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                packRoot,
                wrongNamespaceDatabase,
                CortexKnowledgeSpaces.User,
                "os_pack_route_test",
                time,
                CancellationToken.None);
            LocalAssert.True(
                wrongNamespace.ExitCode == 1 && !File.Exists(wrongNamespaceDatabase),
                "The product route admitted a portable pack outside knowledge.shared.");

            var unsafePack = Path.Combine(root, "unsafe-pack");
            Directory.CreateDirectory(unsafePack);
            File.WriteAllText(
                Path.Combine(unsafePack, "behavior-pack.json"),
                """
                {"schema":"pegasus.cortex.behavior-pack/2.0","packId":"unsafe-pack",
                 "description":"Unsafe task-bearing fixture.","includesTasks":true,
                 "includesMachineHistory":false,"records":[]}
                """,
                new UTF8Encoding(false));
            var unsafeDatabase = Path.Combine(root, "unsafe", "cortex-v2.db");
            var unsafeResult = await CortexLocalProductCommands.ImportBehaviorPackAsync(
                unsafePack,
                unsafeDatabase,
                CortexKnowledgeSpaces.Shared,
                "os_pack_route_test",
                time,
                CancellationToken.None);
            LocalAssert.True(
                unsafeResult.ExitCode == 1 && !File.Exists(unsafeDatabase),
                "A task-bearing behavior pack reached the product store.");

            var productProgram = await File.ReadAllTextAsync(Path.Combine(
                sourceRoot, "src", "PegasusCortex.App", "Program.cs"));
            LocalAssert.True(
                productProgram.Contains(
                    "[\"import-behavior-pack\", \"--pack-root\", var packRoot, \"--namespace\", var targetNamespace]",
                    StringComparison.Ordinal) &&
                !productProgram.Contains("import-behavior-pack --database", StringComparison.Ordinal),
                "The product executable does not expose the fixed pack-root and namespace command shape.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task SharedCorpusRetrievalIsRelevantBoundedAndDeterministicAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cx-geometric-corpus", Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "cortex-v2.db");
        try
        {
            var sourceRoot = FindSourceRoot();
            var corpus = Path.Combine(root, "corpus");
            await StageSourceCorpusAsync(sourceRoot, corpus);
            var import = await CortexLocalProductCommands.ImportStarterCorpusAsync(
                corpus,
                database,
                onlyIfEmpty: true,
                "os_geometric_corpus_test",
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.Equal(0, import.ExitCode, $"The source starter corpus did not seed: {import.Json}");

            var lowLevel = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(database),
                TimeProvider.System);
            await using var operational = new SqliteCortexV2OperationalStore(
                lowLevel,
                "geometric-corpus-test");
            await lowLevel.InitializeAsync(CancellationToken.None);
            var app = new CortexV2Application(
                operational,
                "os_geometric_corpus_test",
                TimeProvider.System);

            var fixtures = new[]
            {
                new
                {
                    Query = "geometric vector semantic space overlap inclusion exclusion",
                    Expected = "tenet.bounded-geometric-knowledge-retrieval.20260828"
                },
                new
                {
                    Query = "signing private keys sign executable manifest trust installation",
                    Expected = "tenet.disciplines-release-deployment-and-rollback"
                },
                new
                {
                    Query = "cross-agent continuity exact task checkpoint handoff",
                    Expected = "tenet.meaningful-cross-agent-continuity.20260816"
                },
                new
                {
                    Query = "ProcessPath apphost deps json executable runtime",
                    Expected = "workaround.dotnet-processpath-apphost.20260813"
                },
                new
                {
                    Query = "sqlite locking bounded transaction contention",
                    Expected = "tenet.specializations-languages-sql"
                },
                new
                {
                    Query = "Cerebral Python virtual environment interactive persistent session reconnect",
                    Expected = "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions"
                },
                new
                {
                    Query = "Windows named pipe ACL peer identity impersonation local protocol",
                    Expected = "Knowledge.Windows.IPC.NamedPipes"
                },
                new
                {
                    Query = "Windows Robocopy directory transfer retries progress summary",
                    Expected = "Knowledge.Windows.FileTransfer.Robocopy"
                },
                new
                {
                    Query = "coordinator helper bounded work contract selected evidence token budget recipes",
                    Expected = "Knowledge.Cerebral.Guide.BoundedWork"
                },
                new
                {
                    Query = "Cerebral plugin authoring native load runtime acceptance source-generation build admission",
                    Expected = "Knowledge.Cerebral.Guide.PluginAuthoring"
                }
            };

            foreach (var fixture in fixtures)
            {
                var first = await app.SearchAsync(new(
                    [CortexKnowledgeSpaces.Shared],
                    fixture.Query,
                    Kinds: null,
                    AfterCursor: null,
                    Limit: 5,
                    MaxUtf8Bytes: 4096,
                    Format: CortexResponseFormatV2.Compact), default).ConfigureAwait(false);
                var replay = await app.SearchAsync(new(
                    [CortexKnowledgeSpaces.Shared],
                    fixture.Query,
                    Kinds: null,
                    AfterCursor: null,
                    Limit: 5,
                    MaxUtf8Bytes: 4096,
                    Format: CortexResponseFormatV2.Compact), default).ConfigureAwait(false);
                LocalAssert.True(
                    first.EffectiveNamespaces.SequenceEqual([CortexKnowledgeSpaces.Shared]) &&
                    first.Items.Count > 0 &&
                    first.Items[0].RecordId == fixture.Expected &&
                    first.Items.Count <= 5 &&
                    first.Items.Select(value => (value.Namespace, value.RecordId, value.Revision))
                        .SequenceEqual(replay.Items.Select(value => (value.Namespace, value.RecordId, value.Revision))) &&
                    JsonSerializer.SerializeToUtf8Bytes(first, CortexV2Json.Options).Length <= 4096,
                    $"The bounded shared-space query was not relevant and deterministic: {fixture.Query}");
            }

            var exact = await app.ReadAsync(new(
                CortexKnowledgeSpaces.Shared,
                CortexRecordTypeV2.Knowledge,
                "tenet.bounded-geometric-knowledge-retrieval.20260828",
                Revision: null), default).ConfigureAwait(false);
            LocalAssert.True(
                exact.Record is CortexKnowledgeV2
                {
                    Revision: 1,
                    Content: var content
                } &&
                content.Contains("deterministic sparse retrieval", StringComparison.Ordinal) &&
                content.Contains("Excluded spaces must contribute no direct hit", StringComparison.Ordinal),
                "The compact preview could not be hydrated into the exact geometric-space contract.");

            var guideRoot = Path.Combine(sourceRoot, "integrations", "cerebral-guides-v1");
            var guideManifest = await CortexFileBehaviorPackManifestV2.ReadAsync(guideRoot);
            var guideRecords = await guideManifest.LoadRecordsAsync(guideRoot);
            foreach (var id in new[]
                     {
                         "Knowledge.Cerebral.Guide.BoundedWork",
                         "Knowledge.Cerebral.Guide.PluginAuthoring"
                     })
            {
                var source = guideRecords.Single(value => value.Descriptor.RecordId == id);
                var read = await app.ReadAsync(new(
                    CortexKnowledgeSpaces.Shared, CortexRecordTypeV2.Knowledge, id, Revision: 1), default);
                LocalAssert.True(
                    read.EffectiveNamespaces.SequenceEqual([CortexKnowledgeSpaces.Shared]) &&
                    read.Record is CortexKnowledgeV2 { Revision: 1, Content: var body } &&
                    body == source.Content,
                    $"The selected guide did not round-trip exactly through the shared store: {id}");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task CerebralGuidesAreBoundedAndContractAccurateAsync()
    {
        var packRoot = Path.Combine(FindSourceRoot(), "integrations", "cerebral-guides-v1");
        var manifest = await CortexFileBehaviorPackManifestV2.ReadAsync(packRoot);
        var records = await manifest.LoadRecordsAsync(packRoot);
        LocalAssert.True(manifest.PackRevision == CerebralGuidePackRevision && records.Count == 17 &&
                         !manifest.IncludesTasks && !manifest.IncludesMachineHistory,
            "The portable Cerebral pack generation or history exclusion changed.");

        foreach (var record in records)
        {
            var budget = Regex.Match(record.Content, @"(?m)^Rendered budget: ([0-9]+) UTF-8 bytes\r?$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var version = Regex.Match(record.Content, @"(?m)^Version: ([0-9]+\.[0-9]+\.[0-9]+)\r?$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            LocalAssert.True(budget.Success && version.Success,
                $"Guide metadata is missing: {record.Descriptor.RecordId}");
            var limit = int.Parse(budget.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            LocalAssert.True(limit is >= 1024 and <= 5120 &&
                             Encoding.UTF8.GetByteCount(record.Content) <= limit &&
                             record.Content.Split('\n').Any(line =>
                                 line.TrimEnd('\r') == $"Object: {record.Descriptor.RecordId}") &&
                             record.Descriptor.Tags.Contains($"guide-version:{version.Groups[1].Value}", StringComparer.Ordinal) &&
                             record.Descriptor.Tags.Contains("generalized", StringComparer.Ordinal),
                $"A guide exceeded its UTF-8 budget or disagrees with its descriptor: {record.Descriptor.RecordId}");
            foreach (var privateMarker in new[] { @"C:\CodexProjects", "Bench Note", "Desk Glow", "GB10", "Trail Note" })
                LocalAssert.True(!record.Content.Contains(privateMarker, StringComparison.OrdinalIgnoreCase),
                    $"A portable guide contains machine/trial history: {record.Descriptor.RecordId}");
        }

        var work = records.Single(value => value.Descriptor.RecordId == "Knowledge.Cerebral.Guide.BoundedWork");
        var plugin = records.Single(value => value.Descriptor.RecordId == "Knowledge.Cerebral.Guide.PluginAuthoring");
        foreach (var clause in new[]
                 {
                     "not a new wire schema", "adds no scheduler", "missing/stale/unavailable",
                     "Never silently substitute a newer revision", "not the parent transcript",
                     "recipe is absent", "not blind retry", "actual injected tokens"
                 })
            LocalAssert.True(work.Content.Contains(clause, StringComparison.Ordinal),
                $"The compact work guide lost a boundary: {clause}");
        foreach (var clause in new[]
                 {
                     "not compiler execution", "fresh preimage", "replacement needs owner approval",
                     "successful native load", "native load, controls, persistence and visual acceptance",
                     "prove only their reported stage"
                 })
            LocalAssert.True(plugin.Content.Contains(clause, StringComparison.Ordinal),
                $"The plugin guide lost an acceptance/recovery distinction: {clause}");
        foreach (var clause in new[]
                 {
                     "plugin.projects {}", "complete, untruncated inventory", "unrelated examples",
                     "plugin.scaffold {pluginId,name,targetDirectory,component}", "Do not mkdir first",
                     "plugin.contract {topic}", "Fetch only needed", "exact copied SDK",
                     "DesktopPluginBase", "DesktopPluginFiles", "The model implements source",
                     "program.source.generate {path,expectedSha256,instructions,referencePaths?}",
                     "cannot perform nested lookup", "registered process.run", "exact Release build",
                     "plugin.verify {root:<exact containing PLUGIN.md>}",
                     "plugin.present {path:<exact returned built desktop-plugin.json>,expectedPackageFingerprint?:<verify fingerprint>}",
                     "64 lowercase hex", "manifestPath is a result coordinate, not input",
                     "queues owner review only", "Service staging/activation", "nondefault save/reload/reopen/restart"
                 })
            LocalAssert.True(plugin.Content.Contains(clause, StringComparison.Ordinal),
                $"The plugin guide lost a typed source/owner boundary: {clause}");
        foreach (var clause in new[]
                 {
                     "optional expectedTexts before delivery", "complete before/after evidence",
                     "exactly one visible enabled matching button", "not persistence",
                     "save known nonempty A while current", "confirm the actual save result",
                     "distinct unsaved B", "invoke Reload with expectedTexts:[A]",
                     "Save/reopen does not test Reload", "not a persistence failure",
                     "actual editable fields before and after a new instance",
                     "Unknown: inspect the actual receipt, never replay a mutation"
                 })
            LocalAssert.True(plugin.Content.Contains(clause, StringComparison.Ordinal),
                $"The plugin guide lost a native-control assertion distinction: {clause}");
        var routing = records.Single(value => value.Descriptor.RecordId == "Knowledge.Cerebral.Guide.ToolRouting");
        var recovery = records.Single(value => value.Descriptor.RecordId == "Knowledge.Cerebral.Guide.RecoveryCompaction");
        foreach (var clause in new[]
                 {
                     "private versioned owner-review source coordinate", "old build credit or wider grants",
                     "its own successful exact-root inventory", "plugin.projects {root:<host-supplied root>}",
                     "Older admitted QA may refresh the real catalog", "Source builds do not replace the immutable installed plugin",
                     "separate owner approval before control tests"
                 })
            LocalAssert.True(plugin.Content.Contains(clause, StringComparison.Ordinal),
                $"The plugin guide lost a QA source/generation boundary: {clause}");
        foreach (var clause in new[]
                 {
                     "pause/drain to a confirmed effect checkpoint", "engine/SDK pins and saved drafts",
                     "Unknown effects require reconciliation, not replay", "its own successful exact-root plugin.projects inventory",
                     "older admitted QA can refresh a real catalog", "separate owner approval before control tests"
                 })
            LocalAssert.True(recovery.Content.Contains(clause, StringComparison.Ordinal),
                $"The recovery guide lost a source-continuation boundary: {clause}");
        foreach (var clause in new[]
                 {
                     "Cache the admitted native catalog", "only allowed mcp.<provider>.cortex_search/read",
                     "explicit namespaces", "search hint", "not a special guidance operation/resource",
                     "result.record", "Fetch only needed plugin.contract topics",
                     "retained error diagnostics only when needed", "never inject whole catalogs or logs each turn"
                 })
            LocalAssert.True(routing.Content.Contains(clause, StringComparison.Ordinal),
                $"The routing guide lost a bounded lookup distinction: {clause}");
        foreach (var clause in new[]
                 {
                     "Cerebral uses Cortex's native pipe; Codex uses MCP",
                     "interface.present queues file/folder/application review",
                     "native plugins use plugin.present with the exact verified built manifest",
                     "pending review, not opening/installation/rendering",
                     "Frozen descriptors must match current contracts",
                     "upgrades can make old continuations ineligible"
                 })
            LocalAssert.True(routing.Content.Contains(clause, StringComparison.Ordinal),
                $"The routing guide lost a presentation/continuation boundary: {clause}");
        LocalAssert.True(work.Descriptor.Source == CortexProvenanceSourceV2.Agent &&
                         plugin.Descriptor.Source == CortexProvenanceSourceV2.Agent &&
                         recovery.Descriptor.Source == CortexProvenanceSourceV2.Agent,
            "Agent-authored guidance was misattributed as direct user evidence.");

        using var adoption = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(packRoot, "adoption.codex-task-019ff269.v1.json")));
        var adoptedIds = adoption.RootElement.GetProperty("records").EnumerateArray()
            .Select(value => value.GetProperty("recordId").GetString()).ToArray();
        LocalAssert.True(adoption.RootElement.GetProperty("packRevision").GetInt32() == manifest.PackRevision &&
                         adoption.RootElement.GetProperty("packId").GetString() == manifest.PackId &&
                         adoptedIds.Length == 7 &&
                         adoptedIds.All(id => manifest.Records.Any(record => record.RecordId == id)) &&
                         !adoptedIds.Contains(work.Descriptor.RecordId, StringComparer.Ordinal),
            "The optional predecessor map is stale or invents a predecessor for the new guide.");
    }

    internal static async Task StageSourceCorpusAsync(string sourceRoot, string destination,
        CancellationToken cancellationToken = default)
    {
        var integrations = Path.Combine(sourceRoot, "integrations");
        var manifest = await CortexFileStarterCorpusManifestV1.ReadAsync(integrations, cancellationToken);
        var sourceDirectories = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["general-knowledge"] = "general-knowledge-v1",
            ["generalized-programming-tenets"] = "file-cortex-v2",
            ["cerebral-guides"] = "cerebral-guides-v1",
            ["windows-operational-knowledge"] = "windows-operational-knowledge-v1"
        };
        var copies = new List<(string Source, string Target)>();
        Plan(Path.Combine(integrations, "starter-corpus.json"), Path.Combine(destination, "starter-corpus.json"));
        foreach (var pack in manifest.Packs)
        {
            if (!sourceDirectories.TryGetValue(pack.Path, out var sourceDirectory))
                throw new InvalidDataException("A starter source mapping needs explicit review.");
            var source = Path.Combine(integrations, sourceDirectory);
            var target = Path.Combine(destination, pack.Path);
            var behavior = await CortexFileBehaviorPackManifestV2.ReadAsync(source, cancellationToken);
            var records = await behavior.LoadRecordsAsync(source, cancellationToken);
            Plan(Path.Combine(source, "behavior-pack.json"), Path.Combine(target, "behavior-pack.json"));
            foreach (var record in records)
            {
                var relative = record.Descriptor.ContentPath.Replace('/', Path.DirectorySeparatorChar);
                Plan(Path.Combine(source, relative), Path.Combine(target, relative));
            }
        }
        if (copies.Select(copy => copy.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copies.Count)
            throw new InvalidDataException("Starter staging has duplicate targets.");
        // Stage only the manifest and referenced eligible bodies. Reference-instance
        // histories and unrelated files are neither product seeds nor fixture inputs.
        foreach (var copy in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(copy.Target)!);
            File.Copy(copy.Source, copy.Target, overwrite: false);
        }

        void Plan(string source, string target)
        {
            var canonicalSource = Path.GetFullPath(source);
            var canonicalTarget = Path.GetFullPath(target);
            if (canonicalSource.Length > 240 || canonicalTarget.Length > 240)
                throw new InvalidDataException("Starter staging exceeds the Windows path budget.");
            CortexProductLayout.RejectExistingReparseComponents(canonicalSource);
            CortexProductLayout.RejectExistingReparseComponents(canonicalTarget);
            if ((File.GetAttributes(canonicalSource) & FileAttributes.Directory) != 0 ||
                File.Exists(canonicalTarget) || Directory.Exists(canonicalTarget))
                throw new InvalidDataException("Starter staging requires ordinary sources and absent destinations.");
            copies.Add((canonicalSource, canonicalTarget));
        }
    }

    private static void WriteCorpus(string root, string generalContent, bool includeLateRecord)
    {
        Directory.CreateDirectory(Path.Combine(root, "general", "records"));
        Directory.CreateDirectory(Path.Combine(root, "programming", "records"));
        File.WriteAllText(Path.Combine(root, "starter-corpus.json"), """
            {
              "schema":"pegasus.cortex.starter-corpus/1.0",
              "corpusId":"starter-fixture",
              "description":"Deterministic starter fixture.",
              "packs":[
                {"namespace":"knowledge.shared","path":"general"},
                {"namespace":"knowledge.shared","path":"programming"}
              ]
            }
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "general", "records", "general.md"), generalContent, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "general", "behavior-pack.json"), """
            {
              "schema":"pegasus.cortex.behavior-pack/2.0",
              "packId":"general-fixture",
              "description":"General fixture.",
              "includesTasks":false,
              "includesMachineHistory":false,
              "records":[{
                "recordId":"tenet.general.fixture","kind":"tenet","tier":"explicit",
                "title":"General fixture","tags":["generalized","general-knowledge"],
                "source":"user","contentPath":"records/general.md","sourceRelativePath":"fixture/general"
              }]
            }
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "programming", "records", "programming.md"), "programming exact", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "programming", "records", "late.md"), "late exact", new UTF8Encoding(false));
        var late = includeLateRecord
            ? ",{\"recordId\":\"workaround.late.fixture\",\"kind\":\"workaround\",\"tier\":\"verified\"," +
              "\"title\":\"Late fixture\",\"tags\":[\"programming\",\"workaround\"]," +
              "\"source\":\"agent\",\"contentPath\":\"records/late.md\"," +
              "\"sourceRelativePath\":\"fixture/late\"}"
            : string.Empty;
        File.WriteAllText(Path.Combine(root, "programming", "behavior-pack.json"), $$"""
            {
              "schema":"pegasus.cortex.behavior-pack/2.0",
              "packId":"programming-fixture",
              "description":"Programming fixture.",
              "includesTasks":false,
              "includesMachineHistory":false,
              "records":[{
                "recordId":"workaround.programming.fixture","kind":"workaround","tier":"verified",
                "title":"Programming fixture","tags":["programming","workaround"],
                "source":"agent","contentPath":"records/programming.md","sourceRelativePath":"fixture/programming"
              }{{late}}]
            }
            """, new UTF8Encoding(false));
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "START_HERE.md")) &&
                File.Exists(Path.Combine(current.FullName, "PegasusCortex.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The Pegasus Cortex source root was not found.");
    }

    private sealed class FixedSeedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
