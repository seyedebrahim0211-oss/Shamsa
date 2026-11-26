using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shamsa.Desktop.Shell;
using Shamsa.Services;

namespace Shamsa.Desktop.Launcher;

public enum ClipKind { Text, Files, Image }

/// <summary>One entry of the clipboard history.</summary>
public sealed class ClipItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public string[]? Files { get; init; }
    /// <summary>Full image (frozen) for pasting.</summary>
    public BitmapSource? Image { get; init; }
    /// <summary>Small frozen thumbnail for the list.</summary>
    public BitmapSource? Thumbnail { get; init; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }

    internal string Fingerprint => Kind switch
    {
        ClipKind.Text => "T:" + Text,
        ClipKind.Files => "F:" + string.Join("|", Files ?? Array.Empty<string>()),
        _ => "I:" + Image?.PixelWidth + "x" + Image?.PixelHeight + ":" + ImageHash
    };

    internal string ImageHash { get; init; } = "";

    /// <summary>Single-line preview for the launcher list.</summary>
    public string Preview => Kind switch
    {
        ClipKind.Text => Collapse(Text ?? ""),
        ClipKind.Files => Files is { Length: 1 } ? Path.GetFileName(Files[0]) : $"{DesktopTheme.Digits((Files?.Length ?? 0).ToString(), true)} فایل: " +
                          string.Join("، ", (Files ?? Array.Empty<string>()).Take(3).Select(Path.GetFileName)),
        _ => $"تصویر {DesktopTheme.Digits($"{Image?.PixelWidth}×{Image?.PixelHeight}", true)}"
    };

    private static string Collapse(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}

/// <summary>
/// System-wide clipboard history for the launcher. Listens with AddClipboardFormatListener on a
/// message-only window, honours the "don't record me" formats password managers set, never logs
/// contents, keeps everything in memory unless the user opts in to a DPAPI-encrypted file.
/// UI thread only.
/// </summary>
public static class ClipboardHistory
{
    private static HwndSource? _sink;
    private static ClipboardSettings _settings = new();
    private static readonly List<ClipItem> ItemsList = new();
    private static int _ignoreUpdates;
    private static DispatcherTimer? _captureTimer;
    private static DispatcherTimer? _saveTimer;

    public static event Action? Changed;
    public static bool IsRunning => _sink != null;
    public static IReadOnlyList<ClipItem> Items => ItemsList;
    private static string StoreFile => Path.Combine(AppPaths.DataDir, "clipboard-history.bin");

    public static void Start(ClipboardSettings settings)
    {
        _settings = settings;
        if (_sink == null)
        {
            if (settings.PersistAcrossRestarts) LoadPersisted();
            _sink = new HwndSource(new HwndSourceParameters("ShamsaClipboardHistory")
            {
                Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            });
            _sink.AddHook(WndProc);
            if (!ClipboardNative.AddClipboardFormatListener(_sink.Handle))
                AppLog.Warn("Clipboard listener could not be registered");
        }
        Trim();
        if (!settings.PersistAcrossRestarts) DeletePersisted();
        Changed?.Invoke();
    }

    public static void Stop()
    {
        if (_sink != null)
        {
            try { ClipboardNative.RemoveClipboardFormatListener(_sink.Handle); } catch { }
            _sink.RemoveHook(WndProc);
            _sink.Dispose();
            _sink = null;
        }
        _captureTimer?.Stop();
        if (_settings.PersistAcrossRestarts) SaveNow();
        else ItemsList.Clear();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == ClipboardNative.WM_CLIPBOARDUPDATE)
        {
            if (_ignoreUpdates > 0) { _ignoreUpdates--; return IntPtr.Zero; }
            // Read slightly later: the owner may still be writing formats / holding the clipboard open.
            _captureTimer ??= CreateCaptureTimer();
            _captureTimer.Stop();
            _captureTimer.Start();
        }
        return IntPtr.Zero;
    }

    private static DispatcherTimer CreateCaptureTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); Capture(attempt: 0); };
        return t;
    }

    private static bool IsIgnoredSource()
    {
        foreach (var hwnd in new[] { ClipboardNative.GetClipboardOwner(), ClipboardNative.GetForegroundWindow() })
        {
            if (hwnd == IntPtr.Zero) continue;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            string? exe = ShellInterop.GetProcessPath(pid);
            if (exe == null) continue;
            string name = Path.GetFileName(exe);
            if (_settings.IgnoredProcesses.Any(p => string.Equals(p.Trim(), name, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static void Capture(int attempt)
    {
        try
        {
            if (IsIgnoredSource()) return;
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            // Formats password managers / sensitive apps set to opt out of clipboard history & monitors.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") && ReadDword(data.GetData("CanIncludeInClipboardHistory")) == 0) return;

            ClipItem? item = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                item = new ClipItem { Kind = ClipKind.Files, Files = files };
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            {
                if (text.Length > 200_000) text = text[..200_000];
                item = new ClipItem { Kind = ClipKind.Text, Text = text };
            }
            else if (_settings.KeepImages && data.GetDataPresent(DataFormats.Bitmap))
            {
                var img = Clipboard.GetImage();
                if (img != null && (long)img.PixelWidth * img.PixelHeight <= 8_000_000)
                    item = MakeImageItem(img);
            }
            if (item != null) Add(item);
        }
        catch (System.Runtime.InteropServices.COMException) when (attempt < 3)
        {
            // Clipboard busy: retry a little later.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150 * (attempt + 1)) };
            t.Tick += (_, _) => { t.Stop(); Capture(attempt + 1); };
            t.Start();
        }
        catch (Exception ex) { AppLog.Warn("Clipboard capture skipped: " + ex.GetType().Name); }
    }

    private static int ReadDword(object? value)
    {
        try
        {
            if (value is MemoryStream ms && ms.Length >= 4) { var b = ms.ToArray(); return BitConverter.ToInt32(b, 0); }
            if (value is int i) return i;
        }
        catch { }
        return 1;
    }

    private static ClipItem MakeImageItem(BitmapSource img)
    {
        BitmapSource full = img;
        if (full.Format != System.Windows.Media.PixelFormats.Bgra32 && full.Format != System.Windows.Media.PixelFormats.Pbgra32)
            full = new FormatConvertedBitmap(full, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        if (!full.IsFrozen) full.Freeze();
        double scale = Math.Min(1.0, 96.0 / Math.Max(full.PixelWidth, full.PixelHeight));
        var thumb = new TransformedBitmap(full, new System.Windows.Media.ScaleTransform(scale, scale));
        thumb.Freeze();
        return new ClipItem { Kind = ClipKind.Image, Image = full, Thumbnail = thumb, ImageHash = HashPixels(full) };
    }

    private static string HashPixels(BitmapSource bmp)
    {
        try
        {
            int stride = bmp.PixelWidth * 4;
            // Sample a few rows only — enough to tell screenshots apart cheaply.
            var row = new byte[stride];
            ulong h = 1469598103934665603;
            for (int y = 0; y < bmp.PixelHeight; y += Math.Max(1, bmp.PixelHeight / 16))
            {
                bmp.CopyPixels(new Int32Rect(0, y, bmp.PixelWidth, 1), row, stride, 0);
                for (int i = 0; i < row.Length; i += 7) { h ^= row[i]; h *= 1099511628211; }
            }
            return h.ToString("X");
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    // ------------------------------------------------------------------ list operations

    public static void Add(ClipItem item)
    {
        var existing = ItemsList.FirstOrDefault(x => x.Fingerprint == item.Fingerprint);
        if (existing != null)
        {
            ItemsList.Remove(existing);
            existing.CapturedUtc = DateTime.UtcNow;
            ItemsList.Insert(0, existing);
        }
        else ItemsList.Insert(0, item);
        Trim();
        ScheduleSave();
        Changed?.Invoke();
    }

    private static void Trim()
    {
        int max = Math.Clamp(_settings.MaxItems, 10, 200);
        while (ItemsList.Count > max)
        {
            var victim = ItemsList.LastOrDefault(x => !x.Pinned);
            if (victim == null) break;
            ItemsList.Remove(victim);
        }
        if (!_settings.KeepImages) ItemsList.RemoveAll(x => x.Kind == ClipKind.Image && !x.Pinned);
    }

    public static void Remove(ClipItem item) { ItemsList.Remove(item); ScheduleSave(); Changed?.Invoke(); }

    public static void TogglePin(ClipItem item) { item.Pinned = !item.Pinned; ScheduleSave(); Changed?.Invoke(); }

    public static void Clear(bool keepPinned = true)
    {
        ItemsList.RemoveAll(x => !keepPinned || !x.Pinned);
        ScheduleSave();
        if (!keepPinned) DeletePersisted();
        Changed?.Invoke();
    }

    /// <summary>Pinned first, then newest; filtered by a normalised substring/subsequence match.</summary>
    public static List<ClipItem> Search(string query)
    {
        string q = LauncherIndex.Normalize(query.Trim());
        IEnumerable<ClipItem> src = ItemsList;
        if (q.Length > 0)
            src = src.Where(x => LauncherIndex.Normalize(x.Kind == ClipKind.Files ? string.Join(" ", x.Files ?? Array.Empty<string>()) : x.Kind == ClipKind.Text ? x.Text ?? "" : "تصویر image").Contains(q));
        return src.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.CapturedUtc).ToList();
    }

    /// <summary>Puts an item back on the clipboard (without re-recording it).</summary>
    public static bool CopyToClipboard(ClipItem item)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                _ignoreUpdates++;
                switch (item.Kind)
                {
                    case ClipKind.Text: Clipboard.SetText(item.Text ?? ""); break;
                    case ClipKind.Files:
                        var sc = new StringCollection();
                        sc.AddRange(item.Files ?? Array.Empty<string>());
                        Clipboard.SetFileDropList(sc);
                        break;
                    case ClipKind.Image when item.Image != null: Clipboard.SetImage(item.Image); break;
                }
                // Move to the top as the most recent.
                ItemsList.Remove(item);
                item.CapturedUtc = DateTime.UtcNow;
                ItemsList.Insert(0, item);
                Changed?.Invoke();
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                _ignoreUpdates = Math.Max(0, _ignoreUpdates - 1);
                Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                _ignoreUpdates = Math.Max(0, _ignoreUpdates - 1);
                AppLog.Warn("Clipboard set failed: " + ex.GetType().Name);
                return false;
            }
        }
        return false;
    }

    /// <summary>Copies the item, focuses <paramref name="target"/> and sends Ctrl+V.</summary>
    public static void Paste(ClipItem item, IntPtr target)
    {
        if (!CopyToClipboard(item)) return;
        if (target == IntPtr.Zero || !ClipboardNative.IsWindow(target)) return;
        ShellInterop.Activate(target);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try { if (ClipboardNative.GetForegroundWindow() == target) ClipboardNative.SendCtrlV(); } catch { }
        };
        t.Start();
    }

    // ------------------------------------------------------------------ optional encrypted persistence

    private sealed record Stored(string Kind, string? Text, string[]? Files, string? Png, DateTime At, bool Pinned);

    private static void ScheduleSave()
    {
        if (!_settings.PersistAcrossRestarts) return;
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static DispatcherTimer CreateSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) => { t.Stop(); SaveNow(); };
        return t;
    }

    private static void SaveNow()
    {
        if (!_settings.PersistAcrossRestarts) return;
        try
        {
            var list = ItemsList.Select(x => new Stored(x.Kind.ToString(), x.Text, x.Files, x.Kind == ClipKind.Image && x.Image != null ? ToPngBase64(x.Image) : null, x.CapturedUtc, x.Pinned)).ToList();
            var plain = JsonSerializer.SerializeToUtf8Bytes(list);
            var cipher = ClipboardNative.Protect(plain);
            Array.Clear(plain);
            if (cipher == null) return;
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllBytes(StoreFile, cipher);
        }
        catch (Exception ex) { AppLog.Warn("Clipboard history not saved: " + ex.GetType().Name); }
    }

    private static void LoadPersisted()
    {
        try
        {
            if (!File.Exists(StoreFile)) return;
            var plain = ClipboardNative.Unprotect(File.ReadAllBytes(StoreFile));
            if (plain == null) return;
            var list = JsonSerializer.Deserialize<List<Stored>>(plain) ?? new();
            Array.Clear(plain);
            ItemsList.Clear();
            foreach (var s in list)
            {
                if (!Enum.TryParse<ClipKind>(s.Kind, out var kind)) continue;
                ClipItem? item = kind switch
                {
                    ClipKind.Text when s.Text != null => new ClipItem { Kind = kind, Text = s.Text },
                    ClipKind.Files when s.Files != null => new ClipItem { Kind = kind, Files = s.Files },
                    ClipKind.Image when s.Png != null => FromPng(s.Png),
                    _ => null
                };
                if (item == null) continue;
                item.CapturedUtc = s.At;
                item.Pinned = s.Pinned;
                ItemsList.Add(item);
            }
        }
        catch (Exception ex) { AppLog.Warn("Clipboard history not loaded: " + ex.GetType().Name); }
    }

    private static void DeletePersisted()
    {
        try { if (File.Exists(StoreFile)) File.Delete(StoreFile); } catch { }
    }

    private static string ToPngBase64(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static ClipItem? FromPng(string b64)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(Convert.FromBase64String(b64));
            bmp.EndInit();
            bmp.Freeze();
            return MakeImageItem(bmp);
        }
        catch { return null; }
    }
}
