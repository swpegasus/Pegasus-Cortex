using Pegasus.Cortex.ResourceEfficiency.Contracts;
using Pegasus.Cortex.ResourceEfficiency.Metrics;

namespace Pegasus.Cortex.ResourceEfficiency.Retention;

/// <summary>
/// Plans one deterministic cleanup page. Providers perform indexed delta reads after the continuation ID.
/// </summary>
public sealed class ResourceRetentionPlanner
{
    private readonly IResourceRetentionRepository _repository;
    private readonly ResourceEfficiencyOptions _options;
    private readonly IResourceMetricsSink _metrics;

    public ResourceRetentionPlanner(
        IResourceRetentionRepository repository,
        ResourceEfficiencyOptions options,
        IResourceMetricsSink? metrics = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _metrics = metrics ?? NullResourceMetricsSink.Instance;
    }

    public async ValueTask<ResourceRetentionPage> PlanAsync(
        DateTimeOffset now,
        string? afterRecordId,
        CancellationToken cancellationToken)
    {
        var requested = _options.RetentionBatchSize + 1;
        var candidates = await _repository.ReadExpiredPageAsync(
            now,
            afterRecordId,
            requested,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count > requested)
            throw new InvalidOperationException("The retention provider exceeded its bounded page contract.");

        var page = candidates.Take(_options.RetentionBatchSize).ToArray();
        var continuation = candidates.Count > page.Length && page.Length != 0
            ? page[^1].RecordId
            : null;
        ResourceMetricRecorder.Gauge(
            _metrics,
            ResourceMetricNames.QueueDepth,
            page.Length,
            "items",
            "cortex.resource.retention.plan");
        return new ResourceRetentionPage(page, continuation);
    }

    public void RecordCleanupFailure(string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        ResourceMetricRecorder.Gauge(
            _metrics,
            ResourceMetricNames.CleanupFailures,
            1,
            "failures",
            operation,
            "failed");
    }
}
