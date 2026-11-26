using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shamsa.Desktop.ControlCenter;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>Compact list of suggestion cards for the hub's home page. Updates itself while it is on screen.</summary>
public static class SmartSuggestionsView
{
    /// <param name="maxCards">How many cards to show (the rest appear after one is handled or dismissed).</param>
    /// <param name="showTitle">False when the host already shows a heading (only the refresh button stays).</param>
    public static UIElement Build(int maxCards = 3, bool showTitle = true)
    {
        var root = new StackPanel { FlowDirection = FlowDirection.RightToLeft };

        void Render()
        {
            root.Children.Clear();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            var refresh = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = StudioUi.IconFont, FontSize = 13 },
                ToolTip = "بررسی دوباره",
                Padding = new Thickness(8, 4, 8, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            if (Application.Current?.TryFindResource("GhostButton") is Style ghost) refresh.Style = ghost;
            refresh.Click += (_, _) => SmartSuggestions.Refresh(force: true);
            DockPanel.SetDock(refresh, System.Windows.Controls.Dock.Left);
            header.Children.Add(refresh);
            var title = new TextBlock { Text = showTitle ? "پیشنهادهای هوشمند" : "", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            header.Children.Add(title);
            root.Children.Add(header);

            if (!InsightsStore.Settings.SuggestionsEnabled)
            {
                root.Children.Add(StudioUi.Muted("پیشنهادها خاموش است. از صفحه «هوشمندسازی» روشنش کن."));
                return;
            }

            var items = SmartSuggestions.Current;
            if (items.Count == 0)
            {
                root.Children.Add(EmptyCard(SmartSuggestions.LastComputeUtc == DateTime.MinValue
                    ? "در حال بررسی…"
                    : "همه‌چیز مرتب است. پیشنهاد تازه‌ای نیست."));
                return;
            }
            foreach (var s in items.Take(maxCards)) root.Children.Add(Card(s));
            if (items.Count > maxCards)
                root.Children.Add(StudioUi.Muted($"و {SmartSuggestions.Fa(items.Count - maxCards)} پیشنهاد دیگر…"));
        }

        void OnChanged()
        {
            if (root.Dispatcher.CheckAccess()) Render();
            else root.Dispatcher.BeginInvoke(Render);
        }

        root.Loaded += (_, _) =>
        {
            SmartSuggestions.Changed += OnChanged;
            InsightsStore.Changed += OnChanged;
            Render();
            SmartSuggestions.Refresh();
        };
        root.Unloaded += (_, _) =>
        {
            SmartSuggestions.Changed -= OnChanged;
            InsightsStore.Changed -= OnChanged;
        };
        Render();
        return root;
    }

    private static Border Shell(UIElement child)
    {
        var b = new Border
        {
            Child = child,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1)
        };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    private static UIElement EmptyCard(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = "", FontFamily = StudioUi.IconFont, FontSize = 16, Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        var t = new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        row.Children.Add(icon);
        row.Children.Add(t);
        return Shell(row);
    }

    private static UIElement Card(SmartSuggestion s)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // icon disc
        var disc = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        disc.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        var glyph = new TextBlock { Text = s.Glyph, FontFamily = StudioUi.IconFont, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        disc.Child = glyph;
        grid.Children.Add(disc);

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        var title = new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var detail = new TextBlock { Text = s.Detail, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), LineHeight = 20 };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.Children.Add(title);
        text.Children.Add(detail);

        var action = new Button { Content = s.ActionLabel, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand };
        if (Application.Current?.TryFindResource("AccentButton") is Style accent) action.Style = accent;
        action.Click += (_, _) =>
        {
            try { s.Run(); }
            catch (Exception ex) { Services.AppLog.Warn("Suggestion action failed: " + ex.Message); }
        };
        text.Children.Add(action);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = StudioUi.IconFont, FontSize = 11 },
            ToolTip = "فعلاً نه",
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        close.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
        close.Click += (_, _) => s.Dismiss();
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        return Shell(grid);
    }
}
