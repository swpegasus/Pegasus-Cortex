using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Cli;

// Presentation is deliberately separate from engine receipts and the CLI wire contract.
internal static class McpOutputProjection
{
    private const int MaxFailureOutputUtf8Bytes = 4096;
    private const int MaxFailureTextUtf8Bytes = 512;
    private const int MaxFailureDiagnostics = 4;
    private const int MaxProcessResultUtf8Bytes = 4096;
    // Presentation only: never change persisted receipts or unescape submitted text.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly HashSet<string> OptionalEmptyMetadata = new(StringComparer.Ordinal)
    {
        "err", "remedy", "detail", "notes", "out", "err_tail"
    };

    public static JsonObject FromReceipt(OperationReceipt receipt, bool agentOutput)
    {
        if(receipt.Op=="terminal"||receipt.Op.StartsWith("terminal_",StringComparison.Ordinal)) return Result(TerminalResultProjection.Serialize(receipt),!receipt.Ok,null);
        var structured = JsonSerializer.SerializeToNode(receipt, Json) as JsonObject ??
            throw new InvalidOperationException("The PAO receipt could not be serialized.");
        var text = agentOutput && TryNumberedText(structured, out var numbered)
            ? numbered
            : agentOutput
                ? receipt.Ok ? Success(receipt) : Failure(receipt)
                : structured.ToJsonString(Json);
        return Result(text, !receipt.Ok, agentOutput ? null : structured);
    }

    public static JsonObject FromError(JsonObject error, bool agentOutput)
    {
        if (!agentOutput) return Result(error.ToJsonString(Json), true, error);
        var body = error["error"] as JsonObject;
        var code = body?["code"]?.GetValue<string>() ?? "pao_error";
        var message = body?["message"]?.GetValue<string>() ?? "The request was rejected before a receipt was created.";
        var text = string.Join('\n',
            $"FAIL no-receipt {code}",
            SafeFailureText(message),
            "execution=not_dispatched source=mcp-admission retry=after_correction retry_source=mcp-admission",
            "next=Correct the typed request using pao help, then retry.",
            "detail=none log=not_retained");
        return Result(BoundUtf8(text, MaxFailureOutputUtf8Bytes), true, null);
    }

    private static JsonObject Result(string text, bool isError, JsonObject? structured)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
        if (structured is not null) result["structuredContent"] = structured;
        return result;
    }

    private static JsonNode Compact(JsonNode value)
    {
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj)
            {
                if (property.Value is null) continue;
                var projected = Compact(property.Value);
                // Empty collections/results, source text, false and zero remain meaningful.
                if (OptionalEmptyMetadata.Contains(property.Key) && IsEmpty(projected)) continue;
                result[property.Key] = projected;
            }
            return result;
        }
        if (value is JsonArray array)
            return new JsonArray(array.Select(item => item is null ? null : Compact(item)).ToArray());
        return value.DeepClone();
    }

    private static bool IsEmpty(JsonNode value) => value is JsonObject { Count: 0 } or JsonArray { Count: 0 } ||
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length == 0;

    private static string Success(OperationReceipt receipt)
    {
        if (receipt.State is "queued" or "running")
        {
            // Admission is not a result. Do not fabricate an exit code, result
            // body or log-availability fact before the retained job completes.
            return new JsonObject { ["state"] = receipt.State, ["jid"] = receipt.Jid }.ToJsonString(Json);
        }
        var projected = new JsonObject();
        if (receipt.Data is not null)
        {
            var node = JsonSerializer.SerializeToNode(receipt.Data, Json) as JsonObject;
            if (node is not null)
            {
                projected = Compact(node).AsObject();
                projected.Remove("seq");
                // Queue inspection owns retained input details. Routine results
                // stay result-only; explicit job_stat/structured receipts retain them.
                projected.Remove("submission");
                if (receipt.Op.StartsWith("registry_", StringComparison.Ordinal))
                {
                    // Result generations/preimages remain inside registry. Request
                    // identity and audit metadata belong to the structured receipt.
                    foreach (var key in new[] { "requestIdentity", "resource_identity", "registry_status", "registry_code", "log_available" })
                        projected.Remove(key);
                }
                if (receipt.Op.StartsWith("certificate_", StringComparison.Ordinal))
                    foreach (var key in new[] { "requestIdentity", "certificate_status", "log_available" }) projected.Remove(key);
                if (receipt.Op == "job_prune")
                {
                    // Keep the full cleanup audit in structured receipts. The
                    // ordinary agent result needs counts and continuation only.
                    foreach (var key in new[] { "mode", "before", "selected", "outcomes_unchanged", "rid_barriers_preserved" })
                        projected.Remove(key);
                    foreach (var key in new[] { "skipped_active", "skipped_invalid" })
                        if (projected[key]?.ToJsonString() == "0") projected.Remove(key);
                    if (projected["more"]?.ToJsonString() != "true") projected.Remove("next");
                }
                if (CarriesRetainedProcessOutput(receipt.Op))
                {
                    var stdout = projected["out"] is JsonValue output && output.TryGetValue<string>(out var captured) ? captured : null;
                    var includesResult = ProcessOutputIsResult(receipt.Op) && !string.IsNullOrEmpty(stdout);
                    var clipped = includesResult && Encoding.UTF8.GetByteCount(stdout!) > MaxProcessResultUtf8Bytes;
                    var logAvailable = HasText(projected["err_tail"]) || HasText(projected["out"]) && (!includesResult || clipped);
                    // Process output remains an untrusted string. Never interpret it as
                    // a typed result or suppress every small value behind a log read.
                    if (includesResult) projected["out"] = BoundUtf8(stdout!, MaxProcessResultUtf8Bytes);
                    else projected.Remove("out");
                    if (clipped) projected["out_truncated"] = true;
                    projected.Remove("err_tail");
                    if (logAvailable) projected["log_available"] = true;
                    else projected.Remove("log_available");
                }
            }
        }
        if (receipt.Partial)
        {
            projected["partial"] = true;
            projected["rid"] = receipt.Rid;
            projected["jid"] = receipt.Jid;
        }
        if (projected["log_available"]?.ToJsonString() == "true") projected["jid"] = receipt.Jid;
        if (receipt.State is not ("done" or "completed"))
        {
            projected[projected.ContainsKey("state") ? "execution_state" : "state"] = receipt.State;
            projected["jid"] = receipt.Jid;
        }
        return projected.Count == 0 ? "Completed." : projected.ToJsonString(Json);
    }

    private static bool ProcessOutputIsResult(string operation) =>
        operation is "run" or "pwsh" or "do" || operation.StartsWith("git_", StringComparison.Ordinal);

    private static bool CarriesRetainedProcessOutput(string operation) =>
        operation is "run" or "pwsh" or "do" ||
        operation.StartsWith("git_", StringComparison.Ordinal) ||
        operation.StartsWith("dotnet_", StringComparison.Ordinal) ||
        operation.StartsWith("7z_", StringComparison.Ordinal) ||
        operation.StartsWith("nsis_", StringComparison.Ordinal);

    private static bool HasText(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > 0;

    private static string Failure(OperationReceipt receipt)
    {
        var evidence = receipt.Failure ?? Fallback(receipt);
        var builder = new StringBuilder()
            .Append("FAIL ").Append(receipt.Jid).Append(' ').Append(receipt.Err ?? "unknown")
            .Append('\n').Append(SafeFailureText(evidence.Message));
        if (evidence.Field is not null)
        {
            builder.Append("\nfield=").Append(evidence.Field.Name);
            if (evidence.Field.Index is not null) builder.Append('[').Append(evidence.Field.Index.Value).Append(']');
            if (evidence.Bound is not null)
            {
                if (evidence.Bound.Actual is not null) builder.Append(" actual=").Append(evidence.Bound.Actual.Value);
                builder.Append(" max=").Append(evidence.Bound.Maximum).Append(" unit=").Append(evidence.Bound.Unit);
            }
        }
        builder.Append("\nexecution=").Append(evidence.Execution.Value)
            .Append(" source=").Append(evidence.Execution.Source)
            .Append(" retry=").Append(evidence.Retry.Value)
            .Append(" retry_source=").Append(evidence.Retry.Source);
        if (evidence.Retry.Value == OperationRetryDisposition.ReconcileFirst || receipt.Partial)
            builder.Append("\nrid=").Append(receipt.Rid);

        var diagnostics = (evidence.Diagnostics ?? []).Distinct().Take(MaxFailureDiagnostics).ToArray();
        for (var index = 0; index < diagnostics.Length; index++)
        {
            var diagnostic = diagnostics[index];
            builder.Append("\ndiag[").Append(index + 1).Append("]=");
            if (diagnostic.Source is not null)
            {
                builder.Append(diagnostic.Source);
                if (diagnostic.Line is not null) builder.Append(':').Append(diagnostic.Line.Value);
                if (diagnostic.Column is not null) builder.Append(':').Append(diagnostic.Column.Value);
                builder.Append(' ');
            }
            if (diagnostic.Code is not null) builder.Append(diagnostic.Code).Append(' ');
            builder.Append(SafeFailureText(diagnostic.Message));
        }
        if (evidence.DiagnosticsOmitted || (evidence.Diagnostics?.Count ?? 0) > MaxFailureDiagnostics)
            builder.Append("\ndiagnostics_omitted=true");
        if (!string.IsNullOrWhiteSpace(evidence.Next)) builder.Append("\nnext=").Append(SafeFailureText(evidence.Next));
        builder.Append("\ndetail=").Append(evidence.DetailAvailability).Append(" log=").Append(evidence.LogAvailability);
        return BoundUtf8(builder.ToString(), MaxFailureOutputUtf8Bytes);
    }

    private static OperationFailureEvidence Fallback(OperationReceipt receipt)
    {
        var execution = receipt.Partial ? OperationExecutionState.Partial : OperationExecutionState.Unknown;
        var retry = receipt.Partial || receipt.Err is "recovery_required" or "timeout"
            ? OperationRetryDisposition.ReconcileFirst
            : OperationRetryDisposition.AfterCorrection;
        var hasLog = receipt.Data is not null &&
            (receipt.Data.TryGetValue("out", out var stdout) && stdout is string { Length: > 0 } ||
             receipt.Data.TryGetValue("err_tail", out var stderr) && stderr is string { Length: > 0 });
        return new OperationFailureEvidence(
            "The typed operation failed with the reported stable code.",
            new OperationFailureFact(execution, "legacy-receipt"),
            new OperationFailureFact(retry, "legacy-receipt"),
            Next: receipt.Remedy,
            DetailAvailability: receipt.Data is { Count: > 0 } ? "structured" : "none",
            LogAvailability: hasLog ? "available" : "not_retained");
    }

    private static string BoundLine(string value) => BoundUtf8(
        value.Replace('\r', ' ').Replace('\n', ' ').Trim(), MaxFailureTextUtf8Bytes);

    private static string SafeFailureText(string value)
    {
        var safe = Regex.Replace(value, "(?i)\\b(api[_-]?key|token|password|secret)\\s*[:=]\\s*[^\\s,;]+", "$1=<redacted>");
        safe = Regex.Replace(safe, "(?i)\\bbearer\\s+[^\\s,;]+", "Bearer <redacted>");
        safe = Regex.Replace(safe, "(?i)(?:[A-Z]:\\\\|\\\\\\\\)[^\\r\\n]+", "<path>");
        return BoundLine(safe);
    }

    private static string BoundUtf8(string value, int maximum)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximum) return value;
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maximum - 3) break;
            builder.Append(rune.ToString());
            bytes += size;
        }
        return builder.Append("...").ToString();
    }

    private static bool TryNumberedText(JsonObject receipt, out string text)
    {
        text = string.Empty;
        if (receipt["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var succeeded) || !succeeded ||
            receipt["op"] is not JsonValue op || !op.TryGetValue<string>(out var operation) || operation != "read_lines" ||
            receipt["data"] is not JsonObject data || data["entries"] is not JsonArray entries || entries.Count > 512)
            return false;

        var lines = new List<(int Number, string Text, bool Truncated)>();
        foreach (var item in entries)
        {
            if (item is not JsonObject entry || entry["line"] is not JsonValue number ||
                !number.TryGetValue<int>(out var line) || line < 1 || entry["text"] is not JsonValue content ||
                !content.TryGetValue<string>(out var sourceText)) return false;
            var truncated = entry["truncated"] is JsonValue marker && marker.TryGetValue<bool>(out var flag) && flag;
            lines.Add((line, sourceText, truncated));
        }

        // The engine caps source text at 256 KiB and 512 lines. Prefixes and this fixed
        // metadata add only bounded overhead; no second clipping can hide continuation.
        var builder = new StringBuilder();
        if (data["has_more"]?.ToJsonString() == "true") AppendFields(builder, data, ["next", "total"]);
        if (receipt["partial"]?.ToJsonString() == "true") builder.Append(" partial=true");
        if (data["truncated"]?.ToJsonString() == "true") builder.Append(" truncated=true");
        var truncatedLines = lines.Where(line => line.Truncated).Select(line => line.Number).ToArray();
        if (truncatedLines.Length > 0) builder.Append(" truncated_lines=").Append(JsonSerializer.Serialize(truncatedLines, Json));
        var metadata = builder.ToString().Trim();
        builder.Clear().Append(metadata);
        foreach (var line in lines)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line.Number).Append(": ").Append(line.Text);
        }
        text = builder.Length == 0 ? "No lines." : builder.ToString();
        return true;
    }

    private static void AppendFields(StringBuilder builder, JsonObject source, string[] fields)
    {
        foreach (var key in fields)
        {
            if (source[key] is not { } value) continue;
            builder.Append(' ').Append(key).Append('=').Append(value.ToJsonString(Json));
        }
    }
}


// Terminal bodies/results are ephemeral, and all interfaces share the same minimal payload.
internal static class TerminalResultProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Serialize(OperationReceipt receipt)
    {
        if(receipt.Ok) return JsonSerializer.Serialize(receipt.Data ?? new Dictionary<string,object?>(),Json);
        var result=new Dictionary<string,object?>{{"error",receipt.Err??"terminal_failed"}};
        if(receipt.Partial) { result["delivery"]="unknown"; result["original_rid"]=receipt.Rid; }
        if(receipt.Data is not null)foreach(var item in receipt.Data)result[item.Key]=item.Value;
        return JsonSerializer.Serialize(result,Json);
    }
}
