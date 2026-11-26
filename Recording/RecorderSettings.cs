using System.Text.Json;
using Shamsa.Services;

namespace Shamsa.Recording;

public enum CaptureMode { FullScreen = 0, Region = 1, Window = 2, LastRegion = 3 }
public enum ScreenshotMode { FullScreen = 0, Region = 1, ActiveWindow = 2 }

public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public int Key { get; set; }
    public HotkeyBinding() { }
    public HotkeyBinding(uint modifiers, int key) { Modifiers = modifiers; Key = key; }
}

/// <summary>Recorder preferences persisted to %LocalAppData%\Shamsa\recorder.json.</summary>
public sealed class RecorderSettings
{
    public int Version { get; set; } = 1;
    public CaptureMode Mode { get; set; } = CaptureMode.FullScreen;
    /// <summary>Index into System.Windows.Forms.Screen.AllScreens.</summary>
    public int MonitorIndex { get; set; }
    /// <summary>Last region in physical screen pixels.</summary>
    public int LastRegionX { get; set; }
    public int LastRegionY { get; set; }
    public int LastRegionW { get; set; }
    public int LastRegionH { get; set; }

    public int Fps { get; set; } = 30;
    /// <summary>10..100; mapped to CRF/CQ (higher = better quality).</summary>
    public int Quality { get; set; } = 70;
    /// <summary>x264, h264_nvenc, h264_qsv, h264_amf</summary>
    public string Encoder { get; set; } = "x264";
    /// <summary>mp4 or mkv</summary>
    public string Container { get; set; } = "mp4";
    public bool UseDesktopDuplication { get; set; }
    public bool DrawMouse { get; set; } = true;
    public string LeftClickColor { get; set; } = "#F6D379";
    public string RightClickColor { get; set; } = "#2DD4BF";
    public string CameraDevice { get; set; } = "";
    public int CameraCorner { get; set; } = 3;
    public int CameraPercent { get; set; } = 24;
    public bool HighlightClicks { get; set; }
    public bool Countdown { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    public bool ShowToolbar { get; set; } = true;

    public string OutputFolder { get; set; } = string.Empty;
    public string ScreenshotFolder { get; set; } = string.Empty;
    public ScreenshotMode ScreenshotMode { get; set; } = ScreenshotMode.FullScreen;
    public bool ScreenshotJpg { get; set; }
    public bool ScreenshotToClipboard { get; set; } = true;

    public bool HotkeysEnabled { get; set; } = true;
    public HotkeyBinding HotkeyRecord { get; set; } = new(0x0002 | 0x0001, 0x52); // Ctrl+Alt+R
    public HotkeyBinding HotkeyPause { get; set; } = new(0x0002 | 0x0001, 0x50);  // Ctrl+Alt+P
    public HotkeyBinding HotkeyShot { get; set; } = new(0x0002 | 0x0001, 0x53);   // Ctrl+Alt+S

    public bool VoiceCommandsEnabled { get; set; }

    public List<string> RecentRecordings { get; set; } = new();

    public string EffectiveOutputFolder => string.IsNullOrWhiteSpace(OutputFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Shamsa Recordings")
        : OutputFolder;

    public string EffectiveScreenshotFolder => string.IsNullOrWhiteSpace(ScreenshotFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Shamsa Screenshots")
        : ScreenshotFolder;

    public void Normalize()
    {
        CameraCorner = Math.Clamp(CameraCorner, 0, 3);
        CameraPercent = Math.Clamp(CameraPercent, 12, 45);
        if (!ThemeService.TryParseAccent(LeftClickColor, out _)) LeftClickColor = "#F6D379";
        if (!ThemeService.TryParseAccent(RightClickColor, out _)) RightClickColor = "#2DD4BF";
        Fps = Fps is 15 or 24 or 30 or 60 ? Fps : 30;
        Quality = Math.Clamp(Quality, 10, 100);
        Encoder = Encoder is "x264" or "h264_nvenc" or "h264_qsv" or "h264_amf" ? Encoder : "x264";
        Container = Container is "mp4" or "mkv" ? Container : "mp4";
        HotkeyRecord ??= new(0x0003, 0x52);
        HotkeyPause ??= new(0x0003, 0x50);
        HotkeyShot ??= new(0x0003, 0x53);
        RecentRecordings ??= new();
        if (RecentRecordings.Count > 30) RecentRecordings = RecentRecordings.Take(30).ToList();
    }
}

public static class RecorderSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "recorder.json");
    public static RecorderSettings Current { get; private set; } = new();
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<RecorderSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex)
        {
            AppLog.Error("Recorder settings load failed; using defaults", ex);
            Current = new();
        }
        Current.Normalize();
    }

    public static void Save()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Recorder settings save failed", ex);
        }
        Changed?.Invoke();
    }

    public static void Update(Action<RecorderSettings> change)
    {
        change(Current);
        Current.Normalize();
        Save();
    }

    public static void AddRecent(string path)
    {
        Update(s =>
        {
            s.RecentRecordings.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            s.RecentRecordings.Insert(0, path);
        });
    }
}
