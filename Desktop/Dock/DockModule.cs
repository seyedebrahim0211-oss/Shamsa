using System.Windows;
using Shamsa.Services;

namespace Shamsa.Desktop.Dock;

/// <summary>macOS-style glass dock on the primary monitor.</summary>
public sealed class DockModule : IDesktopModule
{
    private DockWindow? _window;

    public string Id => "dock";
    public string DisplayName => "داک";
    public bool IsRunning => _window != null;

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Enabled || !settings.Dock.Enabled) { Stop(); return; }
        try
        {
            if (_window == null)
            {
                _window = new DockWindow(settings, preview: false);
                _window.Show();
            }
            else _window.ApplySettings(settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Dock start failed", ex);
            Stop();
        }
    }

    public void Stop()
    {
        try { _window?.Close(); } // Closed handler releases the AppBar and timers
        catch (Exception ex) { AppLog.Warn("Dock stop: " + ex.Message); }
        _window = null;
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) =>
        new DockWindow(settings, preview: true) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
}
