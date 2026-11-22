using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Icon _applicationIcon;private readonly NotifyIcon _icon;private readonly System.Windows.Forms.Timer _timer;private readonly ToolStripMenuItem _status;private readonly ToolStripMenuItem _pause;private QueueWindow? _queue;
    private readonly PersonalProfileAvailability _personalProfile;
    private readonly ToolStripMenuItem _profileStatus;
    private readonly ToolStripMenuItem _profileToggle;
    private readonly ToolStripMenuItem _exitRuntimeItem;
    private readonly ToolStripMenuItem _closeTrayItem;
    private readonly Func<CancellationToken, Task<CortexRuntimeExitResult>>? _exitRuntime;
    private readonly Func<CortexUserPresentationState>? _presentation;
    private readonly Func<IDisposable>? _beginCertificatePasswordOperation;
    private readonly Func<IDisposable>? _beginMcpSetupOperation;
    private readonly Control _uiDispatcher = new();
    private readonly bool _automaticRefresh;
    private bool _exitPending, _exitCommitted;
    public TrayApplicationContext(PersonalProfileAvailability? personalProfile = null,
        Func<CancellationToken, Task<CortexRuntimeExitResult>>? exitRuntime = null,
        Func<CortexUserPresentationState>? presentation = null, bool automaticRefresh = true,
        Func<IDisposable>? beginCertificatePasswordOperation = null, Func<IDisposable>? beginMcpSetupOperation = null)
    {
        _exitRuntime = exitRuntime;
        _presentation = presentation;
        _beginCertificatePasswordOperation = beginCertificatePasswordOperation;
        _beginMcpSetupOperation = beginMcpSetupOperation;
        _automaticRefresh = automaticRefresh;
        _personalProfile = personalProfile ?? new PersonalProfileAvailability(Environment.UserName);
        _profileStatus = new ToolStripMenuItem("Personal profile: not loaded") { Enabled = false };
        _profileToggle = new ToolStripMenuItem("Load personal profile", null, (_, _) => TogglePersonalProfile());
        _status=new ToolStripMenuItem("Service: checking") { Enabled=false };_pause=new ToolStripMenuItem("Pause queue",null,async(_,_)=>await TogglePauseAsync());
        _closeTrayItem = new ToolStripMenuItem("Close tray UI", null, (_, _) => CloseUserInterface());
        _exitRuntimeItem = new ToolStripMenuItem("Exit Cortex", null, async (_, _) => await ExitRuntimeAsync()) { Enabled = exitRuntime is not null };
        var menu=new ContextMenuStrip();menu.Items.AddRange([_status,_profileStatus,_profileToggle,new ToolStripSeparator(),new ToolStripMenuItem("Open Cortex",null,(_,_)=>ShowDashboard()),new ToolStripMenuItem("Work queue",null,(_,_)=>ShowQueue()),_pause,new ToolStripSeparator(),_closeTrayItem,_exitRuntimeItem]);
        CortexTheme.Apply(menu);
        _applicationIcon=LoadApplicationIcon();_icon=new NotifyIcon{Icon=_applicationIcon,Text="Pegasus Cortex",Visible=true,ContextMenuStrip=menu};_icon.DoubleClick+=(_,_)=>ShowDashboard();
        _timer=new System.Windows.Forms.Timer{Interval=3000};_timer.Tick+=async(_,_)=>await RefreshAsync();
        _ = _uiDispatcher.Handle;
        if (_automaticRefresh) { _timer.Start(); _ = RefreshAsync(); }
        // Publish availability only once the successfully constructed tray owns
        // an operating UI message loop, not merely because the host process exists.
        Application.Idle += OnUiReady;
    }
    private void OnUiReady(object? sender, EventArgs e)
    {
        Application.Idle -= OnUiReady;
        _personalProfile.UiLoaded();
        RefreshPersonalProfile();
    }
    private void TogglePersonalProfile()
    {
        if (_personalProfile.Snapshot().State == CortexPersonalProfilePipe.Loaded) _personalProfile.Unload();
        else _personalProfile.Load();
        RefreshPersonalProfile();
    }
    private void RefreshPersonalProfile()
    {
        var status = _personalProfile.Snapshot();
        var loaded = status.State == CortexPersonalProfilePipe.Loaded;
        var label = status.Profile is { Length: > 48 } ? status.Profile[..48] + "…" : status.Profile;
        _profileStatus.Text = loaded ? $"Personal profile: loaded ({label})" :
            $"Personal profile: not loaded ({status.IssueCode ?? CortexPersonalProfilePipe.UiUnavailable})";
        _profileToggle.Text = loaded ? "Unload personal profile" : "Load personal profile";
        _profileToggle.Enabled = status.IssueCode != CortexPersonalProfilePipe.LabelInvalid;
    }
    private static Icon LoadApplicationIcon()
    {
        using var stream=typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("Pegasus.AgentOps.Tray.Icon");
        if(stream is null)return (Icon)SystemIcons.Application.Clone();
        using var icon=new Icon(stream);
        return (Icon)icon.Clone();
    }
    private void ShowQueue(){if(_exitPending||_exitCommitted)return;if(_queue is null||_queue.IsDisposed)_queue=new QueueWindow();_queue.Show();_queue.Activate();_ = _queue.RefreshAsync();}
    private SettingsWindow? _dashboard;
    private void ShowDashboard(){if(_exitPending||_exitCommitted)return;if(_dashboard is null||_dashboard.IsDisposed)_dashboard=new SettingsWindow(_presentation, ShowQueue, _beginCertificatePasswordOperation, _beginMcpSetupOperation);_dashboard.Show();_dashboard.Activate();}
    private async Task RefreshAsync()
    {
        if (_exitPending || _exitCommitted || !_icon.Visible || _uiDispatcher.IsDisposed) return;
        RefreshPersonalProfile();
        try
        {
            var host = await DispatchAsync("diag", new Dictionary<string, string>());
            if (_exitPending || _exitCommitted || !_icon.Visible || _uiDispatcher.IsDisposed) return;
            var service = await DispatchAsync("queue", new Dictionary<string, string> { ["op"] = "stat" });
            if (_exitPending || _exitCommitted || !_icon.Visible || _uiDispatcher.IsDisposed) return;
            _status.Text = host.Ok ? $"Host: ready; elevated service: {(service.Ok ? "ready" : service.Err ?? "unavailable")}" : $"Host: {host.Err ?? "unavailable"}";
            _pause.Enabled = service.Ok;
            _pause.Text = Bool(service, "paused") ? "Resume service queue" : "Pause service queue";
            if (_queue is { Visible: true }) await _queue.RefreshAsync();
        }
        catch { if (!_uiDispatcher.IsDisposed) _status.Text = "Host: unavailable"; }
    }
    private async Task TogglePauseAsync(){if(_exitPending||_exitCommitted)return;var paused=_pause.Text?.StartsWith("Resume",StringComparison.Ordinal)==true;var receipt=await DispatchAsync("queue",new Dictionary<string,string>{{"op",paused?"resume":"pause"}});if(_exitPending||_exitCommitted)return;if(!receipt.Ok)MessageBox.Show($"Queue control failed: {receipt.Err}","Pegasus Cortex",MessageBoxButtons.OK,MessageBoxIcon.Warning);await RefreshAsync();}
    internal bool UserInterfaceVisible => _icon.Visible;
    internal void CloseUserInterface()
    {
        if (_exitPending || _exitCommitted) return;
        _personalProfile.Unload();
        _personalProfile.UiUnavailable();
        _timer.Stop();
        _icon.Visible = false;
        _dashboard?.Dispose(); _dashboard = null;
        _queue?.Dispose(); _queue = null;
        // The existing host's hidden message loop remains available for a
        // later explicit launch to reopen controls. No runtime is started here.
    }
    internal Task<bool> ShowUserInterfaceAsync(CancellationToken cancellationToken)
    {
        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                if (_uiDispatcher.IsDisposed || _exitPending || _exitCommitted) { shown.TrySetResult(false); return; }
                _icon.Visible = true;
                _personalProfile.UiLoaded();
                RefreshPersonalProfile();
                if (_automaticRefresh) { _timer.Start(); _ = RefreshAsync(); }
                ShowDashboard();
                shown.TrySetResult(true);
            }));
        }
        catch (InvalidOperationException) { shown.TrySetResult(false); }
        return shown.Task.WaitAsync(cancellationToken);
    }
    private void Exit(){_personalProfile.Unload();_timer.Stop();_icon.Visible=false;ExitThread();}
    private async Task ExitRuntimeAsync()
    {
        if (_exitRuntime is null || !_exitRuntimeItem.Enabled) return;
        _exitPending = true;
        _timer.Stop();
        _exitRuntimeItem.Enabled = _closeTrayItem.Enabled = _profileToggle.Enabled = _pause.Enabled = false;
        if (_dashboard is not null) _dashboard.Enabled = false;
        if (_queue is not null) _queue.Enabled = false;
        try
        {
            var result = await _exitRuntime(CancellationToken.None);
            _exitCommitted = result.Accepted;
            if (result.Accepted)
            {
                if (result.Issue is null) Exit();
                else MessageBox.Show("Cortex has begun shutting down, but the stop signal could not be confirmed. Work remains blocked; review shutdown before maintenance or restart.",
                    "Cortex shutdown is uncertain", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var message = result.Issue switch
            {
                "work_active" => "Cortex still has active work or terminal sessions. Finish that work, then choose Exit Cortex again.",
                "applications_active" => "Cortex still owns running applications. Stop those applications explicitly before exiting.",
                "work_unresolved" => "Some work has an uncertain outcome. Review it before exiting; nothing was retried or acknowledged.",
                _ => "Cortex could not confirm a complete shutdown. It will stay open. Resolve the service or work status, then try again."
            };
            MessageBox.Show(message, "Exit Cortex incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!_exitCommitted && _icon.Visible && !_uiDispatcher.IsDisposed)
            {
                _exitPending = false;
                _exitRuntimeItem.Enabled = _closeTrayItem.Enabled = true;
                if (_dashboard is not null) _dashboard.Enabled = true;
                if (_queue is not null) _queue.Enabled = true;
                RefreshPersonalProfile();
                if (_automaticRefresh) { _timer.Start(); await RefreshAsync(); }
            }
        }
    }
    protected override void Dispose(bool disposing){if(disposing){Application.Idle-=OnUiReady;_personalProfile.UiUnavailable();_timer.Dispose();_icon.Dispose();_applicationIcon.Dispose();_queue?.Dispose();_dashboard?.Dispose();_uiDispatcher.Dispose();}base.Dispose(disposing);}
    internal static async Task<OperationReceipt> DispatchAsync(string op,IReadOnlyDictionary<string,string> arg,string? body=null){await using var dispatcher=new RoutingDispatcher();return await dispatcher.DispatchAsync(new OperationRequest(op,arg,Body:body));}
    internal static object? Value(OperationReceipt receipt,string key){if(receipt.Data is null||!receipt.Data.TryGetValue(key,out var value))return null;return value is JsonElement json?json.ValueKind==JsonValueKind.String?json.GetString():json.GetRawText():value;}
    internal static bool Bool(OperationReceipt receipt,string key){var value=Value(receipt,key);return value is bool direct?direct:bool.TryParse(value?.ToString(),out var parsed)&&parsed;}
}

internal sealed class QueueWindow : Form
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, MultiSelect = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AccessibleName = "Work queue"
    };
    private readonly Label _identity = new() { AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false, Text = "Select a job." };
    private readonly TextBox _submission = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Retained job submission" };
    private readonly ListBox _arguments = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, AccessibleName = "Retained submission arguments" };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, AccessibleName = "Selected job result receipt" };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, AutoEllipsis = true, UseMnemonic = false };
    private readonly Func<OperationRequest, CancellationToken, Task<OperationReceipt>> _dispatch;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _selection;
    private long _selectionEpoch, _refreshEpoch;
    private bool _binding, _resourcesDisposed;

    public QueueWindow() : this(null) { }

    internal QueueWindow(Func<OperationRequest, CancellationToken, Task<OperationReceipt>>? dispatch)
    {
        _dispatch = dispatch ?? DispatchOwnedAsync;
        Text = "Cortex work queue"; Size = new Size(1060, 780); MinimumSize = new Size(840, 640);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "jid", HeaderText = "Job ID", Width = 250, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "State", Width = 100, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "op", HeaderText = "Operation", Width = 160, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "err", HeaderText = "Error", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "partial", HeaderText = "Partial", Width = 65, ReadOnly = true, ThreeState = false });
        _grid.CellPainting += (_, args) =>
        {
            if (args.RowIndex < 0 || args.ColumnIndex != 4 || args.Graphics is not { } graphics) return;
            args.Paint(args.ClipBounds, args.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            var state = args.Value is true ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedDisabled
                : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled;
            var size = CheckBoxRenderer.GetGlyphSize(graphics, state);
            CheckBoxRenderer.DrawCheckBox(graphics, new Point(args.CellBounds.Left + (args.CellBounds.Width - size.Width) / 2,
                args.CellBounds.Top + (args.CellBounds.Height - size.Height) / 2), state);
            args.Handled = true;
        };
        var bar = new ToolStrip { Dock = DockStyle.Top };
        bar.Items.Add(new ToolStripButton("Refresh", null, async (_, _) => await RefreshAsync()));
        bar.Items.Add(new ToolStripButton("Cancel", null, async (_, _) => await ActionAsync("cancel")));
        bar.Items.Add(new ToolStripButton("Retry", null, async (_, _) => await ActionAsync("retry")));
        var split = new SplitContainer { Size = new Size(1040, 700), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 255 };
        split.Panel1.Controls.Add(_grid);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.Controls.Add(_identity, 0, 0); details.Controls.Add(_submission, 0, 1);
        details.Controls.Add(new Label { AutoSize = true, Text = "Arguments" }, 0, 2);
        details.Controls.Add(_arguments, 0, 3);
        details.Controls.Add(new Label { AutoSize = true, Text = "Result receipt" }, 0, 4);
        details.Controls.Add(_detail, 0, 5);
        split.Panel2.Controls.Add(details);
        Controls.Add(split); Controls.Add(_status); Controls.Add(bar);
        _grid.SelectionChanged += async (_, _) => { if (!_binding) await ShowSelectedAsync(); };
        CortexTheme.Apply(this); CortexWindowIcon.Apply(this);
    }

    private static async Task<OperationReceipt> DispatchOwnedAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        await using var dispatcher = new RoutingDispatcher();
        return await dispatcher.DispatchAsync(request, cancellationToken);
    }

    public async Task RefreshAsync()
    {
        if (IsDisposed || _lifetime.IsCancellationRequested) return;
        var epoch = ++_refreshEpoch;
        try
        {
            var receipt = await _dispatch(new OperationRequest("job", new Dictionary<string, string> { ["op"] = "ls" }), _lifetime.Token);
            if (IsDisposed || epoch != _refreshEpoch) return;
            if (!receipt.Ok) { _status.Text = "Queue unavailable: " + receipt.Err; return; }
            var selected = (_grid.CurrentRow?.Tag as JobRow)?.Jid;
            var jobs = receipt.Data?.GetValueOrDefault("jobs");
            var rows = jobs is null ? Array.Empty<JobRow>() : JsonSerializer.SerializeToElement(jobs, Json).EnumerateArray().Select(JobRow.Read).ToArray();
            _binding = true;
            try
            {
                _grid.Rows.Clear();
                foreach (var row in rows)
                {
                    var index = _grid.Rows.Add(row.Jid, row.State, row.Op, row.Err ?? string.Empty, row.Partial);
                    _grid.Rows[index].Tag = row;
                    _grid.Rows[index].Cells[0].ToolTipText = row.Jid;
                    _grid.Rows[index].Cells[4].ToolTipText = row.Partial ? "Partial result — read-only indicator" : "Not partial — read-only indicator";
                }
                var indexToSelect = Array.FindIndex(rows, row => row.Jid == selected);
                if (rows.Length > 0) _grid.CurrentCell = _grid.Rows[indexToSelect >= 0 ? indexToSelect : 0].Cells[0];
            }
            finally { _binding = false; }
            Text = $"Cortex work queue — {CortexDisplayFormat.Count(rows.Length).Text} recent";
            await ShowSelectedAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        { if (!IsDisposed && epoch == _refreshEpoch) _status.Text = "Queue unavailable: " + error.GetType().Name; }
    }

    internal async Task SelectJobAsync(string jid)
    {
        if (IsDisposed) return;
        var row = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(item => item.Tag is JobRow job && job.Jid == jid);
        if (row is null) return;
        _binding = true;
        try { _grid.CurrentCell = row.Cells[0]; }
        finally { _binding = false; }
        await ShowSelectedAsync();
    }

    private async Task ShowSelectedAsync()
    {
        if (IsDisposed) return;
        var epoch = ++_selectionEpoch;
        _selection?.Cancel(); _selection?.Dispose();
        _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellation = _selection.Token;
        _submission.Clear(); _arguments.Items.Clear(); _detail.Clear();
        if (_grid.CurrentRow?.Tag is not JobRow row) { _identity.Text = "Select a job."; return; }
        _identity.Text = $"Job ID: {row.Jid} • Operation: {row.Op}";
        _submission.Text = "Loading retained submission…";
        try
        {
            var receipt = await _dispatch(new OperationRequest("job", new Dictionary<string, string> { ["op"] = "stat", ["jid"] = row.Jid }), cancellation);
            if (IsDisposed || cancellation.IsCancellationRequested || epoch != _selectionEpoch || (_grid.CurrentRow?.Tag as JobRow)?.Jid != row.Jid) return;
            if (!receipt.Ok || receipt.Data?.GetValueOrDefault("rec") is not { } value)
            { _submission.Text = "Job details are unavailable: " + receipt.Err; return; }
            var result = JsonSerializer.SerializeToElement(value, Json);
            if (String(result, "jid") != row.Jid) { _submission.Text = "Job identity mismatch; details were not displayed."; return; }
            ShowSubmission(result);
            const int maximumCharacters = 64 * 1024;
            var rendered = result.GetRawText();
            _detail.Text = rendered.Length <= maximumCharacters ? rendered : rendered[..maximumCharacters] + "\r\n[Receipt display truncated; original result unchanged.]";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        { if (!IsDisposed && epoch == _selectionEpoch) _submission.Text = "Job details are unavailable: " + error.GetType().Name; }
    }

    private void ShowSubmission(JsonElement receipt)
    {
        _submission.Text = "Submission was not retained for this job.";
        if (!receipt.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        if (data.TryGetProperty("history_pruned", out var pruned) && pruned.ValueKind == JsonValueKind.True)
        { _submission.Text = "Submission was not retained for this job. History details were pruned."; return; }
        if (!data.TryGetProperty("submission", out var submission)) return;
        if (submission.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(submission).Length > 4096 ||
            String(submission, "schema") != "pegasus.cortex.job-submission/1" || String(submission, "state") is not ("available" or "redacted") ||
            String(submission, "operation").Length == 0 ||
            !submission.TryGetProperty("command", out var commandValue) || commandValue.ValueKind != JsonValueKind.String ||
            !submission.TryGetProperty("truncated", out var truncatedValue) || truncatedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !submission.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array ||
            arguments.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String) ||
            !submission.TryGetProperty("omitted", out var omitted) || omitted.ValueKind != JsonValueKind.Array ||
            omitted.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        { _submission.Text = "Retained submission display is unavailable (invalid metadata)."; return; }
        var text = "Submission: " + String(submission, "state") + "\r\nOperation: " + String(submission, "operation") +
            "\r\nCommand: " + (String(submission, "command") is { Length: > 0 } command ? command : "Not retained") +
            (String(submission, "toolReference") is { Length: > 0 } tool ? "\r\nTool reference: " + tool : string.Empty);
        var index = 0;
        foreach (var argument in arguments.EnumerateArray()) _arguments.Items.Add($"[{index++}] {argument.GetString()}");
        var missing = string.Join(", ", omitted.EnumerateArray().Select(item => item.GetString()));
        if (missing.Length > 0) text += "\r\nNot retained: " + missing;
        if (submission.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            text += "\r\nRetained submission metadata was truncated.";
        // One assignment keeps the command at the beginning of the read-only view;
        // AppendText selects the end and can initialize/scroll an unshown native textbox.
        _submission.Text = text;
    }

    private async Task ActionAsync(string operation)
    {
        if (IsDisposed || _grid.CurrentRow?.Tag is not JobRow row) return;
        try
        {
            // Preserve the original action outcome after dispatch; closing a window is not cancellation of that job.
            var receipt = await _dispatch(new OperationRequest("job", new Dictionary<string, string> { ["op"] = operation, ["jid"] = row.Jid }), CancellationToken.None);
            if (IsDisposed) return;
            _status.Text = receipt.Ok ? $"{operation} request for {row.Jid} accepted." : $"{operation} request for {row.Jid} was not confirmed: {receipt.Err}";
            await RefreshAsync();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        { if (!IsDisposed) _status.Text = "Job action was not confirmed: " + error.GetType().Name; }
    }

    private static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private sealed record JobRow(string Jid, string Rid, string State, string Op, string? Err, bool Partial, long Seq)
    {
        internal static JobRow Read(JsonElement value) => new(String(value, "jid"), String(value, "rid"), String(value, "state"),
            String(value, "op"), String(value, "err"), value.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.True,
            value.TryGetProperty("seq", out var seq) ? seq.GetInt64() : 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _selectionEpoch++; _refreshEpoch++; _lifetime.Cancel(); _selection?.Cancel(); _selection?.Dispose(); _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
