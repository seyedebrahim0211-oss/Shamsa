using System.Globalization;

namespace Shamsa.Desktop.Widgets;

/// <summary>
/// Official Iranian holidays. Solar ones are exact; lunar ones are mapped through the Umm al-Qura
/// calendar and may differ by a day from the official Iranian moon-sighting announcement.
/// </summary>
public static class PersianHolidays
{
    public sealed record Occasion(string Title, bool IsHoliday, bool IsLunar);

    private static readonly PersianCalendar Persian = new();
    private static readonly UmAlQuraCalendar Hijri = new();

    private static readonly Dictionary<(int M, int D), string> Solar = new()
    {
        [(1, 1)] = "عید نوروز", [(1, 2)] = "عید نوروز", [(1, 3)] = "عید نوروز", [(1, 4)] = "عید نوروز",
        [(1, 12)] = "روز جمهوری اسلامی", [(1, 13)] = "روز طبیعت (سیزده‌به‌در)",
        [(3, 14)] = "رحلت امام خمینی", [(3, 15)] = "قیام ۱۵ خرداد",
        [(11, 22)] = "پیروزی انقلاب اسلامی", [(12, 29)] = "ملی شدن صنعت نفت",
    };

    private static readonly Dictionary<(int M, int D), string> Lunar = new()
    {
        [(1, 9)] = "تاسوعای حسینی", [(1, 10)] = "عاشورای حسینی",
        [(2, 20)] = "اربعین حسینی", [(2, 28)] = "رحلت پیامبر (ص) و شهادت امام حسن (ع)",
        [(3, 8)] = "شهادت امام حسن عسکری (ع)", [(3, 17)] = "میلاد پیامبر (ص) و امام صادق (ع)",
        [(6, 3)] = "شهادت حضرت فاطمه (س)",
        [(7, 13)] = "ولادت امام علی (ع)", [(7, 27)] = "مبعث پیامبر (ص)",
        [(8, 15)] = "ولادت حضرت مهدی (عج)",
        [(9, 21)] = "شهادت امام علی (ع)",
        [(10, 1)] = "عید سعید فطر", [(10, 2)] = "تعطیل به مناسبت عید فطر", [(10, 25)] = "شهادت امام صادق (ع)",
        [(12, 10)] = "عید سعید قربان", [(12, 18)] = "عید سعید غدیر خم",
    };

    /// <summary>Holiday/occasions of a Gregorian date (empty when none).</summary>
    public static List<Occasion> For(DateTime date)
    {
        var list = new List<Occasion>();
        try
        {
            int pm = Persian.GetMonth(date), pd = Persian.GetDayOfMonth(date);
            if (Solar.TryGetValue((pm, pd), out var s)) list.Add(new Occasion(s, true, false));
        }
        catch { }
        try
        {
            if (date >= Hijri.MinSupportedDateTime && date <= Hijri.MaxSupportedDateTime)
            {
                int hy = Hijri.GetYear(date), hm = Hijri.GetMonth(date), hd = Hijri.GetDayOfMonth(date);
                if (Lunar.TryGetValue((hm, hd), out var l)) list.Add(new Occasion(l, true, true));
                // Martyrdom of Imam Reza: last day of Safar (29 or 30).
                if (hm == 2 && hd == Hijri.GetDaysInMonth(hy, 2)) list.Add(new Occasion("شهادت امام رضا (ع)", true, true));
            }
        }
        catch { }
        return list;
    }

    public static bool IsHoliday(DateTime date) => date.DayOfWeek == DayOfWeek.Friday || For(date).Any(o => o.IsHoliday);

    public const string LunarNote = "تعطیلات قمری بر اساس تقویم محاسباتی است و ممکن است با اعلام رسمی یک روز اختلاف داشته باشد.";
}
