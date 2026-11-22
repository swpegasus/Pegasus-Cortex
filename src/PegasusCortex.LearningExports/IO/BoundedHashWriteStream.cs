using System.Security.Cryptography;

namespace Pegasus.Cortex.LearningExports.IO;

internal sealed class LearningExportLimitExceededException(string limitName) : IOException
{
    public string LimitName { get; } = limitName;
}

internal sealed class BoundedHashWriteStream(
    Stream inner,
    long maximumBytes,
    string limitName,
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
            throw new InvalidOperationException("The stream hash was already completed.");
        _completed = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        inner.Write(buffer);
        _hash.AppendData(buffer);
        BytesWritten += buffer.Length;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureCapacity(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _hash.AppendData(buffer.Span);
        BytesWritten += buffer.Length;
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        inner.Flush();
        if (inner is FileStream file)
            file.Flush(flushToDisk: true);
    }
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

    private void EnsureCapacity(int writeBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("No bytes may be written after completing the hash.");
        if (writeBytes < 0 || BytesWritten > maximumBytes - writeBytes)
            throw new LearningExportLimitExceededException(limitName);
    }
}
