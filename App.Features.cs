using Shamsa.Desktop;
using Shamsa.FolderStudio;
using Shamsa.Recording;
using Shamsa.Services;

namespace Shamsa;

/// <summary>Glue between the app shell and the recorder, folder studio and desktop-environment modules.</summary>
public partial class App
{
    private void InitializeFeatures()
    {
        // Order matters for shortcut conflicts: the donut (ApplySettings) and recorder report first,
        // then desktop modules register and are refused if they would collide.
        try { RecorderFeature.Initialize(); }
        catch (Exception ex) { AppLog.Error("Recorder initialisation failed", ex); }
        try { DesktopFeature.Initialize(); }
        catch (Exception ex) { AppLog.Error("Desktop environment initialisation failed", ex); }
        WireVoiceCommands();
    }

    /// <summary>Persian voice commands → window manager, saved layouts and the recorder.</summary>
    private void WireVoiceCommands()
    {
        Desktop.Voice.VoiceCommands.LayoutCommand += layout => Dispatcher.BeginInvoke(() =>
        {
            if (!Desktop.WindowManager.WindowManagerModule.RequestLayout(layout))
                Notifier.Show("مدیر پنجره‌ها روشن نیست؛ از استودیو دسکتاپ فعالش کن.");
        });
        Desktop.Voice.VoiceCommands.LayoutProfileCommand += name => Dispatcher.BeginInvoke(async () =>
        {
            var layouts = DesktopSettingsStore.Current.Layouts;
            LayoutProfile? profile = name.StartsWith('#') && int.TryParse(name[1..], out int n) && n >= 1 && n <= layouts.Count
                ? layouts[n - 1]
                : layouts.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? layouts.FirstOrDefault(l => l.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (profile == null) { Notifier.Show($"حالت کاری «{name}» پیدا نشد."); return; }
            try { await Desktop.Rules.LayoutProfiles.RestoreAsync(profile); }
            catch (Exception ex) { AppLog.Error("Voice layout restore failed", ex); }
        });
        Desktop.Voice.VoiceCommands.AppCommand += cmd => Dispatcher.BeginInvoke(() =>
        {
            if (cmd == "screenshot") RecorderFeature.TakeScreenshot();
            else if (cmd == "record") RecorderFeature.ShowWindow();
        });
    }

    private void ShutdownFeatures()
    {
        try { DesktopFeature.Shutdown(); }
        catch (Exception ex) { AppLog.Warn("Desktop shutdown: " + ex.Message); }
        try { RecorderFeature.Shutdown(); }
        catch (Exception ex) { AppLog.Warn("Recorder shutdown: " + ex.Message); }
        try { HotkeyRegistry.Dispose(); } catch { }
    }

    private static bool FeatureBusy() => RecorderFeature.IsBusy;

    /// <summary>Handles feature command lines (Explorer verbs, shortcuts). Returns true when consumed.</summary>
    private bool HandleFeatureArgs(string[] args)
    {
        if (args.Length == 0) return false;
        try
        {
            if (FolderStudioFeature.TryHandleArgs(args)) return true;
            bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (Has("--recorder")) { RecorderFeature.ShowWindow(); return true; }
            if (Has("--screenshot")) { RecorderFeature.TakeScreenshot(); return true; }
            if (Has("--folder-studio-window")) { FolderStudioFeature.ShowWindow(); return true; }
            if (Has("--desktop-studio")) { DesktopFeature.ShowStudio(); return true; }
            if (Has("--launcher")) { DesktopFeature.ShowLauncher(); return true; }
            if (Has("--restore-desktop")) { DesktopFeature.RestoreWindowsDesktop(); Notifier.Show("دسکتاپ پیش‌فرض ویندوز بازگردانده شد."); return true; }
            string? studioPage = args.FirstOrDefault(a => a.StartsWith("--desktop-studio=", StringComparison.OrdinalIgnoreCase));
            if (studioPage != null) { DesktopFeature.ShowStudio(studioPage["--desktop-studio=".Length..]); return true; }
            // Self-test / studio: a module's safe standalone preview window (no AppBar, hooks or hotkeys).
            string? preview = args.FirstOrDefault(a => a.StartsWith("--desktop-preview=", StringComparison.OrdinalIgnoreCase));
            if (preview != null)
            {
                var module = DesktopFeature.Find(preview["--desktop-preview=".Length..]);
                var w = module?.CreatePreviewWindow(DesktopSettingsStore.Current.Clone());
                if (w != null) { w.Show(); return true; }
            }
        }
        catch (Exception ex) { AppLog.Error("Handling feature arguments failed", ex); }
        return false;
    }

    private void WireFeatureEvents(Views.ToolsDashboardWindow dashboard)
    {
        dashboard.OpenRecorderRequested += RecorderFeature.ShowWindow;
        dashboard.OpenFolderStudioRequested += () => FolderStudioFeature.ShowWindow();
    }

    private void AddFeatureTrayItems(System.Windows.Forms.ContextMenuStrip menu)
    {
        menu.Items.Add("🎥 ضبط صفحه (Ctrl+Alt+R)", null, (_, _) => RecorderFeature.ShowWindow());
        menu.Items.Add("📸 اسکرین‌شات (Ctrl+Alt+S)", null, (_, _) => RecorderFeature.TakeScreenshot());
        menu.Items.Add("🎨 استودیو پوشه: آیکون و قفل", null, (_, _) => FolderStudioFeature.ShowWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("🖥 استودیو دسکتاپ (نوار بالا، داک، پنجره‌ها)", null, (_, _) => DesktopFeature.ShowStudio());
        var l = DesktopSettingsStore.Current.Launcher;
        menu.Items.Add($"🔍 جستجوی سریع ({GlobalHotkeyService.FormatShortcut(l.HotkeyModifiers, l.HotkeyKey)})", null, (_, _) => DesktopFeature.ShowLauncher());
        menu.Items.Add("🎛 مرکز کنترل", null, (_, _) => DesktopEvents.RequestControlCenter());
        menu.Items.Add("↺ بازگردانی دسکتاپ ویندوز", null, (_, _) => DesktopFeature.RestoreWindowsDesktop());
    }
}
