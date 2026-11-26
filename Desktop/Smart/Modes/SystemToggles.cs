using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shamsa.Services;
using Windows.Media.Control;

namespace Shamsa.Desktop.Smart.Modes;

/// <summary>
/// The small, reversible system switches a smart mode uses. Every registry value is HKCU only and its
/// previous value is recorded in <see cref="ActiveModeState.Originals"/> before the first write.
/// </summary>
public static class SystemToggles
{
    public const string NotificationsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    public const string NotificationsValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";
    public const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Tests set this (e.g. "Software\NegarkhanehSmartTest") so every write lands under a throw-away key
    /// and no system broadcast is sent.
    /// </summary>
    public static string? TestRoot { get; set; }
    private static bool Testing => !string.IsNullOrEmpty(TestRoot);
    private static string Map(string key) => Testing ? TestRoot + "\\" + key : key;

    public static int? ReadDword(string key, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Map(key));
            return k?.GetValue(name) is int i ? i : null;
        }
        catch { return null; }
    }

    /// <summary>Writes a DWORD, remembering the original once per mode activation.</summary>
    public static void WriteDword(ActiveModeState state, string key, string name, int value)
    {
        if (!state.Originals.Any(o => o.Key == key && o.Name == name))
            state.Originals.Add(new SavedValue { Key = key, Name = name, Value = ReadDword(key, name) });
        using var k = Registry.CurrentUser.CreateSubKey(Map(key), true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>Puts every recorded value back (deleting values that did not exist). Safe to call twice.</summary>
    public static void RestoreAll(ActiveModeState state)
    {
        bool themeTouched = false, notifTouched = false;
        foreach (var o in state.Originals.AsEnumerable().Reverse())
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Map(o.Key), true);
                if (o.Value is int v) k.SetValue(o.Name, v, RegistryValueKind.DWord);
                else k.DeleteValue(o.Name, false);
                themeTouched |= o.Key == ThemeKey;
                notifTouched |= o.Key == NotificationsKey;
            }
            catch (Exception ex) { AppLog.Warn($"Smart mode restore {o.Name}: {ex.Message}"); }
        }
        state.Originals.Clear();
        if (themeTouched) BroadcastThemeChange();
        if (notifTouched) BroadcastSettingChange(NotificationsKey);
    }

    public static void SetNotificationsQuiet(ActiveModeState state)
    {
        WriteDword(state, NotificationsKey, NotificationsValue, 0);
        BroadcastSettingChange(NotificationsKey);
    }

    public static void SetTheme(ActiveModeState state, bool dark)
    {
        WriteDword(state, ThemeKey, "AppsUseLightTheme", dark ? 0 : 1);
        WriteDword(state, ThemeKey, "SystemUsesLightTheme", dark ? 0 : 1);
        BroadcastThemeChange();
    }

    // ------------------------------------------------------------------ broadcasts

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    private static void BroadcastSettingChange(string area)
    {
        if (Testing) return;
        try { SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 300, out _); } catch { }
    }

    private static void BroadcastThemeChange() => BroadcastSettingChange("ImmersiveColorSet");

    // ------------------------------------------------------------------ media

    /// <summary>Pauses every media session that is playing right now (never starts anything). Returns how many paused.</summary>
    public static async Task<int> PausePlayingMediaAsync()
    {
        if (Testing) return 0;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            int paused = 0;
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    var info = session.GetPlaybackInfo();
                    if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                        await session.TryPauseAsync()) paused++;
                }
                catch { }
            }
            return paused;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Smart mode media pause: " + ex.Message);
            return 0;
        }
    }

    // ------------------------------------------------------------------ foreground / input

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Process name (no .exe) of the window in front, or null.</summary>
    public static string? ForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>Time since the last keyboard/mouse input.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Names of running processes (lower case, no .exe).</summary>
    public static HashSet<string> RunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.ProcessName); } catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    public static string CleanProcessName(string s)
    {
        s = s.Trim().Trim('"');
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return Path.GetFileName(s);
    }
}
