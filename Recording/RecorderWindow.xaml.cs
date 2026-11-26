using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Shamsa.Services;

namespace Shamsa.Recording;

public partial class RecorderWindow : Window
{
    private bool _loading = true;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private List<MonitorInfo> _monitors = new();
    private List<WindowInfo> _windows = new();

    private sealed record RecentItem(string Path)
    {
        public override string ToString()
        {
            string size = "";
            try { size = FileKinds.FormatSize(new FileInfo(Path).Length).Trim('‪', '‬'); } catch { }
            return $"{System.IO.Path.GetFileName(Path)}    {size}    {File.GetLastWriteTime(Path):yyyy/MM/dd HH:mm}";
        }
    }

    public RecorderWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => TryDarkTitleBar();
        LoadMonitors();
        LoadWindows();
        LoadSettingsIntoUi();
        _loading = false;

        RecorderFeature.StatusChanged += OnStatusChanged;
        RecorderFeature.Message += OnMessage;
        RecorderSettingsStore.Changed += OnSettingsChanged;
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
        UpdateFfmpegCard();
        Closed += (_, _) =>
        {
            _timer.Stop();
            RecorderFeature.StatusChanged -= OnStatusChanged;
            RecorderFeature.Message -= OnMessage;
            RecorderSettingsStore.Changed -= OnSettingsChanged;
        };
    }

    // ------------------------------------------------------------------ hosting in the «نگارخانه» hub

    /// <summary>When set, this window is never shown: its content lives in <see cref="Host"/>.</summary>
    public Window? Host { get; set; }
    private Window Dlg => Host ?? this;
    private bool _hostMinimizedForRecording;

    /// <summary>Takes the window content for hosting elsewhere.</summary>
    public FrameworkElement? DetachForHost(Window host)
    {
        Host = host;
        var root = UiHost.TakeContent(this);
        if (root != null) root.Margin = new Thickness(0);
        return root;
    }

    /// <summary>Detaches static event handlers when the hub closes (the hidden window is never closed itself).</summary>
    public void ReleaseHosted()
    {
        _timer.Stop();
        RecorderFeature.StatusChanged -= OnStatusChanged;
        RecorderFeature.Message -= OnMessage;
        RecorderSettingsStore.Changed -= OnSettingsChanged;
    }

    /// <summary>
    /// The recorder minimises its own window while recording; when hosted, do the same for the hub so it
    /// is not captured (window-only capture keeps it visible, like the stand-alone window).
    /// </summary>
    private void SyncHostWithRecording()
    {
        if (Host == null) return;
        var state = RecorderFeature.State;
        bool starting = RecorderFeature.IsBusy && state != RecorderState.Finalizing;
        if (starting && !_hostMinimizedForRecording && Host.IsVisible && Host.WindowState != WindowState.Minimized
            && RecorderSettingsStore.Current.Mode != CaptureMode.Window)
        {
            _hostMinimizedForRecording = true;
            Host.WindowState = WindowState.Minimized;
        }
        else if (!RecorderFeature.IsBusy && _hostMinimizedForRecording)
        {
            _hostMinimizedForRecording = false;
            if (Host.WindowState == WindowState.Minimized) Host.WindowState = WindowState.Normal;
        }
    }

    private void TryDarkTitleBar()
    {
        try { NativeMethods.TryRoundCorners(new WindowInteropHelper(this).Handle); } catch { }
    }

    private void OnStatusChanged() => Dispatcher.BeginInvoke(() => { RefreshStatus(); SyncHostWithRecording(); });
    private void OnMessage(string m) => Dispatcher.BeginInvoke(() => MessageText.Text = m);
    private void OnSettingsChanged() => Dispatcher.BeginInvoke(LoadRecent);

    // ------------------------------------------------------------------ load

    private void LoadMonitors()
    {
        _monitors = RecorderNative.GetMonitors();
        MonitorBox.Items.Clear();
        foreach (var m in _monitors) MonitorBox.Items.Add(m.Label);
    }

    private void LoadWindows()
    {
        _windows = RecorderNative.GetCapturableWindows();
        WindowBox.Items.Clear();
        foreach (var w in _windows) WindowBox.Items.Add(w.Label);
        int idx = _windows.FindIndex(w => w.Handle == RecorderFeature.SelectedWindow);
        WindowBox.SelectedIndex = idx;
    }

    private void LoadSettingsIntoUi()
    {
        var s = RecorderSettingsStore.Current;
        ModeBox.SelectedIndex = (int)s.Mode;
        MonitorBox.SelectedIndex = _monitors.Count == 0 ? -1 : Math.Clamp(s.MonitorIndex, 0, _monitors.Count - 1);
        SelectByTag(FpsBox, s.Fps.ToString());
        SelectByTag(ContainerBox, s.Container);
        LoadEncoders(new[] { "x264", "h264_nvenc", "h264_qsv", "h264_amf" }, s.Encoder);
        QualitySlider.Value = s.Quality;
        QualityText.Text = $"{s.Quality}%";
        DrawMouseCheck.IsChecked = s.DrawMouse;
        ClicksCheck.IsChecked = s.HighlightClicks;
        CountdownCheck.IsChecked = s.Countdown;
        ToolbarCheck.IsChecked = s.ShowToolbar;
        DdaCheck.IsChecked = s.UseDesktopDuplication;
        SystemAudioCheck.IsChecked = s.RecordSystemAudio;
        MicCheck.IsChecked = s.RecordMicrophone;
        ShotModeBox.SelectedIndex = (int)s.ScreenshotMode;
        ShotClipboardCheck.IsChecked = s.ScreenshotToClipboard;
        ShotJpgCheck.IsChecked = s.ScreenshotJpg;
        ShotFolderBox.Text = s.EffectiveScreenshotFolder;
        OutputFolderBox.Text = s.EffectiveOutputFolder;
        HotkeysCheck.IsChecked = s.HotkeysEnabled;
        VoiceCheck.IsChecked = s.VoiceCommandsEnabled;
        UpdateHotkeyBoxes();
        UpdateModePanels();
        LoadRecent();
    }

    private void LoadEncoders(IEnumerable<string> encoders, string selected)
    {
        EncoderBox.Items.Clear();
        foreach (var e in encoders.Distinct())
            EncoderBox.Items.Add(new ComboBoxItem { Content = EncoderProbe.Label(e), Tag = e });
        SelectByTag(EncoderBox, selected);
        if (EncoderBox.SelectedIndex < 0) EncoderBox.SelectedIndex = 0;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is ComboBoxItem { Tag: string t } && t == tag) { box.SelectedIndex = i; return; }
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void LoadRecent()
    {
        RecentList.Items.Clear();
        foreach (var p in RecorderSettingsStore.Current.RecentRecordings.Where(File.Exists))
            RecentList.Items.Add(new RecentItem(p));
    }

    private void UpdateModePanels()
    {
        var mode = (CaptureMode)Math.Max(0, ModeBox.SelectedIndex);
        MonitorPanel.Visibility = mode == CaptureMode.FullScreen ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = mode == CaptureMode.Window ? Visibility.Visible : Visibility.Collapsed;
        RegionPanel.Visibility = mode is CaptureMode.Region or CaptureMode.LastRegion ? Visibility.Visible : Visibility.Collapsed;
        var s = RecorderSettingsStore.Current;
        LastRegionText.Text = s.LastRegionW > 0
            ? $"آخرین ناحیه: {s.LastRegionW}×{s.LastRegionH} در موقعیت {s.LastRegionX},{s.LastRegionY}"
            : "هنوز ناحیه‌ای انتخاب نشده است؛ هنگام شروع ضبط ناحیه را با موس بکشید.";
    }

    private void UpdateHotkeyBoxes()
    {
        var s = RecorderSettingsStore.Current;
        HkRecordBox.Text = GlobalHotkeyService.FormatShortcut(s.HotkeyRecord.Modifiers, s.HotkeyRecord.Key);
        HkPauseBox.Text = GlobalHotkeyService.FormatShortcut(s.HotkeyPause.Modifiers, s.HotkeyPause.Key);
        HkShotBox.Text = GlobalHotkeyService.FormatShortcut(s.HotkeyShot.Modifiers, s.HotkeyShot.Key);
        string status = RecorderFeature.HotkeyStatus;
        HotkeyStatusText.Text = !s.HotkeysEnabled ? "کلیدهای میانبر خاموش هستند." : string.IsNullOrEmpty(status) ? "✅ همه کلیدها فعال هستند." : status;
        HotkeyStatusText.Foreground = (Brush)FindResource(!s.HotkeysEnabled ? "MutedBrush" : string.IsNullOrEmpty(status) ? "SuccessBrush" : "WarnBrush");
    }

    private void UpdateFfmpegCard()
    {
        FfmpegCard.Visibility = FfmpegManager.GetFfmpegPath() == null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ status

    private void RefreshStatus()
    {
        var state = RecorderFeature.State;
        bool busy = RecorderFeature.IsBusy;
        (string text, string color) = state switch
        {
            RecorderState.Recording => ("در حال ضبط", "#F43F5E"),
            RecorderState.Paused => ("مکث", "#FBBF24"),
            RecorderState.Finalizing => ("در حال ذخیره…", "#2DD4BF"),
            RecorderState.Starting => ("در حال شروع…", "#2DD4BF"),
            _ => busy ? ("آماده‌سازی…", "#2DD4BF") : ("آماده ضبط", "#64748B")
        };
        StateText.Text = text;
        StateDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        TimerText.Text = state is RecorderState.Recording or RecorderState.Paused
            ? (RecorderFeature.Elapsed.TotalHours >= 1 ? RecorderFeature.Elapsed.ToString(@"h\:mm\:ss") : RecorderFeature.Elapsed.ToString(@"mm\:ss"))
              + "  ·  " + FileKinds.FormatSize(RecorderFeature.CurrentBytes).Trim('‪', '‬')
            : string.Empty;
        bool active = state is RecorderState.Recording or RecorderState.Paused;
        RecordBtn.Content = active ? "⏹ توقف و ذخیره" : "⏺ شروع ضبط";
        RecordBtn.IsEnabled = active || (!busy && state == RecorderState.Idle);
        PauseBtn.IsEnabled = active;
        PauseBtn.Content = state == RecorderState.Paused ? "▶ ادامه" : "⏸ مکث";
        VoiceStatusText.Text = string.IsNullOrEmpty(RecorderFeature.VoiceStatus) ? (VoiceCheck.IsChecked == true ? "" : "خاموش") : RecorderFeature.VoiceStatus;
    }

    // ------------------------------------------------------------------ actions

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        UpdateFfmpegCard();
        RecorderFeature.ToggleRecording();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => RecorderFeature.PauseResume();
    private void ShotDefault_Click(object sender, RoutedEventArgs e) => RecorderFeature.TakeScreenshot();
    private void ShotFull_Click(object sender, RoutedEventArgs e) => RecorderFeature.TakeScreenshot(ScreenshotMode.FullScreen);
    private void ShotRegion_Click(object sender, RoutedEventArgs e) => RecorderFeature.TakeScreenshot(ScreenshotMode.Region);

    private async void ShotWindow_Click(object sender, RoutedEventArgs e)
    {
        // Give focus back to the previously active window, otherwise this window would be captured.
        Dlg.WindowState = WindowState.Minimized;
        await Task.Delay(350);
        RecorderFeature.TakeScreenshot(ScreenshotMode.ActiveWindow);
        await Task.Delay(300);
        Dlg.WindowState = WindowState.Normal;
    }

    private async void PickRegion_Click(object sender, RoutedEventArgs e)
    {
        Dlg.WindowState = WindowState.Minimized;
        await Task.Delay(250);
        var r = await RegionSelectorWindow.SelectAsync();
        Dlg.WindowState = WindowState.Normal;
        Dlg.Activate();
        if (r is not { } sel) return;
        RecorderSettingsStore.Update(s =>
        {
            s.LastRegionX = sel.X; s.LastRegionY = sel.Y; s.LastRegionW = sel.Width; s.LastRegionH = sel.Height;
            s.Mode = CaptureMode.LastRegion;
        });
        _loading = true;
        ModeBox.SelectedIndex = (int)CaptureMode.LastRegion;
        _loading = false;
        UpdateModePanels();
    }

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        InstallFfmpegBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(m => FfmpegProgressText.Text = m);
            var (_, msg) = await FfmpegManager.DownloadFfmpegAsync(progress);
            FfmpegProgressText.Text = msg;
        }
        finally
        {
            InstallFfmpegBtn.IsEnabled = true;
            UpdateFfmpegCard();
        }
    }

    private async void DetectEncoders_Click(object sender, RoutedEventArgs e)
    {
        string? ffmpeg = FfmpegManager.GetFfmpegPath(refresh: true);
        if (ffmpeg == null) { UpdateFfmpegCard(); MessageText.Text = "ابتدا FFmpeg را نصب کنید."; return; }
        DetectBtn.IsEnabled = false;
        MessageText.Text = "در حال آزمایش انکودرهای کارت گرافیک…";
        try
        {
            var usable = await EncoderProbe.DetectAllAsync(ffmpeg);
            _loading = true;
            LoadEncoders(usable, RecorderSettingsStore.Current.Encoder);
            _loading = false;
            if (TagOf(EncoderBox) is string tag && tag != RecorderSettingsStore.Current.Encoder)
                RecorderSettingsStore.Update(s => s.Encoder = tag);
            MessageText.Text = usable.Count > 1
                ? "انکودرهای قابل استفاده: " + string.Join("، ", usable.Select(EncoderProbe.Label))
                : "انکودر سخت‌افزاری قابل استفاده‌ای پیدا نشد؛ x264 (پردازنده) استفاده می‌شود.";
        }
        finally { DetectBtn.IsEnabled = true; }
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => LoadWindows();

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateModePanels();
        if (_loading) return;
        RecorderSettingsStore.Update(s => s.Mode = (CaptureMode)Math.Max(0, ModeBox.SelectedIndex));
        if (ModeBox.SelectedIndex == (int)CaptureMode.Window) LoadWindows();
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorBox.SelectedIndex < 0) return;
        RecorderSettingsStore.Update(s => s.MonitorIndex = MonitorBox.SelectedIndex);
    }

    private void Window_Changed(object sender, SelectionChangedEventArgs e)
    {
        int i = WindowBox.SelectedIndex;
        RecorderFeature.SelectedWindow = i >= 0 && i < _windows.Count ? _windows[i].Handle : IntPtr.Zero;
    }

    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText != null) QualityText.Text = $"{(int)e.NewValue}%";
        if (_loading) return;
        RecorderSettingsStore.Update(s => s.Quality = (int)e.NewValue);
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RecorderSettingsStore.Update(s =>
        {
            if (int.TryParse(TagOf(FpsBox), out int fps)) s.Fps = fps;
            s.Container = TagOf(ContainerBox) ?? "mp4";
            s.Encoder = TagOf(EncoderBox) ?? "x264";
            s.DrawMouse = DrawMouseCheck.IsChecked == true;
            s.HighlightClicks = ClicksCheck.IsChecked == true;
            s.Countdown = CountdownCheck.IsChecked == true;
            s.ShowToolbar = ToolbarCheck.IsChecked == true;
            s.UseDesktopDuplication = DdaCheck.IsChecked == true;
            s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
            s.RecordMicrophone = MicCheck.IsChecked == true;
            s.ScreenshotMode = (ScreenshotMode)Math.Max(0, ShotModeBox.SelectedIndex);
            s.ScreenshotToClipboard = ShotClipboardCheck.IsChecked == true;
            s.ScreenshotJpg = ShotJpgCheck.IsChecked == true;
        });
    }

    // ------------------------------------------------------------------ folders & recent

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ShellActions.PickFolder(RecorderSettingsStore.Current.EffectiveOutputFolder, Dlg);
        if (folder == null) return;
        RecorderSettingsStore.Update(s => s.OutputFolder = folder);
        OutputFolderBox.Text = folder;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        string f = RecorderSettingsStore.Current.EffectiveOutputFolder;
        Directory.CreateDirectory(f);
        ShellActions.OpenPath(f);
    }

    private void BrowseShotFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ShellActions.PickFolder(RecorderSettingsStore.Current.EffectiveScreenshotFolder, Dlg);
        if (folder == null) return;
        RecorderSettingsStore.Update(s => s.ScreenshotFolder = folder);
        ShotFolderBox.Text = folder;
    }

    private void OpenShotFolder_Click(object sender, RoutedEventArgs e)
    {
        string f = RecorderSettingsStore.Current.EffectiveScreenshotFolder;
        Directory.CreateDirectory(f);
        ShellActions.OpenPath(f);
    }

    private string? SelectedRecent() => (RecentList.SelectedItem as RecentItem)?.Path;

    private void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecent() is string p && File.Exists(p)) ShellActions.OpenPath(p);
    }

    private void RecentReveal_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecent() is string p) ShellActions.RevealInExplorer(p);
    }

    private void RecentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecent() is not string p || !File.Exists(p)) return;
        if (MessageBox.Show(Dlg, $"فایل «{Path.GetFileName(p)}» به سطل زباله منتقل شود؟", "ضبط صفحه نگارخانه",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            RecorderSettingsStore.Update(s => s.RecentRecordings.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Dlg, "حذف ممکن نشد: " + ex.Message, "ضبط صفحه نگارخانه", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ hotkeys & voice

    private void Hotkeys_Changed(object sender, RoutedEventArgs e)
    {
        RecorderSettingsStore.Update(s => s.HotkeysEnabled = HotkeysCheck.IsChecked == true);
        RecorderFeature.ApplyHotkeys();
        UpdateHotkeyBoxes();
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        RecorderFeature.SuspendHotkeys();
        if (sender is TextBox tb) tb.Text = "کلید ترکیبی را فشار دهید…";
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        RecorderFeature.ApplyHotkeys();
        UpdateHotkeyBoxes();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox tb) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        uint mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= NativeMethods.MOD_CONTROL;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= NativeMethods.MOD_ALT;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= NativeMethods.MOD_SHIFT;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= NativeMethods.MOD_WIN;
        bool isFunction = key >= Key.F1 && key <= Key.F24;
        if (mods == 0 && !isFunction)
        {
            tb.Text = "حداقل یک کلید Ctrl/Alt/Shift لازم است (یا F1 تا F12)";
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        var binding = new HotkeyBinding(mods, vk);
        RecorderSettingsStore.Update(s =>
        {
            switch (tb.Tag as string)
            {
                case "record": s.HotkeyRecord = binding; break;
                case "pause": s.HotkeyPause = binding; break;
                case "shot": s.HotkeyShot = binding; break;
            }
        });
        tb.Text = GlobalHotkeyService.FormatShortcut(mods, vk);
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, this);
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var d = new RecorderSettings();
        RecorderSettingsStore.Update(s => { s.HotkeyRecord = d.HotkeyRecord; s.HotkeyPause = d.HotkeyPause; s.HotkeyShot = d.HotkeyShot; });
        RecorderFeature.ApplyHotkeys();
        UpdateHotkeyBoxes();
    }

    private void Voice_Changed(object sender, RoutedEventArgs e)
    {
        bool want = VoiceCheck.IsChecked == true;
        string? error = RecorderFeature.SetVoiceEnabled(want);
        if (error != null)
        {
            VoiceCheck.IsChecked = false;
            MessageBox.Show(Dlg, error, "فرمان صوتی", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        RefreshStatus();
    }
}
