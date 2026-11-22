using System.Reflection;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexAboutInformation(string BuildVersion, string? Author = null, Uri? Portfolio = null)
{
    internal static CortexAboutInformation Current() => new(
        typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unavailable",
        "Shayne Winter", new Uri("https://shaynewinter.com/"));
}

internal sealed record CortexAboutLink(string Key, string Caption, Uri Address);

/// <summary>Optional window wrapper around the same lazy About tab content.</summary>
internal sealed class CortexAboutDialog : Form
{
    private readonly CortexAboutPage _page;

    internal CortexAboutDialog(CortexAboutInformation? information = null, Action<Uri>? openBrowser = null,
        Action<Control>? theme = null)
    {
        Text = "About Pegasus Cortex";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        MinimumSize = new Size(680, 560);
        Size = new Size(880, 740);
        try
        {
            CortexWindowIcon.Apply(this);
            // Theme the complete window once; the standalone tab themes itself.
            _page = new CortexAboutPage(information, openBrowser, _ => { });
            Controls.Add(_page);
            var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
            var close = new Button { AutoSize = true, Text = "Close", DialogResult = DialogResult.Cancel,
                AccessibleName = "Close About Cortex" };
            footer.Controls.Add(close);
            CancelButton = close; AcceptButton = close;
            Controls.Add(footer);
            (theme ?? CortexTheme.Apply)(this);
        }
        catch { Dispose(); throw; }
    }

    internal IReadOnlyList<CortexAboutLink> Links => _page.Links;
    internal void OpenLink(string key) => _page.OpenLink(key);
}

/// <summary>Keeps the optional About window button below the existing settings tabs.</summary>
internal sealed class CortexSettingsAboutSection : UserControl
{
    private readonly Action<IWin32Window> _showAbout;
    private bool _showingAbout;
    internal TabControl Tabs { get; }

    internal CortexSettingsAboutSection(TabControl tabs, Action<IWin32Window>? showAbout = null, Action<Control>? theme = null)
    {
        Tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _showAbout = showAbout ?? ShowAboutDialog;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tabs.Dock = DockStyle.Fill;
        body.Controls.Add(tabs, 0, 0);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), AccessibleName = "Settings About footer" };
        var about = new Button { Text = "About Cortex…", AutoSize = true, AccessibleName = "About Cortex" };
        about.Click += (_, _) => OpenAbout();
        footer.Controls.Add(about);
        body.Controls.Add(footer, 0, 1);
        Controls.Add(body);
        (theme ?? CortexTheme.Apply)(this);
    }

    internal void OpenAbout()
    {
        if (_showingAbout || IsDisposed || Disposing) return;
        _showingAbout = true;
        try { _showAbout((IWin32Window?)FindForm() ?? this); }
        finally { _showingAbout = false; }
    }

    private static void ShowAboutDialog(IWin32Window owner)
    {
        using var dialog = new CortexAboutDialog();
        dialog.ShowDialog(owner);
    }
}
