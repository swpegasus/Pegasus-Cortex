using Pegasus.Cortex.Contracts.Assets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Persistence.File.Assets;
using Pegasus.Cortex.Product;

internal static class AssetV2Tests
{
    internal static string BoundedFixtureMetric { get; private set; } = "";
    public static async Task ExactFilesAndMetadataAsync()
    {
        using var fixture = new Fixture();
        LocalAssert.True(CortexProductIdentity.IsReleaseVersion("2.2026.10.07.15.19.A") &&
            CortexProductIdentity.IsReleaseVersion("1.2026.09.29.22.18.A") &&
            !CortexProductIdentity.IsReleaseVersion("3.2026.10.07.15.19.A"), "Version2 admission lost bounded legacy upgrade compatibility.");
        var source = Path.Combine(fixture.Root, "image.dat");
        await File.WriteAllBytesAsync(source, [0, 1, 2, 3, 255]);
        const string parameters = "{\r\n\t\"prompt\":\"A cat 🐈\",\"seed\":42,\"unknownSetting\":{\"a\":true},\"escaped\":\"\\u0002\"\r\n}";
        var spec = new CortexAssetSpecV2("knowledge.shared", "icons.cat", "Cat", "A cat, or user-selected go icon", "application/octet-stream",
            Tags: "cat go good", Text: "🐈", Parameters: parameters, Pack: "lab\\pack");
        var first = await fixture.Store.ImportAsync(source, spec);
        var repeated = await fixture.Store.ImportAsync(source, spec);
        LocalAssert.True(first.Ref == repeated.Ref, "Identical import was not idempotent.");
        LocalAssert.True(fixture.Store.Read(spec.Namespace, spec.Id, first.Ref).Metadata.Parameters == parameters,
            "Opaque parameters JSON was rewritten or lost.");
        _ = await fixture.Store.ImportAsync(source, spec with { Namespace = "project.fixture", Id = "pictures.cat" });
        LocalAssert.True(Directory.EnumerateFiles(Path.Combine(fixture.Store.Root, "objects"))
            .Count(path => !path.EndsWith(".chunks", StringComparison.Ordinal)) == 1, "Duplicate bytes were retained twice.");
        await File.WriteAllBytesAsync(source, [9, 8, 7]);
        await RejectAsync(() => fixture.Store.ImportAsync(source, spec), "asset_revision_conflict");
        var newer = await fixture.Store.ImportAsync(source, spec, first.Ref);
        LocalAssert.True(newer.Ref != first.Ref && (await fixture.Store.ReadBytesAsync(spec.Namespace, spec.Id, first.Ref)).Bytes.SequenceEqual(new byte[] { 0, 1, 2, 3, 255 }),
            "An old pinned version was overwritten.");
        await RejectAsync(() => fixture.Store.ImportAsync(source, spec, first.Ref), "asset_revision_conflict");
        await RejectAsync(() => Task.FromResult(fixture.Store.Read("project.fixture", spec.Id, first.Ref)), "asset_scope_mismatch");
        var provider = new CortexAssetResourcesV2(fixture.Store, ["knowledge.shared"]);
        var found = Data(await provider.ReadAsync(CortexAssetResourcesV2.SearchUri(spec.Namespace, "cat"), default));
        LocalAssert.True(found["items"]!.AsArray().Count == 1 && !found.ToJsonString().Contains("unknownSetting", StringComparison.Ordinal),
            "Search hydrated parameters or missed semantic metadata.");
        var selected = Data(await provider.ReadAsync(CortexAssetResourcesV2.SearchUri("project.fixture", "cat"), default));
        LocalAssert.True(selected["items"]!.AsArray().Count == 1, "Catalog discovery became a namespace permission gate.");
        var personal = await fixture.Store.ImportAsync(source, spec with { Namespace = "knowledge.user", Id = "personal.cat" });
        var profile = new AssetProfile();
        var personalProvider = new CortexAssetResourcesV2(fixture.Store, ["knowledge.shared", "knowledge.user"], profile);
        LocalAssert.True(Data(await personalProvider.ReadAsync(CortexAssetResourcesV2.RecordUri(personal), default))["error"]?.GetValue<string>() == "personal_profile_not_loaded",
            "Unloaded personal assets were readable.");
        LocalAssert.True((await personalProvider.ListResourcesAsync(default)).Count == 1, "Unloaded personal assets were advertised.");
        profile.Current = new("loaded", "fixture", "loaded");
        LocalAssert.True(Data(await personalProvider.ReadAsync(CortexAssetResourcesV2.RecordUri(personal), default))["parameters"]?.GetValue<string>() == parameters,
            "Loaded personal assets did not preserve exact metadata.");
        profile.ChangeDuringRead = true;
        LocalAssert.True(Data(await personalProvider.ReadAsync(CortexAssetResourcesV2.BytesUri(personal), default))["error"]?.GetValue<string>() == "personal_profile_changed",
            "Personal asset bytes escaped after profile generation changed.");
        var raw = Data(await provider.ReadAsync(CortexAssetResourcesV2.RecordUri(newer), default));
        LocalAssert.True(raw["parameters"]?.GetValue<string>() == parameters && raw["text"]?.GetValue<string>() == "🐈", "Exact metadata did not preserve Unicode/JSON.");
        LocalAssert.True(CortexAssetResourcesV2.RecordUri(newer) == "cortex://" + newer.Ref &&
            fixture.Store.ReadReference(newer.Ref).Metadata == newer.Metadata, "Unique ID required a namespace or disk path.");
        var original = await provider.ReadAsync("cortex://" + first.Ref + "?part=bytes", default);
        LocalAssert.True(Convert.FromBase64String(original!["contents"]![0]!["blob"]!.GetValue<string>()).SequenceEqual(new byte[] { 0, 1, 2, 3, 255 }),
            "Opaque ID did not retrieve its exact old pinned original.");
        var legacy = Data(await provider.ReadAsync("pegasus-asset://v2/record/knowledge.shared/" + newer.Metadata.Id + "?ref=" + newer.Ref, default));
        LocalAssert.True(legacy["parameters"]?.GetValue<string>() == parameters, "Legacy reference compatibility broke.");
        var invalidOpaque = Data(await provider.ReadAsync("cortex://../objects/whatever", default));
        LocalAssert.True(invalidOpaque["ok"]?.GetValue<bool>() == false, "Opaque reference accepted a file path.");
        var wrongPart = Data(await provider.ReadAsync("cortex://" + first.Ref + "?part=execute", default));
        LocalAssert.True(wrongPart["error"]?.GetValue<string>() == "asset_uri_invalid", "Asset ID became command dispatch.");
        var related = Data(await provider.ReadAsync("pegasus-asset://v2/reference/knowledge.shared?ref=" + newer.Ref, default));
        LocalAssert.True(related["namespace"]?.GetValue<string>() == "knowledge.shared" &&
            related["id"]?.GetValue<string>() == spec.Id, "Related reference lost exact namespace/identity.");
        var traversal = Data(await provider.ReadAsync("pegasus-asset://v2/record/knowledge.shared/../icons.cat?ref=" + newer.Ref, default));
        LocalAssert.True(traversal["error"]?.GetValue<string>() == "asset_uri_invalid", "Resource URI silently normalized dot-segment identity.");
        await RejectAsync(() => fixture.Store.ImportAsync(source, spec with { Id = "bad/id" }), "asset_identifier_invalid");
        await RejectAsync(() => fixture.Store.ImportAsync(source, spec with { Parameters = "not JSON" }), "asset_parameters_json_invalid");
        await RejectAsync(() => fixture.Store.ImportAsync(source, spec with { Title = null! }), "asset_metadata_invalid");
        await Task.WhenAll(fixture.Store.ExposeAsync("project.fixture"), fixture.Store.ExposeAsync("project.generated"));
        LocalAssert.True(fixture.Store.ExposedNamespaces().Count == 3, "Concurrent scope updates lost an exposed namespace.");
        await RejectAsync(() => fixture.Store.ImportAsync(Path.Combine(fixture.Root, "..", "other.dat"), spec), "asset_path_invalid");
        await RejectAsync(() => fixture.Store.ImportAsync(source, spec with { Id = "wrong.pin" }, expectedContentSha256: new string('0', 64)), "asset_integrity_failed");
        var wide = new string('\\', 8190);
        var escaped = JsonSerializer.Serialize(wide);
        await RejectAsync(() => fixture.Store.ImportAsync(source, spec with { Id = "wide", Parameters = escaped }), "asset_metadata_oversized");
    }

    public static async Task BoundedSearchAndByteRangesAsync()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "large.dat");
        var buffer = Enumerable.Range(0, CortexAssetStoreV2.ChunkSize).Select(i => (byte)(i % 251)).ToArray();
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            for (var i = 0; i < 256; i++) await stream.WriteAsync(buffer);
        var first = await fixture.Store.ImportAsync(source, new("knowledge.shared", "large.image", "Large image fixture", "Binary baseline", "application/octet-stream"));
        var bytes = await fixture.Store.ReadBytesAsync("knowledge.shared", first.Metadata.Id, first.Ref, CortexAssetStoreV2.ChunkSize, 70000);
        LocalAssert.True(bytes.Bytes.Length == 70000 && bytes.TotalSize == 16 * 1024 * 1024 &&
            bytes.Bytes.AsSpan(0, buffer.Length).SequenceEqual(buffer), "Large asset range did not remain exact/bounded.");
        var before = GC.GetTotalMemory(forceFullCollection: true);
        for (var request = 0; request < 64; request++)
            _ = await fixture.Store.ReadBytesAsync("knowledge.shared", first.Metadata.Id, first.Ref,
                (request % 256) * (long)CortexAssetStoreV2.ChunkSize, 65536);
        var retainedGrowth = GC.GetTotalMemory(forceFullCollection: true) - before;
        LocalAssert.True(retainedGrowth < 8 * 1024 * 1024, "Repeated bounded reads retained unexpected managed memory.");
        await RejectAsync(() => fixture.Store.ReadBytesAsync("knowledge.shared", first.Metadata.Id, first.Ref, 1), "asset_range_invalid");
        using (var mutate = new FileStream(Path.Combine(fixture.Store.Root, "objects", first.Sha256), FileMode.Open, FileAccess.Write))
        { mutate.Position = CortexAssetStoreV2.ChunkSize; mutate.WriteByte(255); }
        await RejectAsync(() => fixture.Store.ReadBytesAsync("knowledge.shared", first.Metadata.Id, first.Ref, CortexAssetStoreV2.ChunkSize), "asset_integrity_failed");
        var provider = new CortexAssetResourcesV2(fixture.Store, ["knowledge.shared"]);
        var badUri = CortexAssetResourcesV2.BytesUri(first) + "&offset=65536";
        var error = Data(await provider.ReadAsync(badUri, default));
        LocalAssert.True(error["repairRecorded"]?.GetValue<bool>() == true, "Damaged asset was not recorded for repair.");
        _ = await provider.ReadAsync(badUri, default);
        LocalAssert.True(File.ReadAllLines(Path.Combine(fixture.Store.Root, "health.txt")).Length == 1, "Repeated repair event was not deduplicated.");
        File.Delete(Path.Combine(fixture.Store.Root, "objects", first.Sha256));
        await RejectAsync(() => fixture.Store.ReadBytesAsync("knowledge.shared", first.Metadata.Id, first.Ref), "asset_file_unavailable");

        await File.WriteAllBytesAsync(source, [4]);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 520; i++)
            await fixture.Store.ImportAsync(source, new("project.generated", "icon." + i, "Generated", "not a match", "application/octet-stream"));
        var page = fixture.Store.Search("project.generated", "absent");
        LocalAssert.True(page.Items.Count == 0 && page.Scanned == 512 && page.NextCursor is not null, "Search did not enforce scan continuation.");
        var tail = fixture.Store.Search("project.generated", "absent", cursor: page.NextCursor);
        LocalAssert.True(tail.Complete && tail.Scanned == 8, "Search continuation lost/duplicated generated heads.");
        await RejectAsync(() => Task.FromResult(fixture.Store.Search("project.generated", "changed", cursor: page.NextCursor)), "asset_cursor_stale_or_invalid");
        await fixture.Store.ImportAsync(source, new("project.generated", "icon.extra", "Extra", "not a match", "application/octet-stream"));
        await RejectAsync(() => Task.FromResult(fixture.Store.Search("project.generated", "absent", cursor: page.NextCursor)), "asset_cursor_stale_or_invalid");
        LocalAssert.True(fixture.Store.Search("project.generated", "Generated", limit: 4).Items.Count == 4, "Preview page exceeded/missed exact limit.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { fixture.Store.Search("project.generated", "", cancellationToken: cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        BoundedFixtureMetric = "fixture_heads=521 original_bytes=16777216 read_bytes=70000 repeated_reads=64 retained_growth_bytes=" + retainedGrowth +
            " scan=512 continuation=8 resident_catalog=false elapsed_ms=" + timer.ElapsedMilliseconds;
    }

    public static async Task ResourceProtocolAndFrozenIconsAsync()
    {
        using var fixture = new Fixture();
        var pack = CortexIconPackV2.Load(Path.Combine(SourceRoot(), "assets", "icons", "chat-v1"),
            "04aa6881acc2f65edab2c046409cb87f71e932081fb0ac21bea9f2f526aa0011",
            "63f60ac78551b78f4796222a68a323c56267713c94f85f91e1bc3e910a9a427d");
        var count = await CortexAssetProductCommandsV2.ImportIconsAsync(fixture.Store, pack, "knowledge.shared", default);
        LocalAssert.True(count == 14, "Core pack inventory omitted icons or licensing/catalog files.");
        _ = await CortexAssetProductCommandsV2.ImportIconsAsync(fixture.Store, pack, "knowledge.shared", default);
        var selected = fixture.Store.Search("knowledge.shared", "Succeeded").Items.Single();
        LocalAssert.True(fixture.Store.Search("knowledge.shared", "good-to-go").Items.Single().Ref == selected.Ref,
            "Common semantic icon role did not resolve to stable metadata.");
        var provider = new CortexAssetResourcesV2(fixture.Store, ["knowledge.shared"]);
        var exact = await provider.ReadAsync(CortexAssetResourcesV2.BytesUri(selected), default);
        LocalAssert.True(exact?["_meta"]?["complete"]?.GetValue<bool>() == true &&
            Convert.ToBase64String(File.ReadAllBytes(pack.GetFilePath(pack.Entries.Single(entry => entry.Id == selected.Metadata.Id).FilePath))) ==
            exact?["contents"]?[0]?["blob"]?.GetValue<string>(), "Frozen icon resource did not deliver exact admitted bytes.");
        await using var execution = new NoExecution();
        var application = new CortexUnifiedMcpApplication(new NoCortex(), execution, provider);
        LocalAssert.True(application.ListTools().Count == 9, "Assets changed the eight-plus-PAO tool catalog.");
        var messages = new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"asset-fixture\",\"version\":\"1\"}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/templates/list\"}",
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 3, method = "resources/read", @params = new { uri = CortexAssetResourcesV2.SearchUri("knowledge.shared", "Succeeded") } }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 4, method = "resources/read", @params = new { uri = CortexAssetResourcesV2.BytesUri(selected) } })
        };
        using var input = new StringReader(string.Join('\n', messages) + "\n");
        using var output = new StringWriter(); using var diagnostics = new StringWriter();
        await new McpStdioServer(application, input, output, diagnostics).RunAsync(default);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        LocalAssert.True(replies.Length == 4 && replies.All(reply => reply["error"] is null) &&
            replies[1]["result"]?["resourceTemplates"]?.AsArray().Count == 4 &&
            replies[3]["result"]?["_meta"]?["contentSha256"]?.GetValue<string>() == selected.Sha256,
            "Raw JSON-RPC did not retain pinned asset resource contract.");
        LocalAssert.True(string.IsNullOrWhiteSpace(diagnostics.ToString()), "Asset protocol emitted unexpected diagnostics.");
    }

    private static JsonObject Data(JsonObject? resource) => JsonNode.Parse(resource?["contents"]?[0]?["text"]?.GetValue<string>() ?? "{}")!.AsObject();
    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); throw new InvalidOperationException("Expected rejection: " + code); }
        catch (CortexAssetException error) when (error.Code == code) { }
    }
    private static string SourceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PegasusCortex.slnx"))) return current.FullName;
        throw new InvalidOperationException("Exact Cortex source root was not found.");
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cortex-assets-" + Guid.NewGuid().ToString("N"));
        public CortexAssetStoreV2 Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "store")); }
        public void Dispose() { CortexAssetStoreV2.CheckPath(Root); Directory.Delete(Root, true); }
    }
    private sealed class NoExecution : IOperationDispatcher
    {
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Resource retrieval dispatched execution.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class AssetProfile : Pegasus.Cortex.Core.V2.ICortexPersonalProfileProvider
    {
        public Pegasus.Cortex.Contracts.V2.CortexPersonalProfileV2 Current = new("not_loaded");
        public bool ChangeDuringRead;
        public ValueTask<Pegasus.Cortex.Contracts.V2.CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken)
        {
            var value = Current;
            if (ChangeDuringRead) { Current = new("not_loaded", Generation: "unloaded"); ChangeDuringRead = false; }
            return ValueTask.FromResult(value);
        }
    }
    private sealed class NoCortex : ICortexMcpProtocolApplication
    {
        public JsonArray ListTools() => new(Enumerable.Range(0, 8).Select(i => (JsonNode)new JsonObject { ["name"] = "semantic" + i }).ToArray());
        public ValueTask<JsonObject> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new JsonArray());
        public ValueTask<JsonObject?> ReadResourceAsync(string uri, CancellationToken cancellationToken) => ValueTask.FromResult<JsonObject?>(null);
    }
}

