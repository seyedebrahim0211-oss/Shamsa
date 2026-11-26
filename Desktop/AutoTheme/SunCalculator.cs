namespace Shamsa.Desktop.AutoTheme;

/// <summary>NOAA sunrise/sunset approximation (official zenith 90.833°), accurate to about a minute.</summary>
public static class SunCalculator
{
    /// <summary>Local sunrise and sunset for <paramref name="date"/>; null when the sun never rises/sets (polar day/night).</summary>
    public static (DateTime Sunrise, DateTime Sunset)? Compute(DateTime date, double latitude, double longitude, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var rise = Event(date, latitude, longitude, rising: true);
        var set = Event(date, latitude, longitude, rising: false);
        if (rise == null || set == null) return null;
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        DateTime ToLocal(double utcHours) => TimeZoneInfo.ConvertTimeFromUtc(day.AddHours(utcHours), zone);
        return (ToLocal(rise.Value), ToLocal(set.Value));
    }

    private static double Deg(double rad) => rad * 180.0 / Math.PI;
    private static double Rad(double deg) => deg * Math.PI / 180.0;
    private static double Norm(double v, double max) { v %= max; return v < 0 ? v + max : v; }

    /// <summary>UTC hour of the event (may be &lt;0 or ≥24 — caller adds to the date).</summary>
    private static double? Event(DateTime date, double lat, double lon, bool rising)
    {
        const double zenith = 90.833;
        int n = date.DayOfYear;
        double lngHour = lon / 15.0;
        double t = n + ((rising ? 6 : 18) - lngHour) / 24.0;
        double m = 0.9856 * t - 3.289;
        double l = Norm(m + 1.916 * Math.Sin(Rad(m)) + 0.020 * Math.Sin(Rad(2 * m)) + 282.634, 360);
        double ra = Norm(Deg(Math.Atan(0.91764 * Math.Tan(Rad(l)))), 360);
        ra += Math.Floor(l / 90) * 90 - Math.Floor(ra / 90) * 90;
        ra /= 15.0;
        double sinDec = 0.39782 * Math.Sin(Rad(l));
        double cosDec = Math.Cos(Math.Asin(sinDec));
        double cosH = (Math.Cos(Rad(zenith)) - sinDec * Math.Sin(Rad(lat))) / (cosDec * Math.Cos(Rad(lat)));
        if (cosH > 1 || cosH < -1) return null;
        double h = rising ? 360 - Deg(Math.Acos(cosH)) : Deg(Math.Acos(cosH));
        h /= 15.0;
        double localMean = h + ra - 0.06571 * t - 6.622;
        return Norm(localMean - lngHour, 24);
    }
}
