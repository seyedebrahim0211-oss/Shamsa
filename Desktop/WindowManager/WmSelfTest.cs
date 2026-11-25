using System.Windows;

namespace Shamsa.Desktop.WindowManager;

/// <summary>Pure checks of the layout math (no windows are moved). Returns a list of failures.</summary>
public static class WmSelfTest
{
    public static List<string> Run()
    {
        var fails = new List<string>();
        var work = new Rect(0, 0, 1920, 1040);
        const int gap = 8, pad = 10;
        foreach (var kind in TileLayout.Cycle)
        {
            for (int n = 1; n <= 7; n++)
            {
                var rects = TileLayout.Compute(work, n, kind, gap, pad);
                if (rects.Count != n) { fails.Add($"{kind} n={n}: count {rects.Count}"); continue; }
                foreach (var r in rects)
                {
                    if (r.Left < pad - 0.5 || r.Top < pad - 0.5 || r.Right > work.Right - pad + 0.5 || r.Bottom > work.Bottom - pad + 0.5)
                        fails.Add($"{kind} n={n}: {r} outside padded area");
                }
                if (kind == TileLayoutKind.Monocle) continue;
                for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = rects[i]; var b = rects[j];
                    if (a.IntersectsWith(b) && Rect.Intersect(a, b).Width > 0.5 && Rect.Intersect(a, b).Height > 0.5)
                        fails.Add($"{kind} n={n}: tiles {i},{j} overlap");
                }
                // Area coverage: tiles + gaps should cover the padded area (allow rounding).
                double area = rects.Sum(r => r.Width * r.Height);
                double padded = (work.Width - 2 * pad) * (work.Height - 2 * pad);
                if (area > padded + 1 || area < padded * 0.85) fails.Add($"{kind} n={n}: coverage {area / padded:P0}");
            }
        }
        // Halves have exactly one gap between them.
        var l = TileLayout.Snap(work, SnapKind.Left, gap, pad);
        var r2 = TileLayout.Snap(work, SnapKind.Right, gap, pad);
        if (Math.Abs(r2.Left - l.Right - gap) > 1) fails.Add($"snap halves gap {r2.Left - l.Right}");
        if (Math.Abs(l.Left - pad) > 0.5) fails.Add($"snap left padding {l.Left}");
        var c = TileLayout.Snap(work, SnapKind.Center, gap, pad);
        if (Math.Abs(c.Width - 1344) > 1) fails.Add($"center width {c.Width}");
        return fails;
    }
}
