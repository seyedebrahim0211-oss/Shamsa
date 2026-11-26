#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class FolderCustomizerWindow : Window
{
    private readonly List<string> _folders = new();

    public FolderCustomizerWindow()
    {
        InitializeComponent();
    }

    public void AddTargetFolder(string path)
    {
        if (!Directory.Exists(path) || _folders.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _folders.Add(path);
        TargetFoldersList.Items.Add(path);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var p in paths) AddTargetFolder(p);
            StatusText.Text = $"{_folders.Count} پوشه آماده شخصی‌سازی است.";
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "انتخاب پوشه برای شخصی‌سازی", Multiselect = true };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FolderNames) AddTargetFolder(f);
        }
    }

    private void ClearFolders_Click(object sender, RoutedEventArgs e)
    {
        _folders.Clear();
        TargetFoldersList.Items.Clear();
    }

    private void BuildAndApplyIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) { StatusText.Text = "لطفاً حداقل یک پوشه انتخاب کنید."; return; }
        try
        {
            string ico = FolderIconService.CreateCustomFolderIcon(
                BadgeTextBox.Text,
                TopColorBox.Text.Trim(),
                BottomColorBox.Text.Trim(),
                BadgeTextBox.Text);
            FolderIconService.ApplyIconToFolders(_folders, ico, BadgeTextBox.Text);
            StatusText.Text = $"آیکون سفارشی با موفقیت روی {_folders.Count} پوشه اعمال شد.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"خطا: {ex.Message}";
        }
    }

    private void PickImageIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) { StatusText.Text = "ابتدا پوشه مقصد را انتخاب کنید."; return; }
        var dlg = new OpenFileDialog { Filter = "تصاویر و آیکون|*.png;*.jpg;*.jpeg;*.ico" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                string ico = FolderIconService.ConvertImageToIco(dlg.FileName);
                FolderIconService.ApplyIconToFolders(_folders, ico, Path.GetFileNameWithoutExtension(dlg.FileName));
                StatusText.Text = $"آیکون از فایل تصویر ساخته و روی {_folders.Count} پوشه اعمال شد.";
            }
            catch (Exception ex) { StatusText.Text = $"خطا: {ex.Message}"; }
        }
    }

    private void ResetIcons_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _folders) FolderIconService.ResetFolderIcon(f);
        StatusText.Text = "آیکون پیش‌فرض ویندوز برای پوشه‌های انتخابی بازگردانی شد.";
    }

    private void ContextMenu_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "Shamsa.exe";
            FolderIconService.SetExplorerContextMenu(ContextMenuCheck.IsChecked == true, exe);
            StatusText.Text = ContextMenuCheck.IsChecked == true
                ? "گزینه نگارخانه به منوی کلیک‌راست پوشه‌ها اضافه شد."
                : "گزینه منوی کلیک‌راست حذف شد.";
        }
        catch (Exception ex) { StatusText.Text = $"خطا: {ex.Message}"; }
    }

    private void LockFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) { StatusText.Text = "پوشه‌ای برای قفل انتخاب نشده است."; return; }
        try
        {
            string vault = FolderVaultService.LockFolder(_folders[0], VaultPasswordBox.Password, RemoveOriginalCheck.IsChecked == true);
            StatusText.Text = $"پوشه با رمزگذاری AES-256 در مسیر {vault} قفل شد.";
        }
        catch (Exception ex) { StatusText.Text = $"خطا: {ex.Message}"; }
    }

    private void UnlockVault_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "فایل قفل‌شده نگارخانه|*.shamsavault" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                string dir = FolderVaultService.UnlockVault(dlg.FileName, VaultPasswordBox.Password);
                StatusText.Text = $"فایل با موفقیت در {dir} بازگشایی شد.";
            }
            catch (Exception ex) { StatusText.Text = $"خطا: {ex.Message}"; }
        }
    }
}
