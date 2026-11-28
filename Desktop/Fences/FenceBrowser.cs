using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shamsa.Desktop.Launcher;
using Shamsa.Services;

namespace Shamsa.Desktop.Fences;

/// <summary>Searches the complete group but only builds an initial page of tiles.</summary>
internal sealed class FenceBrowser : DockPanel
{
    private const int PageSize = 48;
    private readonly Panel _tiles;
    private readonly Button _back = new() { Margin = new Thickness(6, 2, 6, 2) };
    private string _folder = "";
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button _more = new() { Content = "نمایش موارد بیشتر", Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _search = new() { Margin = new Thickness(8, 4, 8, 2), Padding = new Thickness(6, 2, 6, 2), ToolTip = "جست‌وجوی نام در این گروه" };
    private readonly FenceWindow? _host;
    private readonly DesktopSettings _settings;
    private readonly FenceDefinition _group;
    private readonly DrawingImage? _plate;
    private readonly (FenceItem Item, string Key)[] _entries;
    private readonly TextBlock _count;
    private int _revision;
    private int _limit;

    internal FenceBrowser(FenceDefinition group, DesktopSettings settings, List<FenceItem> items, FenceWindow? host, TextBlock count)
    {
        _group = group; _settings = settings; _host = host; _count = count;
        _tiles = group.Column != null ? new StackPanel() : new WrapPanel();
        _tiles.Margin = new Thickness(4);
        if (group.Column?.Folders.Contains(host?.OpenAppFolder ?? "") == true) _folder = host!.OpenAppFolder;
        else if (host != null) host.OpenAppFolder = "";
        _entries = items.Select(i => (i, LauncherIndex.Normalize(i.Name))).ToArray();
        _limit = host?.VisibleLimit ?? PageSize;
        _plate = FenceIconArt.Plate(settings.Fences.IconStyle, AppIconArt.DesktopAccent(settings));
        _search.Text = host?.Query ?? "";
        _search.Visibility = host?.SearchOpen == true ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_search, "جست‌وجو در " + group.Title);
        DockPanel.SetDock(_search, System.Windows.Controls.Dock.Top);
        Children.Add(_search);
        DockPanel.SetDock(_back, System.Windows.Controls.Dock.Top);
        _back.Visibility = Visibility.Collapsed;
        Children.Add(_back);
        _back.Click += (_, _) => Navigate("");
        var stack = new StackPanel();
        stack.Children.Add(_tiles); stack.Children.Add(_more);
        _scroll.Content = stack;
        Children.Add(_scroll);
        _search.TextChanged += (_, _) =>
        {
            _limit = PageSize;
            if (_host != null) { _host.Query = _search.Text; _host.VisibleLimit = _limit; }
            _scroll.ScrollToTop();
            Refill();
        };
        _more.Click += (_, _) =>
        {
            _limit += PageSize;
            if (_host != null) _host.VisibleLimit = _limit;
            Refill();
        };
        Loaded += (_, _) => { Refill(); _scroll.ScrollToVerticalOffset(host?.ScrollOffset ?? 0); };
        Unloaded += (_, _) => _revision++;
        _scroll.ScrollChanged += (_, _) => { if (_host != null && IsLoaded) _host.ScrollOffset = _scroll.VerticalOffset; };
    }

    private void Navigate(string folder)
    {
        _folder = folder; _limit = PageSize;
        if (_host != null) { _host.OpenAppFolder = folder; _host.VisibleLimit = _limit; }
        _search.Clear(); _scroll.ScrollToTop(); Refill();
    }

    internal void Search(bool show)
    {
        if (_host == null) return;
        _host.SearchOpen = show;
        _search.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { _search.Focus(); _search.SelectAll(); } else { _search.Clear(); }
    }

    private void Refill()
    {
        int revision = ++_revision;
        _tiles.Children.Clear();
        var query = LauncherIndex.Normalize(_search.Text);
        var column = _group.Column;
        _back.Visibility = _folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _back.Content = "بازگشت به ستون  ·  " + _folder;
        int foldersShown = 0;
        if (column != null && _folder.Length == 0)
            foreach (var folder in column.Folders.Where(n => LauncherIndex.Normalize(n).Contains(query, StringComparison.Ordinal)))
            {
                int count = column.Locations.Values.Count(n => n == folder);
                var button = Widgets.StudioUi.Button("📁  " + folder + "  ·  " + DesktopTheme.Digits(count.ToString(), true), () => Navigate(folder));
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                _tiles.Children.Add(button); foldersShown++;
            }
        var hits = _entries.Where(e => e.Key.Contains(query, StringComparison.Ordinal) &&
            (column == null || (query.Length > 0 && _folder.Length == 0) || column.FolderOf(e.Item.Path) == _folder)).ToArray();
        _count.Text = DesktopTheme.Digits(query.Length == 0 && _folder.Length == 0 ? _entries.Length.ToString() : $"{hits.Length} / {_entries.Length}", true);
        var icons = new List<(FenceItem Item, Image Image, TextBlock Fallback)>();
        foreach (var hit in hits.Take(_limit))
        {
            var tile = FenceWindow.ItemTile(hit.Item, _settings, _host == null, _plate, out var image, out var fallback, row: column != null);
            _tiles.Children.Add(tile);
            if (_host != null && (_settings.Fences.IconStyle != "glyph" || AppIconArt.IsApp(hit.Item.Path))) icons.Add((hit.Item, image, fallback));
        }
        _more.Visibility = hits.Length > _limit ? Visibility.Visible : Visibility.Collapsed;
        if (hits.Length == 0 && foldersShown == 0)
        {
            string hint = query.Length > 0 ? "موردی با این نام پیدا نشد." : _group.Mode switch
            {
                "manual" => "فایل یا میانبر را داخل این گروه بکشید.",
                "folder" => "پوشه خالی است یا در دسترس نیست.",
                _ => "این گروه فعلاً خالی است."
            };
            _tiles.Children.Add(new TextBlock { Text = hint, Foreground = DesktopTheme.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) });
        }
        if (IsLoaded && icons.Count > 0) _ = LoadIconsAsync(icons, revision);
    }

    private async Task LoadIconsAsync(List<(FenceItem Item, Image Image, TextBlock Fallback)> icons, int revision)
    {
        int pixels = (int)Math.Clamp(Math.Ceiling(48 * VisualTreeHelper.GetDpi(this).DpiScaleX), 64, 192);
        foreach (var (item, image, fallback) in icons)
        {
            if (!IsLoaded || revision != _revision) return;
            try
            {
                var bitmap = await _host!.Worker.RunAsync(() => item.SystemDef is { } sys
                    ? Personalize.SystemIcons.LoadIcon(Personalize.SystemIcons.CurrentIconLocation(sys), pixels)
                    : (AppIconArt.IsApp(item.Path) ? AppIconArt.CustomImage(_settings, item.Path) : null) ?? ShellThumbnailService.TryGetShellImage(item.Path, pixels));
                if (revision != _revision || !IsLoaded) return;
                if (bitmap != null) { image.Source = bitmap; fallback.Visibility = Visibility.Collapsed; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLog.Warn("Group icon: " + ex.Message); }
        }
    }
}
