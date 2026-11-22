using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Mcp.Hosting;

internal static class CortexMcpSettingsPageTests
{
    internal static int Run()
    {
        try
        {
            DefaultAndSelection();
            PreserveConfiguration();
            FailureAndReload();
            PendingDisposal();
            ParseSelection();
            Console.WriteLine("PASS MCP settings page (injected settings/controller and unshown controls; no UAC, service, network, certificate store or configuration writes)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL MCP settings page: " + error); return 1; }
    }

    private static void DefaultAndSelection()
    {
        var saved = CortexMcpSettingsStore.Default;
        var reads = 0;
        var themes = 0;
        var controller = new FakeController(value => saved = value);
        using var page = new CortexMcpSettingsPage(() => { reads++; return saved; }, controller, _ => themes++);
        var enabled = Find<CheckBox>(page, "Enable shared HTTPS MCP");
        var owner = Find<ComboBox>(page, "Configured MCP owner");
        var save = Find<Button>(page, "Save MCP settings");
        var endpoint = Find<Label>(page, "Configured MCP endpoint");
        var runtime = Find<Label>(page, "Current MCP listener status");
        Check(!enabled.Checked && owner.SelectedIndex == 0 && !save.Enabled, "default selection is not disabled/Application");
        Check(endpoint.Text.Contains("https://localhost:47831/mcp/cortex", StringComparison.Ordinal) &&
            endpoint.Text.Contains("127.0.0.1 (loopback)", StringComparison.Ordinal), "loopback endpoint default is not visible");
        Check(runtime.Text == "Current listener status has not been checked." &&
            Find<Label>(page, "Selected owner certificate references").Text.Contains("incomplete", StringComparison.Ordinal),
            "missing references or unobserved listener were presented as ready");
        Check(reads == 1 && themes == 1 && controller.Saves == 0, "construction polled, saved or missed injected theme");
        enabled.Checked = true;
        owner.SelectedIndex = 1;
        Check(save.Enabled && Find<Label>(page, "MCP owner guidance").Text.Contains("have not been checked", StringComparison.Ordinal) &&
            Find<Label>(page, "MCP owner guidance").Text.Contains("does not fall back", StringComparison.Ordinal),
            "service selection invented availability or fallback");
        Check(controller.Saves == 0 && !saved.Enabled && saved.Owner == CortexMcpOwner.Application,
            "editing a selection wrote settings");
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(controller.Saves == 1 && saved.Enabled && saved.Owner == CortexMcpOwner.Service,
            "explicit save did not preserve enablement/owner selection");
        Check(Find<Label>(page, "MCP settings status").Text.Contains("Restart pending", StringComparison.Ordinal) &&
            runtime.Text == "Current listener status has not been checked." && !save.Enabled,
            "confirmed save implied a live listener or omitted next-restart guidance");
        Check(page.Parent is null, "fixture unexpectedly parented a product window");
    }

    private static void PreserveConfiguration()
    {
        var application = new CortexMcpCertificateReference("CurrentUser", "My", new string('a', 64));
        var service = new CortexMcpCertificateReference("LocalMachine", "My", new string('b', 64));
        var authority = new CortexMcpCertificateReference("LocalMachine", "Root", new string('c', 64));
        var original = CortexMcpSettingsStore.Default with
        {
            BindAddress = "192.168.1.10", Port = 49001, AllowPrivateNetwork = true,
            AllowedPeerCidrs = ["192.168.1.0/24"], ServerName = "cortex.example.test",
            ApplicationCertificate = application, ServiceCertificate = service, CertificateAuthority = authority
        };
        var saved = original;
        var controller = new FakeController(value => saved = value);
        using var page = new CortexMcpSettingsPage(() => saved, controller, _ => { });
        Check(Find<Label>(page, "Configured MCP endpoint").Text.Contains("192.168.1.10", StringComparison.Ordinal) &&
            Find<Label>(page, "Configured MCP endpoint").Text.Contains("49001", StringComparison.Ordinal),
            "existing private-network configuration was replaced by the loopback default");
        Check(Find<Label>(page, "Selected owner certificate references").Text.Contains("have not been checked", StringComparison.Ordinal),
            "configured certificate references implied key/store/TLS qualification");
        Find<CheckBox>(page, "Enable shared HTTPS MCP").Checked = true;
        Find<ComboBox>(page, "Configured MCP owner").SelectedIndex = 1;
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        var requested = controller.Last ?? throw new Exception("controller received no selection");
        Check(requested.Enabled && requested.Owner == CortexMcpOwner.Service &&
            requested.BindAddress == original.BindAddress && requested.Port == original.Port &&
            requested.AllowPrivateNetwork == original.AllowPrivateNetwork && requested.ServerName == original.ServerName &&
            requested.AllowedPeerCidrs.SequenceEqual(original.AllowedPeerCidrs) &&
            requested.CertificateAuthority == authority && requested.ApplicationCertificate == application &&
            requested.ServiceCertificate == service && requested.Contract == original.Contract && requested.Version == original.Version,
            "page changed noneditable settings or certificate references");
        Find<CheckBox>(page, "Enable shared HTTPS MCP").Checked = false;
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(controller.Saves == 2 && !saved.Enabled && saved.Owner == CortexMcpOwner.Service,
            "disable changed the configured owner or skipped the explicit controller");
    }

    private static void FailureAndReload()
    {
        var saved = CortexMcpSettingsStore.Default;
        var failRead = false;
        var controller = new FakeController(value => saved = value) { Refuse = true };
        using var page = new CortexMcpSettingsPage(() => failRead ? throw new IOException("fixture read failed") : saved,
            controller, _ => { });
        var enabled = Find<CheckBox>(page, "Enable shared HTTPS MCP");
        var save = Find<Button>(page, "Save MCP settings");
        var status = Find<Label>(page, "MCP settings status");
        var summary = Find<Label>(page, "Saved MCP selection");
        enabled.Checked = true;
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(controller.Saves == 1 && !saved.Enabled && summary.Text.Contains("disabled", StringComparison.Ordinal) &&
            status.Text.Contains("cancelled", StringComparison.Ordinal) && !save.Enabled,
            "cancelled save became an optimistic persisted selection or automatic retry");
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(controller.Saves == 1, "unconfirmed save was resubmitted without explicit refresh");
        controller.Refuse = false;
        controller.Throw = true;
        page.Reload();
        enabled.Checked = true;
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(controller.Saves == 2 && !saved.Enabled && status.Text.Contains("not confirmed", StringComparison.Ordinal) &&
            status.Text.Contains("Refresh", StringComparison.Ordinal) && !save.Enabled, "save exception escaped or implied success");
        controller.Throw = false;
        page.Reload();
        enabled.Checked = true;
        controller.AfterSave = () => failRead = true;
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(saved.Enabled && status.Text.Contains("save was confirmed", StringComparison.Ordinal) &&
            status.Text.Contains("could not be reloaded", StringComparison.Ordinal) && !save.Enabled,
            "confirmed save plus failed reread was misrepresented or editable with unknown settings");
        Check(!enabled.Enabled, "failed settings load left controls editable");
        failRead = false;
        page.Reload();
        Check(enabled.Checked && enabled.Enabled && !save.Enabled, "explicit refresh did not restore confirmed current settings");
        saved = saved with { Port = 0 };
        page.Reload();
        Check(!enabled.Enabled && !save.Enabled && summary.Text.Contains("could not be loaded", StringComparison.Ordinal),
            "invalid saved settings silently became editable defaults");
    }

    private static void PendingDisposal()
    {
        var completion = new TaskCompletionSource<CortexMcpSettingsSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var controller = new PendingController(completion.Task);
        using var page = new CortexMcpSettingsPage(() => { reads++; return CortexMcpSettingsStore.Default; }, controller, _ => { });
        Find<CheckBox>(page, "Enable shared HTTPS MCP").Checked = true;
        var context = SynchronizationContext.Current;
        Task pending;
        // The disposed continuation is checked without a message loop or live helper.
        try { SynchronizationContext.SetSynchronizationContext(null); pending = page.SaveSelectionAsync(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        Check(!Find<Button>(page, "Save MCP settings").Enabled &&
            !Find<Button>(page, "Refresh saved MCP settings").Enabled &&
            !Find<ComboBox>(page, "Configured MCP owner").Enabled, "pending save left action controls enabled");
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(controller.Saves == 1, "busy save admitted a duplicate");
        page.Dispose();
        completion.SetResult(new(true));
        pending.GetAwaiter().GetResult();
        page.SaveSelectionAsync().GetAwaiter().GetResult();
        Check(reads == 1 && controller.Saves == 1, "disposed save continuation reloaded settings or replayed the controller");
    }

    private static void ParseSelection()
    {
        foreach (var enabled in new[] { "enabled", "disabled" })
        foreach (var owner in new[] { "Application", "Service" })
        {
            var parsed = CortexMcpSettingsControl.ParseSelection(enabled, owner);
            Check(parsed.Enabled == (enabled == "enabled") &&
                parsed.Owner == (owner == "Application" ? CortexMcpOwner.Application : CortexMcpOwner.Service),
                "fixed helper selection changed its two-field grammar");
        }
        foreach (var invalid in new[] { "", "Enabled", "true", "unknown" })
            Check(Rejected(() => CortexMcpSettingsControl.ParseSelection(invalid, "Application")), "invalid enablement was admitted");
        foreach (var invalid in new[] { "", "application", "service", "unknown" })
            Check(Rejected(() => CortexMcpSettingsControl.ParseSelection("enabled", invalid)), "invalid owner was admitted");
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        Descendants(root).OfType<T>().Single(item => item.AccessibleName == name);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static bool Rejected(Action call)
    {
        try { call(); return false; }
        catch (InvalidDataException) { return true; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class FakeController(Action<CortexMcpSettings> save) : ICortexMcpSettingsController
    {
        internal int Saves { get; private set; }
        internal CortexMcpSettings? Last { get; private set; }
        internal bool Refuse { get; set; }
        internal bool Throw { get; set; }
        internal Action? AfterSave { get; set; }
        public Task<CortexMcpSettingsSaveResult> SaveAsync(CortexMcpSettings settings, CancellationToken cancellationToken)
        {
            Saves++;
            Last = settings;
            if (Throw) throw new InvalidOperationException("fixture controller failed");
            if (Refuse) return Task.FromResult(new CortexMcpSettingsSaveResult(false, "Windows elevation was cancelled."));
            save(settings);
            AfterSave?.Invoke();
            return Task.FromResult(new CortexMcpSettingsSaveResult(true));
        }
    }

    private sealed class PendingController(Task<CortexMcpSettingsSaveResult> result) : ICortexMcpSettingsController
    {
        internal int Saves { get; private set; }
        public Task<CortexMcpSettingsSaveResult> SaveAsync(CortexMcpSettings settings, CancellationToken cancellationToken)
        {
            Saves++;
            return result;
        }
    }
}
