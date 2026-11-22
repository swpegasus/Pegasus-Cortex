using System.Globalization;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// A bounded display of admitted typed input, never a runnable shell command or
// a reconstruction of the tool definition/executable that ran in the past.
internal static class JobSubmissionProjection
{
    internal const int MaximumUtf8Bytes = 4096;
    private const int MaximumOmittedFields = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SelectorFamilies = new(StringComparer.Ordinal)
    {
        "ctx", "path", "lines", "disk", "encoding", "wr", "zip", "7z", "git", "dotnet", "proc", "service", "net", "job",
        "feedback", "tool", "recipe", "nsis"
    };
    private static readonly HashSet<string> Families = new(StringComparer.Ordinal)
    {
        "ctx", "stat", "ls", "find", "search", "slice", "diff", "count", "path", "lines", "disk", "rd", "read_lines",
        "encoding", "wr", "edit", "patch", "mkdir", "cp", "mv", "rm", "zip", "sha", "run", "pwsh", "git", "dotnet",
        "tool", "recipe", "do", "7z", "proc", "service", "net", "job", "feedback", "nsis", "diag"
    };
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "ctx", "ctx_add", "ctx_ls", "ctx_cur", "stat", "ls", "find", "search", "slice", "head", "tail", "diff", "count",
        "path", "realpath", "basename", "dirname", "lines", "sort", "uniq", "disk", "du", "df", "rd", "read_lines",
        "encoding", "encoding_inspect", "encoding_convert", "wr", "replace", "edit", "patch", "mkdir", "cp", "mv", "rm",
        "zip", "zip_create", "zip_list", "zip_test", "zip_extract", "sha", "run", "pwsh", "git", "git_status", "git_diff",
        "git_log", "git_show", "git_rev_parse", "dotnet", "dotnet_build", "dotnet_test", "dotnet_restore", "dotnet_clean",
        "dotnet_pack", "dotnet_publish", "tool", "tool_ls", "tool_add", "tool_rm", "tool_probe", "recipe", "recipe_ls",
        "recipe_add", "recipe_rm", "recipe_get", "do", "7z", "7z_list", "7z_test", "7z_extract", "proc", "proc_ls",
        "proc_inspect", "proc_wait", "proc_stop", "proc_tree", "proc_threads", "service", "service_status", "service_start",
        "service_stop", "service_restart", "net", "net_list", "job", "job_ls", "job_stat", "job_log", "job_wait", "job_cancel",
        "job_retry", "job_prune", "feedback", "feedback_submit", "feedback_ls", "feedback_get", "feedback_set", "feedback_export",
        "nsis", "nsis_status", "nsis_validate", "nsis_build", "diag"
    };

    internal static OperationReceipt Attach(OperationReceipt receipt, OperationRequest request)
    {
        if (AgentOpsEngine.IsHistoryPruned(receipt) || Create(request) is not { } submission) return receipt;
        return receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
            { ["submission"] = submission } };
    }

    internal static JsonElement? Create(OperationRequest request)
    {
        // These are the ordinary families only. Registry/certificate/private UI,
        // applications, terminals, administrator and credential routes retain their
        // independent receipt policies, including complete omission of this view.
        // Service bodies and config preimages use their own metadata-only receipt policy.
        if (request.Op == "windows_service" || request.Root is not null || request.System || !Families.Contains(request.Op)) return null;
        var omitted = new SortedSet<string>(StringComparer.Ordinal);
        var arguments = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var truncated = false;
        void Omit(string field)
        {
            omitted.Add(SafeLabel(field) ? field : "unknown");
            if (omitted.Count <= MaximumOmittedFields) return;
            omitted.Remove(omitted.Max!);
            truncated = true;
        }
        void Add(string key, string value)
        {
            if (IsSafeValue(key, value)) arguments[key] = value;
            else { Omit(key); if (value.Length > 240) truncated = true; }
        }

        var operation = OperationNames.Public(request);
        if (!Operations.Contains(operation))
        {
            operation = request.Op;
            Omit(request.Op == "slice" ? "mode" : "op");
        }
        foreach (var field in request.Arg)
        {
            if (field.Key == OperationRequest.BodyOmittedKey) { Omit("body"); continue; }
            if (field.Key == "op")
            {
                // The closed operation name above already renders the selector.
                if (!SelectorFamilies.Contains(request.Op) || !Operations.Contains(OperationNames.Public(request))) Omit("op");
                continue;
            }
            Add(field.Key, field.Value);
        }
        if (request.Cwd is not null) Add("cwd", request.Cwd);
        if (request.Ws is not null) Add("ws", request.Ws);
        if (request.Body is not null) Omit("body");

        JsonElement Build() => JsonSerializer.SerializeToElement(new
        {
            schema = "pegasus.cortex.job-submission/1",
            state = omitted.Count == 0 && !truncated ? "available" : "redacted",
            operation,
            command = "pao " + operation,
            toolReference = arguments.GetValueOrDefault("cfg"),
            arguments = arguments.Select(field => field.Key + "=" + field.Value).ToArray(),
            omitted = omitted.ToArray(),
            truncated
        }, Json);

        var projection = Build();
        // Drop whole typed fields, never invent a shortened path or argument.
        // Both collections are bounded by the fixed allowlist and label limits.
        while (JsonSerializer.SerializeToUtf8Bytes(projection, Json).Length > MaximumUtf8Bytes)
        {
            var last = arguments.Last();
            arguments.Remove(last.Key);
            Omit(last.Key);
            truncated = true;
            projection = Build();
        }
        return projection;
    }

    private static bool IsSafeValue(string key, string value)
    {
        // Never admit arbitrary process arguments, environment/stdin, bodies,
        // search patterns, replacement text, feedback or other free-form text.
        if (value.Length is < 1 or > 240 || value.Any(char.IsControl) || !WellFormed(value)) return false;
        return key switch
        {
            "src" or "dst" or "cwd" or "workspace_root" => Path.IsPathFullyQualified(value),
            "cfg" => Path.IsPathFullyQualified(value) || Identifier(value, 64),
            "ws" => Identifier(value, 64),
            "line" or "count" or "max" or "after" or "pid" or "port" or "tmo" =>
                long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0,
            "recur" or "ignore_case" or "permanent" or "wait" => value is "true" or "false",
            "fmt" => value is "literal" or "regex" or "png" or "text" or "base64",
            "type" => value is "any" or "file" or "dir",
            "pol" => value is "fail" or "replace" or "skip" or "clean",
            "expect" => value is "running" or "stopped",
            "mode" => value is "head" or "tail",
            "enc" => value is "utf8" or "utf8bom" or "utf16le" or "utf16be" or "ascii" or "utf-8" or "utf-16" or "unicode",
            "sha" => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'),
            "start" => value.Length <= 32 && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks > 0,
            "service" => value == "PegasusCerebral",
            _ => false
        };
    }

    private static bool Identifier(string value, int maximum) => value.Length <= maximum &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static bool SafeLabel(string value) => value.Length is >= 1 and <= 32 &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-');
    private static bool WellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || ++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}
