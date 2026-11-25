using System.Windows;
using Shamsa.Services;

namespace Shamsa.Recording;

/// <summary>Entry point used by the app shell: screen recording, screenshots, hotkeys and voice commands.</summary>
public static class RecorderFeature
{
    private static readonly ScreenRecorderEngine Engine = new();
    private static RecorderHotkeys? _hotkeys;
    private static VoiceCommandService? _voice;
    private static RecordingToolbar? _toolbar;
    private static ClickHighlightOverlay? _clickOverlay;
    private static RecorderWindow? _window;
    private static bool _initialized;
    private static bool _starting;
    private static PixelRect _activeRegion;

    /// <summary>Window chosen in the recorder window for "specific window" mode (session only).</summary>
    internal static IntPtr SelectedWindow { get; set; }

    internal static event Action? StatusChanged;
    internal static event Action<string>? Message;
    internal static string HotkeyStatus { get; private set; } = string.Empty;
    internal static string VoiceStatus { get; private set; } = string.Empty;

    internal static RecorderState State => Engine.State;
    internal static TimeSpan Elapsed => Engine.Elapsed;
    internal static long CurrentBytes => Engine.CurrentBytes;

    public static bool IsBusy => _starting || Engine.State != RecorderState.Idle;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        RecorderSettingsStore.Load();
        ScreenRecorderEngine.CleanupStaleTemp();
        Engine.StateChanged += _ => Dispatch(() => StatusChanged?.Invoke());
        Engine.Failed += msg => Dispatch(async () =>
        {
            Report(msg);
            if (Engine.State == RecorderState.Recording) await StopAsync();
        });
        ApplyHotkeys();
        if (RecorderSettingsStore.Current.VoiceCommandsEnabled) SetVoiceEnabled(true, persist: false);
    }

    public static void ShowWindow()
    {
        Initialize();
        if (_window == null || !_window.IsLoaded)
        {
            _window = new RecorderWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public static async void ToggleRecording()
    {
        Initialize();
        try
        {
            if (Engine.State is RecorderState.Recording or RecorderState.Paused) await StopAsync();
            else if (Engine.State == RecorderState.Idle && !_starting) await StartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Recorder toggle failed", ex);
            Report(ex.Message);
        }
    }

    public static async void PauseResume()
    {
        try
        {
            if (Engine.State == RecorderState.Recording) { await Engine.PauseAsync(); Report("ضبط متوقف موقت شد.", notify: false); }
            else if (Engine.State == RecorderState.Paused) await Engine.ResumeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Pause/resume failed", ex);
            Report(ex.Message);
        }
        _toolbar?.Refresh();
    }

    public static void TakeScreenshot() => TakeScreenshot(RecorderSettingsStore.Current.ScreenshotMode);

    internal static async void TakeScreenshot(ScreenshotMode mode)
    {
        Initialize();
        try
        {
            PixelRect? rect = mode switch
            {
                ScreenshotMode.Region => await RegionSelectorWindow.SelectAsync("ناحیه اسکرین‌شات را بکشید · Esc برای لغو"),
                ScreenshotMode.ActiveWindow => ForegroundWindowRect(),
                _ => MonitorUnderCursor()
            };
            if (rect is not { IsEmpty: false } r) return;
            r = ClampToDesktop(r);
            string path = ScreenshotService.Save(r, RecorderSettingsStore.Current);
            Report($"اسکرین‌شات ذخیره شد: {Path.GetFileName(path)}" + (RecorderSettingsStore.Current.ScreenshotToClipboard ? " (در کلیپ‌بورد هم کپی شد)" : ""), path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Screenshot failed", ex);
            Report("گرفتن اسکرین‌شات ناموفق بود: " + ex.Message);
        }
    }

    public static void Shutdown()
    {
        try
        {
            if (Engine.State != RecorderState.Idle)
            {
                // Finalize synchronously enough to keep the file playable.
                var t = Task.Run(() => Engine.StopAsync());
                t.Wait(TimeSpan.FromSeconds(25));
                if (t.IsCompletedSuccessfully && t.Result is { } path) RecorderSettingsStore.AddRecent(path);
            }
        }
        catch (Exception ex) { AppLog.Warn("Recorder shutdown finalize failed: " + ex.Message); }
        CloseRecordingUi();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _voice?.Dispose();
        _voice = null;
    }

    // ------------------------------------------------------------------ internals

    internal static string ApplyHotkeys()
    {
        _hotkeys ??= CreateHotkeys();
        HotkeyStatus = _hotkeys.RegisterAll(RecorderSettingsStore.Current);
        StatusChanged?.Invoke();
        return HotkeyStatus;
    }

    internal static void SuspendHotkeys() => _hotkeys?.UnregisterAll();

    private static RecorderHotkeys CreateHotkeys()
    {
        var h = new RecorderHotkeys();
        h.Pressed += key => Dispatch(() =>
        {
            switch (key)
            {
                case RecorderHotkey.Record: ToggleRecording(); break;
                case RecorderHotkey.Pause: PauseResume(); break;
                case RecorderHotkey.Screenshot: TakeScreenshot(); break;
            }
        });
        return h;
    }

    /// <summary>Turns voice commands on/off. Returns a Persian error, or null when it worked.</summary>
    internal static string? SetVoiceEnabled(bool enabled, bool persist = true)
    {
        string? error = null;
        if (enabled)
        {
            _voice ??= CreateVoice();
            error = _voice.Start();
            VoiceStatus = error ?? $"در حال گوش دادن · {_voice.RecognizerName}";
        }
        else
        {
            _voice?.Stop();
            VoiceStatus = "خاموش";
        }
        bool effective = enabled && error == null;
        if (persist || (!effective && RecorderSettingsStore.Current.VoiceCommandsEnabled))
            RecorderSettingsStore.Update(s => s.VoiceCommandsEnabled = effective);
        StatusChanged?.Invoke();
        return error;
    }

    private static VoiceCommandService CreateVoice()
    {
        var v = new VoiceCommandService();
        v.Recognized += (cmd, text) => Dispatch(() =>
        {
            Message?.Invoke($"🎙 فرمان صوتی: «{text}»");
            switch (cmd)
            {
                case VoiceCommand.StartRecording when Engine.State == RecorderState.Idle: ToggleRecording(); break;
                case VoiceCommand.StopRecording when Engine.State is RecorderState.Recording or RecorderState.Paused: ToggleRecording(); break;
                case VoiceCommand.PauseRecording when Engine.State == RecorderState.Recording: PauseResume(); break;
                case VoiceCommand.ResumeRecording when Engine.State == RecorderState.Paused: PauseResume(); break;
                case VoiceCommand.Screenshot: TakeScreenshot(); break;
            }
        });
        return v;
    }

    private static async Task StartAsync()
    {
        var s = RecorderSettingsStore.Current;
        if (FfmpegManager.GetFfmpegPath(refresh: true) == null)
        {
            Report("برای ضبط صفحه به FFmpeg نیاز است. از پنجره ضبط صفحه دکمه نصب FFmpeg را بزنید.");
            ShowWindow();
            return;
        }
        _starting = true;
        StatusChanged?.Invoke();
        try
        {
            PixelRect? region = await ResolveRegionAsync(s);
            if (region is not { IsEmpty: false } r) return;
            r = ClampToDesktop(r).Even();
            if (r.Width < 16 || r.Height < 16) { Report("ناحیه ضبط خیلی کوچک است."); return; }
            _activeRegion = r;

            bool minimizeWindow = _window != null && _window.IsVisible && s.Mode != CaptureMode.Window;
            if (minimizeWindow) _window!.WindowState = WindowState.Minimized;
            if (s.Countdown) await CountdownWindow.RunAsync(r);

            string folder = s.EffectiveOutputFolder;
            Directory.CreateDirectory(folder);
            string output = ScreenshotService.UniquePath(Path.Combine(folder, $"Shamsa_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{s.Container}"));
            var job = new RecordingJob(r, s.Fps, s.Quality, s.Encoder, s.Container, s.DrawMouse,
                s.UseDesktopDuplication, DdaOutputFor(r), s.RecordSystemAudio, s.RecordMicrophone, output);

            if (s.HighlightClicks) { _clickOverlay = new ClickHighlightOverlay(); _clickOverlay.Show(); }
            try { await Engine.StartAsync(job); }
            catch
            {
                CloseRecordingUi();
                if (minimizeWindow && _window != null) _window.WindowState = WindowState.Normal;
                throw;
            }
            if (!string.IsNullOrEmpty(Engine.Notes)) Report(Engine.Notes, notify: false);
            if (s.ShowToolbar)
            {
                _toolbar = new RecordingToolbar(() => (Engine.State, Engine.Elapsed, Engine.CurrentBytes), PauseResume, ToggleRecording, TakeScreenshot);
                _toolbar.Show();
                _toolbar.PlaceFor(r);
            }
            AppLog.Info($"Recording started: {r} → {output}");
        }
        finally
        {
            _starting = false;
            StatusChanged?.Invoke();
        }
    }

    private static async Task StopAsync()
    {
        CloseRecordingUi();
        Report("در حال ذخیره ضبط…", notify: false);
        var progress = new Progress<string>(m => Message?.Invoke(m));
        string? path = await Engine.StopAsync(progress);
        if (path != null)
        {
            RecorderSettingsStore.AddRecent(path);
            string extra = string.IsNullOrEmpty(Engine.Notes) ? "" : " " + Engine.Notes;
            Report($"ضبط ذخیره شد: {Path.GetFileName(path)}{extra}", path);
        }
        if (_window != null && _window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        StatusChanged?.Invoke();
    }

    private static void CloseRecordingUi()
    {
        try { _toolbar?.Close(); } catch { }
        try { _clickOverlay?.Close(); } catch { }
        _toolbar = null;
        _clickOverlay = null;
    }

    private static async Task<PixelRect?> ResolveRegionAsync(RecorderSettings s)
    {
        switch (s.Mode)
        {
            case CaptureMode.Region:
            {
                var r = await RegionSelectorWindow.SelectAsync("ناحیه ضبط را بکشید · Esc برای لغو");
                if (r is { } sel) RecorderSettingsStore.Update(x => { x.LastRegionX = sel.X; x.LastRegionY = sel.Y; x.LastRegionW = sel.Width; x.LastRegionH = sel.Height; });
                return r;
            }
            case CaptureMode.LastRegion:
                if (s.LastRegionW >= 16 && s.LastRegionH >= 16) return new PixelRect(s.LastRegionX, s.LastRegionY, s.LastRegionW, s.LastRegionH);
                goto case CaptureMode.Region;
            case CaptureMode.Window:
            {
                IntPtr h = SelectedWindow;
                if (!RecorderNative.IsWindowAlive(h))
                {
                    var fg = ForegroundWindowRect();
                    if (fg == null) Report("پنجره‌ای برای ضبط انتخاب نشده است.");
                    return fg;
                }
                RecorderNative.SetForegroundWindow(h);
                await Task.Delay(150);
                return RecorderNative.GetWindowBounds(h);
            }
            default:
            {
                var mons = RecorderNative.GetMonitors();
                if (mons.Count == 0) return RecorderNative.VirtualScreen();
                return mons[Math.Clamp(s.MonitorIndex, 0, mons.Count - 1)].Bounds;
            }
        }
    }

    /// <summary>Desktop Duplication output index: used only when the region lies on the primary display.</summary>
    private static int? DdaOutputFor(PixelRect r)
    {
        var mons = RecorderNative.GetMonitors();
        var primary = mons.FirstOrDefault(m => m.Primary);
        if (primary == null) return null;
        if (mons.Count == 1) return 0;
        return primary.Bounds.Intersect(r) == r ? 0 : null;
    }

    private static PixelRect? ForegroundWindowRect()
    {
        IntPtr fg = RecorderNative.GetForegroundWindow();
        if (fg == IntPtr.Zero || NativeMethods.IsOwnProcessWindow(fg)) return null;
        var r = RecorderNative.GetWindowBounds(fg);
        return r.IsEmpty ? null : r;
    }

    private static PixelRect MonitorUnderCursor()
    {
        var (x, y) = RecorderNative.CursorPixels();
        var mons = RecorderNative.GetMonitors();
        return mons.FirstOrDefault(m => x >= m.Bounds.X && x < m.Bounds.Right && y >= m.Bounds.Y && y < m.Bounds.Bottom)?.Bounds
            ?? mons.FirstOrDefault()?.Bounds ?? RecorderNative.VirtualScreen();
    }

    private static PixelRect ClampToDesktop(PixelRect r)
    {
        var clamped = r.Intersect(RecorderNative.VirtualScreen());
        return clamped.IsEmpty ? r : clamped;
    }

    private static void Report(string message, string? reveal = null, bool notify = true)
    {
        Message?.Invoke(message);
        if (notify) Notifier.Show(message, reveal);
    }

    private static void Dispatch(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
