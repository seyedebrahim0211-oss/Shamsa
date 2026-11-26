using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shamsa.Desktop.Shell;
using Shamsa.Services;
using WDock = System.Windows.Controls.Dock;

namespace Shamsa.Desktop.Rules;

/// <summary>Desktop Studio page: per-app rules and saved layout profiles («حالت‌های کاری»).</summary>
public static class RulesStudioPage
{
    private static readonly (string Value, string Label)[] WmLayouts =
    {
        ("", "تغییر نکند"), ("Bsp", "مارپیچی (BSP)"), ("Columns", "ستونی"), ("Rows", "سطری"), ("MainStack", "اصلی + پشته"), ("Monocle", "تک‌پنجره"),
    };

    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var root = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
        var rulesList = new StackPanel();
        var layoutsList = new StackPanel();

        // ---------------------------------------------------------------- rules
        var rulesCard = Card("قوانین برنامه‌ها",
            "برای هر برنامه مشخص کن پنجره‌اش شناور بماند، همیشه رو باشد، بزرگ باز شود یا جای ثابتی داشته باشد. قوانین روی پنجره‌های تازه اعمال می‌شوند.");
        var addRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        var picker = new ComboBox { MinWidth = 240, IsEditable = true, FlowDirection = FlowDirection.LeftToRight, ToolTip = "برنامه در حال اجرا را انتخاب کن یا نام فایل اجرایی را بنویس" };
        void FillPicker()
        {
            picker.Items.Clear();
            try
            {
                foreach (var exe in ShellInterop.GetAppWindows().Select(w => RulesNative.ExeName(w.ExePath))
                             .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                    picker.Items.Add(exe);
            }
            catch { }
        }
        FillPicker();
        picker.DropDownOpened += (_, _) => { string t = picker.Text; FillPicker(); picker.Text = t; };
        var add = Button("＋ افزودن قانون", "AccentButton", () =>
        {
            string exe = (picker.Text ?? "").Trim();
            if (exe.Length == 0) return;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            s.AppRules.Add(new AppRule { Process = exe, Float = true });
            picker.Text = "";
            RefillRules();
            changed();
        });
        add.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(add, WDock.Right);
        addRow.Children.Add(add);
        addRow.Children.Add(picker);
        rulesCard.Children.Add(addRow);
        rulesCard.Children.Add(rulesList);
        var applyNow = Button("اعمال روی پنجره‌های باز", "GhostButton", () =>
        {
            int n = RulesModule.ApplyRulesToOpenWindows(s.AppRules);
            Notifier.Show(n > 0 ? $"قوانین روی {n} پنجره اعمال شد." : "پنجره بازی با این قوانین پیدا نشد.");
        });
        applyNow.HorizontalAlignment = HorizontalAlignment.Left;
        applyNow.Margin = new Thickness(0, 8, 0, 0);
        rulesCard.Children.Add(applyNow);
        rulesCard.Children.Add(Muted("دسکتاپ مجازی: ویندوز به برنامه‌ها اجازه نمی‌دهد پنجره برنامه دیگری را خودکار به دسکتاپ دیگر ببرند؛ نگارخانه فقط تشخیص می‌دهد و یادآوری می‌کند."));
        root.Children.Add(Wrap(rulesCard));

        void RefillRules()
        {
            rulesList.Children.Clear();
            if (s.AppRules.Count == 0) rulesList.Children.Add(Muted("هنوز قانونی نساخته‌ای."));
            for (int i = 0; i < s.AppRules.Count; i++)
            {
                var rule = s.AppRules[i];
                rulesList.Children.Add(RuleEditor(rule, changed, () => { s.AppRules.Remove(rule); RefillRules(); changed(); }));
            }
        }
        RefillRules();

        // ---------------------------------------------------------------- layouts
        var layoutsCard = Card("حالت‌های کاری",
            "برنامه‌ها را همان‌طور که دوست داری بچین و ذخیره کن؛ بعداً با یک کلیک یا میانبر، برنامه‌های بسته باز و همه پنجره‌ها سر جایشان چیده می‌شوند.");
        var saveRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        var nameBox = new TextBox { Text = "حالت کار", Padding = new Thickness(6, 4, 6, 4), MinWidth = 200 };
        var save = Button("ذخیره چیدمان فعلی", "AccentButton", () =>
        {
            try
            {
                var p = LayoutProfiles.Capture(nameBox.Text);
                if (p.Windows.Count == 0) { Notifier.Show("پنجره بازی برای ذخیره پیدا نشد."); return; }
                s.Layouts.Add(p);
                RefillLayouts();
                changed();
                Notifier.Show($"«{p.Name}» با {p.Windows.Count} پنجره ذخیره شد.");
            }
            catch (Exception ex) { AppLog.Error("Capture layout failed", ex); }
        });
        save.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(save, WDock.Right);
        saveRow.Children.Add(save);
        saveRow.Children.Add(nameBox);
        layoutsCard.Children.Add(saveRow);
        layoutsCard.Children.Add(layoutsList);
        root.Children.Add(Wrap(layoutsCard));

        void RefillLayouts()
        {
            layoutsList.Children.Clear();
            if (s.Layouts.Count == 0) layoutsList.Children.Add(Muted("هنوز حالتی ذخیره نشده است."));
            for (int i = 0; i < s.Layouts.Count; i++)
            {
                var profile = s.Layouts[i];
                layoutsList.Children.Add(LayoutEditor(profile, i, changed, () => { s.Layouts.Remove(profile); RefillLayouts(); changed(); }));
            }
        }
        RefillLayouts();

        return root;
    }

    // ---------------------------------------------------------------- rule editor

    private static UIElement RuleEditor(AppRule r, Action changed, Action delete)
    {
        var p = new StackPanel();
        var head = new DockPanel();
        var del = Button("✕ حذف", "GhostButton", delete);
        DockPanel.SetDock(del, WDock.Right);
        head.Children.Add(del);
        var icon = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) };
        try
        {
            var path = ShellInterop.GetAppWindows().FirstOrDefault(w => string.Equals(RulesNative.ExeName(w.ExePath), r.Process, StringComparison.OrdinalIgnoreCase))?.ExePath;
            icon.Source = ShellInterop.GetAppIcon(path, 32);
        }
        catch { }
        head.Children.Add(icon);
        var procBox = new TextBox { Text = r.Process, Width = 200, FlowDirection = FlowDirection.LeftToRight, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        procBox.TextChanged += (_, _) => { r.Process = procBox.Text.Trim(); changed(); };
        head.Children.Add(procBox);
        head.Children.Add(Check("فعال", r.Enabled, v => { r.Enabled = v; changed(); }, new Thickness(12, 0, 0, 0)));
        p.Children.Add(head);

        var flags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        flags.Children.Add(Check("شناور (کاشی نشود)", r.Float, v => { r.Float = v; changed(); }));
        flags.Children.Add(Check("همیشه رو", r.AlwaysOnTop, v => { r.AlwaysOnTop = v; changed(); }));
        flags.Children.Add(Check("بزرگ باز شود", r.StartMaximized, v => { r.StartMaximized = v; changed(); }));
        p.Children.Add(flags);

        p.Children.Add(Labeled("عنوان شامل (اختیاری)", TextInput(r.TitleContains ?? "", v => { r.TitleContains = string.IsNullOrWhiteSpace(v) ? null : v; changed(); })));

        var rectPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var nums = new WrapPanel();
        TextBox xBox = Num(r.X), yBox = Num(r.Y), wBox = Num(r.Width), hBox = Num(r.Height);
        void Hook(TextBox b, Action<int> set) => b.TextChanged += (_, _) => { if (int.TryParse(DigitsToLatin(b.Text), out int v)) { set(v); changed(); } };
        Hook(xBox, v => r.X = v); Hook(yBox, v => r.Y = v); Hook(wBox, v => r.Width = Math.Max(120, v)); Hook(hBox, v => r.Height = Math.Max(80, v));
        nums.Children.Add(Pair("X", xBox)); nums.Children.Add(Pair("Y", yBox)); nums.Children.Add(Pair("پهنا", wBox)); nums.Children.Add(Pair("ارتفاع", hBox));
        rectPanel.Children.Add(nums);

        var monitors = RulesNative.Monitors();
        var monOptions = monitors.Select((m, i) => (i.ToString(), $"نمایشگر {i + 1}{(m.Primary ? " (اصلی)" : "")} — {m.Bounds.Width}×{m.Bounds.Height}")).ToArray();
        if (monOptions.Length == 0) monOptions = new[] { ("0", "نمایشگر ۱") };
        var monCombo = Combo(monOptions, r.MonitorIndex.ToString(), v => { r.MonitorIndex = int.Parse(v); changed(); });
        rectPanel.Children.Add(Labeled("نمایشگر", monCombo));

        var useCurrent = Button("📍 استفاده از جای فعلی پنجره", "GhostButton", () =>
        {
            var w = ShellInterop.GetAppWindows().FirstOrDefault(x => string.Equals(RulesNative.ExeName(x.ExePath), r.Process, StringComparison.OrdinalIgnoreCase));
            if (w == null) { Notifier.Show($"پنجره‌ای از «{r.Process}» باز نیست."); return; }
            var vis = RulesNative.VisualRect(w.Hwnd);
            int mi = RulesNative.MonitorIndexOf(w.Hwnd);
            var mons = RulesNative.Monitors();
            var work = mons.Count > mi ? mons[mi].Work : new Rect(0, 0, 0, 0);
            r.MonitorIndex = mi;
            r.X = (int)(vis.Left - work.Left); r.Y = (int)(vis.Top - work.Top);
            r.Width = (int)vis.Width; r.Height = (int)vis.Height;
            r.UseFixedRect = true;
            xBox.Text = r.X.ToString(); yBox.Text = r.Y.ToString(); wBox.Text = r.Width.ToString(); hBox.Text = r.Height.ToString();
            monCombo.SelectedIndex = Math.Min(mi, monCombo.Items.Count - 1);
            changed();
        });
        useCurrent.HorizontalAlignment = HorizontalAlignment.Left;
        rectPanel.Children.Add(useCurrent);
        rectPanel.Visibility = r.UseFixedRect ? Visibility.Visible : Visibility.Collapsed;
        p.Children.Add(Check("جای ثابت روی صفحه", r.UseFixedRect, v => { r.UseFixedRect = v; rectPanel.Visibility = v ? Visibility.Visible : Visibility.Collapsed; changed(); }));
        p.Children.Add(rectPanel);

        int desktops = Math.Max(4, RulesNative.VirtualDesktopIds().Count);
        var deskOptions = new[] { ("0", "مهم نیست") }.Concat(Enumerable.Range(1, desktops).Select(i => (i.ToString(), $"دسکتاپ {i} (فقط یادآوری)"))).ToArray();
        p.Children.Add(Labeled("دسکتاپ مجازی", Combo(deskOptions, r.VirtualDesktop.ToString(), v => { r.VirtualDesktop = int.Parse(v); changed(); })));

        var b = new Border { Child = p, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    // ---------------------------------------------------------------- layout editor

    private static UIElement LayoutEditor(LayoutProfile profile, int index, Action changed, Action delete)
    {
        var p = new StackPanel();
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("▶ بازیابی", "AccentButton", () => _ = LayoutProfiles.RestoreAsync(profile)));
        actions.Children.Add(Button("✕ حذف", "GhostButton", delete));
        DockPanel.SetDock(actions, WDock.Right);
        head.Children.Add(actions);
        var name = new TextBox { Text = profile.Name, Width = 220, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        name.TextChanged += (_, _) => { profile.Name = string.IsNullOrWhiteSpace(name.Text) ? "حالت کار" : name.Text.Trim(); changed(); };
        head.Children.Add(name);
        p.Children.Add(head);

        p.Children.Add(Labeled("چیدمان مدیر پنجره", Combo(WmLayouts, profile.WindowManagerLayout ?? "", v => { profile.WindowManagerLayout = v.Length == 0 ? null : v; changed(); })));
        p.Children.Add(Labeled("میانبر", HotkeyBox(profile, "layout." + index, changed)));

        var entries = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var e in profile.Windows)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var launch = Check("اجرا اگر بسته است", e.LaunchIfNotRunning, v => { e.LaunchIfNotRunning = v; changed(); });
            DockPanel.SetDock(launch, WDock.Right);
            row.Children.Add(launch);
            row.Children.Add(new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), Source = SafeIcon(e.ExePath) });
            var label = new TextBlock
            {
                Text = $"{ShellInterop.FriendlyAppName(e.ExePath)}  ·  " + (e.Maximized ? "بزرگ" : DesktopTheme.Digits($"{e.Width}×{e.Height}", true)),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = e.ExePath, TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(label);
            entries.Children.Add(row);
        }
        p.Children.Add(entries);

        var b = new Border { Child = p, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    private static ImageSource? SafeIcon(string path)
    {
        try { return ShellInterop.GetAppIcon(path, 32); } catch { return null; }
    }

    private static UIElement HotkeyBox(LayoutProfile profile, string bindingId, Action changed)
    {
        var stack = new StackPanel();
        var box = new TextBox
        {
            Text = profile.HotkeyKey > 0 ? GlobalHotkeyService.FormatShortcut(profile.HotkeyModifiers, profile.HotkeyKey) : "بدون میانبر",
            IsReadOnly = true, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Thickness(6, 3, 6, 3), ToolTip = "کلیک کن و ترکیب را فشار بده؛ Backspace برای حذف"
        };
        var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if (k is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                profile.HotkeyKey = 0; profile.HotkeyModifiers = 0; box.Text = "بدون میانبر"; warn.Text = ""; changed(); return;
            }
            uint m = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) m |= NativeMethods.MOD_CONTROL;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) m |= NativeMethods.MOD_ALT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) m |= NativeMethods.MOD_SHIFT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) m |= NativeMethods.MOD_WIN;
            if ((m & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_WIN)) == 0) { warn.Text = "حداقل یک کلید Ctrl / Alt / Win لازم است."; return; }
            int vk = KeyInterop.VirtualKeyFromKey(k);
            box.Text = GlobalHotkeyService.FormatShortcut(m, vk);
            string? conflict = HotkeyRegistry.FindConflict(m, vk, bindingId);
            if (conflict != null) { warn.Text = "⚠ تداخل با " + conflict + " — ترکیب دیگری انتخاب کن."; return; }
            warn.Text = "";
            profile.HotkeyModifiers = m; profile.HotkeyKey = vk;
            changed();
        };
        stack.Children.Add(box);
        stack.Children.Add(warn);
        return stack;
    }

    // ---------------------------------------------------------------- small building blocks

    private static StackPanel Card(string title, string subtitle)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        var m = Muted(subtitle); m.Margin = new Thickness(0, 0, 0, 8);
        p.Children.Add(m);
        return p;
    }

    private static Border Wrap(StackPanel content)
    {
        var b = new Border { Child = content, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
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

    private static Button Button(string text, string style, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2, 0, 2, 0) };
        if (Application.Current?.TryFindResource(style) is Style st && st.TargetType.IsInstanceOfType(b)) b.Style = st;
        b.Click += (_, _) =>
        {
            try { click(); } catch (Exception ex) { AppLog.Error("Rules studio action failed", ex); }
        };
        return b;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set, Thickness? margin = null)
    {
        var cb = new CheckBox { Content = text, IsChecked = value, Margin = margin ?? new Thickness(0, 3, 16, 3), Foreground = DesktopTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        return cb;
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static TextBox TextInput(string value, Action<string> set)
    {
        var t = new TextBox { Text = value, Padding = new Thickness(4, 2, 4, 2), MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        t.TextChanged += (_, _) => set(t.Text);
        return t;
    }

    private static TextBox Num(int v) => new() { Text = v.ToString(), Width = 70, FlowDirection = FlowDirection.LeftToRight, Padding = new Thickness(4, 2, 4, 2) };

    private static UIElement Pair(string label, TextBox box)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 12, 2) };
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(box);
        return sp;
    }

    private static ComboBox Combo((string Value, string Label)[] options, string current, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (v, l) in options) combo.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? combo.Items.Cast<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string v }) set(v); };
        return combo;
    }

    internal static string DigitsToLatin(string s)
    {
        var c = s.Trim().ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            if (c[i] is >= '۰' and <= '۹') c[i] = (char)('0' + (c[i] - '۰'));
            else if (c[i] is >= '٠' and <= '٩') c[i] = (char)('0' + (c[i] - '٠'));
        }
        return new string(c);
    }
}
