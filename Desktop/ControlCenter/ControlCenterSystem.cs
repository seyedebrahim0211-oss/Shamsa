using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Shamsa.Services;
using Windows.Devices.Radios;
using Windows.Media.Control;

namespace Shamsa.Desktop.ControlCenter;

/// <summary>
/// Documented system controls for the control center: radios (WinRT), brightness (WMI for built-in
/// panels via late-bound WbemScripting COM, DDC/CI for external monitors), volume (Core Audio) and media (GSMTC).
/// Every call is defensive; "not available" is a normal answer.
/// </summary>
internal static class ControlCenterSystem
{
    // ------------------------------------------------------------------ radios

    public sealed record RadioInfo(bool Available, bool On, string? Name);

    private static async Task<Radio?> FindRadioAsync(RadioKind kind)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return null;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex) { AppLog.Warn($"Radio {kind}: {ex.Message}"); return null; }
    }

    public static async Task<RadioInfo> GetRadioAsync(RadioKind kind)
    {
        var r = await FindRadioAsync(kind);
        return r == null ? new RadioInfo(false, false, null) : new RadioInfo(true, r.State == RadioState.On, r.Name);
    }

    public static async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        var r = await FindRadioAsync(kind);
        if (r == null) return false;
        try
        {
            var result = await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex) { AppLog.Warn($"Radio set {kind}: {ex.Message}"); return false; }
    }

    // ------------------------------------------------------------------ brightness

    /// <summary>"wmi" (laptop panel), "ddc" (external monitor) or null when no control works.</summary>
    public static string? BrightnessBackend { get; private set; }

    /// <summary>Current brightness 0..100, or -1 if no backend works. Slow (WMI/DDC) — call off the UI thread.</summary>
    public static int GetBrightness()
    {
        int wmi = WmiGetBrightness();
        if (wmi >= 0) { BrightnessBackend = "wmi"; return wmi; }
        int ddc = DdcGetBrightness();
        if (ddc >= 0) { BrightnessBackend = "ddc"; return ddc; }
        BrightnessBackend = null;
        return -1;
    }

    public static void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (BrightnessBackend == "wmi") WmiSetBrightness(percent);
        else if (BrightnessBackend == "ddc") DdcSetBrightness(percent);
    }

    private static dynamic? WmiService()
    {
        var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        if (t == null) return null;
        dynamic locator = Activator.CreateInstance(t)!;
        return locator.ConnectServer(".", @"root\wmi");
    }

    private static int WmiGetBrightness()
    {
        try
        {
            dynamic? svc = WmiService();
            if (svc == null) return -1;
            dynamic items = svc.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (dynamic o in (System.Collections.IEnumerable)items)
                return Convert.ToInt32(o.CurrentBrightness);
        }
        catch { /* desktop PCs: class not supported */ }
        return -1;
    }

    private static void WmiSetBrightness(int percent)
    {
        try
        {
            dynamic? svc = WmiService();
            if (svc == null) return;
            dynamic items = svc.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (dynamic m in (System.Collections.IEnumerable)items)
                m.WmiSetBrightness(1, (byte)percent);
        }
        catch (Exception ex) { AppLog.Warn("WMI brightness: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription; }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(IntPtr h, uint value);

    private static void ForEachPhysicalMonitor(Func<IntPtr, bool> action)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        foreach (var hMon in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint n) || n == 0) continue;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) continue;
            try
            {
                foreach (var pm in arr)
                    if (!action(pm.hPhysicalMonitor)) return;
            }
            finally { DestroyPhysicalMonitors(n, arr); }
        }
    }

    private static int DdcGetBrightness()
    {
        int result = -1;
        try
        {
            ForEachPhysicalMonitor(h =>
            {
                if (GetMonitorBrightness(h, out uint min, out uint cur, out uint max) && max > min)
                {
                    result = (int)Math.Round((cur - min) * 100.0 / (max - min));
                    return false;
                }
                return true;
            });
        }
        catch (Exception ex) { AppLog.Warn("DDC brightness: " + ex.Message); }
        return result;
    }

    private static void DdcSetBrightness(int percent)
    {
        try
        {
            ForEachPhysicalMonitor(h =>
            {
                if (GetMonitorBrightness(h, out uint min, out _, out uint max) && max > min)
                    SetMonitorBrightness(h, (uint)(min + (max - min) * percent / 100.0));
                return true;
            });
        }
        catch (Exception ex) { AppLog.Warn("DDC set brightness: " + ex.Message); }
    }

    // ------------------------------------------------------------------ volume

    public static (bool Available, float Level, bool Muted) GetVolume()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return (false, 0, false);
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (true, dev.AudioEndpointVolume.MasterVolumeLevelScalar, dev.AudioEndpointVolume.Mute);
        }
        catch { return (false, 0, false); }
    }

    public static void SetVolume(float level, bool? mute = null)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var ep = dev.AudioEndpointVolume;
            ep.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            if (mute.HasValue) ep.Mute = mute.Value;
            else if (ep.Mute && level > 0) ep.Mute = false;
        }
        catch (Exception ex) { AppLog.Warn("CC volume: " + ex.Message); }
    }

    public static void ToggleMute()
    {
        var (ok, level, muted) = GetVolume();
        if (ok) SetVolume(level, !muted);
    }

    // ------------------------------------------------------------------ media

    private static GlobalSystemMediaTransportControlsSessionManager? _media;

    public sealed record MediaInfo(bool Has, string Title, string Artist, bool Playing);

    public static async Task<MediaInfo> GetMediaAsync()
    {
        try
        {
            _media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _media?.GetCurrentSession();
            if (session == null) return new MediaInfo(false, "", "", false);
            var props = await session.TryGetMediaPropertiesAsync();
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return new MediaInfo(!string.IsNullOrWhiteSpace(props?.Title), props?.Title ?? "", props?.Artist ?? "", playing);
        }
        catch { return new MediaInfo(false, "", "", false); }
    }

    public static async Task MediaCommandAsync(string cmd)
    {
        try
        {
            var session = _media?.GetCurrentSession();
            if (session == null) return;
            switch (cmd)
            {
                case "prev": await session.TrySkipPreviousAsync(); break;
                case "next": await session.TrySkipNextAsync(); break;
                default: await session.TryTogglePlayPauseAsync(); break;
            }
        }
        catch (Exception ex) { AppLog.Warn("CC media: " + ex.Message); }
    }

    // ------------------------------------------------------------------ misc

    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    public static void Lock() { try { LockWorkStation(); } catch { } }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn($"CC open {target}: {ex.Message}"); }
    }
}
