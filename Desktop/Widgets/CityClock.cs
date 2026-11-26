using System.Net.Http;
using System.Text.Json;

namespace Shamsa.Desktop.Widgets;

internal static class CityClock
{
    internal static readonly Dictionary<string, string> Defaults = new()
    {
        ["Asia/Tehran"] = "ایران · تهران", ["Asia/Baghdad"] = "عراق · بغداد",
        ["Asia/Gaza"] = "فلسطین · غزه", ["Asia/Beirut"] = "لبنان · بیروت", ["Asia/Aden"] = "یمن · صنعا"
    };

    internal static TimeZoneInfo? Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    internal sealed record Location(string City, double Latitude, double Longitude, TimeZoneInfo Zone, bool FromEyvan);
    internal static Location Resolve(WidgetsSettings w)
    {
        var state = EyvanBridge.Read(w.EyvanFile);
        if (state != null && state.At >= DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds())
        {
            if (state.Latitude is double lat && state.Longitude is double lng && Zone(state.ZoneId) is { } explicitZone)
                return new(state.City, lat, lng, explicitZone, true);
            var place = WidgetCityData.Find(state.City);
            if (place != null && WidgetViews.CityTimeZone(place.Display, place.Lat, place.Lng) is { } inferred)
                return new(place.Display, place.Lat, place.Lng, inferred, true);
        }
        return new(w.City, w.Latitude, w.Longitude,
            Zone(w.ZoneId) ?? WidgetViews.CityTimeZone(w.City, w.Latitude, w.Longitude) ?? TimeZoneInfo.Local, false);
    }
    internal static TimeZoneInfo LocalZone(WidgetsSettings w) =>
        w.SyncSystemTimeZoneWithIp ? TimeZoneInfo.Local : Resolve(w).Zone;

    /// <summary>
    /// "Now" for the top bar, clock and calendars. With IP sync on this is simply the system clock, which
    /// <see cref="IpTimeZoneSync"/> keeps in the IP's (VPN's) time zone.
    /// </summary>
    internal static DateTime Now(WidgetsSettings w) =>
        w.SyncSystemTimeZoneWithIp ? DateTime.Now : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, LocalZone(w));

    private static TimeZoneInfo? _tehran;
    private static TimeZoneInfo Tehran => _tehran ??= Zone("Iran Standard Time") ?? Zone("Asia/Tehran") ?? TimeZoneInfo.Local;

    /// <summary>
    /// Clock in which prayer times and weather are shown for ANY chosen city — Tehran time by default,
    /// independent of the (VPN-synced) system clock.
    /// </summary>
    internal static TimeZoneInfo PrayerZone(WidgetsSettings w) =>
        w.PrayerWeatherZoneId == IpTimeZoneSync.SystemZoneToken ? TimeZoneInfo.Local : Zone(w.PrayerWeatherZoneId) ?? Tehran;

    internal static string PrayerZoneName(WidgetsSettings w) => w.PrayerWeatherZoneId switch
    {
        "Iran Standard Time" or "Asia/Tehran" or null or "" => "تهران",
        "Arabic Standard Time" => "بغداد",
        "Afghanistan Standard Time" => "کابل",
        "Arabian Standard Time" => "دبی",
        "Turkey Standard Time" => "استانبول",
        IpTimeZoneSync.SystemZoneToken => "سیستم",
        var other => other
    };

    // Called only by the visible IP lookup button. No IP or device identifier is retained.
    internal static async Task<(string City, double Lat, double Lng, string Zone)> LookupAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var doc = JsonDocument.Parse(await http.GetStringAsync("https://ipwho.is/?fields=success,city,latitude,longitude,timezone"));
        var root = doc.RootElement;
        if (!root.GetProperty("success").GetBoolean()) throw new InvalidDataException();
        string city = root.GetProperty("city").GetString() ?? "";
        double lat = root.GetProperty("latitude").GetDouble(), lng = root.GetProperty("longitude").GetDouble();
        string zone = root.GetProperty("timezone").GetProperty("id").GetString() ?? "";
        if (lat is < -90 or > 90 || lng is < -180 or > 180 || city.Length is 0 or > 100 || Zone(zone) == null)
            throw new InvalidDataException();
        return (WidgetCityData.Nearest(lat, lng, .15)?.Display ?? city, lat, lng, zone);
    }
}
