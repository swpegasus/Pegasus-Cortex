using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace Pegasus.AgentOps.Tray;

internal enum CortexAppearance { System, Light, Dark }

internal sealed record CortexPalette(Color Window, Color Surface, Color RaisedSurface, Color Ink,
    Color Muted, Color Accent, Color OnAccent, Color Line, Color Hover, Color Pressed,
    Color Selection, Color SelectionText);

/// <summary>Local WinForms rendering tokens, shared with the Cerebral Blue White visual language.</summary>
internal static class CortexTheme
{
    private static readonly ConditionalWeakTable<Control, object> Attached = new();
    private static CortexPalette? _palette;
    private static readonly Font BodyFont = new("Segoe UI", 10f);
    internal static readonly Font TitleFont = new("Segoe UI", 22f, FontStyle.Bold);
    internal static readonly Font HeadingFont = new("Segoe UI", 13f, FontStyle.Bold);
    internal static CortexAppearance Appearance { get; private set; } = CortexAppearance.System;

    internal static CortexPalette Resolve(CortexAppearance appearance, bool systemDark, bool highContrast)
    {
        if (highContrast)
            return new(SystemColors.Window, SystemColors.Window, SystemColors.Control, SystemColors.WindowText,
                SystemColors.WindowText, SystemColors.Highlight, SystemColors.HighlightText, SystemColors.WindowText,
                SystemColors.Control, SystemColors.ControlDark, SystemColors.Highlight, SystemColors.HighlightText);
        // These are the existing Cerebral Blue White tokens, without a cross-product assembly dependency.
        string[] values = appearance == CortexAppearance.Dark || (appearance == CortexAppearance.System && systemDark)
            ? ["#101A2D", "#16243A", "#1E304A", "#EFF5FF", "#B4C3DA", "#80B0FF", "#0B234A", "#354A68", "#253D60", "#304B72", "#F3F7FF", "#102544"]
            : ["#F3F6FB", "#FFFFFF", "#F8FAFF", "#172B4D", "#52647E", "#245BCE", "#FFFFFF", "#D5DFEF", "#EAF1FF", "#DCE7FA", "#DBE8FF", "#15356E"];
        var colors = values.Select(ColorTranslator.FromHtml).ToArray();
        return new(colors[0], colors[1], colors[2], colors[3], colors[4], colors[5], colors[6], colors[7],
            colors[8], colors[9], colors[10], colors[11]);
    }

    internal static CortexPalette Palette => _palette ??= ReadPalette();

    private static CortexPalette ReadPalette() => Resolve(Appearance,
        Appearance == CortexAppearance.System && SystemUsesDarkAppearance(), SystemInformation.HighContrast);

    internal static void SetAppearance(CortexAppearance appearance)
    {
        Appearance = appearance;
        _palette = null;
        // Appearance is deliberately session-only. No registry, startup, profile or product settings are written.
        foreach (Form form in Application.OpenForms) Apply(form);
    }

    internal static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is Form) root.Font = BodyFont;
        _palette = ReadPalette();
        ApplyTree(root, _palette);
    }

    internal static void Apply(ContextMenuStrip menu)
    {
        Apply((Control)menu);
        menu.Opening -= OnMenuOpening;
        menu.Opening += OnMenuOpening;
        StyleMenu(menu, Palette);
    }

    private static void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (sender is not ContextMenuStrip menu) return;
        Apply((Control)menu);
        StyleMenu(menu, Palette);
    }

    private static void StyleMenu(ContextMenuStrip menu, CortexPalette palette)
    {
        menu.Font = BodyFont;
        menu.Renderer = SystemInformation.HighContrast
            ? new ToolStripSystemRenderer()
            : new MenuRenderer(palette);
        foreach (ToolStripItem item in menu.Items) { item.BackColor = palette.Surface; item.ForeColor = palette.Ink; }
    }

    private static void ApplyTree(Control control, CortexPalette palette)
    {
        control.ForeColor = palette.Ink;
        control.BackColor = control is Form or UserControl or SplitContainer ? palette.Window : palette.Surface;
        switch (control)
        {
            case Button button:
                button.UseVisualStyleBackColor = SystemInformation.HighContrast;
                button.FlatStyle = SystemInformation.HighContrast ? FlatStyle.System : FlatStyle.Flat;
                button.FlatAppearance.BorderColor = palette.Line;
                button.FlatAppearance.MouseOverBackColor = palette.Hover;
                button.FlatAppearance.MouseDownBackColor = palette.Pressed;
                button.Padding = new Padding(9, 4, 9, 4);
                button.MinimumSize = new Size(0, 34);
                break;
            case RadioButton radio when radio.Appearance == System.Windows.Forms.Appearance.Button:
                radio.FlatStyle = SystemInformation.HighContrast ? FlatStyle.System : FlatStyle.Flat;
                radio.FlatAppearance.BorderColor = radio.Checked ? palette.Accent : palette.Line;
                radio.FlatAppearance.MouseOverBackColor = palette.Hover;
                radio.FlatAppearance.MouseDownBackColor = palette.Pressed;
                radio.BackColor = radio.Checked ? palette.Selection : palette.Surface;
                radio.ForeColor = radio.Checked ? palette.SelectionText : palette.Ink;
                radio.Padding = new Padding(10, 4, 4, 4);
                break;
            case TextBoxBase text:
                text.BackColor = palette.RaisedSurface;
                break;
            case ListBox list:
                list.BackColor = palette.RaisedSurface;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ListView list:
                list.BackColor = palette.RaisedSurface;
                break;
            case TabControl tabs:
                tabs.DrawMode = SystemInformation.HighContrast ? TabDrawMode.Normal : TabDrawMode.OwnerDrawFixed;
                break;
            case ToolStrip strip:
                strip.Renderer = SystemInformation.HighContrast ? new ToolStripSystemRenderer() : new MenuRenderer(palette);
                foreach (ToolStripItem item in strip.Items) { item.BackColor = palette.Surface; item.ForeColor = palette.Ink; }
                break;
            case DataGridView grid:
                grid.BackgroundColor = palette.Surface;
                grid.BorderStyle = BorderStyle.None;
                grid.GridColor = palette.Line;
                grid.EnableHeadersVisualStyles = SystemInformation.HighContrast;
                grid.DefaultCellStyle.BackColor = palette.Surface;
                grid.DefaultCellStyle.ForeColor = palette.Ink;
                grid.DefaultCellStyle.SelectionBackColor = palette.Selection;
                grid.DefaultCellStyle.SelectionForeColor = palette.SelectionText;
                grid.ColumnHeadersDefaultCellStyle.BackColor = palette.RaisedSurface;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Ink;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.RaisedSurface;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = palette.Ink;
                grid.ColumnHeadersDefaultCellStyle.Font = BodyFont;
                grid.RowTemplate.Height = 30;
                grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                break;
        }
        if (!Attached.TryGetValue(control, out _))
        {
            Attached.Add(control, new object());
            // Registry results and lazily opened pages arrive after the first render.
            control.ControlAdded += (_, args) => { if (args.Control is { } child) ApplyTree(child, Palette); };
            if (control is TabControl tabs)
                tabs.DrawItem += (_, args) => DrawTab(tabs, args);
            if (control is Form form)
            {
                form.SystemColorsChanged += (_, _) => Apply(form);
                form.Activated += (_, _) => Apply(form);
            }
        }
        foreach (Control child in control.Controls) ApplyTree(child, palette);
    }

    private static void DrawTab(TabControl tabs, DrawItemEventArgs args)
    {
        if (args.Index < 0 || args.Index >= tabs.TabPages.Count) return;
        var palette = Palette;
        var selected = args.Index == tabs.SelectedIndex;
        using var background = new SolidBrush(selected ? palette.Selection : palette.RaisedSurface);
        args.Graphics.FillRectangle(background, args.Bounds);
        TextRenderer.DrawText(args.Graphics, tabs.TabPages[args.Index].Text, tabs.Font, args.Bounds,
            selected ? palette.SelectionText : palette.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if ((args.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(args.Bounds, -4, -4), palette.Ink,
                selected ? palette.Selection : palette.RaisedSurface);
    }

    private static bool SystemUsesDarkAppearance()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", null) is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private sealed class MenuColors(CortexPalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.Surface;
        public override Color ImageMarginGradientBegin => palette.Surface;
        public override Color ImageMarginGradientMiddle => palette.Surface;
        public override Color ImageMarginGradientEnd => palette.Surface;
        public override Color MenuItemSelected => palette.Hover;
        public override Color MenuItemBorder => palette.Accent;
        public override Color MenuBorder => palette.Line;
        public override Color SeparatorDark => palette.Line;
        public override Color SeparatorLight => palette.Surface;
        public override Color ToolStripGradientBegin => palette.Surface;
        public override Color ToolStripGradientMiddle => palette.Surface;
        public override Color ToolStripGradientEnd => palette.Surface;
        public override Color ButtonSelectedGradientBegin => palette.Hover;
        public override Color ButtonSelectedGradientMiddle => palette.Hover;
        public override Color ButtonSelectedGradientEnd => palette.Hover;
        public override Color ButtonPressedGradientBegin => palette.Pressed;
        public override Color ButtonPressedGradientMiddle => palette.Pressed;
        public override Color ButtonPressedGradientEnd => palette.Pressed;
    }

    private sealed class MenuRenderer(CortexPalette palette) : ToolStripProfessionalRenderer(new MenuColors(palette))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs args)
        {
            args.TextColor = args.Item.Enabled ? palette.Ink : palette.Muted;
            base.OnRenderItemText(args);
        }
    }
}
