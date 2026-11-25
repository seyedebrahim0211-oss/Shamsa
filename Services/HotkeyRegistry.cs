using System.Windows.Interop;

namespace Shamsa.Services;

public sealed record HotkeyBinding(string Id, string Owner, string Label, uint Modifiers, int Key)
{
    public string Display => Key <= 0 ? "—" : GlobalHotkeyService.FormatShortcut(Modifiers, Key);
    public bool SameCombo(uint mods, int key) => Key > 0 && Key == key && (Modifiers & 0x000F) == (mods & 0x000F);
}

public enum HotkeyResult { Ok, Disabled, ConflictInternal, InUseByOtherApp, Invalid }

public sealed record HotkeyStatus(HotkeyBinding Binding, HotkeyResult Result, string? Detail);

/// <summary>
/// Single owner of every global shortcut in Shamsa. All modules register here, so two features can
/// never silently fight over the same key combination; the Desktop Studio shows the full table with
/// conflicts. Must be used from the UI thread.
/// </summary>
public static class HotkeyRegistry
{
    private static HwndSource? _sink;
    private static int _nextId = 0x6000;
    private static readonly Dictionary<string, (int NativeId, HotkeyBinding Binding, Action Callback)> Active = new();
    private static readonly Dictionary<string, HotkeyStatus> Statuses = new();

    /// <summary>Raised after any registration change (for the shortcut overview UI).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<HotkeyStatus> All =>
        Statuses.Values.OrderBy(s => s.Binding.Owner).ThenBy(s => s.Binding.Label).ToList();

    private static void EnsureSink()
    {
        if (_sink != null) return;
        _sink = new HwndSource(new HwndSourceParameters("ShamsaHotkeyRegistry")
        {
            Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        });
        _sink.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
            int id = w.ToInt32();
            foreach (var entry in Active.Values)
            {
                if (entry.NativeId != id) continue;
                handled = true;
                try { entry.Callback(); }
                catch (Exception ex) { AppLog.Error($"Hotkey {entry.Binding.Id} failed", ex); }
                break;
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Returns the label of another Shamsa binding that already uses this combination.</summary>
    public static string? FindConflict(uint modifiers, int key, string? exceptId)
    {
        if (key <= 0) return null;
        foreach (var s in Statuses.Values)
        {
            if (s.Binding.Id == exceptId || s.Result is HotkeyResult.Disabled) continue;
            if (s.Binding.SameCombo(modifiers, key)) return $"{s.Binding.Owner} › {s.Binding.Label}";
        }
        return null;
    }

    /// <summary>Registers (or re-registers) a binding. A Key of 0 means "disabled".</summary>
    public static HotkeyResult Register(HotkeyBinding binding, Action callback)
    {
        Unregister(binding.Id, notify: false);
        HotkeyResult result;
        string? detail = null;
        if (binding.Key <= 0)
            result = HotkeyResult.Disabled;
        else if (FindConflict(binding.Modifiers, binding.Key, binding.Id) is { } other)
        {
            result = HotkeyResult.ConflictInternal;
            detail = "تداخل با " + other;
        }
        else
        {
            EnsureSink();
            int id = _nextId++;
            if (NativeMethods.RegisterHotKey(_sink!.Handle, id, binding.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)binding.Key))
            {
                Active[binding.Id] = (id, binding, callback);
                result = HotkeyResult.Ok;
            }
            else
            {
                result = HotkeyResult.InUseByOtherApp;
                detail = "این کلید را برنامه دیگری یا خود ویندوز گرفته است";
            }
        }
        Statuses[binding.Id] = new HotkeyStatus(binding, result, detail);
        if (result is HotkeyResult.ConflictInternal or HotkeyResult.InUseByOtherApp)
            AppLog.Warn($"Hotkey {binding.Display} for {binding.Id}: {result} {detail}");
        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// For modules that register through their own window (legacy donut / recorder): records the
    /// binding so conflicts are detected and shown, without registering it again.
    /// </summary>
    public static void Report(HotkeyBinding binding, bool registeredOk)
    {
        var result = binding.Key <= 0 ? HotkeyResult.Disabled : registeredOk ? HotkeyResult.Ok : HotkeyResult.InUseByOtherApp;
        Statuses[binding.Id] = new HotkeyStatus(binding, result, registeredOk ? null : "ثبت ناموفق (احتمالاً در حال استفاده)");
        Changed?.Invoke();
    }

    public static void Unregister(string id) => Unregister(id, notify: true);

    private static void Unregister(string id, bool notify)
    {
        if (Active.Remove(id, out var entry) && _sink != null)
            NativeMethods.UnregisterHotKey(_sink.Handle, entry.NativeId);
        if (Statuses.Remove(id) && notify) Changed?.Invoke();
    }

    public static void Dispose()
    {
        foreach (var id in Active.Keys.ToList()) Unregister(id, notify: false);
        _sink?.Dispose();
        _sink = null;
    }
}
