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

    internal static TimeZoneInfo LocalZone(WidgetsSettings w) =>
        Zone(w.ZoneId) ?? WidgetViews.CityTimeZone(w.City, w.Latitude, w.Longitude) ?? TimeZoneInfo.Local;
    internal static DateTime Now(WidgetsSettings w) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, w.EyvanFile != null ? Zone("Asia/Tehran") ?? LocalZone(w) : LocalZone(w));

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
