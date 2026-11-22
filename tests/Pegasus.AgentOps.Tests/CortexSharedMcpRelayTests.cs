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
                new RootCodeVerifier(Path.Combine(root, "root.verifier")), false,
                (peer, pipe, request, token) =>
                {
                    Check(peer == owner && pipe == "fixture-shared-mcp", "relay retains registered owner route");
                    Check(request.Root is null && request.Arg.Count == 1 && !request.Arg.ContainsKey("__execution_context_system"),
                        "relay never upgrades ordinary semantic work to System");
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
            var result = await broker.RelaySharedMcpAsync(request, CancellationToken.None);
            Check(result.Ok && result.Rid == request.Rid && calls == 1, "one exact relay call");

            try
            {
                await broker.RelaySharedMcpAsync(request with { Root = "AB12" }, CancellationToken.None);
                throw new InvalidOperationException("relay accepted an execution-root selector");
            }
            catch (AgentOpsException error) when (error.Code == "invalid") { }
            Check(calls == 1, "invalid private shape reaches no callback");

            wrongReply = true;
            var mismatch = await broker.RelaySharedMcpAsync(Request(), CancellationToken.None);
            Check(!mismatch.Ok && mismatch.Partial && mismatch.Err == "relay_identity_failed" && calls == 2,
                "lost/mismatched result is unknown without replay");
            wrongReply = false;

            var foreignImage = owner with { ApplicationPath = Path.Combine(root, "foreign.exe") };
            _ = await broker.RegisterTrayAsync(new(owner.Sid, owner.Session, "fixture-shared-mcp"), foreignImage);
            var refused = await broker.RelaySharedMcpAsync(Request(), CancellationToken.None);
            Check(!refused.Ok && refused.Err == "user_host_unavailable" && calls == 2, "foreign registered image refused");
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

    private static OperationRequest Request() => new(CortexSharedMcpRelay.Operation,
        new Dictionary<string, string> { ["kind"] = "initialize" }, Guid.NewGuid().ToString("N"), Body: "{}");
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
}
