using System.Net.NetworkInformation;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Shamsa.Desktop.Shell;
using Shamsa.Services;
using Windows.Media.Control;

namespace Shamsa.Desktop.TopBar;

/// <summary>
/// Live system data shown on the bar. One instance is shared by all bar windows of a module; each
/// provider is isolated in try/catch so a failing one only hides its own item.
/// </summary>
internal sealed class TopBarState : IDisposable
{
    public event Action? Changed;

    // clock
    public DateTime Now { get; private set; } = DateTime.Now;
    // stats
    public double CpuPercent { get; private set; } = -1;
    public double RamPercent { get; private set; } = -1;
    public double RamUsedGb { get; private set; }
    public double RamTotalGb { get; private set; }
    // network
    public string NetKind { get; private set; } = "none";   // wifi | ethernet | other | none
    public string NetName { get; private set; } = "";
    // volume
    public bool HasAudio { get; private set; }
    public float Volume { get; private set; }
    public bool Muted { get; private set; }
    // battery
    public bool HasBattery { get; private set; }
    public int BatteryPercent { get; private set; }
    public bool Charging { get; private set; }
    // media
    public bool HasMedia { get; private set; }
    public string MediaTitle { get; private set; } = "";
    public string MediaArtist { get; private set; } = "";
    public bool MediaPlaying { get; private set; }
    // workspaces
    public int WorkspaceCount { get; private set; } = 1;
    public int WorkspaceIndex { get; private set; }
    public bool WorkspacesKnown { get; private set; }
    // focused window
    public IntPtr FocusedHwnd { get; private set; }
    public string FocusedTitle { get; private set; } = "";
    public string? FocusedExe { get; private set; }

    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(2) };
    private ulong _prevIdle, _prevTotal;
    private MMDeviceEnumerator? _audioEnum;
    private MMDevice? _audioDevice;
    private DateTime _audioAcquired;
    private GlobalSystemMediaTransportControlsSessionManager? _media;
    private bool _mediaBusy, _mediaRequested;
    private IntPtr _hook;
    private TopBarNative.WinEventProc? _hookProc;
    private bool _started, _disposed;

    public void Start(bool trackForeground)
    {
        if (_started) return;
        _started = true;
        _fast.Tick += (_, _) => { Safe(UpdateClock); Safe(UpdateVolume); Changed?.Invoke(); };
        _slow.Tick += (_, _) => { Safe(UpdateStats); Safe(UpdateNetwork); Safe(UpdateBattery); Safe(UpdateWorkspaces); _ = UpdateMediaAsync(); if (!trackForeground) Safe(UpdateForeground); Changed?.Invoke(); };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Safe(UpdateClock); Safe(UpdateStats); Safe(UpdateNetwork); Safe(UpdateBattery); Safe(UpdateVolume); Safe(UpdateWorkspaces); Safe(UpdateForeground);
        _ = UpdateMediaAsync();
        if (trackForeground) Safe(InstallForegroundHook);
        _fast.Start();
        _slow.Start();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _fast.Stop();
        _slow.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        if (_hook != IntPtr.Zero) { TopBarNative.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _hookProc = null;
        ReleaseAudio();
        _audioEnum?.Dispose();
        _audioEnum = null;
        _media = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { AppLog.Warn("TopBar provider " + a.Method.Name + ": " + ex.Message); }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Safe(UpdateNetwork); Changed?.Invoke(); });

    // ------------------------------------------------------------ providers

    private void UpdateClock() => Now = DateTime.Now;

    private void UpdateStats()
    {
        if (TopBarNative.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong idleV = idle.Value, total = kernel.Value + user.Value; // kernel includes idle
            if (_prevTotal != 0 && total > _prevTotal)
            {
                double busy = 1.0 - (double)(idleV - _prevIdle) / (total - _prevTotal);
                CpuPercent = Math.Clamp(busy * 100, 0, 100);
            }
            _prevIdle = idleV; _prevTotal = total;
        }
        var mem = new TopBarNative.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TopBarNative.MEMORYSTATUSEX>() };
        if (TopBarNative.GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            RamTotalGb = mem.ullTotalPhys / 1073741824.0;
            RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
            RamPercent = mem.dwMemoryLoad;
        }
    }

    private void UpdateNetwork()
    {
        string kind = "none", name = "";
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            string desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") || desc.Contains("vpn") || desc.Contains("bluetooth")) continue;
            bool hasGateway = false;
            try { hasGateway = nic.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)); } catch { }
            if (!hasGateway) continue;
            string k = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi"
                     : nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT ? "ethernet" : "other";
            if (kind == "none" || (kind == "other" && k != "other") || (kind == "wifi" && k == "ethernet")) { kind = k; name = nic.Name; }
        }
        NetKind = kind; NetName = name;
    }

    private void ReleaseAudio()
    {
        try { _audioDevice?.Dispose(); } catch { }
        _audioDevice = null;
    }

    private void UpdateVolume()
    {
        // Re-acquire the default endpoint now and then: the user may switch speakers/headphones.
        if (_audioDevice == null || DateTime.UtcNow - _audioAcquired > TimeSpan.FromSeconds(10))
        {
            ReleaseAudio();
            _audioEnum ??= new MMDeviceEnumerator();
            try
            {
                _audioDevice = _audioEnum.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    ? _audioEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null;
            }
            catch { _audioDevice = null; }
            _audioAcquired = DateTime.UtcNow;
        }
        if (_audioDevice == null) { HasAudio = false; return; }
        var ep = _audioDevice.AudioEndpointVolume;
        Volume = ep.MasterVolumeLevelScalar;
        Muted = ep.Mute;
        HasAudio = true;
    }

    public void SetVolume(float level)
    {
        try
        {
            UpdateVolume();
            if (_audioDevice == null) return;
            _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            if (_audioDevice.AudioEndpointVolume.Mute && level > 0) _audioDevice.AudioEndpointVolume.Mute = false;
            UpdateVolume();
            Changed?.Invoke();
        }
        catch (Exception ex) { AppLog.Warn("TopBar volume: " + ex.Message); }
    }

    public void ToggleMute()
    {
        try
        {
            UpdateVolume();
            if (_audioDevice == null) return;
            _audioDevice.AudioEndpointVolume.Mute = !_audioDevice.AudioEndpointVolume.Mute;
            UpdateVolume();
            Changed?.Invoke();
        }
        catch (Exception ex) { AppLog.Warn("TopBar mute: " + ex.Message); }
    }

    private void UpdateBattery()
    {
        if (!TopBarNative.GetSystemPowerStatus(out var s)) { HasBattery = false; return; }
        // 128 = no system battery, 255 = unknown
        HasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100;
        BatteryPercent = s.BatteryLifePercent;
        Charging = s.ACLineStatus == 1;
    }

    private async Task UpdateMediaAsync()
    {
        if (_mediaBusy) return;
        _mediaBusy = true;
        try
        {
            if (_media == null && !_mediaRequested)
            {
                _mediaRequested = true;
                _media = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            }
            var session = _media?.GetCurrentSession();
            if (session == null) { HasMedia = false; return; }
            var props = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            MediaTitle = props?.Title ?? "";
            MediaArtist = props?.Artist ?? "";
            MediaPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            HasMedia = !string.IsNullOrWhiteSpace(MediaTitle);
        }
        catch (Exception ex) { HasMedia = false; AppLog.Warn("TopBar media: " + ex.Message); }
        finally { _mediaBusy = false; }
        Changed?.Invoke();
    }

    public async void MediaCommand(string cmd)
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
            await Task.Delay(350);
            await UpdateMediaAsync();
        }
        catch (Exception ex) { AppLog.Warn("TopBar media command: " + ex.Message); }
    }

    private void UpdateWorkspaces()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
        byte[]? ids = null, current = null;
        using (var k = Registry.CurrentUser.OpenSubKey(key))
        {
            ids = k?.GetValue("VirtualDesktopIDs") as byte[];
            current = k?.GetValue("CurrentVirtualDesktop") as byte[];
        }
        if (current == null)
        {
            // Windows 10 keeps the current desktop per logon session.
            try
            {
                int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                using var k = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{session}\VirtualDesktops");
                current = k?.GetValue("CurrentVirtualDesktop") as byte[];
            }
            catch { }
        }
        if (ids == null || ids.Length < 16)
        {
            WorkspaceCount = 1; WorkspaceIndex = 0; WorkspacesKnown = ids != null || current == null;
            return;
        }
        int count = ids.Length / 16, index = 0;
        if (current is { Length: 16 })
        {
            for (int i = 0; i < count; i++)
            {
                bool same = true;
                for (int b = 0; b < 16 && same; b++) same = ids[i * 16 + b] == current[b];
                if (same) { index = i; break; }
            }
        }
        WorkspaceCount = count; WorkspaceIndex = index; WorkspacesKnown = true;
    }

    public void SwitchWorkspace(int target)
    {
        try
        {
            Safe(UpdateWorkspaces);
            int steps = target - WorkspaceIndex;
            if (steps == 0) return;
            TopBarNative.SwitchVirtualDesktop(steps);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (_, _) => { t.Stop(); Safe(UpdateWorkspaces); Changed?.Invoke(); };
            t.Start();
        }
        catch (Exception ex) { AppLog.Warn("TopBar workspace switch: " + ex.Message); }
    }

    private void InstallForegroundHook()
    {
        _hookProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject != 0) return; // OBJID_WINDOW
            Safe(UpdateForeground);
            Changed?.Invoke();
        };
        _hook = TopBarNative.SetWinEventHook(TopBarNative.EVENT_SYSTEM_FOREGROUND, TopBarNative.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0, TopBarNative.WINEVENT_OUTOFCONTEXT);
    }

    private void UpdateForeground()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || NativeMethods.IsOwnProcessWindow(fg)) return; // keep last real app
        string cls = NativeMethods.GetClassNameOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            FocusedHwnd = IntPtr.Zero; FocusedTitle = "دسکتاپ"; FocusedExe = null;
            return;
        }
        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        FocusedHwnd = fg;
        FocusedTitle = ShellInterop.GetTitle(fg);
        FocusedExe = ShellInterop.GetProcessPath(pid);
    }
}
