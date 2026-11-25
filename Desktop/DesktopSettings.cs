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
    public bool FocusFollowsMouse { get; set; } = false;
    public bool FloatDialogs { get; set; } = true;
    public List<string> ExcludedProcesses { get; set; } = new() { "Taskmgr.exe", "ScreenClippingHost.exe" };
}

public sealed class DesktopSettings
{
    public int Version { get; set; } = 1;
    /// <summary>Master switch. Off by default — nothing on the Windows desktop changes until the user opts in.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Hide the Windows taskbar while the Shamsa top bar or dock is running (always restored on exit).</summary>
    public bool HideWindowsTaskbar { get; set; } = false;
    /// <summary>Empty = embedded Vazirmatn.</summary>
    public string UiFontFamily { get; set; } = "";
    public DesktopThemeSettings Theme { get; set; } = new();
    public TopBarSettings TopBar { get; set; } = new();
    public DockSettings Dock { get; set; } = new();
    public LauncherSettings Launcher { get; set; } = new();
    public WindowManagerSettings WindowManager { get; set; } = new();

    public void Normalize()
    {
        Theme ??= new(); TopBar ??= new(); Dock ??= new(); Launcher ??= new(); WindowManager ??= new();
        Theme.GlassOpacity = Math.Clamp(Theme.GlassOpacity, 20, 100);
        Theme.CornerRadius = Math.Clamp(Theme.CornerRadius, 0, 24);
        TopBar.Height = Math.Clamp(TopBar.Height, 26, 56);
        Dock.IconSize = Math.Clamp(Dock.IconSize, 32, 80);
        Dock.Magnification = Math.Clamp(Dock.Magnification, 100, 200);
        Launcher.MaxResults = Math.Clamp(Launcher.MaxResults, 4, 15);
        WindowManager.Gap = Math.Clamp(WindowManager.Gap, 0, 40);
        WindowManager.OuterPadding = Math.Clamp(WindowManager.OuterPadding, 0, 60);
        TopBar.LeftItems ??= new(); TopBar.CenterItems ??= new(); TopBar.RightItems ??= new();
        Dock.Pinned ??= new(); Launcher.History ??= new(); WindowManager.ExcludedProcesses ??= new();
        if (Launcher.History.Count > 50) Launcher.History = Launcher.History.Take(50).ToList();
    }

    public DesktopSettings Clone() =>
        JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(this, DesktopSettingsStore.Json), DesktopSettingsStore.Json)!;
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

    public static void Save(DesktopSettings settings)
    {
        settings.Normalize();
        _current = settings;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { AppLog.Error("Saving desktop.json failed", ex); }
        Changed?.Invoke(settings);
    }
}
