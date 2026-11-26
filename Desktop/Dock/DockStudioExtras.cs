using System.Windows;
using System.Windows.Controls;

namespace Shamsa.Desktop.Dock;

/// <summary>Studio helpers for dock options added after the main dock page.</summary>
public static class DockStudioExtras
{
    /// <summary>Toggle for live DWM window thumbnails on hover (DesktopSettings.DockLivePreviews).</summary>
    public static UIElement DockPreviewToggle(DesktopSettings s, Action changed)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "پیش‌نمایش زنده پنجره‌ها هنگام اشاره", TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { Text = "تصویر زنده هر پنجره بالای آیکون؛ کلیک = رفتن به پنجره، ✕ = بستن", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.Children.Add(hint);
        var cb = new CheckBox { IsChecked = s.DockLivePreviews, Content = text, Margin = new Thickness(0, 5, 0, 5), Foreground = DesktopTheme.TextBrush, VerticalContentAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => { s.DockLivePreviews = true; changed(); };
        cb.Unchecked += (_, _) => { s.DockLivePreviews = false; changed(); };
        return cb;
    }
}
