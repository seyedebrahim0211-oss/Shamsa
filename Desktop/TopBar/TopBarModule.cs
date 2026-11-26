using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Shamsa.Desktop.Shell;
using Shamsa.Services;
using Forms = System.Windows.Forms;

namespace Shamsa.Desktop.TopBar;

/// <summary>
/// Seelen-UI-style glass top bar: launcher, virtual desktops, focused app, clock with Persian date,
/// media controls, CPU/RAM, network, volume, battery and a quick-settings menu.
/// </summary>
public sealed class TopBarModule : IDesktopModule
{
    private readonly List<TopBarWindow> _windows = new();
    private TopBarState? _state;
    private DispatcherTimer? _fullscreenTimer;
    private DispatcherTimer? _relayoutTimer;
    private string? _layoutSignature;
    private DesktopSettings? _settings;
    private bool _systemEventsHooked;

    public string Id => "topbar";
    public string DisplayName => "نوار بالا";
    public bool IsRunning => _windows.Count > 0;

    public void Apply(DesktopSettings settings)
    {
        try
        {
            bool want = settings.Enabled && settings.TopBar.Enabled;
            if (!want) { Stop(); return; }
            _settings = settings;
            try { Plugins.TopBarPluginHost.Instance.Apply(settings); }
            catch (Exception ex) { AppLog.Warn("TopBar plugins: " + ex.Message); }
            string sig = Signature(settings);
            if (IsRunning && sig == _layoutSignature)
            {
                foreach (var w in _windows) w.ApplySettings(settings);
                UpdateFullscreenTimer();
                return;
            }
            CloseWindows();
            _layoutSignature = sig;
            _state ??= new TopBarState();
            _state.Start(trackForeground: true);
            foreach (var monitor in TargetMonitors(settings))
            {
                var w = new TopBarWindow(settings, _state, monitor, preview: false);
                w.DisplayChanged += ScheduleRelayout;
                w.Show();
                _windows.Add(w);
            }
            if (!_systemEventsHooked)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                _systemEventsHooked = true;
            }
            UpdateFullscreenTimer();
            AppLog.Info($"TopBar started on {_windows.Count} monitor(s)");
        }
        catch (Exception ex)
        {
            AppLog.Error("TopBar apply failed", ex);
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            _fullscreenTimer?.Stop();
            _fullscreenTimer = null;
            _relayoutTimer?.Stop();
            _relayoutTimer = null;
            if (_systemEventsHooked)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _systemEventsHooked = false;
            }
            CloseWindows();
            _state?.Dispose();
            _state = null;
            _layoutSignature = null;
            Plugins.TopBarPluginHost.Instance.Stop();
        }
        catch (Exception ex) { AppLog.Warn("TopBar stop: " + ex.Message); }
    }

    public Window? CreatePreviewWindow(DesktopSettings settings)
    {
        var state = new TopBarState();
        state.Start(trackForeground: false);
        try { Plugins.TopBarPluginHost.Instance.LoadOnceForPreview(settings); } catch { }
        var w = new TopBarWindow(settings, state, IntPtr.Zero, preview: true);
        w.Closed += (_, _) => state.Dispose();
        return w;
    }

    // ------------------------------------------------------------------ internals

    private void CloseWindows()
    {
        foreach (var w in _windows)
        {
            try { w.DisplayChanged -= ScheduleRelayout; w.Close(); }
            catch (Exception ex) { AppLog.Warn("TopBar close: " + ex.Message); }
        }
        _windows.Clear();
    }

    private static string Signature(DesktopSettings s) =>
        string.Join("|", s.TopBar.Position, s.TopBar.AllMonitors, Forms.Screen.AllScreens.Length,
            string.Join(";", Forms.Screen.AllScreens.Select(x => x.Bounds.ToString())));

    private static IEnumerable<IntPtr> TargetMonitors(DesktopSettings s)
    {
        var screens = s.TopBar.AllMonitors ? Forms.Screen.AllScreens : new[] { Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0] };
        foreach (var sc in screens)
        {
            var b = sc.Bounds;
            yield return TopBarNative.MonitorFromPoint(new TopBarNative.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, 2);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(ScheduleRelayout);

    /// <summary>Debounced rebuild after monitor/DPI/resolution changes (AppBars must be renegotiated).</summary>
    private void ScheduleRelayout()
    {
        if (_settings == null) return;
        _relayoutTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _relayoutTimer.Stop();
        _relayoutTimer.Tick -= OnRelayout;
        _relayoutTimer.Tick += OnRelayout;
        _relayoutTimer.Start();
    }

    private void OnRelayout(object? sender, EventArgs e)
    {
        _relayoutTimer?.Stop();
        if (_settings == null || !IsRunning) return;
        if (Signature(_settings) != _layoutSignature)
        {
            _layoutSignature = null; // force rebuild (monitor set changed)
            Apply(_settings);
        }
        else
        {
            foreach (var w in _windows) w.Reposition();
        }
    }

    private void UpdateFullscreenTimer()
    {
        bool want = _settings?.TopBar.AutoHideOnFullscreen == true;
        if (!want)
        {
            _fullscreenTimer?.Stop();
            _fullscreenTimer = null;
            foreach (var w in _windows) w.SetSuppressed(false);
            return;
        }
        if (_fullscreenTimer != null) return;
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _fullscreenTimer.Tick += (_, _) =>
        {
            try
            {
                bool full = ShellInterop.IsFullscreenAppActive();
                foreach (var w in _windows) w.SetSuppressed(full);
            }
            catch (Exception ex) { AppLog.Warn("TopBar fullscreen check: " + ex.Message); }
        };
        _fullscreenTimer.Start();
    }
}
