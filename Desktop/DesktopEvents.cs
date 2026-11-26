namespace Shamsa.Desktop;

/// <summary>Loose coupling between desktop modules (e.g. the top bar's launcher button opens the launcher).</summary>
public static class DesktopEvents
{
    public static event Action? LauncherRequested;
    public static event Action<string?>? StudioRequested;
    public static event Action? ControlCenterRequested;

    public static void RequestLauncher() => LauncherRequested?.Invoke();
    /// <summary>Opens Desktop Studio, optionally on a page id ("topbar", "dock", "launcher", "wm", "theme", "hotkeys").</summary>
    public static void RequestStudio(string? page = null) => StudioRequested?.Invoke(page);
    public static void RequestControlCenter() => ControlCenterRequested?.Invoke();
}
