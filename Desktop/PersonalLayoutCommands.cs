using System.Windows;
using Shamsa.Desktop.Widgets;

namespace Shamsa.Desktop;

internal static class PersonalLayoutCommands
{
    internal static event Action? BeforeChange;
    internal static event Action<string>? Committed;
    private static readonly LayoutDisplayTracker Display = new();

    internal static string Run(Func<DesktopSettings, Rect, LayoutResult> change)
    {
        BeforeChange?.Invoke();
        if (Application.Current != null)
            foreach (var window in Application.Current.Windows.OfType<DesktopLevelWindow>().ToArray())
                window.FlushPendingBounds();
        var s = DesktopSettingsStore.Current.Clone();
        var area = s.Fences.AvoidOverlap ? DesktopDisplay.PlacementArea(s) : DesktopDisplay.RelativeWorkArea(s);
        var result = change(s, area);
        if (!result.Save) return result.Message;
        if (!DesktopSettingsStore.Save(s, requireDisk: true))
            return "ذخیره انجام نشد. فضای دیسک و دسترسی نوشتن تنظیمات را بررسی کن.";
        Committed?.Invoke(result.Message);
        return result.Message;
    }

    internal static string Apply(string id) => Run((s, area) => PersonalLayouts.Switch(s, area, id));
    internal static string Undo() => Run((s, area) => PersonalLayouts.Switch(s, area, null, undo: true));

    internal static void FollowDisplay(DesktopSettings s)
    {
        if (!s.Enabled)
        {
            Display.Enter(s.PersonalLayouts, "", 0, 0, false);
            return;
        }
        var screen = DesktopDisplay.Target(s);
        var id = Display.Enter(s.PersonalLayouts, screen.DeviceName, screen.Bounds.Width, screen.Bounds.Height, true);
        if (id != null) Apply(id);
    }
}
