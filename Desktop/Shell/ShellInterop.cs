using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Shamsa.Services;

namespace Shamsa.Desktop.Shell;

/// <summary>A top-level application window as the Windows taskbar would show it.</summary>
public sealed record AppWindow(IntPtr Hwnd, string Title, uint ProcessId, string? ExePath, bool IsMinimized, IntPtr Monitor);

/// <summary>
/// Shared, documented-API shell helpers for the desktop modules (top bar, dock, window manager):
/// taskbar-style window enumeration, AppBar space reservation, reversible taskbar hiding,
/// full-screen detection and real application icons.
/// </summary>
public static class ShellInterop
{
    // ------------------------------------------------------------------ Win32

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    public const int SW_HIDE = 0, SW_SHOWNA = 8, SW_RESTORE = 9, SW_MINIMIZE = 6, SW_SHOW = 5;
    public const uint WM_CLOSE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    // ------------------------------------------------------------------ window enumeration

    /// <summary>Windows that belong on a taskbar: visible, un-cloaked, unowned (or APPWINDOW), titled, not tool windows.</summary>
    public static List<AppWindow> GetAppWindows(bool includeOwnProcess = false)
    {
        var list = new List<AppWindow>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsTaskbarWindow(hwnd)) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!includeOwnProcess && pid == self) return true;
                list.Add(new AppWindow(hwnd, GetTitle(hwnd), pid, GetProcessPath(pid), IsIconic(hwnd), MonitorFromWindow(hwnd, 2)));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false; // other virtual desktop / suspended UWP
        string cls = NativeMethods.GetClassNameOf(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    public static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static readonly Dictionary<uint, (string? Path, DateTime At)> PathCache = new();

    public static string? GetProcessPath(uint pid)
    {
        lock (PathCache)
            if (PathCache.TryGetValue(pid, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(2)) return hit.Path;
        string? path = null;
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally { CloseHandle(h); }
        }
        lock (PathCache) PathCache[pid] = (path, DateTime.UtcNow);
        return path;
    }

    // ------------------------------------------------------------------ activation

    /// <summary>Taskbar-like click: restore+focus, or minimise when it is already in front.</summary>
    public static void ActivateOrMinimize(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        if (NativeMethods.GetForegroundWindow() == hwnd && !IsIconic(hwnd)) { ShowWindow(hwnd, SW_MINIMIZE); return; }
        Activate(hwnd);
    }

    public static void Activate(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Asks the window to close (WM_CLOSE) — the app may still prompt to save.</summary>
    public static void RequestClose(IntPtr hwnd) { if (IsWindow(hwnd)) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }

    // ------------------------------------------------------------------ monitors

    public static (Rect Monitor, Rect Work) GetMonitorRects(IntPtr monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return (Rect.Empty, Rect.Empty);
        static Rect R(RECT r) => new(r.Left, r.Top, r.Width, r.Height);
        return (R(mi.rcMonitor), R(mi.rcWork));
    }

    /// <summary>True while a full-screen app, game, presentation or video runs on the window's monitor.</summary>
    public static bool IsFullscreenAppActive(IntPtr targetMonitor = default)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || NativeMethods.IsOwnProcessWindow(fg)) return false;
        var current = MonitorFromWindow(fg, 2);
        if (targetMonitor != IntPtr.Zero && current != targetMonitor) return false;
        string cls = NativeMethods.GetClassNameOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (SHQueryUserNotificationState(out int state) == 0 && state == 3) return true;
        if (!GetWindowRect(fg, out var r)) return false;
        var (mon, _) = GetMonitorRects(current);
        return !mon.IsEmpty && r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
    }

    // ------------------------------------------------------------------ icons

    private static readonly Dictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Large app icon for an executable (cached). STA/UI thread.</summary>
    public static BitmapSource? GetAppIcon(string? exePath, int size = 48)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        string key = exePath + "|" + size;
        if (IconCache.TryGetValue(key, out var hit)) return hit;
        var bmp = ShellThumbnailService.TryGetShellImage(exePath, size, iconOnly: true);
        if (IconCache.Count > 300) IconCache.Clear();
        IconCache[key] = bmp;
        return bmp;
    }

    public static string FriendlyAppName(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return "برنامه";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription!;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exePath);
    }

    // ------------------------------------------------------------------ AppBar (reserve screen space)

    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    internal const int AppBarCallback = 0x8000 + 0x5A, AppBarPositionChanged = 1;
    private static readonly HashSet<IntPtr> RegisteredBars = new();
    private static readonly Dictionary<IntPtr, (uint Edge, Rect Rect)> GrantedBars = new();

    /// <summary>
    /// Registers <paramref name="window"/> as an AppBar docked to <paramref name="edge"/> with the given
    /// thickness in physical pixels, so maximised windows don't cover it. Returns the granted rect.
    /// </summary>
    public static Rect RegisterAppBar(Window window, uint edge, int thicknessPx, IntPtr monitor)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd, uEdge = edge };
        lock (RegisteredBars)
        {
            if (!RegisteredBars.Contains(hwnd))
            {
                data.uCallbackMessage = AppBarCallback;
                SHAppBarMessage(ABM_NEW, ref data);
                RegisteredBars.Add(hwnd);
            }
        }
        var (mon, _) = GetMonitorRects(monitor);
        data.rc = new RECT { Left = (int)mon.Left, Top = (int)mon.Top, Right = (int)mon.Right, Bottom = (int)mon.Bottom };
        switch (edge)
        {
            case ABE_TOP: data.rc.Bottom = data.rc.Top + thicknessPx; break;
            case ABE_BOTTOM: data.rc.Top = data.rc.Bottom - thicknessPx; break;
            case ABE_LEFT: data.rc.Right = data.rc.Left + thicknessPx; break;
            case ABE_RIGHT: data.rc.Left = data.rc.Right - thicknessPx; break;
        }
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        switch (edge)
        {
            case ABE_TOP: data.rc.Bottom = data.rc.Top + thicknessPx; break;
            case ABE_BOTTOM: data.rc.Top = data.rc.Bottom - thicknessPx; break;
            case ABE_LEFT: data.rc.Right = data.rc.Left + thicknessPx; break;
            case ABE_RIGHT: data.rc.Left = data.rc.Right - thicknessPx; break;
        }
        var rect = new Rect(data.rc.Left, data.rc.Top, data.rc.Width, data.rc.Height);
        // ABM_SETPOS notifies every other bar. A no-op renegotiation must not start a feedback loop.
        lock (RegisteredBars)
        {
            if (GrantedBars.TryGetValue(hwnd, out var old) && old == (edge, rect)) return rect;
            SHAppBarMessage(ABM_SETPOS, ref data);
            rect = new Rect(data.rc.Left, data.rc.Top, data.rc.Width, data.rc.Height);
            GrantedBars[hwnd] = (edge, rect);
        }
        return rect;
    }

    public static void UnregisterAppBar(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        lock (RegisteredBars) { if (!RegisteredBars.Remove(hwnd)) return; GrantedBars.Remove(hwnd); }
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    /// <summary>Releases every AppBar this process registered (shutdown / crash handler).</summary>
    public static void UnregisterAllAppBars()
    {
        List<IntPtr> bars;
        lock (RegisteredBars) { bars = RegisteredBars.ToList(); RegisteredBars.Clear(); GrantedBars.Clear(); }
        foreach (var hwnd in bars)
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
            try { SHAppBarMessage(ABM_REMOVE, ref data); } catch { }
        }
    }

    // ------------------------------------------------------------------ Windows taskbar (reversible)

    private static IntPtr _hiddenTaskbar;
    private static string MarkerFile => Path.Combine(AppPaths.DataDir, "taskbar-hidden.flag");

    public static bool IsTaskbarHidden => _hiddenTaskbar != IntPtr.Zero;

    private static IEnumerable<IntPtr> TaskbarWindows()
    {
        IntPtr main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr sec = IntPtr.Zero;
        for (int i = 0; i < 16; i++)
        {
            sec = FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null);
            if (sec == IntPtr.Zero) break;
            yield return sec;
        }
    }

    /// <summary>
    /// Hides (or shows) the Windows taskbar windows. Nothing is written to the registry or Explorer
    /// settings; a marker file lets the next start (or Safe Mode) restore it after a crash.
    /// </summary>
    public static void SetWindowsTaskbarHidden(bool hide, IntPtr monitor = default)
    {
        if (hide)
        {
            var target = monitor == IntPtr.Zero ? FindWindow("Shell_TrayWnd", null) :
                TaskbarWindows().FirstOrDefault(h => MonitorFromWindow(h, 2) == monitor);
            if (target != IntPtr.Zero && target == _hiddenTaskbar && !IsWindowVisible(target)) return;
            if (_hiddenTaskbar != IntPtr.Zero) SetWindowsTaskbarHidden(false);
            if (target == IntPtr.Zero || !IsWindowVisible(target)) return;
            try
            {
                var name = System.Windows.Forms.Screen.FromHandle(target).DeviceName;
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(MarkerFile, "device:" + name);
                _hiddenTaskbar = target;
                ShowWindow(target, SW_HIDE);
            }
            catch (Exception ex) { AppLog.Warn("Taskbar hide: " + ex.Message); }
            return;
        }
        try
        {
            if (_hiddenTaskbar != IntPtr.Zero && IsWindow(_hiddenTaskbar)) ShowWindow(_hiddenTaskbar, SW_SHOWNA);
            _hiddenTaskbar = IntPtr.Zero;
            if (!File.Exists(MarkerFile)) return;
            string marker = File.ReadAllText(MarkerFile);
            foreach (var hwnd in TaskbarWindows())
            {
                if (marker.StartsWith("device:", StringComparison.Ordinal) &&
                    !string.Equals(System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName, marker[7..], StringComparison.OrdinalIgnoreCase)) continue;
                if (marker.StartsWith("primary:", StringComparison.Ordinal) && hwnd != FindWindow("Shell_TrayWnd", null)) continue;
                ShowWindow(hwnd, SW_SHOWNA);
            }
            File.Delete(MarkerFile);
        }
        catch (Exception ex) { AppLog.Warn("Taskbar restore: " + ex.Message); }
    }

    /// <summary>Call at startup and from crash handlers: brings back a taskbar left hidden by a previous run.</summary>
    public static void RestoreTaskbarIfLeftHidden()
    {
        try
        {
            if (File.Exists(MarkerFile)) SetWindowsTaskbarHidden(false);
        }
        catch { }
    }

    /// <summary>Emergency restore of everything the desktop modules changed.</summary>
    public static void RestoreWindowsDesktop()
    {
        UnregisterAllAppBars();
        SetWindowsTaskbarHidden(false);
    }
}
