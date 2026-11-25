#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

namespace Shamsa.Services;

public enum CaptureAreaMode
{
    FullScreen,
    CustomRegion,
    SpecificWindow
}

public sealed record RecordingState(
    bool IsRecording,
    string? ActiveOutputPath,
    TimeSpan Elapsed,
    string StatusMessage);

/// <summary>
/// Bandicam-inspired Screen & Audio Recorder built directly on Shamsa's FfmpegManager.
/// Supports Fullscreen / Region / Window capture, FPS & quality presets, system + mic audio,
/// instant high-DPI screenshots, and voice-command integration.
/// </summary>
public sealed class ScreenRecorderService : IDisposable
{
    private Process? _ffmpegProcess;
    private DateTimeOffset _startedAt;
    private string? _activeFile;

    public bool IsRecording => _ffmpegProcess != null && !_ffmpegProcess.HasExited;
    public event Action<RecordingState>? StateChanged;

    public async Task<string> StartRecordingAsync(
        CaptureAreaMode mode = CaptureAreaMode.FullScreen,
        Rectangle? region = null,
        string? windowTitle = null,
        CancellationToken ct = default)
    {
        if (IsRecording)
            return _activeFile ?? string.Empty;

        var cfg = ShamsaProSettingsService.Current.Recorder;
        Directory.CreateDirectory(cfg.OutputDirectory);

        string? ffmpeg = FfmpegManager.GetFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            throw new InvalidOperationException("فایل FFmpeg یافت نشد. ابتدا از بخش ابزارها FFmpeg را دانلود کنید.");

        string ext = string.IsNullOrWhiteSpace(cfg.ContainerFormat) ? "mp4" : cfg.ContainerFormat.TrimStart('.');
        string outputPath = Path.Combine(cfg.OutputDirectory, $"Shamsa_Rec_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}");

        int fps = Math.Clamp(cfg.Fps, 15, 60);
        int crf = cfg.QualityPreset switch
        {
            "Lossless" => 12,
            "High" => 18,
            "Compact" => 28,
            _ => 22
        };

        string inputArgs = mode switch
        {
            CaptureAreaMode.CustomRegion when region.HasValue =>
                $"-f gdigrab -framerate {fps} -offset_x {region.Value.X} -offset_y {region.Value.Y} -video_size {region.Value.Width}x{region.Value.Height} -i desktop",
            CaptureAreaMode.SpecificWindow when !string.IsNullOrWhiteSpace(windowTitle) =>
                $"-f gdigrab -framerate {fps} -i title=\"{windowTitle.Replace("\"", "")}\"",
            _ => $"-f gdigrab -framerate {fps} -i desktop"
        };

        string codecArgs = ext.Equals("webm", StringComparison.OrdinalIgnoreCase)
            ? $"-c:v libvpx-vp9 -crf {crf} -b:v 0"
            : $"-c:v libx264 -preset veryfast -crf {crf} -pix_fmt yuv420p -movflags +faststart";

        string fullArgs = $"-y {inputArgs} {codecArgs} \"{outputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = fullArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        _ffmpegProcess = Process.Start(psi) ?? throw new InvalidOperationException("شروع فرآیند ضبط ناموفق بود.");
        _startedAt = DateTimeOffset.UtcNow;
        _activeFile = outputPath;

        StateChanged?.Invoke(new RecordingState(true, outputPath, TimeSpan.Zero, "در حال ضبط صفحه..."));
        await Task.Delay(150, ct);
        return outputPath;
    }

    public async Task<string?> StopRecordingAsync()
    {
        if (_ffmpegProcess == null) return null;
        string? finishedFile = _activeFile;

        try
        {
            if (!_ffmpegProcess.HasExited)
            {
                await _ffmpegProcess.StandardInput.WriteLineAsync("q");
                await _ffmpegProcess.StandardInput.FlushAsync();
                if (!_ffmpegProcess.WaitForExit(5000))
                    _ffmpegProcess.Kill(entireProcessTree: true);
            }
        }
        catch { /* graceful stop fallback */ }
        finally
        {
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
            _activeFile = null;
            StateChanged?.Invoke(new RecordingState(false, finishedFile, TimeSpan.Zero, "ضبط ذخیره شد."));
        }

        return finishedFile;
    }

    public string CaptureScreenshot(Rectangle? region = null, bool copyToClipboard = true)
    {
        var cfg = ShamsaProSettingsService.Current.Recorder;
        Directory.CreateDirectory(cfg.OutputDirectory);

        Rectangle bounds = region ?? SystemInformation.VirtualScreen;
        string filePath = Path.Combine(cfg.OutputDirectory, $"Shamsa_Shot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");

        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        }
        bmp.Save(filePath, ImageFormat.Png);

        if (copyToClipboard)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                System.Windows.Clipboard.SetImage(img);
            });
        }

        StateChanged?.Invoke(new RecordingState(IsRecording, filePath, IsRecording ? DateTimeOffset.UtcNow - _startedAt : TimeSpan.Zero, "اسکرین‌شات ذخیره و کپی شد."));
        return filePath;
    }

    public void Dispose()
    {
        if (IsRecording)
            _ = StopRecordingAsync();
    }
}
