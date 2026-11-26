#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class ScreenRecorderHudWindow : Window
{
    public ScreenRecorderHudWindow()
    {
        InitializeComponent();
        if (ShamsaProBootstrap.ScreenRecorder != null)
        {
            ShamsaProBootstrap.ScreenRecorder.StateChanged += state =>
                Dispatcher.Invoke(() =>
                {
                    RecordBtn.Content = state.IsRecording ? "⏹ توقف ضبط" : "⏺ شروع ضبط";
                    StatusLabel.Text = state.StatusMessage;
                });
        }
    }

    private async void ToggleRecord_Click(object sender, RoutedEventArgs e)
    {
        var rec = ShamsaProBootstrap.ScreenRecorder;
        if (rec == null) return;

        var s = ShamsaProSettingsService.Current;
        s.Recorder.Fps = FpsCombo.SelectedIndex switch { 0 => 24, 2 => 60, _ => 30 };
        s.Recorder.QualityPreset = (QualityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced";
        ShamsaProSettingsService.Save(s);

        try
        {
            if (rec.IsRecording)
                await rec.StopRecordingAsync();
            else
                await rec.StartRecordingAsync(CaptureAreaMode.FullScreen);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShamsaProBootstrap.ScreenRecorder?.CaptureScreenshot();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = ShamsaProSettingsService.Current.Recorder.OutputDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
