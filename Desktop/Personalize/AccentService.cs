using System.Windows.Media;
using Microsoft.Win32;

namespace Shamsa.Desktop.Personalize;

/// <summary>Windows accent color and transparency (per-user keys, then an "ImmersiveColorSet" broadcast).</summary>
public static class AccentService
{
    public const string Group = "accent";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string DesktopKey = @"Control Panel\Desktop";

    public static readonly (string Name, string Hex)[] Presets =
    {
        ("فیروزه‌ای", "#14B8A6"), ("لاجوردی", "#2563EB"), ("طلایی", "#D4A017"), ("اناری", "#DC2626"),
        ("زمردی", "#059669"), ("بنفش", "#7C3AED"), ("سرمه‌ای", "#1E3A8A"), ("نارنجی", "#EA580C"),
        ("صورتی", "#DB2777"), ("زغالی", "#374151"),
    };

    public static int ToAbgr(Color c) => unchecked((int)(0xFF000000u | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R));
    public static int ToArgb(Color c, byte alpha = 0xC4) => unchecked((int)(((uint)alpha << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));

    public static Color FromAbgr(int v)
    {
        uint u = unchecked((uint)v);
        return Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
    }

    public static Color? CurrentAccent()
    {
        object? v = PersonalizeStore.Read(DwmKey, "AccentColor");
        return v is int i ? FromAbgr(i) : null;
    }

    public static bool TransparencyEnabled() => PersonalizeStore.Read(PersonalizeKey, "EnableTransparency") is int i ? i != 0 : true;
    public static bool AccentOnTaskbar() => PersonalizeStore.Read(PersonalizeKey, "ColorPrevalence") is int i && i != 0;
    public static bool AccentOnTitleBars() => PersonalizeStore.Read(DwmKey, "ColorPrevalence") is int i && i != 0;

    /// <summary>Keeps a color usable as an accent (not too dark, not too pale).</summary>
    public static Color Normalize(Color c)
    {
        RgbToHsl(c, out double h, out double s, out double l);
        s = Math.Max(s, 0.35);
        l = Math.Clamp(l, 0.32, 0.58);
        return HslToRgb(h, s, l);
    }

    /// <summary>8 shades from light to dark (the AccentPalette layout).</summary>
    public static Color[] Palette(Color c)
    {
        RgbToHsl(c, out double h, out double s, out double l);
        double[] ls = { Math.Min(0.92, l + 0.36), Math.Min(0.85, l + 0.24), Math.Min(0.78, l + 0.12), l,
                        Math.Max(0.08, l - 0.10), Math.Max(0.06, l - 0.20), Math.Max(0.04, l - 0.30), Math.Max(0.10, l - 0.15) };
        return ls.Select((x, i) => i == 3 ? c : HslToRgb(h, s, x)).ToArray();
    }

    public static void SetAccent(Color c)
    {
        var shades = Palette(c);
        var palette = new byte[32];
        for (int i = 0; i < 8; i++) { palette[i * 4] = shades[i].R; palette[i * 4 + 1] = shades[i].G; palette[i * 4 + 2] = shades[i].B; palette[i * 4 + 3] = 0xFF; }
        // Turn off "automatically pick from background" so the choice sticks.
        PersonalizeStore.Write(Group, DesktopKey, "AutoColorization", 0, RegistryValueKind.DWord);
        PersonalizeStore.Write(Group, AccentKey, "AccentPalette", palette, RegistryValueKind.Binary);
        PersonalizeStore.Write(Group, AccentKey, "AccentColorMenu", ToAbgr(shades[4]), RegistryValueKind.DWord);
        PersonalizeStore.Write(Group, AccentKey, "StartColorMenu", ToAbgr(shades[5]), RegistryValueKind.DWord);
        PersonalizeStore.Write(Group, DwmKey, "AccentColor", ToAbgr(c), RegistryValueKind.DWord);
        PersonalizeStore.Write(Group, DwmKey, "ColorizationColor", ToArgb(c), RegistryValueKind.DWord);
        PersonalizeStore.Write(Group, DwmKey, "ColorizationAfterglow", ToArgb(c), RegistryValueKind.DWord);
        PersonalizeStore.Data.Applied.AccentHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        PersonalizeStore.Save();
        Broadcast();
    }

    public static void SetTransparency(bool on)
    {
        PersonalizeStore.Write(Group, PersonalizeKey, "EnableTransparency", on ? 1 : 0, RegistryValueKind.DWord);
        PersonalizeStore.Data.Applied.Transparency = on; PersonalizeStore.Save();
        Broadcast();
    }

    public static void SetAccentOnTaskbar(bool on)
    {
        PersonalizeStore.Write(Group, PersonalizeKey, "ColorPrevalence", on ? 1 : 0, RegistryValueKind.DWord);
        PersonalizeStore.Data.Applied.AccentOnTaskbar = on; PersonalizeStore.Save();
        Broadcast();
    }

    public static void SetAccentOnTitleBars(bool on)
    {
        PersonalizeStore.Write(Group, DwmKey, "ColorPrevalence", on ? 1 : 0, RegistryValueKind.DWord);
        PersonalizeStore.Data.Applied.AccentOnTitleBars = on; PersonalizeStore.Save();
        Broadcast();
    }

    public static void Reset()
    {
        PersonalizeStore.Restore(Group);
        var a = PersonalizeStore.Data.Applied;
        a.AccentHex = null; a.Transparency = null; a.AccentOnTaskbar = null; a.AccentOnTitleBars = null;
        PersonalizeStore.Save();
        Broadcast();
    }

    private static void Broadcast()
    {
        PersonalizeNative.BroadcastSettingChange("ImmersiveColorSet");
        PersonalizeNative.BroadcastSettingChange("WindowsThemeElement");
    }

    // ------------------------------------------------------------------ color math

    public static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min) { h = 0; s = 0; return; }
        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
        else if (max == g) h = ((b - r) / d + 2) * 60;
        else h = ((r - g) / d + 4) * 60;
    }

    public static Color HslToRgb(double h, double s, double l)
    {
        double C = (1 - Math.Abs(2 * l - 1)) * s, X = C * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - C / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (C, X, 0d), < 120 => (X, C, 0d), < 180 => (0d, C, X),
            < 240 => (0d, X, C), < 300 => (X, 0d, C), _ => (C, 0d, X)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>Desktop icon size (Shell Bags, needs Explorer restart) and icon spacing (WindowMetrics, needs sign-out).</summary>
public static class DesktopLayoutService
{
    public const string GroupSize = "iconsize";
    public const string GroupSpacing = "spacing";
    private const string BagKey = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    private const string MetricsKey = @"Control Panel\Desktop\WindowMetrics";

    public static readonly (string Name, int Size)[] Sizes = { ("کوچک", 32), ("متوسط", 48), ("بزرگ", 96) };

    public static int CurrentIconSize() => PersonalizeStore.Read(BagKey, "IconSize") is int i && i > 0 ? i : 48;

    /// <summary>Spacing in pixels (stored as negative twips: -15 × px).</summary>
    public static int CurrentSpacing() => TwipsToPx(PersonalizeStore.Read(MetricsKey, "IconSpacing") as string, 75);
    public static int CurrentVerticalSpacing() => TwipsToPx(PersonalizeStore.Read(MetricsKey, "IconVerticalSpacing") as string, 75);

    private static int TwipsToPx(string? v, int fallback) =>
        int.TryParse(v, out int t) && t < 0 ? -t / 15 : fallback;

    public static void SetIconSize(int size)
    {
        PersonalizeStore.Write(GroupSize, BagKey, "IconSize", Math.Clamp(size, 16, 256), RegistryValueKind.DWord);
        PersonalizeStore.Data.Applied.DesktopIconSize = size; PersonalizeStore.Save();
    }

    public static void SetSpacing(int horizontalPx, int verticalPx)
    {
        PersonalizeStore.Write(GroupSpacing, MetricsKey, "IconSpacing", (-15 * Math.Clamp(horizontalPx, 32, 182)).ToString(), RegistryValueKind.String);
        PersonalizeStore.Write(GroupSpacing, MetricsKey, "IconVerticalSpacing", (-15 * Math.Clamp(verticalPx, 32, 182)).ToString(), RegistryValueKind.String);
        PersonalizeStore.Data.Applied.IconSpacing = horizontalPx; PersonalizeStore.Save();
    }

    public static void ResetSize() { PersonalizeStore.Restore(GroupSize); PersonalizeStore.Data.Applied.DesktopIconSize = null; PersonalizeStore.Save(); }
    public static void ResetSpacing() { PersonalizeStore.Restore(GroupSpacing); PersonalizeStore.Data.Applied.IconSpacing = null; PersonalizeStore.Save(); }
}
