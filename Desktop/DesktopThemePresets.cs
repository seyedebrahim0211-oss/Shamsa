using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shamsa.Services;

namespace Shamsa.Desktop;

/// <summary>
/// Whole-desktop themes («تم‌های کامل»): one click sets the glass/accent of the top bar, dock, launcher and
/// widgets, the look of the icon groups (and optionally gathers every desktop file, folder and shortcut into
/// them on the chosen monitor), the matching Windows system icons (This PC, Recycle Bin, …) and the
/// Negarkhaneh window palette. Everything stays reversible from its own page.
/// </summary>
public static class DesktopThemePresets
{
    public sealed record Preset(
        string Id, string Name, string Description,
        string Accent, string Tint, int Radius, int Opacity, int DockIcon,
        string IconThemeId, string FenceIconStyle, string AppThemeId, string? Wallpaper = null)
    {
        public string? DisplayWallpaper => !Personalize.PersonalizeStudioPage.SafePreviewMode && Personalize.PersonalizeStore.Data.WallpaperEdits.TryGetValue(Id, out var path) && File.Exists(path) ? path : WallpaperPath;
        public string? WallpaperPath => Wallpaper == null ? null : Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpapers", Wallpaper);
    }

    public static readonly IReadOnlyList<Preset> All = new[]
    {
        new Preset("boshrouyeh", "بشرویه", "بافت تاریخی · بادگیر، خشت و گنبد فیروزه‌ای", "#60D3CB", "#342B28", 14, 85, 48, "turquoise", "girih", "PersianTurquoise", "boshrouyeh.jpg"),
        new Preset("mostofi", "بشرویه · خانهٔ مستوفی", "سه بادگیر و حیاط خشتی در نور غروب", "#F2BA82", "#382B29", 14, 85, 48, "enamel3d", "enamel", "PersianTurquoise", "mostofi.jpg"),
        new Preset("boshrouyeh-night", "بشرویه در شب", "دیوار شهربند و گنبدهای فیروزه‌ای در نور کهربایی", "#69D5D8", "#12283C", 13, 85, 48, "nocturne3d", "prismatic", "LapisGlass", "boshrouyeh-night.jpg"),
        new Preset("boshrouyeh-fort", "بشرویه · قلعه‌دختر", "صخره و بقایای قلعه در سپیده‌دم", "#E3A184", "#302A39", 9, 86, 46, "amber3d", "crystal", "AmethystGlass", "boshrouyeh-fort.jpg"),
        new Preset("oculus", "بشرویه · نورگیر", "هندسهٔ سقف و نور میانی", "#F1D6A4", "#282936", 16, 84, 48, "gilded3d", "girih", "PersianTurquoise", "oculus.jpg"),
        new Preset("museum", "بشرویه · حیاط موزه", "گچ سفید، سفال و چوب", "#E6D2B7", "#353434", 16, 84, 48, "pearl3d", "porcelain", "PersianTurquoise", "museum.jpg"),
        new Preset("courtyards", "بشرویه · دو حیاط", "طاق و بام‌های خشتی از بالا", "#DFA281", "#352E30", 16, 84, 48, "amber3d", "smoke", "PersianTurquoise", "courtyards.jpg"),
        new Preset("square", "بشرویه · میدان شهر", "یادمان سفید در میان فضای سبز", "#8CD8BC", "#19352F", 16, 84, 48, "jade3d", "porcelain", "PersianTurquoise", "square.jpg"),
        new Preset("domes", "بشرویه · گنبدهای فیروزه‌ای", "نمای روبه‌روی گنبدها در روشنایی عصر", "#66DAD1", "#153C47", 16, 84, 48, "turquoise", "lens", "PersianTurquoise", "domes.jpg"),
        new Preset("avenue", "بشرویه · بلوار درختان", "محور سبز شهر و طاق‌های ورودی", "#B5D496", "#293729", 16, 84, 48, "jade3d", "smoke", "PersianTurquoise", "avenue.jpg"),
        new Preset("dunes", "بشرویه · ریگزار", "خط تپه‌ها در روشنایی کویر", "#F2C391", "#372D32", 16, 84, 48, "amber3d", "porcelain", "PersianTurquoise", "dunes.jpg"),
        new Preset("assembly", "بشرویه · آیین بشرویه", "صف‌های عزاداران در حیاط و گنبدهای فیروزه‌ای", "#8BC8CF", "#172B38", 16, 84, 48, "nocturne3d", "girih", "PersianTurquoise", "assembly.jpg"),
        new Preset("cotton", "بشرویه · پنبه‌زار", "الیاف پنبه و برگ‌های نقره‌ای", "#CCD7A8", "#2B3932", 16, 84, 48, "pearl3d", "porcelain", "PersianTurquoise", "cotton.jpg"),
        new Preset("forest", "سپیدهٔ جنگل", "کوه، رود و ساعت درشت فارسی", "#8DDEDC", "#193847", 16, 84, 48, "pearl3d", "porcelain", "PersianTurquoise", "forest.jpg"),
        new Preset("mashhad", "مشهد · حرم امام رضا", "گنبد طلایی و صحن در ساعت آبی", "#F0C66C", "#202C44", 14, 86, 48, "gilded3d", "girih", "LapisGlass", "mashhad.jpg"),
        new Preset("tehran-azadi", "تهران · برج آزادی", "قوس‌های مرمر و نور غروب", "#ACDDE9", "#27384C", 13, 85, 46, "pearl3d", "lens", "LapisGlass", "tehran-azadi.jpg"),
        new Preset("isfahan", "اصفهان · پل خواجو", "طاق‌های روشن و انعکاس آجری روی زاینده‌رود", "#F0A46D", "#30273C", 14, 85, 48, "enamel3d", "enamel", "AmethystGlass", "isfahan.jpg"),
        new Preset("shiraz", "شیراز · حافظیه", "گنبد مسی و سروهای باغ در ساعت آبی", "#96CDB6", "#1B342E", 17, 84, 48, "jade3d", "enamel", "EmeraldGlass", "shiraz.jpg"),
        new Preset("heritage", "کاشی معرق", "فرهنگی · فیروزه، لاجورد و برنج", "#51D5BF", "#102C37", 14, 84, 48, "turquoise", "girih", "PersianTurquoise", "heritage.jpg"),
        new Preset("field", "خط تراز", "نظامی · نقش برجستهٔ زمین به رنگ زیتونی", "#C3CF91", "#252C1D", 8, 88, 44, "field", "outline", "EmeraldGlass", "field.jpg"),
        new Preset("atelier", "کارگاه رنگ", "هنری · رنگ‌دانه‌های سفالی و زعفرانی", "#F5A76E", "#342032", 18, 84, 48, "sunset", "crystal", "AmethystGlass", "atelier.jpg"),
        new Preset("arcade", "ایوان", "معماری · طاق آجری و سایه‌روشن حیاط", "#E6BD85", "#342921", 12, 86, 46, "gold", "girih", "PersianTurquoise", "arcade.jpg"),
        new Preset("city", "تهران در شب", "شهر · برج و پنجره‌های روشن", "#92B9FF", "#12243B", 12, 86, 46, "lapis", "crystal", "LapisGlass", "city.jpg"),
        new Preset("iran", "ایران؛ دماوند", "کشور · کوه و دامنه‌های یشمی در سپیده‌دم", "#8BDBC9", "#173735", 14, 84, 46, "emerald", "outline", "EmeraldGlass", "iran.jpg"),
        new Preset("prism", "منشور", "گیمینگ · بلور بنفش با لبه‌های نئونی", "#D29BFF", "#21152F", 10, 88, 44, "amethyst", "crystal", "AmethystGlass", "prism.jpg"),
        new Preset("shamse",   "شمسه",          "فیروزه و لاجورد · کاشی ایرانی",          "#28D5BD", "#10244C", 16, 72, 48, "turquoise", "plate", "PersianTurquoise"),
        new Preset("tazhib",   "تذهیب",          "طلایی و سبز یشمی",                       "#EBC26D", "#13392F", 12, 74, 46, "gold",      "plate", "EmeraldGlass"),
        new Preset("lajvard",  "لاجورد شب",      "آبی لاجوردی با حاشیهٔ طلایی",            "#60A5FA", "#0B1B3F", 14, 74, 48, "lapis",     "plate", "LapisGlass"),
        new Preset("zomorrod", "زمرد",           "سبز زمردی آرام",                         "#34D399", "#062A22", 14, 72, 48, "emerald",   "plate", "EmeraldGlass"),
        new Preset("yaghout",  "یاقوت",          "سرخ یاقوتی گرم",                         "#FB7185", "#2A0B14", 12, 76, 46, "ruby",      "plate", "AmethystGlass"),
        new Preset("yas",      "یاس بنفش",       "بنفش و سرمه‌ای با آیکون‌های درشت",        "#C8A4FF", "#211831", 18, 72, 56, "amethyst",  "plate", "AmethystGlass"),
        new Preset("neon",     "نئون شب",        "سیاه عمیق با خطوط فیروزه‌ای نئونی",       "#22D3EE", "#05070F", 6,  82, 44, "midnight",  "glyph", "LapisGlass"),
        new Preset("ghorub",   "غروب کویر",      "نارنجی و کهربایی گرم",                   "#FDBA74", "#2B1408", 14, 76, 48, "sunset",    "plate", "PersianTurquoise"),
        new Preset("sakura",   "شکوفه",          "صورتی ملایم و گرد",                       "#F9A8D4", "#2A1424", 20, 70, 50, "sakura",    "plate", "AmethystGlass"),
        new Preset("ocean",    "اقیانوس",        "فیروزه‌ای دریایی",                        "#22D3EE", "#062B33", 16, 72, 48, "ocean",     "plate", "EmeraldGlass"),
        new Preset("graphite", "گرافیت مینیمال", "خاکستری خنثی، بدون حاشیهٔ رنگی",          "#E4E4E7", "#18181B", 8,  80, 44, "graphite",  "plain", "LapisGlass"),
        new Preset("game",     "بازی",           "داک جمع‌وجور با تأکید سبز",               "#9FEA69", "#14191E", 4,  84, 36, "lime",      "glyph", "EmeraldGlass"),
    };

    /// <summary>Applies a preset to the studio's working copy. System icons / app palette are applied immediately.</summary>
    public static void Apply(DesktopSettings s, Preset p, bool gatherIcons, bool systemIcons, bool appPalette)
    {
        s.Theme.AccentHex = p.Accent;
        s.Theme.GlassTintHex = p.Tint;
        s.Theme.CornerRadius = p.Radius;
        s.Theme.GlassOpacity = p.Opacity;
        s.Theme.ShowBorders = p.Id != "graphite";
        s.Dock.IconSize = p.DockIcon;
        s.Dock.Enabled = true; s.Dock.Position = p.Id is "courtyards" or "avenue" ? "Left" : "Bottom";
        s.TopBar.Islands = p.Id is "forest" or "museum" or "courtyards" or "avenue" or "cotton";
        s.TopBar.Enabled = true; s.TopBar.Position = "Top";
        s.Launcher.Enabled = true;
        s.HideWindowsTaskbar = true;
        s.ThemePresetId = p.Id;
        s.AppIcons.WallpaperPresetId = null;
        s.AppIcons.WallpaperAccentHex = null;

        // Icon groups wear the theme too.
        s.Fences.FollowTheme = true;
        s.Fences.IconStyle = p.FenceIconStyle;
        if (gatherIcons)
        {
            bool created = false;
            foreach (var sug in Fences.FencesModule.SuggestedFences())
                if (!s.Fences.Fences.Any(f => f.Title == sug.Title || (f.Mode == "filter" && f.Extensions.Contains(Fences.FencesModule.AnyExtension) && sug.Extensions.Contains(Fences.FencesModule.AnyExtension))))
                { s.Fences.Fences.Add(sug); created = true; }
            s.Fences.Enabled = true;
            if (created) DesktopPlacement.Arrange(s, DesktopDisplay.PlacementArea(s), arrangeGroups: true);
            s.Fences.HideDesktopIcons = true;
        }
        // Widgets use the glass brush, so they follow automatically; reset widget colours that would clash.
        foreach (var w in s.Widgets.Items.Where(w => w.Kind != "note")) w.ColorHex = null;

        if (p.Id == "forest")
        {
            s.Widgets.Enabled = true;
            if (!s.Widgets.Items.Any(w => w.Kind == "poster"))
            {
                var slot = DesktopPlacement.ForNew(s, DesktopDisplay.PlacementArea(s), new Size(330, 200));
                s.Widgets.Items.Add(new WidgetPlacement { Kind = "poster", Width = slot.Width, Height = slot.Height, X = slot.X, Y = slot.Y });
            }
        }
        if (systemIcons && Personalize.SystemIcons.Themes.FirstOrDefault(t => t.Id == p.IconThemeId) is { } iconTheme)
        {
            try { Personalize.SystemIcons.ApplyTheme(iconTheme); }
            catch (Exception ex) { AppLog.Warn("Theme preset system icons: " + ex.Message); }
        }
        if (s.MatchWindowBorders)
            Personalize.PersonalizeStore.Transaction(() =>
            {
                Personalize.AccentService.SetAccent(DesktopTheme.ParseColor(p.Accent, Colors.Teal));
                Personalize.AccentService.SetAccentOnTitleBars(true);
            });
        if (appPalette)
        {
            try
            {
                SettingsService.Update(a => { a.ThemeId = p.AppThemeId; a.ThemeAccentHex = p.Accent; });
                ThemeService.Apply(SettingsService.Current);
            }
            catch (Exception ex) { AppLog.Warn("Theme preset palette: " + ex.Message); }
        }
    }

    /// <summary>A visual card: glass strip with mini dock tiles in the theme's colours.</summary>
    public static FrameworkElement Swatch(Preset p, bool current)
    {
        var accent = DesktopTheme.ParseColor(p.Accent, Colors.Teal);
        var tint = DesktopTheme.ParseColor(p.Tint, Colors.Black);
        var icon = Personalize.SystemIcons.Themes.FirstOrDefault(t => t.Id == p.IconThemeId);
        var canvas = new Border
        {
            Height = 74, CornerRadius = new CornerRadius(Math.Max(6, p.Radius * 0.7)),
            Background = new LinearGradientBrush(Lighten(tint, 0.18), tint, 90),
            BorderBrush = new SolidColorBrush(current ? accent : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(current ? 2 : 1)
        };
        if (p.DisplayWallpaper is { } path && File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 384;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                canvas.Height = 94;
                canvas.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                canvas.FlowDirection = FlowDirection.LeftToRight;
                return canvas;
            }
            catch (Exception ex) { AppLog.Warn("Wallpaper preview: " + ex.Message); }
        }
        var grid = new Grid();
        // top bar
        grid.Children.Add(new Border { Height = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 6, 6, 0), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0xA0, accent.R, accent.G, accent.B)) });
        // a fence
        grid.Children.Add(new Border { Width = 46, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 8, 0), CornerRadius = new CornerRadius(Math.Max(3, p.Radius * 0.35)),
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) });
        // dock tiles
        var dock = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 4; i++)
        {
            var top = icon?.Top ?? accent; var bottom = icon?.Bottom ?? tint;
            dock.Children.Add(new Border { Width = 14, Height = 14, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(Math.Max(2, p.Radius * 0.25)),
                Background = new LinearGradientBrush(top, bottom, 90),
                BorderBrush = new SolidColorBrush(icon?.Border ?? accent), BorderThickness = new Thickness(1) });
        }
        grid.Children.Add(dock);
        canvas.Child = grid;
        return canvas;
    }

    private static Color Lighten(Color c, double t) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
}
