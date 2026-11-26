using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shamsa.Desktop;
using Shamsa.FolderStudio;
using Shamsa.Recording;
using Shamsa.Services;
using WDock = System.Windows.Controls.Dock;

namespace Shamsa.Views;

/// <summary>What the hub needs from the app shell (created once by App).</summary>
public sealed class HubActions
{
    public required Func<ToolsDashboardWindow> CreateDashboard { get; init; }
    public Action? OpenShelf { get; init; }
    public Action? DemoDonut { get; init; }
    public Action? OrganizeDownloadsNow { get; init; }
    public Action? ExitApp { get; init; }
}

/// <summary>
/// «نگارخانه»: the single main window. A right-hand (RTL) sidebar groups every feature — files, capture,
/// folders, the whole desktop studio, Windows personalisation, settings and help — and pages are created
/// lazily on first visit and then cached. Only Shamsa's own windows are re-parented here; nothing on the
/// user's desktop is read for this window.
/// </summary>
public sealed class MainHubWindow : Window
{
    /// <summary>Set by App so feature code (e.g. DesktopFeature.ShowStudio) can open a hub page.</summary>
    public static Action<string>? Opener { get; set; }

    public const string AppTitle = "نگارخانه";
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string GuideUrl = "https://github.com/seyedebrahim0211-oss/Shamsa/blob/main/docs/USER_GUIDE_FA.md";

    private sealed record Group(string Id, string Title);

    private sealed class HubPage
    {
        public required string Id { get; init; }
        public required string Group { get; init; }
        public required string Glyph { get; init; }
        public required string Title { get; init; }
        public string Subtitle { get; init; } = "";
        public string Keywords { get; init; } = "";
        public required Func<FrameworkElement> Factory { get; init; }
        /// <summary>Wrap in a ScrollViewer (pages whose content has no scroller of its own).</summary>
        public bool Scroll { get; init; }
        /// <summary>Rebuild on every visit (cheap status pages).</summary>
        public bool Volatile { get; init; }
        /// <summary>Hosted by the shared desktop-studio view; value = its page id.</summary>
        public string? DesktopPage { get; init; }
        public RadioButton? Nav { get; set; }
    }

    private static readonly Group[] Groups =
    {
        new("home", "خانه"),
        new("files", "فایل‌ها"),
        new("capture", "ضبط و تصویر"),
        
        new("desktop", "دسکتاپ"),
        
        new("settings", "تنظیمات و به‌روزرسانی"),
        
    };

    private readonly HubActions _actions;
    private readonly List<HubPage> _pages = new();
    private readonly Dictionary<string, FrameworkElement> _cache = new();
    private readonly Dictionary<string, Expander> _sections = new();
    private readonly Dictionary<string, TextBlock> _groupHeaders = new();

    private ToolsDashboardWindow? _dashboard;
    private RecorderWindow? _recorder;
    private FolderStudioWindow? _folderStudio;
    private DesktopStudioView? _desktopView;

    // chrome
    private readonly ColumnDefinition _sideColumn = new() { Width = new GridLength(248) };
    private readonly StackPanel _navPanel = new() { Margin = new Thickness(10, 4, 10, 8) };
    private readonly TextBox _search = new();
    private readonly TextBlock _searchHint = new();
    private readonly Grid _searchRow = new();
    private readonly StackPanel _brandText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _restore = new();
    private readonly TextBlock _restoreLabel = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly FrameworkElement _headerBlock;
    private readonly ContentControl _host = new() { Focusable = false };
    private bool _collapsed;
    private string _currentId = "home";
    private bool _syncingNav;

    public string CurrentPageId => _currentId;

    public MainHubWindow(HubActions actions)
    {
        _actions = actions;
        Title = AppTitle;
        MinWidth = 980; MinHeight = 640;
        var work = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(1320, work.Width - 40));
        Height = Math.Max(MinHeight, Math.Min(840, work.Height - 40));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = FlowDirection.LeftToRight;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13.5;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/Shamsa.ico")); } catch { }
        SourceInitialized += (_, _) => ApplyDarkTitleBar();

        RegisterPages();

        var root = new Grid { FlowDirection = FlowDirection.RightToLeft };
        root.ColumnDefinitions.Add(_sideColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(BuildSidebar());

        // ---- content
        var content = new Grid { Margin = new Thickness(28, 20, 28, 16) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        _headerBlock = BuildHeader();
        content.Children.Add(_headerBlock);
        Grid.SetRow(_host, 1);
        content.Children.Add(_host);
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;

        PreviewKeyDown += OnPreviewKeyDown;
        Navigate("home");
    }

    // ================================================================== registry

    private void RegisterPages()
    {
        Add(new HubPage
        {
            Id = "home", Group = "home", Glyph = "", Title = "نمای کلی",
            Subtitle = "همه امکانات نگارخانه در یک نگاه؛ هر کارت را روشن/خاموش کن یا برای جزئیات واردش شو.",
            Keywords = "home overview dashboard خانه داشبورد وضعیت", Factory = BuildHome, Scroll = true, Volatile = true
        });

        // ---- files
        Add(new HubPage
        {
            Id = "files.preview", Group = "files", Glyph = "", Title = "پیش‌نمایش‌ها و کارت پوشه",
            Subtitle = "مکث موس روی ویدیو، تصویر، سند یا پوشه در Explorer و دسکتاپ؛ اندازه، تأخیر و ظاهر کارت شیشه‌ای پوشه.",
            Keywords = "preview hover video image document pdf word folder card glass پیش‌نمایش ویدیو تصویر سند پوشه کارت شیشه‌ای تأخیر اندازه",
            Scroll = true, Factory = () => Stack(Dash().DetachNamed("SecPreview"), Dash().DetachNamed("SecFolderCard"))
        });
        Add(new HubPage
        {
            Id = "files.donut", Group = "files", Glyph = "", Title = "منوی دایره‌ای و قفسه",
            Subtitle = "تبدیل سریع با Ctrl + کشیدن فایل یا شورتکات، و قفسه موقت برای جمع کردن فایل‌ها از چند پوشه.",
            Keywords = "donut radial menu shortcut alt+q shelf drop منوی دایره‌ای شورتکات قفسه موقت",
            Scroll = true, Factory = BuildDonutPage
        });
        Add(new HubPage
        {
            Id = "files.organize", Group = "files", Glyph = "", Title = "مرتب‌سازی هوشمند",
            Subtitle = "دسته‌بندی فایل‌های یک پوشه (مثل Downloads) با پیش‌نمایش و بازگردانی کامل.",
            Keywords = "organize sort downloads auto مرتب‌سازی دسته‌بندی دانلود خودکار",
            Factory = BuildOrganizePage
        });
        Add(new HubPage
        {
            Id = "files.rename", Group = "files", Glyph = "", Title = "تغییر نام گروهی",
            Subtitle = "تغییر نام ده‌ها فایل با الگو، شماره‌گذاری و پیش‌نمایش پیش از اجرا.",
            Keywords = "rename batch تغییر نام گروهی الگو", Factory = () => Dash().DetachTab(2) ?? Empty()
        });
        Add(new HubPage
        {
            Id = "files.duplicates", Group = "files", Glyph = "", Title = "فایل‌های تکراری",
            Subtitle = "پیدا کردن نسخه‌های تکراری با مقایسه محتوا و انتقال امن به سطل بازیافت.",
            Keywords = "duplicates duplicate hash تکراری", Factory = () => Dash().DetachTab(3) ?? Empty()
        });
        Add(new HubPage
        {
            Id = "files.convert", Group = "files", Glyph = "", Title = "مبدل و کم‌حجم‌سازی",
            Subtitle = "تبدیل ویدیو، صدا، تصویر و فشرده‌سازی؛ کاملاً آفلاین.",
            Keywords = "convert converter compress mp4 mp3 gif webp zip ffmpeg مبدل تبدیل کم‌حجم",
            Factory = () => Dash().DetachTab(4) ?? Empty()
        });

        // ---- capture / folders
        Add(new HubPage
        {
            Id = "capture.recorder", Group = "capture", Glyph = "", Title = "ضبط صفحه و اسکرین‌شات",
            Subtitle = "ضبط کل صفحه، ناحیه یا یک پنجره با صدا، مکث، نمایش کلیک‌ها، میانبرها و فرمان صوتی.",
            Keywords = "record recorder screen screenshot capture video audio ضبط صفحه اسکرین‌شات فیلم صدا",
            Factory = BuildRecorderPage
        });
        Add(new HubPage
        {
            Id = "folders.studio", Group = "files", Glyph = "", Title = "استودیو پوشه",
            Subtitle = "آیکون دلخواه برای پوشه‌ها و قفل پوشه‌های خصوصی با رمزنگاری AES-256.",
            Keywords = "folder icon lock password encrypt aes پوشه آیکون قفل رمز",
            Factory = BuildFolderStudioPage
        });

        // ---- desktop (one shared studio view)
        foreach (var p in DesktopStudioView.Pages)
            Add(new HubPage
            {
                Id = "desktop." + p.Id, Group = "desktop", Glyph = p.Glyph, Title = p.Title, Keywords = p.Keywords,
                DesktopPage = p.Id, Factory = () => DesktopView()
            });
        Add(new HubPage
        {
            Id = "personalize", Group = "desktop", Glyph = "", Title = "شخصی‌سازی ویندوز",
            Keywords = "personalize windows icons this pc recycle bin wallpaper cursor شخصی‌سازی آیکون این رایانه سطل بازیافت تصویر زمینه نشانگر",
            DesktopPage = "personalize", Factory = () => DesktopView()
        });

        // ---- settings / help
        Add(new HubPage
        {
            Id = "settings", Group = "settings", Glyph = "", Title = "تنظیمات و به‌روزرسانی",
            Subtitle = "ظاهر و تم، به‌روزرسانی امن، موتور FFmpeg و اجرای خودکار با ویندوز.",
            Keywords = "settings theme update ffmpeg startup logs تنظیمات تم به‌روزرسانی اجرای خودکار لاگ",
            Scroll = true,
            Factory = () => Stack(Dash().DetachNamed("SecAppearance"), Dash().DetachNamed("SecUpdate"),
                                  Dash().DetachNamed("SecFfmpeg"), Dash().DetachNamed("SecSystem"))
        });
        Add(new HubPage
        {
            Id = "help", Group = "settings", Glyph = "", Title = "راهنما و درباره",
            Subtitle = "راهنمای کامل فارسی، میانبرها و اطلاعات نسخه.",
            Keywords = "help guide about version shortcuts راهنما درباره نسخه میانبر", Factory = BuildHelpPage, Scroll = true
        });
    }

    private void Add(HubPage p) => _pages.Add(p);

    private HubPage? Find(string id) => _pages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps legacy arguments (--tab=N, --desktop-studio=page …) and aliases to hub page ids.</summary>
    public static string NormalizePageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "home";
        id = id.Trim().ToLowerInvariant();
        if (int.TryParse(id, out int tab))
            return tab >= 0 && tab < ToolsDashboardWindow.TabPageIds.Length ? ToolsDashboardWindow.TabPageIds[tab] : "home";
        return id switch
        {
            "dashboard" or "guide" => "home",
            "recorder" or "record" or "capture" => "capture.recorder",
            "folder-studio" or "folders" or "folder" => "folders.studio",
            "desktop" or "desktop-studio" => "desktop.home",
            "organize" or "rename" or "duplicates" or "convert" or "preview" or "donut" => "files." + id,
            _ when DesktopStudioView.IsPage(id) && id != "home" => id == "personalize" ? "personalize" : "desktop." + id,
            _ => id
        };
    }

    // ================================================================== navigation

    /// <summary>Shows a page (creating it on first visit). Unknown ids fall back to the overview.</summary>
    public void Navigate(string id)
    {
        id = NormalizePageId(id);
        var page = Find(id) ?? Find("home")!;
        _currentId = page.Id;
        if (_sections.TryGetValue(page.Group, out var section)) section.IsExpanded = true;

        FrameworkElement view;
        if (page.DesktopPage != null)
        {
            var dv = DesktopView();
            view = dv;
            SetHeader(page.Title, page.Subtitle);
            dv.ShowPage(page.DesktopPage); // raises HeaderChanged with the page's own subtitle
        }
        else
        {
            if (page.Volatile || !_cache.TryGetValue(page.Id, out view!))
            {
                try { view = page.Factory(); }
                catch (Exception ex)
                {
                    AppLog.Error($"Hub page {page.Id} failed", ex);
                    view = Muted("این بخش بارگذاری نشد. جزئیات در گزارش برنامه ثبت شد.");
                }
                if (page.Scroll) view = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 6, 0) };
                if (!page.Volatile) _cache[page.Id] = view;
            }
            SetHeader(page.Title, page.Subtitle);
        }

        if (!ReferenceEquals(_host.Content, view))
        {
            _host.Content = view;
            FadeIn(view);
        }
        if (view is ScrollViewer sv && page.Volatile) sv.ScrollToTop();

        _syncingNav = true;
        try { foreach (var p in _pages) if (p.Nav != null) p.Nav.IsChecked = ReferenceEquals(p, page); }
        finally { _syncingNav = false; }
        if (page.Nav is { } nav) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => nav.BringIntoView());
    }

    /// <summary>Brings the hub to the front and opens a page.</summary>
    public void ShowPage(string id)
    {
        Navigate(id);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private static void FadeIn(UIElement e)
    {
        // A single cheap opacity animation (no transforms/blur) so page switches feel smooth on any GPU.
        var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() };
        e.BeginAnimation(OpacityProperty, anim);
    }

    private void SetHeader(string? title, string subtitle)
    {
        _headerBlock.Visibility = title == null ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = title ?? "";
        _subtitle.Text = subtitle;
        _subtitle.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_collapsed) SetCollapsed(false);
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _search.IsKeyboardFocused && _search.Text.Length > 0)
        {
            _search.Text = "";
            e.Handled = true;
        }
    }

    // ================================================================== sidebar

    private UIElement BuildSidebar()
    {
        var side = new DockPanel();

        // brand: logo + name + collapse toggle
        var brand = new DockPanel { Margin = new Thickness(14, 14, 10, 10), Height = 44 };
        var collapse = IconButton("", "جمع/باز کردن نوار کناری");
        collapse.Click += (_, _) => SetCollapsed(!_collapsed);
        DockPanel.SetDock(collapse, WDock.Left);
        // In RTL the "Left" dock edge is the far (inner) side of the sidebar.
        var exit = IconButton("", "خروج کامل از نگارخانه");
        exit.Click += (_, _) => _actions.ExitApp?.Invoke();
        DockPanel.SetDock(exit, WDock.Left);
        brand.Children.Add(exit);
        brand.Children.Add(collapse);
        var logo = new Image { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Shamsa-logo.png")); } catch { }
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal };
        brandRow.Children.Add(logo);
        var name = new TextBlock { Text = AppTitle, FontSize = 20, FontWeight = FontWeights.Bold, LineHeight = 26 };
        name.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
        _brandText.Children.Add(name);
        var tag = new TextBlock { Text = "دسکتاپ و فایل‌ها به سلیقه تو", FontSize = 11.5 };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _brandText.Children.Add(tag);
        brandRow.Children.Add(_brandText);
        brand.Children.Add(brandRow);
        DockPanel.SetDock(brand, WDock.Top);
        side.Children.Add(brand);

        // search
        _searchRow.Margin = new Thickness(14, 0, 14, 10);
        _search.Padding = new Thickness(30, 6, 8, 6);
        _search.MinHeight = 36;
        _search.ToolTip = "جستجوی همه بخش‌ها و تنظیمات (Ctrl+F)";
        _search.TextChanged += (_, _) => OnSearchChanged();
        _searchRow.Children.Add(_search);
        var glass = new TextBlock { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 0) };
        glass.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _searchRow.Children.Add(glass);
        _searchHint.Text = "جستجو در نگارخانه…";
        _searchHint.IsHitTestVisible = false;
        _searchHint.VerticalAlignment = VerticalAlignment.Center;
        _searchHint.Margin = new Thickness(32, 0, 8, 0);
        _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _searchRow.Children.Add(_searchHint);
        DockPanel.SetDock(_searchRow, WDock.Top);
        side.Children.Add(_searchRow);

        // footer: restore Windows desktop (always visible)
        var footer = new StackPanel { Margin = new Thickness(14, 8, 14, 14) };
        var divider = new Border { Height = 1.5, Margin = new Thickness(0, 0, 0, 10) };
        divider.SetResourceReference(Border.BackgroundProperty, "PersianDividerBrush");
        footer.Children.Add(divider);
        var restoreRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        restoreRow.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        _restoreLabel.Text = "بازگردانی دسکتاپ ویندوز";
        _restoreLabel.Margin = new Thickness(8, 0, 0, 0);
        _restoreLabel.VerticalAlignment = VerticalAlignment.Center;
        restoreRow.Children.Add(_restoreLabel);
        _restore.Content = restoreRow;
        _restore.Padding = new Thickness(10, 9, 10, 9);
        _restore.ToolTip = "همه بخش‌های محیط دسکتاپ خاموش و تسک‌بار ویندوز فوراً برگردانده می‌شود";
        TryStyle(_restore, "DangerButton");
        _restore.Click += (_, _) =>
        {
            DesktopFeature.RestoreWindowsDesktop();
            _desktopView?.ReloadFromStore("دسکتاپ پیش‌فرض ویندوز بازگردانده شد");
            if (_currentId == "home") Navigate("home");
            Notifier.Show("دسکتاپ پیش‌فرض ویندوز بازگردانده شد.");
        };
        footer.Children.Add(_restore);
        DockPanel.SetDock(footer, WDock.Bottom);
        side.Children.Add(footer);

        // grouped navigation
        foreach (var g in Groups)
        {
            var members = _pages.Where(p => p.Group == g.Id).ToList();
            if (members.Count == 0) continue;
            var header = new TextBlock { Text = g.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, _navPanel.Children.Count == 0 ? 2 : 14, 10, 4) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _groupHeaders[g.Id] = header;
            var links = new StackPanel();
            if (members.Count > 1)
            {
                var section = new Expander { Header = header, Content = links, IsExpanded = false };
                _sections[g.Id] = section;
                _navPanel.Children.Add(section);
            }
            else _navPanel.Children.Add(links);
            foreach (var p in members)
            {
                var nav = NavButton(p);
                p.Nav = nav;
                links.Children.Add(nav);
            }
        }
        var navScroll = new ScrollViewer { Content = _navPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        side.Children.Add(navScroll);

        var border = new Border { Child = side, BorderThickness = new Thickness(0, 0, 1, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "SidebarBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private RadioButton NavButton(HubPage p)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new TextBlock { Text = p.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        row.Children.Add(glyph);
        var label = new TextBlock { Text = p.Title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0), Tag = "label" };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var rb = new RadioButton { Content = row, GroupName = "hubnav", ToolTip = p.Title };
        TryStyle(rb, "HubNavItem");
        rb.Checked += (_, _) => { if (!_syncingNav) Navigate(p.Id); };
        return rb;
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _sideColumn.Width = new GridLength(collapsed ? 64 : 248);
        var vis = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _brandText.Visibility = vis;
        _searchRow.Visibility = vis;
        _restoreLabel.Visibility = vis;
        foreach (var section in _sections.Values) section.IsExpanded = collapsed || section == _sections.GetValueOrDefault(Find(_currentId)?.Group ?? "");
        foreach (var p in _pages)
            if (p.Nav?.Content is Grid g)
            {
                g.ColumnDefinitions[1].Width = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                foreach (var tb in g.Children.OfType<TextBlock>().Where(t => "label".Equals(t.Tag))) tb.Visibility = vis;
            }
        _navPanel.Margin = collapsed ? new Thickness(8, 4, 8, 8) : new Thickness(10, 4, 10, 8);
        if (collapsed && _search.Text.Length > 0) _search.Text = "";
    }

    // ================================================================== search

    private static string Norm(string s) =>
        s.Replace('ي', 'ی').Replace('ك', 'ک').Replace("‌", "").Replace(" ", "").ToLowerInvariant();

    private IEnumerable<HubPage> Matches(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Norm).ToArray();
        return _pages.Where(p =>
        {
            string group = Groups.FirstOrDefault(g => g.Id == p.Group)?.Title ?? "";
            string hay = Norm(p.Title + p.Subtitle + p.Keywords + group);
            return terms.All(t => hay.Contains(t));
        });
    }

    private string _beforeSearch = "home";

    private void OnSearchChanged()
    {
        string q = _search.Text.Trim();
        _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q.Length == 0)
        {
            foreach (var p in _pages) if (p.Nav != null) p.Nav.Visibility = Visibility.Visible;
            foreach (var section in _sections.Values) section.Visibility = Visibility.Visible;
            if (_host.Content is FrameworkElement { Tag: "search" }) Navigate(_beforeSearch);
            return;
        }
        if (_host.Content is not FrameworkElement { Tag: "search" }) _beforeSearch = _currentId;

        var hits = Matches(q).ToList();
        foreach (var p in _pages) if (p.Nav != null) p.Nav.Visibility = hits.Contains(p) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (gid, group) in _sections) { group.Visibility = hits.Any(p => p.Group == gid) ? Visibility.Visible : Visibility.Collapsed; group.IsExpanded = true; }

        var list = new StackPanel();
        if (hits.Count == 0)
            list.Children.Add(Muted("چیزی پیدا نشد. واژه دیگری امتحان کن؛ مثلاً «داک»، «آیکون»، «ضبط» یا «تم»."));
        foreach (var p in hits)
        {
            string group = Groups.FirstOrDefault(g => g.Id == p.Group)?.Title ?? "";
            list.Children.Add(ResultCard(p, group));
        }
        var sv = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Tag = "search" };
        _host.Content = sv;
        SetHeader("نتیجه جستجو", $"{hits.Count.ToString().ToPersianDigits()} بخش برای «{q}»");
        _syncingNav = true;
        try { foreach (var p in _pages) if (p.Nav != null) p.Nav.IsChecked = false; }
        finally { _syncingNav = false; }
    }

    private UIElement ResultCard(HubPage p, string group)
    {
        var row = new DockPanel();
        var glyph = new TextBlock { Text = p.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 20, Width = 40, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        DockPanel.SetDock(glyph, WDock.Left);
        row.Children.Add(glyph);
        var text = new StackPanel();
        var t = new TextBlock { Text = p.Title, FontSize = 15, FontWeight = FontWeights.SemiBold };
        text.Children.Add(t);
        text.Children.Add(Muted(group + (string.IsNullOrEmpty(p.Subtitle) ? "" : " · " + p.Subtitle)));
        row.Children.Add(text);
        var b = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
        b.Click += (_, _) => { string id = p.Id; _search.Text = ""; Navigate(id); };
        return b;
    }

    // ================================================================== header

    private FrameworkElement BuildHeader()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        _title.FontSize = 22;
        _title.FontWeight = FontWeights.Bold;
        _title.TextWrapping = TextWrapping.Wrap;
        _title.LineHeight = 32;
        panel.Children.Add(_title);
        _subtitle.FontSize = 13.5;
        _subtitle.TextWrapping = TextWrapping.Wrap;
        _subtitle.LineHeight = 22;
        _subtitle.Margin = new Thickness(0, 2, 0, 0);
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(_subtitle);
        var divider = new Border { Height = 1.5, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        divider.SetResourceReference(Border.BackgroundProperty, "PersianDividerBrush");
        panel.Children.Add(divider);
        return panel;
    }

    // ================================================================== pages

    private ToolsDashboardWindow Dash()
    {
        if (_dashboard == null)
        {
            _dashboard = _actions.CreateDashboard();
            _dashboard.Host = this;
            _dashboard.NavigateRequested += id => Dispatcher.BeginInvoke(() => Navigate(id));
            // The dashboard's Window.Resources (title-bar styles) are not needed; theme resources are app-wide.
        }
        return _dashboard;
    }

    private DesktopStudioView DesktopView()
    {
        if (_desktopView == null)
        {
            _desktopView = new DesktopStudioView();
            _desktopView.HeaderChanged += (title, subtitle) =>
            {
                var page = Find(_currentId);
                SetHeader(title == null ? null : page?.Title ?? title, subtitle);
            };
            _desktopView.NavigateRequested += id => Navigate(NormalizePageId(id));
        }
        return _desktopView;
    }

    private FrameworkElement BuildRecorderPage()
    {
        _recorder ??= new RecorderWindow();
        return _recorder.DetachForHost(this) ?? Empty();
    }

    private FrameworkElement BuildFolderStudioPage()
    {
        _folderStudio ??= new FolderStudioWindow();
        return _folderStudio.DetachForHost(this) ?? Empty();
    }

    /// <summary>The folder studio page (created if needed) for Explorer verbs: add folders, lock a folder or open a vault.</summary>
    public FolderStudioWindow OpenFolderStudio(int tab = 0)
    {
        ShowPage("folders.studio");
        _folderStudio!.SelectTab(tab);
        return _folderStudio;
    }

    private FrameworkElement BuildOrganizePage()
    {
        var tab = Dash().DetachTab(1) ?? Empty();
        var auto = Dash().DetachNamed("SecAutoOrganize");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (tab.Parent == null) grid.Children.Add(tab);
        if (auto != null)
        {
            auto.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(auto, 1);
            grid.Children.Add(auto);
        }
        return grid;
    }

    private FrameworkElement BuildDonutPage()
    {
        var stack = Stack(Dash().DetachNamed("SecDonut"));
        var card = HubCardPanel(out var body, "قفسه موقت و آزمایش منو", "فایل‌ها را از چند پوشه داخل قفسه شناور بینداز و یکجا به مقصد بکش یا ZIP کن.");
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ActionButton("باز کردن قفسه موقت", "", _actions.OpenShelf, "GoldButton"));
        buttons.Children.Add(ActionButton("نمایش آزمایشی منوی دایره‌ای", "", _actions.DemoDonut, "AccentButton"));
        body.Children.Add(buttons);
        card.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Insert(0, card);
        return stack;
    }

    private FrameworkElement BuildHelpPage()
    {
        var stack = new StackPanel();
        var guide = HubCardPanel(out var g, "راهنمای کامل فارسی", "همه امکانات نگارخانه قدم‌به‌قدم با تصویر: پیش‌نمایش‌ها، منوی دایره‌ای، ضبط صفحه، استودیو پوشه و استودیو دسکتاپ.");
        g.Children.Add(ActionButton("باز کردن راهنما در مرورگر", "", () =>
        {
            try { Process.Start(new ProcessStartInfo(GuideUrl) { UseShellExecute = true }); } catch (Exception ex) { AppLog.Warn("Guide: " + ex.Message); }
        }, "AccentButton"));
        guide.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(guide);

        var keys = HubCardPanel(out var k, "میانبرهای مهم");
        foreach (var (key, what) in new[]
        {
            ("مکث موس روی فایل", "پیش‌نمایش ویدیو، تصویر، سند یا پوشه"),
            ("Alt + Q", "منوی دایره‌ای روی فایل انتخاب‌شده (قابل تغییر)"),
            ("Ctrl + کشیدن فایل", "منوی دایره‌ای تبدیل سریع"),
            ("Ctrl + Alt + R / S", "شروع/توقف ضبط و اسکرین‌شات"),
            ("Ctrl + F (در همین پنجره)", "جستجوی همه بخش‌ها و تنظیمات"),
        })
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var kt = new TextBlock { Text = key, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            kt.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
            row.Children.Add(kt);
            var wt = new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(wt, 1);
            row.Children.Add(wt);
            k.Children.Add(row);
        }
        keys.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(keys);

        var v = typeof(MainHubWindow).Assembly.GetName().Version;
        var about = HubCardPanel(out var a, "درباره نگارخانه");
        a.Children.Add(Muted($"نسخه {(v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}").ToPersianDigits()} · همه پردازش‌ها روی رایانه خودت انجام می‌شود؛ اینترنت فقط برای به‌روزرسانی، آب‌وهوا و ابزارهای اختیاری."));
        var exit = ActionButton("خروج کامل از برنامه", "", _actions.ExitApp, "GhostButton");
        exit.Margin = new Thickness(0, 10, 0, 0);
        a.Children.Add(exit);
        stack.Children.Add(about);
        return stack;
    }

    // ---------------------------------------------------------------- home

    private FrameworkElement BuildHome()
    {
        var root = new StackPanel();
        var s = SettingsService.Current;
        var d = DesktopSettingsStore.Current;

        // quick actions
        var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        quick.Children.Add(ActionButton("اسکرین‌شات", "", () => RecorderFeature.TakeScreenshot(), "GoldButton"));
        quick.Children.Add(ActionButton("شروع سریع دسکتاپ", "", () => Navigate("desktop.home")));
        quick.Children.Add(ActionButton("جستجوی سریع", "", DesktopFeature.ShowLauncher));
        quick.Children.Add(ActionButton("مرتب‌سازی Downloads الان", "", _actions.OrganizeDownloadsNow));
        root.Children.Add(quick);

        var cards = new UniformGrid { Columns = 3 };
        cards.SizeChanged += (_, e) => cards.Columns = e.NewSize.Width < 720 ? 2 : 3;

        cards.Children.Add(FeatureCard("", "پیش‌نمایش کنار موس", "ویدیو، تصویر، سند و کارت شیشه‌ای پوشه",
            s.HoverPreviewEnabled, v => SettingsService.Update(x => x.HoverPreviewEnabled = v), "files.preview"));
        cards.Children.Add(FeatureCard("", "منوی دایره‌ای", "Ctrl + کشیدن یا " + GlobalHotkeyService.FormatShortcut(s.DonutShortcutModifiers, s.DonutShortcutKey),
            s.DonutEnabled, v => SettingsService.Update(x => x.DonutEnabled = v), "files.donut"));
        cards.Children.Add(FeatureCard("", "محیط دسکتاپ", "نوار بالا، داک، جستجوی سریع و ویجت‌ها",
            d.Enabled, v => { var c = DesktopSettingsStore.Current.Clone(); c.Enabled = v; DesktopSettingsStore.Save(c); }, "desktop.home"));
        cards.Children.Add(FeatureCard("", "ضبط صفحه", RecorderStatus(), null, null, "capture.recorder"));
        cards.Children.Add(FeatureCard("", "استودیو پوشه", "آیکون دلخواه و قفل AES-256 برای پوشه‌ها", null, null, "folders.studio"));
        bool autoReady = Directory.Exists(s.AutoOrganizeFolder);
        cards.Children.Add(FeatureCard("", "مرتب‌سازی خودکار", autoReady ? "پوشه: " + Path.GetFileName(s.AutoOrganizeFolder.TrimEnd('\\')) : "اول یک پوشه انتخاب کن",
            autoReady ? s.AutoOrganizeEnabled : null, autoReady ? v => SettingsService.Update(x => x.AutoOrganizeEnabled = v) : null, "files.organize"));
        cards.Children.Add(FeatureCard("", "شخصی‌سازی ویندوز", "آیکون‌ها، این رایانه، تصویر زمینه و…", null, null, "personalize"));
        cards.Children.Add(FeatureCard("", "به‌روزرسانی", UpdateStatus(), null, null, "settings"));
        cards.Children.Add(FeatureCard("", "راهنما", "آموزش تصویری همه امکانات", null, null, "help"));
        root.Children.Add(cards);
        return root;
    }

    private static string RecorderStatus() => RecorderFeature.State switch
    {
        RecorderState.Recording => "در حال ضبط…",
        RecorderState.Paused => "مکث",
        RecorderState.Finalizing => "در حال ذخیره…",
        _ => "آماده · Ctrl+Alt+R"
    };

    private static string UpdateStatus()
    {
        var latest = UpdateService.Latest;
        string current = UpdateService.CurrentVersion.ToString(3).ToPersianDigits();
        return latest != null && latest.Version > UpdateService.CurrentVersion
            ? $"نسخه {latest.Version.ToString(3).ToPersianDigits()} آماده نصب است"
            : $"نسخه فعلی {current}";
    }

    private UIElement FeatureCard(string glyph, string title, string status, bool? on, Action<bool>? toggle, string pageId)
    {
        var border = new Border { Cursor = Cursors.Hand, MinHeight = 118 };
        TryStyle(border, "HubCard");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());

        var top = new DockPanel();
        var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10) };
        icon.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var ig = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ig.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        icon.Child = ig;
        top.Children.Add(icon);
        if (on.HasValue && toggle != null)
        {
            var sw = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "روشن / خاموش", Margin = new Thickness(0) };
            var stateText = new TextBlock { Text = on.Value ? "روشن" : "خاموش", FontSize = 12 };
            stateText.SetResourceReference(TextBlock.ForegroundProperty, on.Value ? "SuccessBrush" : "MutedBrush");
            sw.Content = stateText;
            sw.Click += (_, e) =>
            {
                bool v = sw.IsChecked == true;
                toggle(v);
                stateText.Text = v ? "روشن" : "خاموش";
                stateText.SetResourceReference(TextBlock.ForegroundProperty, v ? "SuccessBrush" : "MutedBrush");
                e.Handled = true;
            };
            sw.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
            top.Children.Add(sw);
        }
        grid.Children.Add(top);

        var t = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(t, 1);
        grid.Children.Add(t);
        var st = Muted(status);
        st.Margin = new Thickness(0);
        Grid.SetRow(st, 2);
        grid.Children.Add(st);
        border.Child = grid;
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src && FindParent<CheckBox>(src) != null) return;
            Navigate(pageId);
        };
        return border;
    }

    // ================================================================== helpers

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static StackPanel Stack(params FrameworkElement?[] parts)
    {
        var s = new StackPanel();
        foreach (var p in parts) if (p != null && p.Parent == null) s.Children.Add(p);
        return s;
    }

    private static FrameworkElement Empty() => Muted("این بخش در دسترس نیست.");

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 20, Margin = new Thickness(0, 2, 0, 2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private static Border HubCardPanel(out StackPanel body, string title, string? subtitle = null)
    {
        body = new StackPanel();
        var t = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
        body.Children.Add(t);
        if (subtitle != null) body.Children.Add(Muted(subtitle));
        var b = new Border { Child = body };
        TryStyle(b, "Card");
        return b;
    }

    private static Button ActionButton(string text, string glyph, Action? click, string? style = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = row, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8), IsEnabled = click != null };
        if (style != null) TryStyle(b, style);
        b.Click += (_, _) =>
        {
            try { click?.Invoke(); }
            catch (Exception ex) { AppLog.Error("Hub action failed", ex); }
        };
        return b;
    }

    private static Button IconButton(string glyph, string tip)
    {
        var b = new Button { Content = glyph, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        TryStyle(b, "HubIconButton");
        return b;
    }

    private static void TryStyle(FrameworkElement e, string key)
    {
        // Resource reference (not a one-time lookup) so the style resolves even if theme dictionaries load later or are swapped.
        e.SetResourceReference(FrameworkElement.StyleProperty, key);
    }

    // ================================================================== lifetime

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_folderStudio != null && !_folderStudio.TryReleaseHosted()) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _dashboard?.ReleaseHosted(); } catch { }
        try { _recorder?.ReleaseHosted(); } catch { }
        try { _desktopView?.Release(); } catch { }
        _host.Content = null;
        _cache.Clear();
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = SettingsService.Current.ThemeId == "Ivory" ? 0 : 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            int round = 2; // DWMWCP_ROUND (Windows 11; ignored elsewhere)
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        }
        catch { }
    }
}

internal static class HubText
{
    public static string ToPersianDigits(this string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] >= '0' && chars[i] <= '9') chars[i] = (char)('۰' + (chars[i] - '0'));
        return new string(chars);
    }
}
