using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Modes;

/// <summary>A calm glass card in the top-right corner (where Persian reading starts) with an optional action button. Never steals focus.</summary>
public static class SmartToast
{
    private static Window? _window;
    private static DispatcherTimer? _timer;

    /// <summary>Set by tests: toasts are recorded here instead of shown.</summary>
    public static List<string>? Captured { get; set; }

    public static void Show(string title, string body, string glyph = "", string? actionText = null, Action? action = null, double seconds = 9)
    {
        if (Captured != null) { Captured.Add(title + " | " + body); return; }
        try
        {
            Close();
            var s = DesktopSettingsStore.Current;
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22,
                Foreground = DesktopTheme.AccentBrush(s), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
            };
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), MaxWidth = 340 };
            text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = DesktopTheme.TextBrush, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = body, FontSize = 13, Foreground = DesktopTheme.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            if (actionText != null && action != null)
                buttons.Children.Add(MakeButton(actionText, true, () => { Close(); try { action(); } catch (Exception ex) { AppLog.Warn("Smart toast action: " + ex.Message); } }));
            buttons.Children.Add(MakeButton("بستن", false, Close));
            text.Children.Add(buttons);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(icon);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var card = new Border
            {
                Child = row, Padding = new Thickness(16, 14, 16, 12), Margin = new Thickness(16),
                CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
                Background = DesktopTheme.GlassBrush(s, 1.4), BorderBrush = DesktopTheme.BorderBrush(s),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.4 }
            };
            _window = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, Content = card, FlowDirection = FlowDirection.RightToLeft,
                FontFamily = DesktopTheme.Font, Title = "نگارخانه — یادآور", Opacity = 0
            };
            _window.Show();
            _window.UpdateLayout();
            var wa = SystemParameters.WorkArea;
            _window.Left = wa.Right - _window.ActualWidth - 4;
            _window.Top = wa.Top + 40;
            _window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _timer.Tick += (_, _) => FadeOut();
            _timer.Start();
            card.MouseEnter += (_, _) => _timer?.Stop();
            card.MouseLeave += (_, _) => _timer?.Start();
        }
        catch (Exception ex) { AppLog.Warn("Smart toast: " + ex.Message); }
    }

    private static Button MakeButton(string text, bool primary, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), MinWidth = 64 };
        string style = primary ? "AccentButton" : "GhostButton";
        if (Application.Current?.TryFindResource(style) is Style st && st.TargetType.IsInstanceOfType(b)) b.Style = st;
        b.Click += (_, _) => click();
        return b;
    }

    private static void FadeOut()
    {
        _timer?.Stop();
        var w = _window;
        if (w == null) return;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        anim.Completed += (_, _) => { if (_window == w) Close(); };
        w.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public static void Close()
    {
        _timer?.Stop();
        _timer = null;
        try { _window?.Close(); } catch { }
        _window = null;
    }
}
