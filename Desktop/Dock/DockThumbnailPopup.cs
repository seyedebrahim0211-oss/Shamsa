using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.Dock;

/// <summary>
/// Glass popup of live DWM window thumbnails shown when hovering a running app in the dock.
/// Thumbnails are composed by DWM straight into this window — nothing is captured or stored.
/// The window is NOT layered (DWM thumbnails don't render into per-pixel-alpha windows).
/// </summary>
internal sealed class DockThumbnailPopup : Window
{
    private const double ThumbMaxW = 220, ThumbMaxH = 140;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

    private const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    private sealed class Cell
    {
        public AppWindow Window = null!;
        public Border Host = null!;        // area where DWM draws
        public IntPtr Thumb;
        public Size Source;
    }

    private readonly List<Cell> _cells = new();
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly Border _card = new();
    private DesktopSettings _s;
    private IntPtr _hwnd;

    /// <summary>The dock item key this popup currently shows (null when hidden).</summary>
    public string? CurrentKey { get; private set; }

    public DockThumbnailPopup(DesktopSettings s)
    {
        _s = s;
        Title = "پیش‌نمایش پنجره‌ها";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = DesktopTheme.Font;
        FlowDirection = FlowDirection.LeftToRight; // thumbnail math in screen space
        UseLayoutRounding = true;
        _card.Child = _panel;
        _card.Padding = new Thickness(8);
        Content = _card;
        ApplySettings(s);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeToolWindowNoActivate(_hwnd);
            NativeMethods.TryRoundCorners(_hwnd);
        };
        Closed += (_, _) => UnregisterAll();
    }

    public void ApplySettings(DesktopSettings s)
    {
        _s = s;
        // Opaque-ish tint: a non-layered window can't be see-through; use the dense glass colour.
        var g = DesktopTheme.GlassBrush(s, 1.0).Color;
        Background = new SolidColorBrush(Color.FromRgb(g.R, g.G, g.B));
        _card.BorderBrush = DesktopTheme.BorderBrush(s);
        _card.BorderThickness = new Thickness(1);
    }

    /// <summary>
    /// Shows live thumbnails for <paramref name="windows"/> next to the dock icon whose screen
    /// rectangle (physical pixels) is <paramref name="anchorPx"/>. Returns false when DWM refused every thumbnail.
    /// </summary>
    public bool ShowFor(string key, string label, IReadOnlyList<AppWindow> windows, BitmapSource? icon, Rect anchorPx, string edge, double dpi)
    {
        UnregisterAll();
        _cells.Clear();
        _panel.Children.Clear();
        _panel.Orientation = edge is "Left" or "Right" ? Orientation.Vertical : Orientation.Horizontal;
        CurrentKey = key;

        if (_hwnd == IntPtr.Zero)
        {
            // Create the HWND off-screen so thumbnails can be registered before the first show.
            Left = -32000; Top = -32000;
            Show();
        }

        foreach (var w in windows.Take(8))
        {
            if (!ShellInterop.IsWindow(w.Hwnd)) continue;
            var cell = new Cell { Window = w };
            if (DwmRegisterThumbnail(_hwnd, w.Hwnd, out var thumb) == 0 && thumb != IntPtr.Zero)
            {
                cell.Thumb = thumb;
                if (DwmQueryThumbnailSourceSize(thumb, out var size) == 0 && size.cx > 0 && size.cy > 0)
                    cell.Source = new Size(size.cx, size.cy);
            }
            _cells.Add(cell);
            _panel.Children.Add(BuildCell(cell, label, icon));
        }

        if (_cells.Count == 0 || _cells.All(c => c.Thumb == IntPtr.Zero))
        {
            HidePopup();
            return false;
        }

        if (!IsVisible) { Left = -32000; Top = -32000; Show(); }
        UpdateLayout();
        PositionNear(anchorPx, edge, dpi);
        UpdateLayout();
        PlaceThumbnails(dpi);
        return true;
    }

    private UIElement BuildCell(Cell cell, string label, BitmapSource? icon)
    {
        double w = ThumbMaxW, h = ThumbMaxH;
        if (cell.Source.Width > 0)
        {
            double scale = Math.Min(ThumbMaxW / cell.Source.Width, ThumbMaxH / cell.Source.Height);
            w = Math.Max(60, cell.Source.Width * scale);
            h = Math.Max(40, cell.Source.Height * scale);
        }

        // Icon behind the thumbnail: visible for minimised windows (DWM draws nothing for them).
        var iconImg = new Image { Source = icon, Width = 40, Height = 40, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cell.Host = new Border { Width = w, Height = h, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), CornerRadius = new CornerRadius(4), Child = iconImg };

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(cell.Window.Title) ? label : cell.Window.Title,
            Foreground = DesktopTheme.TextBrush, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = w - 26, VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = HasRtl(cell.Window.Title) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        var close = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
            Foreground = DesktopTheme.MutedBrush, Padding = new Thickness(5, 3, 5, 3), Cursor = Cursors.Hand,
            ToolTip = "بستن پنجره", VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent
        };
        var hwnd = cell.Window.Hwnd;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShellInterop.RequestClose(hwnd);
            HidePopup();
        };
        close.MouseEnter += (_, _) => close.Foreground = Brushes.White;
        close.MouseLeave += (_, _) => close.Foreground = DesktopTheme.MutedBrush;

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 5), Width = w };
        System.Windows.Controls.DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(cell.Host);
        var outer = new Border
        {
            Child = stack, Padding = new Thickness(6), Margin = new Thickness(3), CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent, Cursor = Cursors.Hand
        };
        var accent = DesktopTheme.Accent(_s);
        outer.MouseEnter += (_, _) => outer.Background = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        outer.MouseLeave += (_, _) => outer.Background = Brushes.Transparent;
        outer.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            ShellInterop.Activate(hwnd);
            HidePopup();
        };
        return outer;
    }

    private static bool HasRtl(string? s) => s != null && s.Any(c => c >= '֐' && c <= 'ࣿ');

    private void PositionNear(Rect anchorPx, string edge, double dpi)
    {
        double w = ActualWidth, h = ActualHeight;
        double ax = anchorPx.X / dpi, ay = anchorPx.Y / dpi, aw = anchorPx.Width / dpi, ah = anchorPx.Height / dpi;
        double x, y;
        switch (edge)
        {
            case "Top": x = ax + aw / 2 - w / 2; y = ay + ah + 10; break;
            case "Left": x = ax + aw + 10; y = ay + ah / 2 - h / 2; break;
            case "Right": x = ax - w - 10; y = ay + ah / 2 - h / 2; break;
            default: x = ax + aw / 2 - w / 2; y = ay - h - 10; break;
        }
        // Keep on the monitor of the anchor.
        var mon = ShellInteropMonitor(anchorPx);
        if (!mon.IsEmpty)
        {
            double ml = mon.Left / dpi, mt = mon.Top / dpi, mr = mon.Right / dpi, mb = mon.Bottom / dpi;
            x = Math.Clamp(x, ml + 4, Math.Max(ml + 4, mr - w - 4));
            y = Math.Clamp(y, mt + 4, Math.Max(mt + 4, mb - h - 4));
        }
        Left = x; Top = y;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private static Rect ShellInteropMonitor(Rect anchorPx)
    {
        var mon = MonitorFromPoint(new POINT { X = (int)(anchorPx.X + anchorPx.Width / 2), Y = (int)(anchorPx.Y + anchorPx.Height / 2) }, 2);
        return ShellInterop.GetMonitorRects(mon).Monitor;
    }

    private void PlaceThumbnails(double dpi)
    {
        foreach (var cell in _cells)
        {
            if (cell.Thumb == IntPtr.Zero || cell.Host.ActualWidth <= 0) continue;
            Point tl;
            try { tl = cell.Host.TranslatePoint(new Point(0, 0), _card); } catch { continue; }
            // _card is the window content at (0,0) of the client area.
            var dest = new RECT
            {
                Left = (int)Math.Round(tl.X * dpi), Top = (int)Math.Round(tl.Y * dpi),
                Right = (int)Math.Round((tl.X + cell.Host.ActualWidth) * dpi), Bottom = (int)Math.Round((tl.Y + cell.Host.ActualHeight) * dpi)
            };
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = dest, opacity = 255, fVisible = true, fSourceClientAreaOnly = false
            };
            if (DwmUpdateThumbnailProperties(cell.Thumb, ref props) != 0)
                AppLog.Warn("DWM thumbnail update failed");
        }
    }

    /// <summary>True while the cursor is over the popup.</summary>
    public bool ContainsCursor()
    {
        if (!IsVisible) return false;
        var p = System.Windows.Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var r = new Rect(Left * dpi, Top * dpi, ActualWidth * dpi, ActualHeight * dpi);
        r.Inflate(6, 6);
        return r.Contains(p.X, p.Y);
    }

    public void HidePopup()
    {
        UnregisterAll();
        CurrentKey = null;
        if (IsVisible) Hide();
    }

    private void UnregisterAll()
    {
        foreach (var c in _cells)
        {
            if (c.Thumb != IntPtr.Zero) { try { DwmUnregisterThumbnail(c.Thumb); } catch { } c.Thumb = IntPtr.Zero; }
        }
    }
}
