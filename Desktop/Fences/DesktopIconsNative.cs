using System.Runtime.InteropServices;
using Shamsa.Services;

namespace Shamsa.Desktop.Fences;

/// <summary>
/// Hides the Windows desktop icon list (SysListView32 "FolderView") while themed groups are shown.
/// Only the window is hidden — Explorer's own "Show desktop icons" setting is never changed, nothing is
/// moved or deleted, and restarting Explorer always brings the icons back. A marker file lets the next
/// start (or a crash handler) restore icons that a previous run hid.
/// </summary>
internal static class DesktopIconsNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);

    private const int SW_HIDE = 0, SW_SHOWNA = 8;
    private static bool _hiddenByUs;
    private static string Marker => Path.Combine(AppPaths.DataDir, "desktop-icons-hidden.flag");

    private static IntPtr ListView()
    {
        IntPtr defView = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // After a wallpaper slideshow / Win+Tab, the icon view is re-parented into a WorkerW window.
            EnumWindows((h, _) =>
            {
                var dv = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv == IntPtr.Zero) return true;
                defView = dv;
                return false;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    public static void SetHidden(bool hide)
    {
        try
        {
            var lv = ListView();
            if (lv == IntPtr.Zero) return;
            if (hide)
            {
                if (!IsWindowVisible(lv)) return; // the user already turned desktop icons off in Windows
                ShowWindow(lv, SW_HIDE);
                _hiddenByUs = true;
                try { Directory.CreateDirectory(AppPaths.DataDir); File.WriteAllText(Marker, DateTime.UtcNow.ToString("o")); } catch { }
            }
            else if (_hiddenByUs || File.Exists(Marker))
            {
                ShowWindow(lv, SW_SHOWNA);
                _hiddenByUs = false;
                try { File.Delete(Marker); } catch { }
            }
        }
        catch (Exception ex) { AppLog.Warn("Desktop icons visibility: " + ex.Message); }
    }

    /// <summary>Brings the icons back if an earlier run hid them and did not exit cleanly.</summary>
    public static void RestoreIfLeftHidden()
    {
        try { if (File.Exists(Marker)) SetHidden(false); } catch { }
    }
}
