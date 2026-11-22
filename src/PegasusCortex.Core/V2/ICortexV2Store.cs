using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Core.V2;

/// <summary>
/// Provider-neutral V2 operational boundary. Implementations must treat each
/// request as self-contained; no namespace or cursor may be inherited from a
/// prior call. Finish is one atomic storage transaction. Before any durable
/// mutation, a provider must construct and validate the exact compact result
/// against the request's response budget; a successful commit must never be
/// hidden by a post-commit response-budget failure. Conflict and stale results
/// may omit a record receipt because they did not commit a requested revision.
/// </summary>
public interface ICortexV2Store
{
    ValueTask<CortexStatusV2> StatusAsync(CancellationToken cancellationToken);

    ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(
        CortexNamespacesRequestV2 request,
        CancellationToken cancellationToken);

    ValueTask<CortexStartResultV2> StartAsync(
        CortexStartRequestV2 request,
        CancellationToken cancellationToken);

    ValueTask<CortexSearchResultV2> SearchAsync(
        CortexSearchRequestV2 request,
        CancellationToken cancellationToken);

    ValueTask<CortexReadResultV2> ReadAsync(
        CortexReadRequestV2 request,
        CancellationToken cancellationToken);

    ValueTask<CortexTaskResultV2> ExecuteTaskAsync(
        CortexTaskCommandV2 command,
        CancellationToken cancellationToken);

    ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(
        CortexKnowledgeCommandV2 command,
        CancellationToken cancellationToken);

    ValueTask<CortexFinishResultV2> FinishAsync(
        CortexFinishRequestV2 request,
        CancellationToken cancellationToken);
}
