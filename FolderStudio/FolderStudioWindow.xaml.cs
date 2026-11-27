using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.FolderStudio;

public sealed class StudioFolderItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private string _status = string.Empty;
    public StudioFolderItem(string path) { Path = path; Name = System.IO.Path.GetFileName(path.TrimEnd('\\')); }
    public string Path { get; }
    public string Name { get; }
    public ImageSource? Icon { get => _icon; set { _icon = value; OnChanged(); } }
    public string Status { get => _status; set { _status = value; OnChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class StudioPresetItem
{
    public StudioPresetItem(IconDesign design, string? tip = null)
    {
        Design = design;
        Name = design.Name;
        Tip = tip ?? design.Name;
        Preview = FolderIconRenderer.Render(design, 64);
    }
    public IconDesign Design { get; }
    public string Name { get; }
    public string Tip { get; }
    public ImageSource Preview { get; }
}

public partial class FolderStudioWindow : Window
{
    private readonly ObservableCollection<StudioFolderItem> _folders = new();
    private IconDesign _design;
    private bool _loading = true;
    private bool _syncingPass;
    private CancellationTokenSource? _lockCts;
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _scanDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };

    public FolderStudioWindow()
    {
        InitializeComponent();
        FoldersList.ItemsSource = _folders;
        _folders.CollectionChanged += (_, _) => DropHint.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _design = FolderStudioStore.Data.LastDesign.Clone();
        BuildSwatches(BaseSwatches, hex => { _design.BaseHex = hex; BaseHexBox.Text = hex; RefreshPreview(); });
        BuildSwatches(GradientSwatches, hex => { _design.GradientHex = hex; GradientHexBox.Text = hex; RefreshPreview(); });
        BuildSwatches(InkSwatches, hex => { AutoInkCheck.IsChecked = false; _design.OverlayHex = hex; InkHexBox.Text = hex; RefreshPreview(); },
            new[] { "#FFFFFF", "#1F2937", "#F6D379", "#0F172A", "#FDE68A", "#14B8A6" });
        foreach (var emoji in FolderPresets.Emojis)
        {
            var b = new Button { Content = emoji, Style = (Style)FindResource("EmojiBtn") };
            b.Click += (_, _) => { EmojiBox.Text = emoji; };
            EmojiButtons.Children.Add(b);
        }

        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); RenderPreview(); };
        _scanDebounce.Tick += (_, _) => { _scanDebounce.Stop(); ScanLockFolder(); };

        LoadDesignIntoUi(_design);
        ShellIntegrationCheck.IsChecked = FolderShellIntegration.IsEnabled();
        _loading = false;
        RefreshPreview();
        RefreshLibrary();
        UpdateStrength();
    }

    // ------------------------------------------------------------------ public helpers

    public void SelectTab(int tab) => Tabs.SelectedIndex = Math.Clamp(tab, 0, Tabs.Items.Count - 1);

    public void AddFolders(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = System.IO.Path.GetFullPath(raw.Trim('"')).TrimEnd('\\'); }
            catch { continue; }
            if (!Directory.Exists(path) || _folders.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new StudioFolderItem(path);
            string? problem = FolderIconService.ValidateFolder(path);
            item.Status = problem ?? string.Empty;
            item.Icon = ShellThumbnailService.TryGetShellImage(path, 48, iconOnly: true);
            _folders.Add(item);
        }
    }

    public void SetLockFolder(string folder)
    {
        LockFolderBox.Text = folder;
        SelectTab(1);
    }

    public void SetVault(string vault)
    {
        VaultBox.Text = vault;
        SelectTab(1);
        UnlockPass.Focus();
    }

    // ------------------------------------------------------------------ drag & drop

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (Tabs.SelectedIndex == 1)
        {
            string? vault = paths.FirstOrDefault(p => p.EndsWith(FolderLockService.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(p));
            if (vault != null) { VaultBox.Text = vault; return; }
            string? dir = paths.FirstOrDefault(Directory.Exists);
            if (dir != null) LockFolderBox.Text = dir;
            return;
        }
        var images = paths.Where(p => File.Exists(p) && FolderIconRenderer.IsSupportedImage(p)).ToList();
        if (images.Count > 0 && !paths.Any(Directory.Exists))
        {
            SetImage(images[0]);
            return;
        }
        AddFolders(paths.Where(Directory.Exists));
    }

    // ------------------------------------------------------------------ folders

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = ShellActions.PickFolder(_folders.LastOrDefault()?.Path, Dlg);
        if (folder != null) AddFolders(new[] { folder });
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FoldersList.SelectedItems.Cast<StudioFolderItem>().ToList()) _folders.Remove(item);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0)
        {
            IconStatusText.Text = "ابتدا یک یا چند پوشه اضافه کنید.";
            return;
        }
        ReadUiIntoDesign();
        if (_design.Overlay == IconOverlayKind.Image && FolderIconRenderer.LoadImage(_design.ImagePath) == null)
        {
            IconStatusText.Text = "تصویر انتخاب‌شده قابل خواندن نیست.";
            return;
        }

        string ico;
        try { ico = FolderIconRenderer.EnsureIcoFile(_design); }
        catch (Exception ex)
        {
            AppLog.Error("Creating folder .ico failed", ex);
            IconStatusText.Text = "ساخت فایل آیکون ناموفق بود: " + ex.Message;
            return;
        }

        int ok = 0, failed = 0;
        foreach (var item in _folders)
        {
            try
            {
                FolderIconService.ApplyIcon(item.Path, ico);
                item.Status = "✅ آیکون اعمال شد";
                item.Icon = ShellThumbnailService.TryGetShellImage(item.Path, 48, iconOnly: true);
                FolderStudioStore.AddHistory(item.Path, _design);
                ok++;
            }
            catch (Exception ex)
            {
                item.Status = "⚠ " + ex.Message;
                failed++;
            }
        }
        FolderStudioStore.Data.LastDesign = _design.Clone();
        FolderStudioStore.Save();
        IconStatusText.Text = $"آیکون روی {ok} پوشه اعمال شد" + (failed > 0 ? $" · {failed} پوشه ناموفق" : "") +
                              ". اگر Explorer هنوز آیکون قبلی را نشان می‌دهد، چند ثانیه صبر کنید یا F5 بزنید.";
        if (LibraryFilter.SelectedIndex == 2) RefreshLibrary();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var targets = FoldersList.SelectedItems.Cast<StudioFolderItem>().ToList();
        if (targets.Count == 0) targets = _folders.ToList();
        if (targets.Count == 0) { IconStatusText.Text = "پوشه‌ای در فهرست نیست."; return; }
        int ok = 0;
        foreach (var item in targets)
        {
            try
            {
                FolderIconService.RestoreDefault(item.Path);
                item.Status = "↺ آیکون پیش‌فرض بازگردانده شد";
                item.Icon = ShellThumbnailService.TryGetShellImage(item.Path, 48, iconOnly: true);
                ok++;
            }
            catch (Exception ex) { item.Status = "⚠ " + ex.Message; }
        }
        IconStatusText.Text = $"آیکون پیش‌فرض {ok} پوشه بازگردانده شد.";
    }

    // ------------------------------------------------------------------ designer

    private void BuildSwatches(Panel host, Action<string> onPick, string[]? colors = null)
    {
        foreach (var hex in colors ?? FolderPresets.Palette.Select(p => p.Hex).ToArray())
        {
            var b = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(FolderIconRenderer.ParseColor(hex, Colors.Gray)),
                ToolTip = FolderPresets.Palette.FirstOrDefault(p => p.Hex == hex).Name ?? hex
            };
            b.Click += (_, _) => onPick(hex);
            host.Children.Add(b);
        }
    }

    private void LoadDesignIntoUi(IconDesign d)
    {
        bool was = _loading;
        _loading = true;
        FinishPicker.SelectedIndex = (int)d.Finish;
        BaseHexBox.Text = d.BaseHex;
        GradientCheck.IsChecked = FolderIconRenderer.IsValidHex(d.GradientHex);
        GradientPanel.IsEnabled = GradientCheck.IsChecked == true;
        if (FolderIconRenderer.IsValidHex(d.GradientHex)) GradientHexBox.Text = d.GradientHex;
        OverlayNone.IsChecked = d.Overlay == IconOverlayKind.None;
        OverlayEmoji.IsChecked = d.Overlay == IconOverlayKind.Emoji;
        OverlayText.IsChecked = d.Overlay == IconOverlayKind.Text;
        OverlayImage.IsChecked = d.Overlay == IconOverlayKind.Image;
        EmojiBox.Text = d.Overlay == IconOverlayKind.Emoji ? d.OverlayText : EmojiBox.Text;
        OverlayTextBox.Text = d.Overlay == IconOverlayKind.Text ? d.OverlayText : OverlayTextBox.Text;
        BoldCheck.IsChecked = d.OverlayBold;
        AutoInkCheck.IsChecked = !FolderIconRenderer.IsValidHex(d.OverlayHex);
        if (FolderIconRenderer.IsValidHex(d.OverlayHex)) InkHexBox.Text = d.OverlayHex;
        ImagePathBox.Text = d.ImagePath;
        ImageBadge.IsChecked = d.ImageMode == IconImageMode.Badge;
        ImageFull.IsChecked = d.ImageMode == IconImageMode.Full;
        ImageFit.IsChecked = d.ImageMode == IconImageMode.Fit;
        RoundedCheck.IsChecked = d.RoundedImage;
        _loading = was;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        EmojiPanel.Visibility = OverlayEmoji.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = OverlayText.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InkPanel.Visibility = OverlayEmoji.IsChecked == true || OverlayText.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ImagePanel.Visibility = OverlayImage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GradientPanel.IsEnabled = GradientCheck.IsChecked == true;
        GradientPanel.Opacity = GradientPanel.IsEnabled ? 1 : 0.35;
        InkSwatchesHost.IsEnabled = AutoInkCheck.IsChecked != true;
        InkSwatchesHost.Opacity = InkSwatchesHost.IsEnabled ? 1 : 0.35;
    }

    private void ReadUiIntoDesign()
    {
        _design.Finish = (FolderFinish)Math.Clamp(FinishPicker.SelectedIndex, 0, 3);
        _design.GradientHex = GradientCheck.IsChecked == true && FolderIconRenderer.IsValidHex(GradientHexBox.Text) ? GradientHexBox.Text.Trim() : string.Empty;
        _design.Overlay = OverlayEmoji.IsChecked == true ? IconOverlayKind.Emoji
            : OverlayText.IsChecked == true ? IconOverlayKind.Text
            : OverlayImage.IsChecked == true ? IconOverlayKind.Image : IconOverlayKind.None;
        _design.OverlayText = _design.Overlay switch
        {
            IconOverlayKind.Emoji => EmojiBox.Text.Trim(),
            IconOverlayKind.Text => OverlayTextBox.Text.Trim(),
            _ => string.Empty
        };
        _design.OverlayBold = BoldCheck.IsChecked == true;
        _design.OverlayHex = AutoInkCheck.IsChecked == true ? string.Empty
            : FolderIconRenderer.IsValidHex(InkHexBox.Text) ? InkHexBox.Text.Trim() : "#FFFFFF";
        _design.ImagePath = ImagePathBox.Text;
        _design.ImageMode = ImageFit.IsChecked == true ? IconImageMode.Fit : ImageFull.IsChecked == true ? IconImageMode.Full : IconImageMode.Badge;
        _design.RoundedImage = RoundedCheck.IsChecked == true;
        _design.Name = _design.Overlay switch
        {
            IconOverlayKind.Text when _design.OverlayText.Length > 0 => "پوشه «" + _design.OverlayText + "»",
            IconOverlayKind.Image => "پوشه با تصویر",
            _ => string.IsNullOrWhiteSpace(_design.Name) ? "پوشه سفارشی" : _design.Name
        };
    }

    private void Design_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdatePanels();
        RefreshPreview();
    }

    private void OverlayInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (sender == EmojiBox && EmojiBox.Text.Length > 0) OverlayEmoji.IsChecked = true;
        if (sender == OverlayTextBox && OverlayTextBox.Text.Length > 0) OverlayText.IsChecked = true;
        UpdatePanels();
        RefreshPreview();
    }

    private void BaseHex_Click(object sender, RoutedEventArgs e)
    {
        if (!FolderIconRenderer.IsValidHex(BaseHexBox.Text)) { IconStatusText.Text = "رنگ را به شکل #RRGGBB وارد کنید."; return; }
        _design.BaseHex = BaseHexBox.Text.Trim().ToUpperInvariant();
        RefreshPreview();
    }

    private void GradientHex_Click(object sender, RoutedEventArgs e)
    {
        if (!FolderIconRenderer.IsValidHex(GradientHexBox.Text)) { IconStatusText.Text = "رنگ دوم را به شکل #RRGGBB وارد کنید."; return; }
        RefreshPreview();
    }

    private void InkHex_Click(object sender, RoutedEventArgs e)
    {
        if (!FolderIconRenderer.IsValidHex(InkHexBox.Text)) { IconStatusText.Text = "رنگ نماد را به شکل #RRGGBB وارد کنید."; return; }
        AutoInkCheck.IsChecked = false;
        UpdatePanels();
        RefreshPreview();
    }

    private void PickImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "انتخاب تصویر برای آیکون پوشه",
            Filter = FolderIconRenderer.OpenFileFilter
        };
        if (dlg.ShowDialog(Dlg) == true) SetImage(dlg.FileName);
    }

    private void SetImage(string path)
    {
        if (FolderIconRenderer.LoadImage(path) == null)
        {
            IconStatusText.Text = "این تصویر قابل خواندن نیست (SVG پشتیبانی نمی‌شود).";
            return;
        }
        ImagePathBox.Text = path;
        var mode = FolderIconRenderer.SuggestMode(path);
        ImageBadge.IsChecked = mode == IconImageMode.Badge;
        ImageFull.IsChecked = mode == IconImageMode.Full;
        ImageFit.IsChecked = mode == IconImageMode.Fit;
        OverlayImage.IsChecked = true;
        UpdatePanels();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_loading) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void RenderPreview()
    {
        ReadUiIntoDesign();
        try
        {
            BigPreview.Source = FolderIconRenderer.Render(_design, 256);
            var p48 = FolderIconRenderer.Render(_design, 48);
            var p32 = FolderIconRenderer.Render(_design, 32);
            var p16 = FolderIconRenderer.Render(_design, 16);
            P48L.Source = P48D.Source = p48;
            P32L.Source = P32D.Source = p32;
            P16L.Source = P16D.Source = p16;
        }
        catch (Exception ex) { AppLog.Warn("Folder icon preview failed: " + ex.Message); }
        FavoriteBtn.Content = FolderStudioStore.IsFavorite(_design) ? "♥ علاقه‌مندی" : "♡ علاقه‌مندی";
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        ReadUiIntoDesign();
        FolderStudioStore.ToggleFavorite(_design);
        RenderPreview();
        if (LibraryFilter.SelectedIndex == 1) RefreshLibrary();
    }

    // ------------------------------------------------------------------ library

    private void Library_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        string q = LibrarySearch.Text;
        IEnumerable<StudioPresetItem> items = LibraryFilter.SelectedIndex switch
        {
            1 => FolderStudioStore.Data.Favorites.Where(d => FolderPresets.Matches(d, q)).Select(d => new StudioPresetItem(d)),
            2 => FolderStudioStore.Data.History.Where(h => FolderPresets.Matches(h.Design, q) || h.Folder.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Select(h => new StudioPresetItem(h.Design, $"{h.Design.Name}\n{h.Folder}\n{h.AppliedUtc.ToLocalTime():yyyy/MM/dd HH:mm}")),
            _ => FolderPresets.All.Where(d => FolderPresets.Matches(d, q)).Select(d => new StudioPresetItem(d))
        };
        LibraryList.ItemsSource = items.Take(240).ToList();
    }

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is not StudioPresetItem item) return;
        _design = item.Design.Clone();
        if (_design.Overlay == IconOverlayKind.Emoji) EmojiBox.Text = _design.OverlayText;
        LoadDesignIntoUi(_design);
        RefreshPreview();
    }

    // ------------------------------------------------------------------ shell integration

    private void ShellIntegration_Click(object sender, RoutedEventArgs e)
    {
        bool on = ShellIntegrationCheck.IsChecked == true;
        try
        {
            FolderShellIntegration.SetEnabled(on);
        }
        catch (Exception ex)
        {
            AppLog.Error("Shell integration change failed", ex);
            MessageBox.Show(Dlg, "تغییر منوی کلیک‌راست ممکن نشد: " + ex.Message, "نگارخانه", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ShellIntegrationCheck.IsChecked = FolderShellIntegration.IsEnabled();
    }

    // ------------------------------------------------------------------ lock

    private void BrowseLockFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = ShellActions.PickFolder(LockFolderBox.Text, Dlg);
        if (folder != null) LockFolderBox.Text = folder;
    }

    private void LockFolder_Changed(object sender, TextChangedEventArgs e)
    {
        _scanDebounce.Stop();
        _scanDebounce.Start();
    }

    private async void ScanLockFolder()
    {
        string folder = LockFolderBox.Text.Trim().Trim('"');
        if (string.IsNullOrEmpty(folder)) { LockScanText.Text = string.Empty; return; }
        string? problem = FolderIconService.ValidateFolder(folder);
        if (problem != null) { LockScanText.Text = "⚠ " + problem; return; }
        LockScanText.Text = "در حال بررسی…";
        try
        {
            var scan = await Task.Run(() => FolderLockService.Scan(folder));
            if (LockFolderBox.Text.Trim().Trim('"') != folder) return;
            LockScanText.Text = $"{scan.Files} فایل · {scan.Folders} زیرپوشه · {FileKinds.FormatSize(scan.Bytes)}" +
                (scan.Bytes > FolderLockService.LargeFolderWarningBytes ? " · ⚠ پوشه بزرگ است؛ قفل کردن زمان‌بر و نیازمند فضای خالی هم‌اندازه است." : "");
        }
        catch (Exception ex)
        {
            LockScanText.Text = "⚠ " + ex.Message;
        }
    }

    private string Pass1 => ShowPassCheck.IsChecked == true ? LockPass1Plain.Text : LockPass1.Password;
    private string Pass2 => ShowPassCheck.IsChecked == true ? LockPass2Plain.Text : LockPass2.Password;

    private void ShowPass_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowPassCheck.IsChecked == true;
        _syncingPass = true;
        if (show) { LockPass1Plain.Text = LockPass1.Password; LockPass2Plain.Text = LockPass2.Password; }
        else { LockPass1.Password = LockPass1Plain.Text; LockPass2.Password = LockPass2Plain.Text; }
        _syncingPass = false;
        LockPass1.Visibility = LockPass2.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        LockPass1Plain.Visibility = LockPass2Plain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateStrength();
    }

    private void LockPass_Changed(object sender, RoutedEventArgs e) { if (!_syncingPass) UpdateStrength(); }
    private void LockPassPlain_Changed(object sender, TextChangedEventArgs e) { if (!_syncingPass) UpdateStrength(); }

    private void UpdateStrength()
    {
        if (StrengthText == null) return;
        string p = Pass1;
        int score = 0;
        if (p.Length >= 8) score++;
        if (p.Length >= 12) score++;
        if (p.Any(char.IsDigit) && p.Any(char.IsLetter)) score++;
        if (p.Any(c => !char.IsLetterOrDigit(c)) || (p.Any(char.IsUpper) && p.Any(char.IsLower))) score++;
        if (p.Length < FolderLockService.MinPasswordLength) score = 0;
        StrengthBar.Value = score;
        string[] labels = { "خیلی ضعیف", "ضعیف", "متوسط", "خوب", "قوی" };
        StrengthText.Text = p.Length == 0 ? $"حداقل {FolderLockService.MinPasswordLength} نویسه؛ ترکیب حروف، عدد و نماد پیشنهاد می‌شود."
            : $"قدرت رمز: {labels[score]}" + (Pass2.Length > 0 && Pass2 != p ? " · ⚠ تکرار رمز یکسان نیست" : "");
        StrengthText.Foreground = (Brush)FindResource(score >= 3 ? "SuccessBrush" : score >= 2 ? "WarnBrush" : "DangerBrush");
    }

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        string folder = LockFolderBox.Text.Trim().Trim('"');
        string? problem = FolderIconService.ValidateFolder(folder);
        if (problem != null) { LockStatusText.Text = "⚠ " + problem; return; }
        string p1 = Pass1, p2 = Pass2;
        if (p1.Length < FolderLockService.MinPasswordLength) { LockStatusText.Text = $"⚠ رمز باید حداقل {FolderLockService.MinPasswordLength} نویسه باشد."; return; }
        if (p1 != p2) { LockStatusText.Text = "⚠ رمز و تکرار آن یکسان نیستند."; return; }

        FolderLockService.FolderScan scan;
        try { scan = await Task.Run(() => FolderLockService.Scan(folder)); }
        catch (Exception ex) { LockStatusText.Text = "⚠ " + ex.Message; return; }

        string sizeNote = scan.Bytes > FolderLockService.LargeFolderWarningBytes
            ? $"\n\n⚠ حجم پوشه {FileKinds.FormatSize(scan.Bytes)} است؛ این کار زمان‌بر است و همین مقدار فضای خالی لازم دارد." : "";
        string msg = $"پوشه «{System.IO.Path.GetFileName(folder)}» ({scan.Files} فایل، {FileKinds.FormatSize(scan.Bytes)}) رمزنگاری می‌شود.\n\n" +
                     "• ابتدا فایل «.shamsalock» ساخته و به‌طور کامل رمزگشایی آزمایشی و هش همه فایل‌ها تأیید می‌شود.\n" +
                     "• فقط پس از تأیید موفق، پوشه اصلی حذف می‌شود. این حذف «پاک‌سازی امن» نیست و ممکن است با ابزارهای بازیابی دیسک قابل بازگشت باشد.\n" +
                     "• اگر رمز را فراموش کنید، هیچ راهی برای بازکردن فایل وجود ندارد (حتی برای سازندگان نگارخانه)." + sizeNote +
                     "\n\nادامه می‌دهید؟";
        if (MessageBox.Show(Dlg, msg, "قفل پوشه با رمز", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await RunLockOperation(async (progress, ct) =>
        {
            var result = await FolderLockService.LockAsync(folder, p1, HintBox.Text.Trim(), deleteOriginalAfterVerify: true, progress, ct);
            LockPass1.Clear(); LockPass2.Clear(); LockPass1Plain.Clear(); LockPass2Plain.Clear();
            LockFolderBox.Text = string.Empty;
            VaultBox.Text = result.VaultPath;
            LockStatusText.Text = $"✅ قفل شد: {result.Files} فایل در «{System.IO.Path.GetFileName(result.VaultPath)}»." +
                                  (result.DeleteWarning != null ? "\n⚠ " + result.DeleteWarning : "");
            Notifier.Show("پوشه با رمز قفل شد.", result.VaultPath);
        });
    }

    private void BrowseVault_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "انتخاب فایل قفل نگارخانه", Filter = "پوشه قفل‌شده نگارخانه|*.shamsalock" };
        if (dlg.ShowDialog(Dlg) == true) VaultBox.Text = dlg.FileName;
    }

    private void Vault_Changed(object sender, TextChangedEventArgs e)
    {
        string path = VaultBox.Text.Trim().Trim('"');
        VaultHintText.Text = string.Empty;
        if (!File.Exists(path)) return;
        try
        {
            string hint = FolderLockService.ReadHint(path);
            VaultHintText.Text = hint.Length > 0 ? "راهنمای رمز: " + hint : "این فایل راهنمای رمز ندارد.";
        }
        catch (Exception ex) { VaultHintText.Text = "⚠ " + ex.Message; }
    }

    private void UnlockPass_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Unlock_Click(sender, e);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        string vault = VaultBox.Text.Trim().Trim('"');
        if (!File.Exists(vault)) { LockStatusText.Text = "⚠ فایل قفل پیدا نشد."; return; }
        string pass = UnlockPass.Password;
        if (pass.Length == 0) { LockStatusText.Text = "⚠ رمز عبور را وارد کنید."; return; }
        bool deleteVault = DeleteVaultCheck.IsChecked == true;

        await RunLockOperation(async (progress, ct) =>
        {
            var result = await FolderLockService.UnlockAsync(vault, pass, deleteVault, progress, ct);
            UnlockPass.Clear();
            VaultBox.Text = string.Empty;
            LockStatusText.Text = $"✅ قفل باز شد: {result.Files} فایل در «{result.FolderPath}»" +
                                  (deleteVault && !result.VaultDeleted ? " (حذف فایل قفل ممکن نشد)" : "");
            ShellActions.RevealInExplorer(result.FolderPath);
        });
    }

    private void CancelLock_Click(object sender, RoutedEventArgs e) => _lockCts?.Cancel();

    private async Task RunLockOperation(Func<IProgress<LockProgress>, CancellationToken, Task> work)
    {
        if (_lockCts != null) return;
        _lockCts = new CancellationTokenSource();
        LockBtn.IsEnabled = UnlockBtn.IsEnabled = false;
        CancelLockBtn.IsEnabled = true;
        LockProgressBar.Value = 0;
        var progress = new Progress<LockProgress>(p =>
        {
            LockProgressBar.Value = p.Fraction;
            LockStatusText.Text = p.Message;
        });
        try
        {
            await work(progress, _lockCts.Token);
        }
        catch (FolderLockException ex)
        {
            LockStatusText.Text = "⚠ " + ex.Message;
            LockProgressBar.Value = 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Folder lock operation failed", ex);
            LockStatusText.Text = "⚠ خطای غیرمنتظره: " + ex.Message;
            LockProgressBar.Value = 0;
        }
        finally
        {
            _lockCts.Dispose();
            _lockCts = null;
            LockBtn.IsEnabled = UnlockBtn.IsEnabled = true;
            CancelLockBtn.IsEnabled = false;
        }
    }

    // ------------------------------------------------------------------ hosting in the «نگارخانه» hub

    /// <summary>When set, this window is never shown: its content lives in <see cref="Host"/>.</summary>
    public Window? Host { get; set; }
    private Window Dlg => Host ?? this;

    /// <summary>Takes the window content for hosting elsewhere (drag &amp; drop of folders keeps working).</summary>
    public FrameworkElement? DetachForHost(Window host)
    {
        Host = host;
        var root = UiHost.TakeContent(this);
        if (root == null) return null;
        root.Margin = new Thickness(0);
        root.AllowDrop = true;
        root.DragOver += Window_DragOver;
        root.Drop += Window_Drop;
        HeaderTitle.Visibility = Visibility.Collapsed; // the hub shows the page title
        return root;
    }

    /// <summary>Same checks as closing the stand-alone window. Returns false when the user keeps a lock operation running.</summary>
    public bool TryReleaseHosted()
    {
        var e = new CancelEventArgs();
        ConfirmCloseAndSave(e);
        return !e.Cancel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ConfirmCloseAndSave(e);
        if (e.Cancel) return;
        base.OnClosing(e);
    }

    private void ConfirmCloseAndSave(CancelEventArgs e)
    {
        if (_lockCts != null)
        {
            if (MessageBox.Show(Dlg, "عملیات قفل در حال اجراست. لغو شود؟ (پوشه اصلی دست‌نخورده می‌ماند)", "نگارخانه",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _lockCts.Cancel();
        }
        try
        {
            ReadUiIntoDesign();
            FolderStudioStore.Data.LastDesign = _design.Clone();
            FolderStudioStore.Save();
        }
        catch { }
    }
}
