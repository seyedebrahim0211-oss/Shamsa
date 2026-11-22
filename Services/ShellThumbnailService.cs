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
