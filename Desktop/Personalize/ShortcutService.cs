using System.Runtime.InteropServices;
using System.Text;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

public sealed record DesktopShortcutDef(string Id, string Name, string FileName, string Glyph);

/// <summary>Optional shortcuts on the user's desktop (plain .lnk/.url files — deleting them undoes everything).</summary>
public static class ShortcutService
{
    public static readonly IReadOnlyList<DesktopShortcutDef> All = new[]
    {
        new DesktopShortcutDef("settings", "تنظیمات ویندوز", "تنظیمات.url", ""),
        new DesktopShortcutDef("thispc", "این رایانه", "این رایانه.lnk", ""),
        new DesktopShortcutDef("control", "کنترل پنل", "کنترل پنل.lnk", ""),
        new DesktopShortcutDef("negar", "نگارخانه", "نگارخانه.lnk", ""),
    };

    public static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string PathOf(DesktopShortcutDef d) => Path.Combine(DesktopDir, d.FileName);
    public static bool Exists(DesktopShortcutDef d) => File.Exists(PathOf(d));

    public static void Create(DesktopShortcutDef d) => CreateIn(d, DesktopDir);

    /// <summary>Creates the shortcut in <paramref name="dir"/> (tests use a scratch folder).</summary>
    public static string CreateIn(DesktopShortcutDef d, string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, d.FileName);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        switch (d.Id)
        {
            case "settings":
                File.WriteAllText(path, "[InternetShortcut]\r\nURL=ms-settings:\r\nIconFile=" +
                    Path.Combine(win, @"ImmersiveControlPanel\SystemSettings.exe") + "\r\nIconIndex=0\r\n", Encoding.Unicode);
                break;
            case "thispc":
                WriteLink(path, Path.Combine(win, "explorer.exe"), "shell:MyComputerFolder", Path.Combine(win, @"System32\imageres.dll"), 104, "این رایانه");
                break;
            case "control":
                WriteLink(path, Path.Combine(win, @"System32\control.exe"), "", Path.Combine(win, @"System32\imageres.dll"), 22, "کنترل پنل");
                break;
            case "negar":
                string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                WriteLink(path, exe, "", exe, 0, "نگارخانه");
                break;
        }
        AppLog.Info("Desktop shortcut created: " + path);
        return path;
    }

    public static void Remove(DesktopShortcutDef d)
    {
        try { if (Exists(d)) File.Delete(PathOf(d)); } catch (Exception ex) { AppLog.Warn("Remove shortcut: " + ex.Message); }
    }

    private static void WriteLink(string path, string target, string args, string iconFile, int iconIndex, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(args);
            link.SetIconLocation(iconFile, iconIndex);
            link.SetDescription(description);
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) link.SetWorkingDirectory(dir);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
