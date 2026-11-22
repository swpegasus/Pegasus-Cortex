using Pegasus.AgentOps.Tray;

internal static class CortexPresentationTests
{
    internal static int Run(string? imageDirectory = null)
    {
        var previousAppearance = CortexTheme.Appearance;
        try
        {
            // Fix the appearance so the fixture never reads the user's Windows theme registry setting.
            CortexTheme.SetAppearance(CortexAppearance.Light);
            foreach (var appearance in new[] { CortexAppearance.Light, CortexAppearance.Dark })
            {
                var palette = CortexTheme.Resolve(appearance, false, false);
                foreach (var surface in new[] { palette.Window, palette.Surface, palette.RaisedSurface })
                {
                    Check(Contrast(palette.Ink, surface) >= 4.5, appearance + " body contrast");
                    Check(Contrast(palette.Muted, surface) >= 4.5, appearance + " secondary contrast");
                    Check(Contrast(palette.Accent, surface) >= 4.5, appearance + " accent contrast");
                }
                Check(Contrast(palette.SelectionText, palette.Selection) >= 4.5, appearance + " selected text contrast");
                Check(Contrast(palette.OnAccent, palette.Accent) >= 4.5, appearance + " accent text contrast");
            }
            Check(CortexTheme.Resolve(CortexAppearance.System, true, false) == CortexTheme.Resolve(CortexAppearance.Dark, false, false),
                "System did not resolve the requested Windows app appearance");
            Check(CortexTheme.Resolve(CortexAppearance.Dark, true, true).Ink == SystemColors.WindowText &&
                CortexTheme.Resolve(CortexAppearance.Light, false, true).Selection == SystemColors.Highlight,
                "High contrast did not override an explicit appearance choice");

            var reads = 0;
            using var settings = new SettingsWindow(() =>
            {
                reads++;
                return new("Running", "Not loaded", null, "Unavailable", "Disabled");
            });
            Check(settings.SelectedDestination == "Overview" && reads > 0, "Overview did not use its supplied session facts");
            var controls = Descendants(settings).ToArray();
            Check(controls.OfType<RadioButton>().Count() == 5, "Task destinations are missing");
            Check(!controls.OfType<UtilizationPage>().Any() && !controls.OfType<ServiceNotificationsPage>().Any(),
                "Initial Overview eagerly constructed a database or service page");
            var overview = controls.OfType<CortexOverviewPage>().Single();
            var labels = Descendants(overview).OfType<Label>().ToArray();
            Check(Value(labels, "User execution host") == "Running" && Value(labels, "Personal profile") == "Not loaded" &&
                Value(labels, "MCP connection") == "Unavailable" && Value(labels, "Next Windows sign-in") == "Disabled",
                "Independent lifecycle facts were collapsed or inferred");
            if (imageDirectory is not null) DashboardTestProgram.Render(settings, imageDirectory, "overview-light.png");

            settings.Navigate("Settings & support");
            Check(!Descendants(settings).OfType<ServiceNotificationsPage>().Any(), "Appearance eagerly constructed the optional-service page");
            Check(Descendants(settings).OfType<ComboBox>().Any(control => control.AccessibleName == "Window appearance"),
                "The appearance setting is not available");
            if (imageDirectory is not null) DashboardTestProgram.Render(settings, imageDirectory, "appearance-light.png");
            settings.Navigate("Overview");
            Check(settings.SelectedDestination == "Overview", "Navigation did not return to Overview");
            if (imageDirectory is not null)
            {
                CortexTheme.SetAppearance(CortexAppearance.Dark);
                DashboardTestProgram.Render(settings, imageDirectory, "overview-dark.png");
                CortexTheme.SetAppearance(CortexAppearance.Light);
            }

            using var absent = new CortexOverviewPage(null, () => { });
            var missing = Descendants(absent).OfType<Label>().ToArray();
            Check(Value(missing, "User execution host") == "Unknown" && Value(missing, "MCP connection") == "Unknown" &&
                Value(missing, "Next Windows sign-in") == "Unknown", "Missing status was presented as ready/running");
            using var faulted = new CortexOverviewPage(() => throw new InvalidOperationException("fixture"), () => { });
            Check(Value(Descendants(faulted).OfType<Label>(), "User execution host") == "Unknown", "A failed display callback inferred host state");
            using var container = new Panel();
            CortexTheme.Apply(container);
            var field = new TextBox();
            container.Controls.Add(field);
            Check(field.BackColor == CortexTheme.Palette.RaisedSurface && field.ForeColor == CortexTheme.Palette.Ink,
                "Controls added after first render did not receive the theme");
            Console.WriteLine("PASS Cortex presentation: palette contrast, system/high-contrast resolution, lazy navigation, independent/unknown session facts and dynamic styling (isolated WinForms fixture; no live service or database)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL Cortex presentation: " + error); return 1; }
        finally { CortexTheme.SetAppearance(previousAppearance); }
    }

    private static string Value(IEnumerable<Label> labels, string name) => labels.Single(label => label.AccessibleName == name).Text;

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
