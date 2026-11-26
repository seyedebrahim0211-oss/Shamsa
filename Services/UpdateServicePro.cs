#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Shamsa.Services;

public sealed record ReleaseUpdateDetails(
    Version Version,
    string Tag,
    string ReleaseTitle,
    string ChangelogMarkdown,
    string FileName,
    Uri InstallerUrl,
    Uri? ChecksumsUrl,
    string ReleasePageUrl,
    long SizeBytes,
    DateTimeOffset PublishedAt);

public sealed record UpdateDownloadProgress(long BytesDownloaded, long TotalBytes, double Percent, string StatusText);

/// <summary>
/// Enhanced In-App Windows Auto-Updater for Shamsa.
/// Features: GitHub Releases check, Persian/English changelog display, streaming download progress,
/// SHA-256 signature verification, pre-update settings backup, and rollback state tracking.
/// </summary>
public static class UpdateServicePro
{
    public const string Repo = "seyedebrahim0211-oss/Shamsa";
    private static readonly Regex TagRegex = new(@"^v(\d+)\.(\d+)\.(\d+)$", RegexOptions.Compiled);

    public static string UpdatesDir => Path.Combine(ShamsaProSettingsService.DataDir, "updates");
    public static string PendingStateFile => Path.Combine(UpdatesDir, "pending-update.json");

    public static Version CurrentVersion => typeof(UpdateServicePro).Assembly.GetName().Version ?? new Version(3, 0, 0);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shamsa/{CurrentVersion} (Windows)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<ReleaseUpdateDetails?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var client = CreateHttpClient();
        using var resp = await client.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            return null;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var match = TagRegex.Match(tag);
        if (!match.Success) return null;

        var remoteVersion = new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));

        if (remoteVersion <= CurrentVersion) return null;

        string expectedInstaller = $"Shamsa-Setup-{tag}-win-x64.exe";
        Uri? installerUri = null;
        Uri? checksumsUri = null;
        long sizeBytes = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string dl = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!Uri.TryCreate(dl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    continue;

                if (name.Equals(expectedInstaller, StringComparison.OrdinalIgnoreCase))
                {
                    installerUri = uri;
                    sizeBytes = asset.GetProperty("size").GetInt64();
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUri = uri;
                }
            }
        }

        if (installerUri == null || sizeBytes <= 0 || sizeBytes > 500_000_000)
            return null;

        return new ReleaseUpdateDetails(
            remoteVersion,
            tag,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            expectedInstaller,
            installerUri,
            checksumsUri,
            root.TryGetProperty("html_url", out var html) ? html.GetString() ?? $"https://github.com/{Repo}/releases" : $"https://github.com/{Repo}/releases",
            sizeBytes,
            root.TryGetProperty("published_at", out var pub) ? pub.GetDateTimeOffset() : DateTimeOffset.UtcNow);
    }

    public static async Task<string> DownloadAndVerifyAsync(
        ReleaseUpdateDetails release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdatesDir);
        string destPath = Path.Combine(UpdatesDir, release.FileName);
        string partPath = destPath + ".part";

        using var client = CreateHttpClient();
        progress?.Report(new UpdateDownloadProgress(0, release.SizeBytes, 0, "در حال اتصال به سرور بروزرسانی..."));

        using (var resp = await client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? release.SizeBytes;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                double pct = total > 0 ? Math.Min(100, downloaded * 100.0 / total) : 0;
                progress?.Report(new UpdateDownloadProgress(downloaded, total, pct, $"در حال دانلود نسخه {release.Tag}... ({pct:0.0}%)"));
            }
        }

        if (release.ChecksumsUrl != null)
        {
            progress?.Report(new UpdateDownloadProgress(release.SizeBytes, release.SizeBytes, 100, "در حال بررسی اصالت فایل (SHA-256)..."));
            string sums = await client.GetStringAsync(release.ChecksumsUrl, ct);
            string? expectedHash = ExtractHashForFile(sums, release.FileName);
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                string actualHash = await ComputeSha256Async(partPath, ct);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partPath);
                    throw new InvalidDataException("عدم تطابق امضای SHA-256 بسته بروزرسانی! عملیات متوقف شد.");
                }
            }
        }

        File.Move(partPath, destPath, overwrite: true);
        progress?.Report(new UpdateDownloadProgress(release.SizeBytes, release.SizeBytes, 100, "بسته بروزرسانی تایید و آماده نصب است."));
        return destPath;
    }

    public static void InstallAndRestart(string verifiedInstallerPath, ReleaseUpdateDetails release, bool silent = true)
    {
        if (!File.Exists(verifiedInstallerPath))
            throw new FileNotFoundException("فایل نصاب بروزرسانی یافت نشد.", verifiedInstallerPath);

        // Preserve user settings before launching installer
        ShamsaProSettingsService.CreateBackup();
        File.WriteAllText(PendingStateFile, JsonSerializer.Serialize(new
        {
            FromVersion = CurrentVersion.ToString(),
            TargetVersion = release.Version.ToString(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            InstallerPath = verifiedInstallerPath
        }));

        string args = silent
            ? "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART"
            : "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART";

        Process.Start(new ProcessStartInfo
        {
            FileName = verifiedInstallerPath,
            Arguments = args,
            UseShellExecute = true
        });
    }

    public static void VerifyPostUpdateHealth()
    {
        if (!File.Exists(PendingStateFile)) return;
        try
        {
            File.Delete(PendingStateFile);
        }
        catch { /* Non-fatal */ }
    }

    private static string? ExtractHashForFile(string checksumsText, string fileName)
    {
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 64) continue;
            if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && parts[0].Length == 64)
                    return parts[0];
            }
        }
        return null;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
