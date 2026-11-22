namespace Pegasus.AgentOps.Tray;

/// <summary>Display-only lifecycle facts supplied by the owning tray. Missing facts remain unknown.</summary>
internal sealed record CortexUserPresentationState(string RuntimeState, string ProfileState, string? ProfileLabel,
    string McpState, string? StartupState = null);

internal sealed class CortexOverviewPage : UserControl
{
    private readonly Func<CortexUserPresentationState>? _presentation;
    private readonly Label _runtime = ValueLabel();
    private readonly Label _profile = ValueLabel();
    private readonly Label _mcp = ValueLabel();
    private readonly Label _startup = ValueLabel();
    private readonly Label _checked = ValueLabel();

    internal CortexOverviewPage(Func<CortexUserPresentationState>? presentation, Action openService)
    {
        _presentation = presentation;
        AutoScroll = true;
        Padding = new Padding(20);
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(body, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Font = CortexTheme.HeadingFont,
            Text = "Your local Cortex session", Margin = new Padding(0, 0, 0, 12)
        });
        Add(body, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 20),
            Text = "See what is available, manage local work, and choose which applications can connect."
        });
        var states = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        states.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        states.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        AddState(states, "User execution host", _runtime);
        AddState(states, "Personal profile", _profile);
        AddState(states, "MCP connection", _mcp);
        AddState(states, "Next Windows sign-in", _startup);
        Add(body, states);
        Add(body, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 16),
            Text = "Session status and next-sign-in startup are separate. Closing this window keeps the tray session available; " +
                "use the tray controls to change the session or exit Cortex."
        });
        var refresh = new Button { Text = "Refresh session status", AutoSize = true, AccessibleName = "Refresh session status" };
        refresh.Click += (_, _) => RefreshPresentation();
        Add(body, refresh);
        Add(body, _checked);
        Add(body, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Font = CortexTheme.HeadingFont,
            Text = "Administrator execution", Margin = new Padding(0, 28, 0, 8)
        });
        Add(body, new Label
        {
            AutoSize = true, Dock = DockStyle.Fill,
            Text = "The optional LocalSystem service handles protected operations. Its installation, running state and startup " +
                "policy are independent of the user host and MCP connection."
        });
        var service = new Button { AutoSize = true, Text = "Manage administrator service", Margin = new Padding(0, 12, 0, 0) };
        service.Click += (_, _) => openService();
        Add(body, service);
        Controls.Add(body);
        RefreshPresentation();
    }

    internal void RefreshPresentation()
    {
        CortexUserPresentationState? state = null;
        try { state = _presentation?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A display callback failure must never be replaced by an inferred running/ready state.
        }
        _runtime.Text = Display(state?.RuntimeState);
        var profileLabel = state?.ProfileLabel;
        _profile.Text = Display(state?.ProfileState) + (string.IsNullOrWhiteSpace(profileLabel)
            ? string.Empty : " · " + Display(profileLabel));
        _mcp.Text = Display(state?.McpState);
        _startup.Text = Display(state?.StartupState);
        _checked.Text = state is null ? "Session status is unavailable. No host was started to obtain it."
            : "Updated " + CortexDisplayFormat.Timestamp(DateTime.UtcNow).Text + " · supplied by this tray session";
    }

    private static string Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var bounded = new string(value.Where(c => !char.IsControl(c)).Take(160).ToArray()).Trim();
        return bounded.Length == 0 ? "Unknown" : bounded;
    }

    private static Label ValueLabel() => new()
    {
        AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8), UseMnemonic = false
    };

    private static void AddState(TableLayoutPanel table, string caption, Label value)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = caption, Margin = new Padding(0, 8, 16, 8) }, 0, row);
        value.AccessibleName = caption;
        table.Controls.Add(value, 1, row);
    }

    private static void Add(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
    }
}
