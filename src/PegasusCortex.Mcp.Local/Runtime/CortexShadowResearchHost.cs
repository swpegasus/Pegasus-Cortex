using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Geometry.Shadow;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

internal sealed record CortexShadowHostDiagnostics(string State, string? Issue, string? SummaryPath,
    CortexShadowStatus? Collector);

/// <summary>One owner-composition lifetime; never created by client adapters or ordinary DB opens.
/// Optional failure disables research only, before ordinary calls and outside provider transactions.</summary>
internal sealed class CortexShadowResearchHost : IAsyncDisposable
{
    private CortexShadowAttachment? _attachment;
    private string _state;
    private string? _issue;
    private CortexShadowResearchHost(ICortexV2Store original, string state, string? issue)
    { Store = original; _state = state; _issue = issue; }
    internal ICortexV2Store Store { get; private set; }
    internal CortexShadowHostDiagnostics Diagnostics => new(_state, _issue,
        _attachment?.SummaryPath, _attachment?.Collector.GetStatus());

    internal static CortexShadowResearchHost Create(ICortexV2Store original, CortexShadowConfiguration? selection)
    {
        var host = new CortexShadowResearchHost(original, selection?.State ?? "not-selected", selection?.Issue);
        if (selection?.State != "selected") return host;
        try
        {
            selection.PrepareRoot();
            host._attachment = new(original, selection.Profile!, selection.ResearchRoot!, selection.Options);
            host.Store = host._attachment.Store;
            host._state = "enabled";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                   System.Text.Json.JsonException or InvalidOperationException)
        { host._state = "disabled"; host._issue = "initialization-" + error.GetType().Name; }
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_attachment is null) return;
        try { await _attachment.DisposeAsync().ConfigureAwait(false); _state = "stopped"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        { _state = "stop-failed"; _issue = "shutdown-" + error.GetType().Name; }
    }
}
