using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.Protocol;

namespace Pegasus.Cortex.Mcp.V2;

internal static class CortexUsageMcpObserver
{
    public static async ValueTask RecordAsync(CortexUsageRecorder usage, string name, JsonElement? arguments,
        JsonObject? result, long started)
    {
        try { await RecordCoreAsync(usage, name, arguments, result, started).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Diagnostics extraction must not mask a committed operation or cancellation.
            usage.ReportLostSample();
        }
    }

    private static ValueTask RecordCoreAsync(CortexUsageRecorder usage, string name, JsonElement? arguments,
        JsonObject? result, long started)
    {
        var operation = CortexV2McpContract.ToolNames.Contains(name, StringComparer.Ordinal) ? name[7..] : "unknown";
        var failed = result is null || result["isError"]?.GetValue<bool>() != false;
        var structured = result?["structuredContent"] as JsonObject;
        var spaces = failed ? [] : ((structured?["ns"] ?? structured?["effectiveNamespaces"]) as JsonArray)?
            .Select(x => x!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var items = structured?["items"] as JsonArray;
        long count = failed ? 0 : items?.Count ?? (structured?["record"] is not null ? 1 : 0);
        if (!failed && name is "cortex_task" or "cortex_finish" && structured?["task"] is not null) count++;
        if (name == "cortex_start") count += structured?["task"] is not null || structured?["currentTask"] is not null ? 1 : 0;
        var batch = name == "cortex_read" && arguments is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("records", out _);
        if (batch) operation = "batch-read";
        string? taskId = null;
        if (name is "cortex_start" or "cortex_task" or "cortex_finish" && arguments is { ValueKind: JsonValueKind.Object } args &&
            args.TryGetProperty("taskId", out var id) && id.ValueKind == JsonValueKind.String)
            taskId = CortexUsageRecorder.CanonicalTaskId(id.GetString());
        return usage.RecordAsync(operation, spaces, failed, count,
            name == "cortex_read" ? count : 0,
            result is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(result, McpProtocol.JsonOptions).Length, started, taskId);
    }
}
