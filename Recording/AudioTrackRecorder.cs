using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Shamsa.Services;

namespace Shamsa.Recording;

/// <summary>
/// Records one WASAPI stream (system loopback or microphone) to a WAV file that stays in sync with
/// wall-clock recording time: WASAPI loopback delivers nothing while the system is silent, so gaps
/// are filled with silence; samples arriving while paused are discarded and the clock is stopped.
/// </summary>
internal sealed class AudioTrackRecorder : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly WaveFileWriter _writer;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private readonly int _bytesPerSecond;
    private readonly int _blockAlign;
    private long _written;
    private volatile bool _paused;
    private bool _stopped;
    private readonly TaskCompletionSource _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string FilePath { get; }
    public string Label { get; }
    public bool HasData => _written > _blockAlign * 100;

    private AudioTrackRecorder(WasapiCapture capture, string path, string label)
    {
        _capture = capture;
        FilePath = path;
        Label = label;
        _writer = new WaveFileWriter(path, capture.WaveFormat);
        _bytesPerSecond = capture.WaveFormat.AverageBytesPerSecond;
        _blockAlign = Math.Max(1, capture.WaveFormat.BlockAlign);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) AppLog.Warn($"{Label} audio stopped with error: {e.Exception.Message}");
            _stoppedTcs.TrySetResult();
        };
    }

    public static AudioTrackRecorder? TryCreateSystem(string path)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            return new AudioTrackRecorder(new WasapiLoopbackCapture(), path, "System");
        }
        catch (Exception ex)
        {
            AppLog.Warn("System audio capture unavailable: " + ex.Message);
            return null;
        }
    }

    public static AudioTrackRecorder? TryCreateMicrophone(string path)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) return null;
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return new AudioTrackRecorder(new WasapiCapture(device), path, "Microphone");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Microphone capture unavailable: " + ex.Message);
            return null;
        }
    }

    public void Start()
    {
        _clock.Start();
        _capture.StartRecording();
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (paused == _paused) return;
            if (paused) { PadToClock(); _clock.Stop(); }
            else _clock.Start();
            _paused = paused;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_stopped || _paused || e.BytesRecorded <= 0) return;
            // Fill a silence gap (loopback is silent-by-absence) before appending fresh samples.
            long expectedBeforeChunk = ExpectedBytes() - e.BytesRecorded;
            if (expectedBeforeChunk - _written > _bytesPerSecond / 10) WriteSilence(expectedBeforeChunk - _written);
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _written += e.BytesRecorded;
        }
    }

    private long ExpectedBytes()
    {
        long bytes = (long)(_clock.Elapsed.TotalSeconds * _bytesPerSecond);
        return bytes - bytes % _blockAlign;
    }

    private void PadToClock()
    {
        long missing = ExpectedBytes() - _written;
        if (missing > _blockAlign) WriteSilence(missing);
    }

    private void WriteSilence(long bytes)
    {
        bytes -= bytes % _blockAlign;
        if (bytes <= 0) return;
        var zero = new byte[Math.Min(bytes, 65536)];
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, zero.Length);
            _writer.Write(zero, 0, n);
            _written += n;
            bytes -= n;
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (!_paused) PadToClock();
            _stopped = true;
            _clock.Stop();
        }
        try { _capture.StopRecording(); } catch { }
        await Task.WhenAny(_stoppedTcs.Task, Task.Delay(2000));
        lock (_gate)
        {
            try { _writer.Flush(); _writer.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _capture.DataAvailable -= OnData; } catch { }
        try { _capture.Dispose(); } catch { }
        lock (_gate)
        {
            try { _writer.Dispose(); } catch { }
        }
    }
}
