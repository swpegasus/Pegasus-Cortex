using System.Runtime.Versioning;
using System.Text;
using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Core.Assets;
using Pegasus.Cortex.Persistence.Sqlite.Assets;

namespace Pegasus.Cortex.Persistence.Sqlite.Tests;

[SupportedOSPlatform("windows")]
internal static class HybridAssetGoldenTests
{
    public static async Task<int> RunAllAsync()
    {
        // Independent original assets/2 literal: fixed field order, escaped opaque text and one final LF.
        const string canonical = """
            [asset]
            contract=pegasus.assets/2
            namespace=test.golden
            id=original
            title=Café 🧠\\part
            meaning=Meaning Ω
            media-type=application/octet-stream
            tags=blue,雪
            alias=alias\\marker
            text=★
            license=CC0-1.0
            source=https://example.invalid/original?q=1
            pack=fixture-pack
            pack-version=1.0
            parameters={"label":"雪","path":"C:\\\\demo\\\\file","quoted":"\\"x\\""}
            details-ref=dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd
            sha256=ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
            size=3
            chunks-sha256=620a3df236da0af638c2a61c86951463731998f91dbb3ed629f47b9fe00ad118
            """ + "\n";
        const string originalHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        const string chunkIndexHash = "620a3df236da0af638c2a61c86951463731998f91dbb3ed629f47b9fe00ad118";
        const string pinnedReference = "89df1f145ef836d81235d75bbd5127e3514750d97d864b06d99479ad3f82dc3b";
        var spec = new CortexAssetSpecV2("test.golden", "original", "Café 🧠\\part", "Meaning Ω",
            "application/octet-stream", "blue,雪", "alias\\marker", "★", "CC0-1.0",
            "https://example.invalid/original?q=1", "fixture-pack", "1.0",
            """{"label":"雪","path":"C:\\demo\\file","quoted":"\"x\""}""", new string('d', 64));
        HybridAssetFixture.Require(Encoding.UTF8.GetByteCount(canonical) == 593 &&
            HybridAssetFixture.Hash(Encoding.UTF8.GetBytes(canonical)) == pinnedReference,
            "The independent original-format vector changed.");
        HybridAssetFixture.Require(CortexAssetCodecV2.Serialize(spec, originalHash, 3, chunkIndexHash) == canonical,
            "Original field order, escaping, Unicode, opaque JSON, details reference or trailing LF changed.");
        HybridAssetFixture.Require(CortexAssetCodecV2.Reference(canonical) == pinnedReference &&
            CortexAssetCodecV2.Parse(pinnedReference, canonical).Metadata == spec,
            "The original pinned reference no longer parses exactly.");
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, new CortexHybridAssetOptionsV1());
        await using var original = new MemoryStream(new byte[] { 97, 98, 99 }, writable: false);
        var asset = await store.ImportStreamAsync(original, 3, spec, expectedContentSha256: originalHash);
        HybridAssetFixture.Require(asset.Ref == pinnedReference && asset.Sha256 == originalHash && asset.ChunksSha256 == chunkIndexHash,
            "The SQLite import changed an independently pinned original-format reference.");
        Console.WriteLine("PASS hybrid-golden.original-format-reference");
        return 1;
    }
}
