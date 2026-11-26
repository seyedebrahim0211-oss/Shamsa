using System.Runtime.InteropServices;
using System.Text;

namespace Shamsa.Desktop.Personalize;

/// <summary>Documented Win32 calls used by the personalization module (per-user settings only).</summary>
internal static class PersonalizeNative
{
    public const uint SPI_GETDESKWALLPAPER = 0x0073;
    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPI_SETCURSORS = 0x0057;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const uint SHCNF_IDLIST = 0x0000;
    public const uint SHCNF_FLUSH = 0x1000;

    public const int COLOR_DESKTOP = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, string? vparam, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder vparam, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    public static extern bool SystemParametersInfoPtr(uint action, uint param, IntPtr vparam, uint winIni);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll")]
    public static extern bool SetSysColors(int count, int[] elements, int[] colors);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    /// <summary>WM_SETTINGCHANGE broadcast so Explorer/apps re-read a setting area (e.g. "ImmersiveColorSet").</summary>
    public static void BroadcastSettingChange(string area)
    {
        const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;
        try { SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 2000, out _); }
        catch { }
    }

    /// <summary>Tells the shell that icon associations changed so desktop icons refresh.</summary>
    public static void RefreshShellIcons()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    public static string GetCurrentWallpaper()
    {
        var sb = new StringBuilder(520);
        try { SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0); } catch { }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Restart Manager (graceful Explorer restart)

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmRegisterResources(uint session, uint nFiles, string[]? files, uint nApps, RM_UNIQUE_PROCESS[] apps, uint nServices, string[]? services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmRestart(uint session, int flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    /// <summary>
    /// Restarts Explorer through the documented Restart Manager (asks it to close, then relaunches it).
    /// Falls back to closing and starting explorer.exe if Restart Manager is unavailable.
    /// </summary>
    public static bool RestartExplorer()
    {
        int sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var explorers = System.Diagnostics.Process.GetProcessesByName("explorer").Where(p => p.SessionId == sessionId).ToList();
        if (explorers.Count == 0) { System.Diagnostics.Process.Start("explorer.exe"); return true; }
        try
        {
            var key = new StringBuilder(64);
            if (RmStartSession(out uint session, 0, key) == 0)
            {
                try
                {
                    var apps = explorers.Select(p =>
                    {
                        long ft = p.StartTime.ToFileTime();
                        return new RM_UNIQUE_PROCESS
                        {
                            dwProcessId = p.Id,
                            ProcessStartTime = new System.Runtime.InteropServices.ComTypes.FILETIME { dwLowDateTime = (int)(ft & 0xFFFFFFFF), dwHighDateTime = (int)(ft >> 32) }
                        };
                    }).ToArray();
                    if (RmRegisterResources(session, 0, null, (uint)apps.Length, apps, 0, null) == 0
                        && RmShutdown(session, 0x1 /* RmForceShutdown */, IntPtr.Zero) == 0)
                    {
                        RmRestart(session, 0, IntPtr.Zero);
                        Thread.Sleep(1500);
                        if (!System.Diagnostics.Process.GetProcessesByName("explorer").Any(p => p.SessionId == sessionId))
                            System.Diagnostics.Process.Start("explorer.exe");
                        return true;
                    }
                }
                finally { RmEndSession(session); }
            }
        }
        catch (Exception ex) { Services.AppLog.Warn("Restart Manager failed: " + ex.Message); }

        try
        {
            foreach (var p in explorers) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            Thread.Sleep(500);
            if (!System.Diagnostics.Process.GetProcessesByName("explorer").Any(p => p.SessionId == sessionId))
                System.Diagnostics.Process.Start("explorer.exe");
            return true;
        }
        catch (Exception ex) { Services.AppLog.Error("Explorer restart failed", ex); return false; }
    }
}
