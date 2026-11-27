using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shamsa.Desktop.Widgets;

namespace Shamsa.Desktop;

internal static class PersonalLayoutsPage
{
    internal static UIElement Build(DesktopSettings s, Action<string> status, Action<string> go)
    {
        var root = new StackPanel();
        var entry = new StackPanel();
        var name = new TextBox { MaxLength = 60, MinWidth = 180, ToolTip = "مثلاً کار روزانه یا طراحی" };
        AutomationProperties.SetName(name, "نام چیدمان جدید");
        entry.Children.Add(StudioUi.Labeled("نام چیدمان جدید", name));
        var actions = new WrapPanel();
        void Create() => status(PersonalLayoutCommands.Run((live, area) => PersonalLayouts.SaveCurrent(live, area, name.Text)));
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Create(); e.Handled = true; } };
        actions.Children.Add(StudioUi.Button("ذخیرهٔ جای فعلی", Create, "AccentButton"));
        if (s.PersonalLayouts.BeforeSwitch != null)
            actions.Children.Add(StudioUi.Button("برگرداندن آخرین جابه‌جایی", () => status(PersonalLayoutCommands.Undo())));
        entry.Children.Add(actions);
        if (s.Fences.Fences.Count + s.Widgets.Items.Count == 0)
        {
            entry.Children.Add(StudioUi.Muted("ابتدا یک گروه یا ویجت به دسکتاپ اضافه کن."));
            var add = new WrapPanel();
            add.Children.Add(StudioUi.Button("افزودن گروه", () => go("fences")));
            add.Children.Add(StudioUi.Button("افزودن ویجت", () => go("widgets")));
            entry.Children.Add(add);
        }
        root.Children.Add(StudioUi.Card(entry));
        if (s.PersonalLayouts.Saved.Count == 0) return root;
        var screen = DesktopDisplay.Target(s);
        string device = screen.DeviceName;
        root.Children.Add(StudioUi.Muted("با تعیین پیش‌فرض، هنگام فعال شدن این نمایشگر یا تغییر وضوح آن، نگارخانه چیدمان انتخابی را اعمال می‌کند."));
        foreach (var layout in s.PersonalLayouts.Saved)
        {
            var stack = new StackPanel();
            var summary = new DockPanel();
            var preview = Preview(layout.Snapshot, s);
            DockPanel.SetDock(preview, System.Windows.Controls.Dock.Left);
            summary.Children.Add(preview);
            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(StudioUi.Title(layout.Name));
            title.Children.Add(StudioUi.Muted(DesktopTheme.Digits($"{layout.Snapshot.Groups.Count} گروه  ·  {layout.Snapshot.Widgets.Count} ویجت", true)));
            if (layout.Id == s.PersonalLayouts.LastAppliedId) title.Children.Add(StudioUi.Muted("آخرین انتخاب"));
            summary.Children.Add(title); stack.Children.Add(summary);
            var row = new WrapPanel();
            row.Children.Add(StudioUi.Button("اعمال چیدمان", () => status(PersonalLayoutCommands.Apply(layout.Id)), "AccentButton"));
            row.Children.Add(StudioUi.Button("ذخیرهٔ جای فعلی روی این چیدمان", () =>
            {
                if (Confirm($"جای فعلی گروه‌ها و ویجت‌ها جایگزین چیدمان «{layout.Name}» شود؟"))
                    status(PersonalLayoutCommands.Run((live, area) => PersonalLayouts.SaveCurrent(live, area, layout.Name, layout.Id)));
            }));
            bool linked = s.PersonalLayouts.Displays.TryGetValue(device, out var binding) && binding == layout.Id;
            var bind = StudioUi.Button(linked ? "برداشتن پیش‌فرض نمایشگر" : "پیش‌فرض این نمایشگر", () =>
                status(PersonalLayoutCommands.Run((live, _) => PersonalLayouts.BindDisplay(live, layout.Id, device))));
            bind.ToolTip = DesktopDisplay.Label(screen);
            row.Children.Add(bind);
            row.Children.Add(StudioUi.Button("حذف", () =>
            {
                if (Confirm($"چیدمان ذخیره‌شدهٔ «{layout.Name}» حذف شود؟"))
                    status(PersonalLayoutCommands.Run((live, _) => PersonalLayouts.Remove(live, layout.Id)));
            }, "GhostButton"));
            stack.Children.Add(row);
            root.Children.Add(StudioUi.Card(stack));
        }
        return root;
    }

    internal static bool Confirm(string question) => MessageBox.Show(question, "نگارخانه", MessageBoxButton.YesNo,
        MessageBoxImage.Question, MessageBoxResult.No, MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading) == MessageBoxResult.Yes;

    private static FrameworkElement Preview(DesktopLayoutSnapshot shot, DesktopSettings s)
    {
        var area = shot.Area!;
        var canvas = new Canvas { Width = area.Width, Height = area.Height, ClipToBounds = true,
            FlowDirection = FlowDirection.LeftToRight, Background = DesktopTheme.GlassBrush(s) };
        void Draw(DesktopItemBounds p, Brush brush, bool enabled)
        {
            var tile = new Border { Width = p.Width, Height = p.Collapsed ? DesktopPlacement.CollapsedHeight : p.Height,
                CornerRadius = new CornerRadius(12), Background = brush, Opacity = enabled ? 0.9 : 0.25 };
            Canvas.SetLeft(tile, p.X - area.X); Canvas.SetTop(tile, p.Y - area.Y);
            canvas.Children.Add(tile);
        }
        foreach (var g in shot.Groups) Draw(g, new SolidColorBrush(DesktopTheme.Accent(s)), shot.GroupsEnabled);
        foreach (var w in shot.Widgets) Draw(w, Brushes.Wheat, shot.WidgetsEnabled);
        return new Viewbox { Width = 142, Height = 76, Margin = new Thickness(8), Child = canvas, FlowDirection = FlowDirection.LeftToRight };
    }
}
