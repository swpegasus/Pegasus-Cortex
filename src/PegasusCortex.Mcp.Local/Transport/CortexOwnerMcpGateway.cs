using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.SharedMcp;

namespace Pegasus.Cortex.Mcp.Local.Transport;

/// <summary>
/// The already-authenticated service pipe's gateway to this ordinary Host.
/// It owns no listener, credentials, database or execution engine. The actual
/// pipe entry must authenticate the service before calling this object.
/// </summary>
public sealed class CortexOwnerMcpGateway
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Func<IMcpDispatcher>? _factory;
    private IMcpDispatcher? _dispatcher;
    private bool _closing;
    private bool _bound;
    private int _active;
    private TaskCompletionSource _idle = Completed();

    public static bool IsServiceRelay(OperationRequest request) => request.Op == CortexSharedMcpRelay.Operation;

    internal void Bind(CortexMcpSettings? startupSettings, Func<IMcpDispatcher> factory)
    {
        lock (_gate)
        {
            if (_bound || _closing) throw new InvalidOperationException("The owner gateway is already bound or closed.");
            _bound = true;
            if (startupSettings is { Enabled: true, Owner: CortexMcpOwner.Service }) _factory = factory;
        }
    }

    public async Task<OperationReceipt> ExecuteServiceRequestAsync(OperationRequest request, CancellationToken token)
    {
        CortexSharedMcpRelay.RequireRequest(request);
        token.ThrowIfCancellationRequested();
        IMcpDispatcher dispatcher;
        lock (_gate)
        {
            if (_closing || _factory is null) return Failure(request, "shared_mcp_unavailable");
            dispatcher = _dispatcher ??= _factory();
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            token = lifetime.Token;
            using var body = JsonDocument.Parse(request.Body!, new JsonDocumentOptions { MaxDepth = 64 });
            var input = body.RootElement;
            JsonObject response;
            if (request.Arg["kind"] == "initialize")
            {
                if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any())
                    return Failure(request, "invalid");
                response = await dispatcher.InitializeAsync(token).ConfigureAwait(false);
            }
            else
            {
                if (!TryInvocation(input, out var context, out var invocation)) return Failure(request, "invalid");
                response = await dispatcher.DispatchAsync(context!, invocation!, token).ConfigureAwait(false);
            }
            return new(true, Guid.NewGuid().ToString("N"), request.Rid!, "done", CortexSharedMcpRelay.Operation,
                Data: new Dictionary<string, object?> { ["mcp"] = response });
        }
        catch (JsonException) { return Failure(request, "invalid"); }
        finally
        {
            lock (_gate) if (--_active == 0) _idle.TrySetResult();
        }
    }

    internal async Task CloseAndDrainAsync()
    {
        BeginClose();
        Task idle;
        lock (_gate) { idle = _idle.Task; }
        await idle.ConfigureAwait(false);
        IMcpDispatcher? dispatcher;
        lock (_gate) { dispatcher = _dispatcher; _dispatcher = null; }
        try { if (dispatcher is not null) await dispatcher.DisposeAsync().ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }

    internal void BeginClose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true; _factory = null;
        }
        try { _lifetime.Cancel(); }
        catch (AggregateException) { /* Join every original callback even if a cancellation callback fails. */ }
        catch (ObjectDisposedException) { /* Concurrent drain already observed idle and completed disposal. */ }
    }

    private static bool TryInvocation(JsonElement input, out McpRequestContext? context, out JsonObject? request)
    {
        context = null; request = null;
        if (input.ValueKind != JsonValueKind.Object ||
            input.EnumerateObject().Count() != 6 ||
            input.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 6 ||
            input.EnumerateObject().Any(p => p.Name is not ("sessionId" or "streamId" or "taskId" or "requestId" or "identity" or "request")) ||
            !input.TryGetProperty("identity",out var identity)||identity.ValueKind!=JsonValueKind.Object||
            identity.EnumerateObject().Count()!=3||identity.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=3||
            identity.EnumerateObject().Any(p=>p.Name is not("authority" or "subject" or "generation"))||
            !identity.TryGetProperty("authority",out var authority)||authority.ValueKind!=JsonValueKind.String||
            !Pegasus.Cortex.Product.CortexMcpConnections.IsId(authority.GetString())||
            !identity.TryGetProperty("subject",out var subject)||subject.ValueKind!=JsonValueKind.String||
            !Pegasus.Cortex.Product.CortexMcpConnections.IsId(subject.GetString())||
            !identity.TryGetProperty("generation",out var generation)||generation.ValueKind!=JsonValueKind.String||
            !Pegasus.Cortex.Product.CortexMcpConnections.IsId(generation.GetString())||
            !input.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String ||
            session.GetString() is not { Length: 32 } sessionId || !sessionId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !input.TryGetProperty("streamId", out var stream) || stream.ValueKind != JsonValueKind.Number ||
            !stream.TryGetInt32(out var streamId) || streamId <= 0 ||
            !input.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() is not { Length: > 0 and <= 2048 } requestId ||
            !input.TryGetProperty("taskId", out var task) || task.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            task.ValueKind == JsonValueKind.String && (task.GetString()!.Length > 512 || task.GetString()!.Any(char.IsControl)) ||
            !input.TryGetProperty("request", out var invocation) || invocation.ValueKind != JsonValueKind.Object)
            return false;
        request = JsonNode.Parse(invocation.GetRawText())!.AsObject();
        // The neutral transport's typed-ID correlation must survive this private
        // hop, but it never selects an owner, execution context or credential.
        if (request["id"]?.ToJsonString() != requestId) return false;
        context = new("cortex", sessionId, streamId, task.ValueKind == JsonValueKind.String ? task.GetString() : null, requestId)
            { Identity=new McpAuthenticatedIdentity(authority.GetString()!,subject.GetString()!,generation.GetString()!) };
        return true;
    }

    private static OperationReceipt Failure(OperationRequest request, string code) =>
        new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", CortexSharedMcpRelay.Operation, code);

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }
}
