using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shamsa.Services;

namespace Shamsa.Desktop;

/// <summary>
/// Whole-desktop themes («تم‌های کامل»): one click sets the glass/accent of the top bar, dock, launcher and
/// widgets, the look of the icon groups (and optionally gathers every desktop file, folder and shortcut into
/// them on the chosen monitor), the matching Windows system icons (This PC, Recycle Bin, …) and the
/// Negarkhaneh window palette. Everything stays reversible from its own page.
/// </summary>
public static class DesktopThemePresets
{
    public sealed record Preset(
        string Id, string Name, string Description,
        string Accent, string Tint, int Radius, int Opacity, int DockIcon,
        string IconThemeId, string FenceIconStyle, string AppThemeId);

    public static readonly IReadOnlyList<Preset> All = new[]
    {
        new Preset("shamse",   "شمسه",          "فیروزه و لاجورد · کاشی ایرانی",          "#28D5BD", "#10244C", 16, 72, 48, "turquoise", "plate", "PersianTurquoise"),
        new Preset("tazhib",   "تذهیب",          "طلایی و سبز یشمی",                       "#EBC26D", "#13392F", 12, 74, 46, "gold",      "plate", "EmeraldGlass"),
        new Preset("lajvard",  "لاجورد شب",      "آبی لاجوردی با حاشیهٔ طلایی",            "#60A5FA", "#0B1B3F", 14, 74, 48, "lapis",     "plate", "LapisGlass"),
        new Preset("zomorrod", "زمرد",           "سبز زمردی آرام",                         "#34D399", "#062A22", 14, 72, 48, "emerald",   "plate", "EmeraldGlass"),
        new Preset("yaghout",  "یاقوت",          "سرخ یاقوتی گرم",                         "#FB7185", "#2A0B14", 12, 76, 46, "ruby",      "plate", "AmethystGlass"),
        new Preset("yas",      "یاس بنفش",       "بنفش و سرمه‌ای با آیکون‌های درشت",        "#C8A4FF", "#211831", 18, 72, 56, "amethyst",  "plate", "AmethystGlass"),
        new Preset("neon",     "نئون شب",        "سیاه عمیق با خطوط فیروزه‌ای نئونی",       "#22D3EE", "#05070F", 6,  82, 44, "midnight",  "glyph", "LapisGlass"),
        new Preset("ghorub",   "غروب کویر",      "نارنجی و کهربایی گرم",                   "#FDBA74", "#2B1408", 14, 76, 48, "sunset",    "plate", "PersianTurquoise"),
        new Preset("sakura",   "شکوفه",          "صورتی ملایم و گرد",                       "#F9A8D4", "#2A1424", 20, 70, 50, "sakura",    "plate", "AmethystGlass"),
        new Preset("ocean",    "اقیانوس",        "فیروزه‌ای دریایی",                        "#22D3EE", "#062B33", 16, 72, 48, "ocean",     "plate", "EmeraldGlass"),
        new Preset("graphite", "گرافیت مینیمال", "خاکستری خنثی، بدون حاشیهٔ رنگی",          "#E4E4E7", "#18181B", 8,  80, 44, "graphite",  "plain", "LapisGlass"),
        new Preset("game",     "بازی",           "داک جمع‌وجور با تأکید سبز",               "#9FEA69", "#14191E", 4,  84, 36, "lime",      "glyph", "EmeraldGlass"),
    };

    /// <summary>Applies a preset to the studio's working copy. System icons / app palette are applied immediately.</summary>
    public static void Apply(DesktopSettings s, Preset p, bool gatherIcons, bool systemIcons, bool appPalette)
    {
        s.Theme.AccentHex = p.Accent;
        s.Theme.GlassTintHex = p.Tint;
        s.Theme.CornerRadius = p.Radius;
        s.Theme.GlassOpacity = p.Opacity;
        s.Theme.ShowBorders = p.Id != "graphite";
        s.Dock.IconSize = p.DockIcon;
        s.Dock.Enabled = true; s.Dock.Position = "Bottom";
        s.TopBar.Enabled = true; s.TopBar.Position = "Top";
        s.Launcher.Enabled = true;
        s.HideWindowsTaskbar = true;
        s.ThemePresetId = p.Id;

        // Icon groups wear the theme too.
        s.Fences.FollowTheme = true;
        s.Fences.IconStyle = p.FenceIconStyle;
        if (gatherIcons)
        {
            bool created = false;
            foreach (var sug in Fences.FencesModule.SuggestedFences())
                if (!s.Fences.Fences.Any(f => f.Title == sug.Title || (f.Mode == "filter" && f.Extensions.Contains(Fences.FencesModule.AnyExtension) && sug.Extensions.Contains(Fences.FencesModule.AnyExtension))))
                { s.Fences.Fences.Add(sug); created = true; }
            if (created) Fences.FencesModule.Arrange(s.Fences.Fences, DesktopDisplay.RelativeWorkArea(s));
            s.Fences.Enabled = true;
            s.Fences.HideDesktopIcons = true;
        }
        // Widgets use the glass brush, so they follow automatically; reset widget colours that would clash.
        foreach (var w in s.Widgets.Items.Where(w => w.Kind != "note")) w.ColorHex = null;

        if (systemIcons && Personalize.SystemIcons.Themes.FirstOrDefault(t => t.Id == p.IconThemeId) is { } iconTheme)
        {
            try { Personalize.SystemIcons.ApplyTheme(iconTheme); }
            catch (Exception ex) { AppLog.Warn("Theme preset system icons: " + ex.Message); }
        }
        if (appPalette)
        {
            try
            {
                SettingsService.Update(a => { a.ThemeId = p.AppThemeId; a.ThemeAccentHex = p.Accent; });
                ThemeService.Apply(SettingsService.Current);
            }
            catch (Exception ex) { AppLog.Warn("Theme preset palette: " + ex.Message); }
        }
    }

    /// <summary>A visual card: glass strip with mini dock tiles in the theme's colours.</summary>
    public static FrameworkElement Swatch(Preset p, bool current)
    {
        var accent = DesktopTheme.ParseColor(p.Accent, Colors.Teal);
        var tint = DesktopTheme.ParseColor(p.Tint, Colors.Black);
        var icon = Personalize.SystemIcons.Themes.FirstOrDefault(t => t.Id == p.IconThemeId);
        var canvas = new Border
        {
            Height = 74, CornerRadius = new CornerRadius(Math.Max(6, p.Radius * 0.7)),
            Background = new LinearGradientBrush(Lighten(tint, 0.18), tint, 90),
            BorderBrush = new SolidColorBrush(current ? accent : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(current ? 2 : 1)
        };
        var grid = new Grid();
        // top bar
        grid.Children.Add(new Border { Height = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 6, 6, 0), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0xA0, accent.R, accent.G, accent.B)) });
        // a fence
        grid.Children.Add(new Border { Width = 46, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 8, 0), CornerRadius = new CornerRadius(Math.Max(3, p.Radius * 0.35)),
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) });
        // dock tiles
        var dock = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 4; i++)
        {
            var top = icon?.Top ?? accent; var bottom = icon?.Bottom ?? tint;
            dock.Children.Add(new Border { Width = 14, Height = 14, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(Math.Max(2, p.Radius * 0.25)),
                Background = new LinearGradientBrush(top, bottom, 90),
                BorderBrush = new SolidColorBrush(icon?.Border ?? accent), BorderThickness = new Thickness(1) });
        }
        grid.Children.Add(dock);
        canvas.Child = grid;
        return canvas;
    }

    private static Color Lighten(Color c, double t) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
}
