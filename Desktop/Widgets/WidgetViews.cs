using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Shamsa.Desktop.TopBar;

namespace Shamsa.Desktop.Widgets;

/// <summary>Content of each widget kind (without the glass frame). Shared by the desktop windows and previews.</summary>
public static class WidgetViews
{
    public static readonly (string Kind, string Title, string Glyph)[] Kinds =
    {
        ("calendar", "تقویم شمسی", ""),
        ("prayer", "اوقات شرعی", ""),
        ("weather", "آب‌وهوا", ""),
        ("note", "یادداشت", ""),
        ("world", "ساعت جهانی", ""),
        ("clock", "ساعت", ""),
    };

    public static string TitleOf(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind).Title ?? kind;

    public static (double W, double H) DefaultSize(string kind) => kind switch
    {
        "calendar" => (300, 330),
        "prayer" => (260, 300),
        "weather" => (280, 230),
        "note" => (240, 220),
        "clock" => (280, 150),
        _ => (260, 220),
    };

    internal static string D(string s) => DesktopTheme.Digits(s, true);

    /// <summary>Time zone of a widget city (Iran, Iraq, Afghanistan, Tajikistan); null = use the PC's zone.</summary>
    internal static TimeZoneInfo? CityTimeZone(string? city, double lat, double lng)
    {
        // Country comes from the city dataset («شهرستان، استان» or a bare name); for hand-typed
        // coordinates the nearest listed place (within ~55 km) decides.
        var place = WidgetCityData.Find(city);
        if (place != null && (Math.Abs(place.Lat - lat) > 1 || Math.Abs(place.Lng - lng) > 1)) place = null; // name and coords disagree
        place ??= WidgetCityData.Nearest(lat, lng);
        string? id = place?.Country switch
        {
            "IR" => "Iran Standard Time",
            "IQ" => "Arabic Standard Time",
            "AF" => "Afghanistan Standard Time",
            "TJ" => "West Asia Standard Time",
            "PS" => "Asia/Gaza",
            "LB" => "Asia/Beirut",
            "YE" => "Asia/Aden",
            _ => lat is >= 25 and <= 40 && lng is >= 44 and <= 63.5 ? "Iran Standard Time" : null,
        };
        if (id == null) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return null; }
    }

    internal static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? brush = null) => new()
    {
        Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? DesktopTheme.TextBrush, TextWrapping = TextWrapping.Wrap
    };

    internal static TextBlock Glyph(string glyph, double size = 16, Brush? brush = null) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
        Foreground = brush ?? DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center
    };

    public static FrameworkElement Build(WidgetPlacement p, DesktopSettings s, Action<string>? noteChanged = null) => p.Kind switch
    {
        "calendar" => new CalendarWidget(s),
        "prayer" => new PrayerWidget(s),
        "weather" => new WeatherWidget(s),
        "note" => new NoteWidget(p.Text ?? "", noteChanged),
        "clock" => new ClockWidget(s.Widgets),
        "world" => new WorldClock(s.Widgets),
        _ => Text("ویجت ناشناخته"),
    };

    // ------------------------------------------------------------------ calendar

    private sealed class CalendarWidget : DockPanel
    {
        private readonly DesktopSettings _s;
        private int _year, _month;
        private readonly TextBlock _title = Text("", 15, FontWeights.Bold);
        private readonly UniformGrid _grid = new() { Columns = 7 };
        private readonly TextBlock _footer = Text("", 11.5, brush: DesktopTheme.MutedBrush);

        public CalendarWidget(DesktopSettings s)
        {
            _s = s;
            var (y, m, _) = PersianDate.Parts(CityClock.Now(_s.Widgets).Date);
            _year = y; _month = m;

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var prev = NavButton("", () => Move(-1)); // in RTL, "previous" sits on the right
            var next = NavButton("", () => Move(1));
            DockPanel.SetDock(prev, System.Windows.Controls.Dock.Left);
            DockPanel.SetDock(next, System.Windows.Controls.Dock.Right);
            header.Children.Add(prev);
            header.Children.Add(next);
            _title.TextAlignment = TextAlignment.Center;
            _title.MouseLeftButtonUp += (_, _) => { var (yy, mm, _) = PersianDate.Parts(CityClock.Now(_s.Widgets).Date); _year = yy; _month = mm; Render(); };
            _title.ToolTip = "برگشت به امروز";
            header.Children.Add(_title);
            DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
            Children.Add(header);

            var week = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
            for (int i = 0; i < 7; i++)
            {
                var t = Text(PersianDate.WeekHeader[i], 11.5, FontWeights.SemiBold, i == 6 ? HolidayBrush : DesktopTheme.MutedBrush);
                t.TextAlignment = TextAlignment.Center;
                week.Children.Add(t);
            }
            DockPanel.SetDock(week, System.Windows.Controls.Dock.Top);
            Children.Add(week);

            _footer.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(_footer, System.Windows.Controls.Dock.Bottom);
            Children.Add(_footer);
            Children.Add(_grid);
            Render();
        }

        private static readonly SolidColorBrush HolidayBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85)));

        private Button NavButton(string glyph, Action click)
        {
            var b = new Button
            {
                Content = Glyph(glyph, 12), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 2, 8, 2), Cursor = System.Windows.Input.Cursors.Hand, Foreground = DesktopTheme.TextBrush
            };
            b.Click += (_, _) => click();
            return b;
        }

        private void Move(int delta)
        {
            _month += delta;
            if (_month > 12) { _month = 1; _year++; }
            if (_month < 1) { _month = 12; _year--; }
            Render();
        }

        private void Render()
        {
            _grid.Children.Clear();
            var cal = PersianDate.Calendar;
            _title.Text = D($"{PersianDate.Months[_month - 1]} {_year}");
            DateTime first = cal.ToDateTime(_year, _month, 1, 0, 0, 0, 0);
            int lead = PersianDate.WeekColumn(first);
            for (int i = 0; i < lead; i++) _grid.Children.Add(new Border());
            int days = cal.GetDaysInMonth(_year, _month);
            var accent = DesktopTheme.AccentBrush(_s);
            var notes = new List<string>();
            for (int d = 1; d <= days; d++)
            {
                var date = first.AddDays(d - 1);
                var occ = PersianHolidays.For(date);
                bool holiday = date.DayOfWeek == DayOfWeek.Friday || occ.Any(o => o.IsHoliday);
                bool today = date == CityClock.Now(_s.Widgets).Date;
                var t = Text(D(d.ToString()), 12.5, today ? FontWeights.Bold : FontWeights.Normal, today ? Brushes.White : holiday ? HolidayBrush : DesktopTheme.TextBrush);
                t.TextAlignment = TextAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                var cell = new Border
                {
                    Child = t, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(14), MinHeight = 26,
                    Background = today ? accent : Brushes.Transparent,
                    BorderBrush = occ.Count > 0 && !today ? HolidayBrush : Brushes.Transparent,
                    BorderThickness = new Thickness(occ.Count > 0 ? 1 : 0)
                };
                if (occ.Count > 0)
                {
                    cell.ToolTip = string.Join("\n", occ.Select(o => o.Title + (o.IsLunar ? " (قمری)" : "")));
                    foreach (var o in occ) notes.Add(D($"{d} {PersianDate.Months[_month - 1]}: {o.Title}"));
                }
                _grid.Children.Add(cell);
            }
            _footer.Text = notes.Count == 0 ? "تعطیلی رسمی در این ماه نیست." : string.Join("\n", notes.Take(3)) + (notes.Count > 3 ? "\n…" : "");
            _footer.ToolTip = PersianHolidays.LunarNote;
        }
    }

    // ------------------------------------------------------------------ prayer times

    private sealed class PrayerWidget : StackPanel
    {
        private readonly DesktopSettings _s;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

        public PrayerWidget(DesktopSettings s)
        {
            _s = s;
            _timer.Tick += (_, _) => Render();
            Loaded += (_, _) => _timer.Start();
            Unloaded += (_, _) => _timer.Stop();
            Render();
        }

        private void Render()
        {
            Children.Clear();
            var w = _s.Widgets;
            var location = CityClock.Resolve(w);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(Glyph("", 15, DesktopTheme.AccentBrush(_s)));
            head.Children.Add(new TextBlock { Text = "  اوقات شرعی " + WidgetCityData.ShortName(location.City), FontSize = 14.5, FontWeight = FontWeights.Bold, Foreground = DesktopTheme.TextBrush });
            Children.Add(head);
            // Prayer times are computed for the chosen city's coordinates but SHOWN in Tehran time (or the clock
            // picked in the studio) — never in the system clock, which follows the VPN's country.
            var cityZone = CityClock.PrayerZone(w);
            Children.Add(Text(PersianDate.Long(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cityZone).Date, true), 11.5, brush: DesktopTheme.MutedBrush));
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cityZone);
            double? offset = cityZone.GetUtcOffset(DateTime.UtcNow).TotalHours;
            PrayerTimes.Times times;
            try { times = PrayerTimes.Compute(now.Date, location.Latitude, location.Longitude, w.PrayerMethod, offset); }
            catch { Children.Add(Text("محاسبه ممکن نشد.")); return; }
            var nextName = times.All.Where(x => x.At > now && x.Name != "طلوع آفتاب" && x.Name != "غروب آفتاب")
                .Select(x => x.Name).FirstOrDefault();
            if (Math.Abs((cityZone.GetUtcOffset(DateTime.UtcNow) - TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow)).TotalMinutes) > 1
                || location.Zone.BaseUtcOffset != cityZone.BaseUtcOffset)
                Children.Add(Text("ساعت‌ها به وقت " + CityClock.PrayerZoneName(w) + " است", 10.5, brush: DesktopTheme.MutedBrush));
            var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (name, at) in times.All)
            {
                bool next = name == nextName;
                var row = new Border
                {
                    Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 1, 0, 1),
                    Background = next ? DesktopTheme.HoverBrush : Brushes.Transparent
                };
                var dp = new DockPanel();
                var time = Text(at == DateTime.MinValue ? "—" : D(at.ToString("HH:mm")), 13, next ? FontWeights.Bold : FontWeights.Normal,
                    next ? DesktopTheme.AccentBrush(_s) : DesktopTheme.TextBrush);
                DockPanel.SetDock(time, System.Windows.Controls.Dock.Right);
                dp.Children.Add(time);
                dp.Children.Add(Text(name, 13, next ? FontWeights.SemiBold : FontWeights.Normal, next ? DesktopTheme.TextBrush : DesktopTheme.MutedBrush));
                row.Child = dp;
                rows.Children.Add(row);
            }
            Children.Add(rows);
            var eyvan = EyvanBridge.Read(w.EyvanFile);
            if (location.FromEyvan && eyvan != null && eyvan.NextAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && eyvan.At >= DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds())
            {
                var at = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(eyvan.NextAt), cityZone).ToString("HH:mm");
                Children.Add(Text(D($"ایوان · {eyvan.City} · {eyvan.NextName}  {at}"), 12, brush: DesktopTheme.AccentBrush(_s)));
                return;
            }
            var nextAt = times.All.FirstOrDefault(x => x.Name == nextName).At;
            if (nextName != null && nextAt > now)
            {
                var left = nextAt - now;
                Children.Add(Text(D($"تا {nextName}: {(int)left.TotalHours} ساعت و {left.Minutes} دقیقه"), 12, brush: DesktopTheme.AccentBrush(_s)));
            }
        }
    }

    // ------------------------------------------------------------------ weather

    private sealed class WeatherWidget : StackPanel
    {
        private readonly DesktopSettings _s;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
        private bool _loading;
        private (double, double)? _shownCoordinates;
        private DateTime _lastRefresh = DateTime.MinValue;

        public WeatherWidget(DesktopSettings s)
        {
            _s = s;
            _timer.Tick += (_, _) => _ = LoadAsync();
            Loaded += (_, _) => { if (_s.Widgets.WeatherOnline) { _timer.Start(); _ = LoadAsync(); } };
            Unloaded += (_, _) => _timer.Stop();
            if (!s.Widgets.WeatherOnline)
            {
                Children.Add(Glyph("", 30, DesktopTheme.MutedBrush));
                Children.Add(Text("آب‌وهوا خاموش است", 14, FontWeights.SemiBold));
                Children.Add(Text("برای دیدن هوای " + s.Widgets.City + " در استودیو دسکتاپ › ویجت‌ها «دریافت آنلاین آب‌وهوا» را روشن کن. فقط مختصات شهر برای Open-Meteo فرستاده می‌شود.", 11.5, brush: DesktopTheme.MutedBrush));
                return;
            }
            Children.Add(Text("در حال دریافت هوای " + s.Widgets.City + "…", 12.5, brush: DesktopTheme.MutedBrush));
        }

        private async Task LoadAsync()
        {
            var w = _s.Widgets;
            var location = CityClock.Resolve(w);
            var coordinates = (location.Latitude, location.Longitude);
            if (_loading || (_shownCoordinates == coordinates && DateTime.UtcNow - _lastRefresh < TimeSpan.FromMinutes(25))) return;
            _loading = true;
            WeatherService.Report? r;
            try { r = await WeatherService.GetAsync(location.Latitude, location.Longitude, zone: CityClock.PrayerZone(w)); }
            finally { _loading = false; }
            var current = CityClock.Resolve(w);
            if (!IsLoaded || !_s.Widgets.WeatherOnline || current.Latitude != location.Latitude || current.Longitude != location.Longitude) return;
            _shownCoordinates = coordinates;
            _lastRefresh = r == null ? DateTime.UtcNow.AddMinutes(-24) : DateTime.UtcNow;
            await Dispatcher.InvokeAsync(() =>
            {
                Children.Clear();
                if (r == null)
                {
                    Children.Add(Text("دریافت آب‌وهوا ممکن نشد (اینترنت؟). دوباره تلاش می‌شود.", 12.5, brush: DesktopTheme.MutedBrush));
                    return;
                }
                var (name, glyph) = WeatherService.Describe(r.Code);
                var top = new StackPanel { Orientation = Orientation.Horizontal };
                top.Children.Add(Glyph(glyph, 38, DesktopTheme.AccentBrush(_s)));
                var mid = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
                mid.Children.Add(Text(D($"{Math.Round(r.Temperature)}°"), 30, FontWeights.Bold));
                mid.Children.Add(Text($"{name} · {WidgetCityData.ShortName(location.City)}", 12.5, brush: DesktopTheme.MutedBrush));
                top.Children.Add(mid);
                Children.Add(top);
                var updated = TimeZoneInfo.ConvertTimeFromUtc(r.FetchedAt, CityClock.PrayerZone(w));
                Children.Add(Text(D($"باد {Math.Round(r.Wind)} کیلومتر بر ساعت · به‌روز {updated:HH:mm} به وقت {CityClock.PrayerZoneName(w)}"), 11.5, brush: DesktopTheme.MutedBrush));
                var days = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 0) };
                foreach (var d in r.Days.Skip(1).Take(3))
                {
                    var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                    var dn = Text(PersianDate.Days[(int)d.Date.DayOfWeek], 11.5, brush: DesktopTheme.MutedBrush);
                    dn.TextAlignment = TextAlignment.Center;
                    col.Children.Add(dn);
                    var g = Glyph(WeatherService.Describe(d.Code).Glyph, 18);
                    g.HorizontalAlignment = HorizontalAlignment.Center;
                    col.Children.Add(g);
                    var t = Text(D($"{Math.Round(d.Max)}° / {Math.Round(d.Min)}°"), 11.5);
                    t.TextAlignment = TextAlignment.Center;
                    col.Children.Add(t);
                    days.Children.Add(col);
                }
                Children.Add(days);
            });
        }
    }

    // ------------------------------------------------------------------ note

    private sealed class NoteWidget : DockPanel
    {
        public NoteWidget(string text, Action<string>? changed)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(Glyph("", 13, DesktopTheme.MutedBrush));
            head.Children.Add(Text("  یادداشت", 12, brush: DesktopTheme.MutedBrush));
            DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
            Children.Add(head);
            var box = new TextBox
            {
                Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Foreground = DesktopTheme.TextBrush, CaretBrush = DesktopTheme.TextBrush,
                FontSize = 14, FlowDirection = FlowDirection.RightToLeft, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = DesktopTheme.Font
            };
            if (string.IsNullOrEmpty(text)) box.Text = "";
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            debounce.Tick += (_, _) => { debounce.Stop(); changed?.Invoke(box.Text); };
            box.TextChanged += (_, _) =>
            {
                // direction follows the first strong character
                var first = box.Text.FirstOrDefault(char.IsLetter);
                box.FlowDirection = first != default && first < 0x0590 ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
                debounce.Stop(); debounce.Start();
            };
            Children.Add(box);
        }
    }

    // ------------------------------------------------------------------ clock

    private sealed class ClockWidget : StackPanel
    {
        private readonly TextBlock _time = Text("", 44, FontWeights.Bold);
        private readonly TextBlock _sec = Text("", 16, brush: DesktopTheme.MutedBrush);
        private readonly TextBlock _date = Text("", 13.5, brush: DesktopTheme.MutedBrush);
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        private readonly WidgetsSettings _settings;
        public ClockWidget(WidgetsSettings settings)
        {
            _settings = settings;
            VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(_time);
            _sec.VerticalAlignment = VerticalAlignment.Bottom;
            _sec.Margin = new Thickness(6, 0, 0, 10);
            row.Children.Add(_sec);
            Children.Add(row);
            _date.TextAlignment = TextAlignment.Center;
            Children.Add(_date);
            _timer.Tick += (_, _) => Tick();
            Loaded += (_, _) => _timer.Start();
            Unloaded += (_, _) => _timer.Stop();
            Tick();
        }

        private void Tick()
        {
            var now = CityClock.Now(_settings);
            _time.Text = D(now.ToString("HH:mm"));
            _sec.Text = D(now.ToString("ss"));
            _date.Text = PersianDate.Long(now, true) + " · \u202A" + now.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + "\u202C";
        }
    }

    private sealed class WorldClock : ScrollViewer
    {
        public WorldClock(WidgetsSettings settings)
        {
            var list = new StackPanel();
            Content = list; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            void Tick()
            {
                var instant = DateTimeOffset.UtcNow;
                list.Children.Clear();
                foreach (var (id, label) in settings.WorldZones)
                    if (CityClock.Zone(id) is { } z)
                        list.Children.Add(Text(label + "   " + D(TimeZoneInfo.ConvertTime(instant, z).ToString("HH:mm")), 15));
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) => Tick(); Loaded += (_, _) => { Tick(); timer.Start(); }; Unloaded += (_, _) => timer.Stop();
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}
