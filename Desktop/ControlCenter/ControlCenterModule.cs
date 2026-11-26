using System.Windows;
using Shamsa.Services;

namespace Shamsa.Desktop.ControlCenter;

/// <summary>Quick-settings flyout opened from the top bar (or the tray / Desktop Studio).</summary>
public sealed class ControlCenterModule : IDesktopModule
{
    private ControlCenterWindow? _window;
    private bool _subscribed;
    private DesktopSettings? _settings;

    public string Id => "controlcenter";
    public string DisplayName => "مرکز کنترل";
    public bool IsRunning => _subscribed;

    public void Apply(DesktopSettings settings)
    {
        _settings = settings;
        // The flyout is useful even when the rest of the desktop environment is off (tray / studio button),
        // so it only depends on its own switch.
        bool want = settings.ControlCenter.Enabled;
        if (want && !_subscribed) { DesktopEvents.ControlCenterRequested += Toggle; _subscribed = true; }
        else if (!want && _subscribed) Stop();
    }

    public void Stop()
    {
        if (_subscribed) { DesktopEvents.ControlCenterRequested -= Toggle; _subscribed = false; }
        try { _window?.Close(); } catch { }
        _window = null;
    }

    /// <summary>Opens the flyout, or closes it when already open.</summary>
    public void Toggle()
    {
        try
        {
            if (_window is { IsLoaded: true }) { _window.Close(); _window = null; return; }
            _window = new ControlCenterWindow(_settings ?? DesktopSettingsStore.Current, preview: false);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
            _window.Activate();
        }
        catch (Exception ex) { AppLog.Error("Control center failed to open", ex); }
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) => new ControlCenterWindow(settings, preview: true);
}
