#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shamsa.Services;

namespace Shamsa.Views.Desktop;

public partial class SpotlightLauncherWindow : Window
{
    private static readonly (string Label, string Target)[] WindowsSettingsShortcuts =
    {
        ("تنظیمات وای‌فای (Wi-Fi)", "ms-settings:network-wifi"),
        ("تنظیمات بلوتوث (Bluetooth)", "ms-settings:bluetooth"),
        ("تنظیمات نمایشگر و روشنایی", "ms-settings:display"),
        ("نور شب (Night Light)", "ms-settings:nightlight"),
        ("صدا و میکروفون", "ms-settings:sound"),
        ("حالت مزاحم نشوید (Focus / DND)", "ms-settings:quiethours"),
        ("بروزرسانی ویندوز (Windows Update)", "ms-settings:windowsupdate"),
        ("برنامه‌های نصب‌شده", "ms-settings:appsfeatures")
    };

    public SpotlightLauncherWindow()
    {
        InitializeComponent();
        RefreshResults();
    }

    public void ShowAndFocus()
    {
        Show();
        Activate();
        QueryBox.Focus();
        QueryBox.SelectAll();
        RefreshResults();
    }

    private void RefreshResults()
    {
        if (ResultsList == null) return;
        ResultsList.Items.Clear();
        string q = QueryBox?.Text?.Trim() ?? string.Empty;

        // Clipboard History Tab
        if (TabClipboard?.IsChecked == true)
        {
            var clips = ShamsaProBootstrap.ClipboardHistory?.Search(q) ?? Array.Empty<Services.Desktop.ClipboardHistoryItem>();
            foreach (var clip in clips)
            {
                ResultsList.Items.Add(new ListBoxItem
                {
                    Content = $"📋 {clip.TextPreview}  ({clip.CopiedAt:HH:mm})",
                    Tag = ("CLIP", clip.FullText)
                });
            }
            if (ResultsList.Items.Count == 0)
                ResultsList.Items.Add("هنوز موردی در تاریخچه کلیپ‌بورد ثبت نشده است.");
            return;
        }

        // Math calculator evaluation
        if (!string.IsNullOrWhiteSpace(q) && q.Any(c => "+-*/()".Contains(c)) && q.All(c => char.IsDigit(c) || "+-*/(). ".Contains(c)))
        {
            try
            {
                var val = new DataTable().Compute(q, null);
                ResultsList.Items.Add(new ListBoxItem { Content = $"🧮 نتیجه محاسبه: {val}  (دابل‌کلیک برای کپی)", Tag = ("CLIP", val?.ToString() ?? "") });
            }
            catch { }
        }

        // Windows Settings search
        foreach (var item in WindowsSettingsShortcuts)
        {
            if (TabSettings?.IsChecked == true || string.IsNullOrWhiteSpace(q) || item.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
                ResultsList.Items.Add(new ListBoxItem { Content = $"⚙ {item.Label}", Tag = ("URI", item.Target) });
        }

        if (TabSettings?.IsChecked == true) return;

        // Start Menu Shortcuts search
        string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        if (Directory.Exists(startMenu) && !string.IsNullOrWhiteSpace(q))
        {
            foreach (var lnk in Directory.EnumerateFiles(startMenu, "*.lnk", SearchOption.AllDirectories)
                         .Where(f => Path.GetFileNameWithoutExtension(f).Contains(q, StringComparison.OrdinalIgnoreCase))
                         .Take(15))
            {
                ResultsList.Items.Add(new ListBoxItem { Content = $"🚀 {Path.GetFileNameWithoutExtension(lnk)}", Tag = ("URI", lnk) });
            }
        }
    }

    private void ResultsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not ListBoxItem item || item.Tag is not ValueTuple<string, string> action) return;
        if (action.Item1 == "CLIP")
        {
            Clipboard.SetText(action.Item2);
            Hide();
        }
        else if (action.Item1 == "URI")
        {
            Process.Start(new ProcessStartInfo { FileName = action.Item2, UseShellExecute = true });
            Hide();
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshResults();
    private void ModeChanged(object sender, RoutedEventArgs e) => RefreshResults();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }
}
