using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexServiceEvent(
    DateTime TimeUtc,
    string Log,
    string Level,
    string Source,
    long EventId,
    string Summary,
    string Details);

internal sealed record CortexServiceNotificationSnapshot(
    DateTime CheckedUtc,
    bool ServiceInstalled,
    string ServiceState,
    int ServiceProcessId,
    string? ScmError,
    bool PipeReady,
    string? PipeError,
    IReadOnlyList<CortexServiceEvent> Events,
    bool EventsTruncated,
    string? EventReadError,
    string ServiceStartup = "unknown");

internal interface ICortexServiceNotificationSource
{
    Task<CortexServiceNotificationSnapshot> ReadAsync(TimeSpan window, int maximumEvents, CancellationToken cancellationToken);
}

internal sealed class ServiceNotificationsPage : UserControl
{
    private readonly ICortexServiceNotificationSource _source;
    private readonly ICortexServiceController _controller;
    private readonly ComboBox _window = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _start = new() { Text = "Start service", AutoSize = true };
    private readonly Button _stopService = new() { Text = "Stop service", AutoSize = true };
    private readonly Button _restart = new() { Text = "Restart service", AutoSize = true };
    private readonly Button _install = new() { Text = "Install administrator service", AutoSize = true };
    private readonly Button _manual = new() { Text = "Allow manual start", AutoSize = true };
    private readonly Button _automatic = new() { Text = "Start service with Windows", AutoSize = true };
    private readonly Button _disableStartup = new() { Text = "Disable service startup", AutoSize = true };
    private readonly Label _actionStatus = new() { Dock = DockStyle.Top, Height = 28, Padding = new Padding(10, 3, 10, 3) };
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 96, Padding = new Padding(10) };
    private readonly Label _eventStatus = new() { Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 5, 10, 5) };
    private readonly DataGridView _events = EventGrid();
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Bottom,
        Height = 120,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    private readonly CancellationTokenSource _stop = new();
    private bool _busy;
    private CortexServiceNotificationSnapshot? _lastSnapshot;

    public ServiceNotificationsPage(
        ICortexServiceNotificationSource? source = null,
        ICortexServiceController? controller = null,
        bool autoLoad = true)
    {
        _source = source ?? new WindowsCortexServiceNotificationSource();
        _controller = controller ?? new ElevatedCortexServiceController();
        _window.Items.AddRange([
            new WindowChoice("24 hours", TimeSpan.FromDays(1)),
            new WindowChoice("7 days", TimeSpan.FromDays(7)),
            new WindowChoice("30 days", TimeSpan.FromDays(30))
        ]);
        _window.SelectedIndex = 1;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        bar.Controls.AddRange([
            new Label { Text = "Recent event window", AutoSize = true, Padding = new Padding(0, 7, 0, 0) },
            _window,
            _refresh,
            _install,
            _start,
            _stopService,
            _restart,
            _manual,
            _automatic,
            _disableStartup
        ]);
        var caveat = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(10),
            Text = "The administrator service is optional. Protected changes request Windows elevation. " +
                "Startup settings do not start or stop it; Start never enables a disabled service. " +
                "The page shows at most 100 Cortex-specific warnings/errors and never retains or ingests event messages."
        };

        Controls.Add(_events);
        Controls.Add(_details);
        Controls.Add(caveat);
        Controls.Add(_eventStatus);
        Controls.Add(_actionStatus);
        Controls.Add(_summary);
        Controls.Add(bar);

        _refresh.Click += async (_, _) => await RefreshAsync();
        _start.Click += async (_, _) => await ControlAsync(CortexServiceControlAction.Start);
        _stopService.Click += async (_, _) => await ControlAsync(CortexServiceControlAction.Stop);
        _restart.Click += async (_, _) => await ControlAsync(CortexServiceControlAction.Restart);
        _install.Click += async (_, _) => await ControlAsync(CortexServiceControlAction.Install);
        _manual.Click += async (_, _) => await ControlAsync(CortexServiceControlAction.EnableManualStartup);
        _automatic.Click += async (_, _) => await ControlAsync(CortexServiceControlAction.EnableAutomaticStartup);
        _disableStartup.Click += async (_, _) => await ControlAsync(CortexServiceControlAction.DisableStartup);
        _window.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        _events.SelectionChanged += (_, _) => ShowSelectedEvent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        if (autoLoad)
        {
            HandleCreated += async (_, _) =>
            {
                _timer.Start();
                await RefreshAsync();
            };
        }
        ApplyControlState();
    }

    internal void Display(CortexServiceNotificationSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        var service = snapshot.ServiceInstalled ? snapshot.ServiceState.ToUpperInvariant() : "NOT INSTALLED";
        var process = snapshot.ServiceProcessId > 0 ? $" • PID {snapshot.ServiceProcessId}" : string.Empty;
        var pipe = snapshot.PipeReady ? "READY" : snapshot.PipeError?.ToUpperInvariant() ?? "UNAVAILABLE";
        _summary.Text = $"Privileged service: {service}{process} • Startup: {snapshot.ServiceStartup.ToUpperInvariant()} • Service pipe: {pipe}\r\n" +
            $"Checked {CortexDisplayFormat.Timestamp(snapshot.CheckedUtc).Text}. " +
            (snapshot.ScmError is null ? "SCM query succeeded." : $"SCM query: {snapshot.ScmError}.");
        _summary.ForeColor = snapshot.ServiceInstalled &&
            StringComparer.OrdinalIgnoreCase.Equals(snapshot.ServiceState, "Running") && snapshot.PipeReady
                ? CortexTheme.Palette.Accent
                : CortexTheme.Palette.Muted;

        _eventStatus.Text = snapshot.EventReadError is not null
            ? $"Windows event log partially unavailable: {snapshot.EventReadError} • retained results: {snapshot.Events.Count}"
            : $"Relevant warnings/errors: {snapshot.Events.Count}" +
              (snapshot.EventsTruncated ? " • bounded result truncated" : string.Empty);

        _events.Rows.Clear();
        foreach (var item in snapshot.Events)
        {
            var index = _events.Rows.Add(
                CortexDisplayFormat.Timestamp(item.TimeUtc).Text,
                item.Level,
                item.Log,
                item.Source,
                item.EventId,
                item.Summary);
            _events.Rows[index].Tag = item;
            var time = CortexDisplayFormat.Timestamp(item.TimeUtc);
            _events.Rows[index].Cells[0].ToolTipText = time.Text + "\r\nStored UTC: " + time.Exact;
        }
        if (_events.Rows.Count > 0)
        {
            _events.CurrentCell = _events.Rows[0].Cells[0];
            _events.Rows[0].Selected = true;
        }
        ShowSelectedEvent();
        ApplyControlState();
    }

    private async Task ControlAsync(CortexServiceControlAction action)
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        ApplyControlState();
        _actionStatus.Text = "Service change requested; waiting for Windows…";
        try
        {
            var result = await _controller.ApplyAsync(action, _stop.Token);
            _actionStatus.Text = result.Ok
                ? $"Service {result.State}; startup {result.Startup ?? "unchanged"}. Refreshing health…"
                : result.Error == "elevation_cancelled"
                    ? "Service unchanged; elevation was cancelled."
                    : ServiceIssue(result.Error);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _busy = false;
            ApplyControlState();
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        ApplyControlState();
        try
        {
            var choice = _window.SelectedItem as WindowChoice ?? new WindowChoice("7 days", TimeSpan.FromDays(7));
            var snapshot = await _source.ReadAsync(choice.Value, 100, _stop.Token);
            if (!IsDisposed) Display(snapshot);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _summary.Text = $"Service notifications unavailable: {ex.GetType().Name}";
                _summary.ForeColor = CortexTheme.Palette.Muted;
            }
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) ApplyControlState();
        }
    }

    private void ApplyControlState()
    {
        if (IsDisposed) return;
        _refresh.Enabled = !_busy;
        var installed = _lastSnapshot?.ServiceInstalled == true;
        var state = _lastSnapshot?.ServiceState;
        var running = string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
        var stopped = string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase);
        var startup = _lastSnapshot?.ServiceStartup;
        var startAllowed = startup is "manual" or "automatic";
        var observed = _lastSnapshot is { ScmError: null };
        _install.Enabled = !_busy && observed && !installed;
        _start.Enabled = !_busy && observed && installed && stopped && startAllowed;
        _stopService.Enabled = !_busy && observed && installed && running;
        _restart.Enabled = !_busy && observed && installed && running && startAllowed;
        _manual.Enabled = !_busy && installed && observed && startup != "manual";
        _automatic.Enabled = !_busy && installed && observed && startup != "automatic";
        _disableStartup.Enabled = !_busy && installed && observed && startup != "disabled";
    }

    private static string ServiceIssue(string? issue) => issue switch
    {
        "service_work_unresolved" => "The service has active or unresolved work. Finish or reconcile it before stopping.",
        "service_disabled" => "Service startup is disabled. Explicitly allow manual start or startup with Windows before choosing Start.",
        "service_shutdown_state_unavailable" or "service_control_timeout" or "service_stop_outcome_unknown" =>
            "Shutdown is unconfirmed. Refresh status and inspect the service diagnostics before another action.",
        "service_identity_conflict" or "host_path_mismatch" =>
            "The selected service or control image does not match this Cortex installation.",
        _ => $"The service change was not confirmed ({issue ?? "unknown"}). Refresh status and review diagnostics."
    };

    private void ShowSelectedEvent()
    {
        _details.Text = _events.CurrentRow?.Tag is CortexServiceEvent selected
            ? $"{CortexDisplayFormat.Timestamp(selected.TimeUtc).Text} • {selected.Level} • {selected.Log} • {selected.Source} • Event {selected.EventId}\r\n\r\n{selected.Details}"
            : string.Empty;
    }

    private static DataGridView EventGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false
        };
        var timeColumn = grid.Columns.Add("time", "Time (Windows local)");
        grid.Columns[timeColumn].Width = 340;
        grid.Columns.Add("level", "Level");
        grid.Columns.Add("log", "Log");
        grid.Columns.Add("source", "Source");
        grid.Columns.Add("id", "Event ID");
        var summary = new DataGridViewTextBoxColumn { Name = "summary", HeaderText = "Summary", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
        grid.Columns.Add(summary);
        return grid;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _stop.Cancel();
            _stop.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record WindowChoice(string Label, TimeSpan Value)
    {
        public override string ToString() => Label;
    }
}

internal sealed class WindowsCortexServiceNotificationSource : ICortexServiceNotificationSource
{
    public async Task<CortexServiceNotificationSnapshot> ReadAsync(
        TimeSpan window,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEvents, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEvents, 100);
        if (window < TimeSpan.FromHours(1) || window > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(window));

        var serviceTask = Task.Run(() => ReadServiceState(), cancellationToken);
        var eventsTask = Task.Run(
            () => CortexServiceEventPolicy.ReadRecent(window, maximumEvents, cancellationToken),
            cancellationToken);
        var service = await serviceTask;
        OperationReceipt pipeReceipt;
        try
        {
            if (service.Installed && service.State == "Running")
            {
                // This is the optional SYSTEM service's authenticated pipe, not
                // ordinary host routing. Health observation never demand-starts.
                await using var dispatcher = new NamedPipeDispatcher(NamedPipeDispatcher.UserPipeName,
                    expectedServer: new LocalPeer("S-1-5-18", 0));
                pipeReceipt = await dispatcher.DispatchAsync(new OperationRequest("queue",
                    new Dictionary<string, string> { ["op"] = "stat" }), cancellationToken);
            }
            else pipeReceipt = new(false, "none", "none", "failed", "queue", service.Error ?? "svc_down");
        }
        catch (Exception ex)
        {
            pipeReceipt = new OperationReceipt(false, "none", "none", "failed", "queue", ex.GetType().Name);
        }

        var events = await eventsTask;
        return new(
            DateTime.UtcNow,
            service.Installed,
            service.State,
            service.ProcessId,
            service.Error,
            pipeReceipt.Ok,
            pipeReceipt.Ok ? null : pipeReceipt.Err ?? "unavailable",
            events.Events,
            events.Truncated,
            events.Error,
            service.Startup);
    }

    private static CortexServiceStatus ReadServiceState() => WindowsCortexServiceControl.ReadStatus();
}

internal static class CortexServiceEventPolicy
{
    private static readonly string[] ProductSources =
    [
        "PegasusCortex.ExecutionService",
        "PegasusCortex.Host",
        "Pegasus Cortex Execution",
        "Pegasus Cortex"
    ];

    private static readonly string[] CrashSources =
    [
        ".NET Runtime",
        "Application Error",
        "Windows Error Reporting",
        "Service Control Manager"
    ];

    private static readonly string[] ProductMarkers =
    [
        CortexProductIdentity.ServiceName,
        "PegasusCortex.ExecutionService",
        "PegasusCortex.Host",
        "PegasusCortex.exe",
        "pao.exe"
    ];

    internal static bool IsRelevant(string source, string message)
    {
        if (ProductSources.Any(value => StringComparer.OrdinalIgnoreCase.Equals(source, value)) ||
            source.StartsWith("PegasusCortex.", StringComparison.OrdinalIgnoreCase)) return true;
        return CrashSources.Any(value => StringComparer.OrdinalIgnoreCase.Equals(source, value)) &&
            ProductMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeMessage(string? message, int maximumCharacters)
    {
        if (maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var normalized = string.Join(' ', (message ?? string.Empty).Split(
            ['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"(?i)\broot=[A-Z0-9]{4}\b",
            "root=[redacted]");
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "…";
    }

    internal static EventReadResult ReadRecent(TimeSpan window, int maximumEvents, CancellationToken cancellationToken)
    {
        var found = new List<CortexServiceEvent>();
        var truncated = false;
        string? error = null;
        foreach (var logName in new[] { "Application", "System" })
        {
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, Query(window))
                {
                    ReverseDirection = true,
                    TolerateQueryErrors = true
                });
                var scanned = 0;
                var matched = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++scanned > 10_000) { truncated = true; break; }
                    using var entry = reader.ReadEvent();
                    if (entry is null) break;
                    try
                    {
                        var source = entry.ProviderName ?? "Unknown";
                        var message = ReadMessage(entry);
                        if (!IsRelevant(source, message)) continue;
                        var details = NormalizeMessage(message, 4096);
                        found.Add(new(
                            entry.TimeCreated?.ToUniversalTime() ?? DateTime.MinValue,
                            logName,
                            entry.LevelDisplayName ?? $"Level {entry.Level?.ToString() ?? "unknown"}",
                            source,
                            entry.Id,
                            NormalizeMessage(message, 280),
                            details));
                        if (++matched > maximumEvents) { truncated = true; break; }
                    }
                    catch (Exception ex) when (IsReadableEventFailure(ex))
                    {
                        error ??= ex.GetType().Name;
                    }
                }
            }
            catch (Exception ex) when (IsReadableEventFailure(ex))
            {
                error ??= ex.GetType().Name;
            }
        }

        var ordered = found.OrderByDescending(item => item.TimeUtc).ThenBy(item => item.Log, StringComparer.Ordinal).ToArray();
        if (ordered.Length > maximumEvents) truncated = true;
        return new(ordered.Take(maximumEvents).ToArray(), truncated, error);
    }

    private static string Query(TimeSpan window)
    {
        var milliseconds = checked((long)window.TotalMilliseconds);
        return $"*[System[(Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {milliseconds}] and (" +
            "Provider[@Name='PegasusCortex.ExecutionService'] or " +
            "Provider[@Name='PegasusCortex.Host'] or " +
            "Provider[@Name='Pegasus Cortex Execution'] or " +
            "Provider[@Name='Pegasus Cortex'] or " +
            "Provider[@Name='.NET Runtime'] or " +
            "Provider[@Name='Application Error'] or " +
            "Provider[@Name='Windows Error Reporting'] or " +
            "Provider[@Name='Service Control Manager'])]]";
    }

    private static string ReadMessage(EventRecord entry)
    {
        try { return entry.FormatDescription() ?? "Event description unavailable."; }
        catch (Exception ex) when (IsReadableEventFailure(ex))
        {
            return "Event description unavailable.";
        }
    }

    private static bool IsReadableEventFailure(Exception exception) =>
        exception is EventLogException or Win32Exception or InvalidOperationException or UnauthorizedAccessException or
            IOException or System.Security.SecurityException or NotSupportedException;

    internal sealed record EventReadResult(IReadOnlyList<CortexServiceEvent> Events, bool Truncated, string? Error);
}
