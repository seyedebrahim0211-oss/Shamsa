using System.Windows;
using System.Windows.Media;

namespace Shamsa.Desktop.Fences;

internal static class FenceIconArt
{
    internal static readonly (string Id, string Name)[] Styles =
    {
        ("crystal", "بلور سه‌بعدی"), ("girih", "قاب گره‌چینی"), ("outline", "خطی مینیمال"),
        ("plate", "کاشی هم‌رنگ تم"), ("glyph", "نماد تک‌رنگ"), ("plain", "آیکون اصلی ویندوز")
    };

    // The same frozen drawing is shared by all tiles in one group. Only the foreground file icon varies.
    internal static DrawingImage? Plate(string style, Color accent)
    {
        if (style is not ("crystal" or "girih" or "outline")) return null;
        var shape = new DrawingGroup();
        var ink = new SolidColorBrush(accent);
        var dark = Color.FromRgb((byte)(accent.R / 5), (byte)(accent.G / 5), (byte)(accent.B / 5));
        var shimmer = new Pen(new SolidColorBrush(Color.FromArgb(178, 235, 252, 255)), 1);
        var face = new Rect(4, 3, 44, 44);
        using (var dc = shape.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 52, 52));
            if (style == "crystal")
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
