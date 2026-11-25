using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Recording;

/// <summary>Floating glass toolbar while recording. Hidden from capture on Windows 10 2004+.</summary>
internal sealed class RecordingToolbar : Window
{
    private readonly Ellipse _dot = new() { Width = 11, Height = 11, Fill = OverlayNative.Frozen("#F43F5E"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, MinWidth = 64 };
    private readonly TextBlock _size = new() { Foreground = OverlayNative.Frozen("#F6D379"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, MinWidth = 56 };
    private readonly Button _pause;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Func<(RecorderState State, TimeSpan Elapsed, long Bytes)> _status;

    public RecordingToolbar(Func<(RecorderState, TimeSpan, long)> status, Action pauseResume, Action stop, Action screenshot)
    {
        _status = status;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Vazirmatn, Segoe UI, Tahoma");

        _pause = MakeButton("⏸", "مکث / ادامه", (_, _) => pauseResume());
        var stopBtn = MakeButton("⏹", "توقف و ذخیره", (_, _) => stop());
        stopBtn.Foreground = OverlayNative.Frozen("#FB7185");
        var shotBtn = MakeButton("📷", "اسکرین‌شات", (_, _) => screenshot());

        var panel = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
        panel.Children.Add(new TextBlock { Text = "⠿", Foreground = OverlayNative.Frozen("#64748B"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) });
        panel.Children.Add(_dot);
        panel.Children.Add(new TextBlock { Text = "REC", Foreground = OverlayNative.Frozen("#FB7185"), FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 8, 0) });
        panel.Children.Add(_time);
        panel.Children.Add(_size);
        panel.Children.Add(_pause);
        panel.Children.Add(stopBtn);
        panel.Children.Add(shotBtn);

        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 5, 6, 5),
            BorderThickness = new Thickness(1),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xAA, 0xE5, 0xB8, 0x5B), 45),
            Background = new LinearGradientBrush(Color.FromArgb(0xF0, 0x0D, 0x1A, 0x33), Color.FromArgb(0xF4, 0x06, 0x0C, 0x1A), 90),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 },
            Margin = new Thickness(10),
            Child = panel
        };

        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) { try { DragMove(); } catch { } } };
        SourceInitialized += (_, _) => { OverlayNative.MakeToolNoActivate(this); OverlayNative.ExcludeFromCapture(this); };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); StartBlink(); };
        Closed += (_, _) => _timer.Stop();
    }

    private static Button MakeButton(string glyph, string tip, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = glyph, ToolTip = tip, Width = 34, Height = 30, Margin = new Thickness(3, 0, 0, 0), FontSize = 14,
            Background = OverlayNative.Frozen("#1A2C50"), Foreground = Brushes.White, BorderBrush = OverlayNative.Frozen("#2A436B"),
            Cursor = Cursors.Hand, Focusable = false, Padding = new Thickness(0)
        };
        b.Click += click;
        return b;
    }

    private void StartBlink()
    {
        var anim = new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        _dot.BeginAnimation(OpacityProperty, anim);
    }

    public void Refresh()
    {
        var (state, elapsed, bytes) = _status();
        _time.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        _size.Text = bytes > 0 ? FileKinds.FormatSize(bytes).Trim('‪', '‬') : "";
        bool paused = state == RecorderState.Paused;
        _pause.Content = paused ? "▶" : "⏸";
        _dot.Fill = paused ? OverlayNative.Frozen("#FBBF24") : OverlayNative.Frozen("#F43F5E");
        _pause.IsEnabled = state is RecorderState.Recording or RecorderState.Paused;
    }

    /// <summary>Places the toolbar at the top-centre of the monitor that holds the capture region.</summary>
    public void PlaceFor(PixelRect region)
    {
        UpdateLayout();
        var centre = new Point(region.X + region.Width / 2.0, region.Y + 2);
        var area = ScreenHelper.WorkAreaDipAt(this, centre);
        double w = ActualWidth > 0 ? ActualWidth : 360;
        Left = area.Left + (area.Width - w) / 2;
        Top = area.Top + 6;
    }
}
