using System.Diagnostics;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.Geometry.Shadow;

/// <summary>Opt-in authenticated-owner composition hook, not a new public transport.
/// Observations happen after the provider returns; no original transaction is retained.
/// Does not own or dispose the wrapped provider. Ordinary result objects are returned verbatim.</summary>
public sealed class CortexObservedV2Store(ICortexV2Store inner, CortexGeometryShadow? shadow = null) : ICortexV2Store, ICortexKnowledgeSpaceMapper
{
    private readonly ICortexV2Store _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public string CanonicalizeNamespace(string value) =>
        _inner is ICortexKnowledgeSpaceMapper mapper ? mapper.CanonicalizeNamespace(value) : value;
    public IReadOnlyList<string> CanonicalizeNamespaces(IReadOnlyList<string> values) =>
        _inner is ICortexKnowledgeSpaceMapper mapper ? mapper.CanonicalizeNamespaces(values) : values;

    private ValueTask<T> Invoke<T>(object? request, Func<CancellationToken, ValueTask<T>> call, CancellationToken token)
    {
        if (shadow is null || !shadow.Enabled) return call(token);
        return ObservedAsync();
        async ValueTask<T> ObservedAsync()
        {
            var total = Stopwatch.GetTimestamp();
            using var trace = CortexSqliteTraceMeasurement.Begin();
            try
            {
            var start = Stopwatch.GetTimestamp();
            T result;
            try { result = await call(token).ConfigureAwait(false); }
            catch
            {
                shadow.ProviderCompleted(Stopwatch.GetTimestamp() - start, trace, true);
                throw;
            }
            var providerTicks = Stopwatch.GetTimestamp() - start;
            shadow.ProviderCompleted(providerTicks, trace, false);
            var hook = Stopwatch.GetTimestamp();
            try { shadow.Observe(request, result, providerTicks, trace); }
            catch (Exception) { shadow.Failure("shadow-observer-failed"); }
            finally { shadow.ObserverCompleted(Stopwatch.GetTimestamp() - hook); }
            return result;
            }
            finally
            {
                trace.Dispose();
                shadow.Add(CortexShadowMetric.ForegroundCallTicks, Stopwatch.GetTimestamp() - total);
            }
        }
    }

    public ValueTask<CortexStatusV2> StatusAsync(CancellationToken ct) =>
        Invoke(null, _inner.StatusAsync, ct);
    public ValueTask<CortexNamespacesResultV2> ListNamespacesAsync(CortexNamespacesRequestV2 r, CancellationToken ct) =>
        Invoke(r, t => _inner.ListNamespacesAsync(r, t), ct);
    public ValueTask<CortexStartResultV2> StartAsync(CortexStartRequestV2 r, CancellationToken ct) =>
        Invoke(r, t => _inner.StartAsync(r, t), ct);
    public ValueTask<CortexSearchResultV2> SearchAsync(CortexSearchRequestV2 r, CancellationToken ct) =>
        Invoke(r, t => _inner.SearchAsync(r, t), ct);
    public ValueTask<CortexReadResultV2> ReadAsync(CortexReadRequestV2 r, CancellationToken ct) =>
        Invoke(r, t => _inner.ReadAsync(r, t), ct);
    public ValueTask<CortexTaskResultV2> ExecuteTaskAsync(CortexTaskCommandV2 r, CancellationToken ct) =>
        Invoke(r, t => _inner.ExecuteTaskAsync(r, t), ct);
    public ValueTask<CortexKnowledgeResultV2> ExecuteKnowledgeAsync(CortexKnowledgeCommandV2 r, CancellationToken ct) =>
        Invoke(r, t => _inner.ExecuteKnowledgeAsync(r, t), ct);
    public ValueTask<CortexFinishResultV2> FinishAsync(CortexFinishRequestV2 r, CancellationToken ct) =>
        Invoke(r, t => _inner.FinishAsync(r, t), ct);
}
