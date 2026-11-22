using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pegasus.Cortex.Mcp.Protocol;

/// <summary>
/// Provider-neutral application surface consumed by the bounded STDIO host.
/// Implementations choose their own domain and persistence composition while
/// the protocol host owns MCP lifecycle, framing, limits, and diagnostics.
/// </summary>
public interface ICortexMcpProtocolApplication
{
    JsonArray ListTools();

    ValueTask<JsonObject> CallToolAsync(
        string name,
        JsonElement? arguments,
        CancellationToken cancellationToken);

    ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken);

    ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken);

    ValueTask<JsonObject?> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken);
}
