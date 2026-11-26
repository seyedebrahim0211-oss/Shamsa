using System.Globalization;

namespace Shamsa.Desktop.TopBar;

/// <summary>Solar Hijri (Jalali) date helpers with Persian names.</summary>
internal static class PersianDate
{
    public static readonly PersianCalendar Calendar = new();

    public static readonly string[] Months =
    {
        "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور",
        "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"
    };

    /// <summary>Indexed by <see cref="DayOfWeek"/> (Sunday = 0).</summary>
    public static readonly string[] Days = { "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه", "شنبه" };

    /// <summary>Week header starting Saturday.</summary>
    public static readonly string[] WeekHeader = { "ش", "ی", "د", "س", "چ", "پ", "ج" };

    public static (int Year, int Month, int Day) Parts(DateTime d) =>
        (Calendar.GetYear(d), Calendar.GetMonth(d), Calendar.GetDayOfMonth(d));

    /// <summary>e.g. "چهارشنبه ۵ آذر ۱۴۰۵".</summary>
    public static string Long(DateTime d, bool persianDigits)
    {
        var (y, m, day) = Parts(d);
        return DesktopTheme.Digits($"{Days[(int)d.DayOfWeek]} {day} {Months[m - 1]} {y}", persianDigits);
    }

    /// <summary>e.g. "۵ آذر".</summary>
    public static string Short(DateTime d, bool persianDigits)
    {
        var (_, m, day) = Parts(d);
        return DesktopTheme.Digits($"{day} {Months[m - 1]}", persianDigits);
    }

    /// <summary>Column (0 = Saturday … 6 = Friday) of a date in a Persian week.</summary>
    public static int WeekColumn(DateTime d) => ((int)d.DayOfWeek + 1) % 7;
}
