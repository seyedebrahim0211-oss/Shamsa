using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NAudio.Wave;
using Shamsa.Desktop.Launcher;

namespace Shamsa.Desktop.Voice;

/// <summary>Microphone lifetime is scoped to one physical press. Recognition reads only the captured buffer.</summary>
public sealed class VoiceModule : IDesktopModule
{
    public string Id => "voice";
    public string DisplayName => "فرمان صوتی فارسی";
    public bool IsRunning => _microphone != null;
    private static WaveInEvent? _microphone;
    private static int _generation;
    public void Apply(DesktopSettings settings) { }
    public void Stop() => Cancel();
    public Window? CreatePreviewWindow(DesktopSettings settings) => null;

    private static void Cancel()
    {
        _generation++;
        var device = _microphone;
        _microphone = null;
        try { device?.StopRecording(); } catch { }
        finally { try { device?.Dispose(); } catch { } }
    }

    public static Button HoldButton(Func<DesktopSettings> settings, Action<string> status)
    {
        var button = new Button { Content = "🎙 نگه دارید و فرمان بگویید", Padding = new Thickness(12, 7, 12, 7),
            ToolTip = "ماوس یا کلید فاصله را نگه دارید؛ با رها کردن، میکروفون بسته می‌شود." };
        MemoryStream? samples = null;
        int session = -1;
        bool down = false;
        var limit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        VoiceLanguage language = VoiceLanguage.Persian;

        void Begin()
        {
            if (down) return;
            Cancel();
            session = _generation;
            language = settings().Voice.PreferPersian ? VoiceLanguage.Persian : VoiceLanguage.Finglish;
            if (language == VoiceLanguage.Persian && !VoiceEngine.Installed)
            { status("ابتدا بستهٔ آفلاین فارسی را از صفحهٔ فرمان صوتی نصب کنید."); return; }
            try
            {
                var buffer = new MemoryStream();
                samples = buffer;
                _microphone = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 40 };
                _microphone.DataAvailable += (_, e) =>
                {
                    lock (buffer)
                        if (buffer.Length + e.BytesRecorded <= 16000 * 2 * 30) buffer.Write(e.Buffer, 0, e.BytesRecorded);
                };
                _microphone.StartRecording();
                down = true;
                limit.Start();
                button.Content = "در حال شنیدن… رها کنید";
                status("میکروفون روشن است");
            }
            catch { Cancel(); status("دسترسی میکروفون را در تنظیمات حریم خصوصی ویندوز بررسی کنید."); }
        }

        async void End(bool recognize)
        {
            limit.Stop();
            if (!down) { if (!recognize && session == _generation) Cancel(); return; }
            down = false;
            button.Content = "🎙 نگه دارید و فرمان بگویید";
            if (session != _generation) return;
            Cancel();
            int token = session = _generation;
            byte[] audio;
            lock (samples!) audio = samples.ToArray();
            samples = null;
            status("میکروفون بسته شد");
            if (!recognize || audio.Length < 1600) return;
            try
            {
                var s = settings();
                var commands = VoiceCommands.Build(language, s.Voice.WakeWord,
                    LauncherModule.SharedIndex?.Apps ?? Array.Empty<LauncherResult>(), s.Layouts.Select(x => x.Name));
                string phrase = await Task.Run(() => VoiceEngine.Decode(audio, language, commands));
                if (token != _generation || !button.IsLoaded) return;
                var match = commands.FirstOrDefault(c => c.Phrase == VoiceCommands.Clean(phrase, language));
                status(match == null ? "فرمان شناخته نشد. " + phrase : match.Description);
                match?.Run();
            }
            catch (Exception ex) { if (token == _generation) status("تشخیص گفتار در دسترس نیست. " + ex.Message); }
        }

        limit.Tick += (_, _) => End(false);
        button.PreviewMouseLeftButtonDown += (_, e) => { button.Focus(); if (button.CaptureMouse()) Begin(); e.Handled = true; };
        button.PreviewMouseLeftButtonUp += (_, e) => { End(true); button.ReleaseMouseCapture(); e.Handled = true; };
        button.LostMouseCapture += (_, _) => { if (down) End(false); };
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) { if (!e.IsRepeat) Begin(); e.Handled = true; } };
        button.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) { End(true); e.Handled = true; } };
        button.LostKeyboardFocus += (_, _) => End(false);
        Window? owner = null;
        void Deactivated(object? sender, EventArgs e) => End(false);
        button.Loaded += (_, _) => { owner = Window.GetWindow(button); if (owner != null) owner.Deactivated += Deactivated; };
        button.Unloaded += (_, _) => { End(false); if (owner != null) owner.Deactivated -= Deactivated; };
        return button;
    }
}
