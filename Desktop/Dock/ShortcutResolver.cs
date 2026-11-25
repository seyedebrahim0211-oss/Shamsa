using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Shamsa.Services;

namespace Shamsa.Desktop.Dock;

/// <summary>Resolves .lnk shortcuts through the shell's own IShellLinkW (no WScript dependency).</summary>
public static class ShortcutResolver
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public sealed record ShortcutInfo(string? TargetPath, string? Arguments, string? Description);

    private static readonly Dictionary<string, (DateTime Stamp, ShortcutInfo? Info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Target of a .lnk file (null for shell-namespace shortcuts such as UWP apps). Thread-safe.</summary>
    public static ShortcutInfo? Resolve(string lnkPath)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(lnkPath); } catch { return null; }
        lock (Cache)
            if (Cache.TryGetValue(lnkPath, out var hit) && hit.Stamp == stamp) return hit.Info;

        ShortcutInfo? info = null;
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sl = (IShellLinkW)link;
            var path = new StringBuilder(1024);
            sl.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(1024);
            sl.GetArguments(args, args.Capacity);
            var desc = new StringBuilder(512);
            try { sl.GetDescription(desc, desc.Capacity); } catch { }
            string target = Environment.ExpandEnvironmentVariables(path.ToString());
            info = new ShortcutInfo(string.IsNullOrWhiteSpace(target) ? null : target,
                                    args.Length == 0 ? null : args.ToString(),
                                    desc.Length == 0 ? null : desc.ToString());
        }
        catch (Exception ex) { AppLog.Warn($"Shortcut resolve failed for {Path.GetFileName(lnkPath)}: {ex.Message}"); }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); }

        lock (Cache) Cache[lnkPath] = (stamp, info);
        return info;
    }

    /// <summary>The executable a pin refers to (for matching running windows), or null.</summary>
    public static string? ExecutableFor(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return null;
        if (pin.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var t = Resolve(pin)?.TargetPath;
            return t != null && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : null;
        }
        return pin.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pin : null;
    }
}
