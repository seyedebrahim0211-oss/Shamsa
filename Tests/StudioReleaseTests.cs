using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shamsa.Desktop;
using Shamsa.Desktop.Fences;
using Shamsa.Desktop.Personalize;
using Xunit;

namespace Shamsa.Tests;

public class StudioReleaseTests
{
    [Fact]
    public void PhotoshopDocumentPreservesPixelValuesAndIndependentLayers()
    {
        DesktopThemeAndClockTests.RunSta(() =>
        {
            string dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                byte[] pixels = [15, 20, 200, 220, 245, 1];
                var frame = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Rgb24, null, pixels, 6);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frame));
                var original = Path.Combine(dir, "original.png");
                using (var dst = File.Create(original)) png.Save(dst);
                var output = Path.Combine(dir, "art.psd");
                WallpaperPsd.Export(original, output, Colors.Teal);
                byte[] psd = File.ReadAllBytes(output);
                Assert.Equal("8BPS", System.Text.Encoding.ASCII.GetString(psd, 0, 4));
                Assert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(psd.AsSpan(42, 2)));
                foreach (string layer in new[] { "Artwork", "Theme tint", "Edge shade" })
                    Assert.Contains(layer, System.Text.Encoding.ASCII.GetString(psd));
                var merged = WallpaperPsd.ReadComposite(new MemoryStream(psd));
                var actual = new byte[6]; merged.CopyPixels(actual, 6, 0);
                Assert.Equal(pixels, actual);
            }
            finally { Directory.Delete(dir, true); }
        });
    }

    [Fact]
    public void PhotoshopPackBitsChecksRowBounds()
    {
        byte[] row = new byte[6];
        WallpaperPsd.DecodeRow(new byte[] { 128, 1, 17, 18, 253, 63 }, row);
        Assert.Equal(new byte[] { 17, 18, 63, 63, 63, 63 }, row);
        Assert.Throws<InvalidDataException>(() => WallpaperPsd.DecodeRow(new byte[] { 249, 2 }, row));
        Assert.Throws<InvalidDataException>(() => WallpaperPsd.DecodeRow(new byte[] { 2, 2 }, row));
    }

    [Fact]
    public void RecentFolderRemovesExpiredFilesAndSortsNewestFirst()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var (name, days) in new[] { ("A", 2), ("B", 0), ("C", 9) })
            {
                string path = Path.Combine(dir, name); File.WriteAllText(path, name);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
            }
            var results = FenceWindow.LoadItems(new FenceDefinition { Mode = "recent", FolderPath = dir }, new DesktopSettings());
            Assert.Equal(new[] { "B", "A" }, results.Select(x => x.Name).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }
}
