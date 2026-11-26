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
        root.Children.Add(StudioUi.Muted("با گفتن «شمسا» و یک فرمان، برنامه باز کن، چیدمان پنجره‌ها را عوض کن یا اسکرین‌شات بگیر. میکروفون فقط وقتی این گزینه روشن است استفاده می‌شود و صدا جایی ارسال نمی‌شود."));

        var card = StudioUi.Card(root, "رفتار");
        card.Children.Add(StudioUi.Toggle("فعال", "به محیط دسکتاپ شمسا و یک میکروفون نیاز دارد", v.Enabled, x => { v.Enabled = x; changed(); }));
        var wake = new TextBox { Text = v.WakeWord, Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
        wake.TextChanged += (_, _) => { v.WakeWord = wake.Text.Trim(); changed(); };
        card.Children.Add(StudioUi.Labeled("کلمه بیدارباش", wake));
        card.Children.Add(StudioUi.Toggle("ترجیح تشخیص فارسی", "اگر بسته گفتار فارسی نصب باشد؛ وگرنه عبارت‌های فینگلیش با تشخیص انگلیسی", v.PreferPersian, x => { v.PreferPersian = x; changed(); }));
        card.Children.Add(StudioUi.Toggle("نمایش آنچه شنیده شد", "پیام کوچک بالای صفحه", v.Feedback, x => { v.Feedback = x; changed(); }));

        var engine = StudioUi.Card(root, "موتور تشخیص");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = DesktopTheme.TextBrush };
        void UpdateStatus() => status.Text = "وضعیت: " + VoiceModule.ActiveEngineDescription;
        UpdateStatus();
        Action onStatus = () => status.Dispatcher.BeginInvoke(UpdateStatus);
        VoiceModule.StatusChanged += onStatus;
        status.Unloaded += (_, _) => VoiceModule.StatusChanged -= onStatus;
        engine.Children.Add(status);
        engine.Children.Add(StudioUi.Muted("برای تشخیص فارسی واقعی، اگر ویندوز بسته گفتار فارسی را پشتیبانی کند آن را از «تنظیمات › زمان و زبان › گفتار» نصب کن. بدون آن، فرمان‌ها را به فینگلیش بگو (مثلاً shamsa baz kon chrome)."));
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        result.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
        Button? test = null;
        test = StudioUi.Button("🎙 آزمایش (۵ ثانیه گوش می‌دهد)", async () =>
        {
            test!.IsEnabled = false;
            result.Text = "در حال گوش دادن… یک فرمان بگو";
            try { result.Text = await VoiceModule.TestListenAsync(s, 5); }
            finally { test.IsEnabled = true; }
        }, "AccentButton");
        engine.Children.Add(test);
        engine.Children.Add(result);

        var help = StudioUi.Card(root, "فرمان‌ها");
        foreach (var (fa, fin, desc) in new[]
        {
            ("شمسا باز کن کروم", "shamsa baz kon chrome", "اجرای برنامه"),
            ("شمسا جستجو", "shamsa jostojoo", "جستجوی سریع"),
            ("شمسا چیدمان ستونی / سطری / مارپیچی / تک پنجره", "shamsa chidman sotuni / satri / marpichi / tak panjere", "چیدمان پنجره‌ها"),
            ("شمسا حالت کار", "shamsa halate kar · shamsa halate yek", "چیدمان ذخیره‌شده"),
            ("شمسا قفل کن", "shamsa ghofl kon", "قفل صفحه"),
            ("شمسا مرکز کنترل", "shamsa markaze control", "مرکز کنترل"),
            ("شمسا عکس بگیر / ضبط کن", "shamsa screenshot / shamsa record", "اسکرین‌شات و ضبط"),
            ("شمسا کلیپ بورد", "shamsa clipboard", "تاریخچه کلیپ‌بورد"),
        })
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
