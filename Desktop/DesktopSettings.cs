using System.Text.Json;
using System.Text.Json.Serialization;
using Shamsa.Services;

namespace Shamsa.Desktop;

/// <summary>Shared look of all desktop modules (top bar, dock, launcher, studio).</summary>
public sealed class DesktopThemeSettings
{
    public string AccentHex { get; set; } = "#14B8A6";
    public string GlassTintHex { get; set; } = "#0B1324";
    public int GlassOpacity { get; set; } = 72;          // 20..100 (%)
    public int CornerRadius { get; set; } = 12;          // 0..24
    public bool UseAcrylic { get; set; } = true;         // DWM blur behind when available
    public bool ShowBorders { get; set; } = true;
}

public sealed class TopBarSettings
{
    public bool ShowNotifications { get; set; } = true;
    public bool Islands { get; set; }
    public bool Enabled { get; set; } = true;
    public string Position { get; set; } = "Top";        // Top | Bottom
    public int Height { get; set; } = 34;                // 26..56 (DIP)
    public bool ReserveSpace { get; set; } = true;       // AppBar: maximised windows stop below it
    public bool AllMonitors { get; set; } = false;
    public bool AutoHideOnFullscreen { get; set; } = true;
    public bool ShowWorkspaces { get; set; } = true;
    public bool ShowFocusedTitle { get; set; } = true;
    public bool ShowMedia { get; set; } = true;
    public bool ShowSystemStats { get; set; } = true;    // CPU / RAM
    public bool ShowNetwork { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowPersianDate { get; set; } = true;
    public bool ShowGregorianDate { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    public bool Use24Hour { get; set; } = true;
    public bool PersianDigits { get; set; } = true;
    /// <summary>Order of items, left→right in LTR terms; unknown ids are ignored.</summary>
    public List<string> LeftItems { get; set; } = new() { "launcher", "workspaces", "title" };
    public List<string> CenterItems { get; set; } = new() { "clock" };
    public List<string> RightItems { get; set; } = new() { "media", "stats", "network", "volume", "battery", "tray" };
}

public sealed class DockSettings
{
    public bool Enabled { get; set; } = true;
    public string Position { get; set; } = "Bottom";     // Bottom | Left | Right | Top
    public int IconSize { get; set; } = 48;              // 32..80
    public int Magnification { get; set; } = 150;        // 100 (off)..200 (%)
    public bool AutoHide { get; set; } = false;
    public bool HideOnFullscreen { get; set; } = true;
    public bool ShowRunningIndicators { get; set; } = true;
    public bool ShowWindowCount { get; set; } = true;
    public bool ShowLabelsOnHover { get; set; } = true;
    public bool GroupWindowsByApp { get; set; } = true;
    public bool ShowTrashAndFolders { get; set; } = true;
    public bool ReserveSpace { get; set; } = false;
    /// <summary>Pinned items: executables, .lnk shortcuts, folders or shell URIs.</summary>
    public List<string> Pinned { get; set; } = new();
}

public sealed class LauncherSettings
{
    public bool Enabled { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
    public int HotkeyKey { get; set; } = 0x20;            // Space → Ctrl+Alt+Space
    public bool SearchApps { get; set; } = true;
    public bool SearchSettings { get; set; } = true;
    public bool SearchFiles { get; set; } = true;         // recent files + Desktop/Documents/Downloads (shallow)
    public bool Calculator { get; set; } = true;
    public bool WebSearch { get; set; } = true;
    public string WebSearchUrl { get; set; } = "https://www.google.com/search?q={0}";
    public int MaxResults { get; set; } = 8;
    public List<string> History { get; set; } = new();
}

public sealed class WindowManagerSettings
{
    public bool Enabled { get; set; } = false;
    public bool AutoTile { get; set; } = false;           // tile new windows automatically
    public string Layout { get; set; } = "Bsp";           // Bsp | Columns | Rows | Monocle | MainStack
    public int Gap { get; set; } = 8;                     // 0..40 px
    public int OuterPadding { get; set; } = 8;            // 0..60 px
    public bool HotkeysEnabled { get; set; } = true;      // Ctrl+Alt+Arrows etc.
    public Dictionary<string, WindowHotkey> Hotkeys { get; set; } = new();
    public bool FocusFollowsMouse { get; set; } = false;
    public bool FloatDialogs { get; set; } = true;
    public List<string> ExcludedProcesses { get; set; } = new() { "Taskmgr.exe", "ScreenClippingHost.exe" };
}

public sealed record WindowHotkey(uint Modifiers, int Key);

public sealed class ControlCenterSettings
{
    public bool Enabled { get; set; } = true;
    public bool ShowWifi { get; set; } = true;
    public bool ShowBluetooth { get; set; } = true;
    public bool ShowBrightness { get; set; } = true;
    public bool ShowNightLight { get; set; } = true;
    public bool ShowFocus { get; set; } = true;          // Do-not-disturb / Focus
    public bool ShowVolume { get; set; } = true;
    public bool ShowMedia { get; set; } = true;
    public bool ShowAutoTheme { get; set; } = true;
}

/// <summary>Per-application window rule (matched by exe file name, optional title substring).</summary>
public sealed class AppRule
{
    public string Process { get; set; } = "";           // e.g. "notepad.exe"
    public string? TitleContains { get; set; }
    public bool Float { get; set; }                      // window manager never tiles it
    public bool AlwaysOnTop { get; set; }
    public bool StartMaximized { get; set; }
    public bool UseFixedRect { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 900;
    public int Height { get; set; } = 600;
    public int MonitorIndex { get; set; }                // 0 = primary
    public int VirtualDesktop { get; set; }              // 0 = don't care, 1.. = desktop number
    public bool Enabled { get; set; } = true;
}

/// <summary>A saved arrangement of apps ("حالت کار").</summary>
public sealed class LayoutProfile
{
    public string Name { get; set; } = "حالت کار";
    public List<LayoutEntry> Windows { get; set; } = new();
    public string? WindowManagerLayout { get; set; }     // optional tiling layout to switch to
    public uint HotkeyModifiers { get; set; }
    public int HotkeyKey { get; set; }                   // 0 = no hotkey
}

public sealed class LayoutEntry
{
    public string ExePath { get; set; } = "";
    public string? Arguments { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public bool LaunchIfNotRunning { get; set; } = true;
}

public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxItems { get; set; } = 50;              // 10..200
    public bool KeepImages { get; set; } = true;
    public bool PersistAcrossRestarts { get; set; } = false;  // stored DPAPI-encrypted when true
    public List<string> IgnoredProcesses { get; set; } = new() { "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe" };
}

public sealed class VoiceSettings
{
    public uint HotkeyModifiers { get; set; } = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
    public int HotkeyKey { get; set; } = 0x4D; // M
    public bool Enabled { get; set; } = false;           // microphone stays off until opted in
    public string WakeWord { get; set; } = "نگار";
    public bool PreferPersian { get; set; } = true;      // fa-IR recogniser when installed, else Finglish phrases
    public bool Feedback { get; set; } = true;           // small toast showing what was heard
}

public sealed class WidgetPlacement
{
    public string Kind { get; set; } = "calendar";       // calendar | prayer | weather | note | clock
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; } = 80;
    public double Y { get; set; } = 80;
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 320;
    public string? Text { get; set; }                    // notes
    public string? ColorHex { get; set; }
}

public sealed class WidgetsSettings
{
    public bool Enabled { get; set; } = false;
    public List<WidgetPlacement> Items { get; set; } = new();
    public string? EyvanFile { get; set; }
    public string? ZoneId { get; set; }
    public Dictionary<string, string> WorldZones { get; set; } = new(Shamsa.Desktop.Widgets.CityClock.Defaults);
    public string City { get; set; } = "تهران";
    public double Latitude { get; set; } = 35.6892;
    public double Longitude { get; set; } = 51.3890;
    public string PrayerMethod { get; set; } = "Tehran";  // Tehran | Jafari | MWL | Karachi | Egypt
    /// <summary>Weather is fetched from Open-Meteo (no key) only when enabled — sends the city coordinates.</summary>
    public bool WeatherOnline { get; set; } = false;
    public bool LockPositions { get; set; } = false;
    /// <summary>
    /// Keep the Windows time zone (system clock and date) in step with the public IP's location — e.g. the
    /// VPN exit country. Sends one request to ipwho.is on start, every 30 minutes and when the network changes.
    /// </summary>
    public bool SyncSystemTimeZoneWithIp { get; set; } = true;
    /// <summary>Clock used to show prayer times and weather for any chosen city (default: Tehran time).</summary>
    public string PrayerWeatherZoneId { get; set; } = "Iran Standard Time";
}

public sealed class FenceDefinition
{
    public Shamsa.Desktop.Fences.AppColumn? Column { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "گروه";
    /// <summary>"folder" = shows a folder's content (portal); "filter" = desktop items matching Extensions.</summary>
    public string Mode { get; set; } = "filter";
    public string? FolderPath { get; set; }
    public Dictionary<string, string> Members { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Extensions { get; set; } = new();
    public double X { get; set; } = 60;
    public double Y { get; set; } = 60;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 240;
    public bool Collapsed { get; set; }
    public string? TintHex { get; set; }
}

public sealed class FencesSettings
{
    public bool AvoidOverlap { get; set; } = true;
    public bool Enabled { get; set; } = false;
    public List<FenceDefinition> Fences { get; set; } = new();
    public bool RollUpOnTitleClick { get; set; } = true;
    public bool LockPositions { get; set; } = false;
    /// <summary>Groups take the desktop theme's glass and accent instead of their own fixed tint.</summary>
    public bool FollowTheme { get; set; } = true;
    /// <summary>"plate" = real icon on a themed tile, "glyph" = monochrome theme glyphs, "plain" = Windows icons as-is.</summary>
    public string IconStyle { get; set; } = "plate";
    /// <summary>
    /// Hide the Windows desktop icons while groups are shown, so every file, folder and shortcut appears only
    /// inside the themed groups (a "سایر" group catches the rest). Always restored on exit / «بازگردانی».
    /// </summary>
    public bool HideDesktopIcons { get; set; } = false;
    /// <summary>Also show This PC / Recycle Bin / … inside the catch-all group.</summary>
    public bool ShowSystemIcons { get; set; } = true;
}

public sealed class AutoThemeSettings
{
    public bool Enabled { get; set; } = false;
    public string Mode { get; set; } = "Sun";             // Sun (sunrise/sunset from widgets coordinates) | Fixed
    public string LightAt { get; set; } = "07:00";
    public string DarkAt { get; set; } = "19:00";
    public bool ApplyToWindows { get; set; } = true;      // HKCU AppsUseLightTheme / SystemUsesLightTheme
    public bool ApplyToShamsa { get; set; } = true;       // Shamsa light/dark glass tint
}

public sealed class TopBarPluginSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Plugin ids (file names in %LocalAppData%\Shamsa\plugins) the user switched off.</summary>
    public List<string> Disabled { get; set; } = new();
}

public sealed class DesktopSettings
{
    public int Version { get; set; } = 1;
    /// <summary>Master switch. Off by default — nothing on the Windows desktop changes until the user opts in.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Hide the Windows taskbar while the Shamsa top bar or dock is running (always restored on exit).</summary>
    public bool HideWindowsTaskbar { get; set; } = false;
    /// <summary>Monitor (Screen.DeviceName, e.g. \\.\DISPLAY2) for fences, widgets, dock and top bar; empty = Windows primary.</summary>
    public string DisplayDevice { get; set; } = "";
    /// <summary>Id of the last whole-desktop theme applied from «حالت‌های آماده» (for the ✓ mark).</summary>
    public string? ThemePresetId { get; set; }
    public bool MatchWindowBorders { get; set; }
    public bool AnimateWallpaper { get; set; }
    public bool UsePresetWallpaper { get; set; } = true;
    /// <summary>Empty = embedded Vazirmatn.</summary>
    public string UiFontFamily { get; set; } = "";
    public DesktopThemeSettings Theme { get; set; } = new();
    public TopBarSettings TopBar { get; set; } = new();
    public DockSettings Dock { get; set; } = new();
    public LauncherSettings Launcher { get; set; } = new();
    public WindowManagerSettings WindowManager { get; set; } = new();
    public ControlCenterSettings ControlCenter { get; set; } = new();
    public List<AppRule> AppRules { get; set; } = new();
    public List<LayoutProfile> Layouts { get; set; } = new();
    public PersonalLayoutSettings PersonalLayouts { get; set; } = new();
    public Shamsa.Desktop.Fences.AppIconSettings AppIcons { get; set; } = new();
    public ClipboardSettings Clipboard { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public WidgetsSettings Widgets { get; set; } = new();
    public FencesSettings Fences { get; set; } = new();
    public AutoThemeSettings AutoTheme { get; set; } = new();
    public TopBarPluginSettings Plugins { get; set; } = new();
    /// <summary>Dock: live DWM window thumbnails when hovering an app.</summary>
    public bool DockLivePreviews { get; set; } = true;

    public void Normalize()
    {
        Theme ??= new(); TopBar ??= new(); Dock ??= new(); Launcher ??= new(); WindowManager ??= new();
        ControlCenter ??= new(); AppRules ??= new(); Layouts ??= new(); Clipboard ??= new(); Voice ??= new();
        // Renamed app: the old wake word becomes the new short one (users who chose their own word keep it).
        if (Voice.WakeWord is "شمسا" or "shamsa") Voice.WakeWord = "نگار";
        Widgets ??= new(); Fences ??= new(); AutoTheme ??= new(); Plugins ??= new();
        Widgets.Items ??= new(); Fences.Fences ??= new(); Plugins.Disabled ??= new(); Clipboard.IgnoredProcesses ??= new();
        PersonalLayouts ??= new(); PersonalLayouts.Normalize();
        AppIcons ??= new(); AppIcons.Normalize();
        Widgets.WorldZones ??= new(Shamsa.Desktop.Widgets.CityClock.Defaults);
        DisplayDevice ??= "";
        if (string.IsNullOrWhiteSpace(Widgets.PrayerWeatherZoneId)) Widgets.PrayerWeatherZoneId = "Iran Standard Time";
        if (!Shamsa.Desktop.Fences.FenceIconArt.Styles.Any(x => x.Id == Fences.IconStyle)) Fences.IconStyle = "plate";
        // Imported settings may contain missing or repeated IDs. Keep every group and its members.
        Widgets.Items.RemoveAll(w => w == null);
        Fences.Fences.RemoveAll(f => f == null);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in Widgets.Items)
            if (string.IsNullOrWhiteSpace(w.Id) || !ids.Add(w.Id)) { w.Id = Guid.NewGuid().ToString("N"); ids.Add(w.Id); }
        ids.Clear();
        foreach (var f in Fences.Fences)
        {
            if (string.IsNullOrWhiteSpace(f.Id) || !ids.Add(f.Id)) { f.Id = Guid.NewGuid().ToString("N"); ids.Add(f.Id); }
            f.Extensions ??= new(); f.Extensions.RemoveAll(string.IsNullOrWhiteSpace); f.Members ??= new();
            if (f.Members.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (path, name) in f.Members) members[path] = name;
                f.Members = members;
            }
            f.Column?.Normalize(f.Members);
        }
        foreach (var l in Layouts) l.Windows ??= new();
        Clipboard.MaxItems = Math.Clamp(Clipboard.MaxItems, 10, 200);
        Theme.GlassOpacity = Math.Clamp(Theme.GlassOpacity, 20, 100);
        Theme.CornerRadius = Math.Clamp(Theme.CornerRadius, 0, 24);
        TopBar.Height = Math.Clamp(TopBar.Height, 26, 56);
        Dock.IconSize = Math.Clamp(Dock.IconSize, 32, 80);
        Dock.Magnification = Math.Clamp(Dock.Magnification, 100, 200);
        Launcher.MaxResults = Math.Clamp(Launcher.MaxResults, 4, 15);
        WindowManager.Hotkeys ??= new();
        WindowManager.Gap = Math.Clamp(WindowManager.Gap, 0, 40);
        WindowManager.OuterPadding = Math.Clamp(WindowManager.OuterPadding, 0, 60);
        TopBar.LeftItems ??= new(); TopBar.CenterItems ??= new(); TopBar.RightItems ??= new();
        Dock.Pinned ??= new(); Launcher.History ??= new(); WindowManager.ExcludedProcesses ??= new();
        if (Launcher.History.Count > 50) Launcher.History = Launcher.History.Take(50).ToList();
    }

    public DesktopSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(this, DesktopSettingsStore.Json), DesktopSettingsStore.Json)!;
        copy.Normalize();
        return copy;
    }
}

/// <summary>Loads/saves %LocalAppData%\Shamsa\desktop.json and notifies modules of changes.</summary>
public static class DesktopSettingsStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static DesktopSettings? _current;
    public static string FilePath => Path.Combine(AppPaths.DataDir, "desktop.json");

    /// <summary>Raised (on the caller's thread) after <see cref="Save"/>.</summary>
    public static event Action<DesktopSettings>? Changed;

    public static DesktopSettings Current => _current ??= Load();

    public static DesktopSettings Load()
    {
        DesktopSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), Json) ?? new()
                : CreateDefaults();
        }
        catch (Exception ex)
        {
            AppLog.Warn("desktop.json unreadable, using defaults: " + ex.Message);
            s = CreateDefaults();
        }
        s.Normalize();
        return s;
    }

    private static DesktopSettings CreateDefaults()
    {
        var s = new DesktopSettings();
        // Sensible first pins: Explorer, the default browser is unknown, so Edge if present, Settings.
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        s.Dock.Pinned.Add(Path.Combine(win, "explorer.exe"));
        string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        if (File.Exists(edge)) s.Dock.Pinned.Add(edge);
        s.Dock.Pinned.Add("ms-settings:");
        return s;
    }

    public static void Save(DesktopSettings settings) => Save(settings, requireDisk: false);

    internal static bool Save(DesktopSettings settings, bool requireDisk)
    {
        settings.Normalize();
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving desktop.json failed", ex);
            if (requireDisk) return false;
        }
        _current = settings;
        Changed?.Invoke(settings);
        return true;
    }
}
