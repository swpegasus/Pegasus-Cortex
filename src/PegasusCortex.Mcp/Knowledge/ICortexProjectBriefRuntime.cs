using Pegasus.Cortex.Core.Query;

namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Provider-neutral MCP seam for a project-brief read model. Local SQLite,
/// PostgreSQL, or test hosts can supply this without coupling the MCP application
/// to a concrete persistence or service composition.
/// </summary>
public interface ICortexProjectBriefRuntime
{
    ValueTask<CortexProjectBriefResult?> ReadProjectBriefAsync(
        string projectLinkId,
        string? knownContextSetId,
        string knownAuthoritySetHash,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken);
}
