using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class TextPreimageContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        using var fixture = new TempRoot();
        var state = fixture.Dir("state");
        var target = Path.Combine(fixture.Path, "owned Unicode 雪.txt");
        var original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("alpha\r\n雪\r\nalpha\r\n")).ToArray();
        await File.WriteAllBytesAsync(target, original);
        var before = Sha(original);
        string failedJid;
        OperationFailureEvidence retainedFailure;

        await using (var engine = new AgentOpsEngine(state))
        {
            var mismatch = await ReceiptAsync(engine, new
            {
                op = "wr", dst = target, text = "private replacement canary", sha = new string('0', 64),
                rid = "write-preimage-mismatch", output = "structured"
            });
            AssertGuard(mismatch, "precondition", "preimage_changed", "sha", "wr");
            Check(mismatch.Rid == "write-preimage-mismatch", "Failure changed the supplied RID.");
            BytesEqual(original, await File.ReadAllBytesAsync(target));
            failedJid = mismatch.Jid;
            retainedFailure = mismatch.Failure!;

            var invalid = await ReceiptAsync(engine, new
            {
                op = "wr", dst = target, text = "private replacement canary", sha = "not-a-hash", output = "structured"
            });
            AssertGuard(invalid, "precondition", "preimage_invalid", "sha", "wr");
            BytesEqual(original, await File.ReadAllBytesAsync(target));

            var missing = Path.Combine(fixture.Path, "missing.txt");
            var absent = await ReceiptAsync(engine, new { op = "wr", dst = missing, text = "never create", sha = before, output = "structured" });
            AssertGuard(absent, "not_found", "preimage_missing", "sha", "wr");
            Check(!File.Exists(missing), "A hash-bound write created a missing target.");
            var created = await ReceiptAsync(engine, new { op = "wr", dst = missing, text = "new 雪\n", output = "structured" });
            Check(created.Ok && !created.Data!.ContainsKey("sha_before"), "No-hash creation changed its optional result contract.");
            Check(await File.ReadAllTextAsync(missing) == "new 雪\n", "No-hash creation changed its Unicode body.");

            // Legacy wr is create-or-overwrite, not a create-only/collision-fail API.
            var overwrite = await ReceiptAsync(engine, new { op = "wr", dst = missing, text = "legacy overwrite\n", output = "structured" });
            Check(overwrite.Ok && await File.ReadAllTextAsync(missing) == "legacy overwrite\n", "Legacy no-hash overwrite changed.");

            var matchedArgs = new { op = "wr", dst = target, text = "updated 雪\n", sha = before.ToUpperInvariant(), rid = "write-preimage-matched", output = "structured" };
            var matched = await ReceiptAsync(engine, matchedArgs);
            Check(matched.Ok && matched.Data!["sha_before"]!.ToString() == before &&
                matched.Data["enc"]!.ToString() == "utf16le" && matched.Data["bom"]!.ToString()!.Equals("true", StringComparison.OrdinalIgnoreCase),
                "Matched write lost exact preimage/encoding evidence.");
            var expected = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("updated 雪\r\n")).ToArray();
            BytesEqual(expected, await File.ReadAllBytesAsync(target));
            Check(matched.Data!["sha"]!.ToString() == Sha(expected), "Postimage digest is not over the published bytes.");
            var replay = await ReceiptAsync(engine, matchedArgs);
            Check(replay.Ok && replay.Jid == matched.Jid && replay.Rid == matched.Rid &&
                replay.Data!["sha_before"]!.ToString() == before, "Replay lost original receipt identity/preimage.");
            BytesEqual(expected, await File.ReadAllBytesAsync(target));

            var literals = Path.Combine(fixture.Path, "literals.txt");
            await File.WriteAllBytesAsync(literals, original);
            foreach (var (oldValue, count, reason, field) in new[]
            {
                ("private missing literal canary", 1, "literal_not_found", "old"),
                ("alpha", 1, "literal_not_unique", "count"),
                ("alpha", 3, "literal_count_mismatch", "count")
            })
            {
                var rejected = await ReceiptAsync(engine, new { op = "replace", dst = literals, old = oldValue, count, text = "private replacement canary", sha = before, output = "structured" });
                AssertGuard(rejected, "precondition", reason, field, "replace");
                BytesEqual(original, await File.ReadAllBytesAsync(literals));
                AssertPrivate(rejected.Failure!, fixture.Path, oldValue, "private replacement canary", before);
            }
            var staleReplace = await ReceiptAsync(engine, new { op = "replace", dst = literals, old = "alpha", count = 2, text = "replacement", sha = new string('0', 64), output = "structured" });
            AssertGuard(staleReplace, "precondition", "preimage_changed", "sha", "replace");
            BytesEqual(original, await File.ReadAllBytesAsync(literals));
            var zero = await ReceiptAsync(engine, new { op = "replace", dst = literals, old = "absent", count = 0, text = "unused", sha = before, output = "structured" });
            Check(zero.Ok, "Explicit zero-occurrence compatibility changed.");
            BytesEqual(original, await File.ReadAllBytesAsync(literals));

            var compact = await CallAsync(engine, new { op = "replace", dst = literals, old = "private missing literal canary", count = 1, text = "private replacement canary", sha = before, output = "agent" });
            var projection = compact["content"]![0]!["text"]!.GetValue<string>();
            Check(compact["isError"]!.GetValue<bool>() && projection.StartsWith("FAIL ", StringComparison.Ordinal) &&
                projection.Contains("literal_not_found", StringComparison.Ordinal) && projection.Contains("execution=not_dispatched", StringComparison.Ordinal) &&
                projection.Contains("no target bytes were written", StringComparison.Ordinal) &&
                !projection.Contains(fixture.Path, StringComparison.Ordinal) && !projection.Contains("canary", StringComparison.Ordinal) &&
                Encoding.UTF8.GetByteCount(projection) <= 4096, "Compact guard failure lost its bounded, privacy-safe reason.");
            BytesEqual(original, await File.ReadAllBytesAsync(literals));

            // Actual binary serialization, not a live installed named-pipe probe.
            await using var frame = new MemoryStream();
            await BinaryProtocol.WriteAsync(frame, mismatch);
            frame.Position = 0;
            var roundTrip = await BinaryProtocol.ReadAsync<OperationReceipt>(frame);
            AssertGuard(roundTrip, "precondition", "preimage_changed", "sha", "wr");
            Check(roundTrip.Jid == mismatch.Jid && roundTrip.Rid == mismatch.Rid, "Binary receipt round-trip changed IDs.");
        }
        await using (var reopened = new AgentOpsEngine(state))
        {
            var lookup = await CallAsync(reopened, new { op = "job_stat", jid = failedJid, detail = true, output = "structured" });
            var saved = lookup["structuredContent"]!["data"]!["rec"]!.Deserialize<OperationReceipt>(Json)!;
            Check(saved.Jid == failedJid && JsonSerializer.Serialize(saved.Failure, Json) == JsonSerializer.Serialize(retainedFailure, Json),
                "SQLite reopening lost the original guard reason/identity.");
        }
        Console.WriteLine("EVIDENCE real isolated filesystem/SQLite with MCP adapter integration, binary receipt round-trip, exact pre/postimage and no-write checks; not installed-provider or live-pipe acceptance.");
    }

    private static async Task<OperationReceipt> ReceiptAsync(AgentOpsEngine engine, object arguments) =>
        (await CallAsync(engine, arguments))["structuredContent"]!.Deserialize<OperationReceipt>(Json)!;

    private static ValueTask<JsonObject> CallAsync(AgentOpsEngine engine, object arguments) =>
        PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(arguments), new EngineDispatcher(engine), CancellationToken.None);

    private static void AssertGuard(OperationReceipt receipt, string error, string reason, string field, string operation)
    {
        var failure = receipt.Failure;
        Check(!receipt.Ok && receipt.Err == error && receipt.ExitCode == 3 && receipt.State == "failed" && !receipt.Partial &&
            receipt.Jid.Length > 0 && receipt.Rid.Length > 0 && receipt.Op == operation &&
            failure is not null && failure.Field == new OperationFailureField(field) && failure.Diagnostics is [{ Code: var code }] && code == reason &&
            failure.Execution == new OperationFailureFact(OperationExecutionState.NotDispatched, "text-publication-guard") &&
            failure.Retry.Value == OperationRetryDisposition.AfterCorrection &&
            failure.DetailAvailability == "none" && failure.LogAvailability == "not_retained", "Guard receipt lost its stable category or truthful failure evidence.");
    }

    private static void AssertPrivate(OperationFailureEvidence failure, params string[] inputs)
    {
        var json = JsonSerializer.Serialize(failure, Json);
        foreach (var input in inputs)
            Check(!json.Contains(input, StringComparison.Ordinal) && !json.Contains(JsonSerializer.Serialize(input, Json)[1..^1], StringComparison.Ordinal), "Guard diagnostics exposed input content.");
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void BytesEqual(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Target bytes changed unexpectedly.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
