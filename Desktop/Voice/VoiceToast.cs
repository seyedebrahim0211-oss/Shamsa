using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Desktop.Voice;

/// <summary>Small glass toast at the top of the screen showing what the voice recogniser heard.</summary>
internal static class VoiceToast
{
    private static Window? _window;
    private static TextBlock? _text, _glyph;
    private static DispatcherTimer? _timer;

    public static void Show(string text, bool understood)
    {
        try
        {
            var s = DesktopSettingsStore.Current;
            if (_window == null)
            {
                _glyph = new TextBlock { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                _text = new TextBlock { FontSize = 14, Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(_glyph);
                row.Children.Add(_text);
                var card = new Border
                {
                    Child = row, Padding = new Thickness(16, 9, 16, 10), Margin = new Thickness(14),
                    CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
                    Background = DesktopTheme.GlassBrush(s, 1.35), BorderBrush = DesktopTheme.BorderBrush(s),
                    Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.4 }
                };
                _window = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                    SizeToContent = SizeToContent.WidthAndHeight, Content = card, FlowDirection = FlowDirection.RightToLeft,
                    FontFamily = DesktopTheme.Font, IsHitTestVisible = false, Title = "فرمان صوتی"
                };
                _window.SourceInitialized += (_, _) => NativeMethods.MakeClickThroughToolWindow(new WindowInteropHelper(_window).Handle);
            }
            _glyph!.Text = understood ? "" : "";
            _glyph.Foreground = understood ? DesktopTheme.AccentBrush(s) : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            _text!.Text = text;
            _window.Opacity = 1;
            _window.BeginAnimation(UIElement.OpacityProperty, null);
            if (!_window.IsVisible) _window.Show();
            _window.UpdateLayout();
            var wa = SystemParameters.WorkArea;
            _window.Left = wa.Left + (wa.Width - _window.ActualWidth) / 2;
            _window.Top = wa.Top + 6;

            _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
        }
        catch (Exception ex) { AppLog.Warn("Voice toast: " + ex.Message); }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        if (_window == null) return;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        anim.Completed += (_, _) => { if (_window != null && _window.Opacity < 0.05) _window.Hide(); };
        _window.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public static void Close()
    {
        _timer?.Stop();
        try { _window?.Close(); } catch { }
        _window = null;
    }
}
