using System.Windows;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>
/// «دسکتاپ مرتب»: five desktop groups (Fences in filter mode) so every new file lands in its group
/// automatically — files stay where they are. Optionally, files older than N days are moved into
/// Desktop\بایگانی with a move log and one-click undo.
/// </summary>
public static class DesktopTidy
{
    public const string FencePrefix = "smart-tidy-";
    public const string ArchiveFolderName = "بایگانی";

    /// <summary>Overridable for tests.</summary>
    public static Func<string> DesktopFolder { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static readonly (string Key, string Title, FileKind[] Kinds, string Tint)[] Groups =
    {
        ("images", "عکس‌ها", new[] { FileKind.Image }, "#1E3A8A"),
        ("docs", "اسناد", new[] { FileKind.Document, FileKind.Ebook }, "#052E2B"),
        ("setup", "نصبی‌ها", new[] { FileKind.Software }, "#3B0764"),
        ("archives", "فشرده", new[] { FileKind.Archive }, "#1F2937"),
        ("videos", "ویدیو", new[] { FileKind.Video }, "#7C2D12"),
    };

    public static List<string> ExtensionsFor(FileKind[] kinds) =>
        kinds.SelectMany(FileKinds.ExtensionsOf)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct().OrderBy(e => e).ToList();

    /// <summary>The group fences, placed in two columns from the right edge of the work area (RTL-friendly).</summary>
    public static List<FenceDefinition> BuildFences(Rect workArea)
    {
        const double W = 300, H = 210, Gap = 16;
        var list = new List<FenceDefinition>();
        for (int i = 0; i < Groups.Length; i++)
        {
            var g = Groups[i];
            int col = i / 3, row = i % 3;
            list.Add(new FenceDefinition
            {
                Id = FencePrefix + g.Key,
                Title = g.Title,
                Mode = "filter",
                Extensions = ExtensionsFor(g.Kinds),
                Width = W,
                Height = H,
                X = Math.Max(workArea.Left + Gap, workArea.Right - (col + 1) * (W + Gap)),
                Y = workArea.Top + Gap + row * (H + Gap),
                TintHex = g.Tint
            });
        }
        return list;
    }

    public static bool HasFences(DesktopSettings s) => s.Fences.Fences.Any(f => f.Id.StartsWith(FencePrefix, StringComparison.Ordinal));

    /// <summary>Adds (or removes) the group fences in <paramref name="s"/>. Returns true when something changed. Does not save.</summary>
    public static bool ApplyTo(DesktopSettings s, bool on, Rect workArea)
    {
        bool changed = false;
        if (on)
        {
            foreach (var def in BuildFences(workArea))
            {
                if (s.Fences.Fences.Any(f => f.Id == def.Id)) continue;
                s.Fences.Fences.Add(def);
                changed = true;
            }
            if (!s.Fences.Enabled) { s.Fences.Enabled = true; changed = true; }
        }
        else
        {
            changed = s.Fences.Fences.RemoveAll(f => f.Id.StartsWith(FencePrefix, StringComparison.Ordinal)) > 0;
            if (changed && s.Fences.Fences.Count == 0 && s.Fences.Enabled) s.Fences.Enabled = false;
        }
        return changed;
    }

    /// <summary>
    /// Turns the groups on/off. With <paramref name="working"/> (the studio's working copy) the fences are added
    /// to it and the caller saves; without it the stored desktop settings are changed and saved here.
    /// Returns true when the desktop settings changed.
    /// </summary>
    public static bool SetEnabled(bool on, DesktopSettings? working = null)
    {
        InsightsStore.Update(st => st.Settings.TidyEnabled = on);
        if (working != null) return ApplyTo(working, on, SystemParameters.WorkArea);
        var s = DesktopSettingsStore.Current.Clone();
        if (!ApplyTo(s, on, SystemParameters.WorkArea)) return false;
        DesktopSettingsStore.Save(s);
        return true;
    }

    // ------------------------------------------------------------------ archive

    /// <summary>Desktop files untouched for <paramref name="days"/> days (shortcuts, hidden/system and in-progress files are kept).</summary>
    public static List<string> ArchiveCandidates(string desktop, int days, DateTime now)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return result;
        var cutoff = now.AddDays(-days);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(desktop); } catch { return result; }
        foreach (var file in files)
        {
            if (FileKinds.IsHiddenOrSystem(file) || FileKinds.IsPartialDownload(file)) continue;
            if (FileKinds.Of(file) == FileKind.Shortcut) continue;
            string ext = Path.GetExtension(file);
            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase) || ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var fi = new FileInfo(file);
                var last = fi.LastWriteTime > fi.CreationTime ? fi.LastWriteTime : fi.CreationTime;
                if (last < cutoff) result.Add(file);
            }
            catch { }
        }
        return result;
    }

    /// <summary>Moves the candidates into Desktop\بایگانی and records the moves for undo. Returns the number moved.</summary>
    public static int RunArchive(DateTime now)
    {
        var st = InsightsStore.Settings;
        string desktop = DesktopFolder();
        var candidates = ArchiveCandidates(desktop, st.ArchiveDays, now);
        if (candidates.Count == 0) return 0;
        string target = Path.Combine(desktop, ArchiveFolderName);
        Directory.CreateDirectory(target);
        var moves = new List<ArchiveMove>();
        foreach (var file in candidates)
        {
            try
            {
                string dest = UniquePath(Path.Combine(target, Path.GetFileName(file)));
                File.Move(file, dest);
                moves.Add(new ArchiveMove(file, dest, DateTime.UtcNow));
            }
            catch (Exception ex) { AppLog.Warn("Desktop archive move failed: " + ex.Message); }
        }
        if (moves.Count > 0)
            InsightsStore.Update(s => { s.LastArchive = moves.Concat(s.LastArchive).Take(2000).ToList(); });
        AppLog.Info($"Desktop archive: {moves.Count} file(s) moved");
        return moves.Count;
    }

    /// <summary>Moves every archived file of the log back to the desktop. Returns the number restored.</summary>
    public static int UndoArchive()
    {
        var moves = InsightsStore.Read(s => s.LastArchive.ToList());
        int back = 0;
        var remaining = new List<ArchiveMove>();
        foreach (var m in moves)
        {
            try
            {
                if (!File.Exists(m.To)) continue; // user already moved/deleted it
                string dest = File.Exists(m.From) ? UniquePath(m.From) : m.From;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(m.To, dest);
                back++;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Desktop archive undo failed: " + ex.Message);
                remaining.Add(m);
            }
        }
        InsightsStore.Update(s => s.LastArchive = remaining);
        return back;
    }

    public static int ArchivedCount => InsightsStore.Read(s => s.LastArchive.Count(m => File.Exists(m.To)));

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }
}
