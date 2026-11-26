using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shamsa.Desktop.Widgets;
using Shamsa.Services;

namespace Shamsa.Desktop.Fences;

/// <summary>
/// Fences-style glass groups on the desktop. A fence either shows desktop items whose extension
/// matches a filter, or the content of a folder ("portal"). Real desktop icons are never hidden,
/// moved or deleted by Shamsa — fences are an extra, organised view.
/// </summary>
public sealed class FencesModule : IDesktopModule
{
    private readonly Dictionary<string, FenceWindow> _windows = new();

    public string Id => "fences";
    public string DisplayName => "گروه‌بندی دسکتاپ";
    public bool IsRunning => _windows.Count > 0;

    public static List<FenceDefinition> SuggestedFences() => new()
    {
        new() { Title = "تصاویر", Mode = "filter", Extensions = new() { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".svg" }, X = 60, Y = 60, TintHex = "#1E3A8A" },
        new() { Title = "اسناد", Mode = "filter", Extensions = new() { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".md", ".odt", ".csv" }, X = 440, Y = 60, TintHex = "#052E2B" },
        new() { Title = "برنامه‌ها و میانبرها", Mode = "filter", Extensions = new() { ".lnk", ".url", ".exe", ".appref-ms" }, X = 60, Y = 330, TintHex = "#3B0764" },
        new() { Title = "فشرده‌ها", Mode = "filter", Extensions = new() { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso" }, X = 440, Y = 330, Height = 180, TintHex = "#1F2937" },
        // Catch-all: folders, This PC / Recycle Bin and every file no other group takes, so nothing is lost
        // when the Windows icons are hidden behind the groups.
        new() { Title = "پوشه‌ها و سایر", Mode = "filter", Extensions = new() { AnyExtension }, X = 820, Y = 60, Height = 450, TintHex = "#0F3D3E" },
    };

    /// <summary>Filter value meaning "everything the other filter groups don't show".</summary>
    public const string AnyExtension = "*";

    /// <summary>
    /// Lays the given fences out in columns from the right edge (RTL) of the chosen monitor's work area.
    /// Coordinates are relative to that work area.
    /// </summary>
    public static void Arrange(IList<FenceDefinition> fences, Rect relativeArea)
    {
        const double Gap = 16;
        double x = relativeArea.Right - Gap, y = relativeArea.Top + Gap, colWidth = 0;
        foreach (var f in fences)
        {
            double h = Math.Min(f.Height, relativeArea.Height - 2 * Gap);
            if (y + h > relativeArea.Bottom - Gap && y > relativeArea.Top + Gap)
            {
                x -= colWidth + Gap; y = relativeArea.Top + Gap; colWidth = 0;
            }
            f.X = Math.Round(Math.Max(relativeArea.Left + Gap, x - f.Width));
            f.Y = Math.Round(y);
            y += h + Gap;
            colWidth = Math.Max(colWidth, f.Width);
        }
    }

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Enabled || !settings.Fences.Enabled) { Stop(); return; }
        DesktopIconsNative.SetHidden(settings.Fences.HideDesktopIcons && settings.Fences.Fences.Count > 0);
        var wanted = settings.Fences.Fences.ToDictionary(f => f.Id);
        foreach (var id in _windows.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _windows[id].Close();
            _windows.Remove(id);
        }
        foreach (var def in settings.Fences.Fences)
        {
            try
            {
                if (_windows.TryGetValue(def.Id, out var existing)) { existing.UpdateFrom(def, settings); continue; }
                var w = new FenceWindow(def, settings, sampleItems: null);
                string id = def.Id;
                w.BoundsCommitted += r => WidgetsModule.Mutate(s =>
                {
                    var f = s.Fences.Fences.FirstOrDefault(x => x.Id == id);
                    if (f == null) return;
                    f.X = Math.Round(r.X); f.Y = Math.Round(r.Y); f.Width = Math.Round(r.Width);
                    if (!f.Collapsed) f.Height = Math.Round(r.Height);
                });
                w.CollapseToggled += collapsed => WidgetsModule.Mutate(s =>
                {
                    var f = s.Fences.Fences.FirstOrDefault(x => x.Id == id);
                    if (f != null) f.Collapsed = collapsed;
                });
                w.RemoveRequested += () => WidgetsModule.Mutate(s => s.Fences.Fences.RemoveAll(x => x.Id == id));
                _windows[def.Id] = w;
                w.Show();
            }
            catch (Exception ex) { AppLog.Error($"Fence {def.Title} failed", ex); }
        }
    }

    public void Stop()
    {
        foreach (var w in _windows.Values) { try { w.Close(); } catch { } }
        _windows.Clear();
        DesktopIconsNative.SetHidden(false);
    }

    /// <summary>Preview with fake sample items so no real file names appear.</summary>
    public Window? CreatePreviewWindow(DesktopSettings settings)
    {
        var s = settings.Clone();
        var panel = new WrapPanel { Margin = new Thickness(16) };
        var samples = new (FenceDefinition Def, string[] Items)[]
        {
            (SuggestedFences()[0], new[] { "سفر شمال.jpg", "logo.png", "عکس پرسنلی.jpg", "نمودار.png", "طرح.svg" }),
            (SuggestedFences()[1], new[] { "گزارش ماهانه.pdf", "قرارداد.docx", "بودجه ۱۴۰۵.xlsx", "ارائه.pptx", "یادداشت.txt", "README.md" }),
            (SuggestedFences()[2], new[] { "Chrome.lnk", "Telegram.lnk", "Visual Studio Code.lnk", "Photoshop.lnk" }),
        };
        foreach (var (def, items) in samples)
        {
            var fake = items.Select(n => new FenceItem(n, n.EndsWith(".lnk") ? n[..^4] : n, false)).ToList();
            var content = FenceWindow.BuildBody(def, s, fake, null, null, preview: true);
            content.Width = 360; content.Height = 250; content.Margin = new Thickness(8);
            panel.Children.Add(content);
        }
        return new Window
        {
            Title = "پیش‌نمایش گروه‌بندی دسکتاپ", Width = 1180, Height = 620, FlowDirection = FlowDirection.RightToLeft,
            Background = new LinearGradientBrush(Color.FromRgb(0x0F, 0x76, 0x6E), Color.FromRgb(0x1E, 0x1B, 0x4B), 30),
            Content = panel, FontFamily = DesktopTheme.Font
        };
    }
}

public sealed record FenceItem(string Path, string Name, bool IsDirectory)
{
    public bool IsSystem => Path.StartsWith(FenceWindow.SystemPrefix, StringComparison.Ordinal);
    public Personalize.SystemIconDef? SystemDef =>
        IsSystem ? Personalize.SystemIcons.All.FirstOrDefault(d => d.Id == Path[FenceWindow.SystemPrefix.Length..]) : null;
}

internal sealed class FenceWindow : DesktopLevelWindow
{
    private FenceDefinition _def;
    private DesktopSettings _s;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private const double HeaderHeight = 34;

    public event Action<bool>? CollapseToggled;
    public event Action? RemoveRequested;

    public FenceWindow(FenceDefinition def, DesktopSettings s, List<FenceItem>? sampleItems)
    {
        _def = def; _s = s;
        Title = "Shamsa fence " + def.Title;
        Width = Math.Max(200, def.Width);
        Height = def.Collapsed ? HeaderHeight + 6 : Math.Max(120, def.Height);
        MinHeight = HeaderHeight + 6;
        PlaceRelative(def.X, def.Y); // on the monitor chosen for the desktop environment
        Locked = s.Fences.LockPositions;
        AllowDrop = true;
        Drop += OnDrop;
        _refresh.Tick += (_, _) => { _refresh.Stop(); Render(); };
        Closed += (_, _) => DisposeWatchers();
        Watch();
        Render();
    }

    public void UpdateFrom(FenceDefinition def, DesktopSettings s)
    {
        bool sourceChanged = def.Mode != _def.Mode || def.FolderPath != _def.FolderPath || !def.Extensions.SequenceEqual(_def.Extensions);
        _def = def; _s = s;
        Locked = s.Fences.LockPositions;
        if (sourceChanged) { DisposeWatchers(); Watch(); }
        Height = def.Collapsed ? HeaderHeight + 6 : Math.Max(120, def.Height);
        FollowArea(def.X, def.Y);
        Render();
    }

    private IEnumerable<string> SourceFolders()
    {
        if (_def.Mode == "manual") yield break;
        if (_def.Mode == "folder")
        {
            if (!string.IsNullOrEmpty(_def.FolderPath) && Directory.Exists(_def.FolderPath)) yield return _def.FolderPath;
            yield break;
        }
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(common)) yield return common;
    }

    private void Watch()
    {
        foreach (var folder in SourceFolders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
                FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _refresh.Stop(); _refresh.Start(); });
                w.Created += h; w.Deleted += h; w.Changed += h;
                w.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _refresh.Stop(); _refresh.Start(); });
                _watchers.Add(w);
            }
            catch (Exception ex) { AppLog.Warn("Fence watcher: " + ex.Message); }
        }
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
        _watchers.Clear();
    }

    private List<FenceItem> LoadItems()
    {
        if (_def.Mode == "manual")
            return _def.Members.OrderBy(x => x.Value).Select(x => new FenceItem(x.Key, x.Value, Directory.Exists(x.Key))).ToList();
        var list = new List<FenceItem>();
        static string Norm(string e) => e == FencesModule.AnyExtension ? e : e.StartsWith('.') ? e : "." + e;
        var exts = new HashSet<string>(_def.Extensions.Select(Norm), StringComparer.OrdinalIgnoreCase);
        bool catchAll = _def.Mode == "filter" && exts.Contains(FencesModule.AnyExtension);
        HashSet<string>? taken = null; // extensions shown by the other filter groups
        if (catchAll)
        {
            taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var other in _s.Fences.Fences.Where(f => f.Id != _def.Id && f.Mode == "filter"))
                foreach (var e in other.Extensions.Select(Norm)) if (e != FencesModule.AnyExtension) taken.Add(e);
            foreach (var other in _s.Fences.Fences.Where(f => f.Id != _def.Id && f.Mode == "manual"))
                foreach (var m in other.Members.Keys) taken.Add("path:" + m);
            if (_s.Fences.ShowSystemIcons) list.AddRange(SystemItems());
        }
        foreach (var folder in SourceFolders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    try
                    {
                        var attr = File.GetAttributes(path);
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        bool dir = (attr & FileAttributes.Directory) != 0;
                        if (catchAll)
                        {
                            if (taken!.Contains("path:" + path)) continue;
                            if (dir ? taken.Contains(".folder") : taken.Contains(Path.GetExtension(path))) continue;
                            if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        else if (_def.Mode != "folder")
                        {
                            if (dir) { if (!exts.Contains(".folder")) continue; }
                            else if (!exts.Contains(Path.GetExtension(path))) continue;
                        }
                        string name = dir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
                        if (!dir && !Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
                            name = Path.GetFileName(path);
                        list.Add(new FenceItem(path, name, dir));
                        if (list.Count >= 300) return list;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { AppLog.Warn("Fence listing: " + ex.Message); }
        }
        return list.OrderByDescending(i => i.IsSystem).ThenByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>This PC, Recycle Bin, … that the user shows on the Windows desktop (Personalize › system icons).</summary>
    private static IEnumerable<FenceItem> SystemItems()
    {
        foreach (var d in Personalize.SystemIcons.All)
        {
            bool visible;
            try { visible = Personalize.SystemIcons.IsVisible(d); } catch { visible = false; }
            if (visible) yield return new FenceItem(SystemPrefix + d.Id, d.Name, false);
        }
    }

    internal const string SystemPrefix = "shamsa-system:";

    private void Render()
    {
        var items = _def.Collapsed ? new List<FenceItem>() : LoadItems();
        Content = BuildBody(_def, _s, items, this, collapsed => ToggleCollapse(collapsed), preview: false);
    }

    private void ToggleCollapse(bool collapsed)
    {
        _def.Collapsed = collapsed;
        Height = collapsed ? HeaderHeight + 6 : Math.Max(120, _def.Height);
        CollapseToggled?.Invoke(collapsed);
        Render();
    }

    /// <summary>Builds the glass fence (header + icon grid). <paramref name="host"/> is null for previews.</summary>
    public static Border BuildBody(FenceDefinition def, DesktopSettings s, List<FenceItem> items, FenceWindow? host, Action<bool>? collapse, bool preview)
    {
        var themeTint = DesktopTheme.ParseColor(s.Theme.GlassTintHex, Color.FromRgb(0x0B, 0x13, 0x24));
        // With «هماهنگ با تم» every group wears the theme's glass; its own colour survives only as a small dot.
        var tint = s.Fences.FollowTheme ? themeTint : DesktopTheme.ParseColor(def.TintHex, themeTint);
        tint.A = (byte)Math.Clamp(s.Theme.GlassOpacity * 2.55 * 0.9, 50, 240);
        var accent = DesktopTheme.Accent(s);
        var root = new DockPanel();

        // header
        var header = new Border
        {
            Height = HeaderHeight, Padding = new Thickness(12, 0, 8, 0), Cursor = Cursors.SizeAll,
            Background = s.Fences.FollowTheme
                ? new LinearGradientBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B), Color.FromArgb(0x14, accent.R, accent.G, accent.B), 0)
                : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(Math.Max(6, s.Theme.CornerRadius), Math.Max(6, s.Theme.CornerRadius), 0, 0)
        };
        var hp = new DockPanel();
        if (s.Fences.FollowTheme && !string.IsNullOrEmpty(def.TintHex))
        {
            var own = DesktopTheme.ParseColor(def.TintHex, accent);
            var dot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Lighten(own, 0.45))
            };
            DockPanel.SetDock(dot, System.Windows.Controls.Dock.Left);
            hp.Children.Add(dot);
        }
        var count = new TextBlock
        {
            Text = def.Collapsed && !preview ? "▸" : DesktopTheme.Digits(items.Count.ToString(), true),
            Foreground = DesktopTheme.MutedBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 12
        };
        DockPanel.SetDock(count, System.Windows.Controls.Dock.Right);
        hp.Children.Add(count);
        hp.Children.Add(new TextBlock { Text = def.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Child = hp;
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);

        if (host != null)
        {
            header.MouseLeftButtonDown += (_, e) =>
            {
                // Roll-up: a click without movement (RollUpOnTitleClick) or a double-click; otherwise drag.
                if (!s.Fences.RollUpOnTitleClick && e.ClickCount == 2)
                {
                    collapse?.Invoke(!def.Collapsed);
                    e.Handled = true;
                    return;
                }
                double l = host.Left, t = host.Top;
                if (!host.Locked) host.StartDrag(e);
                if (s.Fences.RollUpOnTitleClick && e.ClickCount == 1 && Math.Abs(host.Left - l) < 2 && Math.Abs(host.Top - t) < 2)
                {
                    collapse?.Invoke(!def.Collapsed);
                    e.Handled = true;
                }
            };
            header.ContextMenu = host.BuildMenu();
        }

        if (!def.Collapsed || preview)
        {
            var grid = new WrapPanel { Margin = new Thickness(6) };
            foreach (var item in items) grid.Children.Add(ItemTile(item, s, preview));
            if (items.Count == 0)
            {
                grid.Children.Add(new TextBlock
                {
                    Text = def.Mode == "folder" ? "پوشه خالی است یا پیدا نشد." : "موردی با این پسوندها روی دسکتاپ نیست.",
                    Foreground = DesktopTheme.MutedBrush, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap
                });
            }
            root.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        return new Border
        {
            Background = new SolidColorBrush(tint), CornerRadius = new CornerRadius(Math.Max(6, s.Theme.CornerRadius)),
            BorderBrush = DesktopTheme.BorderBrush(s), BorderThickness = new Thickness(s.Theme.ShowBorders ? 1 : 0),
            Child = root, Margin = new Thickness(2)
        };
    }

    private static readonly SolidColorBrush TileHover = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    private static Color Lighten(Color c, double t) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    private static Color Darken(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static FrameworkElement ItemTile(FenceItem item, DesktopSettings s, bool preview)
    {
        string style = s.Fences.IconStyle;
        var accent = DesktopTheme.Accent(s);
        var stack = new StackPanel { Width = 80, Margin = new Thickness(2) };
        var img = new Image { Width = style == "plate" ? 34 : 40, Height = style == "plate" ? 34 : 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var glyph = new TextBlock
        {
            Text = GlyphFor(item), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = style == "plate" ? 24 : 30,
            Foreground = style == "glyph" ? new SolidColorBrush(Lighten(accent, 0.25)) : DesktopTheme.TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var iconHost = new Grid { Width = 52, Height = 52, Margin = new Thickness(0, 6, 0, 2), HorizontalAlignment = HorizontalAlignment.Center };
        if (style is "plate" or "glyph")
        {
            // A themed tile behind every icon: apps, files and folders all share the theme's shape and colour.
            var r = Math.Clamp(s.Theme.CornerRadius, 6, 18);
            iconHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(r),
                Background = style == "plate"
                    ? new LinearGradientBrush(Color.FromArgb(0xE0, accent.R, accent.G, accent.B), Color.FromArgb(0xE0, Darken(accent, 0.55).R, Darken(accent, 0.55).G, Darken(accent, 0.55).B), 90)
                    : new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            });
            if (style == "plate")
                iconHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(r, r, 0, 0), Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 1, 1, 0),
                    Background = new LinearGradientBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90),
                    IsHitTestVisible = false
                });
        }
        iconHost.Children.Add(glyph);
        if (style != "glyph") iconHost.Children.Add(img);
        stack.Children.Add(iconHost);
        var name = new TextBlock
        {
            Text = item.Name, Foreground = DesktopTheme.TextBrush, FontSize = 11.5, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 6)
        };
        stack.Children.Add(name);
        var tile = new Border { Child = stack, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, ToolTip = preview ? null : item.IsSystem ? item.Name : item.Path };
        tile.MouseEnter += (_, _) => tile.Background = TileHover;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        if (!preview)
        {
            if (style != "glyph")
                tile.Loaded += (_, _) => tile.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    try
                    {
                        BitmapSource? bmp;
                        if (item.SystemDef is { } sys)
                            // Shows the Personalize icon theme (if one is applied) — same look as the real desktop.
                            bmp = Personalize.SystemIcons.LoadIcon(Personalize.SystemIcons.CurrentIconLocation(sys), 64);
                        else
                            bmp = ShellThumbnailService.TryGetShellImage(item.Path, 64); // 64 px: sharp at 125–150 % scaling
                        if (bmp != null) { img.Source = bmp; glyph.Visibility = Visibility.Collapsed; }
                    }
                    catch { }
                });
            tile.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Open(item.Path); e.Handled = true; } };
            var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft, FontFamily = DesktopTheme.Font };
            var open = new MenuItem { Header = "باز کردن" };
            open.Click += (_, _) => Open(item.Path);
            var reveal = new MenuItem { Header = "نمایش در Explorer" };
            reveal.Click += (_, _) => Reveal(item.Path);
            menu.Items.Add(open);
            menu.Items.Add(reveal);
            tile.ContextMenu = menu;
        }
        return tile;
    }

    private static string GlyphFor(FenceItem item)
    {
        if (item.SystemDef is { } sys) return sys.Glyph;
        if (item.IsDirectory) return "";
        return Path.GetExtension(item.Path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" => "",
            ".pdf" or ".docx" or ".doc" or ".txt" or ".md" or ".odt" => "",
            ".xlsx" or ".xls" or ".csv" => "",
            ".pptx" or ".ppt" => "",
            ".lnk" or ".url" or ".exe" or ".appref-ms" => "",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso" => "",
            _ => "",
        };
    }

    private static void Open(string path)
    {
        if (path.StartsWith(SystemPrefix, StringComparison.Ordinal))
        {
            var sys = Personalize.SystemIcons.All.FirstOrDefault(d => d.Id == path[SystemPrefix.Length..]);
            if (sys != null)
                try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::" + sys.Clsid) { UseShellExecute = true }); }
                catch (Exception ex) { AppLog.Warn("Fence open system item: " + ex.Message); }
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("Fence open: " + ex.Message); }
    }

    private static void Reveal(string path)
    {
        if (path.StartsWith(SystemPrefix, StringComparison.Ordinal)) { Open(path); return; }
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { AppLog.Warn("Fence reveal: " + ex.Message); }
    }

    internal void StartDrag(MouseButtonEventArgs e) => BeginDrag(e);

    internal ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft, FontFamily = DesktopTheme.Font };
        var roll = new MenuItem { Header = _def.Collapsed ? "باز کردن گروه" : "جمع کردن گروه" };
        roll.Click += (_, _) => ToggleCollapse(!_def.Collapsed);
        menu.Items.Add(roll);
        if (_def.Mode == "folder" && !string.IsNullOrEmpty(_def.FolderPath))
        {
            var openFolder = new MenuItem { Header = "باز کردن پوشه" };
            openFolder.Click += (_, _) => Open(_def.FolderPath!);
            menu.Items.Add(openFolder);
        }
        var settings = new MenuItem { Header = "تنظیمات گروه‌بندی…" };
        settings.Click += (_, _) => DesktopEvents.RequestStudio("fences");
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "حذف این گروه (فایل‌ها دست نمی‌خورند)" };
        remove.Click += (_, _) => RemoveRequested?.Invoke();
        menu.Items.Add(remove);
        return menu;
    }

    /// <summary>Dropping onto a folder fence moves the files into that folder after confirmation.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_def.Mode == "manual" && e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            WidgetsModule.Mutate(s =>
            {
                var group = s.Fences.Fences.FirstOrDefault(x => x.Id == _def.Id);
                if (group == null) return;
                foreach (var path in dropped) group.Members[path] = Path.GetFileName(path);
            });
            e.Handled = true;
            return;
        }
        if (_def.Mode != "folder" || string.IsNullOrEmpty(_def.FolderPath) || !Directory.Exists(_def.FolderPath)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        string msg = files.Length == 1
            ? $"«{Path.GetFileName(files[0])}» به پوشه «{_def.Title}» منتقل شود؟"
            : $"{DesktopTheme.Digits(files.Length.ToString(), true)} مورد به پوشه «{_def.Title}» منتقل شود؟";
        if (MessageBox.Show(msg, "نگارخانه — گروه‌بندی دسکتاپ", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != MessageBoxResult.Yes) return;
        int failed = 0;
        foreach (var src in files)
        {
            try
            {
                string dest = Path.Combine(_def.FolderPath!, Path.GetFileName(src.TrimEnd('\\')));
                if (File.Exists(dest) || Directory.Exists(dest)) { failed++; continue; }
                if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest);
            }
            catch (Exception ex) { failed++; AppLog.Warn("Fence move: " + ex.Message); }
        }
        if (failed > 0)
            MessageBox.Show($"{DesktopTheme.Digits(failed.ToString(), true)} مورد منتقل نشد (نام تکراری یا دسترسی).", "نگارخانه", MessageBoxButton.OK,
                MessageBoxImage.Warning, MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        Render();
    }
}
