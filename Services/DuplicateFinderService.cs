using System.Security.Cryptography;

namespace Shamsa.Services;

public record DuplicateGroup(string Hash, long SizeBytes, List<string> FilePaths)
{
    public long WastedBytes => SizeBytes * Math.Max(0, FilePaths.Count - 1);
}

public record ScanProgress(string Stage, int Done, int Total);

public class DuplicateFinderService
{
    private const int PartialBlock = 64 * 1024;

    /// <summary>
    /// Three-stage scan: size → partial hash (head+tail) → full SHA-256. Huge files are only
    /// fully hashed when they already match on size and head/tail, which is dramatically faster.
    /// Inaccessible folders are skipped (the old version crashed on the first one).
    /// </summary>
    public Task<List<DuplicateGroup>> FindDuplicatesAsync(
        string folderPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        long minSizeBytes = 1)
    {
        return Task.Run(() =>
        {
            var groups = new List<DuplicateGroup>();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return groups;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
            };

            progress?.Report(new ScanProgress("در حال فهرست‌کردن فایل‌ها...", 0, 0));
            var bySize = new Dictionary<long, List<string>>();
            int listed = 0;
            foreach (var path in Directory.EnumerateFiles(folderPath, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                long length;
                try { length = new FileInfo(path).Length; }
                catch { continue; }
                if (length < minSizeBytes) continue;

                if (!bySize.TryGetValue(length, out var list)) bySize[length] = list = new List<string>();
                list.Add(path);
                if (++listed % 500 == 0) progress?.Report(new ScanProgress($"در حال فهرست‌کردن... {listed} فایل", 0, 0));
            }

            var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
            int totalFiles = candidates.Sum(kv => kv.Value.Count);
            int processed = 0;

            foreach (var (size, paths) in candidates)
            {
                ct.ThrowIfCancellationRequested();

                // Stage 2: partial hash
                var byPartial = new Dictionary<string, List<string>>();
                foreach (var p in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    string? h = TryHash(p, partial: true);
                    processed++;
                    if (processed % 20 == 0) progress?.Report(new ScanProgress("در حال مقایسه محتوا...", processed, totalFiles));
                    if (h == null) continue;
                    if (!byPartial.TryGetValue(h, out var l)) byPartial[h] = l = new List<string>();
                    l.Add(p);
                }

                foreach (var partialGroup in byPartial.Values.Where(l => l.Count > 1))
                {
                    if (size <= PartialBlock * 2)
                    {
                        // partial hash already covered the entire file
                        groups.Add(new DuplicateGroup(TryHash(partialGroup[0], partial: false) ?? string.Empty, size, partialGroup));
                        continue;
                    }

                    // Stage 3: full hash
                    var byFull = new Dictionary<string, List<string>>();
                    foreach (var p in partialGroup)
                    {
                        ct.ThrowIfCancellationRequested();
                        string? h = TryHash(p, partial: false);
                        if (h == null) continue;
                        if (!byFull.TryGetValue(h, out var l)) byFull[h] = l = new List<string>();
                        l.Add(p);
                    }
                    groups.AddRange(byFull.Where(kv => kv.Value.Count > 1).Select(kv => new DuplicateGroup(kv.Key, size, kv.Value)));
                }
            }

            progress?.Report(new ScanProgress("پایان اسکن", totalFiles, totalFiles));
            return groups.OrderByDescending(g => g.WastedBytes).ToList();
        }, ct);
    }

    private static string? TryHash(string path, bool partial)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024);
            if (!partial || stream.Length <= PartialBlock * 2)
                return Convert.ToHexString(SHA256.HashData(stream));

            var buffer = new byte[PartialBlock * 2];
            stream.ReadExactly(buffer, 0, PartialBlock);
            stream.Seek(-PartialBlock, SeekOrigin.End);
            stream.ReadExactly(buffer, PartialBlock, PartialBlock);
            return Convert.ToHexString(SHA256.HashData(buffer));
        }
        catch
        {
            return null; // locked or vanished files are simply not reported
        }
    }
}
