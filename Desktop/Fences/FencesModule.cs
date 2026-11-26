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
    };

    public void Apply(DesktopSettings settings)
    {
        if (!settings.Enabled || !settings.Fences.Enabled) { Stop(); return; }
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

public sealed record FenceItem(string Path, string Name, bool IsDirectory);

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
        Left = def.X; Top = def.Y; Width = Math.Max(200, def.Width);
        Height = def.Collapsed ? HeaderHeight + 6 : Math.Max(120, def.Height);
        MinHeight = HeaderHeight + 6;
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
        Render();
    }

    private IEnumerable<string> SourceFolders()
    {
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
        var list = new List<FenceItem>();
        var exts = new HashSet<string>(_def.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
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
                        if (_def.Mode != "folder")
                        {
                            if (dir) { if (!exts.Contains("folder")) continue; }
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
        return list.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

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
        var tint = DesktopTheme.ParseColor(def.TintHex, DesktopTheme.ParseColor(s.Theme.GlassTintHex, Color.FromRgb(0x0B, 0x13, 0x24)));
        tint.A = (byte)Math.Clamp(s.Theme.GlassOpacity * 2.55 * 0.9, 50, 240);
        var root = new DockPanel();

        // header
        var header = new Border
        {
            Height = HeaderHeight, Padding = new Thickness(12, 0, 8, 0), Cursor = Cursors.SizeAll,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(Math.Max(6, s.Theme.CornerRadius), Math.Max(6, s.Theme.CornerRadius), 0, 0)
        };
        var hp = new DockPanel();
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
            foreach (var item in items) grid.Children.Add(ItemTile(item, preview));
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

    private static FrameworkElement ItemTile(FenceItem item, bool preview)
    {
        var stack = new StackPanel { Width = 78, Margin = new Thickness(2) };
        var img = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 6, 0, 2), HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = new TextBlock
        {
            Text = GlyphFor(item), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30,
            Foreground = DesktopTheme.TextBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4)
        };
        var iconHost = new Grid();
        iconHost.Children.Add(glyph);
        iconHost.Children.Add(img);
        stack.Children.Add(iconHost);
        var name = new TextBlock
        {
            Text = item.Name, Foreground = DesktopTheme.TextBrush, FontSize = 11.5, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 6)
        };
        stack.Children.Add(name);
        var tile = new Border { Child = stack, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, ToolTip = preview ? null : item.Path };
        tile.MouseEnter += (_, _) => tile.Background = TileHover;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        if (!preview)
        {
            tile.Loaded += (_, _) => tile.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try
                {
                    BitmapSource? bmp = ShellThumbnailService.TryGetShellImage(item.Path, 48);
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
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("Fence open: " + ex.Message); }
    }

    private static void Reveal(string path)
    {
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
