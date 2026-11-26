using System.Text.Json;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Modes;

/// <summary>What starts a mode automatically.</summary>
public sealed class ModeTriggers
{
    /// <summary>Start when one of these processes runs (names without .exe, e.g. WINWORD).</summary>
    public bool OnApps { get; set; }
    public List<string> Apps { get; set; } = new();

    /// <summary>Start while a full-screen window of one of <see cref="FullscreenApps"/> is in front (empty list = any app).</summary>
    public bool OnFullscreen { get; set; }
    public List<string> FullscreenApps { get; set; } = new();

    /// <summary>Start a few minutes before each azan and end <see cref="PrayerDurationMinutes"/> after it.</summary>
    public bool BeforePrayer { get; set; }
    public int PrayerDurationMinutes { get; set; } = 25;
}

/// <summary>A named set of changes applied together («حالت کار», «حالت فیلم», «حالت نماز» or the user's own).</summary>
public sealed class SmartMode
{
    public string Name { get; set; } = "حالت تازه";
    /// <summary>Segoe Fluent glyph shown on the one-click button.</summary>
    public string Glyph { get; set; } = "";
    public bool BuiltIn { get; set; }

    /// <summary>Turn Windows notification banners off while the mode is on.</summary>
    public bool QuietNotifications { get; set; }
    /// <summary>"" = leave the Windows theme alone, "dark" or "light".</summary>
    public string Theme { get; set; } = "";
    /// <summary>Pause whatever is playing (music, video) when the mode starts.</summary>
    public bool PauseMedia { get; set; }
    /// <summary>Name of a saved window arrangement (Rules › چیدمان‌ها) to restore; empty = none.</summary>
    public string LayoutProfile { get; set; } = "";

    public ModeTriggers Triggers { get; set; } = new();
}

/// <summary>Wallpaper per part of the day. Each slot is an image file or a folder (a random picture from it).</summary>
public sealed class TimeWallpaperSettings
{
    public bool Enabled { get; set; }
    public string Morning { get; set; } = "";
    public string Day { get; set; } = "";
    public string Evening { get; set; } = "";
    public string Night { get; set; } = "";
    /// <summary>Use the city's sunrise/sunset (from the prayer-times city) instead of fixed hours.</summary>
    public bool FollowSun { get; set; } = true;
    /// <summary>Set the Windows accent colour from each new wallpaper.</summary>
    public bool AccentFromWallpaper { get; set; }
}

public sealed class ReminderSettings
{
    public bool BreakReminder { get; set; }
    public int BreakAfterMinutes { get; set; } = 50;
    /// <summary>Tell the user the day before a holiday or occasion.</summary>
    public bool Occasions { get; set; }
    /// <summary>Gentle notice N minutes before each azan (independent of «حالت نماز»).</summary>
    public bool PrayerNotice { get; set; }
    public int PrayerNoticeMinutes { get; set; } = 10;
    /// <summary>Provide the «تا اذان …» countdown text for the top bar.</summary>
    public bool PrayerCountdown { get; set; } = true;
}

/// <summary>A registry value as it was before a mode changed it (Value null = it did not exist).</summary>
public sealed class SavedValue
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Value { get; set; }
}

/// <summary>The mode currently on, and what has to be put back when it ends (persisted so a crash can be undone).</summary>
public sealed class ActiveModeState
{
    public string Mode { get; set; } = "";
    /// <summary>"manual", "prayer", "fullscreen" or "app".</summary>
    public string Trigger { get; set; } = "manual";
    public DateTime Since { get; set; }
    /// <summary>For prayer-triggered modes: when the mode ends by itself.</summary>
    public DateTime? Until { get; set; }
    public List<SavedValue> Originals { get; set; } = new();
}

public sealed class SmartModesData
{
    public List<SmartMode> Modes { get; set; } = new();
    public TimeWallpaperSettings Wallpaper { get; set; } = new();
    public ReminderSettings Reminders { get; set; } = new();
    public ActiveModeState? Active { get; set; }

    // bookkeeping (not shown in the UI)
    public string LastWallpaperSlot { get; set; } = "";
    public DateTime LastOccasionNotice { get; set; }

    public static List<SmartMode> DefaultModes() => new()
    {
        new SmartMode
        {
            Name = "حالت کار", Glyph = "", BuiltIn = true, QuietNotifications = true,
            Triggers = new ModeTriggers { Apps = new() { "WINWORD", "EXCEL", "POWERPNT", "Code", "devenv" } }
        },
        new SmartMode
        {
            Name = "حالت فیلم", Glyph = "", BuiltIn = true, QuietNotifications = true, Theme = "dark",
            Triggers = new ModeTriggers { FullscreenApps = new() { "vlc", "mpc-hc64", "mpc-be64", "PotPlayerMini64", "chrome", "msedge", "firefox", "Video.UI" } }
        },
        new SmartMode
        {
            Name = "حالت نماز", Glyph = "", BuiltIn = true, QuietNotifications = true, PauseMedia = true,
            Triggers = new ModeTriggers { PrayerDurationMinutes = 25 }
        },
    };

    public void Normalize()
    {
        Modes ??= new();
        Wallpaper ??= new();
        Reminders ??= new();
        if (Modes.Count == 0) Modes.AddRange(DefaultModes());
        Modes.RemoveAll(m => m == null || string.IsNullOrWhiteSpace(m.Name));
        foreach (var m in Modes)
        {
            m.Triggers ??= new();
            m.Triggers.Apps ??= new();
            m.Triggers.FullscreenApps ??= new();
            m.Triggers.PrayerDurationMinutes = Math.Clamp(m.Triggers.PrayerDurationMinutes, 5, 120);
            m.Theme = m.Theme is "dark" or "light" ? m.Theme : "";
        }
        Reminders.BreakAfterMinutes = Math.Clamp(Reminders.BreakAfterMinutes, 10, 240);
        Reminders.PrayerNoticeMinutes = Math.Clamp(Reminders.PrayerNoticeMinutes, 1, 60);
    }
}

/// <summary>Loads/saves %LocalAppData%\Shamsa\smart-modes.json.</summary>
public static class SmartModesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static SmartModesData? _data;

    /// <summary>Tests point this at a temporary file so the user's settings are never touched.</summary>
    public static string? PathOverride { get; set; }
    public static string FilePath => PathOverride ?? Path.Combine(AppPaths.DataDir, "smart-modes.json");

    public static event Action? Changed;

    public static SmartModesData Data
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                _data = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<SmartModesData>(File.ReadAllText(FilePath), Json) ?? new()
                    : new();
            }
            catch (Exception ex)
            {
                AppLog.Warn("smart-modes.json unreadable, using defaults: " + ex.Message);
                _data = new();
            }
            _data.Normalize();
            return _data;
        }
    }

    public static void Save(bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { AppLog.Warn("Saving smart-modes.json failed: " + ex.Message); }
        if (notify) Changed?.Invoke();
    }

    /// <summary>Forget the cached copy (tests).</summary>
    internal static void Reset() => _data = null;

    public static SmartMode? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
        : Data.Modes.FirstOrDefault(m => string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
          ?? Data.Modes.FirstOrDefault(m => m.Name.Replace("حالت ", "") == name.Trim().Replace("حالت ", ""));
}
