using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.SharedMcp;

// Isolated loopback HTTP with deterministic injected verifiers/dispatchers. No real credentials,
// persistence, approval policy, product processes, keys, trust changes or installed clients.
internal static class IdentityChecks
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var count = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + label);
            count++; Console.WriteLine("PASS: " + label);
        }
        async Task Until(Func<bool> settled)
        {
            while (!settled()) await Task.Delay(10, token);
        }
        var verifier = new FixtureAuthenticator();
        var product = new FixtureDispatcher();
        await using (var server = await SharedMcpServer.StartAsync(new() { Port = 0, Authenticator = verifier },
            new Dictionary<string, IMcpDispatcher> { ["fixture"] = product }, token))
        {
            using var http = new HttpClient { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(8) };
            var invalid = await Send(http, null, "Bearer invalid", Initialize(), token: token);
            Check(invalid.Status == HttpStatusCode.Unauthorized && invalid.Challenge == "Bearer realm=\"fixture\"" &&
                server.SessionCount == 0 && product.Opened.IsEmpty, "invalid credentials reject before allocation and expose only the product challenge");
            var calls = verifier.Calls;
            Check((await Send(http, null, null, Initialize(), origin: "https://invalid.example", token: token)).Status == HttpStatusCode.Forbidden &&
                verifier.Calls == calls, "Origin guard precedes authentication");
            using (var badHost = new HttpRequestMessage(HttpMethod.Post, "mcp/fixture"))
            {
                badHost.Headers.Host = "invalid.example";
                using var denied = await http.SendAsync(badHost, token);
                Check(denied.StatusCode == HttpStatusCode.Forbidden && verifier.Calls == calls, "Host guard precedes authentication");
            }
            var anonymous = await Connect(http, null, token);
            var a = await Connect(http, "Bearer fixture-a", token);
            var reconnect = await Connect(http, "Bearer fixture-a", token);
            var b = await Connect(http, "Bearer fixture-b", token);
            Check(a != reconnect && product.Opened.Where(c => c.SessionId == a || c.SessionId == reconnect)
                .All(c => c.Identity == FixtureAuthenticator.A), "reconnect has a fresh transport session and the same verified persistent identity");
            var before = verifier.Calls;
            var responses = await Task.WhenAll(Send(http, a, "Bearer fixture-a", Call(1, "chat-a"), token: token),
                Send(http, b, "Bearer fixture-b", Call(1, "chat-b"), token: token));
            Check(responses.All(r => r.Status == HttpStatusCode.OK && r.Body?["id"]?.GetValue<int>() == 1) &&
                product.Contexts.Any(c => c.SessionId == a && c.Identity == FixtureAuthenticator.A && c.TaskId == "chat-a") &&
                product.Contexts.Any(c => c.SessionId == b && c.Identity == FixtureAuthenticator.B && c.TaskId == "chat-b"),
                "same request ID across sessions preserves explicit verified identity and untrusted task correlation");
            Check(verifier.Calls == before + 2, "authenticator runs again for every request on an existing session");
            Check((await Send(http, a, "Bearer fixture-b", Call(2), token: token)).Status == HttpStatusCode.Forbidden,
                "a different principal cannot reuse a session");
            Check((await Send(http, a, "Bearer fixture-a-new", Call(2), token: token)).Status == HttpStatusCode.Forbidden,
                "a different credential generation cannot reuse a session");
            Check((await Send(http, anonymous, "Bearer fixture-a", Call(2), token: token)).Status == HttpStatusCode.Forbidden &&
                (await Send(http, a, null, Call(2), token: token)).Status == HttpStatusCode.Forbidden,
                "anonymous/authenticated transitions require a new session in both directions");
            await Send(http, anonymous, null, Call(3, "claimed-subject"), token: token);
            Check(product.Contexts.Last().Identity is null && product.Contexts.Last().TaskId == "claimed-subject",
                "client metadata and task labels cannot manufacture an authenticated identity");
            Check((await Send(http, null, "Bearer mistakenly-anonymous", Initialize(), token: token)).Status == HttpStatusCode.Unauthorized,
                "presented Authorization cannot silently downgrade to Anonymous");
            Check((await Send(http, null, "Bearer verifier-failure", Initialize(), token: token)).Status == HttpStatusCode.ServiceUnavailable,
                "verifier failure stays closed without exception or credential output");
            verifier.Revoked = true;
            Check((await Send(http, a, "Bearer fixture-a", Call(4), token: token)).Status == HttpStatusCode.Unauthorized &&
                (await Send(http, a, "Bearer fixture-a", null, "DELETE", token: token)).Status == HttpStatusCode.Unauthorized,
                "active-generation revocation rejects calls and DELETE on existing sessions");
            verifier.Revoked = false;
            Check((await Send(http, a, "Bearer fixture-a", null, "GET", token: token)).Status == HttpStatusCode.MethodNotAllowed,
                "authenticated GET retains the JSON transport's 405 profile");
            Check((await Send(http, a, "Bearer fixture-a", null, "DELETE", token: token)).Status == HttpStatusCode.NoContent,
                "authenticated DELETE initiates only its own session teardown");
            await Until(() => product.Closed.Any(c => c.Context.SessionId == a));
            Check(product.Closed.Count(c => c.Context.SessionId == a) == 1 &&
                (await Send(http, a, "Bearer fixture-a", null, "DELETE", token: token)).Status == HttpStatusCode.NotFound,
                "closed session cleanup occurs exactly once");
            Check((await Send(http, b, "Bearer fixture-b", Call(5), token: token)).Status == HttpStatusCode.OK,
                "one identity's teardown leaves another identity's session usable");
        }
        Check(product.Closed.Count == product.Opened.Count && !product.Disposed,
            "listener disposal drains every session but preserves product dispatcher ownership");

        var slowVerifier = new BlockingAuthenticator();
        var authenticationServer = await SharedMcpServer.StartAsync(new()
            { Port = 0, MaximumPendingTotal = 1, Authenticator = slowVerifier, RequestTimeout = TimeSpan.FromMilliseconds(100) },
            new Dictionary<string, IMcpDispatcher> { ["fixture"] = new StatelessDispatcher() }, token);
        using (var http = new HttpClient { BaseAddress = authenticationServer.Address, Timeout = TimeSpan.FromSeconds(8) })
        try
        {
            var request = Send(http, null, null, Initialize(), token: token);
            await slowVerifier.Entered.Task.WaitAsync(token);
            await slowVerifier.Cancelled.Task.WaitAsync(token);
            Check((await Send(http, null, null, Initialize(), token: token)).Status == HttpStatusCode.ServiceUnavailable &&
                slowVerifier.Calls == 1 && authenticationServer.SessionCount == 0,
                "noncooperative verification retains its bounded reservation after timeout without allocating a session");
            slowVerifier.Release.TrySetResult();
            Check((await request).Status == HttpStatusCode.ServiceUnavailable && authenticationServer.SessionCount == 0,
                "late verifier success after its deadline cannot admit a session");
        }
        finally { slowVerifier.Release.TrySetResult(); await authenticationServer.DisposeAsync(); }

        // Close while a dispatcher ignores cancellation, then while its cleanup is still running.
        var blocked = new FixtureDispatcher { BlockDispatch = true, BlockClose = true };
        var draining = await SharedMcpServer.StartAsync(new() { Port = 0, MaximumSessions = 1 },
            new Dictionary<string, IMcpDispatcher> { ["fixture"] = blocked }, token);
        using (var http = new HttpClient { BaseAddress = draining.Address, Timeout = TimeSpan.FromSeconds(8) })
        try
        {
            var session = await Connect(http, null, token);
            var request = Send(http, session, null, Call(7), token: token);
            await blocked.DispatchEntered.Task.WaitAsync(token);
            await Send(http, session, null, null, "DELETE", token: token);
            Check((await request).Body?["error"]?["code"]?.GetValue<int>() == -32800 && blocked.Closed.IsEmpty &&
                draining.SessionCount == 1 && draining.PendingCount == 1,
                "close requests cancellation but retains pending/session capacity until dispatch actually settles");
            Check((await Send(http, null, null, Initialize(), token: token)).Status == HttpStatusCode.ServiceUnavailable,
                "closing sessions cannot be bypassed by reconnecting around the session cap");
            blocked.DispatchRelease.TrySetResult();
            await blocked.CloseEntered.Task.WaitAsync(token);
            Check(blocked.ActiveDispatches == 0 && draining.PendingCount == 0 && draining.SessionCount == 1 &&
                (await Send(http, null, null, Initialize(), token: token)).Status == HttpStatusCode.ServiceUnavailable,
                "close callback starts after dispatch settlement and retains its session slot through cleanup");
            var disposing = draining.DisposeAsync().AsTask();
            Check(!disposing.IsCompleted && !blocked.Disposed, "listener disposal waits for owned close cleanup without disposing its dispatcher");
            blocked.CloseRelease.TrySetResult();
            await disposing.WaitAsync(token);
            Check(blocked.Closed.Count == 1 && draining.SessionCount == 0 && !blocked.Disposed,
                "concurrent DELETE/shutdown pair one final close and release capacity after cleanup");
        }
        finally
        {
            blocked.DispatchRelease.TrySetResult(); blocked.CloseRelease.TrySetResult();
            await draining.DisposeAsync();
        }

        var opening = new FixtureDispatcher { BlockOpen = true };
        var openingServer = await SharedMcpServer.StartAsync(new() { Port = 0, MaximumSessions = 1 },
            new Dictionary<string, IMcpDispatcher> { ["fixture"] = opening }, token);
        using (var http = new HttpClient { BaseAddress = openingServer.Address, Timeout = TimeSpan.FromSeconds(8) })
        try
        {
            var initialize = Send(http, null, null, Initialize(), token: token);
            await opening.OpenEntered.Task.WaitAsync(token);
            var session = opening.Opened.Single().SessionId;
            Check((await Send(http, session, null, new() { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, token: token)).Status == HttpStatusCode.BadRequest &&
                (await Send(http, session, null, Call(10), token: token)).Status == HttpStatusCode.BadRequest && opening.Contexts.IsEmpty,
                "an early initialized notification cannot bypass an unfinished session open");
            await Send(http, session, null, null, "DELETE", token: token);
            Check(opening.Closed.IsEmpty && openingServer.SessionCount == 1 && opening.InitializeCalls == 0,
                "teardown during a noncooperative open waits without freeing its session slot");
            opening.OpenRelease.TrySetResult();
            Check((await initialize).Status == HttpStatusCode.ServiceUnavailable, "cancelled opening cannot initialize or publish a usable session");
            await Until(() => opening.Closed.Count == 1);
            Check(opening.ActiveOpens == 0 && opening.Closed.Single().Reason == McpSessionCloseReason.Deleted,
                "close follows actual open settlement and preserves the first close reason");
        }
        finally { opening.OpenRelease.TrySetResult(); await openingServer.DisposeAsync(); }

        var failing = new FixtureDispatcher { FailOpen = true };
        await using (var server = await SharedMcpServer.StartAsync(new() { Port = 0 },
            new Dictionary<string, IMcpDispatcher> { ["fixture"] = failing }, token))
        {
            using var http = new HttpClient { BaseAddress = server.Address };
            Check((await Send(http, null, null, Initialize(), token: token)).Status == HttpStatusCode.ServiceUnavailable,
                "failed session open does not initialize a backend or return a usable session");
            await Until(() => failing.Closed.Count == 1);
            Check(failing.InitializeCalls == 0 && failing.Closed.Single().Reason == McpSessionCloseReason.InitializationFailed,
                "failed/partially opened session receives exactly one close after open unwinds");
        }
        var legacy = new StatelessDispatcher();
        await using (var server = await SharedMcpServer.StartAsync(new() { Port = 0 },
            new Dictionary<string, IMcpDispatcher> { ["fixture"] = legacy }, token))
        {
            using var http = new HttpClient { BaseAddress = server.Address };
            var session = await Connect(http, "Bearer ignored-without-authenticator", token);
            Check((await Send(http, session, null, Call(9), token: token)).Status == HttpStatusCode.OK && legacy.Identity is null,
                "old stateless dispatcher and explicit no-auth mode retain their existing behavior");
        }
        Console.WriteLine($"{count} focused identity/lifecycle checks passed; tier=isolated real loopback HTTP with deterministic verifier/dispatcher fixtures; installed clients/persistence/Fullcontrol not exercised.");
        return 0;
    }

    private static JsonObject Initialize() => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject
        { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "untrusted fixture label", ["version"] = "0" } }
    };
    private static JsonObject Call(int id, string task = "fixture-chat") => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/list", ["params"] = new JsonObject
        { ["_meta"] = new JsonObject { ["threadId"] = task, ["identity"] = "forged", ["generation"] = "forged" } }
    };
    private static async Task<string> Connect(HttpClient http, string? credential, CancellationToken token)
    {
        var response = await Send(http, null, credential, Initialize(), token: token);
        if (response.Status != HttpStatusCode.OK || response.Session is null) throw new InvalidOperationException("Fixture initialize failed.");
        var ready = await Send(http, response.Session, credential, new() { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, token: token);
        if (ready.Status != HttpStatusCode.Accepted) throw new InvalidOperationException("Fixture initialized notification failed.");
        return response.Session;
    }
    private static async Task<(HttpStatusCode Status, JsonObject? Body, string? Session, string? Challenge)> Send(
        HttpClient http, string? session, string? credential, JsonObject? body, string method = "POST", string? origin = null,
        CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(new(method), "mcp/fixture");
        request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/event-stream");
        if (session is not null) { request.Headers.Add("Mcp-Session-Id", session); request.Headers.Add("MCP-Protocol-Version", "2025-06-18"); }
        if (credential is not null) request.Headers.TryAddWithoutValidation("Authorization", credential);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject,
            response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null,
            response.Headers.TryGetValues("WWW-Authenticate", out var challenges) ? string.Join(", ", challenges) : null);
    }

    private sealed class FixtureAuthenticator : IMcpRequestAuthenticator
    {
        internal static readonly McpAuthenticatedIdentity A = new("fixture-product", "connection-a", "1");
        internal static readonly McpAuthenticatedIdentity B = new("fixture-product", "connection-b", "1");
        public volatile bool Revoked;
        public int Calls;
        public ValueTask<McpAuthenticationResult> AuthenticateAsync(McpAuthenticationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var credential = request.Headers.TryGetValue("Authorization", out var values) ? string.Join(",", values) : null;
            return ValueTask.FromResult(credential switch
            {
                null or "Bearer mistakenly-anonymous" => McpAuthenticationResult.Anonymous,
                "Bearer fixture-a" when !Revoked => McpAuthenticationResult.Authenticated(A),
                "Bearer fixture-a-new" => McpAuthenticationResult.Authenticated(new(A.Authority, A.Subject, "2")),
                "Bearer fixture-b" => McpAuthenticationResult.Authenticated(B),
                "Bearer verifier-failure" => throw new InvalidOperationException("deterministic failure"),
                _ => McpAuthenticationResult.Reject(401, "Bearer realm=\"fixture\"")
            });
        }
    }
    private sealed class BlockingAuthenticator : IMcpRequestAuthenticator
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async ValueTask<McpAuthenticationResult> AuthenticateAsync(McpAuthenticationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            return McpAuthenticationResult.Authenticated(FixtureAuthenticator.A);
        }
    }
    private class StatelessDispatcher : IMcpDispatcher
    {
        public McpAuthenticatedIdentity? Identity;
        public int InitializeCalls;
        public bool Disposed;
        public Task<JsonObject> InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InitializeCalls);
            return Task.FromResult(new JsonObject { ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "identity-fixture", ["version"] = "0" } });
        }
        public virtual Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken cancellationToken)
        {
            Identity = context.Identity;
            return Task.FromResult(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject() });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class FixtureDispatcher : StatelessDispatcher, IMcpSessionLifecycle
    {
        public readonly ConcurrentQueue<McpSessionContext> Opened = new();
        public readonly ConcurrentQueue<McpRequestContext> Contexts = new();
        public readonly ConcurrentQueue<(McpSessionContext Context, McpSessionCloseReason Reason)> Closed = new();
        public readonly TaskCompletionSource DispatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DispatchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CloseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CloseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource OpenRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockDispatch, BlockClose, BlockOpen, FailOpen;
        public int ActiveDispatches, ActiveOpens;
        public async ValueTask SessionOpenedAsync(McpSessionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ActiveOpens);
            try
            {
                Opened.Enqueue(context); OpenEntered.TrySetResult();
                if (BlockOpen) await OpenRelease.Task;
                if (FailOpen) throw new InvalidOperationException("deterministic open failure");
            }
            finally { Interlocked.Decrement(ref ActiveOpens); }
        }
        public async ValueTask SessionClosedAsync(McpSessionContext context, McpSessionCloseReason reason)
        {
            CloseEntered.TrySetResult();
            if (BlockClose) await CloseRelease.Task;
            Closed.Enqueue((context, reason));
        }
        public override async Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ActiveDispatches);
            try
            {
                Contexts.Enqueue(context); DispatchEntered.TrySetResult();
                if (BlockDispatch) await DispatchRelease.Task; // Deliberately ignore cancellation to prove actual drain.
                return await base.DispatchAsync(context, request, cancellationToken);
            }
            finally { Interlocked.Decrement(ref ActiveDispatches); }
        }
    }
}
