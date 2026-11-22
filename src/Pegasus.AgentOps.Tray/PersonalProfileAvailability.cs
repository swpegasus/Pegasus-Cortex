using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

/// <summary>Session-local UI state; it never opens, switches, or migrates a database.</summary>
internal sealed class PersonalProfileAvailability
{
    private readonly object _gate = new();
    private readonly string? _label;
    private readonly Action<string, string?>? _report;
    private bool _uiAvailable;
    private bool _requested = true;
    private bool _pipeAvailable = true;
    private CortexPersonalProfileStatusV1 _status = new(
        CortexPersonalProfilePipe.Version, CortexPersonalProfilePipe.NotLoaded, null,
        Guid.NewGuid().ToString("N"), CortexPersonalProfilePipe.UiUnavailable);

    internal PersonalProfileAvailability(string userName, Action<string, string?>? report = null)
    {
        _report = report;
        try { _label = CortexPersonalProfilePipe.CanonicalProfileLabel(userName); }
        catch (ArgumentException) { _label = null; }
    }

    internal CortexPersonalProfileStatusV1 Snapshot()
    {
        lock (_gate) return _status;
    }

    // A supervised UI restart does not undo the user's explicit unload choice.
    internal void UiLoaded() => Change(() => _uiAvailable = true);
    internal void UiUnavailable() => Change(() => _uiAvailable = false);
    internal void Load() => Change(() => _requested = true);
    internal void Unload() => Change(() => _requested = false);
    internal void PipeAvailable() => Change(() => _pipeAvailable = true);
    internal void PipeUnavailable() => Change(() => _pipeAvailable = false);

    private void Change(Action update)
    {
        CortexPersonalProfileStatusV1 changed;
        lock (_gate)
        {
            update();
            var issue = !_pipeAvailable ? CortexPersonalProfilePipe.PipeUnavailable :
                !_uiAvailable ? CortexPersonalProfilePipe.UiUnavailable :
                !_requested ? CortexPersonalProfilePipe.ProfileUnloaded :
                _label is null ? CortexPersonalProfilePipe.LabelInvalid : null;
            var state = issue is null ? CortexPersonalProfilePipe.Loaded : CortexPersonalProfilePipe.NotLoaded;
            if (_status.State == state && _status.IssueCode == issue) return;
            changed = _status = new(CortexPersonalProfilePipe.Version, state,
                issue is null ? _label : null, Guid.NewGuid().ToString("N"), issue);
        }
        try { _report?.Invoke(changed.State, changed.IssueCode); }
        catch { /* Optional diagnostics cannot affect UI, core, or execution availability. */ }
    }
}
