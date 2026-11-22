using System.Buffers;
using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Pegasus.Cortex.ArchiveTool.Security;
using Pegasus.Cortex.ArchiveTool.Serialization;

namespace Pegasus.Cortex.ArchiveTool.Archive;

/// <summary>
/// Performs a hash-pinned structural preflight before any destination mutation, then reopens the
/// same archive stream for an exactly compared extraction pass. Only regular files and directories
/// are materialized; every other TAR object is rejected.
/// </summary>
public static class StructuredArchiveExtractor
{
    public static ArchiveExtractionResult Extract(ArchiveExtractionRequest request) =>
        Extract(request, new ArchiveExtractionHooks());

    internal static ArchiveExtractionResult Extract(
        ArchiveExtractionRequest request,
        ArchiveExtractionHooks hooks)
    {
        var limits = request.Limits ?? new ArchiveSafetyLimits();
        var validated = ArchivePathPolicy.ValidateRequest(request, limits);
        var compressedLength = new FileInfo(validated.ArchivePath).Length;
        if (compressedLength is <= 0 || compressedLength > limits.MaximumCompressedBytes)
            throw Failure("archive_size_over_budget", "The compressed archive size is outside policy limits.");

        ArchivePreflight preflight;
        using (var archive = OpenPinnedArchive(validated.ArchivePath))
        {
            var initialHash = ComputeArchiveSha256(archive);
            RequirePinnedHash(initialHash, validated.ExpectedSha256);
            preflight = Preflight(archive, initialHash, compressedLength, validated);

            // Re-hash immediately before mutation and keep the handle open through extraction.
            var secondHash = ComputeArchiveSha256(archive);
            RequirePinnedHash(secondHash, validated.ExpectedSha256);

            string? ownedTree = null;
            string? inventoryTemporaryPath = null;
            string? ownedInventoryPath = null;
            try
            {
                ownedTree = SafeFileSystem.CreatePrivateStagingDirectory(validated);
                SafeFileSystem.EnsureMaterializedPathsWithinBudget(
                    ownedTree,
                    preflight.MaterializedEntries,
                    limits.MaximumCanonicalPathUtf16);
                SafeFileSystem.CreateMaterializedDirectories(ownedTree, preflight.MaterializedEntries);
                ExtractComparedPass(archive, ownedTree, preflight, validated, hooks);

                // A final compressed-byte hash catches mutation on platforms where FileShare is advisory.
                var finalHash = ComputeArchiveSha256(archive);
                RequirePinnedHash(finalHash, validated.ExpectedSha256);

                SafeFileSystem.ApplyModes(ownedTree, preflight.MaterializedEntries);
                _ = SafeFileSystem.InventoryAndVerify(ownedTree, preflight.MaterializedEntries);

                SafeFileSystem.CommitDirectoryNoReplace(ownedTree, validated.DestinationPath);
                ownedTree = validated.DestinationPath;
                hooks.AfterDestinationCommit?.Invoke();
                ArchivePathPolicy.EnsurePathIsNotReparsePoint(ownedTree, "destination_unsafe");
                ArchivePathPolicy.EnsureExistingAncestorsAreNotReparsePoints(ownedTree, "destination_unsafe");
                var committedInventory = SafeFileSystem.InventoryAndVerify(
                    ownedTree,
                    preflight.MaterializedEntries);

                inventoryTemporaryPath = SafeFileSystem.CreateInventoryTemporaryPath(
                    validated.InventoryPath,
                    limits.MaximumCanonicalPathUtf16);
                InventoryJsonWriter.WriteCreateNew(inventoryTemporaryPath, preflight, committedInventory);
                SafeFileSystem.CommitFileNoReplace(inventoryTemporaryPath, validated.InventoryPath);
                inventoryTemporaryPath = null;
                ownedInventoryPath = validated.InventoryPath;
                hooks.AfterInventoryCommit?.Invoke();

                var result = new ArchiveExtractionResult(
                    validated.DestinationPath,
                    validated.InventoryPath,
                    preflight.ArchiveSha256,
                    preflight.CompressedBytes,
                    preflight.TarStreamBytes,
                    preflight.TotalFileBytes,
                    preflight.ArchiveEntries.Count,
                    committedInventory.Count);

                // Ownership transfers to the caller only after both no-replace commits succeed.
                ownedTree = null;
                ownedInventoryPath = null;
                return result;
            }
            catch (Exception exception)
            {
                var primary = exception as ArchiveBoundaryException ??
                    Failure("extraction_failed", "Archive extraction failed closed.");
                var cleanupFailures = new List<Exception>();
                cleanupFailures.AddRange(SafeFileSystem.DeleteTreeNoFollow(inventoryTemporaryPath, hooks));
                cleanupFailures.AddRange(SafeFileSystem.DeleteTreeNoFollow(ownedInventoryPath, hooks));
                cleanupFailures.AddRange(SafeFileSystem.DeleteTreeNoFollow(ownedTree, hooks));
                if (cleanupFailures.Count != 0)
                {
                    throw new ArchiveBoundaryException(
                        "cleanup_failed",
                        "Archive extraction failed and owned output cleanup was incomplete.",
                        entryIndex: primary.EntryIndex,
                        primaryCode: primary.Code,
                        cleanupFailures: cleanupFailures);
                }
                throw primary;
            }
        }
    }

    private static ArchivePreflight Preflight(
        FileStream archive,
        byte[] archiveHash,
        long compressedLength,
        ValidatedRequest request)
    {
        archive.Position = 0;
        RequireGZipHeader(archive);
        archive.Position = 0;

        var entries = new List<ArchiveEntryRecord>();
        long totalFileBytes = 0;
        long tarStreamBytes;
        uint tarCrc32;
        try
        {
            using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            using var bounded = new BoundedReadStream(gzip, request.Limits.MaximumTarStreamBytes);
            using var reader = new TarReader(bounded, leaveOpen: true);
            while (true)
            {
                TarEntry? entry;
                try
                {
                    entry = reader.GetNextEntry(copyData: false);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException)
                {
                    throw EntryFailure("archive_malformed", "The TAR stream is malformed.", entries.Count, exception);
                }
                if (entry is null)
                    break;
                if (entries.Count >= request.Limits.MaximumEntries)
                    throw EntryFailure("entry_count_over_budget", "The archive entry count exceeds policy.", entries.Count);

                var record = InspectAndHashEntry(
                    entry,
                    entries.Count,
                    request.DestinationPath,
                    request.Limits,
                    ref totalFileBytes);
                entries.Add(record);
            }

            DrainAndValidateTarPadding(bounded);
            tarStreamBytes = bounded.BytesRead;
            tarCrc32 = bounded.Crc32;
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw Failure("archive_malformed", "The gzip or TAR stream is malformed.", exception);
        }

        if (archive.Position != archive.Length)
            throw Failure("archive_malformed", "The gzip stream was not consumed completely.");
        ValidateGZipFooter(archive, tarCrc32, tarStreamBytes, "archive_malformed");
        if (entries.Count == 0)
            throw Failure("archive_empty", "The archive contains no entries.");
        EnforceExpansionRatio(tarStreamBytes, compressedLength, request.Limits.MaximumExpansionRatio);

        var materialized = BuildMaterializedInventory(entries);
        return new ArchivePreflight(
            Convert.ToHexString(archiveHash).ToLowerInvariant(),
            compressedLength,
            tarStreamBytes,
            totalFileBytes,
            entries,
            materialized);
    }

    private static ArchiveEntryRecord InspectAndHashEntry(
        TarEntry entry,
        int index,
        string destinationPath,
        ArchiveSafetyLimits limits,
        ref long totalFileBytes)
    {
        RejectAmbiguousPax(entry, index);
        var kind = GetSupportedKind(entry, index);
        ValidateMode(entry.Mode, kind, index);
        var validatedPath = ArchivePathPolicy.ValidateArchiveEntryPath(
            entry.Name,
            kind == MaterializedEntryKind.Directory,
            destinationPath,
            limits,
            index);

        if (validatedPath.IsArchiveRoot)
        {
            if (entry.Length != 0)
                throw EntryFailure("archive_malformed", "The archive root directory contains data.", index);
            return new ArchiveEntryRecord(
                index,
                entry.Name,
                string.Empty,
                null,
                entry.EntryType,
                entry.Format,
                0,
                entry.Mode,
                null,
                true);
        }

        if (kind == MaterializedEntryKind.Directory)
        {
            if (entry.Length != 0)
                throw EntryFailure("archive_malformed", "A TAR directory entry contains data.", index);
            return new ArchiveEntryRecord(
                index,
                entry.Name,
                validatedPath.RelativePath,
                kind,
                entry.EntryType,
                entry.Format,
                0,
                entry.Mode,
                null,
                false);
        }

        if (entry.Length < 0 || entry.Length > limits.MaximumFileBytes)
            throw EntryFailure("file_size_over_budget", "An archive file exceeds the per-file size policy.", index);
        try
        {
            totalFileBytes = checked(totalFileBytes + entry.Length);
        }
        catch (OverflowException exception)
        {
            throw EntryFailure("total_size_over_budget", "The archive total size exceeds policy.", index, exception);
        }
        if (totalFileBytes > limits.MaximumTotalFileBytes)
            throw EntryFailure("total_size_over_budget", "The archive total size exceeds policy.", index);

        var data = entry.DataStream ?? throw EntryFailure(
            "archive_malformed",
            "A regular TAR entry has no data stream.",
            index);
        var sha256 = HashExactly(data, entry.Length, index);
        return new ArchiveEntryRecord(
            index,
            entry.Name,
            validatedPath.RelativePath,
            kind,
            entry.EntryType,
            entry.Format,
            entry.Length,
            entry.Mode,
            sha256,
            false);
    }

    private static IReadOnlyList<MaterializedEntryRecord> BuildMaterializedInventory(
        IReadOnlyList<ArchiveEntryRecord> archiveEntries)
    {
        // Portability is evaluated as Windows would evaluate it on every host.
        var rawPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, MaterializedNode>(StringComparer.OrdinalIgnoreCase);
        var rootSeen = false;

        foreach (var entry in archiveEntries)
        {
            if (entry.IsArchiveRoot)
            {
                if (rootSeen)
                    throw EntryFailure("path_collision", "The archive root entry is duplicated.", entry.Index);
                rootSeen = true;
                continue;
            }

            if (rawPaths.TryGetValue(entry.RelativePath, out _))
                throw EntryFailure("path_collision", "Archive entry paths collide under portable comparison.", entry.Index);
            rawPaths.Add(entry.RelativePath, entry.RelativePath);

            var segments = entry.RelativePath.Split('/');
            for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
            {
                var prefix = string.Join('/', segments, 0, segmentIndex);
                if (nodes.TryGetValue(prefix, out var existingParent))
                {
                    if (!string.Equals(existingParent.RelativePath, prefix, StringComparison.Ordinal) ||
                        existingParent.Kind != MaterializedEntryKind.Directory)
                    {
                        throw EntryFailure("path_collision", "Archive entry paths have a case or parent-type collision.", entry.Index);
                    }
                }
                else
                {
                    nodes.Add(prefix, new MaterializedNode(
                        prefix,
                        MaterializedEntryKind.Directory,
                        SafeFileSystem.ImplicitDirectoryMode,
                        true,
                        0,
                        null));
                }
            }

            if (nodes.TryGetValue(entry.RelativePath, out var existing))
            {
                if (!string.Equals(existing.RelativePath, entry.RelativePath, StringComparison.Ordinal) ||
                    existing.Kind != MaterializedEntryKind.Directory ||
                    entry.Kind != MaterializedEntryKind.Directory ||
                    !existing.IsImplicit)
                {
                    throw EntryFailure("path_collision", "Archive entry paths collide under portable comparison.", entry.Index);
                }
                nodes[entry.RelativePath] = new MaterializedNode(
                    entry.RelativePath,
                    MaterializedEntryKind.Directory,
                    entry.Mode,
                    false,
                    0,
                    null);
            }
            else
            {
                nodes.Add(entry.RelativePath, new MaterializedNode(
                    entry.RelativePath,
                    entry.Kind!.Value,
                    entry.Mode,
                    false,
                    entry.Length,
                    entry.Sha256));
            }
        }

        return nodes.Values
            .Select(node => new MaterializedEntryRecord(
                node.RelativePath,
                node.Kind,
                node.Length,
                node.Mode,
                node.Sha256))
            .OrderBy(node => node.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ExtractComparedPass(
        FileStream archive,
        string stagingRoot,
        ArchivePreflight preflight,
        ValidatedRequest request,
        ArchiveExtractionHooks hooks)
    {
        archive.Position = 0;
        RequireGZipHeader(archive);
        archive.Position = 0;
        long totalFileBytes = 0;
        var index = 0;
        try
        {
            using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            using var bounded = new BoundedReadStream(gzip, request.Limits.MaximumTarStreamBytes);
            using var reader = new TarReader(bounded, leaveOpen: true);
            while (true)
            {
                var entry = reader.GetNextEntry(copyData: false);
                if (entry is null)
                    break;
                if (index >= preflight.ArchiveEntries.Count)
                    throw EntryFailure("preflight_mismatch", "The extraction pass has additional TAR entries.", index);

                var expected = preflight.ArchiveEntries[index];
                RejectAmbiguousPax(entry, index);
                var kind = GetSupportedKind(entry, index);
                ValidateMode(entry.Mode, kind, index);
                var validatedPath = ArchivePathPolicy.ValidateArchiveEntryPath(
                    entry.Name,
                    kind == MaterializedEntryKind.Directory,
                    request.DestinationPath,
                    request.Limits,
                    index);

                if (!string.Equals(entry.Name, expected.RawName, StringComparison.Ordinal) ||
                    !string.Equals(validatedPath.RelativePath, expected.RelativePath, StringComparison.Ordinal) ||
                    validatedPath.IsArchiveRoot != expected.IsArchiveRoot ||
                    entry.EntryType != expected.EntryType ||
                    entry.Format != expected.Format ||
                    entry.Length != expected.Length ||
                    entry.Mode != expected.Mode ||
                    (expected.Kind.HasValue && kind != expected.Kind.Value))
                {
                    throw EntryFailure("preflight_mismatch", "A TAR entry differs from structural preflight.", index);
                }

                hooks.BeforeMaterializeEntry?.Invoke(index, expected.RelativePath);
                if (kind == MaterializedEntryKind.File)
                {
                    totalFileBytes = checked(totalFileBytes + entry.Length);
                    var destination = ArchivePathPolicy.ResolveEntryPath(stagingRoot, expected.RelativePath);
                    var data = entry.DataStream ?? throw EntryFailure(
                        "preflight_mismatch",
                        "A regular TAR entry lost its data stream after preflight.",
                        index);
                    var actualHash = SafeFileSystem.WriteFileCreateNew(destination, data, entry.Length, index);
                    if (!string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
                        throw EntryFailure("preflight_mismatch", "An archive file hash differs from preflight.", index);
                }
                index++;
            }

            DrainAndValidateTarPadding(bounded);
            if (bounded.BytesRead != preflight.TarStreamBytes)
                throw Failure("preflight_mismatch", "The TAR stream length differs from preflight.");
            ValidateGZipFooter(archive, bounded.Crc32, bounded.BytesRead, "preflight_mismatch");
        }
        catch (ArchiveBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            throw EntryFailure("preflight_mismatch", "The extraction pass differs from structural preflight.", index, exception);
        }

        if (index != preflight.ArchiveEntries.Count || totalFileBytes != preflight.TotalFileBytes)
            throw Failure("preflight_mismatch", "The extraction pass inventory differs from preflight.");
        if (archive.Position != archive.Length)
            throw Failure("preflight_mismatch", "The extraction gzip stream was not consumed completely.");
        EnforceExpansionRatio(preflight.TarStreamBytes, preflight.CompressedBytes, request.Limits.MaximumExpansionRatio);
    }

    private static MaterializedEntryKind GetSupportedKind(TarEntry entry, int index) =>
        entry.EntryType switch
        {
            TarEntryType.RegularFile or TarEntryType.V7RegularFile => MaterializedEntryKind.File,
            TarEntryType.Directory => MaterializedEntryKind.Directory,
            _ => throw EntryFailure(
                "entry_type_unsupported",
                "The archive contains a link, sparse object, or special TAR entry.",
                index)
        };

    private static void RejectAmbiguousPax(TarEntry entry, int index)
    {
        if (entry is PaxGlobalExtendedAttributesTarEntry ||
            entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes ||
            entry is PaxTarEntry pax && pax.ExtendedAttributes.Count != 0)
        {
            throw EntryFailure(
                "pax_ambiguous",
                "The archive contains global or per-entry PAX extended attributes.",
                index);
        }
    }

    private static void ValidateMode(
        UnixFileMode mode,
        MaterializedEntryKind kind,
        int index)
    {
        const int permissionBits = 0x1ff; // POSIX 0777; all higher mode bits are rejected.
        if (((int)mode & ~permissionBits) != 0)
            throw EntryFailure("entry_mode_unsafe", "An archive entry uses special or unknown mode bits.", index);
        var numericMode = (int)mode;
        if (kind == MaterializedEntryKind.Directory && numericMode != 0x1ed ||
            kind == MaterializedEntryKind.File && numericMode is not (0x1a4 or 0x1ed))
            throw EntryFailure("entry_mode_unsafe", "An archive entry mode is outside the runtime allow-list.", index);
    }

    private static string HashExactly(Stream data, long expectedLength, int index)
    {
        byte[]? buffer = null;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long readTotal = 0;
            while (true)
            {
                var read = data.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                readTotal = checked(readTotal + read);
                if (readTotal > expectedLength)
                    throw EntryFailure("archive_malformed", "A TAR file exceeds its declared length.", index);
                hash.AppendData(buffer, 0, read);
            }
            if (readTotal != expectedLength)
                throw EntryFailure("archive_malformed", "A TAR file differs from its declared length.", index);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void DrainAndValidateTarPadding(BoundedReadStream stream)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (true)
        {
            var read = stream.Read(buffer);
            if (read == 0)
                return;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != 0)
                    throw Failure("archive_malformed", "The TAR stream contains nonzero data after its end marker.");
            }
        }
    }

    private static FileStream OpenPinnedArchive(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.SequentialScan);

    private static byte[] ComputeArchiveSha256(FileStream archive)
    {
        archive.Position = 0;
        var hash = SHA256.HashData(archive);
        archive.Position = 0;
        return hash;
    }

    private static void RequirePinnedHash(byte[] actual, byte[] expected)
    {
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw Failure("hash_mismatch", "The archive SHA-256 does not match the required pin.");
    }

    private static void RequireGZipHeader(FileStream archive)
    {
        Span<byte> header = stackalloc byte[3];
        if (archive.Read(header) != header.Length || header[0] != 0x1f || header[1] != 0x8b || header[2] != 8)
            throw Failure("archive_malformed", "The pinned input is not a gzip stream using DEFLATE.");
    }

    private static void EnforceExpansionRatio(long tarBytes, long compressedBytes, double maximumRatio)
    {
        if (compressedBytes <= 0 || (double)tarBytes / compressedBytes > maximumRatio)
            throw Failure("expansion_ratio_over_budget", "The archive expansion ratio exceeds policy.");
    }

    private static void ValidateGZipFooter(
        FileStream archive,
        uint decompressedCrc32,
        long decompressedBytes,
        string failureCode)
    {
        if (archive.Length < 18)
            throw Failure(failureCode, "The gzip stream is too short for a canonical member.");

        var originalPosition = archive.Position;
        Span<byte> footer = stackalloc byte[8];
        archive.Position = archive.Length - footer.Length;
        if (archive.Read(footer) != footer.Length)
            throw Failure(failureCode, "The gzip footer could not be read completely.");
        archive.Position = originalPosition;

        var footerCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        var footerSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
        if (footerCrc32 != decompressedCrc32 || footerSize != unchecked((uint)decompressedBytes))
            throw Failure(failureCode, "The gzip footer does not bind the complete TAR stream.");
    }

    private static ArchiveBoundaryException Failure(
        string code,
        string message,
        Exception? innerException = null) => new(code, message, innerException);

    private static ArchiveBoundaryException EntryFailure(
        string code,
        string message,
        int entryIndex,
        Exception? innerException = null) => new(code, message, innerException, entryIndex);

    private sealed record MaterializedNode(
        string RelativePath,
        MaterializedEntryKind Kind,
        UnixFileMode Mode,
        bool IsImplicit,
        long Length,
        string? Sha256);
}

/// <summary>
/// Counts every decompressed TAR byte and fails before a read can cross the configured ceiling.
/// </summary>
internal sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly CanonicalTarFramingValidator _framing = new();
    private readonly Crc32Accumulator _crc32 = new();

    internal long BytesRead { get; private set; }

    internal uint Crc32
    {
        get
        {
            _framing.Complete();
            return _crc32.Value;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;
        var remaining = maximumBytes - BytesRead;
        var permitted = (int)Math.Min(buffer.Length, Math.Max(1, remaining + 1));
        var read = _inner.Read(buffer[..permitted]);
        BytesRead = checked(BytesRead + read);
        if (BytesRead > maximumBytes)
            throw new ArchiveBoundaryException(
                "tar_stream_over_budget",
                "The decompressed TAR stream exceeds policy.");
        if (read == 0)
            _framing.Complete();
        else
        {
            _framing.Append(buffer[..read]);
            _crc32.Append(buffer[..read]);
        }
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The caller owns the wrapped GZipStream and disposes it explicitly.
        base.Dispose(disposing);
    }
}

/// <summary>
/// Independently checks canonical 512-byte TAR framing while TarReader performs semantic parsing.
/// Exactly two terminal zero blocks are required and no post-terminator padding is accepted.
/// </summary>
internal sealed class CanonicalTarFramingValidator
{
    private readonly byte[] _block = new byte[512];
    private int _buffered;
    private long _dataBytesRemaining;
    private int _terminalZeroBlocks;
    private bool _completed;

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        if (_completed)
            throw new ArchiveBoundaryException("archive_malformed", "The TAR stream continued after framing completion.");

        while (!bytes.IsEmpty)
        {
            var copy = Math.Min(_block.Length - _buffered, bytes.Length);
            bytes[..copy].CopyTo(_block.AsSpan(_buffered));
            _buffered += copy;
            bytes = bytes[copy..];
            if (_buffered == _block.Length)
            {
                ProcessBlock(_block);
                _buffered = 0;
            }
        }
    }

    internal void Complete()
    {
        if (_completed)
            return;
        _completed = true;
        if (_buffered != 0 || _dataBytesRemaining != 0 || _terminalZeroBlocks != 2)
            throw new ArchiveBoundaryException(
                "archive_malformed",
                "The TAR stream does not end with exactly two complete zero blocks.");
    }

    private void ProcessBlock(ReadOnlySpan<byte> block)
    {
        if (_dataBytesRemaining > 0)
        {
            var payloadBytes = (int)Math.Min(_dataBytesRemaining, block.Length);
            for (var index = payloadBytes; index < block.Length; index++)
            {
                if (block[index] != 0)
                    throw new ArchiveBoundaryException("archive_malformed", "A TAR data block has nonzero alignment padding.");
            }
            _dataBytesRemaining -= payloadBytes;
            return;
        }

        var isZero = block.IndexOfAnyExcept((byte)0) < 0;
        if (_terminalZeroBlocks != 0)
        {
            if (!isZero || _terminalZeroBlocks == 2)
                throw new ArchiveBoundaryException("archive_malformed", "The TAR terminator is non-canonical.");
            _terminalZeroBlocks++;
            return;
        }

        if (isZero)
        {
            _terminalZeroBlocks = 1;
            return;
        }

        _dataBytesRemaining = ParseCanonicalOctalSize(block.Slice(124, 12));
    }

    private static long ParseCanonicalOctalSize(ReadOnlySpan<byte> field)
    {
        if ((field[0] & 0x80) != 0)
            throw new ArchiveBoundaryException("archive_malformed", "A TAR header uses ambiguous base-256 size encoding.");

        var start = 0;
        while (start < field.Length && field[start] == (byte)' ')
            start++;
        var end = start;
        while (end < field.Length && field[end] is >= (byte)'0' and <= (byte)'7')
            end++;
        for (var index = end; index < field.Length; index++)
        {
            if (field[index] is not (0 or (byte)' '))
                throw new ArchiveBoundaryException("archive_malformed", "A TAR header size is not canonical octal.");
        }
        if (end == start)
            throw new ArchiveBoundaryException("archive_malformed", "A TAR header size is missing.");

        long value = 0;
        try
        {
            for (var index = start; index < end; index++)
                value = checked(value * 8 + field[index] - (byte)'0');
        }
        catch (OverflowException)
        {
            throw new ArchiveBoundaryException("archive_malformed", "A TAR header size overflows policy arithmetic.");
        }
        return value;
    }
}

internal sealed class Crc32Accumulator
{
    private static readonly uint[] Table = CreateTable();
    private uint _state = uint.MaxValue;

    internal uint Value => ~_state;

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            _state = Table[(byte)(_state ^ value)] ^ (_state >> 8);
    }

    private static uint[] CreateTable()
    {
        const uint polynomial = 0xedb88320;
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? polynomial ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
