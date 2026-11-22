using Microsoft.Win32;

namespace Shamsa.Services;

/// <summary>Per-user "start with Windows" via HKCU\...\Run (no admin rights needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Shamsa";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to change startup registration", ex);
            return false;
        }
    }
}
