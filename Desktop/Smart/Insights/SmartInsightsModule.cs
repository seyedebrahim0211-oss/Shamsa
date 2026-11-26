using System.Windows;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>
/// Background part of «هوشمندسازی»: morning-routine sampling (1/min, before noon only), suggestions refresh
/// every 30 minutes, and the optional desktop archive every 6 hours. Works even when the desktop environment
/// (top bar/dock) is off, because the suggestions belong to the main hub.
/// </summary>
public sealed class SmartInsightsModule : IDesktopModule
{
    public string Id => "smartinsights";
    public string DisplayName => "هوشمندسازی";
    public bool IsRunning { get; private set; }

    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _archiveTimer;
    private bool _subscribed;

    public void Apply(DesktopSettings settings)
    {
        if (!_subscribed)
        {
            InsightsStore.Changed += OnInsightsChanged;
            _subscribed = true;
        }
        Reconfigure();
    }

    private void OnInsightsChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Reconfigure();
        else d.BeginInvoke(Reconfigure);
    }

    private void Reconfigure()
    {
        var s = InsightsStore.Settings;

        if (s.LearnRoutine && s.SuggestionsEnabled) RoutineTracker.Start();
        else RoutineTracker.Stop();

        if (s.SuggestionsEnabled)
        {
            if (_refreshTimer == null)
            {
                _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SmartSuggestions.MinInterval };
                _refreshTimer.Tick += (_, _) => SmartSuggestions.Refresh();
                _refreshTimer.Start();
                // first look a little after start-up, so app launch stays fast
                var once = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
                once.Tick += (_, _) => { once.Stop(); SmartSuggestions.Refresh(); };
                once.Start();
            }
        }
        else
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
            SmartSuggestions.Refresh();
        }

        if (s.ArchiveEnabled)
        {
            if (_archiveTimer == null)
            {
                _archiveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(6) };
                _archiveTimer.Tick += (_, _) => Archive();
                _archiveTimer.Start();
            }
        }
        else
        {
            _archiveTimer?.Stop();
            _archiveTimer = null;
        }

        IsRunning = RoutineTracker.IsRunning || _refreshTimer != null || _archiveTimer != null;
    }

    private static void Archive()
    {
        _ = Task.Run(() =>
        {
            try
            {
                int n = DesktopTidy.RunArchive(DateTime.Now);
                if (n > 0)
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                        Notifier.Show($"{SmartSuggestions.Fa(n)} فایل قدیمی دسکتاپ به پوشه «{DesktopTidy.ArchiveFolderName}» رفت. از صفحه هوشمندسازی قابل برگشت است."));
            }
            catch (Exception ex) { AppLog.Warn("Desktop archive failed: " + ex.Message); }
        });
    }

    public void Stop()
    {
        RoutineTracker.Stop();
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _archiveTimer?.Stop();
        _archiveTimer = null;
        if (_subscribed)
        {
            InsightsStore.Changed -= OnInsightsChanged;
            _subscribed = false;
        }
        IsRunning = false;
    }

    public Window? CreatePreviewWindow(DesktopSettings settings) => null;
}
