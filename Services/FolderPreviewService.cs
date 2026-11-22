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
}
