using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>Explicit owner-local enrollment; keys never appear in CLI output or arguments.</summary>
public static class CortexConnectionCommands
{
    public static object DescribeEndpoint() => CortexResidentRuntime.DescribeEndpoint();
    public static (int ExitCode, string Json) Run(string[] args)
    {
        try
        {
            if (args is ["connections", "describe"])
                return (0, JsonSerializer.Serialize(DescribeEndpoint(), CortexClientRegistry.Json));
            var layout = CortexCurrentUserLayout.Resolve();
            layout.EnsureCreated();
            var directory = Path.Combine(layout.ConfigDirectory, "connections");
            var registry = new CortexClientRegistry(directory, LocalIdentity.Current());
            if (args is ["connections", "list"]) return (0, JsonSerializer.Serialize(new { ok = true, clients = registry.List() }, CortexClientRegistry.Json));
            if (args is ["connections", "rotate-codex"])
            {
                var product = CortexProductLayout.ResolveCurrent();
                var credential = registry.Register("codex", Path.Combine(product.InstallRoot, "bin", "semantics", "PegasusCortex.exe"),
                    "mcp", new([], [], true), DateTimeOffset.UtcNow.AddYears(1), rotate: true, managedAdapter: true);
                CortexClientRegistry.SaveProtectedCredential(Path.Combine(directory, "codex.key"), credential, replace: true);
                return (0, JsonSerializer.Serialize(new { ok = true, clientId = "codex", credential.KeyId, state = "rotated" }, CortexClientRegistry.Json));
            }
            if (args is ["connections", "revoke", var client])
            { registry.Revoke(client); return (0, JsonSerializer.Serialize(new { ok = true, clientId = client, state = "revoked" }, CortexClientRegistry.Json)); }
            if (args is ["connections", var action, "--client", var id, "--application", var application,
                "--transport", var transport, "--defaults-file", var defaultsFile] && action is "register" or "rotate")
            {
                if (id == "codex" || !File.Exists(application)) throw new InvalidDataException("client_registration_invalid");
                CortexProductLayout.RejectExistingReparseComponents(defaultsFile);
                if (new FileInfo(defaultsFile).Length is < 1 or > 16384) throw new InvalidDataException("client_defaults_invalid");
                var defaults = JsonSerializer.Deserialize<CortexClientDefaults>(File.ReadAllBytes(defaultsFile), CortexClientRegistry.Json)
                    ?? throw new InvalidDataException("client_defaults_invalid");
                var expires = DateTimeOffset.UtcNow.AddDays(90);
                var credential = registry.Register(id, application, transport, defaults, expires, rotate: action == "rotate");
                var keyFile = Path.Combine(directory, id + ".key");
                CortexClientRegistry.SaveProtectedCredential(keyFile, credential, replace: action == "rotate");
                return (0, JsonSerializer.Serialize(new { ok = true, clientId = id, credential.KeyId, transport,
                    expiresUtc = expires, credentialFile = keyFile, protection = "windows-current-user-dpapi" }, CortexClientRegistry.Json));
            }
            return (2, "{\"ok\":false,\"error\":{\"code\":\"connections_arguments_invalid\"}}");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
        { return (3, "{\"ok\":false,\"error\":{\"code\":\"connections_unavailable\"}}"); }
    }
}
