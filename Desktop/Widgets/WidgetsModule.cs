using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shamsa.Services;

namespace Shamsa.Desktop.Widgets;

/// <summary>Desktop widgets: Persian calendar with holidays, prayer times, weather, sticky notes, clock.</summary>
public sealed class WidgetsModule : IDesktopModule
{
    private readonly Dictionary<string, WidgetWindow> _windows = new();

    public string Id => "widgets";
    public string DisplayName => "ویجت‌ها";
    public bool IsRunning => _windows.Count > 0;

    public static readonly string[] NoteColors = { "#F6D379", "#FDA4AF", "#86EFAC", "#93C5FD", "#C4B5FD", "#0B1324" };

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Enabled || !settings.Widgets.Enabled) { Stop(); return; }
        var wanted = settings.Widgets.Items.ToDictionary(i => i.Id);
        foreach (var id in _windows.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _windows[id].Close();
            _windows.Remove(id);
        }
        foreach (var p in settings.Widgets.Items)
        {
            try
            {
                if (_windows.TryGetValue(p.Id, out var existing))
                {
                    existing.UpdateFrom(p, settings);
                    continue;
                }
                var w = new WidgetWindow(p, settings, preview: false);
                w.BoundsCommitted += r => SaveBounds(p.Id, r);
                w.NoteChanged += text => Mutate(s => Find(s, p.Id)?.Let(x => x.Text = text));
                w.RemoveRequested += () => Mutate(s => s.Widgets.Items.RemoveAll(x => x.Id == p.Id));
                w.ColorRequested += hex => Mutate(s => Find(s, p.Id)?.Let(x => x.ColorHex = hex));
                w.LockToggled += () => Mutate(s => s.Widgets.LockPositions = !s.Widgets.LockPositions);
                _windows[p.Id] = w;
                w.Show();
            }
            catch (Exception ex) { AppLog.Error($"Widget {p.Kind} failed", ex); }
        }
    }

    private static WidgetPlacement? Find(DesktopSettings s, string id) => s.Widgets.Items.FirstOrDefault(x => x.Id == id);

    private static void SaveBounds(string id, Rect r) => Mutate(s =>
    {
        var p = Find(s, id);
        if (p == null) return;
        p.X = Math.Round(r.X); p.Y = Math.Round(r.Y); p.Width = Math.Round(r.Width); p.Height = Math.Round(r.Height);
    });

    internal static void Mutate(Action<DesktopSettings> change)
    {
        var s = DesktopSettingsStore.Current.Clone();
        change(s);
        DesktopSettingsStore.Save(s);
    }

    public void Stop()
    {
        foreach (var w in _windows.Values) { try { w.Close(); } catch { } }
        _windows.Clear();
    }

    /// <summary>Sample arrangement (calendar, prayer, note, clock) in a normal window; note text is fake.</summary>
    public Window? CreatePreviewWindow(DesktopSettings settings)
    {
        var s = settings.Clone();
        s.Widgets.WeatherOnline = false;
        var canvas = new WrapPanel { Margin = new Thickness(16) };
        foreach (var (kind, text) in new[] { ("calendar", (string?)null), ("prayer", null), ("clock", null), ("note", "خرید نان و شیر\nجلسه ساعت ۱۰\nتماس با علی"), ("weather", null) })
        {
            var p = new WidgetPlacement { Kind = kind, Text = text, ColorHex = kind == "note" ? "#F6D379" : null };
            var (w, h) = WidgetViews.DefaultSize(kind);
            var frame = WidgetWindow.Frame(WidgetViews.Build(p, s), p, s);
            frame.Width = w; frame.Height = h; frame.Margin = new Thickness(8);
            canvas.Children.Add(frame);
        }
        return new Window
        {
            Title = "پیش‌نمایش ویجت‌ها", Width = 1000, Height = 760, FlowDirection = FlowDirection.RightToLeft,
            Background = new LinearGradientBrush(Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0x0F, 0x76, 0x6E), 35),
            Content = new ScrollViewer { Content = canvas, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            FontFamily = DesktopTheme.Font
        };
    }
}

internal static class LetExtensions
{
    public static void Let<T>(this T value, Action<T> act) => act(value);
}

/// <summary>One desktop widget.</summary>
internal sealed class WidgetWindow : DesktopLevelWindow
{
    private WidgetPlacement _p;
    private DesktopSettings _s;
    private readonly bool _preview;

    public event Action<string>? NoteChanged;
    public event Action? RemoveRequested;
    public event Action<string>? ColorRequested;
    public event Action? LockToggled;

    public WidgetWindow(WidgetPlacement p, DesktopSettings s, bool preview)
    {
        _p = p; _s = s; _preview = preview;
        Title = "Shamsa widget " + p.Kind;
        var bounds = DesktopPlacement.Bounds(p);
        if (preview) { Left = p.X; Top = p.Y; Width = bounds.Width; Height = bounds.Height; }
        else ApplyBounds(bounds);
        Locked = s.Widgets.LockPositions;
        Render();
        ContextMenu = BuildMenu();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside<TextBox>(d)) return;
            if (e.OriginalSource is DependencyObject d2 && IsInside<Button>(d2)) return;
            BeginDrag(e);
        };
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur != null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T) return true;
        return false;
    }

    public void UpdateFrom(WidgetPlacement p, DesktopSettings s)
    {
        bool appearance = p.Kind != _p.Kind || p.ColorHex != _p.ColorHex ||
            s.Theme.GlassOpacity != _s.Theme.GlassOpacity || s.Theme.CornerRadius != _s.Theme.CornerRadius ||
            s.Theme.ShowBorders != _s.Theme.ShowBorders || s.Theme.AccentHex != _s.Theme.AccentHex ||
            s.Theme.GlassTintHex != _s.Theme.GlassTintHex;
        var w = s.Widgets;
        var old = _s.Widgets;
        bool dataChanged = w.City != old.City || w.Latitude != old.Latitude || w.Longitude != old.Longitude ||
            w.PrayerMethod != old.PrayerMethod || w.WeatherOnline != old.WeatherOnline ||
            w.PrayerWeatherZoneId != old.PrayerWeatherZoneId || w.EyvanFile != old.EyvanFile ||
            w.ZoneId != old.ZoneId || w.SyncSystemTimeZoneWithIp != old.SyncSystemTimeZoneWithIp ||
            !w.WorldZones.SequenceEqual(old.WorldZones);
        if (w.LockPositions != old.LockPositions) { Locked = w.LockPositions; }
        _p = p; _s = s;
        if (!_preview) ApplyBounds(DesktopPlacement.Bounds(p));
        if (w.LockPositions != old.LockPositions) ContextMenu = BuildMenu();
        if (appearance || (p.Kind != "note" && dataChanged)) Render();
    }

    private void Render() => Content = Frame(WidgetViews.Build(_p, _s, text => NoteChanged?.Invoke(text)), _p, _s);

    /// <summary>The glass frame around a widget's content (also used by previews).</summary>
    public static Border Frame(FrameworkElement content, WidgetPlacement p, DesktopSettings s)
    {
        Brush bg = DesktopTheme.GlassBrush(s);
        Brush fg = DesktopTheme.TextBrush;
        if (!string.IsNullOrEmpty(p.ColorHex))
        {
            var c = DesktopTheme.ParseColor(p.ColorHex, Colors.Transparent);
            if (c != Colors.Transparent)
            {
                c.A = (byte)(p.Kind == "note" ? 235 : Math.Clamp(s.Theme.GlassOpacity * 2.55, 60, 255));
                bg = new SolidColorBrush(c);
                // dark text on light note paper
                double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
                if (lum > 0.6) fg = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
            }
        }
        var border = new Border
        {
            Background = bg, CornerRadius = new CornerRadius(Math.Max(8, s.Theme.CornerRadius)), Padding = new Thickness(14, 12, 14, 12),
            BorderBrush = DesktopTheme.BorderBrush(s), BorderThickness = new Thickness(s.Theme.ShowBorders ? 1 : 0),
            Child = content, Margin = new Thickness(2)
        };
        if (!ReferenceEquals(fg, DesktopTheme.TextBrush)) ApplyForeground(content, fg);
        return border;
    }

    private static void ApplyForeground(DependencyObject root, Brush fg)
    {
        if (root is TextBlock tb) tb.Foreground = fg;
        if (root is TextBox box) { box.Foreground = fg; box.CaretBrush = fg; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) ApplyForeground(child, fg);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft, FontFamily = DesktopTheme.Font };
        var color = new MenuItem { Header = "رنگ" };
        color.Items.Add(Item("پیش‌فرض (شیشه)", () => ColorRequested?.Invoke("")));
        foreach (var hex in WidgetsModule.NoteColors)
        {
            var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(DesktopTheme.ParseColor(hex, Colors.Gray)) };
            var mi = new MenuItem { Header = hex, Icon = swatch, FlowDirection = FlowDirection.LeftToRight };
            string h = hex;
            mi.Click += (_, _) => ColorRequested?.Invoke(h);
            color.Items.Add(mi);
        }
        menu.Items.Add(color);
        var lockItem = Item(_s.Widgets.LockPositions ? "باز کردن قفل موقعیت" : "قفل موقعیت", () => LockToggled?.Invoke());
        menu.Items.Add(lockItem);
        menu.Items.Add(Item("تنظیمات ویجت‌ها…", () => DesktopEvents.RequestStudio("widgets")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("حذف این ویجت", () => RemoveRequested?.Invoke()));
        return menu;
    }

    private static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }
}
