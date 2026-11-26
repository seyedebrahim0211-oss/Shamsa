using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using static Shamsa.Desktop.ControlCenter.StudioUi;

namespace Shamsa.Desktop.Plugins;

/// <summary>Desktop Studio page «افزونه‌های نوار بالا».</summary>
public static class PluginsStudioPage
{
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var p = s.Plugins;
        var page = Page();
        var main = Body(Card(page, "افزونه‌های نوار بالا",
            "هر فایل JSON در پوشه افزونه‌ها یک جزء تازه به نوار بالا اضافه می‌کند: متن ثابت، خروجی یک فرمان، یک فایل یا یک نشانی وب. راهنما و نمونه در همان پوشه است."));
        main.Children.Add(Toggle("افزونه‌ها فعال باشند", "فقط افزونه‌هایی را اضافه کنید که منبع آن‌ها را می‌شناسید؛ افزونه فرمانی، برنامه اجرا می‌کند.", p.Enabled, v => p.Enabled = v, changed));
        var buttons = new WrapPanel();
        buttons.Children.Add(Button("باز کردن پوشه افزونه‌ها", () =>
        {
            TopBarPluginHost.EnsureSamples();
            Directory.CreateDirectory(TopBarPluginHost.PluginsDir);
            Process.Start(new ProcessStartInfo(TopBarPluginHost.PluginsDir) { UseShellExecute = true });
        }));
        buttons.Children.Add(Button("بارگذاری دوباره", changed));
        main.Children.Add(buttons);

        var list = Body(Card(page, "افزونه‌های پیدا‌شده"));
        var defs = TopBarPluginHost.LoadAll();
        if (defs.Count == 0)
            list.Children.Add(Muted("هنوز افزونه‌ای نیست. با فعال بودن افزونه‌ها، دو نمونه (مدت روشن بودن و IP محلی) ساخته می‌شود."));
        foreach (var d in defs)
        {
            if (d.LoadError != null)
            {
                var err = Muted($"⚠ {d.FileName}: فایل خوانده نشد — {d.LoadError}");
                err.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
                list.Children.Add(err);
                continue;
            }
            string id = d.Id;
            string where = d.Position switch { "left" => "چپ", "center" => "وسط", _ => "راست" };
            string kind = d.Source == null ? "متن ثابت" : (d.Source.Type ?? "").ToLowerInvariant() switch
            {
                var t when t.StartsWith("builtin") => "داخلی",
                "command" => "فرمان",
                "file" => "فایل",
                "http" => "وب",
                _ => d.Source.Type ?? "?"
            };
            list.Children.Add(Toggle($"{d.Icon} {d.Title}", $"{d.FileName} • منبع: {kind} • جای نمایش: {where}",
                !p.Disabled.Contains(id, StringComparer.OrdinalIgnoreCase),
                v =>
                {
                    p.Disabled.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
                    if (!v) p.Disabled.Add(id);
                }, changed));
        }

        var help = Body(Card(page, "نمونه فایل افزونه"));
        var sample = new TextBox
        {
            Text = "{\n  \"id\": \"weather-text\",\n  \"title\": \"دمای هوا\",\n  \"icon\": \"🌤\",\n  \"source\": { \"type\": \"http\", \"value\": \"https://wttr.in/Tehran?format=%t\", \"intervalSeconds\": 900 },\n  \"onClick\": { \"type\": \"open\", \"value\": \"https://wttr.in/Tehran\" },\n  \"position\": \"right\"\n}",
            IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Padding = new Thickness(8), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        help.Children.Add(sample);
        help.Children.Add(Muted("نوع منبع: builtin (uptime، localip، date)، command (حداکثر ۵ ثانیه، بدون پنجره)، file (خط اول فایل)، http (فقط GET، حداکثر ۵ ثانیه). کمترین فاصله به‌روزرسانی ۱۰ ثانیه است."));
        return page;
    }
}
