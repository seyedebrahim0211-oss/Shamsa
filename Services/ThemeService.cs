using System.Windows;
using System.Windows.Media;

namespace Shamsa.Services;

/// <summary>
/// Runtime WPF palette. DynamicResource references update already-open windows
/// without restarting the app, while settings remain under LocalAppData.
/// No compositor hooks or high-cost blur passes are used.
/// </summary>
public static class ThemeService
{
    public static readonly string[] Themes = { "PersianTurquoise", "LapisGlass", "EmeraldGlass", "AmethystGlass" };

    public static bool IsKnownTheme(string? id) => Array.IndexOf(Themes, id) >= 0;

    public static bool TryParseAccent(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string hex = text.Trim();
        if (hex.Length != 7 || hex[0] != '#' || !hex.Skip(1).All(Uri.IsHexDigit)) return false;
        color = Color.FromRgb(Convert.ToByte(hex.Substring(1, 2), 16),
                              Convert.ToByte(hex.Substring(3, 2), 16),
                              Convert.ToByte(hex.Substring(5, 2), 16));
        return true;
    }

    private readonly record struct Palette(Color Bg, Color Surface, Color Surface2, Color Border,
                                           Color Accent, Color Gold, Color Text, Color Muted);

    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value)!;

    private static Palette GetPalette(string themeId) => themeId switch
    {
        "LapisGlass" => new(Hex("#080F24"), Hex("#122145"), Hex("#213763"), Hex("#39588D"),
                            Hex("#70A5FF"), Hex("#F3D99B"), Hex("#F6F8FF"), Hex("#BDD0EF")),
        "EmeraldGlass" => new(Hex("#081A19"), Hex("#12312E"), Hex("#1B4841"), Hex("#326C61"),
                              Hex("#39D8B5"), Hex("#F7D894"), Hex("#F2FFFA"), Hex("#BDDDD4")),
        "AmethystGlass" => new(Hex("#140D23"), Hex("#2B1946"), Hex("#43275F"), Hex("#68458B"),
                               Hex("#C69AFF"), Hex("#FFCFA3"), Hex("#FFF8FF"), Hex("#D9C7EB")),
        // «نگارخانه»: lapis night, Neyshabur turquoise and illumination gold.
        _ => new(Hex("#0A1430"), Hex("#101D3D"), Hex("#182B55"), Hex("#2A4272"),
                 Hex("#19C3B1"), Hex("#E9C46A"), Hex("#F5F7FB"), Hex("#AFC0DC"))
    };

    private static Color Alpha(Color color, byte a) => Color.FromArgb(a, color.R, color.G, color.B);
    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private static LinearGradientBrush Gradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    public static void Apply(AppSettings settings)
    {
        if (Application.Current is not { } app) return;
        var palette = GetPalette(settings.ThemeId);
        Color accent = TryParseAccent(settings.ThemeAccentHex, out var custom) ? custom : palette.Accent;
        int percent = Math.Clamp(settings.GlassOpacityPercent, 45, 97);
        byte opacity = (byte)Math.Round(percent * 255.0 / 100.0);
        Color surface = palette.Surface;
        Color bright = palette.Surface2;
        Color gold = palette.Gold;

        var resources = app.Resources;
        resources["BgBrush"] = Brush(palette.Bg);
        resources["SurfaceBrush"] = Brush(surface);
        resources["Surface2Brush"] = Brush(bright);
        resources["BorderBrush"] = Brush(palette.Border);
        resources["AccentBrush"] = Brush(accent);
        resources["AccentDarkBrush"] = Brush(Color.Multiply(accent, 0.72f));
        resources["GoldBrush"] = Brush(gold);
        resources["TextBrush"] = Brush(palette.Text);
        resources["MutedBrush"] = Brush(palette.Muted);
        resources["InputBrush"] = Brush(palette.Bg);
        resources["PopupBrush"] = Brush(surface);
        resources["GlassCardBrush"] = settings.GlassEffectsEnabled
            ? Gradient(Alpha(bright, (byte)Math.Min(255, opacity + 16)), Alpha(surface, opacity))
            : Brush(surface);
        resources["GlassHighlightBrush"] = settings.GlassEffectsEnabled
            ? Gradient(Alpha(bright, 245), Alpha(accent, (byte)Math.Max(45, opacity / 3)))
            : Brush(bright);
        resources["GlassHeaderBrush"] = settings.GlassEffectsEnabled
            ? Gradient(Alpha(bright, 235), Alpha(palette.Bg, 245))
            : Brush(surface);
        resources["ShelfGlassBrush"] = settings.GlassEffectsEnabled
            ? Gradient(Alpha(surface, (byte)Math.Max(185, (int)opacity)), Alpha(palette.Bg, 245))
            : Brush(palette.Bg);
        resources["PersianDividerBrush"] = new LinearGradientBrush(new GradientStopCollection
        {
            new(Alpha(gold, 0), 0.0), new(Alpha(accent, 170), 0.25),
            new(Alpha(gold, 230), 0.5), new(Alpha(accent, 170), 0.75), new(Alpha(gold, 0), 1.0)
        }, new Point(0, 0.5), new Point(1, 0.5));
        resources["PersianBadgeBrush"] = Gradient(accent, palette.Surface2);
        // Hub chrome (sidebar, navigation states, soft tints) follows the palette too.
        resources["SidebarBrush"] = Brush(Color.Multiply(palette.Bg, 0.8f) with { A = 255 });
        resources["NavHoverBrush"] = Brush(Alpha(palette.Text, 20));
        resources["NavSelectedBrush"] = Brush(Alpha(accent, 48));
        resources["AccentSoftBrush"] = Brush(Alpha(accent, 34));
        resources["GoldSoftBrush"] = Brush(Alpha(gold, 38));
    }
}
