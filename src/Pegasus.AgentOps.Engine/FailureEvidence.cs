using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal static partial class FailureEvidence
{
    private const int MaxDiagnosticCount = 4;
    private const int MaxDiagnosticUtf8Bytes = 512;

    [GeneratedRegex("^(?<source>.+?)\\((?<line>[0-9]+)(?:,(?<column>[0-9]+))?\\)\\s*:\\s*(?:fatal\\s+)?(?:error|warning)\\s*(?<code>[A-Za-z]+[0-9]+)?\\s*:?\\s*(?<message>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerDiagnosticPattern();

    [GeneratedRegex("(?i)\\b(api[_-]?key|token|password|secret)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex("(?i)\\bbearer\\s+[^\\s,;]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)(?:[A-Z]:\\\\|\\\\\\\\)[^\\r\\n]+")]
    private static partial Regex AbsolutePathPattern();

    // Static reasons only: never echo target paths, hashes, literals or bodies.
    // The job may be admitted, but these guards run before target publication.
    public static OperationFailureEvidence TextGuard(string reason, string field)
    {
        var message = reason switch
        {
            "preimage_missing" => "The expected preimage target is missing; no target bytes were written.",
            "preimage_invalid" => "The expected SHA-256 must contain 64 hexadecimal digits; no target bytes were written.",
            "preimage_changed" => "The target preimage changed; no target bytes were written.",
            "literal_not_found" => "The exact old literal was not found; no target bytes were written.",
            "literal_not_unique" => "The exact old literal is not unique as requested; no target bytes were written.",
            "literal_count_mismatch" => "The literal occurrence count differs from the requested count; no target bytes were written.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        return new OperationFailureEvidence(message,
            new OperationFailureFact(OperationExecutionState.NotDispatched, "text-publication-guard"),
            new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "text-publication-guard"),
            Field: new OperationFailureField(field),
            Diagnostics: [new OperationFailureDiagnostic(message, Code: reason)],
            Next: reason == "preimage_invalid" ? "Supply the documented SHA-256 format before retrying."
                : "Read the exact target once, correct the indicated precondition, and retry only if still applicable.");
    }

    // Workspace resolution precedes tool preparation and application launch.
    // Keep the diagnostic static: a rejected ws may contain private path content.
    public static OperationFailureEvidence UnknownApplicationWorkspaceAlias() => new(
        "Unknown workspace alias in ws. app_start requires a registered alias, not an absolute path.",
        new OperationFailureFact(OperationExecutionState.NotDispatched, "application-workspace-resolution"),
        new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "application-workspace-resolution"),
        Field: new OperationFailureField("ws"),
        Diagnostics: [new OperationFailureDiagnostic("The supplied ws does not resolve to a registered workspace alias.", Code: "workspace_alias_unknown")],
        Next: "Use ctx_ls (CLI: pao ctx ls) to find the registered alias and supply it in ws; put the absolute working directory in cwd.");

    // These counts are captured under the manager's admission gate before its
    // launch callback. Report only the exhausted resource, never application data.
    public static OperationFailureEvidence ApplicationCapacity(string resource, int actual, int maximum)
    {
        var (message, unit, next) = resource switch
        {
            "retained" => ("The user host reached its retained application-generation limit; no application was launched.",
                "generations", "Inspect app_inventory. If an explicitly authorized exact aid is fully retired and releasable, submit app_release with its current epoch and a fresh RID; app_stop does not reclaim retained metadata. Preserve live or unknown generations."),
            "active" => ("The user host reached its active application limit; no application was launched.",
                "applications", "Wait for an owned application to finish, or stop only an explicitly authorized exact aid, before submitting a fresh request."),
            _ => throw new ArgumentOutOfRangeException(nameof(resource))
        };
        return new OperationFailureEvidence(message,
            new OperationFailureFact(OperationExecutionState.NotDispatched, "application-capacity-admission"),
            new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "application-capacity-admission"),
            Field: new OperationFailureField(resource == "retained" ? "retained_generations" : "active_applications"),
            Bound: new OperationFailureBound(actual, maximum, unit),
            Diagnostics: [new OperationFailureDiagnostic(message, Code: "application_" + resource + "_capacity")],
            Next: next);
    }

    public static OperationFailureEvidence Admission(AgentOpsException exception, string source) =>
        exception.Failure ?? new OperationFailureEvidence(
            MessageFor(exception.Code),
            new OperationFailureFact(OperationExecutionState.NotDispatched, source),
            new OperationFailureFact(OperationRetryDisposition.AfterCorrection, source),
            Next: exception.Code == "not_found" ? "Correct the exact admitted target and retry." : "Correct the rejected request and retry.",
            DetailAvailability: "none",
            LogAvailability: "not_retained");

    public static OperationFailureEvidence ToolExecution(ToolExecutionException exception)
    {
        var (diagnostics, omitted) = Diagnostics(exception.Result.Stderr, exception.Result.Stdout, exception.ExitCode);
        var retry = exception.Code is "timeout" or "recovery_required"
            ? OperationRetryDisposition.ReconcileFirst
            : OperationRetryDisposition.AfterCorrection;
        var log = string.IsNullOrEmpty(exception.Result.Stdout) && string.IsNullOrEmpty(exception.Result.Stderr)
            ? "empty"
            : "available";
        return new OperationFailureEvidence(
            MessageFor(exception.Code),
            new OperationFailureFact(OperationExecutionState.Started, "registered-process-runner"),
            new OperationFailureFact(retry, "registered-process-runner"),
            Diagnostics: diagnostics,
            DiagnosticsOmitted: omitted || exception.StdoutTruncated || exception.StderrTruncated,
            Next: retry == OperationRetryDisposition.ReconcileFirst
                ? "Inspect the retained receipt and job log, reconcile external state, then decide whether replay is safe."
                : "Correct the reported process failure and retry the typed operation.",
            DetailAvailability: "structured",
            LogAvailability: log);
    }

    public static OperationFailureEvidence Generic(string code, bool partial, string source,
        string detailAvailability = "none", string logAvailability = "not_retained")
    {
        var execution = partial ? OperationExecutionState.Partial : OperationExecutionState.Unknown;
        var retry = partial || code is "recovery_required" or "timeout"
            ? OperationRetryDisposition.ReconcileFirst
            : OperationRetryDisposition.AfterCorrection;
        return new OperationFailureEvidence(
            MessageFor(code),
            new OperationFailureFact(execution, source),
            new OperationFailureFact(retry, source),
            Next: retry == OperationRetryDisposition.ReconcileFirst
                ? "Inspect the retained receipt and reconcile observed state before retrying."
                : "Correct the stable failure and retry only if the operation remains applicable.",
            DetailAvailability: detailAvailability,
            LogAvailability: logAvailability);
    }

    private static (IReadOnlyList<OperationFailureDiagnostic> Items, bool Omitted) Diagnostics(
        string stderr, string stdout, int exitCode)
    {
        var items = new List<OperationFailureDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var omitted = false;
        foreach (var line in Lines(stderr).Concat(Lines(stdout)))
        {
            var match = CompilerDiagnosticPattern().Match(line);
            if (!match.Success) continue;
            var source = Path.GetFileName(match.Groups["source"].Value.Trim());
            var message = Sanitize(match.Groups["message"].Value);
            var code = match.Groups["code"].Success ? match.Groups["code"].Value : null;
            var key = string.Join('|', source, match.Groups["line"].Value, match.Groups["column"].Value, code, message);
            if (!seen.Add(key)) continue;
            if (items.Count == MaxDiagnosticCount) { omitted = true; continue; }
            items.Add(new OperationFailureDiagnostic(
                message,
                string.IsNullOrWhiteSpace(source) ? null : source,
                int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                match.Groups["column"].Success ? int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture) : null,
                code));
        }
        if (items.Count == 0)
            items.Add(new OperationFailureDiagnostic($"External process exited with code {exitCode}.", Code: "process_exit"));
        return (items, omitted);
    }

    private static IEnumerable<string> Lines(string value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Sanitize(string value)
    {
        var sanitized = SecretAssignmentPattern().Replace(value, "$1=<redacted>");
        sanitized = BearerPattern().Replace(sanitized, "Bearer <redacted>");
        sanitized = AbsolutePathPattern().Replace(sanitized, "<path>");
        return BoundUtf8(sanitized.Trim(), MaxDiagnosticUtf8Bytes);
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

    private static string MessageFor(string code) => code switch
    {
        "invalid" => "The typed request failed validation.",
        "not_found" => "The exact admitted target was not found.",
        "external_failed" => "The registered process started but returned a disallowed exit code.",
        "timeout" => "The registered process started but exceeded its bounded timeout.",
        "recovery_required" => "Execution outcome requires reconciliation before replay.",
        "access" or "auth" => "The operation was not authorized in the required execution context.",
        _ => "The typed operation failed with the reported stable code."
    };
}

internal static class JobLogProjection
{
    public static IReadOnlyDictionary<string, object?> Create(OperationReceipt receipt, int maximumUtf8Bytes)
    {
        var source = receipt.Data ?? new Dictionary<string, object?>();
        var data = new Dictionary<string, object?> { ["jid"] = receipt.Jid, ["state"] = receipt.State };
        if (AgentOpsEngine.IsHistoryPruned(receipt))
        {
            data["history_pruned"] = true;
            data["log_available"] = false;
            return data;
        }
        foreach (var key in new[] { "exit", "out_truncated", "err_truncated" })
            if (source.TryGetValue(key, out var value)) data[key] = value;
        foreach (var key in new[] { "out", "err_tail" })
        {
            if (!source.TryGetValue(key, out var value) || !TryText(value, out var text)) continue;
            var bounded = ToolRecipeValidation.BoundUtf8(text, maximumUtf8Bytes);
            data[key] = bounded.Text;
            if (bounded.Truncated) data[key == "out" ? "out_tail_truncated" : "err_tail_truncated"] = true;
        }
        return data;
    }

    private static bool TryText(object? value, out string text)
    {
        if (value is string direct) { text = direct; return true; }
        if (value is JsonElement { ValueKind: JsonValueKind.String } json) { text = json.GetString()!; return true; }
        text = string.Empty;
        return false;
    }
}
