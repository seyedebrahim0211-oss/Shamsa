using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Shamsa.Desktop.Dock;
using WpfDock = System.Windows.Controls.Dock;
using Shamsa.Services;

namespace Shamsa.Desktop.Launcher;

/// <summary>Spotlight-style search window: big search box, ranked results, keyboard driven.</summary>
public sealed class LauncherWindow : Window
{
    private readonly LauncherIndex _index;
    private readonly bool _preview;
    private DesktopSettings _settings;
    private readonly TextBox _box = new();
    private readonly TextBlock _placeholder = new();
    private readonly StackPanel _rows = new();
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll = new();
    private readonly Border _card = new();
    private readonly TextBlock _empty = new();
    private List<LauncherResult> _results = new();
    private readonly List<Border> _rowViews = new();
    private int _selected;
    private int _category; // index into Categories
    private static readonly (string Label, LauncherKind? Kind)[] Categories =
    {
        ("همه", null), ("برنامه‌ها", LauncherKind.App), ("تنظیمات", LauncherKind.Setting),
        ("فایل‌ها", LauncherKind.File), ("فرمان‌ها", LauncherKind.Command), ("کلیپ‌بورد", LauncherKind.Clipboard)
    };
    private static readonly int ClipboardCategory = Categories.Length - 1;
    private readonly TextBlock _footer = new();
    private IntPtr _previousForeground;
    private bool _clipMode;

    public LauncherWindow(LauncherIndex index, DesktopSettings settings, bool preview)
    {
        _index = index;
        _settings = settings;
        _preview = preview;
        Title = "جستجوی سریع شمسا";
        Width = 700;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = preview;
        Topmost = !preview;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = DesktopTheme.Font;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = BuildUi();
        _index.Updated += OnIndexUpdated;
        ClipboardHistory.Changed += OnClipboardChanged;
        Closed += (_, _) => { _index.Updated -= OnIndexUpdated; ClipboardHistory.Changed -= OnClipboardChanged; };
        if (!preview)
        {
            Deactivated += (_, _) => HideLauncher();
            SourceInitialized += (_, _) => NativeMethods.TryRoundCorners(new WindowInteropHelper(this).Handle);
        }
        PreviewKeyDown += OnKey;
    }

    public void ApplySettings(DesktopSettings s)
    {
        _settings = s;
        ApplyTheme();
        Refresh();
    }

    private void ApplyTheme()
    {
        var accent = DesktopTheme.Accent(_settings);
        _card.Background = DesktopTheme.GlassBrush(_settings, 1.35); // denser than bars: readability first
        _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        _card.CornerRadius = new CornerRadius(Math.Max(10, _settings.Theme.CornerRadius + 4));
        _box.CaretBrush = new SolidColorBrush(accent);
        BuildChips();
    }

    private UIElement BuildUi()
    {
        _card.BorderThickness = new Thickness(1);
        _card.Margin = new Thickness(18); // room for the shadow
        _card.Padding = new Thickness(0, 0, 0, 8);
        _card.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black };

        var root = new DockPanel();

        // search row
        var searchRow = new Grid { Margin = new Thickness(18, 14, 18, 6) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22,
            Foreground = DesktopTheme.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0)
        };
        searchRow.Children.Add(glyph);
        _box.FontSize = 24;
        _box.Background = Brushes.Transparent;
        _box.BorderThickness = new Thickness(0);
        _box.Foreground = DesktopTheme.TextBrush;
        _box.Margin = new Thickness(12, 0, 0, 0);
        _box.Padding = new Thickness(0, 4, 0, 4);
        _box.VerticalContentAlignment = VerticalAlignment.Center;
        _box.Style = null;
        _box.Template = BuildPlainTextBoxTemplate();
        _box.TextChanged += (_, _) => { UpdateDirection(); Refresh(); };
        Grid.SetColumn(_box, 1);
        searchRow.Children.Add(_box);
        _placeholder.Text = "جستجوی برنامه، تنظیمات، فایل یا محاسبه…";
        _placeholder.FontSize = 20;
        _placeholder.Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xB6, 0xC6, 0xDF));
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(_placeholder, 1);
        searchRow.Children.Add(_placeholder);
        DockPanel.SetDock(searchRow, WpfDock.Top);
        root.Children.Add(searchRow);

        _chips.Margin = new Thickness(16, 2, 16, 8);
        DockPanel.SetDock(_chips, WpfDock.Top);
        root.Children.Add(_chips);

        var divider = new Border { Height = 1, Margin = new Thickness(14, 0, 14, 6), Background = Application.Current?.TryFindResource("PersianDividerBrush") as Brush ?? DesktopTheme.HoverBrush };
        DockPanel.SetDock(divider, WpfDock.Top);
        root.Children.Add(divider);

        _footer.FontSize = 11.5;
        _footer.Foreground = DesktopTheme.MutedBrush;
        _footer.Margin = new Thickness(18, 6, 18, 0);
        _footer.Opacity = 0.8;
        _footer.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(_footer, WpfDock.Bottom);
        root.Children.Add(_footer);

        _empty.Text = "نتیجه‌ای پیدا نشد";
        _empty.Foreground = DesktopTheme.MutedBrush;
        _empty.Margin = new Thickness(20, 10, 20, 10);
        _empty.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_empty, WpfDock.Top);
        root.Children.Add(_empty);

        _scroll.Content = _rows;
        _scroll.MaxHeight = 470;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.Margin = new Thickness(8, 0, 8, 0);
        root.Children.Add(_scroll);

        _card.Child = root;
        ApplyTheme();
        return _card;
    }

    private static ControlTemplate BuildPlainTextBoxTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        t.VisualTree = host;
        return t;
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        var accent = DesktopTheme.Accent(_settings);
        for (int i = 0; i < Categories.Length; i++)
        {
            bool on = i == _category;
            int idx = i;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(0, 0, 6, 0),
                Background = on ? new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)) : DesktopTheme.HoverBrush,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = Categories[i].Label, FontSize = 12, Foreground = on ? DesktopTheme.TextBrush : DesktopTheme.MutedBrush }
            };
            chip.MouseLeftButtonUp += (_, _) => { _category = idx; BuildChips(); Refresh(); _box.Focus(); };
            _chips.Children.Add(chip);
        }
    }

    private void UpdateDirection()
    {
        _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _box.FlowDirection = DirectionOf(_box.Text) ?? (_box.Text.Any(char.IsDigit) ? FlowDirection.LeftToRight : FlowDirection.RightToLeft);
    }

    /// <summary>Direction of the first strong character, or null when there is none.</summary>
    internal static FlowDirection? DirectionOf(string text)
    {
        foreach (char c in text)
        {
            if (c >= '֐' && c <= 'ࣿ' || c >= 'יִ' && c <= '﷿' || c >= 'ﹰ' && c <= '﻿') return FlowDirection.RightToLeft;
            if (char.IsLetter(c)) return FlowDirection.LeftToRight;
        }
        return null;
    }

    private void OnIndexUpdated() => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && !_clipMode) Refresh(); }));
    private void OnClipboardChanged() => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && _clipMode) Refresh(); }));

    /// <summary>Clipboard mode: the clipboard chip is active or the query starts with "cb " / "؛".</summary>
    private bool InClipboardMode(out string query)
    {
        query = _box.Text;
        if (_category == ClipboardCategory) return true;
        if (query.StartsWith("cb ", StringComparison.OrdinalIgnoreCase)) { query = query[3..]; return true; }
        if (query.StartsWith("؛")) { query = query[1..]; return true; }
        return false;
    }

    private List<LauncherResult> ClipboardResults(string query) =>
        ClipboardHistory.Search(query).Take(Math.Max(_settings.Launcher.MaxResults, 12)).Select(c => new LauncherResult
        {
            Kind = LauncherKind.Clipboard,
            Title = c.Preview,
            Subtitle = (c.Pinned ? "📌 سنجاق‌شده · " : "") + Ago(c.CapturedUtc),
            Target = c.Id,
            Glyph = c.Kind switch { ClipKind.Files => "\uE8B7", ClipKind.Image => "\uEB9F", _ => "\uE8C8" },
            IconPath = c.Kind == ClipKind.Files && c.Files is { Length: 1 } ? c.Files[0] : null,
            Data = c
        }).ToList();

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        string s = d.TotalMinutes < 1 ? "همین حالا" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} دقیقه پیش"
                 : d.TotalDays < 1 ? $"{(int)d.TotalHours} ساعت پیش" : $"{(int)d.TotalDays} روز پیش";
        return DesktopTheme.Digits(s, true);
    }

    private void UpdateFooter()
    {
        _footer.Text = _clipMode
            ? "Enter چسباندن  ·  Shift+Enter فقط کپی  ·  Ctrl+P سنجاق  ·  Delete حذف  ·  Esc بستن"
            : "Enter اجرا  ·  Ctrl+Enter محل فایل  ·  Tab دسته بعدی  ·  «cb » یا «؛» کلیپ‌بورد  ·  Esc بستن";
    }

    public void SetQuery(string text)
    {
        _box.Text = text;
        _box.CaretIndex = text.Length;
    }

    private void Refresh()
    {
        string q = _box.Text;
        _clipMode = InClipboardMode(out string clipQuery);
        UpdateFooter();
        if (_clipMode)
            _results = ClipboardResults(clipQuery);
        else
            _results = string.IsNullOrWhiteSpace(q) ? RecentFromHistory() : _index.Search(q, _settings.Launcher, Categories[_category].Kind);
        _selected = 0;
        _rows.Children.Clear();
        _rowViews.Clear();
        foreach (var r in _results)
        {
            var row = BuildRow(r);
            _rowViews.Add(row);
            _rows.Children.Add(row);
        }
        _empty.Text = _clipMode
            ? (ClipboardHistory.IsRunning ? "تاریخچه کلیپ‌بورد خالی است — هر چیزی کپی کنی اینجا می‌آید" : "تاریخچه کلیپ‌بورد خاموش است (استودیو دسکتاپ › کلیپ‌بورد)")
            : "نتیجه‌ای پیدا نشد";
        _empty.Visibility = _results.Count == 0 && (_clipMode || !string.IsNullOrWhiteSpace(q)) ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    /// <summary>Empty query: most recent launches still in the index.</summary>
    private List<LauncherResult> RecentFromHistory()
    {
        var list = new List<LauncherResult>();
        foreach (var target in _settings.Launcher.History.Take(6))
        {
            var hit = _index.Search(Path.GetFileNameWithoutExtension(target.Split('\\').Last()), _settings.Launcher, null)
                            .FirstOrDefault(r => string.Equals(r.Target, target, StringComparison.OrdinalIgnoreCase));
            if (hit != null) list.Add(hit);
        }
        return list;
    }

    private Border BuildRow(LauncherResult r)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        UIElement icon;
        BitmapSource? bmp = (r.Data as ClipItem)?.Thumbnail;
        if (bmp == null && r.IconPath != null)
        {
            try { bmp = ShellThumbnailService.TryGetShellImage(r.IconPath, 64, iconOnly: true); } catch { }
        }
        if (bmp != null)
        {
            var img = new Image { Source = bmp, Width = 30, Height = 30, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            icon = img;
        }
        else
        {
            icon = new TextBlock
            {
                Text = r.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
                Foreground = DesktopTheme.AccentBrush(_settings), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight
            };
        }
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var title = new TextBlock
        {
            Text = r.Title, FontSize = r.Kind == LauncherKind.Calc ? 20 : 15, FontWeight = FontWeights.SemiBold,
            Foreground = DesktopTheme.TextBrush, TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = DirectionOf(r.Title) ?? FlowDirection.RightToLeft,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        texts.Children.Add(title); // Left = start edge of the RTL window, for Latin titles too
        if (!string.IsNullOrEmpty(r.Subtitle))
            texts.Children.Add(new TextBlock { Text = r.Subtitle, FontSize = 11.5, Foreground = DesktopTheme.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var tag = new TextBlock
        {
            Text = r.Kind switch
            {
                LauncherKind.App => "برنامه", LauncherKind.Setting => "تنظیمات", LauncherKind.File => "فایل",
                LauncherKind.Command => "فرمان", LauncherKind.Calc => "محاسبه", LauncherKind.Clipboard => "کلیپ‌بورد", _ => "وب"
            },
            FontSize = 11, Foreground = DesktopTheme.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75
        };
        Grid.SetColumn(tag, 2);
        grid.Children.Add(tag);

        var border = new Border
        {
            Child = grid, Padding = new Thickness(8, 7, 12, 7), Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = r
        };
        border.MouseEnter += (_, _) => { _selected = _rowViews.IndexOf(border); UpdateSelection(scroll: false); };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (r.Data is ClipItem clip) RunClip(clip, copyOnly: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            else Run(r, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        };
        return border;
    }

    private void UpdateSelection(bool scroll = true)
    {
        var accent = DesktopTheme.Accent(_settings);
        var sel = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        for (int i = 0; i < _rowViews.Count; i++)
            _rowViews[i].Background = i == _selected ? sel : Brushes.Transparent;
        if (scroll && _selected >= 0 && _selected < _rowViews.Count) _rowViews[_selected].BringIntoView();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_box.Text.Length > 0) _box.Clear(); else HideLauncher();
                e.Handled = true; break;
            case Key.Down:
                if (_rowViews.Count > 0) { _selected = (_selected + 1) % _rowViews.Count; UpdateSelection(); }
                e.Handled = true; break;
            case Key.Up:
                if (_rowViews.Count > 0) { _selected = (_selected - 1 + _rowViews.Count) % _rowViews.Count; UpdateSelection(); }
                e.Handled = true; break;
            case Key.Tab:
                _category = (_category + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Categories.Length - 1 : 1)) % Categories.Length;
                BuildChips(); Refresh();
                e.Handled = true; break;
            case Key.Enter:
                if (_selected >= 0 && _selected < _results.Count)
                {
                    if (_results[_selected].Data is ClipItem clip) RunClip(clip, copyOnly: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    else Run(_results[_selected], Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                }
                e.Handled = true; break;
            case Key.Delete when SelectedClip() is { } del && (_box.Text.Length == 0 || _box.CaretIndex >= _box.Text.Length):
                if (!_preview) ClipboardHistory.Remove(del);
                e.Handled = true; break;
            case Key.P when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && SelectedClip() is { } pin:
                if (!_preview) ClipboardHistory.TogglePin(pin);
                e.Handled = true; break;
        }
    }

    private ClipItem? SelectedClip() =>
        _selected >= 0 && _selected < _results.Count ? _results[_selected].Data as ClipItem : null;

    private void RunClip(ClipItem clip, bool copyOnly)
    {
        if (_preview) return;
        var target = _previousForeground;
        HideLauncher();
        if (copyOnly) ClipboardHistory.CopyToClipboard(clip);
        else ClipboardHistory.Paste(clip, target);
    }

    private void Run(LauncherResult r, bool openLocation)
    {
        if (_preview) return; // the studio preview is look-only
        try
        {
            if (openLocation)
            {
                string? loc = r.LocationPath;
                if (loc != null && loc.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && r.Kind == LauncherKind.File)
                    loc = ShortcutResolver.Resolve(loc)?.TargetPath ?? loc;
                if (loc != null && (File.Exists(loc) || Directory.Exists(loc)))
                    LauncherIndex.ShellRun("explorer.exe", $"/select,\"{loc}\"");
                else return;
            }
            else if (r.Execute != null) r.Execute();
            else LauncherIndex.ShellRun(r.Target, null);

            if (r.Kind is not (LauncherKind.Calc or LauncherKind.Web))
            {
                var s = DesktopSettingsStore.Current;
                s.Launcher.History.RemoveAll(h => string.Equals(h, r.Target, StringComparison.OrdinalIgnoreCase));
                s.Launcher.History.Insert(0, r.Target);
                if (s.Launcher.History.Count > 50) s.Launcher.History.RemoveRange(50, s.Launcher.History.Count - 50);
                DesktopSettingsStore.Save(s);
            }
        }
        catch (Exception ex) { AppLog.Warn("Launcher run failed: " + ex.Message); }
        HideLauncher();
    }

    public void ShowLauncher(bool clipboardMode = false, string? query = null)
    {
        var fg = ClipboardNative.GetForegroundWindow();
        if (fg != new WindowInteropHelper(this).Handle) _previousForeground = fg;
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + wa.Height * 0.18;
        _category = clipboardMode ? ClipboardCategory : 0;
        _box.Clear();
        if (!string.IsNullOrEmpty(query)) SetQuery(query);
        BuildChips();
        Refresh();
        Opacity = 0;
        Show();
        Activate();
        NativeMethodsForeground(this);
        _box.Focus();
        Keyboard.Focus(_box);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    private static void NativeMethodsForeground(Window w)
    {
        try { Shell.ShellInterop.SetForegroundWindow(new WindowInteropHelper(w).Handle); } catch { }
    }

    public void HideLauncher()
    {
        if (_preview) return;
        Hide();
    }
}
