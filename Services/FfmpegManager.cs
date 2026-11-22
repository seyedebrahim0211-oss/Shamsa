using System.IO.Compression;
using System.Net.Http;

namespace Shamsa.Services;

public static class FfmpegManager
{
    private static readonly string LocalDir = Path.Combine(AppPaths.DataDir, "ffmpeg");
    private static readonly string LocalExe = Path.Combine(LocalDir, "ffmpeg.exe");

    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private static string? _cachedPath;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly object Gate = new();

    /// <summary>
    /// Finds ffmpeg.exe: the app's private copy, winget/scoop/choco installs, then PATH.
    /// PATH is re-read from the registry so a fresh `winget install` is detected without restarting.
    /// Result is cached for 30 s (it used to spawn `where.exe` on every call).
    /// </summary>
    public static string? GetFfmpegPath(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && _cachedPath != null && File.Exists(_cachedPath)) return _cachedPath;
            if (!refresh && _cachedPath == null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30)) return null;

            _cachedPath = Locate("ffmpeg.exe");
            _cachedAt = DateTime.UtcNow;
            return _cachedPath;
        }
    }

    public static string? GetFfprobePath()
    {
        string? ffmpeg = GetFfmpegPath();
        if (ffmpeg == null) return null;
        string probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        return File.Exists(probe) ? probe : Locate("ffprobe.exe");
    }

    public static bool IsAvailable() => GetFfmpegPath() != null;

    private static string? Locate(string exeName)
    {
        foreach (var candidate in CandidatePaths(exeName))
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths(string exeName)
    {
        yield return Path.Combine(LocalDir, exeName);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exeName);
        yield return Path.Combine(userProfile, "scoop", "shims", exeName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", exeName);

        var pathDirs = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = null;
            try { value = Environment.GetEnvironmentVariable("PATH", target); } catch { }
            if (string.IsNullOrEmpty(value)) continue;
            pathDirs.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var dir in pathDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(dir.Trim('"')); }
            catch { continue; }
            yield return Path.Combine(expanded, exeName);
        }

        // winget package folder (when the Links shim is missing)
        string wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        IEnumerable<string> found = Array.Empty<string>();
        try
        {
            if (Directory.Exists(wingetPackages))
            {
                found = Directory.EnumerateDirectories(wingetPackages, "*FFmpeg*")
                    .SelectMany(d => Directory.EnumerateFiles(d, exeName, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }))
                    .ToList();
            }
        }
        catch { }
        foreach (var f in found) yield return f;
    }

    public static async Task<(bool Success, string Message)> DownloadFfmpegAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string zipPath = Path.Combine(LocalDir, $"ffmpeg_{Guid.NewGuid():N}.zip");
        try
        {
            Directory.CreateDirectory(LocalDir);
            progress?.Report("در حال اتصال به سرور FFmpeg...");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Shamsa/2.0");
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int lastPercent = -1;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;
                    if (total is > 0)
                    {
                        int percent = (int)(readTotal * 100 / total.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report($"در حال دانلود FFmpeg... {percent}٪ ({FileKinds.FormatSize(readTotal)} از {FileKinds.FormatSize(total.Value)})");
                        }
                    }
                    else
                    {
                        progress?.Report($"در حال دانلود FFmpeg... {FileKinds.FormatSize(readTotal)}");
                    }
                }
            }

            progress?.Report("در حال استخراج ffmpeg.exe و ffprobe.exe...");
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var ffmpeg = archive.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("فایل ffmpeg.exe داخل آرشیو پیدا نشد.");
                ffmpeg.ExtractToFile(LocalExe, overwrite: true);

                var ffprobe = archive.Entries.FirstOrDefault(e => e.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase));
                ffprobe?.ExtractToFile(Path.Combine(LocalDir, "ffprobe.exe"), overwrite: true);
            }, ct);

            GetFfmpegPath(refresh: true);
            progress?.Report("FFmpeg با موفقیت نصب شد!");
            return (true, "FFmpeg آماده استفاده است.");
        }
        catch (OperationCanceledException)
        {
            return (false, "دانلود FFmpeg لغو شد.");
        }
        catch (Exception ex)
        {
            AppLog.Error("FFmpeg download failed", ex);
            return (false, $"خطا در دانلود FFmpeg: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }
}
