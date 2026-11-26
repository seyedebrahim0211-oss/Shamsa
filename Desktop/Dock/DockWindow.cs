using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shamsa.Desktop.Shell;
using Shamsa.Services;
using IOPath = System.IO.Path;

namespace Shamsa.Desktop.Dock;

internal enum DockItemKind { App, Folder, Uri, Trash, Separator }

internal sealed class DockItem
{
    public required string Key { get; init; }
    public DockItemKind Kind { get; init; }
    public string? Pin { get; init; }          // the stored pin string, null when not pinned
    public int PinIndex { get; init; } = -1;
    public string? LaunchTarget { get; init; } // what Process.Start opens
    public string? Exe { get; init; }          // executable for matching windows / new instance
    public string Label { get; set; } = "";
    public bool BuiltIn { get; init; }         // Downloads / Recycle Bin
    public List<AppWindow> Windows { get; } = new();
    public bool IsPinned => Pin != null;

    // visuals
    public Grid View = null!;
    public Image? Img;
    public FrameworkElement IconElement = null!;
    public Ellipse Dot = null!;
    public Border Badge = null!;
    public TextBlock BadgeText = null!;
    public TranslateTransform Hop = new();
    public double Scale = 1, TargetScale = 1;
}

/// <summary>
/// macOS-style glass dock: pinned + running apps with real icons, magnification, running dots,
/// window counts, drag-to-pin / reorder, auto-hide and full-screen awareness.
/// </summary>
internal sealed class DockWindow : Window
{
    private const double Pad = 6, EdgeMargin = 6, LabelSpace = 36, Spacing = 6;

    private readonly bool _preview;
    private DesktopSettings _s;
    private readonly Grid _root = new();
    private readonly Border _plate = new();
    private readonly StackPanel _stack = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _label = new();
    private readonly TextBlock _labelText = new();
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private List<DockItem> _items = new();
    private string _signature = "";
    private double? _mouseAxis;
    private bool _rendering, _menuOpen, _hidden, _appBar, _fullscreenHidden;
    private DateTime _lastNear = DateTime.UtcNow;
    private DockItem? _pressItem, _dragItem;
    private Point _pressPoint;
    private double _dpi = 1;
    private DockThumbnailPopup? _thumbs;
    private DockItem? _hoverCandidate;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _thumbHideTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    private string Edge => _s.Dock.Position switch { "Left" => "Left", "Right" => "Right", "Top" => "Top", _ => "Bottom" };
    private bool Horizontal => Edge is "Bottom" or "Top";
    private double BaseSize => _s.Dock.IconSize;
    private double Mag => _s.Dock.Magnification / 100.0;
    private double PlateThickness => BaseSize + Pad * 2;
    private double StripThickness => EdgeMargin + PlateThickness + BaseSize * (Mag - 1) + LabelSpace;

    public DockWindow(DesktopSettings settings, bool preview)
    {
        _s = settings;
        _preview = preview;
        Title = "داک نگارخانه";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = preview;
        ShowActivated = preview;
        Topmost = !preview;
        FontFamily = DesktopTheme.Font;
        Background = preview ? new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x4A)) : Brushes.Transparent;
        AllowDrop = !preview;
        UseLayoutRounding = true;

        _root.RenderTransform = _slide;
        _root.Children.Add(_plate);
        _root.Children.Add(_stack);
        _root.Children.Add(_overlay);
        _label.Child = _labelText;
        _label.Visibility = Visibility.Collapsed;
        _overlay.Children.Add(_label);
        Content = _root;

        _stack.SizeChanged += (_, _) => SyncPlate();
        _root.MouseMove += OnMouseMove;
        _root.MouseLeave += (_, _) => { if (_dragItem == null) { _mouseAxis = null; StartRendering(); } };
        _root.MouseRightButtonUp += (_, e) => { if (!e.Handled) ShowMenu(null, _plate); e.Handled = true; };
        _root.PreviewMouseLeftButtonUp += OnMouseUp;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!_preview) NativeMethods.MakeToolWindowNoActivate(hwnd);
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Place();
        };
        DpiChanged += (_, e) => { _dpi = e.NewDpi.DpiScaleX; Place(); };
        _refreshTimer.Tick += (_, _) => RefreshItems(false);
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowThumbnails(); };
        _thumbHideTimer.Tick += (_, _) => ThumbHideTick();
        _edgeTimer.Tick += (_, _) => EdgeTick();
        Closed += (_, _) =>
        {
            _refreshTimer.Stop(); _edgeTimer.Stop(); _hoverTimer.Stop(); _thumbHideTimer.Stop();
            try { _thumbs?.Close(); } catch { }
            _thumbs = null;
            if (_rendering) CompositionTarget.Rendering -= OnRendering;
            if (_appBar) { ShellInterop.UnregisterAppBar(this); _appBar = false; }
        };
        Loaded += (_, _) => { if (!_preview) { _refreshTimer.Start(); _edgeTimer.Start(); } };

        ApplySettings(settings);
    }

    // ------------------------------------------------------------------ settings / layout

    public void ApplySettings(DesktopSettings s)
    {
        _s = s;
        _plate.Background = DesktopTheme.GlassBrush(s);
        _plate.BorderBrush = DesktopTheme.BorderBrush(s);
        _plate.BorderThickness = new Thickness(1);
        _plate.CornerRadius = new CornerRadius(Math.Min(s.Theme.CornerRadius + 6, PlateThickness / 2));
        _plate.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35, Direction = 270 };
        _label.Background = DesktopTheme.GlassBrush(s, 1.3);
        _label.BorderBrush = DesktopTheme.BorderBrush(s);
        _label.BorderThickness = new Thickness(1);
        _label.CornerRadius = new CornerRadius(7);
        _label.Padding = new Thickness(10, 3, 10, 4);
        _labelText.Foreground = DesktopTheme.TextBrush;
        _labelText.FontSize = 12.5;
        _thumbs?.ApplySettings(s);

        string edge = Edge;
        _stack.Orientation = Horizontal ? Orientation.Horizontal : Orientation.Vertical;
        switch (edge)
        {
            case "Bottom":
                _plate.VerticalAlignment = VerticalAlignment.Bottom; _plate.HorizontalAlignment = HorizontalAlignment.Center;
                _plate.Margin = new Thickness(0, 0, 0, EdgeMargin);
                _stack.VerticalAlignment = VerticalAlignment.Bottom; _stack.HorizontalAlignment = HorizontalAlignment.Center;
                _stack.Margin = new Thickness(0, 0, 0, EdgeMargin + Pad); break;
            case "Top":
                _plate.VerticalAlignment = VerticalAlignment.Top; _plate.HorizontalAlignment = HorizontalAlignment.Center;
                _plate.Margin = new Thickness(0, EdgeMargin, 0, 0);
                _stack.VerticalAlignment = VerticalAlignment.Top; _stack.HorizontalAlignment = HorizontalAlignment.Center;
                _stack.Margin = new Thickness(0, EdgeMargin + Pad, 0, 0); break;
            case "Left":
                _plate.HorizontalAlignment = HorizontalAlignment.Left; _plate.VerticalAlignment = VerticalAlignment.Center;
                _plate.Margin = new Thickness(EdgeMargin, 0, 0, 0);
                _stack.HorizontalAlignment = HorizontalAlignment.Left; _stack.VerticalAlignment = VerticalAlignment.Center;
                _stack.Margin = new Thickness(EdgeMargin + Pad, 0, 0, 0); break;
            default:
                _plate.HorizontalAlignment = HorizontalAlignment.Right; _plate.VerticalAlignment = VerticalAlignment.Center;
                _plate.Margin = new Thickness(0, 0, EdgeMargin, 0);
                _stack.HorizontalAlignment = HorizontalAlignment.Right; _stack.VerticalAlignment = VerticalAlignment.Center;
                _stack.Margin = new Thickness(0, 0, EdgeMargin + Pad, 0); break;
        }
        _slide.X = _slide.Y = 0;
        _hidden = false;
        RefreshItems(true);
        if (IsLoaded || new WindowInteropHelper(this).Handle != IntPtr.Zero) Place();
    }

    private void SyncPlate()
    {
        if (Horizontal) { _plate.Height = PlateThickness; _plate.Width = _stack.ActualWidth + Pad * 2; }
        else { _plate.Width = PlateThickness; _plate.Height = _stack.ActualHeight + Pad * 2; }
    }

    /// <summary>Positions the strip window on the primary monitor's chosen edge (optionally as an AppBar).</summary>
    private void Place()
    {
        if (_preview)
        {
            Width = Horizontal ? 980 : StripThickness + 40;
            Height = Horizontal ? StripThickness + 40 : 780;
            return;
        }
        IntPtr monitor = ShellInterop.MonitorFromWindow(IntPtr.Zero, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var (monPx, workPx) = ShellInterop.GetMonitorRects(monitor);
        if (monPx.IsEmpty) { monPx = new Rect(0, 0, SystemParameters.PrimaryScreenWidth * _dpi, SystemParameters.PrimaryScreenHeight * _dpi); workPx = monPx; }

        Rect area; // DIP
        if (_s.Dock.ReserveSpace)
        {
            uint abe = Edge switch { "Top" => ShellInterop.ABE_TOP, "Left" => ShellInterop.ABE_LEFT, "Right" => ShellInterop.ABE_RIGHT, _ => ShellInterop.ABE_BOTTOM };
            int px = (int)Math.Ceiling((PlateThickness + EdgeMargin * 2) * _dpi);
            var granted = ShellInterop.RegisterAppBar(this, abe, px, monitor);
            _appBar = true;
            // the strip extends from the granted bar's outer edge inwards
            area = new Rect(granted.Left / _dpi, granted.Top / _dpi, granted.Width / _dpi, granted.Height / _dpi);
            area = Edge switch
            {
                "Bottom" => new Rect(monPx.Left / _dpi, area.Bottom - StripThickness, monPx.Width / _dpi, StripThickness),
                "Top" => new Rect(monPx.Left / _dpi, area.Top, monPx.Width / _dpi, StripThickness),
                "Left" => new Rect(area.Left, monPx.Top / _dpi, StripThickness, monPx.Height / _dpi),
                _ => new Rect(area.Right - StripThickness, monPx.Top / _dpi, StripThickness, monPx.Height / _dpi)
            };
        }
        else
        {
            if (_appBar) { ShellInterop.UnregisterAppBar(this); _appBar = false; }
            Rect basePx = ShellInterop.IsTaskbarHidden ? monPx : workPx;
            var b = new Rect(basePx.Left / _dpi, basePx.Top / _dpi, basePx.Width / _dpi, basePx.Height / _dpi);
            double t = StripThickness;
            area = Edge switch
            {
                "Bottom" => new Rect(b.Left, b.Bottom - t, b.Width, t),
                "Top" => new Rect(b.Left, b.Top, b.Width, t),
                "Left" => new Rect(b.Left, b.Top, t, b.Height),
                _ => new Rect(b.Right - t, b.Top, t, b.Height)
            };
        }
        Left = area.Left; Top = area.Top; Width = area.Width; Height = area.Height;
    }

    // ------------------------------------------------------------------ model

    private void RefreshItems(bool force)
    {
        List<AppWindow> windows;
        try { windows = _preview || IsVisible || force ? ShellInterop.GetAppWindows() : new(); }
        catch (Exception ex) { AppLog.Warn("Dock window scan: " + ex.Message); windows = new(); }

        var model = BuildModel(windows);
        string sig = string.Join("|", model.Select(i => i.Key));
        if (force || sig != _signature)
        {
            _signature = sig;
            _items = model;
            BuildVisuals();
        }
        else
        {
            for (int i = 0; i < model.Count; i++)
            {
                _items[i].Windows.Clear();
                _items[i].Windows.AddRange(model[i].Windows);
                if (model[i].Kind == DockItemKind.App && !_items[i].IsPinned) _items[i].Label = model[i].Label;
                UpdateIndicators(_items[i]);
            }
        }
    }

    private List<DockItem> BuildModel(List<AppWindow> windows)
    {
        var list = new List<DockItem>();
        var byKey = new Dictionary<string, DockItem>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _s.Dock.Pinned.Count; i++)
        {
            string pin = _s.Dock.Pinned[i];
            if (string.IsNullOrWhiteSpace(pin)) continue;
            DockItemKind kind;
            bool isUri = (pin.Contains(':') && !IOPath.IsPathRooted(pin)) || pin.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            if (isUri) kind = DockItemKind.Uri;
            else if (Directory.Exists(pin)) kind = DockItemKind.Folder;
            else if (File.Exists(pin)) kind = DockItemKind.App;
            else continue; // missing file: keep the pin stored, just don't show it
            string? exe = kind == DockItemKind.App ? ShortcutResolver.ExecutableFor(pin) : null;
            string key = (exe ?? pin).ToLowerInvariant();
            if (byKey.ContainsKey(key)) continue;
            var item = new DockItem
            {
                Key = key, Kind = kind, Pin = pin, PinIndex = i, LaunchTarget = pin, Exe = exe,
                Label = LabelForPin(pin, kind, exe)
            };
            byKey[key] = item;
            list.Add(item);
        }
        int pinnedCount = list.Count;

        var running = new List<DockItem>();
        foreach (var w in windows)
        {
            string? exe = w.ExePath;
            bool groupable = exe != null && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            string exeKey = groupable ? exe!.ToLowerInvariant() : "hwnd:" + w.Hwnd.ToInt64();
            if (byKey.TryGetValue(exeKey, out var existing) && (existing.IsPinned || _s.Dock.GroupWindowsByApp))
            {
                existing.Windows.Add(w);
                continue;
            }
            string key = _s.Dock.GroupWindowsByApp ? exeKey : "hwnd:" + w.Hwnd.ToInt64();
            var item = new DockItem
            {
                Key = key, Kind = DockItemKind.App, Exe = groupable ? exe : null, LaunchTarget = groupable ? exe : null,
                Label = groupable ? ShellInterop.FriendlyAppName(exe) : w.Title
            };
            if (!_s.Dock.GroupWindowsByApp && groupable) item.Label = w.Title;
            item.Windows.Add(w);
            byKey[key] = item;
            if (_s.Dock.GroupWindowsByApp) byKey[exeKey] = item;
            running.Add(item);
        }
        if (running.Count > 0 && pinnedCount > 0) list.Add(Separator("sep:run"));
        list.AddRange(running);

        if (_s.Dock.ShowTrashAndFolders)
        {
            list.Add(Separator("sep:sys"));
            string dl = NativeMethods.GetDownloadsFolder();
            if (Directory.Exists(dl))
                list.Add(new DockItem { Key = "builtin:downloads", Kind = DockItemKind.Folder, LaunchTarget = dl, Label = "دانلودها", BuiltIn = true });
            list.Add(new DockItem { Key = "builtin:trash", Kind = DockItemKind.Trash, LaunchTarget = "shell:RecycleBinFolder", Label = "سطل بازیافت", BuiltIn = true });
        }
        return list;

        static DockItem Separator(string key) => new() { Key = key, Kind = DockItemKind.Separator };
    }

    private static string LabelForPin(string pin, DockItemKind kind, string? exe)
    {
        if (kind == DockItemKind.Uri)
        {
            if (pin.StartsWith("ms-settings", StringComparison.OrdinalIgnoreCase)) return "تنظیمات";
            if (pin.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase)) return "برنامه";
            return pin;
        }
        if (kind == DockItemKind.Folder)
        {
            string name = IOPath.GetFileName(pin.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? pin : name;
        }
        if (pin.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return IOPath.GetFileNameWithoutExtension(pin);
        if (exe != null && exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) return "فایل اکسپلورر";
        return ShellInterop.FriendlyAppName(exe ?? pin);
    }

    // ------------------------------------------------------------------ visuals

    private void BuildVisuals()
    {
        _stack.Children.Clear();
        foreach (var item in _items)
        {
            if (item.Kind == DockItemKind.Separator)
            {
                var sep = new Grid { Margin = Horizontal ? new Thickness(Spacing / 2, 0, Spacing / 2, 0) : new Thickness(0, Spacing / 2, 0, Spacing / 2) };
                var line = new Border { Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)) };
                if (Horizontal) { line.Width = 1; line.Height = BaseSize * 0.72; sep.VerticalAlignment = AlignToEdgeV(); }
                else { line.Height = 1; line.Width = BaseSize * 0.72; sep.HorizontalAlignment = AlignToEdgeH(); }
                line.Margin = Horizontal ? new Thickness(0, BaseSize * 0.14, 0, BaseSize * 0.14) : new Thickness(BaseSize * 0.14, 0, BaseSize * 0.14, 0);
                sep.Children.Add(line);
                item.View = sep;
                _stack.Children.Add(sep);
                continue;
            }
            BuildItemView(item);
            _stack.Children.Add(item.View);
        }
        SyncPlate();
    }

    private VerticalAlignment AlignToEdgeV() => Edge == "Top" ? VerticalAlignment.Top : VerticalAlignment.Bottom;
    private HorizontalAlignment AlignToEdgeH() => Edge == "Left" ? HorizontalAlignment.Left : HorizontalAlignment.Right;

    private void BuildItemView(DockItem item)
    {
        var g = new Grid
        {
            Width = BaseSize, Height = BaseSize, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Margin = Horizontal ? new Thickness(Spacing / 2, 0, Spacing / 2, 0) : new Thickness(0, Spacing / 2, 0, Spacing / 2)
        };
        if (Horizontal) g.VerticalAlignment = AlignToEdgeV(); else g.HorizontalAlignment = AlignToEdgeH();

        var bmp = LoadIcon(item);
        if (bmp != null)
        {
            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(2), RenderTransform = item.Hop };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            item.Img = img;
            item.IconElement = img;
        }
        else
        {
            var fallback = new Border
            {
                CornerRadius = new CornerRadius(BaseSize * 0.22), Margin = new Thickness(3), RenderTransform = item.Hop,
                Background = new LinearGradientBrush(DesktopTheme.Accent(_s), Color.FromRgb(0x1E, 0x3A, 0x8A), 45),
                Child = new Viewbox
                {
                    Margin = new Thickness(BaseSize * 0.18),
                    Child = new TextBlock
                    {
                        Text = item.Kind switch { DockItemKind.Trash => "", DockItemKind.Folder => "", DockItemKind.Uri => "", _ => "" },
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Brushes.White
                    }
                }
            };
            item.IconElement = fallback;
        }
        g.Children.Add(item.IconElement);

        item.Dot = new Ellipse { Width = 4.5, Height = 4.5, Fill = DesktopTheme.TextBrush, IsHitTestVisible = false };
        switch (Edge)
        {
            case "Bottom": item.Dot.VerticalAlignment = VerticalAlignment.Bottom; item.Dot.HorizontalAlignment = HorizontalAlignment.Center; item.Dot.Margin = new Thickness(0, 0, 0, -(Pad - 0.5)); break;
            case "Top": item.Dot.VerticalAlignment = VerticalAlignment.Top; item.Dot.HorizontalAlignment = HorizontalAlignment.Center; item.Dot.Margin = new Thickness(0, -(Pad - 0.5), 0, 0); break;
            case "Left": item.Dot.HorizontalAlignment = HorizontalAlignment.Left; item.Dot.VerticalAlignment = VerticalAlignment.Center; item.Dot.Margin = new Thickness(-(Pad - 0.5), 0, 0, 0); break;
            default: item.Dot.HorizontalAlignment = HorizontalAlignment.Right; item.Dot.VerticalAlignment = VerticalAlignment.Center; item.Dot.Margin = new Thickness(0, 0, -(Pad - 0.5), 0); break;
        }
        g.Children.Add(item.Dot);

        item.BadgeText = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        item.Badge = new Border
        {
            Child = item.BadgeText, MinWidth = 17, Height = 17, CornerRadius = new CornerRadius(8.5), Padding = new Thickness(4, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)), BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -3, 0),
            IsHitTestVisible = false
        };
        g.Children.Add(item.Badge);

        g.MouseLeftButtonDown += (_, e) =>
        {
            _pressItem = item;
            _pressPoint = e.GetPosition(_root);
            g.CaptureMouse();
            e.Handled = true;
        };
        g.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { NewInstance(item); e.Handled = true; }
        };
        g.MouseRightButtonUp += (_, e) => { ShowMenu(item, g); e.Handled = true; };
        item.View = g;
        UpdateIndicators(item);
    }

    private BitmapSource? LoadIcon(DockItem item)
    {
        int px = (int)Math.Min(256, Math.Ceiling(BaseSize * Mag * Math.Max(1, _dpi)));
        try
        {
            switch (item.Kind)
            {
                case DockItemKind.Trash:
                    return ShellThumbnailService.TryGetShellImage("::{645FF040-5081-101B-9F08-00AA002F954E}", px, iconOnly: true);
                case DockItemKind.Uri:
                    if (item.LaunchTarget!.StartsWith("ms-settings", StringComparison.OrdinalIgnoreCase))
                    {
                        string p = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"ImmersiveControlPanel\SystemSettings.exe");
                        return File.Exists(p) ? ShellThumbnailService.TryGetShellImage(p, px, iconOnly: true) : null;
                    }
                    return ShellThumbnailService.TryGetShellImage(item.LaunchTarget, px, iconOnly: true);
                case DockItemKind.Folder:
                    return ShellThumbnailService.TryGetShellImage(item.LaunchTarget!, px, iconOnly: true);
                default:
                    if (item.Pin != null) return ShellThumbnailService.TryGetShellImage(item.Pin, px, iconOnly: true);
                    if (item.Exe != null) return ShellInterop.GetAppIcon(item.Exe, px);
                    // ungroupable (UWP host) — no reliable icon source without the AUMID
                    return null;
            }
        }
        catch (Exception ex) { AppLog.Warn("Dock icon: " + ex.Message); return null; }
    }

    private void UpdateIndicators(DockItem item)
    {
        if (item.View == null || item.Kind == DockItemKind.Separator || item.Dot == null) return;
        int n = item.Windows.Count;
        item.Dot.Visibility = _s.Dock.ShowRunningIndicators && n > 0 ? Visibility.Visible : Visibility.Hidden;
        item.Dot.Fill = n > 0 && item.Windows.Any(w => w.Hwnd == NativeMethods.GetForegroundWindow())
            ? DesktopTheme.AccentBrush(_s) : DesktopTheme.TextBrush;
        bool badge = _s.Dock.ShowWindowCount && n > 1;
        item.Badge.Visibility = badge ? Visibility.Visible : Visibility.Collapsed;
        if (badge) item.BadgeText.Text = DesktopTheme.Digits(n > 9 ? "9+" : n.ToString(), true);
    }

    // ------------------------------------------------------------------ magnification

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_root);
        if (_pressItem != null && _dragItem == null && _pressItem.IsPinned && !_preview &&
            (Math.Abs(p.X - _pressPoint.X) > 8 || Math.Abs(p.Y - _pressPoint.Y) > 8))
        {
            _dragItem = _pressItem;
            _dragItem.View.Opacity = 0.55;
        }

        if (Mag <= 1.001) { _mouseAxis = null; }
        else
        {
            double axis = Horizontal ? p.X : p.Y;
            double center = (Horizontal ? _root.ActualWidth : _root.ActualHeight) / 2;
            _mouseAxis = axis - center + BaseTotal() / 2;
        }
        _lastNear = DateTime.UtcNow;
        StartRendering();
    }

    private double BaseTotal()
    {
        double total = 0;
        foreach (var i in _items) total += Extent(i);
        return total;
    }

    private double Extent(DockItem i) => i.Kind == DockItemKind.Separator ? 1 + Spacing : BaseSize + Spacing;

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double range = BaseSize * 3;
        double pos = 0;
        bool moving = false;
        DockItem? hovered = null;
        foreach (var item in _items)
        {
            double ext = Extent(item);
            double c = pos + ext / 2;
            pos += ext;
            if (item.Kind == DockItemKind.Separator) continue;
            double target = 1;
            if (_mouseAxis is double m)
            {
                double d = Math.Abs(m - c);
                if (d < range) target = 1 + (Mag - 1) * (Math.Cos(Math.PI * d / range) + 1) / 2;
            }
            item.TargetScale = target;
            double next = item.Scale + (target - item.Scale) * 0.32;
            if (Math.Abs(target - next) < 0.003) next = target; else moving = true;
            if (Math.Abs(next - item.Scale) > 0.0001 || Math.Abs(item.View.Width - BaseSize * next) > 0.1)
            {
                item.Scale = next;
                item.View.Width = item.View.Height = BaseSize * next;
            }
            if (item.View.IsMouseOver) hovered = item;
        }
        UpdateLabel(_mouseAxis == null ? null : hovered);
        TrackHover(_mouseAxis == null ? null : hovered);
        if (!moving && _mouseAxis == null)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    private void UpdateLabel(DockItem? item)
    {
        if (item == null || !_s.Dock.ShowLabelsOnHover || _menuOpen || _dragItem != null || _thumbs is { IsVisible: true })
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        _labelText.Text = item.Windows.Count == 1 && !item.IsPinned && !_s.Dock.GroupWindowsByApp ? item.Windows[0].Title : item.Label;
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _label.DesiredSize;
        Point tl;
        try { tl = item.View.TranslatePoint(new Point(0, 0), _overlay); } catch { return; }
        double w = item.View.ActualWidth, h = item.View.ActualHeight;
        double x, y;
        switch (Edge)
        {
            case "Bottom": x = tl.X + w / 2 - size.Width / 2; y = tl.Y - size.Height - 6; break;
            case "Top": x = tl.X + w / 2 - size.Width / 2; y = tl.Y + h + 6; break;
            case "Left": x = tl.X + w + 8; y = tl.Y + h / 2 - size.Height / 2; break;
            default: x = tl.X - size.Width - 8; y = tl.Y + h / 2 - size.Height / 2; break;
        }
        x = Math.Clamp(x, 0, Math.Max(0, _overlay.ActualWidth - size.Width));
        y = Math.Clamp(y, 0, Math.Max(0, _overlay.ActualHeight - size.Height));
        Canvas.SetLeft(_label, x);
        Canvas.SetTop(_label, y);
    }

    // ------------------------------------------------------------------ clicks / drag

    // ------------------------------------------------------------------ live window thumbnails

    private void TrackHover(DockItem? item)
    {
        if (ReferenceEquals(item, _hoverCandidate)) return;
        _hoverCandidate = item;
        _hoverTimer.Stop();
        if (item == null || _preview || !_s.DockLivePreviews || _menuOpen || _dragItem != null) return;
        if (!item.Windows.Any(w => ShellInterop.IsWindow(w.Hwnd))) { if (_thumbs is { IsVisible: true }) _thumbs.HidePopup(); return; }
        if (_thumbs is { IsVisible: true } && _thumbs.CurrentKey == item.Key) return;
        // Already browsing previews: switch quickly between apps.
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_thumbs is { IsVisible: true } ? 120 : 400);
        _hoverTimer.Start();
    }

    private void ShowThumbnails()
    {
        var item = _hoverCandidate;
        if (item == null || _menuOpen || _dragItem != null || !item.View.IsMouseOver) return;
        var wins = item.Windows.Where(w => ShellInterop.IsWindow(w.Hwnd)).ToList();
        if (wins.Count == 0) return;
        try
        {
            _thumbs ??= new DockThumbnailPopup(_s);
            var tl = item.View.PointToScreen(new Point(0, 0));
            var br = item.View.PointToScreen(new Point(item.View.ActualWidth, item.View.ActualHeight));
            var anchor = new Rect(tl, br);
            if (_thumbs.ShowFor(item.Key, item.Label, wins, item.Img?.Source as System.Windows.Media.Imaging.BitmapSource, anchor, Edge, _dpi))
            {
                _label.Visibility = Visibility.Collapsed;
                _thumbHideTimer.Start();
            }
        }
        catch (Exception ex) { AppLog.Warn("Dock thumbnails: " + ex.Message); }
    }

    private void ThumbHideTick()
    {
        if (_thumbs == null || !_thumbs.IsVisible) { _thumbHideTimer.Stop(); return; }
        if (_menuOpen || _dragItem != null || (!_root.IsMouseOver && !_thumbs.ContainsCursor()))
        {
            _thumbs.HidePopup();
            _thumbHideTimer.Stop();
            _hoverCandidate = null;
        }
    }

    private void HideThumbnails()
    {
        _hoverTimer.Stop();
        _thumbs?.HidePopup();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        HideThumbnails();
        var item = _pressItem;
        _pressItem = null;
        if (item == null) return;
        item.View.ReleaseMouseCapture();
        if (_dragItem != null)
        {
            var drag = _dragItem;
            _dragItem = null;
            drag.View.Opacity = 1;
            FinishReorder(drag, e.GetPosition(_stack));
            e.Handled = true;
            return;
        }
        if (!item.View.IsMouseOver) return;
        e.Handled = true;
        ActivateItem(item);
    }

    private void ActivateItem(DockItem item)
    {
        if (_preview) return;
        var wins = item.Windows.Where(w => ShellInterop.IsWindow(w.Hwnd)).ToList();
        if (wins.Count == 0) { Launch(item); return; }
        if (wins.Count == 1) { ShellInterop.ActivateOrMinimize(wins[0].Hwnd); return; }
        var menu = NewMenu();
        menu.AddHeader(item.Label);
        foreach (var w in wins)
        {
            var hwnd = w.Hwnd;
            menu.AddItem(w.IsMinimized ? "" : "", string.IsNullOrWhiteSpace(w.Title) ? item.Label : w.Title, () => ShellInterop.Activate(hwnd));
        }
        menu.Open(item.View, MenuPlacement());
    }

    private void Launch(DockItem item)
    {
        if (item.LaunchTarget == null) return;
        try
        {
            if (item.Kind is DockItemKind.Folder or DockItemKind.Trash)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.LaunchTarget}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(item.LaunchTarget) { UseShellExecute = true });
            Bounce(item);
        }
        catch (Exception ex) { AppLog.Warn($"Dock launch '{item.Label}': {ex.Message}"); }
    }

    private void NewInstance(DockItem item)
    {
        if (_preview || item.Kind != DockItemKind.App) { if (!_preview && item.Kind != DockItemKind.Separator) Launch(item); return; }
        string? target = item.Pin ?? item.Exe;
        if (target == null) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); Bounce(item); }
        catch (Exception ex) { AppLog.Warn("Dock new instance: " + ex.Message); }
    }

    private void Bounce(DockItem item)
    {
        double amp = -BaseSize * 0.35;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(amp, KeyTime.FromPercent(0.22), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.45), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(amp * 0.5, KeyTime.FromPercent(0.65), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.85), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        var prop = Horizontal ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        double sign = Edge is "Top" or "Left" ? -1 : 1;
        if (sign < 0) foreach (EasingDoubleKeyFrame k in anim.KeyFrames) k.Value = -k.Value;
        item.Hop.BeginAnimation(prop, anim);
    }

    private void FinishReorder(DockItem drag, Point inStack)
    {
        var pinnedItems = _items.Where(i => i.IsPinned).ToList();
        if (pinnedItems.Count < 2) return;
        double axis = Horizontal ? inStack.X : inStack.Y;
        int newOrder = 0;
        foreach (var p in pinnedItems)
        {
            if (p == drag) continue;
            var tl = p.View.TranslatePoint(new Point(0, 0), _stack);
            double c = Horizontal ? tl.X + p.View.ActualWidth / 2 : tl.Y + p.View.ActualHeight / 2;
            if (axis > c) newOrder++;
        }
        var s = DesktopSettingsStore.Current;
        var pins = s.Dock.Pinned;
        int oldIndex = pins.FindIndex(x => string.Equals(x, drag.Pin, StringComparison.OrdinalIgnoreCase));
        if (oldIndex < 0) return;
        // translate "position among visible pinned items" to an index in the stored list
        var visibleOrder = pinnedItems.Where(p => p != drag).Select(p => p.Pin!).ToList();
        string pin = pins[oldIndex];
        pins.RemoveAt(oldIndex);
        int insertAt = newOrder >= visibleOrder.Count
            ? pins.Count
            : Math.Max(0, pins.FindIndex(x => string.Equals(x, visibleOrder[newOrder], StringComparison.OrdinalIgnoreCase)));
        pins.Insert(insertAt, pin);
        SaveSettings(s);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_preview || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var s = DesktopSettingsStore.Current;
        bool changed = false;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || s.Dock.Pinned.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
            s.Dock.Pinned.Add(p);
            changed = true;
        }
        if (changed) SaveSettings(s);
        e.Handled = true;
    }

    private void SaveSettings(DesktopSettings s)
    {
        try { DesktopSettingsStore.Save(s); } // DesktopFeature re-applies (→ ApplySettings) via Changed
        catch (Exception ex) { AppLog.Warn("Dock save: " + ex.Message); }
        if (!ReferenceEquals(s, _s)) ApplySettings(s); else RefreshItems(true);
    }

    // ------------------------------------------------------------------ context menu

    private PlacementMode MenuPlacement() => Edge switch
    {
        "Top" => PlacementMode.Bottom, "Left" => PlacementMode.Right, "Right" => PlacementMode.Left, _ => PlacementMode.Top
    };

    private GlassMenu NewMenu()
    {
        var m = new GlassMenu(_s);
        _menuOpen = true;
        _label.Visibility = Visibility.Collapsed;
        m.Closed += () => { _menuOpen = false; _lastNear = DateTime.UtcNow; };
        return m;
    }

    private void ShowMenu(DockItem? item, UIElement anchor)
    {
        if (_preview) return;
        HideThumbnails();
        var menu = NewMenu();
        if (item != null && item.Kind != DockItemKind.Separator)
        {
            menu.AddHeader(item.Label);
            var wins = item.Windows.Where(w => ShellInterop.IsWindow(w.Hwnd)).ToList();
            foreach (var w in wins.Take(8))
            {
                var hwnd = w.Hwnd;
                menu.AddItem("", string.IsNullOrWhiteSpace(w.Title) ? item.Label : w.Title, () => ShellInterop.Activate(hwnd));
            }
            menu.AddSeparator();
            if (item.Kind == DockItemKind.App && (item.Pin ?? item.Exe) != null)
                menu.AddItem("", "اجرای نسخه جدید", () => NewInstance(item));
            else if (item.LaunchTarget != null)
                menu.AddItem("", "باز کردن", () => Launch(item));

            if (!item.BuiltIn)
            {
                if (item.IsPinned) menu.AddItem("", "برداشتن از داک", () => Unpin(item));
                else if (item.Exe != null) menu.AddItem("", "پین به داک", () => PinItem(item.Exe));
            }
            string? file = item.Kind == DockItemKind.App ? (item.Exe ?? item.Pin) : item.Kind == DockItemKind.Folder ? item.LaunchTarget : null;
            if (file != null && (File.Exists(file) || Directory.Exists(file)))
                menu.AddItem("", "باز کردن محل فایل", () =>
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }));
            if (wins.Count > 0)
                menu.AddItem("", wins.Count > 1 ? $"بستن همه پنجره‌ها ({DesktopTheme.Digits(wins.Count.ToString(), true)})" : "بستن پنجره",
                    () => { foreach (var w in wins) ShellInterop.RequestClose(w.Hwnd); });
            menu.AddSeparator();
        }
        menu.AddItem("", "تنظیمات داک", () => DesktopEvents.RequestStudio("dock"));
        menu.Open(anchor, MenuPlacement());
    }

    private void PinItem(string path)
    {
        var s = DesktopSettingsStore.Current;
        if (!s.Dock.Pinned.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
        {
            s.Dock.Pinned.Add(path);
            SaveSettings(s);
        }
    }

    private void Unpin(DockItem item)
    {
        var s = DesktopSettingsStore.Current;
        if (s.Dock.Pinned.RemoveAll(x => string.Equals(x, item.Pin, StringComparison.OrdinalIgnoreCase)) > 0) SaveSettings(s);
    }

    // ------------------------------------------------------------------ auto-hide / full screen

    private void EdgeTick()
    {
        bool fs = _s.Dock.HideOnFullscreen && ShellInterop.IsFullscreenAppActive();
        if (fs != _fullscreenHidden)
        {
            _fullscreenHidden = fs;
            if (fs) Hide(); else Show();
        }
        if (fs || !_s.Dock.AutoHide) { if (_hidden) SlideTo(false); return; }

        NativeMethods.GetCursorPos(out var cp);
        bool near = IsCursorNear(cp.X, cp.Y);
        if (near || _menuOpen || _pressItem != null) { _lastNear = DateTime.UtcNow; if (_hidden) SlideTo(false); }
        else if (!_hidden && DateTime.UtcNow - _lastNear > TimeSpan.FromMilliseconds(700)) SlideTo(true);
    }

    private bool IsCursorNear(int x, int y)
    {
        try
        {
            // window rect in px
            var tl = PointToScreen(new Point(0, 0));
            var br = PointToScreen(new Point(ActualWidth, ActualHeight));
            if (x < tl.X || y < tl.Y || x > br.X || y > br.Y) return false;
            var ptl = _plate.PointToScreen(new Point(0, 0));
            var pbr = _plate.PointToScreen(new Point(_plate.ActualWidth, _plate.ActualHeight));
            double along0 = Horizontal ? Math.Min(ptl.X, pbr.X) : Math.Min(ptl.Y, pbr.Y);
            double along1 = Horizontal ? Math.Max(ptl.X, pbr.X) : Math.Max(ptl.Y, pbr.Y);
            double a = Horizontal ? x : y;
            if (a < along0 - 4 || a > along1 + 4) return false;
            double reach = (_hidden ? 3 : EdgeMargin + PlateThickness + BaseSize * (Mag - 1) + 8) * _dpi;
            return Edge switch
            {
                "Bottom" => y >= br.Y - reach,
                "Top" => y <= tl.Y + reach,
                "Left" => x <= tl.X + reach,
                _ => x >= br.X - reach
            };
        }
        catch { return false; }
    }

    private void SlideTo(bool hide)
    {
        _hidden = hide;
        double dist = hide ? EdgeMargin + PlateThickness - 2 : 0;
        if (Edge is "Top" or "Left") dist = -dist;
        var anim = new DoubleAnimation(dist, TimeSpan.FromMilliseconds(hide ? 260 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut }
        };
        _slide.BeginAnimation(Horizontal ? TranslateTransform.YProperty : TranslateTransform.XProperty, anim);
        if (hide) { _mouseAxis = null; StartRendering(); }
    }
}
