using System.Text;
using Pegasus.Cortex.LocalTransport.Configuration;
using Pegasus.Cortex.LocalTransport.Pipes;
using Pegasus.Cortex.LocalTransport.Protocol;
using Pegasus.Cortex.Mcp.Protocol;

namespace Pegasus.Cortex.Mcp.Local.Transport;

/// <summary>
/// One pipe-session adapter. A fresh instance owns a fresh MCP lifecycle state.
/// </summary>
internal sealed class CortexMcpLocalPipeHandler : ICortexLocalRequestHandler
{
    private readonly McpJsonLineSessionProcessor _processor;

    public CortexMcpLocalPipeHandler(
        ICortexMcpProtocolApplication application,
        TextWriter diagnostics)
    {
        _processor = new McpJsonLineSessionProcessor(
            application ?? throw new ArgumentNullException(nameof(application)),
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics)));
    }

    public ValueTask<ReadOnlyMemory<byte>?> ProcessAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken) =>
        _processor.ProcessAsync(request, cancellationToken);
}

/// <summary>
/// Bounded stdio-to-current-user-pipe relay. It never opens persistence and
/// never falls back to direct stdio composition if the service is unavailable.
/// </summary>
internal static class CortexMcpLocalBridge
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task RunAsync(
        string profileId,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var endpoint = CortexLocalEndpoint.Create(profileId);
        await using var client = await CortexLocalPipeClient.ConnectAsync(
            endpoint,
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        var reader = new McpJsonLineReader(input);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (line.EndOfStream)
                return;
            if (line.ExceededLimit)
            {
                await WriteProtocolErrorAsync(
                    output,
                    "Request exceeds the bounded message size.",
                    "cortex_mcp_message_too_large",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var request = StrictUtf8.GetBytes(line.Line!);
            var response = await client.CallAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
                continue;
            if (response.Value.Length > McpProtocol.MaximumResponseBytes ||
                response.Value.Span.IndexOfAny((byte)'\r', (byte)'\n') >= 0)
            {
                throw new InvalidDataException(
                    "The local Cortex service returned an invalid JSON-line frame.");
            }
            var json = StrictUtf8.GetString(response.Value.Span);
            await output.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteProtocolErrorAsync(
        TextWriter output,
        string message,
        string code,
        CancellationToken cancellationToken)
    {
        var json =
            $"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32600,\"message\":\"{message}\",\"data\":{{\"code\":\"{code}\"}}}}}}";
        await output.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
