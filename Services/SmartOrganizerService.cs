namespace Shamsa.Services;

public enum OrganizeMode { ByType, ByDate, ByTypeThenDate }

public record OrganizeMove(string From, string To, string Category)
{
    public string FileName => Path.GetFileName(From);
}

public record OrganizePlan(string Folder, IReadOnlyList<OrganizeMove> Moves, int Skipped)
{
    public IEnumerable<(string Category, int Count)> Summary =>
        Moves.GroupBy(m => m.Category).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()));
}

public record OrganizeResult(int Moved, int Failed, IReadOnlyList<OrganizeMove> Done, string Summary);

public class SmartOrganizerService
{
    public static readonly IReadOnlyDictionary<FileKind, string> CategoryFolders = new Dictionary<FileKind, string>
    {
        [FileKind.Image] = "Images",
        [FileKind.Video] = "Videos",
        [FileKind.Audio] = "Audio",
        [FileKind.Document] = "Documents",
        [FileKind.Archive] = "Archives",
        [FileKind.Software] = "Software",
        [FileKind.Code] = "Code",
        [FileKind.Font] = "Fonts",
        [FileKind.Ebook] = "Ebooks",
        [FileKind.Shortcut] = "Shortcuts",
        [FileKind.Other] = "Others"
    };

    /// <summary>
    /// Returns the category folder for a file, or null when it must be left alone
    /// (hidden/system files like desktop.ini, in-progress downloads, Office lock files).
    /// </summary>
    public static string? CategorizeFile(string path)
    {
        if (FileKinds.IsHiddenOrSystem(path) || FileKinds.IsPartialDownload(path)) return null;
        var kind = FileKinds.FromExtension(Path.GetExtension(path));
        return CategoryFolders.TryGetValue(kind, out var folder) ? folder : "Others";
    }

    public OrganizePlan BuildPlan(string folderPath, OrganizeMode mode = OrganizeMode.ByType)
    {
        var moves = new List<OrganizeMove>();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return new OrganizePlan(folderPath, moves, 0);

        string[] files;
        try
        {
            files = Directory.GetFiles(folderPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Organizer cannot list {folderPath}: {ex.Message}");
            return new OrganizePlan(folderPath, moves, 0);
        }

        int skipped = 0;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.OrderBy(f => f, NaturalStringComparer.Instance))
        {
            string? category = CategorizeFile(file);
            if (category == null)
            {
                skipped++;
                continue;
            }

            string relativeDir = mode switch
            {
                OrganizeMode.ByDate => DateFolder(file),
                OrganizeMode.ByTypeThenDate => Path.Combine(category, DateFolder(file)),
                _ => category
            };

            string targetDir = SafeTargetDir(folderPath, relativeDir);
            string dest = UniquePath(Path.Combine(targetDir, Path.GetFileName(file)), reserved);
            reserved.Add(dest);
            moves.Add(new OrganizeMove(file, dest, mode == OrganizeMode.ByDate ? relativeDir : category));
        }

        return new OrganizePlan(folderPath, moves, skipped);
    }

    public OrganizeResult Execute(OrganizePlan plan)
    {
        var done = new List<OrganizeMove>();
        int failed = 0;

        foreach (var move in plan.Moves)
        {
            try
            {
                if (!File.Exists(move.From)) { failed++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                string dest = File.Exists(move.To) ? UniquePath(move.To, null) : move.To;
                File.Move(move.From, dest);
                done.Add(move with { To = dest });
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn($"Organizer could not move {move.From}: {ex.Message}");
            }
        }

        string summary = done.Count == 0
            ? "فایلی برای مرتب‌سازی وجود نداشت."
            : $"{done.Count} فایل مرتب شد: " + string.Join("، ", done.GroupBy(m => m.Category).Select(g => FileKinds.Ltr($"{g.Key} ({g.Count()})")));
        if (failed > 0) summary += $" | {failed} فایل (در حال استفاده) جابه‌جا نشد";
        if (plan.Skipped > 0) summary += $" | {plan.Skipped} فایل سیستمی/در حال دانلود دست‌نخورده ماند";

        return new OrganizeResult(done.Count, failed, done, summary);
    }

    /// <summary>Backwards compatible one-shot API.</summary>
    public (int MovedCount, string Summary) OrganizeFolder(string folderPath, OrganizeMode mode = OrganizeMode.ByType)
    {
        if (!Directory.Exists(folderPath))
            return (0, "پوشه انتخاب‌شده وجود ندارد.");
        var result = Execute(BuildPlan(folderPath, mode));
        return (result.Moved, result.Summary);
    }

    /// <summary>Moves files back to where they were and removes category folders left empty.</summary>
    public int Undo(IEnumerable<OrganizeMove> moves)
    {
        int restored = 0;
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves.Reverse())
        {
            try
            {
                if (!File.Exists(move.To) || File.Exists(move.From)) continue;
                File.Move(move.To, move.From);
                touchedDirs.Add(Path.GetDirectoryName(move.To)!);
                restored++;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Undo failed for {move.To}: {ex.Message}");
            }
        }

        foreach (var dir in touchedDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                var current = new DirectoryInfo(dir);
                // remove the empty folder and empty parents created by date-mode (Images\2026-11)
                while (current != null && current.Exists && !current.EnumerateFileSystemInfos().Any())
                {
                    var parent = current.Parent;
                    current.Delete();
                    current = parent;
                    if (current != null && moves.Any(m => string.Equals(Path.GetDirectoryName(m.From), current.FullName, StringComparison.OrdinalIgnoreCase)))
                        break; // never delete the organized root itself
                }
            }
            catch { }
        }

        return restored;
    }

    private static string DateFolder(string file)
    {
        try { return File.GetLastWriteTime(file).ToString("yyyy-MM"); }
        catch { return "Unknown"; }
    }

    /// <summary>If a *file* already has the category's name, use an alternative folder instead of crashing.</summary>
    private static string SafeTargetDir(string root, string relativeDir)
    {
        string target = Path.Combine(root, relativeDir);
        string probe = root;
        foreach (var part in relativeDir.Split(Path.DirectorySeparatorChar))
        {
            probe = Path.Combine(probe, part);
            if (File.Exists(probe))
                return Path.Combine(root, relativeDir.Replace(part, part + " (Shamsa)"));
        }
        return target;
    }

    private static string UniquePath(string path, HashSet<string>? reserved)
    {
        bool Taken(string p) => File.Exists(p) || Directory.Exists(p) || (reserved?.Contains(p) ?? false);
        if (!Taken(path)) return path;

        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int counter = 1; ; counter++)
        {
            string candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
            if (!Taken(candidate)) return candidate;
        }
    }
}
