using System.Diagnostics;
using System.Text;
using Shamsa.Services;

namespace Shamsa.Recording;

/// <summary>Hidden-console FFmpeg process with graceful "q" stop and a short stderr tail for diagnostics.</summary>
internal sealed class FfmpegProcess : IDisposable
{
    private readonly Process _process;
    private readonly Queue<string> _tail = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }
    public int ExitCode { get { try { return _process.ExitCode; } catch { return -1; } } }

    public string Tail
    {
        get { lock (_gate) return string.Join("\n", _tail); }
    }

    private FfmpegProcess(Process p) => _process = p;

    public static FfmpegProcess Start(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var fp = new FfmpegProcess(p);
        p.ErrorDataReceived += (_, e) => fp.OnLine(e.Data);
        p.OutputDataReceived += (_, e) => fp.OnLine(e.Data);
        p.Exited += (_, _) => fp._started.TrySetResult(false);
        AppLog.Info("ffmpeg " + string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        return fp;
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        if (line.Contains("Press [q]", StringComparison.Ordinal) || line.StartsWith("frame=", StringComparison.Ordinal))
            _started.TrySetResult(true);
        lock (_gate)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 25) _tail.Dequeue();
        }
    }

    /// <summary>True once FFmpeg starts processing; false if it exits (or times out) first.</summary>
    public async Task<bool> WaitStartedAsync(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_started.Task, Task.Delay(timeout));
        if (done == _started.Task) return _started.Task.Result && !HasExited;
        return !HasExited;
    }

    public async Task<int> WaitExitAsync(CancellationToken token = default)
    {
        await _process.WaitForExitAsync(token);
        return ExitCode;
    }

    /// <summary>Asks FFmpeg to finish the file ("q"), killing it only after the timeout.</summary>
    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        if (HasExited) return;
        try
        {
            await _process.StandardInput.WriteAsync("q");
            await _process.StandardInput.FlushAsync();
        }
        catch { }
        using var cts = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            AppLog.Warn("ffmpeg did not stop in time; killing");
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Kill()
    {
        try { if (!HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    /// <summary>Runs FFmpeg to completion (used for concat / mux / probes).</summary>
    public static async Task<(int ExitCode, string Tail)> RunAsync(string ffmpeg, IEnumerable<string> args, TimeSpan timeout)
    {
        using var p = Start(ffmpeg, args);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            int code = await p.WaitExitAsync(cts.Token);
            return (code, p.Tail);
        }
        catch (OperationCanceledException)
        {
            p.Kill();
            return (-1, "timeout\n" + p.Tail);
        }
    }
}

/// <summary>Detects which H.264 hardware encoders actually work on this PC (cached for the session).</summary>
internal static class EncoderProbe
{
    private static readonly Dictionary<string, bool> Cache = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static readonly string[] HardwareEncoders = { "h264_nvenc", "h264_qsv", "h264_amf" };

    public static async Task<bool> IsUsableAsync(string ffmpeg, string encoder)
    {
        if (encoder == "x264") return true;
        await Gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(encoder, out bool ok)) return ok;
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=320x240:d=0.3:r=30" };
            args.AddRange(new[] { "-c:v", encoder, "-pix_fmt", encoder == "h264_qsv" ? "nv12" : "yuv420p", "-f", "null", "-" });
            var (code, _) = await FfmpegProcess.RunAsync(ffmpeg, args, TimeSpan.FromSeconds(15));
            Cache[encoder] = code == 0;
            AppLog.Info($"Encoder probe {encoder}: {(code == 0 ? "ok" : "unavailable")}");
            return code == 0;
        }
        finally { Gate.Release(); }
    }

    public static async Task<List<string>> DetectAllAsync(string ffmpeg)
    {
        var list = new List<string> { "x264" };
        foreach (var enc in HardwareEncoders)
            if (await IsUsableAsync(ffmpeg, enc)) list.Add(enc);
        return list;
    }

    public static string Label(string encoder) => encoder switch
    {
        "h264_nvenc" => "NVIDIA NVENC (کارت گرافیک)",
        "h264_qsv" => "Intel Quick Sync (کارت گرافیک)",
        "h264_amf" => "AMD AMF (کارت گرافیک)",
        _ => "x264 (پردازنده)"
    };
}
