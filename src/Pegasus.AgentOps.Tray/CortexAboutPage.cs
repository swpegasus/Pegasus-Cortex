using System.Diagnostics;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

/// <summary>One local About presentation shared by the settings tab and dialog.</summary>
internal sealed class CortexAboutPage : UserControl
{
    private readonly Action<Uri> _openBrowser;
    private readonly IReadOnlyList<CortexAboutLink> _links;
    private readonly PictureBox _brand = new();
    private readonly Label _linkStatus = Paragraph("About link status", string.Empty);
    private Image? _ownedLogo;
    private bool _fitting;

    internal CortexAboutPage(CortexAboutInformation? information = null, Action<Uri>? openBrowser = null,
        Action<Control>? theme = null)
    {
        information ??= CortexAboutInformation.Current();
        _openBrowser = openBrowser ?? OpenBrowser;
        _links = CreateLinks(information.Portfolio);
        Dock = DockStyle.Fill;
        AccessibleName = "About Pegasus Cortex page";

        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24) };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hero = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 20) };
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = Flow();
        heading.Controls.Add(Heading("About product name", CortexProductIdentity.Product, title: true));
        heading.Controls.Add(Paragraph("About product line", "Continuity. Knowledge. Local tools."));
        heading.Controls.Add(Paragraph("About build version", "Build " + Display(information.BuildVersion, 160) +
            " · " + CortexProductIdentity.Channel));
        heading.Controls.Add(Paragraph("About tagline", "Built for real work. Used by its creator."));
        _brand.Size = new Size(128, 128);
        _brand.SizeMode = PictureBoxSizeMode.Zoom;
        _brand.Margin = new Padding(0, 0, 20, 0);
        _brand.AccessibleName = "Pegasus Cortex product logo";
        _brand.AccessibleDescription = "The existing Cortex product icon, also used by its windows.";
        _brand.TabStop = false;
        hero.Controls.Add(_brand, 0, 0);
        hero.Controls.Add(heading, 1, 0);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        var story = Flow();
        story.Margin = new Padding(0, 0, 24, 0);
        story.Controls.Add(Heading("About purpose heading", "A little more continuity"));
        story.Controls.Add(Paragraph("About purpose",
            "A local place for project context, task continuity and useful knowledge."));
        story.Controls.Add(Paragraph("About why Cortex was built",
            "Cortex was built to make context and lessons from earlier work reusable across tasks and tools. " +
            "It brings durable notes, task state and useful local tools together while keeping people in control of what runs and what changes."));
        story.Controls.Add(Heading("About evolution heading", "How it grew"));
        story.Controls.Add(Paragraph("About evolution",
            "Portable project notes grew into durable task and knowledge records, bounded retrieval and local execution tools. " +
            "The shared MCP connection lets compatible clients use the same running owner instead of creating an adapter for every chat."));
        story.Controls.Add(Paragraph("About local privacy",
            "Opening About is local: no network or AI API calls and no information sent. " +
            "A link opens in your browser only when you choose it."));

        var credits = Flow();
        if (!string.IsNullOrWhiteSpace(information.Author))
        {
            credits.Controls.Add(Heading("About creator heading", "Creator"));
            credits.Controls.Add(Paragraph("About author", "Created by " + Display(information.Author, 120)));
        }
        credits.Controls.Add(Heading("About development heading", "Development assistance"));
        credits.Controls.Add(Paragraph("About development credits", "OpenAI Codex\nPegasus Cerebral"));
        credits.Controls.Add(Paragraph("About copyright", "Copyright © 2026 Shayne Winter."));
        credits.Controls.Add(Paragraph("About license",
            "Pegasus Community License 1.0. Separately licensed components retain their own terms and notices."));
        credits.Controls.Add(Heading("About links heading", "Explore Cortex"));
        foreach (var link in _links)
        {
            var control = new LinkLabel { AutoSize = true, Text = link.Caption, UseMnemonic = false,
                AccessibleName = "About link " + link.Key, AccessibleDescription = link.Address.AbsoluteUri,
                Margin = new Padding(0, 3, 0, 5) };
            void UpdateLinkColor()
            {
                control.LinkColor = control.ForeColor;
                control.ActiveLinkColor = control.ForeColor;
                control.VisitedLinkColor = control.ForeColor;
            }
            control.ForeColorChanged += (_, _) => UpdateLinkColor();
            UpdateLinkColor();
            control.LinkClicked += (_, _) => OpenLink(link.Key);
            credits.Controls.Add(control);
        }
        credits.Controls.Add(_linkStatus);
        details.Controls.Add(story, 0, 0);
        details.Controls.Add(credits, 1, 0);

        var note = Flow();
        note.Padding = new Padding(0, 18, 0, 0);
        note.Controls.Add(Heading("About development note heading", "A development release"));
        note.Controls.Add(Paragraph("About development disclaimer",
            "Development software and AI assistance can make mistakes. Review actions and outputs. " +
            "Privileged controls can change or delete files and system settings, so keep backups. " +
            "The license contains the authoritative terms."));
        content.Controls.Add(hero, 0, 0);
        content.Controls.Add(details, 0, 1);
        content.Controls.Add(note, 0, 2);
        viewport.Controls.Add(content);
        Controls.Add(viewport);

        void FitLayout()
        {
            if (_fitting || IsDisposed || Disposing) return;
            _fitting = true;
            try
            {
                var width = Math.Max(280, viewport.ClientSize.Width - viewport.Padding.Horizontal -
                    SystemInformation.VerticalScrollBarWidth);
                content.MinimumSize = new Size(width, 0);
                content.MaximumSize = new Size(width, 0);
                Fit(heading, Math.Max(160, width - 148));
                Fit(story, Math.Max(150, (int)(width * .6) - story.Margin.Right));
                Fit(credits, Math.Max(130, (int)(width * .4)));
                Fit(note, width);
            }
            finally { _fitting = false; }
        }
        viewport.ClientSizeChanged += (_, _) => FitLayout();
        FontChanged += (_, _) => FitLayout();
        try
        {
            _ownedLogo = LoadProductLogo();
            _brand.Image = _ownedLogo;
            (theme ?? CortexTheme.Apply)(this);
            FitLayout();
        }
        catch
        {
            _brand.Image = null;
            _ownedLogo?.Dispose();
            _ownedLogo = null;
            throw;
        }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownedLogo is { } image)
        {
            _ownedLogo = null;
            _brand.Image = null;
            image.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Image LoadProductLogo()
    {
        using var stream = typeof(CortexWindowIcon).Assembly.GetManifestResourceStream("Pegasus.AgentOps.Tray.Icon")
            ?? throw new InvalidOperationException("The Cortex product icon resource is unavailable.");
        using var icon = new Icon(stream, new Size(128, 128));
        return icon.ToBitmap();
    }

    private static IReadOnlyList<CortexAboutLink> CreateLinks(Uri? portfolio)
    {
        var links = new List<CortexAboutLink>
        {
            new("website", "Cortex website and learning guide", new Uri("https://cerebral.shaynewinter.com/cortex/")),
            new("purpose", "Why Cortex exists", new Uri("https://cerebral.shaynewinter.com/cortex/#why")),
            new("evolution", "How Cortex evolved", new Uri("https://cerebral.shaynewinter.com/cortex/#evolution")),
            new("project", "Project source on GitHub", new Uri("https://github.com/swpegasus/Pegasus-Cortex")),
            new("downloads", "Downloads and releases", new Uri("https://github.com/swpegasus/Pegasus-Cortex/releases")),
            new("cerebral", "Companion: Pegasus Cerebral", new Uri("https://github.com/swpegasus/Pegasus-Cerebral")),
            new("license", "License and use", new Uri("https://cerebral.shaynewinter.com/cortex/#license"))
        };
        if (portfolio is not null)
        {
            if (!portfolio.IsAbsoluteUri || portfolio.Scheme != Uri.UriSchemeHttps || !portfolio.IsDefaultPort ||
                portfolio.UserInfo.Length != 0 || portfolio.AbsoluteUri.Length > 2048)
                throw new ArgumentException("The public portfolio link must be an explicit HTTPS address without credentials.", nameof(portfolio));
            links.Add(new("portfolio", "Shayne Winter · portfolio", portfolio));
        }
        return links.AsReadOnly();
    }

    private static FlowLayoutPanel Flow() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty
    };

    private static Label Heading(string name, string text, bool title = false) => new()
    {
        AutoSize = true, Text = text, UseMnemonic = false, AccessibleName = name,
        Font = title ? CortexTheme.TitleFont : CortexTheme.HeadingFont,
        Margin = new Padding(0, 0, 0, title ? 8 : 6)
    };

    private static Label Paragraph(string name, string text) => new()
    {
        AutoSize = true, Text = text, UseMnemonic = false, AccessibleName = name,
        Margin = new Padding(0, 0, 0, 12)
    };

    private static void Fit(FlowLayoutPanel panel, int width)
    {
        panel.MinimumSize = new Size(width, 0);
        panel.MaximumSize = new Size(width, 0);
        foreach (var label in panel.Controls.OfType<Label>()) label.MaximumSize = new Size(width, 0);
    }

    private static string Display(string? value, int maximum)
    {
        var text = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).Take(maximum).ToArray()).Trim();
        return text.Length == 0 ? "Unavailable" : text;
    }

    private static void OpenBrowser(Uri address)
    {
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }
}
