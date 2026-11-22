using System.Text.Json;

namespace Shamsa.Services;

public sealed class CompressionSettings
{
    /// <summary>0=کیفیت بالا، 1=متعادل (پیشنهادی)، 2=کم‌حجم (تلگرام/وب)، 3=حداکثر فشرده‌سازی، 4=دستی</summary>
    public int Preset { get; set; } = 1;
    /// <summary>15..95 (کیفیت دلخواه تصویر و ویدیو)</summary>
    public int QualityPercent { get; set; } = 75;
    /// <summary>0=ابعاد اصلی، 1=1920px، 2=1280px، 3=1024px، 4=720px، 5=75%، 6=50%</summary>
    public int ResolutionMode { get; set; } = 2;
    /// <summary>0=هوشمند، 1=WEBP، 2=JPG، 3=حفظ فرمت اصلی</summary>
    public int ImageFormatMode { get; set; } = 0;
    /// <summary>0=خودکار (کیفیت ثابت)، یا سقف مگابایت ویدیو (10, 25, 50, 100)</summary>
    public int TargetVideoSizeMb { get; set; } = 0;

    public int EffectiveVideoCrf => Math.Clamp((int)Math.Round(38.0 - (QualityPercent - 15) * (18.0 / 80.0)), 18, 38);
    public int EffectiveAudioKbps => QualityPercent >= 82 ? 192 : QualityPercent >= 68 ? 128 : QualityPercent >= 50 ? 96 : 64;
    public int EffectiveJpgQ => Math.Clamp((int)Math.Round(16.0 - (QualityPercent - 15) * (14.0 / 80.0)), 2, 18);

    public void ApplyPreset(int preset)
    {
        Preset = Math.Clamp(preset, 0, 4);
        switch (Preset)
        {
            case 0: QualityPercent = 88; break;
            case 1: QualityPercent = 75; break;
            case 2: QualityPercent = 60; break;
            case 3: QualityPercent = 42; break;
        }
    }
}

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = 5;
    /// <summary>PersianTurquoise, LapisGlass, EmeraldGlass, AmethystGlass</summary>
    public string ThemeId { get; set; } = "PersianTurquoise";
    /// <summary>Custom accent in #RRGGBB format; empty = palette default.</summary>
    public string ThemeAccentHex { get; set; } = string.Empty;
    /// <summary>Opacity of layered glass cards, 45..97 percent.</summary>
    public int GlassOpacityPercent { get; set; } = 83;
    /// <summary>Use gradients and surface highlights; disabling reduces effects on slower PCs.</summary>
    public bool GlassEffectsEnabled { get; set; } = true;
    /// <summary>Explore the first 100 local entries, show at most six media previews.</summary>
    public bool FolderPreviewEnabled { get; set; } = true;
    /// <summary>Automatic release checks do not install or run downloaded executables.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Optionally stage verified EXE automatically; installation always needs consent.</summary>
    public bool AutoDownloadUpdates { get; set; } = false;
    public bool HoverPreviewEnabled { get; set; } = true;
    public bool PreviewImages { get; set; } = true;
    public bool SmartScrub { get; set; } = true;
    public int HoverDelayMs { get; set; } = 140;
    public int PreviewSize { get; set; } = 1; // 0 = کوچک، 1 = متوسط، 2 = بزرگ (نگهداری برای سازگاری)
    public int PreviewMaxSidePx { get; set; } = 320; // 150..560 پیکسل (ابعاد قابل کوچک/بزرگ کردن)

    public bool DonutEnabled { get; set; } = true;
    public bool DonutAnimations { get; set; } = true;
    public bool DonutShortcutEnabled { get; set; } = true;
    public uint DonutShortcutModifiers { get; set; } = 0x0001; // MOD_ALT
    public int DonutShortcutKey { get; set; } = 0x51;          // VK_Q (Alt + Q)
    public bool MoveOriginalsToRecycleBin { get; set; }

    public CompressionSettings Compression { get; set; } = new();

    public bool AutoOrganizeEnabled { get; set; }
    public string AutoOrganizeFolder { get; set; } = string.Empty;

    public string LastOrganizerFolder { get; set; } = string.Empty;
    public string LastRenameFolder { get; set; } = string.Empty;

    public bool HasSeenGuide { get; set; } = false;
    public List<string> ShelfItems { get; set; } = new();
}

/// <summary>Persists user preferences to %LocalAppData%\Shamsa\settings.json.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string SettingsFile => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Raised (on the caller's thread) after settings are saved.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions);
                if (loaded != null)
                {
                    if (loaded.SettingsVersion < 2)
                    {
                        if (loaded.HoverDelayMs >= 300) loaded.HoverDelayMs = 140;
                    }
                    if (loaded.SettingsVersion < 3)
                    {
                        loaded.PreviewMaxSidePx = loaded.PreviewSize switch
                        {
                            0 => 240,
                            2 => 440,
                            _ => 320
                        };
                        loaded.SettingsVersion = 3;
                    }
                    if (loaded.SettingsVersion < 4) loaded.SettingsVersion = 4;
                    if (loaded.SettingsVersion < 5) loaded.SettingsVersion = 5;
                    loaded.ThemeId = ThemeService.IsKnownTheme(loaded.ThemeId) ? loaded.ThemeId : "PersianTurquoise";
                    loaded.GlassOpacityPercent = Math.Clamp(loaded.GlassOpacityPercent, 45, 97);
                    loaded.ThemeAccentHex = ThemeService.TryParseAccent(loaded.ThemeAccentHex, out _) ? loaded.ThemeAccentHex : string.Empty;
                    loaded.HoverDelayMs = Math.Clamp(loaded.HoverDelayMs, 40, 2000);
                    loaded.PreviewSize = Math.Clamp(loaded.PreviewSize, 0, 2);
                    loaded.PreviewMaxSidePx = Math.Clamp(loaded.PreviewMaxSidePx <= 0 ? 320 : loaded.PreviewMaxSidePx, 150, 560);
                    loaded.Compression ??= new CompressionSettings();
                    loaded.Compression.QualityPercent = Math.Clamp(loaded.Compression.QualityPercent, 15, 95);
                    loaded.Compression.ResolutionMode = Math.Clamp(loaded.Compression.ResolutionMode, 0, 6);
                    loaded.Compression.ImageFormatMode = Math.Clamp(loaded.Compression.ImageFormatMode, 0, 3);
                    loaded.Compression.Preset = Math.Clamp(loaded.Compression.Preset, 0, 4);
                    loaded.ShelfItems ??= new List<string>();
                    Current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to load settings; using defaults", ex);
            Current = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(Current.AutoOrganizeFolder))
            Current.AutoOrganizeFolder = NativeMethods.GetDownloadsFolder();
    }

    public static void Save()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                string tmp = SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save settings", ex);
        }

        Changed?.Invoke();
    }

    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }
}
