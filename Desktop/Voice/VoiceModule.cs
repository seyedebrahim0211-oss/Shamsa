using System.Windows;
using System.Windows.Threading;
using Shamsa.Desktop.Launcher;
using Shamsa.Services;

namespace Shamsa.Desktop.Voice;

/// <summary>
/// «فرمان صوتی فارسی»: always-listening command grammar ("شمسا باز کن کروم", "شمسا چیدمان ستونی" …).
/// Off by default; the microphone is opened only while the module runs and released in <see cref="Stop"/>.
/// </summary>
public sealed class VoiceModule : IDesktopModule
{
    private const double MinConfidence = 0.6;
    private IVoiceEngine? _engine;
    private List<VoiceCommand> _commands = new();
    private DesktopSettings? _settings;
    private LauncherIndex? _ownIndex;
    private DispatcherTimer? _rebuildTimer;
    private string _signature = "";
    private DateTime _lastCommandUtc = DateTime.MinValue;
    private int _generation;

    /// <summary>Human-readable description of the engine in use (or why there is none).</summary>
    public static string ActiveEngineDescription { get; private set; } = "خاموش";

    /// <summary>Phrases of the running grammar (for the studio cheat-sheet).</summary>
    public static IReadOnlyList<VoiceCommand> ActiveCommands { get; private set; } = Array.Empty<VoiceCommand>();

    public static event Action? StatusChanged;

    public string Id => "voice";
    public string DisplayName => "فرمان صوتی فارسی";
    public bool IsRunning => _engine != null;

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Enabled || !settings.Voice.Enabled) { Stop(); return; }
        _settings = settings;
        string sig = $"{settings.Voice.WakeWord}|{settings.Voice.PreferPersian}|{string.Join(",", settings.Layouts.Select(l => l.Name))}";
        if (_engine != null && sig == _signature) return;
        _signature = sig;
        _ = StartAsync();
    }

    private LauncherIndex Index()
    {
        var shared = LauncherModule.SharedIndex;
        if (shared != null) return shared;
        if (_ownIndex == null)
        {
            _ownIndex = new LauncherIndex();
            _ownIndex.Warm(false);
            _ownIndex.Updated += ScheduleRebuild;
        }
        return _ownIndex;
    }

    private void ScheduleRebuild()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _rebuildTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _rebuildTimer.Tick -= OnRebuild;
            _rebuildTimer.Tick += OnRebuild;
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        });
    }

    private void OnRebuild(object? sender, EventArgs e)
    {
        _rebuildTimer?.Stop();
        if (_settings != null) _ = StartAsync();
    }

    private async Task StartAsync()
    {
        int gen = ++_generation;
        var s = _settings;
        if (s == null) return;
        DisposeEngine();
        try
        {
            var engine = VoiceEngineFactory.Create(s.Voice.PreferPersian);
            if (engine == null)
            {
                SetStatus("هیچ موتور تشخیص گفتار روی ویندوز نصب نیست (تنظیمات › زمان و زبان › گفتار)");
                return;
            }
            var index = Index();
            var commands = VoiceCommands.Build(engine.Language, s.Voice.WakeWord, index.Apps, s.Layouts.Select(l => l.Name));
            engine.Heard += h => Application.Current?.Dispatcher.BeginInvoke(() => OnHeard(h));
            await engine.StartAsync(commands.Select(c => c.Phrase).ToList());
            if (gen != _generation || _settings == null) { engine.Dispose(); return; }
            _engine = engine;
            _commands = commands;
            ActiveCommands = commands;
            SetStatus(engine.Description);
            AppLog.Info($"Desktop voice started: {engine.Language}, {commands.Count} phrases");
            // The app index may still be filling: rebuild once it settles.
            if (!index.IsReady && index == _ownIndex) ScheduleRebuild();
            else if (!index.IsReady) { index.Updated -= ScheduleRebuild; index.Updated += ScheduleRebuild; }
        }
        catch (Exception ex)
        {
            AppLog.Error("Desktop voice failed to start", ex);
            SetStatus("میکروفون یا دسترسی گفتار در دسترس نیست: " + ex.Message);
            DisposeEngine();
        }
    }

    private void OnHeard(VoiceHeard heard)
    {
        if (_settings == null) return;
        if (heard.Confidence < MinConfidence)
        {
            if (_settings.Voice.Feedback && heard.Confidence > 0.35) VoiceToast.Show("متوجه نشدم: " + heard.Text, understood: false);
            return;
        }
        if (DateTime.UtcNow - _lastCommandUtc < TimeSpan.FromSeconds(1.2)) return; // echo / double fire
        string text = VoiceCommands.Clean(heard.Text, _engine?.Language ?? VoiceLanguage.Finglish);
        var cmd = _commands.FirstOrDefault(c => c.Phrase == text);
        if (cmd == null) return;
        _lastCommandUtc = DateTime.UtcNow;
        if (_settings.Voice.Feedback) VoiceToast.Show($"«{heard.Text}» — {cmd.Description}", understood: true);
        try { cmd.Run(); }
        catch (Exception ex) { AppLog.Warn("Voice command failed: " + ex.Message); }
    }

    private static void SetStatus(string text)
    {
        ActiveEngineDescription = text;
        StatusChanged?.Invoke();
    }

    private void DisposeEngine()
    {
        var e = _engine;
        _engine = null;
        try { e?.Dispose(); } catch { }
    }

    public void Stop()
    {
        _generation++;
        _rebuildTimer?.Stop();
        DisposeEngine();
        if (_ownIndex != null) { _ownIndex.Updated -= ScheduleRebuild; _ownIndex.Dispose(); _ownIndex = null; }
        var shared = LauncherModule.SharedIndex;
        if (shared != null) shared.Updated -= ScheduleRebuild;
        _settings = null;
        _signature = "";
        _commands = new();
        ActiveCommands = Array.Empty<VoiceCommand>();
        VoiceToast.Close();
        SetStatus("خاموش");
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) => null;

    /// <summary>
    /// Studio "test" button: listens once for up to <paramref name="seconds"/> with the same engine
    /// choice and grammar and returns what was heard (never runs the command).
    /// </summary>
    public static async Task<string> TestListenAsync(DesktopSettings s, int seconds = 5)
    {
        IVoiceEngine? engine = null;
        LauncherIndex? temp = null;
        try
        {
            engine = VoiceEngineFactory.Create(s.Voice.PreferPersian);
            if (engine == null) return "هیچ موتور تشخیص گفتار نصب نیست.";
            var index = LauncherModule.SharedIndex;
            if (index == null) { temp = new LauncherIndex(); temp.Warm(false); await Task.WhenAny(temp.WhenIndexedAsync(), Task.Delay(4000)); index = temp; }
            var phrases = VoiceCommands.Build(engine.Language, s.Voice.WakeWord, index.Apps, s.Layouts.Select(l => l.Name)).Select(c => c.Phrase).ToList();
            var heard = await engine.ListenOnceAsync(phrases, TimeSpan.FromSeconds(seconds));
            if (heard == null) return "چیزی شناخته نشد. نزدیک‌تر به میکروفون و با «" + (engine.Language == VoiceLanguage.Persian ? "شمسا" : "shamsa") + "» شروع کن.";
            return $"شنیده شد: «{heard.Text}» — اطمینان {DesktopTheme.Digits(((int)(heard.Confidence * 100)).ToString(), true)}٪"
                   + (heard.Confidence < MinConfidence ? " (کمتر از حد اجرا)" : "");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Voice test failed: " + ex.Message);
            return "آزمایش ناموفق بود: میکروفون یا دسترسی گفتار ویندوز را بررسی کن.";
        }
        finally
        {
            engine?.Dispose();
            temp?.Dispose();
        }
    }
}
