using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shamsa.Services;

public sealed record UpdateInfo(Version Version, string Tag, string FileName, Uri InstallerUrl,
                                 Uri ChecksumsUrl, string ReleasePage, long SizeBytes);

/// <summary>
/// Checks the official GitHub Release, verifies its SHA-256 manifest and stages the
/// installer under LocalAppData. The installer only runs following explicit consent.
/// No code, documents, or private file names are uploaded.
/// </summary>
public static class UpdateService
{
    private const string Repo = "seyedebrahim0211-oss/Shamsa";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    private static DateTime _lastCheckedUtc = DateTime.MinValue;
    public static UpdateInfo? Latest { get; private set; }
    public static string? LastError { get; private set; }
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    public static string UpdatesDirectory => Path.Combine(AppPaths.DataDir, "updates");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Shamsa-Windows-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateInfo?> CheckAsync(bool force = false, CancellationToken token = default)
    {
        await CheckGate.WaitAsync(token);
        try
        {
            if (!force && DateTime.UtcNow - _lastCheckedUtc < TimeSpan.FromHours(6))
                return Latest;

            using var response = await Http.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = json.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("نسخه آزمایشی قابل نصب خودکار نیست.");
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)
                || !Version.TryParse(tag[1..], out var version))
                throw new InvalidDataException("نسخه انتشار نامعتبر است.");

            UpdateInfo? found = null;
            if (version > CurrentVersion)
            {
                string fileName = $"Shamsa-Setup-{tag}-win-x64.exe";
                Uri? installer = null, checksums = null;
                long size = 0;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    string? url = asset.GetProperty("browser_download_url").GetString();
                    if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        || uri.Scheme != Uri.UriSchemeHttps
                        || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                        || uri.AbsolutePath != $"/{Repo}/releases/download/{tag}/{name}") continue;
                    if (name == fileName)
                    {
                        installer = uri;
                        size = asset.GetProperty("size").GetInt64();
                    }
                    else if (name == "SHA256SUMS.txt") checksums = uri;
                }
                if (installer != null && checksums != null && size is > 0 and < 350_000_000)
                    found = new UpdateInfo(version, tag, fileName, installer, checksums,
                        $"https://github.com/{Repo}/releases/tag/{tag}", size);
                else
                    throw new InvalidDataException("نصب‌کننده رسمی و فایل SHA-256 در این انتشار موجود نیست.");
            }
            Latest = found;
            LastError = null;
            _lastCheckedUtc = DateTime.UtcNow;
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            AppLog.Warn("Checking updates failed: " + ex.Message);
            throw;
        }
        finally { CheckGate.Release(); }
    }

    private static async Task<string> ExpectedSha256Async(UpdateInfo release, CancellationToken token)
    {
        string manifest = await Http.GetStringAsync(release.ChecksumsUrl, token);
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line.Trim(), @"^(?<hash>[a-fA-F0-9]{64})\s+\*?(?<name>\S+)$");
            if (m.Success && m.Groups["name"].Value == release.FileName)
                return m.Groups["hash"].Value.ToLowerInvariant();
        }
        throw new InvalidDataException("هش معتبر فایل نصب در فهرست SHA-256 پیدا نشد.");
    }

    private static async Task<bool> VerifyAsync(string path, string hex, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(hex, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> DownloadVerifiedAsync(UpdateInfo release,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        await DownloadGate.WaitAsync(token);
        string temp = string.Empty;
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            string output = Path.Combine(UpdatesDirectory, release.FileName);
            string expected = await ExpectedSha256Async(release, token);
            if (await VerifyAsync(output, expected, token))
            {
                progress?.Report("نسخه جدید قبلاً دانلود و بررسی شده است.");
                return output;
            }

            temp = Path.Combine(UpdatesDirectory, Guid.NewGuid().ToString("N") + ".part");
            using var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.SizeBytes)
                throw new InvalidDataException("حجم فایل نصب با انتشار رسمی مطابقت ندارد.");

            long readTotal = 0;
            await using (var dest = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await using (var src = await response.Content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await src.ReadAsync(buffer, token)) > 0)
                {
                    readTotal += read;
                    if (readTotal > release.SizeBytes)
                        throw new InvalidDataException("اندازه دانلود از حجم رسمی بیشتر است.");
                    await dest.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report($"دانلود نسخه {release.Version}: {readTotal * 100 / release.SizeBytes}٪");
                }
            }
            if (readTotal != release.SizeBytes || !await VerifyAsync(temp, expected, token))
                throw new InvalidDataException("تأیید امنیت SHA-256 ناموفق بود؛ نصب انجام نمی‌شود.");
            File.Move(temp, output, overwrite: true);
            temp = string.Empty;
            progress?.Report("دانلود و بررسی SHA-256 موفق بود؛ آماده نصب.");
            return output;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temp)) { try { File.Delete(temp); } catch { } }
            DownloadGate.Release();
        }
    }

    public static bool HasVerifiedDownload(UpdateInfo release)
    {
        try
        {
            var file = Path.Combine(UpdatesDirectory, release.FileName);
            return File.Exists(file) && new FileInfo(file).Length == release.SizeBytes;
        }
        catch { return false; }
    }

    public static void LaunchInstaller(string verifiedInstaller, UpdateInfo release)
    {
        string full = Path.GetFullPath(verifiedInstaller);
        if (!full.Equals(Path.GetFullPath(Path.Combine(UpdatesDirectory, release.FileName)),
                         StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("مسیر نصب‌کننده معتبر نیست.");
        Process.Start(new ProcessStartInfo
        {
            FileName = full,
            Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("اجرای نصب‌کننده ممکن نشد.");
    }
}
