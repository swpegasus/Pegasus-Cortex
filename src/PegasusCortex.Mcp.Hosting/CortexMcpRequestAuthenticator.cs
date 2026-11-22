using Pegasus.Cortex.Product;
using Pegasus.SharedMcp;

namespace Pegasus.Cortex.Mcp.Hosting;

/// <summary>Network admission is followed by a separately approved, persistent client credential.</summary>
internal sealed class CortexMcpRequestAuthenticator : IMcpRequestAuthenticator
{
    private readonly Func<CortexMcpConnectionStore?> readApprovals;

    internal CortexMcpRequestAuthenticator(string installRoot)
        : this(() => CortexMcpConnections.ReadForInstallRoot(installRoot)) { }

    // Fixtures substitute only the protected public-metadata read. Credential
    // parsing, digest comparison and admission still use the product contract.
    internal CortexMcpRequestAuthenticator(Func<CortexMcpConnectionStore?> readApprovals) =>
        this.readApprovals = readApprovals ?? throw new ArgumentNullException(nameof(readApprovals));

    public ValueTask<McpAuthenticationResult> AuthenticateAsync(McpAuthenticationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        request.Headers.TryGetValue("Authorization", out var values);
        var header = values is { Count: 1 } ? values[0] : null;
        var authority = CortexMcpConnections.Authenticate(readApprovals(), header);
        return ValueTask.FromResult(authority is null
            ? McpAuthenticationResult.Reject(401, "Bearer realm=\"Pegasus Cortex\"")
            : McpAuthenticationResult.Authenticated(new(authority.Authority, authority.ConnectionId, authority.CredentialGeneration)));
    }
}
