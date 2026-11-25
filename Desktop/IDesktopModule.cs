using System.Windows;

namespace Shamsa.Desktop;

/// <summary>
/// One part of the Shamsa desktop environment (top bar, dock, launcher, window manager).
/// Implementations are created once on the UI thread by <see cref="DesktopFeature"/>.
/// </summary>
public interface IDesktopModule
{
    /// <summary>Stable id: "topbar", "dock", "launcher", "wm".</summary>
    string Id { get; }

    /// <summary>Persian display name for the studio and tray.</summary>
    string DisplayName { get; }

    bool IsRunning { get; }

    /// <summary>
    /// Starts, stops or reconfigures the module so it matches <paramref name="settings"/>
    /// (respecting both the master switch and the module's own Enabled flag). Idempotent.
    /// </summary>
    void Apply(DesktopSettings settings);

    /// <summary>Stops everything and undoes every change to the system (AppBars, hotkeys, hooks, window positions it saved).</summary>
    void Stop();

    /// <summary>
    /// A standalone, non-intrusive window that renders the module with real data — no AppBar, no
    /// hotkeys, no hooks, not topmost. Used for the Desktop Studio live preview and render self-tests.
    /// Return null when the module has no visual (e.g. the window manager).
    /// </summary>
    Window? CreatePreviewWindow(DesktopSettings settings);
}
