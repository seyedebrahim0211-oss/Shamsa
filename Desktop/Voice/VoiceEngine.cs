using System.Speech.Recognition;
using Shamsa.Services;
using WinSpeech = Windows.Media.SpeechRecognition;

namespace Shamsa.Desktop.Voice;

/// <summary>One recognised utterance.</summary>
public sealed record VoiceHeard(string Text, double Confidence);

/// <summary>Common shape of the three recogniser back-ends.</summary>
internal interface IVoiceEngine : IDisposable
{
    VoiceLanguage Language { get; }
    string Description { get; }
    event Action<VoiceHeard>? Heard;
    Task StartAsync(IReadOnlyList<string> phrases);
    /// <summary>Listens once (up to <paramref name="timeout"/>) and returns the phrase heard, or null.</summary>
    Task<VoiceHeard?> ListenOnceAsync(IReadOnlyList<string> phrases, TimeSpan timeout);
}

internal static class VoiceEngineFactory
{
    /// <summary>
    /// Picks the best available engine: an installed Persian SAPI recogniser, then the Windows
    /// (WinRT) recogniser for fa-IR, then an English recogniser with Finglish phrases.
    /// Returns null when the machine has no speech recogniser at all.
    /// </summary>
    public static IVoiceEngine? Create(bool preferPersian)
    {
        if (preferPersian)
        {
            try
            {
                var fa = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "fa");
                if (fa != null) return new SapiEngine(fa, VoiceLanguage.Persian);
            }
            catch (Exception ex) { AppLog.Warn("SAPI recogniser query failed: " + ex.Message); }
            try
            {
                var lang = WinSpeech.SpeechRecognizer.SupportedGrammarLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("fa", StringComparison.OrdinalIgnoreCase));
                if (lang != null) return new WinRtEngine(lang);
            }
            catch (Exception ex) { AppLog.Warn("Windows speech languages query failed: " + ex.Message); }
        }
        try
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            var en = all.FirstOrDefault(r => r.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                     ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                     ?? all.FirstOrDefault();
            if (en != null) return new SapiEngine(en, VoiceLanguage.Finglish);
        }
        catch (Exception ex) { AppLog.Warn("SAPI recogniser query failed: " + ex.Message); }
        return null;
    }
}

/// <summary>System.Speech (SAPI desktop recogniser) with a fixed phrase grammar.</summary>
internal sealed class SapiEngine : IVoiceEngine
{
    private readonly RecognizerInfo _info;
    private SpeechRecognitionEngine? _engine;

    public SapiEngine(RecognizerInfo info, VoiceLanguage lang) { _info = info; Language = lang; }

    public VoiceLanguage Language { get; }
    public string Description => Language == VoiceLanguage.Persian
        ? $"تشخیص گفتار فارسی ویندوز ({_info.Culture.Name})"
        : $"تشخیص گفتار انگلیسی با عبارت‌های فینگلیش ({_info.Culture.Name}) — بسته گفتار فارسی روی ویندوز نصب نیست";
    public event Action<VoiceHeard>? Heard;

    private SpeechRecognitionEngine CreateEngine(IReadOnlyList<string> phrases)
    {
        var engine = new SpeechRecognitionEngine(_info);
        var builder = new GrammarBuilder(new Choices(phrases.ToArray())) { Culture = _info.Culture };
        engine.LoadGrammar(new Grammar(builder) { Name = "ShamsaDesktopVoice" });
        engine.SetInputToDefaultAudioDevice();
        return engine;
    }

    public Task StartAsync(IReadOnlyList<string> phrases)
    {
        Dispose();
        if (phrases.Count == 0) return Task.CompletedTask;
        var engine = CreateEngine(phrases);
        engine.SpeechRecognized += (_, e) =>
        {
            if (e.Result != null) Heard?.Invoke(new VoiceHeard(e.Result.Text, e.Result.Confidence));
        };
        engine.RecognizeAsync(RecognizeMode.Multiple);
        _engine = engine;
        return Task.CompletedTask;
    }

    public Task<VoiceHeard?> ListenOnceAsync(IReadOnlyList<string> phrases, TimeSpan timeout) => Task.Run(() =>
    {
        if (phrases.Count == 0) return (VoiceHeard?)null;
        using var engine = CreateEngine(phrases);
        engine.InitialSilenceTimeout = timeout;
        engine.BabbleTimeout = timeout;
        var r = engine.Recognize(timeout);
        return r == null ? null : new VoiceHeard(r.Text, r.Confidence);
    });

    public void Dispose()
    {
        var e = _engine;
        _engine = null;
        if (e == null) return;
        try { e.RecognizeAsyncCancel(); } catch { }
        try { e.SetInputToNull(); } catch { }
        try { e.Dispose(); } catch { }
    }
}

/// <summary>Windows.Media.SpeechRecognition (WinRT) with a list constraint — used for fa-IR when available.</summary>
internal sealed class WinRtEngine : IVoiceEngine
{
    private readonly Windows.Globalization.Language _lang;
    private WinSpeech.SpeechRecognizer? _recognizer;

    public WinRtEngine(Windows.Globalization.Language lang) { _lang = lang; }

    public VoiceLanguage Language => VoiceLanguage.Persian;
    public string Description => $"تشخیص گفتار فارسی ویندوز (WinRT، {_lang.LanguageTag})";
    public event Action<VoiceHeard>? Heard;

    private async Task<WinSpeech.SpeechRecognizer> CreateAsync(IReadOnlyList<string> phrases)
    {
        var rec = new WinSpeech.SpeechRecognizer(_lang);
        rec.Constraints.Add(new WinSpeech.SpeechRecognitionListConstraint(phrases, "negar"));
        var compiled = await rec.CompileConstraintsAsync();
        if (compiled.Status != WinSpeech.SpeechRecognitionResultStatus.Success)
        {
            rec.Dispose();
            throw new InvalidOperationException("Speech constraints failed: " + compiled.Status);
        }
        return rec;
    }

    public async Task StartAsync(IReadOnlyList<string> phrases)
    {
        Dispose();
        if (phrases.Count == 0) return;
        var rec = await CreateAsync(phrases);
        rec.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
        {
            var r = args.Result;
            if (r.Status == WinSpeech.SpeechRecognitionResultStatus.Success && !string.IsNullOrEmpty(r.Text))
                Heard?.Invoke(new VoiceHeard(r.Text, r.RawConfidence));
        };
        await rec.ContinuousRecognitionSession.StartAsync();
        _recognizer = rec;
    }

    public async Task<VoiceHeard?> ListenOnceAsync(IReadOnlyList<string> phrases, TimeSpan timeout)
    {
        if (phrases.Count == 0) return null;
        using var rec = await CreateAsync(phrases);
        rec.Timeouts.InitialSilenceTimeout = timeout;
        rec.Timeouts.BabbleTimeout = timeout;
        var r = await rec.RecognizeAsync();
        return r.Status == WinSpeech.SpeechRecognitionResultStatus.Success && !string.IsNullOrEmpty(r.Text)
            ? new VoiceHeard(r.Text, r.RawConfidence) : null;
    }

    public void Dispose()
    {
        var rec = _recognizer;
        _recognizer = null;
        if (rec == null) return;
        try { _ = rec.ContinuousRecognitionSession.CancelAsync(); } catch { }
        try { rec.Dispose(); } catch { }
    }
}
