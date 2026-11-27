using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.TopBar;

/// <summary>The bar's visual content: three item groups (left, centre, right) bound to a <see cref="TopBarState"/>.</summary>
internal sealed class TopBarView : Border
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly TopBarState _state;
    private DesktopSettings _s;
    private readonly List<Action> _updaters = new();
    private Popup? _calendarPopup;

    public TopBarView(DesktopSettings settings, TopBarState state)
    {
        _s = settings;
        _state = state;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Build();
        _state.Changed += Refresh;
        Plugins.TopBarPluginHost.Instance.Changed += OnPluginsChanged;
        Unloaded += (_, _) => { _state.Changed -= Refresh; Plugins.TopBarPluginHost.Instance.Changed -= OnPluginsChanged; };
        Loaded += (_, _) =>
        {
            _state.Changed -= Refresh; _state.Changed += Refresh;
            Plugins.TopBarPluginHost.Instance.Changed -= OnPluginsChanged; Plugins.TopBarPluginHost.Instance.Changed += OnPluginsChanged;
            Refresh();
        };
    }

    private int _pluginCount = -1;

    /// <summary>Plugin set changed (loaded/removed) → rebuild slots; plain text updates are picked up by Refresh.</summary>
    private void OnPluginsChanged()
    {
        int count = Plugins.TopBarPluginHost.Instance.Active.Count;
        if (count != _pluginCount) { _pluginCount = count; Build(); }
        Refresh();
    }

    public void ApplySettings(DesktopSettings settings)
    {
        _s = settings;
        Build();
        Refresh();
    }

    public void Detach()
    {
        _state.Changed -= Refresh;
        Plugins.TopBarPluginHost.Instance.Changed -= OnPluginsChanged;
    }

    // ------------------------------------------------------------------ layout

    private void Build()
    {
        _updaters.Clear();
        var t = _s.TopBar;
        bool top = !string.Equals(t.Position, "Bottom", StringComparison.OrdinalIgnoreCase);
        double r = Math.Min(_s.Theme.CornerRadius, t.Height / 2.0);
        Background = DesktopTheme.GlassBrush(_s);
        BorderBrush = DesktopTheme.BorderBrush(_s);
        BorderThickness = top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
        // Corners only on the inner edge so the bar sits flush against the screen edge.
        CornerRadius = top ? new CornerRadius(0, 0, r * 0.5, r * 0.5) : new CornerRadius(r * 0.5, r * 0.5, 0, 0);
        Height = t.Height;

        var grid = new Grid { FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(6, 0, 6, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var (left, center, right) = EffectiveItems();
        grid.Children.Add(Group(left, HorizontalAlignment.Left, 0));
        grid.Children.Add(Group(center, HorizontalAlignment.Center, 1));
        grid.Children.Add(Group(right, HorizontalAlignment.Right, 2));
        Child = grid;
    }

    /// <summary>
    /// The configured item lists plus items that must appear even if desktop.json predates them:
    /// the control-center button (next to "tray") and the plugin slots for each position that has plugins.
    /// </summary>
    private (List<string> Left, List<string> Center, List<string> Right) EffectiveItems()
    {
        var t = _s.TopBar;
        var left = t.LeftItems.Select(Norm).ToList();
        var center = t.CenterItems.Select(Norm).ToList();
        var right = t.RightItems.Select(Norm).ToList();
        bool Has(string id) => left.Contains(id) || center.Contains(id) || right.Contains(id);

        if (_s.ControlCenter.Enabled && !Has("controlcenter"))
        {
            int tray = right.IndexOf("tray");
            if (tray >= 0) right.Insert(tray, "controlcenter"); else right.Add("controlcenter");
        }
        if (_s.Plugins.Enabled && !Has("plugins"))
        {
            var positions = Plugins.TopBarPluginHost.Instance.Active.Select(p => p.Definition.Position).ToHashSet();
            if (positions.Contains("left")) left.Add("plugins");
            if (positions.Contains("center")) center.Add("plugins");
            if (positions.Contains("right")) right.Insert(0, "plugins");
        }
        return (left, center, right);

        static string Norm(string id) => id.Trim().ToLowerInvariant();
    }

    private StackPanel Group(IEnumerable<string> ids, HorizontalAlignment align, int column)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(panel, column);
        string position = column switch { 0 => "left", 1 => "center", _ => "right" };
        foreach (var id in ids)
        {
            FrameworkElement? item = null;
            try { item = id == "plugins" ? PluginsItem(position) : CreateItem(id); }
            catch (Exception ex) { AppLog.Warn($"TopBar item {id}: {ex.Message}"); }
            if (item != null) panel.Children.Add(item);
        }
        return panel;
    }

    private FrameworkElement? CreateItem(string id) => id switch
    {
        "controlcenter" when _s.ControlCenter.Enabled => ControlCenterItem(),
        "launcher" => LauncherItem(),
        "workspaces" when _s.TopBar.ShowWorkspaces => WorkspacesItem(),
        "title" when _s.TopBar.ShowFocusedTitle => TitleItem(),
        "clock" => ClockItem(),
        "media" when _s.TopBar.ShowMedia => MediaItem(),
        "stats" when _s.TopBar.ShowSystemStats => StatsItem(),
        "network" when _s.TopBar.ShowNetwork => NetworkItem(),
        "volume" when _s.TopBar.ShowVolume => VolumeItem(),
        "battery" when _s.TopBar.ShowBattery => BatteryItem(),
        "tray" => TrayItem(),
        _ => null
    };

    public void Refresh()
    {
        foreach (var u in _updaters)
        {
            try { u(); } catch (Exception ex) { AppLog.Warn("TopBar refresh: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ building blocks

    private double FontSz => Math.Clamp(_s.TopBar.Height * 0.38, 11, 17);
    private double IconSz => Math.Clamp(_s.TopBar.Height * 0.42, 12, 20);

    private TextBlock Text(string text = "", bool muted = false) => new()
    {
        Text = text,
        FontFamily = DesktopTheme.Font,
        FontSize = FontSz,
        Foreground = muted ? DesktopTheme.MutedBrush : DesktopTheme.TextBrush,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private TextBlock Icon(string glyph, double? size = null) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size ?? IconSz,
        Foreground = DesktopTheme.TextBrush,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    /// <summary>Hoverable, clickable pill that never takes keyboard focus.</summary>
    private Border Chip(UIElement content, Action? click = null, string? tooltip = null, Action? rightClick = null)
    {
        var b = new Border
        {
            Child = content,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(1, 3, 1, 3),
            CornerRadius = new CornerRadius(Math.Min(_s.Theme.CornerRadius, 10)),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = click != null ? Cursors.Hand : null,
            Focusable = false
        };
        if (tooltip != null) SetTip(b, tooltip);
        b.MouseEnter += (_, _) => b.Background = DesktopTheme.HoverBrush;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        if (click != null)
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Run(click); };
        if (rightClick != null)
            b.MouseRightButtonUp += (_, e) => { e.Handled = true; Run(rightClick); };
        return b;
    }

    private static void SetTip(FrameworkElement e, string text) =>
        e.ToolTip = new ToolTip
        {
            Content = new TextBlock { Text = text, FontFamily = DesktopTheme.Font, FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
            FlowDirection = FlowDirection.RightToLeft
        };

    private static void Run(Action a)
    {
        try { a(); } catch (Exception ex) { AppLog.Warn("TopBar action: " + ex.Message); }
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private string D(string s) => DesktopTheme.Digits(s, _s.TopBar.PersianDigits);

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn($"TopBar open {target}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ items

    private FrameworkElement LauncherItem()
    {
        UIElement content;
        try
        {
            var img = new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Shamsa;component/Assets/Shamsa-logo.png")),
                Width = IconSz + 4, Height = IconSz + 4
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            content = img;
        }
        catch { content = Icon(""); }
        return Chip(content, DesktopEvents.RequestLauncher,
            "جستجو و اجرای برنامه‌ها\nکلیک راست: تنظیمات نوار بالا",
            () => DesktopEvents.RequestStudio("topbar"));
    }

    private FrameworkElement WorkspacesItem()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var host = Chip(panel, null, "میزکارهای مجازی ویندوز — برای رفتن به هر میزکار کلیک کنید");
        host.Padding = new Thickness(6, 0, 6, 0);
        int lastCount = -1, lastIndex = -1;
        _updaters.Add(() =>
        {
            int count = Math.Clamp(_state.WorkspaceCount, 1, 12), index = _state.WorkspaceIndex;
            if (count == lastCount && index == lastIndex) return;
            lastCount = count; lastIndex = index;
            panel.Children.Clear();
            var accent = DesktopTheme.AccentBrush(_s);
            for (int i = 0; i < count; i++)
            {
                int target = i;
                bool current = i == index;
                var dot = new Border
                {
                    Height = Math.Max(8, _s.TopBar.Height * 0.32),
                    MinWidth = current ? Math.Max(22, _s.TopBar.Height * 0.7) : Math.Max(8, _s.TopBar.Height * 0.32),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(2.5, 0, 2.5, 0),
                    Background = current ? accent : DesktopTheme.HoverBrush,
                    BorderBrush = DesktopTheme.MutedBrush,
                    BorderThickness = new Thickness(current ? 0 : 1),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = current && count > 1
                        ? new TextBlock { Text = D((i + 1).ToString()), FontSize = Math.Max(9, FontSz - 3), FontFamily = DesktopTheme.Font, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, -1, 5, 0) }
                        : null
                };
                SetTip(dot, D($"میزکار {i + 1}"));
                dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; _state.SwitchWorkspace(target); };
                panel.Children.Add(dot);
            }
        });
        return host;
    }

    private FrameworkElement TitleItem()
    {
        var img = new Image { Width = IconSz + 2, Height = IconSz + 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = Text();
        text.MaxWidth = 380;
        var row = Row(img, text);
        var chip = Chip(row, () =>
        {
            if (_state.FocusedHwnd != IntPtr.Zero) ShellInterop.Activate(_state.FocusedHwnd);
        });
        string? lastExe = "\0";
        _updaters.Add(() =>
        {
            string title = _state.FocusedTitle;
            if (title.Length > 70) title = title[..67] + "…";
            text.Text = title;
            // RTL titles (Persian apps) read naturally right-to-left.
            text.FlowDirection = HasRtl(title) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            if (lastExe != _state.FocusedExe)
            {
                lastExe = _state.FocusedExe;
                img.Source = ShellInterop.GetAppIcon(_state.FocusedExe, 32);
                img.Visibility = img.Source == null ? Visibility.Collapsed : Visibility.Visible;
                if (_state.FocusedExe != null) SetTip(chip, ShellInterop.FriendlyAppName(_state.FocusedExe));
            }
            chip.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        });
        return chip;
    }

    private static bool HasRtl(string s)
    {
        foreach (char c in s)
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ') return true;
        return false;
    }

    private FrameworkElement ClockItem()
    {
        var time = Text();
        time.FontWeight = FontWeights.SemiBold;
        var date = Text(muted: true);
        date.Margin = new Thickness(8, 0, 0, 0);
        date.FlowDirection = FlowDirection.RightToLeft;
        var greg = Text(muted: true);
        greg.Margin = new Thickness(8, 0, 0, 0);
        var row = Row(time, date, greg);
        Border chip = null!;
        chip = Chip(row, () => ToggleCalendar(chip));
        _updaters.Add(() =>
        {
            var now = _state.Now;
            var t = _s.TopBar;
            string fmt = t.Use24Hour ? (t.ShowSeconds ? "HH:mm:ss" : "HH:mm") : (t.ShowSeconds ? "h:mm:ss" : "h:mm");
            string clock = now.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            if (!t.Use24Hour) clock += now.Hour < 12 ? " ق.ظ" : " ب.ظ";
            time.Text = D(clock);
            date.Visibility = t.ShowPersianDate ? Visibility.Visible : Visibility.Collapsed;
            date.Text = PersianDate.Long(now, t.PersianDigits);
            greg.Visibility = t.ShowGregorianDate ? Visibility.Visible : Visibility.Collapsed;
            greg.Text = now.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
            SetTipOnce(chip, now);
        });
        return chip;
    }

    private DateTime _tipDay;
    private void SetTipOnce(FrameworkElement chip, DateTime now)
    {
        if (_tipDay == now.Date) return;
        _tipDay = now.Date;
        SetTip(chip, PersianDate.Long(now, _s.TopBar.PersianDigits) + "\n" +
                     now.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + "\nبرای تقویم کلیک کنید");
    }

    private void ToggleCalendar(FrameworkElement anchor)
    {
        if (_calendarPopup is { IsOpen: true }) { _calendarPopup.IsOpen = false; return; }
        bool top = !string.Equals(_s.TopBar.Position, "Bottom", StringComparison.OrdinalIgnoreCase);
        _calendarPopup = new Popup
        {
            PlacementTarget = anchor,
            Placement = top ? PlacementMode.Bottom : PlacementMode.Top,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = top ? 6 : -6,
            Child = new PersianCalendarCard(_s, Widgets.CityClock.Now(_s.Widgets).Date)
        };
        _calendarPopup.IsOpen = true;
    }

    private FrameworkElement MediaItem()
    {
        var title = Text();
        title.MaxWidth = 220;
        var prev = Icon("", IconSz - 3);
        var play = Icon("", IconSz - 2);
        var next = Icon("", IconSz - 3);
        Border Btn(TextBlock g, string cmd, string tip)
        {
            var b = Chip(g, () => _state.MediaCommand(cmd), tip);
            b.Padding = new Thickness(5, 0, 5, 0);
            b.Margin = new Thickness(0, 4, 0, 4);
            return b;
        }
        var musicIcon = Icon("", IconSz - 2);
        musicIcon.Margin = new Thickness(0, 0, 6, 0);
        musicIcon.Foreground = DesktopTheme.AccentBrush(_s);
        var info = Row(musicIcon, title);
        var row = Row(info, Btn(prev, "prev", "قبلی"), Btn(play, "play", "پخش / مکث"), Btn(next, "next", "بعدی"));
        var host = new Border { Child = row, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Stretch };
        _updaters.Add(() =>
        {
            host.Visibility = _state.HasMedia ? Visibility.Visible : Visibility.Collapsed;
            if (!_state.HasMedia) return;
            string label = string.IsNullOrWhiteSpace(_state.MediaArtist) ? _state.MediaTitle : $"{_state.MediaTitle} — {_state.MediaArtist}";
            title.Text = label;
            title.FlowDirection = HasRtl(label) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            play.Text = _state.MediaPlaying ? "" : "";
            SetTip(info, label);
        });
        return host;
    }

    private FrameworkElement StatsItem()
    {
        var cpuIcon = Text("CPU", muted: true);
        cpuIcon.FontSize = Math.Max(9, FontSz - 3);
        var cpu = Text();
        cpu.Margin = new Thickness(4, 0, 10, 0);
        var ramIcon = Text("RAM", muted: true);
        ramIcon.FontSize = Math.Max(9, FontSz - 3);
        var ram = Text();
        ram.Margin = new Thickness(4, 0, 0, 0);
        var chip = Chip(Row(cpuIcon, cpu, ramIcon, ram), () => Open("taskmgr.exe"));
        _updaters.Add(() =>
        {
            cpu.Text = _state.CpuPercent < 0 ? "—" : D($"{_state.CpuPercent:0}%");
            ram.Text = _state.RamPercent < 0 ? "—" : D($"{_state.RamPercent:0}%");
            var warn = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            cpu.Foreground = _state.CpuPercent >= 85 ? warn : DesktopTheme.TextBrush;
            ram.Foreground = _state.RamPercent >= 85 ? warn : DesktopTheme.TextBrush;
            chip.ToolTip = null;
            SetTip(chip, D($"پردازنده: {Math.Max(0, _state.CpuPercent):0}٪\nحافظه: {_state.RamUsedGb:0.0} از {_state.RamTotalGb:0.0} گیگابایت\nکلیک: مدیر وظایف"));
        });
        return chip;
    }

    private FrameworkElement NetworkItem()
    {
        var icon = Icon("");
        var chip = Chip(icon, () => Open("ms-settings:network"));
        string last = "";
        _updaters.Add(() =>
        {
            string key = _state.NetKind + "|" + _state.NetName;
            if (key == last) return;
            last = key;
            (icon.Text, string label) = _state.NetKind switch
            {
                "wifi" => ("", "وای‌فای"),
                "ethernet" => ("", "اترنت"),
                "other" => ("", "شبکه"),
                _ => ("", "بدون اتصال")
            };
            icon.Opacity = _state.NetKind == "none" ? 0.6 : 1;
            SetTip(chip, string.IsNullOrEmpty(_state.NetName) ? label + "\nکلیک: تنظیمات شبکه" : $"{label}: {_state.NetName}\nکلیک: تنظیمات شبکه");
        });
        return chip;
    }

    private FrameworkElement VolumeItem()
    {
        var icon = Icon("");
        var pct = Text();
        pct.Margin = new Thickness(4, 0, 0, 0);
        pct.Visibility = Visibility.Collapsed;
        var chip = Chip(Row(icon, pct), _state.ToggleMute, null, () => Open("ms-settings:sound"));
        chip.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            _state.SetVolume(_state.Volume + (e.Delta > 0 ? 0.02f : -0.02f));
            pct.Visibility = Visibility.Visible;
        };
        chip.MouseLeave += (_, _) => pct.Visibility = Visibility.Collapsed;
        _updaters.Add(() =>
        {
            chip.Visibility = _state.HasAudio ? Visibility.Visible : Visibility.Collapsed;
            if (!_state.HasAudio) return;
            int p = (int)Math.Round(_state.Volume * 100);
            icon.Text = _state.Muted || p == 0 ? "" : p < 33 ? "" : p < 66 ? "" : "";
            pct.Text = D($"{p}%");
            SetTip(chip, D(_state.Muted ? $"بی‌صدا ({p}٪)" : $"صدا: {p}٪") + "\nچرخ موس: کم و زیاد • کلیک: بی‌صدا • کلیک راست: تنظیمات صدا");
        });
        return chip;
    }

    private FrameworkElement BatteryItem()
    {
        var icon = Icon("");
        var pct = Text();
        pct.Margin = new Thickness(4, 0, 0, 0);
        var chip = Chip(Row(icon, pct), () => Open("ms-settings:batterysaver"));
        _updaters.Add(() =>
        {
            chip.Visibility = _state.HasBattery ? Visibility.Visible : Visibility.Collapsed;
            if (!_state.HasBattery) return;
            int p = Math.Clamp(_state.BatteryPercent, 0, 100);
            int step = Math.Clamp(p / 10, 0, 10);
            icon.Text = _state.Charging
                ? ((char)(0xEBAB + step)).ToString()                       // BatteryCharging0..10
                : step >= 10 ? "" : ((char)(0xE850 + step)).ToString(); // Battery0..9, full
            pct.Text = D($"{p}%");
            pct.Foreground = !_state.Charging && p <= 15 ? new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)) : DesktopTheme.TextBrush;
            SetTip(chip, D(_state.Charging ? $"در حال شارژ: {p}٪" : $"باتری: {p}٪"));
        });
        return chip;
    }

    /// <summary>Compact icon cluster (Wi‑Fi · sound · brightness) that opens the control center.</summary>
    private FrameworkElement ControlCenterItem()
    {
        double sz = IconSz - 3;
        var row = Row(Icon("", sz), Icon("", sz), Icon("", sz));
        foreach (UIElement c in row.Children) ((FrameworkElement)c).Margin = new Thickness(3, 0, 3, 0);
        var chip = Chip(row, DesktopEvents.RequestControlCenter, "مرکز کنترل: وای‌فای، بلوتوث، روشنایی، صدا، تم\nکلیک راست: تنظیمات",
            () => DesktopEvents.RequestStudio("controlcenter"));
        chip.Padding = new Thickness(7, 0, 7, 0);
        return chip;
    }

    /// <summary>All plugins configured for this position, kept in sync with the plugin host.</summary>
    private FrameworkElement PluginsItem(string position)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        string last = "\0";
        _updaters.Add(() =>
        {
            var items = Plugins.TopBarPluginHost.Instance.Active.Where(p => p.Definition.Position == position).ToList();
            string sig = string.Join("|", items.Select(p => p.Definition.Id + "=" + p.Text + "!" + p.Error));
            if (sig == last) return;
            last = sig;
            panel.Children.Clear();
            foreach (var p in items)
            {
                var d = p.Definition;
                var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (!string.IsNullOrWhiteSpace(d.Icon))
                {
                    bool glyph = d.Icon!.Length == 1 && d.Icon[0] >= '' && d.Icon[0] <= '';
                    var ic = glyph ? Icon(d.Icon, IconSz - 2) : Text(d.Icon);
                    ic.Margin = new Thickness(0, 0, string.IsNullOrEmpty(p.Text) ? 0 : 5, 0);
                    content.Children.Add(ic);
                }
                if (!string.IsNullOrEmpty(p.Text))
                {
                    var tb = Text(p.Text);
                    tb.MaxWidth = 260;
                    tb.FlowDirection = HasRtl(p.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                    content.Children.Add(tb);
                }
                string tip = d.Title + (p.Error != null ? "\n⚠ " + p.Error : "") + (d.OnClick != null ? "\nکلیک: اجرا" : "");
                var chip = Chip(content, d.OnClick != null ? () => Plugins.TopBarPluginHost.Click(d) : null, tip,
                    () => DesktopEvents.RequestStudio("plugins"));
                panel.Children.Add(chip);
            }
        });
        return panel;
    }

    private FrameworkElement TrayItem()
    {
        var chip = Chip(Icon(""), null, "میانبرها و تنظیمات سریع");
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowTrayMenu(chip); };
        return chip;
    }

    private void ShowTrayMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu
        {
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = DesktopTheme.Font,
            PlacementTarget = anchor,
            Placement = string.Equals(_s.TopBar.Position, "Bottom", StringComparison.OrdinalIgnoreCase) ? PlacementMode.Top : PlacementMode.Bottom
        };
        void Add(string glyph, string text, Action a)
        {
            var item = new MenuItem
            {
                Header = text,
                Icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center }
            };
            item.Click += (_, _) => Run(a);
            menu.Items.Add(item);
        }
        Add("", "داشبورد و تنظیمات نگارخانه", () => DesktopEvents.RequestStudio(null));
        Add("", "شخصی‌سازی نوار بالا", () => DesktopEvents.RequestStudio("topbar"));
        Add("", "مرکز کنترل", DesktopEvents.RequestControlCenter);
        menu.Items.Add(new Separator());
        Add("", "تنظیمات وای‌فای و شبکه", () => Open("ms-settings:network-wifi"));
        Add("", "تنظیمات صدا", () => Open("ms-settings:sound"));
        Add("", "تنظیمات نمایشگر", () => Open("ms-settings:display"));
        Add("", "اعلان‌ها", () => Open("ms-actioncenter:"));
        Add("", "Task Manager", () => Open("taskmgr.exe"));
        menu.Items.Add(new Separator());
        Add("", "بازگرداندن دسکتاپ ویندوز", () =>
        {
            ShellInterop.RestoreWindowsDesktop();
            var s = DesktopSettingsStore.Current.Clone();
            s.Enabled = false;
            DesktopSettingsStore.Save(s);
        });
        menu.IsOpen = true;
    }
}
