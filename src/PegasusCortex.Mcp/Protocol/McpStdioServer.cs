using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace Pegasus.Cortex.Mcp.Protocol;

public sealed class McpStdioServer
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _diagnostics;
    private readonly McpRequestProcessor _processor;
    private readonly BoundedRequestDispatcher _dispatcher;
    private readonly string _sessionId;
    private readonly bool _ownsDispatcher;

    public McpStdioServer(ICortexMcpProtocolApplication application, TextReader input,
        TextWriter output, TextWriter diagnostics)
        : this(application, input, output, diagnostics, new BoundedRequestDispatcher(), Guid.NewGuid().ToString("N"), true) { }

    internal McpStdioServer(ICortexMcpProtocolApplication application, TextReader input,
        TextWriter output, TextWriter diagnostics, BoundedRequestDispatcher dispatcher, string sessionId, bool ownsDispatcher = false)
    {
        ArgumentNullException.ThrowIfNull(application);
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _processor = new McpRequestProcessor(application, diagnostics);
        _dispatcher = dispatcher; _sessionId = sessionId; _ownsDispatcher = ownsDispatcher;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try { await RunSessionAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            // Domain ownership differs from finite transport drain. Never return
            // to a caller that may dispose the application while our callback runs.
            if (_ownsDispatcher) await _dispatcher.WhenIdleAsync().ConfigureAwait(false);
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var session = new CorrelatedRequestSession(_dispatcher, _sessionId, WriteResponseAsync,
            TransportError, cancellationToken);
        var reader = new BoundedJsonLineReader(_input, McpProtocol.MaximumRequestBytes);
        while (true)
        {
            session.Token.ThrowIfCancellationRequested();
            BoundedLineReadResult read;
            try { read = await reader.ReadAsync(session.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is DecoderFallbackException or EncoderFallbackException)
            {
                await DiagnosticWriter.WriteExceptionAsync(_diagnostics, "mcp.read", "cortex_mcp_utf8_invalid",
                    exception, session.Token).ConfigureAwait(false);
                session.Reply(null, null, McpProtocol.Error(null, -32700, "Input is not valid UTF-8.", "cortex_mcp_utf8_invalid"));
                break; // Buffered malformed UTF-8 cannot be safely recovered.
            }
            if (read.EndOfStream) break;
            if (read.ExceededLimit)
            {
                await DiagnosticWriter.WriteAsync(_diagnostics, "warning", "mcp.read", "cortex_mcp_message_too_large",
                    "An oversized protocol message was drained and rejected.", session.Token).ConfigureAwait(false);
                session.Reply(null, null, McpProtocol.Error(null, -32600,
                    "Request exceeds the bounded message size.", "cortex_mcp_message_too_large"));
                continue;
            }

            var line = read.Line!;
            string? key = null, method = null, streamId = null;
            JsonNode? id = null;
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                // Reserve every valid supplied ID, including malformed-method
                // errors, so an invalid duplicate cannot emit an ambiguous reply.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var suppliedId) && CorrelatedRequestSession.ValidId(suppliedId))
                { key = CorrelatedRequestSession.IdKey(suppliedId); id = JsonNode.Parse(suppliedId.GetRawText()); }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jsonrpc", out var version) &&
                    version.ValueKind == JsonValueKind.String && version.GetString() == "2.0" &&
                    root.TryGetProperty("method", out var methodValue) && methodValue.ValueKind == JsonValueKind.String)
                {
                    method = methodValue.GetString();
                    if (_processor.Multiplex && root.TryGetProperty("params", out var requestParameters) &&
                        requestParameters.ValueKind == JsonValueKind.Object && requestParameters.TryGetProperty("_meta", out var meta) &&
                        meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("pegasus.cortex/streamId", out var stream))
                    {
                        if (stream.ValueKind != JsonValueKind.String || stream.GetString()!.Length is < 1 or > 64)
                            throw new InvalidDataException("request_stream_invalid");
                        streamId = "stream:" + stream.GetString();
                    }
                    if (!root.TryGetProperty("id", out _) && method == "notifications/cancelled" &&
                        root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("requestId", out var cancelId) && CorrelatedRequestSession.ValidId(cancelId))
                    { session.Cancel(CorrelatedRequestSession.IdKey(cancelId)); continue; }
                }
            }
            catch (JsonException) { /* The serial processor returns the existing bounded parse error. */ }

            // Lifecycle transitions and control notifications stay on the reader
            // path; a blocked domain operation cannot hide a cancellation frame.
            if (key is null || method is null || !_processor.IsReady || method == "initialize")
            {
                var initialHandshake = !_processor.IsReady && method == "initialize";
                if (key is not null) session.RequireAvailableId(key);
                var response = await _processor.ProcessAsync(line, session.Token).ConfigureAwait(false);
                if (response is not null) session.Reply(key, id, response);
                if (initialHandshake)
                {
                    await session.DrainAsync().ConfigureAwait(false);
                    if (_processor.Multiplex) session.EnableMultiplexing();
                }
            }
            else
            {
                var requestId = id!;
                session.Submit(key, requestId, Encoding.UTF8.GetByteCount(line), async token =>
                    await _processor.ProcessAsync(line, token).ConfigureAwait(false) ??
                    McpProtocol.Error(requestId.DeepClone(), -32603, "Request produced no response.", "cortex_mcp_request_failed"), streamId: streamId);
            }
        }
        await session.DrainAsync().ConfigureAwait(false);
    }

    internal static JsonObject TransportError(DispatchState state, JsonNode? id)
    {
        var code = state switch
        {
            DispatchState.NotDispatched => "cortex_mcp_cancelled_before_dispatch",
            DispatchState.CapacityExceeded => "cortex_mcp_pending_limit",
            _ => "cortex_mcp_outcome_unknown"
        };
        var response = McpProtocol.Error(id?.DeepClone(), -32000,
            state == DispatchState.Unknown ? "Request outcome is unknown; do not automatically replay." : "Request was not dispatched.", code);
        response["error"]!["data"]!["outcome"] = state == DispatchState.Unknown ? "Unknown" : "NotDispatched";
        return response;
    }

    private async ValueTask WriteResponseAsync(JsonObject response, CancellationToken cancellationToken)
    {
        var formatted = BoundedJsonResponseFormatter.Format(response);
        if (formatted.ReplacedOversizedResponse)
            await DiagnosticWriter.WriteAsync(_diagnostics, "error", "mcp.write", "cortex_mcp_response_too_large",
                "A response exceeded the bounded protocol output size and was replaced with an error.", cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(formatted.Json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal enum McpLifecycleState
{
    AwaitingInitialize,
    AwaitingInitialized,
    Ready
}

internal sealed class McpRequestProcessor(
    ICortexMcpProtocolApplication application,
    TextWriter diagnostics)
{
    private readonly CortexMcpInvocationProcessor _invocation = new(application, diagnostics);
    private McpLifecycleState _state = McpLifecycleState.AwaitingInitialize;
    internal bool IsReady => _state == McpLifecycleState.Ready;
    internal bool Multiplex { get; private set; }

    public async ValueTask<JsonObject?> ProcessAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException)
        {
            return McpProtocol.Error(null, -32700, "Parse error.", "cortex_mcp_json_invalid");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return McpProtocol.Error(null, -32600, "Invalid Request.", "cortex_mcp_request_invalid");
            if (!root.TryGetProperty("jsonrpc", out var jsonRpc) ||
                jsonRpc.ValueKind != JsonValueKind.String ||
                !string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal))
            {
                return McpProtocol.Error(null, -32600, "Invalid Request.", "cortex_mcp_jsonrpc_invalid");
            }

            var hasId = root.TryGetProperty("id", out var id);
            // MCP RequestId is string or number. In particular, an explicit null
            // is not a notification and must never advance lifecycle state.
            JsonNode? idNode = null;
            if (hasId && CortexMcpInvocationProcessor.ValidateRequestId(id, out idNode) is { } invalidId)
                return invalidId;

            if (!root.TryGetProperty("method", out var methodProperty) ||
                methodProperty.ValueKind != JsonValueKind.String)
            {
                return McpProtocol.Error(idNode, -32600, "Invalid Request.", "cortex_mcp_method_invalid");
            }

            var method = methodProperty.GetString();
            if (!CortexMcpInvocationProcessor.ValidMethod(method))
                return McpProtocol.Error(idNode, -32600, "Invalid Request.", "cortex_mcp_method_invalid");

            JsonElement? parameters = root.TryGetProperty("params", out var paramsProperty)
                ? paramsProperty
                : null;
            if (!hasId)
            {
                await HandleNotificationAsync(method, parameters, cancellationToken).ConfigureAwait(false);
                return null;
            }

            try
            {
                return await HandleRequestAsync(idNode, method, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return await _invocation.RequestFailedAsync(idNode, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask HandleNotificationAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
        {
            if (_state == McpLifecycleState.AwaitingInitialized && IsObjectOrAbsent(parameters))
            {
                _state = McpLifecycleState.Ready;
                return;
            }

            await DiagnosticWriter.WriteAsync(
                diagnostics,
                "warning",
                "mcp.lifecycle",
                "cortex_mcp_initialized_out_of_order",
                "The initialized notification was ignored because initialization was incomplete or its parameters were invalid.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // The concurrent transport owns cancellation IDs. This processor also
        // serves framed serial callers, which have no outstanding request registry.
        if (string.Equals(method, "notifications/cancelled", StringComparison.Ordinal))
            return;

        // JSON-RPC notifications are intentionally fire-and-forget. Unknown
        // notifications produce no stdout response.
    }

    private async ValueTask<JsonObject> HandleRequestAsync(
        JsonNode? id,
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(method, "initialize", StringComparison.Ordinal))
            return Initialize(id, parameters);

        if (_state != McpLifecycleState.Ready)
        {
            return McpProtocol.Error(
                id,
                -32002,
                "Server initialization is not complete.",
                "cortex_mcp_not_initialized");
        }

        return await _invocation.InvokeValidatedAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject Initialize(JsonNode? id, JsonElement? parameters)
    {
        if (_state != McpLifecycleState.AwaitingInitialize)
        {
            return CortexMcpInvocationProcessor.InitializeRepeatedError(id);
        }

        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty("protocolVersion", out var protocolVersion) ||
            protocolVersion.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(protocolVersion.GetString()) ||
            protocolVersion.GetString()!.Length > 32 ||
            !parameters.Value.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty("clientInfo", out var clientInfo) ||
            !ValidClientInfo(clientInfo))
        {
            return McpProtocol.Error(id, -32602, "Invalid initialize parameters.", "cortex_mcp_initialize_params_invalid");
        }

        // MCP negotiation requires the server to return a version it supports
        // when the requested version is unavailable. This host supports one exact revision.
        Multiplex = capabilities.TryGetProperty("experimental", out var experimental) &&
            experimental.ValueKind == JsonValueKind.Object && experimental.TryGetProperty("pegasus.cortex/multiplex", out var multiplex) &&
            multiplex.ValueKind == JsonValueKind.True;
        _state = McpLifecycleState.AwaitingInitialized;
        return McpProtocol.Success(id, CortexMcpInvocationProcessor.CreateInitializeResult());
    }

    private static bool IsObjectOrAbsent(JsonElement? value) =>
        value is null || value.Value.ValueKind == JsonValueKind.Object;

    private static bool ValidClientInfo(JsonElement clientInfo) =>
        clientInfo.ValueKind == JsonValueKind.Object &&
        clientInfo.TryGetProperty("name", out var name) &&
        name.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(name.GetString()) &&
        name.GetString()!.Length <= 128 &&
        clientInfo.TryGetProperty("version", out var version) &&
        version.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(version.GetString()) &&
        version.GetString()!.Length <= 64;
}
