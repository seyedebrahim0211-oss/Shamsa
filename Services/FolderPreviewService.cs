namespace Shamsa.Services;

/// <summary>
/// Non-recursive, bounded and read-only folder inspection for Explorer hover cards.
/// Ignores symlinks, offline placeholders, hidden/system entries and UNC paths so
/// hovering does not wake network shares or hydrate cloud files.
/// </summary>
public static class FolderPreviewService
{
    public sealed record Snapshot(int Files, int Folders, bool IsTruncated, IReadOnlyList<string> VisualFiles, string? Warning);

    public static Snapshot Inspect(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal)
            || !Directory.Exists(folder))
            return new Snapshot(0, 0, false, Array.Empty<string>(), "این مسیر قابل پیش‌نمایش نیست.");

        var visuals = new List<string>(6);
        int files = 0, folders = 0, inspected = 0;
        bool truncated = false;
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System |
                                   FileAttributes.ReparsePoint | FileAttributes.Offline
            };
            foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", options))
            {
                if (++inspected > 100) { truncated = true; break; }
                if (Directory.Exists(path)) { folders++; continue; }
                if (!File.Exists(path)) continue;
                files++;
                if (visuals.Count >= 6) continue;
                try
                {
                    var kind = FileKinds.Of(path);
                    if ((FileKinds.IsPreviewableImage(path) || kind == FileKind.Video)
                         && new FileInfo(path).Length <= 150_000_000)
                        visuals.Add(path);
                }
                catch { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new Snapshot(files, folders, truncated, visuals, "دسترسی خواندن این پوشه محدود است.");
        }
        return new Snapshot(files, folders, truncated, visuals, null);
    }

    public sealed record Entry(string Path, string Name, bool IsFolder, FileKind Kind, long Size, DateTime Modified);

    public sealed record DetailedSnapshot(
        string Folder, string Name, DateTime Modified, int Files, int Folders, long ListedBytes,
        bool IsTruncated, IReadOnlyList<Entry> Entries, IReadOnlyList<Entry> Media, string? Warning);

    /// <summary>
    /// Bounded first-level listing for the large glass folder card: inspects at most 400 entries,
    /// never recurses, never opens file contents, skips hidden/system/offline/reparse entries.
    /// </summary>
    public static DetailedSnapshot InspectDetailed(string folder, int maxItems, int sort)
    {
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = folder;
        DateTime modified = DateTime.MinValue;
        if (!System.IO.Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(folder))
            return new DetailedSnapshot(folder, name, modified, 0, 0, 0, false, Array.Empty<Entry>(), Array.Empty<Entry>(), "این مسیر قابل پیش‌نمایش نیست.");

        try { modified = Directory.GetLastWriteTime(folder); } catch { }
        var all = new List<Entry>(64);
        int files = 0, folders = 0;
        long bytes = 0;
        bool truncated = false;
        string? warning = null;
        try
        {
            var dir = new DirectoryInfo(folder);
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline
            };
            int inspected = 0;
            foreach (var info in dir.EnumerateFileSystemInfos("*", options))
            {
                if (++inspected > 400) { truncated = true; break; }
                try
                {
                    bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    // Cloud placeholders that are not local would download on access.
                    if ((info.Attributes & (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */) != 0 && !isDir) continue;
                    if (isDir)
                    {
                        folders++;
                        all.Add(new Entry(info.FullName, info.Name, true, FileKind.Folder, 0, info.LastWriteTime));
                    }
                    else
                    {
                        long len = ((FileInfo)info).Length;
                        files++;
                        bytes += len;
                        all.Add(new Entry(info.FullName, info.Name, false, FileKinds.FromExtension(info.Extension), len, info.LastWriteTime));
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            warning = "دسترسی خواندن این پوشه محدود است.";
        }

        IEnumerable<Entry> ordered = sort switch
        {
            1 => all.OrderByDescending(e => e.Modified),
            2 => all.OrderBy(e => e.IsFolder ? 0 : 1).ThenBy(e => e.Kind).ThenBy(e => e.Name, NaturalStringComparer.Instance),
            _ => all.OrderBy(e => e.IsFolder ? 0 : 1).ThenBy(e => e.Name, NaturalStringComparer.Instance)
        };
        var list = ordered.Take(Math.Clamp(maxItems, 1, 120)).ToList();
        var media = all.Where(e => !e.IsFolder && e.Size <= 150_000_000
                                   && (e.Kind == FileKind.Video || FileKinds.IsPreviewableImage(e.Path)))
                       .OrderByDescending(e => e.Modified).Take(4).ToList();
        return new DetailedSnapshot(folder, name, modified, files, folders, bytes,
            truncated || all.Count > list.Count, list, media, warning);
    }
}
