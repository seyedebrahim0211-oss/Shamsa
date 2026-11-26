#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shamsa.Services.Desktop;

public sealed record SystemTelemetrySnapshot(
    string CpuUsageText,
    string RamUsageText,
    string DiskUsageText,
    string BatteryText,
    string NetworkSpeedText,
    bool IsInternetConnected,
    string GpuStatusText);

/// <summary>
/// Reads real Windows hardware & OS metrics. Whenever an API is unavailable or restricted,
/// reports "ناموجود" rather than fabricating placeholder values.
/// </summary>
public sealed class SystemTelemetryService : IDisposable
{
    private PerformanceCounter? _cpuCounter;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTimeOffset _lastNetSample = DateTimeOffset.UtcNow;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public SystemTelemetryService()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _ = _cpuCounter.NextValue();
        }
        catch
        {
            _cpuCounter = null;
        }
        SampleNetworkTotals(out _lastBytesReceived, out _lastBytesSent);
    }

    public SystemTelemetrySnapshot ReadSnapshot()
    {
        string cpuText = "ناموجود";
        if (_cpuCounter != null)
        {
            try { cpuText = $"{_cpuCounter.NextValue():0}%"; }
            catch { cpuText = "ناموجود"; }
        }

        string ramText = "ناموجود";
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                double totalGb = mem.ullTotalPhys / 1073741824.0;
                ramText = $"{usedGb:0.0} / {totalGb:0.0} GB ({mem.dwMemoryLoad}%)";
            }
        }
        catch { }

        string diskText = "ناموجود";
        try
        {
            var sysDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase));
            if (sysDrive != null)
            {
                double freeGb = sysDrive.AvailableFreeSpace / 1073741824.0;
                double totalGb = sysDrive.TotalSize / 1073741824.0;
                diskText = $"آزاد: {freeGb:0} از {totalGb:0} GB";
            }
        }
        catch { }

        string batteryText = "ناموجود (دسکتاپ)";
        try
        {
            var ps = SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery && ps.BatteryLifePercent <= 1.0f)
            {
                string charge = ps.PowerLineStatus == PowerLineStatus.Online ? "در حال شارژ" : "باتری";
                batteryText = $"{ps.BatteryLifePercent * 100:0}% ({charge})";
            }
        }
        catch { }

        string netText = "ناموجود";
        bool connected = NetworkInterface.GetIsNetworkAvailable();
        try
        {
            SampleNetworkTotals(out long rx, out long tx);
            double dt = Math.Max(0.25, (DateTimeOffset.UtcNow - _lastNetSample).TotalSeconds);
            double dlKbps = Math.Max(0, (rx - _lastBytesReceived) / 1024.0 / dt);
            double ulKbps = Math.Max(0, (tx - _lastBytesSent) / 1024.0 / dt);
            _lastBytesReceived = rx;
            _lastBytesSent = tx;
            _lastNetSample = DateTimeOffset.UtcNow;
            netText = $"↓ {FormatSpeed(dlKbps)} | ↑ {FormatSpeed(ulKbps)}";
        }
        catch { }

        return new SystemTelemetrySnapshot(cpuText, ramText, diskText, batteryText, netText, connected, "ناموجود (نیاز به درایور NVML/DXGI)");
    }

    private static void SampleNetworkTotals(out long rx, out long tx)
    {
        rx = 0; tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var stats = nic.GetIPStatistics();
            rx += stats.BytesReceived;
            tx += stats.BytesSent;
        }
    }

    private static string FormatSpeed(double kbps)
        => kbps >= 1024 ? $"{kbps / 1024.0:0.0} MB/s" : $"{kbps:0} KB/s";

    public void Dispose() => _cpuCounter?.Dispose();
}
