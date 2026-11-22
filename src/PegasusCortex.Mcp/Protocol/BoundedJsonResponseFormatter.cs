using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pegasus.Cortex.Mcp.Protocol;

internal readonly record struct BoundedJsonResponse(
    string Json,
    bool ReplacedOversizedResponse);

internal static class BoundedJsonResponseFormatter
{
    public static BoundedJsonResponse Format(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (TrySerialize(response, out var json))
            return new BoundedJsonResponse(json, ReplacedOversizedResponse: false);

        var replacement = McpProtocol.Error(
            response["id"]?.DeepClone(),
            -32603,
            "Response exceeds the bounded output size.",
            "cortex_mcp_response_too_large");
        if (!TrySerialize(replacement, out json))
        {
            // Request IDs are independently bounded, so this is only a final
            // fail-closed guard against an internally malformed response.
            replacement = McpProtocol.Error(
                null,
                -32603,
                "Response exceeds the bounded output size.",
                "cortex_mcp_response_too_large");
            if (!TrySerialize(replacement, out json))
                throw new InvalidOperationException("The bounded MCP error response could not be serialized.");
        }

        return new BoundedJsonResponse(json, ReplacedOversizedResponse: true);
    }

    /// <summary>
    /// Measures a JSON value through the same bounded UTF-8 writer used for wire
    /// responses. Callers can therefore reject an optional large projection before
    /// it is placed in the outer JSON-RPC response.
    /// </summary>
    public static bool FitsWithinLimit(
        JsonObject value,
        int maximumBytes,
        out int encodedBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var buffer = new BoundedByteBufferWriter(maximumBytes);
        if (!TryWrite(value, buffer))
        {
            encodedBytes = 0;
            return false;
        }

        encodedBytes = buffer.WrittenSpan.Length;
        return true;
    }

    private static bool TrySerialize(JsonObject response, out string json)
    {
        using var buffer = new BoundedByteBufferWriter(McpProtocol.MaximumResponseBytes);
        if (TryWrite(response, buffer))
        {
            json = Encoding.UTF8.GetString(buffer.WrittenSpan);
            return true;
        }

        json = string.Empty;
        return false;
    }

    private static bool TryWrite(JsonObject response, BoundedByteBufferWriter buffer)
    {
        try
        {
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = McpProtocol.JsonOptions.Encoder,
                Indented = false,
                SkipValidation = false
            });
            response.WriteTo(writer, McpProtocol.JsonOptions);
            writer.Flush();
            return true;
        }
        catch (JsonResponseLimitExceededException)
        {
            return false;
        }
    }
}

internal sealed class JsonResponseLimitExceededException : InvalidOperationException;

internal sealed class BoundedByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly int _maximumBytes;
    private byte[] _buffer;
    private int _written;

    public BoundedByteBufferWriter(int maximumBytes)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, maximumBytes));
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Advance(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count > _maximumBytes - _written)
            throw new JsonResponseLimitExceededException();
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(
            _written,
            Math.Min(_buffer.Length - _written, _maximumBytes - _written));
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(
            _written,
            Math.Min(_buffer.Length - _written, _maximumBytes - _written));
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length != 0)
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        sizeHint = Math.Max(sizeHint, 1);
        if (_written > _maximumBytes - sizeHint)
            throw new JsonResponseLimitExceededException();
        var required = _written + sizeHint;
        if (required <= _buffer.Length)
            return;

        var nextLength = Math.Min(
            _maximumBytes,
            Math.Max(required, Math.Min(_maximumBytes, _buffer.Length * 2)));
        var next = ArrayPool<byte>.Shared.Rent(nextLength);
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = next;
    }
}
