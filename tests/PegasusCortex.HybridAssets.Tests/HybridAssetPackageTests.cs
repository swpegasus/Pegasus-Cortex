using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Core.Assets;
using Pegasus.Cortex.Persistence.Sqlite.Assets;

namespace Pegasus.Cortex.Persistence.Sqlite.Tests;

[SupportedOSPlatform("windows")]
internal static class HybridAssetPackageTests
{
    private static readonly CortexHybridAssetOptionsV1 StoreOptions = new(InlineCutoffBytes: 2048, MaximumAssetBytes: 262144);

    public static async Task<int> RunAllAsync()
    {
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("nonseekable-roundtrip-dedup-and-idempotent-admissions", RoundTripAsync),
            ("namespace-and-destination-preflight", NamespaceAndDestinationPreflightAsync),
            ("license-dependency-preflight-and-topological-order", LicenseDependenciesAsync),
            ("quotas-and-duplicate-reference-preflight", QuotasAndDuplicatesAsync),
            ("corrupt-later-payload-reports-completed-admissions", PartialFailureAsync),
            ("invalid-member-types-and-paths-never-extract", InvalidMembersAsync),
            ("hostile-pax-and-gnu-metadata-headers", HostileExtendedHeadersAsync),
            ("gzip-footer-only-truncation-one-through-eight", TruncatedFootersAsync),
            ("cancellation-and-caller-stream-ownership", CancellationAsync)
        };
        foreach (var test in cases)
        {
            await test.Run();
            Console.WriteLine("PASS hybrid-package." + test.Name);
        }
        return cases.Length;
    }

    private static async Task RoundTripAsync()
    {
        using var sourceFixture = new HybridAssetFixture();
        using var source = NewStore(sourceFixture);
        var assets = await SeedAsync(source, duplicate: true);
        var beforeExport = sourceFixture.SnapshotFiles();
        using var destinationStream = new NonSeekableWriteStream();
        var exported = await CortexAssetPackageV1.ExportAsync(new CortexReadOnlyAssetStoreV1(source), Coordinates(assets), destinationStream);
        HybridAssetFixture.Require(!destinationStream.WasDisposed, "Export disposed its caller stream.");
        var archive = destinationStream.ToArray();
        var entries = ReadEntries(archive);
        HybridAssetFixture.Require(exported.Contract == "pegasus.assets.bundle/1" && exported.References == 3 && exported.Payloads == 2 &&
            entries[0].Name == CortexAssetPackageV1.ManifestEntryName && entries[^1].Name == CortexAssetPackageV1.CompletionEntryName &&
            entries.Count(entry => entry.Name.StartsWith("objects/", StringComparison.Ordinal)) == 2, "Manifest/completion/deduplicated payload framing changed.");
        HybridAssetFixture.Require(beforeExport.SequenceEqual(sourceFixture.SnapshotFiles()), "Export changed its read-only source store.");
        using var targetFixture = new HybridAssetFixture();
        using var target = NewStore(targetFixture);
        await using (var input = new HybridNonSeekableStream(archive))
        {
            var imported = await CortexAssetPackageV1.ImportAsync(target, input, new[] { "test.hybrid" });
            HybridAssetFixture.Require(imported.References == 3 && imported.Payloads == 2 && imported.Committed == 3 && imported.Unchanged == 0,
                "First import admission counts differ.");
            _ = input.Read(Array.Empty<byte>(), 0, 0);
        }
        foreach (var asset in assets)
            HybridAssetFixture.Require(target.ReadReference(asset.Metadata.Namespace, asset.Ref) == asset, "Package roundtrip changed an immutable descriptor.");
        HybridAssetFixture.Require(targetFixture.Scalar("SELECT COUNT(*) FROM asset_objects") == 2 && targetFixture.Scalar("SELECT COUNT(*) FROM asset_refs") == 3,
            "The package did not deduplicate original payloads while preserving metadata.");
        HybridAssetFixture.Require(Directory.GetFiles(targetFixture.LargeRoot).Length == 1 && Directory.GetFiles(targetFixture.Root, "*", SearchOption.AllDirectories).Length == 2,
            "Import extracted per-asset or archive files.");
        await using var replay = new HybridNonSeekableStream(archive);
        var replayed = await CortexAssetPackageV1.ImportAsync(target, replay, new[] { "test.hybrid" });
        HybridAssetFixture.Require(replayed.Committed == 3 && replayed.Unchanged == 3, "Idempotent successful admissions were counted as physical changes.");
    }

    private static async Task NamespaceAndDestinationPreflightAsync()
    {
        using var sourceFixture = new HybridAssetFixture();
        using var source = NewStore(sourceFixture);
        var assets = await SeedAsync(source);
        var archive = await ExportAsync(source, Coordinates(assets));
        using (var targetFixture = new HybridAssetFixture())
        using (var target = NewStore(targetFixture))
        {
            var before = targetFixture.SnapshotFiles();
            var failure = await RejectImportAsync(target, archive, new[] { "other.scope" });
            HybridAssetFixture.Require(failure.Code == "asset_package_namespace_rejected" && failure.Committed == 0 && before.SequenceEqual(targetFixture.SnapshotFiles()),
                "Namespace rejection had effects or incorrect admission evidence.");
        }
        using (var targetFixture = new HybridAssetFixture())
        using (var target = NewStore(targetFixture))
        {
            var old = await ImportAsync(target, HybridAssetFixture.Bytes(32, 99), assets[1].Metadata);
            var failure = await RejectImportAsync(target, archive, new[] { "test.hybrid" });
            HybridAssetFixture.Require(failure.Code == "asset_package_head_conflict" && failure.Committed == 0 &&
                target.Current("test.hybrid", assets[0].Metadata.Id) is null && target.Current("test.hybrid", old.Metadata.Id)?.Ref == old.Ref,
                "A known later destination conflict was not refused before effects.");
            var update = await ExportAsync(source, new[] { new CortexAssetPackageCoordinateV1(assets[1].Metadata.Namespace, assets[1].Metadata.Id, assets[1].Ref, old.Ref) });
            await using var input = new HybridNonSeekableStream(update);
            var imported = await CortexAssetPackageV1.ImportAsync(target, input, new[] { "test.hybrid" });
            HybridAssetFixture.Require(imported.Committed == 1 && target.Current("test.hybrid", old.Metadata.Id)?.Ref == assets[1].Ref,
                "An exact expected-head update was not admitted.");
        }
    }

    private static async Task LicenseDependenciesAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = NewStore(fixture);
        var license = await ImportAsync(store, Encoding.UTF8.GetBytes("Synthetic fixture license."), HybridAssetFixture.Spec("license") with { MediaType = "text/plain" });
        var artwork = await ImportAsync(store, HybridAssetFixture.Bytes(32, 1), HybridAssetFixture.Spec("licensed") with { DetailsRef = license.Ref });
        using (var output = new MemoryStream())
        {
            var failure = await RejectAsync(() => CortexAssetPackageV1.ExportAsync(store, Coordinates(new[] { artwork }), output));
            HybridAssetFixture.Require(failure.Code == "asset_package_dependency_missing" && output.Length == 0, "A missing license dependency was written to an archive.");
        }
        var archive = await ExportAsync(store, Coordinates(new[] { artwork, license }));
        var manifest = JsonNode.Parse(ReadEntries(archive)[0].Bytes)!;
        HybridAssetFixture.Require(manifest["assets"]!.AsArray()[0]!["ref"]!.GetValue<string>() == license.Ref, "License dependency was not ordered before its dependent.");
        var incompatible = await ImportAsync(store, HybridAssetFixture.Bytes(33, 2), HybridAssetFixture.Spec("incompatible-license") with { License = "MIT", DetailsRef = license.Ref });
        using var invalidOutput = new MemoryStream();
        var invalid = await RejectAsync(() => CortexAssetPackageV1.ExportAsync(store, Coordinates(new[] { incompatible, license }), invalidOutput));
        HybridAssetFixture.Require(invalid.Code == "asset_package_dependency_scope_invalid" && invalidOutput.Length == 0, "Incompatible license metadata passed preflight.");
    }

    private static async Task QuotasAndDuplicatesAsync()
    {
        using var sourceFixture = new HybridAssetFixture();
        using var source = NewStore(sourceFixture);
        var assets = await SeedAsync(source);
        var archive = await ExportAsync(source, Coordinates(assets));
        var limits = new[]
        {
            new CortexAssetPackageOptionsV1(MaximumEntries: 1),
            new CortexAssetPackageOptionsV1(MaximumMetadataUtf8Bytes: 32),
            new CortexAssetPackageOptionsV1(MaximumAssetBytes: 1023),
            new CortexAssetPackageOptionsV1(MaximumManifestUtf8Bytes: 4096, MaximumExpandedBytes: 4096),
            new CortexAssetPackageOptionsV1(MaximumCompressedBytes: 32)
        };
        foreach (var options in limits)
        {
            using var fixture = new HybridAssetFixture();
            using var target = NewStore(fixture);
            var failure = await RejectImportAsync(target, archive, new[] { "test.hybrid" }, options);
            HybridAssetFixture.Require(failure.Committed == 0 && fixture.Scalar("SELECT COUNT(*) FROM asset_heads") == 0, "A bounded preflight rejection admitted a reference.");
        }
        using var output = new MemoryStream();
        var duplicate = await RejectAsync(() => CortexAssetPackageV1.ExportAsync(source, Coordinates(new[] { assets[0], assets[0] }), output));
        HybridAssetFixture.Require(duplicate.Code == "asset_package_duplicate_reference" && output.Length == 0, "Duplicate coordinates were written to an archive.");
        var entries = ReadEntries(archive);
        var manifest = JsonNode.Parse(entries[0].Bytes)!.AsObject();
        manifest["assets"]!.AsArray().Add(manifest["assets"]!.AsArray()[0]!.DeepClone());
        entries[0] = entries[0] with { Bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString()) };
        using var duplicateFixture = new HybridAssetFixture();
        using var duplicateTarget = NewStore(duplicateFixture);
        var rejected = await RejectImportAsync(duplicateTarget, WriteEntries(entries), new[] { "test.hybrid" });
        HybridAssetFixture.Require(rejected.Code == "asset_package_duplicate_reference" && rejected.Committed == 0 && duplicateFixture.Scalar("SELECT COUNT(*) FROM asset_heads") == 0,
            "A duplicate manifest was not rejected before effects.");
    }

    private static async Task PartialFailureAsync()
    {
        using var sourceFixture = new HybridAssetFixture();
        using var source = NewStore(sourceFixture);
        var assets = await SeedAsync(source);
        var entries = ReadEntries(await ExportAsync(source, Coordinates(assets)));
        var payloadIndices = Enumerable.Range(0, entries.Count).Where(index => entries[index].Name.StartsWith("objects/", StringComparison.Ordinal)).ToArray();
        entries[payloadIndices[1]].Bytes[10] ^= 0x55;
        using var fixture = new HybridAssetFixture();
        using var target = NewStore(fixture);
        var failure = await RejectImportAsync(target, WriteEntries(entries), new[] { "test.hybrid" });
        HybridAssetFixture.Require(failure.Committed == 1 && fixture.Scalar("SELECT COUNT(*) FROM asset_heads") == 1 &&
            target.Current("test.hybrid", assets[0].Metadata.Id)?.Ref == assets[0].Ref && target.Current("test.hybrid", assets[1].Metadata.Id) is null,
            "A later corrupt payload hid or exaggerated completed admissions.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_import_chunks") == 0, "A corrupt payload retained staging rows.");
    }

    private static async Task InvalidMembersAsync()
    {
        using var sourceFixture = new HybridAssetFixture();
        using var source = NewStore(sourceFixture);
        var assets = await SeedAsync(source);
        var archive = await ExportAsync(source, Coordinates(assets));
        foreach (var symbolic in new[] { false, true })
        {
            var entries = ReadEntries(archive);
            var index = entries.FindIndex(entry => entry.Name.StartsWith("objects/", StringComparison.Ordinal));
            entries[index] = symbolic
                ? entries[index] with { Type = TarEntryType.SymbolicLink, LinkName = "../never-extracted", Bytes = Array.Empty<byte>() }
                : entries[index] with { Name = "objects/../never-extracted" };
            using var fixture = new HybridAssetFixture();
            using var target = NewStore(fixture);
            var before = fixture.SnapshotFiles();
            var failure = await RejectImportAsync(target, WriteEntries(entries), new[] { "test.hybrid" });
            HybridAssetFixture.Require(failure.Committed == 0 && before.SequenceEqual(fixture.SnapshotFiles()) && Directory.GetDirectories(fixture.Root).Length == 0,
                "A rejected archive member extracted files or created payload directories.");
        }
    }

    private static async Task HostileExtendedHeadersAsync()
    {
        foreach (var type in new[] { (byte)'x', (byte)'g', (byte)'L', (byte)'K' })
        {
            // A tiny synthetic gzip body declares 8 GiB of PAX/GNU metadata. Never allocate that declaration.
            var header = new byte[512];
            Encoding.ASCII.GetBytes(CortexAssetPackageV1.ManifestEntryName).CopyTo(header, 0);
            Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
            Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 108);
            Encoding.ASCII.GetBytes("0000000\0").CopyTo(header, 116);
            Encoding.ASCII.GetBytes("77777777777\0").CopyTo(header, 124);
            Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
            header.AsSpan(148, 8).Fill((byte)' ');
            header[156] = type;
            Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
            Encoding.ASCII.GetBytes("00").CopyTo(header, 263);
            var checksum = header.Sum(value => (int)value);
            Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) { gzip.Write(header); gzip.Write(new byte[1024]); }
            using var fixture = new HybridAssetFixture();
            using var target = NewStore(fixture);
            var failure = await RejectImportAsync(target, compressed.ToArray(), new[] { "test.hybrid" });
            HybridAssetFixture.Require(failure.Committed == 0 && fixture.Scalar("SELECT COUNT(*) FROM asset_heads") == 0, "An extended header bypassed bounded manifest admission.");
        }
    }

    private static async Task TruncatedFootersAsync()
    {
        using var sourceFixture = new HybridAssetFixture();
        using var source = NewStore(sourceFixture);
        var asset = await ImportAsync(source, HybridAssetFixture.Bytes(32, 2), HybridAssetFixture.Spec("footer"));
        var archive = await ExportAsync(source, Coordinates(new[] { asset }));
        HybridAssetFixture.Require(ReadEntries(archive)[^1].Name == CortexAssetPackageV1.CompletionEntryName, "The footer fixture lacks a complete original tar body.");
        for (var removed = 1; removed <= 8; removed++)
        {
            using var fixture = new HybridAssetFixture();
            using var target = NewStore(fixture);
            var failure = await RejectImportAsync(target, archive[..^removed], new[] { "test.hybrid" });
            HybridAssetFixture.Require(failure.Committed == fixture.Scalar("SELECT COUNT(*) FROM asset_heads"), "Footer refusal reported unconfirmed admissions.");
        }
    }

    private static async Task CancellationAsync()
    {
        using var sourceFixture = new HybridAssetFixture();
        using var source = NewStore(sourceFixture);
        var assets = await SeedAsync(source);
        var archive = await ExportAsync(source, Coordinates(assets));
        using var fixture = new HybridAssetFixture();
        using var target = NewStore(fixture);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await using var input = new HybridNonSeekableStream(archive);
        var failure = await RejectAsync(() => CortexAssetPackageV1.ImportAsync(target, input, new[] { "test.hybrid" }, cancellationToken: cancellation.Token));
        HybridAssetFixture.Require(failure.Code == "asset_package_cancelled" && failure.Committed == 0 && fixture.Scalar("SELECT COUNT(*) FROM asset_heads") == 0, "Cancellation admission evidence differs.");
        _ = input.Read(Array.Empty<byte>(), 0, 0);
        using var output = new NonSeekableWriteStream();
        var exportFailure = await RejectAsync(() => CortexAssetPackageV1.ExportAsync(source, Coordinates(assets), output, cancellationToken: cancellation.Token));
        HybridAssetFixture.Require(exportFailure.Code == "asset_package_cancelled" && !output.WasDisposed, "Cancelled export disposed its caller stream.");
    }

    private static CortexSqliteAssetStoreV1 NewStore(HybridAssetFixture fixture) => CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, StoreOptions);
    private static CortexAssetPackageCoordinateV1[] Coordinates(IReadOnlyList<CortexAssetV2> assets) => assets.Select(asset => new CortexAssetPackageCoordinateV1(asset.Metadata.Namespace, asset.Metadata.Id, asset.Ref)).ToArray();
    private static async Task<CortexAssetV2[]> SeedAsync(ICortexAssetStreamStoreV1 store, bool duplicate = false)
    {
        var firstBytes = HybridAssetFixture.Bytes(1024, 1);
        var first = await ImportAsync(store, firstBytes, HybridAssetFixture.Spec("package-a"));
        var second = await ImportAsync(store, HybridAssetFixture.Bytes(65537, 2), HybridAssetFixture.Spec("package-b"));
        if (!duplicate) return new[] { first, second };
        var third = await ImportAsync(store, firstBytes, HybridAssetFixture.Spec("package-c"));
        return new[] { first, second, third };
    }
    private static async Task<CortexAssetV2> ImportAsync(ICortexAssetStreamStoreV1 store, byte[] bytes, CortexAssetSpecV2 spec)
    {
        await using var input = new MemoryStream(bytes, writable: false);
        return await store.ImportStreamAsync(input, bytes.Length, spec, expectedContentSha256: HybridAssetFixture.Hash(bytes));
    }
    private static async Task<byte[]> ExportAsync(ICortexAssetStoreV2 store, IReadOnlyList<CortexAssetPackageCoordinateV1> coordinates)
    {
        using var output = new NonSeekableWriteStream();
        await CortexAssetPackageV1.ExportAsync(store, coordinates, output);
        return output.ToArray();
    }
    private static async Task<CortexAssetPackageException> RejectImportAsync(ICortexAssetStreamStoreV1 store, byte[] archive, IReadOnlyList<string> namespaces, CortexAssetPackageOptionsV1? options = null)
    {
        await using var input = new HybridNonSeekableStream(archive);
        return await RejectAsync(() => CortexAssetPackageV1.ImportAsync(store, input, namespaces, options));
    }
    private static async Task<CortexAssetPackageException> RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (CortexAssetPackageException failure) { return failure; }
        throw new InvalidOperationException("Expected a typed package refusal.");
    }
    private sealed record FixtureEntry(string Name, byte[] Bytes, TarEntryType Type = TarEntryType.RegularFile, string LinkName = "");
    private static List<FixtureEntry> ReadEntries(byte[] archive)
    {
        using var input = new MemoryStream(archive, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new List<FixtureEntry>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            using var data = new MemoryStream(); entry.DataStream?.CopyTo(data);
            HybridAssetFixture.Require(data.Length <= 262144 && entries.Count < 16, "Synthetic fixture helper exceeded its own bounds.");
            entries.Add(new(entry.Name, data.ToArray(), entry.EntryType, entry.LinkName));
        }
        return entries;
    }
    private static byte[] WriteEntries(IReadOnlyList<FixtureEntry> entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = new UstarTarEntry(item.Type, item.Name);
                using var data = new MemoryStream(item.Bytes, writable: false);
                if (item.Type == TarEntryType.RegularFile) entry.DataStream = data; else entry.LinkName = item.LinkName;
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }
    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream inner = new();
        public bool WasDisposed { get; private set; }
        public byte[] ToArray() => inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !WasDisposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { WasDisposed = true; if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
