using System.Windows;
using static Shamsa.Desktop.ControlCenter.StudioUi;

namespace Shamsa.Desktop.ControlCenter;

/// <summary>Desktop Studio page «مرکز کنترل».</summary>
public static class ControlCenterStudioPage
{
    public static UIElement Build(DesktopSettings s, Action changed)
    {
        var c = s.ControlCenter;
        var page = Page();
        var main = Body(Card(page, "مرکز کنترل", "از دکمه آیکون‌دار نوار بالا، منوی نوار بالا یا منوی سینی شمسا باز می‌شود."));
        main.Children.Add(Toggle("فعال", null, c.Enabled, v => c.Enabled = v, changed));
        main.Children.Add(Button("باز کردن مرکز کنترل", DesktopEvents.RequestControlCenter));

        var tiles = Body(Card(page, "کاشی‌ها و کنترل‌ها"));
        tiles.Children.Add(Toggle("وای‌فای", "روشن/خاموش کردن مستقیم رادیو", c.ShowWifi, v => c.ShowWifi = v, changed));
        tiles.Children.Add(Toggle("بلوتوث", null, c.ShowBluetooth, v => c.ShowBluetooth = v, changed));
        tiles.Children.Add(Toggle("روشنایی صفحه", "صفحه لپ‌تاپ یا نمایشگرهای دارای DDC/CI", c.ShowBrightness, v => c.ShowBrightness = v, changed));
        tiles.Children.Add(Toggle("نور شب", "ویندوز رابط عمومی ندارد؛ کاشی، صفحه تنظیمات نور شب را باز می‌کند", c.ShowNightLight, v => c.ShowNightLight = v, changed));
        tiles.Children.Add(Toggle("مزاحم نشوید / تمرکز", "صفحه تنظیمات اعلان‌ها و تمرکز ویندوز را باز می‌کند", c.ShowFocus, v => c.ShowFocus = v, changed));
        tiles.Children.Add(Toggle("صدا", null, c.ShowVolume, v => c.ShowVolume = v, changed));
        tiles.Children.Add(Toggle("پخش رسانه", null, c.ShowMedia, v => c.ShowMedia = v, changed));
        tiles.Children.Add(Toggle("تم روشن/تیره", null, c.ShowAutoTheme, v => c.ShowAutoTheme = v, changed));
        return page;
    }
}
