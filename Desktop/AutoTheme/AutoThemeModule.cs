using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Desktop.AutoTheme;

/// <summary>
/// Switches light/dark automatically at fixed times or at sunrise/sunset. Windows' own app/system
/// theme (HKCU Personalize) and Shamsa's glass tint are changed only while enabled; the user's
/// original values are saved first and restored when the module stops.
/// </summary>
public sealed class AutoThemeModule : IDesktopModule
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    /// <summary>Tint used for Shamsa's glass in light mode (kept mid-tone so the white text stays readable).</summary>
    public const string LightTint = "#475569";

    private static AutoThemeModule? _instance;
    private DispatcherTimer? _timer;
    private DesktopSettings? _settings;
    private bool? _applied;          // last state we applied (true = light)
    private bool? _override;         // manual toggle until the schedule changes state
    private bool? _overrideScheduleState;

    private sealed class Originals
    {
        public int? AppsUseLightTheme { get; set; }
        public int? SystemUsesLightTheme { get; set; }
        public string? GlassTintHex { get; set; }
    }

    private static string OriginalsFile => Path.Combine(AppPaths.DataDir, "autotheme-originals.json");

    public AutoThemeModule() { _instance = this; }

    public string Id => "autotheme";
    public string DisplayName => "تم خودکار";
    public bool IsRunning => _timer != null;

    public void Apply(DesktopSettings settings)
    {
        _settings = settings;
        if (!settings.AutoTheme.Enabled) { Stop(); return; }
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _timer.Tick += (_, _) => Evaluate();
            _timer.Start();
        }
        Evaluate();
    }

    public void Stop()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer = null;
        _applied = null;
        _override = null;
        RestoreOriginals();
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) => null;

    // ------------------------------------------------------------------ public helpers

    /// <summary>True when light mode is (or would be) active right now.</summary>
    public static bool IsLightNow(DesktopSettings s)
    {
        if (_instance?._applied is bool applied) return applied;
        return ReadWindowsLight() ?? (s.AutoTheme.Enabled && ScheduleSaysLight(s, DateTime.Now));
    }

    /// <summary>Quick switch from the control center: flips light/dark now.</summary>
    public static void Toggle()
    {
        var s = DesktopSettingsStore.Current;
        bool target = !IsLightNow(s);
        if (_instance is { IsRunning: true } m)
        {
            m._override = target;
            m._overrideScheduleState = ScheduleSaysLight(s, DateTime.Now);
            m.Evaluate();
        }
        else
        {
            // Not scheduled: a plain one-off switch of the Windows theme (nothing to restore later).
            WriteWindowsLight(target);
        }
    }

    /// <summary>Light/dark according to the schedule only.</summary>
    public static bool ScheduleSaysLight(DesktopSettings s, DateTime now)
    {
        var a = s.AutoTheme;
        if (string.Equals(a.Mode, "Sun", StringComparison.OrdinalIgnoreCase))
        {
            var sun = SunCalculator.Compute(now.Date, s.Widgets.Latitude, s.Widgets.Longitude);
            if (sun is { } v) return now >= v.Sunrise && now < v.Sunset;
            // Polar day/night: fall back to the fixed times.
        }
        var light = ParseTime(a.LightAt, new TimeSpan(7, 0, 0));
        var dark = ParseTime(a.DarkAt, new TimeSpan(19, 0, 0));
        var t = now.TimeOfDay;
        return light <= dark ? t >= light && t < dark : t >= light || t < dark;
    }

    /// <summary>Next switching moment (for the studio's status line).</summary>
    public static (DateTime At, bool ToLight)? NextChange(DesktopSettings s, DateTime now)
    {
        bool current = ScheduleSaysLight(s, now);
        for (int minutes = 1; minutes <= 48 * 60; minutes += 1)
        {
            var t = now.AddMinutes(minutes);
            if (ScheduleSaysLight(s, t) != current) return (new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0), !current);
        }
        return null;
    }

    public static TimeSpan ParseTime(string? text, TimeSpan fallback) =>
        TimeSpan.TryParse(DesktopTextDigits(text ?? ""), out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1) ? ts : fallback;

    private static string DesktopTextDigits(string s)
    {
        var chars = s.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '۰' and <= '۹') chars[i] = (char)('0' + (chars[i] - '۰'));
            else if (chars[i] is >= '٠' and <= '٩') chars[i] = (char)('0' + (chars[i] - '٠'));
        }
        return new string(chars);
    }

    // ------------------------------------------------------------------ core

    private void Evaluate()
    {
        try
        {
            var s = _settings ?? DesktopSettingsStore.Current;
            bool schedule = ScheduleSaysLight(s, DateTime.Now);
            if (_override.HasValue && _overrideScheduleState != schedule) _override = null; // schedule moved on
            bool light = _override ?? schedule;
            if (_applied == light) return;
            SaveOriginalsOnce(s);
            if (s.AutoTheme.ApplyToWindows) WriteWindowsLight(light);
            if (s.AutoTheme.ApplyToShamsa) ApplyShamsaTint(light);
            _applied = light;
            AppLog.Info("Auto theme → " + (light ? "light" : "dark"));
        }
        catch (Exception ex) { AppLog.Error("Auto theme evaluation failed", ex); }
    }

    private static void ApplyShamsaTint(bool light)
    {
        var current = DesktopSettingsStore.Current;
        string target;
        if (light) target = LightTint;
        else
        {
            var o = LoadOriginals();
            target = o?.GlassTintHex ?? current.Theme.GlassTintHex;
        }
        if (string.Equals(current.Theme.GlassTintHex, target, StringComparison.OrdinalIgnoreCase)) return;
        var s = current.Clone();
        s.Theme.GlassTintHex = target;
        DesktopSettingsStore.Save(s);
    }

    private static void SaveOriginalsOnce(DesktopSettings s)
    {
        try
        {
            if (File.Exists(OriginalsFile)) return; // keep the very first values (also survives a crash)
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var o = new Originals
            {
                AppsUseLightTheme = k?.GetValue("AppsUseLightTheme") as int?,
                SystemUsesLightTheme = k?.GetValue("SystemUsesLightTheme") as int?,
                GlassTintHex = string.Equals(s.Theme.GlassTintHex, LightTint, StringComparison.OrdinalIgnoreCase) ? null : s.Theme.GlassTintHex
            };
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(OriginalsFile, JsonSerializer.Serialize(o));
        }
        catch (Exception ex) { AppLog.Warn("Auto theme originals: " + ex.Message); }
    }

    private static Originals? LoadOriginals()
    {
        try { return File.Exists(OriginalsFile) ? JsonSerializer.Deserialize<Originals>(File.ReadAllText(OriginalsFile)) : null; }
        catch { return null; }
    }

    private static void RestoreOriginals()
    {
        var o = LoadOriginals();
        if (o == null) return;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
            {
                if (o.AppsUseLightTheme is int a) k.SetValue("AppsUseLightTheme", a, RegistryValueKind.DWord);
                if (o.SystemUsesLightTheme is int b) k.SetValue("SystemUsesLightTheme", b, RegistryValueKind.DWord);
            }
            BroadcastThemeChange();
            if (o.GlassTintHex != null)
            {
                var cur = DesktopSettingsStore.Current;
                if (!string.Equals(cur.Theme.GlassTintHex, o.GlassTintHex, StringComparison.OrdinalIgnoreCase))
                {
                    var s = cur.Clone();
                    s.Theme.GlassTintHex = o.GlassTintHex;
                    DesktopSettingsStore.Save(s);
                }
            }
            File.Delete(OriginalsFile);
            AppLog.Info("Auto theme: original theme restored");
        }
        catch (Exception ex) { AppLog.Warn("Auto theme restore: " + ex.Message); }
    }

    private static bool? ReadWindowsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return k?.GetValue("AppsUseLightTheme") is int v ? v != 0 : null;
        }
        catch { return null; }
    }

    private static void WriteWindowsLight(bool light)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            k.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            BroadcastThemeChange();
        }
        catch (Exception ex) { AppLog.Warn("Auto theme registry: " + ex.Message); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    private static void BroadcastThemeChange()
    {
        try { SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "ImmersiveColorSet", 0x0002 /*SMTO_ABORTIFHUNG*/, 200, out _); }
        catch { }
    }
}
