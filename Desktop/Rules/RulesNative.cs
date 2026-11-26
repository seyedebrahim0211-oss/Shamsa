using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.Rules;

/// <summary>Win32 helpers private to the rules / layout-profile feature.</summary>
internal static class RulesNative
{
    internal delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int SW_MAXIMIZE = 3;

    /// <summary>Monitors in a stable order: primary first, then left-to-right, top-to-bottom. Physical pixels.</summary>
    internal static List<(IntPtr Handle, Rect Bounds, Rect Work, bool Primary)> Monitors()
    {
        var list = new List<(IntPtr, Rect, Rect, bool)>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT __, IntPtr ___) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(m, ref mi))
                    list.Add((m, ToRect(mi.rcMonitor), ToRect(mi.rcWork), (mi.dwFlags & 1) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { AppLog.Warn("EnumDisplayMonitors: " + ex.Message); }
        return list.OrderByDescending(m => m.Item4).ThenBy(m => m.Item2.Left).ThenBy(m => m.Item2.Top).ToList();
    }

    internal static int MonitorIndexOf(IntPtr hwnd)
    {
        IntPtr h = ShellInterop.MonitorFromWindow(hwnd, 2);
        var mons = Monitors();
        int i = mons.FindIndex(m => m.Handle == h);
        return Math.Max(0, i);
    }

    private static Rect ToRect(RECT r) => new(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));

    /// <summary>The rectangle the user sees (without the invisible resize borders of Windows 10/11).</summary>
    internal static Rect VisualRect(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left)
            return ToRect(r);
        return GetWindowRect(hwnd, out var w) ? ToRect(w) : Rect.Empty;
    }

    /// <summary>Moves a window so its visible frame lands exactly on <paramref name="visual"/> (physical px).</summary>
    internal static bool PlaceVisual(IntPtr hwnd, Rect visual)
    {
        if (!ShellInterop.IsWindow(hwnd) || visual.IsEmpty) return false;
        if (ShellInterop.IsIconic(hwnd) || ShellInterop.IsZoomed(hwnd)) ShellInterop.ShowWindow(hwnd, ShellInterop.SW_RESTORE);
        int l = 0, t = 0, r = 0, b = 0;
        if (GetWindowRect(hwnd, out var outer))
        {
            var vis = VisualRect(hwnd);
            if (!vis.IsEmpty)
            {
                l = (int)vis.Left - outer.Left; t = (int)vis.Top - outer.Top;
                r = outer.Right - (int)vis.Right; b = outer.Bottom - (int)vis.Bottom;
            }
        }
        return SetWindowPos(hwnd, IntPtr.Zero, (int)visual.Left - l, (int)visual.Top - t,
            (int)visual.Width + l + r, (int)visual.Height + t + b, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    internal static void SetTopmost(IntPtr hwnd, bool on) =>
        SetWindowPos(hwnd, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    internal static void Maximize(IntPtr hwnd) => ShellInterop.ShowWindow(hwnd, SW_MAXIMIZE);

    // ------------------------------------------------------------------ virtual desktops (documented API only)

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId); // only works for our own windows
    }

    private static IVirtualDesktopManager? _vdm;

    /// <summary>Virtual desktop GUIDs in Task View order (registry, read-only).</summary>
    internal static List<Guid> VirtualDesktopIds()
    {
        var ids = new List<Guid>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("VirtualDesktopIDs") is byte[] raw)
                for (int i = 0; i + 16 <= raw.Length; i += 16) ids.Add(new Guid(raw.AsSpan(i, 16)));
        }
        catch { }
        return ids;
    }

    /// <summary>1-based number of the virtual desktop holding the window, or 0 when unknown.</summary>
    internal static int DesktopNumberOf(IntPtr hwnd)
    {
        try
        {
            _vdm ??= (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"))!)!;
            if (_vdm.GetWindowDesktopId(hwnd, out var id) != 0 || id == Guid.Empty) return 0;
            int i = VirtualDesktopIds().IndexOf(id);
            return i < 0 ? 0 : i + 1;
        }
        catch { return 0; }
    }

    internal static string ExeName(string? path) => string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);
}
