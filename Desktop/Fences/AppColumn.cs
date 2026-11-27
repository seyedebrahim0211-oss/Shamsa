using System.Windows.Media;
using Shamsa.Desktop.Dock;

namespace Shamsa.Desktop.Fences;

public sealed class AppColumn
{
    public List<string> Order { get; set; } = new();
    public List<string> Folders { get; set; } = new();
    public Dictionary<string, string> Locations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    internal void Normalize(Dictionary<string, string> members)
    {
        var paths = members.Keys.ToHashSet(Paths);
        Order = (Order ?? new()).Where(p => p != null && paths.Contains(p)).Concat(members.Keys).Distinct(Paths).ToList();
        Folders = (Folders ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(Paths).ToList();
        var live = new Dictionary<string, string>(Paths);
        if (Locations != null)
            foreach (var (path, folder) in Locations)
                if (paths.Contains(path) && Folders.Contains(folder, Paths)) live[path] = Folders.First(f => Paths.Equals(f, folder));
        Locations = live;
    }

    internal string FolderOf(string path) => Locations.TryGetValue(path, out var name) ? name : "";

    internal void Move(string path, string folder)
    {
        if (folder.Length == 0) Locations.Remove(path);
        else if (Folders.Contains(folder)) Locations[path] = folder;
    }

    internal bool Step(string path, int offset)
    {
        var siblings = Order.Where(p => FolderOf(p) == FolderOf(path)).ToList();
        int at = siblings.FindIndex(p => Paths.Equals(p, path));
        int target = at + offset;
        if (at < 0 || target < 0 || target >= siblings.Count) return false;
        int i = Order.IndexOf(siblings[at]), j = Order.IndexOf(siblings[target]);
        (Order[i], Order[j]) = (Order[j], Order[i]);
        return true;
    }

    internal bool SameAs(AppColumn? other) => other != null && Order.SequenceEqual(other.Order, Paths) &&
        Folders.SequenceEqual(other.Folders) && Locations.Count == other.Locations.Count &&
        Locations.All(k => other.Locations.TryGetValue(k.Key, out var v) && v == k.Value);
}

public sealed record AppIconChoice(string Style = "inherit", string? ColorHex = null);

public sealed class AppIconSettings
{
    public bool MatchWallpaper { get; set; } = true;
    public string? WallpaperPresetId { get; set; }
    public string? WallpaperAccentHex { get; set; }
    public AppIconChoice Default { get; set; } = new("theme");
    public Dictionary<string, AppIconChoice> PerApp { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Normalize()
    {
        Default ??= new("theme");
        var fresh = new Dictionary<string, AppIconChoice>(StringComparer.OrdinalIgnoreCase);
        if (PerApp != null)
            foreach (var (key, choice) in PerApp)
                if (!string.IsNullOrWhiteSpace(key) && choice != null) fresh[key] = choice;
        PerApp = fresh;
    }

    internal bool SameAs(AppIconSettings other) => MatchWallpaper == other.MatchWallpaper && WallpaperPresetId == other.WallpaperPresetId &&
        WallpaperAccentHex == other.WallpaperAccentHex && Default == other.Default && PerApp.Count == other.PerApp.Count &&
        PerApp.All(p => other.PerApp.TryGetValue(p.Key, out var v) && v == p.Value);
}

internal static class AppIconArt
{
    internal static readonly (string Id, string Label)[] Models = {
        ("lens", "عدسی شیشه‌ای"), ("enamel", "مینای برجسته"), ("prismatic", "منشور نئونی"),
        ("crystal", "بلور تراش‌خورده"), ("girih", "گره‌چینی سنتی"), ("outline", "خطی مینیمال"), ("plain", "نشان اصلی")
    };

    internal static bool IsApp(string path) => path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".exe" or ".url" or ".appref-ms";

    // A Start-menu shortcut and the corresponding running task share the same preference.
    internal static string Key(string target) => ShortcutResolver.ExecutableFor(target) ?? target;

    internal static (string Style, Color Color) Resolve(DesktopSettings s, string? target)
    {
        AppIconChoice? custom = null;
        if (target != null && s.AppIcons.PerApp.Count > 0)
            s.AppIcons.PerApp.TryGetValue(Key(target), out custom);
        string style = custom?.Style is null or "inherit" ? s.AppIcons.Default.Style : custom.Style;
        if (!Models.Any(m => m.Id == style))
            style = ((s.AppIcons.MatchWallpaper ? s.AppIcons.WallpaperPresetId : null) ?? s.ThemePresetId) switch
            {
                "mostofi" or "boshrouyeh" or "heritage" or "isfahan" or "shiraz" => "enamel",
                "arcade" or "mashhad" or "tazhib" => "girih",
                "game" or "neon" or "prism" or "city" or "boshrouyeh-night" => "prismatic",
                "graphite" or "field" => "outline",
                "boshrouyeh-fort" or "atelier" => "crystal",
                _ => "lens"
            };
        var color = DesktopTheme.ParseColor(custom?.ColorHex ?? s.AppIcons.Default.ColorHex, DesktopAccent(s));
        return (style, color);
    }

    internal static Color DesktopAccent(DesktopSettings s) =>
        DesktopTheme.ParseColor(s.AppIcons.MatchWallpaper ? s.AppIcons.WallpaperAccentHex : null, DesktopTheme.Accent(s));
}
