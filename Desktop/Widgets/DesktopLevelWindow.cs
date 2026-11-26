using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shamsa.Desktop.Widgets;

/// <summary>
/// Base for widgets and fences: a borderless, transparent tool window that lives at the bottom of the
/// z-order (just above the wallpaper/icons), stays out of Alt+Tab and the taskbar, can be dragged
/// (unless locked) and resized with a grip, and reports its bounds (debounced) so they can be saved.
/// </summary>
public class DesktopLevelWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const int WM_WINDOWPOSCHANGING = 0x0046, WM_ACTIVATE = 0x0006;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private readonly DispatcherTimer _commitTimer;
    private bool _ready;

    /// <summary>When true the window can't be dragged or resized.</summary>
    public bool Locked
    {
        get => _locked;
        set { _locked = value; ResizeMode = value ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip; }
    }
    private bool _locked;

    /// <summary>Raised ~0.6 s after the user stops moving/resizing.</summary>
    public event Action<Rect>? BoundsCommitted;

    public DesktopLevelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        FontFamily = DesktopTheme.Font;
        Foreground = DesktopTheme.TextBrush;
        FlowDirection = FlowDirection.RightToLeft;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        MinWidth = 160; MinHeight = 90;

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _commitTimer.Tick += (_, _) =>
        {
            _commitTimer.Stop();
            if (_ready) { FitPrimary(); BoundsCommitted?.Invoke(new Rect(Left, Top, Width, Height)); }
        };
        LocationChanged += (_, _) => Touch();
        SizeChanged += (_, _) => Touch();
        Loaded += (_, _) =>
        {
            FitPrimary();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        };
        Closed += (_, _) =>
        {
            _ready = false;
            _commitTimer.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        };
        ContentRendered += (_, _) => _ready = true;
    }

    private void DisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() => { if (IsLoaded) FitPrimary(); });

    internal void FitPrimary()
    {
        var area = SystemParameters.WorkArea;
        if (area.IsEmpty || area.Width < MinWidth || area.Height < MinHeight) return;
        Width = Math.Clamp(Width, MinWidth, area.Width);
        Height = Math.Clamp(Height, MinHeight, area.Height);
        Left = Math.Clamp(Left, area.Left, area.Right - Width);
        Top = Math.Clamp(Top, area.Top, area.Bottom - Height);
    }

    private void Touch()
    {
        if (!_ready) return;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        SendToBottom();
    }

    public void SendToBottom()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(l);
            if ((pos.flags & SWP_NOZORDER) == 0 && pos.hwndInsertAfter != HWND_BOTTOM)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, l, false);
            }
        }
        else if (msg == WM_ACTIVATE)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, SendToBottom);
        }
        return IntPtr.Zero;
    }

    /// <summary>Hook this to a drag surface's MouseLeftButtonDown.</summary>
    protected void BeginDrag(MouseButtonEventArgs e)
    {
        if (Locked || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
