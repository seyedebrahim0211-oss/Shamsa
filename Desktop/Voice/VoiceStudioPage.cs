using System.Windows;
using System.Windows.Controls;
using Shamsa.Desktop.Launcher;

namespace Shamsa.Desktop.Voice;

/// <summary>Desktop Studio page for Persian voice commands.</summary>
public static class VoiceStudioPage
{
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var v = s.Voice;
        var root = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
        root.Children.Add(StudioUi.Title("فرمان صوتی فارسی"));
        root.Children.Add(StudioUi.Muted("دکمه را نگه دارید، فرمان را بگویید و رها کنید. تشخیص روی همین رایانه انجام می‌شود."));
        var card = StudioUi.Card(root, "فرمان صوتی");
        card.Children.Add(StudioUi.Toggle("فعال‌سازی میانبر سراسری میکروفون", "یک بار بزنید و صحبت کنید؛ دوباره بزنید تا فرمان اجرا شود. ضبط پس از ۳۰ ثانیه پایان می‌یابد.", v.Enabled, x => { v.Enabled = x; changed(); }));
        card.Children.Add(DesktopStudioView.ShortcutEditor(v.HotkeyModifiers, v.HotkeyKey, VoiceModule.Shortcut,
            (mods, key) => { v.HotkeyModifiers = mods; v.HotkeyKey = key; changed(); }));
        var wake = new TextBox { Text = v.WakeWord, Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
        wake.TextChanged += (_, _) => { v.WakeWord = wake.Text.Trim(); changed(); };
        card.Children.Add(StudioUi.Labeled("کلمه بیدارباش", wake));
        card.Children.Add(StudioUi.Toggle("گفتار فارسی آفلاین", "در حالت خاموش، موتور انگلیسی ویندوز عبارت‌های فینگلیش را تشخیص می‌دهد.", v.PreferPersian, x => { v.PreferPersian = x; changed(); }));
        var status = StudioUi.Muted("");
        var install = new Button { Content = "نصب بستهٔ فارسی (۵۱ مگابایت)", Visibility = VoiceEngine.Installed ? Visibility.Collapsed : Visibility.Visible };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false; status.Text = "در حال دریافت بستهٔ فارسی…";
            try { await VoiceEngine.InstallAsync(); install.Visibility = Visibility.Collapsed; status.Text = "بستهٔ فارسی آماده است."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { install.IsEnabled = true; }
        };
        card.Children.Add(install);
        card.Children.Add(VoiceModule.HoldButton(() => s, text => status.Text = text));
        card.Children.Add(status);
        card.Children.Add(StudioUi.Button("تنظیمات دسترسی میکروفون ویندوز", () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })));
        var help = StudioUi.Card(root, "فرمان‌ها");
        foreach (var (fa, fin, desc) in new[]
        {
            ("نگار باز کن کروم", "negar baz kon chrome", "اجرای برنامه"),
            ("نگار جستجو", "negar jostojoo", "جستجوی سریع"),
            ("نگار چیدمان ستونی / سطری / مارپیچی / تک پنجره", "negar chidman sotuni / satri / marpichi / tak panjere", "چیدمان پنجره‌ها"),
            ("نگار حالت کار", "negar halate kar · negar halate yek", "چیدمان ذخیره‌شده"),
            ("نگار قفل کن", "negar ghofl kon", "قفل صفحه"),
            ("نگار مرکز کنترل", "negar markaze control", "مرکز کنترل"),
            ("نگار عکس بگیر / ضبط کن", "negar screenshot / negar record", "اسکرین‌شات و ضبط"),
            ("نگار کلیپ بورد", "negar clipboard", "تاریخچه کلیپ‌بورد"),
        }.Concat(VoiceCommands.SystemCommandHelp().Select(h => ("نگار " + h.Persian, "negar " + h.Finglish, h.Description))))
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
            var a = new TextBlock { Text = fa, TextWrapping = TextWrapping.Wrap, Foreground = DesktopTheme.TextBrush };
            var b = new TextBlock { Text = fin, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Right };
            b.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
            var c = new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap };
            c.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(b, 1); Grid.SetColumn(c, 2);
            row.Children.Add(a); row.Children.Add(b); row.Children.Add(c);
            help.Children.Add(row);
        }
        return root;
    }
}
