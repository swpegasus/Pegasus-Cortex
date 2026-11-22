using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.SharedMcp;

namespace Pegasus.Cortex.Mcp.Local.Transport;

/// <summary>
/// Adapts the existing ordinary-owner application to shared HTTP without another
/// composition. The resident owns the application and the shared native/HTTP
/// scheduling budget; HTTP correlation labels confer no execution authority.
/// </summary>
internal sealed class CortexEmbeddedMcpDispatcher : IMcpDispatcher
{
    private readonly CortexMcpInvocationProcessor _invocations;
    private readonly BoundedRequestDispatcher _dispatcher;
    private readonly UserHostAdmission _admission;
    private int _closed;

    internal CortexEmbeddedMcpDispatcher(ICortexMcpProtocolApplication admittedApplication,
        BoundedRequestDispatcher dispatcher, UserHostAdmission admission, TextWriter diagnostics)
    {
        _invocations = new(admittedApplication, diagnostics);
        _dispatcher = dispatcher;
        _admission = admission;
    }

    public Task<JsonObject> InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var admission = _admission.TryEnter();
        if (Volatile.Read(ref _closed) != 0 || admission is null)
            throw new IOException("host_stopping");
        return Task.FromResult(CortexMcpInvocationProcessor.CreateInitializeResult());
    }

    public async Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        if (payload.Length > McpProtocol.MaximumRequestBytes)
            return McpProtocol.Error(null, -32600, "Request exceeds the bounded protocol size.", "cortex_mcp_request_too_large");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException)
        { return McpProtocol.Error(null, -32700, "Parse error.", "cortex_mcp_json_invalid"); }
        using var document = parsed;
        var envelope = document.RootElement;
        if (!envelope.TryGetProperty("jsonrpc", out var jsonrpc) || jsonrpc.ValueKind != JsonValueKind.String || jsonrpc.GetString() != "2.0" ||
            !envelope.TryGetProperty("id", out var requestId) ||
            !CorrelatedRequestSession.ValidId(requestId) ||
            !envelope.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String)
            return McpProtocol.Error(null, -32600, "Invalid Request.", "cortex_mcp_request_invalid");

        var id = requestId.Clone();
        var idNode = JsonNode.Parse(id.GetRawText());
        var method = methodValue.GetString()!;
        JsonElement? parameters = envelope.TryGetProperty("params", out var values) ? values.Clone() : null;
        if (Volatile.Read(ref _closed) != 0 || !_admission.Accepting)
            return McpStdioServer.TransportError(DispatchState.NotDispatched, idNode);

        // The transport assigns these labels; neither a task ID nor HTTP metadata
        // changes the owner identity, PAO route, credential or root-code policy.
        var session = "shared:" + context.SessionId;
        var stream = context.StreamId.ToString(CultureInfo.InvariantCulture);
        if (!_dispatcher.TrySubmit(session, payload.Length,
                token => _invocations.InvokeAsync(id, method, parameters, token),
                cancellationToken, CorrelatedRequestSession.RequestTimeout, out var ticket, stream))
            return McpStdioServer.TransportError(DispatchState.CapacityExceeded, idNode);

        // Cancellation can settle the transport before a noncooperative callback
        // returns. Cloned elements remain valid for that callback; the resident
        // retains its scheduler reservation and drains before disposing storage.
        var outcome = await ticket.Completion.ConfigureAwait(false);
        var response = outcome.Response ?? McpStdioServer.TransportError(outcome.State, idNode);
        var formatted = BoundedJsonResponseFormatter.Format(response);
        return formatted.ReplacedOversizedResponse
            ? JsonNode.Parse(formatted.Json)!.AsObject()
            : response;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        // The resident owns and drains the shared scheduler and composition.
        return ValueTask.CompletedTask;
    }
}
