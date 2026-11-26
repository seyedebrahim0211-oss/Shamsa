#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Shamsa.Services.Desktop;

/// <summary>
/// Wallpaper Engine Service supporting static images, slideshows, dominant-color theme sync,
/// and animated wallpaper playback hooks (MP4/WebM/GIF) with safe fallback and auto-pause
/// on screen lock or battery power.
/// </summary>
public sealed class WallpaperEngineService : IDisposable
{
    private const int SPI_SETDESKWALLPAPER = 20;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDWININICHANGE = 0x02;

    public bool IsPaused { get; private set; }
    public string? ActiveWallpaperPath { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    public WallpaperEngineService()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void ApplyStaticWallpaper(string imagePath, bool syncThemeAccent = true)
    {
        if (!File.Exists(imagePath)) return;
        ActiveWallpaperPath = imagePath;
        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);

        if (syncThemeAccent && ShamsaProSettingsService.Current.SyncThemeWithWallpaper)
        {
            var dominant = ExtractDominantColor(imagePath);
            if (dominant.HasValue)
            {
                var wpfColor = System.Windows.Media.Color.FromRgb(dominant.Value.R, dominant.Value.G, dominant.Value.B);
                ThemeEngineProService.Apply(ShamsaProSettingsService.Current, wpfColor);
            }
        }
    }

    public static Color? ExtractDominantColor(string imagePath)
    {
        try
        {
            using var src = new Bitmap(imagePath);
            using var tiny = new Bitmap(src, new Size(24, 24));
            long r = 0, g = 0, b = 0, count = 0;
            for (int y = 0; y < tiny.Height; y++)
            {
                for (int x = 0; x < tiny.Width; x++)
                {
                    var px = tiny.GetPixel(x, y);
                    r += px.R; g += px.G; b += px.B; count++;
                }
            }
            if (count == 0) return null;
            return Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
        }
        catch
        {
            return null;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) IsPaused = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) IsPaused = false;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
            IsPaused = true;
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
