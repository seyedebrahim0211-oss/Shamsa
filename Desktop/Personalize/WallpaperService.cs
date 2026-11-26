using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

public enum WallpaperFit { Fill, Fit, Stretch, Center, Tile, Span }

/// <summary>Desktop wallpaper (per-user SPI + HKCU\Control Panel\Desktop), solid color, slideshow, Bing and lock screen.</summary>
public static class WallpaperService
{
    public const string Group = "wallpaper";
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string ColorsKey = @"Control Panel\Colors";

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".jfif", ".webp" };

    public static string FitName(WallpaperFit f) => f switch
    {
        WallpaperFit.Fill => "پر کردن",
        WallpaperFit.Fit => "جا دادن",
        WallpaperFit.Stretch => "کشیدن",
        WallpaperFit.Center => "وسط",
        WallpaperFit.Tile => "کاشی‌کاری",
        WallpaperFit.Span => "گسترده روی همه نمایشگرها",
        _ => f.ToString()
    };

    public static (string Style, string Tile) StyleValues(WallpaperFit f) => f switch
    {
        WallpaperFit.Fill => ("10", "0"),
        WallpaperFit.Fit => ("6", "0"),
        WallpaperFit.Stretch => ("2", "0"),
        WallpaperFit.Center => ("0", "0"),
        WallpaperFit.Tile => ("0", "1"),
        WallpaperFit.Span => ("22", "0"),
        _ => ("10", "0")
    };

    public static WallpaperFit CurrentFit()
    {
        string style = PersonalizeStore.Read(DesktopKey, "WallpaperStyle") as string ?? "10";
        string tile = PersonalizeStore.Read(DesktopKey, "TileWallpaper") as string ?? "0";
        if (tile == "1") return WallpaperFit.Tile;
        return style switch { "6" => WallpaperFit.Fit, "2" => WallpaperFit.Stretch, "0" => WallpaperFit.Center, "22" => WallpaperFit.Span, _ => WallpaperFit.Fill };
    }

    public static string CurrentWallpaper() => PersonalizeNative.GetCurrentWallpaper();

    private static void RememberOriginal()
    {
        var d = PersonalizeStore.Data;
        if (d.OriginalWallpaper == null)
        {
            d.OriginalWallpaper = CurrentWallpaper();
            PersonalizeStore.Save();
        }
    }

    /// <summary>Sets the desktop wallpaper image and fit. Throws on failure (nothing is changed when the file is missing).</summary>
    public static void SetWallpaper(string path, WallpaperFit fit)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("تصویر پیدا نشد.", path);
        RememberOriginal();
        var (style, tile) = StyleValues(fit);
        PersonalizeStore.Write(Group, DesktopKey, "WallpaperStyle", style, RegistryValueKind.String);
        PersonalizeStore.Write(Group, DesktopKey, "TileWallpaper", tile, RegistryValueKind.String);
        if (!PersonalizeNative.SystemParametersInfo(PersonalizeNative.SPI_SETDESKWALLPAPER, 0, path,
                PersonalizeNative.SPIF_UPDATEINIFILE | PersonalizeNative.SPIF_SENDCHANGE))
            throw new InvalidOperationException("ویندوز تصویر زمینه را نپذیرفت (کد " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").");
        var a = PersonalizeStore.Data.Applied;
        a.WallpaperPath = path; a.WallpaperStyle = fit.ToString(); a.SolidColorHex = null;
        PersonalizeStore.Save();
    }

    /// <summary>Removes the image and shows a solid desktop color.</summary>
    public static void SetSolidColor(Color c)
    {
        RememberOriginal();
        PersonalizeStore.Write(Group, ColorsKey, "Background", $"{c.R} {c.G} {c.B}", RegistryValueKind.String);
        PersonalizeNative.SetSysColors(1, new[] { PersonalizeNative.COLOR_DESKTOP }, new[] { c.R | (c.G << 8) | (c.B << 16) });
        PersonalizeNative.SystemParametersInfo(PersonalizeNative.SPI_SETDESKWALLPAPER, 0, "",
            PersonalizeNative.SPIF_UPDATEINIFILE | PersonalizeNative.SPIF_SENDCHANGE);
        var a = PersonalizeStore.Data.Applied;
        a.SolidColorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; a.WallpaperPath = null;
        PersonalizeStore.Save();
    }

    /// <summary>Restores the wallpaper, fit and background color the user had before.</summary>
    public static void Reset()
    {
        var d = PersonalizeStore.Data;
        StopSlideshow();
        d.Slideshow.Enabled = false; d.Slideshow.BingDaily = false;
        PersonalizeStore.Restore(Group);
        string? orig = d.OriginalWallpaper;
        try
        {
            var bg = PersonalizeStore.Read(ColorsKey, "Background") as string;
            if (bg != null)
            {
                var p = bg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out int v) ? v : 0).ToArray();
                if (p.Length == 3) PersonalizeNative.SetSysColors(1, new[] { PersonalizeNative.COLOR_DESKTOP }, new[] { p[0] | (p[1] << 8) | (p[2] << 16) });
            }
            if (orig != null)
                PersonalizeNative.SystemParametersInfo(PersonalizeNative.SPI_SETDESKWALLPAPER, 0, orig,
                    PersonalizeNative.SPIF_UPDATEINIFILE | PersonalizeNative.SPIF_SENDCHANGE);
        }
        catch (Exception ex) { AppLog.Warn("Wallpaper reset: " + ex.Message); }
        d.OriginalWallpaper = null;
        d.Applied.WallpaperPath = null; d.Applied.SolidColorHex = null;
        PersonalizeStore.Save();
    }

    // ------------------------------------------------------------------ slideshow

    private static System.Windows.Threading.DispatcherTimer? _timer;
    private static readonly Random Rng = new();
    private static int _slideIndex;

    public static bool SlideshowRunning => _timer?.IsEnabled == true;

    public static List<string> ImagesIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new();
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Starts (or restarts) the slideshow / daily Bing timer from the saved settings. Must run on the UI thread.</summary>
    public static void StartSlideshow()
    {
        var ss = PersonalizeStore.Data.Slideshow;
        StopSlideshow();
        if (!ss.Enabled && !ss.BingDaily) return;
        var interval = ss.Enabled ? TimeSpan.FromMinutes(Math.Clamp(ss.IntervalMinutes, 1, 24 * 60)) : TimeSpan.FromHours(1);
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = interval };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        _ = TickAsync(initial: true);
    }

    public static void StopSlideshow()
    {
        if (_timer != null) { _timer.Stop(); _timer = null; }
    }

    private static async Task TickAsync(bool initial = false)
    {
        var ss = PersonalizeStore.Data.Slideshow;
        try
        {
            if (ss.BingDaily)
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                if (ss.LastBingDate != today)
                {
                    string file = await DownloadBingAsync();
                    SetWallpaper(file, Enum.TryParse<WallpaperFit>(PersonalizeStore.Data.Applied.WallpaperStyle, out var f) ? f : WallpaperFit.Fill);
                    ss.LastBingDate = today;
                    PersonalizeStore.Save();
                }
                return;
            }
            if (ss.Enabled && !initial) NextSlide();
        }
        catch (Exception ex) { AppLog.Warn("Wallpaper slideshow tick failed: " + ex.Message); }
    }

    public static void NextSlide()
    {
        var ss = PersonalizeStore.Data.Slideshow;
        var images = ImagesIn(ss.Folder);
        if (images.Count == 0) return;
        string next = ss.Shuffle ? images[Rng.Next(images.Count)] : images[_slideIndex++ % images.Count];
        if (ss.Shuffle && images.Count > 1 && next == PersonalizeStore.Data.Applied.WallpaperPath)
            next = images[(images.IndexOf(next) + 1) % images.Count];
        SetWallpaper(next, Enum.TryParse<WallpaperFit>(PersonalizeStore.Data.Applied.WallpaperStyle, out var f) ? f : WallpaperFit.Fill);
    }

    // ------------------------------------------------------------------ Bing image of the day (opt-in)

    public const string BingApi = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1";

    /// <summary>Downloads today's Bing image to %LocalAppData%\Shamsa\wallpapers (contacts bing.com). Returns the file path.</summary>
    public static async Task<string> DownloadBingAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Negarkhaneh/1.0");
        string json = await http.GetStringAsync(BingApi);
        using var doc = JsonDocument.Parse(json);
        var img = doc.RootElement.GetProperty("images")[0];
        string rel = img.GetProperty("url").GetString() ?? throw new InvalidDataException("پاسخ بینگ نامعتبر است.");
        string date = img.TryGetProperty("startdate", out var sd) ? sd.GetString() ?? DateTime.Now.ToString("yyyyMMdd") : DateTime.Now.ToString("yyyyMMdd");
        var uri = new Uri(new Uri("https://www.bing.com"), rel);
        if (!uri.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("آدرس تصویر بینگ نامعتبر است.");
        Directory.CreateDirectory(PersonalizeStore.WallpapersDir);
        string file = Path.Combine(PersonalizeStore.WallpapersDir, $"bing-{date}.jpg");
        if (!File.Exists(file))
        {
            byte[] bytes = await http.GetByteArrayAsync(uri);
            if (bytes.Length < 1024) throw new InvalidDataException("تصویر دریافتی خالی است.");
            await File.WriteAllBytesAsync(file + ".tmp", bytes);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        return file;
    }

    // ------------------------------------------------------------------ lock screen (WinRT, per-user)

    /// <summary>Sets the lock screen image through Windows.System.UserProfile.LockScreen. Returns an error message or null.</summary>
    public static async Task<string?> SetLockScreenAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return "تصویر پیدا نشد.";
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            PersonalizeStore.Data.Applied.LockScreenImage = path;
            PersonalizeStore.Save();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "ویندوز اجازه تغییر صفحه قفل را به این برنامه نداد (ممکن است با سیاست سازمان یا نسخه ویندوز محدود شده باشد). از تنظیمات ویندوز ← شخصی‌سازی ← صفحه قفل اقدام کنید.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Lock screen: " + ex.Message);
            return "تغییر صفحه قفل ممکن نشد: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ dominant color

    /// <summary>A vivid representative color of an image (downscaled, weighted by saturation). Null if unreadable.</summary>
    public static Color? DominantColor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 64;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return DominantColor(bi);
        }
        catch { return null; }
    }

    public static Color? DominantColor(BitmapSource src)
    {
        var fmt = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        fmt.CopyPixels(px, stride, 0);
        // 12 hue buckets weighted by saturation*value
        var sumR = new double[12]; var sumG = new double[12]; var sumB = new double[12]; var weight = new double[12];
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double sat = max == 0 ? 0 : (max - min) / max;
            double wgt = sat * sat * max + 0.002;
            double hue;
            if (max == min) hue = 0;
            else if (max == r) hue = 60 * (((g - b) / (max - min) + 6) % 6);
            else if (max == g) hue = 60 * ((b - r) / (max - min) + 2);
            else hue = 60 * ((r - g) / (max - min) + 4);
            int k = Math.Clamp((int)(hue / 30), 0, 11);
            sumR[k] += r * wgt; sumG[k] += g * wgt; sumB[k] += b * wgt; weight[k] += wgt;
        }
        int best = Array.IndexOf(weight, weight.Max());
        if (weight[best] <= 0) return null;
        var c = Color.FromRgb((byte)(sumR[best] / weight[best] * 255), (byte)(sumG[best] / weight[best] * 255), (byte)(sumB[best] / weight[best] * 255));
        return AccentService.Normalize(c);
    }
}
