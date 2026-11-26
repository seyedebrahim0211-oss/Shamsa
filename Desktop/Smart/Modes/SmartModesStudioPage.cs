using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shamsa.Desktop.ControlCenter;
using static Shamsa.Desktop.ControlCenter.StudioUi;

namespace Shamsa.Desktop.Smart.Modes;

/// <summary>«حالت‌های هوشمند» page: one-click modes, wallpaper by time of day and reminders. Advanced options sit under «بیشتر».</summary>
public static class SmartModesStudioPage
{
    public static UIElement Build(Action changed)
    {
        var host = new ContentControl();
        var live = new List<Action>();
        string Signature() => SmartModesStore.Data.Active is { } a ? a.Mode + "|" + a.Trigger + "|" + a.Until : "";
        string shown = "";
        void Changed() { SmartModesStore.Save(); changed(); }
        void Render()
        {
            live.Clear();
            shown = Signature();
            host.Content = BuildPage(Changed, Render, live);
        }
        Render();

        // A mode switched on/off (also by its trigger) redraws the tiles; otherwise only the live texts
        // (countdown) change, so open «بیشتر» panels and text boxes are left alone.
        void OnState() => host.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!host.IsLoaded) return;
            if (Signature() != shown) Render();
            else foreach (var a in live) a();
        }));
        host.Loaded += (_, _) => SmartModesModule.StateChanged += OnState;
        host.Unloaded += (_, _) => SmartModesModule.StateChanged -= OnState;
        return host;
    }

    private static UIElement BuildPage(Action changed, Action rerender, List<Action> live)
    {
        var data = SmartModesStore.Data;
        var page = Page();

        // ---------------------------------------------------------------- modes
        var modesCard = Body(Card(page, "حالت‌های هوشمند", "با یک کلیک همه‌چیز برای کار، فیلم یا نماز آماده می‌شود. با خاموش کردن حالت، تنظیمات ویندوز دقیقاً به حالت قبل برمی‌گردد."));
        var tiles = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var mode in data.Modes)
        {
            var m = mode;
            bool on = data.Active?.Mode == m.Name;
            tiles.Children.Add(ModeTile(m, on, () =>
            {
                if (on) SmartModesModule.Deactivate();
                else SmartModesModule.Activate(m.Name);
                rerender();
            }));
        }
        modesCard.Children.Add(tiles);
        modesCard.Children.Add(Muted(data.Active == null
            ? "هیچ حالتی روشن نیست."
            : $"«{data.Active.Mode}» روشن است{TriggerNote(data.Active)}. برای خاموش کردن دوباره روی آن بزنید."));

        foreach (var mode in data.Modes)
        {
            var m = mode;
            var more = More(modesCard, "تنظیمات «" + m.Name + "»");
            more.Children.Add(Toggle("بی‌صدا کردن اعلان‌های ویندوز", null, m.QuietNotifications, v => m.QuietNotifications = v, changed));
            more.Children.Add(Toggle("مکث آهنگ یا ویدیوی در حال پخش", null, m.PauseMedia, v => m.PauseMedia = v, changed));
            more.Children.Add(Labeled("تم ویندوز", Combo(new[] { ("", "بدون تغییر"), ("dark", "تیره"), ("light", "روشن") }, m.Theme, v => m.Theme = v, changed)));
            var layouts = new List<(string, string)> { ("", "بدون چیدمان") };
            layouts.AddRange(DesktopSettingsStore.Current.Layouts.Select(p => (p.Name, p.Name)));
            more.Children.Add(Labeled("چیدمان پنجره‌ها", Combo(layouts.ToArray(), m.LayoutProfile, v => m.LayoutProfile = v, changed)));

            var auto = new TextBlock { Text = "روشن شدن خودکار", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2), Foreground = DesktopTheme.TextBrush };
            more.Children.Add(auto);
            var t = m.Triggers;
            more.Children.Add(Toggle("وقتی یکی از این برنامه‌ها باز است", "نام برنامه‌ها با ویرگول، مثل WINWORD, EXCEL, Code", t.OnApps, v => t.OnApps = v, changed));
            more.Children.Add(ListBox(t.Apps, changed));
            more.Children.Add(Toggle("وقتی ویدیو تمام‌صفحه پخش می‌شود", "فقط برای برنامه‌های زیر؛ خالی بگذارید تا هر برنامه تمام‌صفحه حساب شود", t.OnFullscreen, v => t.OnFullscreen = v, changed));
            more.Children.Add(ListBox(t.FullscreenApps, changed));
            more.Children.Add(Toggle("چند دقیقه پیش از اذان", $"بر اساس شهر ابزارک اوقات شرعی ({DesktopSettingsStore.Current.Widgets.City})؛ پس از نماز خودکار خاموش می‌شود", t.BeforePrayer, v => t.BeforePrayer = v, changed));
            more.Children.Add(Labeled("مدت پس از اذان (دقیقه)", NumberBox(t.PrayerDurationMinutes, v => t.PrayerDurationMinutes = v, changed)));
            if (!m.BuiltIn)
                more.Children.Add(StudioUi.Button("حذف این حالت", () =>
                {
                    if (data.Active?.Mode == m.Name) SmartModesModule.Deactivate();
                    data.Modes.Remove(m);
                    changed();
                    rerender();
                }));
        }

        var add = More(modesCard, "ساخت حالت تازه");
        var nameBox = new TextBox { Width = 200, Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Left, FlowDirection = FlowDirection.RightToLeft };
        add.Children.Add(Labeled("نام حالت", nameBox));
        add.Children.Add(StudioUi.Button("افزودن", () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0 || SmartModesStore.Find(name) != null) return;
            data.Modes.Add(new SmartMode { Name = name });
            changed();
            rerender();
        }, "AccentButton"));

        // ---------------------------------------------------------------- wallpaper
        var w = data.Wallpaper;
        var wallCard = Body(Card(page, "پس‌زمینه بر اساس ساعت روز", "برای صبح، روز، عصر و شب یک عکس یا یک پوشه (هر بار یک عکس تصادفی) انتخاب کنید."));
        wallCard.Children.Add(Toggle("روشن", null, w.Enabled, v => { w.Enabled = v; if (v) data.LastWallpaperSlot = ""; }, changed));
        if (TimeOfDayWallpaper.SlideshowConflict)
            wallCard.Children.Add(Warning("نمایش اسلایدی یا عکس روز Bing در «شخصی‌سازی ویندوز» روشن است؛ تا وقتی روشن است پس‌زمینه ساعتی کاری نمی‌کند."));
        string current = TimeOfDayWallpaper.CurrentSlot(w);
        foreach (var (id, title, _) in TimeOfDayWallpaper.Slots)
            wallCard.Children.Add(SlotRow(w, id, title + (id == current ? "  (اکنون)" : ""), changed, rerender));
        var wallMore = More(wallCard, "بیشتر");
        wallMore.Children.Add(Toggle("بر اساس طلوع و غروب آفتاب شهر", "صبح از طلوع تا سه ساعت بعد، عصر از یک ساعت پیش تا یک ساعت پس از غروب", w.FollowSun, v => w.FollowSun = v, changed));
        wallMore.Children.Add(Toggle("رنگ تأکیدی ویندوز از رنگ عکس", null, w.AccentFromWallpaper, v => w.AccentFromWallpaper = v, changed));
        wallMore.Children.Add(StudioUi.Button("همین حالا اعمال کن", () => { TimeOfDayWallpaper.Tick(data, force: true); changed(); }));

        // ---------------------------------------------------------------- reminders
        var r = data.Reminders;
        var remCard = Body(Card(page, "یادآورها", "آرام و بی‌مزاحمت؛ هنگام تماشای ویدیو تمام‌صفحه یادآور استراحت نشان داده نمی‌شود."));
        remCard.Children.Add(Toggle("یادآور استراحت", $"پس از {DesktopTheme.Digits(r.BreakAfterMinutes.ToString(), true)} دقیقه کار پیوسته", r.BreakReminder, v => r.BreakReminder = v, changed));
        remCard.Children.Add(Toggle("مناسبت‌ها و تعطیلی فردا", "هر روز یک بار، اگر فردا مناسبتی باشد", r.Occasions, v => r.Occasions = v, changed));
        remCard.Children.Add(Toggle("یادآوری پیش از اذان", $"{DesktopTheme.Digits(r.PrayerNoticeMinutes.ToString(), true)} دقیقه قبل، به وقت {DesktopSettingsStore.Current.Widgets.City}", r.PrayerNotice, v => r.PrayerNotice = v, changed));
        remCard.Children.Add(Toggle("شمارش معکوس تا اذان در نوار بالا", null, r.PrayerCountdown, v => r.PrayerCountdown = v, changed));
        var countdown = Muted("");
        void UpdateCountdown()
        {
            string text = SmartModesModule.NextPrayerText;
            countdown.Text = "اکنون: " + text;
            countdown.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateCountdown();
        live.Add(UpdateCountdown);
        remCard.Children.Add(countdown);
        var remMore = More(remCard, "بیشتر");
        remMore.Children.Add(Labeled("استراحت پس از (دقیقه)", NumberBox(r.BreakAfterMinutes, v => r.BreakAfterMinutes = Math.Clamp(v, 10, 240), changed)));
        remMore.Children.Add(Labeled("یادآوری اذان (دقیقه قبل)", NumberBox(r.PrayerNoticeMinutes, v => r.PrayerNoticeMinutes = Math.Clamp(v, 1, 60), changed)));
        remMore.Children.Add(Muted("زمان‌های «حالت نماز» هم از همین عدد استفاده می‌کند. " + Widgets.PersianHolidays.LunarNote));

        return page;
    }

    // ---------------------------------------------------------------- pieces

    private static string TriggerNote(ActiveModeState a) => a.Trigger switch
    {
        "app" => " (خودکار، به خاطر برنامه باز)",
        "fullscreen" => " (خودکار، ویدیو تمام‌صفحه)",
        "prayer" => a.Until is { } u ? $" (تا ساعت {PrayerClock.Clock(u)})" : " (وقت نماز)",
        _ => a.Until is { } until ? $" (تا ساعت {PrayerClock.Clock(until)})" : ""
    };

    private static Border ModeTile(SmartMode m, bool on, Action click)
    {
        var s = DesktopSettingsStore.Current;
        var glyph = new TextBlock { Text = m.Glyph, FontFamily = IconFont, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, Foreground = on ? Brushes.White : DesktopTheme.AccentBrush(s) };
        var name = new TextBlock { Text = m.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var state = new TextBlock { Text = on ? "روشن" : "خاموش", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        if (on) { name.Foreground = Brushes.White; state.Foreground = Brushes.White; }
        else { name.Foreground = DesktopTheme.TextBrush; state.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); }
        var stack = new StackPanel();
        stack.Children.Add(glyph); stack.Children.Add(name); stack.Children.Add(state);
        var tile = new Border
        {
            Child = stack, Width = 128, Padding = new Thickness(10, 12, 10, 10), Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            ToolTip = on ? "برای خاموش کردن بزنید" : "برای روشن کردن بزنید",
            Background = on ? DesktopTheme.AccentBrush(s) : Brushes.Transparent
        };
        tile.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        tile.MouseEnter += (_, _) => { if (!on) tile.Background = DesktopTheme.HoverBrush; };
        tile.MouseLeave += (_, _) => { if (!on) tile.Background = Brushes.Transparent; };
        tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        AutomationProperties(tile, m.Name);
        return tile;
    }

    private static void AutomationProperties(UIElement e, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(e, name);

    /// <summary>A quiet «بیشتر ▾» link that shows/hides the panel it returns (closed by default).</summary>
    private static StackPanel More(StackPanel parent, string label)
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(14, 2, 0, 8) };
        var arrow = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 0, 0) };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Margin = new Thickness(0, 6, 0, 2), Background = Brushes.Transparent };
        row.Children.Add(text); row.Children.Add(arrow);
        var accent = DesktopTheme.AccentBrush(DesktopSettingsStore.Current);
        text.Foreground = accent;
        arrow.Foreground = accent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            bool open = panel.Visibility != Visibility.Visible;
            panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = open ? "" : "";
        };
        parent.Children.Add(row);
        parent.Children.Add(panel);
        return panel;
    }

    private static TextBlock Warning(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 6),
        Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
    };

    private static TextBox ListBox(List<string> items, Action changed)
    {
        var box = StudioUi.Text(string.Join(", ", items), v =>
        {
            items.Clear();
            items.AddRange(v.Split(new[] { ',', '،', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SystemToggles.CleanProcessName).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        }, changed, ltr: true, width: 360);
        box.Margin = new Thickness(28, 0, 0, 6);
        return box;
    }

    private static TextBox NumberBox(int value, Action<int> set, Action changed) =>
        StudioUi.Text(value.ToString(), v => { if (int.TryParse(v.Trim(), out int n) && n > 0) set(n); }, changed, ltr: true, width: 70);

    private static UIElement SlotRow(TimeWallpaperSettings w, string slot, string title, Action changed, Action rerender)
    {
        string value = TimeOfDayWallpaper.Get(w, slot);
        var path = new TextBlock
        {
            Text = value.Length == 0 ? "انتخاب نشده" : Path.GetFileName(value.TrimEnd('\\', '/')) + (Directory.Exists(value) ? "  (پوشه)" : ""),
            ToolTip = value.Length == 0 ? null : value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 230, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 0, 10, 0)
        };
        if (value.Length == 0) path.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        else path.Foreground = DesktopTheme.TextBrush;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(path);
        buttons.Children.Add(Small("عکس", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "عکس پس‌زمینه برای " + title,
                Filter = "تصاویر|" + string.Join(";", Personalize.WallpaperService.ImageExtensions.Select(e => "*" + e))
            };
            if (dlg.ShowDialog() == true) { TimeOfDayWallpaper.Set(w, slot, dlg.FileName); changed(); rerender(); }
        }));
        buttons.Children.Add(Small("پوشه", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "پوشه عکس‌ها برای " + title };
            if (dlg.ShowDialog() == true) { TimeOfDayWallpaper.Set(w, slot, dlg.FolderName); changed(); rerender(); }
        }));
        if (value.Length > 0)
            buttons.Children.Add(Small("پاک", () => { TimeOfDayWallpaper.Set(w, slot, ""); changed(); rerender(); }));
        return Labeled(title, buttons);
    }

    private static Button Small(string text, Action click)
    {
        var b = StudioUi.Button(text, click);
        b.Margin = new Thickness(0, 0, 6, 0);
        b.Padding = new Thickness(10, 3, 10, 3);
        return b;
    }
}
