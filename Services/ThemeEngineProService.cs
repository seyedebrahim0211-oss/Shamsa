#nullable enable
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shamsa.Services;

/// <summary>
/// Extended Theme Engine for Shamsa Pro (Fluent + Glassmorphism + Persian Minimalism).
/// Supports 8 presets, automatic day/night switching by clock hour, RTL & Vazirmatn font,
/// and dynamic accent extraction from active wallpaper.
/// </summary>
public static class ThemeEngineProService
{
    private static DispatcherTimer? _autoScheduleTimer;

    private readonly record struct ThemePalette(
        string Bg,
        string Surface,
        string Surface2,
        string Border,
        string Accent,
        string AccentDark,
        string Gold,
        string Text,
        string Muted,
        string Input,
        string Popup);

    public static void StartAutoThemeScheduler()
    {
        _autoScheduleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _autoScheduleTimer.Tick -= OnScheduleTick;
        _autoScheduleTimer.Tick += OnScheduleTick;
        _autoScheduleTimer.Start();
    }

    public static void StopAutoThemeScheduler()
    {
        if (_autoScheduleTimer != null)
        {
            _autoScheduleTimer.Stop();
            _autoScheduleTimer.Tick -= OnScheduleTick;
        }
    }

    private static void OnScheduleTick(object? sender, EventArgs e)
    {
        if (ShamsaProSettingsService.Current.ThemePreset == ShamsaThemePreset.AutoByTime)
            Apply(ShamsaProSettingsService.Current);
    }

    public static ShamsaThemePreset ResolveEffectivePreset(ShamsaProSettings settings)
    {
        if (settings.ThemePreset != ShamsaThemePreset.AutoByTime)
            return settings.ThemePreset;

        int hour = DateTime.Now.Hour;
        bool isDay = hour >= settings.AutoLightStartHour && hour < settings.AutoDarkStartHour;
        return isDay ? ShamsaThemePreset.Light : ShamsaThemePreset.Dark;
    }

    public static void Apply(ShamsaProSettings settings, Color? dominantWallpaperAccent = null)
    {
        if (Application.Current == null) return;

        var effective = ResolveEffectivePreset(settings);
        var palette = GetPalette(effective);
        var res = Application.Current.Resources;

        Color accent = dominantWallpaperAccent ?? ParseColor(settings.CustomAccentHex, ParseColor(palette.Accent, Colors.Teal));
        Color bg = ParseColor(palette.Bg, Color.FromRgb(15, 23, 42));
        Color surface = ParseColor(palette.Surface, Color.FromRgb(22, 32, 54));
        Color surface2 = ParseColor(palette.Surface2, Color.FromRgb(30, 41, 59));
        Color border = ParseColor(palette.Border, Color.FromRgb(51, 65, 85));
        Color text = ParseColor(palette.Text, Color.FromRgb(241, 245, 249));
        Color muted = ParseColor(palette.Muted, Color.FromRgb(148, 163, 184));
        Color gold = ParseColor(palette.Gold, Color.FromRgb(212, 175, 55));

        byte glassAlpha = (byte)Math.Clamp((int)(settings.GlobalGlassOpacity * 255), 90, 250);

        SetBrush(res, "BgBrush", bg);
        SetBrush(res, "SurfaceBrush", surface);
        SetBrush(res, "Surface2Brush", surface2);
        SetBrush(res, "BorderBrush", border);
        SetBrush(res, "AccentBrush", accent);
        SetBrush(res, "AccentDarkBrush", Darken(accent, 0.22));
        SetBrush(res, "GoldBrush", gold);
        SetBrush(res, "TextBrush", text);
        SetBrush(res, "MutedBrush", muted);
        SetBrush(res, "InputBrush", ParseColor(palette.Input, surface2));
        SetBrush(res, "PopupBrush", ParseColor(palette.Popup, surface));
        SetBrush(res, "GlassCardBrush", Color.FromArgb(glassAlpha, surface.R, surface.G, surface.B));
        SetBrush(res, "GlassHighlightBrush", Color.FromArgb(42, 255, 255, 255));
        SetBrush(res, "GlassHeaderBrush", Color.FromArgb((byte)Math.Min(255, glassAlpha + 16), bg.R, bg.G, bg.B));
        SetBrush(res, "ShelfGlassBrush", Color.FromArgb(glassAlpha, bg.R, bg.G, bg.B));
        SetBrush(res, "PersianDividerBrush", Color.FromArgb(110, gold.R, gold.G, gold.B));
        SetBrush(res, "PersianBadgeBrush", Color.FromArgb(50, accent.R, accent.G, accent.B));

        res["ShamsaCornerRadius"] = new CornerRadius(settings.GlobalCornerRadius);
        res["ShamsaFontFamily"] = new FontFamily($"{settings.FontFamily}, Vazirmatn, Segoe UI Variable, Tahoma");
        res["ShamsaFlowDirection"] = settings.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private static ThemePalette GetPalette(ShamsaThemePreset preset) => preset switch
    {
        ShamsaThemePreset.Light => new("#F4F7FA", "#FFFFFF", "#EEF2F6", "#CBD5E1", "#0D9488", "#0F766E", "#B45309", "#0F172A", "#475569", "#FFFFFF", "#FFFFFF"),
        ShamsaThemePreset.OledBlack => new("#000000", "#0A0A0C", "#121216", "#23232A", "#14B8A6", "#0D9488", "#EAB308", "#F8FAFC", "#94A3B8", "#101014", "#0C0C10"),
        ShamsaThemePreset.PersianTraditional => new("#0B162C", "#132242", "#1C2F57", "#2E4775", "#16B3AC", "#0E857F", "#D9A441", "#F5F2EB", "#A9BCD0", "#162649", "#101D38"),
        ShamsaThemePreset.Minimal => new("#18181B", "#222226", "#2C2C32", "#3F3F46", "#A1A1AA", "#71717A", "#D4D4D8", "#FAFAFA", "#A1A1AA", "#27272A", "#1F1F23"),
        ShamsaThemePreset.Office => new("#F3F4F6", "#FFFFFF", "#E5E7EB", "#D1D5DB", "#2563EB", "#1D4ED8", "#D97706", "#111827", "#6B7280", "#FFFFFF", "#FFFFFF"),
        ShamsaThemePreset.Gaming => new("#090814", "#121026", "#1B1738", "#312961", "#06B6D4", "#0891B2", "#F43F5E", "#F8FAFC", "#94A3B8", "#16132E", "#100E22"),
        ShamsaThemePreset.Dark => new("#0F172A", "#1E293B", "#283548", "#334155", "#38BDF8", "#0284C7", "#F59E0B", "#F8FAFC", "#94A3B8", "#1E293B", "#162032"),
        _ => new("#0B1922", "#112533", "#183345", "#264B63", "#14B8A6", "#0D9488", "#D4AF37", "#F1F5F9", "#94A3B8", "#132938", "#0F222E")
    };

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        res[key] = brush;
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return fallback;
        }
    }

    private static Color Darken(Color c, double factor) => Color.FromArgb(
        c.A,
        (byte)(c.R * (1 - factor)),
        (byte)(c.G * (1 - factor)),
        (byte)(c.B * (1 - factor)));
}
