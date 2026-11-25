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
                var wa = SystemParameters.WorkArea;
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
        var city = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var c in WidgetCities.All) city.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c });
        city.Items.Add(new ComboBoxItem { Content = "دستی (مختصات پایین)", Tag = null });
        city.SelectedItem = city.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is WidgetCities.City cc && cc.Name == w.City)
                            ?? city.Items.Cast<ComboBoxItem>().Last();
        bool syncing = false;
        city.SelectionChanged += (_, _) =>
        {
            if (city.SelectedItem is ComboBoxItem { Tag: WidgetCities.City c })
            {
                syncing = true;
                w.City = c.Name; w.Latitude = c.Lat; w.Longitude = c.Lng;
                latBox.Text = c.Lat.ToString("0.####", CultureInfo.InvariantCulture);
                lngBox.Text = c.Lng.ToString("0.####", CultureInfo.InvariantCulture);
                syncing = false;
                changed();
            }
        };
        void CoordChanged()
        {
            if (syncing) return;
            if (double.TryParse(latBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var la) && la is >= -90 and <= 90 &&
                double.TryParse(lngBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) && lo is >= -180 and <= 180)
            {
                w.Latitude = la; w.Longitude = lo;
                if (WidgetCities.All.All(c => Math.Abs(c.Lat - la) > 0.0001 || Math.Abs(c.Lng - lo) > 0.0001)) w.City = "موقعیت دلخواه";
                changed();
            }
        }
        latBox.TextChanged += (_, _) => CoordChanged();
        lngBox.TextChanged += (_, _) => CoordChanged();
        loc.Children.Add(StudioUi.Labeled("شهر", city));
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
        root.Children.Add(StudioUi.Card(loc));
        return root;
    }
}
