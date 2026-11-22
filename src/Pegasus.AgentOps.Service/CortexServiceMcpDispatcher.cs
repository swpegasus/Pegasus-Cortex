using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.SharedMcp;

namespace Pegasus.AgentOps.Service;

/// <summary>
/// The service owns HTTPS only. Every product invocation crosses its existing
/// generation-pinned authenticated route to the ordinary owner Host; no semantic
/// composition, owner-private certificate, STDIO process or System marker is created here.
/// </summary>
internal sealed class CortexServiceMcpDispatcher(AuthenticatedServiceBroker broker) : IMcpDispatcher
{
    private int _closed;

    public async Task<JsonObject> InitializeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0) throw new IOException("shared_mcp_stopping");
        var receipt = await RelayAsync("initialize", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (!receipt.Ok || receipt.Partial || ReadResponse(receipt) is not { } result)
            throw new IOException("shared_mcp_owner_unavailable");
        return result;
    }

    public async Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed) != 0) return Failure(request, "shared_mcp_stopping", unknown: false);
        var body = new JsonObject
        {
            ["sessionId"] = context.SessionId, ["streamId"] = context.StreamId,
            ["taskId"] = context.TaskId, ["requestId"] = context.RequestId,
            ["request"] = request.DeepClone()
        };
        var receipt = await RelayAsync("invoke", body, cancellationToken).ConfigureAwait(false);
        if (receipt.Ok && !receipt.Partial && ReadResponse(receipt) is { } response) return response;
        return Failure(request, SafeCode(receipt.Err), receipt.Partial || receipt.Ok);
    }

    private Task<OperationReceipt> RelayAsync(string kind, JsonObject body, CancellationToken token) =>
        broker.RelaySharedMcpAsync(new(CortexSharedMcpRelay.Operation,
            new Dictionary<string, string> { ["kind"] = kind }, Guid.NewGuid().ToString("N"),
            Body: body.ToJsonString()), token);

    private static JsonObject? ReadResponse(OperationReceipt receipt)
    {
        if (receipt.Data?.GetValueOrDefault("mcp") is not { } value) return null;
        return value switch
        {
            JsonObject node => node.DeepClone().AsObject(),
            JsonElement { ValueKind: JsonValueKind.Object } element => JsonNode.Parse(element.GetRawText())!.AsObject(),
            _ => null
        };
    }

    private static string SafeCode(string? code) => code is { Length: > 0 and <= 64 } &&
        code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? code : "shared_mcp_owner_unavailable";

    private static JsonObject Failure(JsonObject request, string code, bool unknown) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = request["id"]?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = -32000,
            ["message"] = unknown ? "Request outcome is unknown; do not automatically replay." : "Cortex owner is unavailable; no request was dispatched.",
            ["data"] = new JsonObject { ["code"] = code, ["outcome"] = unknown ? "Unknown" : "NotDispatched" }
        }
    };

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        // The service runtime retains the broker until all guarded calls drain.
        return ValueTask.CompletedTask;
    }
}
