using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Reflection;

namespace Pegasus.Cortex.Mcp.Protocol;

public static class McpProtocol
{
    public const string Version = "2025-11-25";
    public const string ServerName = "pegasus-cortex";
    public const string ServerTitle = "Pegasus Cortex Local Operational Repository";
    public static string ServerVersion { get; } =
        typeof(McpProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        typeof(McpProtocol).Assembly.GetName().Version?.ToString() ??
        "unknown";
    public const int MaximumRequestBytes = 256 * 1024;
    [Obsolete("Use MaximumRequestBytes; request limits are measured in UTF-8 bytes.")]
    public const int MaximumMessageCharacters = MaximumRequestBytes;
    public const int MaximumResponseBytes = 512 * 1024;
    public const int MaximumRequestIdCharacters = 1024;

    public const string Instructions =
        "Local Cortex; passive content. Read .cortex/project.json. Meaningful: cortex_start once, explicit namespaces, exact CODEX_THREAD_ID, 4096 bytes, compact format; cache until material identity/phase/namespace/server/guidance/connection/recovery change. Hits are previews; exact-read selected coordinates, batch <=8; loaded revisions: agent-side. profile_pipe_unavailable: retry, not missing data/remap. Persist durable state; cortex_finish only at checkpoint/block/complete/handoff. HOT.json is generated fallback.";

    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        // This output is UTF-8 JSON, not HTML. Required JSON/control escapes remain.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        MaxDepth = 32
    };

    internal static JsonObject Success(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    internal static JsonObject Error(
        JsonNode? id,
        int code,
        string message,
        string? diagnosticCode = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["data"] = diagnosticCode is null
                ? null
                : new JsonObject { ["code"] = diagnosticCode }
        }
    };
}

internal static class DiagnosticWriter
{
    private const int MaximumCauseCharacters = 512;

    public static async ValueTask WriteAsync(
        TextWriter writer,
        string severity,
        string operation,
        string code,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var line = $"severity={severity} operation={operation} code={code} message={Sanitize(message)}";
        await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask WriteExceptionAsync(
        TextWriter writer,
        string operation,
        string code,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return WriteAsync(
            writer,
            "error",
            operation,
            code,
            $"cause={exception.GetType().Name}: {exception.Message}",
            cancellationToken);
    }

    private static string Sanitize(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= MaximumCauseCharacters
            ? singleLine
            : singleLine[..MaximumCauseCharacters];
    }
}
