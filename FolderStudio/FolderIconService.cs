using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.FolderStudio;

/// <summary>
/// Applies / clears custom folder icons through the documented shell API SHGetSetFolderCustomSettings
/// (it maintains desktop.ini and the folder's read-only/system attribute for us) and notifies Explorer.
/// File contents are never touched.
/// </summary>
public static class FolderIconService
{
    private const uint FCSM_ICONFILE = 0x00000010;
    private const uint FCS_FORCEWRITE = 0x00000002;
    private const uint FCS_READ = 0x00000001;

    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_FLUSH = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFOLDERCUSTOMSETTINGS
    {
        public uint dwSize;
        public uint dwMask;
        public IntPtr pvid;
        public IntPtr pszWebViewTemplate;
        public uint cchWebViewTemplate;
        public IntPtr pszWebViewTemplateVersion;
        public IntPtr pszInfoTip;
        public uint cchInfoTip;
        public IntPtr pclsid;
        public uint dwFlags;
        public IntPtr pszIconFile;
        public uint cchIconFile;
        public int iIconIndex;
        public IntPtr pszLogo;
        public uint cchLogo;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetSetFolderCustomSettings(ref SHFOLDERCUSTOMSETTINGS pfcs, string pszPath, uint dwReadWrite);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string? dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Returns null when the folder may be customised, otherwise a Persian reason.</summary>
    public static string? ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "مسیر پوشه خالی است.";
        if (folder.StartsWith(@"\\", StringComparison.Ordinal)) return "پوشه‌های شبکه (UNC) پشتیبانی نمی‌شوند.";
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\'); }
        catch { return "مسیر پوشه نامعتبر است."; }
        if (!Directory.Exists(full)) return "پوشه وجود ندارد.";
        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (root != null && root.Equals(full, StringComparison.OrdinalIgnoreCase)) return "ریشه درایو قابل تغییر نیست.";

        foreach (var special in ProtectedFolders())
        {
            if (string.IsNullOrEmpty(special)) continue;
            string sp = special.TrimEnd('\\');
            if (full.Equals(sp, StringComparison.OrdinalIgnoreCase)) return "این پوشه سیستمی یا ویژه ویندوز است و تغییر آن مجاز نیست.";
        }
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (!string.IsNullOrEmpty(windows) && full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
            return "پوشه‌های داخل Windows قابل تغییر نیستند.";
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (!string.IsNullOrEmpty(pf) && full.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return "پوشه‌های برنامه‌های نصب‌شده (Program Files) تغییر داده نمی‌شوند.";
        }
        return null;
    }

    private static IEnumerable<string> ProtectedFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetEnvironmentVariable("SystemDrive") + "\\Users";
    }

    /// <summary>Sets the icon (.ico path, index 0). Throws with a Persian message on failure.</summary>
    public static void ApplyIcon(string folder, string icoPath)
    {
        string? problem = ValidateFolder(folder);
        if (problem != null) throw new InvalidOperationException(problem);
        if (!File.Exists(icoPath)) throw new FileNotFoundException("فایل آیکون ساخته نشد.", icoPath);
        Write(folder, icoPath);
        Notify(folder);
    }

    /// <summary>Removes the custom icon entry. Other desktop.ini settings are preserved by the shell.</summary>
    public static void RestoreDefault(string folder)
    {
        string? problem = ValidateFolder(folder);
        if (problem != null) throw new InvalidOperationException(problem);
        Write(folder, null);
        Notify(folder);
    }

    private static void Write(string folder, string? icon)
    {
        IntPtr iconPtr = icon != null ? Marshal.StringToCoTaskMemUni(icon) : IntPtr.Zero;
        try
        {
            var s = new SHFOLDERCUSTOMSETTINGS
            {
                dwSize = (uint)Marshal.SizeOf<SHFOLDERCUSTOMSETTINGS>(),
                dwMask = FCSM_ICONFILE,
                pszIconFile = iconPtr,
                cchIconFile = 0,
                iIconIndex = 0
            };
            int hr = SHGetSetFolderCustomSettings(ref s, Path.GetFullPath(folder), FCS_FORCEWRITE);
            if (hr != 0)
                throw new InvalidOperationException($"ویندوز اجازه تغییر آیکون این پوشه را نداد (HRESULT 0x{hr:X8}). ممکن است دسترسی نوشتن نداشته باشید.");
        }
        finally
        {
            if (iconPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(iconPtr);
        }
    }

    /// <summary>Reads the current custom icon file (null when none).</summary>
    public static string? ReadIcon(string folder)
    {
        IntPtr buf = Marshal.AllocCoTaskMem(1040 * 2);
        try
        {
            var s = new SHFOLDERCUSTOMSETTINGS
            {
                dwSize = (uint)Marshal.SizeOf<SHFOLDERCUSTOMSETTINGS>(),
                dwMask = FCSM_ICONFILE,
                pszIconFile = buf,
                cchIconFile = 1040
            };
            if (SHGetSetFolderCustomSettings(ref s, Path.GetFullPath(folder), FCS_READ) != 0) return null;
            string? value = Marshal.PtrToStringUni(buf);
            return string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value);
        }
        catch { return null; }
        finally { Marshal.FreeCoTaskMem(buf); }
    }

    public static void Notify(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder);
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, full, IntPtr.Zero);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, full, IntPtr.Zero);
            string? parent = Path.GetDirectoryName(full);
            if (parent != null) SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, parent, IntPtr.Zero);
        }
        catch { }
    }

    public static void NotifyAssociationsChanged()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}

/// <summary>Per-user (HKCU) Explorer integration — no administrator rights, fully reversible.</summary>
public static class FolderShellIntegration
{
    private const string Classes = @"Software\Classes";
    private const string IconVerb = @"Directory\shell\Shamsa.FolderStudio";
    private const string LockVerb = @"Directory\shell\Shamsa.FolderLock";
    private const string ProgId = "Shamsa.LockedFolder";
    public const string VaultExtension = ".shamsalock";

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{IconVerb}\command");
            return key?.GetValue(null) is string cmd && ExePath != null && cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool on)
    {
        if (on) Enable(); else Disable();
        FolderIconService.NotifyAssociationsChanged();
    }

    private static void Enable()
    {
        string exe = ExePath ?? throw new InvalidOperationException("مسیر برنامه نگارخانه پیدا نشد.");
        string q = $"\"{exe}\"";
        using var root = Registry.CurrentUser.CreateSubKey(Classes, writable: true);

        using (var verb = root.CreateSubKey(IconVerb))
        {
            verb.SetValue("MUIVerb", "شخصی‌سازی آیکون پوشه با نگارخانه");
            verb.SetValue("Icon", $"{exe},0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue(null, $"{q} --folder-studio \"%1\"");
        }
        using (var verb = root.CreateSubKey(LockVerb))
        {
            verb.SetValue("MUIVerb", "قفل پوشه با رمز (نگارخانه)");
            verb.SetValue("Icon", $"{exe},0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue(null, $"{q} --folder-lock \"%1\"");
        }
        using (var ext = root.CreateSubKey(VaultExtension))
        {
            ext.SetValue(null, ProgId);
        }
        using (var prog = root.CreateSubKey(ProgId))
        {
            prog.SetValue(null, "پوشه قفل‌شده نگارخانه");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"{exe},0");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, $"{q} --unlock \"%1\"");
        }
    }

    private static void Disable()
    {
        using var root = Registry.CurrentUser.OpenSubKey(Classes, writable: true);
        if (root == null) return;
        root.DeleteSubKeyTree(IconVerb, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(LockVerb, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        // Only remove the extension mapping if it still points at our ProgID.
        using (var ext = root.OpenSubKey(VaultExtension))
        {
            if (ext?.GetValue(null) is string v && v != ProgId) return;
        }
        root.DeleteSubKeyTree(VaultExtension, throwOnMissingSubKey: false);
    }
}
