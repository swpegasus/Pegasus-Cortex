using System.Globalization;
using Pegasus.Cortex.Mcp.Local.Runtime;

namespace Pegasus.AgentOps.Tray;

internal sealed class CortexPayloadLifecyclePage : UserControl
{
    private const string CleanupIssue = "payload_policy_cleanup_required";
    private const string ReviewIssue = "payload_policy_review_required";
    private readonly ICortexPayloadLifecycleClient _client;
    private readonly Func<bool> _confirmPrepare;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolTip _tips = new();
    private readonly CheckBox _enabled = new() { AutoSize = true, Text = "Automatically rotate archived payloads", AccessibleName = "Enable automatic payload rotation" };
    private readonly TextBox _namespaces = new() { Multiline = true, Width = 800, Height = 76, MaxLength = 1100,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Payload namespaces" };
    private readonly NumericUpDown _age = Number(1, 36500, 30, "Minimum archive age days");
    private readonly NumericUpDown _target = Number(0, 16L * 1024 * 1024, 0, "Soft hot storage target MiB");
    private readonly NumericUpDown _interval = Number(1, 1440, 60, "Rotation interval minutes");
    private readonly NumericUpDown _milliseconds = Number(50, 10000, 1000, "Cooperative time budget milliseconds");
    private readonly NumericUpDown _heads = Number(256, 65536, 16384, "Metadata work per pass");
    private readonly Button _refresh = new() { AutoSize = true, Text = "Refresh", AccessibleName = "Refresh storage status" };
    private readonly Button _save = new() { AutoSize = true, Text = "Save policy", AccessibleName = "Save storage policy" };
    private readonly Button _run = new() { AutoSize = true, Text = "Run one pass", AccessibleName = "Run storage pass" };
    private readonly Button _prepare = new() { AutoSize = true, Text = "Prepare cold storage…", AccessibleName = "Prepare cold storage" };
    private readonly Label _savedLabel = TextLabel("Saved storage policy", "Saved policy has not been read.");
    private readonly Label _ownerLabel = TextLabel("Observed storage owner", "Current owner status has not been checked.");
    private readonly Label _lastLabel = TextLabel("Last storage pass", "No pass has been observed.");
    private readonly Label _status = TextLabel("Storage page status", "Refresh to read the existing Cortex owner's policy and status.");
    private CortexPayloadLifecycleStatus? _observed;
    private CortexPayloadLifecycleSettings? _saved;
    private bool _busy, _loading, _requiresRefresh = true, _resourcesDisposed, _reviewedPolicy;
    private long _generation;

    internal CortexPayloadLifecyclePage(ICortexPayloadLifecycleClient? client = null,
        Action<Control>? theme = null, Func<bool>? confirmPrepare = null, bool autoLoad = true)
    {
        _client = client ?? new CortexPayloadLifecycleClient();
        _confirmPrepare = confirmPrepare ?? (() => MessageBox.Show(this,
            "Prepare the existing owner's database for verified cold payload storage? This updates its data format. " +
            "Record history and replay information are retained. Running work must drain first. This does not enable rotation or delete records.",
            "Prepare cold storage", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20) };
        body.Controls.Add(new Label { Text = "Archived payload storage", AutoSize = true, Font = CortexTheme.HeadingFont });
        body.Controls.Add(TextLabel("Storage guidance", "Rotate old archived payloads into verified cold storage while keeping their records and exact history available. " +
            "Pins, holds and retained references protect needed data. This policy does not delete records, reclaim SQLite file space or impose an admission quota."));
        body.Controls.Add(_enabled);
        body.Controls.Add(TextLabel("Namespace entry guidance", "Namespaces (one per line; only explicitly listed namespaces are considered)"));
        body.Controls.Add(_namespaces);
        AddNumber(body, "Archived for at least (days)", _age);
        AddNumber(body, "Soft hot storage target (MiB; 0 rotates by age)", _target);
        AddNumber(body, "Check interval (minutes)", _interval);
        AddNumber(body, "Cooperative time budget (ms)", _milliseconds);
        AddNumber(body, "Metadata work per pass", _heads);
        body.Controls.Add(TextLabel("Work budget guidance", "Work limits are cooperative, not guaranteed elapsed deadlines. A deferred pass may need a larger budget. " +
            "A soft size target can remain unmet when active, protected or shared data must stay hot."));
        body.Controls.Add(_savedLabel); body.Controls.Add(_ownerLabel); body.Controls.Add(_lastLabel);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange([_save, _refresh, _run, _prepare]); body.Controls.Add(buttons); body.Controls.Add(_status);
        Controls.Add(body);
        _enabled.CheckedChanged += (_, _) => Changed(); _namespaces.TextChanged += (_, _) => Changed();
        foreach (var input in Inputs()) input.ValueChanged += (_, _) => Changed();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _run.Click += async (_, _) => await RunOnceAsync();
        _prepare.Click += async (_, _) => await PrepareAsync();
        if (autoLoad) Load += async (_, _) => await RefreshAsync();
        UpdateControls();
        (theme ?? CortexTheme.Apply)(this);
    }

    internal async Task RefreshAsync()
    {
        if (!CanStart()) return;
        await PerformAsync(_client.ReadAsync, "Reading the existing owner's storage policy…", mutation: false);
    }

    internal async Task SaveAsync()
    {
        if (!CanStart() || _saved is null || _requiresRefresh) return;
        CortexPayloadLifecyclePolicy policy;
        try { policy = ReadSelection(); }
        catch (ArgumentException) { _status.Text = "Review the namespaces and policy values before saving."; return; }
        var reviewed = _reviewedPolicy && IsReviewRequired(_observed);
        if (!reviewed && CortexPayloadLifecycleClient.SamePolicy(policy, _saved.Policy)) return;
        var expected = _saved.Revision;
        await PerformAsync(token => _client.SaveAsync(policy, expected, token), reviewed
            ? "Making a new save of the reviewed policy…" : "Saving the storage policy…", mutation: true);
    }

    internal async Task RunOnceAsync()
    {
        if (!CanStart() || _requiresRefresh || _saved?.Policy.Enabled != true || !CanRunObserved()) return;
        await PerformAsync(_client.RunOnceAsync, "Waiting for the original bounded storage pass…", mutation: true);
    }

    internal async Task PrepareAsync()
    {
        if (!CanStart() || _requiresRefresh || IsReviewRequired(_observed) || _observed?.OwnerBound != true || _observed.StoreSchemaVersion != 2 || _observed.PassRunning ||
            !_confirmPrepare()) return;
        await PerformAsync(_client.UpgradeAsync, "Preparing cold storage through the existing owner…", mutation: true);
    }

    private async Task PerformAsync(Func<CancellationToken, Task<CortexPayloadLifecycleClientResult>> action,
        string pendingText, bool mutation)
    {
        var generation = ++_generation;
        _reviewedPolicy = false;
        _busy = true; _status.Text = pendingText; UpdateControls();
        try
        {
            var result = await action(_lifetime.Token);
            if (!IsCurrent(generation)) return;
            if (!mutation && (result.State is "done" or "refused") && result.Status is { } reviewedStatus && IsReviewRequired(reviewedStatus))
            {
                ApplyStatus(reviewedStatus);
                _reviewedPolicy = true; _requiresRefresh = false;
                _status.Text = reviewedStatus.Issue == CleanupIssue
                    ? "The selected saved policy is verified, but cleanup of its previous file remains. Review the values, then choose " +
                        "Finish cleanup and save policy to make a new save using the displayed revision. Rotation stays paused until that save is confirmed."
                    : "The selected saved policy is now verified. Review the values, then choose Review and save policy to make a new save " +
                        "using the displayed revision. This does not retry the earlier request. Rotation stays paused until the new save is confirmed.";
            }
            else if (result.State is "done" or "done_cleanup_incomplete" && result.Status is { } status)
            {
                ApplyStatus(status);
                _requiresRefresh = result.State != "done" || IsReviewRequired(status);
                _status.Text = result.State == "done_cleanup_incomplete"
                    ? "The owner's result was confirmed, but follow-up cleanup was incomplete. Refresh before another change."
                    : mutation ? "The owner's result was confirmed. The observed status is shown above." : "Owner status refreshed.";
            }
            else
            {
                if (result.Status is { } retained) ApplyStatus(retained);
                if (result.State == "prepared_status_unavailable")
                    _status.Text = "The preparation request returned success, but cold storage readiness has not been verified. Refresh before another change.";
                else _status.Text = (result.State == "unknown" ? "The original outcome was not confirmed. " : "The request was not completed. ") +
                    Safe(result.Error ?? result.State) + " Refresh and review before another change.";
                _requiresRefresh = true;
                if (!mutation && result.Status is null)
                {
                    _saved = null; _observed = null;
                    _savedLabel.Text = "Saved policy could not be read.";
                    _ownerLabel.Text = "Current owner status is unavailable.";
                    _lastLabel.Text = "Current pass results are unavailable.";
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (!IsCurrent(generation)) return;
            _requiresRefresh = true;
            _status.Text = "The request was not confirmed (" + error.GetType().Name + "). Refresh before another change.";
        }
        finally
        {
            if (IsCurrent(generation)) { _busy = false; UpdateControls(); }
        }
    }

    private void ApplyStatus(CortexPayloadLifecycleStatus status)
    {
        if (status.Schema != CortexPayloadLifecycleClient.StatusSchema) throw new InvalidDataException("lifecycle_status_invalid");
        var saved = status.Saved is { } value ? value with { Policy = CortexPayloadLifecycleSettingsStore.Validate(value.Policy) } : null;
        if (saved?.Revision < 0) throw new InvalidDataException("lifecycle_status_invalid");
        _observed = status with { Saved = saved }; _saved = saved;
        _loading = true;
        try
        {
            if (saved is not null)
            {
                _enabled.Checked = saved.Policy.Enabled; _namespaces.Text = string.Join(Environment.NewLine, saved.Policy.Namespaces);
                _age.Value = saved.Policy.MinimumArchiveAgeDays; _target.Value = saved.Policy.TargetHotMiB;
                _interval.Value = saved.Policy.IntervalMinutes; _milliseconds.Value = saved.Policy.WorkMilliseconds;
                _heads.Value = saved.Policy.MaximumMetadataHeads;
            }
        }
        finally { _loading = false; }
        _savedLabel.Text = saved is null ? "Saved policy is unavailable." :
            "Saved policy r" + saved.Revision.ToString(CultureInfo.CurrentCulture) + ": " + (saved.Policy.Enabled ? "enabled" : "disabled") +
            "; " + saved.Policy.Namespaces.Count.ToString(CultureInfo.CurrentCulture) + " selected namespaces.";
        _ownerLabel.Text = !status.OwnerBound ? "The storage owner is not bound; no running policy is claimed." :
            "Observed owner: " + Safe(status.State).Replace('_', ' ') + ". " + (status.PassRunning
                ? saved?.Policy.Enabled == false ? "Policy is disabled; the original pass is still stopping." : "A pass is running."
                : "No pass is currently running.") +
            (status.StoreSchemaVersion == 2 ? " Explicit cold storage preparation is required before rotation." :
                status.StoreSchemaVersion is null ? " The current storage format has not been checked." :
                " The storage format supports cold payloads.") +
            (status.Issue is null ? "" : " Issue: " + Safe(status.Issue));
        var last = status.LastFinishedUtc is { } time ? CortexDisplayFormat.Timestamp(time.ToString("O", CultureInfo.InvariantCulture)).Text : "not observed";
        _lastLabel.Text = "Last finished: " + last + ". Evicted from hot storage: " +
            (status.HotBytesEvicted is { } bytes ? CortexDisplayFormat.Bytes(bytes).Text : "unavailable") +
            "; selected records: " + (status.SelectedRecords is { } records ? CortexDisplayFormat.Count(records).Text : "unavailable") +
            "; target: " + (status.TargetSatisfied is { } met ? met ? "satisfied" : "not satisfied" : "unavailable") + "." +
            (status.ImmutableObjectsMayRemain ? " Verified immutable staging may remain; no record deletion was performed." : "");
        _tips.SetToolTip(_lastLabel, "Hot bytes evicted: " + (status.HotBytesEvicted?.ToString(CultureInfo.InvariantCulture) ?? "unavailable") +
            "; metadata observations: " + (status.MetadataObservations?.ToString(CultureInfo.InvariantCulture) ?? "unavailable") +
            "; stored finish UTC: " + (status.LastFinishedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable"));
    }

    private CortexPayloadLifecyclePolicy ReadSelection() => CortexPayloadLifecycleSettingsStore.Validate(new(1, _enabled.Checked,
        _namespaces.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        decimal.ToInt32(_age.Value), decimal.ToInt64(_target.Value), decimal.ToInt32(_interval.Value),
        decimal.ToInt32(_milliseconds.Value), decimal.ToInt32(_heads.Value)));
    private void Changed() { if (!_loading && CanStart()) UpdateControls(); }
    private void UpdateControls()
    {
        if (_resourcesDisposed || IsDisposed || Disposing) return;
        var editable = !_busy && _saved is not null && !_requiresRefresh;
        _enabled.Enabled = editable; _namespaces.Enabled = editable;
        foreach (var input in Inputs()) input.Enabled = editable;
        var changed = false;
        if (editable)
            try { changed = !CortexPayloadLifecycleClient.SamePolicy(ReadSelection(), _saved!.Policy); }
            catch (ArgumentException) { }
        var reviewed = _reviewedPolicy && IsReviewRequired(_observed);
        _save.Text = _observed?.Issue == CleanupIssue ? "Finish cleanup and save policy" :
            _observed?.Issue == ReviewIssue ? "Review and save policy" : "Save policy";
        _save.AccessibleDescription = IsReviewRequired(_observed)
            ? "Make a new reviewed save at the actual displayed revision; this does not retry the prior request." : "Save the changed storage policy.";
        _save.Enabled = editable && (changed || reviewed);
        _refresh.Enabled = !_busy;
        _run.Enabled = editable && !changed && _saved!.Policy.Enabled && CanRunObserved();
        _prepare.Enabled = !_busy && !_requiresRefresh && !IsReviewRequired(_observed) && _observed is { OwnerBound: true, StoreSchemaVersion: 2, PassRunning: false };
    }
    private static bool IsReviewRequired(CortexPayloadLifecycleStatus? status) =>
        status is { Issue: CleanupIssue or ReviewIssue, Saved.Revision: > 0 };
    private bool CanRunObserved() => _observed is { OwnerBound: true, StoreSchemaVersion: 3, PassRunning: false } status &&
        status.State != "paused" && !IsReviewRequired(status);
    private IEnumerable<NumericUpDown> Inputs() => [_age, _target, _interval, _milliseconds, _heads];
    private bool CanStart() => !_busy && !_resourcesDisposed && !IsDisposed && !Disposing;
    private bool IsCurrent(long generation) => generation == _generation && !_resourcesDisposed && !IsDisposed && !Disposing;
    private static string Safe(string text) => new(text.Take(256).Where(c => !char.IsControl(c)).ToArray());
    private static Label TextLabel(string name, string text = "") => new()
        { AutoSize = true, MaximumSize = new Size(870, 0), Text = text, AccessibleName = name, Margin = new Padding(0, 6, 0, 6) };
    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value, string name) => new()
        { Minimum = minimum, Maximum = maximum, Value = value, Width = 150, ThousandsSeparator = true, AccessibleName = name };
    private static void AddNumber(Control parent, string label, NumericUpDown number)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(new Label { Text = label, Width = 405, AutoSize = false, Height = 28, Padding = new Padding(0, 5, 8, 0) });
        row.Controls.Add(number); parent.Controls.Add(row);
    }
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && !_resourcesDisposed)
            {
                _resourcesDisposed = true; _generation++;
                try { _lifetime.Cancel(); }
                finally { _lifetime.Dispose(); _tips.Dispose(); }
            }
        }
        finally { base.Dispose(disposing); }
    }
}
