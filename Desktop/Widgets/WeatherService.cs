using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Shamsa.Services;

namespace Shamsa.Desktop.Widgets;

/// <summary>
/// Current weather + 3-day forecast from Open-Meteo (free, no API key). Only used when the user turns
/// on "WeatherOnline"; the request carries nothing but the chosen city's coordinates.
/// </summary>
public static class WeatherService
{
    public sealed record Day(DateTime Date, double Min, double Max, int Code);
    public sealed record Report(double Temperature, double Wind, int Code, List<Day> Days, DateTime FetchedAt);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly object CacheGate = new();
    private static Report? _cache;
    private static (double, double) _cacheKey;

    static WeatherService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Shamsa-Desktop/3");
    }

    public static async Task<Report?> GetAsync(double lat, double lng, bool force = false)
    {
        lock (CacheGate)
            if (!force && _cache != null && _cacheKey == (lat, lng) && DateTime.UtcNow - _cache.FetchedAt < TimeSpan.FromMinutes(25))
                return _cache;
        string url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current=temperature_2m,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min,weather_code&timezone=auto&forecast_days=4",
            lat, lng);
        try
        {
            using var stream = await Http.GetStreamAsync(url).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            var days = new List<Day>();
            var dates = daily.GetProperty("time");
            for (int i = 0; i < dates.GetArrayLength(); i++)
            {
                days.Add(new Day(
                    DateTime.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture),
                    daily.GetProperty("temperature_2m_min")[i].GetDouble(),
                    daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                    daily.GetProperty("weather_code")[i].GetInt32()));
            }
            var report = new Report(cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("wind_speed_10m").GetDouble(),
                cur.GetProperty("weather_code").GetInt32(), days, DateTime.UtcNow);
            lock (CacheGate) { _cache = report; _cacheKey = (lat, lng); }
            return report;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Weather fetch failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>WMO weather code → Persian name and Segoe Fluent/MDL2 glyph.</summary>
    public static (string Name, string Glyph) Describe(int code) => code switch
    {
        0 => ("صاف", ""),
        1 => ("عمدتاً صاف", ""),
        2 => ("نیمه‌ابری", ""),
        3 => ("ابری", ""),
        45 or 48 => ("مه", ""),
        51 or 53 or 55 or 56 or 57 => ("نم‌نم باران", ""),
        61 or 63 or 80 or 81 => ("باران", ""),
        65 or 82 or 66 or 67 => ("باران شدید", ""),
        71 or 73 or 75 or 77 or 85 or 86 => ("برف", ""),
        95 or 96 or 99 => ("رعد و برق", ""),
        _ => ("نامشخص", ""),
    };
}
