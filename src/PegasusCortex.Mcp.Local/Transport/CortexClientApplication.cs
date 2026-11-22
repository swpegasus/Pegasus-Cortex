using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.AgentOps.Engine;

namespace Pegasus.Cortex.Mcp.Local.Transport;

/// <summary>Rechecks connection identity; namespace selections are not permissions.</summary>
internal sealed class CortexClientApplication : ICortexMcpProtocolApplication
{
    private readonly record struct ClientPolicy(bool Pao, bool ManagedAdapter);
    private readonly ICortexMcpProtocolApplication inner;
    private readonly Func<ClientPolicy> requirePolicy;
    private readonly string? callerCwd;
    private readonly UserHostAdmission? admission;

    internal CortexClientApplication(ICortexMcpProtocolApplication inner,
        CortexClientRegistry registry, CortexClientLease lease, string? callerCwd = null,
        UserHostAdmission? admission = null)
        : this(inner, () =>
        {
            var entry = registry.RequireActive(lease);
            return new ClientPolicy(entry.Defaults.Pao, entry.ManagedAdapter);
        }, callerCwd, admission) { }

    private CortexClientApplication(ICortexMcpProtocolApplication inner,
        Func<ClientPolicy> requirePolicy, string? callerCwd, UserHostAdmission? admission)
    {
        this.inner = inner;
        this.requirePolicy = requirePolicy;
        this.callerCwd = callerCwd;
        this.admission = admission;
    }

    // Only the enabled endpoint inside the already-admitted ordinary owner Host
    // uses this authority. HTTP task/session labels never create an identity or
    // execution grant. Private-pipe clients retain their registered-key checks;
    // privileged PAO still uses the existing independent execution authorizer.
    internal static CortexClientApplication ForOwnerEndpoint(
        ICortexMcpProtocolApplication application, UserHostAdmission admission, Func<bool>? connectionIsCurrent=null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(admission);
        return new(application, () =>
        {
            if(connectionIsCurrent is not null&&!connectionIsCurrent())throw new UnauthorizedAccessException("connection_revoked");
            return new ClientPolicy(true,true);
        }, null, admission);
    }

    public JsonArray ListTools()
    {
        using var entryLease = RequireAdmission();
        var policy = requirePolicy();
        return new JsonArray(inner.ListTools().OfType<JsonObject>()
            .Where(tool => policy.Pao || tool["name"]?.GetValue<string>() != "pao")
            .Select(tool => (JsonNode)tool.DeepClone()).ToArray());
    }

    public async ValueTask<JsonObject> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        using var entryLease = admission?.TryEnter();
        if (admission is not null && entryLease is null)
            return Error("host_stopping", "Cortex is stopping; no operation was dispatched.");
        ClientPolicy policy;
        try { policy = requirePolicy(); }
        catch (UnauthorizedAccessException) { return Error("client_auth_failed", "Client registration is revoked or expired."); }
        if (name == "pao")
        {
            // Execution authorization is independent of library access. Native
            // consumers retain their separate authenticated System broker route.
            if (!policy.Pao || !policy.ManagedAdapter && arguments is { ValueKind: JsonValueKind.Object } values &&
                values.EnumerateObject().Any(property =>
                    property.Name is "root" or "root_code" or "credential" or "pathRoots" ||
                    property.Name == "op" && property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is "root" or "queue" or "admin_admit" or "admin_run" or "admin_stat" or "admin_revoke" or "admin_reconcile" or "admin_begin" or "admin_end" or "admin_interval" or "admin_ack" or "service_status" or "service_start" or "service_stop" or "service_restart"))
                return Error("execution_authorization_required", "Use the separately authorized execution route.");
        }
        // Core owns explicit namespace selection, schema/integrity validation
        // and loaded-profile availability. Do not duplicate them as client ACLs.
        return inner is CortexUnifiedMcpApplication unified
            ? await unified.CallWithContextAsync(name, arguments, callerCwd, cancellationToken)
            : await inner.CallToolAsync(name, arguments, cancellationToken);
    }

    public async ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken)
    { using var entryLease = RequireAdmission(); _ = requirePolicy(); return await inner.ListResourcesAsync(cancellationToken); }
    public async ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken)
    { using var entryLease = RequireAdmission(); _ = requirePolicy(); return await inner.ListResourceTemplatesAsync(cancellationToken); }
    public async ValueTask<JsonObject?> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    { using var entryLease = RequireAdmission(); _ = requirePolicy(); return await inner.ReadResourceAsync(uri, cancellationToken); }

    private IDisposable? RequireAdmission()
    {
        var entryLease = admission?.TryEnter();
        if (admission is not null && entryLease is null) throw new IOException("host_stopping");
        return entryLease;
    }

    private static JsonObject Error(string code, string message) => new()
    {
        ["isError"] = true,
        ["structuredContent"] = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } },
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = code })
    };
}
