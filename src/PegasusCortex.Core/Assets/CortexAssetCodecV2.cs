using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Assets;

namespace Pegasus.Cortex.Core.Assets;

/// <summary>One canonical assets/2 codec shared by physical providers. Storage policy is not identity.</summary>
public static class CortexAssetCodecV2
{
    public const string Contract = "pegasus.assets/2";
    public const int ChunkSize = 65536;
    public const int MaximumMetadataBytes = 32768;
    public const int MaximumScan = 512;
    public const long MaximumAssetBytes = 2147483648L;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Validate(CortexAssetSpecV2 spec)
    {
        if (spec is null || new[] { spec.Namespace, spec.Id, spec.Title, spec.Meaning, spec.MediaType,
            spec.Tags, spec.Alias, spec.Text, spec.License, spec.Source, spec.Pack, spec.PackVersion,
            spec.Parameters, spec.DetailsRef }.Any(value => value is null))
            throw new CortexAssetException("asset_metadata_invalid");
        RequireName(spec.Namespace, 63); RequireName(spec.Id, 127);
        foreach (var (value, maximum) in new[] { (spec.Title, 128), (spec.Meaning, 512),
                     (spec.Tags, 256), (spec.Alias, 127), (spec.Text, 32), (spec.License, 128),
                     (spec.Pack, 127), (spec.PackVersion, 64), (spec.Source, 2048) })
            if (value.Length > maximum || value.Any(char.IsControl))
                throw new CortexAssetException("asset_metadata_invalid");
        if (spec.MediaType.Length > 100 || !spec.MediaType.Contains('/') ||
            spec.MediaType.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '.' and not '+' and not '-'))
            throw new CortexAssetException("asset_media_type_invalid");
        if (CountUtf8(spec.Parameters) > 16384) throw new CortexAssetException("asset_parameters_oversized");
        if (spec.Parameters.Length > 0)
        {
            try { using var json = JsonDocument.Parse(spec.Parameters, new() { MaxDepth = 32 }); }
            catch (JsonException) { throw new CortexAssetException("asset_parameters_json_invalid"); }
        }
        if (spec.DetailsRef.Length > 0) RequireHash(spec.DetailsRef);
        if (spec.Source.Length > 0 && (!Uri.TryCreate(spec.Source, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0))
            throw new CortexAssetException("asset_source_invalid");
    }

    public static void RequireName(string value, int maximum)
    {
        if (value is null || value.Length is < 1 || value.Length > maximum || !char.IsAsciiLetterLower(value[0]) ||
            value.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not '.' and not '-' and not '_'))
            throw new CortexAssetException("asset_identifier_invalid");
    }

    public static void RequireHash(string value)
    {
        if (value is null || value.Length != 64 ||
            value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new CortexAssetException("asset_reference_invalid");
    }

    public static string Serialize(CortexAssetSpecV2 m, string sha, long size, string chunks)
    {
        Validate(m); RequireHash(sha); RequireHash(chunks);
        if (size is < 0 or > MaximumAssetBytes) throw new CortexAssetException("asset_metadata_invalid");
        var result = string.Join('\n', new[] { "[asset]", "contract=" + Contract, "namespace=" + m.Namespace, "id=" + m.Id,
            "title=" + Escape(m.Title), "meaning=" + Escape(m.Meaning), "media-type=" + m.MediaType,
            "tags=" + Escape(m.Tags), "alias=" + Escape(m.Alias), "text=" + Escape(m.Text),
            "license=" + Escape(m.License), "source=" + Escape(m.Source), "pack=" + Escape(m.Pack),
            "pack-version=" + Escape(m.PackVersion), "parameters=" + Escape(m.Parameters), "details-ref=" + m.DetailsRef,
            "sha256=" + sha, "size=" + size.ToString(CultureInfo.InvariantCulture), "chunks-sha256=" + chunks, "" });
        if (CountUtf8(result) > MaximumMetadataBytes) throw new CortexAssetException("asset_metadata_oversized");
        return result;
    }

    public static string Reference(string canonical)
    {
        if (canonical is null || CountUtf8(canonical) > MaximumMetadataBytes)
            throw new CortexAssetException("asset_metadata_oversized");
        return Hex(SHA256.HashData(Utf8.GetBytes(canonical)));
    }

    public static CortexAssetV2 Parse(string reference, string canonical)
    {
        RequireHash(reference);
        if (Reference(canonical) != reference) throw new CortexAssetException("asset_metadata_integrity_failed");
        var lines = canonical.Split('\n');
        if (lines.Length < 2 || lines[0] != "[asset]") throw new CortexAssetException("asset_metadata_invalid");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var separator = line.IndexOf('=');
            if (separator < 1 || !fields.TryAdd(line[..separator], Unescape(line[(separator + 1)..])))
                throw new CortexAssetException("asset_metadata_invalid");
        }
        string Field(string key) => fields.GetValueOrDefault(key) ?? "";
        var spec = new CortexAssetSpecV2(Field("namespace"), Field("id"), Field("title"), Field("meaning"),
            Field("media-type"), Field("tags"), Field("alias"), Field("text"), Field("license"), Field("source"),
            Field("pack"), Field("pack-version"), Field("parameters"), Field("details-ref"));
        Validate(spec);
        if (Field("contract") != Contract ||
            !long.TryParse(Field("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size is < 0 or > MaximumAssetBytes)
            throw new CortexAssetException("asset_metadata_invalid");
        var sha = Field("sha256"); var chunks = Field("chunks-sha256");
        RequireHash(sha); RequireHash(chunks);
        if (Serialize(spec, sha, size, chunks) != canonical) throw new CortexAssetException("asset_metadata_invalid");
        return new(spec, reference, sha, size, chunks);
    }

    public static int ValidateRange(long size, long offset, int length)
    {
        if (size is < 0 or > MaximumAssetBytes || offset < 0 || offset > size ||
            offset % ChunkSize != 0 || length is < 1 or > 2 * ChunkSize)
            throw new CortexAssetException("asset_range_invalid");
        return (int)Math.Min(length, size - offset);
    }

    public static void ValidateChunkIndex(CortexAssetV2 asset, byte[] index)
    {
        if (asset is null || asset.Size is < 0 or > MaximumAssetBytes)
            throw new CortexAssetException("asset_integrity_failed");
        var count = (asset.Size + ChunkSize - 1) / ChunkSize;
        if (index is null || index.LongLength != count * 65 || Hex(SHA256.HashData(index)) != asset.ChunksSha256)
            throw new CortexAssetException("asset_integrity_failed");
        for (var offset = 0; offset < index.Length; offset += 65)
        {
            if (index[offset + 64] != (byte)'\n') throw new CortexAssetException("asset_integrity_failed");
            for (var i = 0; i < 64; i++)
                if (index[offset + i] is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f'))
                    throw new CortexAssetException("asset_integrity_failed");
        }
    }

    public static void VerifyChunk(ReadOnlySpan<byte> bytes, string expectedHash)
    {
        RequireHash(expectedHash);
        if (Hex(SHA256.HashData(bytes)) != expectedHash) throw new CortexAssetException("asset_integrity_failed");
    }

    public static string[] ValidateSearch(string ns, string query, int limit)
    {
        RequireName(ns, 63);
        if (query is null || query.Length > 256 || limit is < 1 or > 16)
            throw new CortexAssetException("asset_search_invalid");
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 8) throw new CortexAssetException("asset_search_invalid");
        return words;
    }

    public static bool Matches(CortexAssetSpecV2 metadata, IReadOnlyList<string> words)
    {
        var searchable = string.Join(' ', metadata.Id, metadata.Alias, metadata.Title,
            metadata.Meaning, metadata.Tags, metadata.Text);
        return words.All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountUtf8(string text)
    {
        try { return Utf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw new CortexAssetException("asset_metadata_invalid"); }
    }
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);
    private static string Unescape(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { result.Append(text[i]); continue; }
            if (++i >= text.Length) throw new CortexAssetException("asset_metadata_invalid");
            result.Append(text[i] switch { '\\' => '\\', 'r' => '\r', 'n' => '\n', 't' => '\t',
                _ => throw new CortexAssetException("asset_metadata_invalid") });
        }
        return result.ToString();
    }
}

