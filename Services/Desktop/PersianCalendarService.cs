#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Shamsa.Services.Desktop;

public sealed record PersianDateSummary(
    string JalaliDate,
    string GregorianDate,
    string HijriDate,
    string DayOfWeekFa,
    string? IranianOccasion);

public sealed record PrayerTimesSummary(
    string CityName,
    string Fajr,
    string Sunrise,
    string Dhuhr,
    string Asr,
    string Maghrib,
    string Isha);

/// <summary>
/// Persian (Jalali), Gregorian & Hijri calendar engine with Iranian national holidays
/// and astronomical Prayer Times (اوقات شرعی - Tehran Institute of Geophysics method).
/// </summary>
public static class PersianCalendarService
{
    private static readonly PersianCalendar Jalali = new();
    private static readonly HijriCalendar Hijri = new();

    private static readonly string[] JalaliMonths =
    {
        "", "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور",
        "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"
    };

    private static readonly Dictionary<(int Month, int Day), string> SolarOccasions = new()
    {
        [(1, 1)] = "جشن نوروز (تعطیل رسمی)",
        [(1, 2)] = "عید نوروز (تعطیل رسمی)",
        [(1, 3)] = "عید نوروز (تعطیل رسمی)",
        [(1, 4)] = "عید نوروز (تعطیل رسمی)",
        [(1, 12)] = "روز جمهوری اسلامی (تعطیل رسمی)",
        [(1, 13)] = "روز طبیعت - سیزده‌بدر (تعطیل رسمی)",
        [(3, 14)] = "رحلت امام خمینی (تعطیل رسمی)",
        [(3, 15)] = "قیام ۱۵ خرداد (تعطیل رسمی)",
        [(11, 22)] = "پیروزی انقلاب اسلامی (تعطیل رسمی)",
        [(12, 29)] = "روز ملی شدن صنعت نفت (تعطیل رسمی)"
    };

    public static PersianDateSummary GetTodaySummary(DateTime? date = null)
    {
        DateTime dt = date ?? DateTime.Now;
        int jy = Jalali.GetYear(dt), jm = Jalali.GetMonth(dt), jd = Jalali.GetDayOfMonth(dt);
        int hy = Hijri.GetYear(dt), hm = Hijri.GetMonth(dt), hd = Hijri.GetDayOfMonth(dt);

        string dow = dt.DayOfWeek switch
        {
            DayOfWeek.Saturday => "شنبه",
            DayOfWeek.Sunday => "یکشنبه",
            DayOfWeek.Monday => "دوشنبه",
            DayOfWeek.Tuesday => "سه‌شنبه",
            DayOfWeek.Wednesday => "چهارشنبه",
            DayOfWeek.Thursday => "پنج‌شنبه",
            _ => "جمعه"
        };

        SolarOccasions.TryGetValue((jm, jd), out string? occasion);
        return new PersianDateSummary(
            $"{dow} {jd} {JalaliMonths[jm]} {jy}",
            dt.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            $"{hy}/{hm:00}/{hd:00} هجری قمری",
            dow,
            occasion);
    }

    /// <summary>
    /// Computes approximate local prayer times using solar declination & equation of time (Tehran method: Fajr 17.7°, Maghrib 4.5°, Isha 14°).
    /// </summary>
    public static PrayerTimesSummary ComputePrayerTimes(string cityName, double latitude, double longitude, DateTime? date = null)
    {
        DateTime dt = (date ?? DateTime.Now).Date;
        double tzOffsetHours = TimeZoneInfo.Local.GetUtcOffset(dt).TotalHours;
        int dayOfYear = dt.DayOfYear;

        double gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1);
        double eqTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma)
                                  - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        double decl = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma)
                      - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma);

        double solarNoon = 12.0 + tzOffsetHours - (longitude / 15.0) - (eqTime / 60.0);

        double HourAngle(double angleDeg)
        {
            double latRad = latitude * Math.PI / 180.0;
            double cosH = (-Math.Sin(angleDeg * Math.PI / 180.0) - Math.Sin(latRad) * Math.Sin(decl))
                          / (Math.Cos(latRad) * Math.Cos(decl));
            cosH = Math.Clamp(cosH, -1.0, 1.0);
            return Math.Acos(cosH) * 180.0 / Math.PI / 15.0;
        }

        double fajr = solarNoon - HourAngle(17.7);
        double sunrise = solarNoon - HourAngle(0.833);
        double dhuhr = solarNoon + 0.03;
        double asrAngle = -Math.Atan(1.0 / (1.0 + Math.Tan(Math.Abs(latitude * Math.PI / 180.0 - decl)))) * 180.0 / Math.PI;
        double asr = solarNoon + HourAngle(asrAngle);
        double maghrib = solarNoon + HourAngle(4.5);
        double isha = solarNoon + HourAngle(14.0);

        return new PrayerTimesSummary(
            cityName,
            FormatHours(fajr),
            FormatHours(sunrise),
            FormatHours(dhuhr),
            FormatHours(asr),
            FormatHours(maghrib),
            FormatHours(isha));
    }

    private static string FormatHours(double h)
    {
        h = (h % 24 + 24) % 24;
        int hh = (int)Math.Floor(h);
        int mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh = (hh + 1) % 24; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }
}
