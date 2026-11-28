using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using Shamsa.Desktop.Launcher;
using Shamsa.Services;

namespace Shamsa.Desktop.Voice;

public sealed class VoiceModule : IDesktopModule
{
    internal const string Shortcut = "voice.talk";
    public string Id => "voice";
    public string DisplayName => "فرمان صوتی فارسی";
    public bool IsRunning => _registered;
    private bool _registered;
    private HotkeyBinding? _binding;
    private static Recording? _active;
    private static long _generation;
    private readonly object _shortcutOwner = new();

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Voice.Enabled) { DisableShortcut(); return; }
        var binding = new HotkeyBinding(Shortcut, DisplayName, "شروع و پایان گفتار", settings.Voice.HotkeyModifiers, settings.Voice.HotkeyKey);
        if (binding == _binding && _registered) return;
        DisableShortcut();
        _registered = HotkeyRegistry.Register(binding, () =>
        {
            if (_active?.Owner == _shortcutOwner) { _active.Finish(true); return; }
            var current = DesktopSettingsStore.Current;
            Begin(_shortcutOwner, current, (msg, listening) => VoiceToast.Show(msg, listening, listening),
                "برای پایان دوباره " + binding.Display + " را بزنید.");
        }) == HotkeyResult.Ok;
        _binding = binding;
    }

    private void DisableShortcut()
    {
        if (_active?.Owner == _shortcutOwner) { ++_generation; _active.Finish(false); VoiceToast.Close(); }
        if (_binding != null) HotkeyRegistry.Unregister(Shortcut);
        _binding = null; _registered = false;
    }

    public void Stop()
    {
        DisableShortcut(); _generation++; _active?.Finish(false); VoiceToast.Close();
    }
    public Window? CreatePreviewWindow(DesktopSettings settings) => null;

    private sealed class Recording
    {
        public object Owner { get; }
        private readonly DesktopSettings _settings;
        private readonly Action<string, bool> _status;
        private readonly WaveInEvent _device;
        private readonly MemoryStream _samples = new();
        private readonly DispatcherTimer _limit = new() { Interval = TimeSpan.FromSeconds(30) };
        private readonly long _token;
        private bool _stopping, _recognize;

        public Recording(object owner, DesktopSettings settings, Action<string, bool> status, long token)
        {
            Owner = owner; _settings = settings; _status = status; _token = token;
            _device = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 40 };
            _device.DataAvailable += (_, e) =>
            {
                lock (_samples)
                {
                    int remaining = (int)Math.Max(0, 960000 - _samples.Length);
                    _samples.Write(e.Buffer, 0, Math.Min(e.BytesRecorded, remaining));
                }
            };
            _device.RecordingStopped += (_, e) => Application.Current.Dispatcher.BeginInvoke(() => Complete(e.Exception));
            _limit.Tick += (_, _) => Finish(true);
        }

        public void Start()
        {
            try { _device.StartRecording(); _limit.Start(); }
            catch { _device.Dispose(); throw; }
        }

        public void Finish(bool recognize)
        {
            if (!recognize) _recognize = false;
            if (_stopping) return;
            _stopping = true; _recognize = recognize; _limit.Stop();
            try { _device.StopRecording(); }
            catch (Exception ex) { Complete(ex); }
        }

        private bool _completed;
        private async void Complete(Exception? error)
        {
            if (_completed) return;
            _completed = true; _limit.Stop();
            if (ReferenceEquals(_active, this)) _active = null;
            _device.Dispose();
            byte[] audio;
            lock (_samples) { audio = _samples.ToArray(); _samples.Dispose(); }
            if (_token != _generation) return;
            if (error != null) { _status("میکروفون قطع شد. " + error.Message, false); return; }
            if (!_recognize || audio.Length < 1600) { _status("میکروفون بسته شد.", false); return; }
            _status("در حال تشخیص فرمان…", false);
            try
            {
                var language = _settings.Voice.PreferPersian ? VoiceLanguage.Persian : VoiceLanguage.Finglish;
                var commands = VoiceCommands.Build(language, _settings.Voice.WakeWord,
                    LauncherModule.SharedIndex?.Apps ?? Array.Empty<LauncherResult>(), _settings.Layouts.Select(x => x.Name));
                string phrase = await Task.Run(() => VoiceEngine.Decode(audio, language, commands));
                if (_token != _generation) return;
                var match = commands.FirstOrDefault(c => c.Phrase == VoiceCommands.Clean(phrase, language));
                _status(match?.Description ?? "فرمان شناخته نشد. " + phrase, false);
                match?.Run();
            }
            catch (Exception ex) { if (_token == _generation) _status("تشخیص گفتار در دسترس نیست. " + ex.Message, false); }
        }
    }

    private static bool Begin(object owner, DesktopSettings settings, Action<string, bool> status, string hint)
    {
        if (_active != null) { status("یک فرمان صوتی در حال ضبط است.", false); return false; }
        if (settings.Voice.PreferPersian && !VoiceEngine.Installed)
        { status("ابتدا بستهٔ فارسی را از بخش فرمان صوتی نصب کنید.", false); return false; }
        try
        {
            var r = new Recording(owner, settings, status, ++_generation);
            _active = r; r.Start();
            status("میکروفون روشن است · " + hint, true); return true;
        }
        catch { _active = null; status("میکروفون باز نشد. دستگاه ورودی و دسترسی ویندوز را بررسی کنید.", false); return false; }
    }

    public static Button HoldButton(Func<DesktopSettings> settings, Action<string> status)
    {
        const string label = "🎙 نگه دارید و فرمان بگویید";
        var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), ToolTip = "ماوس یا کلید فاصله را نگه دارید." };
        long token = -1;
        void Start()
        {
            if (Begin(button, settings(), (msg, listening) =>
                { button.Content = listening ? "در حال شنیدن… رها کنید" : label; status(msg); }, "برای پایان رها کنید."))
                token = _generation;
        }
        void End(bool recognize)
        {
            if (!recognize && token == _generation) { ++_generation; button.Content = label; }
            if (_active?.Owner == button) _active.Finish(recognize);
        }
        button.PreviewMouseLeftButtonDown += (_, e) => { button.Focus(); if (button.CaptureMouse()) Start(); e.Handled = true; };
        button.PreviewMouseLeftButtonUp += (_, e) => { End(true); button.ReleaseMouseCapture(); e.Handled = true; };
        button.LostMouseCapture += (_, e) => { if (e.MouseDevice.LeftButton == MouseButtonState.Pressed) End(false); };
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) { if (!e.IsRepeat) Start(); e.Handled = true; } };
        button.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) { End(true); e.Handled = true; } };
        button.LostKeyboardFocus += (_, _) => End(false);
        Window? owner = null;
        void Deactivated(object? sender, EventArgs e) => End(false);
        button.Loaded += (_, _) => { owner = Window.GetWindow(button); if (owner != null) owner.Deactivated += Deactivated; };
        button.Unloaded += (_, _) => { End(false); if (owner != null) owner.Deactivated -= Deactivated; };
        return button;
    }
}
