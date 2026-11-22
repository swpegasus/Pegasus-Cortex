using System.Text.Json;

namespace Pegasus.Cortex.Product;

/// <summary>Public client setup text. The helper obtains its secret directly from the user's vault.</summary>
public static class CortexMcpClientConfiguration
{
    public static string Codex(string launcherPath, string connectionId,
        string endpoint = "https://localhost:47831/mcp/cortex")
    {
        if (!CortexMcpConnections.IsId(connectionId)) throw new InvalidDataException("Invalid connection ID.");
        if (!Path.IsPathFullyQualified(launcherPath) ||
            !string.Equals(Path.GetFullPath(launcherPath), launcherPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(launcherPath), "PegasusCortex.exe", StringComparison.OrdinalIgnoreCase) ||
            launcherPath.Any(c => char.IsControl(c) || c == 0x60 || "\"'%!$&|<>^".Contains(c)))
            throw new InvalidDataException("The client helper requires an explicit safe Cortex launcher path.");
        if (endpoint.Length > 512 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/mcp/cortex")
            throw new InvalidDataException("The client requires an explicit Cortex HTTPS endpoint.");
        var helper = "\"" + launcherPath + "\" mcp headers --connection " + connectionId;
        return "[mcp_servers.Cortex]\nurl = " + JsonSerializer.Serialize(uri.AbsoluteUri) +
            "\nenabled = true\nhttp_headers_helper = " + JsonSerializer.Serialize(helper) + "\n";
    }
}
