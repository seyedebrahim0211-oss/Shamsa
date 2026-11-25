#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class GlassFolderPreviewWindow : Window
{
    private string _currentFolder = string.Empty;
    private List<string> _allEntries = new();

    public GlassFolderPreviewWindow()
    {
        InitializeComponent();
        var cfg = ShamsaProSettingsService.Current.FolderPreview;
        Width = Math.Clamp(cfg.WindowWidth, 460, 1280);
        Height = Math.Clamp(cfg.WindowHeight, 340, 900);
        OpacitySlider.Value = Math.Clamp(cfg.GlassOpacity, 0.45, 0.98);
        GlassRoot.Opacity = OpacitySlider.Value;
    }

    public void LoadFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;
        _currentFolder = folderPath;
        FolderTitleText.Text = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));

        try
        {
            var dirs = Directory.GetDirectories(folderPath).Take(80).ToList();
            var files = Directory.GetFiles(folderPath).Take(160).ToList();
            _allEntries = dirs.Concat(files).ToList();
            FolderStatsText.Text = $"{dirs.Count} پوشه • {files.Count} فایل • {folderPath}";
            RenderFilteredList(string.Empty);
        }
        catch (Exception ex)
        {
            FolderStatsText.Text = $"خطا در خواندن پوشه: {ex.Message}";
        }
    }

    private void RenderFilteredList(string query)
    {
        var items = string.IsNullOrWhiteSpace(query)
            ? _allEntries
            : _allEntries.Where(p => Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        ItemsList.Items.Clear();
        foreach (var path in items)
        {
            bool isDir = Directory.Exists(path);
            string badge = isDir ? "📁" : GetFileBadge(path);
            ItemsList.Items.Add(new ListBoxItem
            {
                Content = $"{badge}  {Path.GetFileName(path)}",
                Tag = path,
                Padding = new Thickness(6, 4, 6, 4)
            });
        }
    }

    private static string GetFileBadge(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "📕",
        ".docx" or ".doc" => "📘",
        ".xlsx" or ".xls" => "📗",
        ".pptx" or ".ppt" => "📙",
        ".png" or ".jpg" or ".jpeg" or ".webp" => "🖼️",
        ".mp4" or ".mkv" or ".webm" => "🎬",
        ".shamsavault" => "🔒",
        _ => "📄"
    };

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ListBoxItem item || item.Tag is not string path) return;
        PreviewImage.Source = null;
        PreviewSnippetText.Text = string.Empty;

        if (Directory.Exists(path))
        {
            PreviewTitleText.Text = $"پوشه: {Path.GetFileName(path)}";
            PreviewSnippetText.Text = "برای باز کردن این پوشه دابل‌کلیک کنید.";
            return;
        }

        if (DocumentPreviewService.IsSupportedDocument(path))
        {
            var docPrev = DocumentPreviewService.GenerateFirstPagePreview(path, 512);
            PreviewTitleText.Text = docPrev.SummaryLabel;
            PreviewImage.Source = docPrev.ThumbnailImage;
            PreviewSnippetText.Text = string.IsNullOrWhiteSpace(docPrev.ExtractedFirstPageSnippet)
                ? "پیش‌نمایش صفحه اول بارگذاری شد."
                : docPrev.ExtractedFirstPageSnippet;
            return;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 420;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                PreviewImage.Source = bmp;
                PreviewTitleText.Text = Path.GetFileName(path);
            }
            catch { }
        }
        else
        {
            var fi = new FileInfo(path);
            PreviewTitleText.Text = fi.Name;
            PreviewSnippetText.Text = $"حجم: {fi.Length / 1024.0:0.0} KB\nآخرین تغییر: {fi.LastWriteTime:yyyy/MM/dd HH:mm}";
        }
    }

    private void ItemsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is not ListBoxItem item || item.Tag is not string path) return;
        if (Directory.Exists(path)) LoadFolder(path);
        else Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderFilteredList(SearchBox.Text);

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlassRoot != null) GlassRoot.Opacity = e.NewValue;
    }

    private void CustomizeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentFolder))
            ShamsaProBootstrap.OpenFolderCustomizer(_currentFolder);
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_currentFolder))
            Process.Start(new ProcessStartInfo { FileName = _currentFolder, UseShellExecute = true });
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        var s = ShamsaProSettingsService.Current;
        s.FolderPreview.WindowWidth = Width;
        s.FolderPreview.WindowHeight = Height;
        s.FolderPreview.GlassOpacity = OpacitySlider.Value;
        ShamsaProSettingsService.Save(s);
        Hide();
    }
}
