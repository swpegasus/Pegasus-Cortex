using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class CortexSharedMcpRelayTests
{
    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-shared-relay-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var owner = LocalIdentity.Current();
            var image = owner.ApplicationPath ?? throw new InvalidOperationException("fixture image unavailable");
            var calls = 0;
            var wrongReply = false;
            await using var broker = new AuthenticatedServiceBroker(root, owner.Sid,
                null, false,
                (peer, pipe, request, token) =>
                {
                    Check(peer == owner && pipe == "fixture-shared-mcp", "relay retains registered owner route");
                    Check(request.Root is null && request.Arg.Count == 1 && !request.Arg.ContainsKey("__execution_context_system"),
                        "relay never upgrades ordinary semantic work to System");
                    if (request.Arg["kind"] == "invoke")
                    {
                        using var body = JsonDocument.Parse(request.Body!);
                        var identity = body.RootElement.GetProperty("identity");
                        var authority = ApprovedConnectionFixture.DefaultAuthority;
                        Check(body.RootElement.EnumerateObject().Count() == 6 &&
                            identity.GetProperty("authority").GetString() == authority.Authority &&
                            identity.GetProperty("subject").GetString() == authority.ConnectionId &&
                            identity.GetProperty("generation").GetString() == authority.CredentialGeneration,
                            "private relay lost immutable public connection coordinates");
                    }
                    calls++;
                    return Task.FromResult(new OperationReceipt(true, Guid.NewGuid().ToString("N"),
                        wrongReply ? "wrong" : request.Rid!, "done", CortexSharedMcpRelay.Operation,
                        Data: new Dictionary<string, object?> { ["fixture"] = true }));
                });
            broker.SetServiceExitProbe(_ => Task.FromResult<string?>(null), image, () => { });
            var request = Request();
            var absent = await broker.RelaySharedMcpAsync(request, CancellationToken.None);
            Check(!absent.Ok && absent.Err == "user_host_unavailable" && calls == 0, "missing host cannot bootstrap or dispatch");
            var registration = await broker.RegisterTrayAsync(new(owner.Sid, owner.Session, "fixture-shared-mcp"), owner);
            Check(registration.Ok, "owned fixture registration");
            var forged = Request(invoke:true);
            var publicUser = await broker.HandleUserAsync(forged,owner,CancellationToken.None);
            var publicApplication = await broker.HandleApplicationAsync(forged,CancellationToken.None);
            var publicSystem = await broker.HandleSystemAsync(forged with { System=true,
                ConnectionAuthority=ApprovedConnectionFixture.DefaultAuthority },owner,CancellationToken.None);
            Check(!publicUser.Ok && publicUser.Err=="private_operation" &&
                !publicApplication.Ok && publicApplication.Err=="private_operation" &&
                !publicSystem.Ok && publicSystem.Err=="private_operation" && calls==0,
                "public callers bounced private connection coordinates through the trusted service relay");
            var result = await broker.RelaySharedMcpAsync(request, CancellationToken.None);
            Check(result.Ok && result.Rid == request.Rid && calls == 1, "one exact relay call");

            try
            {
                await broker.RelaySharedMcpAsync(request with { Root = "AB12" }, CancellationToken.None);
                throw new InvalidOperationException("relay accepted an execution-root selector");
            }
            catch (AgentOpsException error) when (error.Code == "invalid") { }
            Check(calls == 1, "invalid private shape reaches no callback");

            var invoked = await broker.RelaySharedMcpAsync(Request(invoke: true), CancellationToken.None);
            Check(invoked.Ok && calls == 2, "one private invocation preserves full identity envelope");
            wrongReply = true;
            var mismatch = await broker.RelaySharedMcpAsync(Request(), CancellationToken.None);
            Check(!mismatch.Ok && mismatch.Partial && mismatch.Err == "relay_identity_failed" && calls == 3,
                "lost/mismatched result is unknown without replay");
            wrongReply = false;

            var foreignImage = owner with { ApplicationPath = Path.Combine(root, "foreign.exe") };
            _ = await broker.RegisterTrayAsync(new(owner.Sid, owner.Session, "fixture-shared-mcp"), foreignImage);
            var refused = await broker.RelaySharedMcpAsync(Request(), CancellationToken.None);
            Check(!refused.Ok && refused.Err == "user_host_unavailable" && calls == 3, "foreign registered image refused");
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cortex-shared-relay-", StringComparison.Ordinal) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("owned relay fixture cleanup boundary changed");
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("PASS shared MCP service relay: registered image/generation route, private shape, no System marker/bootstrap/replay; owned SQLite and injected transport fixture");
    }

    private static OperationRequest Request(bool invoke = false)
    {
        var source = ApprovedConnectionFixture.DefaultAuthority;
        var body = invoke ? JsonSerializer.Serialize(new
        {
            sessionId = "fixture-session", streamId = "fixture-stream", taskId = (string?)null, requestId = "fixture-request",
            identity = new { authority = source.Authority, subject = source.ConnectionId, generation = source.CredentialGeneration },
            request = new { jsonrpc = "2.0", id = "fixture-request", method = "tools/list" }
        }) : "{}";
        return new(CortexSharedMcpRelay.Operation,
            new Dictionary<string, string> { ["kind"] = invoke ? "invoke" : "initialize" }, Guid.NewGuid().ToString("N"), Body: body);
    }
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
}
