using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Offline;
using Pegasus.Cortex.Contracts.V2;

internal static class PrivateReadCacheCommandTests
{
    public static Task FreshPreviewRejectsForeignScopeAndOmitsCursorsAsync()
    {
        var request = CortexPrivateReadCacheArguments.Parse(PrivateReadCacheArgumentsTests.Arguments(
            "cache-read", Path.Combine(Path.GetTempPath(), "private-cache-projection-fixture"))).Request;
        var typed = new CortexStartResultV2(Enum.GetValues<CortexContextDispositionV2>()[0], request.ReadNamespaces,
            "DO_NOT_PERSIST_TYPED_CURSOR",
            [new CortexContextItemV2("knowledge.shared", "knowledge", "typed-record", 7, "typed title", "typed excerpt",
                CortexKnowledgeKindV2.Observation, CortexKnowledgeTierV2.Verified, null, 1)], false,
            new CortexTaskSummaryV2(request.WriteNamespace, request.TaskId, 2, CortexTaskStatusV2.Open,
                "typed task title", "typed task excerpt", null, null, null));
        var typedWire = JsonSerializer.SerializeToElement(new { isError = false, structuredContent = typed }, CortexV2Json.Options);
        var typedPreview = CortexPrivateReadCacheCommands.ProjectPreview(typedWire, request);
        Check(typedPreview.Contains("typed excerpt", StringComparison.Ordinal) &&
            typedPreview.Contains("typed task title", StringComparison.Ordinal) &&
            !typedPreview.Contains("DO_NOT_PERSIST_TYPED_CURSOR", StringComparison.Ordinal),
            "The actual typed Cortex start wire contract did not project as a passive preview.");
        var tool = JsonNode.Parse("""
            {"isError":false,"structuredContent":{"effectiveNamespaces":["project.private-cache-fixture","knowledge.shared"],
             "contextCursor":"DO_NOT_PERSIST_CURSOR","currentTask":{"namespace":"project.private-cache-fixture",
             "taskId":"codex-private-cache-fixture","title":"fixture task","content":"NON_PREVIEW_BODY"},
             "items":[{"namespace":"knowledge.shared","recordType":"knowledge","recordId":"fixture-record",
             "revision":7,"title":"fixture preview","excerpt":"bounded selected preview","content":"NON_PREVIEW_BODY"}]}}
            """)!;
        string Project(JsonNode value)
        {
            using var parsed = JsonDocument.Parse(value.ToJsonString());
            return CortexPrivateReadCacheCommands.ProjectPreview(parsed.RootElement, request);
        }
        var preview = Project(tool);
        Check(preview.Contains("bounded selected preview", StringComparison.Ordinal) &&
            preview.Contains("Always stale", StringComparison.Ordinal) &&
            !preview.Contains("DO_NOT_PERSIST_CURSOR", StringComparison.Ordinal) &&
            !preview.Contains("NON_PREVIEW_BODY", StringComparison.Ordinal), "The projection retained non-preview authority or cursor data.");
        var wrongTask = tool.DeepClone();
        wrongTask["structuredContent"]!["currentTask"]!["taskId"] = "another-task";
        RejectProvider(() => Project(wrongTask));
        var wrongScope = tool.DeepClone();
        wrongScope["structuredContent"]!["items"]![0]!["namespace"] = "project.foreign";
        RejectProvider(() => Project(wrongScope));
        var reorderedScope = tool.DeepClone();
        reorderedScope["structuredContent"]!["effectiveNamespaces"] = new JsonArray("knowledge.shared", "project.private-cache-fixture");
        RejectProvider(() => Project(reorderedScope));
        var rejected = tool.DeepClone(); rejected["isError"] = true;
        RejectProvider(() => Project(rejected));
        var wide = tool.DeepClone(); wide["structuredContent"]!["items"]![0]!["excerpt"] = new string('\u754c', 5000);
        Check(new UTF8Encoding(false, true).GetByteCount(Project(wide)) <= 4096,
            "The fresh callback exceeded its Unicode-safe input bound.");
        return Task.CompletedTask;
    }

    public static async Task CommandsArePassiveFreshAndIsolatedAsync()
    {
        var fixtureParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-private-read-cache-command-tests"));
        var fixture = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(fixture, "content");
        var privateParent = Path.Combine(fixture, "private");
        Directory.CreateDirectory(Path.Combine(contentRoot, ".cortex"));
        Directory.CreateDirectory(privateParent);
        CortexLocalRegistryAclPolicy.ProtectDirectory(privateParent);
        var manifestPath = Path.Combine(contentRoot, ".cortex", "project.json");
        var manifest = """{"schema":"pegasus.cortex.file-project/2.0","projectId":"private-cache-fixture","writeNamespace":"project.private-cache-fixture","readNamespaces":["project.private-cache-fixture","knowledge.shared"]}""";
        await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false));
        var originalManifest = await File.ReadAllBytesAsync(manifestPath);
        try
        {
            var cache = new CortexPrivateReadCache(privateParent);
            var reads = 0;
            ValueTask<string> Fresh(CortexPrivateReadCacheRequest request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); reads++;
                Check(request.TaskId == "codex-private-cache-fixture", "The provider received a different task.");
                return ValueTask.FromResult("fresh passive preview " + new string('\u754c', 1000));
            }
            ValueTask<(int ExitCode, string Json)> Run(string command, string? generation = null) =>
                CortexPrivateReadCacheCommands.RunAsync(PrivateReadCacheArgumentsTests.Arguments(command, contentRoot, generation),
                    cache, Fresh);

            _ = await Run("cache-status");
            _ = await Run("cache-read");
            Check(reads == 0 && Directory.GetFileSystemEntries(privateParent).Length == 0,
                "Status/read created private state or contacted the provider.");
            Check((await Run("cache-init")).ExitCode == 0 && reads == 0, "Init contacted the provider or failed.");
            var first = await Run("cache-refresh", "none");
            Check(first.ExitCode == 0 && reads == 1, "Initial refresh did not perform one fresh read.");
            using var firstJson = JsonDocument.Parse(first.Json);
            var generation = firstJson.RootElement.GetProperty("snapshotGeneration").GetString()!;
            Check(generation.Length == 64 && firstJson.RootElement.GetProperty("snapshot").ValueKind == JsonValueKind.Null,
                "Refresh expanded the snapshot or omitted its generation.");
            var read = await Run("cache-read");
            Check(read.ExitCode == 0 && reads == 1, "Cache read contacted the provider.");
            using (var readJson = JsonDocument.Parse(read.Json))
            {
                var snapshot = readJson.RootElement;
                Check(snapshot.GetProperty("state").GetString() == "passive-stale" &&
                    !snapshot.GetProperty("authoritative").GetBoolean() &&
                    snapshot.GetProperty("coreStoreIdentity").ValueKind == JsonValueKind.Null &&
                    !snapshot.GetProperty("loadedRevisionProof").GetBoolean() &&
                    snapshot.GetProperty("truncated").GetBoolean() &&
                    Encoding.UTF8.GetByteCount(read.Json) <= 4096 && !snapshot.TryGetProperty("snapshot", out _),
                    "The snapshot lost its passive/unknown identity or total UTF-8 bound.");
            }
            var second = await Run("cache-refresh", generation);
            Check(second.ExitCode == 0 && reads == 2, "An unchanged preview skipped the fresh provider read.");
            using var secondJson = JsonDocument.Parse(second.Json);
            var secondGeneration = secondJson.RootElement.GetProperty("snapshotGeneration").GetString()!;
            Check(secondGeneration != generation, "A refresh did not advance its snapshot generation.");
            var failed = await CortexPrivateReadCacheCommands.RunAsync(
                PrivateReadCacheArgumentsTests.Arguments("cache-refresh", contentRoot, secondGeneration), cache,
                (_, _) => ValueTask.FromException<string>(new IOException("fixture provider unavailable")));
            Check(failed.ExitCode != 0, "A failed fresh read was reported as successful.");
            var securityRejected = await CortexPrivateReadCacheCommands.RunAsync(
                PrivateReadCacheArgumentsTests.Arguments("cache-refresh", contentRoot, secondGeneration), cache,
                (_, _) => ValueTask.FromException<string>(new UnauthorizedAccessException("fixture peer rejected")));
            using (var rejectedJson = JsonDocument.Parse(securityRejected.Json))
                Check(securityRejected.ExitCode != 0 && rejectedJson.RootElement.GetProperty("state").GetString() == "security-rejected",
                    "Peer rejection was reported as provider health or availability.");
            using (var retained = JsonDocument.Parse((await Run("cache-read")).Json))
                Check(retained.RootElement.GetProperty("generation").GetString() == secondGeneration,
                    "A failed provider changed the retained snapshot.");
            Check((await File.ReadAllBytesAsync(manifestPath)).SequenceEqual(originalManifest) &&
                Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories).SequenceEqual(new[] { manifestPath }),
                "Cache commands changed shared project files or created a project HOT/spool.");
        }
        finally
        {
            // Delete only the exact disposable GUID child created by this fixture.
            if (Path.GetDirectoryName(fixture) != fixtureParent || !Guid.TryParseExact(Path.GetFileName(fixture), "N", out _))
                throw new InvalidOperationException("Unexpected disposable fixture path.");
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static void Check(bool condition, string message) => PrivateReadCacheArgumentsTests.Check(condition, message);
    private static void RejectProvider(Action action)
    {
        try { action(); }
        catch (Exception error) when (error.GetType().Name == "ProviderException") { return; }
        throw new InvalidOperationException("A foreign or rejected provider response was accepted.");
    }
}
