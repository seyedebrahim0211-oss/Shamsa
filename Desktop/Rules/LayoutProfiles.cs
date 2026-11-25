using System.Diagnostics;
using Shamsa.Desktop.Shell;
using Shamsa.Desktop.WindowManager;
using Shamsa.Services;

namespace Shamsa.Desktop.Rules;

/// <summary>
/// Saved app arrangements («حالت کار»): capture where the open apps are, and later reopen missing apps
/// and put every window back. Only executable paths and rectangles are stored — never window titles.
/// </summary>
public static class LayoutProfiles
{
    private static bool _restoring;

    /// <summary>Snapshot of the current application windows (Shamsa's own windows excluded).</summary>
    public static LayoutProfile Capture(string name)
    {
        var profile = new LayoutProfile { Name = string.IsNullOrWhiteSpace(name) ? "حالت کار" : name.Trim() };
        foreach (var w in ShellInterop.GetAppWindows())
        {
            try
            {
                if (string.IsNullOrEmpty(w.ExePath) || w.IsMinimized || NativeMethods.IsOwnProcessWindow(w.Hwnd)) continue;
                if (IsShellHost(w.ExePath)) continue;
                bool max = ShellInterop.IsZoomed(w.Hwnd);
                var r = RulesNative.VisualRect(w.Hwnd);
                if (r.IsEmpty) continue;
                profile.Windows.Add(new LayoutEntry
                {
                    ExePath = w.ExePath!, X = (int)r.Left, Y = (int)r.Top, Width = (int)r.Width, Height = (int)r.Height,
                    Maximized = max, LaunchIfNotRunning = true
                });
            }
            catch { }
        }
        return profile;
    }

    /// <summary>
    /// UWP / Store apps all run inside ApplicationFrameHost; relaunching that exe does nothing useful,
    /// so such windows are positioned when open but never launched.
    /// </summary>
    internal static bool IsShellHost(string exePath)
    {
        return Path.GetFileName(exePath).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reopens missing apps and moves every window of the profile into place.</summary>
    public static async Task<int> RestoreAsync(LayoutProfile profile)
    {
        if (_restoring || profile.Windows.Count == 0) return 0;
        _restoring = true;
        int placed = 0, launched = 0, failed = 0;
        try
        {
            var used = new HashSet<IntPtr>();
            foreach (var group in profile.Windows.GroupBy(e => e.ExePath, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var entry in group)
                {
                    IntPtr hwnd = FindUnusedWindow(entry.ExePath, used);
                    if (hwnd == IntPtr.Zero && entry.LaunchIfNotRunning && File.Exists(entry.ExePath) && !IsShellHost(entry.ExePath))
                    {
                        var before = WindowsOf(entry.ExePath);
                        try
                        {
                            Process.Start(new ProcessStartInfo(entry.ExePath, entry.Arguments ?? string.Empty)
                            {
                                UseShellExecute = true,
                                WorkingDirectory = Path.GetDirectoryName(entry.ExePath) ?? string.Empty
                            });
                            launched++;
                        }
                        catch (Exception ex) { AppLog.Warn("Layout launch failed: " + ex.Message); failed++; continue; }
                        var until = DateTime.UtcNow.AddSeconds(15);
                        while (DateTime.UtcNow < until)
                        {
                            await Task.Delay(250);
                            hwnd = WindowsOf(entry.ExePath).FirstOrDefault(h => !before.Contains(h) && !used.Contains(h));
                            if (hwnd != IntPtr.Zero) break;
                        }
                        if (hwnd != IntPtr.Zero) await Task.Delay(400); // let the app finish its own first positioning
                    }
                    if (hwnd == IntPtr.Zero) { failed++; continue; }
                    used.Add(hwnd);
                    try
                    {
                        var target = ClampToScreens(new System.Windows.Rect(entry.X, entry.Y, Math.Max(120, entry.Width), Math.Max(80, entry.Height)));
                        RulesNative.PlaceVisual(hwnd, target);
                        if (entry.Maximized) RulesNative.Maximize(hwnd);
                        placed++;
                    }
                    catch (Exception ex) { AppLog.Warn("Layout place failed: " + ex.Message); failed++; }
                }
            }
            if (!string.IsNullOrWhiteSpace(profile.WindowManagerLayout))
                WindowManagerModule.RequestLayout(profile.WindowManagerLayout!);
            Notifier.Show($"«{profile.Name}» بازیابی شد: {placed} پنجره چیده شد" +
                          (launched > 0 ? $"، {launched} برنامه اجرا شد" : "") + (failed > 0 ? $"، {failed} مورد پیدا نشد" : "") + ".");
        }
        catch (Exception ex) { AppLog.Error("Layout restore failed", ex); }
        finally { _restoring = false; }
        return placed;
    }

    private static List<IntPtr> WindowsOf(string exePath) =>
        ShellInterop.GetAppWindows()
            .Where(w => string.Equals(w.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Hwnd).ToList();

    private static IntPtr FindUnusedWindow(string exePath, HashSet<IntPtr> used) =>
        WindowsOf(exePath).FirstOrDefault(h => !used.Contains(h));

    /// <summary>Pulls a saved rectangle back onto a connected monitor (monitors may have changed since capture).</summary>
    internal static System.Windows.Rect ClampToScreens(System.Windows.Rect r)
    {
        var mons = RulesNative.Monitors();
        if (mons.Count == 0) return r;
        var center = new System.Windows.Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var work = mons.FirstOrDefault(m => m.Bounds.Contains(center)).Work;
        if (work.IsEmpty || work.Width <= 0) work = mons[0].Work;
        return Fit(r, work);
    }

    /// <summary>Pure helper: keeps <paramref name="r"/> inside <paramref name="work"/>, shrinking if needed.</summary>
    internal static System.Windows.Rect Fit(System.Windows.Rect r, System.Windows.Rect work)
    {
        double w = Math.Min(r.Width, work.Width), h = Math.Min(r.Height, work.Height);
        double x = Math.Clamp(r.Left, work.Left, work.Right - w), y = Math.Clamp(r.Top, work.Top, work.Bottom - h);
        return new System.Windows.Rect(x, y, w, h);
    }
}
