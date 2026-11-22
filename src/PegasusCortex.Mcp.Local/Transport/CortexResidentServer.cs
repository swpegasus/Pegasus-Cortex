using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Product;
using Pegasus.SharedMcp;

namespace Pegasus.Cortex.Mcp.Local.Transport;

/// <summary>Two wire adapters, one shared application/store. No client starts a semantic process.</summary>
internal sealed class CortexResidentServer(ICortexMcpProtocolApplication application,
    CortexClientRegistry clients, LocalPeer owner, string hostVersion, TextWriter diagnostics,
    UserHostAdmission? admission = null, Action? endpointsStopping = null)
{
    public const string NativeContract = "pegasus.cortex.native/1";
    public const string NativeMultiplexContract = "pegasus.cortex.native/2";
    public const string McpContract = "pegasus.cortex.mcp-pipe/1";
    public const int RequestBytes = 256 * 1024;
    public const int ResponseBytes = 512 * 1024;
    public const int ConnectionLimit = 24; // Shared across native and MCP, not one budget per endpoint.
    private readonly SemaphoreSlim _slots = new(ConnectionLimit, ConnectionLimit);
    private readonly ConcurrentDictionary<string, Task> _sessions = new();
    private readonly BoundedRequestDispatcher _dispatcher = new();
    internal int ConnectionCount => _sessions.Count;

    internal IMcpDispatcher CreateOwnerEndpointDispatcher(UserHostAdmission ownerAdmission,string? installRoot=null)
    {
        if (!ReferenceEquals(admission, ownerAdmission))
            throw new InvalidOperationException("The endpoint must retain the resident owner's admission boundary.");
        return new CortexEmbeddedMcpDispatcher(
            CortexClientApplication.ForOwnerEndpoint(application, ownerAdmission),
            _dispatcher, ownerAdmission, diagnostics, installRoot is null?null:context =>
            {
                var identity=context.Identity??throw new UnauthorizedAccessException("connection_auth_required");
                var store=Pegasus.Cortex.Product.CortexMcpConnections.ReadForInstallRoot(installRoot);
                var grant=store?.Connections.SingleOrDefault(item=>item.Request.ConnectionId==identity.Subject);
                if(store?.Authority!=identity.Authority||grant is not { Enabled:true }||
                    grant.CredentialGeneration!=identity.Generation)throw new UnauthorizedAccessException("connection_revoked");
                var authority=new Pegasus.Cortex.Product.CortexMcpConnectionAuthority(store.Authority,
                    grant.Request.ConnectionId,grant.CredentialGeneration,grant.Revision);
                bool Current(bool full)=>Pegasus.Cortex.Product.CortexMcpConnections.Find(
                    Pegasus.Cortex.Product.CortexMcpConnections.ReadForInstallRoot(installRoot),authority,full) is not null;
                var scoped=application is Pegasus.Cortex.Mcp.Local.Runtime.CortexUnifiedMcpApplication unified
                    ?unified.ForConnection(authority,grant.Request.Name,Current):application;
                return new Pegasus.Cortex.Mcp.Protocol.CortexMcpInvocationProcessor(
                    CortexClientApplication.ForOwnerEndpoint(scoped,ownerAdmission,()=>Current(false)),diagnostics);
            });
    }

    // HTTP cancellation may settle its response before a callback cooperates.
    // The composition owner calls this after every transport has stopped, too.
    internal Task WhenIdleAsync() => _dispatcher.WhenIdleAsync();

    public Task RunAsync(string nativePipe, string mcpPipe, CancellationToken cancellationToken) =>
        RunEndpointsAsync([(nativePipe, "native"), (mcpPipe, "mcp")], cancellationToken);

    // The source interoperability harness needs the real native endpoint only,
    // not an installed host, MCP endpoint or execution subsystem.
    internal Task RunNativeAsync(string nativePipe, CancellationToken cancellationToken) =>
        RunEndpointsAsync([(nativePipe, "native")], cancellationToken);

    private async Task RunEndpointsAsync((string Name, string Transport)[] endpoints, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listeners = endpoints.Select(endpoint => AcceptAsync(endpoint.Name, endpoint.Transport, stop.Token)).ToArray();
        try { await await Task.WhenAny(listeners); }
        finally
        {
            try
            {
                Exception? stoppingFailure = null;
                try
                {
                    try { endpointsStopping?.Invoke(); }
                    finally { stop.Cancel(); }
                }
                catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
                { stoppingFailure = failure; }
                finally
                {
                    // Even a failing shutdown notification must join every
                    // accept loop before taking the final session snapshot.
                    try { await Task.WhenAll(listeners); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception failure) when (stoppingFailure is not null)
                    { await diagnostics.WriteLineAsync("component=resident-listeners state=stop_failed cause=" + failure.GetType().Name); }
                }
                if (stoppingFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stoppingFailure).Throw();
            }
            finally
            {
                try { await Task.WhenAll(_sessions.Values); }
                finally
                {
                    // Cleanup failures cannot dispose shared dependencies under
                    // a callback which outlives its finite transport drain.
                    await _dispatcher.WhenIdleAsync();
                    _slots.Dispose();
                }
            }
        }
    }

    private async Task AcceptAsync(string name, string transport, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Keep accepting to reject overflow explicitly; idle clients cannot block admission.
            var pipe = LocalPipeFactory.Create(name, owner.Sid);
            try { await pipe.WaitForConnectionAsync(cancellationToken); }
            catch { pipe.Dispose(); throw; }
            if (!_slots.Wait(0)) { pipe.Dispose(); continue; }
            var id = Guid.NewGuid().ToString("N");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = ServeAsync(pipe, transport, id, ready.Task, cancellationToken);
            _sessions[id] = task;
            ready.SetResult();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, string transport, string id, Task ready, CancellationToken cancellationToken)
    {
        await ready;
        using var close = cancellationToken.Register(pipe.Dispose);
        var dataPhase = false;
        try
        {
            var peer = LocalIdentity.Capture(pipe);
            if (!peer.SameIdentity(owner)) return;
            using var authDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            var hello = await BinaryProtocol.ReadAsync<JsonElement>(pipe, authDeadline.Token, 4096);
            RequireObject(hello, "op", "contract", "clientId", "keyId", "apiKey", "cwd");
            var requestedContract = String(hello, "contract");
            var contract = transport == "native"
                ? requestedContract == NativeMultiplexContract ? NativeMultiplexContract : NativeContract
                : McpContract;
            if (String(hello, "op") != "hello" || String(hello, "contract") != contract)
            {
                await BinaryProtocol.WriteAsync(pipe, Error("contract_mismatch"), authDeadline.Token, ResponseBytes);
                return;
            }
            var lease = clients.Authenticate(String(hello, "clientId"), String(hello, "keyId"), String(hello, "apiKey"), transport, peer);
            using var handshake = admission?.TryEnter();
            if (admission is not null && handshake is null)
            {
                await BinaryProtocol.WriteAsync(pipe, Describe(contract, lease.ClientId, stopping: true), authDeadline.Token, ResponseBytes);
                return;
            }
            string? callerCwd = null;
            if (hello.TryGetProperty("cwd", out _))
            {
                callerCwd = CortexProductLayout.RequireCanonicalAbsoluteDirectory(String(hello, "cwd"), "cwd");
                CortexProductLayout.RejectExistingReparseComponents(callerCwd);
            }
            hello = default; // Do not keep the credential frame for the lifetime of a connection.
            var scoped = new CortexClientApplication(application, clients, lease, callerCwd, admission);
            await BinaryProtocol.WriteAsync(pipe, Describe(contract, lease.ClientId), authDeadline.Token, ResponseBytes);
            dataPhase = true;
            handshake?.Dispose();
            if (transport == "mcp")
            {
                using var reader = new StreamReader(pipe, new System.Text.UTF8Encoding(false, true), false, 4096, true);
                await using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false, true), 4096, true) { NewLine = "\n" };
                await new McpStdioServer(scoped, reader, writer, diagnostics, _dispatcher, id).RunAsync(cancellationToken);
            }
            else
            {
                using var session = new CorrelatedRequestSession(_dispatcher, id, async (response, token) =>
                {
                    if (JsonSerializer.SerializeToUtf8Bytes(response).Length > ResponseBytes)
                        response = Error("response_too_large", response["id"]?.GetValue<string>());
                    await BinaryProtocol.WriteAsync(pipe, response, token, ResponseBytes);
                }, NativeTransportError, cancellationToken);
                var multiplex = contract == NativeMultiplexContract;
                if (multiplex) session.EnableMultiplexing();
                while (!session.Token.IsCancellationRequested)
                {
                    // Idle and execution budgets are independent. Admission starts
                    // the operation deadline; time spent waiting for input does not.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    idle.CancelAfter(CorrelatedRequestSession.RequestTimeout);
                    var request = await BinaryProtocol.ReadAsync<JsonElement>(pipe, idle.Token, RequestBytes);
                    if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var requestId) &&
                        requestId.ValueKind == JsonValueKind.String && requestId.GetString()!.Length is >= 1 and <= 64)
                    {
                        var text = requestId.GetString()!;
                        session.RequireAvailableId("s:" + text);
                        if (multiplex && String(request, "op") == "cancel")
                        {
                            RequireObject(request, "id", "op", "targetId");
                            _ = clients.RequireActive(lease);
                            var target = String(request, "targetId");
                            if (target.Length is < 1 or > 64) throw new InvalidDataException("request_invalid");
                            var known = session.Cancel("s:" + target);
                            session.Reply("s:" + text, JsonValue.Create(text), new JsonObject { ["id"] = text, ["ok"] = true,
                                ["result"] = new JsonObject { ["state"] = "cancellation_requested", ["knownRequest"] = known } });
                            continue;
                        }
                        string? streamId = null;
                        if (multiplex)
                        {
                            streamId = String(request, "streamId");
                            if (streamId.Length is < 1 or > 64) throw new InvalidDataException("request_stream_invalid");
                        }
                        session.Submit("s:" + text, JsonValue.Create(text)!, System.Text.Encoding.UTF8.GetByteCount(request.GetRawText()),
                            token => ExecuteNativeAsync(scoped, lease, request, token, multiplex), streamId: streamId);
                    }
                    else session.Reply(null, null, await ExecuteNativeAsync(scoped, lease, request, session.Token));
                }
                await session.DrainAsync();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // After hello, a correlated writer may still be settling. Closing
            // cannot prove its rollback; never inject an out-of-band auth frame.
            if (dataPhase) return;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                await BinaryProtocol.WriteAsync(pipe, Error("client_auth_failed"), deadline.Token, ResponseBytes);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException or AgentOpsException)
        {
            // Deliberately no exception message/request body: auth frames contain secrets.
            await diagnostics.WriteLineAsync("component=resident-session state=closed cause=" + error.GetType().Name);
        }
        catch (Exception error)
        { await diagnostics.WriteLineAsync("component=resident-session state=failed cause=" + error.GetType().Name); }
        finally { pipe.Dispose(); _slots.Release(); _sessions.TryRemove(id, out _); }
    }

    internal async ValueTask<JsonObject> ExecuteNativeAsync(CortexClientApplication scoped, CortexClientLease lease,
        JsonElement request, CancellationToken cancellationToken, bool multiplex = false)
    {
        string? id = null;
        try
        {
            if (multiplex) RequireObject(request, "id", "op", "name", "arguments", "uri", "streamId");
            else RequireObject(request, "id", "op", "name", "arguments", "uri");
            id = String(request, "id");
            if (id.Length is < 1 or > 64) return Error("request_invalid");
            var op = String(request, "op");
            var entry = clients.RequireActive(lease);
            JsonNode? result;
            switch (op)
            {
                case "catalog":
                    Only(request, multiplex, "id", "op");
                    result = new JsonObject { ["operations"] = scoped.ListTools(),
                        ["defaults"] = JsonSerializer.SerializeToNode(entry.Defaults, CortexClientRegistry.Json),
                        ["namespacePolicy"] = "selection",
                        ["resources"] = await scoped.ListResourcesAsync(cancellationToken),
                        ["resourceTemplates"] = await scoped.ListResourceTemplatesAsync(cancellationToken) };
                    break;
                case "call":
                    Only(request, multiplex, "id", "op", "name", "arguments");
                    var args = request.GetProperty("arguments");
                    var called = await scoped.CallToolAsync(String(request, "name"), args, cancellationToken);
                    if (called["isError"]?.GetValue<bool>() == true)
                        return new() { ["id"] = id, ["ok"] = false,
                            ["error"] = (called["structuredContent"]?["error"] ?? called["structuredContent"])?.DeepClone() ??
                                new JsonObject { ["code"] = "operation_failed" } };
                    result = called["structuredContent"]?.DeepClone() ?? called["content"]?.DeepClone();
                    break;
                case "resource":
                    Only(request, multiplex, "id", "op", "uri");
                    result = await scoped.ReadResourceAsync(String(request, "uri"), cancellationToken);
                    if (result is null) return Error("resource_unavailable", id);
                    break;
                case "ping": Only(request, multiplex, "id", "op"); result = new JsonObject { ["state"] = admission is { Accepting: false } ? "stopping" : "ready" }; break;
                default: return Error("operation_unknown", id);
            }
            return new() { ["id"] = id, ["ok"] = true, ["result"] = result };
        }
        catch (UnauthorizedAccessException) { return Error("client_auth_failed", id); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return Error("request_invalid", id); }
    }

    private JsonObject Describe(string contract, string clientId, bool stopping = false) => new()
    {
        ["ok"] = true, ["contract"] = contract, ["product"] = CortexProductIdentity.Product,
        ["version"] = hostVersion, ["session"] = owner.Session, ["ownerSid"] = owner.Sid,
        ["clientId"] = clientId, ["state"] = stopping || admission is { Accepting: false } ? "stopping" : "ready", ["storageKind"] = "sqlite-v2",
        ["maximumRequestBytes"] = RequestBytes, ["maximumResponseBytes"] = ResponseBytes,
        ["maximumConnections"] = ConnectionLimit, ["network"] = false,
        ["multiplexing"] = contract == NativeMultiplexContract ? new JsonObject
        {
            ["streamIdRequired"] = true, ["responseOrder"] = "correlated", ["streamExecutionOrder"] = "fifo",
            ["maximumPendingRequests"] = BoundedRequestDispatcher.MaximumSessionRequests,
            ["maximumActiveCallbacks"] = BoundedRequestDispatcher.MaximumActiveCallbacks, ["requestTimeoutSeconds"] = 300, ["drainTimeoutSeconds"] = 10
        } : null
    };
    private static JsonObject NativeTransportError(DispatchState state, JsonNode? id)
    {
        var result = Error(state switch
        {
            DispatchState.NotDispatched => "request_cancelled_before_dispatch",
            DispatchState.CapacityExceeded => "request_pending_limit",
            _ => "operation_outcome_unknown"
        }, id?.GetValue<string>());
        result["error"]!["outcome"] = state == DispatchState.Unknown ? "Unknown" : "NotDispatched";
        return result;
    }

    internal static JsonObject Error(string code, string? id = null)
    {
        var result = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code } };
        if (id is not null) result["id"] = id;
        return result;
    }
    private static string String(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException("request_invalid");
    private static void Only(JsonElement value, bool multiplex, params string[] names) =>
        RequireObject(value, multiplex ? [.. names, "streamId"] : names);
    private static void RequireObject(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)) ||
            value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new InvalidDataException("request_invalid");
    }
}
