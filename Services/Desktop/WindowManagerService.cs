#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Shamsa.Services.Desktop;

public sealed record ManagedWindowInfo(IntPtr Handle, string Title, string ProcessName, int ProcessId);

/// <summary>
/// Real Win32 Tiling Window Manager & Workspace Profile Controller for Shamsa.
/// Uses EnumWindows, SetWindowPos, and DWM cloaking checks while strictly avoiding
/// system, elevated, protected, or fullscreen game windows.
/// </summary>
public sealed class WindowManagerService : IDisposable
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int DWMWA_CLOAKED = 14;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "LockApp", "LogonUI", "csrss", "winlogon", "Taskmgr", "Shamsa"
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    public List<ManagedWindowInfo> EnumerateTileableWindows()
    {
        var results = new List<ManagedWindowInfo>();
        var rules = ShamsaProSettingsService.Current.AppRules;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
            if ((style & WS_CAPTION) == 0 || (style & WS_THICKFRAME) == 0)
                return true;

            var sb = new StringBuilder(256);
            if (GetWindowText(hWnd, sb, sb.Capacity) == 0) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = "";
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }

            if (ProtectedProcesses.Contains(procName)) return true;

            var rule = rules.FirstOrDefault(r => r.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase));
            if (rule?.ExcludeFromTiling == true) return true;

            results.Add(new ManagedWindowInfo(hWnd, sb.ToString(), procName, (int)pid));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public void ApplyLayout(TilingLayoutMode mode, Screen? targetScreen = null)
    {
        if (mode == TilingLayoutMode.Floating) return;
        var windows = EnumerateTileableWindows();
        if (windows.Count == 0) return;

        var screen = targetScreen ?? Screen.PrimaryScreen;
        if (screen == null) return;

        int topBarReserve = ShamsaProSettingsService.Current.EnableTopBarControlCenter ? 40 : 0;
        int dockReserve = ShamsaProSettingsService.Current.EnableSmartDock ? 74 : 0;
        int gap = ShamsaProSettingsService.Current.WindowGapPx;

        var area = screen.WorkingArea;
        int x0 = area.Left + gap;
        int y0 = area.Top + topBarReserve + gap;
        int totalW = Math.Max(400, area.Width - gap * 2);
        int totalH = Math.Max(300, area.Height - topBarReserve - dockReserve - gap * 2);

        switch (mode)
        {
            case TilingLayoutMode.TwoColumns:
            {
                int leftW = (totalW - gap) / 2;
                int rightW = totalW - leftW - gap;
                SetBounds(windows[0].Handle, x0, y0, leftW, totalH);
                int rightCount = windows.Count - 1;
                if (rightCount > 0)
                {
                    int slotH = (totalH - gap * (rightCount - 1)) / rightCount;
                    for (int i = 0; i < rightCount; i++)
                        SetBounds(windows[i + 1].Handle, x0 + leftW + gap, y0 + i * (slotH + gap), rightW, slotH);
                }
                break;
            }
            case TilingLayoutMode.ThreeColumns:
            {
                int cols = Math.Min(3, windows.Count);
                int colW = (totalW - gap * (cols - 1)) / cols;
                for (int i = 0; i < cols; i++)
                    SetBounds(windows[i].Handle, x0 + i * (colW + gap), y0, colW, totalH);
                break;
            }
            case TilingLayoutMode.FourGrid:
            {
                int cellW = (totalW - gap) / 2;
                int cellH = (totalH - gap) / 2;
                int limit = Math.Min(4, windows.Count);
                for (int i = 0; i < limit; i++)
                {
                    int col = i % 2;
                    int row = i / 2;
                    SetBounds(windows[i].Handle, x0 + col * (cellW + gap), y0 + row * (cellH + gap), cellW, cellH);
                }
                break;
            }
            case TilingLayoutMode.Stacked:
            {
                int offset = 28;
                int w = Math.Max(400, totalW - offset * Math.Min(5, windows.Count));
                int h = Math.Max(300, totalH - offset * Math.Min(5, windows.Count));
                for (int i = 0; i < windows.Count; i++)
                    SetBounds(windows[i].Handle, x0 + (i % 6) * offset, y0 + (i % 6) * offset, w, h);
                break;
            }
        }

        ApplyPerAppRules(windows);
    }

    public void ApplyPerAppRules(IEnumerable<ManagedWindowInfo>? windows = null)
    {
        var list = windows ?? EnumerateTileableWindows();
        var rules = ShamsaProSettingsService.Current.AppRules;
        foreach (var win in list)
        {
            var rule = rules.FirstOrDefault(r => r.ProcessName.Equals(win.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (rule == null) continue;

            SetWindowPos(win.Handle, rule.AlwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            if (rule.FixedX.HasValue && rule.FixedY.HasValue && rule.FixedWidth.HasValue && rule.FixedHeight.HasValue)
                SetBounds(win.Handle, rule.FixedX.Value, rule.FixedY.Value, rule.FixedWidth.Value, rule.FixedHeight.Value);
        }
    }

    public void ActivateWorkspaceByName(string workspaceName)
    {
        var s = ShamsaProSettingsService.Current;
        var profile = s.Workspaces.FirstOrDefault(w => w.Name.Contains(workspaceName, StringComparison.OrdinalIgnoreCase));
        if (profile == null) return;
        ActivateWorkspace(profile);
    }

    public void ActivateWorkspace(WorkspaceProfile profile)
    {
        var s = ShamsaProSettingsService.Current;
        s.ActiveWorkspaceId = profile.Id;
        s.ActiveLayout = profile.DefaultLayout;
        s.ThemePreset = profile.ThemePreset;
        ShamsaProSettingsService.Save(s);

        foreach (var appPath in profile.StartupApps)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(appPath))
                    Process.Start(new ProcessStartInfo { FileName = appPath, UseShellExecute = true });
            }
            catch { /* skip missing app */ }
        }

        ApplyLayout(profile.DefaultLayout);
    }

    private static void SetBounds(IntPtr hWnd, int x, int y, int w, int h)
        => SetWindowPos(hWnd, IntPtr.Zero, x, y, Math.Max(200, w), Math.Max(150, h), SWP_NOZORDER | SWP_NOACTIVATE);

    public void Dispose() { }
}
