using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexPayloadTieringClientResult(string State, string? Error = null,
    OperationReceipt? Receipt = null, string? RequestId = null)
{
    internal int ExitCode => State == "done" ? 0 : State is "invalid" or "refused" ? 3 : 4;
}

// The stable launcher selects this installed Host image for the fixed helper
// mode. Reuse Exit's exact-generation owner connection, never start an owner or
// open a database here. A lost reply is uncertain and is never resubmitted.
internal static class CortexPayloadTieringClient
{
    internal const string Flag = "--payload-storage";

    internal static async Task<CortexPayloadTieringClientResult> RunAsync(string[] arguments,
        TextReader input, CancellationToken token,
        Func<CancellationToken, Task<ICortexHostExitConnection?>>? connect = null)
    {
        if (token.IsCancellationRequested) return new("invalid", "canceled_before_submission");
        OperationRequest request;
        try
        {
            string body;
            if (arguments is [Flag, "upgrade"])
                body = """{"version":1,"action":"upgrade","expectedSchemaVersion":2}""";
            else if (arguments is [Flag, "offload" or "rehydrate", "--stdin"])
                body = await ReadBodyAsync(input, token).ConfigureAwait(false);
            else return new("invalid", "invalid_arguments");
            request = new(CortexPayloadTieringControl.Operation, new Dictionary<string, string>(),
                Guid.NewGuid().ToString("N"), Body: body);
            var command = CortexPayloadTieringControl.ValidateRequest(request);
            if (!string.Equals(command.Action.ToString(), arguments[1], StringComparison.OrdinalIgnoreCase))
                return new("invalid", "action_mismatch");
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException or
                                      IOException or OperationCanceledException)
        { return new("invalid", error is OperationCanceledException ? "canceled_before_submission" : "invalid_request"); }

        ICortexHostExitConnection? connection = null;
        var submitted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            connection = await (connect ?? CortexHostExitControl.ConnectCurrentAsync)(token).ConfigureAwait(false);
            if (connection is null) return new("unavailable", "owner_unavailable");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            submitted = true;
            var receipt = await connection.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (receipt.Op != request.Op || receipt.Rid != request.Rid ||
                string.IsNullOrWhiteSpace(receipt.Jid) ||
                receipt.State is not ("done" or "failed" or "canceled" or "recovery_required") ||
                receipt.Ok != (receipt.State == "done"))
                return new("unknown", "reply_unverified", RequestId: request.Rid);
            if (receipt.Partial || receipt.State == "recovery_required" ||
                receipt.Data?.GetValueOrDefault("outcome_unknown") is { } unknown &&
                JsonSerializer.SerializeToElement(unknown).ValueKind == JsonValueKind.True)
                return new("unknown", receipt.Err ?? "outcome_unknown", receipt, request.Rid);
            return receipt.Ok && receipt.State == "done"
                ? new("done", Receipt: receipt, RequestId: request.Rid)
                : new("refused", receipt.Err ?? "operation_refused", receipt, request.Rid);
        }
        catch (Exception error) when (CortexHostExitControl.IsExpectedFailure(error))
        { return new(submitted ? "unknown" : "unavailable", submitted ? "reply_unavailable" : "owner_unavailable",
            RequestId: submitted ? request.Rid : null); }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBodyAsync(TextReader input, CancellationToken token)
    {
        var buffer = new char[1024];
        var body = new StringBuilder();
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > CortexPayloadTieringControl.MaximumBodyBytes)
                    throw new InvalidDataException("payload_request_too_large");
                body.Append(buffer, 0, count);
            }
            var text = body.ToString();
            if (new UTF8Encoding(false, true).GetByteCount(text) > CortexPayloadTieringControl.MaximumBodyBytes)
                throw new InvalidDataException("payload_request_too_large");
            return text;
        }
        finally { Array.Clear(buffer); }
    }
}
