using System.Collections.ObjectModel;
using System.Net;

namespace Pegasus.SharedMcp;

/// <summary>
/// Public, opaque identifiers derived by the product verifier. Generation identifies the current
/// credential family/version, not a privileged grant. Never put a token, secret or private key here.
/// </summary>
public sealed record McpAuthenticatedIdentity
{
    public string Authority { get; }
    public string Subject { get; }
    public string Generation { get; }

    public McpAuthenticatedIdentity(string authority, string subject, string generation)
    {
        Authority = RequireIdentifier(authority);
        Subject = RequireIdentifier(subject);
        Generation = RequireIdentifier(generation);
    }

    private static string RequireIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl)
            ? value : throw new ArgumentException("Authenticated identifiers must be nonempty bounded public values.");
}

/// <summary>
/// Immutable snapshot for one verification call. Headers may contain credentials: do not log or
/// persist them. Peer/source information and client-supplied headers are never identity proof.
/// </summary>
public sealed class McpAuthenticationRequest
{
    public string Product { get; }
    public string Method { get; }
    public Uri Resource { get; }
    public IPAddress PeerAddress { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    internal McpAuthenticationRequest(string product, string method, Uri resource, IPAddress peerAddress,
        IEnumerable<KeyValuePair<string, string[]>> headers)
    {
        Product = product;
        Method = method;
        Resource = resource;
        PeerAddress = new IPAddress(peerAddress.GetAddressBytes());
        Headers = new ReadOnlyDictionary<string, IReadOnlyList<string>>(headers.ToDictionary(
            p => p.Key, p => (IReadOnlyList<string>)Array.AsReadOnly((string[])p.Value.Clone()), StringComparer.OrdinalIgnoreCase));
    }
}

public interface IMcpRequestAuthenticator
{
    /// <summary>
    /// Validate credentials and their active generation on EVERY request. Missing credentials may
    /// be Anonymous under product policy; invalid/revoked/presented credentials must be Reject.
    /// Return no credential material. The product owns issuance, custody, persistence and grants.
    /// </summary>
    ValueTask<McpAuthenticationResult> AuthenticateAsync(McpAuthenticationRequest request, CancellationToken cancellationToken);
}

/// <summary>Only the product verifier constructs admission results; failures never become anonymous admission.</summary>
public sealed class McpAuthenticationResult
{
    public McpAuthenticatedIdentity? Identity { get; }
    public int? RejectionStatus { get; }
    public string? Challenge { get; }
    private McpAuthenticationResult(McpAuthenticatedIdentity? identity, int? rejectionStatus, string? challenge)
        => (Identity, RejectionStatus, Challenge) = (identity, rejectionStatus, challenge);
    public static McpAuthenticationResult Anonymous { get; } = new(null, null, null);
    public static McpAuthenticationResult Authenticated(McpAuthenticatedIdentity identity) =>
        new(identity ?? throw new ArgumentNullException(nameof(identity)), null, null);
    public static McpAuthenticationResult Reject(int status = 401, string? challenge = null)
    {
        if (status is not (401 or 403) || challenge is not null &&
            (challenge.Length is < 1 or > 2048 || challenge.Any(char.IsControl)))
            throw new ArgumentException("Use 401/403 and an optional bounded single-line WWW-Authenticate challenge.");
        return new(null, status, challenge);
    }
}

public sealed record McpSessionContext(string Product, string SessionId, int StreamId, string ProtocolVersion,
    McpAuthenticatedIdentity? Identity);

public enum McpSessionCloseReason { Deleted, IdleExpired, InitializationFailed, ServerShutdown }

/// <summary>
/// Optional dispatcher seam. Open runs once before InitializeAsync. Close pairs even with a failed
/// open and runs exactly once after all admitted callbacks settle. Close must perform bounded
/// product cleanup; it has no cancellation token that could abandon owned cleanup underneath work.
/// The host owns the dispatcher and must not dispose it before listener disposal completes.
/// </summary>
public interface IMcpSessionLifecycle
{
    ValueTask SessionOpenedAsync(McpSessionContext context, CancellationToken cancellationToken);
    ValueTask SessionClosedAsync(McpSessionContext context, McpSessionCloseReason reason);
}
