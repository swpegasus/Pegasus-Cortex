using System.Diagnostics;
using System.Reflection;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexAboutInformation(string BuildVersion, string? Author = null, Uri? Portfolio = null)
{
    internal static CortexAboutInformation Current() => new(
        typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unavailable",
        "Shayne Winter", new Uri("https://shaynewinter.com/"));
}

internal sealed record CortexAboutLink(string Key, string Caption, Uri Address);

/// <summary>Local display only. A browser is opened solely by an explicit link action.</summary>
internal sealed class CortexAboutDialog : Form
{
    private readonly Action<Uri> _openBrowser;
    private readonly IReadOnlyList<CortexAboutLink> _links;
    private readonly Label _linkStatus = Paragraph("About link status", string.Empty);

    internal CortexAboutDialog(CortexAboutInformation? information = null, Action<Uri>? openBrowser = null,
        Action<Control>? theme = null)
    {
        information ??= CortexAboutInformation.Current();
        _openBrowser = openBrowser ?? OpenBrowser;
        _links = CreateLinks(information.Portfolio);
        Text = "About Pegasus Cortex";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        MinimumSize = new Size(580, 500);
        Size = new Size(720, 620);
        CortexWindowIcon.Apply(this);

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 20, 24, 12) };
        body.Controls.Add(new Label { AutoSize = true, Font = CortexTheme.TitleFont,
            Text = CortexProductIdentity.Product, AccessibleName = "About product name", Margin = new Padding(0, 0, 0, 8) });
        body.Controls.Add(Paragraph("About build version", "Build " + Display(information.BuildVersion, 160) + " · " + CortexProductIdentity.Channel));
        if (!string.IsNullOrWhiteSpace(information.Author))
            body.Controls.Add(Paragraph("About author", "Created by " + Display(information.Author, 120)));
        body.Controls.Add(Paragraph("About purpose",
            "A local place for project context, task continuity and useful knowledge."));
        body.Controls.Add(Paragraph("About why Cortex was built",
            "Cortex was built to make context and lessons from earlier work reusable across tasks and tools. " +
            "It brings durable notes, task state and useful local tools together while keeping people in control of what runs and what changes."));
        body.Controls.Add(Paragraph("About local privacy",
            "Opening this window stays on your computer. It makes no API calls and sends no information. " +
            "A link opens in your browser only when you choose it."));
        foreach (var link in _links)
        {
            var control = new LinkLabel { AutoSize = true, Text = link.Caption,
                AccessibleName = "About " + link.Key, UseMnemonic = false, Margin = new Padding(0, 5, 0, 5),
                AccessibleDescription = link.Address.AbsoluteUri };
            // Match the surrounding theme, including later high-contrast or appearance changes.
            void UpdateLinkColor() { control.LinkColor = control.ForeColor; control.ActiveLinkColor = control.ForeColor; control.VisitedLinkColor = control.ForeColor; }
            control.ForeColorChanged += (_, _) => UpdateLinkColor();
            UpdateLinkColor();
            control.LinkClicked += (_, _) => OpenLink(link.Key);
            body.Controls.Add(control);
        }
        body.Controls.Add(_linkStatus);
        void FitText()
        {
            var width = Math.Max(180, body.ClientSize.Width - body.Padding.Horizontal - 24);
            foreach (var label in body.Controls.OfType<Label>()) label.MaximumSize = new Size(width, 0);
        }
        body.ClientSizeChanged += (_, _) => FitText();
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        var close = new Button { AutoSize = true, Text = "Close", DialogResult = DialogResult.Cancel,
            AccessibleName = "Close About Cortex" };
        footer.Controls.Add(close);
        CancelButton = close; AcceptButton = close;
        Controls.Add(body); Controls.Add(footer);
        (theme ?? CortexTheme.Apply)(this);
        FitText();
    }

    internal IReadOnlyList<CortexAboutLink> Links => _links;

    internal void OpenLink(string key)
    {
        if (IsDisposed || Disposing) return;
        var link = _links.SingleOrDefault(candidate => candidate.Key == key)
            ?? throw new ArgumentOutOfRangeException(nameof(key));
        try { _openBrowser(link.Address); _linkStatus.Text = "Opening in your browser: " + link.Caption; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { _linkStatus.Text = "The browser could not be opened. Link: " + link.Address.AbsoluteUri; }
    }

    private static IReadOnlyList<CortexAboutLink> CreateLinks(Uri? portfolio)
    {
        var links = new List<CortexAboutLink>
        {
            new("project", "Project source on GitHub", new Uri("https://github.com/swpegasus/Pegasus-Cortex")),
            new("downloads", "Downloads and releases", new Uri("https://github.com/swpegasus/Pegasus-Cortex/releases")),
            new("cerebral", "Companion project: Pegasus Cerebral", new Uri("https://github.com/swpegasus/Pegasus-Cerebral")),
            new("website", "Cortex website and learning guide", new Uri("https://cerebral.shaynewinter.com/cortex/"))
        };
        if (portfolio is not null)
        {
            if (!portfolio.IsAbsoluteUri || portfolio.Scheme != Uri.UriSchemeHttps || !portfolio.IsDefaultPort ||
                portfolio.UserInfo.Length != 0 || portfolio.AbsoluteUri.Length > 2048)
                throw new ArgumentException("The public portfolio link must be an explicit HTTPS address without credentials.", nameof(portfolio));
            links.Add(new("portfolio", "Public portfolio", portfolio));
        }
        return links.AsReadOnly();
    }

    private static string Display(string? value, int maximum)
    {
        var text = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).Take(maximum).ToArray()).Trim();
        return text.Length == 0 ? "Unavailable" : text;
    }

    private static Label Paragraph(string name, string text) => new()
    {
        AutoSize = true, MaximumSize = new Size(620, 0), Text = text, UseMnemonic = false,
        AccessibleName = name, Margin = new Padding(0, 4, 0, 10)
    };

    private static void OpenBrowser(Uri address)
    {
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>Keeps About below the settings tabs without adding another destination or tab.</summary>
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
