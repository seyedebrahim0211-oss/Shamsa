using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Shamsa.Desktop.Dock;

/// <summary>Small dark glass popup menu (the stock ContextMenu is unthemed and needs activation).</summary>
internal sealed class GlassMenu
{
    private readonly Popup _popup;
    private readonly StackPanel _panel = new() { MinWidth = 190 };
    public event Action? Closed;

    public GlassMenu(DesktopSettings s)
    {
        var border = new Border
        {
            Child = _panel,
            Background = DesktopTheme.GlassBrush(s, 1.35),
            BorderBrush = DesktopTheme.BorderBrush(s),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Math.Max(6, s.Theme.CornerRadius - 2)),
            Padding = new Thickness(4),
            Margin = new Thickness(10),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.45 }
        };
        _popup = new Popup
        {
            Child = border, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            FlowDirection = FlowDirection.RightToLeft
        };
        TextElement_SetFont(border);
        _popup.Closed += (_, _) => Closed?.Invoke();
    }

    private static void TextElement_SetFont(Border b) => TextElement.SetFontFamily(b, DesktopTheme.Font);

    public bool HasItems => _panel.Children.Count > 0;

    public void AddHeader(string text) =>
        _panel.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, Foreground = DesktopTheme.MutedBrush, Margin = new Thickness(10, 4, 10, 2),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320
        });

    public void AddSeparator()
    {
        if (_panel.Children.Count == 0 || _panel.Children[^1] is Separator) return;
        _panel.Children.Add(new Separator { Margin = new Thickness(6, 3, 6, 3), Background = DesktopTheme.HoverBrush, Height = 1 });
    }

    public void AddItem(string glyph, string text, Action action, bool enabled = true)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13,
            Foreground = DesktopTheme.MutedBrush, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight
        });
        var label = new TextBlock
        {
            Text = text, FontSize = 13, Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var item = new Border
        {
            Child = row, Padding = new Thickness(8, 6, 12, 6), CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent, Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.45
        };
        if (enabled)
        {
            item.MouseEnter += (_, _) => item.Background = DesktopTheme.HoverBrush;
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _popup.IsOpen = false;
                try { action(); } catch (Exception ex) { Services.AppLog.Warn("Dock menu action: " + ex.Message); }
            };
        }
        _panel.Children.Add(item);
    }

    public void Open(UIElement target, PlacementMode placement)
    {
        _popup.PlacementTarget = target;
        _popup.Placement = placement;
        _popup.IsOpen = true;
    }

    public void Close() => _popup.IsOpen = false;
}
