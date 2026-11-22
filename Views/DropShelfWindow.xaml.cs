using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class DropShelfWindow : Window
{
    public sealed class ShelfItem
    {
        public ShelfItem(string fullPath)
        {
            FullPath = fullPath;
            bool isDir = Directory.Exists(fullPath);
            FileName = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(FileName)) FileName = fullPath;
            Icon = ShellIcons.Get(fullPath);
            try { Detail = isDir ? "پوشه" : FileKinds.FormatSize(new FileInfo(fullPath).Length); }
            catch { Detail = string.Empty; }
        }

        public string FullPath { get; }
        public string FileName { get; }
        public string Detail { get; }
        public ImageSource? Icon { get; }
    }

    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromArgb(0x88, 0xE5, 0xB8, 0x5B));
    private static readonly Brush HoverBorder = new SolidColorBrush(Color.FromRgb(0x14, 0xB8, 0xA6));

    private readonly ObservableCollection<ShelfItem> _items = new();
    private readonly ConverterService _converter;
    private Point _dragStartPoint;
    private bool _dragArmed;

    public DropShelfWindow(ConverterService converter)
    {
        InitializeComponent();
        _converter = converter;
        FilesList.ItemsSource = _items;
        _items.CollectionChanged += Items_CollectionChanged;

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Top + (area.Height - Height) / 2;

        // restore last session's shelf (only items that still exist)
        foreach (var path in SettingsService.Current.ShelfItems.Where(p => File.Exists(p) || Directory.Exists(p)))
            _items.Add(new ShelfItem(path));
        UpdateHeader();
    }

    public int Count => _items.Count;

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateHeader();
        SettingsService.Current.ShelfItems = _items.Select(i => i.FullPath).ToList();
        SettingsService.Save();
    }

    private void UpdateHeader()
    {
        HeaderText.Text = _items.Count == 0 ? "🧺 قفسه موقت" : $"🧺 قفسه موقت ({_items.Count})";
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            bool exists = File.Exists(path) || Directory.Exists(path); // folders were silently ignored before
            if (exists && !_items.Any(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                _items.Add(new ShelfItem(path));
                added++;
            }
        }
        ShowShelf();
        if (added > 0) FilesList.ScrollIntoView(_items[^1]);
    }

    public void ShowShelf()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private List<ShelfItem> TargetItems() =>
        FilesList.SelectedItems.Count > 0 ? FilesList.SelectedItems.Cast<ShelfItem>().ToList() : _items.ToList();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        // COPY only: answering MOVE would make Explorer delete the originals after the drop.
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        Shell.BorderBrush = hasFiles ? HoverBorder : NormalBorder;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => Shell.BorderBrush = NormalBorder;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        Shell.BorderBrush = NormalBorder;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Effects = DragDropEffects.Copy;
            AddFiles(files);
        }
        e.Handled = true;
    }

    private void FilesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        // only arm a drag when pressing on an item, not on the scrollbar
        _dragArmed = e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.ListBoxItem>(d) != null;
    }

    private void FilesList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _items.Count == 0) return;

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        var targets = TargetItems();
        var data = new DataObject(DataFormats.FileDrop, targets.Select(x => x.FullPath).ToArray());
        var result = DragDrop.DoDragDrop(FilesList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

        // Items that were moved away no longer exist — drop them from the shelf.
        if (result.HasFlag(DragDropEffects.Move) || targets.Any(t => !File.Exists(t.FullPath) && !Directory.Exists(t.FullPath)))
            RemoveMissing();
    }

    private void RemoveMissing()
    {
        foreach (var item in _items.Where(i => !File.Exists(i.FullPath) && !Directory.Exists(i.FullPath)).ToList())
            _items.Remove(item);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is ShelfItem item) ShellActions.OpenPath(item.FullPath);
    }

    private async void ZipAll_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        try
        {
            var result = await _converter.ConvertFilesAsync(TargetItems().Select(x => x.FullPath), "zip");
            Notifier.Show(result.Message, result.Outputs.FirstOrDefault());
        }
        catch (Exception ex)
        {
            AppLog.Error("Shelf ZIP failed", ex);
            Notifier.Show("خطا در ساخت ZIP: " + ex.Message);
        }
    }

    private void CopyTo_Click(object sender, RoutedEventArgs e) => TransferTo(move: false);
    private void MoveTo_Click(object sender, RoutedEventArgs e) => TransferTo(move: true);

    private void TransferTo(bool move)
    {
        var targets = TargetItems();
        if (targets.Count == 0) return;
        string? dest = ShellActions.PickFolder();
        if (dest == null) return;

        int ok = 0, failed = 0;
        // Runs on the UI (STA) thread on purpose: the shell copy engine and its progress dialog require STA.
        {
            foreach (var item in targets)
            {
                try
                {
                    string target = ConverterService.GetUniquePath(Path.Combine(dest, item.FileName));
                    bool isDir = Directory.Exists(item.FullPath);
                    // VisualBasic FileSystem shows the native progress dialog for big copies
                    if (move)
                    {
                        if (isDir) FileSystem.MoveDirectory(item.FullPath, target, UIOption.AllDialogs);
                        else FileSystem.MoveFile(item.FullPath, target, UIOption.AllDialogs);
                    }
                    else
                    {
                        if (isDir) FileSystem.CopyDirectory(item.FullPath, target, UIOption.AllDialogs);
                        else FileSystem.CopyFile(item.FullPath, target, UIOption.AllDialogs);
                    }
                    ok++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn($"Shelf transfer failed for {item.FullPath}: {ex.Message}");
                }
            }
        }

        if (move) RemoveMissing();
        string verb = move ? "منتقل" : "کپی";
        Notifier.Show($"{ok} مورد {verb} شد" + (failed > 0 ? $" ({failed} خطا)" : "."), dest);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FilesList.SelectedItems.Cast<ShelfItem>().Take(10)) ShellActions.OpenPath(item.FullPath);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is ShelfItem item) ShellActions.RevealInExplorer(item.FullPath);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = TargetItems().Select(i => i.FullPath);
        ShellActions.TrySetClipboardText(string.Join(Environment.NewLine, paths));
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        foreach (var item in FilesList.SelectedItems.Cast<ShelfItem>().ToList()) _items.Remove(item);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelected();
        else if (e.Key == Key.Escape) Hide();
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) FilesList.SelectAll();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _items.Clear();
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
