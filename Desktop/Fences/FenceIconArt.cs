using System.Windows;
using System.Windows.Media;

namespace Shamsa.Desktop.Fences;

internal static class FenceIconArt
{
    internal static readonly (string Id, string Name)[] Styles =
    {
        ("lens", "عدسی شیشه‌ای"), ("enamel", "مینای برجسته"), ("prismatic", "منشور نئونی"),
        ("crystal", "بلور سه‌بعدی"), ("girih", "قاب گره‌چینی"), ("outline", "خطی مینیمال"),
        ("plate", "کاشی هم‌رنگ تم"), ("glyph", "نماد تک‌رنگ"), ("plain", "آیکون اصلی ویندوز")
    };

    // The same frozen drawing is shared by all tiles in one group. Only the foreground file icon varies.
    internal static DrawingImage? Plate(string style, Color accent)
    {
        if (style is not ("crystal" or "girih" or "outline" or "lens" or "enamel" or "prismatic")) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue((style, accent), out var cached)) return cached;
            if (Cache.Count >= 160) Cache.Clear();
            return Cache[(style, accent)] = Draw(style, accent);
        }
    }

    private static readonly Dictionary<(string, Color), DrawingImage> Cache = new();

    private static DrawingImage Draw(string style, Color accent)
    {
        var shape = new DrawingGroup();
        var ink = new SolidColorBrush(accent);
        var dark = Color.FromRgb((byte)(accent.R / 5), (byte)(accent.G / 5), (byte)(accent.B / 5));
        var shimmer = new Pen(new SolidColorBrush(Color.FromArgb(178, 235, 252, 255)), 1);
        var face = new Rect(4, 3, 44, 44);
        using (var dc = shape.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 52, 52));
            if (style == "lens")
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(dark), null, new Rect(4, 7, 44, 42), 14, 14);
                var glass = new LinearGradientBrush();
                glass.StartPoint = new Point(0.15, 0); glass.EndPoint = new Point(0.8, 1);
                glass.GradientStops.Add(new GradientStop(Color.FromArgb(182, 232, 251, 255), 0));
                glass.GradientStops.Add(new GradientStop(accent, 0.32));
                glass.GradientStops.Add(new GradientStop(dark, 0.78));
                glass.GradientStops.Add(new GradientStop(accent, 1));
                dc.DrawRoundedRectangle(glass, shimmer, new Rect(4, 3, 44, 42), 14, 14);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(78, 255, 255, 255)), null,
                    Geometry.Parse("M8,19 Q8,6 20,6 L35,6 Q41,7 43,11 Q22,12 8,24 Z"));
            }
            else if (style == "enamel")
            {
                var gold = new Pen(new SolidColorBrush(Color.FromRgb(240, 206, 146)), 1.4);
                var medallion = Geometry.Parse("M26,2 L32,7 40,7 43,15 49,21 46,29 46,38 38,42 32,48 24,46 16,47 11,40 5,36 6,27 3,20 10,14 14,7 21,7 Z");
                dc.PushTransform(new TranslateTransform(0, 2)); dc.DrawGeometry(new SolidColorBrush(dark), gold, medallion); dc.Pop();
                dc.DrawGeometry(new LinearGradientBrush(accent, dark, 65), gold, medallion);
                dc.DrawEllipse(null, gold, new Point(26, 25), 17, 17);
                dc.DrawEllipse(null, shimmer, new Point(26, 25), 15.5, 15.5);
            }
            else if (style == "prismatic")
            {
                var edge = new Pen(ink, 1.8);
                dc.DrawGeometry(new SolidColorBrush(dark), edge, Geometry.Parse("M5,12 L14,4 41,4 47,10 47,39 38,47 11,47 5,41 Z"));
                dc.DrawGeometry(new LinearGradientBrush(accent, dark, 33), shimmer, Geometry.Parse("M7,12 L15,6 39,6 44,11 44,36 36,43 13,43 7,39 Z"));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(65, 12, 17, 38)), null, Geometry.Parse("M9,32 L38,7 43,12 43,35 35,42 13,42 Z"));
                dc.DrawLine(edge, new Point(16, 46), new Point(36, 46));
            }
            else if (style == "crystal")
            {
                var bevel = Geometry.Parse("M13,5 L38,5 47,13 47,38 40,46 15,49 5,40 5,15 Z");
                dc.DrawGeometry(new SolidColorBrush(dark), null, bevel);
                var front = Geometry.Parse("M12,3 L37,3 46,11 46,36 38,43 13,46 4,37 4,13 Z");
                dc.DrawGeometry(new LinearGradientBrush(accent, dark, 55), shimmer, front);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(73, 255, 255, 255)), null,
                    Geometry.Parse("M12,4 L36,4 44,11 7,26 5,14 Z"));
                dc.DrawLine(shimmer, new Point(14, 45), new Point(37, 42));
            }
            else if (style == "girih")
            {
                var gold = new Pen(new SolidColorBrush(Color.FromRgb(235, 201, 136)), 1.35);
                dc.DrawRoundedRectangle(new LinearGradientBrush(dark, accent, 58), gold, face, 8, 8);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(92, 255, 230, 170)), 0.9),
                    new Rect(7, 6, 38, 38), 6, 6);
                dc.DrawGeometry(null, gold, Geometry.Parse("M5,17 L17,5 M6,11 L12,11 12,5 M35,46 L47,34 M40,46 L40,39 47,39"));
            }
            else
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(135, dark.R, dark.G, dark.B)),
                    new Pen(ink, 1.4), new Rect(6, 5, 40, 40), 10, 10);
                dc.DrawLine(new Pen(ink, 2.4), new Point(20, 48), new Point(32, 48));
            }
        }
        var image = new DrawingImage(shape);
        image.Freeze();
        return image;
    }
}
