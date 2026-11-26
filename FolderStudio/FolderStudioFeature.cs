using System.Windows;

namespace Shamsa.FolderStudio;

/// <summary>Entry points used by App / dashboard / tray / command line.</summary>
public static class FolderStudioFeature
{
    private static FolderStudioWindow? _window;

    /// <summary>Opens (or focuses) the studio. tab 0 = icons, 1 = lock. Must be called on the UI thread.</summary>
    public static void ShowWindow(IEnumerable<string>? folders = null, int tab = 0)
    {
        var w = EnsureWindow();
        if (folders != null)
        {
            var list = folders.ToList();
            if (tab == 1 && list.Count > 0) w.SetLockFolder(list[0]);
            else w.AddFolders(list);
        }
        w.SelectTab(tab);
        Activate(w);
    }

    /// <summary>Handles --folder-studio "&lt;dir&gt;" | --folder-lock "&lt;dir&gt;" | --unlock "&lt;file.shamsalock&gt;". Returns true when handled.</summary>
    public static bool TryHandleArgs(string[] args)
    {
        if (args == null || args.Length == 0) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? value = i + 1 < args.Length ? args[i + 1].Trim('"') : null;
            if (a.Equals("--folder-studio", StringComparison.OrdinalIgnoreCase))
            {
                ShowWindow(value != null && Directory.Exists(value) ? new[] { value } : null, 0);
                return true;
            }
            if (a.Equals("--folder-lock", StringComparison.OrdinalIgnoreCase))
            {
                ShowWindow(value != null && Directory.Exists(value) ? new[] { value } : null, 1);
                return true;
            }
            if (a.Equals("--unlock", StringComparison.OrdinalIgnoreCase))
            {
                var w = EnsureWindow();
                if (value != null && File.Exists(value)) w.SetVault(value);
                else w.SelectTab(1);
                Activate(w);
                return true;
            }
        }
        return false;
    }

    public static bool IsShellIntegrationEnabled() => FolderShellIntegration.IsEnabled();

    public static void SetShellIntegrationEnabled(bool on) => FolderShellIntegration.SetEnabled(on);

    private static FolderStudioWindow EnsureWindow()
    {
        if (_window == null || !_window.IsLoaded)
        {
            _window = new FolderStudioWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        return _window;
    }

    private static void Activate(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;  // bring in front of Explorer when launched from the context menu
        w.Topmost = false;
        w.Focus();
    }
}
