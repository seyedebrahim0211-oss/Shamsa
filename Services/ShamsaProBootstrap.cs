#nullable enable
using System;
using System.Windows;
using Shamsa.Services.Desktop;
using Shamsa.Views;
using Shamsa.Views.Desktop;

namespace Shamsa.Services;

/// <summary>
/// Single-entry bootstrapper to integrate all Shamsa v3.0.0 Pro modules into existing App.xaml.cs
/// with only two lines:
///   ShamsaProBootstrap.Initialize(this);
///   ShamsaProBootstrap.Shutdown();
/// </summary>
public static class ShamsaProBootstrap
{
    private static TopBarWindow? _topBar;
    private static SmartDockWindow? _dock;
    private static DesktopFencesWindow? _fences;
    private static WidgetHostWindow? _widgets;
    private static SpotlightLauncherWindow? _spotlight;
    private static ShamsaProHubWindow? _hub;
    private static ScreenRecorderHudWindow? _recorderHud;
    private static FolderCustomizerWindow? _folderCustomizer;
    private static GlassFolderPreviewWindow? _glassFolderPreview;

    public static WindowManagerService? WindowManager { get; private set; }
    public static ScreenRecorderService? ScreenRecorder { get; private set; }
    public static VoiceCommandService? VoiceCommands { get; private set; }
    public static WallpaperEngineService? WallpaperEngine { get; private set; }
    public static ClipboardHistoryService? ClipboardHistory { get; private set; }
    public static PluginHostService? PluginHost { get; private set; }
    public static DesktopLayoutManager? LayoutManager { get; private set; }

    public static void Initialize(Application app)
    {
        bool previousCrash = RecoveryService.DetectUncleanShutdown();
        RecoveryService.MarkSessionStarted();

        var settings = ShamsaProSettingsService.Load();
        UpdateServicePro.VerifyPostUpdateHealth();

        if (previousCrash)
        {
            RecoveryService.RestoreWindowsDesktop(enterSafeMode: true);
            settings = ShamsaProSettingsService.Current;
        }

        ThemeEngineProService.Apply(settings);
        ThemeEngineProService.StartAutoThemeScheduler();

        LayoutManager = new DesktopLayoutManager();
        WindowManager = new WindowManagerService();
        ScreenRecorder = new ScreenRecorderService();
        VoiceCommands = new VoiceCommandService();
        WallpaperEngine = new WallpaperEngineService();
        ClipboardHistory = new ClipboardHistoryService();
        PluginHost = new PluginHostService();

        WireVoiceCommands();
        ShamsaProSettingsService.Changed += OnSettingsChanged;

        if (!settings.SafeMode)
            ApplyModuleStates(settings);
    }

    private static void WireVoiceCommands()
    {
        if (VoiceCommands == null) return;
        VoiceCommands.CommandRecognized += cmd =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                switch (cmd.Action)
                {
                    case VoiceActionKind.StartRecording:
                        OpenScreenRecorderHud();
                        _ = ScreenRecorder?.StartRecordingAsync();
                        break;
                    case VoiceActionKind.StopRecording:
                        _ = ScreenRecorder?.StopRecordingAsync();
                        break;
                    case VoiceActionKind.TakeScreenshot:
                        ScreenRecorder?.CaptureScreenshot();
                        break;
                    case VoiceActionKind.SwitchWorkspace:
                        if (!string.IsNullOrWhiteSpace(cmd.Argument))
                            WindowManager?.ActivateWorkspaceByName(cmd.Argument);
                        break;
                    case VoiceActionKind.ApplyTwoColumnLayout:
                        WindowManager?.ApplyLayout(TilingLayoutMode.TwoColumns);
                        break;
                    case VoiceActionKind.ToggleDesktopIcons:
                        _fences?.ToggleVisibility();
                        break;
                    case VoiceActionKind.OpenSpotlight:
                        OpenSpotlight();
                        break;
                }
            });
        };
    }

    private static void OnSettingsChanged(ShamsaProSettings settings)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ThemeEngineProService.Apply(settings);
            ApplyModuleStates(settings);
        });
    }

    private static void ApplyModuleStates(ShamsaProSettings s)
    {
        if (s.SafeMode)
        {
            CloseOverlayWindows();
            RecoveryService.SetNativeTaskbarVisible(true);
            return;
        }

        RecoveryService.SetNativeTaskbarVisible(!s.HideNativeTaskbar);

        if (s.EnableTopBarControlCenter)
        {
            _topBar ??= new TopBarWindow();
            if (!_topBar.IsVisible) _topBar.Show();
        }
        else _topBar?.Hide();

        if (s.EnableSmartDock)
        {
            _dock ??= new SmartDockWindow();
            if (!_dock.IsVisible) _dock.Show();
        }
        else _dock?.Hide();

        if (s.EnableDesktopFences)
        {
            _fences ??= new DesktopFencesWindow();
            if (!_fences.IsVisible) _fences.Show();
        }
        else _fences?.Hide();

        if (s.EnableDesktopWidgets)
        {
            _widgets ??= new WidgetHostWindow();
            if (!_widgets.IsVisible) _widgets.Show();
        }
        else _widgets?.Hide();

        if (s.EnableClipboardHistory)
            ClipboardHistory?.StartListening();
        else
            ClipboardHistory?.StopListening();

        if (s.EnablePlugins)
            PluginHost?.LoadInstalledPlugins();

        if (s.Recorder.VoiceCommandsEnabled)
            VoiceCommands?.StartListening(s.Recorder.VoiceLanguage);
        else
            VoiceCommands?.StopListening();
    }

    public static void OpenProHub()
    {
        _hub ??= new ShamsaProHubWindow();
        _hub.Show();
        _hub.Activate();
    }

    public static void OpenSpotlight()
    {
        _spotlight ??= new SpotlightLauncherWindow();
        _spotlight.ShowAndFocus();
    }

    public static void OpenScreenRecorderHud()
    {
        _recorderHud ??= new ScreenRecorderHudWindow();
        _recorderHud.Show();
        _recorderHud.Activate();
    }

    public static void OpenFolderCustomizer(string? folderPath = null)
    {
        _folderCustomizer ??= new FolderCustomizerWindow();
        if (!string.IsNullOrWhiteSpace(folderPath))
            _folderCustomizer.AddTargetFolder(folderPath);
        _folderCustomizer.Show();
        _folderCustomizer.Activate();
    }

    public static void ShowGlassFolderPreview(string folderPath)
    {
        _glassFolderPreview ??= new GlassFolderPreviewWindow();
        _glassFolderPreview.LoadFolder(folderPath);
        _glassFolderPreview.Show();
        _glassFolderPreview.Activate();
    }

    private static void CloseOverlayWindows()
    {
        _topBar?.Hide();
        _dock?.Hide();
        _fences?.Hide();
        _widgets?.Hide();
        _spotlight?.Hide();
    }

    public static void Shutdown()
    {
        try
        {
            ShamsaProSettingsService.Changed -= OnSettingsChanged;
            ThemeEngineProService.StopAutoThemeScheduler();
            RecoveryService.SetNativeTaskbarVisible(true);
            VoiceCommands?.Dispose();
            ScreenRecorder?.Dispose();
            WallpaperEngine?.Dispose();
            ClipboardHistory?.Dispose();
            WindowManager?.Dispose();
            CloseOverlayWindows();
            RecoveryService.MarkSessionCleanExit();
        }
        catch { /* ensure clean exit */ }
    }
}
