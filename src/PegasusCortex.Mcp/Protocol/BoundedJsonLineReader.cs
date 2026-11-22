using System.Text;

namespace Pegasus.Cortex.Mcp.Protocol;

public readonly record struct BoundedLineReadResult(
    string? Line,
    bool EndOfStream,
    bool ExceededLimit);

/// <summary>
/// Reads strict UTF-8 newline-delimited protocol messages through a TextReader and
/// enforces the limit over the payload's canonical UTF-8 bytes. Oversized lines are
/// drained through their newline so the next valid request can still be processed.
/// </summary>
public sealed class BoundedJsonLineReader
{
    private const int BufferSize = 4096;
    private readonly TextReader _reader;
    private readonly int _maximumBytes;
    private readonly char[] _buffer = new char[BufferSize];
    private int _offset;
    private int _count;
    private bool _terminalCancellation;

    public BoundedJsonLineReader(TextReader reader, int maximumBytes)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    public async ValueTask<BoundedLineReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (_terminalCancellation)
            throw new InvalidOperationException("A cancelled bounded reader cannot be reused.");
        cancellationToken.ThrowIfCancellationRequested();
        var line = new StringBuilder(capacity: 256);
        var payloadBytes = 0;
        var exceededLimit = false;
        var sawCharacter = false;
        var pendingCarriageReturn = false;
        var pendingHighSurrogate = '\0';

        while (true)
        {
            if (_offset >= _count)
            {
                _count = await ReadBufferAsync(cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                {
                    if (pendingHighSurrogate != '\0')
                        throw InvalidUtf8();
                    if (!sawCharacter)
                        return new BoundedLineReadResult(null, EndOfStream: true, ExceededLimit: false);

                    // Preserve the legacy framing behavior: a final CR at EOF is
                    // treated like the CR in CRLF and is not part of the payload.
                    return exceededLimit
                        ? new BoundedLineReadResult(null, EndOfStream: false, ExceededLimit: true)
                        : new BoundedLineReadResult(line.ToString(), EndOfStream: false, ExceededLimit: false);
                }
            }

            var character = _buffer[_offset++];
            sawCharacter = true;

            if (pendingHighSurrogate != '\0')
            {
                if (!char.IsLowSurrogate(character))
                    throw InvalidUtf8();
                AppendScalar(
                    line,
                    pendingHighSurrogate,
                    character,
                    utf8Bytes: 4,
                    ref payloadBytes,
                    ref exceededLimit);
                pendingHighSurrogate = '\0';
                continue;
            }

            if (char.IsLowSurrogate(character))
                throw InvalidUtf8();

            if (character == '\n')
            {
                // LF and its immediately preceding framing CR are excluded from
                // the request-byte limit. An internal CR is committed below.
                return exceededLimit
                    ? new BoundedLineReadResult(null, EndOfStream: false, ExceededLimit: true)
                    : new BoundedLineReadResult(line.ToString(), EndOfStream: false, ExceededLimit: false);
            }

            if (pendingCarriageReturn)
            {
                AppendScalar(
                    line,
                    '\r',
                    second: null,
                    utf8Bytes: 1,
                    ref payloadBytes,
                    ref exceededLimit);
                pendingCarriageReturn = false;
            }

            if (character == '\r')
            {
                pendingCarriageReturn = true;
                continue;
            }

            if (char.IsHighSurrogate(character))
            {
                pendingHighSurrogate = character;
                continue;
            }

            var utf8Bytes = character <= '\u007f'
                ? 1
                : character <= '\u07ff'
                    ? 2
                    : 3;
            AppendScalar(
                line,
                character,
                second: null,
                utf8Bytes,
                ref payloadBytes,
                ref exceededLimit);
        }
    }

    private async ValueTask<int> ReadBufferAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pendingRead = _reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
        if (pendingRead.IsCompletedSuccessfully)
        {
            var count = pendingRead.Result;
            cancellationToken.ThrowIfCancellationRequested();
            return count;
        }
        if (!cancellationToken.CanBeCanceled)
            return await pendingRead.ConfigureAwait(false);

        var readTask = pendingRead.AsTask();
        try
        {
            // Some console and pipe readers do not interrupt an in-flight OS read
            // when their token is cancelled. WaitAsync keeps server shutdown bounded.
            return await readTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation ends this MCP session. Poison the reader because an
            // abandoned OS read may still own and later write into its buffer.
            _terminalCancellation = true;
            // Observe any later read fault without changing the cancellation result.
            _ = readTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }
    }

    private void AppendScalar(
        StringBuilder line,
        char first,
        char? second,
        int utf8Bytes,
        ref int payloadBytes,
        ref bool exceededLimit)
    {
        if (exceededLimit)
            return;
        if (payloadBytes > _maximumBytes - utf8Bytes)
        {
            exceededLimit = true;
            line.Clear();
            return;
        }

        line.Append(first);
        if (second.HasValue)
            line.Append(second.Value);
        payloadBytes += utf8Bytes;
    }

    private static DecoderFallbackException InvalidUtf8() =>
        new("Input contains a character sequence that cannot be encoded as UTF-8.");
}

/// <summary>
/// Owns the real stdio decoding policy. Disabling BOM auto-detection prevents a
/// UTF-16/UTF-32 stream from bypassing the UTF-8 request-byte contract.
/// </summary>
internal static class McpUtf8Input
{
    public static StreamReader Create(Stream stream, bool leaveOpen) => new(
        stream ?? throw new ArgumentNullException(nameof(stream)),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 4096,
        leaveOpen);
}
