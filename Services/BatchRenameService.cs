using System.Globalization;
using System.Text.RegularExpressions;

namespace Shamsa.Services;

public enum RenameCase { None, Lower, Upper, Title }

public enum RenameStatus { Unchanged, Ok, Conflict, Invalid }

public sealed class RenameOptions
{
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public string Find { get; set; } = string.Empty;
    public string Replace { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool MatchCase { get; set; } = true;
    public RenameCase Case { get; set; } = RenameCase.None;
    public bool Numbering { get; set; }
    public int NumberStart { get; set; } = 1;
    public int NumberPadding { get; set; } = 3;
    public string NumberSeparator { get; set; } = "_";
    /// <summary>Optional filter, e.g. ".jpg;.png" or "jpg png". Empty = all files.</summary>
    public string ExtensionFilter { get; set; } = string.Empty;
}

public record RenamePreviewItem(string OriginalPath, string OriginalName, string NewName, RenameStatus Status, string Note)
{
    public bool WillRename => Status == RenameStatus.Ok;

    public string StatusText => Status switch
    {
        RenameStatus.Ok => "✓ آماده",
        RenameStatus.Unchanged => "بدون تغییر",
        RenameStatus.Conflict => "⚠ " + Note,
        RenameStatus.Invalid => "✕ " + Note,
        _ => Note
    };
}

public record RenameUndoEntry(string CurrentPath, string OriginalPath);

public record RenameResult(int Renamed, int Failed, IReadOnlyList<RenameUndoEntry> Undo, IReadOnlyList<string> Errors);

public class BatchRenameService
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public List<RenamePreviewItem> BuildPreview(string folderPath, RenameOptions options)
    {
        var result = new List<RenamePreviewItem>();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return result;

        var filter = ParseExtensionFilter(options.ExtensionFilter);
        List<string> allFiles;
        try
        {
            allFiles = Directory.GetFiles(folderPath).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Rename preview could not list {folderPath}: {ex.Message}");
            return result;
        }

        // Hidden/system files (desktop.ini, thumbs.db) are never renamed — renaming desktop.ini breaks the folder.
        var files = allFiles
            .Where(f => !FileKinds.IsHiddenOrSystem(f))
            .Where(f => filter.Count == 0 || filter.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
            .ToList();

        Regex? regex = null;
        if (options.UseRegex && !string.IsNullOrEmpty(options.Find))
        {
            try
            {
                regex = new Regex(options.Find, options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                return files.Select(f => new RenamePreviewItem(f, Path.GetFileName(f), Path.GetFileName(f), RenameStatus.Invalid, "عبارت Regex نامعتبر: " + ex.Message)).ToList();
            }
        }

        int number = options.NumberStart;
        int padding = Math.Clamp(options.NumberPadding, 1, 10);
        var proposed = new List<(string Path, string OldName, string NewName)>();

        foreach (var file in files)
        {
            string oldName = Path.GetFileName(file);
            string baseName = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);

            string newBase = baseName;
            if (!string.IsNullOrEmpty(options.Find))
            {
                try
                {
                    newBase = regex != null
                        ? regex.Replace(newBase, options.Replace ?? string.Empty)
                        : newBase.Replace(options.Find, options.Replace ?? string.Empty,
                            options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException) { }
            }

            newBase = options.Case switch
            {
                RenameCase.Lower => newBase.ToLowerInvariant(),
                RenameCase.Upper => newBase.ToUpperInvariant(),
                RenameCase.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(newBase.ToLowerInvariant()),
                _ => newBase
            };

            string numberPart = options.Numbering
                ? (options.NumberSeparator ?? string.Empty) + number.ToString(new string('0', padding), CultureInfo.InvariantCulture)
                : string.Empty;

            proposed.Add((file, oldName, $"{options.Prefix}{newBase}{options.Suffix}{numberPart}{ext}"));
            number++;
        }

        // Names currently on disk that are NOT being renamed (hidden files, filtered-out files) are obstacles.
        var renamingSet = new HashSet<string>(proposed.Where(p => p.NewName != p.OldName).Select(p => p.OldName), StringComparer.OrdinalIgnoreCase);
        var blockedNames = new HashSet<string>(allFiles.Select(Path.GetFileName).Where(n => n != null && !renamingSet.Contains(n))!, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.GetDirectories(folderPath)) blockedNames.Add(Path.GetFileName(d));
        }
        catch { }

        var targetCounts = proposed
            .GroupBy(p => p.NewName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var (path, oldName, newName) in proposed)
        {
            if (newName == oldName)
            {
                result.Add(new RenamePreviewItem(path, oldName, newName, RenameStatus.Unchanged, string.Empty));
                continue;
            }

            string? invalid = ValidateName(newName);
            if (invalid != null)
            {
                result.Add(new RenamePreviewItem(path, oldName, newName, RenameStatus.Invalid, invalid));
                continue;
            }

            bool caseOnlyChange = newName.Equals(oldName, StringComparison.OrdinalIgnoreCase);
            if (targetCounts[newName] > 1)
                result.Add(new RenamePreviewItem(path, oldName, newName, RenameStatus.Conflict, "نام تکراری در لیست"));
            else if (!caseOnlyChange && blockedNames.Contains(newName))
                result.Add(new RenamePreviewItem(path, oldName, newName, RenameStatus.Conflict, "فایلی با این نام وجود دارد"));
            else
                result.Add(new RenamePreviewItem(path, oldName, newName, RenameStatus.Ok, string.Empty));
        }

        return result;
    }

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "نام خالی";
        if (name.IndexOfAny(InvalidChars) >= 0) return "کاراکتر غیرمجاز";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "پایان با فاصله/نقطه";
        if (name.Length > 255) return "نام بیش از حد طولانی";
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem)) return "نام خالی";
        if (ReservedNames.Contains(stem)) return "نام رزرو‌شده ویندوز";
        return null;
    }

    private static HashSet<string> ParseExtensionFilter(string filter)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filter)) return set;
        foreach (var part in filter.Split(new[] { ';', ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string ext = part.Trim().TrimStart('*');
            if (ext.Length == 0) continue;
            set.Add(ext.StartsWith('.') ? ext : "." + ext);
        }
        return set;
    }

    /// <summary>
    /// Two-phase rename (original → temp → final) so chains (a→b, b→c), swaps and
    /// case-only changes (file.txt → File.txt) all work. The old version silently skipped them.
    /// </summary>
    public RenameResult ApplyRename(IEnumerable<RenamePreviewItem> items)
    {
        var work = items.Where(i => i.WillRename).Select(i =>
        {
            string dir = Path.GetDirectoryName(i.OriginalPath)!;
            return (From: i.OriginalPath, To: Path.Combine(dir, i.NewName));
        }).ToList();
        return MoveTwoPhase(work);
    }

    public RenameResult Undo(IEnumerable<RenameUndoEntry> entries) =>
        MoveTwoPhase(entries.Select(e => (From: e.CurrentPath, To: e.OriginalPath)).ToList());

    private static RenameResult MoveTwoPhase(List<(string From, string To)> work)
    {
        var errors = new List<string>();
        var staged = new List<(string Temp, string From, string To)>();

        foreach (var (from, to) in work)
        {
            if (!File.Exists(from))
            {
                errors.Add($"{Path.GetFileName(from)}: فایل پیدا نشد");
                continue;
            }
            string temp = Path.Combine(Path.GetDirectoryName(from)!, $".qf_tmp_{Guid.NewGuid():N}{Path.GetExtension(from)}");
            try
            {
                File.Move(from, temp);
                staged.Add((temp, from, to));
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(from)}: {ex.Message}");
            }
        }

        var undo = new List<RenameUndoEntry>();
        int renamed = 0;
        foreach (var (temp, from, to) in staged)
        {
            try
            {
                if (File.Exists(to)) throw new IOException("مقصد از قبل وجود دارد");
                File.Move(temp, to);
                undo.Add(new RenameUndoEntry(to, from));
                renamed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(from)}: {ex.Message}");
                try { File.Move(temp, from); } catch { AppLog.Error($"Rename rollback failed: {temp} -> {from}"); }
            }
        }

        return new RenameResult(renamed, work.Count - renamed, undo, errors);
    }
}
