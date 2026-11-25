using System.Globalization;
using System.Speech.Recognition;
using Shamsa.Services;

namespace Shamsa.Recording;

public enum VoiceCommand { StartRecording, StopRecording, PauseRecording, ResumeRecording, Screenshot }

/// <summary>
/// Offline voice commands through the Windows desktop speech recogniser (System.Speech).
/// Windows ships no Persian recogniser, so a small English grammar is used.
/// </summary>
internal sealed class VoiceCommandService : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private DateTime _lastCommandAt = DateTime.MinValue;
    private const float MinConfidence = 0.70f;

    public event Action<VoiceCommand, string>? Recognized;
    public bool IsRunning => _engine != null;
    public string? RecognizerName { get; private set; }

    public static readonly (string Phrase, VoiceCommand Command)[] Phrases =
    {
        ("start recording", VoiceCommand.StartRecording),
        ("shamsa record", VoiceCommand.StartRecording),
        ("stop recording", VoiceCommand.StopRecording),
        ("shamsa stop", VoiceCommand.StopRecording),
        ("pause recording", VoiceCommand.PauseRecording),
        ("resume recording", VoiceCommand.ResumeRecording),
        ("take screenshot", VoiceCommand.Screenshot),
        ("take a screenshot", VoiceCommand.Screenshot),
        ("shamsa screenshot", VoiceCommand.Screenshot),
    };

    public static string NoRecognizerMessage =>
        "هیچ موتور تشخیص گفتار ویندوز روی این سیستم نصب نیست (ویندوز موتور فارسی ندارد). " +
        "از Settings › Time & language › Language & region یک زبان English (United States) با قابلیت «Speech recognition» اضافه کنید، سپس دوباره تلاش کنید.";

    /// <summary>Returns an installed recogniser, preferring English (en-US, then any en-*).</summary>
    public static RecognizerInfo? FindRecognizer()
    {
        try
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            return all.FirstOrDefault(r => r.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Speech recogniser enumeration failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Starts listening; returns a Persian error message, or null on success.</summary>
    public string? Start()
    {
        if (_engine != null) return null;
        var info = FindRecognizer();
        if (info == null) return NoRecognizerMessage;
        try
        {
            var engine = new SpeechRecognitionEngine(info);
            var choices = new Choices(Phrases.Select(p => p.Phrase).ToArray());
            var builder = new GrammarBuilder(choices) { Culture = info.Culture };
            engine.LoadGrammar(new Grammar(builder) { Name = "ShamsaRecorder" });
            engine.SpeechRecognized += OnRecognized;
            try { engine.SetInputToDefaultAudioDevice(); }
            catch (Exception ex)
            {
                engine.Dispose();
                AppLog.Warn("No microphone for voice commands: " + ex.Message);
                return "میکروفونی برای فرمان صوتی پیدا نشد. یک میکروفون وصل کنید و دسترسی میکروفون ویندوز را برای برنامه‌های دسکتاپ فعال کنید.";
            }
            engine.RecognizeAsync(RecognizeMode.Multiple);
            _engine = engine;
            RecognizerName = $"{info.Description} ({info.Culture.Name})";
            AppLog.Info("Voice commands started with " + RecognizerName);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Voice command start failed", ex);
            return "راه‌اندازی فرمان صوتی ناموفق بود: " + ex.Message;
        }
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result == null || e.Result.Confidence < MinConfidence) return;
        if (DateTime.UtcNow - _lastCommandAt < TimeSpan.FromSeconds(1.5)) return; // debounce echoes
        string text = e.Result.Text.ToLower(CultureInfo.InvariantCulture);
        foreach (var (phrase, cmd) in Phrases)
        {
            if (phrase != text) continue;
            _lastCommandAt = DateTime.UtcNow;
            AppLog.Info($"Voice command: {text} ({e.Result.Confidence:0.00})");
            Recognized?.Invoke(cmd, text);
            return;
        }
    }

    public void Stop()
    {
        var engine = _engine;
        _engine = null;
        if (engine == null) return;
        try
        {
            engine.SpeechRecognized -= OnRecognized;
            engine.RecognizeAsyncCancel();
        }
        catch { }
        try { engine.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
