using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Protocol;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>
/// One public MCP host for Cortex continuity and the closed PAO execution
/// contract. The execution adapter owns no semantic state and delegates only
/// validated typed requests to the authenticated local pipe dispatcher.
/// </summary>
internal sealed class CortexUnifiedMcpApplication(
    ICortexMcpProtocolApplication cortex,
    IOperationDispatcher execution,
    CortexAssetResourcesV2? assets = null) : ICortexMcpProtocolApplication
{
    public JsonArray ListTools()
    {
        var tools = cortex.ListTools();
        tools.Add(PaoProgram.UnifiedMcpToolDefinition());
        return tools;
    }

    public ValueTask<JsonObject> CallToolAsync(
        string name,
        JsonElement? arguments,
        CancellationToken cancellationToken) =>
        CallWithContextAsync(name, arguments, Environment.CurrentDirectory, cancellationToken);

    internal ValueTask<JsonObject> CallWithContextAsync(string name, JsonElement? arguments,
        string? defaultCwd, CancellationToken cancellationToken) =>
        string.Equals(name, "pao", StringComparison.Ordinal)
            ? PaoProgram.CallUnifiedMcpToolAsync(
                arguments,
                execution,
                cancellationToken,
                defaultCwd: defaultCwd)
            : cortex.CallToolAsync(name, arguments, cancellationToken);

    public async ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken)
    {
        var result = await cortex.ListResourcesAsync(cancellationToken);
        if (assets is not null) foreach (var resource in await assets.ListResourcesAsync(cancellationToken)) result.Add(resource?.DeepClone());
        return result;
    }

    public async ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken)
    {
        var result = await cortex.ListResourceTemplatesAsync(cancellationToken);
        if (assets is not null) foreach (var template in assets.ListTemplates()) result.Add(template?.DeepClone());
        return result;
    }

    public async ValueTask<JsonObject?> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken) =>
        (assets is null ? null : await assets.ReadAsync(uri, cancellationToken)) ??
        await cortex.ReadResourceAsync(uri, cancellationToken);
}
