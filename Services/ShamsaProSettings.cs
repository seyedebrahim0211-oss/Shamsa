#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shamsa.Services;

public enum ShamsaThemePreset
{
    PersianTurquoise,
    Dark,
    Light,
    AutoByTime,
    OledBlack,
    PersianTraditional,
    Minimal,
    Office,
    Gaming
}

public enum DockPosition
{
    Bottom,
    Top,
    Left,
    Right
}

public enum TilingLayoutMode
{
    Floating,
    TwoColumns,
    ThreeColumns,
    FourGrid,
    Stacked
}

public sealed class PerAppWindowRule
{
    public string ProcessName { get; set; } = string.Empty;
    public bool AlwaysOnTop { get; set; }
    public bool ExcludeFromTiling { get; set; }
    public int? TargetWorkspaceIndex { get; set; }
    public bool RememberBounds { get; set; } = true;
    public int? FixedX { get; set; }
    public int? FixedY { get; set; }
    public int? FixedWidth { get; set; }
    public int? FixedHeight { get; set; }
}

public sealed class WorkspaceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "میز کار شخصی";
    public string IconGlyph { get; set; } = "\uE7F4";
    public TilingLayoutMode DefaultLayout { get; set; } = TilingLayoutMode.TwoColumns;
    public string? WallpaperPath { get; set; }
    public ShamsaThemePreset ThemePreset { get; set; } = ShamsaThemePreset.PersianTurquoise;
    public List<string> StartupApps { get; set; } = new();
    public List<string> PinnedDockApps { get; set; } = new();
    public List<string> EnabledWidgets { get; set; } = new() { "Clock", "PersianCalendar", "PrayerTimes", "Weather", "StickyNotes" };
}

public sealed class WidgetInstanceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WidgetType { get; set; } = "Clock";
    public string MonitorDeviceName { get; set; } = "PRIMARY";
    public double Left { get; set; } = 48;
    public double Top { get; set; } = 64;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 170;
    public bool IsLocked { get; set; }
    public double Opacity { get; set; } = 0.88;
    public double CornerRadius { get; set; } = 16;
    public string FontFamily { get; set; } = "Vazirmatn";
    public double FontSize { get; set; } = 13;
    public string? CustomAccentHex { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public int RefreshIntervalSeconds { get; set; } = 5;
}

public sealed class FolderFenceGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "پوشه‌ها و ابزارها";
    public string Category { get; set; } = "Custom";
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 240;
    public bool IsCollapsed { get; set; }
    public bool ViewAsList { get; set; }
    public int IconSizePx { get; set; } = 44;
    public List<string> ItemPaths { get; set; } = new();
}

public sealed class ScreenRecorderConfig
{
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ShamsaRecordings");
    public string ContainerFormat { get; set; } = "mp4";
    public int Fps { get; set; } = 30;
    public string QualityPreset { get; set; } = "Balanced"; // Lossless, High, Balanced, Compact
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; } = true;
    public bool ShowFloatingHud { get; set; } = true;
    public bool VoiceCommandsEnabled { get; set; } = true;
    public string VoiceLanguage { get; set; } = "fa-IR";
}

public sealed class FolderPreviewProConfig
{
    public bool GlassPreviewEnabled { get; set; } = true;
    public double WindowWidth { get; set; } = 680;
    public double WindowHeight { get; set; } = 460;
    public double GlassOpacity { get; set; } = 0.86;
    public double CornerRadius { get; set; } = 20;
    public bool PreviewOfficeFirstPage { get; set; } = true;
    public bool PreviewPdfFirstPage { get; set; } = true;
    public bool EnableAcrylicBackdrop { get; set; } = true;
}

public sealed class ShamsaProSettings
{
    public int SchemaVersion { get; set; } = 300;
    public bool SafeMode { get; set; }
    public bool LowPerformanceMode { get; set; }
    public bool ReduceAnimations { get; set; }
    public string FontFamily { get; set; } = "Vazirmatn";
    public bool RightToLeft { get; set; } = true;

    // Theme & Auto-Schedule
    public ShamsaThemePreset ThemePreset { get; set; } = ShamsaThemePreset.PersianTurquoise;
    public string CustomAccentHex { get; set; } = "#14B8A6";
    public int AutoLightStartHour { get; set; } = 7;
    public int AutoDarkStartHour { get; set; } = 19;
    public double GlobalBlurStrength { get; set; } = 24;
    public double GlobalCornerRadius { get; set; } = 16;
    public double GlobalGlassOpacity { get; set; } = 0.85;
    public bool SyncThemeWithWallpaper { get; set; } = true;

    // Independent Module Toggles
    public bool EnableTopBarControlCenter { get; set; } = true;
    public bool HideNativeTaskbar { get; set; } = false; // Strictly optional & restorable
    public bool EnableSmartDock { get; set; } = true;
    public bool EnableWindowManager { get; set; } = true;
    public bool EnableDesktopWidgets { get; set; } = true;
    public bool EnableDesktopFences { get; set; } = true;
    public bool EnableWallpaperEngine { get; set; } = false;
    public bool EnableSpotlightLauncher { get; set; } = true;
    public bool EnableClipboardHistory { get; set; } = true;
    public bool EnableScreenRecorder { get; set; } = true;
    public bool EnableFolderIconStudio { get; set; } = true;
    public bool EnablePlugins { get; set; } = true;

    // Dock Config
    public DockPosition DockPosition { get; set; } = DockPosition.Bottom;
    public double DockIconSize { get; set; } = 48;
    public double DockMagnificationScale { get; set; } = 1.45;
    public bool DockAutoHide { get; set; }
    public bool DockHideOnFullscreen { get; set; } = true;
    public bool DockLiveWindowPreview { get; set; } = true;

    // Tiling Window Manager Config
    public TilingLayoutMode ActiveLayout { get; set; } = TilingLayoutMode.TwoColumns;
    public int WindowGapPx { get; set; } = 10;
    public List<PerAppWindowRule> AppRules { get; set; } = new();

    // Workspaces & Widgets & Fences
    public string ActiveWorkspaceId { get; set; } = "work";
    public List<WorkspaceProfile> Workspaces { get; set; } = CreateDefaultWorkspaces();
    public List<WidgetInstanceConfig> Widgets { get; set; } = new();
    public List<FolderFenceGroup> Fences { get; set; } = new();

    // Sub-configs
    public ScreenRecorderConfig Recorder { get; set; } = new();
    public FolderPreviewProConfig FolderPreview { get; set; } = new();

    // Location for Prayer Times & Weather (Default: Tehran; configurable for any city e.g. Boshruyeh)
    public string CityName { get; set; } = "تهران";
    public double Latitude { get; set; } = 35.6892;
    public double Longitude { get; set; } = 51.3890;

    // Updater
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = false;

    public static List<WorkspaceProfile> CreateDefaultWorkspaces() => new()
    {
        new WorkspaceProfile { Id = "work", Name = "حالت کار", IconGlyph = "\uE821", DefaultLayout = TilingLayoutMode.TwoColumns },
        new WorkspaceProfile { Id = "design", Name = "میز کار طراحی", IconGlyph = "\uE790", DefaultLayout = TilingLayoutMode.Floating },
        new WorkspaceProfile { Id = "dev", Name = "میز کار برنامه‌نویسی", IconGlyph = "\uE943", DefaultLayout = TilingLayoutMode.ThreeColumns },
        new WorkspaceProfile { Id = "edit", Name = "میز کار تدوین", IconGlyph = "\uE714", DefaultLayout = TilingLayoutMode.TwoColumns },
        new WorkspaceProfile { Id = "study", Name = "میز کار مطالعه", IconGlyph = "\uE736", DefaultLayout = TilingLayoutMode.TwoColumns },
        new WorkspaceProfile { Id = "gaming", Name = "میز کار بازی", IconGlyph = "\uE7FC", DefaultLayout = TilingLayoutMode.Floating },
        new WorkspaceProfile { Id = "personal", Name = "میز کار شخصی", IconGlyph = "\uE77B", DefaultLayout = TilingLayoutMode.Floating }
    };
}

public static class ShamsaProSettingsService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shamsa");

    public static string SettingsPath => Path.Combine(DataDir, "pro-settings.json");
    public static string BackupPath => Path.Combine(DataDir, "pro-settings.backup.json");

    public static ShamsaProSettings Current { get; private set; } = new();
    public static event Action<ShamsaProSettings>? Changed;

    public static ShamsaProSettings Load()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<ShamsaProSettings>(json, JsonOpts);
                    if (loaded != null)
                    {
                        Current = Normalize(loaded);
                        return Current;
                    }
                }
                catch
                {
                    // Fallback safely to defaults if settings file is corrupted
                }
            }

            Current = Normalize(new ShamsaProSettings());
            SaveInternal(Current);
            return Current;
        }
    }

    public static void Save(ShamsaProSettings? settings = null)
    {
        ShamsaProSettings snapshot;
        lock (Gate)
        {
            if (settings != null) Current = Normalize(settings);
            SaveInternal(Current);
            snapshot = Current;
        }
        Changed?.Invoke(snapshot);
    }

    public static void CreateBackup()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(SettingsPath))
                File.Copy(SettingsPath, BackupPath, overwrite: true);
        }
    }

    public static void ExportToFile(string destinationPath)
    {
        lock (Gate)
        {
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            File.WriteAllText(destinationPath, json);
        }
    }

    public static ShamsaProSettings ImportFromFile(string sourcePath)
    {
        var json = File.ReadAllText(sourcePath);
        var imported = JsonSerializer.Deserialize<ShamsaProSettings>(json, JsonOpts)
            ?? throw new InvalidDataException("فایل تنظیمات معتبر نیست.");
        Save(imported);
        return Current;
    }

    public static void ResetToDefaults()
    {
        CreateBackup();
        Save(new ShamsaProSettings());
    }

    private static void SaveInternal(ShamsaProSettings settings)
    {
        Directory.CreateDirectory(DataDir);
        var tmp = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(tmp, json);
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    private static ShamsaProSettings Normalize(ShamsaProSettings s)
    {
        s.GlobalGlassOpacity = Math.Clamp(s.GlobalGlassOpacity, 0.35, 0.98);
        s.GlobalCornerRadius = Math.Clamp(s.GlobalCornerRadius, 0, 32);
        s.DockIconSize = Math.Clamp(s.DockIconSize, 32, 84);
        s.DockMagnificationScale = Math.Clamp(s.DockMagnificationScale, 1.0, 2.0);
        s.WindowGapPx = Math.Clamp(s.WindowGapPx, 0, 36);
        s.AutoLightStartHour = Math.Clamp(s.AutoLightStartHour, 0, 23);
        s.AutoDarkStartHour = Math.Clamp(s.AutoDarkStartHour, 0, 23);
        if (s.Workspaces == null || s.Workspaces.Count == 0)
            s.Workspaces = ShamsaProSettings.CreateDefaultWorkspaces();
        return s;
    }
}
