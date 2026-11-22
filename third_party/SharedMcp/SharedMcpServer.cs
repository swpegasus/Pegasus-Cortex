using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pegasus.SharedMcp;

/// <summary>
/// Embeddable legacy Streamable HTTP transport. Each product supplies its own dispatcher;
/// session labels never select an OS identity, credential, or privileged execution route.
/// Dispatchers are owned and disposed by the embedding product, not by this listener.
/// </summary>
public sealed class SharedMcpServer : IAsyncDisposable
{
    private static readonly string[] Protocols = ["2025-11-25", "2025-06-18", "2025-03-26"];
    private readonly McpServerOptions options;
    private readonly Dictionary<string, IMcpDispatcher> products;
    private readonly PrivateNetworkPolicy network;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly object sessionGate = new();
    private readonly SemaphoreSlim total;
    private readonly SemaphoreSlim authenticating;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposalCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? authenticationDrained;
    private int activeAuthentications;
    private WebApplication? app;
    private Task? sweeper;
    private int streamSequence;
    private int disposed;
    private volatile bool tlsQualified;
    public Uri Address { get; private set; } = null!;
    public McpTlsQualificationResult? LastTlsQualification { get; private set; }
    public int SessionCount => sessions.Count;
    public int PendingCount => options.MaximumPendingTotal - total.CurrentCount;

    private SharedMcpServer(McpServerOptions options, IReadOnlyDictionary<string, IMcpDispatcher> products)
    {
        if (options.Port is < 0 or > 65535 || !PrivateNetworkPolicy.IsPrivateAddress(options.BindAddress) ||
            options.BindAddress.IsIPv4MappedToIPv6 || options.MaximumSessions is < 1 or > 1024 ||
            options.MaximumPendingPerSession is < 1 or > 128 || options.MaximumPendingTotal is < 1 or > 1024 ||
            options.MaximumRequestBytes is < 1024 or > 1048576 || options.MaximumResponseBytes is < 1024 or > 8388608 ||
            options.RequestTimeout < TimeSpan.FromMilliseconds(100) || options.RequestTimeout > TimeSpan.FromMinutes(30) ||
            options.SessionIdleTimeout < TimeSpan.FromSeconds(1) || options.SessionIdleTimeout > TimeSpan.FromDays(1))
            throw new ArgumentException("Invalid listener address or resource bounds.");
        if (products.Count is < 1 or > 16 || products.Any(p => p.Value is null || p.Key.Length is < 1 or > 40 ||
            p.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Supply one to sixteen explicit product dispatchers with simple product keys.");
        // Freeze caller-owned collections so later mutation cannot widen policy or reroute a product.
        this.options = options;
        tlsQualified = options.ServerCertificate is null;
        if (options.ServerCertificate is { } certificate)
            McpTlsQualification.RequireServerCertificate(certificate, options.ServerName ?? options.BindAddress.ToString());
        else if (options.ServerName is not null || options.QualificationAuthority is not null)
            throw new ArgumentException("ServerName and QualificationAuthority require an explicit TLS server certificate.");
        this.products = products.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        network = new(options.AllowedPeerCidrs.ToArray());
        total = new(options.MaximumPendingTotal, options.MaximumPendingTotal);
        authenticating = new(options.MaximumPendingTotal, options.MaximumPendingTotal);
    }

    public static async Task<SharedMcpServer> StartAsync(McpServerOptions options,
        IReadOnlyDictionary<string, IMcpDispatcher> products, CancellationToken cancellationToken = default)
    {
        var server = new SharedMcpServer(options, products);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // Embedding application settings/environment must never add Kestrel listeners or widen binding.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders(); // Generic HTTP logging must not capture tool bodies or headers.
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = options.MaximumRequestBytes;
            k.Limits.MaxConcurrentConnections = 128;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Listen(options.BindAddress, options.Port, listen =>
            {
                if (options.ServerCertificate is { } certificate)
                {
                    // Offline context prevents startup from retrieving intermediate certificates via AIA.
                    var certificateContext = SslStreamCertificateContext.Create(certificate, null, offline: true);
                    listen.UseHttps(new TlsHandshakeCallbackOptions
                    {
                        HandshakeTimeout = TimeSpan.FromSeconds(5),
                        OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = certificateContext,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false,
                            AllowRenegotiation = false
                        })
                    });
                }
            });
        });
        server.app = builder.Build();
        server.app.Run(server.HandleAsync);
        try
        {
            await server.app.StartAsync(cancellationToken);
            var address = server.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server.Address = options.ServerCertificate is not null && options.ServerName is { } name
                ? new UriBuilder(address) { Host = name }.Uri : new(address);
            server.sweeper = server.SweepAsync();
            if (options.QualificationAuthority is { } authority)
            {
                var qualification = await server.QualifyLoopbackAsync(authority, cancellationToken);
                if (!qualification.Success) throw new McpTlsQualificationException(qualification.Code);
            }
            return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    private async Task HandleAsync(HttpContext http)
    {
        var path = http.Request.Path.Value ?? "";
        if (!network.Allows(http.Connection.RemoteIpAddress) || !ValidHost(http.Request.Host))
        { http.Response.StatusCode = 403; return; }
        // Browsers cannot turn an allowed network into cross-origin access. No CORS is enabled.
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.GetLeftPart(UriPartial.Authority) != Address.GetLeftPart(UriPartial.Authority)))
        { http.Response.StatusCode = 403; return; }
        if (!path.StartsWith("/mcp/", StringComparison.Ordinal) || !products.TryGetValue(path[5..], out var dispatcher))
        { http.Response.StatusCode = 404; return; }
        if (!tlsQualified) { http.Response.StatusCode = 503; return; }
        var product = path[5..];
        // Authentication is product policy, after transport guards and before any session lookup.
        // Neither a session ID nor a task/client label can manufacture or replace this identity.
        var admission = await AuthenticateAsync(http, product);
        if (admission is null) return;
        if (http.Request.Method == "GET")
        { http.Response.StatusCode = 405; http.Response.Headers.Allow = "POST, DELETE"; return; }
        if (http.Request.Method is not ("POST" or "DELETE"))
        { http.Response.StatusCode = 405; http.Response.Headers.Allow = "POST, DELETE"; return; }
        Session? session = null;
        var sessionId = http.Request.Headers["Mcp-Session-Id"].ToString();
        if (sessionId.Length > 0 && (!sessions.TryGetValue(sessionId, out session) || session.Product != product || session.Closed))
        { http.Response.StatusCode = 404; return; }
        if (session is not null && session.Context.Identity != admission.Identity)
        { http.Response.StatusCode = 403; return; }
        var protocol = http.Request.Headers["MCP-Protocol-Version"].ToString();
        if (protocol.Length > 0 && (!Protocols.Contains(protocol) || session is not null && session.Protocol != protocol))
        { await Write(http, Error(null, -32600, "Unsupported or mismatched MCP protocol version."), 400); return; }
        if (http.Request.Method == "DELETE")
        {
            if (session is null) { http.Response.StatusCode = 400; return; }
            Close(session, McpSessionCloseReason.Deleted); http.Response.StatusCode = 204; return;
        }
        if (!(http.Request.ContentType ?? "").Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        { http.Response.StatusCode = 415; return; }
        IList<Microsoft.Net.Http.Headers.MediaTypeHeaderValue>? accepts;
        try { accepts = http.Request.GetTypedHeaders().Accept; }
        catch (FormatException) { http.Response.StatusCode = 406; return; }
        if (accepts is null || !accepts.Any(a => a.MediaType.Value == "application/json") ||
            !accepts.Any(a => a.MediaType.Value == "text/event-stream"))
        { http.Response.StatusCode = 406; return; }
        JsonObject request;
        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted);
                if (read == 0) break;
                if (body.Length + read > options.MaximumRequestBytes) { http.Response.StatusCode = 413; return; }
                body.Write(buffer, 0, read);
            }
            using var doc = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
            ValidateUniqueFields(doc.RootElement);
            request = JsonNode.Parse(doc.RootElement.GetRawText()) as JsonObject ?? throw new JsonException();
            if (request["jsonrpc"]?.GetValue<string>() != "2.0" || request["method"] is not JsonValue methodValue ||
                !methodValue.TryGetValue<string>(out _) || request.ContainsKey("result") || request.ContainsKey("error") ||
                request["params"] is not (null or JsonObject)) throw new JsonException();
            if (request.ContainsKey("id") && !ValidId(request["id"])) throw new JsonException();
        }
        catch (BadHttpRequestException) { http.Response.StatusCode = 413; return; }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { await Write(http, Error(null, -32600, "Invalid bounded JSON-RPC request."), 400); return; }
        var id = request["id"];
        var method = request["method"]!.GetValue<string>();
        if (method == "initialize")
        {
            if (session is not null || id is null) { await Write(http, Error(id, -32600, "Initialize without an existing session."), 400); return; }
            if (request["params"]?["protocolVersion"] is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var requested) ||
                request["params"]?["capabilities"] is not JsonObject || request["params"]?["clientInfo"] is not JsonObject)
            { await Write(http, Error(id, -32602, "Initialization requires protocolVersion, capabilities and clientInfo."), 400); return; }
            lock (sessionGate)
            {
                if (disposed == 0 && sessions.Count < options.MaximumSessions && total.Wait(0))
                {
                    session = new(Guid.NewGuid().ToString("N"), product, Interlocked.Increment(ref streamSequence),
                        Protocols.Contains(requested) ? requested : Protocols[0], admission.Identity, lifetime.Token);
                    sessions[session.Id] = session;
                    // Owned by this session and awaited by DisposeAsync, including noncooperative cleanup.
                    session.Cleanup = CloseWhenDrainedAsync(session, dispatcher);
                }
            }
            if (session is null)
            { await Write(http, Error(id, -32000, "Session or pending capacity reached."), 503); return; }
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
                using var startupTimer = new Timer(_ => CancelSafely(startup), null, options.RequestTimeout, Timeout.InfiniteTimeSpan);
                if (dispatcher is IMcpSessionLifecycle lifecycle)
                    await lifecycle.SessionOpenedAsync(session.Context, startup.Token);
                startup.Token.ThrowIfCancellationRequested();
                var result = (JsonObject)(await dispatcher.InitializeAsync(startup.Token)).DeepClone();
                startup.Token.ThrowIfCancellationRequested();
                result["protocolVersion"] = session.Protocol;
                // This JSON-response profile has no server-initiated requests or notification channel.
                if (result["capabilities"] is JsonObject capabilities)
                {
                    capabilities.Remove("logging"); capabilities.Remove("experimental");
                    foreach (var capability in capabilities.ToArray())
                        if (capability.Value is JsonObject obj) { obj.Remove("listChanged"); obj.Remove("subscribe"); }
                }
                lock (session.Gate)
                {
                    if (session.Closed) throw new OperationCanceledException();
                    session.Initialized = true;
                }
                http.Response.Headers["Mcp-Session-Id"] = session.Id;
                await Write(http, Result(id, result));
            }
            catch (Exception) { Close(session, McpSessionCloseReason.InitializationFailed); await Write(http, Error(id, -32001, "Product backend unavailable; no operation was replayed."), 503); }
            finally { total.Release(); ReleaseCallback(session); }
            return;
        }
        if (session is null) { await Write(http, Error(id, -32600, "Mcp-Session-Id required."), 400); return; }
        if (id is null)
        {
            lock (session.Gate)
            {
                if (session.Closed) { http.Response.StatusCode = 404; return; }
                session.LastUse = DateTime.UtcNow;
                if (method == "notifications/initialized")
                {
                    if (!session.Initialized) { http.Response.StatusCode = 400; return; }
                    session.Ready = true;
                }
            }
            if (method == "notifications/cancelled" && request["params"]?["requestId"] is { } cancelId && ValidId(cancelId))
                session.Cancel(IdKey(cancelId));
            http.Response.StatusCode = 202; return;
        }
        if (!session.Ready) { await Write(http, Error(id, -32000, "Session initialization is incomplete."), 400); return; }
        if (method is "resources/subscribe" or "resources/unsubscribe")
        { await Write(http, Error(id, -32601, "Subscriptions are unavailable in this JSON-response transport profile.")); return; }
        var key = IdKey(id);
        Pending? pending = null;
        lock (session.Gate)
        {
            if (!session.Closed && session.Pending.Count < options.MaximumPendingPerSession &&
                !session.Pending.ContainsKey(key) && total.Wait(0))
            {
                var context = new McpRequestContext(product, session.Id, session.StreamId, TaskLabel(request), id.ToJsonString())
                    { Identity = session.Context.Identity };
                pending = new(context, CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token));
                pending.StartTimeout(options.RequestTimeout);
                session.Pending[key] = pending;
                session.ActiveCallbacks++;
                session.LastUse = DateTime.UtcNow;
            }
        }
        if (pending is null) { await Write(http, Error(id, -32000, "Duplicate ID, closed session or pending capacity reached."), 429); return; }
        Trace("admitted", pending.Context);
        var work = DispatchAsync(dispatcher, session, key, request, pending);
        JsonObject response;
        try { response = await work.WaitAsync(pending.Token); }
        catch (OperationCanceledException)
        { response = Error(id, -32800, "Cancelled, expired or session closed; dispatched outcome may be unknown. Inspect original work before retrying."); }
        // A dropped HTTP connection does not cancel dispatch. Explicit cancellation, timeout or DELETE does.
        try { await Write(http, response); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }

    private async Task<JsonObject> DispatchAsync(IMcpDispatcher dispatcher, Session session, string key, JsonObject request, Pending pending)
    {
        try
        {
            var response = await dispatcher.DispatchAsync(pending.Context, request, pending.Cancellation.Token);
            if (response["jsonrpc"]?.GetValue<string>() != "2.0" || !ValidId(response["id"]) || IdKey(response["id"]!) != key ||
                response.ContainsKey("result") == response.ContainsKey("error"))
                return Error(request["id"], -32603, "Product dispatcher returned an invalid response mapping.");
            return response;
        }
        catch (OperationCanceledException) { return Error(request["id"], -32800, "Cancellation requested; dispatched outcome may be unknown."); }
        catch (Exception) { return Error(request["id"], -32001, "Product backend failed; dispatched outcome may be unknown. No automatic replay."); }
        finally
        {
            lock (session.Gate) { session.Pending.Remove(key); }
            // Capacity is retained until even a noncooperative dispatcher actually finishes.
            total.Release(); Trace("settled", pending.Context);
            pending.Dispose();
            ReleaseCallback(session);
        }
    }

    private async Task<McpAuthenticationResult?> AuthenticateAsync(HttpContext http, string product)
    {
        lock (sessionGate)
        {
            if (disposed != 0) { http.Response.StatusCode = 503; return null; }
            if (options.Authenticator is null) return McpAuthenticationResult.Anonymous;
            if (!authenticating.Wait(0)) { http.Response.StatusCode = 503; return null; }
            if (activeAuthentications++ == 0)
                authenticationDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, http.RequestAborted);
            deadline.CancelAfter(options.RequestTimeout);
            var input = new McpAuthenticationRequest(product, http.Request.Method, new Uri(Address, "mcp/" + product),
                http.Connection.RemoteIpAddress!, http.Request.Headers.Select(h =>
                    new KeyValuePair<string, string[]>(h.Key, h.Value.Select(v => v ?? "").ToArray())));
            var result = await options.Authenticator!.AuthenticateAsync(input, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (result is null) { http.Response.StatusCode = 503; return null; }
            // Standard Authorization cannot silently downgrade even if a verifier mistakenly says Anonymous.
            // A product accepting custom credential headers must apply the same rule in its verifier.
            if (result.RejectionStatus is not null || result.Identity is null && http.Request.Headers.ContainsKey("Authorization"))
            {
                http.Response.StatusCode = result.RejectionStatus ?? 401;
                if (result.Challenge is { } challenge) http.Response.Headers.WWWAuthenticate = challenge;
                return null;
            }
            return result;
        }
        catch (Exception) { http.Response.StatusCode = 503; return null; }
        finally
        {
            authenticating.Release();
            lock (sessionGate) if (--activeAuthentications == 0) authenticationDrained!.TrySetResult();
        }
    }

    private bool ValidHost(HostString host)
    {
        if ((host.Port ?? (Address.Scheme == "https" ? 443 : 80)) != Address.Port) return false;
        var name = host.Host.Trim('[', ']');
        if (options.ServerCertificate is not null)
            return name.Equals(options.ServerName ?? options.BindAddress.ToString(), StringComparison.OrdinalIgnoreCase);
        return IPAddress.TryParse(name, out var address) ? address.Equals(options.BindAddress) :
            name.Equals("localhost", StringComparison.OrdinalIgnoreCase) && IPAddress.IsLoopback(options.BindAddress);
    }

    /// <summary>Qualifies this TLS listener without importing trust or exporting a private key.</summary>
    public async Task<McpTlsQualificationResult> QualifyLoopbackAsync(X509Certificate2 selectedCa, CancellationToken cancellationToken = default)
    {
        if (options.ServerCertificate is null) throw new InvalidOperationException("The listener has no configured TLS certificate.");
        tlsQualified = false;
        McpTlsQualificationResult result;
        if (IPAddress.IsLoopback(options.BindAddress))
            result = await McpTlsQualification.ProbeAsync(new(options.BindAddress, Address.Port), options.ServerName ?? options.BindAddress.ToString(),
                selectedCa, options.ServerCertificate, cancellationToken);
        else
        {
            // Explicit LAN/VPN opt-in still qualifies the same selected certificate on loopback.
            // The configured listener remains dispatch-gated; the temporary listener never admits product calls.
            await using var loopback = await StartAsync(new McpServerOptions
            { Port = 0, ServerCertificate = options.ServerCertificate, ServerName = options.ServerName ?? options.BindAddress.ToString() }, products, cancellationToken);
            result = await McpTlsQualification.ProbeAsync(new(IPAddress.Loopback, loopback.Address.Port),
                options.ServerName ?? options.BindAddress.ToString(), selectedCa, options.ServerCertificate, cancellationToken);
        }
        LastTlsQualification = result;
        tlsQualified = result.Success;
        return result;
    }

    private static string? TaskLabel(JsonObject request)
    {
        // Codex supplies threadId on tool calls. Other MCP clients may leave correlation unknown.
        var label = request["params"] is JsonObject p && p["_meta"] is JsonObject metadata ? metadata["threadId"] : null;
        return label is JsonValue v && v.TryGetValue<string>(out var text) && text.Length is > 0 and <= 128 &&
            !text.Any(char.IsControl) ? text : null;
    }

    private void Trace(string action, McpRequestContext context)
    { try { options.Trace?.Invoke(new(action, context)); } catch { /* Diagnostics cannot change product outcomes. */ } }

    private static void CancelSafely(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Product callbacks cannot break cancellation of sibling work. */ }
    }

    private void Close(Session session, McpSessionCloseReason reason)
    {
        Pending[] pending;
        lock (session.Gate)
        {
            if (session.Closed) return;
            session.Closed = true;
            session.ActiveCallbacks++; // The cancellation pass must also finish before close cleanup.
            pending = session.Pending.Values.ToArray();
            session.CloseRequested.TrySetResult(reason);
        }
        try
        {
            CancelSafely(session.Cancellation);
            foreach (var item in pending) item.Cancel();
        }
        finally { ReleaseCallback(session); }
    }

    private static void ReleaseCallback(Session session)
    {
        lock (session.Gate)
            if (--session.ActiveCallbacks == 0 && session.Closed) session.Drained.TrySetResult();
    }

    private async Task CloseWhenDrainedAsync(Session session, IMcpDispatcher dispatcher)
    {
        var reason = await session.CloseRequested.Task;
        await session.Drained.Task;
        try
        {
            if (dispatcher is IMcpSessionLifecycle lifecycle)
                await lifecycle.SessionClosedAsync(session.Context, reason);
        }
        catch (Exception)
        {
            // Cleanup failure is observable but must not close another product or leak a session slot.
            Trace("session-close-failed", new(session.Product, session.Id, session.StreamId, null, "null")
                { Identity = session.Context.Identity });
        }
        finally
        {
            session.Cancellation.Dispose();
            sessions.TryRemove(session.Id, out _);
        }
    }

    private async Task SweepAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                foreach (var session in sessions.Values)
                    if (DateTime.UtcNow - session.LastUse > options.SessionIdleTimeout) Close(session, McpSessionCloseReason.IdleExpired);
        }
        catch (OperationCanceledException) { }
    }

    private async Task Write(HttpContext http, JsonObject response, int status = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        if (bytes.Length > options.MaximumResponseBytes)
        { bytes = JsonSerializer.SerializeToUtf8Bytes(Error(response["id"], -32603, "Product response exceeds configured byte bound.")); status = 502; }
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.Body.WriteAsync(bytes, http.RequestAborted);
    }
    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
    private static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    private static bool ValidId(JsonNode? id) => id is JsonValue v &&
        (v.TryGetValue<string>(out var s) && s.Length is > 0 and <= 128 || v.TryGetValue<long>(out _));
    private static string IdKey(JsonNode id) => id is JsonValue v && v.TryGetValue<string>(out var s)
        ? "s:" + s : "n:" + id.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void ValidateUniqueFields(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject()) { if (!names.Add(p.Name)) throw new JsonException(); ValidateUniqueFields(p.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) ValidateUniqueFields(item);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { await disposalCompleted.Task; return; }
        try
        {
            // Allocation and verifier admission use the same gate; capture every owned callback before teardown.
            Session[] closing;
            Task authentication;
            lock (sessionGate)
            {
                closing = sessions.Values.ToArray();
                authentication = authenticationDrained?.Task ?? Task.CompletedTask;
            }
            CancelSafely(lifetime);
            foreach (var session in closing) Close(session, McpSessionCloseReason.ServerShutdown);
            if (sweeper is not null) await sweeper;
            Exception? transportFailure = null;
            if (app is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await app.StopAsync(deadline.Token); }
                catch (OperationCanceledException) { }
                catch (Exception error) { transportFailure = error; }
                try { await app.DisposeAsync(); }
                catch (Exception error) { transportFailure ??= error; }
            }
            // Cancellation cannot prove completion. Keep borrowed dispatchers/resources alive until actual settlement.
            await authentication;
            await Task.WhenAll(closing.Select(s => s.Cleanup));
            if (transportFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(transportFailure).Throw();
            disposalCompleted.TrySetResult();
        }
        catch (Exception error) { disposalCompleted.TrySetException(error); throw; }
    }

    private sealed class Session(string id, string product, int streamId, string protocol,
        McpAuthenticatedIdentity? identity, CancellationToken cancellationToken)
    {
        public string Id { get; } = id;
        public string Product { get; } = product;
        public int StreamId { get; } = streamId;
        public string Protocol { get; } = protocol;
        public McpSessionContext Context { get; } = new(product, id, streamId, protocol, identity);
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public TaskCompletionSource<McpSessionCloseReason> CloseRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Cleanup = Task.CompletedTask;
        public int ActiveCallbacks = 1; // The open/initialize path owns the first reservation.
        public object Gate { get; } = new();
        public volatile bool Closed;
        public bool Initialized;
        public volatile bool Ready;
        public DateTime LastUse = DateTime.UtcNow;
        public Dictionary<string, Pending> Pending { get; } = new(StringComparer.Ordinal);
        public void Cancel(string id)
        {
            Pending? pending;
            lock (Gate) Pending.TryGetValue(id, out pending);
            pending?.Cancel(); // Product cancellation callbacks never execute under the session lock.
        }
    }
    private sealed record Pending(McpRequestContext Context, CancellationTokenSource Cancellation)
    {
        public CancellationToken Token { get; } = Cancellation.Token;
        private Timer? timeout;
        public void StartTimeout(TimeSpan duration) => timeout = new Timer(_ => Cancel(), null, duration, Timeout.InfiniteTimeSpan);
        public void Cancel() => CancelSafely(Cancellation);
        public void Dispose() { timeout?.Dispose(); Cancellation.Dispose(); }
    }
}
