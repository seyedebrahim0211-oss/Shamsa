#nullable enable
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Shamsa.Services;
using Shamsa.Services.Desktop;

namespace Shamsa.Views.Desktop;

public partial class WidgetHostWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly SystemTelemetryService _telemetry = new();
    private static string NoteFile => Path.Combine(ShamsaProSettingsService.DataDir, "sticky-note.txt");

    public WidgetHostWindow()
    {
        InitializeComponent();
        Left = Math.Max(20, SystemParameters.PrimaryScreenWidth - Width - 24);
        Top = 52;

        if (File.Exists(NoteFile))
            StickyNoteBox.Text = File.ReadAllText(NoteFile);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => RefreshWidgets();
        _timer.Start();
        RefreshWidgets();
    }

    private void RefreshWidgets()
    {
        var s = ShamsaProSettingsService.Current;
        ClockBigText.Text = DateTime.Now.ToString("HH:mm");

        var date = PersianCalendarService.GetTodaySummary();
        JalaliDateText.Text = date.JalaliDate;
        AltDatesText.Text = $"{date.GregorianDate}  |  {date.HijriDate}";
        OccasionText.Text = date.IranianOccasion ?? "مناسبت تعطیل رسمی ثبت نشده است.";

        var prayer = PersianCalendarService.ComputePrayerTimes(s.CityName, s.Latitude, s.Longitude);
        PrayerHeaderText.Text = $"🕌 اوقات شرعی ({prayer.CityName})";
        PrayerBodyText.Text =
            $"اذان صبح: {prayer.Fajr}   •   طلوع: {prayer.Sunrise}\n" +
            $"اذان ظهر: {prayer.Dhuhr}   •   عصر: {prayer.Asr}\n" +
            $"مغرب: {prayer.Maghrib}   •   عشاء: {prayer.Isha}";

        var t = _telemetry.ReadSnapshot();
        TelemetryWidgetText.Text =
            $"پردازنده (CPU): {t.CpuUsageText}\n" +
            $"حافظه (RAM): {t.RamUsageText}\n" +
            $"دیسک: {t.DiskUsageText}\n" +
            $"باتری: {t.BatteryText}\n" +
            $"شبکه: {t.NetworkSpeedText}";
    }

    private void StickyNoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ShamsaProSettingsService.DataDir);
            File.WriteAllText(NoteFile, StickyNoteBox.Text);
        }
        catch { }
    }

    private void DragCard_MouseDown(object sender, MouseButtonEventArgs e) => DragMove();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _telemetry.Dispose();
        base.OnClosed(e);
    }
}
