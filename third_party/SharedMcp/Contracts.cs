using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace Pegasus.SharedMcp;

/// <summary>Labels are correlation only. Identity comes exclusively from the host's authenticator, never request metadata.</summary>
public sealed record McpRequestContext(string Product, string SessionId, int StreamId, string? TaskId, string RequestId)
{
    /// <summary>Authenticated connection identity, not an operation grant or an OS execution identity.</summary>
    public McpAuthenticatedIdentity? Identity { get; init; }
}

/// <summary>The product owns its catalog, validation, execution, and privilege boundary.</summary>
public interface IMcpDispatcher : IAsyncDisposable
{
    /// <returns>A legacy MCP InitializeResult object, without the JSON-RPC envelope.</returns>
    Task<JsonObject> InitializeAsync(CancellationToken cancellationToken);
    /// <returns>A JSON-RPC response envelope using the request's original typed ID.</returns>
    Task<JsonObject> DispatchAsync(McpRequestContext context, JsonObject request, CancellationToken cancellationToken);
}

public sealed record McpTrace(string Event, McpRequestContext Context);

public sealed class McpServerOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 47831;
    /// <summary>Optional borrowed server certificate with private key. The caller keeps it alive until listener disposal.</summary>
    public X509Certificate2? ServerCertificate { get; init; }
    /// <summary>Exact TLS DNS/IP identity and advertised HTTP host. Defaults to BindAddress when TLS is enabled.</summary>
    public string? ServerName { get; init; }
    /// <summary>Optional borrowed custom root. TLS product dispatch stays gated until loopback qualification succeeds.</summary>
    public X509Certificate2? QualificationAuthority { get; init; }
    public IReadOnlyList<string> AllowedPeerCidrs { get; init; } = ["127.0.0.0/8", "::1/128"];
    public int MaximumSessions { get; init; } = 64;
    public int MaximumPendingPerSession { get; init; } = 8;
    public int MaximumPendingTotal { get; init; } = 32;
    public int MaximumRequestBytes { get; init; } = 256 * 1024;
    public int MaximumResponseBytes { get; init; } = 2300000;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Optional host-owned credential verifier. Null preserves the unauthenticated transport profile.</summary>
    public IMcpRequestAuthenticator? Authenticator { get; init; }
    /// <summary>Optional bounded metadata sink. Bodies, headers and credentials never enter this callback.</summary>
    public Action<McpTrace>? Trace { get; init; }
}
