using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Views;

/// <summary>
/// Large, customisable glass card that shows a folder's first-level contents (Explorer-like grid,
/// media gallery or detailed list) when the mouse rests on a folder. Strictly read-only: nothing is
/// opened, executed, moved or recursed into; thumbnails come from the Windows thumbnail cache.
/// </summary>
public partial class FolderPreviewWindow : Window
{
    private static readonly Brush TileBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TileBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush NameBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF6)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA9, 0xB9, 0xCF)));
    private static readonly Brush GoldBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF6, 0xD3, 0x79)));

    private readonly StaWorker _worker = new("Shamsa.FolderCardSta");
    private readonly DispatcherTimer _demoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private IntPtr _hwnd;
    private long _request;
    private string? _currentFolder;
    private bool _glass;

    private sealed record PendingImage(FolderPreviewService.Entry Entry, Border Target, bool Hero, double Size);

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    public FolderPreviewWindow()
    {
        InitializeComponent();
        _demoTimer.Tick += (_, _) => { _demoTimer.Stop(); HidePreview(); };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeClickThroughToolWindow(_hwnd);
            _glass = NativeMethods.TryEnableGlassBackdrop(_hwnd);
            var src = HwndSource.FromHwnd(_hwnd);
            if (_glass)
            {
                Background = Brushes.Transparent;
                if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            else NativeMethods.TryRoundCorners(_hwnd);
            src?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg != NativeMethods.WM_NCHITTEST) return IntPtr.Zero;
                handled = true;
                return NativeMethods.HTTRANSPARENT;
            });
        };
    }

    public bool IsShowingFolder(string path) =>
        IsVisible && string.Equals(_currentFolder, path, StringComparison.OrdinalIgnoreCase);

    public void ShowPreview(HoverTarget target)
    {
        if (IsShowingFolder(target.FilePath)) return;
        _demoTimer.Stop();
        _currentFolder = target.FilePath;
        long request = ++_request;
        _ = LoadAsync(target, request);
    }

    /// <summary>Settings "try it" button: shows the card for a folder near the screen centre for a few seconds.</summary>
    public void ShowDemo(string folder)
    {
        HidePreview();
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
        var point = new Point(wa.Left + wa.Width * 0.30, wa.Top + wa.Height * 0.18);
        ShowPreview(new HoverTarget(folder, FileKind.Folder, point, Rect.Empty));
        _demoTimer.Start();
    }

    public void HidePreview()
    {
        _request++;
        _currentFolder = null;
        _demoTimer.Stop();
        if (IsVisible) Hide();
    }

    private async Task LoadAsync(HoverTarget target, long request)
    {
        var s = SettingsService.Current;
        try
        {
            var snap = await Task.Run(() => FolderPreviewService.InspectDetailed(target.FilePath, s.FolderPreviewMaxItems, s.FolderPreviewSort));
            if (request != _request) return;

            ApplyAppearance(s);
            var area = ScreenHelper.WorkAreaDipAt(this, target.CursorPos);
            double width = Math.Min(s.FolderPreviewWidth, area.Width * 0.92);
            var pending = BuildContent(snap, s, width);

            CardRoot.Measure(new Size(width - 34, double.PositiveInfinity));
            double height = Math.Min(CardRoot.DesiredSize.Height + 30, area.Height * 0.88);
            var pos = ComputeBestPosition(target, width, height, area);
            ApplyBounds(pos, width, height);

            bool wasVisible = IsVisible;
            if (!wasVisible) Show();
            PlayGrowAnimation(target, pos, width);

            await LoadImagesAsync(snap, pending, request);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Folder card failed: " + ex.Message);
            if (request == _request) HidePreview();
        }
    }

    // ------------------------------------------------------------------ appearance & layout

    private void ApplyAppearance(AppSettings s)
    {
        ThemeService.TryParseAccent(s.FolderPreviewTintHex, out var tint);
        byte alpha = (byte)Math.Round(255 * Math.Clamp(s.FolderPreviewTintOpacity, 10, 95) / 100.0);
        if (!_glass) alpha = (byte)Math.Max(alpha, (byte)235); // no OS blur → keep text readable
        var top = Color.FromArgb(alpha, tint.R, tint.G, tint.B);
        var bottom = Color.FromArgb((byte)Math.Min(255, alpha + 22), (byte)(tint.R * 0.75), (byte)(tint.G * 0.75), (byte)(tint.B * 0.8));
        TintLayer.Background = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(1, 1));
    }

    private List<PendingImage> BuildContent(FolderPreviewService.DetailedSnapshot snap, AppSettings s, double width)
    {
        var pending = new List<PendingImage>();
        FolderNameText.Text = snap.Name;
        FolderPathText.Text = snap.Folder;
        FolderIconImage.Source = null;

        ChipsPanel.Children.Clear();
        AddChip($"📁 {Fa(snap.Folders)} پوشه");
        AddChip($"📄 {Fa(snap.Files)} فایل");
        if (snap.ListedBytes > 0) AddChip($"💾 {FileKinds.FormatSize(snap.ListedBytes)}");
        if (snap.Warning != null) AddChip("⚠ " + snap.Warning, warn: true);

        ModifiedText.Text = snap.Modified > DateTime.MinValue ? "آخرین تغییر: " + ToPersianDate(snap.Modified) : string.Empty;
        int hidden = snap.Files + snap.Folders - snap.Entries.Count;
        MoreText.Text = hidden > 0 ? $"＋ {Fa(hidden)} مورد دیگر" : (snap.IsTruncated ? "نمایش بخشی از پوشه" : string.Empty);

        double contentW = width - 34;
        double radius = s.FolderPreviewCornerRadius;

        // Gallery hero mosaic
        HeroGrid.Children.Clear();
        HeroGrid.ColumnDefinitions.Clear();
        bool hero = s.FolderPreviewStyle == 1 && snap.Media.Count > 0;
        HeroGrid.Visibility = hero ? Visibility.Visible : Visibility.Collapsed;
        if (hero)
        {
            double heroH = Math.Round(contentW * (snap.Media.Count == 1 ? 0.42 : 0.30));
            HeroGrid.Height = heroH;
            for (int i = 0; i < snap.Media.Count; i++)
            {
                HeroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 && snap.Media.Count == 3 ? 2 : 1, GridUnitType.Star) });
                var cell = new Border
                {
                    CornerRadius = new CornerRadius(radius + 2),
                    Background = TileBackground,
                    BorderBrush = TileBorder,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, i == snap.Media.Count - 1 ? 0 : 4, 0),
                    FlowDirection = FlowDirection.LeftToRight
                };
                Grid.SetColumn(cell, i);
                var holder = new Grid();
                holder.Children.Add(cell);
                if (snap.Media[i].Kind == FileKind.Video) holder.Children.Add(VideoBadge(14));
                Grid.SetColumn(holder, i);
                HeroGrid.Children.Add(holder);
                pending.Add(new PendingImage(snap.Media[i], cell, true, Math.Max(heroH, contentW / snap.Media.Count)));
            }
        }

        TilesPanel.Children.Clear();
        ListPanel.Children.Clear();
        EmptyText.Visibility = snap.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = snap.Warning ?? "این پوشه خالی است.";

        if (s.FolderPreviewStyle == 2)
        {
            TilesPanel.Visibility = Visibility.Collapsed;
            ListPanel.Visibility = Visibility.Visible;
            foreach (var entry in snap.Entries) pending.Add(AddListRow(entry, radius));
        }
        else
        {
            ListPanel.Visibility = Visibility.Collapsed;
            TilesPanel.Visibility = Visibility.Visible;
            double tile = s.FolderPreviewTileSize;
            // stretch tiles slightly so the row fills the card edge to edge
            int perRow = Math.Max(2, (int)Math.Floor(contentW / (tile + 10)));
            double slot = Math.Floor(contentW / perRow);
            tile = slot - 10;
            foreach (var entry in snap.Entries) pending.Add(AddTile(entry, tile, slot, radius, s.FolderPreviewShowNames));
        }
        return pending;
    }

    private void AddChip(string text, bool warn = false)
    {
        ChipsPanel.Children.Add(new Border
        {
            Background = ChipBackground,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(0, 0, 6, 2),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = warn ? GoldBrush : NameBrush, FontWeight = FontWeights.SemiBold }
        });
    }

    private PendingImage AddTile(FolderPreviewService.Entry entry, double tile, double slot, double radius, bool showNames)
    {
        var face = new Border
        {
            Width = tile,
            Height = tile,
            CornerRadius = new CornerRadius(radius),
            Background = TileBackground,
            BorderBrush = TileBorder,
            BorderThickness = new Thickness(1),
            FlowDirection = FlowDirection.LeftToRight
        };
        var faceHost = new Grid { Width = tile, Height = tile };
        faceHost.Children.Add(face);
        if (entry.Kind == FileKind.Video) faceHost.Children.Add(VideoBadge(11));

        var stack = new StackPanel { Width = slot, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(faceHost);
        if (showNames)
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 10.5,
                Foreground = NameBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 30,
                Margin = new Thickness(3, 4, 3, 0),
                FlowDirection = DocumentPreviewService.HasRtl(entry.Name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            });
        }
        TilesPanel.Children.Add(stack);
        return new PendingImage(entry, face, false, tile);
    }

    private PendingImage AddListRow(FolderPreviewService.Entry entry, double radius)
    {
        var row = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var bg = new Border { CornerRadius = new CornerRadius(Math.Min(radius, 8)), Background = TileBackground };
        Grid.SetColumnSpan(bg, 4);
        row.Children.Add(bg);
        var icon = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(5), FlowDirection = FlowDirection.LeftToRight };
        row.Children.Add(icon);
        var name = new TextBlock
        {
            Text = entry.Name, Foreground = NameBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 8, 0),
            FlowDirection = DocumentPreviewService.HasRtl(entry.Name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var size = new TextBlock { Text = entry.IsFolder ? "پوشه" : FileKinds.FormatSize(entry.Size), Foreground = MutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        var date = new TextBlock { Text = ToPersianDate(entry.Modified), Foreground = MutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(date, 3);
        row.Children.Add(date);
        ListPanel.Children.Add(row);
        return new PendingImage(entry, icon, false, 24);
    }

    private static FrameworkElement VideoBadge(double size) => new Border
    {
        Width = size * 2, Height = size * 2, CornerRadius = new CornerRadius(size),
        Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x05, 0x0A, 0x16)),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(6), IsHitTestVisible = false,
        Child = new TextBlock { Text = "▶", Foreground = Brushes.White, FontSize = size * 0.9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(size * 0.15, 0, 0, 0) }
    };

    // ------------------------------------------------------------------ images (progressive, STA)

    private async Task LoadImagesAsync(FolderPreviewService.DetailedSnapshot snap, List<PendingImage> pending, long request)
    {
        double dpi = ScreenHelper.GetDpi(this).DpiScaleX;
        var icon = await _worker.RunAsync(() => ShellThumbnailService.TryGetShellImage(snap.Folder, (int)(96 * dpi), iconOnly: true));
        if (request != _request) return;
        FolderIconImage.Source = icon;

        // hero first (most visible), then tiles in reading order
        foreach (var item in pending.OrderByDescending(p => p.Hero))
        {
            if (request != _request) return;
            int px = (int)Math.Ceiling(item.Size * dpi);
            var (bmp, isThumb) = await _worker.RunAsync(() => LoadVisual(item.Entry, px, item.Hero));
            if (request != _request) return;
            if (bmp == null) continue;
            if (isThumb && item.Size > 30)
            {
                item.Target.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
            }
            else
            {
                item.Target.Child = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(item.Size > 30 ? item.Size * 0.16 : 0),
                };
                RenderOptions.SetBitmapScalingMode(item.Target.Child, BitmapScalingMode.HighQuality);
            }
        }
    }

    private static (BitmapSource? Bitmap, bool IsThumbnail) LoadVisual(FolderPreviewService.Entry entry, int px, bool hero)
    {
        try
        {
            if (entry.IsFolder)
                return (ShellThumbnailService.TryGetShellImage(entry.Path, Math.Max(32, px), iconOnly: true), false);

            if (hero && FileKinds.IsPreviewableImage(entry.Path))
            {
                using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = Math.Clamp(px, 120, 900);
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return (image, true);
            }
            if (px > 30)
            {
                var thumb = entry.Kind == FileKind.Video
                    ? ShellThumbnailService.TryGetVideoThumbnail(entry.Path, Math.Max(96, px))
                    : ShellThumbnailService.TryGetShellImage(entry.Path, Math.Max(64, px), thumbnailOnly: true);
                if (thumb != null) return (thumb, true);
            }
            return (ShellThumbnailService.TryGetShellImage(entry.Path, Math.Max(32, px), iconOnly: true), false);
        }
        catch { return (null, false); }
    }

    // ------------------------------------------------------------------ positioning & motion

    private Point ComputeBestPosition(HoverTarget target, double w, double h, Rect area)
    {
        var dip = ScreenHelper.PixelsToDip(this, target.CursorPos);
        var size = new Size(w, h);
        var candidates = new List<Point>(6);
        if (!target.ItemBounds.IsEmpty)
        {
            var item = new Rect(ScreenHelper.PixelsToDip(this, target.ItemBounds.TopLeft), ScreenHelper.PixelsToDip(this, target.ItemBounds.BottomRight));
            candidates.Add(new Point(item.Right + 18, item.Top - 20));
            candidates.Add(new Point(item.Left - 18 - w, item.Top - 20));
            candidates.Add(new Point(dip.X - w / 2, item.Bottom + 14));
            candidates.Add(new Point(dip.X - w / 2, item.Top - 14 - h));
        }
        candidates.Add(new Point(dip.X + 28, dip.Y - h / 3));
        candidates.Add(new Point(dip.X - 28 - w, dip.Y - h / 3));
        foreach (var raw in candidates)
        {
            var c = ScreenHelper.Clamp(raw, size, area);
            bool coversCursor = dip.X >= c.X - 12 && dip.X <= c.X + w + 12 && dip.Y >= c.Y - 12 && dip.Y <= c.Y + h + 12;
            if (!coversCursor) return c;
        }
        return ScreenHelper.Clamp(candidates[0], size, area);
    }

    private void ApplyBounds(Point pos, double w, double h)
    {
        BeginAnimation(TopProperty, null);
        Width = w;
        Height = h;
        Left = pos.X;
        Top = pos.Y;
        if (_hwnd != IntPtr.Zero)
        {
            var dpi = ScreenHelper.GetDpi(this);
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
                (int)Math.Round(pos.X * dpi.DpiScaleX), (int)Math.Round(pos.Y * dpi.DpiScaleY),
                (int)Math.Round(w * dpi.DpiScaleX), (int)Math.Round(h * dpi.DpiScaleY), NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void PlayGrowAnimation(HoverTarget target, Point pos, double width)
    {
        var s = SettingsService.Current;
        if (!s.FolderPreviewZoomAnimation)
        {
            CardRoot.BeginAnimation(OpacityProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CardRoot.Opacity = 1;
            CardScale.ScaleX = CardScale.ScaleY = 1;
            return;
        }
        // grow out of the side that faces the cursor (RTL layout mirrors the X origin)
        var cursor = ScreenHelper.PixelsToDip(this, target.CursorPos);
        bool cardRightOfCursor = pos.X + width / 2 > cursor.X;
        CardRoot.RenderTransformOrigin = new Point(cardRightOfCursor ? 1.0 : 0.0, 0.25);

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
        var dur = TimeSpan.FromMilliseconds(280);
        var scale = new DoubleAnimation(0.82, 1.0, dur) { EasingFunction = ease };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        CardRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    // ------------------------------------------------------------------ helpers

    private static string Fa(int n)
    {
        var s = n.ToString("#,0", CultureInfo.InvariantCulture);
        var chars = s.Select(c => c is >= '0' and <= '9' ? (char)('۰' + (c - '0')) : c == ',' ? '٬' : c).ToArray();
        return new string(chars);
    }

    private static string ToPersianDate(DateTime d)
    {
        if (d == DateTime.MinValue) return "-";
        var pc = new PersianCalendar();
        try
        {
            return $"{pc.GetYear(d)}/{pc.GetMonth(d):00}/{pc.GetDayOfMonth(d):00}";
        }
        catch { return d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _demoTimer.Stop();
        _worker.Dispose();
        base.OnClosed(e);
    }
}
