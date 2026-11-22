using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.Paths;

namespace Pegasus.Cortex.OfflineJournal.IO;

internal sealed class JournalFileStore : IDisposable
{
    public const string MetadataFileName = "m.json";
    public const string PayloadFileName = "p.bin";
    public const string ReceiptFileName = "r.json";
    public const string EvidenceFileName = "e.bin";
    public const string ImportManifestFileName = "i.json";
    public const string StateFileName = "h.json";
    public const string PublicBundleFileName = "b.json";
    public const string PublicReceiptFileName = "s.json";

    private readonly JournalPathPolicy _paths;
    private readonly IJournalFileSystemBoundary _fileSystem;

    public JournalFileStore(JournalPathPolicy paths, IJournalFileSystemBoundary fileSystem)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        if (!string.Equals(_paths.Root, _fileSystem.RootPath, PathComparison))
            throw new ArgumentException("The filesystem boundary and journal path policy must share one root.", nameof(fileSystem));
        JournalRoot = _paths.Resolve("j");
        StagingRoot = _paths.Resolve("j", "s");
        PendingRoot = _paths.Resolve("j", "p");
        QuarantineRoot = _paths.Resolve("j", "q");
        ReceiptRoot = _paths.Resolve("j", "r");
        StateRoot = _paths.Resolve("j", "h");
        PublicReceiptRoot = _paths.Resolve("j", "a");
        ImportRoot = _paths.Resolve("j", "i");
        LockRoot = _paths.Resolve("j", "l");
    }

    public string JournalRoot { get; }
    public string StagingRoot { get; }
    public string PendingRoot { get; }
    public string QuarantineRoot { get; }
    public string ReceiptRoot { get; }
    public string StateRoot { get; }
    public string PublicReceiptRoot { get; }
    public string ImportRoot { get; }
    public string LockRoot { get; }

    public JournalDurabilityCapabilities Durability => _fileSystem.Durability;

    public void EnsureLayout()
    {
        _fileSystem.EnsureDirectory(string.Empty);
        foreach (var path in new[]
                 {
                     JournalRoot, StagingRoot, PendingRoot, QuarantineRoot, ReceiptRoot,
                     StateRoot, PublicReceiptRoot, ImportRoot, LockRoot
                 })
            _fileSystem.EnsureDirectory(ToRelative(path));
    }

    public string CreateStageDirectory(string prefix, int maximumArtifacts)
    {
        if (maximumArtifacts < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumArtifacts));
        EnsureDirectory(StagingRoot);
        var occupied = _fileSystem.EnumerateDirectory(ToRelative(StagingRoot), maximumArtifacts);
        if (occupied.Count >= maximumArtifacts)
            throw new JournalLimitExceededException("staging_artifacts");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var name = $"{prefix}-{Guid.NewGuid():N}";
            var path = _paths.Resolve("j", "s", name);
            if (_fileSystem.DirectoryExists(ToRelative(path)) || _fileSystem.FileExists(ToRelative(path)))
                continue;
            _fileSystem.CreateDirectory(ToRelative(path));
            return path;
        }
        throw new IOException("A unique journal staging directory could not be created.");
    }

    public string PendingPath(string directoryName) => _paths.Resolve("j", "p", directoryName);
    public string ReceiptPath(string directoryName) => _paths.Resolve("j", "r", directoryName);
    public string StatePath(string directoryName) => _paths.Resolve("j", "h", directoryName);
    public string PublicReceiptPath(string receiptId) => _paths.Resolve("j", "a", receiptId);
    public string ImportPath(string runId) => _paths.Resolve("j", "i", runId);

    public bool DirectoryExists(string path) => _fileSystem.DirectoryExists(ToRelative(path));

    public bool FileExists(string path) => _fileSystem.FileExists(ToRelative(path));

    public FileStream OpenReadFile(
        string path,
        int bufferSize = 64 * 1024,
        bool asynchronous = true,
        bool sequentialScan = true) =>
        _fileSystem.OpenReadFile(ToRelative(path), bufferSize, asynchronous, sequentialScan);

    public FileStream AcquireWriterLock()
    {
        EnsureLayout();
        var path = _paths.Resolve("j", "l", "w.lock");
        return _fileSystem.AcquireExclusiveLock(ToRelative(path));
    }

    public FileStream CreatePrivateFile(string path)
        => _fileSystem.CreateNewFile(ToRelative(path), 64 * 1024, asynchronous: true);

    public async ValueTask WriteCanonicalAsync<T>(
        string path,
        T value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = CortexCanonicalJson.Serialize(value);
        if (bytes.Length is 0 || bytes.Length > maximumBytes)
            throw new JournalLimitExceededException("metadata_bytes");

        await using var output = CreatePrivateFile(path);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    public async ValueTask<(T Value, byte[] Bytes)> ReadCanonicalAsync<T>(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = _fileSystem.OpenReadFile(
            ToRelative(path),
            16 * 1024,
            asynchronous: true,
            sequentialScan: true);
        if (input.Length is <= 0 || input.Length > maximumBytes || input.Length > int.MaxValue)
            throw new JournalLimitExceededException("metadata_bytes");
        var bytes = GC.AllocateUninitializedArray<byte>((int)input.Length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Journal metadata ended before its opened length.");
            offset += read;
        }
        var extra = new byte[1];
        if (await input.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0 || input.Length != bytes.Length)
            throw new IOException("Journal metadata changed while it was being read.");
        var value = CortexCanonicalJson.Deserialize<T>(bytes);
        if (!bytes.AsSpan().SequenceEqual(CortexCanonicalJson.Serialize(value)))
            throw new InvalidDataException("Journal metadata is not canonical JSON.");
        return (value, bytes);
    }

    public async ValueTask<FileDigest> CopyBoundedAsync(
        Stream source,
        string destinationPath,
        long maximumBytes,
        long? declaredBytes,
        CancellationToken cancellationToken)
    {
        if (declaredBytes is < 0 || declaredBytes > maximumBytes)
            throw new JournalLimitExceededException("declared_bytes");

        await using var destination = CreatePrivateFile(destinationPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long written = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (written > maximumBytes - read)
                    throw new JournalLimitExceededException("stream_bytes");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                written += read;
            }

            if (declaredBytes is not null && written != declaredBytes.Value)
                throw new InvalidDataException("The stream length does not match its declared length.");
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return new FileDigest(written, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask<FileDigest> ComputeDigestAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        await using var input = _fileSystem.OpenReadFile(
            ToRelative(path),
            64 * 1024,
            asynchronous: true,
            sequentialScan: true);
        if (input.Length < 0 || !allowEmpty && input.Length == 0 || input.Length > maximumBytes)
            throw new JournalLimitExceededException("file_bytes");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long readTotal = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                readTotal += read;
                if (readTotal > maximumBytes)
                    throw new JournalLimitExceededException("file_bytes");
                hash.AppendData(buffer, 0, read);
            }
            if (readTotal != input.Length)
                throw new IOException("The journal file changed while it was being verified.");
            return new FileDigest(readTotal, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public IReadOnlyList<string> EnumerateDirectories(string root, int maximumEntries)
    {
        EnsureDirectory(root);
        var results = new List<string>(Math.Min(maximumEntries, 256));
        foreach (var entry in _fileSystem.EnumerateDirectory(ToRelative(root), maximumEntries))
        {
            if (entry.Kind != JournalFileSystemEntryKind.Directory)
                throw new IOException("A journal collection contains a non-directory entry.");
            results.Add(ResolveRelative(entry.RelativePath));
        }
        return results;
    }

    public void EnsureExactFiles(string directory, params string[] expectedNames)
    {
        EnsureDirectory(directory);
        var actual = _fileSystem.EnumerateDirectory(ToRelative(directory), expectedNames.Length + 1)
            .Select(entry =>
            {
                if (entry.Kind != JournalFileSystemEntryKind.RegularFile)
                    throw new IOException("Journal artifacts may contain regular files only.");
                return GetRelativeLeaf(entry.RelativePath);
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = expectedNames.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("The journal artifact contains unexpected entries.");
    }

    public void PromoteDirectory(string stageDirectory, string finalDirectory)
    {
        EnsureOwnedStage(stageDirectory);
        if (_fileSystem.DirectoryExists(ToRelative(finalDirectory)) || _fileSystem.FileExists(ToRelative(finalDirectory)))
            throw new JournalDestinationExistsException(finalDirectory);
        _fileSystem.MoveDirectory(ToRelative(stageDirectory), ToRelative(finalDirectory));
    }

    public string QuarantineDirectory(string directory, string category)
    {
        if (!_fileSystem.DirectoryExists(ToRelative(directory)))
            return string.Empty;
        var sourceName = Path.GetFileName(directory);
        if (string.IsNullOrEmpty(sourceName))
            throw new IOException("A quarantine source must have a bounded leaf name.");
        var sourceNameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceName)))
            .ToLowerInvariant()[..12];
        var destination = _paths.Resolve("j", "q", $"{category}-{sourceNameHash}-{Guid.NewGuid():N}");
        _fileSystem.MoveDirectory(ToRelative(directory), ToRelative(destination));
        return destination;
    }

    public void DeleteOwnedStage(string stageDirectory)
    {
        if (!_fileSystem.DirectoryExists(ToRelative(stageDirectory)))
            return;
        EnsureOwnedStage(stageDirectory);
        foreach (var entry in _fileSystem.EnumerateDirectory(
                     ToRelative(stageDirectory),
                     CortexSyncContractLimits.MaximumMigrationFiles + 2))
        {
            if (entry.Kind != JournalFileSystemEntryKind.RegularFile)
                throw new IOException("Owned stage cleanup refuses nested directories or special entries.");
            _fileSystem.DeleteFile(entry.RelativePath);
        }
        _fileSystem.DeleteEmptyDirectory(ToRelative(stageDirectory));
    }

    public void EnsureRegularFile(string path)
    {
        if (_fileSystem.GetEntryKind(ToRelative(path)) != JournalFileSystemEntryKind.RegularFile)
            throw new IOException("Journal data must be a regular non-reparse file.");
    }

    public void EnsureDirectory(string path)
    {
        if (_fileSystem.GetEntryKind(ToRelative(path)) != JournalFileSystemEntryKind.Directory)
            throw new IOException("Journal directories cannot be reparse points.");
    }

    private void EnsureOwnedStage(string path)
    {
        var canonical = Path.GetFullPath(path);
        var prefix = StagingRoot + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new IOException("The operation attempted to mutate a directory outside journal staging.");
        }
        EnsureDirectory(canonical);
    }

    public void Dispose() => _fileSystem.Dispose();

    private string ToRelative(string path)
    {
        var canonical = Path.GetFullPath(path);
        if (string.Equals(canonical, _paths.Root, PathComparison))
            return string.Empty;
        if (!canonical.StartsWith(_paths.Root + Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("The operation attempted to escape its authorized filesystem boundary.");
        return Path.GetRelativePath(_paths.Root, canonical).Replace('\\', '/');
    }

    private string ResolveRelative(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return _paths.Root;
        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => !JournalPathPolicy.IsPortableIdentifier(segment, 128)))
            throw new IOException("The filesystem boundary returned an invalid journal-relative path.");
        return _paths.Resolve(segments);
    }

    private static string GetRelativeLeaf(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        var leaf = separator < 0 ? relativePath : relativePath[(separator + 1)..];
        if (!JournalPathPolicy.IsPortableIdentifier(leaf, 128))
            throw new IOException("The filesystem boundary returned an invalid journal entry name.");
        return leaf;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal sealed record FileDigest(long Bytes, string Sha256);

internal sealed class JournalLimitExceededException(string limitName) : IOException
{
    public string LimitName { get; } = limitName;
}

internal sealed class JournalDestinationExistsException(string path) : IOException
{
    public string PathValue { get; } = path;
}

internal sealed class BoundedHashWriteStream(
    Stream inner,
    long maximumBytes,
    bool leaveOpen) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _completed;
    private bool _disposed;

    public long BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && inner.CanWrite;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

    public string CompleteHash()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("The protected payload hash was already completed.");
        _completed = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        inner.Flush();
        if (inner is FileStream file)
            file.Flush(flushToDisk: true);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        inner.Write(buffer);
        _hash.AppendData(buffer);
        BytesWritten += buffer.Length;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureCapacity(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _hash.AppendData(buffer.Span);
        BytesWritten += buffer.Length;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        if (disposing)
        {
            _hash.Dispose();
            if (!leaveOpen)
                inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hash.Dispose();
        if (!leaveOpen)
            await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void EnsureCapacity(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("No protected bytes may be written after hashing completes.");
        if (count < 0 || BytesWritten > maximumBytes - count)
            throw new JournalLimitExceededException("protected_payload_bytes");
    }
}

internal sealed class BoundedHashReadStream(
    Stream inner,
    long maximumBytes,
    long? declaredBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _reachedEnd;
    private bool _completed;
    private bool _disposed;

    public long BytesRead { get; private set; }
    public override bool CanRead => !_disposed && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public string CompleteHash()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed || !_reachedEnd)
            throw new InvalidOperationException("The protector must consume the complete plaintext stream exactly once.");
        if (declaredBytes is not null && BytesRead != declaredBytes.Value)
            throw new InvalidDataException("The plaintext length does not match its declared length.");
        _completed = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        EnsureReadable();
        var read = inner.Read(buffer);
        Capture(buffer[..read]);
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Capture(buffer.Span[..read]);
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            _reachedEnd = true;
            return;
        }
        if (BytesRead > maximumBytes - bytes.Length)
            throw new JournalLimitExceededException("plaintext_bytes");
        _hash.AppendData(bytes);
        BytesRead += bytes.Length;
    }

    private void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed || _reachedEnd)
            throw new InvalidOperationException("The plaintext stream cannot be reread by the protector.");
    }
}
