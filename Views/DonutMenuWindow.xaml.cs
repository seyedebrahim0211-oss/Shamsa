using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class DonutMenuWindow : Window
{
    private record SliceDef(string Icon, string Label, string Action);
    private record Face(string Title, SliceDef[] Slices);

    private static readonly Face VideoFace = new("ویدیو", new SliceDef[]
    {
        new("🎬", "MP4", "mp4"), new("🎵", "MP3", "mp3"), new("🎞", "GIF", "gif"),
        new("🗜", "کم‌حجم", "compress"), new("📦", "ZIP", "zip")
    });

    private static readonly Face ImageFace = new("تصویر", new SliceDef[]
    {
        new("🖼", "PNG", "png"), new("📷", "JPG", "jpg"), new("🌐", "WEBP", "webp"),
        new("🗜", "کم‌حجم", "compress"), new("📦", "ZIP", "zip")
    });

    private static readonly Face AudioFace = new("صوت", new SliceDef[]
    {
        new("🎵", "MP3", "mp3"), new("🎚", "WAV", "wav"), new("🗜", "کم‌حجم", "compress"),
        new("🧺", "قفسه", "shelf"), new("📦", "ZIP", "zip")
    });

    private static readonly Face GeneralFace = new("تبدیل فرمت", new SliceDef[]
    {
        new("🎬", "MP4", "mp4"), new("🎵", "MP3", "mp3"), new("🎞", "GIF", "gif"),
        new("🌐", "WEBP", "webp"), new("📦", "ZIP", "zip")
    });

    private static readonly Face ToolsFace = new("ابزار سریع", new SliceDef[]
    {
        new("🧺", "قفسه", "shelf"), new("📋", "کپی مسیر", "copypath"), new("📦", "ZIP", "zip"),
        new("🗜", "کم‌حجم", "compress"), new("📷", "JPG", "jpg")
    });

    // پالت خوانا و پرکنتراست شمسا (کاشی لاجوردی، فیروزه نیشابوری و طلایی روشن)
    private static readonly Brush SliceNormalBg = FrozenGrad("#FF1D335B", "#FF122140");
    private static readonly Brush SliceHoverBg = FrozenGrad("#FF14B8A6", "#FF0D9488");
    private static readonly Brush SliceDisabledBg = Frozen("#E60D1628");

    private static readonly Brush SliceBorderHover = Frozen("#FFFFE599");
    private static readonly Brush SliceBorderDisabled = Frozen("#40475569");
    private static readonly Brush LabelNormalFg = Frozen("#FFFFFFFF");
    private static readonly Brush LabelDisabledFg = Frozen("#FF64748B");

    private static readonly Brush CenterBorderNormal = Frozen("#FFE5B85B");
    private static readonly Brush CenterBorderHover = Frozen("#FF2DD4BF");

    private static readonly Brush ArcActiveBrush = Frozen("#2DD4BF");
    private static readonly Brush ArcDoneBrush = Frozen("#10B981");
    private static readonly Brush ArcErrorBrush = Frozen("#F43F5E");

    // بردارهای دقیق حرکت اولیه برای انیمیشن شکوفایی دایره‌ای (مشابه Filee.sh)
    private static readonly (double Dx, double Dy)[] SlicePullOffsets =
    {
        (0, 49.6), (-47.1, 15.3), (-29.1, -40.1), (29.1, -40.1), (47.1, 15.3)
    };

    private readonly ConverterService _converter;
    private readonly Border[] _slices;
    private readonly TextBlock[] _labels;
    private readonly TextBlock[] _icons;
    private readonly ScaleTransform[] _sliceScales;
    private readonly TranslateTransform[] _sliceTranslates;

    private Face _smartFace = GeneralFace;
    private bool _showTools;
    private bool _isFlipping;
    private DateTime _lastWheelFlip = DateTime.MinValue;
    private string[]? _currentFiles;

    public bool IsShowingProgress { get; private set; }
    public bool IsDemoMode { get; private set; }
    public bool IsShortcutMode { get; private set; }

    private bool IsInteractiveClickMode => IsDemoMode || IsShortcutMode;

    public event Action<string[]>? SendToShelfRequested;
    public event Action<bool>? BusyChanged;

    public DonutMenuWindow(ConverterService converter)
    {
        InitializeComponent();
        _converter = converter;
        _slices = new[] { Slice0, Slice1, Slice2, Slice3, Slice4 };
        _labels = new[] { Slice0Text, Slice1Text, Slice2Text, Slice3Text, Slice4Text };
        _icons = new[] { Slice0Icon, Slice1Icon, Slice2Icon, Slice3Icon, Slice4Icon };
        _sliceScales = new ScaleTransform[_slices.Length];
        _sliceTranslates = new TranslateTransform[_slices.Length];

        for (int i = 0; i < _slices.Length; i++)
        {
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            _sliceScales[i] = scale;
            _sliceTranslates[i] = translate;
            _slices[i].RenderTransform = group;
        }

        SourceInitialized += (_, _) => NativeMethods.MakeToolWindowNoActivate(new WindowInteropHelper(this).Handle);
        ApplyFace();
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static Brush FrozenGrad(string topHex, string bottomHex)
    {
        var g = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(topHex),
            (Color)ColorConverter.ConvertFromString(bottomHex),
            new Point(0.2, 0), new Point(0.8, 1));
        g.Freeze();
        return g;
    }

    private static Brush BorderForAction(string action) => action switch
    {
        "mp4" or "gif" => Frozen("#CC2DD4BF"),
        "mp3" or "wav" => Frozen("#CCFBBF24"),
        "png" or "jpg" or "webp" => Frozen("#CC38BDF8"),
        "compress" => Frozen("#CC34D399"),
        _ => Frozen("#CCE5B85B")
    };

    /// <summary>باز کردن منو در حالت Drag &amp; Drop یا آزمایشی.</summary>
    public void OpenAt(Point cursorPx, bool demoMode = false)
    {
        IsShowingProgress = false;
        IsDemoMode = demoMode;
        IsShortcutMode = false;
        _isFlipping = false;
        _currentFiles = null;
        _smartFace = GeneralFace;
        _showTools = false;

        SetCenterCount(demoMode ? "آزمایشی" : null);
        CenterHint.Text = demoMode ? "کلیک ⟳ چرخش" : "Tab ⟳ چرخش";

        ShowAtCursor(cursorPx, activate: demoMode);
    }

    /// <summary>باز کردن منو با شورتکات صفحه‌کلید روی فایل(های) انتخاب‌شده یا زیر موس (قابل کلیک).</summary>
    public void OpenForFiles(string[] files, Point cursorPx)
    {
        if (files == null || files.Length == 0) return;

        IsShowingProgress = false;
        IsDemoMode = false;
        IsShortcutMode = true;
        _isFlipping = false;
        _currentFiles = files;

        _smartFace = PickSmartFace(files);
        _showTools = _smartFace == ToolsFace;
        if (_showTools) _smartFace = GeneralFace;

        SetCenterCount(files.Length == 1 ? "۱ فایل" : $"{files.Length} فایل");
        CenterHint.Text = "کلیک / Tab ⟳ چرخش";

        ShowAtCursor(cursorPx, activate: true);
    }

    private void ShowAtCursor(Point cursorPx, bool activate)
    {
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootScale.ScaleX = 1;
        RootScale.ScaleY = 1;

        ProgressRingOverlay.Visibility = Visibility.Collapsed;
        MenuLayer.Visibility = Visibility.Visible;
        MenuLayer.Opacity = 1;
        Root.Opacity = 1;
        ApplyFace();

        var dip = ScreenHelper.PixelsToDip(this, cursorPx);
        var area = ScreenHelper.WorkAreaDipAt(this, cursorPx);
        var pos = ScreenHelper.Clamp(new Point(dip.X - Width / 2, dip.Y - Height / 2), new Size(Width, Height), area);
        Left = pos.X;
        Top = pos.Y;

        if (!IsVisible) Show();
        if (activate) Activate();
        PlayOpenAnimation();
    }

    private void SetCenterCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            CenterCount.Text = string.Empty;
            CenterCountBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            CenterCount.Text = text;
            CenterCountBadge.Visibility = Visibility.Visible;
        }
    }

    public void ShowDemo()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
        var centerPx = new Point(screen.Left + screen.Width / 2.0, screen.Top + screen.Height / 2.0);
        OpenAt(centerPx, demoMode: true);
    }

    private void PlayOpenAnimation()
    {
        if (!SettingsService.Current.DonutAnimations)
        {
            RootScale.ScaleX = 1;
            RootScale.ScaleY = 1;
            Root.Opacity = 1;
            for (int i = 0; i < _slices.Length; i++)
            {
                _sliceScales[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _sliceScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _sliceTranslates[i].BeginAnimation(TranslateTransform.XProperty, null);
                _sliceTranslates[i].BeginAnimation(TranslateTransform.YProperty, null);
                _sliceScales[i].ScaleX = 1;
                _sliceScales[i].ScaleY = 1;
                _sliceTranslates[i].X = 0;
                _sliceTranslates[i].Y = 0;
            }
            return;
        }

        var hubEase = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut };
        var hubAnim = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(165)) { EasingFunction = hubEase };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, hubAnim);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, hubAnim);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        for (int i = 0; i < _slices.Length; i++)
        {
            var delay = TimeSpan.FromMilliseconds(i * 22);
            var dur = TimeSpan.FromMilliseconds(210);
            var ease = new BackEase { Amplitude = 0.34, EasingMode = EasingMode.EaseOut };

            var (dx, dy) = SlicePullOffsets[i];
            _sliceTranslates[i].BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(dx, 0, dur) { BeginTime = delay, EasingFunction = ease });
            _sliceTranslates[i].BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(dy, 0, dur) { BeginTime = delay, EasingFunction = ease });

            var sc = new DoubleAnimation(0.5, 1, dur) { BeginTime = delay, EasingFunction = ease };
            _sliceScales[i].BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            _sliceScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, sc);
        }
    }

    /// <summary>
    /// چرخش سکه‌ای دو مرحله‌ای — فقط با فرمان مستقیم کاربر (Tab، اسکرول موس یا کلیک مرکز) انجام می‌شود
    /// و هرگز به‌صورت خودکار تغییر نمی‌کند.
    /// </summary>
    public void FlipFace()
    {
        if (_isFlipping || IsShowingProgress) return;

        if (!SettingsService.Current.DonutAnimations)
        {
            _showTools = !_showTools;
            ApplyFace();
            return;
        }

        _isFlipping = true;
        var shrink = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(95))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        shrink.Completed += (_, _) =>
        {
            if (!IsVisible)
            {
                _isFlipping = false;
                return;
            }
            _showTools = !_showTools;
            ApplyFace();
            var expand = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(135))
            {
                EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            };
            expand.Completed += (_, _) => _isFlipping = false;
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
    }

    private void ApplyFace()
    {
        var face = _showTools ? ToolsFace : _smartFace;
        CenterTitle.Text = face.Title;
        for (int i = 0; i < _slices.Length; i++)
        {
            var def = face.Slices[i];
            _labels[i].Text = def.Label;
            _icons[i].Text = def.Icon;
            _slices[i].Tag = def.Action;

            bool enabled = _currentFiles == null || ConverterService.IsApplicable(_currentFiles, def.Action);
            _slices[i].Opacity = 1;
            _slices[i].Background = enabled ? SliceNormalBg : SliceDisabledBg;
            _slices[i].BorderBrush = enabled ? BorderForAction(def.Action) : SliceBorderDisabled;
            _labels[i].Foreground = enabled ? LabelNormalFg : LabelDisabledFg;
            _icons[i].Opacity = enabled ? 1.0 : 0.4;
            _slices[i].Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
            _slices[i].ToolTip = enabled ? ConverterService.ActionLabel(def.Action) : "برای این فایل‌ها مناسب نیست";
        }
    }

    private static Face PickSmartFace(string[] files)
    {
        var kinds = files.Select(FileKinds.Of).Distinct().ToList();
        if (kinds.Count == 1)
        {
            return kinds[0] switch
            {
                FileKind.Video => VideoFace,
                FileKind.Image => ImageFace,
                FileKind.Audio => AudioFace,
                FileKind.Folder or FileKind.Document or FileKind.Archive or FileKind.Other or FileKind.Code => ToolsFace,
                _ => GeneralFace
            };
        }
        return kinds.All(k => k is FileKind.Video or FileKind.Image or FileKind.Audio) ? GeneralFace : ToolsFace;
    }

    private static string[]? GetFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (IsShowingProgress) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (_currentFiles == null && GetFiles(e) is { Length: > 0 } files)
        {
            _currentFiles = files;
            _smartFace = PickSmartFace(files);
            _showTools = _smartFace == ToolsFace;
            if (_showTools) _smartFace = GeneralFace;
            SetCenterCount(files.Length == 1 ? "۱ فایل" : $"{files.Length} فایل");
            ApplyFace();
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private bool SliceAccepts(Border border) =>
        border.Tag is string action && (_currentFiles == null || ConverterService.IsApplicable(_currentFiles, action));

    private void HighlightSlice(Border border)
    {
        int idx = Array.IndexOf(_slices, border);
        if (idx < 0) return;
        border.Background = SliceHoverBg;
        border.BorderBrush = SliceBorderHover;
        _labels[idx].Foreground = LabelNormalFg;
        AnimateSliceScale(idx, 1.14);
        for (int i = 0; i < _slices.Length; i++)
            if (i != idx) AnimateSliceScale(i, 0.94);
    }

    private void Slice_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!IsInteractiveClickMode || IsShowingProgress || sender is not Border border) return;
        if (IsShortcutMode && !SliceAccepts(border)) return;
        HighlightSlice(border);
    }

    private void Slice_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!IsInteractiveClickMode || IsShowingProgress) return;
        ResetAllSlices();
    }

    private void Slice_DragEnter(object sender, DragEventArgs e)
    {
        if (IsShowingProgress || sender is not Border border) return;
        bool ok = SliceAccepts(border) && GetFiles(e) != null;
        if (ok) HighlightSlice(border);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Slice_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !IsShowingProgress && sender is Border b && SliceAccepts(b) && GetFiles(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Slice_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not Border) return;
        ResetAllSlices();
        e.Handled = true;
    }

    private void AnimateSliceScale(int idx, double target)
    {
        if (idx < 0 || idx >= _sliceScales.Length) return;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(105))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _sliceScales[idx].BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _sliceScales[idx].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void ResetAllSlices()
    {
        for (int i = 0; i < _slices.Length; i++)
        {
            bool ok = SliceAccepts(_slices[i]);
            string action = _slices[i].Tag as string ?? string.Empty;
            _slices[i].Background = ok ? SliceNormalBg : SliceDisabledBg;
            _slices[i].BorderBrush = ok ? BorderForAction(action) : SliceBorderDisabled;
            _labels[i].Foreground = ok ? LabelNormalFg : LabelDisabledFg;
            _icons[i].Opacity = ok ? 1.0 : 0.4;
            AnimateSliceScale(i, 1.0);
        }
    }

    private void Center_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!IsInteractiveClickMode || IsShowingProgress) return;
        CenterHub.BorderBrush = CenterBorderHover;
    }

    private void Center_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!IsInteractiveClickMode || IsShowingProgress) return;
        CenterHub.BorderBrush = CenterBorderNormal;
    }

    private void Center_DragEnter(object sender, DragEventArgs e)
    {
        // هیچ تایمر خودکاری برای چرخش فعال نمی‌شود تا منو هرگز ناخواسته عوض نشود
        if (!IsShowingProgress) CenterHub.BorderBrush = CenterBorderHover;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Center_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Center_DragLeave(object sender, DragEventArgs e)
    {
        CenterHub.BorderBrush = CenterBorderNormal;
        e.Handled = true;
    }

    private void Center_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        HideMenu();
    }

    private void Center_MouseUp(object sender, MouseButtonEventArgs e) => FlipFace();

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (IsInteractiveClickMode && !IsShowingProgress)
            HideMenu();
    }

    private async void Slice_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsInteractiveClickMode || IsShowingProgress || sender is not Border border || border.Tag is not string action) return;

        if (IsDemoMode)
        {
            EnterProgressMode($"آزمایش {action.ToUpperInvariant()}");
            try
            {
                for (int p = 5; p <= 100; p += 5)
                {
                    UpdateProgressArc(p / 100.0);
                    ProgressPercentText.Text = $"{p}٪";
                    await Task.Delay(35);
                }
                await FinishProgressRingAsync(success: true);
            }
            finally
            {
                IsShowingProgress = false;
                HideMenu();
            }
            return;
        }

        if (IsShortcutMode)
        {
            var files = _currentFiles;
            if (files == null || files.Length == 0 || !SliceAccepts(border)) return;
            IsShortcutMode = false;
            ResetAllSlices();
            await RunActionAsync(action, files);
        }
    }

    private async void Slice_Drop(object sender, DragEventArgs e)
    {
        if (IsShowingProgress || sender is not Border border || border.Tag is not string action) return;
        var files = GetFiles(e);
        if (files == null || files.Length == 0 || !SliceAccepts(border))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        ResetAllSlices();

        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await RunActionAsync(action, files);
        }
        catch (Exception ex)
        {
            AppLog.Error("Donut drop action failed", ex);
            Notifier.Show("خطا در اجرای عملیات: " + ex.Message);
        }
    }

    private async Task RunActionAsync(string action, string[] files)
    {
        switch (action)
        {
            case "shelf":
                HideMenu();
                SendToShelfRequested?.Invoke(files);
                return;

            case "copypath":
                HideMenu();
                bool copied = ShellActions.TrySetClipboardText(string.Join(Environment.NewLine, files));
                Notifier.Show(copied ? $"مسیر {files.Length} مورد در کلیپ‌بورد کپی شد." : "کلیپ‌بورد در دسترس نبود.");
                return;
        }

        EnterProgressMode(ConverterService.ActionLabel(action));
        BusyChanged?.Invoke(true);
        try
        {
            var progress = new Progress<ConversionProgress>(p =>
            {
                UpdateProgressArc(p.Overall);
                ProgressPercentText.Text = $"{(int)Math.Round(p.Overall * 100)}٪";
                ProgressLabelText.Text = p.FileCount > 1 ? $"{p.FileIndex + 1} از {p.FileCount}" : action.ToUpperInvariant();
            });

            var result = await _converter.ConvertFilesAsync(files, action, progress);
            Notifier.Show(result.Message, result.Outputs.FirstOrDefault());

            await FinishProgressRingAsync(result.Success);
        }
        finally
        {
            BusyChanged?.Invoke(false);
            IsShowingProgress = false;
            HideMenu();
        }
    }

    private void EnterProgressMode(string label)
    {
        IsShowingProgress = true;
        IsDemoMode = false;
        IsShortcutMode = false;

        MenuLayer.Visibility = Visibility.Collapsed;
        ProgressRingOverlay.Visibility = Visibility.Visible;
        ProgressArcPath.Stroke = ArcActiveBrush;
        ProgressPercentText.Foreground = Frozen("#FFF6D379");
        ProgressPercentText.Text = "0٪";
        ProgressLabelText.Text = label;
        UpdateProgressArc(0.03);

        var pop = new DoubleAnimation(0.76, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut }
        };
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void UpdateProgressArc(double fraction)
    {
        double clamped = Math.Clamp(fraction, 0.001, 0.999);
        double angleRad = (clamped * 360.0 - 90.0) * Math.PI / 180.0;
        const double radius = 62.0;
        const double center = 68.0;
        double x = center + radius * Math.Cos(angleRad);
        double y = center + radius * Math.Sin(angleRad);

        ProgressArcSegment.IsLargeArc = clamped > 0.5;
        ProgressArcSegment.Point = new Point(x, y);
    }

    private async Task FinishProgressRingAsync(bool success)
    {
        UpdateProgressArc(0.999);
        ProgressArcPath.Stroke = success ? ArcDoneBrush : ArcErrorBrush;
        ProgressPercentText.Foreground = success ? ArcDoneBrush : ArcErrorBrush;
        ProgressPercentText.Text = success ? "✓" : "✕";
        ProgressLabelText.Text = success ? "انجام شد" : "خطا";

        var pulse = new DoubleAnimation(0.92, 1.08, TimeSpan.FromMilliseconds(175))
        {
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

        await Task.Delay(850);
    }

    public void HideMenu()
    {
        if (IsShowingProgress) return;
        _isFlipping = false;
        IsDemoMode = false;
        IsShortcutMode = false;
        _currentFiles = null;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootScale.ScaleX = 1;
        RootScale.ScaleY = 1;
        CenterHub.BorderBrush = CenterBorderNormal;
        ResetAllSlices();
        ProgressRingOverlay.Visibility = Visibility.Collapsed;
        MenuLayer.Visibility = Visibility.Visible;
        if (IsVisible) Hide();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsDemoMode = false;
            IsShortcutMode = false;
            HideMenu();
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            FlipFace();
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // جلوگیری از چرخش دوتایی با تاچ‌پد
        if ((DateTime.UtcNow - _lastWheelFlip).TotalMilliseconds < 240) return;
        _lastWheelFlip = DateTime.UtcNow;
        FlipFace();
    }
}
