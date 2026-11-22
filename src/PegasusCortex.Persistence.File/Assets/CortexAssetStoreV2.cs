using System.Globalization;
using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Core.Assets;
using System.Security.Cryptography;
using System.Text;

namespace Pegasus.Cortex.Persistence.File.Assets;

using File = System.IO.File;

/// <summary>
/// Optional, owner-local passive files. No SQLite bodies, resident catalog, background worker,
/// renderer or network. Immutable bytes/references precede the atomic head commit.
/// </summary>
public sealed class CortexAssetStoreV2 : ICortexAssetStoreV2
{
    public const string Contract = CortexAssetCodecV2.Contract;
    public const int ChunkSize = CortexAssetCodecV2.ChunkSize;
    public const int MaximumMetadataBytes = CortexAssetCodecV2.MaximumMetadataBytes;
    public const int MaximumScan = CortexAssetCodecV2.MaximumScan;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public string Root { get; }
    public long MaximumAssetBytes { get; }

    public CortexAssetStoreV2(string root, long maximumAssetBytes = 256L * 1024 * 1024)
    {
        if (!Path.IsPathFullyQualified(root) || maximumAssetBytes is < 1 or > 2147483648L)
            throw new CortexAssetException("asset_configuration_invalid");
        CheckPath(root);
        Root = Path.GetFullPath(root);
        MaximumAssetBytes = maximumAssetBytes;
        CheckPath(Root);
    }

    public async Task<CortexAssetV2> ImportAsync(string source, CortexAssetSpecV2 metadata,
        string? expectedRef = null, CancellationToken cancellationToken = default,
        string? expectedContentSha256 = null)
    {
        Validate(metadata);
        if (expectedRef is not null) RequireHash(expectedRef);
        if (expectedContentSha256 is not null) RequireHash(expectedContentSha256);
        if (!Path.IsPathFullyQualified(source)) throw new CortexAssetException("asset_path_invalid");
        CheckPath(source);
        source = Path.GetFullPath(source);
        CheckPath(source);
        var initial = new FileInfo(source);
        if (!initial.Exists || initial.Length > MaximumAssetBytes)
            throw new CortexAssetException("asset_source_missing_or_oversized");
        Ensure(Root);
        await using var gate = await LockAsync(cancellationToken).ConfigureAwait(false);
        var head = HeadPath(metadata.Namespace, metadata.Id);
        var oldRef = File.Exists(head) ? ReadSmall(head, 128).Trim() : null;
        if (expectedRef is not null && expectedRef != oldRef)
            throw new CortexAssetException("asset_revision_conflict");
        Ensure(Under("objects"));
        Ensure(Under("refs"));
        var temporary = Under("objects", Guid.NewGuid().ToString("N") + ".part");
        var chunkTemporary = temporary + ".chunks";
        var temporaryOwned = false;
        var chunkTemporaryOwned = false;
        try
        {
            long size = 0;
            string sha;
            string chunksSha;
            await using (var input = OpenRead(source))
            await using (var output = CreateTemporary(temporary, ref temporaryOwned))
            await using (var chunkOutput = CreateTemporary(chunkTemporary, ref chunkTemporaryOwned))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var chunkDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[ChunkSize];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count == 0) break;
                    size = checked(size + count);
                    if (size > MaximumAssetBytes) throw new CortexAssetException("asset_oversized");
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    var line = Utf8.GetBytes(Hex(SHA256.HashData(buffer.AsSpan(0, count))) + "\n");
                    chunkDigest.AppendData(line);
                    await chunkOutput.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                }
                if (size != input.Length) throw new CortexAssetException("asset_source_changed");
                output.Flush(true);
                chunkOutput.Flush(true);
                sha = Hex(digest.GetHashAndReset());
                chunksSha = Hex(chunkDigest.GetHashAndReset());
            }
            var canonical = Serialize(metadata, sha, size, chunksSha);
            if (expectedContentSha256 is not null && sha != expectedContentSha256)
                throw new CortexAssetException("asset_integrity_failed");
            var reference = CortexAssetCodecV2.Reference(canonical);
            if (oldRef is not null && oldRef != reference && expectedRef is null)
                throw new CortexAssetException("asset_revision_conflict");
            var objectPath = ObjectPath(sha);
            if (File.Exists(objectPath))
            {
                await using var existing = OpenRead(objectPath);
                if (existing.Length != size || Hex(await SHA256.HashDataAsync(existing, cancellationToken)) != sha)
                    throw new CortexAssetException("asset_integrity_failed");
            }
            else File.Move(temporary, objectPath);
            var chunksPath = objectPath + ".chunks";
            CheckPath(chunksPath);
            if (File.Exists(chunksPath))
            {
                await using var existing = OpenRead(chunksPath);
                if (Hex(await SHA256.HashDataAsync(existing, cancellationToken)) != chunksSha)
                    throw new CortexAssetException("asset_integrity_failed");
            }
            else File.Move(chunkTemporary, chunksPath);
            WriteImmutable(Under("refs", reference + ".ini"), canonical);
            if (oldRef != reference)
            {
                // Invalidate continuation before publication. Exact pinned references remain valid.
                var namespaceRoot = NamespaceRoot(metadata.Namespace, create: true);
                var generationPath = UnderRelative(namespaceRoot, "generation");
                var generation = Guid.NewGuid().ToString("N");
                WriteAtomic(generationPath, "pending." + generation);
                Ensure(Path.GetDirectoryName(head)!);
                try { WriteAtomic(head, reference); }
                finally { WriteAtomic(generationPath, generation); }
            }
            return new(metadata, reference, sha, size, chunksSha);
        }
        finally
        {
            // These exact names are generated inside the validated private root; never recurse.
            if (temporaryOwned && File.Exists(temporary)) File.Delete(temporary);
            if (chunkTemporaryOwned && File.Exists(chunkTemporary)) File.Delete(chunkTemporary);
        }
    }

    public CortexAssetV2 Read(string ns, string id, string reference)
    {
        RequireName(id, 127);
        var asset = ReadReference(ns, reference);
        if (asset.Metadata.Id != id) throw new CortexAssetException("asset_scope_mismatch");
        return asset;
    }

    public CortexAssetV2 ReadReference(string ns, string reference)
    {
        RequireName(ns, 63);
        var asset = ReadReference(reference);
        if (asset.Metadata.Namespace != ns) throw new CortexAssetException("asset_scope_mismatch");
        return asset;
    }

    /// <summary>Resolve one globally unique pinned reference without a catalog scan.</summary>
    public CortexAssetV2 ReadReference(string reference)
    {
        RequireHash(reference);
        var path = Under("refs", reference + ".ini");
        if (!File.Exists(path)) throw new CortexAssetException("asset_reference_unavailable");
        var text = ReadSmall(path, MaximumMetadataBytes);
        return CortexAssetCodecV2.Parse(reference, text);
    }

    public CortexAssetV2? Current(string ns, string id)
    {
        RequireName(ns, 63); RequireName(id, 127);
        var path = HeadPath(ns, id);
        return File.Exists(path) ? Read(ns, id, ReadSmall(path, 128).Trim()) : null;
    }

    public IReadOnlyList<string> ExposedNamespaces()
    {
        var path = Under("scope.ini");
        if (!File.Exists(path)) return ["knowledge.shared"];
        var lines = ReadSmall(path, 4096).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 1 or > 33 || lines[0] != "[scope]") throw new CortexAssetException("asset_scope_invalid");
        var result = lines.Skip(1).ToArray();
        foreach (var ns in result) RequireName(ns, 63);
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) throw new CortexAssetException("asset_scope_invalid");
        return result;
    }

    public async Task ExposeAsync(string ns, CancellationToken cancellationToken = default)
    {
        RequireName(ns, 63);
        Ensure(Root);
        await using var gate = await LockAsync(cancellationToken);
        var scopes = ExposedNamespaces().Append(ns).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (scopes.Length > 32) throw new CortexAssetException("asset_scope_capacity");
        WriteAtomic(Under("scope.ini"), "[scope]\n" + string.Join('\n', scopes) + "\n");
    }

    /// <summary>Bounded identifier-only repair evidence; no submitted bodies, paths, or secrets.</summary>
    public bool RecordIssue(string ns, string id, string reference, string code)
    {
        try
        {
            RequireName(ns, 63); RequireName(id, 127); RequireHash(reference);
            if (code is not ("asset_file_unavailable" or "asset_integrity_failed" or "asset_metadata_integrity_failed" or "asset_reference_unavailable")) return false;
            if (!Directory.Exists(Root)) return false;
            // Diagnostic contention never stalls byte delivery or core operations.
            using var gate = new FileStream(Under("health.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Under("health.txt");
            var existing = File.Exists(path) ? ReadSmall(path, 65536).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
            var coordinate = code + "\t" + ns + "\t" + id + "\t" + reference;
            if (existing.Any(line => line.EndsWith("\t" + coordinate, StringComparison.Ordinal))) return true;
            var row = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\t" + coordinate;
            WriteAtomic(path, string.Join('\n', existing.TakeLast(127).Append(row)) + "\n");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task<CortexAssetBytesV2> ReadBytesAsync(string ns, string id, string reference,
        long offset = 0, int length = ChunkSize, CancellationToken cancellationToken = default)
    {
        var asset = Read(ns, id, reference);
        _ = CortexAssetCodecV2.ValidateRange(asset.Size, offset, length);
        var path = ObjectPath(asset.Sha256);
        if (!File.Exists(path) || !File.Exists(path + ".chunks"))
            throw new CortexAssetException("asset_file_unavailable");
        await using var input = OpenRead(path);
        await using var chunks = OpenRead(path + ".chunks");
        var chunkCount = (asset.Size + ChunkSize - 1) / ChunkSize;
        if (input.Length != asset.Size || chunks.Length != chunkCount * 65 ||
            Hex(await SHA256.HashDataAsync(chunks, cancellationToken)) != asset.ChunksSha256)
            throw new CortexAssetException("asset_integrity_failed");
        var wanted = (int)Math.Min(length, asset.Size - offset);
        var result = new byte[wanted];
        // Verify complete affected chunks, including bytes outside a short requested range.
        var buffer = new byte[ChunkSize];
        var filled = 0;
        while (filled < wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = offset + filled;
            var count = (int)Math.Min(ChunkSize, asset.Size - position);
            input.Position = position;
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            chunks.Position = position / ChunkSize * 65;
            var line = new byte[65];
            await chunks.ReadExactlyAsync(line, cancellationToken);
            if (line[64] != (byte)'\n' || Utf8.GetString(line, 0, 64) != Hex(SHA256.HashData(buffer.AsSpan(0, count))))
                throw new CortexAssetException("asset_integrity_failed");
            var take = Math.Min(count, wanted - filled);
            buffer.AsSpan(0, take).CopyTo(result.AsSpan(filled));
            filled += take;
        }
        return new(result, offset, asset.Size, Hex(SHA256.HashData(result)), asset.Sha256);
    }

    public CortexAssetSearchV2 Search(string ns, string query, int limit = 16,
        string? cursor = null, CancellationToken cancellationToken = default)
    {
        var words = CortexAssetCodecV2.ValidateSearch(ns, query, limit);
        var namespaceRoot = NamespaceRoot(ns, create: false);
        if (!Directory.Exists(namespaceRoot)) return new([], null, 0, true);
        var generation = ReadSmall(UnderRelative(namespaceRoot, "generation"), 64).Trim();
        if (generation.StartsWith("pending.", StringComparison.Ordinal)) throw new CortexAssetException("asset_busy");
        var binding = Hex(SHA256.HashData(Utf8.GetBytes(ns + "\0" + query + "\0" + limit + "\0" + generation)))[..16];
        var after = "";
        if (cursor is not null)
        {
            var parts = cursor.Split('.');
            if (parts.Length != 2 || parts[0] != binding || parts[1].Length != 66 || !parts[1].All(Uri.IsHexDigit))
                throw new CortexAssetException("asset_cursor_stale_or_invalid");
            after = parts[1];
        }
        var items = new List<CortexAssetV2>(limit);
        var scanned = 0;
        var last = after;
        var more = false;
        for (var shard = 0; shard < 256 && !more; shard++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = shard.ToString("x2", CultureInfo.InvariantCulture);
            var directory = UnderRelative(namespaceRoot, prefix);
            if (!Directory.Exists(directory)) continue;
            // A shard is capped during import; sorting never materializes an unbounded catalog.
            var paths = Directory.EnumerateFiles(directory, "*.head").Take(1025).ToArray();
            if (paths.Length > 1024) throw new CortexAssetException("asset_shard_capacity");
            foreach (var candidate in paths.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckPath(candidate);
                var key = Path.GetFileNameWithoutExtension(candidate);
                RequireHash(key);
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new CortexAssetException("asset_metadata_integrity_failed");
                var position = prefix + key;
                if (StringComparer.Ordinal.Compare(position, after) <= 0) continue;
                if (scanned >= MaximumScan || items.Count >= limit) { more = true; break; }
                last = position;
                var reference = ReadSmall(candidate, 128).Trim();
                RequireHash(reference);
                var asset = ReadReference(ns, reference);
                if (Hex(SHA256.HashData(Utf8.GetBytes(asset.Metadata.Id))) != key)
                    throw new CortexAssetException("asset_metadata_integrity_failed");
                scanned++;
                if (CortexAssetCodecV2.Matches(asset.Metadata, words)) items.Add(asset);
            }
        }
        if (ReadSmall(UnderRelative(namespaceRoot, "generation"), 64).Trim() != generation)
            throw new CortexAssetException("asset_cursor_stale_or_invalid");
        return new(items, more ? binding + "." + last : null, scanned, !more);
    }

    public static void Validate(CortexAssetSpecV2 spec) => CortexAssetCodecV2.Validate(spec);
    public static void RequireName(string value, int maximum) => CortexAssetCodecV2.RequireName(value, maximum);
    public static void RequireHash(string value) => CortexAssetCodecV2.RequireHash(value);

    private string HeadPath(string ns, string id)
    {
        var directory = NamespaceRoot(ns, create: false);
        var key = Hex(SHA256.HashData(Utf8.GetBytes(id)));
        var shard = UnderRelative(directory, key[..2]);
        if (Directory.Exists(shard) && Directory.EnumerateFiles(shard, "*.head").Take(1025).Count() >= 1024 &&
            !File.Exists(UnderRelative(shard, key + ".head"))) throw new CortexAssetException("asset_shard_capacity");
        return UnderRelative(shard, key + ".head");
    }

    private string NamespaceRoot(string ns, bool create)
    {
        var directory = Under("heads", Hex(SHA256.HashData(Utf8.GetBytes(ns)))[..16]);
        var marker = UnderRelative(directory, "namespace.ini");
        if (create) { Ensure(directory); WriteImmutable(marker, ns); }
        if (File.Exists(marker) && ReadSmall(marker, 128) != ns) throw new CortexAssetException("asset_scope_mismatch");
        return directory;
    }

    private string ObjectPath(string sha) => Under("objects", sha);
    private string Under(params string[] components) => UnderRelative(Root, components);
    private string UnderRelative(string root, params string[] components)
    {
        var full = Path.GetFullPath(Path.Combine([root, .. components]));
        if (!full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CortexAssetException("asset_path_invalid");
        CheckPath(full);
        return full;
    }

    public static void CheckPath(string full)
    {
        if (!Path.IsPathFullyQualified(full) || full.Length > 240 || full.StartsWith("\\\\", StringComparison.Ordinal))
            throw new CortexAssetException("asset_path_invalid");
        var root = Path.GetPathRoot(full)!;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Encoding.UTF8.GetByteCount(segment) > 255 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
                throw new CortexAssetException("asset_path_invalid");
        }
        for (string? path = full; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new CortexAssetException("asset_reparse_path");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void Ensure(string directory) { CheckPath(directory); Directory.CreateDirectory(directory); CheckPath(directory); }
    private static FileStream OpenRead(string path)
    {
        CheckPath(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    private static FileStream CreateTemporary(string path, ref bool owned)
    {
        CheckPath(path);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        owned = true;
        return stream;
    }
    private static string ReadSmall(string path, int maximum)
    {
        using var stream = OpenRead(path);
        if (stream.Length > maximum) throw new CortexAssetException("asset_metadata_oversized");
        var buffer = new byte[checked((int)stream.Length)];
        stream.ReadExactly(buffer);
        return Utf8.GetString(buffer);
    }
    private static void WriteImmutable(string path, string text)
    {
        if (File.Exists(path)) { if (ReadSmall(path, MaximumMetadataBytes) != text) throw new CortexAssetException("asset_integrity_failed"); return; }
        WriteAtomic(path, text);
    }
    private static void WriteAtomic(string path, string text)
    {
        CheckPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".new");
        CheckPath(temporary);
        var owned = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owned = true; file.Write(Utf8.GetBytes(text)); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        var path = Under("writer.lock");
        var deadline = Environment.TickCount64 + 2000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { await Task.Delay(25, cancellationToken); }
            catch (IOException) { throw new CortexAssetException("asset_busy"); }
        }
    }
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
    private static string Serialize(CortexAssetSpecV2 metadata, string sha, long size, string chunks) =>
        CortexAssetCodecV2.Serialize(metadata, sha, size, chunks);
}

