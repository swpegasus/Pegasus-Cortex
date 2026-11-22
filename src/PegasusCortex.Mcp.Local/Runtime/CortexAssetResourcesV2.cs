using Pegasus.Cortex.Contracts.Assets;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Encodings.Web;
using Pegasus.Cortex.Persistence.File.Assets;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>Read-only, root-pinned library resources; only the personal layer requires a loaded profile.</summary>
internal sealed class CortexAssetResourcesV2
{
    private const string Prefix = "pegasus-asset://v2/";
    private const string ReferencePrefix = "cortex://";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 32 };
    private readonly ICortexAssetStoreV2 _store;
    private readonly HashSet<string> _namespaces;
    private readonly ICortexPersonalProfileProvider? _profile;
    public CortexAssetResourcesV2(ICortexAssetStoreV2 store, IEnumerable<string> catalogNamespaces,
        ICortexPersonalProfileProvider? profile = null)
    {
        _store = store;
        _profile = profile;
        _namespaces = new(catalogNamespaces.Take(33), StringComparer.Ordinal);
        if (_namespaces.Count > 32) throw new CortexAssetException("asset_scope_invalid");
        foreach (var ns in _namespaces) CortexAssetStoreV2.RequireName(ns, 63);
    }

    public async ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken)
    {
        var personalLoaded = !_namespaces.Any(Personal) || (await ProfileAsync(cancellationToken)) is not null;
        return new(_namespaces.Where(ns => personalLoaded || !Personal(ns)).Order(StringComparer.Ordinal).Select(ns =>
        (JsonNode)new JsonObject { ["uri"] = SearchUri(ns, ""), ["name"] = "Assets: " + ns,
            ["description"] = "Bounded metadata only; use search template, then exact pinned metadata/bytes.",
            ["mimeType"] = "application/json" }).ToArray());
    }

    public JsonArray ListTemplates() =>
    [
        new JsonObject { ["uriTemplate"] = Prefix + "search/{namespace}{?q,limit,after}",
            ["name"] = "Asset and icon metadata search", ["mimeType"] = "application/json",
            ["description"] = "Explicit namespace selection; default4/max16 previews,512 metadata scans. Query labels, meanings, aliases, tags or Unicode. Follow nextUri; no artwork/parameters hydration." },
        new JsonObject { ["uriTemplate"] = ReferencePrefix + "{assetId}",
            ["name"] = "Pinned asset metadata", ["mimeType"] = "application/json",
            ["description"] = "Unique pinned asset ID resolves exact metadata, including optional opaque parameters JSON text. No namespace or filesystem path required." },
        new JsonObject { ["uriTemplate"] = Prefix + "reference/{namespace}{?ref}",
            ["name"] = "Pinned related metadata", ["mimeType"] = "application/json",
            ["description"] = "Resolve detailsRef in its explicit namespace; returns exact ID and pinned read coordinates." },
        new JsonObject { ["uriTemplate"] = ReferencePrefix + "{assetId}?part=bytes{&offset,length}",
            ["name"] = "Pinned passive asset bytes",
            ["description"] = "Default64KiB, maximum128KiB; offset multiple65536. Verified selected chunks; assemble and check full SHA256 before consumer admission. Not automatically injected/rendered/executed." }
    ];

    public async ValueTask<JsonObject?> ReadAsync(string value, CancellationToken cancellationToken)
    {
        CortexAssetV2? resolved = null;
        var requestedUri = value;
        if (value.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            try
            {
                var reference = value[ReferencePrefix.Length..].Split('?')[0];
                resolved = _store.ReadReference(reference);
                var query = Query(value.Contains('?') ? value[value.IndexOf('?')..] : "");
                Only(query, "part", "offset", "length");
                var bytes = query.GetValueOrDefault("part") == "bytes";
                if (!bytes && query.Count != 0) throw new CortexAssetException("asset_uri_invalid");
                var m = resolved.Metadata;
                value = Prefix + (bytes ? "bytes/" : "record/") + m.Namespace + "/" + m.Id + "?ref=" + resolved.Ref;
                if (bytes)
                {
                    if (query.TryGetValue("offset", out var offset)) value += "&offset=" + offset;
                    if (query.TryGetValue("length", out var length)) value += "&length=" + length;
                }
            }
            catch (CortexAssetException error) { return Failure(requestedUri, error.Code); }
            catch (IOException) { return Failure(requestedUri, "asset_reference_unavailable"); }
            catch (UnauthorizedAccessException) { return Failure(requestedUri, "asset_access_denied"); }
            catch (DecoderFallbackException) { return Failure(requestedUri, "asset_metadata_integrity_failed"); }
        }
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = value[Prefix.Length..].Split('?')[0].Split('/');
        var personal = parts.Length >= 2 && Personal(parts[1]);
        var admitted = personal ? await ProfileAsync(cancellationToken) : null;
        if (personal && admitted is null) return Failure(requestedUri, "personal_profile_not_loaded");
        var result = await ReadCoreAsync(value, cancellationToken, resolved);
        // Never deliver personal bytes after their loaded generation changes.
        if (personal && await ProfileAsync(cancellationToken) != admitted) return Failure(requestedUri, "personal_profile_changed");
        if (result?["contents"] is JsonArray contents)
            foreach (var item in contents.OfType<JsonObject>()) item["uri"] = requestedUri;
        return result;
    }

    private static bool Personal(string ns) => ns is "knowledge.user" or "user.preferences";
    private async ValueTask<CortexPersonalProfileV2?> ProfileAsync(CancellationToken cancellationToken)
    {
        if (_profile is null) return null;
        try
        {
            var current = await _profile.GetAsync(cancellationToken);
            return current is { State: "loaded", Profile: not null, Generation: not null } ? current : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or
            OperationCanceledException or InvalidOperationException or ArgumentException or JsonException or System.Security.SecurityException)
        { return null; }
    }

    private async ValueTask<JsonObject?> ReadCoreAsync(string value, CancellationToken cancellationToken, CortexAssetV2? resolved = null)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        string? selectedNamespace = null, selectedId = null, selectedRef = null;
        try
        {
            var rawPath = value[Prefix.Length..].Split('?')[0];
            if (rawPath.Split('/').Any(part => part is "." or ".." || part.Contains('%')))
                throw new CortexAssetException("asset_uri_invalid");
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new CortexAssetException("asset_uri_invalid");
            var parts = uri.AbsolutePath.TrimStart('/').Split('/');
            if (parts.Length is < 2 or > 3 || parts.Any(part => part.Length == 0 || part.Contains('%')))
                throw new CortexAssetException("asset_uri_invalid");
            var mode = parts[0];
            var ns = parts[1];
            // The bounded catalog is discovery, not an allowlist. Store validation
            // still binds exact namespace/reference identity and canonical paths.
            CortexAssetStoreV2.RequireName(ns, 63);
            var query = Query(uri.Query);
            JsonObject data;
            if (mode == "reference" && parts.Length == 2)
            {
                Only(query, "ref");
                var related = _store.ReadReference(ns, query.GetValueOrDefault("ref") ?? "");
                var relatedData = Preview(related);
                relatedData["namespace"] = ns;
                relatedData["contract"] = CortexAssetStoreV2.Contract;
                return Text(value, relatedData);
            }
            if (mode == "search" && parts.Length == 2)
            {
                Only(query, "q", "limit", "after");
                var text = query.GetValueOrDefault("q") ?? "";
                var limit = Integer(query, "limit", 4);
                var result = _store.Search(ns, text, limit, query.GetValueOrDefault("after"), cancellationToken);
                data = new() { ["contract"] = CortexAssetStoreV2.Contract, ["namespace"] = ns,
                    ["items"] = new JsonArray(result.Items.Select(item => (JsonNode)Preview(item, compact: true)).ToArray()),
                    ["scanned"] = result.Scanned, ["complete"] = result.Complete };
                if (result.NextCursor is not null) data["nextUri"] = SearchUri(ns, text, limit, result.NextCursor);
                return Text(value, data);
            }
            if (parts.Length != 3 || mode is not ("record" or "bytes")) throw new CortexAssetException("asset_uri_invalid");
            var id = parts[2];
            var reference = query.GetValueOrDefault("ref") ?? "";
            selectedNamespace = ns; selectedId = id; selectedRef = reference;
            if (mode == "record")
            {
                Only(query, "ref");
                var asset = resolved ?? _store.Read(ns, id, reference);
                data = Preview(asset);
                data["contract"] = CortexAssetStoreV2.Contract;
                data["namespace"] = ns;
                Add(data, "license", asset.Metadata.License);
                Add(data, "source", asset.Metadata.Source);
                Add(data, "pack", asset.Metadata.Pack);
                Add(data, "packVersion", asset.Metadata.PackVersion);
                Add(data, "parameters", asset.Metadata.Parameters);
                Add(data, "detailsRef", asset.Metadata.DetailsRef);
                if (asset.Metadata.DetailsRef.Length > 0)
                    data["detailsUri"] = ReferencePrefix + asset.Metadata.DetailsRef;
                return Text(value, data);
            }
            Only(query, "ref", "offset", "length");
            var offset = Long(query, "offset", 0);
            var length = Integer(query, "length", CortexAssetStoreV2.ChunkSize);
            var metadata = resolved ?? _store.Read(ns, id, reference);
            var bytes = await _store.ReadBytesAsync(ns, id, reference, offset, length, cancellationToken);
            return new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject { ["uri"] = value,
                    ["mimeType"] = metadata.Metadata.MediaType, ["blob"] = Convert.ToBase64String(bytes.Bytes) }),
                ["_meta"] = new JsonObject { ["contract"] = CortexAssetStoreV2.Contract, ["namespace"] = ns,
                    ["id"] = id, ["ref"] = reference, ["offset"] = bytes.Offset, ["length"] = bytes.Bytes.Length,
                    ["totalSize"] = bytes.TotalSize, ["sha256"] = bytes.Sha256, ["contentSha256"] = bytes.ContentSha256,
                    ["complete"] = bytes.Offset == 0 && bytes.Bytes.Length == bytes.TotalSize }
            };
        }
        catch (CortexAssetException error) { return Report(error.Code); }
        catch (IOException) { return Report("asset_file_unavailable"); }
        catch (UnauthorizedAccessException) { return Failure(value, "asset_access_denied"); }
        catch (FormatException) { return Failure(value, "asset_uri_invalid"); }
        catch (OverflowException) { return Failure(value, "asset_uri_invalid"); }
        catch (DecoderFallbackException) { return Report("asset_metadata_integrity_failed"); }
        JsonObject Report(string code)
        {
            var logged = selectedNamespace is not null && selectedId is not null && selectedRef is not null &&
                _store.RecordIssue(selectedNamespace, selectedId, selectedRef, code);
            return Text(value, new JsonObject { ["contract"] = CortexAssetStoreV2.Contract,
                ["ok"] = false, ["error"] = code, ["repairRecorded"] = logged });
        }
    }

    internal static string SearchUri(string ns, string query, int limit = 4, string? cursor = null) =>
        Prefix + "search/" + ns + "?q=" + Uri.EscapeDataString(query) + "&limit=" + limit +
        (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor));
    internal static string RecordUri(CortexAssetV2 asset) => ReferencePrefix + asset.Ref;
    internal static string BytesUri(CortexAssetV2 asset) => RecordUri(asset) + "?part=bytes";
    private static JsonObject Preview(CortexAssetV2 asset, bool compact = false)
    {
        var m = asset.Metadata;
        var meaning = compact ? string.Concat(m.Meaning.EnumerateRunes().Take(160).Select(rune => rune.ToString())) : m.Meaning;
        var result = new JsonObject { ["id"] = m.Id, ["ref"] = asset.Ref, ["title"] = m.Title,
            ["meaning"] = meaning, ["mimeType"] = m.MediaType, ["size"] = asset.Size };
        if (compact)
        {
            result["uri"] = RecordUri(asset);
            if (meaning != m.Meaning) result["preview"] = true;
        }
        else
        {
            result["sha256"] = asset.Sha256; result["metadataUri"] = RecordUri(asset); result["bytesUri"] = BytesUri(asset);
            Add(result, "tags", m.Tags);
        }
        Add(result, "alias", m.Alias); Add(result, "text", m.Text);
        return result;
    }
    private static void Add(JsonObject target, string name, string value) { if (value.Length > 0) target[name] = value; }
    private static JsonObject Text(string uri, JsonObject data) => new()
    {
        ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = "application/json",
            ["text"] = data.ToJsonString(JsonOptions) })
    };
    private static JsonObject Failure(string uri, string code) => Text(uri, new JsonObject
    { ["contract"] = CortexAssetStoreV2.Contract, ["ok"] = false, ["error"] = code });
    private static Dictionary<string, string> Query(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 1 || !result.TryAdd(pair[..separator], Uri.UnescapeDataString(pair[(separator + 1)..])))
                throw new CortexAssetException("asset_uri_invalid");
        }
        return result;
    }
    private static void Only(Dictionary<string, string> fields, params string[] allowed)
    { if (fields.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new CortexAssetException("asset_uri_invalid"); }
    private static int Integer(Dictionary<string, string> fields, string key, int fallback) =>
        fields.TryGetValue(key, out var value) ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
    private static long Long(Dictionary<string, string> fields, string key, long fallback) =>
        fields.TryGetValue(key, out var value) ? long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
}

