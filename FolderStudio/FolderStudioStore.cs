using System.Text.Json;
using Shamsa.Services;

namespace Shamsa.FolderStudio;

public sealed class AppliedIconRecord
{
    public string Folder { get; set; } = string.Empty;
    public IconDesign Design { get; set; } = new();
    public DateTime AppliedUtc { get; set; }
}

public sealed class FolderStudioData
{
    public int Version { get; set; } = 1;
    public List<IconDesign> Favorites { get; set; } = new();
    public List<AppliedIconRecord> History { get; set; } = new();
    public IconDesign LastDesign { get; set; } = new();
}

/// <summary>Persists favourites, history and the last design in %LocalAppData%\Shamsa\folder-studio.json.</summary>
public static class FolderStudioStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppPaths.DataDir, "folder-studio.json");
    private static FolderStudioData? _data;

    public static FolderStudioData Data
    {
        get
        {
            lock (Gate)
            {
                if (_data != null) return _data;
                try
                {
                    if (File.Exists(FilePath))
                        _data = JsonSerializer.Deserialize<FolderStudioData>(File.ReadAllText(FilePath), Json);
                }
                catch (Exception ex) { AppLog.Warn("folder-studio.json unreadable, starting fresh: " + ex.Message); }
                _data ??= new FolderStudioData();
                _data.Favorites ??= new();
                _data.History ??= new();
                _data.LastDesign ??= new();
                return _data;
            }
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { AppLog.Warn("Saving folder-studio.json failed: " + ex.Message); }
        }
    }

    public static bool IsFavorite(IconDesign d) => Data.Favorites.Any(f => f.Signature == d.Signature);

    public static void ToggleFavorite(IconDesign d)
    {
        string sig = d.Signature;
        int removed = Data.Favorites.RemoveAll(f => f.Signature == sig);
        if (removed == 0) Data.Favorites.Insert(0, d.Clone());
        if (Data.Favorites.Count > 200) Data.Favorites.RemoveRange(200, Data.Favorites.Count - 200);
        Save();
    }

    public static void AddHistory(string folder, IconDesign d)
    {
        Data.History.Insert(0, new AppliedIconRecord { Folder = folder, Design = d.Clone(), AppliedUtc = DateTime.UtcNow });
        if (Data.History.Count > 150) Data.History.RemoveRange(150, Data.History.Count - 150);
        Save();
    }
}

/// <summary>Procedurally generated, fully local preset library (colours × themes). Not an online community library.</summary>
public static class FolderPresets
{
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("طلایی", "#F4B740"), ("نارنجی", "#F97316"), ("قرمز", "#EF4444"), ("صورتی", "#EC4899"), ("ارغوانی", "#A855F7"),
        ("نیلی", "#6366F1"), ("آبی", "#3B82F6"), ("آسمانی", "#0EA5E9"), ("فیروزه‌ای", "#14B8A6"), ("سبز", "#22C55E"),
        ("لیمویی", "#84CC16"), ("زرد", "#EAB308"), ("قهوه‌ای", "#A16207"), ("خاکستری", "#64748B"), ("زغالی", "#334155"),
        ("لاجوردی", "#1E3A8A"), ("زمردی", "#047857"), ("یاسی", "#C4B5FD")
    };

    public static readonly (string Name, string Glyph, string Keywords)[] Themes =
    {
        ("اسناد", "📄", "documents docs word اسناد سند"),
        ("موسیقی", "🎵", "music audio song موسیقی آهنگ"),
        ("تصاویر", "🖼️", "photos pictures images تصاویر عکس"),
        ("ویدیو", "🎬", "videos movies film ویدیو فیلم"),
        ("بازی", "🎮", "games gaming بازی"),
        ("کد", "💻", "code dev programming کد برنامه‌نویسی"),
        ("کار", "💼", "work office کار اداری"),
        ("دانلود", "⬇️", "downloads دانلود"),
        ("علاقه‌مندی", "⭐", "favorites star علاقه"),
        ("آرشیو", "📦", "archive backup آرشیو پشتیبان"),
        ("محرمانه", "🔒", "lock private secure قفل محرمانه"),
        ("مالی", "💰", "money finance invoice مالی پول"),
        ("آموزش", "📚", "study books school آموزش کتاب"),
        ("سفر", "✈️", "travel trip سفر"),
        ("خانواده", "❤️", "family love خانواده"),
        ("طراحی", "🎨", "design art طراحی هنر"),
        ("پروژه", "🚀", "project startup پروژه"),
        ("ابری", "☁️", "cloud sync ابری"),
    };

    public static readonly string[] Emojis =
    {
        "📄","📁","📂","🗂️","📚","📝","📊","📈","💼","🏢","🧾","💰",
        "🎵","🎧","🎸","🎹","🎬","🎥","📷","🖼️","🎨","✏️","🖌️","🎭",
        "💻","🖥️","⌨️","🧩","⚙️","🛠️","🔧","🔒","🔑","🛡️","☁️","⬇️",
        "🎮","🕹️","🏆","⚽","🏀","🚀","✈️","🚗","🏠","🌍","🌙","☀️",
        "⭐","❤️","🔥","⚡","🌸","🍀","🐱","🐶","🍕","☕","🎁","✅"
    };

    private static List<IconDesign>? _all;

    public static IReadOnlyList<IconDesign> All => _all ??= Build();

    private static List<IconDesign> Build()
    {
        var list = new List<IconDesign>();
        foreach (var (name, hex) in Palette)
            list.Add(new IconDesign { Name = $"رنگ {name}", BaseHex = hex, Keywords = "color plain رنگ ساده " + name });
        string[] themeColors = { "#F4B740", "#3B82F6", "#22C55E", "#EF4444", "#A855F7", "#14B8A6", "#F97316", "#334155", "#EC4899" };
        foreach (var (name, glyph, keywords) in Themes)
            foreach (var hex in themeColors)
                list.Add(new IconDesign
                {
                    Name = name,
                    BaseHex = hex,
                    Overlay = IconOverlayKind.Emoji,
                    OverlayText = glyph,
                    OverlayHex = string.Empty, // automatic contrast
                    Keywords = keywords
                });
        return list;
    }

    public static bool Matches(IconDesign d, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();
        return d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || d.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase)
            || d.OverlayText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
