using System.Windows;
using System.Windows.Controls;
using Shamsa.Services;

namespace Shamsa.Views;

/// <summary>
/// Lets the «نگارخانه» hub show the dashboard's tabs and settings sections as its own pages. The window
/// itself is never shown: its constructor does all initialisation, so its parts can be re-parented
/// into the hub while every handler (bound to this instance) keeps working.
/// </summary>
public partial class ToolsDashboardWindow
{
    /// <summary>The window that hosts the detached parts (owner for dialogs and message boxes).</summary>
    public Window? Host { get; set; }

    private Window Dlg => Host ?? this;

    /// <summary>Raised instead of switching tabs when hosted: hub page id to open.</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>Hub page id for each dashboard tab (0 guide, 1 organizer, 2 rename, 3 duplicates, 4 converter, 5 settings).</summary>
    public static readonly string[] TabPageIds = { "home", "files.organize", "files.rename", "files.duplicates", "files.convert", "settings" };

    /// <summary>Removes the content of tab <paramref name="index"/> so it can be placed in another window.</summary>
    public FrameworkElement? DetachTab(int index)
    {
        if (index < 0 || index >= Tabs.Items.Count || Tabs.Items[index] is not TabItem tab) return null;
        var content = tab.Content as FrameworkElement;
        tab.Content = null;
        return content;
    }

    /// <summary>Removes a named element (e.g. a settings section) from its parent panel.</summary>
    public FrameworkElement? DetachNamed(string name)
    {
        if (FindName(name) is not FrameworkElement e) return null;
        UiHost.Detach(e);
        return e;
    }

    /// <summary>Stops timers and static subscriptions when the hub that shows these parts closes.</summary>
    public void ReleaseHosted()
    {
        UpdateService.StateChanged -= OnUpdateStateChanged;
        _dupCts?.Cancel();
        _convertCts?.Cancel();
        _renameDebounce.Stop();
        _organizerDebounce.Stop();
    }
}
