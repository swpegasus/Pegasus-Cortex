using Pegasus.Cortex.Mcp.Local.Recovery;

internal static class MetadataReadBoundsTests
{
    public static async Task RunAsync()
    {
        foreach (var length in new[] { 0L, 9L, long.MaxValue })
        {
            using var input = new ObservedStream([], length);
            await ThrowsAsync<InvalidDataException>(() => Read(input, 8));
            Require(input.ReadCalls == 0, "Rejected length reached the metadata body reader.");
        }

        using (var input = new ObservedStream([1, 2, 3, 4, 5, 6, 7, 8], 8))
        {
            var result = await Read(input, 8);
            Require(result.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
                "The exact boundary bytes changed.");
        }
        using (var input = new ObservedStream([1, 2, 3, 4, 5, 6, 7, 8, 9], 8))
        {
            await ThrowsAsync<InvalidDataException>(() => Read(input, 8));
            Require(input.BytesRead <= 9, "Growth bypassed the bounded payload plus EOF probe.");
        }
        using (var input = new ObservedStream([1, 2, 3, 4, 5, 6, 7], 8))
            await ThrowsAsync<EndOfStreamException>(() => Read(input, 8));

        using (var cancellation = new CancellationTokenSource())
        using (var input = new ObservedStream([1, 2], 2))
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => Read(input, 8, cancellation.Token));
            Require(input.ReadCalls == 0, "Canceled metadata read reached the body reader.");
        }
        await RecoveryFileBoundsAsync();
    }

    private static async Task RecoveryFileBoundsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CortexMetadataBounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "manifest.json");
        try
        {
            foreach (var length in new[] { 1, 9 })
            {
                await File.WriteAllBytesAsync(path, new byte[length]);
                await ThrowsAsync<InvalidDataException>(() =>
                    CortexLocalRecoveryExportReader.ReadExactAsync(root, "manifest.json", 8,
                        CancellationToken.None).AsTask());
                await ThrowsAsync<InvalidDataException>(() =>
                    CortexLocalBoundedMetadataReader.ReadAsync(path, 8, CancellationToken.None).AsTask());
            }
            var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            await File.WriteAllBytesAsync(path, expected);
            var actual = await CortexLocalRecoveryExportReader.ReadExactAsync(
                root, "manifest.json", 8, CancellationToken.None);
            Require(actual.AsSpan().SequenceEqual(expected), "Recovery file boundary bytes changed.");
        }
        finally
        {
            // Delete only the exact fixture file and its now-empty owned directory.
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    private static Task<byte[]> Read(Stream input, int maximum, CancellationToken token = default) =>
        CortexLocalBoundedMetadataReader.ReadStreamAsync(input, maximum, token).AsTask();

    private static async Task ThrowsAsync<T>(Func<Task<byte[]>> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The advertised length is independent of the tiny retained body, so the
    // fixture can prove refusal of huge lengths without allocating a huge file.
    private sealed class ObservedStream(byte[] content, long advertisedLength) : Stream
    {
        private int _position;
        public int ReadCalls { get; private set; }
        public int BytesRead => _position;
        public override long Length => advertisedLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            var count = Math.Min(buffer.Length, content.Length - _position);
            content.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
