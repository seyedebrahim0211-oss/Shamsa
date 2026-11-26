#nullable enable
using System;
using System.Windows;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class ShamsaProHubWindow : Window
{
    private ReleaseUpdateDetails? _pendingRelease;

    public ShamsaProHubWindow()
    {
        InitializeComponent();
        ThemeCombo.ItemsSource = Enum.GetValues<ShamsaThemePreset>();
        CurrentVersionText.Text = $"نسخه فعلی نصب‌شده: v{UpdateServicePro.CurrentVersion}";
        LoadSettingsToUi();
    }

    private void LoadSettingsToUi()
    {
        var s = ShamsaProSettingsService.Current;
        ChkTopBar.IsChecked = s.EnableTopBarControlCenter;
        ChkHideTaskbar.IsChecked = s.HideNativeTaskbar;
        ChkDock.IsChecked = s.EnableSmartDock;
        ChkFences.IsChecked = s.EnableDesktopFences;
        ChkWidgets.IsChecked = s.EnableDesktopWidgets;
        ChkClipboard.IsChecked = s.EnableClipboardHistory;
        ChkVoice.IsChecked = s.Recorder.VoiceCommandsEnabled;
        ChkLowPerf.IsChecked = s.LowPerformanceMode;

        ThemeCombo.SelectedItem = s.ThemePreset;
        AccentHexBox.Text = s.CustomAccentHex;
        GlassOpacitySlider.Value = s.GlobalGlassOpacity;
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var s = ShamsaProSettingsService.Current;
        s.EnableTopBarControlCenter = ChkTopBar.IsChecked == true;
        s.HideNativeTaskbar = ChkHideTaskbar.IsChecked == true;
        s.EnableSmartDock = ChkDock.IsChecked == true;
        s.EnableDesktopFences = ChkFences.IsChecked == true;
        s.EnableDesktopWidgets = ChkWidgets.IsChecked == true;
        s.EnableClipboardHistory = ChkClipboard.IsChecked == true;
        s.Recorder.VoiceCommandsEnabled = ChkVoice.IsChecked == true;
        s.LowPerformanceMode = ChkLowPerf.IsChecked == true;
        s.ReduceAnimations = s.LowPerformanceMode;

        if (ThemeCombo.SelectedItem is ShamsaThemePreset preset) s.ThemePreset = preset;
        s.CustomAccentHex = AccentHexBox.Text.Trim();
        s.GlobalGlassOpacity = GlassOpacitySlider.Value;

        ShamsaProSettingsService.Save(s);
        HubStatusText.Text = "تنظیمات ذخیره و با موفقیت اعمال شد.";
    }

    private void RestoreDesktop_Click(object sender, RoutedEventArgs e)
    {
        RecoveryService.RestoreWindowsDesktop(enterSafeMode: false);
        LoadSettingsToUi();
        HubStatusText.Text = "نوار وظیفه و دسکتاپ اصلی ویندوز با موفقیت بازیابی شد.";
    }

    private void Tile2_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.WindowManager?.ApplyLayout(TilingLayoutMode.TwoColumns);
    private void Tile3_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.WindowManager?.ApplyLayout(TilingLayoutMode.ThreeColumns);
    private void Tile4_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.WindowManager?.ApplyLayout(TilingLayoutMode.FourGrid);
    private void TileStack_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.WindowManager?.ApplyLayout(TilingLayoutMode.Stacked);

    private void OpenFolderStudio_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.OpenFolderCustomizer();
    private void OpenRecorder_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.OpenScreenRecorderHud();
    private void OpenSpotlight_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.OpenSpotlight();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "در حال بررسی آخرین نسخه در گیت‌هاب...";
        InstallUpdateBtn.IsEnabled = false;
        try
        {
            _pendingRelease = await UpdateServicePro.CheckForUpdateAsync();
            if (_pendingRelease == null)
            {
                UpdateStatusText.Text = "نگارخانه کاملاً به‌روز است.";
                ChangelogBox.Text = "نسخه جدیدتری یافت نشد.";
                return;
            }

            UpdateStatusText.Text = $"نسخه جدید {_pendingRelease.Tag} آماده دریافت است ({_pendingRelease.SizeBytes / 1048576.0:0.0} MB).";
            ChangelogBox.Text = string.IsNullOrWhiteSpace(_pendingRelease.ChangelogMarkdown)
                ? _pendingRelease.ReleaseTitle
                : _pendingRelease.ChangelogMarkdown;
            InstallUpdateBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"خطا در بررسی بروزرسانی: {ex.Message}";
        }
    }

    private async void DownloadInstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRelease == null) return;
        InstallUpdateBtn.IsEnabled = false;
        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            UpdateProgressBar.Value = p.Percent;
            UpdateStatusText.Text = p.StatusText;
        });

        try
        {
            string installer = await UpdateServicePro.DownloadAndVerifyAsync(_pendingRelease, progress);
            UpdateServicePro.InstallAndRestart(installer, _pendingRelease, silent: true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"خطا در بروزرسانی: {ex.Message}";
            InstallUpdateBtn.IsEnabled = true;
        }
    }
}
