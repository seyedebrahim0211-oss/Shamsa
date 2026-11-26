using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shamsa.Services;

public sealed record UpdateInfo(Version Version, string Tag, string FileName, Uri InstallerUrl,
                                 Uri ChecksumsUrl, string ReleasePage, long SizeBytes, string Notes, DateTime PublishedUtc);

/// <summary>
/// In-app Windows updater. Checks the official GitHub Release, verifies the installer against the
/// SHA-256 manifest published in the same release, stages it under LocalAppData and installs it
/// (interactive or silent with automatic relaunch). No user files or file names are uploaded —
/// the only request is an anonymous GET of the public release metadata.
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
    public static DateTime LastCheckedLocal => _lastCheckedUtc == DateTime.MinValue ? DateTime.MinValue : _lastCheckedUtc.ToLocalTime();
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    public static string UpdatesDirectory => Path.Combine(AppPaths.DataDir, "updates");
    private static string PendingMarker => Path.Combine(UpdatesDirectory, "pending-install.json");

    /// <summary>Raised (any thread) after a check finds a new version or a download finishes.</summary>
    public static event Action? StateChanged;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Shamsa-Windows-Updater/3.0");
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

                string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                    ? body.GetString() ?? string.Empty : string.Empty;
                if (notes.Length > 6000) notes = notes[..6000] + "…";
                DateTime published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(p.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.UtcNow;

                if (installer != null && checksums != null && size is > 0 and < 350_000_000)
                    found = new UpdateInfo(version, tag, fileName, installer, checksums,
                        $"https://github.com/{Repo}/releases/tag/{tag}", size, notes.Trim(), published);
                else
                    throw new InvalidDataException("نصب‌کننده رسمی و فایل SHA-256 در این انتشار موجود نیست.");
            }
            Latest = found;
            LastError = null;
            _lastCheckedUtc = DateTime.UtcNow;
            StateChanged?.Invoke();
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

    /// <summary>Downloads (resuming nothing, always re-verified) and returns the path of a SHA-256 verified installer.</summary>
    public static async Task<string> DownloadVerifiedAsync(UpdateInfo release,
        IProgress<string>? progress = null, CancellationToken token = default, IProgress<double>? percent = null)
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
                percent?.Report(100);
                return output;
            }

            temp = Path.Combine(UpdatesDirectory, Guid.NewGuid().ToString("N") + ".part");
            using var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.SizeBytes)
                throw new InvalidDataException("حجم فایل نصب با انتشار رسمی مطابقت ندارد.");

            long readTotal = 0;
            int lastPercent = -1;
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
                    int pct = (int)(readTotal * 100 / release.SizeBytes);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        percent?.Report(pct);
                        progress?.Report($"دانلود نسخه {release.Version}: {pct}٪ ({FileKinds.FormatSize(readTotal)} از {FileKinds.FormatSize(release.SizeBytes)})");
                    }
                }
            }
            progress?.Report("در حال بررسی امنیتی SHA-256…");
            if (readTotal != release.SizeBytes || !await VerifyAsync(temp, expected, token))
                throw new InvalidDataException("تأیید امنیت SHA-256 ناموفق بود؛ نصب انجام نمی‌شود.");
            File.Move(temp, output, overwrite: true);
            temp = string.Empty;
            CleanupOldInstallers(keep: output);
            progress?.Report("دانلود و بررسی SHA-256 موفق بود؛ آماده نصب.");
            StateChanged?.Invoke();
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

    /// <summary>
    /// Re-verifies and starts the installer. Silent mode uses Inno Setup /VERYSILENT; the installer
    /// relaunches Shamsa with --updated afterwards. The caller must exit the app right after this returns.
    /// </summary>
    public static async Task LaunchInstallerAsync(string verifiedInstaller, UpdateInfo release, bool silent, CancellationToken token = default)
    {
        string full = Path.GetFullPath(verifiedInstaller);
        if (!full.Equals(Path.GetFullPath(Path.Combine(UpdatesDirectory, release.FileName)),
                         StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("مسیر نصب‌کننده معتبر نیست.");
        // Defence against a swapped file between download and launch.
        string expected = await ExpectedSha256Async(release, token);
        if (!await VerifyAsync(full, expected, token))
            throw new InvalidDataException("فایل نصب پس از دانلود تغییر کرده است؛ نصب متوقف شد.");

        WritePendingMarker(release);
        string args = silent
            ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP- /LOG"
            : "/CLOSEAPPLICATIONS /NORESTART";
        _ = Process.Start(new ProcessStartInfo { FileName = full, Arguments = args, UseShellExecute = true })
            ?? throw new InvalidOperationException("اجرای نصب‌کننده ممکن نشد.");
        AppLog.Info($"Launched {(silent ? "silent" : "interactive")} installer for {release.Version}");
    }

    private sealed record PendingInstall(string Version, string Notes, DateTime StartedUtc);

    private static void WritePendingMarker(UpdateInfo release)
    {
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            File.WriteAllText(PendingMarker, JsonSerializer.Serialize(new PendingInstall(release.Version.ToString(3), release.Notes, DateTime.UtcNow)));
        }
        catch (Exception ex) { AppLog.Warn("Could not write update marker: " + ex.Message); }
    }

    /// <summary>
    /// Called at startup. Returns a user-facing message when an update was just installed (or failed),
    /// otherwise null. Also removes stale installers once the new version runs.
    /// </summary>
    public static string? ConsumePostUpdateMessage()
    {
        try
        {
            if (!File.Exists(PendingMarker)) return null;
            var pending = JsonSerializer.Deserialize<PendingInstall>(File.ReadAllText(PendingMarker));
            File.Delete(PendingMarker);
            if (pending == null || !Version.TryParse(pending.Version, out var target)) return null;
            if (CurrentVersion >= target)
            {
                CleanupOldInstallers(keep: null);
                string firstLine = pending.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => !l.StartsWith('#')) ?? string.Empty;
                return $"✅ شمسا به نسخه {CurrentVersion.ToString(3)} به‌روزرسانی شد." + (firstLine.Length > 0 ? " " + firstLine.TrimStart('-', '*', ' ') : "");
            }
            if (DateTime.UtcNow - pending.StartedUtc < TimeSpan.FromDays(2))
                return $"⚠️ نصب نسخه {pending.Version} کامل نشد؛ نسخه {CurrentVersion.ToString(3)} همچنان فعال است و تنظیمات شما دست‌نخورده است.";
        }
        catch (Exception ex) { AppLog.Warn("Reading update marker failed: " + ex.Message); }
        return null;
    }

    private static void CleanupOldInstallers(string? keep)
    {
        try
        {
            if (!Directory.Exists(UpdatesDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(UpdatesDirectory, "Shamsa-Setup-*.exe"))
                if (keep == null || !file.Equals(keep, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(file); } catch { }
            foreach (var part in Directory.EnumerateFiles(UpdatesDirectory, "*.part"))
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > TimeSpan.FromHours(1)) File.Delete(part); } catch { }
        }
        catch { }
    }

    // ------------------------------------------------------------------ idle detection for smart install

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Time since the last keyboard/mouse input on this session.</summary>
    public static TimeSpan GetUserIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
