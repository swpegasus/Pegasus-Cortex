using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Contracts.Assets;

namespace Pegasus.Cortex.Core.Assets;

/// <summary>One explicitly selected reference and its allowed destination head.</summary>
public sealed record CortexAssetPackageCoordinateV1(string Namespace, string Id, string Ref,
    string? ExpectedHeadRef = null);

public sealed record CortexAssetPackageOptionsV1(
    int MaximumEntries = 50000,
    int MaximumMetadataUtf8Bytes = 32768,
    int MaximumManifestUtf8Bytes = 64 * 1024 * 1024,
    long MaximumAssetBytes = 256L * 1024 * 1024,
    long MaximumExpandedBytes = 1024L * 1024 * 1024,
    long MaximumCompressedBytes = 1024L * 1024 * 1024);

/// <summary>
/// Committed counts confirmed successful reference admissions, including idempotent admissions.
/// Unchanged is the subset observed already current before admission, not an atomic row-change disposition.
/// </summary>
public sealed record CortexAssetPackageResultV1(string Contract, int References, int Payloads,
    long PayloadBytes, int Committed, int Unchanged, long CompressedBytes, long ExpandedBytes);

/// <summary>
/// Committed counts confirmed completed admissions before failure, including idempotent admissions.
/// It does not prove that an in-flight backend call which failed or became uncertain had no effect.
/// </summary>
public sealed class CortexAssetPackageException(string code, int committed = 0, Exception? inner = null)
    : IOException(code, inner)
{
    public string Code { get; } = code;
    public int Committed { get; } = committed;
}

/// <summary>
/// Passive gzip/USTAR interchange. Caller streams stay open; no paths are extracted.
/// Manifest scope, dependency closure and destination conflicts precede all imports.
/// Later stream/integrity/cancellation failures can leave confirmed per-asset commits.
/// </summary>
public static class CortexAssetPackageV1
{
    public const string Contract = "pegasus.assets.bundle/1";
    public const string ManifestEntryName = "manifest.v1.json";
    public const string CompletionEntryName = "complete.v1.json";
    private const string CompletionSchema = "pegasus.assets.bundle-complete/1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    private sealed record Manifest(string Schema, IReadOnlyList<ManifestAsset> Assets);
    private sealed record ManifestAsset(string Namespace, string Id, string Ref,
        string CanonicalMetadata, string? ExpectedHeadRef);
    private sealed record Completion(string Schema, string ManifestSha256, int Payloads, long PayloadBytes);
    private sealed record Selected(ManifestAsset Manifest, CortexAssetV2 Asset);
    private sealed record Plan(IReadOnlyList<Selected> References, IReadOnlyList<Selected> Payloads, long PayloadBytes);

    public static async Task<CortexAssetPackageResultV1> ExportAsync(ICortexAssetStoreV2 store,
        IReadOnlyList<CortexAssetPackageCoordinateV1> coordinates, Stream destination,
        CortexAssetPackageOptionsV1? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(destination);
        var limits = Validate(options ?? new());
        try
        {
            Require(destination.CanWrite && coordinates.Count is > 0 && coordinates.Count <= limits.MaximumEntries,
                "asset_package_arguments_invalid");
            // Freeze public input before the first asynchronous operation.
            var selected = new List<ManifestAsset>(coordinates.Count);
            foreach (var coordinate in coordinates.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(coordinate is not null, "asset_package_coordinate_invalid");
                var asset = store.Read(coordinate.Namespace, coordinate.Id, coordinate.Ref);
                var canonical = CortexAssetCodecV2.Serialize(asset.Metadata, asset.Sha256, asset.Size, asset.ChunksSha256);
                Require(asset.Ref == coordinate.Ref && asset.Metadata.Namespace == coordinate.Namespace &&
                    asset.Metadata.Id == coordinate.Id, "asset_package_coordinate_invalid");
                selected.Add(new(coordinate.Namespace, coordinate.Id, coordinate.Ref, canonical, coordinate.ExpectedHeadRef));
            }
            var plan = BuildPlan(new(Contract, selected), null, limits, store.MaximumAssetBytes, cancellationToken);
            var manifest = new Manifest(Contract, plan.References.Select(item => item.Manifest).ToArray());
            var manifestBytes = await SerializeBoundedAsync(manifest, limits.MaximumManifestUtf8Bytes, cancellationToken)
                .ConfigureAwait(false);
            var completionBytes = JsonSerializer.SerializeToUtf8Bytes(new Completion(CompletionSchema,
                Hex(SHA256.HashData(manifestBytes)), plan.Payloads.Count, plan.PayloadBytes), Json);
            RequireMinimumExpanded(plan, manifestBytes.Length, completionBytes.Length, limits);
            using var compressed = new LimitedStream(destination, limits.MaximumCompressedBytes, "asset_package_compressed_limit");
            long expandedBytes;
            await using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                using var expanded = new LimitedStream(gzip, limits.MaximumExpandedBytes, "asset_package_expanded_limit");
                await using (var tar = new TarWriter(expanded, TarEntryFormat.Ustar, leaveOpen: true))
                {
                    using var manifestStream = new MemoryStream(manifestBytes, writable: false);
                    await WriteEntryAsync(tar, ManifestEntryName, manifestStream, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var payload in plan.Payloads)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var source = new StorePayloadStream(store, payload.Asset);
                        using var verified = new VerifiedPayloadStream(source, payload.Asset, seekableLength: true);
                        await WriteEntryAsync(tar, ObjectName(payload.Asset.Sha256), verified, cancellationToken).ConfigureAwait(false);
                        verified.RequireComplete();
                    }
                    using var completionStream = new MemoryStream(completionBytes, writable: false);
                    await WriteEntryAsync(tar, CompletionEntryName, completionStream, cancellationToken)
                        .ConfigureAwait(false);
                }
                expandedBytes = expanded.Count;
            }
            return new(Contract, plan.References.Count, plan.Payloads.Count, plan.PayloadBytes,
                0, 0, compressed.Count, expandedBytes);
        }
        catch (CortexAssetPackageException) { throw; }
        catch (OperationCanceledException error) { throw new CortexAssetPackageException("asset_package_cancelled", 0, error); }
        catch (CortexAssetException error) { throw new CortexAssetPackageException(error.Code, 0, error); }
        catch (Exception error) when (DataFailure(error)) { throw new CortexAssetPackageException("asset_package_invalid", 0, error); }
    }

    public static async Task<CortexAssetPackageResultV1> ImportAsync(ICortexAssetStreamStoreV1 store,
        Stream source, IReadOnlyList<string> namespaces, CortexAssetPackageOptionsV1? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(namespaces);
        var limits = Validate(options ?? new());
        var committed = 0;
        var unchanged = 0;
        try
        {
            Require(source.CanRead && namespaces.Count is > 0 and <= 32, "asset_package_arguments_invalid");
            var scope = namespaces.ToArray();
            foreach (var ns in scope) CortexAssetCodecV2.RequireName(ns, 63);
            Require(scope.Distinct(StringComparer.Ordinal).Count() == scope.Length, "asset_package_scope_invalid");
            using var compressed = new LimitedStream(source, limits.MaximumCompressedBytes, "asset_package_compressed_limit", gzipEnvelope: true);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            using var expanded = new LimitedStream(gzip, limits.MaximumExpandedBytes, "asset_package_expanded_limit", crc32: true);
            using var framed = new UstarInputStream(expanded, limits);
            using var tar = new TarReader(framed, leaveOpen: true);
            var first = await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
            RequireEntry(first, ManifestEntryName, limits.MaximumManifestUtf8Bytes);
            var manifestBytes = await ReadEntryAsync(first!, limits.MaximumManifestUtf8Bytes, cancellationToken).ConfigureAwait(false);
            var manifest = DeserializeManifest(manifestBytes, limits);
            var plan = BuildPlan(manifest, scope, limits, store.MaximumAssetBytes, cancellationToken);
            var expectedCompletion = new Completion(CompletionSchema, Hex(SHA256.HashData(manifestBytes)),
                plan.Payloads.Count, plan.PayloadBytes);
            var completionBytes = JsonSerializer.SerializeToUtf8Bytes(expectedCompletion, Json);
            RequireMinimumExpanded(plan, manifestBytes.Length, completionBytes.Length, limits);
            // Every namespace, license dependency and known destination conflict is checked before effects.
            foreach (var item in plan.References)
            { cancellationToken.ThrowIfCancellationRequested(); _ = DestinationHead(store, item); }
            var position = 0;
            var available = new HashSet<string>(StringComparer.Ordinal);
            foreach (var payload in plan.Payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
                RequireEntry(entry, ObjectName(payload.Asset.Sha256), payload.Asset.Size);
                Require(entry!.Length == payload.Asset.Size, "asset_package_payload_size_invalid");
                Require(position < plan.References.Count && plan.References[position].Asset.Ref == payload.Asset.Ref,
                    "asset_package_payload_order_invalid");
                var firstReference = plan.References[position];
                var head = DestinationHead(store, firstReference);
                using var verified = new VerifiedPayloadStream(entry.DataStream ?? Stream.Null, payload.Asset);
                var imported = await store.ImportStreamAsync(verified, payload.Asset.Size, firstReference.Asset.Metadata,
                    head.Expected, cancellationToken, payload.Asset.Sha256).ConfigureAwait(false);
                // This is confirmed admission, not an atomic physical-change disposition.
                committed++; if (head.Unchanged) unchanged++;
                verified.RequireComplete();
                RequireSame(imported, firstReference.Asset);
                available.Add(payload.Asset.Sha256);
                position++;
                // A single global topological walk preserves dependencies even when payloads are shared.
                while (position < plan.References.Count && available.Contains(plan.References[position].Asset.Sha256))
                {
                    var item = plan.References[position];
                    head = DestinationHead(store, item);
                    imported = await store.ImportExistingObjectAsync(item.Asset.Sha256, item.Asset.Metadata,
                        head.Expected, cancellationToken).ConfigureAwait(false);
                    committed++; if (head.Unchanged) unchanged++;
                    RequireSame(imported, item.Asset);
                    position++;
                }
            }
            Require(position == plan.References.Count, "asset_package_references_incomplete");
            var last = await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
            RequireEntry(last, CompletionEntryName, 4096);
            var completion = Deserialize<Completion>(await ReadEntryAsync(last!, 4096, cancellationToken).ConfigureAwait(false));
            Require(completion == expectedCompletion, "asset_package_completion_invalid");
            Require(await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is null,
                "asset_package_extra_entry");
            // Force decompression to EOF/CRC processing. Only tar's zero padding may remain.
            var padding = new byte[65536];
            int count;
            while ((count = await framed.ReadAsync(padding, cancellationToken).ConfigureAwait(false)) != 0)
                Require(padding.AsSpan(0, count).IndexOfAnyExcept((byte)0) < 0, "asset_package_trailing_data");
            if (await compressed.ReadAsync(padding.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                throw new CortexAssetPackageException("asset_package_trailing_data");
            compressed.ValidateGzipEnvelope(expanded.Crc32, expanded.Count);
            return new(Contract, plan.References.Count, plan.Payloads.Count, plan.PayloadBytes,
                committed, unchanged, compressed.Count, expanded.Count);
        }
        catch (CortexAssetPackageException error)
        { throw new CortexAssetPackageException(error.Code, committed, error); }
        catch (OperationCanceledException error)
        { throw new CortexAssetPackageException("asset_package_cancelled", committed, error); }
        catch (CortexAssetException error)
        { throw new CortexAssetPackageException(error.Code, committed, error); }
        catch (Exception error) when (DataFailure(error))
        { throw new CortexAssetPackageException("asset_package_invalid", committed, error); }
    }

    private static Plan BuildPlan(Manifest manifest, IReadOnlyList<string>? namespaces,
        CortexAssetPackageOptionsV1 options, long backendMaximum, CancellationToken cancellationToken)
    {
        Require(manifest is not null && manifest.Schema == Contract && manifest.Assets is not null &&
            manifest.Assets.Count is > 0 && manifest.Assets.Count <= options.MaximumEntries, "asset_package_manifest_invalid");
        var references = new List<Selected>(manifest.Assets.Count);
        var byRef = new Dictionary<string, int>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(item is not null && item.CanonicalMetadata is not null &&
                Utf8.GetByteCount(item.CanonicalMetadata) <= options.MaximumMetadataUtf8Bytes, "asset_package_metadata_invalid");
            var asset = CortexAssetCodecV2.Parse(item.Ref, item.CanonicalMetadata);
            Require(asset.Metadata.Namespace == item.Namespace && asset.Metadata.Id == item.Id &&
                asset.Size <= options.MaximumAssetBytes && asset.Size <= backendMaximum, "asset_package_coordinate_invalid");
            Require(namespaces is null || namespaces.Contains(item.Namespace, StringComparer.Ordinal), "asset_package_namespace_rejected");
            if (item.ExpectedHeadRef is not null) CortexAssetCodecV2.RequireHash(item.ExpectedHeadRef);
            Require(byRef.TryAdd(item.Ref, references.Count) && identities.Add(item.Namespace + "\0" + item.Id),
                "asset_package_duplicate_reference");
            references.Add(new(item, asset));
        }
        var dependents = Enumerable.Range(0, references.Count).Select(_ => new List<int>()).ToArray();
        var incoming = new int[references.Count];
        for (var index = 0; index < references.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = references[index].Asset;
            if (asset.Metadata.DetailsRef.Length == 0) continue;
            Require(byRef.TryGetValue(asset.Metadata.DetailsRef, out var dependency), "asset_package_dependency_missing");
            var license = references[dependency].Asset;
            Require(license.Metadata.Namespace == asset.Metadata.Namespace &&
                (asset.Metadata.License.Length == 0 || license.Metadata.License == asset.Metadata.License),
                "asset_package_dependency_scope_invalid");
            incoming[index]++;
            dependents[dependency].Add(index);
        }
        var ready = new PriorityQueue<int, int>();
        for (var index = 0; index < incoming.Length; index++) if (incoming[index] == 0) ready.Enqueue(index, index);
        var ordered = new List<Selected>(references.Count);
        while (ready.TryDequeue(out var index, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordered.Add(references[index]);
            foreach (var child in dependents[index]) if (--incoming[child] == 0) ready.Enqueue(child, child);
        }
        Require(ordered.Count == references.Count, "asset_package_dependency_cycle");
        var objects = new Dictionary<string, Selected>(StringComparer.Ordinal);
        var payloads = new List<Selected>();
        long bytes = 0;
        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (objects.TryGetValue(item.Asset.Sha256, out var existing))
                Require(existing.Asset.Size == item.Asset.Size && existing.Asset.ChunksSha256 == item.Asset.ChunksSha256,
                    "asset_package_payload_identity_invalid");
            else { objects.Add(item.Asset.Sha256, item); payloads.Add(item); bytes = checked(bytes + item.Asset.Size); }
        }
        Require(bytes <= options.MaximumExpandedBytes, "asset_package_expanded_limit");
        return new(ordered, payloads, bytes);
    }

    private static (string? Expected, bool Unchanged) DestinationHead(ICortexAssetStoreV2 store, Selected item)
    {
        var current = store.Current(item.Asset.Metadata.Namespace, item.Asset.Metadata.Id);
        if (current?.Ref == item.Asset.Ref) { RequireSame(current, item.Asset); return (item.Asset.Ref, true); }
        Require(current?.Ref == item.Manifest.ExpectedHeadRef, "asset_package_head_conflict");
        return (item.Manifest.ExpectedHeadRef, false);
    }

    private static void RequireSame(CortexAssetV2 actual, CortexAssetV2 expected) =>
        Require(actual == expected, "asset_package_backend_identity_invalid");

    private static CortexAssetPackageOptionsV1 Validate(CortexAssetPackageOptionsV1 options)
    {
        Require(options.MaximumEntries is > 0 and <= 50000 && options.MaximumMetadataUtf8Bytes is > 0 and <= 32768 &&
            options.MaximumManifestUtf8Bytes is > 0 and <= 128 * 1024 * 1024 &&
            options.MaximumAssetBytes is > 0 and <= CortexAssetCodecV2.MaximumAssetBytes &&
            options.MaximumExpandedBytes > 0 && options.MaximumCompressedBytes > 0 &&
            options.MaximumManifestUtf8Bytes <= options.MaximumExpandedBytes, "asset_package_limits_invalid");
        return options;
    }

    private static void RequireMinimumExpanded(Plan plan, int manifestSize, int completionSize, CortexAssetPackageOptionsV1 limits)
    {
        static long Entry(long size) => checked(512 + (size + 511) / 512 * 512);
        var minimum = checked(Entry(manifestSize) + Entry(completionSize) + 1024);
        foreach (var payload in plan.Payloads) minimum = checked(minimum + Entry(payload.Asset.Size));
        Require(minimum <= limits.MaximumExpandedBytes, "asset_package_expanded_limit");
    }

    private static string ObjectName(string sha) => "objects/" + sha;
    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new CortexAssetPackageException(code); }
    private static bool DataFailure(Exception error) => error is IOException or JsonException or ArgumentException or
        OverflowException or InvalidOperationException or NotSupportedException;

    private static void RequireEntry(TarEntry? entry, string name, long maximum)
    {
        Require(entry is not null && entry.Format == TarEntryFormat.Ustar && entry.EntryType == TarEntryType.RegularFile &&
            entry.Name == name && string.IsNullOrEmpty(entry.LinkName) && entry.Length >= 0 && entry.Length <= maximum,
            "asset_package_entry_invalid");
    }

    private static async Task WriteEntryAsync(TarWriter writer, string name, Stream data, CancellationToken token)
    {
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = data };
        await writer.WriteEntryAsync(entry, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadEntryAsync(TarEntry entry, int maximum, CancellationToken token)
    {
        Require(entry.Length <= maximum, "asset_package_manifest_limit");
        var bytes = new byte[checked((int)entry.Length)];
        if (bytes.Length != 0)
        {
            Require(entry.DataStream is not null, "asset_package_entry_invalid");
            await entry.DataStream!.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        return bytes;
    }

    private static async Task<byte[]> SerializeBoundedAsync<T>(T value, int maximum, CancellationToken token)
    {
        using var memory = new MemoryStream();
        using var bounded = new LimitedStream(memory, maximum, "asset_package_manifest_limit");
        await JsonSerializer.SerializeAsync(bounded, value, Json, token).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new CortexAssetPackageException("asset_package_manifest_invalid");
    }

    private static Manifest DeserializeManifest(byte[] bytes, CortexAssetPackageOptionsV1 limits)
    {
        // Count/size admission precedes model allocation. The JSON document itself remains byte/depth bounded.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        long tokens = 0;
        var entries = 0;
        while (reader.Read())
        {
            Require(++tokens <= 16L * limits.MaximumEntries + 16, "asset_package_manifest_limit");
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 2)
                Require(++entries <= limits.MaximumEntries, "asset_package_manifest_limit");
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                Require(reader.ValueSpan.Length <= 6L * limits.MaximumMetadataUtf8Bytes, "asset_package_metadata_invalid");
            if (reader.TokenType == JsonTokenType.Number)
                Require(reader.ValueSpan.Length <= 64, "asset_package_manifest_invalid");
        }
        return Deserialize<Manifest>(bytes);
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { Require(names.Add(property.Name), "asset_package_manifest_invalid"); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) RejectDuplicates(value);
    }

    /// <summary>Bounds bytes actually crossing a layer without owning the caller stream.</summary>
    private sealed class LimitedStream(Stream inner, long maximum, string code,
        bool gzipEnvelope = false, bool crc32 = false) : Stream
    {
        private readonly GzipEnvelope? _envelope = gzipEnvelope ? new() : null;
        private readonly Crc32State? _crc = crc32 ? new() : null;
        public long Count { get; private set; }
        public uint Crc32 => _crc?.Value ?? throw new InvalidOperationException();
        public void ValidateGzipEnvelope(uint crc, long expandedBytes) =>
            (_envelope ?? throw new InvalidOperationException()).Validate(crc, expandedBytes);
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        private int ReadLength(int requested)
        {
            var remaining = maximum - Count;
            return Math.Min(requested, remaining >= int.MaxValue ? int.MaxValue : checked((int)remaining + 1));
        }
        private void Add(ReadOnlySpan<byte> bytes)
        {
            Count = checked(Count + bytes.Length); Require(Count <= maximum, code);
            _envelope?.Append(bytes); _crc?.Append(bytes);
        }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = inner.Read(buffer, offset, ReadLength(count)); Add(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = await inner.ReadAsync(buffer[..ReadLength(buffer.Length)], token).ConfigureAwait(false); Add(buffer.Span[..read]); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Write(byte[] buffer, int offset, int count)
        { Require(count <= maximum - Count, code); inner.Write(buffer, offset, count); Add(buffer.AsSpan(offset, count)); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Require(buffer.Length <= maximum - Count, code); await inner.WriteAsync(buffer, token).ConfigureAwait(false); Add(buffer.Span); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }

    /// <summary>
    /// Admit the fixed USTAR header before the framework can parse extension data or allocate it.
    /// Only 512 header bytes are buffered; regular payloads stay sequential and caller-owned.
    /// </summary>
    private sealed class UstarInputStream(Stream inner, CortexAssetPackageOptionsV1 limits) : Stream
    {
        private readonly byte[] _header = new byte[512];
        private int _headerPosition = 512;
        private long _contentRemaining;
        private int _paddingRemaining;
        private int _entries;
        private bool _completion;
        private bool _ended;
        private long _zeroTail;
        private long _declared;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            token.ThrowIfCancellationRequested();
            if (_headerPosition < 512)
            {
                var take = Math.Min(buffer.Length, 512 - _headerPosition);
                _header.AsMemory(_headerPosition, take).CopyTo(buffer); _headerPosition += take; return take;
            }
            if (_contentRemaining > 0)
            {
                var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _contentRemaining)], token).ConfigureAwait(false);
                Require(read > 0, "asset_package_payload_incomplete"); _contentRemaining -= read; return read;
            }
            if (_paddingRemaining > 0)
            {
                var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _paddingRemaining)], token).ConfigureAwait(false);
                Require(read > 0 && buffer.Span[..read].IndexOfAnyExcept((byte)0) < 0, "asset_package_padding_invalid");
                _paddingRemaining -= read; return read;
            }
            if (_ended)
            {
                var read = await inner.ReadAsync(buffer, token).ConfigureAwait(false);
                Require(buffer.Span[..read].IndexOfAnyExcept((byte)0) < 0, "asset_package_trailing_data");
                _zeroTail = checked(_zeroTail + read);
                if (read == 0) Require(_zeroTail >= 1024, "asset_package_truncated");
                return read;
            }
            await inner.ReadExactlyAsync(_header, token).ConfigureAwait(false);
            _headerPosition = 0;
            if (_header.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                Require(_completion, "asset_package_completion_invalid");
                _ended = true; _zeroTail = 512;
            }
            else AdmitHeader();
            return await ReadAsync(buffer, token).ConfigureAwait(false);
        }
        private void AdmitHeader()
        {
            Require(!_completion && ++_entries <= limits.MaximumEntries + 2 &&
                (_header[156] is 0 or (byte)'0') &&
                _header.AsSpan(257, 6).SequenceEqual("ustar\0"u8) && _header.AsSpan(263, 2).SequenceEqual("00"u8) &&
                _header.AsSpan(157, 100).IndexOfAnyExcept((byte)0) < 0 &&
                _header.AsSpan(345, 155).IndexOfAnyExcept((byte)0) < 0,
                "asset_package_entry_invalid");
            var length = Array.IndexOf(_header, (byte)0, 0, 100);
            if (length < 0) length = 100;
            Require(_header.AsSpan(length, 100 - length).IndexOfAnyExcept((byte)0) < 0,
                "asset_package_entry_invalid");
            var name = Encoding.ASCII.GetString(_header, 0, length);
            long maximum;
            if (_entries == 1)
            { Require(name == ManifestEntryName, "asset_package_entry_invalid"); maximum = limits.MaximumManifestUtf8Bytes; }
            else if (name == CompletionEntryName)
            { _completion = true; maximum = 4096; }
            else
            {
                Require(name.StartsWith("objects/", StringComparison.Ordinal) && name.Length == 72,
                    "asset_package_entry_invalid");
                CortexAssetCodecV2.RequireHash(name[8..]); maximum = limits.MaximumAssetBytes;
            }
            var encodedSize = Encoding.ASCII.GetString(_header, 124, 12).Trim('\0', ' ');
            Require(encodedSize.Length is > 0 and <= 11 && encodedSize.All(value => value is >= '0' and <= '7'),
                "asset_package_entry_invalid");
            long size = 0;
            foreach (var digit in encodedSize) size = checked(size * 8 + digit - '0');
            Require(size <= maximum, "asset_package_entry_invalid");
            _paddingRemaining = (int)((512 - size % 512) % 512);
            _contentRemaining = size;
            _declared = checked(_declared + 512 + size + _paddingRemaining);
            Require(_declared <= limits.MaximumExpandedBytes - 1024, "asset_package_expanded_limit");
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Format v1 permits the fixed gzip header (no optional name/comment/extra fields) and one trailer.
    // This check is independent of framework EOF permissiveness for a truncated gzip trailer.
    private sealed class GzipEnvelope
    {
        private readonly byte[] _header = new byte[10];
        private readonly byte[] _tail = new byte[8];
        private int _headerLength;
        private long _length;
        public void Append(ReadOnlySpan<byte> bytes)
        {
            var take = Math.Min(bytes.Length, 10 - _headerLength);
            bytes[..take].CopyTo(_header.AsSpan(_headerLength)); _headerLength += take;
            ReadOnlySpan<byte> prefix = [31, 139, 8, 0];
            Require(_header.AsSpan(0, Math.Min(_headerLength, 4)).SequenceEqual(prefix[..Math.Min(_headerLength, 4)]),
                "asset_package_gzip_integrity_failed");
            if (bytes.Length >= 8) bytes[^8..].CopyTo(_tail);
            else if (!bytes.IsEmpty)
            { _tail.AsSpan(bytes.Length).CopyTo(_tail); bytes.CopyTo(_tail.AsSpan(8 - bytes.Length)); }
            _length = checked(_length + bytes.Length);
        }
        public void Validate(uint crc, long expandedBytes)
        {
            Require(_length >= 18 && _headerLength == 10 && _header.AsSpan(0, 4).SequenceEqual(new byte[] { 31, 139, 8, 0 }) &&
                BinaryPrimitives.ReadUInt32LittleEndian(_tail) == crc &&
                BinaryPrimitives.ReadUInt32LittleEndian(_tail.AsSpan(4)) == unchecked((uint)expandedBytes),
                "asset_package_gzip_integrity_failed");
        }
    }

    private sealed class Crc32State
    {
        private static readonly uint[] Table = BuildTable();
        private uint _value = uint.MaxValue;
        public uint Value => ~_value;
        public void Append(ReadOnlySpan<byte> bytes)
        { foreach (var value in bytes) _value = Table[(byte)(_value ^ value)] ^ (_value >> 8); }
        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    /// <summary>Verifies content and legacy chunk-index identities before returning final bytes to a sink.</summary>
    private sealed class VerifiedPayloadStream : Stream
    {
        private readonly Stream _source;
        private readonly CortexAssetV2 _asset;
        private readonly bool _seekableLength;
        private readonly IncrementalHash _whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash _chunk = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash _index = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _read;
        private int _chunkLength;
        private bool _complete;
        public VerifiedPayloadStream(Stream source, CortexAssetV2 asset, bool seekableLength = false)
        { _source = source; _asset = asset; _seekableLength = seekableLength; if (asset.Size == 0) Complete(); }
        public override bool CanRead => true;
        public override bool CanSeek => _seekableLength;
        public override bool CanWrite => false;
        public override long Length => _asset.Size;
        public override long Position { get => _read; set => Seek(value, SeekOrigin.Begin); }
        public void RequireComplete() => Require(_complete && _read == _asset.Size, "asset_package_payload_incomplete");
        private void Add(ReadOnlySpan<byte> bytes)
        {
            _whole.AppendData(bytes);
            var remaining = bytes;
            while (!remaining.IsEmpty)
            {
                var take = Math.Min(CortexAssetCodecV2.ChunkSize - _chunkLength, remaining.Length);
                _chunk.AppendData(remaining[..take]); _chunkLength += take; remaining = remaining[take..];
                if (_chunkLength == CortexAssetCodecV2.ChunkSize) EndChunk();
            }
            _read = checked(_read + bytes.Length);
            if (_read == _asset.Size) Complete();
        }
        private void EndChunk()
        { _index.AppendData(Utf8.GetBytes(Hex(_chunk.GetHashAndReset()) + "\n")); _chunkLength = 0; }
        private void Complete()
        {
            if (_complete) return;
            if (_chunkLength > 0) EndChunk();
            Require(Hex(_whole.GetHashAndReset()) == _asset.Sha256 && Hex(_index.GetHashAndReset()) == _asset.ChunksSha256,
                "asset_package_payload_integrity_failed");
            _complete = true;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var wanted = (int)Math.Min(count, _asset.Size - _read);
            if (wanted == 0) return 0;
            var read = _source.Read(buffer, offset, wanted);
            Require(read != 0, "asset_package_payload_incomplete"); Add(buffer.AsSpan(offset, read)); return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var wanted = (int)Math.Min(buffer.Length, _asset.Size - _read);
            if (wanted == 0) return 0;
            var read = await _source.ReadAsync(buffer[..wanted], token).ConfigureAwait(false);
            Require(read != 0, "asset_package_payload_incomplete"); Add(buffer.Span[..read]); return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_read + offset),
                SeekOrigin.End => checked(_asset.Size + offset), _ => -1 };
            if (!_seekableLength || position != _read) throw new NotSupportedException("Payload verification is sequential.");
            return _read;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (disposing) { _whole.Dispose(); _chunk.Dispose(); _index.Dispose(); } base.Dispose(disposing); }
    }

    private sealed class StorePayloadStream(ICortexAssetStoreV2 store, CortexAssetV2 asset) : Stream
    {
        private long _position;
        private byte[] _buffer = [];
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => asset.Size;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty || _position == asset.Size) return 0;
            if (_offset == _buffer.Length)
            {
                var range = await store.ReadBytesAsync(asset.Metadata.Namespace, asset.Metadata.Id, asset.Ref,
                    _position, CortexAssetCodecV2.ChunkSize, token).ConfigureAwait(false);
                var wanted = (int)Math.Min(CortexAssetCodecV2.ChunkSize, asset.Size - _position);
                Require(range.Offset == _position && range.TotalSize == asset.Size && range.ContentSha256 == asset.Sha256 &&
                    range.Bytes.Length == wanted && Hex(SHA256.HashData(range.Bytes)) == range.Sha256,
                    "asset_package_source_integrity_failed");
                _buffer = range.Bytes; _offset = 0;
            }
            var take = Math.Min(buffer.Length, _buffer.Length - _offset);
            _buffer.AsMemory(_offset, take).CopyTo(buffer); _offset += take; _position += take; return take;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
