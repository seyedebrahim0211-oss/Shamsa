using System.Windows;

namespace Shamsa.Desktop.Personalize;

/// <summary>
/// Background part of Windows personalization: runs the wallpaper slideshow / daily Bing timer.
/// Register it in DesktopFeature's module list (Id "personalize"). Stop() only stops the timer —
/// the user's Windows settings stay as chosen; resets are explicit actions on the studio page.
/// </summary>
public sealed class PersonalizeModule : IDesktopModule
{
    public string Id => "personalize";
    public string DisplayName => "شخصی‌سازی ویندوز";
    public bool IsRunning => WallpaperService.SlideshowRunning;

    public void Apply(DesktopSettings settings)
    {
        var ss = PersonalizeStore.Data.Slideshow;
        if (ss.Enabled || ss.BingDaily) { if (!WallpaperService.SlideshowRunning) WallpaperService.StartSlideshow(); }
        else WallpaperService.StopSlideshow();
    }

    public void Stop() => WallpaperService.StopSlideshow();

    public Window? CreatePreviewWindow(DesktopSettings settings) => null;
}
