using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using static Shamsa.Desktop.ControlCenter.StudioUi;

namespace Shamsa.Desktop.AutoTheme;

/// <summary>Desktop Studio page «تم خودکار».</summary>
public static class AutoThemeStudioPage
{
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var a = s.AutoTheme;
        var page = Page();
        var status = Muted("");
        void UpdateStatus()
        {
            try
            {
                var now = DateTime.Now;
                bool light = AutoThemeModule.ScheduleSaysLight(s, now);
                string text = "طبق زمان‌بندی، الان " + (light ? "روشن" : "تیره") + " است.";
                if (string.Equals(a.Mode, "Sun", StringComparison.OrdinalIgnoreCase))
                {
                    var sun = SunCalculator.Compute(now.Date, s.Widgets.Latitude, s.Widgets.Longitude);
                    if (sun is { } v)
                        text += $"  طلوع امروز {v.Sunrise:HH:mm} • غروب {v.Sunset:HH:mm} ({s.Widgets.City})";
                }
                if (AutoThemeModule.NextChange(s, now) is { } next)
                    text += $"\nتغییر بعدی: {next.At:HH:mm} ← " + (next.ToLight ? "روشن" : "تیره");
                status.Text = DesktopTheme.Digits(text, true);
            }
            catch { status.Text = ""; }
        }
        void Changed() { changed(); UpdateStatus(); }

        var main = Body(Card(page, "تم خودکار روشن و تیره", "حالت روشن در روز و تیره در شب، با ساعت ثابت یا طلوع و غروب خورشید. با خاموش کردن، تم قبلی شما برمی‌گردد."));
        main.Children.Add(Toggle("فعال", null, a.Enabled, v => a.Enabled = v, Changed));
        main.Children.Add(Labeled("زمان‌بندی", Combo(new[] { ("Sun", "طلوع و غروب خورشید"), ("Fixed", "ساعت ثابت") }, a.Mode, v => a.Mode = v, Changed)));
        main.Children.Add(Labeled("روشن از ساعت", Text(a.LightAt, v => a.LightAt = v, Changed)));
        main.Children.Add(Labeled("تیره از ساعت", Text(a.DarkAt, v => a.DarkAt = v, Changed)));
        main.Children.Add(Muted("ساعت‌ها در حالت «ساعت ثابت» و وقتی طلوع/غروب قابل محاسبه نیست به کار می‌روند (قالب ۲۴ ساعته: ساعت:دقیقه)."));

        var loc = Body(Card(page, "موقعیت برای طلوع و غروب", "همان موقعیت ویجت‌ها (اوقات شرعی و آب‌وهوا) است. فقط روی همین رایانه محاسبه می‌شود."));
        loc.Children.Add(Labeled("شهر", Text(s.Widgets.City, v => s.Widgets.City = v, Changed, ltr: false, width: 160)));
        loc.Children.Add(Labeled("عرض جغرافیایی", Text(s.Widgets.Latitude.ToString("0.####", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d is >= -90 and <= 90) s.Widgets.Latitude = d; }, Changed)));
        loc.Children.Add(Labeled("طول جغرافیایی", Text(s.Widgets.Longitude.ToString("0.####", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d is >= -180 and <= 180) s.Widgets.Longitude = d; }, Changed)));
        var cities = new WrapPanel();
        foreach (var (name, lat, lon) in new[] { ("تهران", 35.6892, 51.3890), ("مشهد", 36.2605, 59.6168), ("اصفهان", 32.6546, 51.6680), ("شیراز", 29.5918, 52.5837), ("تبریز", 38.0800, 46.2919), ("کرج", 35.8400, 50.9391), ("اهواز", 31.3183, 48.6706), ("قم", 34.6399, 50.8759) })
        {
            cities.Children.Add(Button(name, () =>
            {
                s.Widgets.City = name; s.Widgets.Latitude = lat; s.Widgets.Longitude = lon;
                Changed();
            }));
        }
        loc.Children.Add(cities);
        loc.Children.Add(Muted("پس از انتخاب شهر آماده، صفحه را دوباره باز کنید تا مختصات در کادرها دیده شود."));

        var what = Body(Card(page, "چه چیزی عوض شود"));
        what.Children.Add(Toggle("تم برنامه‌ها و خود ویندوز", "همان کلید «حالت روشن/تیره» تنظیمات ویندوز", a.ApplyToWindows, v => a.ApplyToWindows = v, Changed));
        what.Children.Add(Toggle("شیشه نوار بالا، داک و جستجوی نگارخانه", "در حالت روشن رنگ شیشه روشن‌تر می‌شود و بعد به رنگ خودتان برمی‌گردد", a.ApplyToShamsa, v => a.ApplyToShamsa = v, Changed));
        what.Children.Add(Button("جابه‌جایی فوری روشن/تیره", AutoThemeModule.Toggle));

        var st = Body(Card(page, "وضعیت"));
        st.Children.Add(status);
        UpdateStatus();
        return page;
    }
}
