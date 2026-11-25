using System.Windows;
using Shamsa.Services;

namespace Shamsa.Desktop.Launcher;

/// <summary>Spotlight-style quick search (apps, settings, files, commands, calculator, web).</summary>
public sealed class LauncherModule : IDesktopModule
{
    public const string HotkeyId = "launcher.open";
    private LauncherIndex? _index;
    private LauncherWindow? _window;
    private DesktopSettings? _settings;
    private bool _subscribed;

    public string Id => "launcher";
    public string DisplayName => "جستجوی سریع";
    public bool IsRunning => _settings != null;

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Enabled || !settings.Launcher.Enabled) { Stop(); return; }
        _settings = settings;
        _index ??= new LauncherIndex();
        _index.Warm(settings.Launcher.SearchFiles);
        HotkeyRegistry.Register(new HotkeyBinding(HotkeyId, "جستجوی سریع", "باز کردن",
            settings.Launcher.HotkeyModifiers, settings.Launcher.HotkeyKey), Toggle);
        if (!_subscribed) { DesktopEvents.LauncherRequested += Toggle; _subscribed = true; }
        _window?.ApplySettings(settings);
    }

    public void Toggle()
    {
        if (_settings == null || _index == null) return;
        try
        {
            if (_window is { IsVisible: true }) { _window.HideLauncher(); return; }
            _index.Warm(_settings.Launcher.SearchFiles);
            _window ??= new LauncherWindow(_index, _settings, preview: false);
            _window.ApplySettings(_settings);
            _window.ShowLauncher();
        }
        catch (Exception ex) { AppLog.Error("Launcher toggle failed", ex); }
    }

    public void Stop()
    {
        HotkeyRegistry.Unregister(HotkeyId);
        if (_subscribed) { DesktopEvents.LauncherRequested -= Toggle; _subscribed = false; }
        try { _window?.Close(); } catch { }
        _window = null;
        _index?.Dispose();
        _index = null;
        _settings = null;
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
