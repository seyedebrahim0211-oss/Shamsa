using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WDock = System.Windows.Controls.Dock;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Desktop;

/// <summary>
/// Stand-alone fallback window around <see cref="DesktopStudioView"/>. The main «نگارخانه» hub hosts the
/// same view directly; this window is only used when the hub is not available (very early start-up).
/// </summary>
public sealed class DesktopStudioWindow : Window
{
    private readonly DesktopStudioView _view = new();

    public DesktopStudioWindow()
    {
        Title = "استودیو دسکتاپ نگارخانه";
        Width = 980; Height = 680; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = FlowDirection.RightToLeft;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Shamsa.ico")); } catch { }
        Content = _view;
        Closed += (_, _) => _view.Release();
    }

    public void ShowPage(string? id) => _view.ShowPage(id);
}

/// <summary>
/// «استودیو دسکتاپ»: every desktop-environment page (top bar, dock, launcher, window manager, theme,
/// widgets, …) as one embeddable view. The hub owns navigation; this view builds the requested page,
/// edits one shared clone of desktop.json and saves it (debounced) so all modules apply live.
/// </summary>
public sealed class DesktopStudioView : UserControl
{
    private DesktopSettings _s;
    private readonly StackPanel _page = new();
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _saveTimer;
    private readonly TextBlock _status = new();
    private readonly Border _statusPill;
    private string _currentPage = "home";
    private bool _building;
    private bool _released;

    /// <summary>Page ids, Segoe Fluent glyphs and titles (the hub builds its «دسکتاپ» group from this list).</summary>
    public static readonly (string Id, string Glyph, string Title, string Keywords)[] Pages =
    {
        ("home", "", "شروع سریع دسکتاپ", "desktop seelen preset حالت آماده محیط دسکتاپ تسک‌بار taskbar"),
        ("theme", "", "ظاهر و فونت", "theme font color accent glass رنگ فونت شیشه"),
        ("topbar", "", "نوار بالا", "topbar bar clock date ساعت تاریخ شمسی نوار"),
        ("dock", "", "داک", "dock pin magnify پین داک بزرگ‌نمایی"),
        ("launcher", "", "جستجوی سریع", "launcher search spotlight جستجو لانچر"),
        ("wm", "", "مدیر پنجره‌ها", "window manager tile snap کاشی چسباندن پنجره"),
        ("rules", "", "قوانین و حالت‌های کاری", "rules layout profile قانون چیدمان حالت کاری"),
        ("widgets", "", "ویجت‌ها", "widgets calendar weather prayer تقویم اوقات شرعی آب‌وهوا ویجت"),
        ("fences", "", "گروه‌بندی دسکتاپ", "fences icons group آیکون گروه دسکتاپ"),
        ("controlcenter", "", "مرکز کنترل", "control center wifi bluetooth volume brightness وای‌فای صدا روشنایی"),
        ("autotheme", "", "تم خودکار روز و شب", "auto theme dark light sunset تیره روشن غروب"),
        ("clipboard", "", "تاریخچه کلیپ‌بورد", "clipboard history copy کپی کلیپ‌بورد"),
        ("voice", "", "فرمان صوتی فارسی", "voice speech microphone صوتی میکروفون"),
        ("plugins", "", "افزونه‌های نوار بالا", "plugins json افزونه"),
        ("hotkeys", "", "میانبرها و تداخل‌ها", "hotkeys shortcuts keyboard میانبر کلید"),
    };

    /// <summary>Raised whenever a page sets its title/subtitle (title null = the page draws its own heading).</summary>
    public event Action<string?, string>? HeaderChanged;

    public string CurrentPage => _currentPage;

    private static readonly string[] AccentSwatches = { "#14B8A6", "#3B82F6", "#8B5CF6", "#EC4899", "#F43F5E", "#F59E0B", "#22C55E", "#F6D379", "#E2E8F0" };
    private static readonly string[] TintSwatches = { "#0B1324", "#000000", "#111827", "#1E1B4B", "#052E2B", "#3B0764", "#1F2937", "#F8FAFC" };

    public DesktopStudioView()
    {
        _s = DesktopSettingsStore.Current.Clone();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); DesktopSettingsStore.Save(_s.Clone()); SetStatus("ذخیره و اعمال شد ✓"); };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        _status.FontSize = 12;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        _statusPill = new Border
        {
            Child = _status, Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1)
        };
        _statusPill.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _statusPill.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        root.Children.Add(_statusPill);

        _page.Margin = new Thickness(0, 0, 8, 24);
        _scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        Content = root;
        HotkeyRegistry.Changed += OnHotkeysChanged;
    }

    /// <summary>Flushes a pending save and detaches static event handlers (call when the host closes).</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        HotkeyRegistry.Changed -= OnHotkeysChanged;
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); DesktopSettingsStore.Save(_s.Clone()); }
    }

    /// <summary>Re-reads desktop.json (e.g. after «بازگردانی دسکتاپ ویندوز») and rebuilds the current page.</summary>
    public void ReloadFromStore(string? status = null)
    {
        _saveTimer.Stop();
        _s = DesktopSettingsStore.Current.Clone();
        ShowPage(_currentPage);
        if (status != null) SetStatus(status);
    }

    private void OnHotkeysChanged()
    {
        if (_currentPage == "hotkeys") Dispatcher.BeginInvoke(() => ShowPage("hotkeys"));
    }

    // ------------------------------------------------------------------ paging

    public static bool IsPage(string? id) => id != null && (id == "personalize" || Pages.Any(p => p.Id == id));

    public void ShowPage(string? id)
    {
        id ??= "home";
        if (!IsPage(id)) id = "home";
        // Pick up changes saved elsewhere (top-bar menus, auto theme, restore) unless an edit is pending.
        if (!_saveTimer.IsEnabled) _s = DesktopSettingsStore.Current.Clone();
        _currentPage = id;
        _building = true;
        try
        {
            _page.Children.Clear();
            switch (id)
            {
                case "home": BuildHome(); break;
                case "theme": BuildTheme(); break;
                case "topbar": BuildTopBar(); break;
                case "dock": BuildDock(); break;
                case "launcher": BuildLauncher(); break;
                case "wm": BuildWindowManager(); break;
                case "hotkeys": BuildHotkeys(); break;
                case "rules": External("قوانین و حالت‌های کاری", "برای هر برنامه قانون بگذار (شناور، همیشه رو، جای ثابت) و چیدمان پنجره‌ها را با یک کلید ذخیره و بازیابی کن.",
                    () => Rules.RulesStudioPage.Build(_s, Changed)); break;
                case "widgets": External("ویجت‌ها", "تقویم شمسی با تعطیلات، اوقات شرعی، آب‌وهوا، یادداشت و ساعت روی دسکتاپ.",
                    () => Widgets.WidgetsStudioPage.Build(_s, Changed)); break;
                case "fences": External("گروه‌بندی دسکتاپ", "جعبه‌های شیشه‌ای برای مرتب دیدن آیکون‌ها؛ آیکون‌های واقعی دسکتاپ حذف یا پنهان نمی‌شوند.",
                    () => Fences.FencesStudioPage.Build(_s, Changed)); break;
                case "controlcenter": External("مرکز کنترل", "وای‌فای، بلوتوث، روشنایی، صدا، رسانه، نور شب و مزاحم نشوید — از دکمه کنار ساعت نوار بالا یا منوی سینی.",
                    () => ControlCenter.ControlCenterStudioPage.Build(_s, Changed)); break;
                case "autotheme": External("تم خودکار روز و شب", "تم روشن و تیره ویندوز و نگارخانه در ساعت دلخواه یا با طلوع و غروب خورشید شهر شما عوض می‌شود.",
                    () => AutoTheme.AutoThemeStudioPage.Build(_s, Changed)); break;
                case "clipboard": External(null, "هر چه کپی می‌کنی در جستجوی سریع (Ctrl+Alt+V یا «cb ») در دسترس است؛ رمزهای مدیران رمز ذخیره نمی‌شوند.",
                    () => Launcher.ClipboardStudioPage.Build(_s, Changed)); break;
                case "voice": External(null, "با گفتن واژه بیداری (پیش‌فرض «نگار») و یک فرمان، برنامه باز کن، چیدمان را عوض کن یا اسکرین‌شات بگیر.",
                    () => Voice.VoiceStudioPage.Build(_s, Changed)); break;
                case "plugins": External("افزونه‌های نوار بالا", "فایل‌های JSON ساده در پوشه افزونه‌ها، اجزای تازه به نوار بالا اضافه می‌کنند.",
                    () => Plugins.PluginsStudioPage.Build(_s, Changed)); break;
                case "personalize": BuildPersonalize(); break;
            }
            _scroll.ScrollToTop();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _scroll.ScrollToTop());
        }
        finally { _building = false; }
    }

    /// <summary>
    /// «شخصی‌سازی ویندوز» (Desktop/Personalize): system icons, wallpaper, accent colour, cursors and theme packages.
    /// </summary>
    private void BuildPersonalize()
    {
        const string title = "شخصی‌سازی ویندوز";
        const string subtitle = "آیکون‌های دسکتاپ، «این رایانه»، سطل بازیافت، تصویر زمینه، نشانگر موس و ظاهر ویندوز — همه قابل برگشت.";
        // The personalisation page draws its own hero heading, so the host hides its title row.
        External(null, subtitle, () => Personalize.PersonalizeStudioPage.Build(_s, Changed));
    }

    private void Changed()
    {
        if (_building) return;
        SetStatus("در حال اعمال…");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _statusPill.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ pages

    private void BuildHome()
    {
        Header("شروع سریع دسکتاپ", "محیط دسکتاپ نگارخانه کاملاً اختیاری است؛ با یک کلید روشن می‌شود و با یک کلید همه‌چیز به حالت ویندوز برمی‌گردد.");

        var master = Card();
        master.Children.Add(Toggle("فعال‌سازی محیط دسکتاپ نگارخانه", "نوار بالا، داک، جستجوی سریع و مدیر پنجره طبق تنظیمات هر بخش اجرا می‌شوند",
            _s.Enabled, v => { _s.Enabled = v; ShowPageLater(); }));
        master.Children.Add(Toggle("پنهان کردن تسک‌بار ویندوز", "فقط وقتی نوار بالا یا داک نگارخانه روشن است؛ هنگام خروج، خطا یا «بازگردانی» حتماً برمی‌گردد",
            _s.HideWindowsTaskbar, v => _s.HideWindowsTaskbar = v));
        master.Children.Add(Muted("نمایشگر محیط دسکتاپ — گروه‌های آیکون، ویجت‌ها، داک و نوار بالا روی این نمایشگر قرار می‌گیرند (همان‌جا که آیکون‌های دسکتاپت هستند):"));
        master.Children.Add(DesktopDisplay.Picker(_s.DisplayDevice, v => { _s.DisplayDevice = v; Changed(); }));
        _page.Children.Add(Wrap(master));

        // ---- whole-desktop themes
        var themes = Card("تم‌های کامل دسکتاپ", "نوار بالا، داک، جستجو، ویجت‌ها، گروه‌های آیکون، آیکون‌های سیستمی ویندوز و ظاهر خود نگارخانه با یک کلیک هم‌رنگ می‌شوند.");
        bool gather = true, sysIcons = true, palette = true;
        var opts = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        CheckBox Opt(string text, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 2, 18, 2) };
            cb.Checked += (_, _) => set(true);
            cb.Unchecked += (_, _) => set(false);
            return cb;
        }
        opts.Children.Add(Opt("همه آیکون‌ها، برنامه‌ها و فایل‌های دسکتاپ داخل گروه‌های هم‌رنگ تم", gather, v => gather = v));
        opts.Children.Add(Opt("آیکون‌های سیستمی ویندوز (این رایانه، سطل بازیافت…)", sysIcons, v => sysIcons = v));
        opts.Children.Add(Opt("ظاهر پنجرهٔ نگارخانه", palette, v => palette = v));
        themes.Children.Add(opts);
        var gallery = new UniformGrid { Columns = 4 };
        gallery.SizeChanged += (_, e) => gallery.Columns = e.NewSize.Width < 560 ? 2 : e.NewSize.Width < 820 ? 3 : 4;
        foreach (var preset in DesktopThemePresets.All)
        {
            var p = preset;
            bool current = _s.ThemePresetId == p.Id;
            var card = new StackPanel();
            card.Children.Add(DesktopThemePresets.Swatch(p, current));
            card.Children.Add(new TextBlock { Text = p.Name + (current ? "  ✓" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            var d = Muted(p.Description); d.Margin = new Thickness(0);
            card.Children.Add(d);
            var b = new Button { Content = card, Margin = new Thickness(4), Padding = new Thickness(8), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "اعمال تم «" + p.Name + "»" };
            b.Click += (_, _) =>
            {
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    _s.Enabled = true;
                    DesktopThemePresets.Apply(_s, p, gather, sysIcons, palette);
                }
                finally { Mouse.OverrideCursor = null; }
                Changed();
                SetStatus("تم «" + p.Name + "» روی همه بخش‌ها اعمال شد");
                ShowPageLater();
            };
            gallery.Children.Add(b);
        }
        themes.Children.Add(gallery);
        _page.Children.Add(Wrap(themes));

        var presets = Card("چیدمان‌های آماده", "رفتار محیط دسکتاپ (بدون تغییر رنگ‌ها)؛ بعداً هر جزئیاتی را می‌توانی تغییر دهی.");
        var grid = new UniformGrid { Columns = 2 };
        grid.Children.Add(PresetButton("🖼 شیشه‌ای (Seelen)", "نوار بالای شیشه‌ای + داک + پنهان شدن تسک‌بار", s =>
        {
            s.TopBar.Enabled = true; s.TopBar.Position = "Top"; s.Dock.Enabled = true; s.Dock.Position = "Bottom";
            s.Dock.Magnification = 150; s.Launcher.Enabled = true; s.HideWindowsTaskbar = true; s.WindowManager.Enabled = false;
        }));
        grid.Children.Add(PresetButton("🍏 مک", "نوار بالا + داک با بزرگ‌نمایی زیاد", s =>
        {
            s.TopBar.Enabled = true; s.TopBar.Position = "Top"; s.TopBar.CenterItems = new() { "clock" };
            s.Dock.Enabled = true; s.Dock.Position = "Bottom"; s.Dock.Magnification = 185; s.Dock.IconSize = 52;
            s.Launcher.Enabled = true; s.HideWindowsTaskbar = true;
        }));
        grid.Children.Add(PresetButton("🧩 کاشی‌کاری حرفه‌ای", "نوار بالا + مدیر پنجره با چیدمان خودکار", s =>
        {
            s.TopBar.Enabled = true; s.Dock.Enabled = true; s.Launcher.Enabled = true;
            s.WindowManager.Enabled = true; s.WindowManager.AutoTile = true; s.WindowManager.Layout = "Bsp"; s.HideWindowsTaskbar = true;
        }));
        grid.Children.Add(PresetButton("🌿 مینیمال", "فقط جستجوی سریع و چسباندن پنجره‌ها؛ تسک‌بار ویندوز می‌ماند", s =>
        {
            s.TopBar.Enabled = false; s.Dock.Enabled = false; s.Launcher.Enabled = true;
            s.WindowManager.Enabled = true; s.WindowManager.AutoTile = false; s.HideWindowsTaskbar = false;
        }));
        presets.Children.Add(grid);
        _page.Children.Add(Wrap(presets));

        var modules = Card("وضعیت بخش‌ها");
        foreach (var m in DesktopFeature.AllModules)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var go = new Button { Content = "تنظیم", Padding = new Thickness(12, 3, 12, 3) };
            TryStyle(go, "GhostButton");
            string id = m.Id;
            go.Click += (_, _) => Go(id);
            DockPanel.SetDock(go, WDock.Right);
            row.Children.Add(go);
            var dot = new TextBlock { Text = "●", Margin = new Thickness(0, 0, 8, 0), Foreground = m.IsRunning ? Brushes.MediumSeaGreen : Brushes.Gray };
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = m.DisplayName + (m.IsRunning ? " — در حال اجرا" : " — خاموش"), VerticalAlignment = VerticalAlignment.Center });
            modules.Children.Add(row);
        }
        if (DesktopFeature.AllModules.Count == 0) modules.Children.Add(Muted("هیچ بخشی بارگذاری نشد."));
        _page.Children.Add(Wrap(modules));

        var tips = Card("نکته‌ها");
        tips.Children.Add(Muted("• جستجوی سریع: " + GlobalHotkeyService.FormatShortcut(_s.Launcher.HotkeyModifiers, _s.Launcher.HotkeyKey) +
                                "   • چسباندن پنجره: Ctrl+Alt+جهت‌ها   • کاشی‌کاری خودکار: Ctrl+Alt+T"));
        tips.Children.Add(Muted("• روی دکمه نگارخانه در نوار بالا کلیک‌راست کن تا مستقیم به این استودیو بیایی."));
        tips.Children.Add(Muted("• اگر چیزی درست نمایش داده نشد، «بازگردانی دسکتاپ ویندوز» همیشه در دسترس است (منوی سینی هم دارد)."));
        _page.Children.Add(Wrap(tips));
    }

    private void BuildTheme()
    {
        Header("ظاهر و فونت", "رنگ، شیشه و فونت همه بخش‌ها (نوار بالا، داک، جستجو) از اینجا می‌آید.");
        var c = Card("رنگ تأکیدی");
        c.Children.Add(Swatches(AccentSwatches, _s.Theme.AccentHex, hex => _s.Theme.AccentHex = hex));
        _page.Children.Add(Wrap(c));

        var g = Card("شیشه");
        g.Children.Add(Swatches(TintSwatches, _s.Theme.GlassTintHex, hex => _s.Theme.GlassTintHex = hex));
        g.Children.Add(SliderRow("تیرگی شیشه", 20, 100, _s.Theme.GlassOpacity, "{0}٪", v => _s.Theme.GlassOpacity = v));
        g.Children.Add(SliderRow("گردی گوشه‌ها", 0, 24, _s.Theme.CornerRadius, "{0}px", v => _s.Theme.CornerRadius = v));
        g.Children.Add(Toggle("محوی پشت شیشه (Acrylic)", "اگر کارت گرافیک ضعیف است خاموشش کن", _s.Theme.UseAcrylic, v => _s.Theme.UseAcrylic = v));
        g.Children.Add(Toggle("حاشیه روشن ظریف", null, _s.Theme.ShowBorders, v => _s.Theme.ShowBorders = v));
        _page.Children.Add(Wrap(g));

        var f = Card("فونت رابط کاربری", "وزیرمتن داخل برنامه قرار دارد و روی همه سیستم‌ها یکسان دیده می‌شود. تغییر فونت پس از اجرای دوباره نگارخانه کامل اعمال می‌شود.");
        var fonts = new List<(string Value, string Label)> { ("", "وزیرمتن (داخلی، پیشنهادی)") };
        string[] persianHints = { "Vazir", "IRAN", "Yekan", "Sahel", "Shabnam", "Samim", "Estedad", "Tanha", "Parastoo", "Lalezar", "Mikhak", "Dana", "Peyda", "Kalameh" };
        foreach (var ff in Fonts.SystemFontFamilies.Select(x => x.Source).Distinct().OrderBy(x => x))
            if (persianHints.Any(h => ff.Contains(h, StringComparison.OrdinalIgnoreCase))) fonts.Add((ff, ff));
        fonts.Add(("Segoe UI", "Segoe UI (پیش‌فرض ویندوز)"));
        fonts.Add(("Tahoma", "Tahoma"));
        var combo = Combo(fonts.Select(x => (x.Value, x.Label)).ToArray(), _s.UiFontFamily, v => _s.UiFontFamily = v);
        f.Children.Add(combo);
        var sample = new TextBlock { Text = "نمونه: نگارخانه، دسکتاپ به سلیقه تو — ۱۴۰۵/۰۹/۰۴ — Negarkhaneh", FontSize = 18, Margin = new Thickness(0, 10, 0, 0) };
        void UpdateSample()
        {
            try { sample.FontFamily = string.IsNullOrEmpty(_s.UiFontFamily) ? DesktopTheme.Font : new FontFamily(_s.UiFontFamily); } catch { }
        }
        UpdateSample();
        combo.SelectionChanged += (_, _) => UpdateSample();
        f.Children.Add(sample);
        _page.Children.Add(Wrap(f));
    }

    private void BuildTopBar()
    {
        var t = _s.TopBar;
        Header("نوار بالا", "نوار شیشه‌ای با فضای کاری، عنوان برنامه فعال، ساعت و تاریخ شمسی، پخش رسانه و وضعیت سیستم.");
        PreviewButton("topbar");
        var c = Card("رفتار");
        c.Children.Add(Toggle("نمایش نوار بالا", null, t.Enabled, v => t.Enabled = v));
        c.Children.Add(Labeled("محل", Combo(new[] { ("Top", "بالای صفحه"), ("Bottom", "پایین صفحه") }, t.Position, v => t.Position = v)));
        c.Children.Add(SliderRow("ارتفاع", 26, 56, t.Height, "{0}px", v => t.Height = v));
        c.Children.Add(Toggle("رزرو فضا", "پنجره‌های بزرگ‌شده زیر نوار نمی‌روند", t.ReserveSpace, v => t.ReserveSpace = v));
        c.Children.Add(Toggle("روی همه نمایشگرها", null, t.AllMonitors, v => t.AllMonitors = v));
        c.Children.Add(Toggle("پنهان در حالت تمام‌صفحه (بازی، فیلم، ارائه)", null, t.AutoHideOnFullscreen, v => t.AutoHideOnFullscreen = v));
        _page.Children.Add(Wrap(c));

        var items = Card("اجزای نوار");
        items.Children.Add(Toggle("فضاهای کاری (دسکتاپ‌های مجازی ویندوز)", null, t.ShowWorkspaces, v => { t.ShowWorkspaces = v; SetItem(t, "workspaces", v, t.LeftItems); }));
        items.Children.Add(Toggle("عنوان برنامه فعال", null, t.ShowFocusedTitle, v => { t.ShowFocusedTitle = v; SetItem(t, "title", v, t.LeftItems); }));
        items.Children.Add(Toggle("کنترل پخش رسانه", null, t.ShowMedia, v => { t.ShowMedia = v; SetItem(t, "media", v, t.RightItems); }));
        items.Children.Add(Toggle("مصرف پردازنده و حافظه", null, t.ShowSystemStats, v => { t.ShowSystemStats = v; SetItem(t, "stats", v, t.RightItems); }));
        items.Children.Add(Toggle("شبکه", null, t.ShowNetwork, v => { t.ShowNetwork = v; SetItem(t, "network", v, t.RightItems); }));
        items.Children.Add(Toggle("صدا (چرخ موس برای تغییر)", null, t.ShowVolume, v => { t.ShowVolume = v; SetItem(t, "volume", v, t.RightItems); }));
        items.Children.Add(Toggle("باتری", "روی رایانه‌های رومیزی خودکار پنهان است", t.ShowBattery, v => { t.ShowBattery = v; SetItem(t, "battery", v, t.RightItems); }));
        items.Children.Add(Labeled("جای ساعت", Combo(new[] { ("center", "وسط"), ("right", "سمت راست"), ("left", "سمت چپ") }, ClockSlot(t), v => MoveClock(t, v))));
        _page.Children.Add(Wrap(items));

        var clock = Card("ساعت و تاریخ");
        clock.Children.Add(Toggle("تاریخ شمسی", null, t.ShowPersianDate, v => t.ShowPersianDate = v));
        clock.Children.Add(Toggle("تاریخ میلادی", null, t.ShowGregorianDate, v => t.ShowGregorianDate = v));
        clock.Children.Add(Toggle("ثانیه", null, t.ShowSeconds, v => t.ShowSeconds = v));
        clock.Children.Add(Toggle("ساعت ۲۴ ساعته", null, t.Use24Hour, v => t.Use24Hour = v));
        clock.Children.Add(Toggle("ارقام فارسی (۱۲۳)", null, t.PersianDigits, v => t.PersianDigits = v));
        _page.Children.Add(Wrap(clock));
    }

    private static string ClockSlot(TopBarSettings t) =>
        t.CenterItems.Contains("clock") ? "center" : t.LeftItems.Contains("clock") ? "left" : "right";

    private static void MoveClock(TopBarSettings t, string slot)
    {
        t.LeftItems.Remove("clock"); t.CenterItems.Remove("clock"); t.RightItems.Remove("clock");
        switch (slot)
        {
            case "left": t.LeftItems.Add("clock"); break;
            case "right": t.RightItems.Insert(Math.Max(0, t.RightItems.Count - 1), "clock"); break;
            default: t.CenterItems.Insert(0, "clock"); break;
        }
    }

    private static void SetItem(TopBarSettings t, string id, bool on, List<string> home)
    {
        bool present = t.LeftItems.Contains(id) || t.CenterItems.Contains(id) || t.RightItems.Contains(id);
        if (on && !present) home.Add(id);
        if (!on) { t.LeftItems.Remove(id); t.CenterItems.Remove(id); t.RightItems.Remove(id); }
    }

    private void BuildDock()
    {
        var d = _s.Dock;
        Header("داک", "داک شیشه‌ای با آیکون واقعی برنامه‌ها، بزرگ‌نمایی، نشانگر اجرا و شمارش پنجره‌ها. فایل یا میانبر را روی داک بکش تا پین شود.");
        PreviewButton("dock");
        var c = Card("رفتار");
        c.Children.Add(Toggle("نمایش داک", null, d.Enabled, v => d.Enabled = v));
        c.Children.Add(Labeled("محل", Combo(new[] { ("Bottom", "پایین"), ("Left", "چپ"), ("Right", "راست"), ("Top", "بالا") }, d.Position, v => d.Position = v)));
        c.Children.Add(SliderRow("اندازه آیکون", 32, 80, d.IconSize, "{0}px", v => d.IconSize = v));
        c.Children.Add(SliderRow("بزرگ‌نمایی زیر موس", 100, 200, d.Magnification, "{0}٪", v => d.Magnification = v));
        c.Children.Add(Toggle("پنهان شدن خودکار", "با بردن موس به لبه صفحه ظاهر می‌شود", d.AutoHide, v => d.AutoHide = v));
        c.Children.Add(Toggle("پنهان در حالت تمام‌صفحه", null, d.HideOnFullscreen, v => d.HideOnFullscreen = v));
        c.Children.Add(Toggle("رزرو فضا", null, d.ReserveSpace, v => d.ReserveSpace = v));
        c.Children.Add(Toggle("نقطه برنامه‌های در حال اجرا", null, d.ShowRunningIndicators, v => d.ShowRunningIndicators = v));
        c.Children.Add(Toggle("شمارش پنجره‌ها", null, d.ShowWindowCount, v => d.ShowWindowCount = v));
        c.Children.Add(Toggle("نام برنامه هنگام اشاره", null, d.ShowLabelsOnHover, v => d.ShowLabelsOnHover = v));
        c.Children.Add(Toggle("گروه‌بندی پنجره‌های یک برنامه", null, d.GroupWindowsByApp, v => d.GroupWindowsByApp = v));
        c.Children.Add(Toggle("دانلودها و سطل بازیافت", null, d.ShowTrashAndFolders, v => d.ShowTrashAndFolders = v));
        try { c.Children.Add(Dock.DockStudioExtras.DockPreviewToggle(_s, Changed)); } catch (Exception ex) { AppLog.Warn("Dock preview toggle: " + ex.Message); }
        _page.Children.Add(Wrap(c));

        var pins = Card("موارد پین‌شده", "ترتیب را با فلش‌ها عوض کن.");
        var list = new StackPanel();
        void Refill()
        {
            list.Children.Clear();
            for (int i = 0; i < d.Pinned.Count; i++)
            {
                int idx = i;
                string item = d.Pinned[i];
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                actions.Children.Add(SmallButton("▲", () => { if (idx > 0) { (d.Pinned[idx - 1], d.Pinned[idx]) = (d.Pinned[idx], d.Pinned[idx - 1]); Refill(); Changed(); } }));
                actions.Children.Add(SmallButton("▼", () => { if (idx < d.Pinned.Count - 1) { (d.Pinned[idx + 1], d.Pinned[idx]) = (d.Pinned[idx], d.Pinned[idx + 1]); Refill(); Changed(); } }));
                actions.Children.Add(SmallButton("✕", () => { d.Pinned.RemoveAt(idx); Refill(); Changed(); }));
                DockPanel.SetDock(actions, WDock.Right);
                row.Children.Add(actions);
                var icon = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), Source = Shell.ShellInterop.GetAppIcon(item, 32) };
                row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = PinLabel(item), VerticalAlignment = VerticalAlignment.Center, ToolTip = item, TextTrimming = TextTrimming.CharacterEllipsis });
                list.Children.Add(row);
            }
            if (d.Pinned.Count == 0) list.Children.Add(Muted("چیزی پین نشده است."));
        }
        Refill();
        pins.Children.Add(list);
        var add = new Button { Content = "＋ افزودن برنامه یا میانبر…", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        TryStyle(add, "AccentButton");
        add.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "برنامه یا میانبر|*.exe;*.lnk;*.url;*.bat;*.cmd|همه فایل‌ها|*.*", DereferenceLinks = false, Multiselect = true };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                foreach (var f in dlg.FileNames) if (!d.Pinned.Contains(f, StringComparer.OrdinalIgnoreCase)) d.Pinned.Add(f);
                Refill(); Changed();
            }
        };
        pins.Children.Add(add);
        _page.Children.Add(Wrap(pins));
    }

    private static string PinLabel(string item)
    {
        if (item.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return "تنظیمات ویندوز";
        if (Directory.Exists(item)) return Path.GetFileName(item.TrimEnd('\\')) is { Length: > 0 } n ? n : item;
        if (item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Shell.ShellInterop.FriendlyAppName(item);
        return Path.GetFileNameWithoutExtension(item);
    }

    private void BuildLauncher()
    {
        var l = _s.Launcher;
        Header("جستجوی سریع", "مثل Spotlight: برنامه‌ها، تنظیمات ویندوز، فایل‌ها، ماشین‌حساب و جستجوی وب — با فارسی و انگلیسی.");
        PreviewButton("launcher");
        var c = Card("کلید میانبر");
        c.Children.Add(Toggle("فعال", null, l.Enabled, v => l.Enabled = v));
        c.Children.Add(Labeled("میانبر", HotkeyBox(l.HotkeyModifiers, l.HotkeyKey, "launcher.open", (m, k) => { l.HotkeyModifiers = m; l.HotkeyKey = k; })));
        var tryNow = new Button { Content = "امتحان کن", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        TryStyle(tryNow, "GhostButton");
        tryNow.Click += (_, _) => DesktopEvents.RequestLauncher();
        c.Children.Add(tryNow);
        _page.Children.Add(Wrap(c));

        var src = Card("منابع جستجو");
        src.Children.Add(Toggle("برنامه‌های منوی شروع", null, l.SearchApps, v => l.SearchApps = v));
        src.Children.Add(Toggle("صفحه‌های تنظیمات ویندوز", null, l.SearchSettings, v => l.SearchSettings = v));
        src.Children.Add(Toggle("فایل‌های اخیر و پوشه‌های کاربر", null, l.SearchFiles, v => l.SearchFiles = v));
        src.Children.Add(Toggle("ماشین‌حساب (مثلاً ۱۲*۸+۵)", null, l.Calculator, v => l.Calculator = v));
        src.Children.Add(Toggle("جستجوی وب وقتی نتیجه‌ای نیست", null, l.WebSearch, v => l.WebSearch = v));
        src.Children.Add(Labeled("نشانی جستجو", TextRow(l.WebSearchUrl, v => l.WebSearchUrl = v, ltr: true)));
        src.Children.Add(SliderRow("حداکثر نتیجه", 4, 15, l.MaxResults, "{0}", v => l.MaxResults = v));
        var clear = new Button { Content = $"پاک کردن تاریخچه ({l.History.Count})", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        TryStyle(clear, "GhostButton");
        clear.Click += (_, _) => { l.History.Clear(); clear.Content = "پاک کردن تاریخچه (0)"; Changed(); };
        src.Children.Add(clear);
        _page.Children.Add(Wrap(src));
    }

    private void BuildWindowManager()
    {
        var w = _s.WindowManager;
        Header("مدیر پنجره‌ها", "چسباندن پنجره به نیمه‌ها و ربع‌ها و کاشی‌کاری خودکار با فاصله‌های دلخواه. با خاموش کردن، جای پنجره‌ها برمی‌گردد.");
        PreviewButton("wm");
        var c = Card("رفتار");
        c.Children.Add(Toggle("فعال", null, w.Enabled, v => w.Enabled = v));
        c.Children.Add(Toggle("کاشی‌کاری خودکار پنجره‌های جدید", null, w.AutoTile, v => w.AutoTile = v));
        c.Children.Add(Labeled("چیدمان", Combo(new[] { ("Bsp", "مارپیچی (BSP)"), ("Columns", "ستونی"), ("Rows", "سطری"), ("MainStack", "اصلی + پشته"), ("Monocle", "تک‌پنجره") }, w.Layout, v => w.Layout = v)));
        c.Children.Add(SliderRow("فاصله بین پنجره‌ها", 0, 40, w.Gap, "{0}px", v => w.Gap = v));
        c.Children.Add(SliderRow("فاصله از لبه صفحه", 0, 60, w.OuterPadding, "{0}px", v => w.OuterPadding = v));
        c.Children.Add(Toggle("کلیدهای میانبر", null, w.HotkeysEnabled, v => w.HotkeysEnabled = v));
        c.Children.Add(Toggle("فوکوس با حرکت موس", null, w.FocusFollowsMouse, v => w.FocusFollowsMouse = v));
        c.Children.Add(Toggle("پنجره‌های گفتگو شناور بمانند", null, w.FloatDialogs, v => w.FloatDialogs = v));
        _page.Children.Add(Wrap(c));

        var ex = Card("برنامه‌های مستثنا", "هر خط نام یک فایل اجرایی، مثلاً Photoshop.exe");
        ex.Children.Add(TextRow(string.Join(Environment.NewLine, w.ExcludedProcesses), v =>
            w.ExcludedProcesses = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), ltr: true, multiline: true));
        _page.Children.Add(Wrap(ex));

        var keys = Card("راهنمای کلیدها");
        foreach (var (k, d) in new[]
        {
            ("Ctrl+Alt+← → ↑ ↓", "نیمه چپ/راست/بالا/پایین"), ("Ctrl+Alt+U / I / J / K", "ربع‌های صفحه"),
            ("Ctrl+Alt+Enter", "بزرگ با فاصله / بازگردانی"), ("Ctrl+Alt+C", "وسط صفحه"),
            ("Ctrl+Alt+T", "روشن/خاموش کاشی‌کاری خودکار"), ("Ctrl+Alt+L", "چیدمان بعدی"),
            ("Ctrl+Alt+F", "شناور کردن پنجره"), ("Ctrl+Alt+[ / ]", "پنجره قبلی/بعدی"), ("Ctrl+Alt+Shift+← →", "جابه‌جایی با همسایه"),
        })
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var key = new TextBlock { Text = k, Width = 190, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Right };
            key.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
            row.Children.Add(key);
            row.Children.Add(new TextBlock { Text = d, Margin = new Thickness(12, 0, 0, 0) });
            keys.Children.Add(row);
        }
        _page.Children.Add(Wrap(keys));
    }

    private void BuildHotkeys()
    {
        Header("میانبرها و تداخل‌ها", "همه کلیدهای سراسری نگارخانه یک‌جا. اگر دو بخش یک کلید را بخواهند، دومی ثبت نمی‌شود و اینجا با رنگ قرمز دیده می‌شود.");
        var c = Card();
        var all = HotkeyRegistry.All;
        if (all.Count == 0) c.Children.Add(Muted("هنوز میانبری ثبت نشده است."));
        foreach (var st in all)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var owner = new TextBlock { Text = st.Binding.Owner };
            owner.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            row.Children.Add(owner);
            var label = new TextBlock { Text = st.Binding.Label };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var keyText = new TextBlock { Text = st.Binding.Display, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Right };
            keyText.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
            Grid.SetColumn(keyText, 2); row.Children.Add(keyText);
            var (text, brush) = st.Result switch
            {
                HotkeyResult.Ok => ("✓ فعال", "SuccessBrush"),
                HotkeyResult.Disabled => ("خاموش", "MutedBrush"),
                HotkeyResult.ConflictInternal => ("⚠ " + st.Detail, "DangerBrush"),
                HotkeyResult.InUseByOtherApp => ("⚠ " + (st.Detail ?? "در حال استفاده"), "WarnBrush"),
                _ => ("نامعتبر", "DangerBrush"),
            };
            var status = new TextBlock { Text = text, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 260, TextWrapping = TextWrapping.Wrap };
            status.SetResourceReference(TextBlock.ForegroundProperty, brush);
            Grid.SetColumn(status, 3); row.Children.Add(status);
            c.Children.Add(row);
        }
        _page.Children.Add(Wrap(c));
        var editor = Card("تغییر میانبرهای مدیر پنجره‌ها");
        foreach (var (id, label, mods, key) in new[] {
            ("left", "نیمه چپ", 3u, 0x25),
            ("right", "نیمه راست", 3u, 0x27),
            ("up", "نیمه بالا", 3u, 0x26),
            ("down", "نیمه پایین", 3u, 0x28),
            ("tl", "ربع بالا-چپ", 3u, 0x55),
            ("tr", "ربع بالا-راست", 3u, 0x49),
            ("bl", "ربع پایین-چپ", 3u, 0x4A),
            ("br", "ربع پایین-راست", 3u, 0x4B),
            ("max", "تمام‌صفحه با فاصله / بازگردانی", 3u, 0x0D),
            ("center", "وسط صفحه (۷۰٪)", 3u, 0x43),
            ("tile", "روشن/خاموش چیدمان خودکار", 3u, 0x54),
            ("layout", "چیدمان بعدی", 3u, 0x4C),
            ("float", "شناور کردن پنجره", 3u, 0x46),
            ("prev", "پنجره قبلی", 3u, 0xDB),
            ("next", "پنجره بعدی", 3u, 0xDD),
            ("swapl", "جابه‌جایی با قبلی", 7u, 0x25),
            ("swapr", "جابه‌جایی با بعدی", 7u, 0x27)
        })
        {
            var value = _s.WindowManager.Hotkeys.TryGetValue(id, out var custom) && custom != null ? custom : new WindowHotkey(mods, key);
            editor.Children.Add(Labeled(label, HotkeyBox(value.Modifiers, value.Key, "wm." + id,
                (m, k) => _s.WindowManager.Hotkeys[id] = new WindowHotkey(m, k))));
        }
        editor.Children.Add(SmallButton("بازنشانی میانبرها", () => { _s.WindowManager.Hotkeys.Clear(); Changed(); ShowPageLater(); }));
        _page.Children.Add(Wrap(editor));
        var note = Card("پیشنهاد");
        note.Children.Add(Muted("از ترکیب‌های Alt+Shift و Ctrl+Shift تنها استفاده نکن؛ در ویندوز فارسی برای تغییر زبان صفحه‌کلید به کار می‌روند. کلیدهای Win+… هم بیشتر در اختیار خود ویندوز هستند."));
        note.Children.Add(Muted("میانبر منوی دایره‌ای و ضبط صفحه از داشبورد نگارخانه و پنجره ضبط تغییر می‌کنند؛ میانبر جستجوی سریع از همین استودیو."));
        _page.Children.Add(Wrap(note));
    }

    // ------------------------------------------------------------------ building blocks

    /// <summary>Hosts a page built by a feature module (its own controls edit <see cref="_s"/> and call Changed()).</summary>
    private void External(string? title, string subtitle, Func<UIElement> build)
    {
        // Pages that draw their own title pass null so the header is not shown twice.
        if (title != null) Header(title, subtitle);
        else HeaderChanged?.Invoke(null, subtitle);
        try { _page.Children.Add(build()); }
        catch (Exception ex)
        {
            AppLog.Error($"Studio page {title} failed", ex);
            _page.Children.Add(Muted("این صفحه بارگذاری نشد. جزئیات در گزارش برنامه ثبت شد."));
        }
    }

    private void ShowPageLater() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ShowPage(_currentPage));

    /// <summary>Asks the host to navigate (keeps the hub sidebar in sync); falls back to an in-place page switch.</summary>
    public event Action<string>? NavigateRequested;

    private void Go(string id)
    {
        if (NavigateRequested != null) NavigateRequested(id);
        else ShowPage(id);
    }

    private void Header(string title, string subtitle)
    {
        if (HeaderChanged != null) { HeaderChanged(title, subtitle); return; }
        _page.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        var sub = Muted(subtitle);
        sub.Margin = new Thickness(0, 0, 0, 14);
        _page.Children.Add(sub);
    }

    private void PreviewButton(string moduleId)
    {
        var module = DesktopFeature.Find(moduleId);
        if (module == null) return;
        var b = new Button { Content = "👁 پیش‌نمایش زنده", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        TryStyle(b, "GhostButton");
        b.Click += (_, _) =>
        {
            try
            {
                var w = module.CreatePreviewWindow(_s.Clone());
                if (w == null) return;
                w.Owner = Window.GetWindow(this);
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                w.Show();
            }
            catch (Exception ex) { AppLog.Error("Preview failed", ex); SetStatus("پیش‌نمایش باز نشد"); }
        };
        _page.Children.Add(b);
    }

    private static StackPanel Card(string? title = null, string? subtitle = null)
    {
        var p = new StackPanel();
        if (title != null) p.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, subtitle == null ? 8 : 2) });
        if (subtitle != null) { var m = Muted(subtitle); m.Margin = new Thickness(0, 0, 0, 8); p.Children.Add(m); }
        return p;
    }

    private static Border Wrap(StackPanel content)
    {
        var b = new Border { Child = content, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private UIElement Toggle(string label, string? hint, bool value, Action<bool> set)
    {
        var cb = new CheckBox { IsChecked = value, Margin = new Thickness(0, 5, 0, 5), VerticalContentAlignment = VerticalAlignment.Center };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (hint != null) text.Children.Add(Muted(hint));
        cb.Content = text;
        cb.Foreground = DesktopTheme.TextBrush;
        cb.Checked += (_, _) => { set(true); Changed(); };
        cb.Unchecked += (_, _) => { set(false); Changed(); };
        return cb;
    }

    private UIElement SliderRow(string label, int min, int max, int value, string format, Action<int> set)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1); g.Children.Add(slider);
        var val = new TextBlock { Text = DesktopTheme.Digits(string.Format(format, value), true), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        val.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
        Grid.SetColumn(val, 2); g.Children.Add(val);
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            val.Text = DesktopTheme.Digits(string.Format(format, v), true);
            set(v); Changed();
        };
        return g;
    }

    private ComboBox Combo((string Value, string Label)[] options, string current, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (v, l) in options) combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, current, StringComparison.OrdinalIgnoreCase))
                             ?? combo.Items.Cast<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string v }) { set(v); Changed(); }
        };
        return combo;
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private UIElement TextRow(string value, Action<string> set, bool ltr = false, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = value, Padding = new Thickness(6, 4, 6, 4), FlowDirection = ltr ? FlowDirection.LeftToRight : FlowDirection.RightToLeft,
            AcceptsReturn = multiline, MinHeight = multiline ? 90 : 0, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };
        box.TextChanged += (_, _) => { set(box.Text); Changed(); };
        return box;
    }

    private UIElement Swatches(string[] colors, string current, Action<string> set)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        var hex = new TextBox { Text = current, Width = 96, Margin = new Thickness(8, 4, 0, 4), FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Center };
        var buttons = new List<Border>();
        void Select(string h)
        {
            foreach (var b in buttons) b.BorderThickness = new Thickness(string.Equals((string)b.Tag, h, StringComparison.OrdinalIgnoreCase) ? 3 : 1);
        }
        foreach (var col in colors)
        {
            var sw = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(4), Cursor = Cursors.Hand, Tag = col,
                Background = new SolidColorBrush(DesktopTheme.ParseColor(col, Colors.Gray)), BorderBrush = Brushes.White, ToolTip = col
            };
            sw.MouseLeftButtonUp += (_, _) => { set(col); hex.Text = col; Select(col); Changed(); };
            buttons.Add(sw);
            panel.Children.Add(sw);
        }
        hex.TextChanged += (_, _) =>
        {
            string h = hex.Text.Trim();
            if (h.Length is 7 or 9 && h.StartsWith('#') && DesktopTheme.ParseColor(h, Colors.Transparent) != Colors.Transparent)
            {
                set(h); Select(h); Changed();
            }
        };
        panel.Children.Add(hex);
        Select(current);
        return panel;
    }

    private UIElement HotkeyBox(uint mods, int key, string bindingId, Action<uint, int> set)
    {
        var stack = new StackPanel();
        var box = new TextBox
        {
            Text = GlobalHotkeyService.FormatShortcut(mods, key), IsReadOnly = true, Width = 220, HorizontalAlignment = HorizontalAlignment.Left,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Thickness(6, 4, 6, 4), ToolTip = "کلیک کن و ترکیب جدید را فشار بده"
        };
        var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            uint m = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) m |= NativeMethods.MOD_CONTROL;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) m |= NativeMethods.MOD_ALT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) m |= NativeMethods.MOD_SHIFT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) m |= NativeMethods.MOD_WIN;
            if (m == 0 && k != Key.Escape) { warn.Text = "حداقل یک کلید Ctrl / Alt / Win لازم است."; return; }
            if (k == Key.Escape && m == 0) return;
            int vk = KeyInterop.VirtualKeyFromKey(k);
            box.Text = GlobalHotkeyService.FormatShortcut(m, vk);
            string? conflict = HotkeyRegistry.FindConflict(m, vk, bindingId);
            bool layoutSwitch = (m & ~NativeMethods.MOD_SHIFT) is NativeMethods.MOD_ALT or NativeMethods.MOD_CONTROL && (m & NativeMethods.MOD_SHIFT) != 0 && vk is 0 or 0x10;
            warn.Text = conflict != null ? "⚠ تداخل با " + conflict + " — ترکیب دیگری انتخاب کن."
                       : (m & NativeMethods.MOD_WIN) != 0 ? "توجه: بسیاری از کلیدهای Win در اختیار ویندوز هستند."
                       : layoutSwitch ? "این ترکیب ممکن است زبان صفحه‌کلید را عوض کند." : "";
            if (conflict == null) { set(m, vk); Changed(); }
        };
        stack.Children.Add(box);
        stack.Children.Add(warn);
        return stack;
    }

    private Button PresetButton(string title, string description, Action<DesktopSettings> apply)
    {
        var content = new StackPanel { Margin = new Thickness(4) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        content.Children.Add(Muted(description));
        var b = new Button { Content = content, Margin = new Thickness(4), Padding = new Thickness(12, 10, 12, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        b.Click += (_, _) =>
        {
            // A preset starts from stable shell defaults even after switching from auto-tiling.
            _s.TopBar.AllMonitors = false;
            _s.WindowManager.AutoTile = false;
            _s.WindowManager.Enabled = false;
            _s.Dock.Magnification = 145;
            _s.TopBar.Position = "Top";
            // Layout presets keep the colours of the chosen theme (they used to reset them to the default).
            apply(_s);
            _s.Enabled = true;
            Changed();
            SetStatus("حالت «" + title + "» اعمال شد");
            ShowPageLater();
        };
        return b;
    }

    private static Button SmallButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(2, 0, 2, 0), MinWidth = 0 };
        TryStyle(b, "GhostButton");
        b.Click += (_, _) => click();
        return b;
    }

    private static void TryStyle(FrameworkElement e, string key)
    {
        if (Application.Current?.TryFindResource(key) is Style s && s.TargetType.IsInstanceOfType(e)) e.Style = s;
    }
}
