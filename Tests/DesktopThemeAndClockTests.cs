using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Shamsa.Desktop;
using Shamsa.Desktop.Fences;
using Shamsa.Desktop.Widgets;
using Shamsa.FolderStudio;
using Xunit;

namespace Shamsa.Tests;

public class DesktopThemeAndClockTests
{
    [Fact]
    public void PrayerAndWeatherUseTehranTimeByDefault()
    {
        var w = new WidgetsSettings { City = "بغداد", Latitude = 33.3152, Longitude = 44.3661 };
        Assert.Equal(TimeSpan.FromHours(3.5), CityClock.PrayerZone(w).BaseUtcOffset);
        Assert.Equal("تهران", CityClock.PrayerZoneName(w));
    }

    [Fact]
    public void PrayerZoneCanFollowSystemClock()
    {
        var w = new WidgetsSettings { PrayerWeatherZoneId = IpTimeZoneSync.SystemZoneToken };
        Assert.Equal(TimeZoneInfo.Local.Id, CityClock.PrayerZone(w).Id);
    }

    [Fact]
    public void SystemClockFollowsWindowsZoneWhenIpSyncIsOn()
    {
        var w = new WidgetsSettings { SyncSystemTimeZoneWithIp = true, ZoneId = "Asia/Baghdad" };
        Assert.Equal(TimeZoneInfo.Local.Id, CityClock.LocalZone(w).Id);
        Assert.True(Math.Abs((CityClock.Now(w) - DateTime.Now).TotalSeconds) < 5);
    }

    [Fact]
    public void ArrangedFencesStayInsideTheChosenMonitor()
    {
        var fences = FencesModule.SuggestedFences();
        var area = new Rect(0, 0, 1600, 860);
        FencesModule.Arrange(fences, area);
        foreach (var f in fences)
        {
            Assert.InRange(f.X, area.Left, area.Right - f.Width);
            Assert.InRange(f.Y, area.Top, area.Bottom);
        }
        // RTL: the first group starts at the right edge.
        Assert.True(fences[0].X > area.Width / 2);
    }

    [Fact]
    public void SuggestedFencesIncludeCatchAllGroup()
    {
        Assert.Contains(FencesModule.SuggestedFences(), f => f.Extensions.Contains(FencesModule.AnyExtension));
    }

    [Fact]
    public void ThemePresetsReferenceExistingIconThemes()
    {
        foreach (var p in DesktopThemePresets.All)
            Assert.Contains(Shamsa.Desktop.Personalize.SystemIcons.Themes, t => t.Id == p.IconThemeId);
        Assert.Equal(DesktopThemePresets.All.Count, DesktopThemePresets.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void FolderIconFromWidePhotoIsSharp()
    {
        // A wide 2000×500 photo with fine stripes: the old 512-px-wide decode left it 128 px tall and the
        // 256-px icon came out blurred. The new pipeline must keep high-frequency detail.
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            RunSta(() =>
            {
                const int w = 2000, h = 500;
                var px = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        byte v = (byte)(((x / 8) % 2 == 0) ? 255 : 0);
                        int i = (y * w + x) * 4;
                        px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
                    }
                var src = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
                using (var fs = File.Create(path)) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src)); enc.Save(fs); }

                // The full height must be decoded (the old fixed 512-px width gave only 128 px here).
                var decoded = FolderIconRenderer.LoadImage(path);
                Assert.NotNull(decoded);
                Assert.Equal(500, decoded!.PixelHeight);

                var design = new IconDesign { Overlay = IconOverlayKind.Image, ImagePath = path, ImageMode = IconImageMode.Full, RoundedImage = false };
                var icon = FolderIconRenderer.Render(design, 256);
                Assert.Equal(256, icon.PixelWidth);
                // 8-px stripes shown at ~0.47× are ~4 px wide: a sharp render keeps near-full contrast.
                var row = new byte[256 * 4];
                icon.CopyPixels(new Int32Rect(0, 128, 256, 1), row, 256 * 4, 0);
                int min = 255, max = 0;
                for (int x = 40; x < 216; x++) { int g = row[x * 4 + 1]; min = Math.Min(min, g); max = Math.Max(max, g); }
                Assert.True(max - min > 180, $"contrast {max - min}");
            });
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void IcoContainsEveryExplorerSize()
    {
        Assert.Contains(20, FolderIconRenderer.IcoSizes);
        Assert.Contains(40, FolderIconRenderer.IcoSizes);
        Assert.Contains(96, FolderIconRenderer.IcoSizes);
        Assert.Contains(256, FolderIconRenderer.IcoSizes);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.Message, error);
    }
}
