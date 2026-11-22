using System.Windows.Input;
using System.Windows.Interop;

namespace Shamsa.Services;

/// <summary>
/// Registers a single system-wide keyboard shortcut via Win32 RegisterHotKey on a hidden message-only window.
/// Zero CPU usage when idle and works even when File Explorer or Desktop has focus.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x5A11;
    private HwndSource? _source;
    private bool _registered;

    public bool IsRegistered => _registered;
    public event Action? HotkeyPressed;

    public void EnsureWindow()
    {
        if (_source != null) return;
        var p = new HwndSourceParameters("ShamsaHotkeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE (message-only window)
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool Register(uint modifiers, int virtualKey)
    {
        EnsureWindow();
        Unregister();

        if (virtualKey <= 0 || _source == null) return false;
        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, (uint)virtualKey);
        if (_registered)
            AppLog.Info($"Global hotkey registered: {FormatShortcut(modifiers, virtualKey)}");
        else
            AppLog.Warn($"Global hotkey registration failed (probably in use): {FormatShortcut(modifiers, virtualKey)}");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source != null)
        {
            try { NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId); } catch { }
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public static string FormatShortcut(uint modifiers, int virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");

        Key wpfKey = KeyInterop.KeyFromVirtualKey(virtualKey);
        string keyName = wpfKey switch
        {
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            Key.Oem3 => "`", Key.OemMinus => "-", Key.OemPlus => "=",
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemQuestion => "/", Key.OemPeriod => ".", Key.OemComma => ",",
            Key.Space => "Space",
            Key.None => $"Key(0x{virtualKey:X})",
            _ => wpfKey.ToString()
        };
        parts.Add(keyName);
        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        Unregister();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
