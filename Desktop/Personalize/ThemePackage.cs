using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

/// <summary>Contents of theme.json inside a .negar package.</summary>
public sealed class ThemeManifest
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "تم نگارخانه";
    public string? Wallpaper { get; set; }              // entry name inside the zip
    public string WallpaperStyle { get; set; } = "Fill";
    public string? SolidColorHex { get; set; }
    public string? AccentHex { get; set; }
    public bool? Transparency { get; set; }
    public bool? AccentOnTaskbar { get; set; }
    public bool? AccentOnTitleBars { get; set; }
    public string? CursorScheme { get; set; }
    public int? CursorSize { get; set; }
    public Dictionary<string, string> Icons { get; set; } = new();     // system icon id → entry name (.ico)
    public string? RecycleFullIcon { get; set; }
    public bool HasDesktopSettings { get; set; }
}

/// <summary>Theme export/import as a .negar zip (theme.json, wallpaper, icons, desktop.json).</summary>
public static class ThemePackage
{
    public const string Extension = ".negar";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string ThemesDir => Path.Combine(AppPaths.DataDir, "themes");

    /// <summary>Builds a package from the current state. <paramref name="desktopJsonPath"/> null = current desktop.json.</summary>
    public static void Export(string target, string name, string? desktopJsonPath = null)
    {
        var a = PersonalizeStore.Data.Applied;
        var m = new ThemeManifest
        {
            Name = name,
            WallpaperStyle = a.WallpaperStyle,
            SolidColorHex = a.SolidColorHex,
            AccentHex = a.AccentHex ?? Hex(AccentService.CurrentAccent()),
            Transparency = a.Transparency ?? AccentService.TransparencyEnabled(),
            AccentOnTaskbar = a.AccentOnTaskbar,
            AccentOnTitleBars = a.AccentOnTitleBars,
            CursorScheme = a.CursorScheme,
            CursorSize = a.CursorSize,
        };
        string tmp = target + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                string wp = a.WallpaperPath ?? WallpaperService.CurrentWallpaper();
                if (a.SolidColorHex == null && !string.IsNullOrWhiteSpace(wp) && File.Exists(wp))
                {
                    m.Wallpaper = "wallpaper" + Path.GetExtension(wp).ToLowerInvariant();
                    zip.CreateEntryFromFile(wp, m.Wallpaper, CompressionLevel.Optimal);
                }
                foreach (var (id, loc) in a.IconFiles)
                {
                    var (file, _) = SystemIcons.ParseLocation(loc);
                    if (!File.Exists(file) || !file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;
                    string entry = $"icons/{id}.ico";
                    zip.CreateEntryFromFile(file, entry);
                    m.Icons[id] = entry;
                    if (id == "recycle")
                    {
                        string? full = SystemIcons.CurrentIconLocation(SystemIcons.Get("recycle"), full: true);
                        if (full != null)
                        {
                            var (ff, _) = SystemIcons.ParseLocation(full);
                            if (File.Exists(ff) && ff.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && !ff.Equals(file, StringComparison.OrdinalIgnoreCase))
                            {
                                zip.CreateEntryFromFile(ff, "icons/recycle-full.ico");
                                m.RecycleFullIcon = "icons/recycle-full.ico";
                            }
                        }
                    }
                }
                string dj = desktopJsonPath ?? DesktopSettingsStore.FilePath;
                if (File.Exists(dj)) { zip.CreateEntryFromFile(dj, "desktop.json"); m.HasDesktopSettings = true; }
                var e = zip.CreateEntry("theme.json");
                using var w = new StreamWriter(e.Open());
                w.Write(JsonSerializer.Serialize(m, Json));
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    /// <summary>Safely extracts a package into %LocalAppData%\Shamsa\themes\&lt;name&gt; and reads its manifest.</summary>
    public static (ThemeManifest Manifest, string Folder) Open(string package, string? themesRoot = null)
    {
        string root = themesRoot ?? ThemesDir;
        string folder = Path.Combine(root, Path.GetFileNameWithoutExtension(package) + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(folder);
        string full = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(package))
        {
            if (zip.Entries.Count > 200) throw new InvalidDataException("بسته تم بیش از حد فایل دارد.");
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                if (e.Length > 64L * 1024 * 1024) throw new InvalidDataException("فایلی در بسته بیش از حد بزرگ است.");
                string dest = Path.GetFullPath(Path.Combine(folder, e.FullName));
                if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("مسیر نامعتبر در بسته تم.");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
        }
        string mf = Path.Combine(folder, "theme.json");
        if (!File.Exists(mf)) throw new InvalidDataException("این فایل بسته تم نگارخانه نیست (theme.json ندارد).");
        var m = JsonSerializer.Deserialize<ThemeManifest>(File.ReadAllText(mf), Json) ?? throw new InvalidDataException("theme.json نامعتبر است.");
        m.Icons ??= new();
        return (m, folder);
    }

    /// <summary>Applies an opened package. Each part is applied independently; returns the list of errors (empty = success).</summary>
    public static List<string> Apply(ThemeManifest m, string folder, bool includeDesktopSettings, Action<DesktopSettings>? applyDesktop = null)
    {
        var errors = new List<string>();
        string? Entry(string? name) => name == null ? null : Path.GetFullPath(Path.Combine(folder, name));
        void Try(string what, Action act) { try { act(); } catch (Exception ex) { AppLog.Warn($"Theme import {what}: {ex.Message}"); errors.Add(what + ": " + ex.Message); } }

        if (Entry(m.Wallpaper) is { } wp && File.Exists(wp))
        {
            // keep a stable copy outside the extraction folder
            Directory.CreateDirectory(PersonalizeStore.WallpapersDir);
            string dst = Path.Combine(PersonalizeStore.WallpapersDir, "theme-" + Path.GetFileName(folder) + Path.GetExtension(wp));
            Try("تصویر زمینه", () => { File.Copy(wp, dst, true); WallpaperService.SetWallpaper(dst, Enum.TryParse<WallpaperFit>(m.WallpaperStyle, out var f) ? f : WallpaperFit.Fill); });
        }
        else if (m.SolidColorHex != null)
            Try("رنگ زمینه", () => WallpaperService.SetSolidColor(DesktopTheme.ParseColor(m.SolidColorHex, Colors.Black)));

        if (m.AccentHex != null) Try("رنگ تأکیدی", () => AccentService.SetAccent(DesktopTheme.ParseColor(m.AccentHex, Colors.Teal)));
        if (m.Transparency is bool tr) Try("شفافیت", () => AccentService.SetTransparency(tr));
        if (m.AccentOnTaskbar is bool tb) Try("رنگ نوار وظیفه", () => AccentService.SetAccentOnTaskbar(tb));
        if (m.AccentOnTitleBars is bool tt) Try("رنگ نوار عنوان", () => AccentService.SetAccentOnTitleBars(tt));

        if (m.Icons.Count > 0)
        {
            string iconDir = Path.Combine(PersonalizeStore.IconsDir, "theme-" + Path.GetFileName(folder));
            Directory.CreateDirectory(iconDir);
            foreach (var (id, entry) in m.Icons)
            {
                var def = SystemIcons.All.FirstOrDefault(d => d.Id == id);
                if (def == null || Entry(entry) is not { } src || !File.Exists(src)) continue;
                Try("آیکن " + def.Name, () =>
                {
                    string dst = Path.Combine(iconDir, id + ".ico");
                    File.Copy(src, dst, true);
                    string? full = null;
                    if (def.IsRecycleBin && Entry(m.RecycleFullIcon) is { } fs && File.Exists(fs))
                    {
                        full = Path.Combine(iconDir, "recycle-full.ico");
                        File.Copy(fs, full, true);
                    }
                    SystemIcons.SetIcon(def, dst + ",0", full != null ? full + ",0" : null);
                });
            }
        }

        if (m.CursorScheme != null)
        {
            var scheme = CursorService.Schemes().FirstOrDefault(s => s.Name.Equals(m.CursorScheme, StringComparison.OrdinalIgnoreCase));
            if (scheme != null) Try("نشانگر ماوس", () => CursorService.ApplyScheme(scheme));
            else errors.Add($"طرح نشانگر «{m.CursorScheme}» روی این رایانه نصب نیست.");
        }
        if (m.CursorSize is int cs) Try("اندازه نشانگر", () => CursorService.SetSize(cs));

        if (includeDesktopSettings && m.HasDesktopSettings && Entry("desktop.json") is { } dj && File.Exists(dj))
            Try("تنظیمات دسکتاپ نگارخانه", () =>
            {
                var s = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(dj), DesktopSettingsStore.Json)
                        ?? throw new InvalidDataException("desktop.json نامعتبر است.");
                if (applyDesktop != null) applyDesktop(s); else DesktopSettingsStore.Save(s);
            });
        return errors;
    }

    private static string? Hex(Color? c) => c is { } v ? $"#{v.R:X2}{v.G:X2}{v.B:X2}" : null;
}
