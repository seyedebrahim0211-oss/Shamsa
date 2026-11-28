using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shamsa.Desktop.Widgets;

namespace Shamsa.Desktop.Fences;

internal static class AppColumnPage
{
    internal static UIElement Build(DesktopSettings s, Action changed)
    {
        var root = new StackPanel();
        var appearance = new StackPanel();
        appearance.Children.Add(StudioUi.Title("ظاهر برنامه‌ها در داک و گروه‌های دسکتاپ"));
        appearance.Children.Add(StudioUi.Toggle("هماهنگی خودکار آیکون‌ها با پس‌زمینه‌ای که در نگارخانه انتخاب می‌کنم",
            s.AppIcons.MatchWallpaper, v => s.AppIcons.MatchWallpaper = v, changed));
        appearance.Children.Add(IconEditor(s, null, changed));
        root.Children.Add(StudioUi.Card(appearance));
        var content = new StackPanel();
        root.Children.Add(StudioUi.Card(content));
        string? selected = null;
        string? path = null;
        string folder = "";
        void Refresh()
        {
            content.Children.Clear();
            var columns = s.Fences.Fences.Where(f => f.Mode == "manual" && f.Column != null).ToList();
            var group = columns.Find(f => f.Id == selected) ?? columns.FirstOrDefault();
            content.Children.Add(StudioUi.Title("ستون شیشه‌ای برنامه‌ها"));
            if (group == null)
            {
                content.Children.Add(StudioUi.Muted("برنامه‌های منوی شروع یا میان‌برهای دلخواهت را در یک ستون روی دسکتاپ جمع کن."));
                content.Children.Add(StudioUi.Button("ساخت و نمایش ستون برنامه‌ها", () =>
                {
                    var created = new FenceDefinition { Title = "برنامه‌های من", Mode = "manual", Column = new() };
                    // Seed from actual user pins, even if the dock itself is off.
                    foreach (string pin in s.Dock.Pinned.Where(p => !string.IsNullOrWhiteSpace(p) && AppIconArt.IsApp(p)))
                    {
                        if (!File.Exists(pin) && !pin.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) &&
                            !pin.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase)) continue;
                        string name = pin.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? "تنظیمات ویندوز" : Path.GetFileNameWithoutExtension(pin);
                        created.Members[pin] = string.IsNullOrWhiteSpace(name) ? pin : name;
                    }
                    created.Column.Normalize(created.Members);
                    var r = DesktopPlacement.ForNew(s, DesktopDisplay.PlacementArea(s), new Size(264, 410));
                    created.X = r.X; created.Y = r.Y; created.Width = r.Width; created.Height = r.Height;
                    s.Fences.Fences.Add(created); s.Fences.Enabled = s.Enabled = true;
                    selected = created.Id; changed(); Refresh();
                }, "AccentButton"));
                return;
            }
            if (columns.Count > 1)
                content.Children.Add(Choice(columns.Select(f => (f.Id, f.Title)), group.Id, "ستون", id => { selected = id; path = null; folder = ""; Refresh(); }));
            var col = group.Column!;
            col.Normalize(group.Members);
            if (!col.Folders.Contains(folder)) folder = "";
            var titleBox = new TextBox { Text = group.Title, MaxLength = 60 };
            content.Children.Add(StudioUi.Labeled("عنوان ستون", titleBox));
            var manage = new WrapPanel();
            manage.Children.Add(StudioUi.Button("ذخیرهٔ عنوان", () => { if (!string.IsNullOrWhiteSpace(titleBox.Text)) { group.Title = titleBox.Text.Trim(); changed(); } }));
            manage.Children.Add(StudioUi.Button("انتخاب برنامه‌ها…", () => FencesStudioPage.EditMembers(group, () => { changed(); Refresh(); }), "AccentButton"));
            if (!s.Enabled || !s.Fences.Enabled)
                manage.Children.Add(StudioUi.Button("نمایش روی دسکتاپ", () => { s.Enabled = s.Fences.Enabled = true; changed(); Refresh(); }));
            content.Children.Add(manage);
            var chooser = col.Folders.Select(f => (f, f)).Prepend(("", "بیرون پوشه‌ها")).ToList();
            content.Children.Add(StudioUi.Labeled("محتوای پوشه", Choice(chooser, folder, "محتوای پوشه", id => { folder = id; path = null; Refresh(); })));
            var add = new WrapPanel();
            var folderName = new TextBox { Width = 148, MaxLength = 60, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "نام پوشهٔ جدید" };
            AutomationProperties.SetName(folderName, "نام پوشهٔ جدید");
            var feedback = StudioUi.Muted("");
            add.Children.Add(folderName);
            add.Children.Add(StudioUi.Button("ساخت پوشه", () =>
            {
                string label = folderName.Text.Trim();
                if (label.Length == 0) { feedback.Text = "نام پوشه را بنویس."; return; }
                if (col.Folders.Contains(label, StringComparer.OrdinalIgnoreCase)) { feedback.Text = "پوشه‌ای با این نام وجود دارد."; return; }
                col.Folders.Add(label); folder = label; path = null; changed(); Refresh();
            }));
            if (folder.Length > 0)
                add.Children.Add(StudioUi.Button("برداشتن پوشه", () =>
                {
                    if (!PersonalLayoutsPage.Confirm($"پوشهٔ «{folder}» برداشته شود و برنامه‌هایش به ستون برگردند؟")) return;
                    col.Folders.Remove(folder); col.Normalize(group.Members);
                    folder = ""; changed(); Refresh();
                }, "GhostButton"));
            content.Children.Add(add); content.Children.Add(feedback);
            var paths = col.Order.Where(p => col.FolderOf(p) == folder).ToList();
            if (paths.Count == 0)
            {
                content.Children.Add(StudioUi.Muted(folder.Length > 0 ? "از بیرون پوشه‌ها، برنامه را انتخاب کن و مقصدش را روی این پوشه بگذار. می‌توانی میان‌بر را هم روی پوشهٔ بازِ دسکتاپ رها کنی." : "با «انتخاب برنامه‌ها» یا کشیدن میان‌بر روی ستون، برنامه اضافه کن."));
                return;
            }
            path = paths.Find(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) ?? paths[0];
            string current = path;
            var apps = new ListBox { MaxHeight = 224, MinHeight = 48, Margin = new Thickness(0, 6, 0, 4) };
            AutomationProperties.SetName(apps, "برنامه‌های ستون به ترتیب نمایش");
            foreach (var p in paths) apps.Items.Add(new ListBoxItem { Content = group.Members[p], Tag = p, ToolTip = p });
            apps.SelectedIndex = paths.IndexOf(current);
            var details = new StackPanel();
            void ShowSelection()
            {
                if (apps.SelectedItem is not ListBoxItem { Tag: string item }) return;
                current = item; path = item;
                details.Children.Clear();
                var order = new WrapPanel();
                foreach (var (n, text) in new[] { (-1, "بالاتر ↑"), (1, "پایین‌تر ↓") })
                {
                    var move = StudioUi.Button(text, () => { if (col.Step(item, n)) { changed(); Refresh(); } });
                    move.IsEnabled = paths.IndexOf(item) + n >= 0 && paths.IndexOf(item) + n < paths.Count;
                    order.Children.Add(move);
                }
                details.Children.Add(order);
                details.Children.Add(StudioUi.Labeled("انتقال برنامه به", Choice(chooser, folder, "انتقال برنامه به پوشه", f =>
                {
                    col.Move(item, f); folder = f; changed(); Refresh();
                })));
                if (AppIconArt.IsApp(item))
                {
                    details.Children.Add(StudioUi.Title("آیکون " + group.Members[item]));
                    details.Children.Add(IconEditor(s, item, changed));
                }
            }
            apps.SelectionChanged += (_, _) => ShowSelection();
            content.Children.Add(apps); content.Children.Add(details); ShowSelection();
        }
        Refresh(); return root;
    }

    private static ComboBox Choice(IEnumerable<(string Id, string Title)> items, string selected, string label, Action<string> pick)
    {
        var box = new ComboBox { MinWidth = 155, Margin = new Thickness(0, 2, 0, 2) };
        AutomationProperties.SetName(box, label);
        foreach (var (id, name) in items) box.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? box.Items[0];
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string value }) pick(value); };
        return box;
    }

    private static UIElement IconEditor(DesktopSettings s, string? target, Action changed)
    {
        string? key = target == null ? null : AppIconArt.Key(target);
        var choice = key == null ? s.AppIcons.Default : s.AppIcons.PerApp.GetValueOrDefault(key) ?? new();
        var host = new DockPanel();
        var icon = new Image { Width = 64, Height = 64, Margin = new Thickness(8), FlowDirection = FlowDirection.LeftToRight };
        var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 25,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mark = new Image { Width = 38, Height = 38 };
        var preview = new Grid { Width = 84, Height = 84 };
        preview.Children.Add(icon); preview.Children.Add(glyph); preview.Children.Add(mark);
        DockPanel.SetDock(preview, System.Windows.Controls.Dock.Left); host.Children.Add(preview);
        var form = new StackPanel();
        void Draw()
        {
            var resolved = AppIconArt.Resolve(s, target);
            icon.Source = FenceIconArt.Plate(resolved.Style, resolved.Color);
            mark.Source = AppIconArt.CustomImage(s, target);
            glyph.Visibility = mark.Source == null ? Visibility.Visible : Visibility.Collapsed;
        }
        void Save(AppIconChoice value)
        {
            choice = value;
            if (key == null) s.AppIcons.Default = value;
            else if (value.Style == "inherit" && value.ColorHex == null && value.ImageId == null) s.AppIcons.PerApp.Remove(key);
            else s.AppIcons.PerApp[key] = value;
            Draw(); changed();
        }
        var models = AppIconArt.Models.AsEnumerable().Prepend(key == null ? ("theme", "خودکار با تم و پس‌زمینه") : ("inherit", "مدل عمومی برنامه‌ها"));
        form.Children.Add(StudioUi.Labeled("مدل آیکون", Choice(models, choice.Style, "مدل آیکون", id => Save(choice with { Style = id }))));
        var colors = new WrapPanel();
        var swatch = StudioUi.Button("انتخاب رنگ…", () =>
        {
            var c = AppIconArt.Resolve(s, target).Color;
            using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                Save(choice with { ColorHex = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}" });
        });
        colors.Children.Add(swatch);
        colors.Children.Add(StudioUi.Button(key == null ? "رنگ خودکار تم" : "رنگ عمومی برنامه‌ها", () => Save(choice with { ColorHex = null })));
        if (key != null)
        {
            colors.Children.Add(StudioUi.Button("تصویر دلخواه…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "آیکون و تصویر|*.png;*.ico;*.jpg;*.jpeg" };
                if (dlg.ShowDialog() != true) return;
                try { Save(choice with { ImageId = AppIconArt.ImportImage(dlg.FileName) }); }
                catch (Exception ex) { MessageBox.Show("تصویر باز نشد. " + ex.Message, "نگارخانه"); }
            }));
            colors.Children.Add(StudioUi.Button("نشان اصلی برنامه", () => Save(choice with { ImageId = null })));
        }
        form.Children.Add(colors); host.Children.Add(form); Draw();
        return host;
    }
}
