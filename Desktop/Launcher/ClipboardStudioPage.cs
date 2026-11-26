using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shamsa.Desktop.Launcher;

/// <summary>Desktop Studio page for the clipboard history.</summary>
public static class ClipboardStudioPage
{
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var c = s.Clipboard;
        var root = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
        root.Children.Add(StudioUi.Title("تاریخچه کلیپ‌بورد"));
        root.Children.Add(StudioUi.Muted("هر متن، فایل یا تصویری که کپی می‌کنی در جستجوی سریع می‌ماند. با Ctrl+Alt+V یا تایپ «cb » یا «؛» در جستجو بازش کن؛ Enter می‌چسباند."));

        var card = StudioUi.Card(root, "رفتار");
        card.Children.Add(StudioUi.Toggle("فعال", "فقط وقتی جستجوی سریع هم روشن است کار می‌کند", c.Enabled, v => { c.Enabled = v; changed(); }));
        card.Children.Add(StudioUi.Slider("حداکثر موارد", 10, 200, c.MaxItems, v => { c.MaxItems = v; changed(); }));
        card.Children.Add(StudioUi.Toggle("نگه داشتن تصویرها", "تا ۸ مگاپیکسل؛ فقط در حافظه", c.KeepImages, v => { c.KeepImages = v; changed(); }));
        card.Children.Add(StudioUi.Toggle("ماندن پس از اجرای دوباره (رمزنگاری‌شده)",
            "با DPAPI ویندوز و فقط برای حساب کاربری خودت رمز می‌شود؛ خاموش = هیچ چیز روی دیسک نوشته نمی‌شود",
            c.PersistAcrossRestarts, v => { c.PersistAcrossRestarts = v; changed(); }));

        var privacy = StudioUi.Card(root, "حریم خصوصی", "برنامه‌های مدیریت رمز به‌صورت خودکار نادیده گرفته می‌شوند. هر خط نام یک فایل اجرایی:");
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, c.IgnoredProcesses), AcceptsReturn = true, MinHeight = 80,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Thickness(6, 4, 6, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        box.TextChanged += (_, _) =>
        {
            c.IgnoredProcesses = box.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            changed();
        };
        privacy.Children.Add(box);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = DesktopTheme.MutedBrush };
        void UpdateCount() => count.Text = DesktopTheme.Digits($"{ClipboardHistory.Items.Count} مورد در تاریخچه", true);
        UpdateCount();
        var clear = StudioUi.Button("پاک کردن تاریخچه (به‌جز سنجاق‌شده‌ها)", () => { ClipboardHistory.Clear(keepPinned: true); UpdateCount(); });
        var clearAll = StudioUi.Button("پاک کردن همه", () => { ClipboardHistory.Clear(keepPinned: false); UpdateCount(); });
        actions.Children.Add(clear);
        actions.Children.Add(clearAll);
        actions.Children.Add(count);
        privacy.Children.Add(actions);
        return root;
    }
}

/// <summary>Small shared builders for studio pages of the launcher/voice/dock folders (theme-resource aware).</summary>
internal static class StudioUi
{
    public static TextBlock Title(string text) =>
        new() { Text = text, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4), Foreground = DesktopTheme.TextBrush };

    public static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 10) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    public static StackPanel Card(Panel parent, string title, string? subtitle = null)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), Foreground = DesktopTheme.TextBrush });
        if (subtitle != null) p.Children.Add(Muted(subtitle));
        var b = new Border { Child = p, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        parent.Children.Add(b);
        return p;
    }

    public static UIElement Toggle(string label, string? hint, bool value, Action<bool> set)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (hint != null)
        {
            var h = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            h.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            text.Children.Add(h);
        }
        var cb = new CheckBox { IsChecked = value, Content = text, Margin = new Thickness(0, 5, 0, 5), Foreground = DesktopTheme.TextBrush, VerticalContentAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        return cb;
    }

    public static UIElement Slider(string label, int min, int max, int value, Action<int> set)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sl, 1); g.Children.Add(sl);
        var val = new TextBlock { Text = DesktopTheme.Digits(value.ToString(), true), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        val.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
        Grid.SetColumn(val, 2); g.Children.Add(val);
        sl.ValueChanged += (_, e) => { int v = (int)Math.Round(e.NewValue); val.Text = DesktopTheme.Digits(v.ToString(), true); set(v); };
        return g;
    }

    public static Button Button(string text, Action click, string style = "GhostButton")
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (Application.Current?.TryFindResource(style) is Style st && st.TargetType.IsInstanceOfType(b)) b.Style = st;
        b.Click += (_, _) => click();
        return b;
    }

    public static UIElement Labeled(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }
}
