using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Shamsa.Services;

/// <summary>Converts physical cursor pixels to WPF DIPs and finds the work area of the monitor under a point.</summary>
public static class ScreenHelper
{
    public static DpiScale GetDpi(Visual? visual)
    {
        try { return visual != null ? VisualTreeHelper.GetDpi(visual) : new DpiScale(1, 1); }
        catch { return new DpiScale(1, 1); }
    }

    public static Point PixelsToDip(Visual? visual, Point pixels)
    {
        var dpi = GetDpi(visual);
        return new Point(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY);
    }

    public static Rect WorkAreaDipAt(Visual? visual, Point pixels)
    {
        var dpi = GetDpi(visual);
        var wa = Forms.Screen.FromPoint(new System.Drawing.Point((int)pixels.X, (int)pixels.Y)).WorkingArea;
        return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    /// <summary>Clamp a window rectangle inside a work area.</summary>
    public static Point Clamp(Point topLeft, Size size, Rect area) => new(
        Math.Max(area.Left, Math.Min(topLeft.X, area.Right - size.Width)),
        Math.Max(area.Top, Math.Min(topLeft.Y, area.Bottom - size.Height)));
}

/// <summary>Shell icons for files and folders, cached per extension.</summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path)
    {
        bool isDir = Directory.Exists(path);
        string ext = Path.GetExtension(path);
        // .exe/.ico/.lnk carry their own icon; everything else shares one per extension
        bool unique = !isDir && ext.ToLowerInvariant() is ".exe" or ".ico" or ".lnk" or ".url";
        string key = isDir ? "<dir>" : unique ? path : (string.IsNullOrEmpty(ext) ? "<none>" : ext);
        return Cache.GetOrAdd(key, _ => Load(path, isDir, unique));
    }

    private static ImageSource? Load(string path, bool isDir, bool unique)
    {
        var info = new NativeMethods.SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        uint attrs = 0;
        if (!unique)
        {
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES; // don't touch the disk
            attrs = isDir ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        }

        try
        {
            IntPtr res = NativeMethods.SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), flags);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>App-wide notifications (tray balloons). A click on the balloon can reveal a file.</summary>
public static class Notifier
{
    public static event Action<string, string?>? Requested;

    public static void Show(string message, string? revealPath = null)
    {
        AppLog.Info("Notify: " + message);
        Requested?.Invoke(message, revealPath);
    }
}

public static class ShellActions
{
    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(Path.GetDirectoryName(path)))
                OpenPath(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reveal failed for {path}: {ex.Message}");
        }
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Open failed for {path}: {ex.Message}");
        }
    }

    /// <summary>Clipboard can be momentarily locked by other apps; retry a few times.</summary>
    public static bool TrySetClipboardText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }

    public static string? PickFolder(string? initial = null, Window? owner = null)
    {
        using var dialog = new Forms.FolderBrowserDialog { UseDescriptionForTitle = true, Description = "انتخاب پوشه" };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;

        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var result = owner != null
            ? dialog.ShowDialog(new Win32Owner(new WindowInteropHelper(owner).Handle))
            : dialog.ShowDialog();
        return result == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private sealed class Win32Owner : Forms.IWin32Window
    {
        public Win32Owner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
}

/// <summary>Helpers for re-parenting parts of one window into another (used by the «نگارخانه» hub).</summary>
public static class UiHost
{
    /// <summary>Removes <paramref name="e"/> from whatever logical parent holds it.</summary>
    public static void Detach(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case System.Windows.Controls.Panel p: p.Children.Remove(e); break;
            case System.Windows.Controls.ContentControl c when ReferenceEquals(c.Content, e): c.Content = null; break;
            case System.Windows.Controls.Decorator d when ReferenceEquals(d.Child, e): d.Child = null; break;
            case System.Windows.Controls.ItemsControl i: i.Items.Remove(e); break;
            case Window w when ReferenceEquals(w.Content, e): w.Content = null; break;
        }
    }

    /// <summary>
    /// Takes a window's root content for hosting elsewhere and copies the window's own resources onto it,
    /// so styles and brushes defined in Window.Resources still resolve after the move.
    /// </summary>
    public static FrameworkElement? TakeContent(Window w)
    {
        if (w.Content is not FrameworkElement root) return null;
        w.Content = null;
        CopyResources(w.Resources, root);
        return root;
    }

    public static void CopyResources(ResourceDictionary from, FrameworkElement to)
    {
        foreach (var key in from.Keys)
            if (!to.Resources.Contains(key)) to.Resources[key] = from[key];
    }
}