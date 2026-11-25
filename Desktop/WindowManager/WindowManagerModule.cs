using System.Windows;
using System.Windows.Threading;
using Shamsa.Desktop.Shell;
using Shamsa.Services;
using static Shamsa.Desktop.WindowManager.WmNative;

namespace Shamsa.Desktop.WindowManager;

/// <summary>
/// Tiling / snapping window manager. Snap hotkeys work whenever the module runs; auto-tiling arranges
/// every manageable window per monitor. Every window it touches gets its original placement saved and
/// restored when tiling is switched off or the module stops.
/// </summary>
public sealed class WindowManagerModule : IDesktopModule
{
    public const string Owner = "مدیر پنجره‌ها";

    public string Id => "wm";
    public string DisplayName => Owner;
    public bool IsRunning { get; private set; }

    /// <summary>Persian status text for the top bar (layout changes, tiling on/off).</summary>
    public static event Action<string>? StatusChanged;

    private static WindowManagerModule? _instance;

    /// <summary>
    /// Switches the running window manager to a layout ("Bsp", "Columns", "Rows", "Monocle", "MainStack")
    /// and turns tiling on. Used by saved layout profiles. Returns false when the module is not running.
    /// </summary>
    public static bool RequestLayout(string layout)
    {
        var wm = _instance;
        if (wm == null || !wm.IsRunning || string.IsNullOrWhiteSpace(layout)) return false;
        try
        {
            wm._layout = TileLayout.Parse(layout);
            if (!wm._tiling) wm.SetTiling(true, announce: false); else wm.Relayout();
            wm.Announce();
            return true;
        }
        catch (Exception ex) { AppLog.Warn("RequestLayout failed: " + ex.Message); return false; }
    }

    private WindowManagerSettings _cfg = new();
    private bool? _appliedAutoTile;
    private string? _appliedLayout;
    private bool _tiling;
    private TileLayoutKind _layout = TileLayoutKind.Bsp;

    private readonly List<IntPtr> _order = new();                    // tiled windows, in tiling order
    private readonly HashSet<IntPtr> _floating = new();              // user-floated or unmovable
    private readonly Dictionary<IntPtr, WINDOWPLACEMENT> _original = new();
    private readonly Dictionary<IntPtr, Rect> _assigned = new();     // last visual rect we gave a tiled window
    private readonly Dictionary<IntPtr, WINDOWPLACEMENT> _preMaxGap = new();

    private readonly List<IntPtr> _hooks = new();
    private WinEventProc? _hookProc;                                 // kept alive for the native callback
    private DispatcherTimer? _debounce;
    private DispatcherTimer? _ffmTimer;
    private IntPtr _ffmCandidate;
    private DateTime _ffmSince;
    private readonly List<string> _hotkeyIds = new();

    // ------------------------------------------------------------------ IDesktopModule

    public void Apply(DesktopSettings settings)
    {
        try
        {
            if (!settings.Enabled || !settings.WindowManager.Enabled) { Stop(); return; }
            _cfg = settings.WindowManager;
            _instance = this;
            if (!IsRunning)
            {
                IsRunning = true;
                InstallHooks();
                _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
                _debounce.Tick += (_, _) => { _debounce!.Stop(); Relayout(); };
            }

            // Adopt studio changes, but don't override a runtime toggle when unrelated settings change.
            if (_appliedLayout != _cfg.Layout) { _layout = TileLayout.Parse(_cfg.Layout); _appliedLayout = _cfg.Layout; }
            if (_appliedAutoTile != _cfg.AutoTile) { _appliedAutoTile = _cfg.AutoTile; SetTiling(_cfg.AutoTile, announce: false); }

            if (_cfg.HotkeysEnabled) RegisterHotkeys(); else UnregisterHotkeys();

            if (_cfg.FocusFollowsMouse)
            {
                if (_ffmTimer == null)
                {
                    _ffmTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
                    _ffmTimer.Tick += (_, _) => FocusFollowsMouseTick();
                    _ffmTimer.Start();
                }
            }
            else { _ffmTimer?.Stop(); _ffmTimer = null; }

            if (_tiling) ScheduleRelayout();
        }
        catch (Exception ex) { AppLog.Error("Window manager apply failed", ex); }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try
        {
            UnregisterHotkeys();
            foreach (var h in _hooks) UnhookWinEvent(h);
            _hooks.Clear();
            _hookProc = null;
            _debounce?.Stop(); _debounce = null;
            _ffmTimer?.Stop(); _ffmTimer = null;
            RestoreAll();
        }
        catch (Exception ex) { AppLog.Error("Window manager stop failed", ex); }
        finally
        {
            _tiling = false;
            _appliedAutoTile = null;
            _appliedLayout = null;
            IsRunning = false;
        }
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) => new WindowManagerPreviewWindow(settings, this);

    // ------------------------------------------------------------------ hotkeys

    private void RegisterHotkeys()
    {
        if (_hotkeyIds.Count > 0) return;
        const uint CA = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        const uint CAS = CA | NativeMethods.MOD_SHIFT;
        void Add(string id, string label, uint mods, int vk, Action a)
        {
            HotkeyRegistry.Register(new HotkeyBinding("wm." + id, Owner, label, mods, vk), () => Safe(a));
            _hotkeyIds.Add("wm." + id);
        }
        Add("left", "نیمه چپ", CA, 0x25, () => SnapFocused(SnapKind.Left));
        Add("right", "نیمه راست", CA, 0x27, () => SnapFocused(SnapKind.Right));
        Add("up", "نیمه بالا", CA, 0x26, () => SnapFocused(SnapKind.Top));
        Add("down", "نیمه پایین", CA, 0x28, () => SnapFocused(SnapKind.Bottom));
        Add("tl", "ربع بالا-چپ", CA, 0x55, () => SnapFocused(SnapKind.TopLeft));     // U
        Add("tr", "ربع بالا-راست", CA, 0x49, () => SnapFocused(SnapKind.TopRight));  // I
        Add("bl", "ربع پایین-چپ", CA, 0x4A, () => SnapFocused(SnapKind.BottomLeft)); // J
        Add("br", "ربع پایین-راست", CA, 0x4B, () => SnapFocused(SnapKind.BottomRight)); // K
        Add("max", "تمام‌صفحه با فاصله / بازگردانی", CA, 0x0D, ToggleMaxInGaps);
        Add("center", "وسط صفحه (۷۰٪)", CA, 0x43, () => SnapFocused(SnapKind.Center));
        Add("tile", "روشن/خاموش چیدمان خودکار", CA, 0x54, () => SetTiling(!_tiling, announce: true));
        Add("layout", "چیدمان بعدی", CA, 0x4C, CycleLayout);
        Add("float", "شناور کردن پنجره", CA, 0x46, ToggleFloatFocused);
        Add("prev", "پنجره قبلی", CA, 0xDB, () => FocusNeighbour(-1));  // [
        Add("next", "پنجره بعدی", CA, 0xDD, () => FocusNeighbour(+1));  // ]
        Add("swapl", "جابه‌جایی با قبلی", CAS, 0x25, () => SwapFocused(-1));
        Add("swapr", "جابه‌جایی با بعدی", CAS, 0x27, () => SwapFocused(+1));
    }

    private void UnregisterHotkeys()
    {
        foreach (var id in _hotkeyIds) HotkeyRegistry.Unregister(id);
        _hotkeyIds.Clear();
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { AppLog.Error("Window manager command failed", ex); }
    }

    // ------------------------------------------------------------------ events

    private void InstallHooks()
    {
        _hookProc = OnWinEvent;
        uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        foreach (var (min, max) in new[]
                 {
                     (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
                     (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND),
                     (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                     (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE)
                 })
        {
            IntPtr h = SetWinEventHook(min, max, IntPtr.Zero, _hookProc, 0, 0, flags);
            if (h != IntPtr.Zero) _hooks.Add(h);
            else AppLog.Warn($"SetWinEventHook 0x{min:X} failed");
        }
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW / CHILDID_SELF only
        try
        {
            switch (evt)
            {
                case EVENT_OBJECT_DESTROY:
                    if (_order.Remove(hwnd) | _floating.Remove(hwnd))
                    {
                        _original.Remove(hwnd); _assigned.Remove(hwnd); _preMaxGap.Remove(hwnd);
                        ScheduleRelayout();
                    }
                    else { _original.Remove(hwnd); _preMaxGap.Remove(hwnd); }
                    break;
                case EVENT_OBJECT_SHOW:
                case EVENT_OBJECT_HIDE:
                case EVENT_SYSTEM_MINIMIZESTART:
                case EVENT_SYSTEM_MINIMIZEEND:
                    if (_tiling && (evt != EVENT_OBJECT_SHOW || GetAncestor(hwnd, GA_ROOT) == hwnd)) ScheduleRelayout();
                    break;
                case EVENT_SYSTEM_MOVESIZEEND:
                    if (_tiling && _order.Contains(hwnd)) OnDragEnd(hwnd);
                    break;
                case EVENT_SYSTEM_FOREGROUND:
                    if (_tiling && _layout == TileLayoutKind.Monocle && _order.Contains(hwnd)) ScheduleRelayout();
                    break;
            }
        }
        catch (Exception ex) { AppLog.Warn("WM event: " + ex.Message); }
    }

    private void ScheduleRelayout()
    {
        if (_debounce == null) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDragEnd(IntPtr hwnd)
    {
        GetCursorPos(out var p);
        foreach (var other in _order)
        {
            if (other == hwnd || !_assigned.TryGetValue(other, out var r)) continue;
            if (r.Contains(new Point(p.X, p.Y)))
            {
                int a = _order.IndexOf(hwnd), b = _order.IndexOf(other);
                (_order[a], _order[b]) = (_order[b], _order[a]);
                break;
            }
        }
        ScheduleRelayout(); // either swapped, or snap the dragged window back into its tile
    }

    // ------------------------------------------------------------------ eligibility

    private static bool IsExcluded(string? exePath, WindowManagerSettings cfg)
    {
        if (string.IsNullOrEmpty(exePath)) return true; // protected/elevated process we can't even query
        string name = Path.GetFileName(exePath);
        return cfg.ExcludedProcesses.Any(x => string.Equals(x.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSystemClass(IntPtr hwnd)
    {
        string cls = NativeMethods.GetClassNameOf(hwnd);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    private bool IsDialogLike(IntPtr hwnd)
    {
        long style = Style(hwnd), ex = ExStyle(hwnd);
        if ((style & WS_THICKFRAME) == 0) return true;                       // fixed size
        if ((ex & WS_EX_DLGMODALFRAME) != 0) return true;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
        if ((style & WS_POPUP) != 0 && (style & WS_CAPTION) != WS_CAPTION) return true;
        return false;
    }

    /// <summary>Windows the tiler may arrange.</summary>
    internal List<AppWindow> ManageableWindows(WindowManagerSettings? cfgOverride = null)
    {
        var cfg = cfgOverride ?? _cfg;
        var list = new List<AppWindow>();
        foreach (var w in ShellInterop.GetAppWindows())
        {
            try
            {
                if (w.IsMinimized || _floating.Contains(w.Hwnd) || IsSystemClass(w.Hwnd)) continue;
                if (NativeMethods.IsOwnProcessWindow(w.Hwnd) || IsExcluded(w.ExePath, cfg)) continue;
                if ((Style(w.Hwnd) & WS_THICKFRAME) == 0) continue;
                if (cfg.FloatDialogs && IsDialogLike(w.Hwnd)) continue;
                if ((ExStyle(w.Hwnd) & WS_EX_TOPMOST) != 0) continue;           // pinned-on-top tools stay floating
                if (Rules.RulesModule.IsFloating(w.Hwnd)) continue;             // per-app "float" rule
                list.Add(w);
            }
            catch { }
        }
        return list;
    }

    private static bool CanSnap(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && ShellInterop.IsWindow(hwnd) && !IsSystemClass(hwnd)
        && !NativeMethods.IsOwnProcessWindow(hwnd) && (Style(hwnd) & WS_THICKFRAME) != 0;

    // ------------------------------------------------------------------ tiling

    private void SetTiling(bool on, bool announce)
    {
        if (on == _tiling) { if (announce) Announce(); return; }
        _tiling = on;
        if (on) Relayout();
        else RestoreAll();
        if (announce || on) Announce();
    }

    private void CycleLayout()
    {
        _layout = TileLayout.Next(_layout);
        if (!_tiling) SetTiling(true, announce: false);
        else Relayout();
        Announce();
    }

    private void Announce()
    {
        string text = _tiling ? "چیدمان: " + TileLayout.PersianName(_layout) : "چیدمان خودکار خاموش شد";
        try { StatusChanged?.Invoke(text); } catch { }
    }

    /// <summary>Computes, without applying, the visual rect for each manageable window, grouped by monitor.</summary>
    internal List<(AppWindow Window, Rect Target, Rect WorkArea)> PlanLayout(IReadOnlyList<AppWindow> windows, TileLayoutKind layout, int gap, int padding)
    {
        var plan = new List<(AppWindow, Rect, Rect)>();
        foreach (var group in windows.GroupBy(w => w.Monitor))
        {
            var (_, work) = ShellInterop.GetMonitorRects(group.Key);
            if (work.IsEmpty) continue;
            var items = group.ToList();
            if (layout == TileLayoutKind.Monocle)
            {
                var r = TileLayout.Compute(work, 1, layout, gap, padding)[0];
                foreach (var w in items) plan.Add((w, r, work));
                continue;
            }
            var rects = TileLayout.Compute(work, items.Count, layout, gap, padding);
            for (int i = 0; i < items.Count; i++) plan.Add((items[i], rects[i], work));
        }
        return plan;
    }

    private void Relayout()
    {
        if (!_tiling || !IsRunning) return;
        try
        {
            var current = ManageableWindows();
            var set = current.Select(w => w.Hwnd).ToHashSet();
            _order.RemoveAll(h => !set.Contains(h));
            foreach (var w in current) if (!_order.Contains(w.Hwnd)) _order.Add(w.Hwnd);
            var byHwnd = current.ToDictionary(w => w.Hwnd);
            var ordered = _order.Where(byHwnd.ContainsKey).Select(h => byHwnd[h]).ToList();

            _assigned.Clear();
            foreach (var (w, target, _) in PlanLayout(ordered, _layout, _cfg.Gap, _cfg.OuterPadding))
            {
                if (!Place(w.Hwnd, target))
                {
                    _floating.Add(w.Hwnd); // e.g. elevated window (UIPI) — leave it alone from now on
                    _order.Remove(w.Hwnd);
                    continue;
                }
                _assigned[w.Hwnd] = target;
            }
        }
        catch (Exception ex) { AppLog.Error("Window manager relayout failed", ex); }
    }

    private void RestoreAll()
    {
        foreach (var (hwnd, wp) in _original.ToList())
        {
            try
            {
                if (!ShellInterop.IsWindow(hwnd)) continue;
                var p = wp;
                p.length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>();
                SetWindowPlacement(hwnd, ref p);
            }
            catch { }
        }
        _original.Clear();
        _order.Clear();
        _assigned.Clear();
        _preMaxGap.Clear();
        _floating.Clear();
    }

    private void Remember(IntPtr hwnd)
    {
        if (_original.ContainsKey(hwnd)) return;
        var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hwnd, ref wp)) _original[hwnd] = wp;
    }

    /// <summary>Moves a window so its *visible* frame matches <paramref name="target"/> (physical pixels).</summary>
    private bool Place(IntPtr hwnd, Rect target)
    {
        Remember(hwnd);
        if (ShellInterop.IsZoomed(hwnd) || ShellInterop.IsIconic(hwnd)) ShellInterop.ShowWindow(hwnd, ShellInterop.SW_RESTORE);
        // Invisible resize borders: window rect vs DWM extended frame bounds.
        int l = 0, t = 0, r = 0, b = 0;
        if (GetWindowRect(hwnd, out var wr) &&
            DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var ef, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
        {
            l = ef.Left - wr.Left; t = ef.Top - wr.Top; r = wr.Right - ef.Right; b = wr.Bottom - ef.Bottom;
            if (l is < 0 or > 40) l = 0; if (t is < 0 or > 40) t = 0; if (r is < 0 or > 40) r = 0; if (b is < 0 or > 40) b = 0;
        }
        return SetWindowPos(hwnd, IntPtr.Zero,
            (int)target.X - l, (int)target.Y - t, (int)target.Width + l + r, (int)target.Height + t + b,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    // ------------------------------------------------------------------ commands

    private void SnapFocused(SnapKind kind)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (!CanSnap(fg)) return;
        var (_, work) = ShellInterop.GetMonitorRects(ShellInterop.MonitorFromWindow(fg, 2));
        if (work.IsEmpty) return;
        if (_tiling && _order.Remove(fg)) { _floating.Add(fg); ScheduleRelayout(); } // a snapped window leaves the tiling
        Place(fg, TileLayout.Snap(work, kind, _cfg.Gap, _cfg.OuterPadding));
    }

    private void ToggleMaxInGaps()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (!CanSnap(fg)) return;
        if (_preMaxGap.Remove(fg, out var before))
        {
            before.length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>();
            SetWindowPlacement(fg, ref before);
            return;
        }
        var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(fg, ref wp)) _preMaxGap[fg] = wp;
        SnapFocused(SnapKind.Full);
    }

    private void ToggleFloatFocused()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (!CanSnap(fg)) return;
        if (_floating.Remove(fg))
        {
            StatusChanged?.Invoke("پنجره به چیدمان برگشت");
        }
        else
        {
            _floating.Add(fg);
            _order.Remove(fg);
            if (_original.Remove(fg, out var wp))
            {
                wp.length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>();
                SetWindowPlacement(fg, ref wp);
            }
            StatusChanged?.Invoke("پنجره شناور شد");
        }
        if (_tiling) ScheduleRelayout();
    }

    private List<IntPtr> NavigationOrder()
    {
        if (_tiling && _order.Count > 0) return _order.Where(ShellInterop.IsWindow).ToList();
        // Without tiling: visible app windows on the focused monitor, left→right, top→bottom.
        IntPtr mon = ShellInterop.MonitorFromWindow(NativeMethods.GetForegroundWindow(), 2);
        return ShellInterop.GetAppWindows()
            .Where(w => !w.IsMinimized && w.Monitor == mon)
            .Select(w => { GetWindowRect(w.Hwnd, out var r); return (w.Hwnd, r); })
            .OrderBy(x => x.r.Left).ThenBy(x => x.r.Top)
            .Select(x => x.Hwnd).ToList();
    }

    private void FocusNeighbour(int delta)
    {
        var list = NavigationOrder();
        if (list.Count == 0) return;
        int i = list.IndexOf(NativeMethods.GetForegroundWindow());
        int next = i < 0 ? 0 : ((i + delta) % list.Count + list.Count) % list.Count;
        ShellInterop.Activate(list[next]);
    }

    private void SwapFocused(int delta)
    {
        if (!_tiling) { StatusChanged?.Invoke("جابه‌جایی فقط در چیدمان خودکار"); return; }
        IntPtr fg = NativeMethods.GetForegroundWindow();
        int i = _order.IndexOf(fg);
        if (i < 0 || _order.Count < 2) return;
        int j = ((i + delta) % _order.Count + _order.Count) % _order.Count;
        (_order[i], _order[j]) = (_order[j], _order[i]);
        Relayout();
    }

    // ------------------------------------------------------------------ focus follows mouse

    private void FocusFollowsMouseTick()
    {
        try
        {
            if (AnyMouseButtonDown() || !GetCursorPos(out var p)) { _ffmCandidate = IntPtr.Zero; return; }
            IntPtr root = GetAncestor(WindowFromPoint(p), GA_ROOT);
            if (root == IntPtr.Zero || root == NativeMethods.GetForegroundWindow()) { _ffmCandidate = IntPtr.Zero; return; }
            bool managed = _tiling ? _order.Contains(root) : CanSnap(root) && ShellInterop.IsTaskbarWindow(root);
            if (!managed) { _ffmCandidate = IntPtr.Zero; return; }
            if (root != _ffmCandidate) { _ffmCandidate = root; _ffmSince = DateTime.UtcNow; return; }
            if (DateTime.UtcNow - _ffmSince >= TimeSpan.FromMilliseconds(250))
            {
                ShellInterop.SetForegroundWindow(root);
                _ffmCandidate = IntPtr.Zero;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ preview support

    internal TileLayoutKind CurrentLayout => _layout;
    internal bool TilingActive => _tiling;
}
