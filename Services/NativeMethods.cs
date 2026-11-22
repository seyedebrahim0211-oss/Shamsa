using System.Runtime.InteropServices;
using System.Text;

namespace Shamsa.Services;

internal enum ShellSurfaceKind { None, Explorer, Desktop }

/// <summary>Shared Win32 interop used across the app.</summary>
internal static class NativeMethods
{
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_TAB = 0x09;
    public const int VK_CONTROL = 0x11;
    public const int VK_ESCAPE = 0x1B;

    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_NCHITTEST = 0x0084;
    public static readonly IntPtr HTTRANSPARENT = new(-1);
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_ROUND = 2;
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic frosted glass (Win11 22H2+)

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
        public MARGINS(int all) { Left = Right = Top = Bottom = all; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string x, string y);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x100;
    public const uint SHGFI_SMALLICON = 0x1;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    public static string GetClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Returns the top-level window handle and its class name under the given screen point.</summary>
    public static (IntPtr Root, string ClassName) GetRootWindowAt(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT(x, y));
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        return (root, GetClassNameOf(root));
    }

    /// <summary>
    /// Reliably classifies whether the screen point is over File Explorer or the Windows Desktop
    /// (works across Windows 10, Windows 11 23H2, and Windows 11 24H2 Progman/WorkerW/SHELLDLL_DefView hierarchies).
    /// </summary>
    public static (ShellSurfaceKind Surface, IntPtr Root, IntPtr Child) GetShellSurfaceAt(int x, int y)
    {
        IntPtr child = WindowFromPoint(new POINT(x, y));
        if (child == IntPtr.Zero || IsOwnProcessWindow(child))
            return (ShellSurfaceKind.None, IntPtr.Zero, IntPtr.Zero);

        IntPtr root = GetAncestor(child, GA_ROOT);
        if (root == IntPtr.Zero || IsOwnProcessWindow(root))
            return (ShellSurfaceKind.None, IntPtr.Zero, IntPtr.Zero);

        string rootCls = GetClassNameOf(root);
        if (IsExplorerClass(rootCls))
            return (ShellSurfaceKind.Explorer, root, child);

        if (IsDesktopClass(rootCls))
            return (ShellSurfaceKind.Desktop, root, child);

        // Fallback: check child & parent for SHELLDLL_DefView (Desktop icon list host)
        string childCls = GetClassNameOf(child);
        if (childCls == "SHELLDLL_DefView")
            return (ShellSurfaceKind.Desktop, root, child);

        if (childCls == "SysListView32")
        {
            IntPtr parent = GetAncestor(child, GA_PARENT);
            if (GetClassNameOf(parent) == "SHELLDLL_DefView")
                return (ShellSurfaceKind.Desktop, root, child);
        }

        return (ShellSurfaceKind.None, root, child);
    }

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    public static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == CurrentProcessId;
    }

    public static bool IsCursorOverOwnProcess(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT(x, y));
        return IsOwnProcessWindow(hwnd);
    }

    public static void DisableChildWindowsInput(IntPtr parentHwnd)
    {
        if (parentHwnd == IntPtr.Zero) return;
        try
        {
            EnumChildWindows(parentHwnd, (child, _) =>
            {
                EnableWindow(child, false);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    public static bool IsExplorerClass(string cls) => cls == "CabinetWClass" || cls == "ExploreWClass";
    public static bool IsDesktopClass(string cls) => cls == "Progman" || cls == "WorkerW";

    public static void MakeClickThroughToolWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        const int flags = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
        if (Environment.Is64BitProcess)
        {
            long style = GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style | flags));
        }
        else
        {
            int style = GetWindowLong32(hwnd, GWL_EXSTYLE);
            SetWindowLong32(hwnd, GWL_EXSTYLE, style | flags);
        }
    }

    public static void MakeToolWindowNoActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (Environment.Is64BitProcess)
        {
            long style = GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        }
        else
        {
            int style = GetWindowLong32(hwnd, GWL_EXSTYLE);
            SetWindowLong32(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }
    }

    public static void TryRoundCorners(IntPtr hwnd)
    {
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Enables real OS-level frosted glass (Windows 11 Acrylic / Windows 10 AcrylicBlurBehind)
    /// while keeping AllowsTransparency="False" so LibVLC HwndHost renders at full speed with zero airspace glitches.
    /// </summary>
    public static bool TryEnableGlassBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            // Soft luminous glass-turquoise/silver DWM frame edge (COLORREF: 0x00BBGGRR)
            int borderColor = 0x00A8985A;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            var margins = new MARGINS(-1);
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // Windows 11 22H2+ native Acrylic backdrop
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                return true;

            // Windows 10 / Win11 21H2 fallback: SetWindowCompositionAttribute Acrylic BlurBehind
            var accent = new AccentPolicy
            {
                AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                GradientColor = 0x9924130A // AABBGGRR translucent midnight lapis glass
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19, // WCA_ACCENT_POLICY
                    SizeOfData = size,
                    Data = ptr
                };
                if (SetWindowCompositionAttribute(hwnd, ref data) != 0)
                    return true;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }

        return false;
    }

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid FolderIdPublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");

    private static string? GetKnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr ptr) == 0)
            {
                string? path = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { }
        return null;
    }

    public static string GetDownloadsFolder() =>
        GetKnownFolder(FolderIdDownloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Returns all active Desktop folders (User Desktop including OneDrive + Public Desktop).</summary>
    public static List<string> GetDesktopFolders()
    {
        var list = new List<string>(3);
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                list.Add(p);
        }
        Add(GetKnownFolder(FolderIdDesktop));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(GetKnownFolder(FolderIdPublicDesktop));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        return list;
    }
}
