using System.Windows;
using System.Windows.Threading;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.Rules;

/// <summary>
/// «قوانین برنامه‌ها»: applies per-app rules (float, always-on-top, maximised, fixed position) to new
/// application windows, and owns the global shortcuts of saved layout profiles («حالت‌های کاری»).
/// </summary>
public sealed class RulesModule : IDesktopModule
{
    public const string LayoutOwner = "حالت‌های کاری";

    public string Id => "rules";
    public string DisplayName => "قوانین برنامه‌ها";
    public bool IsRunning { get; private set; }

    /// <summary>Rules in effect (snapshot from the last Apply); read by the window manager.</summary>
    private static List<AppRule> _activeRules = new();

    private readonly List<IntPtr> _hooks = new();
    private RulesNative.WinEventProc? _proc;
    private readonly HashSet<IntPtr> _seen = new();
    private readonly Dictionary<IntPtr, int> _pending = new();       // hwnd → remaining title/visibility retries
    private DispatcherTimer? _pendingTimer;
    private readonly List<string> _hotkeyIds = new();
    private readonly HashSet<string> _desktopWarned = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ matching

    internal static AppRule? Match(IEnumerable<AppRule> rules, string? exePath, string? title)
    {
        string exe = RulesNative.ExeName(exePath);
        if (exe.Length == 0) return null;
        foreach (var r in rules)
        {
            if (!r.Enabled || string.IsNullOrWhiteSpace(r.Process)) continue;
            string want = r.Process.Trim();
            if (!want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) want += ".exe";
            if (!string.Equals(want, exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(r.TitleContains) &&
                (title == null || title.IndexOf(r.TitleContains.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0)) continue;
            return r;
        }
        return null;
    }

    private static AppRule? MatchWindow(IEnumerable<AppRule> rules, IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return Match(rules, ShellInterop.GetProcessPath(pid), ShellInterop.GetTitle(hwnd));
    }

    /// <summary>True when an enabled "float" rule matches the window — the tiling window manager skips it.</summary>
    public static bool IsFloating(IntPtr hwnd)
    {
        var rules = _activeRules;
        if (rules.Count == 0 || !rules.Any(r => r.Enabled && r.Float)) return false;
        try { return MatchWindow(rules.Where(r => r.Float), hwnd) != null; }
        catch { return false; }
    }

    // ------------------------------------------------------------------ IDesktopModule

    public void Apply(DesktopSettings s)
    {
        try
        {
            bool wanted = s.Enabled && (s.AppRules.Any(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Process))
                                        || s.Layouts.Any(l => l.HotkeyKey > 0));
            if (!wanted) { Stop(); return; }
            _activeRules = s.AppRules.Select(Copy).ToList();

            if (!IsRunning)
            {
                IsRunning = true;
                // Windows that are already open are left alone; "apply to open windows" is explicit in the studio.
                foreach (var w in ShellInterop.GetAppWindows()) _seen.Add(w.Hwnd);
                _proc = OnWinEvent;
                uint flags = RulesNative.WINEVENT_OUTOFCONTEXT | RulesNative.WINEVENT_SKIPOWNPROCESS;
                _hooks.Add(RulesNative.SetWinEventHook(RulesNative.EVENT_OBJECT_DESTROY, RulesNative.EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, flags));
                _hooks.Add(RulesNative.SetWinEventHook(RulesNative.EVENT_SYSTEM_FOREGROUND, RulesNative.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, flags));
                _pendingTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                _pendingTimer.Tick += (_, _) => ProcessPending();
            }
            RegisterLayoutHotkeys(s);
        }
        catch (Exception ex) { AppLog.Error("Rules apply failed", ex); }
    }

    public void Stop()
    {
        UnregisterLayoutHotkeys();
        _activeRules = new();
        if (!IsRunning) return;
        try
        {
            foreach (var h in _hooks) if (h != IntPtr.Zero) RulesNative.UnhookWinEvent(h);
        }
        catch (Exception ex) { AppLog.Warn("Rules unhook: " + ex.Message); }
        _hooks.Clear();
        _proc = null;
        _pendingTimer?.Stop(); _pendingTimer = null;
        _pending.Clear(); _seen.Clear();
        IsRunning = false;
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) => null;

    // ------------------------------------------------------------------ events

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0) return;
        try
        {
            if (evt == RulesNative.EVENT_OBJECT_DESTROY) { _seen.Remove(hwnd); _pending.Remove(hwnd); return; }
            if (_seen.Contains(hwnd) || _pending.ContainsKey(hwnd)) return;
            // Titles and styles are often set just after the window is shown: retry for ~2 s.
            _pending[hwnd] = 8;
            _pendingTimer?.Start();
        }
        catch { }
    }

    private void ProcessPending()
    {
        foreach (var hwnd in _pending.Keys.ToList())
        {
            try
            {
                if (!ShellInterop.IsWindow(hwnd)) { _pending.Remove(hwnd); continue; }
                if (ShellInterop.IsTaskbarWindow(hwnd))
                {
                    _pending.Remove(hwnd);
                    _seen.Add(hwnd);
                    var rule = MatchWindow(_activeRules, hwnd);
                    if (rule != null) ApplyRule(hwnd, rule, newWindow: true);
                    continue;
                }
                if (--_pending[hwnd] <= 0) { _pending.Remove(hwnd); _seen.Add(hwnd); }
            }
            catch (Exception ex) { _pending.Remove(hwnd); AppLog.Warn("Rule check failed: " + ex.Message); }
        }
        if (_pending.Count == 0) _pendingTimer?.Stop();
    }

    // ------------------------------------------------------------------ applying

    /// <summary>Applies the matching rules to every window that is open right now. Returns how many were changed.</summary>
    public static int ApplyRulesToOpenWindows(IReadOnlyList<AppRule> rules)
    {
        int n = 0;
        foreach (var w in ShellInterop.GetAppWindows())
        {
            try
            {
                var rule = Match(rules, w.ExePath, w.Title);
                if (rule != null && ApplyRule(w.Hwnd, rule, newWindow: false)) n++;
            }
            catch (Exception ex) { AppLog.Warn("Applying rule failed: " + ex.Message); }
        }
        return n;
    }

    internal static bool ApplyRule(IntPtr hwnd, AppRule rule, bool newWindow)
    {
        bool changed = false;
        if (rule.UseFixedRect)
        {
            var mons = RulesNative.Monitors();
            if (mons.Count > 0)
            {
                var work = mons[Math.Clamp(rule.MonitorIndex, 0, mons.Count - 1)].Work;
                var target = new Rect(work.Left + rule.X, work.Top + rule.Y, Math.Max(120, rule.Width), Math.Max(80, rule.Height));
                // Keep it on screen even if the monitor got smaller.
                if (target.Right > work.Right) target.X = Math.Max(work.Left, work.Right - target.Width);
                if (target.Bottom > work.Bottom) target.Y = Math.Max(work.Top, work.Bottom - target.Height);
                changed |= RulesNative.PlaceVisual(hwnd, target);
            }
        }
        if (rule.StartMaximized && !ShellInterop.IsZoomed(hwnd)) { RulesNative.Maximize(hwnd); changed = true; }
        if (rule.AlwaysOnTop) { RulesNative.SetTopmost(hwnd, true); changed = true; }
        if (rule.VirtualDesktop > 0 && newWindow)
        {
            int now = RulesNative.DesktopNumberOf(hwnd);
            if (now > 0 && now != rule.VirtualDesktop && Instance != null && Instance._desktopWarned.Add(rule.Process))
                Notifier.Show($"«{rule.Process}» روی دسکتاپ {now} باز شد، نه {rule.VirtualDesktop}. ویندوز اجازه نمی‌دهد برنامه دیگری پنجره‌ها را خودکار جابه‌جا کند؛ با Win+Tab آن را به دسکتاپ {rule.VirtualDesktop} بکشید.");
        }
        return changed;
    }

    private static RulesModule? Instance;
    public RulesModule() { Instance = this; }

    private static AppRule Copy(AppRule r) => new()
    {
        Process = r.Process, TitleContains = r.TitleContains, Float = r.Float, AlwaysOnTop = r.AlwaysOnTop,
        StartMaximized = r.StartMaximized, UseFixedRect = r.UseFixedRect, X = r.X, Y = r.Y, Width = r.Width,
        Height = r.Height, MonitorIndex = r.MonitorIndex, VirtualDesktop = r.VirtualDesktop, Enabled = r.Enabled
    };

    // ------------------------------------------------------------------ layout hotkeys

    private void RegisterLayoutHotkeys(DesktopSettings s)
    {
        UnregisterLayoutHotkeys();
        for (int i = 0; i < s.Layouts.Count; i++)
        {
            var profile = s.Layouts[i];
            if (profile.HotkeyKey <= 0) continue;
            string id = "layout." + i;
            var snapshot = profile;
            HotkeyRegistry.Register(new HotkeyBinding(id, LayoutOwner, profile.Name, profile.HotkeyModifiers, profile.HotkeyKey),
                () => _ = LayoutProfiles.RestoreAsync(snapshot));
            _hotkeyIds.Add(id);
        }
    }

    private void UnregisterLayoutHotkeys()
    {
        foreach (var id in _hotkeyIds) HotkeyRegistry.Unregister(id);
        _hotkeyIds.Clear();
    }
}
