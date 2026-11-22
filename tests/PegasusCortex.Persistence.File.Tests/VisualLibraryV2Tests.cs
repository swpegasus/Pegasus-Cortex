using Pegasus.Cortex.Contracts.Assets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Persistence.File.Assets;

namespace Pegasus.Cortex.Persistence.File.Tests;

internal static class VisualLibraryV2Tests
{
    internal static async Task ExactReplayAndLicensingAsync()
    {
        using var fixture = new Fixture();
        var first = await CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin);
        var repeat = await CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin);
        Require(first.Created == 2 && repeat.Created == 0 && repeat.Unchanged == 2 &&
            first.Sample == repeat.Sample && repeat.Sample is not null, "Replay changed exact coordinates.");
        var image = fixture.Store.ReadReference(repeat.Sample!.Ref);
        var license = fixture.Store.ReadReference(image.Metadata.DetailsRef);
        Require(license.Metadata.Id == fixture.License.Metadata.Id && image.Metadata.Parameters == "{\"seed\":7}",
            "Exact licensing or opaque parameters were lost.");
        var bytes = await fixture.Store.ReadBytesAsync(image.Metadata.Namespace, image.Metadata.Id, image.Ref);
        Require(Encoding.UTF8.GetString(bytes.Bytes) == Fixture.Svg && first.ArtworkCount == 1 && first.References == 2,
            "Typed bytes/inventory differed.");
    }

    internal static async Task AdmissionFailsBeforeWritesAsync()
    {
        using var fixture = new Fixture();
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, new string('0', 64)));
        await System.IO.File.AppendAllTextAsync(fixture.ImagePath, "tamper");
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin));
        await System.IO.File.WriteAllTextAsync(fixture.ImagePath, Fixture.Svg);
        fixture.Write(fixture.Image with { File = "../image.svg" });
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin));
        fixture.Write(fixture.Image with { LicenseId = "visual.fixture.missing-license" });
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin));
        fixture.Write(fixture.Image with { Metadata = fixture.Image.Metadata with { Namespace = "project.other" } });
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin));
        fixture.Write(fixture.Image);
        Require(fixture.Store.Current("knowledge.shared", fixture.License.Metadata.Id) is null,
            "Rejected preflight committed a license.");
        await fixture.Store.ImportAsync(fixture.LicensePath, fixture.License.Metadata with { Pack = "user.owned" });
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin));
        Require(fixture.Store.Current("knowledge.shared", fixture.Image.Metadata.Id) is null,
            "Foreign licensing head did not reject before artwork writes.");
    }

    internal static async Task BoundedManifestAndCancellationAsync()
    {
        using var fixture = new Fixture();
        var original = await System.IO.File.ReadAllTextAsync(fixture.Manifest);
        var headerEnd = original.IndexOf('\n');
        var duplicated = original[..headerEnd].TrimEnd('\r').TrimEnd('}') + ",\"contract\":\"pegasus.visual-library/1\"}" + original[headerEnd..];
        await System.IO.File.WriteAllTextAsync(fixture.Manifest, duplicated, new UTF8Encoding(false));
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin));
        await System.IO.File.WriteAllTextAsync(fixture.Manifest, new string(' ', 32769) + "\n", new UTF8Encoding(false));
        await Reject(() => CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin));
        fixture.Write(fixture.Image);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await CortexVisualLibraryV2.ImportAsync(fixture.Store, fixture.Manifest, fixture.Pin, cancelled.Token);
            throw new InvalidDataException("Cancelled admission unexpectedly succeeded.");
        }
        catch (OperationCanceledException) { }
        Require(fixture.Store.Current("knowledge.shared", fixture.License.Metadata.Id) is null,
            "Invalid/cancelled manifest committed state.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }

    private static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { return; }
        throw new InvalidDataException("Expected bounded admission rejection.");
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\"><path d=\"M0 0\"/></svg>";
        private const string Revision = "0123456789012345678901234567890123456789";
        private readonly string root = Path.Combine(Path.GetTempPath(), "cortex-visual-tests-" + Guid.NewGuid().ToString("N"));
        internal CortexAssetStoreV2 Store { get; }
        internal string Manifest { get; }
        internal string ImagePath { get; }
        internal string LicensePath { get; }
        internal CortexVisualLibraryEntryV2 Image { get; }
        internal CortexVisualLibraryEntryV2 License { get; }
        internal string Pin => Hex(SHA256.HashData(System.IO.File.ReadAllBytes(Manifest)));

        internal Fixture()
        {
            Directory.CreateDirectory(root);
            LicensePath = Path.Combine(root, "license.txt");
            ImagePath = Path.Combine(root, "image.svg");
            System.IO.File.WriteAllText(LicensePath, "MIT License\nFixture only.\n", new UTF8Encoding(false));
            System.IO.File.WriteAllText(ImagePath, Svg, new UTF8Encoding(false));
            var spec = new CortexAssetSpecV2("knowledge.shared", "visual.fixture.license", "Fixture license",
                "Fixture only", "text/plain", License: "MIT", Pack: "visual.fixture", PackVersion: Revision);
            License = new("license.txt", Hex(SHA256.HashData(System.IO.File.ReadAllBytes(LicensePath))), spec, null);
            Image = new("image.svg", Hex(SHA256.HashData(System.IO.File.ReadAllBytes(ImagePath))),
                spec with { Id = "visual.fixture.image", MediaType = "image/svg+xml", Parameters = "{\"seed\":7}" }, spec.Id);
            Manifest = Path.Combine(root, "assets.jsonl");
            Store = new CortexAssetStoreV2(Path.Combine(root, "store"));
            Write(Image);
        }

        internal void Write(CortexVisualLibraryEntryV2 image)
            => System.IO.File.WriteAllLines(Manifest,
                [JsonSerializer.Serialize(new CortexVisualLibraryHeaderV2(CortexVisualLibraryV2.Contract,
                    "knowledge.shared", "visual.fixture", Revision, "https://example.com/fixture", Revision, "MIT"), CortexVisualLibraryV2.Json),
                 JsonSerializer.Serialize(License, CortexVisualLibraryV2.Json), JsonSerializer.Serialize(image, CortexVisualLibraryV2.Json)],
                new UTF8Encoding(false));

        public void Dispose()
        {
            CortexAssetStoreV2.CheckPath(root);
            Directory.Delete(root, true);
        }

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

