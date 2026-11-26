using System.Windows;
using Shamsa.Services;

namespace Shamsa.Desktop.Launcher;

/// <summary>Spotlight-style quick search (apps, settings, files, commands, calculator, web, clipboard history).</summary>
public sealed class LauncherModule : IDesktopModule
{
    public const string HotkeyId = "launcher.open";
    public const string ClipboardHotkeyId = "clipboard.open";
    private LauncherIndex? _index;
    private LauncherWindow? _window;
    private DesktopSettings? _settings;
    private bool _subscribed;

    /// <summary>The running launcher (null when the module is stopped). Used by voice commands.</summary>
    public static LauncherModule? Instance { get; private set; }

    /// <summary>The live app index of the running launcher, if any.</summary>
    public static LauncherIndex? SharedIndex => Instance?._index;

    public string Id => "launcher";
    public string DisplayName => "جستجوی سریع";
    public bool IsRunning => _settings != null;

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Enabled || !settings.Launcher.Enabled) { Stop(); return; }
        _settings = settings;
        Instance = this;
        _index ??= new LauncherIndex();
        _index.Warm(settings.Launcher.SearchFiles);
        HotkeyRegistry.Register(new HotkeyBinding(HotkeyId, "جستجوی سریع", "باز کردن",
            settings.Launcher.HotkeyModifiers, settings.Launcher.HotkeyKey), Toggle);
        if (!_subscribed) { DesktopEvents.LauncherRequested += Toggle; _subscribed = true; }

        if (settings.Clipboard.Enabled)
        {
            try { ClipboardHistory.Start(settings.Clipboard); }
            catch (Exception ex) { AppLog.Error("Clipboard history failed to start", ex); }
            HotkeyRegistry.Register(new HotkeyBinding(ClipboardHotkeyId, "کلیپ‌بورد", "تاریخچه کلیپ‌بورد",
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x56 /* V */), () => Show(clipboardMode: true));
        }
        else
        {
            HotkeyRegistry.Unregister(ClipboardHotkeyId);
            ClipboardHistory.Stop();
        }
        _window?.ApplySettings(settings);
    }

    public void Toggle()
    {
        if (_window is { IsVisible: true }) { _window.HideLauncher(); return; }
        Show();
    }

    /// <summary>Opens the launcher, optionally in clipboard mode or pre-filled with a query.</summary>
    public void Show(bool clipboardMode = false, string? query = null)
    {
        if (_settings == null || _index == null) return;
        try
        {
            _index.Warm(_settings.Launcher.SearchFiles);
            _window ??= new LauncherWindow(_index, _settings, preview: false);
            _window.ApplySettings(_settings);
            _window.ShowLauncher(clipboardMode, query);
        }
        catch (Exception ex) { AppLog.Error("Launcher show failed", ex); }
    }

    /// <summary>Opens the running launcher with a query (voice "جستجو …"). Returns false when the launcher is off.</summary>
    public static bool OpenWithQuery(string query)
    {
        if (Instance == null) return false;
        Instance.Show(query: query);
        return true;
    }

    public void Stop()
    {
        HotkeyRegistry.Unregister(HotkeyId);
        HotkeyRegistry.Unregister(ClipboardHotkeyId);
        if (_subscribed) { DesktopEvents.LauncherRequested -= Toggle; _subscribed = false; }
        try { ClipboardHistory.Stop(); } catch { }
        try { _window?.Close(); } catch { }
        _window = null;
        _index?.Dispose();
        _index = null;
        _settings = null;
        if (Instance == this) Instance = null;
    }

    public Window? CreatePreviewWindow(DesktopSettings settings)
    {
        var index = new LauncherIndex();
        index.Warm(settings.Launcher.SearchFiles);
        var w = new LauncherWindow(index, settings, preview: true) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        w.SetQuery("تنظ");
        w.Closed += (_, _) => index.Dispose();
        return w;
    }
}
