using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

/// <summary>
/// «شخصی‌سازی ویندوز» — one scrolling page of RTL cards. Every change is per-user (HKCU / per-user APIs),
/// backed up before the first write and undoable per item or all at once.
/// </summary>
public static class PersonalizeStudioPage
{
    /// <summary>
    /// When true the page never reads the user's wallpaper or lock-screen image (renders placeholders).
    /// Used by render self-tests so a captured page cannot contain personal pictures.
    /// </summary>
    public static bool SafePreviewMode { get; set; }

    /// <summary>Entry point used by the Desktop Studio hub.</summary>
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var host = new Border { FlowDirection = FlowDirection.RightToLeft };
        host.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        var page = new Page(host, s, changed);
        page.Render();
        return host;
    }

    private sealed class Page
    {
        private readonly Border _host;
        private readonly DesktopSettings _s;
        private readonly Action _changed;
        private double _scroll;

        public Page(Border host, DesktopSettings s, Action changed) { _host = host; _s = s; _changed = changed; }

        public void Render()
        {
            var root = new StackPanel { Margin = new Thickness(4, 0, 4, 16) };
            void Add(Func<UIElement> section)
            {
                try { root.Children.Add(section()); }
                catch (Exception ex) { AppLog.Error("Personalize section failed", ex); root.Children.Add(Wrap(Card("این بخش بارگذاری نشد", ex.Message))); }
            }
            Add(Hero);
            Add(IconsCard);
            Add(IconThemesCard);
            Add(LayoutCard);
            Add(WallpaperCard);
            Add(PhotoshopCard);
            Add(SlideshowCard);
            Add(LockScreenCard);
            Add(AccentCard);
            Add(CursorCard);
            Add(ShortcutsCard);
            Add(ThemeFileCard);

            var sv = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            double restore = _scroll;
            sv.Loaded += (_, _) => sv.ScrollToVerticalOffset(restore);
            sv.ScrollChanged += (_, e) => _scroll = e.VerticalOffset;
            _host.Child = sv;
        }

        /// <summary>Runs a change all-or-nothing, logs and reports errors in Persian, then refreshes the page.</summary>
        private void Do(string what, Action action, string? success = null)
        {
            try
            {
                PersonalizeStore.Transaction(action);
                AppLog.Info("Personalize: " + what);
                if (success != null) Info(success);
            }
            catch (Exception ex)
            {
                AppLog.Error("Personalize failed: " + what, ex);
                MessageBox.Show(Window.GetWindow(_host), $"«{what}» انجام نشد و چیزی تغییر نکرد.\n\n{ex.Message}", "نگارخانه",
                    MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            }
            Render();
        }

        private bool Confirm(string text) =>
            MessageBox.Show(Window.GetWindow(_host), text, "نگارخانه", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) == MessageBoxResult.Yes;

        private void Info(string text) =>
            MessageBox.Show(Window.GetWindow(_host), text, "نگارخانه", MessageBoxButton.OK, MessageBoxImage.Information,
                MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

        // ================================================================== hero

        private UIElement Hero()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = "شخصی‌سازی ویندوز", FontSize = 22, FontWeight = FontWeights.Bold });
            text.Children.Add(Muted("آیکن‌های دسکتاپ، تصویر زمینه، صفحه قفل، رنگ، نشانگر ماوس و تم — همه در یک صفحه. " +
                                    "همه تغییرها فقط برای حساب شما اعمال می‌شوند و قبل از اولین تغییر، مقدار اصلی ذخیره می‌شود."));
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            int count = PersonalizeStore.Data.Originals.Count;
            row.Children.Add(Chip(count == 0 ? "هنوز تغییری داده نشده" : $"{Fa(count)} مقدار تغییر کرده و قابل بازگشت است", count == 0 ? "SuccessBrush" : "WarnBrush"));
            var resetAll = Btn("بازگرداندن همه به پیش‌فرض ویندوز", ResetAll, "DangerButton");
            resetAll.IsEnabled = count > 0 || ShortcutService.All.Any(ShortcutService.Exists) || PersonalizeStore.Data.OriginalWallpaper != null;
            row.Children.Add(resetAll);
            text.Children.Add(row);
            grid.Children.Add(text);

            var preview = DesktopPreview();
            preview.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(preview, 1);
            grid.Children.Add(preview);

            var b = Wrap(new StackPanel { Children = { grid } });
            b.Background = new LinearGradientBrush(Color.FromArgb(0x33, 0x14, 0xB8, 0xA6), Color.FromArgb(0x10, 0xD4, 0xA0, 0x17), 0);
            return b;
        }

        /// <summary>A miniature desktop: wallpaper, the visible system icons and a taskbar in the accent color.</summary>
        private FrameworkElement DesktopPreview()
        {
            var accent = AccentService.CurrentAccent() ?? Color.FromRgb(0x14, 0xB8, 0xA6);
            var canvas = new Grid { Width = 260, Height = 150, ClipToBounds = true, FlowDirection = FlowDirection.LeftToRight };
            canvas.Children.Add(new Border { Background = WallpaperBrush(260) });
            var icons = new WrapPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var d in SystemIcons.All.Where(SystemIcons.IsVisible))
            {
                var cell = new StackPanel { Width = 44, Margin = new Thickness(0, 0, 0, 2) };
                var img = SystemIcons.LoadIcon(SystemIcons.CurrentIconLocation(d), 32);
                cell.Children.Add(img != null ? new Image { Source = img, Width = 22, Height = 22 } : (UIElement)new TextBlock { Text = d.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                cell.Children.Add(new TextBlock { Text = d.Name, FontSize = 7.5, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.RightToLeft });
                icons.Children.Add(cell);
            }
            canvas.Children.Add(icons);
            canvas.Children.Add(new Border
            {
                Height = 16, VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(AccentService.AccentOnTaskbar() ? Color.FromArgb(0xE0, accent.R, accent.G, accent.B) : Color.FromArgb(0xD0, 0x1F, 0x29, 0x37)),
                Child = new Border { Width = 30, Height = 3, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) }
            });
            return new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(3), BorderBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)), Child = canvas, VerticalAlignment = VerticalAlignment.Center };
        }

        private Brush WallpaperBrush(int decodeWidth)
        {
            if (!SafePreviewMode)
            {
                var a = PersonalizeStore.Data.Applied;
                if (a.SolidColorHex != null) return new SolidColorBrush(DesktopTheme.ParseColor(a.SolidColorHex, Colors.Black));
                var img = LoadThumb(WallpaperService.CurrentWallpaper(), decodeWidth);
                if (img != null) return new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                var bg = PersonalizeStore.Read(@"Control Panel\Colors", "Background") as string;
                var p = bg?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => byte.TryParse(x, out var v) ? v : (byte)0).ToArray();
                if (p is { Length: 3 }) return new SolidColorBrush(Color.FromRgb(p[0], p[1], p[2]));
            }
            // placeholder: a calm Persian-turquoise gradient
            return new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x0F, 0x76, 0x6E), 0), new GradientStop(Color.FromRgb(0x1E, 0x3A, 0x8A), 0.6), new GradientStop(Color.FromRgb(0x0B, 0x13, 0x24), 1)
            }, 45);
        }

        private static BitmapImage? LoadThumb(string? path, int width)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = width; bi.UriSource = new Uri(path); bi.EndInit(); bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        private void ResetAll()
        {
            if (!Confirm("همه تغییرهای نگارخانه در ویندوز (آیکن‌ها، نمایش آیکن‌ها، اندازه و فاصله، تصویر زمینه، رنگ، نشانگر ماوس) به حالت قبل برگردد و میانبرهای ساخته‌شده حذف شوند؟\n\n" +
                         "اگر اندازه آیکن‌ها تغییر کرده بود، Explorer یک بار بازراه‌اندازی می‌شود."))
                return;
            bool sizeChanged = PersonalizeStore.HasChanges(DesktopLayoutService.GroupSize);
            try
            {
                WallpaperService.Reset();
                foreach (var d in SystemIcons.All) SystemIcons.ResetIcon(d);
                PersonalizeStore.Restore(SystemIcons.GroupVisibility);
                AccentService.Reset();
                CursorService.Reset();
                PersonalizeStore.Restore();   // anything left (layout groups, …)
                var a = PersonalizeStore.Data.Applied;
                a.DesktopIconSize = null; a.IconSpacing = null; a.IconTheme = null;
                PersonalizeStore.Save();
                foreach (var sc in ShortcutService.All) ShortcutService.Remove(sc);
                PersonalizeNative.RefreshShellIcons();
                if (sizeChanged) PersonalizeNative.RestartExplorer();
                Info("همه چیز به حالت قبل برگشت." + (PersonalizeStore.Data.Originals.Count > 0 ? "\nبعضی مقدارها برنگشتند؛ جزئیات در گزارش برنامه ثبت شد." : ""));
            }
            catch (Exception ex) { AppLog.Error("Reset all failed", ex); Info("بازگرداندن کامل نشد: " + ex.Message); }
            Render();
        }

        // ================================================================== desktop icons

        private UIElement IconsCard()
        {
            var card = Card("آیکن‌های دسکتاپ", "نمایش یا پنهان کردن آیکن‌های سیستمی و تعویض تصویر هر کدام.");
            var wrap = new WrapPanel();
            foreach (var d in SystemIcons.All)
            {
                var tile = new StackPanel { Width = 150, Margin = new Thickness(0, 0, 10, 10) };
                var imgBorder = new Border { Height = 64, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 6) };
                imgBorder.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
                var img = SystemIcons.LoadIcon(SystemIcons.CurrentIconLocation(d), 48);
                imgBorder.Child = img != null
                    ? new Image { Source = img, Width = 44, Height = 44 }
                    : new TextBlock { Text = d.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                tile.Children.Add(imgBorder);
                tile.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });

                var show = new CheckBox { Content = "نمایش روی دسکتاپ", IsChecked = SystemIcons.IsVisible(d), Margin = new Thickness(0, 4, 0, 4) };
                show.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                show.Click += (_, _) => Do((show.IsChecked == true ? "نمایش " : "پنهان کردن ") + d.Name, () => SystemIcons.SetVisible(d, show.IsChecked == true));
                tile.Children.Add(show);

                var btns = new WrapPanel();
                btns.Children.Add(Btn("تغییر آیکن…", () => PickIcon(d)));
                bool custom = PersonalizeStore.Data.Applied.IconFiles.ContainsKey(d.Id);
                var reset = Btn("پیش‌فرض", () => Do("بازگرداندن آیکن " + d.Name, () => SystemIcons.ResetIcon(d)));
                reset.IsEnabled = custom;
                btns.Children.Add(reset);
                tile.Children.Add(btns);
                wrap.Children.Add(tile);
            }
            card.Children.Add(wrap);
            var foot = new WrapPanel();
            var resetVis = Btn("بازگرداندن نمایش آیکن‌ها", () => Do("بازگرداندن نمایش آیکن‌ها", () => { PersonalizeStore.Restore(SystemIcons.GroupVisibility); PersonalizeNative.RefreshShellIcons(); }));
            resetVis.IsEnabled = PersonalizeStore.HasChanges(SystemIcons.GroupVisibility);
            foot.Children.Add(resetVis);
            card.Children.Add(foot);
            return Wrap(card);
        }

        private void PickIcon(SystemIconDef d)
        {
            var picker = new IconPickerWindow { Owner = Window.GetWindow(_host) };
            if (picker.ShowDialog() != true || picker.Result == null) return;
            string? full = null;
            if (d.IsRecycleBin && Confirm("برای حالت «سطل پر» هم آیکن جداگانه انتخاب می‌کنید؟\n(«خیر» = همین آیکن برای هر دو حالت)"))
            {
                var p2 = new IconPickerWindow(SystemIcons.ParseLocation(picker.Result).File) { Owner = Window.GetWindow(_host), Title = "آیکن سطل پر" };
                if (p2.ShowDialog() == true) full = p2.Result;
            }
            Do("تغییر آیکن " + d.Name, () => SystemIcons.SetIcon(d, picker.Result, full));
        }

        private UIElement IconThemesCard()
        {
            var card = Card("تم آیکن‌ها", "یک مجموعه آیکن هماهنگ برای همه آیکن‌های سیستمی بسازید. فایل‌ها در پوشه برنامه ذخیره می‌شوند.");
            var wrap = new WrapPanel();
            string? current = PersonalizeStore.Data.Applied.IconTheme;
            foreach (var t in SystemIcons.Themes)
            {
                var box = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
                var tiles = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
                foreach (var d in SystemIcons.All)
                    tiles.Children.Add(new Image { Source = SystemIcons.RenderTile(d, t, 48, full: false), Width = 40, Height = 40, Margin = new Thickness(2) });
                var frame = new Border { Child = tiles, Padding = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(current == t.Id ? 2 : 1) };
                frame.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
                frame.SetResourceReference(Border.BorderBrushProperty, current == t.Id ? "AccentBrush" : "BorderBrush");
                box.Children.Add(frame);
                var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                var apply = Btn(current == t.Id ? "اعمال‌شده ✓" : "اعمال", () => Do("اعمال تم آیکن " + t.Name, () => SystemIcons.ApplyTheme(t)), "AccentButton");
                System.Windows.Controls.DockPanel.SetDock(apply, System.Windows.Controls.Dock.Left);
                row.Children.Add(apply);
                row.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                box.Children.Add(row);
                wrap.Children.Add(box);
            }
            card.Children.Add(wrap);
            var reset = Btn("بازگرداندن همه آیکن‌ها", () => Do("بازگرداندن آیکن‌ها", () =>
            {
                foreach (var d in SystemIcons.All) SystemIcons.ResetIcon(d);
                PersonalizeStore.Data.Applied.IconTheme = null; PersonalizeStore.Save();
            }));
            reset.IsEnabled = PersonalizeStore.HasChanges(SystemIcons.GroupIcons);
            card.Children.Add(new WrapPanel { Children = { reset } });
            return Wrap(card);
        }

        // ================================================================== icon size & spacing

        private UIElement LayoutCard()
        {
            var card = Card("اندازه و فاصله آیکن‌ها");
            int cur = DesktopLayoutService.CurrentIconSize();
            var sizes = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var (name, size) in DesktopLayoutService.Sizes)
            {
                var rb = new RadioButton { Content = $"{name} ({Fa(size)} پیکسل)", GroupName = "pz-iconsize", IsChecked = cur == size, Margin = new Thickness(0, 0, 16, 0) };
                rb.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                int sz = size;
                rb.Click += (_, _) =>
                {
                    if (sz == DesktopLayoutService.CurrentIconSize()) return;
                    if (!Confirm("برای اعمال اندازه جدید، Explorer (نوار وظیفه و دسکتاپ) چند ثانیه بسته و دوباره باز می‌شود. پنجره‌های باز برنامه‌ها بسته نمی‌شوند.\n\nادامه می‌دهید؟"))
                    { Render(); return; }
                    Do("تغییر اندازه آیکن‌ها", () => { DesktopLayoutService.SetIconSize(sz); PersonalizeNative.RestartExplorer(); });
                };
                sizes.Children.Add(rb);
            }
            card.Children.Add(sizes);
            card.Children.Add(Muted($"اندازه فعلی: {Fa(cur)} پیکسل. تغییر اندازه نیاز به بازراه‌اندازی Explorer دارد (با تأیید شما انجام می‌شود)."));

            int sp = DesktopLayoutService.CurrentSpacing(), vsp = DesktopLayoutService.CurrentVerticalSpacing();
            var spH = Slider(32, 150, sp); var spV = Slider(32, 150, vsp);
            card.Children.Add(LabeledRow("فاصله افقی", spH, v => Fa((int)v) + " px"));
            card.Children.Add(LabeledRow("فاصله عمودی", spV, v => Fa((int)v) + " px"));
            card.Children.Add(Muted("فاصله آیکن‌ها پس از خروج از حساب و ورود دوباره اعمال می‌شود. پیش‌فرض ویندوز ۷۵ پیکسل است."));
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(Btn("ذخیره فاصله", () => Do("تغییر فاصله آیکن‌ها", () => DesktopLayoutService.SetSpacing((int)spH.Value, (int)spV.Value),
                "ذخیره شد. برای دیدن نتیجه یک بار از حساب ویندوز خارج و دوباره وارد شوید."), "AccentButton"));
            var rs = Btn("اندازه پیش‌فرض", () =>
            {
                if (!Confirm("اندازه آیکن‌ها برگردد؟ Explorer یک بار بازراه‌اندازی می‌شود.")) return;
                Do("بازگرداندن اندازه آیکن‌ها", () => { DesktopLayoutService.ResetSize(); PersonalizeNative.RestartExplorer(); });
            });
            rs.IsEnabled = PersonalizeStore.HasChanges(DesktopLayoutService.GroupSize);
            row.Children.Add(rs);
            var rsp = Btn("فاصله پیش‌فرض", () => Do("بازگرداندن فاصله آیکن‌ها", DesktopLayoutService.ResetSpacing, "برگشت. پس از ورود دوباره اعمال می‌شود."));
            rsp.IsEnabled = PersonalizeStore.HasChanges(DesktopLayoutService.GroupSpacing);
            row.Children.Add(rsp);
            card.Children.Add(row);
            return Wrap(card);
        }

        // ================================================================== wallpaper

        private UIElement WallpaperCard()
        {
            var card = Card("تصویر زمینه");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var thumb = new Border { Width = 220, Height = 124, CornerRadius = new CornerRadius(10), Background = WallpaperBrush(440), BorderThickness = new Thickness(1) };
            thumb.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            if (SafePreviewMode) thumb.Child = new TextBlock { Text = "پیش‌نمایش", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
            grid.Children.Add(thumb);

            var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
            var fit = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            foreach (WallpaperFit f in Enum.GetValues<WallpaperFit>()) fit.Items.Add(new ComboBoxItem { Content = WallpaperService.FitName(f), Tag = f });
            var curFit = WallpaperService.CurrentFit();
            fit.SelectedIndex = (int)curFit;
            right.Children.Add(LabeledRow("نحوه نمایش", fit));
            var row = new WrapPanel();
            row.Children.Add(Btn("انتخاب تصویر…", () =>
            {
                var dlg = new OpenFileDialog { Title = "تصویر زمینه", Filter = "تصاویر|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.jfif;*.webp" };
                if (dlg.ShowDialog(Window.GetWindow(_host)) != true) return;
                var f = (WallpaperFit)((ComboBoxItem)fit.SelectedItem).Tag;
                Do("تغییر تصویر زمینه", () => WallpaperService.SetStaticWallpaper(dlg.FileName, f));
            }, "AccentButton"));
            row.Children.Add(Btn("اعمال نحوه نمایش", () =>
            {
                string cur = PersonalizeStore.Data.Applied.WallpaperPath ?? WallpaperService.CurrentWallpaper();
                if (string.IsNullOrWhiteSpace(cur) || !File.Exists(cur)) { Info("تصویر زمینه فعلی پیدا نشد؛ ابتدا یک تصویر انتخاب کنید."); return; }
                var f = (WallpaperFit)((ComboBoxItem)fit.SelectedItem).Tag;
                Do("تغییر نحوه نمایش تصویر زمینه", () => WallpaperService.SetWallpaper(cur, f));
            }));
            right.Children.Add(row);

            right.Children.Add(new TextBlock { Text = "رنگ ساده به جای تصویر", Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold });
            var swatches = new WrapPanel();
            foreach (var hex in new[] { "#0B1324", "#1E3A8A", "#0F766E", "#14532D", "#7C2D12", "#4C1D95", "#374151", "#F1F5F9" })
            {
                var c = DesktopTheme.ParseColor(hex, Colors.Black);
                swatches.Children.Add(Swatch(c, () => Do("رنگ زمینه ساده", () => WallpaperService.SetSolidColor(c))));
            }
            right.Children.Add(swatches);

            var reset = Btn("بازگرداندن تصویر زمینه قبلی", () => Do("بازگرداندن تصویر زمینه", WallpaperService.Reset));
            reset.IsEnabled = PersonalizeStore.HasChanges(WallpaperService.Group) || PersonalizeStore.Data.OriginalWallpaper != null;
            reset.Margin = new Thickness(0, 10, 0, 0); reset.HorizontalAlignment = HorizontalAlignment.Left;
            right.Children.Add(reset);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            card.Children.Add(grid);
            card.Children.Add(Toggle("حرکت آرام پس‌زمینه", "در حالت «پر کردن» روی نمایشگر انتخابی فعال است. هنگام بازی تمام‌صفحه یا استفاده از باتری مکث می‌کند.", _s.AnimateWallpaper,
                enabled => { _s.AnimateWallpaper = enabled; _changed(); }));
            var gallery = new WrapPanel();
            foreach (var p in DesktopThemePresets.All.Where(p => p.Wallpaper != null))
            {
                var preview = new StackPanel();
                preview.Children.Add(DesktopThemePresets.Swatch(p, false));
                preview.Children.Add(new TextBlock { Text = p.Name, Margin = new Thickness(2, 4, 2, 0) });
                var b = Btn(p.Name, () => Do("تغییر تصویر زمینه", () =>
                    WallpaperService.SetStaticWallpaper(p.DisplayWallpaper!, (WallpaperFit)((ComboBoxItem)fit.SelectedItem).Tag)));
                b.Content = preview; b.Width = 150; b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                b.ToolTip = p.Description;
                System.Windows.Automation.AutomationProperties.SetName(b, "انتخاب تصویر زمینهٔ " + p.Name);
                gallery.Children.Add(b);
            }
            card.Children.Add(new TextBlock { Text = "پس‌زمینه‌های سه‌بعدی نگارخانه", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
            card.Children.Add(gallery);
            return Wrap(card);
        }

        private UIElement PhotoshopCard()
        {
            var card = Card("ویرایش پس‌زمینه در فتوشاپ", "تم را انتخاب کنید و PSD را در فتوشاپ باز کنید. لایه‌های رنگ تم و سایهٔ لبه در آغاز خاموش‌اند. پس از ویرایش، PSD را با Maximize Compatibility ذخیره کنید یا یک PNG بگیرید.");
            var themes = DesktopThemePresets.All.Where(p => p.Wallpaper != null).ToList();
            var picker = new ComboBox { ItemsSource = themes, DisplayMemberPath = "Name", MinWidth = 220,
                HorizontalAlignment = HorizontalAlignment.Left };
            picker.SelectedItem = themes.FirstOrDefault(p => p.DisplayWallpaper == PersonalizeStore.Data.Applied.WallpaperPath)
                ?? themes.FirstOrDefault(p => p.Id == _s.ThemePresetId) ?? themes[0];
            card.Children.Add(picker);
            var row = new WrapPanel();
            var status = Muted("");
            async Task Run(Func<Task> action)
            {
                row.IsEnabled = picker.IsEnabled = false;
                status.Text = "در حال پردازش تصویر…";
                try { await action(); }
                catch (Exception ex) { status.Text = "عملیات انجام نشد. " + ex.Message; AppLog.Warn("Wallpaper editor: " + ex.Message); }
                finally { row.IsEnabled = picker.IsEnabled = true; }
            }
            row.Children.Add(Btn("ذخیرهٔ PSD لایه‌دار…", () =>
            {
                var preset = (DesktopThemePresets.Preset)picker.SelectedItem;
                var dlg = new SaveFileDialog { FileName = preset.Id + ".psd", Filter = "Photoshop|*.psd", DefaultExt = ".psd" };
                if (dlg.ShowDialog(Window.GetWindow(_host)) != true) return;
                var source = preset.DisplayWallpaper!;
                var accent = DesktopTheme.ParseColor(preset.Accent, Colors.Teal);
                _ = Run(async () =>
                {
                    await Task.Run(() => WallpaperPsd.Export(source, dlg.FileName, accent));
                    status.Text = "فایل فتوشاپ ذخیره شد.";
                });
            }, "AccentButton"));
            row.Children.Add(Btn("وارد کردن نسخهٔ ویرایش‌شده…", () =>
            {
                var preset = (DesktopThemePresets.Preset)picker.SelectedItem;
                var dlg = new OpenFileDialog { Filter = "PSD، PNG یا JPEG|*.psd;*.png;*.jpg;*.jpeg" };
                if (dlg.ShowDialog(Window.GetWindow(_host)) != true) return;
                _ = Run(async () =>
                {
                    var path = await Task.Run(() => WallpaperPsd.Import(dlg.FileName));
                    PersonalizeStore.Transaction(() => WallpaperService.SetStaticWallpaper(path, WallpaperService.CurrentFit()));
                    PersonalizeStore.Data.WallpaperEdits[preset.Id] = path;
                    PersonalizeStore.Save();
                    Render();
                });
            }));
            var restore = Btn("بازگشت به تصویر اصلی تم", () =>
            {
                var preset = (DesktopThemePresets.Preset)picker.SelectedItem;
                Do("بازگشت به تصویر اصلی", () =>
                {
                    WallpaperService.SetStaticWallpaper(preset.WallpaperPath!, WallpaperService.CurrentFit());
                    PersonalizeStore.Data.WallpaperEdits.Remove(preset.Id);
                    PersonalizeStore.Save();
                });
            });
            void UpdateRestore() => restore.IsEnabled = picker.SelectedItem is DesktopThemePresets.Preset p
                && PersonalizeStore.Data.WallpaperEdits.ContainsKey(p.Id);
            picker.SelectionChanged += (_, _) => { UpdateRestore(); status.Text = ""; };
            UpdateRestore(); row.Children.Add(restore); card.Children.Add(row); card.Children.Add(status);
            return Wrap(card);
        }

        private UIElement SlideshowCard()
        {
            var ss = PersonalizeStore.Data.Slideshow;
            var card = Card("نمایش اسلاید و تصویر روز", "تصویر زمینه به‌طور خودکار عوض شود (تا وقتی نگارخانه باز است).");

            var folderText = new TextBlock { Text = string.IsNullOrWhiteSpace(ss.Folder) ? "پوشه‌ای انتخاب نشده" : ss.Folder, FlowDirection = FlowDirection.LeftToRight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            folderText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var fr = new DockPanel();
            var pick = Btn("انتخاب پوشه…", () =>
            {
                var dlg = new OpenFolderDialog { Title = "پوشه تصاویر" };
                if (dlg.ShowDialog(Window.GetWindow(_host)) != true) return;
                int n = WallpaperService.ImagesIn(dlg.FolderName).Count;
                if (n == 0) { Info("در این پوشه تصویری پیدا نشد."); return; }
                ss.Folder = dlg.FolderName; PersonalizeStore.Save(); Render();
            });
            System.Windows.Controls.DockPanel.SetDock(pick, System.Windows.Controls.Dock.Left);
            fr.Children.Add(pick);
            fr.Children.Add(folderText);
            card.Children.Add(fr);
            if (!string.IsNullOrWhiteSpace(ss.Folder))
                card.Children.Add(Muted($"{Fa(WallpaperService.ImagesIn(ss.Folder).Count)} تصویر در پوشه"));

            var interval = new ComboBox { MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (label, min) in new[] { ("هر ۱ دقیقه", 1), ("هر ۱۰ دقیقه", 10), ("هر ۳۰ دقیقه", 30), ("هر ۱ ساعت", 60), ("هر ۶ ساعت", 360), ("روزی یک بار", 1440) })
            {
                interval.Items.Add(new ComboBoxItem { Content = label, Tag = min });
                if (min == ss.IntervalMinutes) interval.SelectedIndex = interval.Items.Count - 1;
            }
            if (interval.SelectedIndex < 0) interval.SelectedIndex = 2;
            interval.SelectionChanged += (_, _) => { ss.IntervalMinutes = (int)((ComboBoxItem)interval.SelectedItem).Tag; PersonalizeStore.Save(); if (ss.Enabled) WallpaperService.StartSlideshow(); };
            card.Children.Add(LabeledRow("فاصله تعویض", interval));

            card.Children.Add(Toggle("ترتیب تصادفی", null, ss.Shuffle, v => { ss.Shuffle = v; PersonalizeStore.Save(); }));
            card.Children.Add(Toggle("نمایش اسلاید فعال باشد", null, ss.Enabled, v =>
            {
                if (v && WallpaperService.ImagesIn(ss.Folder).Count == 0) { Info("ابتدا پوشه‌ای با تصویر انتخاب کنید."); Render(); return; }
                ss.Enabled = v; if (v) ss.BingDaily = false; PersonalizeStore.Save();
                try { if (v) { WallpaperService.StartSlideshow(); WallpaperService.NextSlide(); } else WallpaperService.StopSlideshow(); }
                catch (Exception ex) { AppLog.Warn("Slideshow: " + ex.Message); Info("نمایش اسلاید شروع نشد: " + ex.Message); }
                Render();
            }));
            card.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
            card.Children.Add(Toggle("تصویر روز بینگ (روزانه)",
                "با روشن کردن این گزینه، نگارخانه روزی یک بار به bing.com وصل می‌شود و تصویر روز را در پوشه برنامه ذخیره و به‌عنوان تصویر زمینه قرار می‌دهد. اطلاعات شخصی ارسال نمی‌شود.",
                ss.BingDaily, v =>
                {
                    ss.BingDaily = v; if (v) { ss.Enabled = false; ss.LastBingDate = null; }
                    PersonalizeStore.Save();
                    if (v) WallpaperService.StartSlideshow(); else WallpaperService.StopSlideshow();
                    Render();
                }));
            var now = Btn("دریافت تصویر امروز بینگ", async () =>
            {
                if (!Confirm("نگارخانه برای دریافت تصویر به bing.com وصل می‌شود. ادامه می‌دهید؟")) return;
                try
                {
                    string file = await WallpaperService.DownloadBingAsync();
                    Do("تصویر روز بینگ", () => WallpaperService.SetWallpaper(file, Enum.TryParse<WallpaperFit>(PersonalizeStore.Data.Applied.WallpaperStyle, out var f) ? f : WallpaperFit.Fill));
                }
                catch (Exception ex) { AppLog.Warn("Bing: " + ex.Message); Info("دریافت تصویر از بینگ ممکن نشد (اتصال اینترنت را بررسی کنید).\n" + ex.Message); }
            });
            now.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(now);
            return Wrap(card);
        }

        // ================================================================== lock screen

        private UIElement LockScreenCard()
        {
            var card = Card("صفحه قفل", "تصویر صفحه قفل ویندوز را عوض کنید. اگر ویندوز (یا سیاست سازمان) اجازه ندهد، دلیلش نمایش داده می‌شود.");
            var row = new WrapPanel();
            var status = Muted(PersonalizeStore.Data.Applied.LockScreenImage is { } l && !SafePreviewMode ? "آخرین تصویر: " + Path.GetFileName(l) : "");
            row.Children.Add(Btn("انتخاب تصویر صفحه قفل…", async () =>
            {
                var dlg = new OpenFileDialog { Title = "تصویر صفحه قفل", Filter = "تصاویر|*.jpg;*.jpeg;*.png;*.bmp" };
                if (dlg.ShowDialog(Window.GetWindow(_host)) != true) return;
                string? err = await WallpaperService.SetLockScreenAsync(dlg.FileName);
                if (err == null) { AppLog.Info("Personalize: lock screen set"); Info("تصویر صفحه قفل تغییر کرد."); Render(); }
                else Info(err);
            }, "AccentButton"));
            row.Children.Add(Btn("تنظیمات صفحه قفل ویندوز", () => OpenUri("ms-settings:lockscreen")));
            card.Children.Add(row);
            card.Children.Add(status);
            card.Children.Add(Muted("برای برگرداندن صفحه قفل، از تنظیمات ویندوز «Windows Spotlight» یا تصویر دلخواه را انتخاب کنید (ویندوز تصویر قبلی را در اختیار برنامه‌ها نمی‌گذارد)."));
            return Wrap(card);
        }

        // ================================================================== accent

        private UIElement AccentCard()
        {
            var card = Card("رنگ تأکیدی و شفافیت", "رنگ منوی شروع، دکمه‌ها و حاشیه پنجره‌های ویندوز.");
            var cur = AccentService.CurrentAccent();
            var sw = new WrapPanel();
            foreach (var (name, hex) in AccentService.Presets)
            {
                var c = DesktopTheme.ParseColor(hex, Colors.Teal);
                var b = Swatch(c, () => Do("تغییر رنگ تأکیدی به " + name, () => AccentService.SetAccent(c)), selected: cur == c);
                b.ToolTip = name;
                sw.Children.Add(b);
            }
            card.Children.Add(sw);

            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var hexBox = new TextBox { Width = 100, Text = cur is { } cc ? $"#{cc.R:X2}{cc.G:X2}{cc.B:X2}" : "#14B8A6", FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
            row.Children.Add(hexBox);
            row.Children.Add(Btn("اعمال رنگ دلخواه", () =>
            {
                var c = DesktopTheme.ParseColor(hexBox.Text.Trim(), Colors.Transparent);
                if (c == Colors.Transparent) { Info("کد رنگ معتبر نیست (مثال: ‎#14B8A6)."); return; }
                Do("تغییر رنگ تأکیدی", () => AccentService.SetAccent(Color.FromRgb(c.R, c.G, c.B)));
            }));
            row.Children.Add(Btn("رنگ از روی والپیپر", () =>
            {
                string wp = PersonalizeStore.Data.Applied.WallpaperPath ?? WallpaperService.CurrentWallpaper();
                var c = WallpaperService.DominantColor(wp);
                if (c == null) { Info("رنگ غالب تصویر زمینه پیدا نشد (شاید رنگ ساده انتخاب شده باشد)."); return; }
                Do("رنگ تأکیدی از تصویر زمینه", () => AccentService.SetAccent(c.Value));
            }, "GoldButton"));
            card.Children.Add(row);

            card.Children.Add(Toggle("جلوه‌های شفافیت", null, AccentService.TransparencyEnabled(), v => Do("شفافیت", () => AccentService.SetTransparency(v))));
            card.Children.Add(Toggle("رنگ تأکیدی روی منوی شروع و نوار وظیفه", "در حالت روشن ویندوز ممکن است اثر نداشته باشد.", AccentService.AccentOnTaskbar(), v => Do("رنگ نوار وظیفه", () => AccentService.SetAccentOnTaskbar(v))));
            card.Children.Add(Toggle("رنگ تأکیدی روی نوار عنوان و حاشیه پنجره‌ها", null, AccentService.AccentOnTitleBars(), v => Do("رنگ نوار عنوان", () => AccentService.SetAccentOnTitleBars(v))));
            var reset = Btn("بازگرداندن رنگ و شفافیت", () => Do("بازگرداندن رنگ", AccentService.Reset));
            reset.IsEnabled = PersonalizeStore.HasChanges(AccentService.Group);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(reset);
            return Wrap(card);
        }

        // ================================================================== cursors

        private UIElement CursorCard()
        {
            var card = Card("نشانگر ماوس", "طرح‌های نصب‌شده روی ویندوز و اندازه نشانگر.");
            var schemes = CursorService.Schemes();
            string curName = CursorService.CurrentSchemeName();
            var combo = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var sc in schemes)
            {
                combo.Items.Add(new ComboBoxItem { Content = sc.DisplayName + (sc.IsSystem ? "" : "  (کاربر)"), Tag = sc });
                if (sc.Name.Equals(curName, StringComparison.OrdinalIgnoreCase)) combo.SelectedIndex = combo.Items.Count - 1;
            }
            var row = new WrapPanel();
            row.Children.Add(combo);
            var apply = Btn("اعمال طرح", () =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: CursorScheme sc }) Do("طرح نشانگر " + sc.DisplayName, () => CursorService.ApplyScheme(sc));
            }, "AccentButton");
            apply.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(apply);
            card.Children.Add(LabeledRow("طرح", row));
            if (schemes.Count == 0) card.Children.Add(Muted("طرحی پیدا نشد."));

            var size = Slider(1, 15, CursorService.CurrentSize());
            size.IsSnapToTickEnabled = true; size.TickFrequency = 1;
            card.Children.Add(LabeledRow("اندازه", size, v => Fa((int)v)));
            var r2 = new WrapPanel();
            r2.Children.Add(Btn("اعمال اندازه", () => Do("اندازه نشانگر", () => CursorService.SetSize((int)size.Value))));
            var reset = Btn("بازگرداندن نشانگر", () => Do("بازگرداندن نشانگر", CursorService.Reset));
            reset.IsEnabled = PersonalizeStore.HasChanges(CursorService.Group);
            r2.Children.Add(reset);
            card.Children.Add(r2);
            return Wrap(card);
        }

        // ================================================================== shortcuts

        private UIElement ShortcutsCard()
        {
            var card = Card("میانبرهای دسکتاپ", "میانبرهای کاربردی روی دسکتاپ شما ساخته یا حذف می‌شوند.");
            var wrap = new WrapPanel();
            foreach (var sc in ShortcutService.All)
            {
                bool exists = ShortcutService.Exists(sc);
                var cb = new CheckBox { IsChecked = exists, Margin = new Thickness(0, 4, 18, 4), VerticalContentAlignment = VerticalAlignment.Center };
                cb.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                cb.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = sc.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = sc.Name, VerticalAlignment = VerticalAlignment.Center }
                    }
                };
                cb.Click += (_, _) => Do((cb.IsChecked == true ? "ساخت میانبر " : "حذف میانبر ") + sc.Name, () =>
                {
                    if (cb.IsChecked == true) ShortcutService.Create(sc); else ShortcutService.Remove(sc);
                });
                wrap.Children.Add(cb);
            }
            card.Children.Add(wrap);
            return Wrap(card);
        }

        // ================================================================== theme file

        private UIElement ThemeFileCard()
        {
            var card = Card("تم نگارخانه (‎.negar‎)", "تصویر زمینه، رنگ، آیکن‌ها، نشانگر و تنظیمات دسکتاپ نگارخانه را در یک فایل ذخیره کنید یا تم دیگران را بارگذاری کنید.");
            var row = new WrapPanel();
            row.Children.Add(Btn("ذخیره تم فعلی…", () =>
            {
                var dlg = new SaveFileDialog { Title = "ذخیره تم", Filter = "تم نگارخانه|*.negar", FileName = "تم من.negar", AddExtension = true, DefaultExt = ".negar" };
                if (dlg.ShowDialog(Window.GetWindow(_host)) != true) return;
                try
                {
                    ThemePackage.Export(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName));
                    AppLog.Info("Theme exported: " + dlg.FileName);
                    Info("تم ذخیره شد.");
                }
                catch (Exception ex) { AppLog.Error("Theme export failed", ex); Info("ذخیره تم ممکن نشد: " + ex.Message); }
            }, "AccentButton"));
            row.Children.Add(Btn("بارگذاری تم…", ImportTheme));
            card.Children.Add(row);
            return Wrap(card);
        }

        private void ImportTheme()
        {
            var dlg = new OpenFileDialog { Title = "بارگذاری تم", Filter = "تم نگارخانه|*.negar|فایل zip|*.zip" };
            if (dlg.ShowDialog(Window.GetWindow(_host)) != true) return;
            try
            {
                var (m, folder) = ThemePackage.Open(dlg.FileName);
                var parts = new List<string>();
                if (m.Wallpaper != null) parts.Add("تصویر زمینه"); else if (m.SolidColorHex != null) parts.Add("رنگ زمینه");
                if (m.AccentHex != null) parts.Add("رنگ تأکیدی");
                if (m.Icons.Count > 0) parts.Add($"{Fa(m.Icons.Count)} آیکن");
                if (m.CursorScheme != null) parts.Add("نشانگر ماوس");
                if (!Confirm($"تم «{m.Name}» شامل: {string.Join("، ", parts.DefaultIfEmpty("—"))}\n\nاعمال شود؟ (همه موارد قابل بازگشت هستند)")) return;
                bool desk = m.HasDesktopSettings && Confirm("این تم تنظیمات دسکتاپ نگارخانه (نوار بالا، داک و …) را هم دارد. آن‌ها هم جایگزین شوند؟");
                var errors = ThemePackage.Apply(m, folder, desk, imported =>
                {
                    foreach (var p in typeof(DesktopSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
                        p.SetValue(_s, p.GetValue(imported));
                    _changed();
                });
                Info(errors.Count == 0 ? "تم اعمال شد." : "تم با این خطاها اعمال شد:\n• " + string.Join("\n• ", errors));
            }
            catch (Exception ex) { AppLog.Error("Theme import failed", ex); Info("بارگذاری تم ممکن نشد: " + ex.Message); }
            Render();
        }

        // ================================================================== small UI helpers

        private static StackPanel Card(string? title = null, string? subtitle = null)
        {
            var p = new StackPanel();
            if (title != null) p.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, subtitle == null ? 10 : 2) });
            if (subtitle != null) { var m = Muted(subtitle); m.Margin = new Thickness(0, 0, 0, 10); p.Children.Add(m); }
            return p;
        }

        private static Border Wrap(StackPanel content)
        {
            var b = new Border { Child = content, Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return b;
        }

        private static TextBlock Muted(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return t;
        }

        private static Border Chip(string text, string brushKey)
        {
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BorderBrushProperty, brushKey);
            var t = new TextBlock { Text = text, FontSize = 12 };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            b.Child = t;
            return b;
        }

        private static Button Btn(string text, Action click, string style = "GhostButton")
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 6), MinWidth = 0 };
            if (Application.Current?.TryFindResource(style) is Style st && st.TargetType.IsInstanceOfType(b)) b.Style = st;
            b.Click += (_, _) => click();
            return b;
        }

        private static Button Swatch(Color c, Action click, bool selected = false)
        {
            var b = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(0),
                BorderThickness = new Thickness(selected ? 3 : 1), Cursor = System.Windows.Input.Cursors.Hand,
                Content = new Border { Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(15), Width = 24, Height = 24 },
                ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            };
            if (Application.Current?.TryFindResource("Swatch") is Style st && st.TargetType.IsInstanceOfType(b)) b.Style = st;
            b.Background = new SolidColorBrush(c);
            b.SetResourceReference(Control.BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
            b.Click += (_, _) => click();
            return b;
        }

        private UIElement Toggle(string label, string? hint, bool value, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = value, Margin = new Thickness(0, 5, 0, 5), VerticalContentAlignment = VerticalAlignment.Center };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            if (hint != null) text.Children.Add(Muted(hint));
            cb.Content = text;
            cb.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            cb.Click += (_, _) => set(cb.IsChecked == true);
            return cb;
        }

        private static Slider Slider(double min, double max, double value) =>
            new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 220, VerticalAlignment = VerticalAlignment.Center };

        private static UIElement LabeledRow(string label, FrameworkElement control, Func<double, string>? valueText = null)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            if (valueText != null && control is RangeBase rb)
            {
                var v = Muted(valueText(rb.Value));
                v.Margin = new Thickness(10, 0, 0, 0); v.VerticalAlignment = VerticalAlignment.Center;
                rb.ValueChanged += (_, e) => v.Text = valueText(e.NewValue);
                Grid.SetColumn(v, 2);
                g.Children.Add(v);
            }
            return g;
        }

        private static string Fa(int n) => DesktopTheme.Digits(n.ToString(), true);

        private static void OpenUri(string uri)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn("Open " + uri + ": " + ex.Message); }
        }
    }
}
