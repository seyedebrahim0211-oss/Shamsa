#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shamsa.Services;
using Shamsa.Services.Desktop;

namespace Shamsa.Views.Desktop;

/// <summary>
/// Floating Smart Dock with running & pinned apps, hover magnification animation,
/// window count badges, and real DWM Live Window Thumbnail Preview on hover (DwmRegisterThumbnail).
/// </summary>
public partial class SmartDockWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private Window? _livePreviewPopup;
    private IntPtr _activeThumbHandle = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumb, ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public SmartDockWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionDock();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => RefreshRunningApps();
        _refreshTimer.Start();
        RefreshRunningApps();
    }

    private void PositionDock()
    {
        var s = ShamsaProSettingsService.Current;
        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.PrimaryScreenHeight;
        Left = (screenW - ActualWidth) / 2.0;
        Top = s.DockPosition == DockPosition.Top ? 46 : screenH - Height - 12;
    }

    private void RefreshRunningApps()
    {
        DockItemsPanel.Children.Clear();
        var s = ShamsaProSettingsService.Current;
        double iconSize = s.DockIconSize;

        // Core Shamsa quick-launch pins
        AddDockButton("🖼", "مرکز تنظیمات نگارخانه", iconSize, null, () => ShamsaProBootstrap.OpenProHub());
        AddDockButton("🔍", "جستجوی سریع و کلیپ‌بورد", iconSize, null, () => ShamsaProBootstrap.OpenSpotlight());
        AddDockButton("🎨", "آیکون و قفل پوشه", iconSize, null, () => ShamsaProBootstrap.OpenFolderCustomizer());
        AddDockButton("🎥", "ضبط صفحه و اسکرین‌شات", iconSize, null, () => ShamsaProBootstrap.OpenScreenRecorderHud());

        // Live running Windows grouped by process
        var openWindows = ShamsaProBootstrap.WindowManager?.EnumerateTileableWindows() ?? new List<ManagedWindowInfo>();
        var grouped = openWindows.GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).Take(10);

        foreach (var group in grouped)
        {
            var first = group.First();
            string label = group.Count() > 1 ? $"{first.ProcessName} ({group.Count()})" : first.ProcessName;
            string shortGlyph = first.ProcessName.Length >= 2 ? first.ProcessName[..2].ToUpperInvariant() : first.ProcessName;
            AddDockButton(shortGlyph, $"{label}\n{first.Title}", iconSize, first.Handle, () => SetForegroundWindow(first.Handle));
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionDock));
    }

    private void AddDockButton(string glyph, string tooltip, double size, IntPtr? targetHwnd, Action onClick)
    {
        var scale = new ScaleTransform(1.0, 1.0);
        var btn = new Button
        {
            Content = glyph,
            ToolTip = tooltip,
            Width = size,
            Height = size,
            Margin = new Thickness(4, 0, 4, 0),
            Background = (Brush)FindResource("Surface2Brush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.Bold,
            RenderTransformOrigin = new Point(0.5, 0.8),
            RenderTransform = scale
        };

        btn.Click += (_, _) => onClick();
        btn.MouseEnter += (_, _) =>
        {
            double target = ShamsaProSettingsService.Current.ReduceAnimations ? 1.0 : ShamsaProSettingsService.Current.DockMagnificationScale;
            AnimateScale(scale, target);
            if (targetHwnd.HasValue && ShamsaProSettingsService.Current.DockLiveWindowPreview)
                ShowLiveWindowPreview(targetHwnd.Value, btn);
        };
        btn.MouseLeave += (_, _) =>
        {
            AnimateScale(scale, 1.0);
            HideLiveWindowPreview();
        };

        DockItemsPanel.Children.Add(btn);
    }

    private static void AnimateScale(ScaleTransform transform, double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140));
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void ShowLiveWindowPreview(IntPtr sourceHwnd, FrameworkElement anchor)
    {
        HideLiveWindowPreview();
        try
        {
            var pt = anchor.PointToScreen(new Point(0, 0));
            _livePreviewPopup = new Window
            {
                Width = 260,
                Height = 160,
                Left = Math.Max(12, pt.X - 100),
                Top = Math.Max(46, pt.Y - 172),
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Black
            };
            _livePreviewPopup.Show();

            IntPtr destHwnd = new WindowInteropHelper(_livePreviewPopup).Handle;
            if (DwmRegisterThumbnail(destHwnd, sourceHwnd, out _activeThumbHandle) == 0 && _activeThumbHandle != IntPtr.Zero)
            {
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 | 0x4 | 0x8, // RECTDESTINATION | OPACITY | VISIBLE
                    rcDestination = new RECT { Left = 4, Top = 4, Right = 256, Bottom = 156 },
                    opacity = 255,
                    fVisible = true
                };
                DwmUpdateThumbnailProperties(_activeThumbHandle, ref props);
            }
        }
        catch { HideLiveWindowPreview(); }
    }

    private void HideLiveWindowPreview()
    {
        if (_activeThumbHandle != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(_activeThumbHandle);
            _activeThumbHandle = IntPtr.Zero;
        }
        _livePreviewPopup?.Close();
        _livePreviewPopup = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        HideLiveWindowPreview();
        base.OnClosed(e);
    }
}
