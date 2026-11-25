using System.Windows;

namespace Shamsa.Desktop.WindowManager;

public enum TileLayoutKind { Bsp, Columns, Rows, Monocle, MainStack }

public enum SnapKind { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight, Full, Center }

/// <summary>Pure layout math (no Win32) — easy to test and reused by the preview.</summary>
public static class TileLayout
{
    public static readonly TileLayoutKind[] Cycle =
        { TileLayoutKind.Bsp, TileLayoutKind.Columns, TileLayoutKind.Rows, TileLayoutKind.MainStack, TileLayoutKind.Monocle };

    public static TileLayoutKind Parse(string? name) =>
        Enum.TryParse<TileLayoutKind>(name, true, out var k) ? k : TileLayoutKind.Bsp;

    public static string PersianName(TileLayoutKind k) => k switch
    {
        TileLayoutKind.Bsp => "مارپیچی (BSP)",
        TileLayoutKind.Columns => "ستونی",
        TileLayoutKind.Rows => "ردیفی",
        TileLayoutKind.Monocle => "تک‌پنجره",
        TileLayoutKind.MainStack => "اصلی و پشته",
        _ => k.ToString()
    };

    public static TileLayoutKind Next(TileLayoutKind k)
    {
        int i = Array.IndexOf(Cycle, k);
        return Cycle[(i + 1) % Cycle.Length];
    }

    /// <summary>
    /// Splits <paramref name="workArea"/> into <paramref name="count"/> visual rects. Outer edges sit
    /// <paramref name="padding"/> from the work area; neighbours are <paramref name="gap"/> apart.
    /// </summary>
    public static List<Rect> Compute(Rect workArea, int count, TileLayoutKind kind, double gap, double padding)
    {
        var result = new List<Rect>(Math.Max(count, 0));
        if (count <= 0 || workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0) return result;
        double half = gap / 2;
        // Lay out cells in an area whose edges are padding-half from the work area, then shrink each cell by half a gap.
        var area = Inset(workArea, padding - half);
        var cells = new List<Rect>(count);
        switch (kind)
        {
            case TileLayoutKind.Monocle:
                for (int i = 0; i < count; i++) cells.Add(area);
                break;
            case TileLayoutKind.Columns:
                for (int i = 0; i < count; i++)
                    cells.Add(new Rect(area.X + area.Width * i / count, area.Y, area.Width / count, area.Height));
                break;
            case TileLayoutKind.Rows:
                for (int i = 0; i < count; i++)
                    cells.Add(new Rect(area.X, area.Y + area.Height * i / count, area.Width, area.Height / count));
                break;
            case TileLayoutKind.MainStack:
                if (count == 1) { cells.Add(area); break; }
                double mainW = area.Width * 0.55;
                cells.Add(new Rect(area.X, area.Y, mainW, area.Height));
                int stack = count - 1;
                for (int i = 0; i < stack; i++)
                    cells.Add(new Rect(area.X + mainW, area.Y + area.Height * i / stack, area.Width - mainW, area.Height / stack));
                break;
            default: // Bsp / dwindle: each window takes half of what is left, alternating by the longer side
                var rest = area;
                for (int i = 0; i < count; i++)
                {
                    if (i == count - 1) { cells.Add(rest); break; }
                    if (rest.Width >= rest.Height)
                    {
                        cells.Add(new Rect(rest.X, rest.Y, rest.Width / 2, rest.Height));
                        rest = new Rect(rest.X + rest.Width / 2, rest.Y, rest.Width / 2, rest.Height);
                    }
                    else
                    {
                        cells.Add(new Rect(rest.X, rest.Y, rest.Width, rest.Height / 2));
                        rest = new Rect(rest.X, rest.Y + rest.Height / 2, rest.Width, rest.Height / 2);
                    }
                }
                break;
        }
        foreach (var c in cells) result.Add(Round(Inset(c, half)));
        return result;
    }

    /// <summary>Target rect for a snap command inside a work area.</summary>
    public static Rect Snap(Rect workArea, SnapKind kind, double gap, double padding)
    {
        double half = gap / 2;
        var a = Inset(workArea, padding - half);
        double w2 = a.Width / 2, h2 = a.Height / 2;
        Rect cell = kind switch
        {
            SnapKind.Left => new Rect(a.X, a.Y, w2, a.Height),
            SnapKind.Right => new Rect(a.X + w2, a.Y, w2, a.Height),
            SnapKind.Top => new Rect(a.X, a.Y, a.Width, h2),
            SnapKind.Bottom => new Rect(a.X, a.Y + h2, a.Width, h2),
            SnapKind.TopLeft => new Rect(a.X, a.Y, w2, h2),
            SnapKind.TopRight => new Rect(a.X + w2, a.Y, w2, h2),
            SnapKind.BottomLeft => new Rect(a.X, a.Y + h2, w2, h2),
            SnapKind.BottomRight => new Rect(a.X + w2, a.Y + h2, w2, h2),
            SnapKind.Center => new Rect(workArea.X + workArea.Width * 0.15, workArea.Y + workArea.Height * 0.15,
                                        workArea.Width * 0.7, workArea.Height * 0.7),
            _ => a
        };
        return Round(kind == SnapKind.Center ? cell : Inset(cell, half));
    }

    public static Rect Inset(Rect r, double d)
    {
        double w = Math.Max(1, r.Width - 2 * d), h = Math.Max(1, r.Height - 2 * d);
        return new Rect(r.X + d, r.Y + d, w, h);
    }

    private static Rect Round(Rect r)
    {
        double x = Math.Round(r.X), y = Math.Round(r.Y);
        return new Rect(x, y, Math.Max(1, Math.Round(r.Right) - x), Math.Max(1, Math.Round(r.Bottom) - y));
    }
}
