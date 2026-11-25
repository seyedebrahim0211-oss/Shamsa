using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace Shamsa.Recording;

internal static class OverlayNative
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

    public static void MakeClickThrough(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        long s = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(s | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED));
    }

    public static void MakeToolNoActivate(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        long s = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(s | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// <summary>Hides the window from screen capture (Windows 10 2004+); silently ignored elsewhere.</summary>
    public static void ExcludeFromCapture(Window w)
    {
        try { SetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }
    }

    public static void CoverVirtualScreen(Window w)
    {
        w.Left = SystemParameters.VirtualScreenLeft;
        w.Top = SystemParameters.VirtualScreenTop;
        w.Width = SystemParameters.VirtualScreenWidth;
        w.Height = SystemParameters.VirtualScreenHeight;
    }

    public static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>Dimmed full-desktop overlay to drag-select a capture region (physical pixels). Esc / right click cancels.</summary>
internal sealed class RegionSelectorWindow : Window
{
    private readonly TaskCompletionSource<PixelRect?> _result = new();
    private readonly ShapePath _shade = new() { Fill = OverlayNative.Frozen("#99050A14") };
    private readonly Rectangle _border = new() { Stroke = OverlayNative.Frozen("#F6D379"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 5, 3 }, Visibility = Visibility.Collapsed };
    private readonly Border _label = new()
    {
        Background = OverlayNative.Frozen("#E60B1324"), BorderBrush = OverlayNative.Frozen("#2DD4BF"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight };
    private readonly Canvas _canvas = new();
    private (int X, int Y)? _start;
    private PixelRect _current;

    public RegionSelectorWindow(string hint)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        OverlayNative.CoverVirtualScreen(this);
        _label.Child = _labelText;
        var hintBox = new Border
        {
            Background = OverlayNative.Frozen("#EE0B1324"), BorderBrush = OverlayNative.Frozen("#F6D379"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 8, 16, 8),
            Child = new TextBlock { Text = hint, Foreground = Brushes.White, FontSize = 14, FontFamily = new FontFamily("Vazirmatn, Segoe UI, Tahoma"), FlowDirection = FlowDirection.RightToLeft }
        };
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_border);
        _canvas.Children.Add(_label);
        var root = new Grid();
        root.Children.Add(_canvas);
        hintBox.HorizontalAlignment = HorizontalAlignment.Center;
        hintBox.VerticalAlignment = VerticalAlignment.Top;
        hintBox.Margin = new Thickness(0, 40, 0, 0);
        hintBox.IsHitTestVisible = false;
        root.Children.Add(hintBox);
        Content = root;
        Loaded += (_, _) => { UpdateShade(Rect.Empty); Activate(); Focus(); };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, _) => Finish(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Deactivated += (_, _) => { if (_start == null) Activate(); };
    }

    public static Task<PixelRect?> SelectAsync(string hint = "برای انتخاب ناحیه بکشید · Esc برای لغو")
    {
        var w = new RegionSelectorWindow(hint);
        w.Show();
        return w._result.Task;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = RecorderNative.CursorPixels();
        CaptureMouse();
        _border.Visibility = _label.Visibility = Visibility.Visible;
        OnMove(sender, e);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } s) return;
        var (cx, cy) = RecorderNative.CursorPixels();
        _current = new PixelRect(Math.Min(s.X, cx), Math.Min(s.Y, cy), Math.Abs(cx - s.X), Math.Abs(cy - s.Y));
        Point a = PointFromScreenSafe(_current.X, _current.Y), b = PointFromScreenSafe(_current.Right, _current.Bottom);
        var r = new Rect(a, b);
        Canvas.SetLeft(_border, r.X);
        Canvas.SetTop(_border, r.Y);
        _border.Width = r.Width;
        _border.Height = r.Height;
        _labelText.Text = $"{_current.Width & ~1} × {_current.Height & ~1}";
        Canvas.SetLeft(_label, r.X);
        Canvas.SetTop(_label, r.Y > 30 ? r.Y - 28 : r.Bottom + 6);
        UpdateShade(r);
    }

    private Point PointFromScreenSafe(int x, int y)
    {
        try { return PointFromScreen(new Point(x, y)); } catch { return new Point(x, y); }
    }

    private void UpdateShade(Rect hole)
    {
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height)));
        if (!hole.IsEmpty) g.Children.Add(new RectangleGeometry(hole));
        _shade.Data = g;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start == null) return;
        ReleaseMouseCapture();
        Finish(_current.Width >= 16 && _current.Height >= 16 ? _current : null);
    }

    private void Finish(PixelRect? result)
    {
        if (_result.Task.IsCompleted) return;
        Hide();
        // Let DWM remove the overlay before anything captures the screen.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            await Task.Delay(120);
            Close();
            _result.TrySetResult(result);
        });
    }
}

/// <summary>3-2-1 countdown, hidden from capture, shown at the centre of the capture region.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly TextBlock _text = new() { FontSize = 96, FontWeight = FontWeights.Bold, Foreground = OverlayNative.Frozen("#F6D379"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = Height = 200;
        Content = new Border
        {
            Width = 180, Height = 180, CornerRadius = new CornerRadius(90),
            Background = OverlayNative.Frozen("#D90B1324"), BorderBrush = OverlayNative.Frozen("#2DD4BF"), BorderThickness = new Thickness(3),
            Child = _text
        };
        SourceInitialized += (_, _) => { OverlayNative.MakeClickThrough(this); OverlayNative.ExcludeFromCapture(this); };
    }

    public static async Task RunAsync(PixelRect region, int seconds = 3)
    {
        var w = new CountdownWindow();
        var dpi = Services.ScreenHelper.PixelsToDip(Application.Current?.MainWindow ?? w, new Point(region.X + region.Width / 2.0, region.Y + region.Height / 2.0));
        w.Left = dpi.X - 100;
        w.Top = dpi.Y - 100;
        w.Show();
        for (int i = seconds; i >= 1; i--)
        {
            w._text.Text = i.ToString();
            var anim = new DoubleAnimation(1.4, 1.0, TimeSpan.FromMilliseconds(350)) { EasingFunction = new QuadraticEase() };
            var st = new ScaleTransform(1, 1, 0, 0);
            w._text.RenderTransformOrigin = new Point(0.5, 0.5);
            w._text.RenderTransform = st;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            await Task.Delay(1000);
        }
        w.Close();
        await Task.Delay(120);
    }
}

/// <summary>Click-through overlay that draws a fading ring at each mouse click so it appears in the recording.</summary>
internal sealed class ClickHighlightOverlay : Window
{
    private readonly Canvas _canvas = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private bool _wasLeft, _wasRight;
    private static readonly Brush LeftBrush = OverlayNative.Frozen("#F6D379");
    private static readonly Brush RightBrush = OverlayNative.Frozen("#2DD4BF");

    public ClickHighlightOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        OverlayNative.CoverVirtualScreen(this);
        Content = _canvas;
        SourceInitialized += (_, _) => OverlayNative.MakeClickThrough(this);
        _timer.Tick += Tick;
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick(object? sender, EventArgs e)
    {
        bool left = RecorderNative.IsKeyDown(0x01), right = RecorderNative.IsKeyDown(0x02);
        if (left && !_wasLeft) Ripple(LeftBrush);
        if (right && !_wasRight) Ripple(RightBrush);
        _wasLeft = left;
        _wasRight = right;
    }

    private void Ripple(Brush brush)
    {
        var (x, y) = RecorderNative.CursorPixels();
        Point p;
        try { p = PointFromScreen(new Point(x, y)); } catch { return; }
        const double size = 44;
        var ring = new Ellipse { Width = size, Height = size, Stroke = brush, StrokeThickness = 4, Fill = OverlayNative.Frozen("#33F6D379"), RenderTransformOrigin = new Point(0.5, 0.5) };
        var st = new ScaleTransform(0.3, 0.3);
        ring.RenderTransform = st;
        Canvas.SetLeft(ring, p.X - size / 2);
        Canvas.SetTop(ring, p.Y - size / 2);
        _canvas.Children.Add(ring);
        var dur = TimeSpan.FromMilliseconds(480);
        var grow = new DoubleAnimation(0.3, 1.0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(1.0, 0.0, dur);
        fade.Completed += (_, _) => _canvas.Children.Remove(ring);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }
}
