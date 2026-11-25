#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Shamsa.Services;

/// <summary>
/// Safe Mode, Recovery Mode & One-Click Restore Windows Desktop / Taskbar utility.
/// Guarantees Windows Explorer and Taskbar are never permanently altered even after a crash.
/// </summary>
public static class RecoveryService
{
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static string CrashMarkerFile => Path.Combine(ShamsaProSettingsService.DataDir, "running.lock");

    public static bool DetectUncleanShutdown() => File.Exists(CrashMarkerFile);

    public static void MarkSessionStarted()
    {
        Directory.CreateDirectory(ShamsaProSettingsService.DataDir);
        File.WriteAllText(CrashMarkerFile, DateTimeOffset.UtcNow.ToString("O"));
    }

    public static void MarkSessionCleanExit()
    {
        try
        {
            if (File.Exists(CrashMarkerFile))
                File.Delete(CrashMarkerFile);
        }
        catch { /* ignore */ }
    }

    public static void SetNativeTaskbarVisible(bool visible)
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero)
            ShowWindow(taskbar, visible ? SW_SHOW : SW_HIDE);

        IntPtr secondary = FindWindow("Shell_SecondaryTrayWnd", null);
        if (secondary != IntPtr.Zero)
            ShowWindow(secondary, visible ? SW_SHOW : SW_HIDE);
    }

    /// <summary>
    /// Restores native Windows Taskbar, stops custom desktop overlays, and enables Safe Mode if requested.
    /// </summary>
    public static void RestoreWindowsDesktop(bool enterSafeMode = false)
    {
        SetNativeTaskbarVisible(true);

        var s = ShamsaProSettingsService.Current;
        s.HideNativeTaskbar = false;
        if (enterSafeMode)
        {
            s.SafeMode = true;
            s.EnableTopBarControlCenter = false;
            s.EnableSmartDock = false;
            s.EnableWallpaperEngine = false;
            s.EnableDesktopFences = false;
            s.EnableDesktopWidgets = false;
        }
        ShamsaProSettingsService.Save(s);
    }
}
