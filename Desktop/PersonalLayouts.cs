using System.Windows;

namespace Shamsa.Desktop;

public sealed record LayoutViewport(double X, double Y, double Width, double Height)
{
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

public sealed record DesktopItemBounds(string Id, double X, double Y, double Width, double Height, bool Collapsed = false)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Id) && double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

// Layouts describe where existing items belong. File membership, note text and appearance belong to the live items.
public sealed class DesktopLayoutSnapshot
{
    public LayoutViewport? Area { get; set; }
    public List<DesktopItemBounds> Groups { get; set; } = new();
    public List<DesktopItemBounds> Widgets { get; set; } = new();
    public bool GroupsEnabled { get; set; }
    public bool WidgetsEnabled { get; set; }

    internal bool Normalize()
    {
        Groups = Clean(Groups);
        Widgets = Clean(Widgets);
        return Area?.IsValid == true && Groups.Count + Widgets.Count > 0;
    }

    private static List<DesktopItemBounds> Clean(List<DesktopItemBounds>? items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items?.Where(x => x?.IsValid == true && seen.Add(x.Id)).ToList() ?? new();
    }
}

public sealed class PersonalDesktopLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DesktopLayoutSnapshot Snapshot { get; set; } = new();
}

public sealed class PersonalLayoutSettings
{
    public List<PersonalDesktopLayout> Saved { get; set; } = new();
    public Dictionary<string, string> Displays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DesktopLayoutSnapshot? BeforeSwitch { get; set; }
    public string? LastAppliedId { get; set; }
    public string? BeforeSwitchId { get; set; }

    internal void Normalize()
    {
        Saved ??= new();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Saved.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) ||
            p.Snapshot?.Normalize() != true || !ids.Add(p.Id));
        var displays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Displays != null)
            foreach (var (device, id) in Displays)
                if (!string.IsNullOrWhiteSpace(device) && ids.Contains(id)) displays[device] = id;
        Displays = displays;
        if (BeforeSwitch?.Normalize() != true) BeforeSwitch = null;
        if (!ids.Contains(LastAppliedId ?? "")) LastAppliedId = null;
        if (BeforeSwitch == null || !ids.Contains(BeforeSwitchId ?? "")) BeforeSwitchId = null;
    }
}

internal readonly record struct LayoutResult(bool Save, string Message);

internal static class PersonalLayouts
{
    internal static DesktopLayoutSnapshot Capture(DesktopSettings s, Rect area)
    {
        if (s.Enabled && s.Fences.AvoidOverlap)
        {
            s = s.Clone();
            DesktopPlacement.Arrange(s, area);
        }
        var shot = new DesktopLayoutSnapshot
        {
            Area = new(area.X, area.Y, area.Width, area.Height),
            GroupsEnabled = s.Fences.Enabled, WidgetsEnabled = s.Widgets.Enabled
        };
        foreach (var f in s.Fences.Fences)
        {
            var r = DesktopPlacement.Bounds(f);
            shot.Groups.Add(new(f.Id, r.X, r.Y, r.Width, Math.Max(120, f.Height), f.Collapsed));
        }
        foreach (var w in s.Widgets.Items)
        {
            var r = DesktopPlacement.Bounds(w);
            shot.Widgets.Add(new(w.Id, r.X, r.Y, r.Width, r.Height));
        }
        return shot;
    }

    // Preserve the fraction of each axis the item can travel. A right-anchored item stays on the right
    // even when the target has a side dock or the item needs to shrink to fit a laptop display.
    private static double Position(double value, double sourceStart, double sourceTravel, double targetStart, double targetTravel)
    {
        if (sourceStart == targetStart && sourceTravel == targetTravel)
            return Math.Clamp(value, targetStart, targetStart + targetTravel);
        double fraction = sourceTravel > 0 ? Math.Clamp((value - sourceStart) / sourceTravel, 0, 1) : 0;
        return targetStart + fraction * targetTravel;
    }

    internal static Rect Project(DesktopItemBounds p, LayoutViewport source, Rect target)
    {
        double width = Math.Min(p.Width, target.Width);
        double height = Math.Min(p.Height, target.Height);
        double visibleHeight = p.Collapsed ? DesktopPlacement.CollapsedHeight : p.Height;
        double x = Position(p.X, source.X, source.Width - p.Width, target.X, target.Width - width);
        double y = Position(p.Y, source.Y, source.Height - visibleHeight, target.Y, target.Height - Math.Min(visibleHeight, target.Height));
        return new(x, y, width, height);
    }

    internal static LayoutResult SaveCurrent(DesktopSettings s, Rect area, string name, string? replaceId = null)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60) return new(false, "نامی بین ۱ تا ۶۰ نویسه بنویس.");
        var saved = s.PersonalLayouts.Saved;
        var profile = saved.Find(p => p.Id == replaceId);
        if (replaceId != null && profile == null) return new(false, "این چیدمان دیگر در فهرست نیست.");
        static string Key(string text) => text.Trim().Replace('ي', 'ی').Replace('ك', 'ک').Replace('‌', ' ');
        if (saved.Any(p => p.Id != replaceId && string.Equals(Key(p.Name), Key(name), StringComparison.OrdinalIgnoreCase)))
            return new(false, "چیدمانی با این نام داری. نام دیگری انتخاب کن.");
        var shot = Capture(s, area);
        if (shot.Groups.Count + shot.Widgets.Count == 0) return new(false, "ابتدا یک گروه یا ویجت به دسکتاپ اضافه کن.");
        if (profile == null) { profile = new(); saved.Add(profile); }
        profile.Name = name;
        profile.Snapshot = shot;
        return new(true, $"چیدمان «{name}» ذخیره شد.");
    }

    internal static LayoutResult Remove(DesktopSettings s, string id)
    {
        if (s.PersonalLayouts.Saved.RemoveAll(p => p.Id == id) == 0) return new(false, "این چیدمان دیگر در فهرست نیست.");
        s.PersonalLayouts.Normalize();
        return new(true, "چیدمان از فهرست حذف شد.");
    }

    internal static LayoutResult BindDisplay(DesktopSettings s, string id, string device)
    {
        if (string.IsNullOrWhiteSpace(device) || !s.PersonalLayouts.Saved.Any(p => p.Id == id))
            return new(false, "نمایشگر یا چیدمان در دسترس نیست.");
        var links = s.PersonalLayouts.Displays;
        if (links.TryGetValue(device, out var existing) && existing == id)
        {
            links.Remove(device);
            return new(true, "انتخاب خودکار برای این نمایشگر خاموش شد.");
        }
        links[device] = id;
        return new(true, "چیدمان پیش‌فرض این نمایشگر ذخیره شد.");
    }

    internal static LayoutResult Switch(DesktopSettings s, Rect target, string? id, bool undo = false)
    {
        var catalog = s.PersonalLayouts;
        var profile = catalog.Saved.Find(p => p.Id == id);
        var shot = undo ? catalog.BeforeSwitch : profile?.Snapshot;
        if (shot?.Area is not { IsValid: true } source || target.IsEmpty || target.Width <= 0 || target.Height <= 0)
            return new(false, "چیدمانی برای بازگردانی در دسترس نیست.");

        var before = Capture(s, target);
        var groups = s.Fences.Fences.ToDictionary(f => f.Id);
        var widgets = s.Widgets.Items.ToDictionary(w => w.Id);
        int matchedGroups = 0, matchedWidgets = 0;
        foreach (var p in shot.Groups)
        {
            if (!groups.TryGetValue(p.Id, out var f)) continue;
            var r = Project(p, source, target);
            f.X = r.X; f.Y = r.Y; f.Width = r.Width; f.Height = r.Height; f.Collapsed = p.Collapsed;
            matchedGroups++;
        }
        foreach (var p in shot.Widgets)
        {
            if (!widgets.TryGetValue(p.Id, out var w)) continue;
            var r = Project(p, source, target);
            w.X = r.X; w.Y = r.Y; w.Width = r.Width; w.Height = r.Height;
            matchedWidgets++;
        }
        if (matchedGroups + matchedWidgets == 0) return new(false, "گروه‌ها و ویجت‌های این چیدمان دیگر روی دسکتاپ وجود ندارند.");
        if (matchedGroups > 0) s.Fences.Enabled = shot.GroupsEnabled;
        if (matchedWidgets > 0) s.Widgets.Enabled = shot.WidgetsEnabled;
        if (undo)
        {
            catalog.LastAppliedId = catalog.BeforeSwitchId;
            catalog.BeforeSwitch = null;
            catalog.BeforeSwitchId = null;
        }
        else
        {
            var after = Capture(s, target);
            if (!before.Groups.SequenceEqual(after.Groups) || !before.Widgets.SequenceEqual(after.Widgets) ||
                before.GroupsEnabled != after.GroupsEnabled || before.WidgetsEnabled != after.WidgetsEnabled)
            {
                catalog.BeforeSwitch = before;
                catalog.BeforeSwitchId = catalog.LastAppliedId;
            }
            catalog.LastAppliedId = profile!.Id;
        }
        string message = undo ? "چیدمان قبلی برگشت." : $"چیدمان «{profile!.Name}» اعمال شد.";
        int missing = shot.Groups.Count + shot.Widgets.Count - matchedGroups - matchedWidgets;
        if (missing > 0) message += " " + DesktopTheme.Digits(missing.ToString(), true) + " مورد دیگر روی دسکتاپ وجود ندارد.";
        if (!s.Enabled) message += " برای دیدن آن، محیط دسکتاپ را در «شروع سریع دسکتاپ» روشن کن.";
        return new(true, message);
    }
}

// Ignore ordinary settings notifications, including the one produced by restoring a layout.
internal sealed class LayoutDisplayTracker
{
    private (string Device, int Width, int Height)? _last;

    internal string? Enter(PersonalLayoutSettings layouts, string device, int width, int height, bool enabled)
    {
        if (!enabled) { _last = null; return null; }
        var current = (device.ToUpperInvariant(), width, height);
        if (_last == current) return null;
        _last = current;
        return layouts.Displays.TryGetValue(device, out string? id) ? id : null;
    }
}
