using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Desktop.Widgets;

/// <summary>
/// Keeps the Windows time zone — and so the system clock and date everywhere — in step with the country of the
/// current public IP (for example a VPN exit server). Changing the time zone needs no administrator rights
/// (standard users hold SeTimeZonePrivilege), so <c>tzutil /s</c> is used. Only the time-zone id is read from
/// ipwho.is; nothing is stored. Prayer times and weather deliberately do NOT follow this clock — they use
/// <see cref="WidgetsSettings.PrayerWeatherZoneId"/> (Tehran by default), see <see cref="CityClock.PrayerZone"/>.
/// </summary>
public static class IpTimeZoneSync
{
    /// <summary>Value of <see cref="WidgetsSettings.PrayerWeatherZoneId"/> meaning "use the system clock".</summary>
    public const string SystemZoneToken = "system";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static DispatcherTimer? _timer;
    private static DispatcherTimer? _debounce;
    private static bool _running, _busy;
    private static DateTime _lastCheck = DateTime.MinValue;

    public static string Status { get; private set; } = "هنوز بررسی نشده است.";

    /// <summary>Raised on the UI thread after the Windows time zone was changed.</summary>
    public static event Action? ZoneChanged;

    static IpTimeZoneSync()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Negarkhaneh-Desktop/3");
    }

    /// <summary>Starts/stops the background sync according to the settings. Call from the UI thread.</summary>
    public static void Apply(DesktopSettings s)
    {
        // Like every desktop feature, nothing on the system changes until the desktop environment is switched on.
        bool want = s.Enabled && s.Widgets.SyncSystemTimeZoneWithIp;
        if (want == _running) return;
        _running = want;
        if (want)
        {
            _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
            _ = SyncNowAsync();
        }
        else
        {
            _timer?.Stop();
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
            Status = "هماهنگ‌سازی خودکار خاموش است.";
        }
    }

    private static void OnTick(object? sender, EventArgs e) => _ = SyncNowAsync();
    private static void OnNetworkAvailability(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged(sender, e);

    /// <summary>A VPN connecting/disconnecting changes the network addresses: re-check a few seconds later.</summary>
    private static void OnNetworkChanged(object? sender, EventArgs e)
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _debounce.Stop();
            _debounce.Tick -= OnDebounce;
            _debounce.Tick += OnDebounce;
            _debounce.Start();
        });
    }

    private static void OnDebounce(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        _ = SyncNowAsync(force: true);
    }

    public static async Task SyncNowAsync(bool force = false)
    {
        if (_busy) return;
        if (!force && DateTime.UtcNow - _lastCheck < TimeSpan.FromMinutes(2)) return;
        _busy = true;
        _lastCheck = DateTime.UtcNow;
        try
        {
            var (iana, serverUtc) = await LookupZoneAsync().ConfigureAwait(true);
            if (iana == null) { Status = "دریافت منطقهٔ زمانی از IP ممکن نشد (اینترنت؟)."; return; }
            if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var windowsId) || windowsId == null)
            {
                Status = $"منطقهٔ زمانی «{iana}» در ویندوز شناخته نشد.";
                return;
            }
            TimeZoneInfo.ClearCachedData();
            string drift = DriftNote(serverUtc);
            if (string.Equals(TimeZoneInfo.Local.Id, windowsId, StringComparison.OrdinalIgnoreCase))
            {
                Status = $"منطقهٔ زمانی سیستم با IP هماهنگ است: {Pretty(iana)}{drift}";
                return;
            }
            string old = TimeZoneInfo.Local.Id;
            if (await SetWindowsZoneAsync(windowsId).ConfigureAwait(true))
            {
                TimeZoneInfo.ClearCachedData();
                CultureInfo.CurrentCulture.ClearCachedData();
                Status = $"منطقهٔ زمانی سیستم به {Pretty(iana)} تغییر کرد (قبلی: {old}).{drift}";
                AppLog.Info($"Time zone synced with IP: {old} → {windowsId}");
                ZoneChanged?.Invoke();
            }
            else Status = $"تغییر منطقهٔ زمانی به {Pretty(iana)} ممکن نشد. (تنظیمات ویندوز › زمان › «تنظیم خودکار منطقهٔ زمانی» را خاموش کن.)";
        }
        catch (Exception ex)
        {
            Status = "هماهنگ‌سازی ساعت ناموفق بود.";
            AppLog.Warn("IP time zone sync: " + ex.Message);
        }
        finally { _busy = false; }
    }

    private static string Pretty(string iana) => iana.Replace('_', ' ');

    /// <summary>The clock itself is kept right by Windows Time (NTP); we only report a big difference.</summary>
    private static string DriftNote(DateTimeOffset? serverUtc)
    {
        if (serverUtc is not { } server) return "";
        var diff = DateTimeOffset.UtcNow - server;
        if (Math.Abs(diff.TotalMinutes) < 2) return "";
        return DesktopTheme.Digits($" · ساعت رایانه {Math.Abs((int)diff.TotalMinutes)} دقیقه {(diff > TimeSpan.Zero ? "جلو" : "عقب")} است؛ در تنظیمات ویندوز «همگام‌سازی اکنون» را بزن.", true);
    }

    private static async Task<(string? Zone, DateTimeOffset? ServerUtc)> LookupZoneAsync()
    {
        try
        {
            using var resp = await Http.GetAsync("https://ipwho.is/?fields=success,timezone").ConfigureAwait(false);
            DateTimeOffset? date = resp.Headers.Date;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return (null, date);
            string? id = root.GetProperty("timezone").GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.Contains('/')) return (null, date);
            return (id, date);
        }
        catch (Exception ex)
        {
            AppLog.Warn("IP time zone lookup: " + ex.Message);
            return (null, null);
        }
    }

    private static async Task<bool> SetWindowsZoneAsync(string windowsId)
    {
        if (windowsId.Contains('"')) return false;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tzutil.exe"), $"/s \"{windowsId}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("tzutil failed: " + ex.Message);
            return false;
        }
    }
}
