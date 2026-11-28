using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

/// <summary>An input-disabled child behind Explorer's icon view on the selected monitor.</summary>
internal sealed class LivingWallpaper
{
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder cls, int size);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DesktopSettings? _settings;
    private HwndSource? _surface;
    private string? _path;
    private System.Drawing.Rectangle _bounds;
    private string? _lastError;

    public bool IsRunning => _surface != null;
    public LivingWallpaper() => _timer.Tick += (_, _) => Update();
    public void Apply(DesktopSettings s)
    {
        _settings = s;
        if (!s.Enabled || !s.AnimateWallpaper) { Stop(); return; }
        _timer.Start(); Update();
    }
    public void Stop() { _timer.Stop(); Remove(); }
    private void Remove()
    {
        var old = _surface;
        _surface = null; _path = null;
        old?.Dispose();
    }
    private void Update()
    {
        try
        {
            var s = _settings!;
            var active = PersonalizeStore.Data.Applied;
            string? path = active.WallpaperPath;
            if (string.IsNullOrEmpty(path) || active.SolidColorHex != null || active.WallpaperStyle != "Fill" ||
                !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || !File.Exists(path) ||
                System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline ||
                ShellInterop.IsFullscreenAppActive(DesktopDisplay.Monitor(s)))
            { Remove(); return; }
            var bounds = DesktopDisplay.Target(s).Bounds;
            if (_surface != null && IsWindow(_surface.Handle) && _path == path && bounds == _bounds) return;
            Remove();
            IntPtr parent = IntPtr.Zero, icons = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                var cls = new System.Text.StringBuilder(64);
                GetClassName(window, cls, cls.Capacity);
                if (cls.ToString() is not ("Progman" or "WorkerW")) return true;
                icons = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (icons == IntPtr.Zero) return true;
                parent = window; return false;
            }, IntPtr.Zero);
            if (parent == IntPtr.Zero) return;
            var origin = bounds.Location;
            if (!ScreenToClient(parent, ref origin)) return;
            var src = new BitmapImage();
            src.BeginInit(); src.CacheOption = BitmapCacheOption.OnLoad;
            src.DecodePixelWidth = Math.Min(4096, Math.Max(1280, bounds.Width)); src.UriSource = new Uri(path);
            src.EndInit(); src.Freeze();
            var zoom = new ScaleTransform(1.025, 1.025);
            var img = new Image { Source = src, Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(.45, .54), RenderTransform = zoom };
            var surface = new HwndSource(new HwndSourceParameters("Negarkhaneh wallpaper") {
                ParentWindow = parent, WindowStyle = 0x40000000 | 0x08000000, ExtendedWindowStyle = 0x08000020,
                PositionX = origin.X, PositionY = origin.Y, Width = bounds.Width, Height = bounds.Height
            });
            _surface = surface;
            surface.RootVisual = new Grid { ClipToBounds = true, Children = { img } };
            if (!SetWindowPos(surface.Handle, icons, origin.X, origin.Y, bounds.Width, bounds.Height, 0x10 | 0x40))
            { Remove(); return; }
            var motion = new DoubleAnimation(1.025, 1.075, TimeSpan.FromSeconds(18)) { AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(motion, 20);
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
            _path = path; _bounds = bounds; _lastError = null;
        }
        catch (Exception ex)
        {
            Remove();
            if (_lastError != ex.Message) AppLog.Warn("Wallpaper motion: " + ex.Message);
            _lastError = ex.Message;
        }
    }
}
