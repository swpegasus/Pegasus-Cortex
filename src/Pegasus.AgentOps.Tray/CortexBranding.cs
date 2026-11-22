using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Pegasus.AgentOps.Tray;

/// <summary>Embedded product artwork; each consumer owns its decoded image and decorative lifetime.</summary>
internal static class CortexBranding
{
    private const string LogoResource = "Pegasus.AgentOps.Tray.BrandLogo";
    private const string WatermarkResource = "Pegasus.AgentOps.Tray.Watermark";

    internal static string BuildVersion => CortexAboutInformation.Current().BuildVersion;

    internal static Image LoadLogo() => LoadRaster(LogoResource);

    internal static IDisposable AttachWatermark(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.IsDisposed || surface.Disposing) throw new ObjectDisposedException(nameof(surface));
        return new WatermarkAttachment(surface, LoadRaster(WatermarkResource));
    }

    private static Image LoadRaster(string resource)
    {
        using var stream = typeof(CortexBranding).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The embedded Cortex branding resource is unavailable: " + resource);
        using var original = Image.FromStream(stream);
        // Detach from the resource stream. No live image or file handle is shared between windows.
        return new Bitmap(original);
    }

    internal static Rectangle WatermarkBounds(Size surface, Size artwork, int dpi)
    {
        if (artwork.Width <= 0 || artwork.Height <= 0) return Rectangle.Empty;
        var padding = Math.Max(1, (int)Math.Round(16d * Math.Max(1, dpi) / 96d));
        var width = surface.Width - padding * 2;
        var height = surface.Height - padding * 2;
        if (width <= 0 || height <= 0) return Rectangle.Empty;
        var scale = Math.Min(width / (double)artwork.Width, height / (double)artwork.Height);
        var size = new Size(Math.Max(1, (int)Math.Floor(artwork.Width * scale)),
            Math.Max(1, (int)Math.Floor(artwork.Height * scale)));
        return new Rectangle(surface.Width - padding - size.Width, surface.Height - padding - size.Height,
            size.Width, size.Height);
    }

    internal static void PaintWatermark(Graphics graphics, Size surface, Image artwork, int dpi, bool highContrast, Color tint)
    {
        // Decorations never compete with a Windows high-contrast palette. Version text is separate.
        if (highContrast) return;
        var bounds = WatermarkBounds(surface, artwork.Size, dpi);
        if (bounds.IsEmpty) return;
        using var attributes = new ImageAttributes();
        // The PNG supplies shape/alpha only; its generated colors must not create a bright
        // patch in either theme. Very faint source fringes are further attenuated by the opacity.
        attributes.SetColorMatrix(new ColorMatrix
        {
            Matrix00 = 0, Matrix11 = 0, Matrix22 = 0, Matrix33 = .055f,
            Matrix40 = tint.R / 255f, Matrix41 = tint.G / 255f, Matrix42 = tint.B / 255f
        });
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(artwork, bounds, 0, 0, artwork.Width, artwork.Height, GraphicsUnit.Pixel, attributes);
        }
        finally { graphics.Restore(state); }
    }

    private sealed class WatermarkAttachment : IDisposable
    {
        private Control? _surface;
        private Image? _artwork;

        internal WatermarkAttachment(Control surface, Image artwork)
        {
            _surface = surface;
            _artwork = artwork;
            surface.Paint += Paint;
            surface.SizeChanged += Resize;
            surface.SystemColorsChanged += ColorsChanged;
            surface.Disposed += OwnerDisposed;
            surface.Invalidate();
        }

        private void Paint(object? sender, PaintEventArgs args)
        {
            if (_surface is { IsDisposed: false } surface && _artwork is { } artwork)
                PaintWatermark(args.Graphics, surface.ClientSize, artwork, surface.DeviceDpi,
                    SystemInformation.HighContrast, CortexTheme.Palette.Accent);
        }

        private void Resize(object? sender, EventArgs args) => _surface?.Invalidate();
        private void ColorsChanged(object? sender, EventArgs args) => _surface?.Invalidate();
        private void OwnerDisposed(object? sender, EventArgs args) => Dispose();

        public void Dispose()
        {
            var surface = _surface;
            var artwork = _artwork;
            _surface = null;
            _artwork = null;
            if (surface is not null)
            {
                surface.Paint -= Paint;
                surface.SizeChanged -= Resize;
                surface.SystemColorsChanged -= ColorsChanged;
                surface.Disposed -= OwnerDisposed;
                if (!surface.IsDisposed && !surface.Disposing) surface.Invalidate();
            }
            artwork?.Dispose();
        }
    }
}
