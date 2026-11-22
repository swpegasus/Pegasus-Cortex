using System.Text;
using System.Text.Json.Nodes;

namespace Pegasus.Cortex.Mcp.Protocol;

/// <summary>
/// One stateful MCP JSON-line session detached from any concrete transport.
/// Create a new instance per stdio or local-pipe connection so lifecycle state
/// can never leak between clients.
/// </summary>
public sealed class McpJsonLineSessionProcessor
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly TextWriter _diagnostics;
    private readonly McpRequestProcessor _processor;

    public McpJsonLineSessionProcessor(
        ICortexMcpProtocolApplication application,
        TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        _diagnostics = diagnostics ?? TextWriter.Null;
        _processor = new McpRequestProcessor(application, _diagnostics);
    }

    /// <summary>
    /// Processes one exact UTF-8 JSON-RPC line without its newline delimiter.
    /// A null result is the normal completion for an MCP notification.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ProcessAsync(
        ReadOnlyMemory<byte> requestUtf8,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject? response;
        if (requestUtf8.Length is < 1 or > McpProtocol.MaximumRequestBytes)
        {
            response = McpProtocol.Error(
                null,
                -32600,
                "Request exceeds the bounded message size.",
                "cortex_mcp_message_too_large");
        }
        else if (requestUtf8.Span.IndexOfAny((byte)'\r', (byte)'\n') >= 0)
        {
            response = McpProtocol.Error(
                null,
                -32600,
                "A framed request must contain exactly one JSON line.",
                "cortex_mcp_frame_invalid");
        }
        else
        {
            string line;
            try
            {
                line = StrictUtf8.GetString(requestUtf8.Span);
            }
            catch (DecoderFallbackException exception)
            {
                await DiagnosticWriter.WriteExceptionAsync(
                    _diagnostics,
                    "mcp.frame.read",
                    "cortex_mcp_utf8_invalid",
                    exception,
                    cancellationToken).ConfigureAwait(false);
                response = McpProtocol.Error(
                    null,
                    -32700,
                    "Input is not valid UTF-8.",
                    "cortex_mcp_utf8_invalid");
                return await Format(response, cancellationToken).ConfigureAwait(false);
            }

            response = await _processor.ProcessAsync(line, cancellationToken)
                .ConfigureAwait(false);
        }

        if (response is null)
            return (ReadOnlyMemory<byte>?)null;
        return await Format(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ReadOnlyMemory<byte>> Format(
        JsonObject response,
        CancellationToken cancellationToken)
    {
        var formatted = BoundedJsonResponseFormatter.Format(response);
        if (formatted.ReplacedOversizedResponse)
        {
            await DiagnosticWriter.WriteAsync(
                _diagnostics,
                "error",
                "mcp.frame.write",
                "cortex_mcp_response_too_large",
                "A response exceeded the bounded protocol output size and was replaced with an error.",
                cancellationToken).ConfigureAwait(false);
        }
        return StrictUtf8.GetBytes(formatted.Json);
    }
}

/// <summary>
/// Public bounded line-reader facade for explicit transports such as the local
/// pipe bridge. It retains the same byte-count and drain behavior as stdio.
/// </summary>
public sealed class McpJsonLineReader
{
    private readonly BoundedJsonLineReader _reader;

    public McpJsonLineReader(
        TextReader reader,
        int maximumBytes = McpProtocol.MaximumRequestBytes)
    {
        _reader = new BoundedJsonLineReader(reader, maximumBytes);
    }

    public async ValueTask<McpJsonLineReadResult> ReadAsync(
        CancellationToken cancellationToken)
    {
        var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new McpJsonLineReadResult(
            result.Line,
            result.EndOfStream,
            result.ExceededLimit);
    }
}

public readonly record struct McpJsonLineReadResult(
    string? Line,
    bool EndOfStream,
    bool ExceededLimit);

/// <summary>
/// Strict UTF-8 stream-reader factory shared by explicit MCP transports.
/// </summary>
public static class McpStrictUtf8
{
    public static StreamReader CreateReader(Stream stream, bool leaveOpen) => new(
        stream ?? throw new ArgumentNullException(nameof(stream)),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 4096,
        leaveOpen);
}
