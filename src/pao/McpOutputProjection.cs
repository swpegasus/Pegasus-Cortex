using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Cli;

// Presentation is deliberately separate from engine receipts and the CLI wire contract.
internal static class McpOutputProjection
{
    private const int MaxFailureOutputUtf8Bytes = 4096;
    private const int MaxFailureTextUtf8Bytes = 512;
    private const int MaxFailureDiagnostics = 4;
    private const int MaxProcessResultUtf8Bytes = 4096;
    private static readonly HashSet<string> TransferMetadata = new(StringComparer.Ordinal)
    {
        "src", "dst", "pol", "del", "cp", "skipped", "bytes_transferred", "byte_progress_observed",
        "byte_count_saturated", "destination_may_have_changed", "source_deletion_issued", "partial",
        "cross_volume", "verified", "source_removed", "recovery_required", "replaced", "outcome_unknown"
    };
    // Presentation only: never change persisted receipts or unescape submitted text.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static JsonObject FromReceipt(OperationReceipt receipt, bool agentOutput, OperationRequest? request = null)
    {
        if(receipt.Op=="terminal"||receipt.Op.StartsWith("terminal_",StringComparison.Ordinal)) return Result(TerminalResultProjection.Serialize(receipt),!receipt.Ok,null);
        if (!agentOutput)
        {
            var structured = SerializeReceipt(receipt);
            return Result(structured.ToJsonString(Json), !receipt.Ok, structured);
        }
        var selected = request?.Op == "job" && request.Arg.GetValueOrDefault("op") is "stat" or "wait" or "cancel";
        var original = receipt;
        var cancelRequested = false;
        JsonNode? progress = null;
        if (selected)
        {
            var jid = request!.Arg.GetValueOrDefault("jid");
            if (request.Arg.GetValueOrDefault("op") != "wait")
            {
                if (!TrySelectedReceipt(receipt, jid, out original))
                    return request.Detail
                        ? Result(SerializeReceipt(receipt).ToJsonString(Json), !receipt.Ok, null)
                        : !receipt.Ok ? Result(Failure(receipt), true, null)
                        : Result("Job receipt unavailable or unverified. Inspect the original with output=structured; do not resubmit the operation.", true, null);
                cancelRequested = ScalarBoolean(receipt.Data?.GetValueOrDefault("cancel_requested")) == true;
                if (receipt.Data?.GetValueOrDefault("progress") is { } liveProgress)
                    progress = JsonSerializer.SerializeToNode(liveProgress, Json);
            }
            else if (receipt.Jid != jid && receipt.Ok)
                return Result("Job receipt identity was not confirmed. Inspect the original with output=structured; do not resubmit the operation.", true, null);
        }
        if (request?.Detail == true)
        {
            var detail = progress is null ? SerializeReceipt(original) :
                new JsonObject { ["rec"] = SerializeReceipt(original), ["progress"] = progress.DeepClone() };
            return Result(detail.ToJsonString(Json), !receipt.Ok, null);
        }
        var text = AgentText(original, selected);
        if (progress is not null)
        {
            var observed = new JsonObject { ["state"] = original.State, ["progress"] = progress };
            if (!original.Ok || original.Partial || original.State is not ("queued" or "running")) observed["result"] = text;
            text = observed.ToJsonString(Json);
        }
        if (cancelRequested) text = "Cancellation requested. " + text;
        // A successful job observation may describe a failed original operation.
        // Preserve that distinction and its failure/reconciliation evidence.
        return Result(text, !receipt.Ok, null);
    }

    private static JsonObject SerializeReceipt(OperationReceipt receipt) =>
        JsonSerializer.SerializeToNode(receipt, Json) as JsonObject ??
            throw new InvalidOperationException("The PAO receipt could not be serialized.");

    private static bool TrySelectedReceipt(OperationReceipt observed, string? jid, out OperationReceipt original)
    {
        original = observed;
        if (string.IsNullOrEmpty(jid) || observed.Data?.GetValueOrDefault("rec") is not { } value) return false;
        try
        {
            var node = JsonSerializer.SerializeToNode(value, Json) as JsonObject;
            if (node is null || node["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out _) ||
                !HasText(node["rid"]) || !HasText(node["state"]) || !HasText(node["op"]) ||
                node["jid"] is not JsonValue id || !id.TryGetValue<string>(out var actual) || actual != jid)
                return false;
            var parsed = node.Deserialize<OperationReceipt>(Json);
            if (parsed is null) return false;
            original = parsed;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        { return false; }
    }

    private static string AgentText(OperationReceipt receipt, bool knownJob)
    {
        if (receipt.Op == "terminal" || receipt.Op.StartsWith("terminal_", StringComparison.Ordinal))
            return TerminalResultProjection.Serialize(receipt);
        if (receipt.Ok && receipt.Op == "read_lines" && TryNumberedText(SerializeReceipt(receipt), out var numbered)) return numbered;
        return receipt.Ok ? Success(receipt, knownJob) : Failure(receipt);
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

    private static string Success(OperationReceipt receipt, bool knownJob)
    {
        if (receipt.State is "queued" or "running")
        {
            // Admission is not a result. Do not fabricate an exit code, result
            // body or log-availability fact before the retained job completes.
            var pending = new JsonObject { ["state"] = receipt.State };
            if (!knownJob) pending["jid"] = receipt.Jid;
            return pending.ToJsonString(Json);
        }
        var projected = new JsonObject();
        if (receipt.Data is not null)
        {
            var node = JsonSerializer.SerializeToNode(receipt.Data, Json) as JsonObject;
            if (node is not null)
            {
                // The result body is requested data. Its nulls, false/zero values,
                // empty strings and nested properties are not receipt defaults.
                projected = node.DeepClone().AsObject();
                projected.Remove("seq");
                // Queue inspection owns retained input details. Routine results
                // stay result-only; explicit detail/structured retains them.
                projected.Remove("submission");
                if (receipt.Op is "cp" or "mv")
                {
                    if (TryTransferText(receipt, projected, out var transferred)) return transferred;
                    foreach (var key in new[] { "src", "dst", "pol" }) projected.Remove(key);
                }
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
                    var requestedResult = ProcessOutputIsResult(receipt.Op) && projected.ContainsKey("out");
                    var includesResult = requestedResult && stdout is not null;
                    var clipped = includesResult && Encoding.UTF8.GetByteCount(stdout!) > MaxProcessResultUtf8Bytes;
                    var logAvailable = HasText(projected["err_tail"]) || HasText(projected["out"]) && (!includesResult || clipped);
                    // Process output remains an untrusted string. Never interpret it as
                    // a typed result or suppress every small value behind a log read.
                    if (includesResult) projected["out"] = BoundUtf8(stdout!, MaxProcessResultUtf8Bytes);
                    else if (!requestedResult) projected.Remove("out");
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
        if (!knownJob && projected["log_available"]?.ToJsonString() == "true") projected["jid"] = receipt.Jid;
        if (receipt.State is not ("done" or "completed"))
        {
            projected[projected.ContainsKey("state") ? "execution_state" : "state"] = receipt.State;
            if (!knownJob || receipt.Partial) projected["jid"] = receipt.Jid;
        }
        return projected.Count == 0 ? "Completed." : projected.ToJsonString(Json);
    }

    private static bool TryTransferText(OperationReceipt receipt, JsonObject data, out string text)
    {
        text = string.Empty;
        if (receipt.Partial || receipt.State is not ("done" or "completed") ||
            data["partial"]?.ToJsonString() == "true" || data.Any(pair => !TransferMetadata.Contains(pair.Key)) ||
            data["outcome_unknown"]?.ToJsonString() == "true" || data["recovery_required"]?.ToJsonString() == "true" ||
            data["byte_count_saturated"]?.ToJsonString() == "true") return false;
        if (receipt.Op == "cp" && data["source_deletion_issued"]?.ToJsonString() == "true") return false;
        if (receipt.Op == "mv" && (data["source_removed"]?.ToJsonString() != "true" ||
            data["verified"]?.ToJsonString() != "true")) return false;
        // Same-volume rename reports no copied files. Copy count is not a move
        // item count, so do not manufacture "Moved 0 files" or infer a tree size.
        if (receipt.Op == "mv") { text = "Moved."; return true; }
        if (!TryCount(data, "cp", out var count)) return false;
        if (data.ContainsKey("byte_progress_observed") && data["byte_progress_observed"]?.ToJsonString() != "true") return false;
        if (data.ContainsKey("skipped") && !TryCount(data, "skipped", out _)) return false;
        if (data.ContainsKey("del") && !TryCount(data, "del", out _)) return false;
        TryCount(data, "skipped", out var skipped);
        TryCount(data, "del", out var deleted);
        text = "Copied " + Number(count) + (count == 1 ? " file" : " files");
        if (skipped > 0) text += "; skipped " + Number(skipped) + (skipped == 1 ? " existing entry" : " existing entries");
        else if (data["byte_progress_observed"]?.ToJsonString() == "true" && TryCount(data, "bytes_transferred", out var bytes))
            text += " (" + Number(bytes) + (bytes == 1 ? " byte)" : " bytes)");
        if (deleted > 0) text += "; removed " + Number(deleted) + (deleted == 1 ? " existing entry" : " existing entries");
        text += ".";
        return true;
    }

    private static bool TryCount(JsonObject data, string name, out long count)
    {
        count = 0;
        return data[name] is JsonValue value && value.TryGetValue<long>(out count) && count >= 0;
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static bool? ScalarBoolean(object? value) => value switch
    { bool boolean => boolean, JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false, _ => null };

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
        if (receipt.Partial) builder.Append("\npartial=true");
        if (HistoryUnavailable(receipt))
        {
            if (receipt.Failure is null)
            {
                foreach (var key in new[] { "history_pruned", "history_discarded" })
                    if (ScalarBoolean(receipt.Data?.GetValueOrDefault(key)) is { } flag)
                        builder.Append('\n').Append(key).Append('=').Append(flag ? "true" : "false");
            }
            else if (ScalarBoolean(receipt.Data?.GetValueOrDefault("history_discarded")) == true)
                builder.Append("\nhistory_discarded=true");
        }
        if (receipt.Op is "cp" or "mv" && receipt.Data is not null)
        {
            var progress = new JsonObject();
            foreach (var key in new[] { "cp", "del", "skipped", "bytes_transferred", "byte_progress_observed",
                "byte_count_saturated", "destination_may_have_changed", "source_deletion_issued", "source_removed", "outcome_unknown", "recovery_required" })
            {
                if (!receipt.Data.TryGetValue(key, out var value) || value is null) continue;
                var number = JsonSerializer.SerializeToNode(value, Json);
                if (number is JsonValue scalar && (scalar.TryGetValue<bool>(out _) || scalar.TryGetValue<long>(out _)))
                    progress[key] = number;
            }
            if (progress.Count > 0) builder.Append("\nprogress=").Append(progress.ToJsonString(Json));
        }

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
        var pruned = HistoryUnavailable(receipt);
        var execution = receipt.Partial ? OperationExecutionState.Partial : OperationExecutionState.Unknown;
        var retry = pruned || receipt.Partial || receipt.Err is "recovery_required" or "timeout"
            ? OperationRetryDisposition.ReconcileFirst
            : OperationRetryDisposition.AfterCorrection;
        var hasLog = receipt.Data is not null &&
            (receipt.Data.TryGetValue("out", out var stdout) && stdout is string { Length: > 0 } ||
             receipt.Data.TryGetValue("err_tail", out var stderr) && stderr is string { Length: > 0 });
        return new OperationFailureEvidence(
            pruned ? "Historical failure details were pruned; the original outcome is unchanged." :
                "The typed operation failed with the reported stable code.",
            new OperationFailureFact(execution, "legacy-receipt"),
            new OperationFailureFact(retry, "legacy-receipt"),
            Next: pruned ? "Reconcile the retained original outcome and external state before considering a new operation." : receipt.Remedy,
            DetailAvailability: pruned ? "not_retained" : receipt.Data is { Count: > 0 } ? "structured" : "none",
            LogAvailability: !pruned && hasLog ? "available" : "not_retained");
    }

    private static bool HistoryUnavailable(OperationReceipt receipt) =>
        ScalarBoolean(receipt.Data?.GetValueOrDefault("history_pruned")) == true ||
        ScalarBoolean(receipt.Data?.GetValueOrDefault("history_discarded")) == true;

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
