using Shamsa.Desktop.Widgets;

namespace Shamsa.Desktop.Smart.Modes;

/// <summary>Next azan and sun times for the city chosen in the prayer-times widget (in that city's own clock).</summary>
public static class PrayerClock
{
    public sealed record NextAzan(string Name, DateTime AtCity, TimeSpan Remaining);
    public sealed record SunTimes(DateTime Sunrise, DateTime Sunset);

    // Iran's three daily azans (صبح، ظهر، مغرب); عشا is prayed with مغرب, so it gets no separate notice.
    private static readonly HashSet<string> NonAzan = new() { "طلوع آفتاب", "غروب آفتاب", "نیمه‌شب شرعی", "عشا" };

    /// <summary>Tests can freeze the clock.</summary>
    public static Func<DateTime>? NowOverride { get; set; }
    private static DateTime UtcNow => (NowOverride?.Invoke() ?? DateTime.Now).ToUniversalTime();

    private static (DateTime CityNow, double? Offset) CityNow(WidgetsSettings w)
    {
        var zone = WidgetViews.CityTimeZone(w.City, w.Latitude, w.Longitude);
        var utc = UtcNow;
        if (zone == null) return (utc.ToLocalTime(), null);
        var cityNow = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        return (cityNow, zone.GetUtcOffset(cityNow).TotalHours);
    }

    private static PrayerTimes.Times? Compute(WidgetsSettings w, DateTime date, double? offset)
    {
        try { return PrayerTimes.Compute(date, w.Latitude, w.Longitude, w.PrayerMethod, offset); }
        catch { return null; }
    }

    public static NextAzan? Next(WidgetsSettings? w = null)
    {
        w ??= DesktopSettingsStore.Current.Widgets;
        var (now, offset) = CityNow(w);
        for (int day = 0; day < 2; day++)
        {
            var t = Compute(w, now.Date.AddDays(day), offset);
            if (t == null) return null;
            var hit = t.All.Where(x => !NonAzan.Contains(x.Name) && x.At > now).OrderBy(x => x.At).FirstOrDefault();
            if (hit.Name != null) return new NextAzan(hit.Name, hit.At, hit.At - now);
        }
        return null;
    }

    /// <summary>The most recent azan today (for ending «حالت نماز»), or null.</summary>
    public static (string Name, DateTime AtCity, TimeSpan Ago)? Previous(WidgetsSettings? w = null)
    {
        w ??= DesktopSettingsStore.Current.Widgets;
        var (now, offset) = CityNow(w);
        var t = Compute(w, now.Date, offset);
        if (t == null) return null;
        var hit = t.All.Where(x => !NonAzan.Contains(x.Name) && x.At <= now).OrderByDescending(x => x.At).FirstOrDefault();
        return hit.Name == null ? null : (hit.Name, hit.At, now - hit.At);
    }

    /// <summary>Today's sunrise/sunset in the city's clock, plus the city's "now" (null when it cannot be computed).</summary>
    public static (SunTimes Sun, DateTime CityNow)? Sun(WidgetsSettings? w = null)
    {
        w ??= DesktopSettingsStore.Current.Widgets;
        var (now, offset) = CityNow(w);
        var t = Compute(w, now.Date, offset);
        return t == null ? null : (new SunTimes(t.Sunrise, t.Sunset), now);
    }

    /// <summary>«۱ ساعت و ۵ دقیقه» / «۱۲ دقیقه» / «کمتر از یک دقیقه».</summary>
    public static string Duration(TimeSpan span)
    {
        int minutes = (int)Math.Ceiling(span.TotalMinutes);
        if (minutes <= 0) return "کمتر از یک دقیقه";
        int h = minutes / 60, m = minutes % 60;
        string text = h > 0 ? (m > 0 ? $"{h} ساعت و {m} دقیقه" : $"{h} ساعت") : $"{m} دقیقه";
        return DesktopTheme.Digits(text, true);
    }

    public static string Clock(DateTime t) => DesktopTheme.Digits(t.ToString("HH:mm"), true);
}
