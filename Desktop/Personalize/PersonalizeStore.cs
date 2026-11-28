using System.Text.Json;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Desktop.Personalize;

/// <summary>One HKCU value as it was before Negarkhaneh first changed it.</summary>
public sealed class RegistryOriginal
{
    public string Group { get; set; } = "";       // icons | visibility | iconsize | wallpaper | accent | cursors
    public string SubKey { get; set; } = "";      // relative to HKEY_CURRENT_USER
    public string Name { get; set; } = "";        // "" = default value
    public bool Existed { get; set; }
    public string Kind { get; set; } = "String";  // RegistryValueKind
    public string? Text { get; set; }             // string / expand string / multi (joined by \n)
    public long Number { get; set; }              // dword / qword
    public string? Base64 { get; set; }           // binary
}

/// <summary>What the user applied (used for export / UI state).</summary>
public sealed class PersonalizeState
{
    public Dictionary<string, string> IconFiles { get; set; } = new();   // icon id → "path,index"
    public string? IconTheme { get; set; }
    public string? WallpaperPath { get; set; }
    public string WallpaperStyle { get; set; } = "Fill";
    public string? SolidColorHex { get; set; }
    public string? AccentHex { get; set; }
    public bool? Transparency { get; set; }
    public bool? AccentOnTaskbar { get; set; }
    public bool? AccentOnTitleBars { get; set; }
    public string? CursorScheme { get; set; }
    public int? CursorSize { get; set; }
    public int? DesktopIconSize { get; set; }
    public int? IconSpacing { get; set; }
    public string? LockScreenImage { get; set; }
}

public sealed class SlideshowSettings
{
    public bool Enabled { get; set; }
    public string? Folder { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public bool Shuffle { get; set; } = true;
    public bool BingDaily { get; set; }
    public string? LastBingDate { get; set; }
}

public sealed class PersonalizeData
{
    public int Version { get; set; } = 1;
    public List<RegistryOriginal> Originals { get; set; } = new();
    public PersonalizeState Applied { get; set; } = new();
    public SlideshowSettings Slideshow { get; set; } = new();
    public Dictionary<string, string> WallpaperEdits { get; set; } = new();
    public string? OriginalWallpaper { get; set; }
}

/// <summary>
/// %LocalAppData%\Shamsa\personalize.json plus a registry writer that records every original HKCU value
/// before the first change, so each group (and everything) can be put back exactly.
/// </summary>
public static class PersonalizeStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static PersonalizeData? _data;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "personalize.json");
    public static string IconsDir => Path.Combine(AppPaths.DataDir, "icons");
    public static string WallpapersDir => Path.Combine(AppPaths.DataDir, "wallpapers");

    public static event Action? Changed;

    public static PersonalizeData Data
    {
        get
        {
            lock (Gate)
            {
                if (_data != null) return _data;
                try
                {
                    _data = File.Exists(FilePath)
                        ? JsonSerializer.Deserialize<PersonalizeData>(File.ReadAllText(FilePath), Json) ?? new()
                        : new();
                }
                catch (Exception ex) { AppLog.Warn("personalize.json unreadable: " + ex.Message); _data = new(); }
                _data.Originals ??= new(); _data.Applied ??= new(); _data.Slideshow ??= new();
                _data.WallpaperEdits ??= new();
                _data.Applied.IconFiles ??= new();
                return _data;
            }
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { AppLog.Error("Saving personalize.json failed", ex); }
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ registry with backup

    public static object? Read(string subKey, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(subKey); return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
        catch { return null; }
    }

    /// <summary>Records the original value (once) then writes the new one.</summary>
    public static void Write(string group, string subKey, string name, object value, RegistryValueKind kind)
    {
        Backup(group, subKey, name);
        Capture(subKey, name);
        using var k = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
                      ?? throw new InvalidOperationException("Cannot open HKCU\\" + subKey);
        k.SetValue(name, value, kind);
    }

    /// <summary>Records the original value (once) then deletes it.</summary>
    public static void Delete(string group, string subKey, string name)
    {
        Backup(group, subKey, name);
        Capture(subKey, name);
        using var k = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void Backup(string group, string subKey, string name)
    {
        var d = Data;
        if (d.Originals.Any(o => o.SubKey.Equals(subKey, StringComparison.OrdinalIgnoreCase) && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;
        var orig = new RegistryOriginal { Group = group, SubKey = subKey, Name = name };
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey);
            object? v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (k != null && v != null)
            {
                var kind = k.GetValueKind(name);
                orig.Existed = true;
                orig.Kind = kind.ToString();
                switch (kind)
                {
                    case RegistryValueKind.DWord: orig.Number = Convert.ToInt64(unchecked((uint)(int)v)); break;
                    case RegistryValueKind.QWord: orig.Number = (long)v; break;
                    case RegistryValueKind.Binary: orig.Base64 = Convert.ToBase64String((byte[])v); break;
                    case RegistryValueKind.MultiString: orig.Text = string.Join("\n", (string[])v); break;
                    default: orig.Text = v.ToString(); break;
                }
            }
        }
        catch (Exception ex) { AppLog.Warn($"Backup of HKCU\\{subKey}\\{name} failed: {ex.Message}"); }
        d.Originals.Add(orig);
        Save();
    }

    /// <summary>Puts back every original value of the given groups (null = all). Returns how many were restored.</summary>
    public static int Restore(params string[]? groups)
    {
        var d = Data;
        var targets = d.Originals.Where(o => groups == null || groups.Length == 0 || groups.Contains(o.Group)).ToList();
        int n = 0;
        foreach (var o in targets)
        {
            try
            {
                if (!o.Existed)
                {
                    using var k = Registry.CurrentUser.OpenSubKey(o.SubKey, writable: true);
                    k?.DeleteValue(o.Name, throwOnMissingValue: false);
                }
                else
                {
                    using var k = Registry.CurrentUser.CreateSubKey(o.SubKey, writable: true);
                    var kind = Enum.TryParse<RegistryValueKind>(o.Kind, out var parsed) ? parsed : RegistryValueKind.String;
                    object value = kind switch
                    {
                        RegistryValueKind.DWord => unchecked((int)(uint)o.Number),
                        RegistryValueKind.QWord => o.Number,
                        RegistryValueKind.Binary => Convert.FromBase64String(o.Base64 ?? ""),
                        RegistryValueKind.MultiString => (o.Text ?? "").Split('\n'),
                        _ => o.Text ?? ""
                    };
                    k!.SetValue(o.Name, value, kind);
                }
                d.Originals.Remove(o);
                n++;
            }
            catch (Exception ex) { AppLog.Warn($"Restore of HKCU\\{o.SubKey}\\{o.Name} failed: {ex.Message}"); }
        }
        Save();
        return n;
    }

    // ------------------------------------------------------------------ all-or-nothing

    [ThreadStatic] private static List<(string SubKey, string Name, object? Value, RegistryValueKind Kind)>? _tx;

    private static void Capture(string subKey, string name)
    {
        if (_tx == null || _tx.Any(c => c.SubKey.Equals(subKey, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        object? v = null; var kind = RegistryValueKind.Unknown;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey);
            v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v != null) kind = k!.GetValueKind(name);
        }
        catch { }
        _tx.Add((subKey, name, v, kind));
    }

    /// <summary>
    /// Runs a multi-value change; if it throws, every value it touched is put back to what it was just
    /// before (so a setting is never left half-applied) and the exception is rethrown.
    /// </summary>
    public static void Transaction(Action action)
    {
        if (_tx != null) { action(); return; }
        _tx = new();
        try { action(); }
        catch
        {
            foreach (var c in Enumerable.Reverse(_tx))
            {
                try
                {
                    if (c.Value == null)
                    {
                        using var k = Registry.CurrentUser.OpenSubKey(c.SubKey, writable: true);
                        k?.DeleteValue(c.Name, false);
                    }
                    else
                    {
                        using var k = Registry.CurrentUser.CreateSubKey(c.SubKey, writable: true);
                        k!.SetValue(c.Name, c.Value, c.Kind);
                    }
                }
                catch (Exception ex) { AppLog.Warn($"Rollback of HKCU\\{c.SubKey}\\{c.Name} failed: {ex.Message}"); }
            }
            throw;
        }
        finally { _tx = null; }
    }

    public static bool HasChanges(string group) => Data.Originals.Any(o => o.Group == group);
}
