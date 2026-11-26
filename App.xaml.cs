using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Shamsa.Services;
using Shamsa.Views;
using Forms = System.Windows.Forms;

namespace Shamsa;

public partial class App : Application
{
    private const string MutexName = @"Local\Shamsa.SingleInstance.v2";
    private const string ShowEventName = @"Local\Shamsa.ShowDashboard.v2";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;

    private Forms.NotifyIcon? _trayIcon;
    private IntPtr _trayIconHandle;
    private string? _balloonRevealPath;

    private readonly ExplorerHoverService _hoverService = new();
    private readonly ConverterService _converterService = new();
    private readonly AutoOrganizeWatcher _autoOrganizer = new();
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly StaWorker _hoverWorker = new("Shamsa.HoverSta");
    private bool _hotkeyPausedForRecording;

    private VideoPreviewWindow? _previewWindow;
    private FolderPreviewWindow? _folderWindow;
    private DispatcherTimer? _idleInstallTimer;
    private bool _installingUpdate;
    private DonutMenuWindow? _donutWindow;
    private DropShelfWindow? _shelfWindow;
    private ToolsDashboardWindow? _dashboardWindow;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _updateTimer;

    // tray menu items kept for syncing with settings
    private Forms.ToolStripMenuItem? _menuPreview, _menuDonut, _menuAutoOrganize, _menuStartup;

    // hover state
    private System.Windows.Point _lastCursor;
    private DateTime _hoverStartTime = DateTime.Now;
    private DateTime _lastHoverQuery = DateTime.MinValue;
    private bool _hoverQueriedHere;
    private bool _isCheckingHover;
    private DateTime _hoverQueryStartedAt = DateTime.MinValue;
    private long _hoverQuerySeq;
    private HoverTarget? _activeHoverTarget;
    private DateTime _lastPreviewClosedAt = DateTime.MinValue;

    // Ctrl+Drag state
    private bool _wasLeftDown;
    private bool _wasTabDown;
    private System.Windows.Point _leftDownPos;
    private Task<bool>? _pressOnShellItem;
    private bool _donutOpenedThisDrag;
    private DateTime? _donutHideAt;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // ---- single instance: a second launch just opens the dashboard of the running one
        // SHAMSA_DEV_INSTANCE lets a development build run beside the installed app (separate mutex/event).
        string devSuffix = Environment.GetEnvironmentVariable("SHAMSA_DEV_INSTANCE") ?? string.Empty;
        _singleInstanceMutex = new Mutex(true, MutexName + devSuffix, out bool isFirst);
        if (!isFirst)
        {
            // Explorer context-menu verbs start a second process: hand its arguments to the running instance.
            ForwardArgsToRunningInstance(e.Args);
            try { EventWaitHandle.OpenExisting(ShowEventName + devSuffix).Set(); } catch { }
            Shutdown();
            return;
        }

        RegisterGlobalExceptionHandlers();
        SettingsService.Load();
        ThemeService.Apply(SettingsService.Current);
        Shamsa.Desktop.DesktopFeature.ApplyFontEarly();
        AppLog.Info($"Shamsa starting (v{typeof(App).Assembly.GetName().Version}) args: {string.Join(' ', e.Args)}");

        var showEvent = _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + devSuffix);
        var listener = new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(ProcessForwardedArgs);
            }
            catch (ObjectDisposedException) { /* app is exiting */ }
        }) { IsBackground = true, Name = "Shamsa.ShowListener" };
        listener.Start();

        _previewWindow = new VideoPreviewWindow();
        _previewWindow.WarmUp();
        _folderWindow = new FolderPreviewWindow();

        _shelfWindow = new DropShelfWindow(_converterService);

        _donutWindow = new DonutMenuWindow(_converterService);
        _hotkeyService.HotkeyPressed += () => Dispatcher.BeginInvoke(OnDonutHotkeyPressed);
        _donutWindow.SendToShelfRequested += files => _shelfWindow.AddFiles(files);
        _donutWindow.BusyChanged += busy => SetTrayBusy(busy);

        Notifier.Requested += (msg, path) => Dispatcher.BeginInvoke(() => ShowNotification(msg, path));
        SettingsService.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            ThemeService.Apply(SettingsService.Current);
            ApplySettings();
        });

        _autoOrganizer.FileOrganized += (name, category, dest) =>
            Notifier.Show($"«{name}» به پوشه {category} منتقل شد.", dest);

        SetupTrayIcon();
        ApplySettings();
        InitializeFeatures();

        // بازه ۳۵ میلی‌ثانیه با اولویت Normal برای واکنش فوق‌سریع و آنی روی فایل‌ها
        _pollTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(35) };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        // Network checks run asynchronously. Installation always needs user consent.
        _ = CheckForUpdatesInBackgroundAsync(initial: true);
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesInBackgroundAsync(initial: false);
        _updateTimer.Start();
        _idleInstallTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _idleInstallTimer.Tick += (_, _) => _ = TryIdleInstallAsync();
        _idleInstallTimer.Start();

        string? postUpdate = UpdateService.ConsumePostUpdateMessage();
        if (postUpdate != null)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowNotification(postUpdate));

        RunSelfTestIfRequested(e.Args);
        if (HandleFeatureArgs(e.Args)) return;

        bool silent = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        string? tabArg = e.Args.FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase));
        int? tab = tabArg != null && int.TryParse(tabArg[6..], out int t) ? t : null;
        if (tab.HasValue || e.Args.Any(a => a.Equals("--dashboard", StringComparison.OrdinalIgnoreCase)))
            OpenDashboard(tab);
        else if (!silent && !SettingsService.Current.HasSeenGuide)
        {
            SettingsService.Update(s => s.HasSeenGuide = true);
            OpenDashboard(tab: 0);
        }
        else if (!silent)
            ShowNotification("شمسا فعال شد! روی ویدیوها و تصاویر مکث کنید یا شورتکات منوی دایره‌ای را بزنید.");
    }

    private async Task CheckForUpdatesInBackgroundAsync(bool initial)
    {
        if (!SettingsService.Current.AutoCheckUpdates) return;
        if (initial) await Task.Delay(TimeSpan.FromSeconds(8));
        try
        {
            var release = await UpdateService.CheckAsync();
            if (release == null) return;
            if (release.Version.ToString(3) == SettingsService.Current.SkippedUpdateVersion) return;
            if (SettingsService.Current.AutoDownloadUpdates)
            {
                try
                {
                    await UpdateService.DownloadVerifiedAsync(release);
                    ShowNotification(SettingsService.Current.AutoInstallWhenIdle
                        ? $"نسخه {release.Version} شمسا دریافت و تأیید شد؛ هنگام بیکاری سیستم خودکار نصب می‌شود."
                        : $"نسخه {release.Version} شمسا دریافت و تأیید شد. برای نصب با یک کلیک به تنظیمات › به‌روزرسانی بروید.");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Automatic update download failed: " + ex.Message);
                    ShowNotification($"نسخه {release.Version} منتشر شده؛ دانلود خودکار ناموفق بود. از تنظیمات بررسی کنید.");
                }
            }
            else ShowNotification($"نسخه جدید شمسا {release.Version} آماده است؛ برای نصب به تنظیمات بروید.");
            _dashboardWindow?.RefreshUpdateStatus();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Background update check: " + ex.Message);
        }
    }

    /// <summary>
    /// Smart update: once a verified installer is staged and the user opted in, install silently after
    /// 10 minutes without keyboard/mouse input — never while recording, converting or showing a menu.
    /// The installer relaunches Shamsa with --updated.
    /// </summary>
    private async Task TryIdleInstallAsync()
    {
        var s = SettingsService.Current;
        var release = UpdateService.Latest;
        if (_installingUpdate || !s.AutoInstallWhenIdle || release == null || !UpdateService.HasVerifiedDownload(release)) return;
        if (release.Version.ToString(3) == s.SkippedUpdateVersion) return;
        if (UpdateService.GetUserIdleTime() < TimeSpan.FromMinutes(10) || IsAppBusy()) return;
        _installingUpdate = true;
        try
        {
            string installer = Path.Combine(UpdateService.UpdatesDirectory, release.FileName);
            await UpdateService.LaunchInstallerAsync(installer, release, silent: true);
            AppLog.Info($"Idle auto-install of {release.Version} started");
            Shutdown();
        }
        catch (Exception ex)
        {
            _installingUpdate = false;
            AppLog.Warn("Idle auto-install failed: " + ex.Message);
        }
    }

    private bool IsAppBusy() =>
        (_donutWindow?.IsShowingProgress ?? false) || (_donutWindow?.IsVisible ?? false) || FeatureBusy();

    // ------------------------------------------------------------------ forwarded command lines (Explorer verbs)

    private static string IpcDir => Path.Combine(AppPaths.DataDir, "ipc");

    private static void ForwardArgsToRunningInstance(string[] args)
    {
        if (args.Length == 0) return;
        try
        {
            Directory.CreateDirectory(IpcDir);
            string file = Path.Combine(IpcDir, $"{DateTime.UtcNow.Ticks}-{Environment.ProcessId}.args");
            File.WriteAllLines(file + ".tmp", args);
            File.Move(file + ".tmp", file);
        }
        catch (Exception ex) { AppLog.Warn("Forwarding arguments failed: " + ex.Message); }
    }

    private void ProcessForwardedArgs()
    {
        bool handledAny = false;
        try
        {
            if (Directory.Exists(IpcDir))
            {
                foreach (var file in Directory.GetFiles(IpcDir, "*.args").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string[] args = Array.Empty<string>();
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(2))
                            args = File.ReadAllLines(file);
                    }
                    catch { }
                    try { File.Delete(file); } catch { }
                    if (args.Length > 0 && HandleFeatureArgs(args)) handledAny = true;
                    else if (args.Length > 0) { HandleDashboardArgs(args); handledAny = true; }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Reading forwarded arguments failed: " + ex.Message); }
        if (!handledAny) OpenDashboard();
    }

    private void HandleDashboardArgs(string[] args)
    {
        string? tabArg = args.FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase));
        int? tab = tabArg != null && int.TryParse(tabArg[6..], out int t) ? t : null;
        if (args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)) && tab == null) return;
        OpenDashboard(tab);
    }

    private void RunSelfTestIfRequested(string[] args)
    {
        string? arg = args.FirstOrDefault(a => a.StartsWith("--selftest=", StringComparison.OrdinalIgnoreCase));
        if (arg == null) return;
        string spec = arg["--selftest=".Length..];

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _pollTimer?.Stop();
            if (spec.StartsWith("preview:", StringComparison.OrdinalIgnoreCase))
            {
                string path = spec["preview:".Length..].Trim('"');
                var target = new HoverTarget(path, FileKinds.Of(path), new System.Windows.Point(300, 300), Rect.Empty);
                if (target.Kind == FileKind.Folder) _folderWindow?.ShowPreview(target);
                else _previewWindow?.ShowPreview(target);
            }
            else if (spec.Equals("donut", StringComparison.OrdinalIgnoreCase))
            {
                _donutWindow?.OpenAt(new System.Windows.Point(700, 450));
            }
            else if (spec.StartsWith("dashboard:", StringComparison.OrdinalIgnoreCase) && int.TryParse(spec[10..], out int dt))
                OpenDashboard(dt);
            else if (spec.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
                HandleFeatureArgs(new[] { spec[7..] });
        };
        timer.Start();

        // --selftest-render=<file.png>: saves only Shamsa's own preview window (no screen capture) and exits.
        string? renderArg = args.FirstOrDefault(a => a.StartsWith("--selftest-render=", StringComparison.OrdinalIgnoreCase));
        if (renderArg == null) return;
        string outPng = renderArg["--selftest-render=".Length..].Trim('"');
        var renderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.5) };
        renderTimer.Tick += (_, _) =>
        {
            renderTimer.Stop();
            Window? w = (_folderWindow?.IsVisible ?? false) ? _folderWindow : (_previewWindow?.IsVisible ?? false) ? _previewWindow
                : Windows.OfType<Window>().Where(x => x.IsVisible && x.ActualWidth > 1 && x.ActualHeight > 1)
                    .OrderByDescending(x => x.ShowInTaskbar).ThenByDescending(x => x.ActualWidth * x.ActualHeight).FirstOrDefault();
            try
            {
                if (w?.Content is FrameworkElement root && root.ActualWidth > 0)
                {
                    var dpi = ScreenHelper.GetDpi(root);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                        96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x1A, 0x30)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                        // The window mirrors RTL content itself; a VisualBrush of the root lacks that transform.
                        if (w.FlowDirection == FlowDirection.RightToLeft)
                            dc.PushTransform(new System.Windows.Media.MatrixTransform(-1, 0, 0, 1, root.ActualWidth, 0));
                        dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    }
                    rtb.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(outPng);
                    enc.Save(fs);
                    AppLog.Info($"Selftest render saved {root.ActualWidth}x{root.ActualHeight}");
                }
                else AppLog.Warn("Selftest render: no preview window visible");
            }
            catch (Exception ex) { AppLog.Error("Selftest render failed", ex); }
            Shutdown();
        };
        renderTimer.Start();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
            ShowNotification("خطای غیرمنتظره رخ داد و ثبت شد. برنامه به کار ادامه می‌دهد.");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("Fatal unhandled exception", args.ExceptionObject as Exception);
            // Never leave the user without a taskbar / with reserved AppBar space after a crash.
            Shamsa.Desktop.DesktopFeature.EmergencyRestore();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shamsa.Desktop.DesktopFeature.EmergencyRestore();
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    // ------------------------------------------------------------------ settings

    private void ApplySettings()
    {
        var s = SettingsService.Current;
        _hoverService.IncludeImages = s.PreviewImages;
        _hoverService.IncludeFolders = s.FolderPreviewEnabled;
        _hoverService.IncludeDocuments = s.PreviewDocuments;
        _previewWindow?.ApplySize();
        if (!s.HoverPreviewEnabled) HideHoverPreview(releaseFileNow: true);

        if (s.AutoOrganizeEnabled && Directory.Exists(s.AutoOrganizeFolder))
            _autoOrganizer.Start(s.AutoOrganizeFolder);
        else
            _autoOrganizer.Stop();

        if (!_hotkeyPausedForRecording)
        {
            var donutBinding = new HotkeyBinding("donut.open", "منوی دایره‌ای", "باز کردن منو",
                s.DonutShortcutModifiers, s.DonutShortcutEnabled ? s.DonutShortcutKey : 0);
            if (s.DonutShortcutEnabled && s.DonutShortcutKey > 0)
                HotkeyRegistry.Report(donutBinding, _hotkeyService.Register(s.DonutShortcutModifiers, s.DonutShortcutKey));
            else
            {
                _hotkeyService.Unregister();
                HotkeyRegistry.Report(donutBinding, false);
            }
        }

        if (_menuPreview != null) _menuPreview.Checked = s.HoverPreviewEnabled;
        if (_menuDonut != null) _menuDonut.Checked = s.DonutEnabled;
        if (_menuAutoOrganize != null) _menuAutoOrganize.Checked = s.AutoOrganizeEnabled;
        if (_menuStartup != null) _menuStartup.Checked = StartupManager.IsEnabled();

        _dashboardWindow?.RefreshSettings();
    }

    // ------------------------------------------------------------------ tray

    private void ShowNotification(string message, string? revealPath = null)
    {
        if (_trayIcon == null) return;
        _balloonRevealPath = revealPath;
        string text = message.Length > 250 ? message[..247] + "..." : message;
        _trayIcon.ShowBalloonTip(3500, "شمسا (Shamsa)", text, Forms.ToolTipIcon.Info);
    }

    private void SetTrayBusy(bool busy)
    {
        if (_trayIcon != null)
            _trayIcon.Text = busy ? "شمسا (Shamsa) — در حال پردازش..." : "شمسا (Shamsa) — دستیار هوشمند فایل";
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "شمسا (Shamsa) — دستیار هوشمند فایل"
        };

        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes, ShowCheckMargin = true };
        menu.Items.Add("🧰 داشبورد ابزارها", null, (_, _) => OpenDashboard());
        menu.Items.Add("🧺 قفسه موقت (Drop Shelf)", null, (_, _) => _shelfWindow?.ShowShelf());
        menu.Items.Add("📖 راهنمای شروع سریع", null, (_, _) => OpenDashboard(tab: 0));
        menu.Items.Add("🔄 مبدل فایل", null, (_, _) => OpenDashboard(tab: 4));
        menu.Items.Add("✨ مرتب‌سازی Downloads الان", null, (_, _) => OrganizeDownloadsNow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddFeatureTrayItems(menu);
        menu.Items.Add(new Forms.ToolStripSeparator());

        _menuPreview = AddToggle(menu, "پیش‌نمایش ویدیو/تصویر کنار موس", v => SettingsService.Update(s => s.HoverPreviewEnabled = v));

        var sizeMenu = new Forms.ToolStripMenuItem("📐 اندازه پیش‌نمایش");
        foreach (var (label, px) in new[] { ("خیلی کوچک (180px)", 180), ("کوچک (250px)", 250), ("متوسط (320px)", 320), ("بزرگ (420px)", 420), ("خیلی بزرگ (520px)", 520) })
        {
            int targetPx = px;
            sizeMenu.DropDownItems.Add(label, null, (_, _) => SettingsService.Update(s =>
            {
                s.PreviewMaxSidePx = targetPx;
                s.PreviewSize = targetPx <= 260 ? 0 : targetPx >= 400 ? 2 : 1;
            }));
        }
        menu.Items.Add(sizeMenu);
        _menuDonut = AddToggle(menu, "منوی دایره‌ای (Ctrl + کشیدن)", v => SettingsService.Update(s => s.DonutEnabled = v));
        _menuAutoOrganize = AddToggle(menu, "مرتب‌سازی خودکار Downloads", v => SettingsService.Update(s => s.AutoOrganizeEnabled = v));
        _menuStartup = AddToggle(menu, "اجرا با شروع ویندوز", v =>
        {
            StartupManager.SetEnabled(v);
            SettingsService.Save();
        });

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("⏻ خروج کامل از برنامه", null, (_, _) => Shutdown());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) OpenDashboard();
        };
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_balloonRevealPath)) ShellActions.RevealInExplorer(_balloonRevealPath);
        };
    }

    private static Forms.ToolStripMenuItem AddToggle(Forms.ContextMenuStrip menu, string text, Action<bool> onChange)
    {
        var item = new Forms.ToolStripMenuItem(text) { CheckOnClick = true };
        item.Click += (_, _) => onChange(item.Checked);
        menu.Items.Add(item);
        return item;
    }

    private System.Drawing.Icon CreateTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/Shamsa.ico"));
            if (res != null)
            {
                using var stream = res.Stream;
                using var fullIcon = new System.Drawing.Icon(stream);
                return new System.Drawing.Icon(fullIcon, 32, 32);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Embedded Shamsa.ico load failed, using fallback: " + ex.Message);
        }

        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new System.Drawing.Rectangle(1, 1, 30, 30);
                using var path = new GraphicsPath();
                int r = 9;
                path.AddArc(rect.X, rect.Y, r, r, 180, 90);
                path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
                path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
                path.CloseFigure();
                using var brush = new LinearGradientBrush(rect, System.Drawing.Color.FromArgb(20, 184, 166), System.Drawing.Color.FromArgb(15, 42, 82), 45f);
                g.FillPath(brush, path);
                using var goldPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(229, 184, 91), 1.6f);
                g.DrawPath(goldPen, path);
            }
            _trayIconHandle = bmp.GetHicon();
            return System.Drawing.Icon.FromHandle(_trayIconHandle);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private void OpenDashboard(int? tab = null)
    {
        if (_dashboardWindow == null || !_dashboardWindow.IsLoaded)
        {
            _dashboardWindow = new ToolsDashboardWindow(_converterService, () => _hotkeyService.IsRegistered);
            _dashboardWindow.DemoDonutRequested += () => _donutWindow?.ShowDemo();
            _dashboardWindow.OpenShelfRequested += () => _shelfWindow?.ShowShelf();
            _dashboardWindow.ExitAppRequested += () => Shutdown();
            _dashboardWindow.DemoFolderPreviewRequested += folder => _folderWindow?.ShowDemo(folder);
            WireFeatureEvents(_dashboardWindow);
            _dashboardWindow.HotkeyRecordingChanged += paused =>
            {
                _hotkeyPausedForRecording = paused;
                if (paused) _hotkeyService.Unregister();
                else ApplySettings();
            };
            _dashboardWindow.Closed += (_, _) =>
            {
                _hotkeyPausedForRecording = false;
                ApplySettings();
                _dashboardWindow = null;
            };
            _dashboardWindow.Show();
        }

        if (_dashboardWindow.WindowState == WindowState.Minimized) _dashboardWindow.WindowState = WindowState.Normal;
        if (tab.HasValue) _dashboardWindow.SelectTab(tab.Value);
        _dashboardWindow.Activate();
    }

    private async void OnDonutHotkeyPressed()
    {
        if (_donutWindow == null || _donutWindow.IsShowingProgress) return;

        HideHoverPreview(releaseFileNow: true);

        if (_donutWindow.IsVisible && _donutWindow.IsShortcutMode)
        {
            _donutWindow.HideMenu();
            return;
        }

        var winPos = Forms.Cursor.Position;
        var cursorPos = new System.Windows.Point(winPos.X, winPos.Y);

        var files = await _hoverWorker.RunAsync(() => _hoverService.GetTargetFilesForShortcut());
        if (files == null || files.Length == 0)
        {
            ShowNotification("ابتدا یک یا چند فایل را در File Explorer یا دسکتاپ انتخاب کنید (یا موس را روی فایل ببرید) و سپس شورتکات را بزنید.");
            return;
        }

        _donutWindow.OpenForFiles(files, cursorPos);
    }

    private async void OrganizeDownloadsNow()
    {
        try
        {
            string folder = NativeMethods.GetDownloadsFolder();
            var organizer = new SmartOrganizerService();
            var result = await Task.Run(() => organizer.Execute(organizer.BuildPlan(folder)));
            Notifier.Show(result.Summary, result.Done.Count > 0 ? folder : null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Quick organize failed", ex);
        }
    }

    // ------------------------------------------------------------------ polling loop

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            PollOnce();
        }
        catch (Exception ex)
        {
            AppLog.Error("Poll tick failed", ex);
        }
    }

    private void PollOnce()
    {
        var settings = SettingsService.Current;
        bool ctrlDown = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        bool leftDown = NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON);
        bool rightDown = NativeMethods.IsKeyDown(NativeMethods.VK_RBUTTON);
        bool tabDown = NativeMethods.IsKeyDown(NativeMethods.VK_TAB);
        bool escDown = NativeMethods.IsKeyDown(NativeMethods.VK_ESCAPE);

        var winPos = Forms.Cursor.Position;
        var currentPos = new System.Windows.Point(winPos.X, winPos.Y);

        // ---- 1. Ctrl + Drag → donut menu
        if (leftDown && !_wasLeftDown)
        {
            _leftDownPos = currentPos;
            _donutOpenedThisDrag = false;
            _pressOnShellItem = settings.DonutEnabled
                ? _hoverWorker.RunAsync(() => ExplorerHoverService.IsShellItemAt(winPos.X, winPos.Y))
                : null;
        }
        _wasLeftDown = leftDown;

        if (leftDown)
        {
            // هنگام کلیک/کشیدن، فایل ویدیو را فوراً آزاد کن تا در انتقال/تغییرنام/حذف قفل نماند
            HideHoverPreview(releaseFileNow: true);
            _donutHideAt = null;

            if (_donutWindow != null && _donutWindow.IsVisible && !_donutWindow.IsShowingProgress && !_donutWindow.IsDemoMode && !_donutWindow.IsShortcutMode)
            {
                if (tabDown && !_wasTabDown) _donutWindow.FlipFace();
                if (escDown) _donutWindow.HideMenu();
            }
            else if (!_donutOpenedThisDrag && ctrlDown && _donutWindow != null && !_donutWindow.IsShowingProgress && !_donutWindow.IsShortcutMode
                     && Distance(currentPos, _leftDownPos) > 12
                     && _pressOnShellItem is { IsCompletedSuccessfully: true, Result: true })
            {
                _donutOpenedThisDrag = true;
                _donutWindow.OpenAt(currentPos);
            }

            _wasTabDown = tabDown;
            return;
        }
        _wasTabDown = tabDown;

        if (_donutWindow != null && _donutWindow.IsVisible && !_donutWindow.IsShowingProgress && !_donutWindow.IsDemoMode && !_donutWindow.IsShortcutMode)
        {
            _donutHideAt ??= DateTime.Now.AddMilliseconds(250);
            if (DateTime.Now >= _donutHideAt)
            {
                _donutWindow.HideMenu();
                _donutHideAt = null;
            }
        }

        // ---- 2. Hover preview (فوق‌سریع، پایدار روی فایل، و ایمن هنگام ورود موس به روی پیش‌نمایش)
        if (!settings.HoverPreviewEnabled || _previewWindow == null || rightDown || (_donutWindow?.IsVisible ?? false))
        {
            HideHoverPreview(releaseFileNow: rightDown);
            return;
        }

        // Watchdog: اگر به هر دلیل یک کوئری UIAutomation بیش از ۹۰۰ میلی‌ثانیه طول کشید، قفل نشود
        if (_isCheckingHover && (DateTime.Now - _hoverQueryStartedAt).TotalMilliseconds > 900)
        {
            _isCheckingHover = false;
            _hoverQuerySeq++;
        }

        // اگر موس وارد خود پنجره پیش‌نمایش شد (مثلاً برای رفتن روی فایل‌های زیرش) -> فوراً پیش‌نمایش را ببند و آماده فایل زیرین شو!
        if ((_previewWindow.IsVisible || (_folderWindow?.IsVisible ?? false)) && NativeMethods.IsCursorOverOwnProcess(winPos.X, winPos.Y))
        {
            HideHoverPreview(releaseFileNow: false);
            _lastCursor = currentPos;
            _hoverStartTime = DateTime.Now;
            _hoverQueriedHere = false;
            return;
        }

        if (_activeHoverTarget != null)
        {
            var (surfaceNow, _, _) = NativeMethods.GetShellSurfaceAt(winPos.X, winPos.Y);
            bool overShell = surfaceNow != ShellSurfaceKind.None;
            bool insideSameFile = overShell && IsInsideTarget(_activeHoverTarget, currentPos, margin: 5);

            if (insideSameFile)
            {
                // موس هنوز روی همین فایل است → پیش‌نمایش کاملاً ثابت می‌ماند
                if (!_isCheckingHover && (DateTime.Now - _lastHoverQuery).TotalMilliseconds >= 1100)
                    _ = QueryHoverAsync(currentPos, isRecheck: true);
                return;
            }

            // موس از محدوده فایل خارج شد → فوراً پیش‌نمایش را ببند و تایمر را برای فایل جدید آماده کن
            HideHoverPreview(releaseFileNow: false);
            _lastCursor = currentPos;
            _hoverStartTime = DateTime.Now;
            _hoverQueriedHere = false;
            return;
        }

        // اگر موس اصلاً روی Explorer یا دسکتاپ نیست، هیچ کوئری سنگینی اجرا نکن
        var (underSurface, _, _) = NativeMethods.GetShellSurfaceAt(winPos.X, winPos.Y);
        if (underSurface == ShellSurfaceKind.None)
        {
            _lastCursor = currentPos;
            _hoverStartTime = DateTime.Now;
            _hoverQueriedHere = false;
            return;
        }

        if (Distance(currentPos, _lastCursor) > 10)
        {
            _lastCursor = currentPos;
            _hoverStartTime = DateTime.Now;
            _hoverQueriedHere = false;
            return;
        }

        // حالت مرور سریع (Chain-Hover): هنگام حرکت بین فایل‌ها تأخیر به ۴۰ میلی‌ثانیه کاهش می‌یابد
        double effectiveDelay = (DateTime.Now - _lastPreviewClosedAt).TotalMilliseconds < 1200
            ? Math.Min(settings.HoverDelayMs, 40)
            : settings.HoverDelayMs;

        if (_isCheckingHover || (DateTime.Now - _hoverStartTime).TotalMilliseconds < effectiveDelay)
            return;

        if (_hoverQueriedHere && (DateTime.Now - _lastHoverQuery).TotalMilliseconds < 600)
            return;

        _ = QueryHoverAsync(currentPos, isRecheck: false);
    }

    private static bool IsInsideTarget(HoverTarget target, System.Windows.Point pt, double margin)
    {
        if (!target.ItemBounds.IsEmpty && target.ItemBounds.Width > 0 && target.ItemBounds.Height > 0)
        {
            var r = target.ItemBounds;
            return pt.X >= r.Left - margin && pt.X <= r.Right + margin
                && pt.Y >= r.Top - margin && pt.Y <= r.Bottom + margin;
        }
        return Distance(pt, target.CursorPos) <= 32;
    }

    private async Task QueryHoverAsync(System.Windows.Point queriedAt, bool isRecheck)
    {
        long seq = ++_hoverQuerySeq;
        _isCheckingHover = true;
        _hoverQueriedHere = true;
        _hoverQueryStartedAt = DateTime.Now;
        _lastHoverQuery = DateTime.Now;
        try
        {
            var target = await _hoverWorker.RunAsync(() => _hoverService.GetHoveredFile());
            if (seq != _hoverQuerySeq) return; // کوئری جدیدتر آمده یا کنسل شده

            var nowWin = Forms.Cursor.Position;
            var nowPos = new System.Windows.Point(nowWin.X, nowWin.Y);

            if (_wasLeftDown || NativeMethods.IsKeyDown(NativeMethods.VK_RBUTTON)
                || !SettingsService.Current.HoverPreviewEnabled
                || (_donutWindow?.IsVisible ?? false))
            {
                HideHoverPreview(releaseFileNow: _wasLeftDown);
                return;
            }

            if (target != null && (IsInsideTarget(target, nowPos, margin: 10) || Distance(queriedAt, nowPos) <= 22))
            {
                _activeHoverTarget = target;
                if (target.Kind == FileKind.Folder && _folderWindow != null)
                {
                    _previewWindow?.HidePreview(releaseFileNow: false);
                    _folderWindow.ShowPreview(target);
                }
                else
                {
                    _folderWindow?.HidePreview();
                    _previewWindow?.ShowPreview(target);
                }
            }
            else if (isRecheck)
            {
                HideHoverPreview(releaseFileNow: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hover query failed: " + ex.Message);
        }
        finally
        {
            if (seq == _hoverQuerySeq)
                _isCheckingHover = false;
        }
    }

    private void HideHoverPreview(bool releaseFileNow = false)
    {
        _hoverQuerySeq++; // لغو هر کوئری در حال اجرا تا پنجره بسته شده دوباره باز نشود
        _isCheckingHover = false;
        if (_activeHoverTarget != null)
            _lastPreviewClosedAt = DateTime.Now;
        _activeHoverTarget = null;
        _previewWindow?.HidePreview(releaseFileNow);
        _folderWindow?.HidePreview();
    }

    private static double Distance(System.Windows.Point a, System.Windows.Point b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    // ------------------------------------------------------------------ shutdown

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        try
        {
            _pollTimer?.Stop();
            _updateTimer?.Stop();
            _autoOrganizer.Dispose();
            _hotkeyService.Dispose();
            _hoverWorker.Dispose();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            if (_trayIconHandle != IntPtr.Zero) NativeMethods.DestroyIcon(_trayIconHandle);

            _idleInstallTimer?.Stop();
            ShutdownFeatures();
            _previewWindow?.Close();
            _folderWindow?.Close();
            _showEvent?.Dispose();
            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
            }
            AppLog.Info("Shamsa exited");
        }
        catch { }
    }
}
