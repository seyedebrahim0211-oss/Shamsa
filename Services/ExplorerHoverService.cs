using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace Shamsa.Services;

public record HoverTarget(string FilePath, FileKind Kind, Point CursorPos, Rect ItemBounds);

/// <summary>
/// Resolves which file is under the mouse in File Explorer or on the Windows Desktop.
/// Handles Windows 11 Explorer tabs, Windows 10/11 Desktop SysListView32 UIA quirks,
/// OneDrive-redirected Desktops, hidden file extensions, and Persian/Bidi filenames.
/// </summary>
public class ExplorerHoverService
{
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);
    }

    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    private readonly object _cacheGate = new();
    private (IntPtr Hwnd, IntPtr Tab, string Path, DateTime At) _folderCache;

    public bool IncludeImages { get; set; } = true;

    public HoverTarget? GetHoveredFile()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return null;
        var cursorPos = new Point(pt.X, pt.Y);

        try
        {
            var (surface, root, child) = NativeMethods.GetShellSurfaceAt(pt.X, pt.Y);
            if (surface == ShellSurfaceKind.None) return null;

            List<string> folders;
            if (surface == ShellSurfaceKind.Explorer)
            {
                string? folder = GetExplorerFolderPath(root);
                if (folder == null) return null;
                folders = new List<string> { folder };
            }
            else
            {
                folders = NativeMethods.GetDesktopFolders();
                if (folders.Count == 0) return null;
            }

            var (itemName, itemBounds) = surface == ShellSurfaceKind.Desktop
                ? GetDesktopItemInfoAt(cursorPos, root, child)
                : GetItemInfoAt(cursorPos);

            if (string.IsNullOrWhiteSpace(itemName)) return null;

            foreach (var folder in folders)
            {
                string? path = ResolveFile(folder, itemName);
                if (path != null)
                    return new HoverTarget(path, FileKinds.Of(path), cursorPos, itemBounds);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // transient UI Automation / COM failures while the mouse moves — ignore
        }

        return null;
    }

    /// <summary>
    /// Resolves the currently selected file(s)/folder(s) in the active Explorer tab or Desktop,
    /// falling back to whichever file/folder is under the cursor. Used by the global Donut shortcut.
    /// </summary>
    public string[] GetTargetFilesForShortcut()
    {
        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            string fgCls = NativeMethods.GetClassNameOf(fg);

            if (NativeMethods.IsExplorerClass(fgCls))
            {
                var selected = GetExplorerSelectedPaths(fg);
                if (selected.Length > 0) return selected;
            }

            if (NativeMethods.IsDesktopClass(fgCls))
            {
                var desktopSelected = GetDesktopSelectedPaths(fg);
                if (desktopSelected.Length > 0) return desktopSelected;
            }

            var underCursor = GetAnyHoveredItemPath();
            if (underCursor != null) return new[] { underCursor };
        }
        catch (Exception ex)
        {
            AppLog.Warn("Shortcut target resolution failed: " + ex.Message);
        }

        return Array.Empty<string>();
    }

    private string? GetAnyHoveredItemPath()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return null;
        var cursorPos = new Point(pt.X, pt.Y);

        var (surface, root, child) = NativeMethods.GetShellSurfaceAt(pt.X, pt.Y);
        if (surface == ShellSurfaceKind.None) return null;

        List<string> folders;
        if (surface == ShellSurfaceKind.Explorer)
        {
            string? folder = GetExplorerFolderPath(root);
            if (string.IsNullOrEmpty(folder)) return null;
            folders = new List<string> { folder };
        }
        else
        {
            folders = NativeMethods.GetDesktopFolders();
        }

        var (itemName, _) = surface == ShellSurfaceKind.Desktop
            ? GetDesktopItemInfoAt(cursorPos, root, child)
            : GetItemInfoAt(cursorPos);

        if (string.IsNullOrWhiteSpace(itemName)) return null;

        foreach (var folder in folders)
        {
            string? resolved = ResolveAnyItem(folder, itemName);
            if (resolved != null) return resolved;
        }

        return null;
    }

    private static string CleanItemName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            // Strip invisible Unicode bidi/directionality marks injected by Windows Explorer/Desktop on RTL locales
            if (c is '\u200E' or '\u200F' or '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E'
                or '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\uFEFF')
                continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string? ResolveAnyItem(string folder, string itemName)
    {
        string clean = CleanItemName(itemName);
        if (string.IsNullOrEmpty(clean) || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        string direct = Path.Combine(folder, clean);
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), clean, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        catch { }

        return null;
    }

    private static string[] GetDesktopSelectedPaths(IntPtr desktopHwnd)
    {
        var results = new List<string>();
        try
        {
            IntPtr listHwnd = FindDesktopListView(desktopHwnd);
            var rootEl = AutomationElement.FromHandle(listHwnd != IntPtr.Zero ? listHwnd : desktopHwnd);
            if (rootEl == null) return Array.Empty<string>();

            var cond = new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true);
            var selected = rootEl.FindAll(TreeScope.Descendants, cond);
            if (selected == null || selected.Count == 0) return Array.Empty<string>();

            var desktopFolders = NativeMethods.GetDesktopFolders();
            for (int i = 0; i < selected.Count; i++)
            {
                string name = CleanItemName(selected[i]?.Current.Name);
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var dir in desktopFolders)
                {
                    string? found = ResolveAnyItem(dir, name);
                    if (found != null)
                    {
                        results.Add(found);
                        break;
                    }
                }
            }
        }
        catch { }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] GetExplorerSelectedPaths(IntPtr explorerHwnd)
    {
        IntPtr activeTab = NativeMethods.FindWindowEx(explorerHwnd, IntPtr.Zero, "ShellTabWindowClass", null);
        var paths = new List<string>();
        object? shell = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return Array.Empty<string>();
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return Array.Empty<string>();

            dynamic windows = ((dynamic)shell).Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = windows.Item(i);
                    if (window == null) continue;
                    dynamic w = window;
                    if (new IntPtr((long)w.HWND) != explorerHwnd) continue;

                    if (activeTab != IntPtr.Zero && GetTabHandle(window) is IntPtr tab && tab != IntPtr.Zero && tab != activeTab)
                        continue;

                    object? itemsObj = null;
                    try
                    {
                        itemsObj = w.Document.SelectedItems();
                        if (itemsObj == null) continue;
                        dynamic items = itemsObj;
                        int selCount = items.Count;
                        for (int j = 0; j < selCount; j++)
                        {
                            object? itemObj = null;
                            try
                            {
                                itemObj = items.Item(j);
                                string? p = itemObj != null ? (string)((dynamic)itemObj).Path : null;
                                if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                                    paths.Add(p);
                            }
                            catch { }
                            finally
                            {
                                if (itemObj != null && Marshal.IsComObject(itemObj)) Marshal.ReleaseComObject(itemObj);
                            }
                        }
                    }
                    finally
                    {
                        if (itemsObj != null && Marshal.IsComObject(itemsObj)) Marshal.ReleaseComObject(itemsObj);
                    }

                    break;
                }
                catch { }
                finally
                {
                    if (window != null && Marshal.IsComObject(window)) Marshal.ReleaseComObject(window);
                }
            }

            if (Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
        catch { }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsShellItemAt(int x, int y)
    {
        try
        {
            var (surface, root, child) = NativeMethods.GetShellSurfaceAt(x, y);
            if (surface == ShellSurfaceKind.None) return false;

            var pt = new Point(x, y);
            if (surface == ShellSurfaceKind.Desktop)
            {
                var (name, _) = GetDesktopItemInfoAt(pt, root, child);
                return !string.IsNullOrWhiteSpace(name);
            }

            var element = AutomationElement.FromPoint(pt);
            for (int depth = 0; element != null && depth < 3; depth++)
            {
                var type = element.Current.ControlType;
                if (type == ControlType.ListItem || type == ControlType.DataItem || type == ControlType.TreeItem)
                    return true;
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Locates the desktop SysListView32 handle reliably even when WorkerW wallpaper layers exist.
    /// </summary>
    private static IntPtr FindDesktopListView(IntPtr candidateRoot)
    {
        IntPtr CheckHost(IntPtr host)
        {
            if (host == IntPtr.Zero) return IntPtr.Zero;
            IntPtr defView = NativeMethods.FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                return NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            return IntPtr.Zero;
        }

        IntPtr lv = CheckHost(candidateRoot);
        if (lv != IntPtr.Zero) return lv;

        // Check Progman directly
        IntPtr progman = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
        lv = CheckHost(progman);
        if (lv != IntPtr.Zero) return lv;

        // Enumerate top-level WorkerW windows (Windows 10/11 desktop wallpaper host)
        IntPtr worker = IntPtr.Zero;
        for (int i = 0; i < 32; i++)
        {
            worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
            if (worker == IntPtr.Zero) break;
            lv = CheckHost(worker);
            if (lv != IntPtr.Zero) return lv;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Resolves the icon under the cursor on the Windows Desktop.
    /// On Windows 10/11 Desktop, UIA FromPoint frequently stops at the "Desktop" List container
    /// instead of the icon ListItem; we fall back to scanning SysListView32 children's BoundingRectangles.
    /// </summary>
    private static (string? Name, Rect Bounds) GetDesktopItemInfoAt(Point cursorPos, IntPtr rootHwnd, IntPtr childHwnd)
    {
        // Fast path: try FromPoint first
        var element = AutomationElement.FromPoint(cursorPos);
        if (element != null)
        {
            var probe = element;
            for (int depth = 0; probe != null && depth < 4; depth++)
            {
                if (probe.Current.ControlType == ControlType.ListItem)
                {
                    string n = CleanItemName(probe.Current.Name);
                    if (!string.IsNullOrEmpty(n))
                        return (n, SafeBounds(probe));
                }
                probe = TreeWalker.ControlViewWalker.GetParent(probe);
            }
        }

        // Reliable Desktop fallback: walk children of the Desktop SysListView32
        IntPtr listHwnd = NativeMethods.GetClassNameOf(childHwnd) == "SysListView32"
            ? childHwnd
            : FindDesktopListView(rootHwnd);

        AutomationElement? listEl = listHwnd != IntPtr.Zero
            ? AutomationElement.FromHandle(listHwnd)
            : (element?.Current.ControlType == ControlType.List ? element : null);

        if (listEl == null) return (null, Rect.Empty);

        var walker = TreeWalker.ControlViewWalker;
        var item = walker.GetFirstChild(listEl);
        int guard = 0;
        while (item != null && ++guard < 400)
        {
            try
            {
                var r = item.Current.BoundingRectangle;
                if (!r.IsEmpty && cursorPos.X >= r.Left && cursorPos.X <= r.Right
                    && cursorPos.Y >= r.Top && cursorPos.Y <= r.Bottom)
                {
                    string n = CleanItemName(item.Current.Name);
                    if (!string.IsNullOrEmpty(n))
                        return (n, SafeBounds(item));
                }
            }
            catch { }
            item = walker.GetNextSibling(item);
        }

        return (null, Rect.Empty);
    }

    private static (string? Name, Rect Bounds) GetItemInfoAt(Point cursorPos)
    {
        var element = AutomationElement.FromPoint(cursorPos);
        if (element == null) return (null, Rect.Empty);

        AutomationElement? listItem = null;
        var probe = element;
        for (int depth = 0; probe != null && depth < 4; depth++)
        {
            var ct = probe.Current.ControlType;
            if (ct == ControlType.ListItem || ct == ControlType.DataItem)
            {
                listItem = probe;
                break;
            }
            probe = TreeWalker.ControlViewWalker.GetParent(probe);
        }

        var target = listItem ?? element;
        string name = CleanItemName(target.Current.Name);
        return (string.IsNullOrEmpty(name) ? null : name, SafeBounds(target));
    }

    private static Rect SafeBounds(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            if (!r.IsEmpty && r.Width is > 8 and < 2400 && r.Height is > 8 and < 1200)
                return r;
        }
        catch { }
        return Rect.Empty;
    }

    private string? ResolveFile(string folder, string itemName)
    {
        string clean = CleanItemName(itemName);
        if (string.IsNullOrEmpty(clean) || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        string direct = Path.Combine(folder, clean);
        if (File.Exists(direct) && IsPreviewable(direct)) return direct;

        foreach (var ext in FileKinds.ExtensionsOf(FileKind.Video))
        {
            string candidate = direct + ext;
            if (File.Exists(candidate)) return candidate;
        }

        if (IncludeImages)
        {
            foreach (var ext in FileKinds.ExtensionsOf(FileKind.Image))
            {
                string candidate = direct + ext;
                if (File.Exists(candidate) && FileKinds.IsPreviewableImage(candidate)) return candidate;
            }
        }

        return null;
    }

    private bool IsPreviewable(string path) =>
        FileKinds.IsVideo(path) || (IncludeImages && FileKinds.IsPreviewableImage(path));

    private string? GetExplorerFolderPath(IntPtr explorerHwnd)
    {
        IntPtr activeTab = NativeMethods.FindWindowEx(explorerHwnd, IntPtr.Zero, "ShellTabWindowClass", null);

        lock (_cacheGate)
        {
            if (_folderCache.Hwnd == explorerHwnd && _folderCache.Tab == activeTab
                && DateTime.UtcNow - _folderCache.At < TimeSpan.FromSeconds(1.5))
                return _folderCache.Path;
        }

        string? result = null;
        object? shell = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            dynamic windows = ((dynamic)shell).Windows();
            int count = windows.Count;
            for (int i = 0; i < count && result == null; i++)
            {
                object? window = null;
                try
                {
                    window = windows.Item(i);
                    if (window == null) continue;
                    dynamic w = window;
                    if (new IntPtr((long)w.HWND) != explorerHwnd) continue;

                    if (activeTab != IntPtr.Zero && GetTabHandle(window) is IntPtr tab && tab != IntPtr.Zero && tab != activeTab)
                        continue;

                    string? path = null;
                    try { path = (string)w.Document.Folder.Self.Path; } catch { }
                    if (string.IsNullOrEmpty(path))
                    {
                        string url = w.LocationURL;
                        if (!string.IsNullOrEmpty(url) && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                            path = new Uri(url).LocalPath;
                    }

                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) result = path;
                }
                catch { }
                finally
                {
                    if (window != null && Marshal.IsComObject(window)) Marshal.ReleaseComObject(window);
                }
            }

            if (Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
        catch { }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }

        if (result != null)
        {
            lock (_cacheGate) _folderCache = (explorerHwnd, activeTab, result, DateTime.UtcNow);
        }
        return result;
    }

    private static IntPtr? GetTabHandle(object window)
    {
        if (window is not IServiceProvider sp) return null;
        Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
        if (sp.QueryService(ref sid, ref iid, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return null;
        try
        {
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(ptr);
            try
            {
                return browser.GetWindow(out IntPtr hwnd) == 0 ? hwnd : null;
            }
            finally
            {
                Marshal.ReleaseComObject(browser);
            }
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }
}
