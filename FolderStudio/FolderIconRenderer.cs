using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shamsa.FolderStudio;

public enum FolderFinish { Classic, Crystal, Girih, Minimal }
public enum IconOverlayKind { None, Emoji, Text, Image }
/// <summary>Badge = small picture on the folder; Full = picture fills the icon (cropped);
/// Fit = whole picture visible on a transparent square (best for logos and app icons).</summary>
public enum IconImageMode { Badge, Full, Fit }

/// <summary>Serializable description of a folder icon. Rendering is deterministic, so equal designs produce equal .ico files.</summary>
public sealed class IconDesign
{
    public string Name { get; set; } = "پوشه سفارشی";
    public FolderFinish Finish { get; set; } = FolderFinish.Classic;
    public string BaseHex { get; set; } = "#F4B740";
    /// <summary>Second gradient colour; empty = automatic soft gradient of <see cref="BaseHex"/>.</summary>
    public string GradientHex { get; set; } = string.Empty;
    public IconOverlayKind Overlay { get; set; } = IconOverlayKind.None;
    public string OverlayText { get; set; } = string.Empty;
    public string OverlayHex { get; set; } = "#FFFFFF";
    public bool OverlayBold { get; set; } = true;
    public string ImagePath { get; set; } = string.Empty;
    public IconImageMode ImageMode { get; set; } = IconImageMode.Badge;
    public bool RoundedImage { get; set; } = true;
    /// <summary>Search keywords (English + Persian) for presets.</summary>
    public string Keywords { get; set; } = string.Empty;

    public IconDesign Clone() => (IconDesign)MemberwiseClone();

    /// <summary>Stable identity used for favourites and to name the generated .ico.</summary>
    [JsonIgnore]
    public string Signature
    {
        get
        {
            string imageStamp = string.Empty;
            if (Overlay == IconOverlayKind.Image && File.Exists(ImagePath))
            {
                try
                {
                    var fi = new FileInfo(ImagePath);
                    imageStamp = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                }
                catch { }
            }
            // v2: high-quality resampling + more icon frames — old (soft) .ico files are regenerated.
            string raw = string.Join("\u001F", "v2", BaseHex, GradientHex, Overlay, OverlayText, OverlayHex, OverlayBold,
                imageStamp, ImageMode, RoundedImage);
            if (Finish != FolderFinish.Classic) raw += "\u001F" + Finish;
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
    }
}

/// <summary>Draws the Windows 11 style folder in WPF vectors and writes multi-size PNG-compressed .ico files.</summary>
public static class FolderIconRenderer
{
    /// <summary>Every size Explorer asks for at 100–200 % display scaling, so Windows never has to resample (blur) a frame.</summary>
    public static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 256 };
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji, Segoe UI Symbol");
    private static readonly FontFamily TextFont = new("Vazirmatn, Segoe UI, Tahoma");

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return fallback; }
    }

    public static bool IsValidHex(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && System.Text.RegularExpressions.Regex.IsMatch(hex.Trim(), "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    private static Color Shade(Color c, double factor)
    {
        // factor < 1 darkens, > 1 lightens towards white
        if (factor <= 1)
            return Color.FromArgb(c.A, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
        double t = Math.Min(1, factor - 1);
        return Color.FromArgb(c.A, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>Renders the design at <paramref name="size"/> px. Must run on an STA thread (WPF visuals).</summary>
    public static BitmapSource Render(IconDesign d, int size)
    {
        var visual = new DrawingVisual();
        // Pictures are drawn much smaller than their source: the default (linear) filter aliases and
        // blurs them. Fant/HighQuality averages every source pixel, which keeps photos crisp.
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Draw(dc, d, size);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Resamples <paramref name="image"/> so that, once drawn into a <paramref name="logical"/> rectangle (256-unit
    /// design space) of an icon rendered at <paramref name="pixelSize"/>, it is used at ~1:1 pixels. Big downscales
    /// are done in halving steps (each one a high-quality box filter), which looks far sharper than one huge jump.
    /// </summary>
    private static BitmapSource FitForIcon(BitmapSource image, Size logical, int pixelSize, bool fill)
    {
        try
        {
            double targetW = logical.Width * pixelSize / 256.0, targetH = logical.Height * pixelSize / 256.0;
            double scale = fill
                ? Math.Max(targetW / image.PixelWidth, targetH / image.PixelHeight)
                : Math.Min(targetW / image.PixelWidth, targetH / image.PixelHeight);
            if (scale >= 0.98) return image; // already small enough; WPF draws it directly
            BitmapSource current = image;
            while (scale < 0.5)
            {
                current = Resample(current, Math.Max(1, (int)Math.Round(current.PixelWidth * 0.5)), Math.Max(1, (int)Math.Round(current.PixelHeight * 0.5)));
                scale *= 2;
            }
            return Resample(current, Math.Max(1, (int)Math.Round(current.PixelWidth * scale)), Math.Max(1, (int)Math.Round(current.PixelHeight * scale)));
        }
        catch { return image; }
    }

    private static BitmapSource Resample(BitmapSource src, int w, int h)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Draw(DrawingContext dc, IconDesign d, int pixelSize)
    {
        var baseColor = ParseColor(d.BaseHex, Color.FromRgb(0xF4, 0xB7, 0x40));
        var image = d.Overlay == IconOverlayKind.Image ? LoadImage(d.ImagePath) : null;
        if (image != null)
        {
            var (box, fill) = d.ImageMode switch
            {
                IconImageMode.Fit => (new Size(248, 248), false),
                IconImageMode.Full => (new Size(236, 236), true),
                _ => (new Size(104, 104), true),
            };
            // The layout below only uses the picture's aspect ratio, which resampling keeps.
            image = FitForIcon(image, box, pixelSize, fill);
        }

        if (image != null && d.ImageMode == IconImageMode.Fit)
        {
            var rect = new Rect(4, 4, 248, 248);
            double scale = Math.Min(rect.Width / image.PixelWidth, rect.Height / image.PixelHeight);
            double w = image.PixelWidth * scale, h = image.PixelHeight * scale;
            var target = new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
            double radius = Math.Min(w, h) * 0.16;
            if (d.RoundedImage) dc.PushClip(new RectangleGeometry(target, radius, radius));
            dc.DrawImage(image, target);
            if (d.RoundedImage) dc.Pop();
            return;
        }

        if (image != null && d.ImageMode == IconImageMode.Full)
        {
            var rect = new Rect(10, 10, 236, 236);
            dc.PushClip(d.RoundedImage ? new RectangleGeometry(rect, 44, 44) : new RectangleGeometry(rect));
            DrawUniformToFill(dc, image, rect);
            dc.Pop();
            if (d.RoundedImage)
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 3), rect, 44, 44);
            return;
        }

        var topColor = IsValidHex(d.GradientHex) ? ParseColor(d.GradientHex, baseColor) : Shade(baseColor, 1.18);
        var bottomColor = IsValidHex(d.GradientHex) ? baseColor : Shade(baseColor, 0.86);

        // soft shadow
        bool small = pixelSize <= 24;
        if (!small)
        {
            for (int i = 3; i >= 1; i--)
            {
                var shadow = new SolidColorBrush(Color.FromArgb((byte)(16 * i), 0, 0, 0));
                dc.DrawRoundedRectangle(shadow, null, new Rect(20 - i * 2, 74 + i * 3, 216 + i * 4, 156 + i), 26, 26);
            }
        }

        // back panel with tab
        var back = new StreamGeometry();
        using (var g = back.Open())
        {
            g.BeginFigure(new Point(20, 62), true, true);
            g.ArcTo(new Point(42, 40), new Size(22, 22), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(96, 40), true, true);
            g.BezierTo(new Point(106, 40), new Point(110, 44), new Point(116, 51), true, true);
            g.LineTo(new Point(124, 60), true, true);
            g.BezierTo(new Point(128, 64), new Point(132, 66), new Point(140, 66), true, true);
            g.LineTo(new Point(214, 66), true, true);
            g.ArcTo(new Point(236, 88), new Size(22, 22), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(236, 206), true, true);
            g.ArcTo(new Point(214, 228), new Size(22, 22), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(new Point(42, 228), true, true);
            g.ArcTo(new Point(20, 206), new Size(22, 22), 0, false, SweepDirection.Clockwise, true, true);
        }
        back.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Shade(bottomColor, 0.78)), null, back);

        // paper sheet peeking out
        if (!small)
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 250, 250, 252)), null, new Rect(36, 76, 184, 60), 10, 10);

        // front panel
        var frontRect = new Rect(20, 92, 216, 136);
        var frontBrush = new LinearGradientBrush(topColor, bottomColor, new Point(0, 0), new Point(0, 1));
        dc.DrawRoundedRectangle(frontBrush, null, frontRect, 22, 22);
        // top highlight
        if (!small)
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 3),
                new Rect(22, 93.5, 212, 133), 21, 21);

        DrawFinish(dc, d.Finish, frontRect, topColor, bottomColor, small);

        var center = new Point(128, 162);
        switch (d.Overlay)
        {
            case IconOverlayKind.Emoji when !string.IsNullOrWhiteSpace(d.OverlayText):
                DrawGlyph(dc, d.OverlayText.Trim(), EmojiFont, 78, FontWeights.Normal, OverlayInk(d, bottomColor), center, pixelSize);
                break;
            case IconOverlayKind.Text when !string.IsNullOrWhiteSpace(d.OverlayText):
                string text = new string(d.OverlayText.Trim().Take(3).ToArray());
                double fontSize = text.Length switch { 1 => 88, 2 => 70, _ => 54 };
                DrawGlyph(dc, text, TextFont, fontSize, d.OverlayBold ? FontWeights.Bold : FontWeights.SemiBold, OverlayInk(d, bottomColor), center, pixelSize);
                break;
            case IconOverlayKind.Image when image != null:
                var badge = new Rect(center.X - 52, center.Y - 52, 104, 104);
                dc.PushClip(d.RoundedImage ? new RectangleGeometry(badge, 22, 22) : new RectangleGeometry(badge));
                DrawUniformToFill(dc, image, badge);
                dc.Pop();
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 4), badge,
                    d.RoundedImage ? 22 : 0, d.RoundedImage ? 22 : 0);
                break;
        }
    }

    private static void DrawFinish(DrawingContext dc, FolderFinish finish, Rect face, Color light, Color dark, bool small)
    {
        if (finish == FolderFinish.Classic) return;
        var clip = new RectangleGeometry(face, 22, 22);
        dc.PushClip(clip);
        if (finish == FolderFinish.Crystal)
        {
            dc.DrawRectangle(new LinearGradientBrush(Shade(light, 1.5), Shade(dark, 0.62), 62), null, face);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)), null,
                Geometry.Parse("M20,92 L224,92 20,180 Z"));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), null,
                Geometry.Parse("M150,228 L236,157 236,228 Z"));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 240, 253, 255)), 3),
                new Point(34, 98), new Point(213, 98));
        }
        else if (finish == FolderFinish.Girih)
        {
            var gold = new Pen(new SolidColorBrush(Color.FromRgb(242, 209, 140)), small ? 5 : 3);
            dc.DrawRoundedRectangle(null, gold, new Rect(26, 98, 204, 124), 17, 17);
            // Woven corner bands leave the centre clear for a badge, a photograph or Persian initials.
            dc.DrawGeometry(null, gold, Geometry.Parse("M20,149 L77,92 M20,169 L97,92 M34,92 L34,122 64,122 64,92 M180,228 L236,172 M160,228 L236,152 M190,228 L190,198 220,198 220,228"));
        }
        else if (finish == FolderFinish.Minimal)
        {
            dc.DrawRectangle(new SolidColorBrush(dark), null, face);
            dc.DrawRoundedRectangle(new SolidColorBrush(Shade(light, 1.55)), null, new Rect(45, 112, 56, 9), 4, 4);
        }
        dc.Pop();
    }

    private static Color OverlayInk(IconDesign d, Color panel)
    {
        if (IsValidHex(d.OverlayHex)) return ParseColor(d.OverlayHex, Colors.White);
        return Luminance(panel) > 0.62 ? Color.FromRgb(0x1F, 0x29, 0x37) : Colors.White;
    }

    private static void DrawGlyph(DrawingContext dc, string text, FontFamily family, double size, FontWeight weight, Color ink, Point center, int pixelSize)
    {
        bool rtl = text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﻿');
        var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        var culture = rtl ? CultureInfo.GetCultureInfo("fa-IR") : CultureInfo.InvariantCulture;
        FormattedText Make(double s, Color c) => new(text, culture, FlowDirection.LeftToRight, typeface, s, new SolidColorBrush(c), 1.0);

        var ft = Make(size, ink);
        const double maxW = 190; // fit inside the front panel
        if (ft.Width > maxW)
        {
            size *= maxW / ft.Width;
            ft = Make(size, ink);
        }
        var origin = new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2);
        if (pixelSize > 24)
            dc.DrawText(Make(size, Color.FromArgb(60, 0, 0, 0)), new Point(origin.X + 2, origin.Y + 3));
        dc.DrawText(ft, origin);
    }

    private static void DrawUniformToFill(DrawingContext dc, BitmapSource image, Rect target)
    {
        double scale = Math.Max(target.Width / image.PixelWidth, target.Height / image.PixelHeight);
        double w = image.PixelWidth * scale, h = image.PixelHeight * scale;
        dc.DrawImage(image, new Rect(target.X + (target.Width - w) / 2, target.Y + (target.Height - h) / 2, w, h));
    }

    private static readonly Dictionary<string, (DateTime Stamp, BitmapSource Image)> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pictures decoded directly by WPF/WIC (HEIC/AVIF/JXL work when the Windows codec is installed).</summary>
    private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".jpe", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp",
        ".heic", ".heif", ".avif", ".jxl", ".jxr", ".wdp", ".dds"
    };

    /// <summary>Files whose own icon is used (programs, libraries, shortcuts, icon files).</summary>
    private static readonly HashSet<string> IconSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ico", ".cur", ".exe", ".dll", ".lnk", ".icl", ".cpl", ".scr", ".url", ".appref-ms"
    };

    /// <summary>Formats Windows can usually thumbnail through the shell (vector, design and raw photo files).</summary>
    private static readonly HashSet<string> ShellThumbExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".psd", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".emf", ".wmf", ".pdf"
    };

    /// <summary>True for any picture, icon file, program, library or shortcut the app can turn into a folder icon.</summary>
    public static bool IsSupportedImage(string path)
    {
        string ext = Path.GetExtension(path);
        return RasterExtensions.Contains(ext) || IconSourceExtensions.Contains(ext) || ShellThumbExtensions.Contains(ext);
    }

    /// <summary>File-dialog filter covering everything <see cref="IsSupportedImage"/> accepts.</summary>
    public static string OpenFileFilter
    {
        get
        {
            static string Join(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*" + e));
            var all = RasterExtensions.Concat(IconSourceExtensions).Concat(ShellThumbExtensions);
            return $"همه عکس‌ها و آیکون‌ها|{Join(all)}|عکس‌ها|{Join(RasterExtensions)}|آیکون، برنامه و میانبر|{Join(IconSourceExtensions)}|سایر (SVG، PSD، RAW…)|{Join(ShellThumbExtensions)}|همه فایل‌ها|*.*";
        }
    }

    /// <summary>Best image mode for a source: icons/programs/logos keep their shape, photos fill the icon.</summary>
    public static IconImageMode SuggestMode(string path)
    {
        string ext = Path.GetExtension(path);
        if (IconSourceExtensions.Contains(ext) || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return IconImageMode.Fit;
        // Pictures with transparency (logos, PNG icons) look best uncropped.
        var img = LoadImage(path);
        return img != null && HasTransparentCorners(img) ? IconImageMode.Fit : IconImageMode.Full;
    }

    /// <summary>Phone photos store their orientation in EXIF; rotate so the icon is upright.</summary>
    private static BitmapSource ApplyExifRotation(string path, BitmapSource bmp)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("System.Photo.Orientation"))
            {
                double angle = Convert.ToUInt16(meta.GetQuery("System.Photo.Orientation")) switch
                {
                    3 => 180,
                    6 => 90,
                    8 => 270,
                    _ => 0
                };
                if (angle != 0) return new TransformedBitmap(bmp, new RotateTransform(angle));
            }
        }
        catch { }
        return bmp;
    }

    /// <summary>
    /// One step: turns any picture, icon, program or shortcut into a multi-size .ico and sets it on the folder.
    /// <paramref name="mode"/> null = automatic (logos/icons kept whole, photos fill the icon). STA thread.
    /// </summary>
    public static string ApplyImageToFolder(string folder, string imagePath, IconImageMode? mode = null, bool rounded = true)
    {
        if (LoadImage(imagePath) == null)
            throw new InvalidOperationException("این فایل به عکس یا آیکون تبدیل نشد.");
        var design = new IconDesign
        {
            Name = Path.GetFileNameWithoutExtension(imagePath),
            Overlay = IconOverlayKind.Image,
            ImagePath = imagePath,
            ImageMode = mode ?? SuggestMode(imagePath),
            RoundedImage = rounded
        };
        string ico = EnsureIcoFile(design);
        FolderIconService.ApplyIcon(folder, ico);
        try { FolderStudioStore.AddHistory(folder, design); } catch { }
        return ico;
    }

    private static bool HasTransparentCorners(BitmapSource img)
    {
        try
        {
            var src = img.Format == PixelFormats.Bgra32 ? img : new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w < 2 || h < 2) return false;
            var px = new byte[4];
            foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
            {
                src.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                if (px[3] < 32) return true;
            }
        }
        catch { }
        return false;
    }

    public static BitmapSource? LoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsSupportedImage(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (ImageCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Image;
            string ext = Path.GetExtension(path);
            BitmapSource? result = null;
            if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cur", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    result = decoder.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).First();
                }
                catch { result = null; }
            }
            else if (RasterExtensions.Contains(ext))
            {
                try
                {
                    // Decode so the SHORT side is at least 1024 px. A fixed width of 512 left wide/panorama
                    // pictures only ~130 px tall, which the "fill" crop then had to enlarge → blurry icons.
                    int decodeW = 0, decodeH = 0;
                    try
                    {
                        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                        int pw = frame.PixelWidth, ph = frame.PixelHeight;
                        const int shortSide = 1024;
                        if (pw > 0 && ph > 0 && Math.Min(pw, ph) > shortSide)
                        {
                            if (pw <= ph) decodeW = shortSide; else decodeH = shortSide;
                        }
                    }
                    catch { decodeW = 1024; }
                    BitmapImage Decode(bool ignoreProfile)
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        // Keep the colour profile (phone photos are often Display-P3); ignoring it washes colours out.
                        if (ignoreProfile) bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        if (decodeW > 0) bmp.DecodePixelWidth = decodeW;
                        if (decodeH > 0) bmp.DecodePixelHeight = decodeH;
                        bmp.StreamSource = fs;
                        bmp.EndInit();
                        return bmp;
                    }
                    BitmapImage decoded;
                    try { decoded = Decode(false); } catch { decoded = Decode(true); } // broken embedded profile
                    result = ApplyExifRotation(path, decoded);
                }
                catch { result = null; }
            }

            // Programs, shortcuts, SVG/PSD/RAW, or a picture WIC could not decode: ask the Windows shell.
            if (result == null)
            {
                bool iconOnly = IconSourceExtensions.Contains(ext);
                result = Services.ShellThumbnailService.TryGetShellImage(path, iconOnly ? 256 : 768, thumbnailOnly: !iconOnly, iconOnly: iconOnly);
            }
            if (result == null) return null;
            if (!result.IsFrozen) result.Freeze();
            if (ImageCache.Count > 32) ImageCache.Clear();
            ImageCache[path] = (stamp, result);
            return result;
        }
        catch (Exception ex)
        {
            Services.AppLog.Warn("Folder icon image load failed: " + ex.Message);
            return null;
        }
    }

    public static string IconsDirectory => Path.Combine(Services.AppPaths.DataDir, "FolderIcons");

    /// <summary>Creates (or reuses) %LocalAppData%\Shamsa\FolderIcons\&lt;sha1&gt;.ico for the design. STA thread.</summary>
    public static string EnsureIcoFile(IconDesign design)
    {
        Directory.CreateDirectory(IconsDirectory);
        string path = Path.Combine(IconsDirectory, design.Signature + ".ico");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        var frames = new List<byte[]>();
        foreach (int size in IcoSizes)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(design, size)));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            frames.Add(ms.ToArray());
        }

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            WriteIco(fs, IcoSizes, frames);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>ICO container with PNG-compressed images (supported since Windows Vista).</summary>
    public static void WriteIco(Stream output, IReadOnlyList<int> sizes, IReadOnlyList<byte[]> pngFrames)
    {
        using var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        w.Write((ushort)0);           // reserved
        w.Write((ushort)1);           // type: icon
        w.Write((ushort)sizes.Count); // count
        int offset = 6 + 16 * sizes.Count;
        for (int i = 0; i < sizes.Count; i++)
        {
            int s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s)); // width
            w.Write((byte)(s >= 256 ? 0 : s)); // height
            w.Write((byte)0);                  // palette
            w.Write((byte)0);                  // reserved
            w.Write((ushort)1);                // planes
            w.Write((ushort)32);               // bpp
            w.Write(pngFrames[i].Length);
            w.Write(offset);
            offset += pngFrames[i].Length;
        }
        foreach (var png in pngFrames) w.Write(png);
    }
}
