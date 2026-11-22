using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Assets;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.Sqlite.Assets;

namespace Pegasus.Cortex.Persistence.Sqlite.Tests;

/// <summary>Actual URI adapter + disposable SQLite fixtures; personal availability is a deterministic fake.</summary>
[SupportedOSPlatform("windows")]
internal static class HybridAssetResourceTests
{
    public static async Task<int> RunAllAsync()
    {
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("actual-uri-metadata-search-and-original-ranges", MetadataSearchAndRangesAsync),
            ("fake-profile-before-and-after-generation-gates", ProfileGenerationGatesAsync),
            ("malformed-uris-fail-through-readonly-wrapper", MalformedUrisAsync)
        };
        foreach (var test in cases)
        {
            await test.Run();
            Console.WriteLine("PASS hybrid-resource." + test.Name);
        }
        return cases.Length;
    }

    private static async Task MetadataSearchAndRangesAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
            new CortexHybridAssetOptionsV1(InlineCutoffBytes: 64, MaximumAssetBytes: 262144));
        var bytes = HybridAssetFixture.Bytes(65537, 3);
        var asset = await ImportAsync(store, bytes, HybridAssetFixture.Spec("resource-large"));
        _ = await ImportAsync(store, HybridAssetFixture.Bytes(32, 4), HybridAssetFixture.Spec("resource-inline"));
        var resources = new CortexAssetResourcesV2(store, new[] { "test.hybrid" });
        var list = await resources.ListResourcesAsync(CancellationToken.None);
        HybridAssetFixture.Require(list.Count == 1 && list[0]!["uri"]!.GetValue<string>() == "pegasus-asset://v2/search/test.hybrid?q=&limit=4", "Resource discovery URI changed.");
        HybridAssetFixture.Require(resources.ListTemplates().Count == 4, "Asset URI templates changed.");
        var search = Data(await resources.ReadAsync("pegasus-asset://v2/search/test.hybrid?q=fixture&limit=4", CancellationToken.None));
        var items = search["items"]!.AsArray();
        HybridAssetFixture.Require(items.Count == 2 && items.All(item => item is JsonObject preview && !preview.ContainsKey("parameters") && !preview.ContainsKey("blob")), "Search hydrated opaque metadata or original bytes.");
        var primary = "cortex://" + asset.Ref;
        var metadata = Data(await resources.ReadAsync(primary, CancellationToken.None));
        HybridAssetFixture.Require(metadata["ref"]!.GetValue<string>() == asset.Ref && metadata["sha256"]!.GetValue<string>() == asset.Sha256 && metadata["parameters"]!.GetValue<string>() == asset.Metadata.Parameters, "Pinned metadata changed.");
        var explicitMetadata = Data(await resources.ReadAsync("pegasus-asset://v2/record/test.hybrid/resource-large?ref=" + asset.Ref, CancellationToken.None));
        HybridAssetFixture.Require(explicitMetadata.ToJsonString() == metadata.ToJsonString(), "Explicit and primary pinned metadata URIs diverged.");
        foreach (var uri in new[]
        {
            primary + "?part=bytes&offset=65536&length=1",
            "pegasus-asset://v2/bytes/test.hybrid/resource-large?ref=" + asset.Ref + "&offset=65536&length=1"
        })
        {
            var result = await resources.ReadAsync(uri, CancellationToken.None) ?? throw new InvalidOperationException("Asset bytes URI was unhandled.");
            var content = result["contents"]!.AsArray()[0]!;
            HybridAssetFixture.Require(content["uri"]!.GetValue<string>() == uri && Convert.FromBase64String(content["blob"]!.GetValue<string>()).SequenceEqual(bytes[^1..]), "Pinned original byte range changed.");
            var meta = result["_meta"]!;
            HybridAssetFixture.Require(meta["offset"]!.GetValue<long>() == 65536 && meta["totalSize"]!.GetValue<long>() == bytes.Length && meta["contentSha256"]!.GetValue<string>() == asset.Sha256, "Byte range provenance changed.");
        }
        HybridAssetFixture.Require(await resources.ReadAsync("https://example.invalid/unhandled", CancellationToken.None) is null, "An unrelated URI was claimed.");
    }

    private static async Task ProfileGenerationGatesAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, new CortexHybridAssetOptionsV1());
        var bytes = HybridAssetFixture.Bytes(32, 5);
        var personal = await ImportAsync(store, bytes, HybridAssetFixture.Spec("personal") with { Namespace = "knowledge.user" });
        var shared = await ImportAsync(store, bytes, HybridAssetFixture.Spec("shared"));
        var unloaded = new FixtureProfileProvider(new CortexPersonalProfileV2("not_loaded"));
        var resources = new CortexAssetResourcesV2(store, new[] { "test.hybrid", "knowledge.user" }, unloaded);
        HybridAssetFixture.Require((await resources.ListResourcesAsync(CancellationToken.None)).Count == 1, "An unloaded personal catalog was advertised.");
        var blocked = Data(await resources.ReadAsync("cortex://" + personal.Ref, CancellationToken.None));
        HybridAssetFixture.Require(blocked["error"]!.GetValue<string>() == "personal_profile_not_loaded", "Personal metadata escaped an unloaded profile gate.");
        var stable = new FixtureProfileProvider(new CortexPersonalProfileV2("loaded", "fixture-profile", "fixture-generation-a"));
        resources = new CortexAssetResourcesV2(store, new[] { "knowledge.user" }, stable);
        var admitted = Data(await resources.ReadAsync("cortex://" + personal.Ref, CancellationToken.None));
        HybridAssetFixture.Require(admitted["ref"]!.GetValue<string>() == personal.Ref && stable.Calls == 2, "The loaded profile was not checked before and after delivery.");
        var changed = new FixtureProfileProvider(new CortexPersonalProfileV2("loaded", "fixture-profile", "fixture-generation-a"), new CortexPersonalProfileV2("loaded", "fixture-profile", "fixture-generation-b"));
        resources = new CortexAssetResourcesV2(store, new[] { "knowledge.user" }, changed);
        var rejected = Data(await resources.ReadAsync("cortex://" + personal.Ref + "?part=bytes", CancellationToken.None));
        HybridAssetFixture.Require(rejected["error"]!.GetValue<string>() == "personal_profile_changed" && rejected["blob"] is null, "Personal bytes survived a generation change.");
        var unused = new FixtureProfileProvider(new CortexPersonalProfileV2("not_loaded"));
        resources = new CortexAssetResourcesV2(store, new[] { "test.hybrid" }, unused);
        HybridAssetFixture.Require(Data(await resources.ReadAsync("cortex://" + shared.Ref, CancellationToken.None))["ref"]!.GetValue<string>() == shared.Ref && unused.Calls == 0, "Public asset reads required a personal profile.");
    }

    private static async Task MalformedUrisAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, new CortexHybridAssetOptionsV1());
        var asset = await ImportAsync(store, HybridAssetFixture.Bytes(32, 9), HybridAssetFixture.Spec("invalid-uri"));
        var resources = new CortexAssetResourcesV2(new CortexReadOnlyAssetStoreV1(store), new[] { "test.hybrid" });
        var before = fixture.SnapshotFiles();
        foreach (var uri in new[]
        {
            "cortex://" + asset.Ref + "?part=unknown",
            "cortex://" + asset.Ref + "?part=bytes&length=not-a-number",
            "pegasus-asset://v2/search/test.hybrid?q=a&q=b",
            "pegasus-asset://v2/record/test.hybrid/%2e%2e?ref=" + asset.Ref
        })
        {
            var result = Data(await resources.ReadAsync(uri, CancellationToken.None));
            HybridAssetFixture.Require(result["ok"]!.GetValue<bool>() == false && result["error"]!.GetValue<string>() == "asset_uri_invalid", "A malformed asset URI was admitted.");
        }
        var range = Data(await resources.ReadAsync("cortex://" + asset.Ref + "?part=bytes&offset=1&length=1", CancellationToken.None));
        HybridAssetFixture.Require(range["error"]!.GetValue<string>() == "asset_range_invalid", "An unaligned asset range was admitted.");
        HybridAssetFixture.Require(before.SequenceEqual(fixture.SnapshotFiles()), "Readonly URI failure handling changed storage.");
    }

    private static async Task<CortexAssetV2> ImportAsync(ICortexAssetStreamStoreV1 store, byte[] bytes, CortexAssetSpecV2 spec)
    {
        await using var input = new MemoryStream(bytes, writable: false);
        return await store.ImportStreamAsync(input, bytes.Length, spec, expectedContentSha256: HybridAssetFixture.Hash(bytes));
    }

    private static JsonObject Data(JsonObject? response)
    {
        if (response is null) throw new InvalidOperationException("Asset URI was unhandled.");
        return JsonNode.Parse(response["contents"]!.AsArray()[0]!["text"]!.GetValue<string>())!.AsObject();
    }

    private sealed class FixtureProfileProvider(params CortexPersonalProfileV2[] snapshots) : ICortexPersonalProfileProvider
    {
        public int Calls { get; private set; }
        public ValueTask<CortexPersonalProfileV2> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots[Math.Min(Calls++, snapshots.Length - 1)]);
        }
    }
}
