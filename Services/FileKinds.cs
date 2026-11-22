namespace Shamsa.Services;

public enum FileKind { Other, Video, Image, Audio, Document, Archive, Software, Code, Font, Ebook, Shortcut, Folder }

/// <summary>Single source of truth for file-type detection used by every feature.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        Register(FileKind.Video, ".mp4", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".m4v", ".flv", ".mpeg", ".mpg", ".3gp", ".ts", ".mts", ".m2ts", ".ogv", ".vob");
        Register(FileKind.Image, ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".heic", ".heif", ".ico", ".tif", ".tiff", ".avif", ".jfif", ".raw", ".cr2", ".nef", ".arw", ".dng", ".psd");
        Register(FileKind.Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".opus", ".wma", ".aiff", ".alac", ".amr", ".mid", ".midi");
        Register(FileKind.Document, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".rtf", ".odt", ".ods", ".odp", ".xps", ".one", ".pages", ".numbers", ".key");
        Register(FileKind.Archive, ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".cab", ".zst");
        Register(FileKind.Software, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd", ".iso", ".img", ".apk", ".dmg", ".reg");
        Register(FileKind.Code, ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".sql", ".sh", ".ps1", ".ipynb", ".kt", ".swift", ".dart", ".vue");
        Register(FileKind.Font, ".ttf", ".otf", ".woff", ".woff2", ".fon");
        Register(FileKind.Ebook, ".epub", ".mobi", ".azw", ".azw3", ".djvu", ".fb2", ".cbz", ".cbr");
        Register(FileKind.Shortcut, ".lnk", ".url");
    }

    private static void Register(FileKind kind, params string[] extensions)
    {
        foreach (var ext in extensions) Map[ext] = kind;
    }

    /// <summary>Extensions that mean "this download is still in progress" — never touch these.</summary>
    public static readonly HashSet<string> PartialDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".tmp", ".temp", ".opdownload", ".!ut", ".!qb", ".aria2", ".bc!", ".dlpart"
    };

    public static FileKind FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return FileKind.Other;
        return Map.TryGetValue(extension, out var kind) ? kind : FileKind.Other;
    }

    public static FileKind Of(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        return FromExtension(Path.GetExtension(path));
    }

    public static bool IsVideo(string path) => FromExtension(Path.GetExtension(path)) == FileKind.Video;
    public static bool IsImage(string path) => FromExtension(Path.GetExtension(path)) == FileKind.Image;

    /// <summary>Images WPF can decode natively (used for hover preview).</summary>
    public static bool IsPreviewableImage(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".ico" or ".webp" or ".heic" or ".avif";
    }

    public static IEnumerable<string> ExtensionsOf(FileKind kind) =>
        Map.Where(kv => kv.Value == kind).Select(kv => kv.Key);

    public static bool IsPartialDownload(string path) =>
        PartialDownloadExtensions.Contains(Path.GetExtension(path));

    /// <summary>True for hidden/system files such as desktop.ini or thumbs.db that must never be moved or renamed.</summary>
    public static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
        }
        catch
        {
            return true; // can't read attributes → treat as off-limits
        }

        string name = Path.GetFileName(path);
        return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~$", StringComparison.Ordinal); // Office lock files
    }

    /// <summary>Wraps Latin text (e.g. "Images (12)") in an LTR embedding so it renders intact inside Persian sentences.</summary>
    public static string Ltr(string text) => "‪" + text + "‬";

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        // LTR-embedded so "4.9 KB" doesn't render as "KB 4.9" inside Persian text
        return Ltr(unit == 0
            ? $"{bytes} B"
            : value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit]);
    }
}

/// <summary>Natural ("file2" before "file10") ordering, matching File Explorer.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        try { return NativeMethods.StrCmpLogicalW(x, y); }
        catch { return string.Compare(x, y, StringComparison.OrdinalIgnoreCase); }
    }
}
