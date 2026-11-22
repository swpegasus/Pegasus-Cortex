using System.Reflection;
using System.Drawing.Imaging;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Tray;

internal static class CortexAboutDialogTests
{
    private static int _checks;

    internal static int Run(string? imageOutputDirectory = null)
    {
        var previousAppearance = CortexTheme.Appearance;
        try
        {
            _checks = 0;
            CortexTheme.SetAppearance(CortexAppearance.Light);
            DialogContentAndLinks();
            BrandedPageAndDisposal();
            FooterIsPassiveAndLast();
            NavigationStaysLazy();
            if (imageOutputDirectory is not null) RenderUnshown(imageOutputDirectory);
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL Cortex About: " + error); return 1; }
        finally { CortexTheme.SetAppearance(previousAppearance); }
        Console.WriteLine($"PASS Cortex About: {_checks} injected unshown-control checks; shared branded tab/dialog, real product icon, creator/assistance/license/disclaimer, local build metadata, exact links, lazy footer and five-destination navigation; no browser, network, product calls or runtime probe");
        return 0;
    }

    private static void DialogContentAndLinks()
    {
        var browserCalls = new List<Uri>();
        var themes = 0;
        using (var dialog = new CortexAboutDialog(new("fixture-build"), browserCalls.Add, control => { themes++; ApplyFixtureTheme(control); }))
        {
            Check(!dialog.Visible && themes == 1 && browserCalls.Count == 0, "construction only displays injected local information");
            Check(Text(dialog, "About build version").Contains("fixture-build", StringComparison.Ordinal), "the supplied build version is displayed");
            Check(Text(dialog, "About purpose").Contains("task continuity", StringComparison.Ordinal) &&
                Text(dialog, "About why Cortex was built").Contains("lessons", StringComparison.Ordinal) &&
                Text(dialog, "About why Cortex was built").Contains("people in control", StringComparison.Ordinal),
                "project rationale stays within continuity, learning and human control");
            Check(Text(dialog, "About local privacy").Contains("no network or AI API calls", StringComparison.Ordinal) &&
                Text(dialog, "About local privacy").Contains("only when you choose", StringComparison.Ordinal),
                "About privacy is scoped to this local window and explicit link actions");
            Check(!Descendants(dialog).Any(control => control.AccessibleName == "About author") &&
                dialog.Links.All(link => link.Key != "portfolio"), "unknown author/portfolio are not invented");
            Check(dialog.Links.Single(link => link.Key == "website").Address.AbsoluteUri == "https://cerebral.shaynewinter.com/cortex/",
                "project website uses the exact authorized public location");
            Check(dialog.Links.Single(link => link.Key == "project").Address.AbsoluteUri == "https://github.com/swpegasus/Pegasus-Cortex" &&
                dialog.Links.Single(link => link.Key == "downloads").Address.AbsoluteUri == "https://github.com/swpegasus/Pegasus-Cortex/releases",
                "project and release coordinates are exact");
            foreach (var link in dialog.Links) dialog.OpenLink(link.Key);
            Check(browserCalls.SequenceEqual(dialog.Links.Select(link => link.Address)), "each explicit action dispatches only its selected fixed link");
            var calls = browserCalls.Count;
            try { dialog.OpenLink("not-a-link"); throw new InvalidOperationException("Unknown link was accepted."); }
            catch (ArgumentOutOfRangeException) { }
            Check(browserCalls.Count == calls, "unrecognized link cannot launch a browser");
            Check(dialog.CancelButton is Button { DialogResult: DialogResult.Cancel }, "close and Escape use normal dialog ownership");
            Check(Descendants(dialog).OfType<CortexAboutPage>().Count() == 1,
                "the dialog hosts the same single About page rather than a second content implementation");
        }
        var portfolio = new Uri("https://example.test/portfolio");
        using (var dialog = new CortexAboutDialog(new("fixture-build", "Fixture Author", portfolio), browserCalls.Add, ApplyFixtureTheme))
        {
            Check(Text(dialog, "About author") == "Created by Fixture Author" &&
                dialog.Links.Single(link => link.Key == "portfolio").Address == portfolio,
                "verified author and portfolio can be injected without a biography or network request");
        }
        var errors = 0;
        using (var dialog = new CortexAboutDialog(new("fixture-build"), _ => { errors++; throw new IOException("fixture browser failure"); }, ApplyFixtureTheme))
        {
            dialog.OpenLink("downloads");
            Check(errors == 1 && Text(dialog, "About link status").Contains("could not be opened", StringComparison.Ordinal),
                "browser failure is displayed without replay or alternate route");
        }
        var current = CortexAboutInformation.Current();
        var version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unavailable";
        Check(current.BuildVersion == version, "current version comes from the executing Host assembly metadata rather than a registry or source file");
        Check(current.Author == "Shayne Winter" && current.Portfolio?.AbsoluteUri == "https://shaynewinter.com/",
            "current public author and portfolio match the verified published portfolio");
    }

    private static void BrandedPageAndDisposal()
    {
        var actions = new List<Uri>();
        using var page = new CortexAboutPage(new("fixture-build", "Shayne Winter", new Uri("https://shaynewinter.com/")),
            actions.Add, ApplyFixtureTheme);
        page.Size = new Size(820, 680);
        page.PerformLayout();
        var logo = Descendants(page).OfType<PictureBox>().Single();
        var namedControls = Descendants(page).Where(control => !string.IsNullOrWhiteSpace(control.AccessibleName)).ToArray();
        Check(namedControls.GroupBy(control => control.AccessibleName ?? string.Empty, StringComparer.Ordinal).All(group => group.Count() == 1) &&
            page.Links.All(link => namedControls.Single(control => control.AccessibleName == "About link " + link.Key) is LinkLabel),
            "link accessibility identities are unique and distinct from their descriptive paragraphs");
        Check(page.FindForm() is null && actions.Count == 0 && logo.Image is { Width: >= 64, Height: >= 64 } &&
            logo.AccessibleName == "Pegasus Cortex product logo" && !logo.TabStop,
            "the unshown local page uses the existing embedded product icon without an eager browser action");
        Check(Descendants(page).OfType<Panel>().Any(control => control.AutoScroll) &&
            Descendants(page).OfType<Label>().All(control => control.AutoSize && control.MaximumSize.Width > 0),
            "About detail and link text wrap within a scrollable page rather than a fixed clipped canvas");
        Check(Text(page, "About tagline") == "Built for real work. Used by its creator." &&
            Text(page, "About evolution").Contains("Portable project notes", StringComparison.Ordinal),
            "branding includes the requested personal tagline and bounded product evolution");
        Check(Text(page, "About author") == "Created by Shayne Winter" &&
            Text(page, "About development credits") == "OpenAI Codex\nPegasus Cerebral" &&
            Text(page, "About copyright") == "Copyright © 2026 Shayne Winter." &&
            Text(page, "About license").Contains("Pegasus Community License 1.0", StringComparison.Ordinal),
            "creator ownership and licensed rights stay distinct from development assistance credits");
        var disclaimer = Text(page, "About development disclaimer");
        Check(disclaimer.Contains("AI assistance can make mistakes", StringComparison.Ordinal) &&
            disclaimer.Contains("Review actions and outputs", StringComparison.Ordinal) &&
            disclaimer.Contains("change or delete files and system settings", StringComparison.Ordinal) &&
            disclaimer.Contains("keep backups", StringComparison.Ordinal) &&
            disclaimer.Contains("license contains the authoritative terms", StringComparison.Ordinal),
            "the concise disclaimer states practical development risks and defers legal terms to the license");
        foreach (var (key, fragment) in new[] { ("purpose", "why"), ("evolution", "evolution"), ("license", "license") })
        {
            Check(page.Links.Single(link => link.Key == key).Address.AbsoluteUri ==
                "https://cerebral.shaynewinter.com/cortex/#" + fragment,
                "the selected learning link uses the website owner's real section: " + key);
        }
        page.OpenLink("evolution");
        Check(actions.Count == 1 && actions[0] == page.Links.Single(link => link.Key == "evolution").Address,
            "the evolution link opens only on explicit action");
        page.Dispose();
        page.Dispose();
        page.OpenLink("project");
        Check(logo.Image is null && page.IsDisposed && actions.Count == 1,
            "owned branding is released once and a disposed page cannot open a browser");
    }

    private static void FooterIsPassiveAndLast()
    {
        var shows = 0; var themes = 0;
        var tabs = new TabControl();
        foreach (var title in new[] { "Appearance", "Administrator service", "Processors", "MCP", "Storage", "About" })
            tabs.TabPages.Add(new TabPage(title));
        using var section = new CortexSettingsAboutSection(tabs, _ => shows++, control => { themes++; ApplyFixtureTheme(control); });
        Check(shows == 0 && themes == 1 && section.Parent is null, "footer construction does not construct/show About or invoke a browser");
        var layout = section.Controls.OfType<TableLayoutPanel>().Single();
        var footer = Descendants(section).Single(control => control.AccessibleName == "Settings About footer");
        Check(layout.RowCount == 2 && layout.GetRow(tabs) == 0 && layout.GetRow(footer) == 1 &&
            footer.Controls.OfType<Button>().Single().AccessibleName == "About Cortex",
            "the About window button stays below the settings tabs, including the separate About tab");
        section.Tabs.SelectedIndex = 1;
        Check(ReferenceEquals(section.Tabs, tabs) && tabs.SelectedTab?.Text == "Administrator service" && tabs.TabCount == 6,
            "the same settings tabs preserve the administrator-service index");
        section.OpenAbout();
        Check(shows == 1, "About is shown only on explicit action");
        section.Dispose(); section.OpenAbout();
        Check(shows == 1, "disposed settings footer cannot create another dialog");
    }

    private static void NavigationStaysLazy()
    {
        using var settings = new SettingsWindow(() => new("Fixture", "Not loaded", null, "Unavailable", "Disabled"));
        Check(settings.SelectedDestination == "Overview" && Descendants(settings).OfType<RadioButton>().Count() == 5 &&
            !Descendants(settings).OfType<CortexSettingsAboutSection>().Any(), "Overview retains exactly five destinations and creates no Settings/About page");
        settings.Navigate("Settings & support");
        var section = Descendants(settings).OfType<CortexSettingsAboutSection>().Single();
        Check(section.Tabs.SelectedIndex == 0 && section.Tabs.TabPages.Cast<TabPage>().Select(page => page.Text)
                .SequenceEqual(["Appearance", "Administrator service", "Processors", "MCP", "Storage", "About"]) &&
            !Descendants(settings).OfType<ServiceNotificationsPage>().Any() &&
            !Descendants(settings).OfType<CortexAboutPage>().Any(),
            "settings retain lazy Appearance first and append About without constructing it or the service page");
        // SelectedIndex can be cached before a TabControl has its native handle.
        // Establish only this owned control before testing the real selection event.
        _ = section.Tabs.Handle;
        Check(section.Tabs.IsHandleCreated && !settings.Visible &&
            !Descendants(settings).OfType<CortexAboutPage>().Any() &&
            !Descendants(settings).OfType<ServiceNotificationsPage>().Any(),
            "owned tab handle creation stays unshown and does not eagerly construct About or service pages");
        section.Tabs.SelectedIndex = 5;
        Check(Descendants(settings).OfType<CortexAboutPage>().Count() == 1 &&
            section.Tabs.TabPages[1].Text == "Administrator service",
            "selecting About constructs one page while preserving administrator-service index one");
        settings.Navigate("Overview");
        Check(settings.SelectedDestination == "Overview" && Descendants(settings).OfType<RadioButton>().Count() == 5,
            "footer does not alter navigation or add a sixth destination");
    }

    private static void ApplyFixtureTheme(Control root)
    {
        root.ForeColor = Color.Black; root.BackColor = Color.White;
        foreach (Control child in root.Controls) ApplyFixtureTheme(child);
    }

    private static void RenderUnshown(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("Use an absolute task-owned image directory.");
        var target = Path.GetFullPath(directory);
        PathNormalizer.RejectAbsoluteReparseComponents(target);
        if (!Directory.Exists(target) || new DirectoryInfo(target).LinkTarget is not null)
            throw new IOException("Invalid fixture image directory.");
        var destination = Path.Combine(target, "cortex-about.png");
        if (destination.Length > 240) throw new IOException("Fixture image path exceeds the Windows path budget.");
        using var form = new Form { ClientSize = new Size(1200, 900), ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font };
        using var page = new CortexAboutPage(CortexAboutInformation.Current(),
            _ => throw new InvalidOperationException("Capture must not open a browser."));
        form.Controls.Add(page);
        CortexTheme.Apply(form);
        _ = form.Handle;
        foreach (var control in Descendants(form).ToArray()) _ = control.Handle;
        form.PerformLayout();
        foreach (var panel in Descendants(form).OfType<TableLayoutPanel>()) panel.PerformLayout();
        foreach (var panel in Descendants(form).OfType<FlowLayoutPanel>()) panel.PerformLayout();
        Check(!form.Visible && page.Size == new Size(1200, 900),
            "the About capture stays unshown with a useful exact client area");
        var logo = Descendants(page).OfType<PictureBox>().Single();
        Check(logo.Image is not null && logo.Width >= 96 && logo.Height >= 96 &&
            page.ClientRectangle.Contains(BoundsIn(page, logo)),
            "the actual product icon has useful visible bounds in the About capture");
        foreach (var name in new[] { "About product name", "About build version", "About purpose",
            "About author", "About development credits", "About copyright", "About development disclaimer",
            "About link website", "About link project", "About link portfolio" })
        {
            var control = Descendants(page).Single(candidate => candidate.AccessibleName == name);
            Check(control.Text.Length > 0 && control.Width > 0 && control.Height > 0 &&
                page.ClientRectangle.Contains(BoundsIn(page, control)),
                "About capture text is nonempty and fully contained: " + name);
        }
        var purpose = Descendants(page).Single(control => control.AccessibleName == "About purpose");
        var disclaimer = Descendants(page).Single(control => control.AccessibleName == "About development disclaimer");
        Check(purpose.Width >= 300 && disclaimer.Width >= 500,
            "About capture allocates useful wrapped text widths rather than a narrow clipped strip");
        using var bitmap = new Bitmap(1200, 900);
        page.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bitmap.Save(output, ImageFormat.Png);
        // All scopes, including image/page ownership and output flush, close
        // before Run restores the theme and reports PASS.
    }

    private static Rectangle BoundsIn(Control root, Control child)
    {
        var offset = Point.Empty;
        Control? current = child;
        while (current is not null && !ReferenceEquals(current, root))
        {
            offset.Offset(current.Left, current.Top);
            current = current.Parent;
        }
        if (current is null) throw new InvalidOperationException("The capture control is outside its owned page.");
        return new Rectangle(offset, child.Size);
    }

    private static string Text(Control root, string name) => Descendants(root).Single(control => control.AccessibleName == name).Text;
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException("About fixture: " + message);
    }
}
