using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shamsa.Desktop.AutoTheme;
using Shamsa.Services;
using Windows.Devices.Radios;

namespace Shamsa.Desktop.ControlCenter;

/// <summary>Glass quick-settings flyout: radios, night light, focus, auto theme, brightness, volume, media.</summary>
internal sealed class ControlCenterWindow : Window
{
    private static readonly FontFamily IconFont = StudioUi.IconFont;
    private readonly DesktopSettings _s;
    private readonly bool _preview;
    private readonly StackPanel _root = new();
    private bool _closing;

    public ControlCenterWindow(DesktopSettings settings, bool preview)
    {
        _s = settings;
        _preview = preview;
        Title = preview ? "Shamsa — پیش‌نمایش مرکز کنترل" : "Shamsa Control Center";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = preview;
        Topmost = !preview;
        SizeToContent = SizeToContent.Height;
        Width = 372;
        FontFamily = DesktopTheme.Font;
        FlowDirection = FlowDirection.RightToLeft;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;

        var card = new Border
        {
            Child = _root,
            Margin = new Thickness(10),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(Math.Max(10, settings.Theme.CornerRadius + 4)),
            Background = DesktopTheme.GlassBrush(settings, 1.25),
            BorderBrush = DesktopTheme.BorderBrush(settings),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
        };
        Content = card;
        Build();

        if (!preview)
        {
            Deactivated += (_, _) => SafeClose();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
            Loaded += (_, _) => { PlaceNearBar(); AnimateIn(); };
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { }
    }

    private void AnimateIn()
    {
        Opacity = 0;
        var tt = new TranslateTransform(0, IsBarAtBottom ? 12 : -12);
        ((FrameworkElement)Content).RenderTransform = tt;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private bool IsBarAtBottom => _s.TopBar.Enabled && string.Equals(_s.TopBar.Position, "Bottom", StringComparison.OrdinalIgnoreCase);

    /// <summary>Under the top bar (work area excludes the AppBar), at the screen's right edge on the cursor's monitor.</summary>
    private void PlaceNearBar()
    {
        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(cursor);
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
            var wa = screen.WorkingArea;
            double right = wa.Right * sx, top = wa.Top * sy, bottom = wa.Bottom * sy;
            Left = right - ActualWidth - 4;
            Top = IsBarAtBottom ? bottom - ActualHeight - 4 : top + 2;
        }
        catch (Exception ex) { AppLog.Warn("Control center placement: " + ex.Message); }
    }

    // ------------------------------------------------------------------ content

    private void Build()
    {
        var cc = _s.ControlCenter;
        _root.Children.Clear();

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
        var gear = IconButton("", "استودیو دسکتاپ", () => { SafeClose(); DesktopEvents.RequestStudio("controlcenter"); });
        DockPanel.SetDock(gear, System.Windows.Controls.Dock.Left);
        header.Children.Add(gear);
        header.Children.Add(new TextBlock { Text = "مرکز کنترل", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center });
        _root.Children.Add(header);

        var tiles = new UniformGrid { Columns = 3 };
        if (cc.ShowWifi) tiles.Children.Add(RadioTile(RadioKind.WiFi, "", "وای‌فای", "ms-settings:network-wifi"));
        if (cc.ShowBluetooth) tiles.Children.Add(RadioTile(RadioKind.Bluetooth, "", "بلوتوث", "ms-settings:bluetooth"));
        if (cc.ShowNightLight)
            tiles.Children.Add(Tile("", "نور شب", "تنظیمات", false,
                () => ControlCenterSystem.Open("ms-settings:nightlight"),
                "ویندوز برای روشن/خاموش کردن نور شب رابط عمومی ندارد؛ صفحه تنظیمات آن باز می‌شود."));
        if (cc.ShowFocus)
            tiles.Children.Add(Tile("", "مزاحم نشوید", "تنظیمات", false,
                () => ControlCenterSystem.Open("ms-settings:quiethours"),
                "حالت تمرکز / مزاحم نشوید از تنظیمات اعلان‌های ویندوز کنترل می‌شود؛ صفحه آن باز می‌شود."));
        if (cc.ShowAutoTheme) tiles.Children.Add(AutoThemeTile());
        tiles.Children.Add(Tile("", "قفل", "فوری", false, () => { SafeClose(); ControlCenterSystem.Lock(); }, "قفل کردن رایانه (Win+L)"));
        _root.Children.Add(tiles);

        if (cc.ShowBrightness) _root.Children.Add(BrightnessRow());
        if (cc.ShowVolume) _root.Children.Add(VolumeRow());
        if (cc.ShowMedia) _root.Children.Add(MediaCard());

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(PillButton("", "تنظیمات ویندوز", () => { SafeClose(); ControlCenterSystem.Open("ms-settings:"); }));
        footer.Children.Add(PillButton("", "استودیو دسکتاپ", () => { SafeClose(); DesktopEvents.RequestStudio(null); }));
        footer.Children.Add(PillButton("", "اعلان‌ها", () => { SafeClose(); ControlCenterSystem.Open("ms-actioncenter:"); }));
        _root.Children.Add(footer);
    }

    private Border Tile(string glyph, string title, string state, bool active, Action click, string? tip = null, Action? rightClick = null)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 18, Foreground = DesktopTheme.TextBrush, HorizontalAlignment = HorizontalAlignment.Center };
        var t = new TextBlock { Text = title, FontSize = 12.5, Foreground = DesktopTheme.TextBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var st = new TextBlock { Text = state, FontSize = 10.5, Foreground = DesktopTheme.MutedBrush, HorizontalAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(icon); panel.Children.Add(t); panel.Children.Add(st);
        var b = new Border
        {
            Child = panel, Height = 82, Margin = new Thickness(4), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = DesktopTheme.BorderBrush(_s), Tag = (icon, st)
        };
        SetActive(b, active);
        if (tip != null) b.ToolTip = new ToolTip { Content = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 }, FlowDirection = FlowDirection.RightToLeft };
        b.MouseEnter += (_, _) => b.Opacity = 0.88;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { click(); } catch (Exception ex) { AppLog.Warn("CC tile: " + ex.Message); } };
        if (rightClick != null) b.MouseRightButtonUp += (_, e) => { e.Handled = true; rightClick(); };
        return b;
    }

    private void SetActive(Border tile, bool active)
    {
        tile.Background = active ? DesktopTheme.AccentBrush(_s) : DesktopTheme.HoverBrush;
    }

    private static void SetState(Border tile, string text)
    {
        if (tile.Tag is (TextBlock, TextBlock st)) st.Text = text;
    }

    private Border RadioTile(RadioKind kind, string glyph, string title, string settingsUri)
    {
        bool on = false, available = false, busy = false;
        Border tile = null!;
        tile = Tile(glyph, title, "…", false, async () =>
        {
            if (busy) return;
            if (!available) { ControlCenterSystem.Open(settingsUri); return; }
            busy = true;
            SetState(tile, "…");
            bool ok = await ControlCenterSystem.SetRadioAsync(kind, !on);
            var info = await ControlCenterSystem.GetRadioAsync(kind);
            on = info.On; available = info.Available;
            SetActive(tile, on);
            SetState(tile, !ok ? "دسترسی نیست" : on ? "روشن" : "خاموش");
            busy = false;
        }, "کلیک: روشن/خاموش • کلیک راست: تنظیمات", () => { SafeClose(); ControlCenterSystem.Open(settingsUri); });
        _ = Task.Run(async () =>
        {
            var info = await ControlCenterSystem.GetRadioAsync(kind);
            Dispatcher.BeginInvoke(() =>
            {
                on = info.On; available = info.Available;
                SetActive(tile, on);
                SetState(tile, !available ? "در دسترس نیست" : on ? "روشن" : "خاموش");
            });
        });
        return tile;
    }

    private Border AutoThemeTile()
    {
        bool light = AutoThemeModule.IsLightNow(DesktopSettingsStore.Current);
        Border tile = null!;
        tile = Tile(light ? "" : "", "تم روشن/تیره", ModeText(light), DesktopSettingsStore.Current.AutoTheme.Enabled, () =>
        {
            AutoThemeModule.Toggle();
            bool now = AutoThemeModule.IsLightNow(DesktopSettingsStore.Current);
            if (tile.Tag is (TextBlock icon, TextBlock _)) icon.Text = now ? "" : "";
            SetState(tile, ModeText(now));
        }, "کلیک: جابه‌جایی فوری روشن/تیره (تم خودکار تا تغییر بعدی زمان‌بندی کنار می‌رود)\nکلیک راست: تنظیمات تم خودکار",
        () => { SafeClose(); DesktopEvents.RequestStudio("autotheme"); });
        return tile;

        static string ModeText(bool l) => l ? "روشن" : "تیره";
    }

    private UIElement SliderRow(string glyph, string tip, out Slider slider, out TextBlock icon)
    {
        var g = new Grid { Margin = new Thickness(4, 10, 4, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 17, Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = tip };
        g.Children.Add(icon);
        slider = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, FlowDirection = FlowDirection.LeftToRight };
        Grid.SetColumn(slider, 1);
        g.Children.Add(slider);
        return g;
    }

    private UIElement BrightnessRow()
    {
        var row = SliderRow("", "روشنایی صفحه", out var slider, out var icon);
        slider.IsEnabled = false;
        slider.ToolTip = "در حال خواندن روشنایی…";
        bool ready = false;
        var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            int v = (int)slider.Value;
            _ = Task.Run(() => ControlCenterSystem.SetBrightness(v));
        };
        slider.ValueChanged += (_, _) => { if (ready) { debounce.Stop(); debounce.Start(); } };
        icon.MouseLeftButtonUp += (_, _) => { SafeClose(); ControlCenterSystem.Open("ms-settings:display"); };
        _ = Task.Run(() =>
        {
            int b = ControlCenterSystem.GetBrightness();
            string? backend = ControlCenterSystem.BrightnessBackend;
            Dispatcher.BeginInvoke(() =>
            {
                if (b < 0)
                {
                    slider.IsEnabled = false;
                    slider.ToolTip = "کنترل روشنایی این نمایشگر در دسترس نیست (DDC/CI خاموش است یا پشتیبانی نمی‌شود) — روی آیکون بزنید تا تنظیمات نمایشگر باز شود";
                    icon.Opacity = 0.6;
                    return;
                }
                slider.Value = b;
                slider.IsEnabled = true;
                slider.ToolTip = backend == "wmi" ? "روشنایی صفحه داخلی" : "روشنایی نمایشگر (DDC/CI)";
                ready = true;
            });
        });
        return row;
    }

    private UIElement VolumeRow()
    {
        var row = SliderRow("", "کلیک: بی‌صدا", out var slider, out var icon);
        var (ok, level, muted) = ControlCenterSystem.GetVolume();
        if (!ok) { slider.IsEnabled = false; return row; }
        slider.Value = Math.Round(level * 100);
        void UpdateIcon(bool m, double v) => icon.Text = m || v == 0 ? "" : v < 33 ? "" : v < 66 ? "" : "";
        UpdateIcon(muted, slider.Value);
        slider.ValueChanged += (_, e) => { ControlCenterSystem.SetVolume((float)(e.NewValue / 100)); UpdateIcon(false, e.NewValue); };
        icon.MouseLeftButtonUp += (_, _) =>
        {
            ControlCenterSystem.ToggleMute();
            var now = ControlCenterSystem.GetVolume();
            UpdateIcon(now.Muted, now.Level * 100);
        };
        return row;
    }

    private UIElement MediaCard()
    {
        var title = new TextBlock { FontSize = 13, Foreground = DesktopTheme.TextBrush, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
        var artist = new TextBlock { FontSize = 11.5, Foreground = DesktopTheme.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(title); info.Children.Add(artist);
        TextBlock play = null!;
        var card = new Border { Margin = new Thickness(4, 12, 4, 0), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(12), Background = DesktopTheme.HoverBrush, Visibility = Visibility.Collapsed };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
        buttons.Children.Add(IconButton("", "قبلی", () => Cmd("prev")));
        var playBtn = IconButton("", "پخش / مکث", () => Cmd("play"));
        play = (TextBlock)playBtn.Child;
        buttons.Children.Add(playBtn);
        buttons.Children.Add(IconButton("", "بعدی", () => Cmd("next")));
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Left);
        dock.Children.Add(buttons);
        dock.Children.Add(info);
        card.Child = dock;

        async void Refresh()
        {
            var m = await ControlCenterSystem.GetMediaAsync();
            card.Visibility = m.Has ? Visibility.Visible : Visibility.Collapsed;
            title.Text = m.Title;
            artist.Text = m.Artist;
            artist.Visibility = string.IsNullOrWhiteSpace(m.Artist) ? Visibility.Collapsed : Visibility.Visible;
            play.Text = m.Playing ? "" : "";
        }
        async void Cmd(string c)
        {
            await ControlCenterSystem.MediaCommandAsync(c);
            await Task.Delay(300);
            Refresh();
        }
        Refresh();
        return card;
    }

    private Border IconButton(string glyph, string tip, Action click)
    {
        var b = new Border
        {
            Child = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15, Foreground = DesktopTheme.TextBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip
        };
        b.MouseEnter += (_, _) => b.Background = DesktopTheme.HoverBrush;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { click(); } catch (Exception ex) { AppLog.Warn("CC button: " + ex.Message); } };
        return b;
    }

    private Border PillButton(string glyph, string text, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Child = row, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(14), Background = DesktopTheme.HoverBrush, Cursor = Cursors.Hand };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { click(); } catch (Exception ex) { AppLog.Warn("CC pill: " + ex.Message); } };
        return b;
    }
}
