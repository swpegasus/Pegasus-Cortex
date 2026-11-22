using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Tray;

internal interface ICortexConnectionProfileController
{
    Task<OperationReceipt> ExecuteAsync(string action, string body, string? rid, CancellationToken token);
}

internal sealed class CortexConnectionProfileController(Func<IOperationDispatcher>? create = null) : ICortexConnectionProfileController
{
    public async Task<OperationReceipt> ExecuteAsync(string action, string body, string? rid, CancellationToken token)
    {
        var request = new OperationRequest("connection_profile", new Dictionary<string, string> { ["op"] = action }, rid, Body: body);
        _ = ConnectionProfileOperationContract.Parse(request);
        await using var dispatcher = (create ?? (() => new RoutingDispatcher()))();
        return await dispatcher.DispatchAsync(request, token);
    }
}

/// <summary>Explicit nonsecret profile edits and pinned local mapping observations; no opening probes.</summary>
internal sealed class CortexConnectionProfilesPage : UserControl
{
    private readonly ICortexConnectionProfileController _controller;
    private readonly Func<string, bool> _confirm;
    private readonly CancellationTokenSource _reads = new();
    private readonly ListBox _profiles = new() { Dock = DockStyle.Fill, AccessibleName = "Saved connection profiles", IntegralHeight = false };
    private readonly TextBox _id = Entry("Profile ID", 64), _label = Entry("Profile label", 80), _share = Entry("Remote SMB share", 240),
        _drive = Entry("Local drive", 2), _remoteUser = Entry("Remote SMB user", 256), _passwordReference = Entry("SMB password reference", 64);
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, AccessibleName = "Connection identity" };
    private readonly Label _pin = new() { AutoSize = true, AccessibleName = "Profile revision", MaximumSize = new Size(600, 0) };
    private readonly Label _status = new() { AutoSize = true, AccessibleName = "Connection profile status", MaximumSize = new Size(850, 0) };
    private readonly Button _load = Button("Load profiles"), _next = Button("Next page"), _new = Button("New profile"),
        _refresh = Button("Refresh profile"), _save = Button("Save profile"), _remove = Button("Remove profile"),
        _inspect = Button("Inspect drive"), _connect = Button("Connect drive"), _disconnect = Button("Disconnect drive");
    private SmbConnectionProfileSnapshot? _snapshot;
    private string? _loadedId, _nextId;
    private long _revision, _epoch;
    private bool _busy, _confirming, _updating, _resourcesDisposed;
    private Task _pending = Task.CompletedTask;
    private Unconfirmed? _unconfirmed;
    private sealed record Unconfirmed(string Id, SmbConnectionProfileSnapshot? Mapping);
    private sealed record Item(SmbConnectionProfileSnapshot Snapshot)
    { public override string ToString() => Snapshot.Profile.Label + " (" + Snapshot.Profile.Id + ")"; }

    internal CortexConnectionProfilesPage(ICortexConnectionProfileController? controller = null,
        Func<string, bool>? confirm = null, Action<Control>? theme = null)
    {
        _controller = controller ?? new CortexConnectionProfileController();
        _confirm = confirm ?? (message => MessageBox.Show(this, message, "Connection profile",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
        _mode.Items.AddRange(["Current Windows user", "Explicit remote account"]);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text =
            "Connection profiles contain no passwords. Use an SMB password reference for an explicit remote account. " +
            "Opening this page does not read saved profiles, inspect a drive or connect. Mapping observations do not verify remote authentication." }, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(920, 380), SplitterDistance = 220, Panel1MinSize = 160, Panel2MinSize = 350 };
        split.Panel1.Controls.Add(_profiles);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };
        AddField(fields, "ID", _id); AddField(fields, "Label", _label); AddField(fields, @"Share (\\server\share)", _share);
        AddField(fields, "Drive (for example Z:)", _drive); AddField(fields, "Identity", _mode);
        AddField(fields, "Remote user (nonsecret)", _remoteUser); AddField(fields, "Password reference", _passwordReference);
        AddField(fields, "Selected revision", _pin);
        split.Panel2.AutoScroll = true; split.Panel2.Controls.Add(fields); root.Controls.Add(split, 0, 1);
        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        commands.Controls.AddRange([_load, _next, _new, _refresh, _save, _remove, _inspect, _connect, _disconnect]);
        root.Controls.Add(commands, 0, 2); root.Controls.Add(_status, 0, 3); Controls.Add(root);
        _profiles.SelectedIndexChanged += (_, _) =>
        { if (!_updating && _profiles.SelectedItem is Item selected) { _epoch++; Apply(selected.Snapshot); } };
        _mode.SelectedIndexChanged += (_, _) => UpdateControls();
        _id.TextChanged += (_, _) => { if (!_updating) { _epoch++; UpdateControls(); } };
        _load.Click += async (_, _) => await LoadAsync(); _next.Click += async (_, _) => await LoadAsync(next: true);
        _new.Click += (_, _) => NewProfile(); _refresh.Click += async (_, _) => await RefreshAsync();
        _save.Click += async (_, _) => await SaveAsync(); _remove.Click += async (_, _) => await RemoveAsync();
        _inspect.Click += async (_, _) => await InspectAsync(); _connect.Click += async (_, _) => await MapAsync("connect");
        _disconnect.Click += async (_, _) => await MapAsync("disconnect");
        NewProfile(); _status.Text = "Profiles have not been loaded.";
        (theme ?? CortexTheme.Apply)(this);
    }

    internal void NewProfile()
    {
        if (!CanStart() || _unconfirmed is not null) return;
        ResetDraft();
    }
    private void ResetDraft()
    {
        _epoch++; _snapshot = null; _loadedId = null; _revision = 0;
        _updating = true; try { _profiles.ClearSelected(); } finally { _updating = false; }
        _id.Text = _label.Text = _remoteUser.Text = _passwordReference.Text = string.Empty;
        _share.Text = @"\\"; _drive.Text = "Z:"; _mode.SelectedIndex = 0;
        _pin.Text = "New profile; save expects no previous revision."; UpdateControls();
    }

    internal Task LoadAsync(bool next = false)
    {
        if (next && _nextId is null) return Task.CompletedTask;
        var afterId = next ? _nextId : null;
        var body = Base(); body["limit"] = 32; if (next) body["afterId"] = afterId;
        return RunAsync("list", body, null, null, receipt =>
        {
            var array = Data(receipt, "profiles");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 32) throw Invalid();
            var snapshots = array.EnumerateArray().Select(ParseSnapshot).ToArray();
            if (snapshots.Select(item => item.Profile.Id).Distinct(StringComparer.Ordinal).Count() != snapshots.Length) throw Invalid();
            var nextId = OptionalText(receipt, "nextId");
            if (nextId is not null)
            {
                // Store cursors advance over removed-profile tombstones too;
                // an empty visible page can still have a valid next position.
                SmbConnectionProfile.RequireReference(nextId);
                if (afterId is not null && StringComparer.Ordinal.Compare(nextId, afterId) <= 0) throw Invalid();
            }
            if (snapshots.Any(item => afterId is not null && StringComparer.Ordinal.Compare(item.Profile.Id, afterId) <= 0 ||
                nextId is not null && StringComparer.Ordinal.Compare(item.Profile.Id, nextId) > 0)) throw Invalid();
            _updating = true;
            try
            {
                _profiles.Items.Clear(); _profiles.Items.AddRange(snapshots.Select(item => new Item(item)).Cast<object>().ToArray());
                var pinnedId = _unconfirmed?.Id;
                _profiles.SelectedIndex = pinnedId is null ? snapshots.Length == 0 ? -1 : 0
                    : Array.FindIndex(snapshots, item => item.Profile.Id == pinnedId);
            }
            finally { _updating = false; }
            _nextId = nextId; _epoch++;
            if (_unconfirmed is null)
            {
                if (snapshots.Length > 0) Apply(snapshots[0]); else ResetDraft();
                _status.Text = snapshots.Length + " saved profiles in this page." + (nextId is null ? string.Empty : " More profiles are available.");
            }
            else ShowUnconfirmed();
        });
    }

    internal Task RefreshAsync()
    {
        var id = _unconfirmed?.Id ?? _id.Text;
        try { SmbConnectionProfile.RequireReference(id); } catch (SmbConnectionException) { ShowInvalid(); return Task.CompletedTask; }
        var body = Base(); body["id"] = id;
        return RunAsync("read", body, id, null, receipt =>
        {
            var wasUnconfirmed = _unconfirmed is { Mapping: null } change && change.Id == id;
            if (Data(receipt, "snapshot").ValueKind == JsonValueKind.Null && OptionalText(receipt, "state") is "missing" or "removed")
            {
                _snapshot = null; _loadedId = id; _id.Text = id; _revision = Number(receipt, "revision", minimum: 0);
                _pin.Text = "No current profile; observed revision " + _revision + ".";
            }
            else
            {
                var snapshot = ParseSnapshot(Data(receipt, "snapshot"));
                if (snapshot.Profile.Id != id) throw Invalid();
                Apply(snapshot);
            }
            if (wasUnconfirmed) _unconfirmed = null;
            _status.Text = "Saved profile refreshed." + (wasUnconfirmed ? " The earlier change remains unconfirmed; a new change must be deliberate." : string.Empty);
            if (_unconfirmed is not null) ShowUnconfirmed();
        });
    }

    internal Task SaveAsync()
    {
        if (_unconfirmed is not null) return Task.CompletedTask;
        SmbConnectionProfile profile;
        try
        {
            profile = SmbConnectionProfile.Validate(new(1, _id.Text, _label.Text, _share.Text, _drive.Text,
                _mode.SelectedIndex == 0 ? "currentUser" : "explicitRemote",
                _mode.SelectedIndex == 0 ? null : _remoteUser.Text, _mode.SelectedIndex == 0 ? null : _passwordReference.Text));
            if (_loadedId is not null && _loadedId != profile.Id) throw Invalid();
        }
        catch (Exception error) when (error is SmbConnectionException or InvalidDataException) { ShowInvalid(); return Task.CompletedTask; }
        var body = Base(); body["profile"] = JsonSerializer.SerializeToNode(profile, SmbConnectionProfile.Json); body["expectedRevision"] = _revision;
        var expectedRevision = _revision;
        return RunAsync("save", body, profile.Id, null, receipt =>
        {
            var saved = ParseSnapshot(Data(receipt, "snapshot"));
            if (saved.Profile != profile || saved.Revision != expectedRevision + 1) throw Invalid();
            Apply(saved); _status.Text = "Profile saved at revision " + saved.Revision + ". No drive was connected.";
        });
    }

    internal Task RemoveAsync()
    {
        var selected = _snapshot;
        if (selected is null || _unconfirmed is not null || !Confirm("Remove saved profile '" + selected.Profile.Id + "'? This does not disconnect a drive or remove its password reference.", selected))
            return Task.CompletedTask;
        var body = Base(); body["id"] = selected.Profile.Id; body["expectedRevision"] = selected.Revision;
        return RunAsync("remove", body, selected.Profile.Id, null, receipt =>
        {
            if (OptionalText(receipt, "state") != "removed") throw Invalid();
            var revision = Number(receipt, "revision", 1); if (revision != selected.Revision + 1) throw Invalid();
            _snapshot = null; _revision = revision; _loadedId = selected.Profile.Id;
            _updating = true;
            try
            {
                for (var index = _profiles.Items.Count - 1; index >= 0; index--)
                    if (_profiles.Items[index] is Item item && item.Snapshot.Profile.Id == selected.Profile.Id) _profiles.Items.RemoveAt(index);
            }
            finally { _updating = false; }
            _pin.Text = "Removed; observed revision " + _revision + ".";
            _status.Text = "Saved profile removed. The drive mapping and password reference were not changed.";
        });
    }

    internal Task InspectAsync()
    {
        var selected = _unconfirmed?.Mapping ?? _snapshot;
        if (selected is null) return Task.CompletedTask;
        return RunAsync("inspect", Pinned(selected), selected.Profile.Id, null, receipt =>
        {
            var text = OptionalText(receipt, "state") switch
            {
                "mapped" => "The selected drive/share mapping was observed.", "free" => "The selected drive is free.",
                "occupied" => "The selected drive is occupied by another mapping or device.",
                "remembered" => "The selected drive has a remembered mapping.", _ => throw Invalid()
            };
            var earlier = _unconfirmed?.Mapping is not null;
            if (earlier) _unconfirmed = null;
            _status.Text = text + " Remote authentication has not been verified." +
                (earlier ? " The earlier action remains unconfirmed; a new action must be deliberate." : string.Empty);
        });
    }

    internal Task MapAsync(string action)
    {
        if (action is not ("connect" or "disconnect")) throw new ArgumentOutOfRangeException(nameof(action));
        var selected = _snapshot;
        if (selected is null || _unconfirmed is not null || !Confirm((action == "connect" ? "Connect " : "Disconnect ") +
            selected.Profile.LocalDrive + " for " + selected.Profile.RemoteShare + " using this exact saved profile?", selected)) return Task.CompletedTask;
        var body = Pinned(selected); if (action == "disconnect") body["expectedRemoteShare"] = selected.Profile.RemoteShare;
        return RunAsync(action, body, selected.Profile.Id, selected, receipt =>
        {
            var state = OptionalText(receipt, "state");
            if (state != (action == "connect" ? "connected" : "disconnected") || action == "connect" && !Flag(receipt, "mapping_observed")) throw Invalid();
            _status.Text = action == "connect" ? "The drive/share mapping was observed. Remote authentication has not been independently verified."
                : "The selected drive mapping was observed disconnected.";
        });
    }

    private Task RunAsync(string action, JsonObject body, string? id, SmbConnectionProfileSnapshot? mapping, Action<OperationReceipt> accept)
    {
        if (!CanStart()) return Task.CompletedTask;
        var mutation = !ConnectionProfileOperationContract.IsReadOnly(action);
        if (mutation && _unconfirmed is not null) return Task.CompletedTask;
        var serialized = body.ToJsonString(SmbConnectionProfile.Json);
        var rid = Guid.NewGuid().ToString("N");
        try { _ = ConnectionProfileOperationContract.Parse(new("connection_profile", new Dictionary<string, string> { ["op"] = action }, rid, Body: serialized)); }
        catch (ConnectionProfileContractException) { ShowInvalid(); return Task.CompletedTask; }
        _busy = true; UpdateControls(); var epoch = _epoch;
        // Publish ownership before invoking a controller that may synchronously
        // reenter disposal. A delivered mutation is never canceled by page close.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion.Task;
        _ = CompleteAsync();
        return completion.Task;
        async Task CompleteAsync()
        {
            var entered = false;
            try
            {
                entered = true;
                var receipt = await _controller.ExecuteAsync(action, serialized, rid, mutation ? CancellationToken.None : _reads.Token);
                var identity = receipt.Op == ConnectionProfileOperationContract.PublicName(action) && receipt.Rid == rid;
                var uncertain = mutation && (!identity || receipt.Partial || Flag(receipt, "outcome_unknown") ||
                    receipt.State is "accepted" or "queued" or "running" || !receipt.Ok && Flag(receipt, "action_issued"));
                if (uncertain) _unconfirmed = new(id!, mapping);
                if (IsDisposed || Disposing || epoch != _epoch) return;
                if (uncertain) { ShowUnconfirmed(); return; }
                if (!identity || !receipt.Ok || receipt.State != "done" || receipt.Partial || Flag(receipt, "outcome_unknown"))
                { _status.Text = "The operation was not confirmed. Refresh the profile or inspect the drive before a deliberate new attempt."; return; }
                accept(receipt);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                if (mutation && entered) _unconfirmed = new(id!, mapping);
                if (!IsDisposed && !Disposing && epoch == _epoch)
                { if (_unconfirmed is not null) ShowUnconfirmed(); else _status.Text = "The profile operation could not be observed. No automatic retry was made."; }
            }
            finally
            {
                _busy = false; if (!IsDisposed && !Disposing) UpdateControls(); completion.TrySetResult();
            }
        }
    }

    private bool Confirm(string message, SmbConnectionProfileSnapshot selected)
    {
        if (!CanStart()) return false;
        var epoch = _epoch; _confirming = true; UpdateControls();
        try { return _confirm(message) && !IsDisposed && !Disposing && epoch == _epoch && ReferenceEquals(selected, _snapshot); }
        finally { _confirming = false; if (!IsDisposed && !Disposing) UpdateControls(); }
    }
    private bool CanStart() => !IsDisposed && !Disposing && !_busy && !_confirming;
    private void Apply(SmbConnectionProfileSnapshot snapshot)
    {
        _snapshot = snapshot; _loadedId = snapshot.Profile.Id; _revision = snapshot.Revision;
        _updating = true;
        try
        {
            for (var index = 0; index < _profiles.Items.Count; index++)
                if (_profiles.Items[index] is Item item && item.Snapshot.Profile.Id == snapshot.Profile.Id)
                { _profiles.Items[index] = new Item(snapshot); break; }
        }
        finally { _updating = false; }
        _id.Text = snapshot.Profile.Id; _label.Text = snapshot.Profile.Label; _share.Text = snapshot.Profile.RemoteShare;
        _drive.Text = snapshot.Profile.LocalDrive; _mode.SelectedIndex = snapshot.Profile.IdentityMode == "currentUser" ? 0 : 1;
        _remoteUser.Text = snapshot.Profile.RemoteUser ?? string.Empty; _passwordReference.Text = snapshot.Profile.PasswordReference ?? string.Empty;
        _pin.Text = "Saved revision " + snapshot.Revision + "."; UpdateControls();
    }
    private void UpdateControls()
    {
        if (IsDisposed || Disposing) return;
        var idle = !_busy && !_confirming; var editable = idle && _unconfirmed is null;
        _profiles.Enabled = editable; _new.Enabled = editable; _id.Enabled = editable && _loadedId is null;
        _label.Enabled = _share.Enabled = _drive.Enabled = _mode.Enabled = editable;
        _remoteUser.Enabled = _passwordReference.Enabled = editable && _mode.SelectedIndex == 1;
        _load.Enabled = idle; _next.Enabled = idle && _nextId is not null; _refresh.Enabled = idle && (_id.Text.Length > 0 || _unconfirmed is not null);
        _save.Enabled = editable; _remove.Enabled = editable && _snapshot is not null;
        _inspect.Enabled = idle && (_snapshot is not null || _unconfirmed?.Mapping is not null);
        _connect.Enabled = _disconnect.Enabled = editable && _snapshot is not null;
    }
    private void ShowInvalid() { if (!IsDisposed && !Disposing) _status.Text = "Enter a valid profile ID, label, exact SMB share, drive and identity fields. No password value belongs in a profile."; }
    private void ShowUnconfirmed() => _status.Text = _unconfirmed?.Mapping is null
        ? "An earlier profile change was not confirmed. Refresh that profile before another change; this will not certify the earlier outcome."
        : "An earlier mapping action was not confirmed. Inspect that exact saved profile before another change; no retry or compensating disconnect was made.";
    private static JsonObject Base() => new() { ["version"] = 1, ["kind"] = "smb-drive" };
    private static JsonObject Pinned(SmbConnectionProfileSnapshot snapshot)
    { var body = Base(); body["id"] = snapshot.Profile.Id; body["revision"] = snapshot.Revision; body["contentHash"] = snapshot.ContentHash; return body; }
    private static SmbConnectionProfileSnapshot ParseSnapshot(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "contentHash", "profile", "revision" }) is false ||
            !value.GetProperty("revision").TryGetInt64(out var revision) || revision < 1 || revision == long.MaxValue) throw Invalid();
        var hash = value.GetProperty("contentHash").GetString();
        if (hash is not { Length: 64 } || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid();
        return new(SmbConnectionProfile.Parse(value.GetProperty("profile")), revision, hash);
    }
    private static JsonElement Data(OperationReceipt receipt, string name)
    {
        if (receipt.Data?.GetValueOrDefault(name) is not { } value) return JsonSerializer.SerializeToElement<object?>(null);
        return value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, SmbConnectionProfile.Json);
    }
    private static string? OptionalText(OperationReceipt receipt, string name)
    { var value = Data(receipt, name); return value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid(); }
    private static long Number(OperationReceipt receipt, string name, long minimum)
    { var value = Data(receipt, name); return value.TryGetInt64(out var number) && number >= minimum && number < long.MaxValue ? number : throw Invalid(); }
    private static bool Flag(OperationReceipt receipt, string name) => Data(receipt, name).ValueKind == JsonValueKind.True;
    private static InvalidDataException Invalid() => new("connection_profile_response_invalid");
    private static TextBox Entry(string name, int max) => new() { Width = 340, MaxLength = max, AccessibleName = name };
    private static Button Button(string name) => new() { AutoSize = true, Text = name, AccessibleName = name };
    private static void AddField(TableLayoutPanel table, string name, Control control)
    { var row = table.RowCount++; table.Controls.Add(new Label { AutoSize = true, Text = name, Padding = new Padding(0, 6, 8, 0) }, 0, row); table.Controls.Add(control, 1, row); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            try { _reads.Cancel(); }
            catch (AggregateException) { /* A read observer cannot prevent owned callback teardown. */ }
            finally
            {
                if (_pending.IsCompleted) _reads.Dispose();
                else _ = _pending.ContinueWith(_ => _reads.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        base.Dispose(disposing);
    }
}
