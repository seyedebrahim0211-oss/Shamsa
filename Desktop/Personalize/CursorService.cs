using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

public sealed record CursorScheme(string Name, string[] Files, bool IsSystem)
{
    public string DisplayName => Name switch
    {
        "Windows Default" => "پیش‌فرض ویندوز",
        "Windows Black" => "سیاه ویندوز",
        "Windows Inverted" => "معکوس ویندوز",
        "Windows Standard" => "استاندارد ویندوز",
        "Windows Aero" => "ایرو ویندوز",
        "Windows Aero L" => "ایرو ویندوز (بزرگ)",
        "Windows Aero XL)" or "Windows Aero XL" => "ایرو ویندوز (خیلی بزرگ)",
        "Magnified" => "بزرگ‌نمایی‌شده",
        _ when Name.StartsWith("Windows Inverted (large)", StringComparison.OrdinalIgnoreCase) => "معکوس ویندوز (بزرگ)",
        _ when Name.StartsWith("Windows Inverted (extra large)", StringComparison.OrdinalIgnoreCase) => "معکوس ویندوز (خیلی بزرگ)",
        _ when Name.StartsWith("Windows Standard (large)", StringComparison.OrdinalIgnoreCase) => "استاندارد ویندوز (بزرگ)",
        _ when Name.StartsWith("Windows Standard (extra large)", StringComparison.OrdinalIgnoreCase) => "استاندارد ویندوز (خیلی بزرگ)",
        _ when Name.StartsWith("Windows Default (large)", StringComparison.OrdinalIgnoreCase) => "پیش‌فرض ویندوز (بزرگ)",
        _ when Name.StartsWith("Windows Default (extra large)", StringComparison.OrdinalIgnoreCase) => "پیش‌فرض ویندوز (خیلی بزرگ)",
        _ when Name.StartsWith("Windows Black (large)", StringComparison.OrdinalIgnoreCase) => "سیاه ویندوز (بزرگ)",
        _ when Name.StartsWith("Windows Black (extra large)", StringComparison.OrdinalIgnoreCase) => "سیاه ویندوز (خیلی بزرگ)",
        _ => Name
    };
}

/// <summary>Mouse pointer schemes and size (HKCU\Control Panel\Cursors + Accessibility, then SPI_SETCURSORS).</summary>
public static class CursorService
{
    public const string Group = "cursors";
    private const string CursorsKey = @"Control Panel\Cursors";
    private const string SchemesKey = @"Control Panel\Cursors\Schemes";
    private const string SystemSchemesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cursors\Schemes";
    private const string AccessibilityKey = @"Software\Microsoft\Accessibility";

    /// <summary>Order of the comma-separated files in a scheme value.</summary>
    public static readonly string[] Roles =
    {
        "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS", "SizeWE",
        "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person"
    };

    /// <summary>Per-user schemes plus the system schemes (HKLM, read-only).</summary>
    public static List<CursorScheme> Schemes()
    {
        var list = new List<CursorScheme>();
        void ReadFrom(RegistryKey root, string path, bool system)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) return;
                foreach (string name in k.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string v = k.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                    if (list.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new CursorScheme(name, v.Split(','), system));
                }
            }
            catch (Exception ex) { AppLog.Warn("Reading cursor schemes: " + ex.Message); }
        }
        ReadFrom(Registry.CurrentUser, SchemesKey, false);
        ReadFrom(Registry.LocalMachine, SystemSchemesKey, true);
        // "Windows Default" (what a fresh profile uses) has no scheme entry; it is the Aero set.
        if (!list.Any(s => s.Name.Equals("Windows Default", StringComparison.OrdinalIgnoreCase))
            && list.FirstOrDefault(s => s.Name.Equals("Windows Aero", StringComparison.OrdinalIgnoreCase)) is { } aero)
            list.Add(new CursorScheme("Windows Default", aero.Files, true));
        return list.OrderBy(s => s.IsSystem ? 0 : 1).ThenBy(s => s.Name).ToList();
    }

    public static string CurrentSchemeName() => PersonalizeStore.Read(CursorsKey, "") as string ?? "";

    /// <summary>1 (normal) … 15 (largest).</summary>
    public static int CurrentSize() => PersonalizeStore.Read(AccessibilityKey, "CursorSize") is int i ? Math.Clamp(i, 1, 15) : 1;

    public static void ApplyScheme(CursorScheme scheme)
    {
        PersonalizeStore.Write(Group, CursorsKey, "", scheme.Name, RegistryValueKind.String);
        for (int i = 0; i < Roles.Length; i++)
        {
            string file = i < scheme.Files.Length ? scheme.Files[i].Trim() : "";
            PersonalizeStore.Write(Group, CursorsKey, Roles[i], file, RegistryValueKind.ExpandString);
        }
        PersonalizeStore.Write(Group, CursorsKey, "Scheme Source", scheme.IsSystem ? 2 : 1, RegistryValueKind.DWord);
        PersonalizeStore.Data.Applied.CursorScheme = scheme.Name; PersonalizeStore.Save();
        Reload();
    }

    public static void SetSize(int size)
    {
        size = Math.Clamp(size, 1, 15);
        PersonalizeStore.Write(Group, AccessibilityKey, "CursorSize", size, RegistryValueKind.DWord);
        PersonalizeStore.Write(Group, CursorsKey, "CursorBaseSize", 32 + (size - 1) * 16, RegistryValueKind.DWord);
        PersonalizeStore.Data.Applied.CursorSize = size; PersonalizeStore.Save();
        Reload();
    }

    public static void Reset()
    {
        PersonalizeStore.Restore(Group);
        PersonalizeStore.Data.Applied.CursorScheme = null; PersonalizeStore.Data.Applied.CursorSize = null;
        PersonalizeStore.Save();
        Reload();
    }

    private static void Reload()
    {
        try { PersonalizeNative.SystemParametersInfoPtr(PersonalizeNative.SPI_SETCURSORS, 0, IntPtr.Zero, PersonalizeNative.SPIF_UPDATEINIFILE | PersonalizeNative.SPIF_SENDCHANGE); }
        catch (Exception ex) { AppLog.Warn("SPI_SETCURSORS: " + ex.Message); }
    }
}
