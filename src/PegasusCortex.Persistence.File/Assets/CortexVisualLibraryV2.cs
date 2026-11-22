using Pegasus.Cortex.Contracts.Assets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Persistence.File.Assets;

public sealed record CortexVisualLibraryHeaderV2(string Contract, string Namespace, string Pack,
    string Version, string Upstream, string Revision, string License);
public sealed record CortexVisualLibraryEntryV2(string File, string Sha256,
    CortexAssetSpecV2 Metadata, string? LicenseId);
public sealed record CortexVisualLibrarySampleV2(string Id, string Ref, string Uri,
    string BytesUri, string LicenseUri, string Sha256, long Size, string License);
public sealed record CortexVisualLibraryReceiptV2(bool Ok, string Contract, string Namespace,
    string Pack, string Version, string ManifestSha256, int References, int Created,
    int Unchanged, long ArtworkBytes, int ArtworkCount, CortexVisualLibrarySampleV2? Sample);

/// <summary>
/// Explicit passive data admission. A pinned streaming manifest binds original bytes,
/// metadata and licensing; this never opens semantic SQLite or executes artwork.
/// </summary>
public static class CortexVisualLibraryV2
{
    public const string Contract = "pegasus.visual-library/1";
    public const int MaximumEntries = 50000;
    public const int MaximumAssetBytes = 2 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<CortexVisualLibraryReceiptV2> ImportAsync(ICortexAssetStoreV2 store,
        string manifest, string pin, CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        CortexAssetStoreV2.RequireHash(pin);
        CortexAssetStoreV2.CheckPath(manifest);
        if (!Path.IsPathFullyQualified(manifest)) throw new InvalidDataException("Manifest must be an absolute file.");
        manifest = Path.GetFullPath(manifest);
        // Hold the manifest stable throughout preflight and admission. Source files
        // are independently rebound by digest immediately before each commit.
        await using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        if (stream.Length is < 1 or > 128 * 1024 * 1024) throw new InvalidDataException("Manifest size invalid.");
        if (Hex(await SHA256.HashDataAsync(stream, cancellationToken)) != pin)
            throw new CortexAssetException("asset_integrity_failed");
        stream.Position = 0;
        var headerText = ReadLine(stream) ?? throw new InvalidDataException("Header absent.");
        using (var document = JsonDocument.Parse(headerText)) Unique(document.RootElement);
        var header = JsonSerializer.Deserialize<CortexVisualLibraryHeaderV2>(headerText, Json)
            ?? throw new InvalidDataException("Header absent.");
        if (header.Contract != Contract || header.Namespace != "knowledge.shared" ||
            header.Pack is null || !header.Pack.StartsWith("visual.", StringComparison.Ordinal) ||
            header.Version != header.Revision || header.Revision is null || header.Revision.Length != 40 ||
            header.Revision.Any(c => !char.IsAsciiHexDigit(c)) ||
            !Uri.TryCreate(header.Upstream, UriKind.Absolute, out var upstream) || upstream.Scheme != "https")
            throw new InvalidDataException("Unsupported visual-library identity.");
        CortexAssetStoreV2.RequireName(header.Pack, 127);
        var root = Path.GetDirectoryName(manifest)!;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var licenses = new Dictionary<string, CortexVisualLibraryEntryV2>(StringComparer.Ordinal);
        var existingLicenses = new Dictionary<string, string>(StringComparer.Ordinal);
        var artworkStarted = false;
        var references = 0;
        // All conflicts and source/license pins are checked before the first write.
        // Only bounded IDs and <=32 licensing entries are retained, never artwork bodies.
        foreach (var entry in Entries(stream))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Metadata is null || ++references > MaximumEntries || !identifiers.Add(entry.Metadata.Id))
                throw new InvalidDataException("Absent/duplicate/oversized inventory.");
            CortexAssetStoreV2.Validate(entry.Metadata);
            CortexAssetStoreV2.RequireHash(entry.Sha256);
            if (entry.Metadata.Namespace != header.Namespace || entry.Metadata.Pack != header.Pack ||
                entry.Metadata.PackVersion != header.Version ||
                !entry.Metadata.Id.StartsWith(header.Pack + ".", StringComparison.Ordinal) ||
                entry.Metadata.DetailsRef != "" ||
                entry.Metadata.License is not ("MIT" or "Apache-2.0" or "CC-BY-4.0" or "CC-BY-SA-4.0"))
                throw new InvalidDataException("Entry identity/license invalid.");
            await using (var input = new FileStream(Source(root, entry.File), FileMode.Open, FileAccess.Read, FileShare.Read))
                if (input.Length is < 1 or > MaximumAssetBytes ||
                    Hex(await SHA256.HashDataAsync(input, cancellationToken)) != entry.Sha256)
                    throw new CortexAssetException("asset_integrity_failed");
            var old = store.Current(header.Namespace, entry.Metadata.Id);
            var details = "";
            if (entry.LicenseId is null)
            {
                if (artworkStarted || entry.Metadata.MediaType != "text/plain" || licenses.Count >= 32)
                    throw new InvalidDataException("License/notice entries must precede artwork.");
                licenses.Add(entry.Metadata.Id, entry);
                if (old is not null) existingLicenses.Add(entry.Metadata.Id, old.Ref);
            }
            else
            {
                artworkStarted = true;
                if (!licenses.TryGetValue(entry.LicenseId, out var license) ||
                    license.Metadata.License != entry.Metadata.License || entry.Metadata.MediaType != "image/svg+xml")
                    throw new InvalidDataException("Artwork license link invalid.");
                if (old is not null && !existingLicenses.TryGetValue(entry.LicenseId, out details!))
                    throw new InvalidDataException("Existing artwork lost its licensing head.");
            }
            if (old is not null && (old.Sha256 != entry.Sha256 ||
                old.Metadata != (entry.Metadata with { DetailsRef = details })))
                throw new CortexAssetException("asset_pack_revision_conflict");
        }
        if (!artworkStarted) throw new InvalidDataException("Artwork inventory empty.");
        stream.Position = 0;
        _ = ReadLine(stream);
        var licenseRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        int created = 0, unchanged = 0, artCount = 0, committed = 0;
        long artworkBytes = 0;
        CortexVisualLibrarySampleV2? sample = null;
        try
        {
            foreach (var entry in Entries(stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var spec = entry.Metadata with { DetailsRef = entry.LicenseId is null ? "" : licenseRefs[entry.LicenseId] };
                var before = store.Current(header.Namespace, spec.Id);
                // Identical replay leaves refs/generation unchanged. A divergent
                // concurrent head is never overwritten with an expected-ref override.
                var admitted = before is not null && before.Sha256 == entry.Sha256 && before.Metadata == spec
                    ? before
                    : await store.ImportAsync(Source(root, entry.File), spec,
                        cancellationToken: cancellationToken, expectedContentSha256: entry.Sha256);
                committed++;
                if (before is null) created++; else unchanged++;
                if (entry.LicenseId is null) licenseRefs.Add(spec.Id, admitted.Ref);
                else
                {
                    artCount++;
                    artworkBytes += admitted.Size;
                    sample ??= new(spec.Id, admitted.Ref, "cortex://" + admitted.Ref,
                        "cortex://" + admitted.Ref + "?part=bytes", "cortex://" + spec.DetailsRef,
                        admitted.Sha256, admitted.Size, spec.License);
                }
                if (committed % 1000 == 0) progress?.Invoke($"stage=admit pack={header.Pack} references={committed}");
            }
        }
        catch (CortexAssetException error) { throw new CortexAssetException(error.Code, committed); }
        catch (IOException) { throw new CortexAssetException("asset_file_unavailable", committed); }
        catch (OperationCanceledException) { throw new CortexAssetException("asset_import_cancelled", committed); }
        return new(true, Contract, header.Namespace, header.Pack, header.Version,
            pin, references, created, unchanged, artworkBytes, artCount, sample);
    }

    private static IEnumerable<CortexVisualLibraryEntryV2> Entries(Stream stream)
    {
        string? line;
        while ((line = ReadLine(stream)) is not null)
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            Unique(document.RootElement);
            yield return JsonSerializer.Deserialize<CortexVisualLibraryEntryV2>(line, Json)
                ?? throw new InvalidDataException("Entry absent.");
        }
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Unique(item);
    }

    private static string? ReadLine(Stream stream)
    {
        // Reject before decoding; malformed inputs cannot allocate a giant line.
        var bytes = new List<byte>(2048);
        int next;
        while ((next = stream.ReadByte()) >= 0 && next != '\n')
        {
            if (bytes.Count >= 32768) throw new InvalidDataException("Manifest line oversized.");
            bytes.Add((byte)next);
        }
        if (next < 0 && bytes.Count == 0) return null;
        return Utf8.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private static string Source(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':') ||
            Path.IsPathRooted(relative) || relative.Split('/').Any(s => s is "" or "." or ".."))
            throw new InvalidDataException("Unsafe pack path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pack path escapes root.");
        CortexAssetStoreV2.CheckPath(path);
        return path;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

