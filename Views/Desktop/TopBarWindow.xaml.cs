#nullable enable
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Shamsa.Services;
using Shamsa.Services.Desktop;

namespace Shamsa.Views.Desktop;

public partial class TopBarWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly SystemTelemetryService _telemetry = new();

    public TopBarWindow()
    {
        InitializeComponent();
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;

        PopulateWorkspaces();
        RenderPlugins();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        UpdateStatus();
    }

    private void PopulateWorkspaces()
    {
        WorkspaceCombo.Items.Clear();
        var s = ShamsaProSettingsService.Current;
        int selected = 0;
        for (int i = 0; i < s.Workspaces.Count; i++)
        {
            WorkspaceCombo.Items.Add(new ComboBoxItem { Content = s.Workspaces[i].Name, Tag = s.Workspaces[i] });
            if (s.Workspaces[i].Id == s.ActiveWorkspaceId) selected = i;
        }
        if (WorkspaceCombo.Items.Count > 0) WorkspaceCombo.SelectedIndex = selected;
    }

    private void RenderPlugins()
    {
        PluginSlotPanel.Children.Clear();
        if (ShamsaProBootstrap.PluginHost == null) return;
        foreach (var plugin in ShamsaProBootstrap.PluginHost.LoadedPlugins)
        {
            if (!plugin.Enabled || !plugin.Slot.Equals("TopBar", StringComparison.OrdinalIgnoreCase)) continue;
            PluginSlotPanel.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("PersianBadgeBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(4, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(plugin.DisplayTemplate) ? plugin.Name : plugin.DisplayTemplate,
                    FontSize = 11,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
                }
            });
        }
    }

    private void UpdateStatus()
    {
        var date = PersianCalendarService.GetTodaySummary();
        ClockDateText.Text = $"{date.JalaliDate}  •  {DateTime.Now:HH:mm}";

        var snap = _telemetry.ReadSnapshot();
        TelemetryMiniText.Text = $"CPU {snap.CpuUsageText} | RAM {snap.RamUsageText} | {snap.BatteryText}";
    }

    private void WorkspaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceCombo.SelectedItem is ComboBoxItem item && item.Tag is WorkspaceProfile profile)
            ShamsaProBootstrap.WindowManager?.ActivateWorkspace(profile);
    }

    private void TileTwoCol_Click(object sender, RoutedEventArgs e)
        => ShamsaProBootstrap.WindowManager?.ApplyLayout(TilingLayoutMode.TwoColumns);

    private void OpenHub_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.OpenProHub();
    private void OpenRecorder_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.OpenScreenRecorderHud();
    private void OpenSpotlight_Click(object sender, RoutedEventArgs e) => ShamsaProBootstrap.OpenSpotlight();

    // Official Windows Settings URIs for OS-level Control Center actions
    private static void LaunchUri(string uri) => Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
    private void OpenWifiSettings_Click(object sender, RoutedEventArgs e) => LaunchUri("ms-settings:network-wifi");
    private void OpenBluetoothSettings_Click(object sender, RoutedEventArgs e) => LaunchUri("ms-settings:bluetooth");
    private void OpenNightLightSettings_Click(object sender, RoutedEventArgs e) => LaunchUri("ms-settings:nightlight");
    private void OpenDndSettings_Click(object sender, RoutedEventArgs e) => LaunchUri("ms-settings:quiethours");
    private void OpenDisplaySettings_Click(object sender, RoutedEventArgs e) => LaunchUri("ms-settings:display");

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _telemetry.Dispose();
        base.OnClosed(e);
    }
}
