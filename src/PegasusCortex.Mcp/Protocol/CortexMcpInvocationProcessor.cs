using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pegasus.Cortex.Mcp.Protocol;

/// <summary>
/// Stateless method invocation after a transport has completed MCP initialization.
/// The caller supplies an already product-admitted application and retains
/// authentication, admission, bounded dispatch, cancellation correlation, framing,
/// and application lifetime. This facade owns none of those transport resources.
/// </summary>
public sealed class CortexMcpInvocationProcessor
{
    private const int MaximumMethodCharacters = 128;
    private readonly ICortexMcpProtocolApplication _application;
    private readonly TextWriter _diagnostics;

    public CortexMcpInvocationProcessor(ICortexMcpProtocolApplication application, TextWriter? diagnostics = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _diagnostics = diagnostics ?? TextWriter.Null;
    }

    /// <summary>Returns a fresh payload; no mutable initialization nodes are shared between clients.</summary>
    public static JsonObject CreateInitializeResult()
    {
        return new JsonObject
        {
            ["protocolVersion"] = McpProtocol.Version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
                // MCP experimental capability values are objects, including option-free extensions.
                ["experimental"] = new JsonObject { ["pegasus.cortex/multiplex"] = new JsonObject() }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = McpProtocol.ServerName,
                ["title"] = McpProtocol.ServerTitle,
                ["version"] = McpProtocol.ServerVersion
            },
            ["instructions"] = McpProtocol.Instructions
        };
    }

    /// <summary>
    /// Invokes one request after external transport initialization. IDs retain
    /// their original JSON string/number type. Keep parameters alive until this
    /// invocation settles. Notifications and targeted cancellation belong to the
    /// caller's transport; a second initialize request is rejected.
    /// </summary>
    public async ValueTask<JsonObject> InvokeAsync(JsonElement requestId, string method,
        JsonElement? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invalidId = ValidateRequestId(requestId, out var id);
        if (invalidId is not null) return invalidId;
        if (!ValidMethod(method))
            return McpProtocol.Error(id, -32600, "Invalid Request.", "cortex_mcp_method_invalid");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await InvokeValidatedAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return await RequestFailedAsync(id, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    // Shared with the native lifecycle processor so validation ordering and
    // existing protocol errors have one implementation.
    internal static JsonObject? ValidateRequestId(JsonElement id, out JsonNode? idNode)
    {
        idNode = null;
        if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            return McpProtocol.Error(null, -32600, "Invalid Request.", "cortex_mcp_id_invalid");
        if (id.ValueKind == JsonValueKind.String
                ? id.GetString()!.Length > McpProtocol.MaximumRequestIdCharacters
                : id.GetRawText().Length > McpProtocol.MaximumRequestIdCharacters)
            return McpProtocol.Error(null, -32600, "Invalid Request.", "cortex_mcp_id_too_large");
        idNode = JsonNode.Parse(id.GetRawText());
        return null;
    }

    internal static bool ValidMethod([NotNullWhen(true)] string? method) =>
        !string.IsNullOrWhiteSpace(method) && method.Length <= MaximumMethodCharacters;

    internal static JsonObject InitializeRepeatedError(JsonNode? id) =>
        McpProtocol.Error(id, -32600, "The server has already processed initialization.",
            "cortex_mcp_initialize_repeated");

    // The native lifecycle processor already validated its envelope and owns
    // the exception boundary. Keeping this core uncaught avoids double logging
    // or translating the same domain failure twice.
    internal async ValueTask<JsonObject> InvokeValidatedAsync(JsonNode? id, string method,
        JsonElement? parameters, CancellationToken cancellationToken)
    {
        return method switch
        {
            "initialize" => InitializeRepeatedError(id),
            "ping" => McpProtocol.Success(id, new JsonObject()),
            "tools/list" => ListTools(id, parameters),
            "tools/call" => await CallToolAsync(id, parameters, cancellationToken).ConfigureAwait(false),
            "resources/list" => await ListResourcesAsync(id, parameters, cancellationToken).ConfigureAwait(false),
            "resources/templates/list" => await ListResourceTemplatesAsync(id, parameters, cancellationToken).ConfigureAwait(false),
            "resources/read" => await ReadResourceAsync(id, parameters, cancellationToken).ConfigureAwait(false),
            _ => McpProtocol.Error(id, -32601, "Method not found.", "cortex_mcp_method_not_found")
        };
    }

    internal async ValueTask<JsonObject> RequestFailedAsync(JsonNode? id, Exception exception,
        CancellationToken cancellationToken)
    {
        await DiagnosticWriter.WriteExceptionAsync(_diagnostics, "mcp.request", "cortex_mcp_request_failed",
            exception, cancellationToken).ConfigureAwait(false);
        var response = McpProtocol.Error(id, -32603,
            "Request outcome is unknown; do not automatically replay.", "cortex_mcp_outcome_unknown");
        response["error"]!["data"]!["outcome"] = "Unknown";
        return response;
    }

    private JsonObject ListTools(JsonNode? id, JsonElement? parameters)
    {
        if (!ValidListParameters(parameters))
            return McpProtocol.Error(id, -32602, "Invalid tools/list parameters.", "cortex_mcp_list_params_invalid");
        return McpProtocol.Success(id, new JsonObject { ["tools"] = _application.ListTools() });
    }

    private async ValueTask<JsonObject> CallToolAsync(
        JsonNode? id,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(parameters.Value, "name", "arguments", "_meta") ||
            !parameters.Value.TryGetProperty("name", out var nameProperty) ||
            nameProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameProperty.GetString()) ||
            nameProperty.GetString()!.Length > MaximumMethodCharacters)
        {
            return McpProtocol.Error(id, -32602, "Invalid tools/call parameters.", "cortex_mcp_tool_params_invalid");
        }

        JsonElement? arguments = parameters.Value.TryGetProperty("arguments", out var argumentProperty)
            ? argumentProperty
            : null;
        var result = await _application.CallToolAsync(
            nameProperty.GetString()!,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return McpProtocol.Success(id, result);
    }

    private async ValueTask<JsonObject> ListResourcesAsync(
        JsonNode? id,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (!ValidListParameters(parameters))
            return McpProtocol.Error(id, -32602, "Invalid resources/list parameters.", "cortex_mcp_list_params_invalid");
        var resources = await _application.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
        return McpProtocol.Success(id, new JsonObject { ["resources"] = resources });
    }

    private async ValueTask<JsonObject> ListResourceTemplatesAsync(
        JsonNode? id,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (!ValidListParameters(parameters))
        {
            return McpProtocol.Error(
                id,
                -32602,
                "Invalid resources/templates/list parameters.",
                "cortex_mcp_list_params_invalid");
        }

        var templates = await _application.ListResourceTemplatesAsync(cancellationToken).ConfigureAwait(false);
        return McpProtocol.Success(id, new JsonObject { ["resourceTemplates"] = templates });
    }

    private async ValueTask<JsonObject> ReadResourceAsync(
        JsonNode? id,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(parameters.Value, "uri", "_meta") ||
            !parameters.Value.TryGetProperty("uri", out var uriProperty) ||
            uriProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(uriProperty.GetString()) ||
            uriProperty.GetString()!.Length > 4096)
        {
            return McpProtocol.Error(id, -32602, "Invalid resources/read parameters.", "cortex_mcp_resource_params_invalid");
        }

        var uri = uriProperty.GetString()!;
        var result = await _application.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return McpProtocol.Error(id, -32002, "Resource not found.", "cortex_resource_not_found");
        return McpProtocol.Success(id, result);
    }

    private static bool ValidListParameters(JsonElement? parameters)
    {
        if (parameters is null)
            return true;
        if (parameters.Value.ValueKind != JsonValueKind.Object ||
            !OnlyProperties(parameters.Value, "cursor", "_meta"))
        {
            return false;
        }

        return !parameters.Value.TryGetProperty("cursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null;
    }

    private static bool OnlyProperties(JsonElement value, params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => allowed.Contains(property.Name));
    }
}

