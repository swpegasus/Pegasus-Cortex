using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Mcp.Local.Runtime;

internal static class CortexPayloadTieringClientTests
{
    private const string Upgrade = """{"version":1,"action":"upgrade","expectedSchemaVersion":2}""";
    private static string Offload => "{\"version\":1,\"action\":\"offload\",\"expectedSchemaVersion\":3,\"records\":[" +
        "{\"namespace\":\"project.fixture\",\"kind\":\"task\",\"recordId\":\"archived\",\"expectedHeadRevision\":2,\"expectedHeadHash\":\"" + new string('a', 64) + "\"}]}";

    internal static async Task RunAsync()
    {
        var connects = 0;
        Task<ICortexHostExitConnection?> Absent(CancellationToken _)
        { connects++; return Task.FromResult<ICortexHostExitConnection?>(null); }
        foreach (var (arguments, body) in new[]
        {
            (new[] { "--payload-storage" }, ""),
            (new[] { "--payload-storage", "upgrade", "--database", "forbidden" }, ""),
            (new[] { "--payload-storage", "offload", "--stdin" }, Upgrade),
            (new[] { "--payload-storage", "offload", "--stdin" }, "{\"version\":1,\"version\":1}"),
            (new[] { "--payload-storage", "offload", "--stdin" }, new string('x', CortexPayloadTieringControl.MaximumBodyBytes + 1)),
            (new[] { "--payload-storage", "offload", "--stdin" }, "\ud800")
        })
        {
            var invalid = await CortexPayloadTieringClient.RunAsync(arguments, new StringReader(body), CancellationToken.None, Absent);
            Check(invalid.State == "invalid" && connects == 0, "invalid intent reached owner connection");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var before = await CortexPayloadTieringClient.RunAsync(["--payload-storage", "upgrade"],
                new StringReader(""), canceled.Token, Absent);
            Check(before.Error == "canceled_before_submission" && connects == 0, "pre-canceled helper connected");
        }
        var missing = await CortexPayloadTieringClient.RunAsync(["--payload-storage", "upgrade"],
            new StringReader(""), CancellationToken.None, Absent);
        Check(missing.State == "unavailable" && connects == 1, "missing owner was treated as successful upgrade");

        foreach (var action in new[] { "upgrade", "offload" })
        {
            var connection = new Connection();
            var args = action == "upgrade" ? new[] { "--payload-storage", "upgrade" } :
                new[] { "--payload-storage", "offload", "--stdin" };
            var result = await CortexPayloadTieringClient.RunAsync(args, new StringReader(Offload),
                CancellationToken.None, _ => Task.FromResult<ICortexHostExitConnection?>(connection));
            Check(result.State == "done" && result.Receipt is not null && connection.Sends == 1 && connection.Disposals == 1,
                "fixed helper failed one original submission/cleanup");
            var request = connection.Last!;
            var parsed = CortexPayloadTieringControl.ValidateRequest(request);
            Check(parsed.Action.ToString().Equals(action, StringComparison.OrdinalIgnoreCase) && request.Arg.Count == 0 &&
                request.Root is null && request.Ws is null && request.Cwd is null && request.PathRoots is null,
                "fixed helper selected extra context or changed intended action");
        }

        foreach (var change in new Func<OperationReceipt, OperationReceipt>[]
        {
            receipt => receipt with { Rid = "wrong" }, receipt => receipt with { Op = "wrong" },
            receipt => receipt with { Jid = "" }, receipt => receipt with { State = "failed" }
        })
        {
            var connection = new Connection { Change = change };
            var result = await CortexPayloadTieringClient.RunAsync(["--payload-storage", "upgrade"],
                new StringReader(""), CancellationToken.None, _ => Task.FromResult<ICortexHostExitConnection?>(connection));
            Check(result.State == "unknown" && result.Receipt is null && result.RequestId == connection.Last!.Rid &&
                connection.Sends == 1 && connection.Disposals == 1,
                "unverified reply was accepted or automatically resubmitted");
        }
        var partial = new Connection
        {
            Change = receipt => receipt with
            {
                Ok = false, State = "recovery_required", Err = "payload_schema_unavailable", Partial = true,
                Data = new Dictionary<string, object?> { ["outcome_unknown"] = JsonSerializer.SerializeToElement(true) }
            }
        };
        var unknown = await CortexPayloadTieringClient.RunAsync(["--payload-storage", "upgrade"],
            new StringReader(""), CancellationToken.None, _ => Task.FromResult<ICortexHostExitConnection?>(partial));
        Check(unknown.State == "unknown" && unknown.Receipt?.Partial == true && partial.Sends == 1,
            "identity-verified partial receipt was lost or relabeled done");
        var failed = new Connection { FailSend = true };
        var lost = await CortexPayloadTieringClient.RunAsync(["--payload-storage", "upgrade"],
            new StringReader(""), CancellationToken.None, _ => Task.FromResult<ICortexHostExitConnection?>(failed));
        Check(lost.State == "unknown" && lost.RequestId == failed.Last!.Rid && failed.Sends == 1 && failed.Disposals == 1,
            "lost original acknowledgement was retried or classified unissued");
        Console.WriteLine("PASS payload tiering CLI client: fixed intent/closed bounded input, attach-only original send, typed receipt/RID validation, retained partial uncertainty and no replay; injected connection, no owner/database/runtime action");
    }

    private sealed class Connection : ICortexHostExitConnection
    {
        internal int Sends, Disposals;
        internal OperationRequest? Last;
        internal Func<OperationReceipt, OperationReceipt>? Change;
        internal bool FailSend;
        public uint ProcessId => 42;
        public long CreationFileTime => 123;
        public Task<OperationReceipt> SendAsync(OperationRequest request, CancellationToken token)
        {
            Sends++; Last = request;
            if (FailSend) throw new IOException("synthetic lost reply");
            var receipt = new OperationReceipt(true, "fixture-jid", request.Rid!, "done", CortexPayloadTieringControl.Operation);
            return Task.FromResult(Change?.Invoke(receipt) ?? receipt);
        }
        public Task<bool> WaitForStopAsync(TimeSpan timeout, CancellationToken token) =>
            throw new InvalidOperationException("Tiering must not stop or wait for the ordinary Host.");
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
}
