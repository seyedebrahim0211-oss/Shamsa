using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Shamsa.FolderStudio;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

/// <summary>A Windows desktop system icon (This PC, Recycle Bin, …).</summary>
public sealed record SystemIconDef(string Id, string Name, string Clsid, string Glyph, bool CanHide = true)
{
    public bool IsRecycleBin => Id == "recycle";
}

/// <summary>A built-in generated icon theme.</summary>
public sealed record IconTheme(string Id, string Name, Color Top, Color Bottom, Color Glyph, Color? Border);

/// <summary>
/// Desktop system icons: show/hide (HideDesktopIcons\NewStartPanel), change icon (per-user DefaultIcon
/// override under HKCU), and generated styled icon themes written as .ico into %LocalAppData%\Shamsa\icons.
/// </summary>
public static class SystemIcons
{
    public const string GroupVisibility = "visibility";
    public const string GroupIcons = "icons";

    private const string HideKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string RecycleIconKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon";

    public static readonly IReadOnlyList<SystemIconDef> All = new[]
    {
        new SystemIconDef("thispc",  "این رایانه",       "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", ""),
        new SystemIconDef("recycle", "سطل بازیافت",      "{645FF040-5081-101B-9F08-00AA002F954E}", ""),
        new SystemIconDef("network", "شبکه",             "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", ""),
        new SystemIconDef("user",    "پوشه کاربر",        "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", ""),
        new SystemIconDef("control", "کنترل پنل",         "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", ""),
    };

    public static readonly IReadOnlyList<IconTheme> Themes = new[]
    {
        new IconTheme("turquoise", "کاشی فیروزه‌ای", Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0F, 0x76, 0x6E), Colors.White, Color.FromRgb(0xE8, 0xC5, 0x6B)),
        new IconTheme("minimal",   "مینیمال",         Color.FromRgb(0xF8, 0xFA, 0xFC), Color.FromRgb(0xE2, 0xE8, 0xF0), Color.FromRgb(0x1F, 0x29, 0x37), Color.FromRgb(0xCB, 0xD5, 0xE1)),
        new IconTheme("gold",      "طلایی",           Color.FromRgb(0xF6, 0xD3, 0x79), Color.FromRgb(0xB8, 0x86, 0x0B), Color.FromRgb(0x0B, 0x13, 0x24), Color.FromRgb(0x7A, 0x5A, 0x08)),
        new IconTheme("lapis",     "لاجوردی",         Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x1E, 0x3A, 0x8A), Colors.White, Color.FromRgb(0xE8, 0xC5, 0x6B)),
        new IconTheme("emerald",   "زمرد",            Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x06, 0x5F, 0x46), Colors.White, Color.FromRgb(0xA7, 0xF3, 0xD0)),
        new IconTheme("ruby",      "یاقوت",           Color.FromRgb(0xFB, 0x71, 0x85), Color.FromRgb(0x9F, 0x12, 0x39), Colors.White, Color.FromRgb(0xFE, 0xCD, 0xD3)),
        new IconTheme("amethyst",  "یاس بنفش",        Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0x58, 0x1C, 0x87), Colors.White, Color.FromRgb(0xE9, 0xD5, 0xFF)),
        new IconTheme("midnight",  "نئون شب",         Color.FromRgb(0x1E, 0x29, 0x3B), Color.FromRgb(0x02, 0x06, 0x17), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x22, 0xD3, 0xEE)),
        new IconTheme("sunset",    "غروب کویر",       Color.FromRgb(0xFD, 0xBA, 0x74), Color.FromRgb(0xC2, 0x41, 0x0C), Colors.White, Color.FromRgb(0xFE, 0xD7, 0xAA)),
        new IconTheme("graphite",  "گرافیت",          Color.FromRgb(0x52, 0x52, 0x5B), Color.FromRgb(0x18, 0x18, 0x1B), Color.FromRgb(0xF4, 0xF4, 0xF5), Color.FromRgb(0x71, 0x71, 0x7A)),
        new IconTheme("sakura",    "شکوفه",           Color.FromRgb(0xFB, 0xCF, 0xE8), Color.FromRgb(0xEC, 0x48, 0x99), Colors.White, Color.FromRgb(0xFD, 0xF2, 0xF8)),
        new IconTheme("ocean",     "اقیانوس",         Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x0E, 0x74, 0x90), Colors.White, Color.FromRgb(0xCF, 0xFA, 0xFE)),
        new IconTheme("lime",      "سبز بازی",        Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0x14, 0x19, 0x1E), Color.FromRgb(0x14, 0x19, 0x1E), Color.FromRgb(0xA3, 0xE6, 0x35)),
    };

    public static SystemIconDef Get(string id) => All.First(d => d.Id == id);

    // ------------------------------------------------------------------ visibility

    /// <summary>True when the icon is shown on the desktop (NewStartPanel value 0 or, for most icons, missing).</summary>
    public static bool IsVisible(SystemIconDef d)
    {
        object? v = PersonalizeStore.Read(HideKey, d.Clsid);
        if (v is int i) return i == 0;
        // Windows defaults when the value is absent: only Recycle Bin is shown on a fresh profile.
        return d.IsRecycleBin;
    }

    public static void SetVisible(SystemIconDef d, bool visible)
    {
        PersonalizeStore.Write(GroupVisibility, HideKey, d.Clsid, visible ? 0 : 1, RegistryValueKind.DWord);
        PersonalizeNative.RefreshShellIcons();
    }

    // ------------------------------------------------------------------ icon override

    private static string IconKey(SystemIconDef d) =>
        d.IsRecycleBin ? RecycleIconKey : $@"Software\Classes\CLSID\{d.Clsid}\DefaultIcon";

    /// <summary>The icon location currently in effect ("path,index"), per-user override first, then HKCR.</summary>
    public static string? CurrentIconLocation(SystemIconDef d, bool full = false)
    {
        string name = d.IsRecycleBin ? (full ? "full" : "empty") : "";
        string? v = PersonalizeStore.Read(IconKey(d), name) as string;
        if (!string.IsNullOrWhiteSpace(v)) return v;
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{d.Clsid}\DefaultIcon");
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                   ?? k?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch { return null; }
    }

    /// <summary>Sets the icon (for the Recycle Bin, <paramref name="fullLocation"/> is the "full" state).</summary>
    public static void SetIcon(SystemIconDef d, string location, string? fullLocation = null)
    {
        string key = IconKey(d);
        if (d.IsRecycleBin)
        {
            string full = fullLocation ?? location;
            bool isFull = IsRecycleBinFull();
            PersonalizeStore.Write(GroupIcons, key, "empty", location, RegistryValueKind.ExpandString);
            PersonalizeStore.Write(GroupIcons, key, "full", full, RegistryValueKind.ExpandString);
            PersonalizeStore.Write(GroupIcons, key, "", isFull ? full : location, RegistryValueKind.ExpandString);
        }
        else
        {
            PersonalizeStore.Write(GroupIcons, key, "", location, RegistryValueKind.ExpandString);
        }
        PersonalizeStore.Data.Applied.IconFiles[d.Id] = location;
        PersonalizeStore.Save();
        PersonalizeNative.RefreshShellIcons();
    }

    /// <summary>Restores one icon's original DefaultIcon values.</summary>
    public static void ResetIcon(SystemIconDef d)
    {
        string key = IconKey(d);
        var originals = PersonalizeStore.Data.Originals
            .Where(o => o.Group == GroupIcons && o.SubKey.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var o in originals)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(o.SubKey, writable: true);
                if (!o.Existed) k?.DeleteValue(o.Name, false);
                else k?.SetValue(o.Name, o.Text ?? "", Enum.TryParse<RegistryValueKind>(o.Kind, out var kind) ? kind : RegistryValueKind.ExpandString);
                PersonalizeStore.Data.Originals.Remove(o);
            }
            catch (Exception ex) { AppLog.Warn("Reset icon failed: " + ex.Message); }
        }
        PersonalizeStore.Data.Applied.IconFiles.Remove(d.Id);
        PersonalizeStore.Save();
        PersonalizeNative.RefreshShellIcons();
    }

    private static bool IsRecycleBinFull()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    // ------------------------------------------------------------------ generated themes

    /// <summary>Renders the styled tile for one icon (STA thread). <paramref name="full"/> adds a badge for a non-empty bin.</summary>
    public static BitmapSource RenderTile(SystemIconDef d, IconTheme t, int size, bool full = false)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size, pad = s * 0.06, r = s * 0.22;
            var rect = new Rect(pad, pad, s - 2 * pad, s - 2 * pad);
            var bg = new LinearGradientBrush(t.Top, t.Bottom, 90);
            Pen? pen = t.Border is { } b ? new Pen(new SolidColorBrush(b), Math.Max(1, s * 0.035)) : null;
            // soft shadow
            if (size >= 32)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null,
                    new Rect(rect.X, rect.Y + s * 0.025, rect.Width, rect.Height), r, r);
            dc.DrawRoundedRectangle(bg, pen, rect, r, r);
            // glossy highlight
            var gloss = new LinearGradientBrush(Color.FromArgb(0x55, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawRoundedRectangle(gloss, null, new Rect(rect.X, rect.Y, rect.Width, rect.Height * 0.5), r, r);

            var ft = new FormattedText(d.Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(GlyphFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                s * 0.52, new SolidColorBrush(t.Glyph), 1.0);
            dc.DrawText(ft, new Point((s - ft.Width) / 2, (s - ft.Height) / 2));

            if (full)
            {
                double br = s * 0.13;
                var c = new Point(s - pad - br * 0.9, pad + br * 0.9);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), new Pen(Brushes.White, Math.Max(1, s * 0.03)), c, br, br);
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private static FontFamily GlyphFont
    {
        get
        {
            foreach (var f in Fonts.SystemFontFamilies)
                if (f.Source == "Segoe Fluent Icons") return f;
            return new FontFamily("Segoe MDL2 Assets");
        }
    }

    /// <summary>Writes a multi-size .ico for the tile. Returns the file path.</summary>
    public static string WriteThemeIco(SystemIconDef d, IconTheme t, string directory, bool full = false)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{t.Id}-{d.Id}{(full ? "-full" : "")}.ico");
        var sizes = FolderIconRenderer.IcoSizes;
        var frames = new List<byte[]>();
        foreach (int sz in sizes)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(RenderTile(d, t, sz, full)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            frames.Add(ms.ToArray());
        }
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) FolderIconRenderer.WriteIco(fs, sizes, frames);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Applies a theme to every system icon. All .ico files are generated first so a failure never
    /// leaves a half-applied theme.
    /// </summary>
    public static void ApplyTheme(IconTheme t)
    {
        var files = new Dictionary<string, (string Empty, string? Full)>();
        foreach (var d in All)
            files[d.Id] = (WriteThemeIco(d, t, PersonalizeStore.IconsDir),
                           d.IsRecycleBin ? WriteThemeIco(d, t, PersonalizeStore.IconsDir, full: true) : null);
        foreach (var d in All)
        {
            var (e, f) = files[d.Id];
            SetIcon(d, e + ",0", f != null ? f + ",0" : null);
        }
        PersonalizeStore.Data.Applied.IconTheme = t.Id;
        PersonalizeStore.Save();
    }

    // ------------------------------------------------------------------ previews

    /// <summary>Loads an icon location ("path,index") as a bitmap for previews (null when unavailable).</summary>
    public static BitmapSource? LoadIcon(string? location, int size = 48)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var (file, index) = ParseLocation(location);
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        return ExtractIcon(file, index, size);
    }

    public static (string File, int Index) ParseLocation(string location)
    {
        string s = location.Trim().Trim('"');
        int index = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim(), out int i)) { index = i; s = s[..comma].Trim().Trim('"'); }
        return (Environment.ExpandEnvironmentVariables(s), index);
    }

    /// <summary>Extracts one icon (index ≥ 0 = position, &lt; 0 = resource id) at the given size.</summary>
    public static BitmapSource? ExtractIcon(string file, int index, int size)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            uint n = PersonalizeNative.PrivateExtractIcons(file, index, size, size, handles, ids, 1, 0);
            if (n == 0 || n == 0xFFFFFFFF || handles[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (handles[0] != IntPtr.Zero) PersonalizeNative.DestroyIcon(handles[0]); }
    }

    /// <summary>Number of icons in a .ico/.exe/.dll.</summary>
    public static int CountIcons(string file)
    {
        try { return (int)PersonalizeNative.ExtractIconEx(file, -1, null, null, 0); } catch { return 0; }
    }
}
