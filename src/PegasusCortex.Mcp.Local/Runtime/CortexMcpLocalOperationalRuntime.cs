using Pegasus.Cortex.Core.Operations;
using Pegasus.Cortex.Mcp.Knowledge;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

internal sealed class CortexMcpLocalOperationalRuntime : ICortexOperationalRuntime
{
    private readonly CortexOperationalApplicationService _application;
    private readonly string _actor;

    public CortexMcpLocalOperationalRuntime(
        CortexOperationalApplicationService application,
        string actor)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
    }

    public ValueTask<CortexOperationalWriteResult> CreateTaskAsync(
        string ns, string taskId, string title, string content,
        string? assignee, IReadOnlyList<string> dependencies,
        string idempotencyKey, CancellationToken cancellationToken) =>
        _application.CreateTaskAsync(
            ns, taskId, title, content, assignee, dependencies,
            idempotencyKey, _actor, cancellationToken);

    public ValueTask<CortexOperationalWriteResult> MutateTaskAsync(
        string ns, string taskId, long expectedRevision, string operation,
        string? title, string? content, string? assignee, string? claimedBy,
        string? reason, string? dependencyId, string idempotencyKey,
        CancellationToken cancellationToken) =>
        _application.MutateTaskAsync(
            ns, taskId, expectedRevision, operation, title, content,
            assignee, claimedBy, reason, dependencyId, idempotencyKey,
            _actor, cancellationToken);

    public ValueTask<CortexOperationalTask?> ReadTaskAsync(
        string ns, string taskId, CancellationToken cancellationToken) =>
        _application.ReadTaskAsync(ns, taskId, cancellationToken);

    public ValueTask<CortexOperationalPage<CortexOperationalTask>> ListTasksAsync(
        string ns, string? afterTaskId, int limit,
        CancellationToken cancellationToken) =>
        _application.ListTasksAsync(ns, afterTaskId, limit, cancellationToken);

    public ValueTask<IReadOnlyList<CortexOperationalTask>> ReadTaskHistoryAsync(
        string ns, string taskId, long firstRevision, long lastRevision,
        CancellationToken cancellationToken) =>
        _application.ReadTaskHistoryAsync(
            ns, taskId, firstRevision, lastRevision, cancellationToken);

    public ValueTask<CortexOperationalWriteResult> CreateKnowledgeAsync(
        string ns, string recordId, string title, string content,
        IReadOnlyList<string> tags, string idempotencyKey,
        CancellationToken cancellationToken) =>
        _application.CreateKnowledgeAsync(
            ns, recordId, title, content, tags, idempotencyKey,
            _actor, cancellationToken);

    public ValueTask<CortexOperationalWriteResult> MutateKnowledgeAsync(
        string ns, string recordId, long expectedRevision, string operation,
        string? title, string? content, IReadOnlyList<string>? tags,
        string? replacementRecordId, string idempotencyKey,
        CancellationToken cancellationToken) =>
        _application.MutateKnowledgeAsync(
            ns, recordId, expectedRevision, operation, title, content, tags,
            replacementRecordId, idempotencyKey, _actor, cancellationToken);

    public ValueTask<CortexOperationalKnowledge?> ReadKnowledgeAsync(
        string ns, string recordId, CancellationToken cancellationToken) =>
        _application.ReadKnowledgeAsync(ns, recordId, cancellationToken);

    public ValueTask<IReadOnlyList<CortexOperationalKnowledge>> SearchKnowledgeAsync(
        string ns, string text, int limit, CancellationToken cancellationToken) =>
        _application.SearchKnowledgeAsync(ns, text, limit, cancellationToken);
}
