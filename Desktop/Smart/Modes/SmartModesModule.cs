using System.Windows;
using System.Windows.Threading;
using Shamsa.Desktop.Rules;
using Shamsa.Desktop.Shell;
using Shamsa.Desktop.Widgets;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Modes;

/// <summary>
/// «حالت‌های هوشمند»: one-click modes (کار، فیلم، نماز و حالت‌های کاربر) that can also start by themselves,
/// wallpaper by time of day, and gentle reminders (break, occasions, azan). Everything is off by default;
/// every system change a mode makes is undone when the mode ends, on exit and after a crash.
/// </summary>
public sealed class SmartModesModule : IDesktopModule
{
    public static SmartModesModule? Instance { get; private set; }

    public string Id => "smartmodes";
    public string DisplayName => "حالت‌های هوشمند";
    public bool IsRunning => _timer?.IsEnabled == true;

    /// <summary>Raised (UI thread) when a mode starts or ends, or the countdown text changes.</summary>
    public static event Action? StateChanged;

    private DispatcherTimer? _timer;
    private static bool _startupRecoveryDone;
    private DateTime _activeSince = DateTime.Now;
    private bool _breakShown;
    private string _lastPrayerNotice = "";
    private DateTime _lastPrayerModeFor = DateTime.MinValue;

    public SmartModesModule()
    {
        Instance = this;
        SmartModesStore.Changed += OnStoreChanged;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>The mode that is on right now, or null.</summary>
    public static string? ActiveMode => SmartModesStore.Data.Active?.Mode;

    /// <summary>Turns a mode on by name («حالت کار», «کار», «فیلم» …). Returns false when there is no such mode.</summary>
    public static bool Activate(string modeName) => Activate(modeName, "manual", null);

    /// <summary>Modes the user switched off while their automatic trigger was still true; they stay off until the trigger ends.</summary>
    private static readonly HashSet<string> Suppressed = new();

    /// <summary>Ends the current mode (the user's choice) and puts every changed setting back.</summary>
    public static void Deactivate()
    {
        var active = SmartModesStore.Data.Active;
        if (active != null && active.Trigger is "app" or "fullscreen" or "prayer") Suppressed.Add(active.Mode);
        DeactivateCore();
    }

    private static void DeactivateCore()
    {
        var data = SmartModesStore.Data;
        var active = data.Active;
        if (active == null) return;
        try { SystemToggles.RestoreAll(active); }
        catch (Exception ex) { AppLog.Warn("Smart mode restore: " + ex.Message); }
        data.Active = null;
        SmartModesStore.Save(notify: false);
        AppLog.Info("Smart mode off: " + active.Mode);
        StateChanged?.Invoke();
    }

    /// <summary>«اذان مغرب ۱۷:۰۲ · ۴۵ دقیقه دیگر», or "" when the countdown is off or cannot be computed. For the top bar.</summary>
    public static string NextPrayerText
    {
        get
        {
            try
            {
                if (!SmartModesStore.Data.Reminders.PrayerCountdown) return "";
                var next = PrayerClock.Next();
                // Words sit between the two numbers so the clock and the duration never merge into one LTR run.
                return next == null ? "" : $"{PrayerClock.Duration(next.Remaining)} تا {next.Name} (ساعت {PrayerClock.Clock(next.AtCity)})";
            }
            catch { return ""; }
        }
    }

    /// <summary>Crash handlers: undo registry changes of an active mode without any UI work.</summary>
    public static void EmergencyRestore()
    {
        try
        {
            var active = SmartModesStore.Data.Active;
            if (active == null) return;
            SystemToggles.RestoreAll(active);
            SmartModesStore.Data.Active = null;
            SmartModesStore.Save(notify: false);
        }
        catch { }
    }

    // ------------------------------------------------------------------ IDesktopModule

    public void Apply(DesktopSettings settings)
    {
        var data = SmartModesStore.Data;
        if (!_startupRecoveryDone)
        {
            _startupRecoveryDone = true;
            // A mode left on by a crash or power loss: put the user's settings back first.
            if (data.Active != null)
            {
                AppLog.Info("Smart mode left on from last session, restoring: " + data.Active.Mode);
                DeactivateCore();
            }
        }

        bool needed = data.Wallpaper.Enabled
                      || data.Reminders.BreakReminder || data.Reminders.Occasions || data.Reminders.PrayerNotice || data.Reminders.PrayerCountdown
                      || data.Modes.Any(m => m.Triggers.OnApps || m.Triggers.OnFullscreen || m.Triggers.BeforePrayer)
                      || data.Active != null;
        if (needed)
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
                _timer.Tick += (_, _) => Tick();
            }
            if (!_timer.IsEnabled)
            {
                _activeSince = DateTime.Now;
                _timer.Start();
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Tick(forceWallpaper: true)));
            }
        }
        else _timer?.Stop();
    }

    public void Stop()
    {
        _timer?.Stop();
        DeactivateCore();
        SmartToast.Close();
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) => null;

    private void OnStoreChanged()
    {
        try { Apply(DesktopSettingsStore.Current); } catch (Exception ex) { AppLog.Warn("Smart modes apply: " + ex.Message); }
    }

    // ------------------------------------------------------------------ activation

    private static readonly string[] TriggerPriority = { "app", "fullscreen", "prayer", "manual" };
    private static int Rank(string trigger) => Array.IndexOf(TriggerPriority, trigger);

    internal static bool Activate(string modeName, string trigger, DateTime? until)
    {
        var data = SmartModesStore.Data;
        var mode = SmartModesStore.Find(modeName);
        if (mode == null) return false;
        if (data.Active != null)
        {
            if (data.Active.Mode == mode.Name)
            {
                // Same mode: a manual click makes it sticky; a trigger only refreshes its end time.
                if (Rank(trigger) > Rank(data.Active.Trigger)) data.Active.Trigger = trigger;
                if (until != null) data.Active.Until = until;
                SmartModesStore.Save(notify: false);
                return true;
            }
            if (Rank(trigger) < Rank(data.Active.Trigger)) return false; // never override a stronger reason
            DeactivateCore();
        }

        var state = new ActiveModeState { Mode = mode.Name, Trigger = trigger, Since = DateTime.Now, Until = until };
        data.Active = state;
        SmartModesStore.Save(notify: false); // record intent before touching the system
        try
        {
            if (mode.QuietNotifications) SystemToggles.SetNotificationsQuiet(state);
            if (mode.Theme is "dark" or "light") SystemToggles.SetTheme(state, mode.Theme == "dark");
        }
        catch (Exception ex) { AppLog.Warn($"Smart mode {mode.Name}: {ex.Message}"); }
        SmartModesStore.Save(notify: false); // originals are now on disk

        if (mode.PauseMedia) _ = SystemToggles.PausePlayingMediaAsync();
        if (!string.IsNullOrWhiteSpace(mode.LayoutProfile))
        {
            var profile = DesktopSettingsStore.Current.Layouts.FirstOrDefault(p => p.Name == mode.LayoutProfile);
            if (profile != null) _ = RestoreLayoutAsync(profile);
        }
        AppLog.Info($"Smart mode on: {mode.Name} ({trigger})");
        StateChanged?.Invoke();
        return true;
    }

    private static async Task RestoreLayoutAsync(LayoutProfile profile)
    {
        try { await LayoutProfiles.RestoreAsync(profile); }
        catch (Exception ex) { AppLog.Warn("Smart mode layout: " + ex.Message); }
    }

    // ------------------------------------------------------------------ the tick

    internal void Tick(bool forceWallpaper = false)
    {
        var data = SmartModesStore.Data;
        try { TickTriggers(data); } catch (Exception ex) { AppLog.Warn("Smart triggers: " + ex.Message); }
        try { TickReminders(data); } catch (Exception ex) { AppLog.Warn("Smart reminders: " + ex.Message); }
        try
        {
            if (TimeOfDayWallpaper.Tick(data, forceWallpaper) != null) SmartModesStore.Save(notify: false);
        }
        catch (Exception ex) { AppLog.Warn("Smart wallpaper: " + ex.Message); }
        StateChanged?.Invoke();
    }

    private void TickTriggers(SmartModesData data)
    {
        var active = data.Active;

        // 1. A mode with an end time (prayer) ends by itself.
        if (active is { Until: { } until } && DateTime.Now >= until) { DeactivateCore(); Suppressed.Clear(); }

        // 2. Prayer: N minutes before azan.
        var prayerMode = data.Modes.FirstOrDefault(m => m.Triggers.BeforePrayer);
        if (prayerMode != null && PrayerClock.Next() is { } next)
        {
            int lead = data.Reminders.PrayerNoticeMinutes;
            if (next.Remaining <= TimeSpan.FromMinutes(lead) && _lastPrayerModeFor != next.AtCity)
            {
                _lastPrayerModeFor = next.AtCity;
                var end = DateTime.Now + next.Remaining + TimeSpan.FromMinutes(prayerMode.Triggers.PrayerDurationMinutes);
                if (Activate(prayerMode.Name, "prayer", end))
                    SmartToast.Show($"{PrayerClock.Duration(next.Remaining)} تا {next.Name}", $"«{prayerMode.Name}» روشن شد؛ پس از نماز خودکار خاموش می‌شود.", "",
                        "خاموش کن", Deactivate);
            }
        }

        // 3. Full screen video.
        var fsMode = data.Modes.FirstOrDefault(m => m.Triggers.OnFullscreen);
        if (fsMode != null)
        {
            bool fs = false;
            if (ShellInterop.IsFullscreenAppActive())
            {
                var list = fsMode.Triggers.FullscreenApps;
                string? fg = SystemToggles.ForegroundProcessName();
                fs = list.Count == 0 || (fg != null && list.Any(a => string.Equals(SystemToggles.CleanProcessName(a), fg, StringComparison.OrdinalIgnoreCase)));
            }
            if (!fs) Suppressed.Remove(fsMode.Name);
            if (fs && !Suppressed.Contains(fsMode.Name)) Activate(fsMode.Name, "fullscreen", null);
            else if (!fs && data.Active is { Trigger: "fullscreen" } a && a.Mode == fsMode.Name) DeactivateCore();
        }

        // 4. Apps.
        var appModes = data.Modes.Where(m => m.Triggers.OnApps && m.Triggers.Apps.Count > 0).ToList();
        if (appModes.Count > 0)
        {
            var running = SystemToggles.RunningProcessNames();
            var matching = appModes.Where(m => m.Triggers.Apps.Any(a => running.Contains(SystemToggles.CleanProcessName(a)))).ToList();
            foreach (var m in appModes.Except(matching)) Suppressed.Remove(m.Name);
            var match = matching.FirstOrDefault(m => !Suppressed.Contains(m.Name));
            if (match != null) Activate(match.Name, "app", null);
            else if (data.Active is { Trigger: "app" } cur && !matching.Any(m => m.Name == cur.Mode)) DeactivateCore();
        }
    }

    private void TickReminders(SmartModesData data)
    {
        var r = data.Reminders;

        // Break reminder: continuous use (a pause of 5 minutes or more resets the clock).
        if (r.BreakReminder)
        {
            var idle = SystemToggles.IdleTime();
            if (idle >= TimeSpan.FromMinutes(5)) { _activeSince = DateTime.Now; _breakShown = false; }
            else if (!_breakShown && DateTime.Now - _activeSince >= TimeSpan.FromMinutes(r.BreakAfterMinutes)
                     && !ShellInterop.IsFullscreenAppActive())
            {
                _breakShown = true;
                SmartToast.Show("وقت یک استراحت کوتاه", $"{PrayerClock.Duration(DateTime.Now - _activeSince)} پیوسته پای سیستم بودی. چند دقیقه چشم‌هایت را استراحت بده و کمی راه برو.", "",
                    "یادآوری بعد", () => { _activeSince = DateTime.Now; _breakShown = false; });
            }
        }

        // Occasions: once a day (after 9:00), about tomorrow.
        if (r.Occasions && DateTime.Now.Hour >= 9 && data.LastOccasionNotice.Date != DateTime.Today)
        {
            data.LastOccasionNotice = DateTime.Today;
            SmartModesStore.Save(notify: false);
            var tomorrow = PersianHolidays.For(DateTime.Today.AddDays(1));
            if (tomorrow.Count > 0)
            {
                bool holiday = tomorrow.Any(o => o.IsHoliday);
                string titles = string.Join("، ", tomorrow.Select(o => o.Title));
                SmartToast.Show(holiday ? "فردا تعطیل است" : "مناسبت فردا", titles + (tomorrow.Any(o => o.IsLunar) ? "\n" + PersianHolidays.LunarNote : ""), "", seconds: 14);
            }
        }

        // Azan notice (when «حالت نماز» is not doing it already).
        bool prayerModeAuto = data.Modes.Any(m => m.Triggers.BeforePrayer);
        if (r.PrayerNotice && !prayerModeAuto && PrayerClock.Next() is { } next)
        {
            string key = next.Name + next.AtCity.ToString("yyyyMMddHHmm");
            if (next.Remaining <= TimeSpan.FromMinutes(r.PrayerNoticeMinutes) && _lastPrayerNotice != key)
            {
                _lastPrayerNotice = key;
                var prayer = data.Modes.FirstOrDefault(m => m.Name == "حالت نماز");
                SmartToast.Show($"{PrayerClock.Duration(next.Remaining)} تا {next.Name}", $"اذان ساعت {PrayerClock.Clock(next.AtCity)} به وقت {WidgetCityData.ShortName(DesktopSettingsStore.Current.Widgets.City)}", "",
                    prayer != null ? "روشن کردن «حالت نماز»" : null,
                    prayer != null ? () => Activate(prayer.Name, "manual", DateTime.Now + next.Remaining + TimeSpan.FromMinutes(prayer.Triggers.PrayerDurationMinutes)) : null);
            }
        }
    }
}
