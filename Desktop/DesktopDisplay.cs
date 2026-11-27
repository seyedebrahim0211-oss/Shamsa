using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Shamsa.Desktop;

/// <summary>
/// Which monitor the desktop environment lives on. Fences, widgets, the dock and the (single-monitor) top bar
/// all use the same display so a theme lands where the user's desktop icons are — not on whichever screen
/// Windows happens to call "primary". Empty <see cref="DesktopSettings.DisplayDevice"/> = Windows primary.
/// </summary>
public static class DesktopDisplay
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    /// <summary>Screens in Windows' own numbering (\\.\DISPLAY1, \\.\DISPLAY2 …), as shown in Settings › Display.</summary>
    public static Forms.Screen[] Screens()
    {
        try
        {
            return Forms.Screen.AllScreens
                .OrderBy(s => Number(s) ?? int.MaxValue)
                .ThenBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top)
                .ToArray();
        }
        catch { return Array.Empty<Forms.Screen>(); }
    }

    private static int? Number(Forms.Screen s)
    {
        string digits = new(s.DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out int n) ? n : null;
    }

    public static Forms.Screen Target(DesktopSettings s)
    {
        var all = Screens();
        if (!string.IsNullOrWhiteSpace(s.DisplayDevice))
        {
            var hit = all.FirstOrDefault(x => x.DeviceName.Equals(s.DisplayDevice, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return Forms.Screen.PrimaryScreen ?? (all.Length > 0 ? all[0] : Forms.Screen.AllScreens[0]);
    }

    /// <summary>HMONITOR of the target display.</summary>
    public static IntPtr Monitor(DesktopSettings s)
    {
        var b = Target(s).Bounds;
        return MonitorFromPoint(new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, 2 /* NEAREST */);
    }

    /// <summary>
    /// Pixels → WPF DIPs. The app is system-DPI aware, so every monitor uses the primary's scale in this process.
    /// </summary>
    private static double Scale
    {
        get
        {
            try
            {
                var p = Forms.Screen.PrimaryScreen;
                double dip = SystemParameters.PrimaryScreenWidth;
                return p != null && dip > 0 ? Math.Max(0.5, p.Bounds.Width / dip) : 1;
            }
            catch { return 1; }
        }
    }

    private static Rect ToDip(System.Drawing.Rectangle r)
    {
        double k = Scale;
        return new Rect(r.Left / k, r.Top / k, r.Width / k, r.Height / k);
    }

    /// <summary>Work area (without taskbar/AppBars) of the target display, in DIPs.</summary>
    public static Rect WorkAreaDip(DesktopSettings s)
    {
        try
        {
            var screen = Target(s);
            // The primary's work area from WPF is always current (AppBars just registered included).
            if (screen.Primary) return SystemParameters.WorkArea;
            return ToDip(screen.WorkingArea);
        }
        catch { return SystemParameters.WorkArea; }
    }

    /// <summary>The target work area with its origin at (0,0): the space fence/widget coordinates are stored in.</summary>
    public static Rect RelativeWorkArea(DesktopSettings s)
    {
        var a = WorkAreaDip(s);
        return new Rect(0, 0, a.Width, a.Height);
    }

    /// <summary>Placement space for groups and widgets, leaving visible overlay bars accessible.</summary>
    internal static Rect PlacementArea(DesktopSettings s)
    {
        var area = RelativeWorkArea(s);
        double left = 0, right = 0, top = 0, bottom = 0;
        if (s.TopBar.Enabled && !s.TopBar.ReserveSpace)
        {
            if (s.TopBar.Position == "Bottom") bottom = s.TopBar.Height; else top = s.TopBar.Height;
        }
        if (s.Dock.Enabled && !s.Dock.ReserveSpace && !s.Dock.AutoHide)
        {
            double thickness = s.Dock.IconSize + 20;
            switch (s.Dock.Position)
            {
                case "Left": left = thickness; break;
                case "Right": right = thickness; break;
                case "Top": top = Math.Max(top, thickness); break;
                default: bottom = Math.Max(bottom, thickness); break;
            }
        }
        return new Rect(left, top, Math.Max(1, area.Width - left - right), Math.Max(1, area.Height - top - bottom));
    }

    public static string Label(Forms.Screen sc)
    {
        int? n = Number(sc);
        string name = n.HasValue ? $"نمایشگر {n}" : sc.DeviceName;
        return DesktopTheme.Digits($"{name} — {sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? " (اصلی ویندوز)" : "")}", true);
    }

    /// <summary>Choices for a combo box: "" = primary, then each monitor by device name.</summary>
    public static (string Value, string Label)[] Options()
    {
        var list = new List<(string, string)> { ("", "نمایشگر اصلی ویندوز (پیش‌فرض)") };
        foreach (var sc in Screens()) list.Add((sc.DeviceName, Label(sc)));
        return list.ToArray();
    }

    /// <summary>Shows a big number on every monitor for two seconds so the user knows which is which.</summary>
    public static void Identify()
    {
        var windows = new List<Window>();
        foreach (var sc in Screens())
        {
            var r = ToDip(sc.Bounds);
            int? n = Number(sc);
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                Width = 260, Height = 220, Left = r.Left + (r.Width - 260) / 2, Top = r.Top + (r.Height - 220) / 2,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x13, 0x24)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x14, 0xB8, 0xA6)), BorderThickness = new Thickness(3),
                    Child = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = DesktopTheme.Digits((n ?? 0).ToString(), true), FontSize = 110, FontWeight = FontWeights.Bold,
                                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = DesktopTheme.Font },
                            new TextBlock { Text = sc.Primary ? "اصلی ویندوز" : "", FontSize = 16, Foreground = Brushes.LightGray,
                                HorizontalAlignment = HorizontalAlignment.Center, FontFamily = DesktopTheme.Font }
                        }
                    }
                }
            };
            w.Show();
            windows.Add(w);
        }
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        t.Tick += (_, _) => { t.Stop(); foreach (var w in windows) { try { w.Close(); } catch { } } };
        t.Start();
    }

    /// <summary>Studio row: monitor picker + identify button. <paramref name="set"/> receives the device name ("" = primary).</summary>
    public static FrameworkElement Picker(string current, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, label) in Options()) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (current ?? "")) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string v }) set(v); };
        var identify = new Button { Content = "شناسایی نمایشگرها", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        identify.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        identify.Click += (_, _) => Identify();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(combo);
        row.Children.Add(identify);
        return row;
    }
}
