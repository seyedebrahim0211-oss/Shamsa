using System.Windows;
using System.Windows.Media;

namespace Shamsa.Desktop;

/// <summary>Brushes and helpers derived from <see cref="DesktopThemeSettings"/>, shared by every desktop module.</summary>
public static class DesktopTheme
{
    public static Color ParseColor(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return fallback; }
    }

    public static Color Accent(DesktopSettings s) => ParseColor(s.Theme.AccentHex, Color.FromRgb(0x14, 0xB8, 0xA6));

    public static SolidColorBrush AccentBrush(DesktopSettings s) => Frozen(new SolidColorBrush(Accent(s)));

    /// <summary>Background of bars/dock: tint colour at the configured opacity.</summary>
    public static SolidColorBrush GlassBrush(DesktopSettings s, double extraOpacity = 1.0)
    {
        var c = ParseColor(s.Theme.GlassTintHex, Color.FromRgb(0x0B, 0x13, 0x24));
        c.A = (byte)Math.Clamp(s.Theme.GlassOpacity / 100.0 * 255 * extraOpacity, 0, 255);
        return Frozen(new SolidColorBrush(c));
    }

    public static SolidColorBrush BorderBrush(DesktopSettings s) =>
        Frozen(new SolidColorBrush(s.Theme.ShowBorders ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Colors.Transparent));

    public static SolidColorBrush TextBrush { get; } = Frozen(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)));
    public static SolidColorBrush MutedBrush { get; } = Frozen(new SolidColorBrush(Color.FromRgb(0xB6, 0xC6, 0xDF)));
    public static SolidColorBrush HoverBrush { get; } = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    public static CornerRadius Radius(DesktopSettings s) => new(s.Theme.CornerRadius);

    /// <summary>The UI font (embedded Vazirmatn unless the user picked another one).</summary>
    public static FontFamily Font =>
        Application.Current?.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");

    private static SolidColorBrush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>Western → Persian digits (۰-۹) when enabled.</summary>
    public static string Digits(string text, bool persian)
    {
        if (!persian) return text;
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= '0' and <= '9') chars[i] = (char)('۰' + (chars[i] - '0'));
        return new string(chars);
    }
}
