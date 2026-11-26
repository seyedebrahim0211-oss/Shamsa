using Shamsa.FolderStudio;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>
/// Suggests folder icons from the folder's name (Persian and English keywords) and from a picture inside it.
/// Pure reads: nothing is applied until the caller uses FolderIconRenderer.ApplyImageToFolder / FolderIconService.
/// </summary>
public static class FolderIconSuggester
{
    /// <summary>Keyword → (preset theme name in <see cref="FolderPresets.Themes"/>, base colour). No star motifs.</summary>
    private static readonly (string[] Words, string Theme, string Hex)[] Rules =
    {
        (new[] { "پروژه", "project", "projects", "startup" }, "پروژه", "#6366F1"),
        (new[] { "کار", "اداری", "اداره", "work", "office", "job", "شرکت", "company" }, "کار", "#334155"),
        (new[] { "عکس", "تصویر", "تصاویر", "photo", "photos", "pictures", "picture", "images", "image", "camera", "dcim", "گالری", "gallery" }, "تصاویر", "#0EA5E9"),
        (new[] { "موسیقی", "آهنگ", "music", "songs", "song", "audio", "mp3", "podcast", "پادکست" }, "موسیقی", "#EC4899"),
        (new[] { "فیلم", "ویدیو", "ویدئو", "سریال", "movie", "movies", "video", "videos", "film", "series" }, "ویدیو", "#EF4444"),
        (new[] { "دانشگاه", "درس", "مدرسه", "کلاس", "آموزش", "کتاب", "جزوه", "university", "uni", "school", "study", "course", "books", "book", "ebook" }, "آموزش", "#22C55E"),
        (new[] { "بازی", "game", "games", "gaming", "steam" }, "بازی", "#A855F7"),
        (new[] { "دانلود", "download", "downloads" }, "دانلود", "#14B8A6"),
        (new[] { "اسناد", "سند", "مدارک", "document", "documents", "docs", "pdf", "نامه" }, "اسناد", "#3B82F6"),
        (new[] { "مالی", "حساب", "فاکتور", "قبض", "بانک", "پول", "finance", "invoice", "invoices", "bank", "money", "tax", "مالیات" }, "مالی", "#047857"),
        (new[] { "کد", "برنامه نویسی", "برنامه‌نویسی", "code", "dev", "src", "source", "github", "repo", "programming" }, "کد", "#1E3A8A"),
        (new[] { "طراحی", "هنر", "گرافیک", "design", "art", "graphic", "graphics", "photoshop", "figma" }, "طراحی", "#F97316"),
        (new[] { "سفر", "travel", "trip", "vacation", "مسافرت" }, "سفر", "#0EA5E9"),
        (new[] { "خانواده", "family", "kids", "بچه‌ها", "عروسی", "wedding" }, "خانواده", "#EC4899"),
        (new[] { "آرشیو", "بایگانی", "پشتیبان", "بکاپ", "archive", "backup", "backups", "old" }, "آرشیو", "#64748B"),
        (new[] { "محرمانه", "شخصی", "خصوصی", "private", "secret", "personal" }, "محرمانه", "#334155"),
        (new[] { "ابری", "cloud", "onedrive", "dropbox", "drive" }, "ابری", "#3B82F6"),
    };

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic", ".heif", ".gif" };

    /// <summary>
    /// Up to <paramref name="max"/> designs, best first: picture from inside the folder (when there is one),
    /// then keyword matches in a few colours. Falls back to a neutral colour set when nothing matches.
    /// </summary>
    public static List<IconDesign> Suggest(string folder, int max = 8)
    {
        var list = new List<IconDesign>();
        if (string.IsNullOrWhiteSpace(folder)) return list;
        string name = Normalize(Path.GetFileName(folder.TrimEnd('\\', '/')));

        string? picture = PictureInside(folder);
        if (picture != null)
            list.Add(new IconDesign
            {
                Name = "عکسی از داخل پوشه",
                Overlay = IconOverlayKind.Image,
                ImagePath = picture,
                ImageMode = IconImageMode.Full,
                RoundedImage = true,
                Keywords = "picture photo عکس"
            });

        foreach (var rule in MatchRules(name))
        {
            var theme = FolderPresets.Themes.FirstOrDefault(t => t.Name == rule.Theme);
            if (theme.Name == null) continue;
            foreach (var hex in new[] { rule.Hex, "#F4B740", "#14B8A6" })
            {
                if (list.Any(d => d.OverlayText == theme.Glyph && d.BaseHex == hex)) continue;
                list.Add(new IconDesign
                {
                    Name = theme.Name,
                    BaseHex = hex,
                    Overlay = IconOverlayKind.Emoji,
                    OverlayText = theme.Glyph,
                    OverlayHex = string.Empty,
                    Keywords = theme.Keywords
                });
            }
        }

        if (list.Count(d => d.Overlay != IconOverlayKind.Image) == 0)
        {
            // No keyword: the first letter of the name on calm colours.
            string letter = FirstLetter(Path.GetFileName(folder.TrimEnd('\\', '/')));
            foreach (var hex in new[] { "#14B8A6", "#3B82F6", "#F4B740", "#A855F7" })
                list.Add(new IconDesign
                {
                    Name = letter.Length > 0 ? "حرف اول نام" : "رنگ ساده",
                    BaseHex = hex,
                    Overlay = letter.Length > 0 ? IconOverlayKind.Text : IconOverlayKind.None,
                    OverlayText = letter,
                    OverlayHex = "#FFFFFF"
                });
        }
        return list.Take(max).ToList();
    }

    /// <summary>The theme names a folder name matches, best (longest keyword) first. Public for tests.</summary>
    public static List<string> MatchedThemes(string folderName) => MatchRules(Normalize(folderName)).Select(r => r.Theme).ToList();

    private static IEnumerable<(string[] Words, string Theme, string Hex)> MatchRules(string normalizedName)
    {
        if (normalizedName.Length == 0) return Enumerable.Empty<(string[], string, string)>();
        var tokens = normalizedName.Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        return Rules
            .Select(r => (Rule: r, Score: r.Words.Select(Normalize).Where(w => Hit(normalizedName, tokens, w)).Select(w => w.Length).DefaultIfEmpty(0).Max()))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Rule);
    }

    private static bool Hit(string name, string[] tokens, string word)
    {
        if (word.Length == 0) return false;
        // Persian words match inside the name («عکس‌های سفر»); short Latin words must be whole tokens («doc» ≠ «doctor»).
        bool latin = word.All(c => c < 0x0250);
        if (!latin) return name.Contains(word, StringComparison.Ordinal);
        if (word.Contains(' ')) return name.Contains(word, StringComparison.Ordinal);
        return tokens.Any(t => t == word || (word.Length >= 5 && t.StartsWith(word, StringComparison.Ordinal)));
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace('ي', 'ی').Replace('ك', 'ک').Replace('‌', ' ').Replace('ۀ', 'ه').Replace('ة', 'ه');
        return string.Join(' ', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FirstLetter(string name)
    {
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "";
    }

    /// <summary>The most recent picture among the folder's top-level files (≥ 20 KB, so icons/thumbs are skipped). Reads only file metadata.</summary>
    public static string? PictureInside(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder)
                .Take(2000)
                .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase) && !FileKinds.IsHiddenOrSystem(p))
                .Select(p => { try { var fi = new FileInfo(p); return (Path: p, fi.Length, fi.LastWriteTimeUtc); } catch { return (Path: p, Length: 0L, LastWriteTimeUtc: DateTime.MinValue); } })
                .Where(x => x.Length >= 20 * 1024)
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ThenByDescending(x => x.Length)
                .Select(x => x.Path)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
