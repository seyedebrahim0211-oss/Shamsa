using System.Diagnostics;
using System.Globalization;
using System.Text;
using Shamsa.Services;

namespace Shamsa.Recording;

public enum RecorderState { Idle, Starting, Recording, Paused, Finalizing }

/// <summary>Immutable description of one recording job.</summary>
internal sealed record RecordingJob(PixelRect Rect, int Fps, int Quality, string Encoder, string Container,
    bool DrawMouse, bool UseDesktopDuplication, int? DdaOutputIndex, bool SystemAudio, bool Microphone, string OutputPath, string CameraDevice, int CameraCorner, int CameraPercent);

/// <summary>
/// Segment-based FFmpeg screen recorder. Each start/resume launches one FFmpeg segment (MKV, video only);
/// audio is captured with WASAPI into WAV tracks. On stop, segments are concatenated losslessly and the
/// audio is mixed/muxed into the final MP4/MKV. Temporary files live under %LocalAppData%\Shamsa\rec-temp.
/// </summary>
internal sealed class ScreenRecorderEngine
{
    private readonly Stopwatch _elapsed = new();
    private readonly List<string> _segments = new();
    private FfmpegProcess? _current;
    private AudioTrackRecorder? _systemAudio, _micAudio;
    private RecordingJob? _job;
    private string _tempDir = string.Empty;
    private string _ffmpeg = string.Empty;
    private string _effectiveEncoder = "x264";
    private bool _effectiveDda;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public event Action<RecorderState>? StateChanged;
    /// <summary>Raised when an FFmpeg segment dies unexpectedly while recording.</summary>
    public event Action<string>? Failed;
    public string? Notes { get; private set; }

    public long CurrentBytes
    {
        get
        {
            long total = 0;
            foreach (var s in _segments.ToArray())
            {
                try { total += new FileInfo(s).Length; } catch { }
            }
            return total;
        }
    }

    private void SetState(RecorderState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public async Task StartAsync(RecordingJob job)
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecorderState.Idle) throw new InvalidOperationException("ضبط دیگری در حال اجراست.");
            _ffmpeg = FfmpegManager.GetFfmpegPath(refresh: true) ?? throw new InvalidOperationException("FFmpeg نصب نیست.");
            SetState(RecorderState.Starting);
            _job = job with { Rect = job.Rect.Even() };
            _segments.Clear();
            Notes = null;
            _tempDir = Path.Combine(AppPaths.DataDir, "rec-temp", DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(_tempDir);

            _effectiveEncoder = job.Encoder;
            if (_effectiveEncoder != "x264" && !await EncoderProbe.IsUsableAsync(_ffmpeg, _effectiveEncoder))
            {
                Notes = $"انکودر {EncoderProbe.Label(job.Encoder)} در دسترس نبود؛ از x264 استفاده شد.";
                _effectiveEncoder = "x264";
            }
            _effectiveDda = job.UseDesktopDuplication && job.DdaOutputIndex.HasValue;

            if (job.SystemAudio) _systemAudio = AudioTrackRecorder.TryCreateSystem(Path.Combine(_tempDir, "system.wav"));
            if (job.Microphone) _micAudio = AudioTrackRecorder.TryCreateMicrophone(Path.Combine(_tempDir, "mic.wav"));
            if (job.Microphone && _micAudio == null) Notes = AppendNote(Notes, "میکروفون پیدا نشد؛ ضبط بدون میکروفون انجام می‌شود.");

            await StartSegmentAsync();

            _systemAudio?.Start();
            _micAudio?.Start();
            _elapsed.Restart();
            SetState(RecorderState.Recording);
        }
        catch
        {
            await CleanupAfterFailureAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    private static string AppendNote(string? existing, string add) => string.IsNullOrEmpty(existing) ? add : existing + " " + add;

    private async Task StartSegmentAsync()
    {
        var job = _job!;
        string seg = Path.Combine(_tempDir, $"seg{_segments.Count:D3}.mkv");

        // Try the preferred configuration first, then fall back step by step.
        var attempts = new List<(bool Dda, string Encoder)> { (_effectiveDda, _effectiveEncoder) };
        if (_effectiveDda) attempts.Add((false, _effectiveEncoder));
        if (_effectiveEncoder != "x264") attempts.Add((false, "x264"));

        string lastError = string.Empty;
        foreach (var (dda, enc) in attempts.Distinct())
        {
            try { if (File.Exists(seg)) File.Delete(seg); } catch { }
            var proc = FfmpegProcess.Start(_ffmpeg, BuildCaptureArgs(job, dda, enc, seg));
            if (await proc.WaitStartedAsync(TimeSpan.FromSeconds(8)))
            {
                if (dda != _effectiveDda) Notes = AppendNote(Notes, "ضبط با Desktop Duplication شروع نشد؛ از روش GDI استفاده شد.");
                if (enc != _effectiveEncoder) Notes = AppendNote(Notes, "انکودر سخت‌افزاری شروع نشد؛ از x264 استفاده شد.");
                _effectiveDda = dda;
                _effectiveEncoder = enc;
                _current = proc;
                _segments.Add(seg);
                MonitorSegment(proc);
                return;
            }
            lastError = proc.Tail;
            proc.Dispose();
        }
        AppLog.Warn("ffmpeg capture failed:\n" + lastError);
        throw new InvalidOperationException("FFmpeg نتوانست ضبط را شروع کند. " + LastMeaningfulLine(lastError));
    }

    private void MonitorSegment(FfmpegProcess proc)
    {
        _ = Task.Run(async () =>
        {
            try { await proc.WaitExitAsync(); } catch { return; }
            if (ReferenceEquals(proc, _current) && State == RecorderState.Recording)
            {
                AppLog.Warn("ffmpeg segment exited unexpectedly:\n" + proc.Tail);
                Failed?.Invoke("ضبط به‌طور غیرمنتظره متوقف شد: " + LastMeaningfulLine(proc.Tail));
            }
        });
    }

    private static string LastMeaningfulLine(string tail)
    {
        var line = tail.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("frame=", StringComparison.Ordinal));
        return line ?? string.Empty;
    }

    private static List<string> BuildCaptureArgs(RecordingJob job, bool dda, string encoder, string output)
    {
        var r = job.Rect;
        string fps = job.Fps.ToString(CultureInfo.InvariantCulture);
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "info", "-y" };
        if (dda)
        {
            // Desktop Duplication captures one output; offsets are relative to that monitor.
            var mon = RecorderNative.GetMonitors().FirstOrDefault(m => m.Bounds.Intersect(r).Width > 0);
            int ox = mon != null ? r.X - mon.Bounds.X : 0, oy = mon != null ? r.Y - mon.Bounds.Y : 0;
            a.AddRange(new[] { "-f", "lavfi", "-i",
                $"ddagrab=output_idx={job.DdaOutputIndex ?? 0}:framerate={fps}:draw_mouse={(job.DrawMouse ? 1 : 0)}:offset_x={ox}:offset_y={oy}:video_size={r.Width}x{r.Height}" });

        }
        else
        {
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", fps,
                "-draw_mouse", job.DrawMouse ? "1" : "0",
                "-offset_x", r.X.ToString(CultureInfo.InvariantCulture), "-offset_y", r.Y.ToString(CultureInfo.InvariantCulture),
                "-video_size", $"{r.Width}x{r.Height}", "-i", "desktop" });
        }

        if (!string.IsNullOrWhiteSpace(job.CameraDevice))
        {
            int maxW = Math.Max(2, r.Width * job.CameraPercent / 200 * 2);
            int maxH = Math.Max(2, r.Height * job.CameraPercent / 200 * 2);
            string x = job.CameraCorner % 2 == 0 ? "4" : "W-w-4";
            string y = job.CameraCorner < 2 ? "4" : "H-h-4";
            string screen = dda ? "[0:v]hwdownload,format=bgra[desktop];" : "";
            string input = dda ? "[desktop]" : "[0:v]";
            a.AddRange(new[] { "-thread_queue_size", "128", "-f", "dshow", "-i", "video=" + job.CameraDevice,
                "-filter_complex", $"{screen}[1:v]scale={maxW}:{maxH}:force_original_aspect_ratio=decrease,setsar=1[cam];{input}[cam]overlay={x}:{y}:eof_action=pass[out]",
                "-map", "[out]" });
        }
        else if (dda) a.AddRange(new[] { "-vf", "hwdownload,format=bgra" });

        int q = Math.Clamp(job.Quality, 10, 100);
        int crf = (int)Math.Round(36 - q * 0.2);           // 70% → 22
        switch (encoder)
        {
            case "h264_nvenc":
                a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-pix_fmt", "yuv420p" });
                break;
            case "h264_qsv":
                a.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "faster", "-global_quality", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "nv12" });
                break;
            case "h264_amf":
                a.AddRange(new[] { "-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp",
                    "-qp_i", crf.ToString(CultureInfo.InvariantCulture), "-qp_p", (crf + 2).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p" });
                break;
            default:
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p" });
                break;
        }
        a.AddRange(new[] { "-g", (job.Fps * 2).ToString(CultureInfo.InvariantCulture), "-an", output });
        return a;
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecorderState.Recording || _current == null) return;
            _elapsed.Stop();
            _systemAudio?.SetPaused(true);
            _micAudio?.SetPaused(true);
            var proc = _current;
            _current = null;
            SetState(RecorderState.Paused);
            await proc.StopGracefullyAsync(TimeSpan.FromSeconds(8));
            proc.Dispose();
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecorderState.Paused) return;
            SetState(RecorderState.Starting);
            await StartSegmentAsync();
            _systemAudio?.SetPaused(false);
            _micAudio?.SetPaused(false);
            _elapsed.Start();
            SetState(RecorderState.Recording);
        }
        catch
        {
            SetState(RecorderState.Paused);
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops, finalizes and returns the output path (null if nothing usable was recorded).</summary>
    public async Task<string?> StopAsync(IProgress<string>? progress = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (State is RecorderState.Idle or RecorderState.Finalizing || _job == null) return null;
            SetState(RecorderState.Finalizing);
            _elapsed.Stop();
            progress?.Report("در حال بستن فایل ضبط…");
            if (_current != null)
            {
                await _current.StopGracefullyAsync(TimeSpan.FromSeconds(10));
                _current.Dispose();
                _current = null;
            }
            var audio = new List<AudioTrackRecorder>();
            foreach (var t in new[] { _systemAudio, _micAudio })
            {
                if (t == null) continue;
                await t.StopAsync();
                if (t.HasData) audio.Add(t);
            }

            var segments = _segments.Where(s => File.Exists(s) && new FileInfo(s).Length > 1024).ToList();
            if (segments.Count == 0) throw new InvalidOperationException("هیچ تصویری ضبط نشد.");

            progress?.Report("در حال ساخت فایل نهایی…");
            string video = segments[0];
            if (segments.Count > 1)
            {
                string list = Path.Combine(_tempDir, "concat.txt");
                var sb = new StringBuilder();
                foreach (var s in segments) sb.Append("file '").Append(s.Replace("'", "'\\''")).Append("'\n");
                await File.WriteAllTextAsync(list, sb.ToString(), new UTF8Encoding(false));
                video = Path.Combine(_tempDir, "joined.mkv");
                var (code, tail) = await FfmpegProcess.RunAsync(_ffmpeg,
                    new[] { "-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", video }, TimeSpan.FromMinutes(10));
                if (code != 0) throw new InvalidOperationException("اتصال بخش‌های ضبط ناموفق بود: " + LastMeaningfulLine(tail));
            }

            string output = _job.OutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var args = new List<string> { "-hide_banner", "-y", "-i", video };
            foreach (var t in audio) args.AddRange(new[] { "-i", t.FilePath });
            if (audio.Count == 2)
                args.AddRange(new[] { "-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=0[a]", "-map", "0:v", "-map", "[a]" });
            else if (audio.Count == 1)
                args.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
            args.AddRange(new[] { "-c:v", "copy" });
            if (audio.Count > 0) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-shortest" });
            if (_job.Container == "mp4") args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(output);
            var (muxCode, muxTail) = await FfmpegProcess.RunAsync(_ffmpeg, args, TimeSpan.FromMinutes(20));
            if (muxCode != 0 || !File.Exists(output))
            {
                // Keep the raw recording rather than losing it.
                string rescue = Path.ChangeExtension(output, ".mkv");
                if (rescue.Equals(output, StringComparison.OrdinalIgnoreCase)) rescue = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_raw.mkv");
                File.Copy(video, rescue, overwrite: true);
                AppLog.Warn("Final mux failed, kept raw video:\n" + muxTail);
                Notes = AppendNote(Notes, "ادغام صدا ناموفق بود؛ فایل ویدیویی بدون صدا ذخیره شد.");
                output = rescue;
            }
            return output;
        }
        finally
        {
            DisposeAudio();
            TryDeleteTemp();
            _job = null;
            SetState(RecorderState.Idle);
            _gate.Release();
        }
    }

    private async Task CleanupAfterFailureAsync()
    {
        try { _current?.Dispose(); } catch { }
        _current = null;
        foreach (var t in new[] { _systemAudio, _micAudio })
            if (t != null) { try { await t.StopAsync(); } catch { } }
        DisposeAudio();
        TryDeleteTemp();
        _job = null;
        SetState(RecorderState.Idle);
    }

    private void DisposeAudio()
    {
        _systemAudio?.Dispose();
        _micAudio?.Dispose();
        _systemAudio = _micAudio = null;
    }

    private void TryDeleteTemp()
    {
        if (string.IsNullOrEmpty(_tempDir)) return;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { AppLog.Warn("Recorder temp cleanup failed: " + ex.Message); }
        _tempDir = string.Empty;
    }

    /// <summary>Removes leftovers from crashed sessions (older than a day).</summary>
    public static void CleanupStaleTemp()
    {
        try
        {
            string root = Path.Combine(AppPaths.DataDir, "rec-temp");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root))
                if (DateTime.Now - Directory.GetCreationTime(d) > TimeSpan.FromDays(1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }
}
