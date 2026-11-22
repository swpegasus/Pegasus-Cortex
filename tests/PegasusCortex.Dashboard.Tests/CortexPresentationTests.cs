using System.Drawing.Imaging;
using Pegasus.AgentOps.Engine;
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
            BrandingPaintAndLifetime();

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
            var build = controls.OfType<Label>().Single(control => control.AccessibleName == "Cortex build version");
            Check(build.Text == "Build\n" + CortexAboutInformation.Current().BuildVersion && build.AutoSize &&
                build.MaximumSize.Width > 0 && !build.UseMnemonic && !build.TabStop,
                "The build is readable wrapping text from the executing Host, not text baked into artwork");
            Check(build.ForeColor == CortexTheme.Palette.Ink &&
                Contrast(build.ForeColor, build.BackColor) >= 4.5,
                "Version text retains normal theme contrast independently of the decorative watermark");
            var overview = controls.OfType<CortexOverviewPage>().Single();
            var labels = Descendants(overview).OfType<Label>().ToArray();
            Check(Value(labels, "User execution host") == "Running" && Value(labels, "Personal profile") == "Not loaded" &&
                Value(labels, "MCP connection") == "Unavailable" && Value(labels, "Next Windows sign-in") == "Disabled",
                "Independent lifecycle facts were collapsed or inferred");
            _ = settings.Handle;
            settings.PerformLayout();
            var navigation = build.Parent ?? throw new InvalidOperationException("Build label has no navigation owner.");
            navigation.PerformLayout();
            Check(!settings.Visible && navigation.ClientRectangle.Contains(build.Bounds) &&
                build.Height >= build.Font.Height * 2 &&
                build.Top >= navigation.Controls.OfType<RadioButton>().Max(button => button.Bottom),
                "The unshown window keeps the complete build label below navigation and inside useful bounds");
            if (imageDirectory is not null) RenderUnshown(settings, imageDirectory, "overview-light.png");

            settings.Navigate("Settings & support");
            Check(!Descendants(settings).OfType<ServiceNotificationsPage>().Any(), "Appearance eagerly constructed the optional-service page");
            Check(Descendants(settings).OfType<ComboBox>().Any(control => control.AccessibleName == "Window appearance"),
                "The appearance setting is not available");
            if (imageDirectory is not null) RenderUnshown(settings, imageDirectory, "appearance-light.png");
            settings.Navigate("Overview");
            Check(settings.SelectedDestination == "Overview", "Navigation did not return to Overview");
            if (imageDirectory is not null)
            {
                CortexTheme.SetAppearance(CortexAppearance.Dark);
                RenderUnshown(settings, imageDirectory, "overview-dark.png");
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
            settings.Dispose();
            settings.Dispose();
            Check(settings.IsDisposed, "Repeated window disposal releases its decorative ownership normally");
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL Cortex presentation: " + error); return 1; }
        finally { CortexTheme.SetAppearance(previousAppearance); }
        Console.WriteLine("PASS Cortex presentation: palette contrast, system/high-contrast resolution, subtle raster watermark and readable build, decorative ownership, lazy navigation, independent/unknown session facts and dynamic styling (isolated WinForms fixture; no live service or database)");
        return 0;
    }

    private static void BrandingPaintAndLifetime()
    {
        using var firstLogo = CortexBranding.LoadLogo();
        using var secondLogo = CortexBranding.LoadLogo();
        Check(firstLogo.Width >= 64 && firstLogo.Height >= 64 && firstLogo.Size == secondLogo.Size &&
            !ReferenceEquals(firstLogo, secondLogo), "Embedded raster consumers receive independent owned images");
        firstLogo.Dispose();
        Check(secondLogo.Width >= 64, "Releasing one logo does not invalidate another window's artwork");

        using var synthetic = new Bitmap(60, 40);
        using (var fill = Graphics.FromImage(synthetic)) fill.Clear(Color.Blue);
        var canvasSize = new Size(320, 240);
        foreach (var dpi in new[] { 96, 192 })
        {
            var bounds = CortexBranding.WatermarkBounds(canvasSize, synthetic.Size, dpi);
            Check(!bounds.IsEmpty && new Rectangle(Point.Empty, canvasSize).Contains(bounds) &&
                Math.Abs(bounds.Width / (double)bounds.Height - 1.5d) < .02d,
                "Watermark fits its DPI-scaled chrome bounds without changing the artwork's aspect ratio");
            using var decorated = new Bitmap(canvasSize.Width, canvasSize.Height);
            using (var graphics = Graphics.FromImage(decorated))
            {
                graphics.Clear(Color.White);
                CortexBranding.PaintWatermark(graphics, canvasSize, synthetic, dpi, highContrast: false, tint: Color.Blue);
            }
            var sample = decorated.GetPixel(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Check(sample.R < 255 && sample.R >= 239 && sample.G >= 239 && sample.B == 255,
                "Normal decoration is visible but bounded to a faint background tint");
            using var highContrast = new Bitmap(canvasSize.Width, canvasSize.Height);
            using (var graphics = Graphics.FromImage(highContrast))
            {
                graphics.Clear(Color.White);
                CortexBranding.PaintWatermark(graphics, canvasSize, synthetic, dpi, highContrast: true, tint: Color.Blue);
            }
            Check(!HasDifferentPixel(highContrast, Color.White), "High contrast suppresses the decorative pixels completely");
        }
        Check(CortexBranding.WatermarkBounds(new Size(1, 1), synthetic.Size, 192).IsEmpty,
            "A chrome surface too small for its DPI padding produces no invalid image rectangle");

        using var surface = new Panel { Size = canvasSize, BackColor = Color.FromArgb(120, 120, 120) };
        var originalTabStop = surface.TabStop;
        using var attachment = CortexBranding.AttachWatermark(surface);
        _ = surface.Handle;
        using var beforeDetach = new Bitmap(surface.Width, surface.Height);
        surface.DrawToBitmap(beforeDetach, surface.ClientRectangle);
        Check(surface.Controls.Count == 0 && surface.TabStop == originalTabStop,
            "A paint attachment adds no overlay control, navigation target or input interception");
        if (!SystemInformation.HighContrast)
            Check(HasDifferentPixel(beforeDetach, surface.BackColor), "The actual embedded watermark paints in an unshown ordinary panel");
        attachment.Dispose();
        attachment.Dispose();
        using var afterDetach = new Bitmap(surface.Width, surface.Height);
        surface.DrawToBitmap(afterDetach, surface.ClientRectangle);
        Check(!HasDifferentPixel(afterDetach, surface.BackColor), "Disposal detaches painting and permits repeated disposal");
        using var ownerAttachment = CortexBranding.AttachWatermark(surface);
        surface.Dispose();
        ownerAttachment.Dispose();
        Check(surface.IsDisposed, "Disposing the surface also releases its decoration before a later explicit disposal");
    }

    private static bool HasDifferentPixel(Bitmap bitmap, Color background)
    {
        var expected = background.ToArgb();
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).ToArgb() != expected) return true;
        return false;
    }

    private static void RenderUnshown(SettingsWindow form, string directory, string file)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("Use an absolute task-owned image directory.");
        var target = Path.GetFullPath(directory);
        PathNormalizer.RejectAbsoluteReparseComponents(target);
        if (!Directory.Exists(target) || new DirectoryInfo(target).LinkTarget is not null)
            throw new IOException("Invalid fixture image directory.");
        var destination = Path.Combine(target, file);
        if (destination.Length > 240) throw new IOException("Fixture image path exceeds the Windows path budget.");
        form.ClientSize = new Size(1160, 780);
        form.ShowInTaskbar = false;
        CortexTheme.Apply(form);
        _ = form.Handle;
        foreach (var control in Descendants(form).ToArray()) _ = control.Handle;
        form.PerformLayout();
        foreach (var panel in Descendants(form).OfType<FlowLayoutPanel>()) panel.PerformLayout();
        Check(!form.Visible && form.ClientSize == new Size(1160, 780),
            "Presentation capture uses an unshown window with a useful exact client area");
        var version = Descendants(form).OfType<Label>().Single(control => control.AccessibleName == "Cortex build version");
        Check(version.Parent is { } owner && owner.ClientRectangle.Contains(version.Bounds) &&
            version.Text.Contains(CortexBranding.BuildVersion, StringComparison.Ordinal),
            "Presentation capture retains the actual complete build text inside its owner");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bitmap.Save(output, ImageFormat.Png);
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
