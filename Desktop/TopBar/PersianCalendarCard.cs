using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Shamsa.Desktop.TopBar;

/// <summary>Small glass Solar Hijri month calendar shown from the clock.</summary>
internal sealed class PersianCalendarCard : Border
{
    private readonly DesktopSettings _s;
    private DateTime _month; // any day inside the displayed Persian month
    private readonly Grid _grid = new();
    private readonly TextBlock _header = new();
    private readonly TextBlock _footer = new();

    public PersianCalendarCard(DesktopSettings s, DateTime today)
    {
        _s = s;
        _month = today;
        var tint = DesktopTheme.ParseColor(s.Theme.GlassTintHex, Color.FromRgb(0x0B, 0x13, 0x24));
        tint.A = 0xF2;
        Background = new SolidColorBrush(tint);
        BorderBrush = DesktopTheme.BorderBrush(s);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Math.Max(8, s.Theme.CornerRadius));
        Padding = new Thickness(14, 12, 14, 12);
        FlowDirection = FlowDirection.RightToLeft;
        Width = 290;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45 };
        Margin = new Thickness(8);

        var root = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        // RTL: System.Windows.Controls.Dock.Left is the visual right edge. Previous month sits on the right, pointing right.
        var prev = Nav("", -1);
        var next = Nav("", 1);
        prev.FlowDirection = FlowDirection.LeftToRight;
        next.FlowDirection = FlowDirection.LeftToRight;
        prev.ToolTip = "ماه قبل";
        next.ToolTip = "ماه بعد";
        DockPanel.SetDock(prev, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(next, System.Windows.Controls.Dock.Right);
        top.Children.Add(prev);
        top.Children.Add(next);
        _header.FontFamily = DesktopTheme.Font;
        _header.FontSize = 15;
        _header.FontWeight = FontWeights.SemiBold;
        _header.Foreground = DesktopTheme.TextBrush;
        _header.HorizontalAlignment = HorizontalAlignment.Center;
        top.Children.Add(_header);
        root.Children.Add(top);

        for (int c = 0; c < 7; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(_grid);

        _footer.FontFamily = DesktopTheme.Font;
        _footer.FontSize = 12;
        _footer.Foreground = DesktopTheme.MutedBrush;
        _footer.Margin = new Thickness(0, 10, 0, 0);
        _footer.HorizontalAlignment = HorizontalAlignment.Center;
        _footer.Cursor = Cursors.Hand;
        _footer.MouseLeftButtonUp += (_, _) => { _month = DateTime.Today; Render(); };
        root.Children.Add(_footer);

        Child = root;
        Render();
    }

    private TextBlock Nav(string glyph, int delta)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = DesktopTheme.TextBrush,
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        t.MouseEnter += (_, _) => t.Background = DesktopTheme.HoverBrush;
        t.MouseLeave += (_, _) => t.Background = Brushes.Transparent;
        t.MouseLeftButtonUp += (_, e) => { e.Handled = true; _month = PersianDate.Calendar.AddMonths(_month, delta); Render(); };
        return t;
    }

    private void Render()
    {
        bool fa = _s.TopBar.PersianDigits;
        var cal = PersianDate.Calendar;
        int y = cal.GetYear(_month), m = cal.GetMonth(_month);
        var first = cal.ToDateTime(y, m, 1, 0, 0, 0, 0);
        int days = cal.GetDaysInMonth(y, m);
        _header.Text = DesktopTheme.Digits($"{PersianDate.Months[m - 1]} {y}", fa);
        _footer.Text = "امروز: " + PersianDate.Long(DateTime.Today, fa);

        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        int startCol = PersianDate.WeekColumn(first);
        int rows = 1 + (int)Math.Ceiling((startCol + days) / 7.0);
        for (int r = 0; r < rows; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        var accent = DesktopTheme.AccentBrush(_s);
        for (int c = 0; c < 7; c++)
            Add(new TextBlock { Text = PersianDate.WeekHeader[c], Foreground = c == 6 ? new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x8B)) : DesktopTheme.MutedBrush, FontSize = 12 }, 0, c);

        for (int d = 1; d <= days; d++)
        {
            var date = first.AddDays(d - 1);
            int idx = startCol + d - 1;
            int row = 1 + idx / 7, col = idx % 7;
            bool today = date.Date == DateTime.Today;
            var text = new TextBlock
            {
                Text = DesktopTheme.Digits(d.ToString(), fa),
                Foreground = today ? Brushes.White : col == 6 ? new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x8B)) : DesktopTheme.TextBrush,
                FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 13
            };
            var cell = new Border
            {
                Width = 30, Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = today ? accent : Brushes.Transparent,
                Child = text,
                ToolTip = date.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            };
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            Add(cell, row, col);
        }
    }

    private void Add(FrameworkElement e, int row, int col)
    {
        if (e is TextBlock tb)
        {
            tb.FontFamily = DesktopTheme.Font;
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.VerticalAlignment = VerticalAlignment.Center;
            if (e is Border { Child: TextBlock inner }) inner.FontFamily = DesktopTheme.Font;
        }
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        _grid.Children.Add(e);
    }
}
