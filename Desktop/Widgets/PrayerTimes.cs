namespace Shamsa.Desktop.Widgets;

/// <summary>
/// Prayer times computed offline (port of the public PrayTimes.org algorithm).
/// Tehran method: Fajr 17.7°, Isha 14°, Maghrib 4.5°, Jafari midnight.
/// </summary>
public static class PrayerTimes
{
    public sealed record Method(string Id, string Name, double Fajr, double? IshaAngle, double? MaghribAngle, double? IshaMinutes, bool JafariMidnight);

    public static readonly Method[] Methods =
    {
        new("Tehran", "مؤسسه ژئوفیزیک تهران", 17.7, 14, 4.5, null, true),
        new("Jafari", "شیعه اثناعشری (قم)", 16, 14, 4, null, true),
        new("MWL", "اتحادیه جهانی مسلمانان", 18, 17, null, null, false),
        new("Karachi", "دانشگاه کراچی", 18, 18, null, null, false),
        new("Egypt", "سازمان نقشه‌برداری مصر", 19.5, 17.5, null, null, false),
    };

    public static Method Find(string? id) => Methods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Methods[0];

    public sealed record Times(DateTime Fajr, DateTime Sunrise, DateTime Dhuhr, DateTime Sunset, DateTime Maghrib, DateTime Isha, DateTime Midnight)
    {
        public IEnumerable<(string Name, DateTime At)> All => new[]
        {
            ("اذان صبح", Fajr), ("طلوع آفتاب", Sunrise), ("اذان ظهر", Dhuhr), ("غروب آفتاب", Sunset),
            ("اذان مغرب", Maghrib), ("عشا", Isha), ("نیمه‌شب شرعی", Midnight),
        };
    }

    public static Times Compute(DateTime date, double lat, double lng, string? methodId, double? utcOffsetHours = null)
    {
        var m = Find(methodId);
        double tz = utcOffsetHours ?? TimeZoneInfo.Local.GetUtcOffset(date.Date.AddHours(12)).TotalHours;
        double jDate = Julian(date.Year, date.Month, date.Day) - lng / (15 * 24.0);

        // initial guesses (hours), refined twice
        double fajr = 5, sunrise = 6, dhuhr = 12, sunset = 18, maghrib = 18, isha = 18;
        for (int i = 0; i < 2; i++)
        {
            fajr = SunAngleTime(jDate, lat, m.Fajr, fajr / 24, ccw: true);
            sunrise = SunAngleTime(jDate, lat, 0.833, sunrise / 24, ccw: true);
            dhuhr = MidDay(jDate, dhuhr / 24);
            sunset = SunAngleTime(jDate, lat, 0.833, sunset / 24, ccw: false);
            maghrib = m.MaghribAngle is double ma ? SunAngleTime(jDate, lat, ma, maghrib / 24, ccw: false) : sunset;
            isha = m.IshaAngle is double ia ? SunAngleTime(jDate, lat, ia, isha / 24, ccw: false) : sunset + (m.IshaMinutes ?? 90) / 60;
        }

        double adj = tz - lng / 15;
        fajr += adj; sunrise += adj; dhuhr += adj; sunset += adj; maghrib += adj; isha += adj;
        double midnight = m.JafariMidnight
            ? sunset + FixHour(fajr - sunset) / 2
            : sunset + FixHour(sunrise - sunset) / 2;

        DateTime At(double h) => double.IsNaN(h) ? DateTime.MinValue : date.Date.AddMinutes(Math.Round(h * 60));
        return new Times(At(fajr), At(sunrise), At(dhuhr), At(sunset), At(maghrib), At(isha), At(midnight));
    }

    private static double Julian(int year, int month, int day)
    {
        if (month <= 2) { year -= 1; month += 12; }
        double a = Math.Floor(year / 100.0);
        double b = 2 - a + Math.Floor(a / 4);
        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
    }

    private static (double Decl, double Eqt) SunPosition(double jd)
    {
        double d = jd - 2451545.0;
        double g = FixAngle(357.529 + 0.98560028 * d);
        double q = FixAngle(280.459 + 0.98564736 * d);
        double l = FixAngle(q + 1.915 * Sin(g) + 0.020 * Sin(2 * g));
        double e = 23.439 - 0.00000036 * d;
        double ra = ArcTan2(Cos(e) * Sin(l), Cos(l)) / 15;
        double eqt = q / 15 - FixHour(ra);
        double decl = ArcSin(Sin(e) * Sin(l));
        return (decl, eqt);
    }

    private static double MidDay(double jDate, double t) => FixHour(12 - SunPosition(jDate + t).Eqt);

    private static double SunAngleTime(double jDate, double lat, double angle, double t, bool ccw)
    {
        double decl = SunPosition(jDate + t).Decl;
        double noon = MidDay(jDate, t);
        double x = (-Sin(angle) - Sin(decl) * Sin(lat)) / (Cos(decl) * Cos(lat));
        if (x < -1 || x > 1) return double.NaN; // polar day/night
        double time = ArcCos(x) / 15;
        return noon + (ccw ? -time : time);
    }

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;
    private static double Sin(double d) => Math.Sin(Rad(d));
    private static double Cos(double d) => Math.Cos(Rad(d));
    private static double ArcSin(double x) => Deg(Math.Asin(x));
    private static double ArcCos(double x) => Deg(Math.Acos(x));
    private static double ArcTan2(double y, double x) => Deg(Math.Atan2(y, x));
    private static double FixAngle(double a) { a -= 360 * Math.Floor(a / 360); return a < 0 ? a + 360 : a; }
    private static double FixHour(double a) { a -= 24 * Math.Floor(a / 24); return a < 0 ? a + 24 : a; }
}

/// <summary>Cities offered in the prayer/weather pickers.</summary>
public static class WidgetCities
{
    public sealed record City(string Name, double Lat, double Lng);

    public static readonly City[] All =
    {
        new("تهران", 35.6892, 51.3890), new("مشهد", 36.2605, 59.6168), new("اصفهان", 32.6546, 51.6680),
        new("شیراز", 29.5918, 52.5837), new("تبریز", 38.0800, 46.2919), new("قم", 34.6399, 50.8759),
        new("کرج", 35.8400, 50.9391), new("اهواز", 31.3183, 48.6706), new("کرمانشاه", 34.3142, 47.0650),
        new("ارومیه", 37.5527, 45.0761), new("رشت", 37.2808, 49.5832), new("زاهدان", 29.4963, 60.8629),
        new("کرمان", 30.2839, 57.0834), new("همدان", 34.7983, 48.5148), new("یزد", 31.8974, 54.3569),
        new("اردبیل", 38.2498, 48.2933), new("بندرعباس", 27.1832, 56.2666), new("اراک", 34.0954, 49.7013),
        new("زنجان", 36.6736, 48.4787), new("سنندج", 35.3219, 46.9862), new("قزوین", 36.2688, 50.0041),
        new("خرم‌آباد", 33.4878, 48.3558), new("گرگان", 36.8456, 54.4393), new("ساری", 36.5633, 53.0601),
        new("بوشهر", 28.9234, 50.8203), new("بیرجند", 32.8649, 59.2262), new("بجنورد", 37.4747, 57.3290),
        new("سمنان", 35.5729, 53.3971), new("ایلام", 33.6374, 46.4227), new("یاسوج", 30.6682, 51.5880),
        new("شهرکرد", 32.3256, 50.8644), new("بشرویه", 33.8680, 57.4280), new("کیش", 26.5578, 53.9800),
        new("کابل", 34.5553, 69.2075), new("هرات", 34.3529, 62.2040), new("دوشنبه", 38.5598, 68.7870),
    };
}
