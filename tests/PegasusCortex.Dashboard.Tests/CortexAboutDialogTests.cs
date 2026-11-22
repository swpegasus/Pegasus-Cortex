using System.Reflection;
using Pegasus.AgentOps.Tray;

internal static class CortexAboutDialogTests
{
    private static int _checks;

    internal static int Run()
    {
        var previousAppearance = CortexTheme.Appearance;
        try
        {
            _checks = 0;
            CortexTheme.SetAppearance(CortexAppearance.Light);
            DialogContentAndLinks();
            FooterIsPassiveAndLast();
            NavigationStaysLazy();
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL Cortex About: " + error); return 1; }
        finally { CortexTheme.SetAppearance(previousAppearance); }
        Console.WriteLine($"PASS Cortex About: {_checks} injected unshown-control checks; local build metadata, purpose/privacy, explicit browser links, website, lazy bottom footer and five-destination navigation; no browser, network, API or runtime probe");
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
            Check(Text(dialog, "About local privacy").Contains("no API calls", StringComparison.Ordinal) &&
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

    private static void FooterIsPassiveAndLast()
    {
        var shows = 0; var themes = 0;
        var tabs = new TabControl();
        foreach (var title in new[] { "Appearance", "Administrator service", "Processors", "MCP", "Storage" })
            tabs.TabPages.Add(new TabPage(title));
        using var section = new CortexSettingsAboutSection(tabs, _ => shows++, control => { themes++; ApplyFixtureTheme(control); });
        Check(shows == 0 && themes == 1 && section.Parent is null, "footer construction does not construct/show About or invoke a browser");
        var layout = section.Controls.OfType<TableLayoutPanel>().Single();
        var footer = Descendants(section).Single(control => control.AccessibleName == "Settings About footer");
        Check(layout.RowCount == 2 && layout.GetRow(tabs) == 0 && layout.GetRow(footer) == 1 &&
            footer.Controls.OfType<Button>().Single().AccessibleName == "About Cortex",
            "About is the bottom footer below settings tabs, not another tab");
        section.Tabs.SelectedIndex = 1;
        Check(ReferenceEquals(section.Tabs, tabs) && tabs.SelectedTab?.Text == "Administrator service" && tabs.TabCount == 5,
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
                .SequenceEqual(["Appearance", "Administrator service", "Processors", "MCP", "Storage"]) &&
            !Descendants(settings).OfType<ServiceNotificationsPage>().Any(), "settings footer retains lazy Appearance first and unchanged service/tab order");
        settings.Navigate("Overview");
        Check(settings.SelectedDestination == "Overview" && Descendants(settings).OfType<RadioButton>().Count() == 5,
            "footer does not alter navigation or add a sixth destination");
    }

    private static void ApplyFixtureTheme(Control root)
    {
        root.ForeColor = Color.Black; root.BackColor = Color.White;
        foreach (Control child in root.Controls) ApplyFixtureTheme(child);
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
