using System.Windows.Interop;
using Shamsa.Services;

namespace Shamsa.Recording;

public enum RecorderHotkey { Record = 1, Pause = 2, Screenshot = 3 }

/// <summary>Several system-wide shortcuts on a dedicated message-only window (independent of the donut hotkey).</summary>
internal sealed class RecorderHotkeys : IDisposable
{
    private const int BaseId = 0x6A00;
    private HwndSource? _source;
    private readonly HashSet<int> _registered = new();

    public event Action<RecorderHotkey>? Pressed;

    private void EnsureWindow()
    {
        if (_source != null) return;
        _source = new HwndSource(new HwndSourceParameters("ShamsaRecorderHotkeys")
        {
            Width = 0, Height = 0, WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers all bindings; returns a Persian description of any that failed (empty = all ok).</summary>
    public string RegisterAll(RecorderSettings s)
    {
        UnregisterAll();
        if (!s.HotkeysEnabled) return string.Empty;
        EnsureWindow();
        var failures = new List<string>();
        void Reg(RecorderHotkey id, HotkeyBinding b, string label)
        {
            var shared = new Services.HotkeyBinding("recorder." + id.ToString().ToLowerInvariant(), "ضبط صفحه", label, b.Modifiers, b.Key);
            if (b.Key <= 0) { HotkeyRegistry.Report(shared, false); return; }
            if (HotkeyRegistry.FindConflict(b.Modifiers, b.Key, shared.Id) is { } other)
            {
                failures.Add($"{label} ({GlobalHotkeyService.FormatShortcut(b.Modifiers, b.Key)}) با {other}");
                HotkeyRegistry.Report(shared, false);
                return;
            }
            int hid = BaseId + (int)id;
            bool ok = NativeMethods.RegisterHotKey(_source!.Handle, hid, b.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)b.Key);
            HotkeyRegistry.Report(shared, ok);
            if (ok)
                _registered.Add(hid);
            else
                failures.Add($"{label} ({GlobalHotkeyService.FormatShortcut(b.Modifiers, b.Key)})");
        }
        Reg(RecorderHotkey.Record, s.HotkeyRecord, "شروع/توقف");
        Reg(RecorderHotkey.Pause, s.HotkeyPause, "مکث");
        Reg(RecorderHotkey.Screenshot, s.HotkeyShot, "اسکرین‌شات");
        if (failures.Count > 0)
            AppLog.Warn("Recorder hotkeys failed: " + string.Join(", ", failures));
        return failures.Count == 0 ? string.Empty : "این کلیدها توسط برنامه دیگری استفاده می‌شوند: " + string.Join("، ", failures);
    }

    public void UnregisterAll()
    {
        if (_source == null) return;
        foreach (int id in _registered)
        {
            try { NativeMethods.UnregisterHotKey(_source.Handle, id); } catch { }
        }
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32() - BaseId;
            if (id is >= 1 and <= 3)
            {
                handled = true;
                Pressed?.Invoke((RecorderHotkey)id);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
