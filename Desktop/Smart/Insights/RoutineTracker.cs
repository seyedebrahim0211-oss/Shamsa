using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>
/// Learns the morning routine with almost no cost: once a minute it looks at which program owns the
/// foreground window and, before noon, remembers the first day-use of each program. Only program paths
/// and dates are stored, only on this PC (smart-insights.json). Nothing about windows' content is read.
/// </summary>
public static class RoutineTracker
{
    public const int MorningStartHour = 5;
    public const int MorningEndHour = 12;
    private const int KeepDays = 14;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "Shamsa", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "LockApp", "LogonUI", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "Taskmgr",
        "dwm", "csrss", "winlogon", "rundll32", "dllhost", "consent", "SecurityHealthSystray", "ShellHost"
    };

    private static DispatcherTimer? _timer;

    public static bool IsRunning => _timer != null;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
        Sample();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private static void Sample()
    {
        try
        {
            var now = DateTime.Now;
            if (now.Hour < MorningStartHour || now.Hour >= MorningEndHour) return;
            string? path = ForegroundProcessPath();
            if (path != null) Record(path, now);
        }
        catch (Exception ex) { AppLog.Warn("Routine sample failed: " + ex.Message); }
    }

    private static string? ForegroundProcessPath()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Records that <paramref name="exePath"/> was used on the morning of <paramref name="when"/>. Public for tests.</summary>
    public static void Record(string exePath, DateTime when)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (Ignored.Contains(name)) return;
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (exePath.StartsWith(Path.Combine(winDir, "System32"), StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("notepad", StringComparison.OrdinalIgnoreCase) && !name.Equals("mspaint", StringComparison.OrdinalIgnoreCase))
            return;

        string day = DayKey(when);
        bool already = InsightsStore.Read(s => s.Routine.LastDay == day && s.Routine.SeenToday.Contains(exePath, StringComparer.OrdinalIgnoreCase));
        if (already) return; // nothing new: no disk write
        InsightsStore.Update(s =>
        {
            var r = s.Routine;
            if (r.LastDay != day) { r.LastDay = day; r.SeenToday.Clear(); }
            if (r.SeenToday.Contains(exePath, StringComparer.OrdinalIgnoreCase)) return;
            r.SeenToday.Add(exePath);
            if (!r.MorningDays.TryGetValue(exePath, out var days)) r.MorningDays[exePath] = days = new List<string>();
            if (!days.Contains(day)) days.Add(day);
            // keep only recent days and at most 60 programs
            string oldest = DayKey(when.AddDays(-KeepDays));
            days.RemoveAll(d => string.CompareOrdinal(d, oldest) < 0);
            foreach (var key in r.MorningDays.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) r.MorningDays.Remove(key);
            if (r.MorningDays.Count > 60)
                foreach (var key in r.MorningDays.OrderBy(kv => kv.Value.Max()).Take(r.MorningDays.Count - 60).Select(kv => kv.Key).ToList())
                    r.MorningDays.Remove(key);
        }, notify: false);
    }

    /// <summary>Programs used on at least 3 of the last 7 mornings, most regular first.</summary>
    public static List<string> MorningApps(DateTime now, int minDays = 3)
    {
        string from = DayKey(now.AddDays(-7));
        var counts = InsightsStore.Read(s => s.Routine.MorningDays
            .Select(kv => (Path: kv.Key, Count: kv.Value.Count(d => string.CompareOrdinal(d, from) >= 0)))
            .ToList());
        return counts
            .Where(x => x.Count >= minDays && File.Exists(x.Path))
            .OrderByDescending(x => x.Count)
            .Select(x => x.Path)
            .Take(6)
            .ToList();
    }

    public static bool IsRunningProcess(string exePath)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(exePath);
            var procs = Process.GetProcessesByName(name);
            bool any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return any;
        }
        catch { return false; }
    }

    public static string FriendlyName(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription!.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exePath);
    }
}
