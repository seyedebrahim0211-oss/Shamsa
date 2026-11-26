using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using WpfDock = System.Windows.Controls.Dock;

namespace Shamsa.Desktop.Personalize;

/// <summary>Pick one icon (file + index) from a .ico/.exe/.dll, like Windows' "Change Icon" dialog.</summary>
public sealed class IconPickerWindow : Window
{
    private readonly TextBox _path = new() { FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 4, 6, 4) };
    private readonly WrapPanel _grid = new() { Margin = new Thickness(4) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private ToggleButton? _selected;

    /// <summary>"path,index" of the chosen icon, set when the dialog returns true.</summary>
    public string? Result { get; private set; }

    public IconPickerWindow(string? initialFile = null)
    {
        Title = "انتخاب آیکن";
        Width = 560; Height = 480; MinWidth = 420; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = DesktopTheme.Font;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var browse = Btn("انتخاب فایل…", Browse);
        DockPanel.SetDock(browse, WpfDock.Left);
        top.Children.Add(browse);
        var load = Btn("نمایش", () => Load(_path.Text));
        DockPanel.SetDock(load, WpfDock.Left);
        top.Children.Add(load);
        top.Children.Add(_path);
        DockPanel.SetDock(top, WpfDock.Top);
        root.Children.Add(top);

        var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var (name, file) in new[] { ("imageres", "imageres.dll"), ("shell32", "shell32.dll"), ("ddores", "ddores.dll"), ("mmres", "mmres.dll") })
        {
            string full = Path.Combine(sys, file);
            if (File.Exists(full)) quick.Children.Add(Btn(name, () => { _path.Text = full; Load(full); }, ghost: true));
        }
        DockPanel.SetDock(quick, WpfDock.Top);
        root.Children.Add(quick);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Btn("تأیید", Accept, accent: true));
        buttons.Children.Add(Btn("انصراف", () => { DialogResult = false; }));
        DockPanel.SetDock(buttons, WpfDock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(_status, WpfDock.Bottom);
        root.Children.Add(_status);

        var border = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        root.Children.Add(border);
        Content = root;

        string start = initialFile ?? Path.Combine(sys, "imageres.dll");
        _path.Text = start;
        Loaded += (_, _) => Load(start);
    }

    private void Browse()
    {
        var dlg = new OpenFileDialog
        {
            Title = "فایل آیکن",
            Filter = "فایل‌های آیکن (*.ico;*.exe;*.dll;*.icl)|*.ico;*.exe;*.dll;*.icl|همه فایل‌ها|*.*"
        };
        if (dlg.ShowDialog(this) == true) { _path.Text = dlg.FileName; Load(dlg.FileName); }
    }

    private void Load(string file)
    {
        _grid.Children.Clear();
        _selected = null;
        file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (!File.Exists(file)) { _status.Text = "فایل پیدا نشد."; return; }
        int count = SystemIcons.CountIcons(file);
        if (count <= 0) { _status.Text = "این فایل آیکنی ندارد."; return; }
        int shown = Math.Min(count, 400);
        for (int i = 0; i < shown; i++)
        {
            var img = SystemIcons.ExtractIcon(file, i, 48);
            if (img == null) continue;
            int index = i;
            var tb = new ToggleButton
            {
                Width = 64, Height = 64, Margin = new Thickness(3), Padding = new Thickness(4),
                Background = Brushes.Transparent, BorderThickness = new Thickness(2),
                Content = new Image { Source = img, Width = 40, Height = 40 },
                ToolTip = "شماره " + index, Tag = $"{file},{index}"
            };
            tb.SetResourceReference(BorderBrushProperty, "BorderBrush");
            tb.Checked += (_, _) =>
            {
                if (_selected != null && _selected != tb) _selected.IsChecked = false;
                _selected = tb;
                tb.SetResourceReference(BorderBrushProperty, "AccentBrush");
            };
            tb.Unchecked += (_, _) => tb.SetResourceReference(BorderBrushProperty, "BorderBrush");
            tb.MouseDoubleClick += (_, _) => { tb.IsChecked = true; Accept(); };
            _grid.Children.Add(tb);
        }
        _status.Text = $"{count} آیکن" + (count > shown ? $" (نمایش {shown} مورد نخست)" : "") + " — یکی را انتخاب کنید.";
    }

    private void Accept()
    {
        if (_selected?.Tag is not string loc) { _status.Text = "ابتدا یک آیکن را انتخاب کنید."; return; }
        Result = loc;
        DialogResult = true;
    }

    private static Button Btn(string text, Action click, bool accent = false, bool ghost = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), MinWidth = 0 };
        string key = accent ? "AccentButton" : "GhostButton";
        if (Application.Current?.TryFindResource(key) is Style s && s.TargetType.IsInstanceOfType(b)) b.Style = s;
        b.Click += (_, _) => click();
        return b;
    }
}
