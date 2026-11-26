using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shamsa.Desktop.Widgets;

/// <summary>Small building blocks for studio pages (matching the Desktop Studio look).</summary>
internal static class StudioUi
{
    public static Border Card(UIElement content)
    {
        var b = new Border { Child = content, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    public static TextBlock Title(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    public static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    public static CheckBox Toggle(string label, bool value, Action<bool> set, Action changed)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, 5), Foreground = DesktopTheme.TextBrush };
        cb.Checked += (_, _) => { set(true); changed(); };
        cb.Unchecked += (_, _) => { set(false); changed(); };
        return cb;
    }

    public static Button Button(string text, Action click, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(12, 5, 12, 5) };
        if (style != null && Application.Current?.TryFindResource(style) is Style st) b.Style = st;
        b.Click += (_, _) => click();
        return b;
    }

    public static UIElement Labeled(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }
}

/// <summary>Desktop Studio page «ویجت‌ها».</summary>
public static class WidgetsStudioPage
{
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var w = s.Widgets;
        var root = new StackPanel();
        var list = new StackPanel();

        void RefillList()
        {
            list.Children.Clear();
            if (w.Items.Count == 0) list.Children.Add(StudioUi.Muted("هنوز ویجتی اضافه نشده است."));
            foreach (var item in w.Items.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var del = StudioUi.Button("✕ حذف", () => { w.Items.Remove(item); RefillList(); changed(); }, "GhostButton");
                DockPanel.SetDock(del, System.Windows.Controls.Dock.Right);
                row.Children.Add(del);
                string extra = item.Kind == "note" && !string.IsNullOrWhiteSpace(item.Text) ? " — " + item.Text.Split('\n')[0].Trim() : "";
                if (extra.Length > 40) extra = extra[..40] + "…";
                row.Children.Add(new TextBlock { Text = WidgetViews.TitleOf(item.Kind) + extra, VerticalAlignment = VerticalAlignment.Center });
                list.Children.Add(row);
            }
        }

        // ---- general
        var general = new StackPanel();
        general.Children.Add(StudioUi.Title("ویجت‌های دسکتاپ"));
        general.Children.Add(StudioUi.Muted("ویجت‌ها پشت پنجره‌ها و روی تصویر زمینه می‌نشینند. با کشیدن جابه‌جا و با گوشه پایین اندازه‌شان را عوض کن؛ کلیک‌راست برای رنگ، قفل و حذف."));
        general.Children.Add(StudioUi.Toggle("نمایش ویجت‌ها", w.Enabled, v => w.Enabled = v, changed));
        general.Children.Add(StudioUi.Toggle("قفل موقعیت همه ویجت‌ها", w.LockPositions, v => w.LockPositions = v, changed));
        root.Children.Add(StudioUi.Card(general));

        // ---- add
        var add = new StackPanel();
        add.Children.Add(StudioUi.Title("افزودن ویجت"));
        var buttons = new WrapPanel();
        foreach (var (kind, title, _) in WidgetViews.Kinds)
        {
            string k = kind;
            buttons.Children.Add(StudioUi.Button("＋ " + title, () =>
            {
                var (dw, dh) = WidgetViews.DefaultSize(k);
                var wa = DesktopDisplay.RelativeWorkArea(s); // positions are relative to the chosen monitor
                int n = w.Items.Count;
                w.Items.Add(new WidgetPlacement
                {
                    Kind = k, Width = dw, Height = dh,
                    X = Math.Round(wa.Right - dw - 40 - (n % 3) * 30), Y = Math.Round(wa.Top + 40 + (n % 6) * 40),
                    ColorHex = k == "note" ? WidgetsModule.NoteColors[0] : null
                });
                w.Enabled = true;
                RefillList();
                changed();
            }, k == "calendar" ? "AccentButton" : null));
        }
        add.Children.Add(buttons);
        add.Children.Add(StudioUi.Title("ویجت‌های فعلی"));
        add.Children.Add(list);
        RefillList();
        root.Children.Add(StudioUi.Card(add));

        // ---- location
        var loc = new StackPanel();
        loc.Children.Add(StudioUi.Title("شهر، اوقات شرعی و آب‌وهوا"));
        var latBox = new TextBox { Text = w.Latitude.ToString("0.####", CultureInfo.InvariantCulture), Width = 110, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
        var lngBox = new TextBox { Text = w.Longitude.ToString("0.####", CultureInfo.InvariantCulture), Width = 110, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
        // Two-level picker: province / governorate → searchable county list.
        var current = WidgetCityData.Find(w.City);
        var selected = new TextBlock { Margin = new Thickness(0, 2, 0, 4), FontWeight = FontWeights.SemiBold, Text = "انتخاب فعلی: " + (w.City ?? "") };
        var province = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        province.Items.Add(new ComboBoxItem { Content = "همه استان‌ها و کشورها", Tag = null });
        foreach (var g in WidgetCityData.Groups) province.Items.Add(new ComboBoxItem { Content = g.Label, Tag = g });
        var search = new TextBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "بخشی از نام شهرستان یا شهر را بنویس" };
        var counties = new ListBox { Height = 190, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        ScrollViewer.SetHorizontalScrollBarVisibility(counties, ScrollBarVisibility.Disabled);
        var countLabel = StudioUi.Muted("");
        bool syncing = false;

        void FillCounties()
        {
            var group = (province.SelectedItem as ComboBoxItem)?.Tag as WidgetCityData.Group;
            string q = WidgetCityData.Normalize(search.Text);
            IEnumerable<WidgetCityData.Place> src = group?.Places ?? WidgetCityData.All;
            var hits = (q.Length == 0 ? src : src.Where(p => WidgetCityData.Normalize(p.Name).Contains(q))).ToList();
            if (group == null && q.Length == 0) hits = new(); // the full list (~700) only appears once something is typed
            syncing = true;
            counties.Items.Clear();
            foreach (var p in hits.Take(300))
                counties.Items.Add(new ListBoxItem { Content = group == null ? p.Display : p.Name, Tag = p });
            counties.SelectedItem = counties.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag is WidgetCityData.Place p && p.Display == w.City);
            if (counties.SelectedItem != null) counties.ScrollIntoView(counties.SelectedItem);
            syncing = false;
            countLabel.Text = group == null && q.Length == 0
                ? "یک استان را انتخاب کن یا نام شهرستان را بنویس."
                : $"{hits.Count.ToString(CultureInfo.InvariantCulture)} مورد" + (hits.Count > 300 ? " (۳۰۰ مورد اول نمایش داده شد)" : "");
            countLabel.Text = DesktopTheme.Digits(countLabel.Text, true);
        }

        province.SelectionChanged += (_, _) => FillCounties();
        search.TextChanged += (_, _) => FillCounties();
        counties.SelectionChanged += (_, _) =>
        {
            if (syncing || counties.SelectedItem is not ListBoxItem { Tag: WidgetCityData.Place p }) return;
            syncing = true;
            w.EyvanFile = null; w.ZoneId = null;
            w.City = p.Display; w.Latitude = p.Lat; w.Longitude = p.Lng;
            latBox.Text = p.Lat.ToString("0.####", CultureInfo.InvariantCulture);
            lngBox.Text = p.Lng.ToString("0.####", CultureInfo.InvariantCulture);
            selected.Text = "انتخاب فعلی: " + p.Display;
            syncing = false;
            changed();
        };
        province.SelectedItem = province.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is WidgetCityData.Group g && current != null && g.Places.Contains(current))
                                ?? province.Items[0];
        FillCounties();

        void CoordChanged()
        {
            if (syncing) return;
            if (double.TryParse(latBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var la) && la is >= -90 and <= 90 &&
                double.TryParse(lngBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) && lo is >= -180 and <= 180)
            {
                w.EyvanFile = null; w.ZoneId = null;
                w.Latitude = la; w.Longitude = lo;
                if (WidgetCityData.All.All(c => Math.Abs(c.Lat - la) > 0.0001 || Math.Abs(c.Lng - lo) > 0.0001)) w.City = "موقعیت دلخواه";
                selected.Text = "انتخاب فعلی: " + w.City;
                changed();
            }
        }
        latBox.TextChanged += (_, _) => CoordChanged();
        lngBox.TextChanged += (_, _) => CoordChanged();
        var source = StudioUi.Muted(w.EyvanFile == null ? "" : "منبع شهر و اذان بعدی: ایوان");
        var ip = new Button { Content = "تشخیص شهر با IP", ToolTip = "استعلام یک‌بارهٔ موقعیت تقریبی از ipwho.is" };
        ip.Click += async (_, _) =>
        {
            ip.IsEnabled = false;
            try
            {
                var place = await CityClock.LookupAsync();
                if (!ip.IsLoaded) return;
                syncing = true;
                w.City = place.City; w.Latitude = place.Lat; w.Longitude = place.Lng; w.ZoneId = place.Zone; w.EyvanFile = null;
                latBox.Text = place.Lat.ToString("0.####", CultureInfo.InvariantCulture);
                lngBox.Text = place.Lng.ToString("0.####", CultureInfo.InvariantCulture);
                selected.Text = "انتخاب فعلی: " + place.City;
                source.Text = "موقعیت تقریبی IP · " + place.Zone;
                changed();
            }
            catch { source.Text = "دریافت موقعیت ممکن نشد؛ شهر را دستی انتخاب کنید."; }
            finally { syncing = false; ip.IsEnabled = true; }
        };
        loc.Children.Add(ip);
        loc.Children.Add(StudioUi.Button("اتصال به افزونهٔ ایوان", () =>
        {
            try { source.Text = EyvanBridge.Register(); w.EyvanFile = EyvanBridge.Snapshot; changed(); }
            catch (Exception ex) { source.Text = ex.Message; }
        }));
        loc.Children.Add(StudioUi.Button("فایل ایوان…", () =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Eyvan JSON|*.json" };
            if (pick.ShowDialog() != true) return;
            var state = EyvanBridge.Read(pick.FileName);
            if (state == null) { source.Text = "فایل وضعیت رابط ایوان (schema 1) را انتخاب کنید."; return; }
            w.EyvanFile = pick.FileName; source.Text = "متصل به وضعیت ایوان · " + state.City; changed();
        }));
        loc.Children.Add(StudioUi.Button("قطع اتصال ایوان", () =>
        {
            EyvanBridge.Unregister(); w.EyvanFile = null; source.Text = "شهر دستی فعال است."; changed();
        }));
        loc.Children.Add(source);
        loc.Children.Add(selected);
        loc.Children.Add(StudioUi.Labeled("استان / کشور", province));
        loc.Children.Add(StudioUi.Labeled("جست‌وجوی شهرستان", search));
        loc.Children.Add(StudioUi.Labeled("شهرستان", counties));
        loc.Children.Add(countLabel);
        loc.Children.Add(StudioUi.Muted("یا مختصات دلخواه را دستی وارد کن:"));
        loc.Children.Add(StudioUi.Labeled("عرض جغرافیایی", latBox));
        loc.Children.Add(StudioUi.Labeled("طول جغرافیایی", lngBox));

        var method = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var m in PrayerTimes.Methods) method.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
        method.SelectedItem = method.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == PrayerTimes.Find(w.PrayerMethod).Id);
        method.SelectionChanged += (_, _) => { if (method.SelectedItem is ComboBoxItem { Tag: string id }) { w.PrayerMethod = id; changed(); } };
        loc.Children.Add(StudioUi.Labeled("روش محاسبه", method));

        loc.Children.Add(StudioUi.Toggle("دریافت آنلاین آب‌وهوا (Open-Meteo)", w.WeatherOnline, v => w.WeatherOnline = v, changed));
        loc.Children.Add(StudioUi.Muted("برای آب‌وهوا فقط مختصات شهر به api.open-meteo.com فرستاده می‌شود (بدون حساب و کلید). تقویم و اوقات شرعی کاملاً آفلاین محاسبه می‌شوند."));
        loc.Children.Add(StudioUi.Muted(PersianHolidays.LunarNote));
        loc.Children.Add(StudioUi.Muted(WidgetCityData.Attribution));
        root.Children.Add(StudioUi.Card(loc));

        // ---- time: system clock follows the IP (VPN), prayer times & weather follow Tehran
        var time = new StackPanel();
        time.Children.Add(StudioUi.Title("ساعت و تاریخ"));
        time.Children.Add(StudioUi.Muted("ساعت و تاریخ سیستم (نوار بالا، ساعت و تقویم) با کشور IP فعلی — مثلاً سرور فیلترشکن — هماهنگ می‌شود؛ اوقات شرعی و آب‌وهوای هر شهری که انتخاب کنی با ساعت انتخابی زیر (پیش‌فرض تهران) نمایش داده می‌شود."));
        var syncStatus = StudioUi.Muted(IpTimeZoneSync.Status);
        time.Children.Add(StudioUi.Toggle("هماهنگ‌سازی خودکار منطقهٔ زمانی ویندوز با IP", w.SyncSystemTimeZoneWithIp, v => w.SyncSystemTimeZoneWithIp = v, changed));
        time.Children.Add(StudioUi.Button("همگام‌سازی الان", async () =>
        {
            syncStatus.Text = "در حال بررسی IP…";
            await IpTimeZoneSync.SyncNowAsync(force: true);
            syncStatus.Text = IpTimeZoneSync.Status;
        }));
        time.Children.Add(syncStatus);
        var prayerZone = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (id, label) in new[]
        {
            ("Iran Standard Time", "ساعت تهران (پیشنهادی)"), ("Arabic Standard Time", "ساعت بغداد"), ("Afghanistan Standard Time", "ساعت کابل"),
            ("Arabian Standard Time", "ساعت دبی"), ("Turkey Standard Time", "ساعت استانبول"), (IpTimeZoneSync.SystemZoneToken, "ساعت سیستم (همان IP)"),
        })
            prayerZone.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        prayerZone.SelectedItem = prayerZone.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == w.PrayerWeatherZoneId) ?? prayerZone.Items[0];
        prayerZone.SelectionChanged += (_, _) => { if (prayerZone.SelectedItem is ComboBoxItem { Tag: string id }) { w.PrayerWeatherZoneId = id; changed(); } };
        time.Children.Add(StudioUi.Labeled("ساعت اوقات شرعی و آب‌وهوا", prayerZone));
        root.Children.Add(StudioUi.Card(time));

        var world = new StackPanel();
        world.Children.Add(StudioUi.Title("ساعت جهانی"));
        var zones = new ComboBox { MaxDropDownHeight = 320, ItemsSource = TimeZoneInfo.GetSystemTimeZones(),
            DisplayMemberPath = "DisplayName", IsTextSearchEnabled = true };
        var name = new TextBox { ToolTip = "نام کشور یا شهر" };
        var clocks = new StackPanel();
        void FillClocks()
        {
            clocks.Children.Clear();
            foreach (var (id, label) in w.WorldZones.ToArray())
                clocks.Children.Add(StudioUi.Button(label + "  ×", () => { w.WorldZones.Remove(id); FillClocks(); changed(); }));
        }
        world.Children.Add(StudioUi.Labeled("منطقهٔ زمانی", zones));
        world.Children.Add(StudioUi.Labeled("نام نمایشی", name));
        world.Children.Add(StudioUi.Button("افزودن ساعت", () =>
        {
            if (zones.SelectedItem is not TimeZoneInfo z) return;
            string id = TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id;
            w.WorldZones[id] = string.IsNullOrWhiteSpace(name.Text) ? z.DisplayName : name.Text.Trim();
            FillClocks(); changed();
        }));
        world.Children.Add(clocks);
        FillClocks();
        root.Children.Add(StudioUi.Card(world));
        return root;
    }
}
