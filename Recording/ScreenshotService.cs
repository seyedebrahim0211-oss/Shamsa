using System.Drawing.Imaging;
using Shamsa.Services;

namespace Shamsa.Recording;

/// <summary>GDI screen capture in physical pixels, saved as PNG/JPG and optionally copied to the clipboard.</summary>
internal static class ScreenshotService
{
    public static System.Drawing.Bitmap Capture(PixelRect rect) => RecorderNative.PerMonitor(() =>
    {
        var bmp = new System.Drawing.Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.X, rect.Y, 0, 0, new System.Drawing.Size(rect.Width, rect.Height), System.Drawing.CopyPixelOperation.SourceCopy);
        return bmp;
    });

    /// <summary>Captures, saves and (optionally) copies. Must run on the UI (STA) thread for the clipboard.</summary>
    public static string Save(PixelRect rect, RecorderSettings s)
    {
        if (rect.IsEmpty) throw new InvalidOperationException("ناحیه تصویر معتبر نیست.");
        using var bmp = Capture(rect);
        string folder = s.EffectiveScreenshotFolder;
        Directory.CreateDirectory(folder);
        string ext = s.ScreenshotJpg ? ".jpg" : ".png";
        string path = UniquePath(Path.Combine(folder, $"Shamsa_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{ext}"));
        if (s.ScreenshotJpg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            bmp.Save(path, codec, ep);
        }
        else bmp.Save(path, ImageFormat.Png);

        if (s.ScreenshotToClipboard)
        {
            for (int i = 0; i < 5; i++)
            {
                try { System.Windows.Forms.Clipboard.SetImage(bmp); break; }
                catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(40); }
            }
        }
        AppLog.Info("Screenshot saved: " + path);
        return path;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
