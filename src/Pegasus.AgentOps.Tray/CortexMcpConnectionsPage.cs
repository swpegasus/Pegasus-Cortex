using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed class CortexMcpConnectionsPage : UserControl
{
    private readonly ICortexMcpConnectionsController _controller;
    private readonly Action<string> _clipboard;
    private readonly Func<string, string> _codexConfiguration;
    private readonly Func<string?> _chooseImport;
    private readonly Func<CortexMcpConnectionRequest, bool> _confirmFull;
    private readonly Func<CortexMcpConnectionRequest, bool> _confirmRevoke;
    private readonly bool _autoLoad;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, CortexMcpConnectionRequest> _drafts = new(StringComparer.Ordinal);
    private readonly TextBox _name = new() { AccessibleName = "Connection name", Dock = DockStyle.Fill, MaxLength = 96 };
    private readonly TextBox _application = new() { AccessibleName = "Client application", Dock = DockStyle.Fill, MaxLength = 128 };
    private readonly TextBox _chat = new() { AccessibleName = "Optional chat label", Dock = DockStyle.Fill, MaxLength = 128 };
    private readonly DataGridView _grid = new()
    {
        AccessibleName = "MCP connections", Dock = DockStyle.Fill, ReadOnly = true,
        AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false, MinimumSize = new Size(0, 150), EditMode = DataGridViewEditMode.EditProgrammatically
    };
    private readonly TextBox _id = new()
    {
        AccessibleName = "Full connection ID", ReadOnly = true, Dock = DockStyle.Fill
    };
    private readonly Label _details = Label("Connection approval detail");
    private readonly Label _guidance = Label("Connection guidance",
        "Name each client, create a local credential or import its public enrollment request, then approve its access. " +
        "Approvals persist until explicitly changed. Names, applications and chat labels are supplied descriptions.");
    private readonly Label _status = Label("Connection status");
    private readonly Label _danger = Label("Full control warning",
        "FULL CONTROL: broad authority for this credential. Every chat sharing the credential inherits Full control. " +
        "A name or chat label does not isolate its access. Use separate connections when Full control must differ.");
    private readonly Button _create = Button("Create local connection");
    private readonly Button _import = Button("Import public request…");
    private readonly Button _refresh = Button("Refresh");
    private readonly Button _ordinary = Button("Approve ordinary access");
    private readonly Button _full = Button("Grant Full control…");
    private readonly Button _revokeFull = Button("Revoke Full control");
    private readonly Button _revoke = Button("Revoke all access…");
    private readonly Button _copy = Button("Copy full connection ID");
    private readonly Button _copyConfiguration = Button("Copy Codex settings");
    private CortexMcpConnectionStore? _store;
    private Font? _dangerFont;
    private bool _observed, _busy, _loading, _requiresRefresh, _disposed, _constructed;

    internal CortexMcpConnectionsPage(ICortexMcpConnectionsController? controller = null,
        Action<string>? clipboard = null, Func<string?>? chooseImport = null,
        Func<CortexMcpConnectionRequest, bool>? confirmFull = null,
        Func<CortexMcpConnectionRequest, bool>? confirmRevoke = null,
        Action<Control>? theme = null, bool autoLoad = true, Func<string, string>? codexConfiguration = null)
    {
        _controller = controller ?? new CortexMcpConnectionsControl();
        _clipboard = clipboard ?? Clipboard.SetText;
        _codexConfiguration = codexConfiguration ?? (id =>
        {
            var installed = CortexMcpSettingsControl.ResolveInstalledHost();
            var settings = CortexMcpSettingsControl.ReadCurrent();
            var endpoint = new UriBuilder("https", settings.ServerName, settings.Port, "/mcp/cortex").Uri.AbsoluteUri;
            return CortexMcpClientConfiguration.Codex(Path.Combine(installed.Root, "PegasusCortex.exe"), id, endpoint);
        });
        _chooseImport = chooseImport ?? (() =>
        {
            using var dialog = new OpenFileDialog
            {
                CheckFileExists = true, Filter = "Public MCP enrollment request (*.json)|*.json",
                Title = "Import public MCP connection request"
            };
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        });
        _confirmFull = confirmFull ?? (request => MessageBox.Show(this,
            "Grant Full control to “" + request.Name + "” (" + request.Application + ")?\n\n" +
            "This grants broad authority. ALL CHATS SHARING THIS CREDENTIAL inherit it. " +
            "The optional chat label is descriptive only. Use a separate connection when permissions must differ.",
            "DANGER — grant Full control", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK);
        _confirmRevoke = confirmRevoke ?? (request => MessageBox.Show(this,
            "Revoke access for “" + request.Name + "”? Every chat sharing this credential loses its approval.",
            "Revoke connection", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK);
        _autoLoad = autoLoad;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            AutoScroll = true, Padding = new Padding(16) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 8; index++) body.RowStyles.Add(new RowStyle(
            index == 3 ? SizeType.Percent : SizeType.AutoSize, index == 3 ? 100 : 0));
        var heading = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(new Label { AutoSize = true, Font = CortexTheme.HeadingFont, Text = "Connections & Full control" });
        heading.Controls.Add(_guidance);
        body.Controls.Add(heading, 0, 0);
        var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, RowCount = 2 };
        for (var column = 0; column < 3; column++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        fields.Controls.Add(Label("", "Name"), 0, 0); fields.Controls.Add(_name, 0, 1);
        fields.Controls.Add(Label("", "Application"), 1, 0); fields.Controls.Add(_application, 1, 1);
        fields.Controls.Add(Label("", "Chat label (optional)"), 2, 0); fields.Controls.Add(_chat, 2, 1);
        body.Controls.Add(fields, 0, 1);
        var setup = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        setup.Controls.AddRange([_create, _import, _refresh, _copyConfiguration]);
        body.Controls.Add(setup, 0, 2);
        foreach (var column in new[] { ("Name", 19f), ("Application", 18f), ("Chat label", 16f),
                     ("Connection ID", 23f), ("Access", 12f), ("Full control", 12f) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, Name = column.Item1,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = column.Item2, ReadOnly = true });
        body.Controls.Add(_grid, 0, 3);
        var identity = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); identity.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        identity.Controls.Add(_id, 0, 0); identity.Controls.Add(_copy, 1, 0);
        identity.Controls.Add(_details, 0, 1); identity.SetColumnSpan(_details, 2);
        body.Controls.Add(identity, 0, 4);
        body.Controls.Add(_danger, 0, 5);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        actions.Controls.AddRange([_ordinary, _full, _revokeFull, _revoke]);
        body.Controls.Add(actions, 0, 6); body.Controls.Add(_status, 0, 7);
        Controls.Add(body);
        (theme ?? CortexTheme.Apply)(this);
        _dangerFont = new Font(_danger.Font, FontStyle.Bold);
        _danger.Font = _dangerFont; _danger.ForeColor = Color.MistyRose;
        _danger.BackColor = Color.FromArgb(100, 25, 25); _danger.Padding = new Padding(12);
        _full.BackColor = Color.FromArgb(125, 25, 25); _full.ForeColor = Color.White;
        _full.UseVisualStyleBackColor = false;
        _create.Click += (_, _) => CreateLocal();
        _import.Click += (_, _) => ImportPublic();
        _refresh.Click += (_, _) => Reload();
        _ordinary.Click += async (_, _) => await ApproveAsync(false);
        _full.Click += async (_, _) => await ApproveAsync(true);
        _revokeFull.Click += async (_, _) => await RevokeAsync(true);
        _revoke.Click += async (_, _) => await RevokeAsync(false);
        _copy.Click += (_, _) => CopyId();
        _copyConfiguration.Click += (_, _) =>
        {
            if (Selected is not { } row) return;
            try
            {
                _clipboard(_codexConfiguration(row.Request.ConnectionId));
                _status.Text = "Public Codex settings copied. Replace the Cortex table in your Codex configuration, then reload its connection. The credential stays in this Windows user's vault.";
            }
            catch (Exception error) { _status.Text = "Client settings could not be copied: " + error.GetType().Name + "."; }
        };
        _grid.SelectionChanged += (_, _) => { if (!_loading) UpdateSelection(); };
        _name.TextChanged += (_, _) => UpdateButtons(); _application.TextChanged += (_, _) => UpdateButtons();
        SizeChanged += (_, _) =>
        {
            foreach (var label in new[] { _guidance, _danger, _details, _status })
                label.MaximumSize = new Size(Math.Max(180, ClientSize.Width - 48), 0);
        };
        _status.Text = "Connection approvals have not been read.";
        UpdateButtons();
        _constructed = true;
        if (_autoLoad && IsHandleCreated) Reload();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_autoLoad && _constructed && !_observed && !_disposed) Reload();
    }

    internal void Reload()
    {
        if (_busy || _disposed || Disposing) return;
        var selectedId = Selected?.Request.ConnectionId;
        _loading = true;
        try
        {
            var snapshot = _controller.Read();
            var observed = snapshot.Store;
            _store = observed is null ? null : CortexMcpConnections.Validate(observed) with
                { Connections = observed.Connections.ToArray() };
            if (snapshot.Pending is null || snapshot.Pending.Length > CortexMcpConnections.MaximumConnections)
                throw new InvalidDataException("Invalid public request snapshot.");
            foreach (var request in snapshot.Pending)
            {
                CortexMcpConnections.Validate(request);
                if (!_drafts.ContainsKey(request.ConnectionId) && _drafts.Count >= CortexMcpConnections.MaximumConnections)
                    throw new InvalidDataException("Too many retained public requests.");
                if (_drafts.TryGetValue(request.ConnectionId, out var held) && held != request)
                    throw new InvalidDataException("A different pending request uses the same ID.");
                _drafts[request.ConnectionId] = request;
            }
            _observed = true; _requiresRefresh = false;
            Render(selectedId);
            _status.Text = _store is null ? "No connections are approved. Public requests require explicit approval." :
                "Read current approvals · registry revision " + _store.Revision + ".";
        }
        catch (Exception error)
        {
            _observed = false; _requiresRefresh = true; _store = null; _grid.Rows.Clear();
            _status.Text = "Connection state could not be read: " + error.GetType().Name + ". Refresh before changing access.";
        }
        finally { _loading = false; UpdateSelection(); }
    }

    internal void CreateLocal()
    {
        if (!CanChange || _drafts.Count >= CortexMcpConnections.MaximumConnections) return;
        try
        {
            var draft = _controller.Create(_name.Text.Trim(), _application.Text.Trim(),
                string.IsNullOrWhiteSpace(_chat.Text) ? null : _chat.Text.Trim());
            Hold(draft.Request);
            _status.Text = draft.Warning ?? "Local credential created in the Windows user vault. Approval is a separate action.";
        }
        catch (Exception error) { _status.Text = "Local connection creation was not confirmed: " + error.GetType().Name + "."; }
    }

    internal void ImportPublic()
    {
        if (!CanChange || _drafts.Count >= CortexMcpConnections.MaximumConnections) return;
        var path = _chooseImport();
        if (path is null) return;
        try
        {
            var draft = _controller.Import(path);
            Hold(draft.Request);
            _status.Text = draft.Warning ?? "Public request retained. Review its declared labels before approving.";
        }
        catch (Exception error) { _status.Text = "Public request could not be imported: " + error.GetType().Name + "."; }
    }

    private void Hold(CortexMcpConnectionRequest request)
    {
        request = CortexMcpConnections.Validate(request);
        if (!_drafts.ContainsKey(request.ConnectionId) && _drafts.Count >= CortexMcpConnections.MaximumConnections)
            throw new InvalidDataException("Too many retained public requests.");
        _drafts[request.ConnectionId] = request;
        _loading = true;
        try { Render(request.ConnectionId); }
        finally { _loading = false; UpdateSelection(); }
    }

    private void Render(string? selectedId)
    {
        _grid.Rows.Clear();
        var actual = (_store?.Connections ?? []).ToDictionary(item => item.Request.ConnectionId, StringComparer.Ordinal);
        var ids = actual.Keys.Concat(_drafts.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            actual.TryGetValue(id, out var grant);
            var draft = _drafts.GetValueOrDefault(id);
            var request = grant?.Request ?? draft!;
            var matches = grant is null || draft is null || grant.Request.CredentialSha256 == draft.CredentialSha256;
            var row = new ConnectionRow(request, grant, matches);
            var index = _grid.Rows.Add(request.Name, request.Application, request.ChatLabel ?? "",
                id, !matches ? "Conflict" : grant is null ? "Not approved" : grant.Enabled ? "Approved" : "Revoked",
                grant?.FullControl == true ? "GRANTED" : "Off");
            _grid.Rows[index].Tag = row;
            if (grant?.FullControl == true) _grid.Rows[index].Cells[5].Style.ForeColor = Color.Red;
            if (id == selectedId) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }
        if (selectedId is null && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
    }

    internal async Task ApproveAsync(bool fullControl)
    {
        var row = Selected;
        if (!CanChange || row is null || !row.Matches ||
            (fullControl ? row.Grant?.FullControl == true : row.Grant?.Enabled == true)) return;
        if (fullControl && !_confirmFull(row.Request)) return;
        var expected = row.Grant?.Revision ?? 0;
        await ChangeAsync(() => _controller.ApproveAsync(row.Request, expected, fullControl, _lifetime.Token),
            () => CurrentGrant(row.Request.ConnectionId) is { Enabled: true } saved &&
                expected < long.MaxValue && saved.Revision == expected + 1 &&
                saved.FullControl == fullControl && saved.Request == row.Request);
    }

    internal async Task RevokeAsync(bool fullControlOnly)
    {
        var row = Selected;
        if (!CanChange || row is not { Grant: { Enabled: true } grant } ||
            fullControlOnly && !grant.FullControl) return;
        if (!fullControlOnly && !_confirmRevoke(row.Request)) return;
        var expected = grant.Revision;
        await ChangeAsync(() => _controller.RevokeAsync(row.Request.ConnectionId,
            expected, fullControlOnly, _lifetime.Token),
            () => CurrentGrant(row.Request.ConnectionId) is { FullControl: false } saved &&
                expected < long.MaxValue && saved.Revision == expected + 1 && saved.Enabled == fullControlOnly);
    }

    private CortexMcpConnectionGrant? CurrentGrant(string id) =>
        _store?.Connections.SingleOrDefault(item => item.Request.ConnectionId == id);

    private async Task ChangeAsync(Func<Task<CortexMcpConnectionControlResult>> action, Func<bool> observed)
    {
        _busy = true; UpdateButtons();
        CortexMcpConnectionControlResult result;
        try { result = await action(); }
        catch (Exception error) { result = new(false, "Connection change was not confirmed: " + error.GetType().Name + ". Refresh."); }
        finally { _busy = false; }
        if (_disposed || IsDisposed || Disposing) return;
        if (result.Confirmed)
        {
            Reload();
            if (_observed && observed()) _status.Text = "Connection change confirmed from current approvals.";
            else if (_observed)
            {
                _requiresRefresh = true;
                _status.Text = "The helper completed, but the selected approval is not confirmed by current state. Refresh before another action.";
                UpdateButtons();
            }
        }
        else
        {
            _requiresRefresh = true;
            _status.Text = result.Error ?? "Connection change was not confirmed. Refresh before another action.";
            UpdateButtons();
        }
    }

    private void CopyId()
    {
        if (Selected is { } row)
        {
            try { _clipboard(row.Request.ConnectionId); _status.Text = "Full connection ID copied."; }
            catch (Exception error) { _status.Text = "The connection ID could not be copied: " + error.GetType().Name + "."; }
        }
    }

    private void UpdateSelection()
    {
        var row = Selected;
        _id.Text = row?.Request.ConnectionId ?? "";
        _details.Text = row is null ? "Select a connection or public request." :
            !row.Matches ? "The pending request conflicts with the stored credential. Approval is unavailable; the current grant can still be revoked." :
            row.Grant is null ? "Public enrollment request · not approved." :
            "Observed grant revision " + row.Grant.Revision + " · " +
                (row.Grant.Enabled ? "ordinary access approved" : "all access revoked") + " · Full control " +
                (row.Grant.FullControl ? "GRANTED to every chat sharing this credential." : "off.");
        UpdateButtons();
    }

    private bool CanChange => _observed && !_busy && !_requiresRefresh && !_disposed && !Disposing;
    private ConnectionRow? Selected => _grid.CurrentRow?.Tag as ConnectionRow;
    private void UpdateButtons()
    {
        if (_disposed) return;
        var row = Selected;
        _grid.Enabled = !_busy; _refresh.Enabled = !_busy;
        _name.Enabled = _application.Enabled = _chat.Enabled = !_busy;
        _create.Enabled = CanChange && _drafts.Count < CortexMcpConnections.MaximumConnections &&
            !string.IsNullOrWhiteSpace(_name.Text) && !string.IsNullOrWhiteSpace(_application.Text);
        _import.Enabled = CanChange && _drafts.Count < CortexMcpConnections.MaximumConnections;
        _copy.Enabled = !_busy && row is not null;
        _copyConfiguration.Enabled = CanChange && row?.Matches == true && row.Grant?.Enabled == true;
        _ordinary.Enabled = CanChange && row?.Matches == true && row.Grant?.Enabled != true;
        _full.Enabled = CanChange && row?.Matches == true && row.Grant?.FullControl != true;
        _revoke.Enabled = CanChange && row?.Grant?.Enabled == true;
        _revokeFull.Enabled = CanChange && row?.Grant?.FullControl == true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); _dangerFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Label Label(string accessible, string text = "") => new()
    {
        AccessibleName = accessible, Text = text, AutoSize = true,
        MaximumSize = new Size(850, 0), Margin = new Padding(0, 4, 0, 6)
    };
    private static Button Button(string text) => new() { Text = text, AccessibleName = text, AutoSize = true };
    private sealed record ConnectionRow(CortexMcpConnectionRequest Request, CortexMcpConnectionGrant? Grant, bool Matches);
}
