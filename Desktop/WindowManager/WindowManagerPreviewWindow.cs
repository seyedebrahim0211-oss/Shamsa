using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.WindowManager;

/// <summary>
/// Schematic of what auto-tiling would do on the focused monitor: computed from the real window list,
/// never applied. Shows app names (not window titles) for privacy.
/// </summary>
public sealed class WindowManagerPreviewWindow : Window
{
    public WindowManagerPreviewWindow(DesktopSettings settings, WindowManagerModule module)
    {
        Title = "پیش‌نمایش مدیر پنجره‌ها";
        Width = 560; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = DesktopTheme.Font;
        Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x12, 0x24));

        var cfg = settings.WindowManager;
        var layout = module.IsRunning && module.TilingActive ? module.CurrentLayout : TileLayout.Parse(cfg.Layout);
        var accent = DesktopTheme.Accent(settings);

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "چیدمان: " + TileLayout.PersianName(layout),
            FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = DesktopTheme.TextBrush
        });
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);

        // The schematic is a physical screen layout: always left→right.
        var canvas = new Canvas { FlowDirection = FlowDirection.LeftToRight, ClipToBounds = true };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = DesktopTheme.BorderBrush(settings),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x1E, 0x38)),
            Child = canvas
        };

        int count = 0;
        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            IntPtr monitor = ShellInterop.MonitorFromWindow(fg, 2);
            var windows = module.ManageableWindows(cfg).Where(w => w.Monitor == monitor).ToList();
            var (_, work) = ShellInterop.GetMonitorRects(monitor);
            if (work.IsEmpty) work = new Rect(0, 0, 1920, 1040);
            if (windows.Count == 0)
                windows = module.ManageableWindows(cfg).Take(6).Select(w => w with { Monitor = monitor }).ToList();
            var plan = module.PlanLayout(windows, layout, cfg.Gap, cfg.OuterPadding);
            count = plan.Count;

            const double W = 526, H = 296;
            double scale = Math.Min(W / work.Width, H / work.Height);
            canvas.Width = work.Width * scale; canvas.Height = work.Height * scale;
            int idx = 0;
            foreach (var (w, target, area) in plan)
            {
                var tile = new Border
                {
                    Width = Math.Max(8, target.Width * scale), Height = Math.Max(8, target.Height * scale),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)),
                    BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(1.2),
                    Child = BuildTileContent(w, ++idx)
                };
                Canvas.SetLeft(tile, (target.X - area.X) * scale);
                Canvas.SetTop(tile, (target.Y - area.Y) * scale);
                canvas.Children.Add(tile);
                if (layout == TileLayoutKind.Monocle) break; // stacked; one is enough to show
            }
        }
        catch (Exception ex) { AppLog.Warn("WM preview: " + ex.Message); }

        header.Children.Add(new TextBlock
        {
            Text = count == 0
                ? "پنجره‌ای برای چیدن پیدا نشد."
                : $"{DesktopTheme.Digits(count.ToString(), true)} پنجره · فاصله {DesktopTheme.Digits(cfg.Gap.ToString(), true)} · حاشیه {DesktopTheme.Digits(cfg.OuterPadding.ToString(), true)} پیکسل · فقط پیش‌نمایش، چیزی جابه‌جا نمی‌شود",
            FontSize = 12, Foreground = DesktopTheme.MutedBrush, Margin = new Thickness(0, 3, 0, 0)
        });
        root.Children.Add(new Viewbox { Child = frame, Stretch = Stretch.Uniform });
        Content = root;
    }

    private static UIElement BuildTileContent(AppWindow w, int index)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = ShellInterop.GetAppIcon(w.ExePath, 32);
        if (icon != null) panel.Children.Add(new Image { Source = icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 0, 3) });
        panel.Children.Add(new TextBlock
        {
            Text = $"{index} · {ShellInterop.FriendlyAppName(w.ExePath)}",
            Foreground = DesktopTheme.TextBrush, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(4, 0, 4, 0)
        });
        return panel;
    }
}
