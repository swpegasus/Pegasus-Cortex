using Pegasus.Cortex.Contracts.Assets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.File.Assets;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

public static class CortexAssetProductCommandsV2
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };

    public static async Task<(int ExitCode, string Json)> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (args is ["assets", "verify-icons", "--pack-root", var verifyRoot,
                "--catalog-sha", var verifyCatalog, "--content-sha", var verifyContent])
            {
                var verified = CortexIconPackV2.Load(verifyRoot, verifyCatalog, verifyContent, cancellationToken);
                return Success(new JsonObject { ["pack"] = verified.Id, ["packVersion"] = verified.Version,
                    ["producerContract"] = CortexIconPackV2.ProducerContract, ["catalogSha256"] = verified.CatalogSha256,
                    ["contentSha256"] = verified.ContentSha256, ["icons"] = verified.Entries.Count,
                    ["files"] = verified.Files.Count, ["size"] = verified.Files.Sum(file => file.Size) });
            }
            var layout = CortexCurrentUserLayout.Resolve();
            var store = new CortexAssetStoreV2(layout.AssetDirectory);
            if (args.Length is 6 or 8 && args[0] == "assets" && args[1] == "import" &&
                args[2] == "--file" && args[4] == "--metadata-file" && (args.Length == 6 || args[6] == "--expected-ref"))
            {
                // Explicit owner-local ingestion only; imported files never become tools or commands.
                var source = args[3];
                var metadataPath = args[5];
                CortexAssetStoreV2.CheckPath(metadataPath);
                await using var input = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > CortexAssetStoreV2.MaximumMetadataBytes) throw new CortexAssetException("asset_metadata_oversized");
                using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
                var json = await reader.ReadToEndAsync(cancellationToken);
                var metadata = JsonSerializer.Deserialize<CortexAssetSpecV2>(json, Options) ?? throw new CortexAssetException("asset_metadata_invalid");
                EnsureAssetDirectory(layout);
                var asset = await store.ImportAsync(source, metadata, args.Length == 8 ? args[7] : null, cancellationToken);
                return Success(new JsonObject { ["namespace"] = metadata.Namespace, ["id"] = metadata.Id,
                    ["ref"] = asset.Ref, ["uri"] = CortexAssetResourcesV2.RecordUri(asset), ["sha256"] = asset.Sha256, ["size"] = asset.Size });
            }
            if (args is ["assets", "import-icons", "--pack-root", var root, "--namespace", var ns,
                "--catalog-sha", var catalogSha, "--content-sha", var contentSha])
            {
                var pack = CortexIconPackV2.Load(root, catalogSha, contentSha, cancellationToken);
                EnsureAssetDirectory(layout);
                var count = await ImportIconsAsync(store, pack, ns, cancellationToken);
                return Success(new JsonObject { ["namespace"] = ns, ["pack"] = pack.Id,
                    ["packVersion"] = pack.Version, ["catalogSha256"] = pack.CatalogSha256,
                    ["contentSha256"] = pack.ContentSha256, ["references"] = count });
            }
            if (args is ["assets", "import-visual-library", "--manifest", var visualManifest, "--sha256", var visualPin])
            {
                // Explicit selected-module admission, never part of machine setup.
                // The bounded receipt and file store are shared with the development helper.
                EnsureAssetDirectory(layout);
                var result = await CortexVisualLibraryV2.ImportAsync(store, visualManifest, visualPin, cancellationToken);
                return (0, JsonSerializer.Serialize(result, CortexVisualLibraryV2.Json));
            }
            if (args is ["assets", "expose", "--namespace", var nsToExpose])
            {
                CortexAssetStoreV2.RequireName(nsToExpose, 63);
                EnsureAssetDirectory(layout);
                await store.ExposeAsync(nsToExpose, cancellationToken);
                return Success(new JsonObject { ["namespace"] = nsToExpose, ["exposed"] = true });
            }
            return (2, "{\"ok\":false,\"error\":\"asset_arguments_invalid\"}");
        }
        catch (CortexAssetException error) { return Failure(error.Code, error.Committed); }
        catch (InvalidDataException) { return Failure("asset_pack_invalid"); }
        catch (IOException) { return Failure("asset_file_unavailable"); }
        catch (UnauthorizedAccessException) { return Failure("asset_access_denied"); }
        catch (JsonException) { return Failure("asset_metadata_invalid"); }
        catch (DecoderFallbackException) { return Failure("asset_encoding_invalid"); }
    }

    internal static IReadOnlyList<string> ReadExposedNamespaces(ICortexAssetStoreV2 store)
        => store.ExposedNamespaces();

    internal static async Task<int> ImportIconsAsync(ICortexAssetStoreV2 store, CortexIconPackV2 pack,
        string ns, CancellationToken cancellationToken)
    {
        CortexAssetStoreV2.RequireName(ns, 63);
        var specifications = pack.Entries.Select(entry => (entry.FilePath, entry.Sha256,
            new CortexAssetSpecV2(ns, entry.Id, entry.Title, entry.Meaning, entry.MediaType,
                Tags: IconTags(entry.Alias), Alias: entry.Alias, Text: entry.Text,
                License: entry.License, Source: entry.Source, Pack: pack.Id, PackVersion: pack.Version))).ToList();
        foreach (var file in pack.Files.Where(file => file.Role is "catalog" or "license" or "notice"))
            specifications.Add((file.FilePath, file.Sha256, new CortexAssetSpecV2(ns,
                pack.Id + "." + file.Role, "Icon pack " + file.Role, "Pinned pack provenance and licensing.",
                file.MediaType, Tags: "icon licensing provenance", License: pack.License, Source: pack.Upstream,
                Pack: pack.Id, PackVersion: pack.Version)));
        // Preflight all existing heads before the first per-asset commit; never adopt foreign/user heads.
        var plan = specifications.Select(item => (Item: item, Current: store.Current(ns, item.Item3.Id))).ToArray();
        foreach (var item in plan)
            if (item.Current is { } previous && (previous.Metadata.Pack != pack.Id ||
                !Version.TryParse(previous.Metadata.PackVersion, out var previousVersion) ||
                previousVersion > Version.Parse(pack.Version) ||
                previous.Metadata.PackVersion == pack.Version && (previous.Sha256 != item.Item.Sha256 || previous.Metadata != item.Item.Item3)))
                throw new CortexAssetException("asset_pack_revision_conflict");
        var committed = 0;
        foreach (var item in plan)
        {
            try
            {
                await store.ImportAsync(pack.GetFilePath(item.Item.FilePath), item.Item.Item3,
                    item.Current?.Ref, cancellationToken, item.Item.Sha256);
            }
            catch (CortexAssetException error) { throw new CortexAssetException(error.Code, committed); }
            catch (IOException) { throw new CortexAssetException("asset_file_unavailable", committed); }
            catch (OperationCanceledException) { throw new CortexAssetException("asset_import_cancelled", committed); }
            committed++;
        }
        return committed;
    }

    private static void EnsureAssetDirectory(CortexCurrentUserLayout layout)
    {
        layout.EnsureCreated();
        CortexUserStateDirectories.EnsureCurrentUserDirectory(layout.AssetDirectory);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(layout.AssetDirectory);
    }
    private static string IconTags(string alias) => "icon emoji " + alias.Replace('.', ' ') + (alias switch
    {
        "status.success" => " good done complete approved go good-to-go",
        "status.error" => " bad failed failure no-good",
        "status.warning" => " caution attention",
        "status.cancelled" => " stop cancel blocked",
        "status.working" => " progress running busy",
        "status.awaiting" => " wait pending",
        _ => ""
    });
    private static (int, string) Success(JsonObject result) { result["ok"] = true; result["contract"] = CortexAssetStoreV2.Contract; return (0, result.ToJsonString()); }
    private static (int, string) Failure(string code, int? committed = null)
    {
        var result = new JsonObject { ["ok"] = false, ["error"] = code };
        if (committed is { } count) { result["committed"] = count; result["partial"] = count > 0; }
        return (committed is > 0 ? 7 : 3, result.ToJsonString());
    }
}

