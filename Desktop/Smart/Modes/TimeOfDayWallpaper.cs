using Shamsa.Desktop.Personalize;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Modes;

/// <summary>Changes the wallpaper (and optionally the accent colour) for morning, day, evening and night.</summary>
public static class TimeOfDayWallpaper
{
    public static readonly (string Id, string Title, string Glyph)[] Slots =
    {
        ("morning", "صبح", ""),
        ("day", "روز", ""),
        ("evening", "عصر", ""),
        ("night", "شب", ""),
    };

    public static string Get(TimeWallpaperSettings w, string slot) => slot switch
    {
        "morning" => w.Morning, "day" => w.Day, "evening" => w.Evening, _ => w.Night
    };

    public static void Set(TimeWallpaperSettings w, string slot, string value)
    {
        switch (slot)
        {
            case "morning": w.Morning = value; break;
            case "day": w.Day = value; break;
            case "evening": w.Evening = value; break;
            default: w.Night = value; break;
        }
    }

    /// <summary>
    /// Part of the day. With <see cref="TimeWallpaperSettings.FollowSun"/> the city's sunrise and sunset decide
    /// (morning = sunrise … +3 h, evening = sunset −1 h … +1 h); otherwise 5/11/17/20 o'clock.
    /// </summary>
    public static string CurrentSlot(TimeWallpaperSettings w, DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.Now;
        if (w.FollowSun && nowOverride == null && PrayerClock.Sun() is { } sun)
        {
            var (s, cityNow) = sun;
            if (cityNow < s.Sunrise.AddMinutes(-30)) return "night";
            if (cityNow < s.Sunrise.AddHours(3)) return "morning";
            if (cityNow < s.Sunset.AddHours(-1)) return "day";
            if (cityNow < s.Sunset.AddHours(1)) return "evening";
            return "night";
        }
        int h = now.Hour;
        return h >= 5 && h < 11 ? "morning" : h >= 11 && h < 17 ? "day" : h >= 17 && h < 20 ? "evening" : "night";
    }

    /// <summary>An image for the slot: the file itself, or a random picture from the folder. Null when nothing usable.</summary>
    public static string? PickImage(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (File.Exists(source)) return source;
        if (!Directory.Exists(source)) return null;
        var images = WallpaperService.ImagesIn(source);
        return images.Count == 0 ? null : images[Random.Shared.Next(images.Count)];
    }

    /// <summary>The Personalize slideshow already rotates wallpapers; the two would fight, so the slideshow wins.</summary>
    public static bool SlideshowConflict => PersonalizeStore.Data.Slideshow.Enabled || PersonalizeStore.Data.Slideshow.BingDaily;

    /// <summary>Applies the wallpaper when the part of the day changed (or <paramref name="force"/>). Returns the new slot or null.</summary>
    public static string? Tick(SmartModesData data, bool force = false)
    {
        var w = data.Wallpaper;
        if (!w.Enabled || SlideshowConflict) return null;
        string slot = CurrentSlot(w);
        if (!force && slot == data.LastWallpaperSlot) return null;
        string? image = PickImage(Get(w, slot));
        data.LastWallpaperSlot = slot;
        if (image == null) return slot; // nothing set for this part of the day: leave the wallpaper alone
        try
        {
            WallpaperService.SetWallpaper(image, WallpaperService.CurrentFit());
            if (w.AccentFromWallpaper && WallpaperService.DominantColor(image) is { } c)
                AccentService.SetAccent(AccentService.Normalize(c));
            AppLog.Info("Time-of-day wallpaper: " + slot);
        }
        catch (Exception ex) { AppLog.Warn("Time-of-day wallpaper failed: " + ex.Message); }
        return slot;
    }
}
