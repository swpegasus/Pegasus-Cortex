using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.Cortex.Product;
using Pegasus.SharedMcp;

/// <summary>
/// Real owned loopback HTTP with the product authenticator/digest contract.
/// Only public approval reads and product dispatch are deterministic fixtures;
/// no Credential Manager, certificate, service or privileged action is used.
/// </summary>
internal static class CortexMcpConnectionHttpTests
{
    private const string Protocol = "2025-11-25";
    private const int MaximumResponseBytes = 8192;
    private static int checks;

    internal static async Task RunAsync()
    {
        checks = 0;
        var secret = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        try
        {
            var request = CortexMcpConnections.Validate(new CortexMcpConnectionRequest(
                CortexMcpConnections.RequestSchema, new string('1', 32),
                Convert.ToHexStringLower(SHA256.HashData(secret)), "Fixture client", "Fixture application", "Declared chat"));
            var grant = new CortexMcpConnectionGrant(request, new string('2', 32), 1, true, true);
            var initial = CortexMcpConnections.Validate(new CortexMcpConnectionStore(
                CortexMcpConnections.StoreSchema, new string('3', 32), 1, [grant]));
            var header = "Bearer " + request.ConnectionId + "." +
                Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var fixture = await Fixture.StartAsync(initial))
            {
                await RefusedCredentialsAsync(fixture, header, initial);
                await SessionsAndChangesAsync(fixture, header, initial);
            }
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        Console.WriteLine($"PASS MCP connection HTTP: {checks} checks; real ephemeral loopback HTTP and product credential verification, in-memory public approvals and deterministic dispatcher; no vault, TLS, service or child process");
    }

    private static async Task RefusedCredentialsAsync(Fixture fixture, string header, CortexMcpConnectionStore initial)
    {
        foreach (var invalid in new string?[]
        {
            null, "", "Bearer invalid", "Basic fixture", header.Insert(7, " "),
            header.Replace(new string('1', 32), new string('0', 32), StringComparison.Ordinal),
            "Bearer " + new string('1', 32) + "." + new string('_', 43),
            header[..^1] + (header[^1] == 'A' ? "E" : "A")
        })
        {
            var response = await fixture.SendAsync(Initialize(), invalid);
            Check(response.Status == HttpStatusCode.Unauthorized && response.Session is null &&
                response.Challenge == "Bearer realm=\"Pegasus Cortex\"", "missing or malformed credential was not challenged");
        }
        await fixture.DuplicateAuthorizationAsync(header);
        Check(fixture.Dispatcher.Opened.IsEmpty && fixture.Dispatcher.Initializations == 0 &&
            fixture.Dispatcher.Requests.IsEmpty && fixture.Server.SessionCount == 0,
            "refused credential reached product/session callbacks");

        fixture.SetStore(null);
        Check((await fixture.SendAsync(Initialize(), header)).Status == HttpStatusCode.Unauthorized,
            "valid-looking bearer bypassed missing approvals");
        fixture.ReadFailure = true;
        Check((await fixture.SendAsync(Initialize(), header)).Status == HttpStatusCode.ServiceUnavailable,
            "unreadable approvals became anonymous access");
        fixture.ReadFailure = false;
        fixture.SetStore(initial);
        Check(fixture.Dispatcher.Opened.IsEmpty && fixture.Server.SessionCount == 0,
            "missing or unavailable registry created a session");
    }

    private static async Task SessionsAndChangesAsync(Fixture fixture, string header, CortexMcpConnectionStore initial)
    {
        var first = await fixture.OpenAsync(header, "First client label");
        var second = await fixture.OpenAsync(header, "Different declared chat");
        Check(first != second && fixture.Server.SessionCount == 2, "new sessions reused transport identity");
        var firstResult = await fixture.CallAsync(first, header, "first-task");
        var firstContext = fixture.Dispatcher.Requests.Last();
        var secondResult = await fixture.CallAsync(second, header, "different-task");
        var secondContext = fixture.Dispatcher.Requests.Last();
        var expected = new McpAuthenticatedIdentity(initial.Authority, initial.Connections[0].Request.ConnectionId,
            initial.Connections[0].CredentialGeneration);
        Check(firstContext.Identity == expected && secondContext.Identity == expected &&
            firstContext.SessionId != secondContext.SessionId && firstContext.StreamId != secondContext.StreamId,
            "display/task/session labels changed persistent authenticated identity");
        Check(firstResult.Status == HttpStatusCode.OK && secondResult.Status == HttpStatusCode.OK &&
            firstContext.TaskId == "first-task" && secondContext.TaskId == "different-task",
            "independent session correlation was lost");

        Check((await fixture.SendAsync(null, null, first, HttpMethod.Delete)).Status == HttpStatusCode.Unauthorized,
            "session ID alone authorized deletion");
        Check((await fixture.SendAsync(null, header, first, HttpMethod.Delete)).Status == HttpStatusCode.NoContent,
            "authenticated owner could not close its session");
        var reconnected = await fixture.OpenAsync(header, "Reopened client");
        await fixture.CallAsync(reconnected, header, "reconnected-task");
        Check(fixture.Dispatcher.Requests.Last().Identity == expected && reconnected != first,
            "reconnect changed persistent identity or reused the deleted transport session");

        var oldAuthority = CortexMcpConnections.Authenticate(initial, header) ?? throw new InvalidOperationException("fixture digest refused");
        Check(CortexMcpConnections.Find(initial, oldAuthority, requireFullControl: true) is not null,
            "initial explicit Full control was unavailable");
        var fullRevoked = initial with { Revision = 2, Connections = [initial.Connections[0] with { Revision = 2, FullControl = false }] };
        fixture.SetStore(fullRevoked);
        var reads = fixture.ApprovalReads;
        await fixture.CallAsync(second, header, "ordinary-after-full-revoke");
        var freshAuthority = CortexMcpConnections.Authenticate(fullRevoked, header) ??
            throw new InvalidOperationException("Full-control-only revoke removed ordinary credentials");
        Check(fixture.ApprovalReads == reads + 1 && fixture.Dispatcher.Requests.Last().Identity == expected,
            "existing session cached approval or lost ordinary identity after Full-control revoke");
        Check(freshAuthority.GrantRevision == 2 && freshAuthority.GrantRevision != oldAuthority.GrantRevision &&
            CortexMcpConnections.Find(fullRevoked, oldAuthority) is null &&
            CortexMcpConnections.Find(fullRevoked, freshAuthority) is not null &&
            CortexMcpConnections.Find(fullRevoked, freshAuthority, requireFullControl: true) is null,
            "Full-control revoke did not invalidate old product authority and preserve a fresh ordinary grant");

        var revoked = fullRevoked with { Revision = 3, Connections = [fullRevoked.Connections[0] with { Revision = 3, Enabled = false }] };
        fixture.SetStore(revoked);
        var calls = fixture.Dispatcher.Requests.Count;
        Check((await fixture.SendAsync(Call("revoked-task"), header, second)).Status == HttpStatusCode.Unauthorized,
            "revoked credential retained access through an existing session");
        Check((await fixture.SendAsync(null, header, second, HttpMethod.Delete)).Status == HttpStatusCode.Unauthorized,
            "revoked credential retained session-control access");
        Check((await fixture.SendAsync(Initialize(), header)).Status == HttpStatusCode.Unauthorized &&
            fixture.Dispatcher.Requests.Count == calls, "revoked credential initialized or dispatched work");

        var rotated = fullRevoked with { Revision = 4, Connections = [fullRevoked.Connections[0] with
            { Revision = 4, CredentialGeneration = new string('4', 32) }] };
        fixture.SetStore(rotated);
        Check((await fixture.SendAsync(Call("old-generation"), header, second)).Status == HttpStatusCode.Forbidden &&
            fixture.Dispatcher.Requests.Count == calls, "old session admitted a changed credential generation");
        var newGeneration = await fixture.OpenAsync(header, "New generation");
        await fixture.CallAsync(newGeneration, header, "new-generation");
        Check(fixture.Dispatcher.Requests.Last().Identity == new McpAuthenticatedIdentity(initial.Authority,
            initial.Connections[0].Request.ConnectionId, rotated.Connections[0].CredentialGeneration),
            "fresh session did not bind the currently authenticated generation");
        var changedDigest = rotated with { Revision = 5, Connections = [rotated.Connections[0] with
            { Revision = 5, Request = rotated.Connections[0].Request with { CredentialSha256 = new string('0', 64) } }] };
        fixture.SetStore(changedDigest);
        Check((await fixture.SendAsync(Call("old-secret"), header, newGeneration)).Status == HttpStatusCode.Unauthorized,
            "old digest remained admitted after credential replacement");
    }

    private static JsonObject Initialize(string label = "Fixture client") => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
        ["params"] = new JsonObject { ["protocolVersion"] = Protocol, ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = label, ["version"] = "fixture" } }
    };

    private static JsonObject Call(string task) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = "fixture.identity", ["arguments"] = new JsonObject(),
            ["_meta"] = new JsonObject { ["threadId"] = task, ["authority"] = "caller-forged-authority",
                ["connectionId"] = "caller-forged-connection", ["pegasus.cortex/streamId"] = 999 } }
    };

    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }

    private sealed record Exchange(HttpStatusCode Status, string? Session, string? Challenge, JsonObject? Body);

    private static string StatusDiagnostic(Exchange response)
    {
        var rpcCode = response.Body?["error"] is JsonObject error && error["code"] is JsonValue value &&
            value.TryGetValue<int>(out var code) ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
        // Fixed shape and numeric status/code only: no header, token, body or server message.
        return $" (http={(int)response.Status}, rpc_error={rpcCode}, session_present={response.Session is not null})";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private CortexMcpConnectionStore? store;
        private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false,
            AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        internal SharedMcpServer Server = null!;
        internal readonly IdentityDispatcher Dispatcher = new();
        internal bool ReadFailure;
        internal int ApprovalReads;

        internal static async Task<Fixture> StartAsync(CortexMcpConnectionStore initial)
        {
            var fixture = new Fixture();
            fixture.SetStore(initial);
            try
            {
                fixture.Server = await SharedMcpServer.StartAsync(new McpServerOptions
                {
                    BindAddress = IPAddress.Loopback, Port = 0, MaximumSessions = 8,
                    MaximumPendingPerSession = 2, MaximumPendingTotal = 4,
                    MaximumRequestBytes = 8192, MaximumResponseBytes = MaximumResponseBytes,
                    RequestTimeout = TimeSpan.FromSeconds(5),
                    Authenticator = new CortexMcpRequestAuthenticator(fixture.Read)
                }, new Dictionary<string, IMcpDispatcher> { ["cortex"] = fixture.Dispatcher });
                Check(fixture.Server.Address.Scheme == "http" && IPAddress.IsLoopback(IPAddress.Parse(fixture.Server.Address.Host)),
                    "fixture did not use an owned ephemeral loopback HTTP listener");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal void SetStore(CortexMcpConnectionStore? value) =>
            Volatile.Write(ref store, value is null ? null : CortexMcpConnections.Validate(value));

        private CortexMcpConnectionStore? Read()
        {
            Interlocked.Increment(ref ApprovalReads);
            if (ReadFailure) throw new IOException("fixture approvals unavailable");
            return Volatile.Read(ref store);
        }

        internal async Task<string> OpenAsync(string header, string label)
        {
            var initialized = await SendAsync(Initialize(label), header);
            Check(initialized.Status == HttpStatusCode.OK && initialized.Session is { Length: 32 } &&
                initialized.Body?["result"] is JsonObject, "valid product digest could not initialize" + StatusDiagnostic(initialized));
            var session = initialized.Session!;
            var ready = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, header, session);
            Check(ready.Status == HttpStatusCode.Accepted, "valid authenticated initialized notification refused" + StatusDiagnostic(ready));
            return session;
        }

        internal async Task<Exchange> CallAsync(string session, string header, string task)
        {
            var response = await SendAsync(Call(task), header, session);
            Check(response.Status == HttpStatusCode.OK && response.Body is { } body && body["id"]?.GetValue<int>() == 7 &&
                body["result"] is JsonObject && !body.ContainsKey("error"),
                "authenticated synthetic dispatch failed or changed request identity" + StatusDiagnostic(response));
            return response;
        }

        internal async Task<Exchange> SendAsync(JsonObject? body, string? header, string? session = null, HttpMethod? method = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(method ?? HttpMethod.Post, new Uri(Server.Address, "/mcp/cortex"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (header is not null) Check(request.Headers.TryAddWithoutValidation("Authorization", header), "fixture header rejected before HTTP");
            if (session is not null)
            {
                request.Headers.Add("Mcp-Session-Id", session);
                request.Headers.Add("MCP-Protocol-Version", Protocol);
            }
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Check(response.Content.Headers.ContentLength is null or <= MaximumResponseBytes, "fixture response exceeded its bound");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                var count = await input.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                Check(output.Length + count <= MaximumResponseBytes, "fixture response exceeded its bound");
                output.Write(buffer, 0, count);
            }
            var bytes = output.ToArray();
            return new(response.StatusCode,
                response.Headers.TryGetValues("Mcp-Session-Id", out var sessions) ? sessions.Single() : null,
                response.Headers.WwwAuthenticate.SingleOrDefault()?.ToString(),
                bytes.Length == 0 ? null : JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("fixture JSON response invalid"));
        }

        internal async Task DuplicateAuthorizationAsync(string header)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, Server.Address.Port, timeout.Token);
            await using var stream = client.GetStream();
            var body = Initialize().ToJsonString();
            var bytes = Encoding.ASCII.GetBytes($"POST /mcp/cortex HTTP/1.1\r\nHost: 127.0.0.1:{Server.Address.Port}\r\nAuthorization: {header}\r\nAuthorization: {header}\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            try { await stream.WriteAsync(bytes, timeout.Token); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            using var response = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                Check(response.Length + count <= MaximumResponseBytes, "duplicate-header response exceeded bound");
                response.Write(buffer, 0, count);
            }
            Check(Encoding.ASCII.GetString(response.ToArray()).StartsWith("HTTP/1.1 401 ", StringComparison.Ordinal),
                "two actual Authorization header fields were not refused");
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            try { if (Server is not null) await Server.DisposeAsync(); }
            finally { await Dispatcher.DisposeAsync(); }
            Check(Server is null || Server.SessionCount == 0 && Server.PendingCount == 0,
                "owned HTTP sessions or work survived listener cleanup");
            Check(Dispatcher.Opened.Count == Dispatcher.Closed.Count && Dispatcher.Disposals == 1,
                "owned session lifecycle or dispatcher cleanup was incomplete");
        }
    }

    private sealed class IdentityDispatcher : IMcpDispatcher, IMcpSessionLifecycle
    {
        internal readonly ConcurrentQueue<McpSessionContext> Opened = new();
        internal readonly ConcurrentQueue<McpSessionContext> Closed = new();
        internal readonly ConcurrentQueue<McpRequestContext> Requests = new();
        internal int Initializations, Disposals;

        public Task<JsonObject> InitializeAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Initializations);
            return Task.FromResult(new JsonObject { ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "deterministic-identity-fixture", ["version"] = "1" } });
        }

        public Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Enqueue(context);
            return Task.FromResult(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                ["result"] = new JsonObject { ["authority"] = context.Identity?.Authority,
                    ["connectionId"] = context.Identity?.Subject, ["generation"] = context.Identity?.Generation } });
        }

        public ValueTask SessionOpenedAsync(McpSessionContext context, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Opened.Enqueue(context); return ValueTask.CompletedTask; }
        public ValueTask SessionClosedAsync(McpSessionContext context, McpSessionCloseReason reason)
        { Closed.Enqueue(context); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
}
