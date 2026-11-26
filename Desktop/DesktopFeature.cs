using System.Windows;
using System.Windows.Media;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop;

/// <summary>
/// Host of the Seelen-UI-style desktop environment: creates the modules, applies desktop.json,
/// owns the Windows-taskbar hiding and guarantees everything is undone on exit or on request.
/// </summary>
public static class DesktopFeature
{
    private static readonly List<IDesktopModule> Modules = new();
    private static DesktopStudioWindow? _studio;
    private static bool _initialised;

    public static IReadOnlyList<IDesktopModule> AllModules => Modules;

    public static IDesktopModule? Find(string id) => Modules.FirstOrDefault(m => m.Id == id);

    /// <summary>Applies the chosen UI font. Call before the first window is created.</summary>
    public static void ApplyFontEarly()
    {
        string name = DesktopSettingsStore.Current.UiFontFamily;
        if (string.IsNullOrWhiteSpace(name) || Application.Current == null) return;
        try { Application.Current.Resources["UiFont"] = new FontFamily(name + ", Segoe UI, Tahoma"); }
        catch (Exception ex) { AppLog.Warn("UI font not applied: " + ex.Message); }
    }

    public static void Initialize()
    {
        if (_initialised) return;
        _initialised = true;
        // A previous run that crashed while the taskbar was hidden: bring it back first.
        ShellInterop.RestoreTaskbarIfLeftHidden();
        foreach (var factory in ModuleFactories())
        {
            try { Modules.Add(factory()); }
            catch (Exception ex) { AppLog.Error("Desktop module could not be created", ex); }
        }
        DesktopEvents.StudioRequested += page => ShowStudio(page);
        DesktopSettingsStore.Changed += s => Application.Current?.Dispatcher.BeginInvoke(() => Apply(s));
        Apply(DesktopSettingsStore.Current);
    }

    private static IEnumerable<Func<IDesktopModule>> ModuleFactories()
    {
        yield return () => new TopBar.TopBarModule();
        yield return () => new Dock.DockModule();
        yield return () => new Launcher.LauncherModule();
        yield return () => new WindowManager.WindowManagerModule();
        yield return () => new Rules.RulesModule();
        yield return () => new Widgets.WidgetsModule();
        yield return () => new Fences.FencesModule();
        yield return () => new ControlCenter.ControlCenterModule();
        yield return () => new AutoTheme.AutoThemeModule();
        yield return () => new Voice.VoiceModule();
        yield return () => new Personalize.PersonalizeModule();
    }

    public static bool IsActive => DesktopSettingsStore.Current.Enabled && Modules.Any(m => m.IsRunning);

    public static void Apply(DesktopSettings s)
    {
        foreach (var m in Modules)
        {
            try { m.Apply(s); }
            catch (Exception ex) { AppLog.Error($"Desktop module {m.Id} failed to apply", ex); }
        }
        // Only hide the Windows taskbar when one of our replacements is actually on screen.
        bool replacementRunning = (Find("topbar")?.IsRunning ?? false) || (Find("dock")?.IsRunning ?? false);
        bool hide = s.Enabled && s.HideWindowsTaskbar && replacementRunning;
        if (hide != ShellInterop.IsTaskbarHidden) ShellInterop.SetWindowsTaskbarHidden(hide);
    }

    /// <summary>Turns the whole desktop environment off and restores the stock Windows desktop.</summary>
    public static void RestoreWindowsDesktop(bool persist = true)
    {
        foreach (var m in Modules)
        {
            try { m.Stop(); } catch (Exception ex) { AppLog.Warn($"Stopping {m.Id}: {ex.Message}"); }
        }
        ShellInterop.RestoreWindowsDesktop();
        if (persist)
        {
            var s = DesktopSettingsStore.Current.Clone();
            s.Enabled = false;
            s.HideWindowsTaskbar = false;
            DesktopSettingsStore.Save(s);
        }
    }

    public static void Shutdown()
    {
        foreach (var m in Modules)
        {
            try { m.Stop(); } catch { }
        }
        ShellInterop.RestoreWindowsDesktop();
    }

    /// <summary>Last-chance cleanup from crash handlers (no UI work).</summary>
    public static void EmergencyRestore()
    {
        try { ShellInterop.RestoreWindowsDesktop(); } catch { }
    }

    public static void ShowStudio(string? page = null)
    {
        // The «نگارخانه» main window hosts every studio page; the stand-alone window is only a fallback.
        if (Shamsa.Views.MainHubWindow.Opener is { } openHub)
        {
            openHub(string.IsNullOrWhiteSpace(page) ? "desktop.home" : page.Trim().ToLowerInvariant() == "personalize" ? "personalize" : "desktop." + page.Trim().ToLowerInvariant());
            return;
        }
        if (_studio == null || !_studio.IsLoaded)
        {
            _studio = new DesktopStudioWindow();
            _studio.Closed += (_, _) => _studio = null;
        }
        _studio.ShowPage(page);
        _studio.Show();
        if (_studio.WindowState == WindowState.Minimized) _studio.WindowState = WindowState.Normal;
        _studio.Activate();
    }

    public static void ShowLauncher() => DesktopEvents.RequestLauncher();
}
