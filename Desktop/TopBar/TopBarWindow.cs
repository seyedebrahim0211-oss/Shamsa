using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.TopBar;

/// <summary>
/// One bar on one monitor. In live mode it is a topmost, never-activating tool window docked to the
/// screen edge (optionally reserving space as an AppBar); in preview mode it is an ordinary window.
/// </summary>
internal sealed class TopBarWindow : Window
{
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    private readonly TopBarView _view;
    private readonly bool _preview;
    private DesktopSettings _s;
    private bool _appBarRegistered;

    public IntPtr Monitor { get; }
    public event Action? DisplayChanged;

    public TopBarWindow(DesktopSettings settings, TopBarState state, IntPtr monitor, bool preview)
    {
        _s = settings;
        _preview = preview;
        Monitor = monitor;
        Title = preview ? "Shamsa — پیش‌نمایش نوار بالا" : "Shamsa Top Bar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = preview;
        ShowActivated = preview;
        Topmost = !preview;
        FontFamily = DesktopTheme.Font;
        UseLayoutRounding = true;
        _view = new TopBarView(settings, state);
        Content = _view;
        if (preview)
        {
            Width = 1200;
            Height = settings.TopBar.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            Left = -32000; Top = -32000; Width = 10; Height = settings.TopBar.Height; // placed in SourceInitialized
            SourceInitialized += OnSourceInitialized;
        }
        Closed += (_, _) => { _view.Detach(); ReleaseAppBar(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeToolWindowNoActivate(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        Reposition();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case TopBarNative.WM_DISPLAYCHANGE:
            case TopBarNative.WM_DPICHANGED:
                DisplayChanged?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    public void ApplySettings(DesktopSettings settings)
    {
        _s = settings;
        _view.ApplySettings(settings);
        if (_preview) Height = settings.TopBar.Height;
        else Reposition();
    }

    /// <summary>Docks the window to its monitor edge (physical pixels), reserving space when asked.</summary>
    public void Reposition()
    {
        if (_preview) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var t = _s.TopBar;
            bool bottom = string.Equals(t.Position, "Bottom", StringComparison.OrdinalIgnoreCase);
            double scale = TopBarNative.MonitorScale(Monitor);
            int thickness = (int)Math.Round(t.Height * scale);
            var (mon, _) = ShellInterop.GetMonitorRects(Monitor);
            if (mon.IsEmpty) return;

            Rect rect;
            if (t.ReserveSpace)
            {
                rect = ShellInterop.RegisterAppBar(this, bottom ? ShellInterop.ABE_BOTTOM : ShellInterop.ABE_TOP, thickness, Monitor);
                _appBarRegistered = true;
            }
            else
            {
                ReleaseAppBar();
                rect = bottom ? new Rect(mon.Left, mon.Bottom - thickness, mon.Width, thickness)
                              : new Rect(mon.Left, mon.Top, mon.Width, thickness);
            }
            TopBarNative.SetWindowPos(hwnd, TopBarNative.HWND_TOPMOST, (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height,
                TopBarNative.SWP_NOACTIVATE | TopBarNative.SWP_NOOWNERZORDER);
        }
        catch (Exception ex) { AppLog.Error("TopBar reposition failed", ex); }
    }

    private void ReleaseAppBar()
    {
        if (!_appBarRegistered) return;
        _appBarRegistered = false;
        try { ShellInterop.UnregisterAppBar(this); } catch (Exception ex) { AppLog.Warn("TopBar AppBar remove: " + ex.Message); }
    }

    /// <summary>Hide while a full-screen app runs (keeps the AppBar reservation so windows don't jump).</summary>
    public void SetSuppressed(bool suppressed)
    {
        var want = suppressed ? Visibility.Hidden : Visibility.Visible;
        if (Visibility == want) return;
        Visibility = want;
        if (!suppressed) Reposition(); // re-assert topmost position
    }
}
