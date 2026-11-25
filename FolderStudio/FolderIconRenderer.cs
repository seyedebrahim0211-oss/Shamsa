using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shamsa.FolderStudio;

public enum IconOverlayKind { None, Emoji, Text, Image }
public enum IconImageMode { Badge, Full }

/// <summary>Serializable description of a folder icon. Rendering is deterministic, so equal designs produce equal .ico files.</summary>
public sealed class IconDesign
{
    public string Name { get; set; } = "پوشه سفارشی";
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
            string raw = string.Join("\u001F", "v1", BaseHex, GradientHex, Overlay, OverlayText, OverlayHex, OverlayBold,
                imageStamp, ImageMode, RoundedImage);
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
    }
}

/// <summary>Draws the Windows 11 style folder in WPF vectors and writes multi-size PNG-compressed .ico files.</summary>
public static class FolderIconRenderer
{
    public static readonly int[] IcoSizes = { 16, 24, 32, 48, 64, 128, 256 };
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

    private static void Draw(DrawingContext dc, IconDesign d, int pixelSize)
    {
        var baseColor = ParseColor(d.BaseHex, Color.FromRgb(0xF4, 0xB7, 0x40));
        var image = d.Overlay == IconOverlayKind.Image ? LoadImage(d.ImagePath) : null;

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

    public static bool IsSupportedImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ico" or ".gif" or ".tif" or ".tiff" or ".webp";

    public static BitmapSource? LoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsSupportedImage(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (ImageCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Image;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapSource result;
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                var decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                result = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            else
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 512;
                bmp.StreamSource = fs;
                bmp.EndInit();
                result = bmp;
            }
            result.Freeze();
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
