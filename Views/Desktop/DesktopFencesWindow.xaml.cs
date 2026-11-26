#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shamsa.Services;

namespace Shamsa.Views.Desktop;

/// <summary>
/// Non-destructive Stardock Fences-style translucent desktop group.
/// Indexes and groups Desktop shortcuts/folders/media/documents without moving or deleting user files.
/// </summary>
public partial class DesktopFencesWindow : Window
{
    private List<string> _items = new();
    private bool _collapsed;
    private double _expandedHeight = 300;

    public DesktopFencesWindow()
    {
        InitializeComponent();
        LoadDesktopItems();
    }

    public void ToggleVisibility()
    {
        if (IsVisible) Hide();
        else Show();
    }

    private void LoadDesktopItems()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;

        _items = Directory.GetDirectories(desktop)
            .Concat(Directory.GetFiles(desktop))
            .Where(p => !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Take(80)
            .ToList();
        RenderItems(string.Empty);
    }

    private void RenderItems(string query)
    {
        FenceItemsList.Items.Clear();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _items
            : _items.Where(p => Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var path in filtered)
        {
            string icon = Directory.Exists(path) ? "📁" : DocumentPreviewService.IsSupportedDocument(path) ? "📄" : "🔹";
            FenceItemsList.Items.Add(new ListBoxItem { Content = $"{icon} {Path.GetFileName(path)}", Tag = path });
        }
    }

    private void AutoGroupDesktop_Click(object sender, RoutedEventArgs e)
    {
        LoadDesktopItems();
        FenceTitleText.Text = $"📦 دسکتاپ ({_items.Count} مورد)";
    }

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        if (_collapsed)
        {
            _expandedHeight = Height;
            Height = 46;
            CollapseBtn.Content = "▸";
        }
        else
        {
            Height = _expandedHeight;
            CollapseBtn.Content = "▾";
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderItems(FilterBox.Text);

    private void FenceItemsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FenceItemsList.SelectedItem is not ListBoxItem item || item.Tag is not string path) return;
        if (Directory.Exists(path))
            ShamsaProBootstrap.ShowGlassFolderPreview(path);
        else
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
