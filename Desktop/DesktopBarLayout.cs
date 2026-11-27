using System.Windows;

namespace Shamsa.Desktop;

internal static class DesktopBarLayout
{
    internal static string DockEdge(DesktopSettings s)
    {
        var edge = s.Dock.Position is "Left" or "Right" or "Top" ? s.Dock.Position : "Bottom";
        if (s.TopBar.Enabled && edge == s.TopBar.Position) return edge == "Top" ? "Bottom" : "Top";
        return edge;
    }

    // The anchor is the shell's granted strip or the monitor work area, in physical pixels.
    internal static int DockReservation(bool autoHide, double fullHeight, double scale) =>
        (int)Math.Ceiling((autoHide ? 2 : fullHeight) * scale);

    internal static Rect Strip(Rect anchor, string edge, double thickness) => edge switch
    {
        "Top" => new Rect(anchor.Left, anchor.Top, anchor.Width, thickness),
        "Left" => new Rect(anchor.Left, anchor.Top, thickness, anchor.Height),
        "Right" => new Rect(anchor.Right - thickness, anchor.Top, thickness, anchor.Height),
        _ => new Rect(anchor.Left, anchor.Bottom - thickness, anchor.Width, thickness)
    };

    internal static Rect SideSpan(Rect anchor, DesktopSettings s, double scale)
    {
        if (!s.TopBar.Enabled || s.TopBar.ReserveSpace || DockEdge(s) is "Top" or "Bottom") return anchor;
        var inset = Math.Min(s.TopBar.Height * scale, Math.Max(0, anchor.Height - 1));
        return new Rect(anchor.X, anchor.Y + (s.TopBar.Position == "Bottom" ? 0 : inset), anchor.Width, anchor.Height - inset);
    }
}
