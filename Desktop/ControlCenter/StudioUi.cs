using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shamsa.Desktop.ControlCenter;

/// <summary>Small RTL building blocks for the Desktop Studio pages of the control center, auto theme and plugins.</summary>
internal static class StudioUi
{
    public static StackPanel Page() => new() { FlowDirection = FlowDirection.RightToLeft };

    public static Border Card(StackPanel page, string? title, string? subtitle = null)
    {
        var content = new StackPanel();
        if (title != null)
            content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, subtitle == null ? 8 : 2), Foreground = DesktopTheme.TextBrush });
        if (subtitle != null)
        {
            var m = Muted(subtitle);
            m.Margin = new Thickness(0, 0, 0, 8);
            content.Children.Add(m);
        }
        var b = new Border { Child = content, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        page.Children.Add(b);
        return b;
    }

    public static StackPanel Body(Border card) => (StackPanel)card.Child;

    public static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    public static CheckBox Toggle(string label, string? hint, bool value, Action<bool> set, Action changed)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (hint != null) text.Children.Add(Muted(hint));
        var cb = new CheckBox { IsChecked = value, Content = text, Margin = new Thickness(0, 5, 0, 5), Foreground = DesktopTheme.TextBrush, VerticalContentAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => { set(true); changed(); };
        cb.Unchecked += (_, _) => { set(false); changed(); };
        return cb;
    }

    public static UIElement Labeled(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = DesktopTheme.TextBrush });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    public static ComboBox Combo((string Value, string Label)[] options, string current, Action<string> set, Action changed)
    {
        var combo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (v, l) in options) combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, current, StringComparison.OrdinalIgnoreCase))
                             ?? combo.Items.Cast<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string v }) { set(v); changed(); }
        };
        return combo;
    }

    public static TextBox Text(string value, Action<string> set, Action changed, bool ltr = true, double width = 120)
    {
        var box = new TextBox { Text = value, Width = width, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3), FlowDirection = ltr ? FlowDirection.LeftToRight : FlowDirection.RightToLeft };
        box.TextChanged += (_, _) => { set(box.Text); changed(); };
        return box;
    }

    public static Button Button(string text, Action click, string? style = "GhostButton")
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
        if (style != null && Application.Current?.TryFindResource(style) is Style st && st.TargetType.IsInstanceOfType(b)) b.Style = st;
        b.Click += (_, _) => { try { click(); } catch (Exception ex) { Services.AppLog.Warn("Studio button: " + ex.Message); } };
        return b;
    }

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}
