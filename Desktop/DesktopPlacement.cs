using System.Windows;
using Shamsa.Desktop.Widgets;

namespace Shamsa.Desktop;

/// <summary>Places windows in monitor-relative DIPs. Each free rectangle can hold a whole window.</summary>
internal static class DesktopPlacement
{
    internal const double CollapsedHeight = 40;
    private const double Gap = 10;

    internal static Rect Bounds(FenceDefinition f) => new(f.X, f.Y, Math.Max(200, f.Width),
        f.Collapsed ? CollapsedHeight : Math.Max(120, f.Height));

    internal static Rect Bounds(WidgetPlacement p)
    {
        var (w, h) = WidgetViews.DefaultSize(p.Kind);
        return new Rect(p.X, p.Y, p.Width > 60 ? Math.Max(160, p.Width) : w, p.Height > 60 ? Math.Max(90, p.Height) : h);
    }

    internal static bool Arrange(DesktopSettings settings, Rect area, bool arrangeGroups = false)
    {
        if (area.IsEmpty || area.Width < 200 + 2 * Gap || area.Height < 120 + 2 * Gap) return false;
        area.Inflate(-Gap, -Gap);
        var free = new List<Rect> { area };
        bool fits = true;
        if (settings.Widgets.Enabled)
        {
            foreach (var widget in settings.Widgets.Items)
            {
                Rect wanted = Bounds(widget);
                fits &= Place(area, free, wanted, true, out var r);
                widget.X = r.X; widget.Y = r.Y; widget.Width = r.Width; widget.Height = r.Height;
            }
        }
        if (!settings.Fences.Enabled || settings.Fences.Fences.Count == 0) return fits;

        // Prefer the saved sizes. On small displays retry the whole group layout at more compact sizes.
        var groups = settings.Fences.Fences;
        var result = new Rect[groups.Count];
        bool allFit = false;
        foreach (var max in new[] { new Size(area.Width, area.Height), new Size(290, 195), new Size(210, 125) })
        {
            var slots = new List<Rect>(free);
            allFit = true;
            for (int i = 0; i < groups.Count; i++)
            {
                Rect wanted = Bounds(groups[i]);
                wanted.Size = new Size(Math.Min(wanted.Width, max.Width), Math.Min(wanted.Height, max.Height));
                allFit &= Place(area, slots, wanted, !arrangeGroups, out result[i]);
            }
            if (allFit) break;
        }
        for (int i = 0; i < groups.Count; i++)
        {
            var f = groups[i];
            f.X = result[i].X; f.Y = result[i].Y; f.Width = result[i].Width;
            if (!f.Collapsed) f.Height = result[i].Height;
        }
        return fits && allFit;
    }

    // A new window takes an unoccupied spot before any existing window needs to move.
    internal static Rect ForNew(DesktopSettings s, Rect area, Size size)
    {
        if (area.Width > 2 * Gap && area.Height > 2 * Gap) area.Inflate(-Gap, -Gap);
        var free = new List<Rect> { area };
        if (s.Widgets.Enabled) foreach (var w in s.Widgets.Items) Occupy(free, Bounds(w));
        if (s.Fences.Enabled) foreach (var f in s.Fences.Fences) Occupy(free, Bounds(f));
        Place(area, free, new Rect(new Point(area.Right - size.Width, area.Top), size), false, out var bounds);
        return bounds;
    }

    private static bool Place(Rect area, List<Rect> free, Rect wanted, bool preferSaved, out Rect placed)
    {
        var size = new Size(Math.Min(area.Width, wanted.Width), Math.Min(area.Height, wanted.Height));
        placed = new Rect(Math.Clamp(wanted.X, area.Left, area.Right - size.Width),
                          Math.Clamp(wanted.Y, area.Top, area.Bottom - size.Height), size.Width, size.Height);
        var preferred = placed;
        bool fits = preferSaved && free.Any(r => r.Contains(preferred));
        if (!fits)
        {
            var slot = free.Where(r => r.Width >= size.Width && r.Height >= size.Height)
                .OrderByDescending(r => r.Right).ThenBy(r => r.Top).FirstOrDefault(Rect.Empty);
            fits = !slot.IsEmpty;
            if (fits) placed = new Rect(new Point(slot.Right - size.Width, slot.Top), size);
        }
        Occupy(free, placed);
        return fits;
    }

    private static void Occupy(List<Rect> free, Rect used)
    {
        used.Inflate(Gap, Gap);
        var next = new List<Rect>();
        foreach (var r in free)
        {
            var cut = Rect.Intersect(r, used);
            if (cut.IsEmpty || cut.Width == 0 || cut.Height == 0) { next.Add(r); continue; }
            if (cut.Left > r.Left) next.Add(new Rect(r.Left, r.Top, cut.Left - r.Left, r.Height));
            if (cut.Right < r.Right) next.Add(new Rect(cut.Right, r.Top, r.Right - cut.Right, r.Height));
            if (cut.Top > r.Top) next.Add(new Rect(r.Left, r.Top, r.Width, cut.Top - r.Top));
            if (cut.Bottom < r.Bottom) next.Add(new Rect(r.Left, cut.Bottom, r.Width, r.Bottom - cut.Bottom));
        }
        free.Clear();
        foreach (var candidate in next.Distinct())
            if (!next.Any(r => r != candidate && r.Contains(candidate))) free.Add(candidate);
    }
}
