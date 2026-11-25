using System.Windows;
using System.Windows.Controls;
using Shamsa.Desktop.Widgets;

namespace Shamsa.Desktop.Fences;

internal static class FenceUiExt { public static T Also<T>(this T v, Action<T> a) { a(v); return v; } }

/// <summary>Desktop Studio page «گروه‌بندی دسکتاپ».</summary>
public static class FencesStudioPage
{
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var f = s.Fences;
        var root = new StackPanel();
        var list = new StackPanel();

        void Refill()
        {
            list.Children.Clear();
            if (f.Fences.Count == 0) list.Children.Add(StudioUi.Muted("هنوز گروهی ساخته نشده است."));
            foreach (var fence in f.Fences.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var del = StudioUi.Button("✕ حذف", () => { f.Fences.Remove(fence); Refill(); changed(); }, "GhostButton");
                DockPanel.SetDock(del, System.Windows.Controls.Dock.Right);
                row.Children.Add(del);
                string desc = fence.Mode == "folder"
                    ? "پوشه: " + (fence.FolderPath ?? "—")
                    : "پسوندها: " + string.Join(" ", fence.Extensions);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = fence.Title, FontWeight = FontWeights.SemiBold });
                var d = StudioUi.Muted(desc);
                d.FlowDirection = FlowDirection.LeftToRight;
                d.TextAlignment = TextAlignment.Right;
                d.TextTrimming = TextTrimming.CharacterEllipsis;
                d.TextWrapping = TextWrapping.NoWrap;
                text.Children.Add(d);
                row.Children.Add(text);
                list.Children.Add(row);
            }
        }

        var general = new StackPanel();
        general.Children.Add(StudioUi.Title("گروه‌بندی آیکون‌های دسکتاپ"));
        general.Children.Add(StudioUi.Muted("کادرهای شیشه‌ای روی دسکتاپ که فایل‌ها را بر اساس نوع یا محتوای یک پوشه نشان می‌دهند. آیکون‌ها و فایل‌های واقعی دسکتاپ هرگز پنهان، جابه‌جا یا حذف نمی‌شوند."));
        general.Children.Add(StudioUi.Toggle("نمایش گروه‌ها", f.Enabled, v => f.Enabled = v, changed));
        general.Children.Add(StudioUi.Toggle("جمع/باز شدن با یک کلیک روی عنوان (وگرنه دوبار کلیک)", f.RollUpOnTitleClick, v => f.RollUpOnTitleClick = v, changed));
        general.Children.Add(StudioUi.Toggle("قفل موقعیت و اندازه", f.LockPositions, v => f.LockPositions = v, changed));
        root.Children.Add(StudioUi.Card(general));

        var groups = new StackPanel();
        groups.Children.Add(StudioUi.Title("گروه‌ها"));
        groups.Children.Add(StudioUi.Button("✨ ساخت گروه‌های پیشنهادی (تصاویر، اسناد، برنامه‌ها، فشرده‌ها)", () =>
        {
            foreach (var sug in FencesModule.SuggestedFences())
                if (!f.Fences.Any(x => x.Title == sug.Title)) f.Fences.Add(sug);
            f.Enabled = true;
            Refill();
            changed();
        }, "AccentButton").Also(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        groups.Children.Add(list);
        Refill();
        root.Children.Add(StudioUi.Card(groups));

        // ---- custom fence
        var custom = new StackPanel();
        custom.Children.Add(StudioUi.Title("گروه جدید"));
        var title = new TextBox { Text = "گروه من", MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var mode = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        mode.Items.Add(new ComboBoxItem { Content = "فایل‌های دسکتاپ با پسوند مشخص", Tag = "filter" });
        mode.Items.Add(new ComboBoxItem { Content = "محتوای یک پوشه (درگاه)", Tag = "folder" });
        mode.SelectedIndex = 0;
        var exts = new TextBox { Text = ".pdf .docx", MinWidth = 260, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "با فاصله یا کاما جدا کن؛ «folder» برای پوشه‌ها" };
        var folderPath = new TextBox { MinWidth = 260, FlowDirection = FlowDirection.LeftToRight, IsReadOnly = true };
        var pick = StudioUi.Button("انتخاب پوشه…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "پوشه گروه را انتخاب کن" };
            if (dlg.ShowDialog() == true) folderPath.Text = dlg.FolderName;
        });
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        folderRow.Children.Add(folderPath);
        folderRow.Children.Add(pick);
        var extsRow = StudioUi.Labeled("پسوندها", exts);
        var folderLabeled = StudioUi.Labeled("پوشه", folderRow);
        folderLabeled.Visibility = Visibility.Collapsed;
        mode.SelectionChanged += (_, _) =>
        {
            bool folder = (mode.SelectedItem as ComboBoxItem)?.Tag as string == "folder";
            extsRow.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
            folderLabeled.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        };
        custom.Children.Add(StudioUi.Labeled("عنوان", title));
        custom.Children.Add(StudioUi.Labeled("نوع", mode));
        custom.Children.Add(extsRow);
        custom.Children.Add(folderLabeled);
        var status = StudioUi.Muted("");
        custom.Children.Add(StudioUi.Button("＋ افزودن گروه", () =>
        {
            string m = (mode.SelectedItem as ComboBoxItem)?.Tag as string ?? "filter";
            var def = new FenceDefinition { Title = string.IsNullOrWhiteSpace(title.Text) ? "گروه" : title.Text.Trim(), Mode = m };
            if (m == "folder")
            {
                if (!Directory.Exists(folderPath.Text)) { status.Text = "اول یک پوشه موجود انتخاب کن."; return; }
                def.FolderPath = folderPath.Text;
            }
            else
            {
                def.Extensions = exts.Text.Split(new[] { ' ', ',', '،', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(e => e.Equals("folder", StringComparison.OrdinalIgnoreCase) ? "folder" : e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
                    .Distinct().ToList();
                if (def.Extensions.Count == 0) { status.Text = "حداقل یک پسوند بنویس."; return; }
            }
            var wa = SystemParameters.WorkArea;
            int n = f.Fences.Count;
            def.X = Math.Round(wa.Left + 60 + (n % 3) * 380);
            def.Y = Math.Round(wa.Top + 60 + (n / 3 % 3) * 270);
            f.Fences.Add(def);
            f.Enabled = true;
            status.Text = "گروه «" + def.Title + "» اضافه شد.";
            Refill();
            changed();
        }, "GoldButton"));
        custom.Children.Add(status);
        root.Children.Add(StudioUi.Card(custom));
        return root;
    }
}
