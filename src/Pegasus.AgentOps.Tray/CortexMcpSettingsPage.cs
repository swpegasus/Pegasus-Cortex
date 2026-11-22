using System.Net;
using Pegasus.Cortex.Mcp.Hosting;

namespace Pegasus.AgentOps.Tray;

internal sealed class CortexMcpSettingsPage : UserControl
{
    private readonly Func<CortexMcpSettings> _read;
    private readonly ICortexMcpSettingsController _controller;
    private readonly ICortexMcpSetupController _setupController;
    private readonly Func<IDisposable>? _beginSetupOperation;
    private readonly Func<bool> _confirmSetup;
    private readonly Func<CortexMcpSetupRetry, bool> _confirmRetry;
    private readonly CheckBox _enabled = new()
    {
        AutoSize = true, Text = "Enable shared HTTPS MCP", AccessibleName = "Enable shared HTTPS MCP"
    };
    private readonly ComboBox _owner = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, AccessibleName = "Configured MCP owner"
    };
    private readonly Button _save = new() { AutoSize = true, Text = "Save MCP settings…", AccessibleName = "Save MCP settings" };
    private readonly Button _refresh = new() { AutoSize = true, Text = "Refresh", AccessibleName = "Refresh saved MCP settings" };
    private readonly Button _setup = new() { AutoSize = true, Text = "Set up and save MCP…", AccessibleName = "Set up shared MCP" };
    private readonly Button _retrySetup = new() { AutoSize = true, Text = "Review and retry canceled step…", AccessibleName = "Review and retry canceled MCP step" };
    private readonly TextBox _issuerReference = new() { Width = 350, MaxLength = 64, AccessibleName = "Existing MCP certificate authority reference" };
    private readonly Label _savedSelection = TextLabel("Saved MCP selection");
    private readonly Label _endpoint = TextLabel("Configured MCP endpoint");
    private readonly Label _certificates = TextLabel("Selected owner certificate references");
    private readonly Label _ownerHelp = TextLabel("MCP owner guidance");
    private readonly Label _selection = TextLabel("MCP selection changes");
    private readonly Label _status = TextLabel("MCP settings status");
    private CortexMcpSettings? _saved;
    private bool _loading;
    private bool _busy;
    private bool _requiresRefresh;
    private CortexMcpSetupRequest? _retryRequest;
    private CortexMcpSetupRetry? _retryStep;

    internal CortexMcpSettingsPage(Func<CortexMcpSettings>? read = null,
        ICortexMcpSettingsController? controller = null, Action<Control>? theme = null,
        ICortexMcpSetupController? setupController = null, Func<IDisposable>? beginSetupOperation = null,
        Func<bool>? confirmSetup = null, Func<CortexMcpSetupRetry, bool>? confirmRetry = null)
    {
        _read = read ?? CortexMcpSettingsControl.ReadCurrent;
        _controller = controller ?? new CortexMcpSettingsControl();
        _setupController = setupController ?? new CortexMcpSetupControl();
        _beginSetupOperation = beginSetupOperation;
        _confirmSetup = confirmSetup ?? (() => MessageBox.Show(this,
            "Set up the selected MCP owner and save this selection? Missing certificates may be created, " +
            "and their public certificate authority will be trusted for all users on this computer. Windows may ask for approval. " +
            "Existing valid selections are reused. No service or running listener will be started or restarted.",
            "Set up shared MCP",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)==DialogResult.OK);
        _confirmRetry = confirmRetry ?? (step => MessageBox.Show(this,
            "The original " + step.Action + " step (job " + step.JobId + ") was canceled before it changed anything. " +
            "Retry only that step and continue setup? Completed certificate steps will be retained. Windows may ask for approval again.",
            "Review canceled MCP step",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)==DialogResult.OK);
        _owner.Items.AddRange([new OwnerChoice(CortexMcpOwner.Application, "Application"),
            new OwnerChoice(CortexMcpOwner.Service, "Existing local service")]);
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20)
        };
        body.Controls.Add(new Label { AutoSize = true, Font = CortexTheme.HeadingFont, Text = "Shared MCP" });
        body.Controls.Add(TextLabel("", "Choose whether the shared HTTPS MCP endpoint is enabled and which existing Cortex owner hosts it."));
        body.Controls.Add(_enabled);
        var ownerRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        ownerRow.Controls.Add(new Label { AutoSize = true, Text = "Host in", Padding = new Padding(0, 7, 8, 0) });
        ownerRow.Controls.Add(_owner);
        body.Controls.Add(ownerRow);
        body.Controls.Add(_ownerHelp);
        body.Controls.Add(_savedSelection);
        body.Controls.Add(_endpoint);
        body.Controls.Add(_certificates);
        body.Controls.Add(TextLabel("MCP setup guidance",beginSetupOperation is null
            ? "Certificate setup is available from the running Cortex application's MCP page."
            : "Set up checks the selected certificates, prepares missing material and tests a local HTTPS connection before saving. " +
                "An existing authority needs its signing reference only when a new server certificate is required."));
        body.Controls.Add(TextLabel("Existing authority reference guidance","Existing certificate authority reference (optional; leave blank for a new setup)"));
        body.Controls.Add(_issuerReference);
        body.Controls.Add(TextLabel("Current MCP listener status", "Current listener status has not been checked."));
        body.Controls.Add(_selection);
        body.Controls.Add(TextLabel("MCP restart guidance",
            "Changes apply at the next deliberate restart of the selected owner. When changing owners, deliberately stop the previous owner before starting the selected owner. " +
            "Saving does not restart either owner or change service or application startup settings."));
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange([_setup, _retrySetup, _save, _refresh]);
        body.Controls.Add(buttons);
        body.Controls.Add(_status);
        Controls.Add(body);
        _enabled.CheckedChanged += (_, _) => { if (!_loading) UpdateSelection(); };
        _owner.SelectedIndexChanged += (_, _) => { if (!_loading) UpdateSelection(); };
        _issuerReference.TextChanged += (_, _) => { if (!_loading) UpdateSelection(); };
        _refresh.Click += (_, _) => Reload();
        _save.Click += async (_, _) => await SaveSelectionAsync();
        _setup.Click += async (_, _) => await SetupSelectionAsync();
        _retrySetup.Click += async (_, _) => await RetrySetupSelectionAsync();
        Reload();
        (theme ?? CortexTheme.Apply)(this);
    }

    internal void Reload()
    {
        if (_busy || IsDisposed || Disposing) return;
        _loading = true;
        try
        {
            _saved = CortexMcpSettingsStore.Validate(_read());
            _requiresRefresh = false;
            if (_retryRequest is not null && CortexMcpSetupWorkflow.Fingerprint(_retryRequest.Baseline) !=
                CortexMcpSetupWorkflow.Fingerprint(_saved)) ClearRetry();
            _enabled.Checked = _retryRequest?.Enabled ?? _saved.Enabled;
            _owner.SelectedIndex = (_retryRequest?.Owner ?? _saved.Owner) == CortexMcpOwner.Application ? 0 : 1;
            if (_retryRequest is not null) _issuerReference.Text = _retryRequest.IssuerReference ?? string.Empty;
            _savedSelection.Text = "Saved: " + (_saved.Enabled ? "enabled" : "disabled") + " · " +
                (_saved.Owner == CortexMcpOwner.Application ? "Application" : "Existing local service");
            var uri = new UriBuilder(Uri.UriSchemeHttps, _saved.ServerName, _saved.Port, CortexMcpHostController.EndpointPath).Uri;
            _endpoint.Text = "Configured endpoint: " + uri.AbsoluteUri + "\r\nBind address: " + _saved.BindAddress +
                (IPAddress.IsLoopback(IPAddress.Parse(_saved.BindAddress)) ? " (loopback)" : " (private network)") +
                ". " + (_saved.AllowPrivateNetwork ? "Saved private-network access is enabled." : "Saved access is loopback only.");
            _status.Text = _retryStep is null ? "Review the selection, then save. Saving requests Windows elevation." :
                "The canceled step made no changes. Review and retry it explicitly to continue setup.";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _saved = null;
            _requiresRefresh = true;
            _savedSelection.Text = "Saved MCP settings could not be loaded.";
            _endpoint.Text = "Configured endpoint unavailable.";
            _status.Text = "Settings were not loaded: " + error.GetType().Name + ". Refresh after resolving the settings error.";
        }
        finally
        {
            _loading = false;
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        if (IsDisposed || Disposing) return;
        if (!_loading && _retryRequest is not null && !RetryMatchesSelection()) ClearRetry();
        var owner = (_owner.SelectedItem as OwnerChoice)?.Owner;
        _ownerHelp.Text = owner == CortexMcpOwner.Service
            ? "Requires the existing local service. Its installation and running state have not been checked. This selection does not install, enable or start it; a stopped service does not fall back to the application."
            : "Uses the signed-in Cortex application. This selection does not change application startup.";
        if (_saved is null || owner is null)
        {
            _certificates.Text = "Certificate reference configuration unavailable.";
            _selection.Text = "Load saved settings before making a change.";
        }
        else
        {
            // These are saved public references, not a certificate-store or listener observation.
            var certificate = owner == CortexMcpOwner.Application ? _saved.ApplicationCertificate : _saved.ServiceCertificate;
            _certificates.Text = _saved.CertificateAuthority is null || certificate is null
                ? "Certificate references are incomplete for the selected owner. MCP is unavailable for that owner until its authority and server certificate references are configured."
                : "Certificate references are configured for the selected owner. Certificate, private-key and TLS availability have not been checked.";
            _selection.Text = HasChanges()
                ? "Unsaved selection. Changes will require the next deliberate restart of the selected owner."
                : "The selection matches the saved settings. Whether a running listener has applied them is not known.";
        }
        UpdateControls();
    }

    private bool HasChanges() => _saved is not null && _owner.SelectedItem is OwnerChoice owner &&
        (_enabled.Checked != _saved.Enabled || owner.Owner != _saved.Owner);

    private void UpdateControls()
    {
        var editable = !_busy && _saved is not null;
        _enabled.Enabled = editable;
        _owner.Enabled = editable;
        _save.Enabled = editable && !_requiresRefresh && HasChanges();
        _refresh.Enabled = !_busy;
        _setup.Enabled = editable && !_requiresRefresh && _beginSetupOperation is not null && _retryStep is null;
        _retrySetup.Enabled = editable && !_requiresRefresh && _beginSetupOperation is not null &&
            _retryStep is not null && RetryMatchesSelection();
        _issuerReference.Enabled = editable && !_requiresRefresh && _beginSetupOperation is not null;
    }

    internal async Task SetupSelectionAsync()
    {
        if(IsDisposed||Disposing||_busy||_requiresRefresh||_saved is null||_beginSetupOperation is null ||
            _owner.SelectedItem is not OwnerChoice owner)return;
        if(!_confirmSetup())return;
        var request = new CortexMcpSetupRequest(_saved,_enabled.Checked,owner.Owner,
            string.IsNullOrWhiteSpace(_issuerReference.Text)?null:_issuerReference.Text);
        await RunSetupAsync(request);
    }

    internal async Task RetrySetupSelectionAsync()
    {
        if(IsDisposed||Disposing||_busy||_requiresRefresh||_beginSetupOperation is null||
            _retryRequest is null||_retryStep is null||!RetryMatchesSelection())return;
        var request = _retryRequest;
        var step = _retryStep;
        var reviewed = request.ReviewedCanceledJobIds?.ToArray() ?? [];
        if(reviewed.Length >= CortexMcpSetupWorkflow.MaximumReviewedCancellations || !_confirmRetry(step))return;
        if(IsDisposed||Disposing||_busy||_requiresRefresh||_retryRequest != request||_retryStep != step||!RetryMatchesSelection())return;
        // The workflow re-observes this exact original and verifies all no-effect
        // evidence. The UI descriptor itself never authorizes certificate work.
        await RunSetupAsync(request with { ReviewedCanceledJobIds = [..reviewed, step.JobId] });
    }

    private bool RetryMatchesSelection() => _saved is not null && _retryRequest is not null &&
        _owner.SelectedItem is OwnerChoice owner && owner.Owner == _retryRequest.Owner &&
        _enabled.Checked == _retryRequest.Enabled &&
        (string.IsNullOrWhiteSpace(_issuerReference.Text)?null:_issuerReference.Text) == _retryRequest.IssuerReference &&
        CortexMcpSetupWorkflow.Fingerprint(_saved) == CortexMcpSetupWorkflow.Fingerprint(_retryRequest.Baseline);

    private void ClearRetry() { _retryRequest = null; _retryStep = null; }

    private async Task RunSetupAsync(CortexMcpSetupRequest request)
    {
        _busy=true;UpdateControls();
        _status.Text="Preparing MCP. Waiting for the original certificate and Windows approval steps…";
        try
        {
            // Hold actual owner admission/effect until every original step returns,
            // even if the page is disposed. Page closure does not cancel/replay UAC.
            var result = await RunOwnedSetupAsync(_beginSetupOperation!,_setupController,request);
            if(IsDisposed||Disposing)return;
            if(!result.Ok || !result.TlsQualified)
            {
                _requiresRefresh=true;
                _retryStep=result.Retry;
                _retryRequest=result.Retry is null?null:request with
                { ReviewedCanceledJobIds=request.ReviewedCanceledJobIds?.ToArray() ?? [] };
                var detail=result.Error??"MCP setup was not confirmed.";
                _status.Text=(detail.Length<=400?detail:detail[..400])+
                    (result.JobId is { Length:>0 } job?" Job: "+job:string.Empty)+" Refresh and review before another change.";
                return;
            }
            ClearRetry();
            _busy=false;Reload();
            if(_saved is null || _saved.Enabled!=request.Enabled || _saved.Owner!=request.Owner)
            {
                _requiresRefresh=true;
                _status.Text="Setup finished, but the saved selection could not be confirmed. Refresh and review it.";
            }
            else _status.Text="MCP setup saved. The local certificate connection check passed. Restart pending: " +
                "apply the selection at the next deliberate restart. Current listener and client readiness have not been checked.";
        }
        catch(Exception error) when(error is not OutOfMemoryException and not StackOverflowException)
        {
            if(!IsDisposed&&!Disposing)
            {
                _requiresRefresh=true;
                ClearRetry();
                _status.Text="MCP setup was not confirmed: "+error.GetType().Name+". Review the original work before trying again.";
            }
        }
        finally { _busy=false;if(!IsDisposed&&!Disposing)UpdateControls(); }
    }

    internal static async Task<CortexMcpSetupResult> RunOwnedSetupAsync(Func<IDisposable> begin,
        ICortexMcpSetupController controller,CortexMcpSetupRequest request)
    {
        using var operation=begin();
        // The original UAC/certificate work can outlive this UI loop. Releasing
        // Engine ownership must not depend on posting to a failed old window.
        return await controller.SetupAsync(request,CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task SaveSelectionAsync()
    {
        if (IsDisposed || Disposing || _busy || _requiresRefresh || _saved is null || !HasChanges()) return;
        if (_owner.SelectedItem is not OwnerChoice owner) return;
        // The controller's fixed helper merges only these two fields with fresh protected settings.
        var requested = _saved with { Enabled = _enabled.Checked, Owner = owner.Owner };
        _busy = true;
        UpdateControls();
        _status.Text = "Waiting for Windows elevation to save MCP settings…";
        try
        {
            var result = await _controller.SaveAsync(requested, CancellationToken.None);
            if (IsDisposed || Disposing) return;
            if (!result.Ok)
            {
                _requiresRefresh = true;
                var detail = result.Error ?? "MCP settings were not confirmed.";
                _status.Text = (detail.Length <= 400 ? detail : detail[..400]) + " Refresh before making another change.";
                return;
            }
            _busy = false;
            Reload();
            if (_saved is null)
                _status.Text = "The save was confirmed, but saved settings could not be reloaded. Refresh before making another change.";
            else if (_saved.Enabled != requested.Enabled || _saved.Owner != requested.Owner)
            {
                _requiresRefresh = true;
                _status.Text = "The save was confirmed, but the saved selection has since changed. Refresh and review it before saving again.";
            }
            else
                _status.Text = "MCP settings saved. Restart pending: apply this change at the next deliberate restart of the selected owner. No owner was restarted.";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (!IsDisposed && !Disposing)
            {
                _requiresRefresh = true;
                _status.Text = "MCP settings were not confirmed: " + error.GetType().Name + ". Refresh before making another change.";
            }
        }
        finally
        {
            _busy = false;
            if (!IsDisposed && !Disposing) UpdateControls();
        }
    }

    private static Label TextLabel(string name, string text = "") => new()
    {
        AutoSize = true, MaximumSize = new Size(700, 0), AccessibleName = name, Text = text,
        Margin = new Padding(3, 6, 3, 6), UseMnemonic = false
    };

    private sealed record OwnerChoice(CortexMcpOwner Owner, string Label)
    {
        public override string ToString() => Label;
    }
}
