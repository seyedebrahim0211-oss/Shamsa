using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Shamsa.Services;

/// <summary>
/// Extracts Windows-cached high-res video poster frames via IShellItemImageFactory in ~2-5 ms.
/// Displayed immediately during the entrance animation while LibVLC decodes frame 1, eliminating
/// any black flash or stale frame from the previous video.
/// </summary>
public static class ShellThumbnailService
{
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_THUMBNAILONLY = 0x00000008;

    private static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly ConcurrentDictionary<string, (DateTime WriteUtc, BitmapSource Bitmap)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory? ppv);

    private const uint SIIGBF_ICONONLY = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private static readonly ConcurrentDictionary<string, (DateTime WriteUtc, BitmapSource Bitmap)> ShellCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shell thumbnail (or, unless <paramref name="thumbnailOnly"/>, the Explorer icon — including custom
    /// folder icons from desktop.ini) with correct per-pixel alpha. Must be called on an STA thread.
    /// </summary>
    public static BitmapSource? TryGetShellImage(string path, int size, bool thumbnailOnly = false, bool iconOnly = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        DateTime writeUtc = DateTime.MinValue;
        try { writeUtc = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); } catch { }
        string key = $"{path}|{size}|{thumbnailOnly}|{iconOnly}";
        if (ShellCache.TryGetValue(key, out var hit) && hit.WriteUtc == writeUtc) return hit.Bitmap;

        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            Guid iid = IidShellItemImageFactory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
            uint flags = SIIGBF_BIGGERSIZEOK | (thumbnailOnly ? SIIGBF_THUMBNAILONLY : 0) | (iconOnly ? SIIGBF_ICONONLY : 0);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hBitmap) != 0 || hBitmap == IntPtr.Zero) return null;
            var bmp = HBitmapToBitmapSource(hBitmap);
            if (bmp == null) return null;
            if (ShellCache.Count >= 400) ShellCache.Clear();
            ShellCache[key] = (writeUtc, bmp);
            return bmp;
        }
        catch { return null; }
        finally
        {
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (factory != null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32-bpp DIB keeping its alpha channel (CreateBitmapSourceFromHBitmap drops it → black icon backgrounds).</summary>
    private static BitmapSource? HBitmapToBitmapSource(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0) return null;
        int w = info.bmWidth, h = Math.Abs(info.bmHeight);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down rows
            biPlanes = 1,
            biBitCount = 32
        };
        var pixels = new byte[w * h * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(w, h, 96, 96, hasAlpha ? System.Windows.Media.PixelFormats.Pbgra32 : System.Windows.Media.PixelFormats.Bgra32,
            null, pixels, w * 4);
        source.Freeze();
        return source;
    }

    public static BitmapSource? TryGetVideoThumbnail(string path, int maxSide = 480)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        DateTime writeUtc = DateTime.MinValue;
        try { writeUtc = File.GetLastWriteTimeUtc(path); } catch { }

        if (Cache.TryGetValue(path, out var hit) && hit.WriteUtc == writeUtc)
            return hit.Bitmap;

        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            Guid iid = IidShellItemImageFactory;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (hr != 0 || factory == null) return null;

            hr = factory.GetImage(new SIZE { cx = maxSide, cy = maxSide }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();

            if (Cache.Count >= 48) Cache.Clear();
            Cache[path] = (writeUtc, bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (factory != null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory);
        }
    }
}
