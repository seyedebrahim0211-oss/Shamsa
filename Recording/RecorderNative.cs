using System.Runtime.InteropServices;
using System.Text;

namespace Shamsa.Recording;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>H.264 (yuv420p) needs even dimensions.</summary>
    public PixelRect Even() => new(X, Y, Math.Max(2, Width & ~1), Math.Max(2, Height & ~1));

    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : default;
    }

    public override string ToString() => $"{Width}×{Height} @ {X},{Y}";
}

public sealed record MonitorInfo(int Index, string Name, PixelRect Bounds, bool Primary)
{
    public string Label => $"مانیتور {Index + 1}{(Primary ? " (اصلی)" : "")} — {Bounds.Width}×{Bounds.Height}";
}

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, PixelRect Bounds)
{
    public string Label => $"{Title}  ·  {ProcessName}  ({Bounds.Width}×{Bounds.Height})";
}

/// <summary>
/// Win32 helpers that always work in physical pixels: FFmpeg gdigrab / ddagrab and GDI capture
/// address the desktop in physical pixels, so the calling thread is switched to per-monitor DPI
/// awareness while measuring monitors, windows and the cursor.
/// </summary>
internal static class RecorderNative
{
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    public static T PerMonitor<T>(Func<T> func)
    {
        IntPtr old = IntPtr.Zero;
        try { old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
        try { return func(); }
        finally
        {
            if (old != IntPtr.Zero) { try { SetThreadDpiAwarenessContext(old); } catch { } }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint GW_OWNER = 4;

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static List<MonitorInfo> GetMonitors() => PerMonitor(() =>
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(0, mi.szDevice, new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (mi.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        var ordered = list.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
        return ordered.Select((m, i) => m with { Index = i }).ToList();
    });

    public static PixelRect VirtualScreen()
    {
        var mons = GetMonitors();
        if (mons.Count == 0) return new PixelRect(0, 0, 1920, 1080);
        int x = mons.Min(m => m.Bounds.X), y = mons.Min(m => m.Bounds.Y);
        int r = mons.Max(m => m.Bounds.Right), b = mons.Max(m => m.Bounds.Bottom);
        return new PixelRect(x, y, r - x, b - y);
    }

    public static (int X, int Y) CursorPixels() => PerMonitor(() =>
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    });

    public static PixelRect GetWindowBounds(IntPtr hwnd) => PerMonitor(() =>
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    });

    public static List<WindowInfo> GetCapturableWindows()
    {
        var result = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                int len = GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                Shamsa.Services.NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == ownPid) return true;
                var bounds = GetWindowBounds(hwnd);
                if (bounds.Width < 80 || bounds.Height < 60) return true;
                string proc = "";
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
                result.Add(new WindowInfo(hwnd, sb.ToString(), proc, bounds));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool IsWindowAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
}
