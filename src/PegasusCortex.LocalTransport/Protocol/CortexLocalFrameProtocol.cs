using System.Buffers.Binary;

namespace Pegasus.Cortex.LocalTransport.Protocol;

/// <summary>
/// Private length-prefixed pipe framing. Responses begin with a one-byte presence
/// marker so notification completion is explicit without fabricating MCP output.
/// </summary>
public static class CortexLocalFrameProtocol
{
    public const int MaximumRequestBytes = 128 * 1024;
    public const int MaximumResponseBytes = 256 * 1024;
    private const int PrefixBytes = sizeof(int);

    internal static async ValueTask<byte[]?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var length = await ReadLengthAsync(stream, allowEndOfStream: true, cancellationToken)
            .ConfigureAwait(false);
        if (length is null)
            return null;
        if (length is < 1 or > MaximumRequestBytes)
            throw new InvalidDataException("The local Cortex request frame exceeds its byte boundary.");
        return await ReadPayloadAsync(stream, length.Value, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var marker = new byte[1];
        await ReadExactlyAsync(stream, marker, cancellationToken).ConfigureAwait(false);
        if (marker[0] == 0)
            return null;
        if (marker[0] != 1)
            throw new InvalidDataException("The local Cortex response marker is invalid.");
        var length = await ReadLengthAsync(stream, allowEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (length is null or < 1 or > MaximumResponseBytes)
            throw new InvalidDataException("The local Cortex response frame exceeds its byte boundary.");
        return await ReadPayloadAsync(stream, length.Value, cancellationToken).ConfigureAwait(false);
    }

    internal static ValueTask WriteRequestAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, payload, MaximumRequestBytes, cancellationToken);

    internal static void ValidateRequestPayload(ReadOnlyMemory<byte> payload) =>
        ValidatePayload(payload, MaximumRequestBytes, nameof(payload));

    internal static ValueTask WriteResponseAsync(
        Stream stream,
        ReadOnlyMemory<byte>? payload,
        CancellationToken cancellationToken) =>
        WriteResponseCoreAsync(stream, payload, cancellationToken);

    private static async ValueTask WriteResponseCoreAsync(
        Stream stream,
        ReadOnlyMemory<byte>? payload,
        CancellationToken cancellationToken)
    {
        // Validate the complete response before emitting its presence marker.
        // Otherwise a rejected empty/oversized response leaves the peer with a
        // marker for a frame which will never follow.
        if (payload is { } response)
            ValidatePayload(response, MaximumResponseBytes, nameof(payload));
        await stream.WriteAsync(
            payload is null ? new byte[] { 0 } : new byte[] { 1 },
            cancellationToken).ConfigureAwait(false);
        if (payload is not null)
        {
            await WriteFrameAsync(
                stream,
                payload.Value,
                MaximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int?> ReadLengthAsync(
        Stream stream,
        bool allowEndOfStream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[PrefixBytes];
        var offset = 0;
        while (offset < prefix.Length)
        {
            var read = await stream.ReadAsync(prefix.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowEndOfStream)
                    return null;
                throw new EndOfStreamException("The local Cortex frame prefix was truncated.");
            }
            offset += read;
        }
        return BinaryPrimitives.ReadInt32BigEndian(prefix);
    }

    private static async ValueTask<byte[]> ReadPayloadAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var payload = new byte[length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The local Cortex frame payload was truncated.");
            offset += read;
        }
        return payload;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The local Cortex response marker was truncated.");
            offset += read;
        }
    }

    private static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidatePayload(payload, maximumBytes, nameof(payload));
        await WriteLengthAsync(stream, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePayload(
        ReadOnlyMemory<byte> payload,
        int maximumBytes,
        string parameterName)
    {
        if (payload.Length is < 1 || payload.Length > maximumBytes)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static async ValueTask WriteLengthAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[PrefixBytes];
        BinaryPrimitives.WriteInt32BigEndian(prefix, length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
    }
}
