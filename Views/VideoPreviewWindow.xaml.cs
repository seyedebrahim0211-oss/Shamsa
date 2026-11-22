using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class VideoPreviewWindow : Window
{
    private readonly record struct CachedImage(DateTime WriteUtc, int DecodeMax, BitmapSource Bitmap, int Width, int Height);

    private static readonly ConcurrentDictionary<string, CachedImage> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    // کروم دقیق مطابق XAML (OuterGlassBorder: 16px + MediaFrame Border: 2px = 18px)
    private const double ChromeW = 18.0;
    private const double ChromeHImage = 57.0; // 18 + Footer(34 + 5)
    private const double ChromeHVideo = 65.0; // 18 + ScrubBar(3 + 5) + Footer(34 + 5)

    private static readonly System.Windows.Media.Brush VideoDotBrush = CreateFrozenBrush("#2DD4BF");
    private static readonly System.Windows.Media.Brush ImageDotBrush = CreateFrozenBrush("#F6D379");

    private readonly StaWorker _thumbWorker = new("Shamsa.ThumbSta");
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private IntPtr _hwnd;
    private string? _currentFile;
    private HoverTarget? _currentTarget;
    private int _currentMediaW;
    private int _currentMediaH;
    private bool _currentHasScrub;
    private bool _videoReady;
    private bool _videoAspectApplied;
    private bool _pendingShow;
    private bool _waitingForFirstFrame;
    private DateTime _pendingShowSince;
    private DateTime _videoPlayRequestedAt;
    private int _scrubTicks;
    private long _requestId;
    private volatile bool _isPausedOrStopping;
    private readonly object _vlcGate = new();
    private readonly DispatcherTimer _scrubTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _idleStopTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    public bool VideoAvailable => _mediaPlayer != null;
    public IntPtr PreviewHwnd => _hwnd;

    private static System.Windows.Media.Brush CreateFrozenBrush(string hex)
    {
        var b = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public VideoPreviewWindow()
    {
        InitializeComponent();

        try
        {
            Core.Initialize();
            _libVlc = new LibVLC(
                "--no-audio",
                "--input-repeat=65535",
                "--quiet",
                "--no-video-title-show",
                "--avcodec-hw=any",
                "--avcodec-fast",
                "--avcodec-threads=2",
                "--drop-late-frames",
                "--skip-frames",
                "--file-caching=30",
                "--no-sub-autodetect-file",
                "--no-spu",
                "--no-metadata-network-access");
            _mediaPlayer = new MediaPlayer(_libVlc) { Mute = true, EnableHardwareDecoding = true };
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            VlcVideoView.Loaded += (_, _) =>
            {
                VlcVideoView.MediaPlayer = _mediaPlayer;
                if (_hwnd != IntPtr.Zero) NativeMethods.DisableChildWindowsInput(_hwnd);
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("LibVLC failed to initialize — video preview disabled, image preview still works", ex);
            _mediaPlayer = null;
        }

        _scrubTimer.Tick += ScrubTimer_Tick;
        _idleStopTimer.Tick += (_, _) =>
        {
            _idleStopTimer.Stop();
            if (!IsVisible && !_pendingShow)
                ReleaseVlcMediaAsync();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeClickThroughToolWindow(_hwnd);

            bool glassOk = NativeMethods.TryEnableGlassBackdrop(_hwnd);
            var src = HwndSource.FromHwnd(_hwnd);
            if (glassOk)
            {
                Background = System.Windows.Media.Brushes.Transparent;
                if (src?.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            }
            else
            {
                NativeMethods.TryRoundCorners(_hwnd);
                OuterGlassBorder.Background = CreateFrozenBrush("#F00A1326");
            }

            src?.AddHook(WndProc);
        };
    }

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (!_waitingForFirstFrame || _isPausedOrStopping) return;
        _waitingForFirstFrame = false;
        long req = _requestId;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => RevealLiveVideo(req));
    }

    private void RevealLiveVideo(long req)
    {
        if (req != _requestId || _isPausedOrStopping) return;
        _waitingForFirstFrame = false;
        VlcVideoView.Visibility = Visibility.Visible;
        if (_hwnd != IntPtr.Zero) NativeMethods.DisableChildWindowsInput(_hwnd);
        ImagePreview.Visibility = Visibility.Collapsed;
        ImagePreview.Source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            handled = true;
            return NativeMethods.HTTRANSPARENT;
        }
        return IntPtr.Zero;
    }

    public void WarmUp()
    {
        ApplyDefaultSize();
        Left = -20000;
        Top = -20000;
        Show();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_hwnd != IntPtr.Zero) NativeMethods.DisableChildWindowsInput(_hwnd);
            Hide();
            _videoReady = true;
        });
    }

    public void ApplySize()
    {
        if (IsVisible && _currentTarget != null && _currentMediaW > 0 && _currentMediaH > 0)
        {
            var (tw, th) = ComputeWindowSizeForAspect(_currentMediaW, _currentMediaH, _currentHasScrub);
            ApplyBoundsAtomic(_currentTarget, tw, th);
        }
        else if (!IsVisible)
        {
            ApplyDefaultSize();
        }
    }

    private double MaxPreviewSide => Math.Clamp(SettingsService.Current.PreviewMaxSidePx, 150, 560);

    private void ApplyDefaultSize()
    {
        double w = MaxPreviewSide;
        double h = Math.Round(w * 9.0 / 16.0);
        Width = w + ChromeW;
        Height = h + ChromeHVideo;
    }

    /// <summary>
    /// محاسبه دقیق ابعاد پنجره به طوری که کادر داخلی (MediaFrame) در هر دو حالت افقی و عمودی
    /// دقیقاً هم‌نسبت با ابعاد واقعی فیلم/عکس باشد و هیچ حاشیه سیاه (Letterbox/Pillarbox) ایجاد نشود.
    /// </summary>
    private (double TargetW, double TargetH) ComputeWindowSizeForAspect(int mediaW, int mediaH, bool hasScrubBar)
    {
        if (mediaW <= 0 || mediaH <= 0) return (Width, Height);

        double maxSide = MaxPreviewSide;
        double aspect = (double)mediaW / mediaH;
        double contentW, contentH;

        if (aspect < 1.0)
        {
            // عمودی (Portrait) — ارتفاع مساوی اندازه انتخابی و عرض دقیقاً متناسب با نسبت واقعی فیلم
            contentH = maxSide;
            contentW = contentH * aspect;

            // حفظ حداقل عرض خوانا بدون برهم زدن نسبت تصویر
            const double minPortraitW = 98.0;
            if (contentW < minPortraitW)
            {
                contentW = minPortraitW;
                contentH = contentW / aspect;
            }
        }
        else
        {
            // افقی (Landscape) یا مربعی — عرض مساوی اندازه انتخابی و ارتفاع دقیقاً متناسب با نسبت واقعی فیلم
            contentW = maxSide;
            contentH = contentW / aspect;

            const double minLandscapeH = 86.0;
            if (contentH < minLandscapeH)
            {
                contentH = minLandscapeH;
                contentW = contentH * aspect;
            }
        }

        double chromeH = hasScrubBar ? ChromeHVideo : ChromeHImage;

        // محدود کردن به حداکثر ۷۲٪ فضای قابل استفاده مانیتور با حفظ دقیق نسبت تصویر هر دو محور
        if (_currentTarget != null)
        {
            var area = ScreenHelper.WorkAreaDipAt(this, _currentTarget.CursorPos);
            double maxContentW = Math.Max(110, area.Width * 0.72 - ChromeW);
            double maxContentH = Math.Max(110, area.Height * 0.72 - chromeH);

            double scale = 1.0;
            if (contentW > maxContentW && contentW > 0)
                scale = Math.Min(scale, maxContentW / contentW);
            if (contentH > maxContentH && contentH > 0)
                scale = Math.Min(scale, maxContentH / contentH);

            if (scale < 1.0)
            {
                contentW *= scale;
                contentH *= scale;
            }
        }

        double finalContentW = Math.Max(80, Math.Round(contentW));
        double finalContentH = Math.Max(72, Math.Round(contentH));

        double targetW = finalContentW + ChromeW;
        double targetH = finalContentH + chromeH;

        // تطبیق ظریف اندازه فونت نوار اطلاعات برای حالت‌های خیلی کوچک (Mini)
        bool isMini = targetW < 185;
        FileNameText.FontSize = isMini ? 10.0 : 11.0;
        FileInfoText.FontSize = isMini ? 9.0 : 10.0;

        return (targetW, targetH);
    }

    private Point ApplyBoundsAtomic(HoverTarget target, double targetW, double targetH)
    {
        BeginAnimation(TopProperty, null);

        var pos = ComputeBestPosition(target, targetW, targetH);
        Width = targetW;
        Height = targetH;
        Left = pos.X;
        Top = pos.Y;

        if (_hwnd != IntPtr.Zero)
        {
            var dpi = ScreenHelper.GetDpi(this);
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOPMOST,
                (int)Math.Round(pos.X * dpi.DpiScaleX),
                (int)Math.Round(pos.Y * dpi.DpiScaleY),
                (int)Math.Round(targetW * dpi.DpiScaleX),
                (int)Math.Round(targetH * dpi.DpiScaleY),
                NativeMethods.SWP_NOACTIVATE);
        }

        return pos;
    }

    private void PlayEntranceAnimation(double finalTop)
    {
        if (!SettingsService.Current.DonutAnimations)
        {
            CardRoot.Opacity = 1;
            CardScale.ScaleX = 1;
            CardScale.ScaleY = 1;
            ImagePreview.Opacity = 1;
            ImageScale.ScaleX = 1;
            ImageScale.ScaleY = 1;
            return;
        }

        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(155);

        Top = finalTop;
        var slideAnim = new DoubleAnimation(finalTop + 8, finalTop, dur)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(TopProperty, slideAnim);

        var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(135)) { EasingFunction = ease };
        CardRoot.BeginAnimation(OpacityProperty, fadeAnim);

        var scaleAnim = new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease };
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnim);

        if (ImagePreview.Visibility == Visibility.Visible)
        {
            var imgScale = new DoubleAnimation(1.035, 1.0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
            ImageScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, imgScale);
            ImageScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, imgScale);
        }
    }

    public void ShowPreview(HoverTarget target)
    {
        _idleStopTimer.Stop();
        bool isNewFile = !string.Equals(_currentFile, target.FilePath, StringComparison.OrdinalIgnoreCase);
        _currentTarget = target;

        if (isNewFile)
        {
            _currentFile = target.FilePath;
            long request = ++_requestId;

            FileNameText.Text = Path.GetFileName(target.FilePath);
            FileInfoText.Text = SafeSize(target.FilePath);

            if (target.Kind == FileKind.Video)
            {
                if (_mediaPlayer == null || !_videoReady || _libVlc == null) return;
                MediaTypeDot.Fill = VideoDotBrush;
                StartVideoPreviewAsync(target, request);
            }
            else
            {
                PauseVideoFast();
                _waitingForFirstFrame = false;
                MediaTypeDot.Fill = ImageDotBrush;
                VlcVideoView.Visibility = Visibility.Collapsed;
                ScrubBar.Visibility = Visibility.Collapsed;
                LoadImageAsync(target, request);
            }
        }
        else if (!IsVisible && !_pendingShow)
        {
            var pos = ApplyBoundsAtomic(target, Width, Height);
            Show();
            PlayEntranceAnimation(pos.Y);
            if (target.Kind == FileKind.Video)
            {
                _isPausedOrStopping = false;
                _ = Task.Run(() =>
                {
                    lock (_vlcGate)
                    {
                        try { _mediaPlayer?.SetPause(false); } catch { }
                    }
                });
                _scrubTimer.Start();
            }
        }
    }

    private async void StartVideoPreviewAsync(HoverTarget target, long request)
    {
        string videoPath = target.FilePath;
        int thumbSide = Math.Max(320, (int)Math.Round(MaxPreviewSide * 1.25));

        var probeTask = Task.Run(() => MediaProbeService.GetMediaMeta(videoPath));
        var thumbTask = _thumbWorker.RunAsync(() => ShellThumbnailService.TryGetVideoThumbnail(videoPath, thumbSide));

        await Task.WhenAll(probeTask, thumbTask);
        if (request != _requestId || _mediaPlayer == null || _libVlc == null) return;

        var meta = probeTask.Result;
        var poster = thumbTask.Result;

        int probedW = meta.Width;
        int probedH = meta.Height;

        if ((probedW <= 0 || probedH <= 0) && poster != null && poster.PixelWidth > 0 && poster.PixelHeight > 0)
        {
            probedW = poster.PixelWidth;
            probedH = poster.PixelHeight;
        }

        _currentTarget = target;
        _currentMediaW = probedW;
        _currentMediaH = probedH;
        _currentHasScrub = true;

        ScrubBar.Visibility = Visibility.Visible;
        ScrubBar.Value = 0;
        _scrubTicks = 0;
        _isPausedOrStopping = false;
        _videoPlayRequestedAt = DateTime.UtcNow;

        if (poster != null)
        {
            VlcVideoView.Visibility = Visibility.Hidden;
            ImagePreview.Source = poster;
            ImagePreview.Visibility = Visibility.Visible;
            _waitingForFirstFrame = true;
        }
        else
        {
            ImagePreview.Visibility = Visibility.Collapsed;
            ImagePreview.Source = null;
            VlcVideoView.Visibility = Visibility.Visible;
            _waitingForFirstFrame = false;
        }

        if (probedW > 0 && probedH > 0)
        {
            _videoAspectApplied = meta.Width > 0 && meta.Height > 0;
            _pendingShow = false;
            var (tw, th) = ComputeWindowSizeForAspect(probedW, probedH, hasScrubBar: true);
            bool wasVisible = IsVisible;
            var pos = ApplyBoundsAtomic(target, tw, th);

            string durText = meta.DurationSeconds > 0 ? $"{FormatTime((long)(meta.DurationSeconds * 1000))} · " : string.Empty;
            FileInfoText.Text = $"{probedW}×{probedH} · {durText}{SafeSize(videoPath)}";

            if (!wasVisible)
            {
                Show();
                PlayEntranceAnimation(pos.Y);
            }
            if (_hwnd != IntPtr.Zero) NativeMethods.DisableChildWindowsInput(_hwnd);
            _scrubTimer.Interval = TimeSpan.FromMilliseconds(180);
        }
        else
        {
            _videoAspectApplied = false;
            _pendingShow = true;
            _pendingShowSince = DateTime.UtcNow;
            if (IsVisible) Hide();
            _scrubTimer.Interval = TimeSpan.FromMilliseconds(35);
        }

        _ = Task.Run(() =>
        {
            lock (_vlcGate)
            {
                if (request != _requestId || _libVlc == null || _mediaPlayer == null) return;
                try
                {
                    using var media = new Media(_libVlc, new Uri(videoPath));
                    _mediaPlayer.Play(media);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("VLC play failed: " + ex.Message);
                }
            }
        });

        _scrubTimer.Start();
    }

    private (int Width, int Height) GetEffectiveVlcVideoSize(MediaPlayer player)
    {
        uint w = 0, h = 0;
        try { player.Size(0, ref w, ref h); } catch { }
        if (w == 0 || h == 0) return (0, 0);

        int iw = (int)w, ih = (int)h;
        try
        {
            var tracks = player.Media?.Tracks;
            if (tracks != null)
            {
                foreach (var t in tracks)
                {
                    if (t.TrackType == TrackType.Video)
                    {
                        var v = t.Data.Video;
                        if (v.SarNum > 0 && v.SarDen > 0 && v.SarNum != v.SarDen)
                            iw = Math.Max(2, (int)Math.Round(iw * ((double)v.SarNum / v.SarDen)));

                        var o = v.Orientation;
                        if (o is VideoOrientation.LeftTop or VideoOrientation.LeftBottom
                            or VideoOrientation.RightTop or VideoOrientation.RightBottom)
                        {
                            (iw, ih) = (ih, iw);
                        }
                        break;
                    }
                }
            }
        }
        catch { }

        return (iw, ih);
    }

    private async void LoadImageAsync(HoverTarget target, long request)
    {
        string path = target.FilePath;
        int decodeMax = Math.Max(320, (int)Math.Round(MaxPreviewSide * 1.4));

        try
        {
            DateTime writeUtc = DateTime.MinValue;
            try { writeUtc = File.GetLastWriteTimeUtc(path); } catch { }

            if (ImageCache.TryGetValue(path, out var cached)
                && cached.WriteUtc == writeUtc && cached.DecodeMax == decodeMax)
            {
                if (request != _requestId) return;
                DisplayLoadedImage(target, cached.Bitmap, cached.Width, cached.Height);
                return;
            }

            var (bitmap, w, h) = await Task.Run(() =>
            {
                int rotation = 0;
                int pw = 0, ph = 0;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384);
                try
                {
                    var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    pw = frame.PixelWidth;
                    ph = frame.PixelHeight;
                    rotation = ReadExifRotation(frame.Metadata as BitmapMetadata);
                }
                catch { }

                fs.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (ph > pw && ph > 0)
                    bmp.DecodePixelHeight = decodeMax;
                else
                    bmp.DecodePixelWidth = decodeMax;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();

                BitmapSource finalSource = bmp;
                if (rotation != 0)
                {
                    var rotated = new TransformedBitmap(bmp, new System.Windows.Media.RotateTransform(rotation));
                    rotated.Freeze();
                    finalSource = rotated;
                    if (rotation == 90 || rotation == 270)
                        (pw, ph) = (ph, pw);
                }

                if (pw <= 0 || ph <= 0)
                {
                    pw = finalSource.PixelWidth;
                    ph = finalSource.PixelHeight;
                }

                return (finalSource, pw, ph);
            });

            if (ImageCache.Count >= 32) ImageCache.Clear();
            ImageCache[path] = new CachedImage(writeUtc, decodeMax, bitmap, w, h);

            if (request != _requestId) return;
            DisplayLoadedImage(target, bitmap, w, h);
        }
        catch (Exception ex)
        {
            if (request != _requestId) return;
            AppLog.Warn($"Image preview failed for {path}: {ex.Message}");
            HidePreview(releaseFileNow: false);
        }
    }

    private void DisplayLoadedImage(HoverTarget target, BitmapSource bitmap, int w, int h)
    {
        _currentTarget = target;
        _currentMediaW = w;
        _currentMediaH = h;
        _currentHasScrub = false;
        _pendingShow = false;
        bool wasVisible = IsVisible;

        ImagePreview.Source = bitmap;
        ImagePreview.Visibility = Visibility.Visible;
        if (w > 0 && h > 0)
            FileInfoText.Text = $"{w}×{h} · {SafeSize(target.FilePath)}";

        var (tw, th) = ComputeWindowSizeForAspect(w, h, hasScrubBar: false);
        var pos = ApplyBoundsAtomic(target, tw, th);

        if (!wasVisible)
        {
            Show();
            PlayEntranceAnimation(pos.Y);
        }
    }

    private static int ReadExifRotation(BitmapMetadata? meta)
    {
        if (meta == null) return 0;
        string[] queries = { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/xmp/tiff:Orientation" };
        foreach (var q in queries)
        {
            try
            {
                if (!meta.ContainsQuery(q)) continue;
                object? val = meta.GetQuery(q);
                int orient = val switch
                {
                    ushort u => u,
                    int i => i,
                    string s when int.TryParse(s, out int parsed) => parsed,
                    _ => 0
                };
                if (orient > 0)
                {
                    return orient switch
                    {
                        3 => 180,
                        5 or 6 => 90,
                        7 or 8 => 270,
                        _ => 0
                    };
                }
            }
            catch { }
        }
        return 0;
    }

    private void ScrubTimer_Tick(object? sender, EventArgs e)
    {
        var player = _mediaPlayer;
        if (player == null || _isPausedOrStopping) return;

        if (_waitingForFirstFrame && (DateTime.UtcNow - _videoPlayRequestedAt).TotalMilliseconds > 260)
            RevealLiveVideo(_requestId);

        if (!_videoAspectApplied)
        {
            var (vw, vh) = GetEffectiveVlcVideoSize(player);
            if (vw > 0 && vh > 0)
            {
                _videoAspectApplied = true;
                _currentMediaW = vw;
                _currentMediaH = vh;
                _currentHasScrub = true;
                var (tw, th) = ComputeWindowSizeForAspect(vw, vh, hasScrubBar: true);
                if (_currentTarget != null)
                {
                    bool wasVisible = IsVisible;
                    var pos = ApplyBoundsAtomic(_currentTarget, tw, th);
                    if (_pendingShow)
                    {
                        _pendingShow = false;
                        _scrubTimer.Interval = TimeSpan.FromMilliseconds(180);
                        if (!wasVisible)
                        {
                            Show();
                            PlayEntranceAnimation(pos.Y);
                        }
                        if (_hwnd != IntPtr.Zero) NativeMethods.DisableChildWindowsInput(_hwnd);
                    }
                }
            }
            else if (_pendingShow && (DateTime.UtcNow - _pendingShowSince).TotalMilliseconds > 360)
            {
                _pendingShow = false;
                _scrubTimer.Interval = TimeSpan.FromMilliseconds(180);
                ApplyDefaultSize();
                if (_currentTarget != null)
                {
                    var pos = ApplyBoundsAtomic(_currentTarget, Width, Height);
                    if (!IsVisible)
                    {
                        Show();
                        PlayEntranceAnimation(pos.Y);
                    }
                    if (_hwnd != IntPtr.Zero) NativeMethods.DisableChildWindowsInput(_hwnd);
                }
            }
        }

        if (!IsVisible) return;

        long length = player.Length;
        if (length > 0)
        {
            string info = SafeSize(_currentFile);
            int dispW = _currentMediaW;
            int dispH = _currentMediaH;
            if (dispW <= 0 || dispH <= 0) (dispW, dispH) = GetEffectiveVlcVideoSize(player);
            string res = dispW > 0 && dispH > 0 ? $"{dispW}×{dispH} · " : string.Empty;
            FileInfoText.Text = $"{res}{FormatTime(length)} · {info}";
            ScrubBar.Value = Math.Clamp(player.Position, 0, 1);
        }

        if (!player.IsPlaying || length <= 0) return;

        if (!SettingsService.Current.SmartScrub || length < 16_000) return;
        if (++_scrubTicks % 11 != 0) return;
        float next = player.Position + 0.14f;
        player.Position = next > 0.94f ? 0.04f : next;
    }

    private Point ComputeBestPosition(HoverTarget target, double winW, double winH)
    {
        var cursorPx = target.CursorPos;
        var dip = ScreenHelper.PixelsToDip(this, cursorPx);
        var area = ScreenHelper.WorkAreaDipAt(this, cursorPx);
        var winSize = new Size(winW, winH);

        var candidates = new List<Point>(8);

        if (!target.ItemBounds.IsEmpty && target.ItemBounds.Width > 0 && target.ItemBounds.Height > 0)
        {
            var bTopLeft = ScreenHelper.PixelsToDip(this, target.ItemBounds.TopLeft);
            var bBottomRight = ScreenHelper.PixelsToDip(this, target.ItemBounds.BottomRight);
            var itemDip = new Rect(bTopLeft, bBottomRight);

            candidates.Add(new Point(itemDip.Right + 16, itemDip.Top));
            candidates.Add(new Point(itemDip.Left - 16 - winW, itemDip.Top));
            candidates.Add(new Point(dip.X + 20, itemDip.Bottom + 14));
            candidates.Add(new Point(dip.X + 20, itemDip.Top - 14 - winH));
        }

        candidates.Add(new Point(dip.X + 26, dip.Y + 26));
        candidates.Add(new Point(dip.X - 26 - winW, dip.Y - 26 - winH));
        candidates.Add(new Point(dip.X - 26 - winW, dip.Y + 26));
        candidates.Add(new Point(dip.X + 26, dip.Y - 26 - winH));

        Point chosen = ScreenHelper.Clamp(candidates[0], winSize, area);
        const double cursorSafeMargin = 14;

        foreach (var raw in candidates)
        {
            var clamped = ScreenHelper.Clamp(raw, winSize, area);
            bool coversCursor =
                dip.X >= clamped.X - cursorSafeMargin && dip.X <= clamped.X + winW + cursorSafeMargin &&
                dip.Y >= clamped.Y - cursorSafeMargin && dip.Y <= clamped.Y + winH + cursorSafeMargin;

            if (!coversCursor)
            {
                chosen = clamped;
                break;
            }
        }

        return chosen;
    }

    public void HidePreview(bool releaseFileNow = false)
    {
        if (!IsVisible && _currentFile == null && !_pendingShow)
        {
            if (releaseFileNow) ReleaseVlcMediaAsync();
            return;
        }

        _requestId++;
        _pendingShow = false;
        _waitingForFirstFrame = false;
        _currentFile = null;
        _currentTarget = null;
        BeginAnimation(TopProperty, null);
        ImagePreview.Source = null;
        VlcVideoView.Visibility = Visibility.Hidden;

        if (IsVisible) Hide();

        if (releaseFileNow)
        {
            _idleStopTimer.Stop();
            ReleaseVlcMediaAsync();
        }
        else
        {
            PauseVideoFast();
            _idleStopTimer.Stop();
            _idleStopTimer.Start();
        }
    }

    private void PauseVideoFast()
    {
        _scrubTimer.Stop();
        _isPausedOrStopping = true;
        var player = _mediaPlayer;
        if (player == null) return;

        _ = Task.Run(() =>
        {
            lock (_vlcGate)
            {
                try
                {
                    if (player.IsPlaying)
                        player.SetPause(true);
                }
                catch { }
            }
        });
    }

    private void ReleaseVlcMediaAsync()
    {
        _scrubTimer.Stop();
        _isPausedOrStopping = true;
        var player = _mediaPlayer;
        if (player == null) return;

        _ = Task.Run(() =>
        {
            lock (_vlcGate)
            {
                try
                {
                    var state = player.State;
                    if (player.IsPlaying || (state != VLCState.Stopped && state != VLCState.NothingSpecial))
                        player.Stop();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("VLC stop failed: " + ex.Message);
                }
            }
        });
    }

    private static string SafeSize(string? path)
    {
        try { return path != null ? FileKinds.FormatSize(new FileInfo(path).Length) : string.Empty; }
        catch { return string.Empty; }
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    protected override void OnClosed(EventArgs e)
    {
        _scrubTimer.Stop();
        _idleStopTimer.Stop();
        _thumbWorker.Dispose();
        _isPausedOrStopping = true;
        var player = _mediaPlayer;
        var vlc = _libVlc;
        _mediaPlayer = null;
        _libVlc = null;

        _ = Task.Run(() =>
        {
            lock (_vlcGate)
            {
                try
                {
                    if (player != null)
                    {
                        player.TimeChanged -= MediaPlayer_TimeChanged;
                        player.Stop();
                        player.Dispose();
                    }
                    vlc?.Dispose();
                }
                catch { }
            }
        });
        base.OnClosed(e);
    }
}
