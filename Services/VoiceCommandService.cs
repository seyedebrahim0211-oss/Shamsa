#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Speech.Recognition;

namespace Shamsa.Services;

public enum VoiceActionKind
{
    Unknown,
    StartRecording,
    StopRecording,
    TakeScreenshot,
    SwitchWorkspace,
    ApplyTwoColumnLayout,
    ToggleDesktopIcons,
    OpenSpotlight
}

public sealed record RecognizedVoiceCommand(
    string RawText,
    VoiceActionKind Action,
    string? Argument,
    float Confidence);

/// <summary>
/// Offline Persian & English Voice Command Engine for Shamsa.
/// Supports hands-free screen recording, screenshots, workspace switching, and layout changes.
/// Falls back cleanly with a clear status message if no Windows Speech recognizer is installed.
/// </summary>
public sealed class VoiceCommandService : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    public bool IsListening { get; private set; }
    public string StatusMessage { get; private set; } = "غیرفعال";

    public event Action<RecognizedVoiceCommand>? CommandRecognized;

    private static readonly Dictionary<string, (VoiceActionKind Action, string? Arg)> BuiltInPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Persian commands
        ["شروع ضبط"] = (VoiceActionKind.StartRecording, null),
        ["ضبط صفحه"] = (VoiceActionKind.StartRecording, null),
        ["توقف ضبط"] = (VoiceActionKind.StopRecording, null),
        ["پایان ضبط"] = (VoiceActionKind.StopRecording, null),
        ["اسکرین شات"] = (VoiceActionKind.TakeScreenshot, null),
        ["عکس از صفحه"] = (VoiceActionKind.TakeScreenshot, null),
        ["حالت کار"] = (VoiceActionKind.SwitchWorkspace, "حالت کار"),
        ["حالت طراحی"] = (VoiceActionKind.SwitchWorkspace, "میز کار طراحی"),
        ["حالت برنامه نویسی"] = (VoiceActionKind.SwitchWorkspace, "میز کار برنامه‌نویسی"),
        ["حالت بازی"] = (VoiceActionKind.SwitchWorkspace, "میز کار بازی"),
        ["چیدمان دو ستونه"] = (VoiceActionKind.ApplyTwoColumnLayout, null),
        ["مخفی کردن آیکون ها"] = (VoiceActionKind.ToggleDesktopIcons, null),
        ["جستجوی سریع"] = (VoiceActionKind.OpenSpotlight, null),

        // English fallback commands (always available on Windows English SAPI)
        ["start recording"] = (VoiceActionKind.StartRecording, null),
        ["stop recording"] = (VoiceActionKind.StopRecording, null),
        ["take screenshot"] = (VoiceActionKind.TakeScreenshot, null),
        ["work mode"] = (VoiceActionKind.SwitchWorkspace, "حالت کار"),
        ["design mode"] = (VoiceActionKind.SwitchWorkspace, "میز کار طراحی"),
        ["split screen"] = (VoiceActionKind.ApplyTwoColumnLayout, null),
        ["quick search"] = (VoiceActionKind.OpenSpotlight, null)
    };

    public bool StartListening(string preferredCulture = "fa-IR")
    {
        StopListening();
        try
        {
            RecognizerInfo? selected = null;
            foreach (var info in SpeechRecognitionEngine.InstalledRecognizers())
            {
                if (info.Culture.Name.Equals(preferredCulture, StringComparison.OrdinalIgnoreCase))
                {
                    selected = info;
                    break;
                }
            }
            selected ??= SpeechRecognitionEngine.InstalledRecognizers().Count > 0
                ? SpeechRecognitionEngine.InstalledRecognizers()[0]
                : null;

            if (selected == null)
            {
                StatusMessage = "موتور تشخیص گفتار ویندوز نصب نیست (ناموجود).";
                return false;
            }

            _engine = new SpeechRecognitionEngine(selected);
            var choices = new Choices();
            foreach (var phrase in BuiltInPhrases.Keys)
                choices.Add(phrase);

            var gb = new GrammarBuilder(choices) { Culture = selected.Culture };
            _engine.LoadGrammar(new Grammar(gb));
            _engine.SpeechRecognized += OnSpeechRecognized;
            _engine.SetInputToDefaultAudioDevice();
            _engine.RecognizeAsync(RecognizeMode.Multiple);

            IsListening = true;
            StatusMessage = $"گوش‌به‌زنگ فرمان صوتی ({selected.Culture.DisplayName})";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"فرمان صوتی در دسترس نیست: {ex.Message}";
            IsListening = false;
            return false;
        }
    }

    public void StopListening()
    {
        if (_engine == null) return;
        try
        {
            _engine.SpeechRecognized -= OnSpeechRecognized;
            _engine.RecognizeAsyncCancel();
            _engine.Dispose();
        }
        catch { }
        finally
        {
            _engine = null;
            IsListening = false;
            StatusMessage = "غیرفعال";
        }
    }

    /// <summary>
    /// Allows external Persian STT engines (e.g., local Whisper/Vosk bridge) to dispatch transcribed text.
    /// </summary>
    public void DispatchExternalTranscript(string transcript, float confidence = 0.95f)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return;
        string clean = transcript.Trim();
        foreach (var kv in BuiltInPhrases)
        {
            if (clean.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                CommandRecognized?.Invoke(new RecognizedVoiceCommand(clean, kv.Value.Action, kv.Value.Arg, confidence));
                return;
            }
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result == null || e.Result.Confidence < 0.55f) return;
        if (BuiltInPhrases.TryGetValue(e.Result.Text, out var mapped))
        {
            CommandRecognized?.Invoke(new RecognizedVoiceCommand(
                e.Result.Text,
                mapped.Action,
                mapped.Arg,
                e.Result.Confidence));
        }
    }

    public void Dispose() => StopListening();
}
