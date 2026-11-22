using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Shamsa.Services;

namespace Shamsa.Views;

public partial class ToolsDashboardWindow : Window
{
    public sealed class DupRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public DupRow(string path, string groupTitle, int groupIndex)
        {
            Path = path;
            GroupTitle = groupTitle;
            GroupIndex = groupIndex;
            try { Modified = File.GetLastWriteTime(path); } catch { Modified = DateTime.MinValue; }
        }

        public string Path { get; }
        public string GroupTitle { get; }
        public int GroupIndex { get; }
        public DateTime Modified { get; }
        public string ModifiedText => Modified == DateTime.MinValue ? "-" : Modified.ToString("yyyy/MM/dd HH:mm");

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }


    public sealed class ConvertItemRow : INotifyPropertyChanged
    {
        private static readonly System.Windows.Media.Brush GoldBrush = FrozenBrush("#F6D379");
        private static readonly System.Windows.Media.Brush DoneBrush = FrozenBrush("#34D399");
        private static readonly System.Windows.Media.Brush MutedBrush = FrozenBrush("#94A3B8");

        private string _dimensionsText = string.Empty;
        private string _sizePreviewText = string.Empty;
        private System.Windows.Media.Brush _sizePreviewBrush = GoldBrush;

        public ConvertItemRow(string fullPath)
        {
            FullPath = fullPath;
            bool isDir = Directory.Exists(fullPath);
            FileName = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(FileName)) FileName = fullPath;
            Kind = FileKinds.Of(fullPath);
            Icon = ShellIcons.Get(fullPath);
            if (isDir)
            {
                SizePreviewText = "پوشه";
                SizePreviewBrush = MutedBrush;
            }
            else
            {
                try
                {
                    long len = new FileInfo(fullPath).Length;
                    Meta = new MediaMeta(0, 0, 0, len);
                    SizePreviewText = FileKinds.FormatSize(len);
                }
                catch { }
            }
        }

        private static System.Windows.Media.Brush FrozenBrush(string hex)
        {
            var b = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public string FullPath { get; }
        public string FileName { get; }
        public FileKind Kind { get; }
        public System.Windows.Media.ImageSource? Icon { get; }
        public MediaMeta Meta { get; set; }
        public long EstimatedBytes { get; private set; }
        public bool IsActualResult { get; private set; }

        public string DimensionsText
        {
            get => _dimensionsText;
            private set { _dimensionsText = value; OnChanged(nameof(DimensionsText)); OnChanged(nameof(DimensionsVisibility)); }
        }

        public Visibility DimensionsVisibility => string.IsNullOrEmpty(_dimensionsText) ? Visibility.Collapsed : Visibility.Visible;

        public string SizePreviewText
        {
            get => _sizePreviewText;
            private set { _sizePreviewText = value; OnChanged(nameof(SizePreviewText)); }
        }

        public System.Windows.Media.Brush SizePreviewBrush
        {
            get => _sizePreviewBrush;
            private set { _sizePreviewBrush = value; OnChanged(nameof(SizePreviewBrush)); }
        }

        public void UpdateEstimate(string action, CompressionSettings opts)
        {
            IsActualResult = false;
            if (Kind == FileKind.Folder)
            {
                DimensionsText = string.Empty;
                SizePreviewText = "پوشه";
                SizePreviewBrush = MutedBrush;
                EstimatedBytes = 0;
                return;
            }

            if (action == "compress" && ConverterService.IsSupported(FullPath, "compress"))
            {
                var est = ConverterService.EstimateCompressedOutput(FullPath, Meta, opts);
                EstimatedBytes = est.EstimatedBytes;

                if (est.OrigWidth > 0 && est.OrigHeight > 0)
                {
                    DimensionsText = (est.OutWidth == est.OrigWidth && est.OutHeight == est.OrigHeight)
                        ? $"{est.OrigWidth}×{est.OrigHeight}"
                        : $"{est.OrigWidth}×{est.OrigHeight} → {est.OutWidth}×{est.OutHeight}";
                }
                else
                {
                    DimensionsText = string.Empty;
                }

                if (est.OrigBytes > 0)
                {
                    int pct = Math.Clamp((int)Math.Round((est.OrigBytes - est.EstimatedBytes) * 100.0 / est.OrigBytes), 0, 99);
                    SizePreviewText = $"{FileKinds.FormatSize(est.OrigBytes)} ← ~{FileKinds.FormatSize(est.EstimatedBytes)} ({pct}٪-)";
                }
                else
                {
                    SizePreviewText = $"~{FileKinds.FormatSize(est.EstimatedBytes)}";
                }
                SizePreviewBrush = GoldBrush;
            }
            else
            {
                EstimatedBytes = Meta.SizeBytes;
                DimensionsText = Meta.Width > 0 && Meta.Height > 0 ? $"{Meta.Width}×{Meta.Height}" : string.Empty;
                SizePreviewText = Meta.SizeBytes > 0 ? FileKinds.FormatSize(Meta.SizeBytes) : string.Empty;
                SizePreviewBrush = MutedBrush;
            }
        }

        public void ApplyActualOutput(string outputPath)
        {
            try
            {
                var outMeta = MediaProbeService.GetMediaMeta(outputPath);
                long outSize = outMeta.SizeBytes;
                long origSize = Meta.SizeBytes;
                IsActualResult = true;

                if (Meta.Width > 0 && outMeta.Width > 0)
                {
                    DimensionsText = (Meta.Width == outMeta.Width && Meta.Height == outMeta.Height)
                        ? $"{outMeta.Width}×{outMeta.Height}"
                        : $"{Meta.Width}×{Meta.Height} → {outMeta.Width}×{outMeta.Height}";
                }

                if (origSize > 0 && outSize > 0)
                {
                    int pct = (int)Math.Round((origSize - outSize) * 100.0 / origSize);
                    SizePreviewText = pct > 0
                        ? $"✓ {FileKinds.FormatSize(origSize)} ← {FileKinds.FormatSize(outSize)} ({pct}٪-)"
                        : $"✓ {FileKinds.FormatSize(outSize)}";
                }
                else
                {
                    SizePreviewText = $"✓ {FileKinds.FormatSize(outSize)}";
                }
                SizePreviewBrush = DoneBrush;
            }
            catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    private readonly SmartOrganizerService _organizer = new();
    private readonly BatchRenameService _renamer = new();
    private readonly DuplicateFinderService _dupFinder = new();
    private readonly ConverterService _converter;

    private readonly DispatcherTimer _renameDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _organizerDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private List<RenamePreviewItem> _lastPreview = new();
    private IReadOnlyList<RenameUndoEntry>? _renameUndo;
    private OrganizePlan? _organizerPlan;
    private IReadOnlyList<OrganizeMove>? _organizerUndo;

    private readonly ObservableCollection<DupRow> _dupRows = new();
    private CancellationTokenSource? _dupCts;

    private readonly ObservableCollection<ConvertItemRow> _convertItems = new();
    private CancellationTokenSource? _convertCts;
    private string? _lastConvertOutput;

    private bool _loading = true;

    private readonly Func<bool>? _isHotkeyRegistered;

    public event Action? DemoDonutRequested;
    public event Action? OpenShelfRequested;
    public event Action? ExitAppRequested;
    public event Action<bool>? HotkeyRecordingChanged;

    public ToolsDashboardWindow(ConverterService converter, Func<bool>? isHotkeyRegistered = null)
    {
        InitializeComponent();
        _converter = converter;
        _isHotkeyRegistered = isHotkeyRegistered;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version != null ? $"نسخه {version.Major}.{version.Minor}.{version.Build}" : string.Empty;

        // Organizer
        var settings = SettingsService.Current;
        OrganizerFolderBox.Text = Directory.Exists(settings.LastOrganizerFolder) ? settings.LastOrganizerFolder : NativeMethods.GetDownloadsFolder();
        _organizerDebounce.Tick += (_, _) => { _organizerDebounce.Stop(); RefreshOrganizerPreview(); };

        // Rename
        RenameFolderBox.Text = Directory.Exists(settings.LastRenameFolder) ? settings.LastRenameFolder : string.Empty;
        _renameDebounce.Tick += (_, _) => { _renameDebounce.Stop(); RefreshRenamePreview(); };

        // Duplicates (grouped view assigned in code; a XAML CollectionViewSource left the empty grid's headers collapsed)
        var dupView = new ListCollectionView(_dupRows);
        dupView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DupRow.GroupTitle)));
        DupGrid.ItemsSource = dupView;

        // Converter
        ConvertList.ItemsSource = _convertItems;
        _convertItems.CollectionChanged += (_, _) => UpdateConvertSupport();
        foreach (var action in ConverterService.ConvertActions.Append("zip"))
            ConvertActionBox.Items.Add(new ComboBoxItem { Content = ConverterService.ActionLabel(action), Tag = action });
        ConvertActionBox.SelectedIndex = 0;

        LoadSettingsIntoUi();
        UpdateFfmpegStatus();
        _loading = false;

        RefreshOrganizerPreview();
        if (!string.IsNullOrEmpty(RenameFolderBox.Text)) RefreshRenamePreview();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ExitApp_Click(object sender, RoutedEventArgs e) => ExitAppRequested?.Invoke();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowOuterBorder != null)
            WindowOuterBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (MaxRestoreBtn != null)
            MaxRestoreBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
    }

    private void DemoDonut_Click(object sender, RoutedEventArgs e) => DemoDonutRequested?.Invoke();
    private void OpenShelfFromHeader_Click(object sender, RoutedEventArgs e) => OpenShelfRequested?.Invoke();
    private void JumpToOrganizer_Click(object sender, RoutedEventArgs e) => SelectTab(1);
    private void JumpToConverter_Click(object sender, RoutedEventArgs e) => SelectTab(4);
    private void JumpToSettings_Click(object sender, RoutedEventArgs e) => SelectTab(5);

    // =====================================================================
    // Smart organizer
    // =====================================================================

    private OrganizeMode SelectedOrganizeMode => (OrganizeMode)Math.Max(0, OrganizeModeBox.SelectedIndex);

    private void BrowseOrganizer_Click(object sender, RoutedEventArgs e)
    {
        var path = ShellActions.PickFolder(OrganizerFolderBox.Text, this);
        if (path != null) OrganizerFolderBox.Text = path;
    }

    private void OrganizerDownloads_Click(object sender, RoutedEventArgs e) => OrganizerFolderBox.Text = NativeMethods.GetDownloadsFolder();
    private void OrganizerDesktop_Click(object sender, RoutedEventArgs e) => OrganizerFolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private void OrganizerInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _organizerDebounce.Stop();
        _organizerDebounce.Start();
    }

    private async void RefreshOrganizerPreview()
    {
        string folder = OrganizerFolderBox.Text.Trim();
        var mode = SelectedOrganizeMode;
        if (!Directory.Exists(folder))
        {
            _organizerPlan = null;
            OrganizerGrid.ItemsSource = null;
            OrganizerStatus.Text = string.IsNullOrEmpty(folder) ? string.Empty : "پوشه وجود ندارد.";
            RunOrganizerBtn.IsEnabled = false;
            return;
        }

        OrganizePlan plan;
        try
        {
            plan = await Task.Run(() => _organizer.BuildPlan(folder, mode));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Organizer preview failed: " + ex.Message);
            return;
        }
        if (folder != OrganizerFolderBox.Text.Trim() || mode != SelectedOrganizeMode) return; // stale

        _organizerPlan = plan;
        OrganizerGrid.ItemsSource = plan.Moves;
        RunOrganizerBtn.IsEnabled = plan.Moves.Count > 0;
        OrganizerStatus.Text = plan.Moves.Count == 0
            ? "این پوشه مرتب است ✓"
            : $"پیش‌نمایش: {plan.Moves.Count} فایل ← " + string.Join("، ", plan.Summary.Take(6).Select(s => FileKinds.Ltr($"{s.Category} ({s.Count})")));
    }

    private async void RunOrganizer_Click(object sender, RoutedEventArgs e)
    {
        if (_organizerPlan == null || _organizerPlan.Moves.Count == 0) return;
        RunOrganizerBtn.IsEnabled = false;
        var plan = _organizerPlan;
        try
        {
            var result = await Task.Run(() => _organizer.Execute(plan));
            _organizerUndo = result.Done;
            UndoOrganizerBtn.IsEnabled = result.Done.Count > 0;
            SettingsService.Update(s => s.LastOrganizerFolder = plan.Folder);
            RefreshOrganizerPreview();
            OrganizerStatus.Text = result.Summary;
        }
        catch (Exception ex)
        {
            AppLog.Error("Organize failed", ex);
            OrganizerStatus.Text = "خطا: " + ex.Message;
        }
    }

    private async void UndoOrganizer_Click(object sender, RoutedEventArgs e)
    {
        if (_organizerUndo == null) return;
        var moves = _organizerUndo;
        UndoOrganizerBtn.IsEnabled = false;
        int restored = await Task.Run(() => _organizer.Undo(moves));
        _organizerUndo = null;
        RefreshOrganizerPreview();
        OrganizerStatus.Text = $"{restored} فایل به جای قبلی برگشت.";
    }

    // =====================================================================
    // Batch rename (live preview)
    // =====================================================================

    private void BrowseRename_Click(object sender, RoutedEventArgs e)
    {
        var path = ShellActions.PickFolder(RenameFolderBox.Text, this);
        if (path != null) RenameFolderBox.Text = path; // TextChanged triggers preview
    }

    private void RenameInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _renameDebounce.Stop();
        _renameDebounce.Start();
    }

    private RenameOptions ReadRenameOptions() => new()
    {
        Prefix = PrefixBox.Text,
        Suffix = SuffixBox.Text,
        Find = FindBox.Text,
        Replace = ReplaceBox.Text,
        UseRegex = RegexCheck.IsChecked == true,
        MatchCase = MatchCaseCheck.IsChecked == true,
        Numbering = NumberingCheck.IsChecked == true,
        NumberStart = int.TryParse(NumberStartBox.Text, out int start) ? start : 1,
        NumberPadding = int.TryParse(NumberPadBox.Text, out int pad) ? pad : 3,
        Case = (RenameCase)Math.Max(0, CaseBox.SelectedIndex),
        ExtensionFilter = ExtFilterBox.Text
    };

    private void RefreshRenamePreview()
    {
        string folder = RenameFolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            _lastPreview = new List<RenamePreviewItem>();
            RenamePreviewGrid.ItemsSource = null;
            RenameStatusText.Text = string.IsNullOrEmpty(folder) ? "یک پوشه انتخاب کنید." : "پوشه وجود ندارد.";
            ApplyRenameBtn.IsEnabled = false;
            return;
        }

        _lastPreview = _renamer.BuildPreview(folder, ReadRenameOptions());
        RenamePreviewGrid.ItemsSource = _lastPreview;

        int ok = _lastPreview.Count(p => p.Status == RenameStatus.Ok);
        int conflicts = _lastPreview.Count(p => p.Status == RenameStatus.Conflict);
        int invalid = _lastPreview.Count(p => p.Status == RenameStatus.Invalid);
        ApplyRenameBtn.IsEnabled = ok > 0;

        var parts = new List<string> { $"{_lastPreview.Count} فایل", $"{ok} آماده تغییر" };
        if (conflicts > 0) parts.Add($"{conflicts} تداخل");
        if (invalid > 0) parts.Add($"{invalid} نامعتبر");
        RenameStatusText.Text = string.Join(" · ", parts);
    }

    private void ApplyRename_Click(object sender, RoutedEventArgs e)
    {
        // Always rebuild first: the old version applied a stale preview if the inputs changed after "Preview".
        RefreshRenamePreview();
        int count = _lastPreview.Count(p => p.WillRename);
        if (count == 0) return;

        var result = _renamer.ApplyRename(_lastPreview);
        _renameUndo = result.Undo;
        UndoRenameBtn.IsEnabled = result.Undo.Count > 0;
        SettingsService.Update(s => s.LastRenameFolder = RenameFolderBox.Text.Trim());

        RefreshRenamePreview();
        RenameStatusText.Text = $"{result.Renamed} فایل تغییر نام یافت" + (result.Failed > 0 ? $" · {result.Failed} خطا: {result.Errors.FirstOrDefault()}" : ".");
    }

    private void UndoRename_Click(object sender, RoutedEventArgs e)
    {
        if (_renameUndo == null) return;
        var result = _renamer.Undo(_renameUndo);
        _renameUndo = null;
        UndoRenameBtn.IsEnabled = false;
        RefreshRenamePreview();
        RenameStatusText.Text = $"{result.Renamed} فایل به نام قبلی برگشت" + (result.Failed > 0 ? $" · {result.Failed} خطا" : ".");
    }

    // =====================================================================
    // Duplicate finder
    // =====================================================================

    private void BrowseDup_Click(object sender, RoutedEventArgs e)
    {
        var path = ShellActions.PickFolder(DupFolderBox.Text, this);
        if (path != null) DupFolderBox.Text = path;
    }

    private async void ScanDuplicates_Click(object sender, RoutedEventArgs e)
    {
        string folder = DupFolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            var picked = ShellActions.PickFolder(null, this);
            if (picked == null) return;
            DupFolderBox.Text = folder = picked;
        }

        _dupCts?.Cancel();
        _dupCts = new CancellationTokenSource();
        var token = _dupCts.Token;

        _dupRows.Clear();
        ScanDupBtn.IsEnabled = false;
        CancelDupBtn.IsEnabled = true;
        DupProgress.Visibility = Visibility.Visible;
        DupProgress.IsIndeterminate = true;
        DupStatus.Text = "در حال اسکن...";

        var progress = new Progress<ScanProgress>(p =>
        {
            DupStatus.Text = p.Total > 0 ? $"{p.Stage} {p.Done}/{p.Total}" : p.Stage;
            if (p.Total > 0)
            {
                DupProgress.IsIndeterminate = false;
                DupProgress.Maximum = p.Total;
                DupProgress.Value = p.Done;
            }
        });

        try
        {
            var groups = await _dupFinder.FindDuplicatesAsync(folder, progress, token);
            int index = 1;
            foreach (var g in groups)
            {
                string title = $"گروه {index}: {g.FilePaths.Count} نسخه · {FileKinds.FormatSize(g.SizeBytes)} هرکدام · {FileKinds.FormatSize(g.WastedBytes)} قابل آزادسازی";
                foreach (var f in g.FilePaths) _dupRows.Add(new DupRow(f, title, index));
                index++;
            }

            long wasted = groups.Sum(g => g.WastedBytes);
            DupStatus.Text = groups.Count == 0
                ? "هیچ فایل تکراری یافت نشد ✓"
                : $"{groups.Count} گروه تکراری · {FileKinds.FormatSize(wasted)} فضای قابل آزادسازی. «انتخاب خودکار» را بزنید.";
        }
        catch (OperationCanceledException)
        {
            DupStatus.Text = "اسکن لغو شد.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Duplicate scan failed", ex);
            DupStatus.Text = "خطا: " + ex.Message;
        }
        finally
        {
            ScanDupBtn.IsEnabled = true;
            CancelDupBtn.IsEnabled = false;
            DupProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelDup_Click(object sender, RoutedEventArgs e) => _dupCts?.Cancel();

    private void AutoSelect(bool keepOldest)
    {
        foreach (var group in _dupRows.GroupBy(r => r.GroupIndex))
        {
            var ordered = keepOldest ? group.OrderBy(r => r.Modified) : group.OrderByDescending(r => r.Modified);
            bool first = true;
            foreach (var row in ordered)
            {
                row.IsSelected = !first;
                first = false;
            }
        }
        UpdateDupSelectionStatus();
    }

    private void AutoSelectKeepOldest_Click(object sender, RoutedEventArgs e) => AutoSelect(keepOldest: true);
    private void AutoSelectKeepNewest_Click(object sender, RoutedEventArgs e) => AutoSelect(keepOldest: false);

    private void ClearDupSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _dupRows) r.IsSelected = false;
        UpdateDupSelectionStatus();
    }

    private void UpdateDupSelectionStatus()
    {
        var selected = _dupRows.Where(r => r.IsSelected).ToList();
        long bytes = selected.Sum(r => { try { return new FileInfo(r.Path).Length; } catch { return 0L; } });
        DupStatus.Text = $"{selected.Count} فایل انتخاب شده · {FileKinds.FormatSize(bytes)}";
    }

    private void DeleteDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var selected = _dupRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            DupStatus.Text = "ابتدا فایل‌هایی را برای حذف انتخاب کنید.";
            return;
        }

        // Safety: never delete every copy of a file.
        var fullyDeleted = _dupRows.GroupBy(r => r.GroupIndex).Where(g => g.All(r => r.IsSelected)).Select(g => g.Key).ToList();
        if (fullyDeleted.Count > 0)
        {
            MessageBox.Show(this, $"در {fullyDeleted.Count} گروه همه نسخه‌ها انتخاب شده‌اند. حداقل یک نسخه از هر فایل باید باقی بماند.",
                "Shamsa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this, $"{selected.Count} فایل به سطل زباله منتقل شود؟ (قابل بازیابی از Recycle Bin)",
                "Shamsa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int ok = 0, failed = 0;
        foreach (var row in selected)
        {
            try
            {
                FileSystem.DeleteFile(row.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                _dupRows.Remove(row);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn($"Recycle failed for {row.Path}: {ex.Message}");
            }
        }

        // drop groups that no longer have duplicates
        foreach (var lonely in _dupRows.GroupBy(r => r.GroupIndex).Where(g => g.Count() < 2).SelectMany(g => g).ToList())
            _dupRows.Remove(lonely);

        DupStatus.Text = $"{ok} فایل به سطل زباله منتقل شد" + (failed > 0 ? $" · {failed} خطا" : ".");
    }

    private void DupGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DupGrid.SelectedItem is DupRow row) ShellActions.RevealInExplorer(row.Path);
    }

    // =====================================================================
    // Converter & Smart Compression
    // =====================================================================

    private string SelectedConvertAction => (ConvertActionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "compress";

    public void AddConvertItems(IEnumerable<string> paths)
    {
        var added = new List<ConvertItemRow>();
        foreach (var p in paths)
        {
            if ((File.Exists(p) || Directory.Exists(p)) && !_convertItems.Any(i => i.FullPath.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new ConvertItemRow(p);
                row.UpdateEstimate(SelectedConvertAction, SettingsService.Current.Compression);
                _convertItems.Add(row);
                added.Add(row);
            }
        }

        if (added.Count > 0)
            _ = ProbeConvertItemsAsync(added);
    }

    private async Task ProbeConvertItemsAsync(List<ConvertItemRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Kind == FileKind.Folder) continue;
            var meta = await Task.Run(() => MediaProbeService.GetMediaMeta(row.FullPath));
            row.Meta = meta;
            row.UpdateEstimate(SelectedConvertAction, SettingsService.Current.Compression);
        }
        RefreshCompressionSummary();
    }

    private void ConvertDrop_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        ConvertDropZone.BorderBrush = ok ? (System.Windows.Media.Brush)FindResource("AccentBrush") : (System.Windows.Media.Brush)FindResource("BorderBrush");
        e.Handled = true;
    }

    private void ConvertDrop_DragLeave(object sender, DragEventArgs e) =>
        ConvertDropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");

    private void ConvertDrop_Drop(object sender, DragEventArgs e)
    {
        ConvertDropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Effects = DragDropEffects.Copy;
            AddConvertItems(files);
            SuggestAction(files);
        }
        e.Handled = true;
    }

    private void SuggestAction(string[] files)
    {
        if (_convertItems.All(i => ConverterService.IsSupported(i.FullPath, SelectedConvertAction))) return;
        var kinds = _convertItems.Select(i => FileKinds.Of(i.FullPath)).Distinct().ToList();
        string suggestion = kinds.All(k => k is FileKind.Video or FileKind.Image or FileKind.Audio) ? "compress" : "zip";

        foreach (ComboBoxItem item in ConvertActionBox.Items)
        {
            if ((string)item.Tag == suggestion)
            {
                ConvertActionBox.SelectedItem = item;
                break;
            }
        }
    }

    private void AddConvertFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "انتخاب فایل‌ها" };
        if (dialog.ShowDialog(this) == true)
        {
            AddConvertItems(dialog.FileNames);
            SuggestAction(dialog.FileNames);
        }
    }

    private void ClearConvertList_Click(object sender, RoutedEventArgs e)
    {
        _convertItems.Clear();
        RefreshCompressionSummary();
    }

    private void ConvertList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        foreach (var item in ConvertList.SelectedItems.Cast<ConvertItemRow>().ToList()) _convertItems.Remove(item);
        RefreshCompressionSummary();
    }

    private void ConvertActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConvertSupport();
        RefreshAllCompressionEstimates();
    }

    private void UpdateConvertSupport()
    {
        if (ConvertEmptyHint == null || ConvertSupportText == null) return;
        string action = SelectedConvertAction;
        if (CompressionPanel != null)
            CompressionPanel.Visibility = action == "compress" ? Visibility.Visible : Visibility.Collapsed;

        ConvertEmptyHint.Visibility = _convertItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_convertItems.Count == 0)
        {
            ConvertSupportText.Text = string.Empty;
            RefreshCompressionSummary();
            return;
        }

        int supported = action == "zip" ? _convertItems.Count : _convertItems.Count(i => ConverterService.IsApplicable(new[] { i.FullPath }, action));
        ConvertSupportText.Text = $"{supported} از {_convertItems.Count} مورد قابل پردازش";
        RefreshCompressionSummary();
    }

    private void LoadCompressionUi()
    {
        if (CompressPresetBox == null) return;
        var c = SettingsService.Current.Compression;
        CompressPresetBox.SelectedIndex = Math.Clamp(c.Preset, 0, 4);
        CompressResBox.SelectedIndex = Math.Clamp(c.ResolutionMode, 0, 6);
        CompressImgFmtBox.SelectedIndex = Math.Clamp(c.ImageFormatMode, 0, 3);
        CompressQualitySlider.Value = Math.Clamp(c.QualityPercent, 15, 95);

        int targetIdx = 0;
        for (int i = 0; i < CompressTargetMbBox.Items.Count; i++)
        {
            if (CompressTargetMbBox.Items[i] is ComboBoxItem ci && ci.Tag is string ts
                && int.TryParse(ts, out int mb) && mb == c.TargetVideoSizeMb)
            {
                targetIdx = i;
                break;
            }
        }
        CompressTargetMbBox.SelectedIndex = targetIdx;
        UpdateQualityLabel(c);
        RefreshAllCompressionEstimates();
    }

    private void UpdateQualityLabel(CompressionSettings c)
    {
        if (CompressQualityText == null) return;
        CompressQualityText.Text = $"{c.QualityPercent}% · CRF {c.EffectiveVideoCrf}";
    }

    private void CompressPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CompressPresetBox.SelectedIndex < 0) return;
        int preset = CompressPresetBox.SelectedIndex;
        SettingsService.Update(s => s.Compression.ApplyPreset(preset));

        _loading = true;
        CompressQualitySlider.Value = SettingsService.Current.Compression.QualityPercent;
        _loading = false;

        UpdateQualityLabel(SettingsService.Current.Compression);
        RefreshAllCompressionEstimates();
    }

    private void CompressQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CompressQualityText == null) return;
        int q = (int)Math.Round(e.NewValue);
        if (_loading)
        {
            UpdateQualityLabel(SettingsService.Current.Compression);
            return;
        }

        SettingsService.Update(s =>
        {
            s.Compression.QualityPercent = q;
            s.Compression.Preset = 4; // حالت دستی
        });

        _loading = true;
        if (CompressPresetBox != null) CompressPresetBox.SelectedIndex = 4;
        _loading = false;

        UpdateQualityLabel(SettingsService.Current.Compression);
        RefreshAllCompressionEstimates();
    }

    private void CompressOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int resMode = Math.Max(0, CompressResBox.SelectedIndex);
        int imgFmt = Math.Max(0, CompressImgFmtBox.SelectedIndex);
        int targetMb = 0;
        if (CompressTargetMbBox.SelectedItem is ComboBoxItem ci && ci.Tag is string ts)
            int.TryParse(ts, out targetMb);

        SettingsService.Update(s =>
        {
            s.Compression.ResolutionMode = resMode;
            s.Compression.ImageFormatMode = imgFmt;
            s.Compression.TargetVideoSizeMb = targetMb;
        });

        RefreshAllCompressionEstimates();
    }

    private void RefreshAllCompressionEstimates()
    {
        if (_convertItems == null) return;
        string action = SelectedConvertAction;
        var opts = SettingsService.Current.Compression;
        foreach (var row in _convertItems)
            row.UpdateEstimate(action, opts);
        RefreshCompressionSummary();
    }

    private void RefreshCompressionSummary()
    {
        if (CompressTotalEstimateText == null) return;
        if (SelectedConvertAction != "compress")
        {
            CompressTotalEstimateText.Text = string.Empty;
            return;
        }

        var mediaRows = _convertItems.Where(r => r.Kind != FileKind.Folder && ConverterService.IsSupported(r.FullPath, "compress")).ToList();
        if (mediaRows.Count == 0)
        {
            CompressTotalEstimateText.Text = "فایل عکس یا ویدیو اضافه کنید تا ابعاد و حجم تخمینی نمایش داده شود";
            return;
        }

        long origSum = mediaRows.Sum(r => r.Meta.SizeBytes);
        long estSum = mediaRows.Sum(r => r.EstimatedBytes);
        if (origSum > 0)
        {
            int pct = Math.Clamp((int)Math.Round((origSum - estSum) * 100.0 / origSum), 0, 99);
            CompressTotalEstimateText.Text = $"📊 مجموع: {FileKinds.FormatSize(origSum)} ← ~{FileKinds.FormatSize(estSum)} (حدود {pct}٪ کاهش)";
        }
        else
        {
            CompressTotalEstimateText.Text = $"📊 تخمین خروجی: ~{FileKinds.FormatSize(estSum)}";
        }
    }

    private async void StartConvert_Click(object sender, RoutedEventArgs e)
    {
        if (_convertItems.Count == 0)
        {
            ConvertStatus.Text = "ابتدا فایل‌ها را اضافه کنید.";
            return;
        }

        _convertCts = new CancellationTokenSource();
        StartConvertBtn.IsEnabled = false;
        CancelConvertBtn.IsEnabled = true;
        ConvertProgress.Value = 0;
        ConvertStatus.Text = "در حال شروع...";

        var progress = new Progress<ConversionProgress>(p =>
        {
            ConvertProgress.Value = p.Overall;
            ConvertStatus.Text = $"({p.FileIndex + 1}/{p.FileCount}) {p.FileName} — {p.Fraction:P0}";
        });

        try
        {
            var result = await _converter.ConvertFilesAsync(
                _convertItems.Select(i => i.FullPath).ToList(),
                SelectedConvertAction,
                progress,
                _convertCts.Token,
                SettingsService.Current.Compression);

            ConvertProgress.Value = result.Success ? 1 : 0;
            _lastConvertOutput = result.Outputs.FirstOrDefault();
            ConvertStatus.Text = result.Message + (_lastConvertOutput != null ? "  (کلیک: نمایش خروجی)" : string.Empty);

            if (result.OutputByInput != null)
            {
                foreach (var row in _convertItems)
                {
                    if (result.OutputByInput.TryGetValue(row.FullPath, out var outPath))
                        row.ApplyActualOutput(outPath);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Convert tab failed", ex);
            ConvertStatus.Text = "خطا: " + ex.Message;
        }
        finally
        {
            StartConvertBtn.IsEnabled = true;
            CancelConvertBtn.IsEnabled = false;
            UpdateFfmpegStatus();
        }
    }

    private void CancelConvert_Click(object sender, RoutedEventArgs e) => _convertCts?.Cancel();

    private void ConvertStatus_Click(object sender, MouseButtonEventArgs e)
    {
        if (_lastConvertOutput != null) ShellActions.RevealInExplorer(_lastConvertOutput);
    }

    // =====================================================================
    // Settings
    // =====================================================================

    private void LoadSettingsIntoUi()
    {
        var s = SettingsService.Current;
        HoverCheck.IsChecked = s.HoverPreviewEnabled;
        PreviewImagesCheck.IsChecked = s.PreviewImages;
        SmartScrubCheck.IsChecked = s.SmartScrub;
        HoverDelaySlider.Value = s.HoverDelayMs;
        HoverDelayText.Text = $"{s.HoverDelayMs} ms";
        PreviewSizeSlider.Value = Math.Clamp(s.PreviewMaxSidePx, 150, 560);
        PreviewSizeText.Text = $"{(int)PreviewSizeSlider.Value} px";
        DonutCheck.IsChecked = s.DonutEnabled;
        DonutShortcutCheck.IsChecked = s.DonutShortcutEnabled;
        DonutAnimCheck.IsChecked = s.DonutAnimations;
        UpdateShortcutUi();
        LoadCompressionUi();
        RecycleCheck.IsChecked = s.MoveOriginalsToRecycleBin;
        AutoOrganizeCheck.IsChecked = s.AutoOrganizeEnabled;
        AutoOrganizeFolderBox.Text = s.AutoOrganizeFolder;
        StartupCheck.IsChecked = StartupManager.IsEnabled();
    }

    /// <summary>Called by the app when settings change elsewhere (e.g. from the tray menu).</summary>
    public void RefreshSettings()
    {
        _loading = true;
        LoadSettingsIntoUi();
        _loading = false;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Update(s =>
        {
            s.HoverPreviewEnabled = HoverCheck.IsChecked == true;
            s.PreviewImages = PreviewImagesCheck.IsChecked == true;
            s.SmartScrub = SmartScrubCheck.IsChecked == true;
            s.DonutEnabled = DonutCheck.IsChecked == true;
            s.DonutShortcutEnabled = DonutShortcutCheck.IsChecked == true;
            s.DonutAnimations = DonutAnimCheck.IsChecked == true;
            s.MoveOriginalsToRecycleBin = RecycleCheck.IsChecked == true;
            s.AutoOrganizeEnabled = AutoOrganizeCheck.IsChecked == true;
            s.AutoOrganizeFolder = AutoOrganizeFolderBox.Text.Trim();
        });
    }

    private void UpdateShortcutUi()
    {
        var s = SettingsService.Current;
        string label = GlobalHotkeyService.FormatShortcut(s.DonutShortcutModifiers, s.DonutShortcutKey);
        if (ShortcutBox != null && !ShortcutBox.IsFocused)
            ShortcutBox.Text = label;

        if (ShortcutStatusText != null)
        {
            if (!s.DonutShortcutEnabled)
            {
                ShortcutStatusText.Text = "غیرفعال";
                ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            }
            else if (_isHotkeyRegistered != null && !_isHotkeyRegistered())
            {
                ShortcutStatusText.Text = "⚠️ این کلید توسط برنامه دیگری اشغال است — کلید دیگری بزنید";
                ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarnBrush");
            }
            else
            {
                ShortcutStatusText.Text = $"✓ فعال ({label})";
                ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            }
        }

        if (GuideDonutDesc != null)
        {
            GuideDonutDesc.Text = s.DonutShortcutEnabled
                ? $"دو راه سریع دارید: یا روی فایل کلید {FileKinds.Ltr(label)} را بزنید و گزینه را کلیک کنید، یا Ctrl را نگه دارید و فایل را بکشید. با Tab یا اسکرول موس منو به ابزارهای سریع می‌چرخد."
                : "کلید Ctrl را نگه دارید و فایل را کمی بکشید تا منوی حلقه‌ای دقیقاً کنار موس باز شود. با Tab یا اسکرول موس منو به ابزارهای سریع می‌چرخد.";
        }

        if (GuideShortcutsText != null)
        {
            GuideShortcutsText.Text =
                $"• مکث موس روی فایل ویدیو/تصویر ← پیش‌نمایش شناور (عمودی/افقی خودکار)    |    • کلید {FileKinds.Ltr(label)} روی فایل ← منوی دایره‌ای قابل کلیک\n" +
                "• نگه داشتن Ctrl + کشیدن فایل ← منوی دایره‌ای Drag & Drop    |    • کلید Tab یا اسکرول ← چرخش منو    |    • Esc ← بستن";
        }
    }

    private void ShortcutBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HotkeyRecordingChanged?.Invoke(true);
        ShortcutBox.Text = "کلید ترکیبی را بزنید...";
        ShortcutStatusText.Text = "مثلاً Alt + Q یا Ctrl + Shift + D (یا Esc برای لغو)";
        ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
    }

    private void ShortcutBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HotkeyRecordingChanged?.Invoke(false);
        UpdateShortcutUi();
        LoadCompressionUi();
    }

    private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        // نادیده گرفتن کلیدهای کمکی تنها
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.DeadCharProcessed)
            return;

        uint mods = 0;
        var wpfMods = Keyboard.Modifiers;
        if (wpfMods.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (wpfMods.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (wpfMods.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;

        bool isFunctionKey = key is >= Key.F1 and <= Key.F12;
        if (mods == 0 && !isFunctionKey)
        {
            ShortcutStatusText.Text = "⚠️ حداقل یک کلید کمکی (Ctrl، Alt یا Shift) همراه با کلید اصلی بزنید (یا F1 تا F12).";
            ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarnBrush");
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return;

        SettingsService.Update(s =>
        {
            s.DonutShortcutEnabled = true;
            s.DonutShortcutModifiers = mods;
            s.DonutShortcutKey = vk;
        });

        Keyboard.ClearFocus();
        HotkeyRecordingChanged?.Invoke(false);
        UpdateShortcutUi();
        LoadCompressionUi();
    }

    private void ResetShortcut_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Update(s =>
        {
            s.DonutShortcutEnabled = true;
            s.DonutShortcutModifiers = NativeMethods.MOD_ALT;
            s.DonutShortcutKey = 0x51; // Alt + Q
        });
        UpdateShortcutUi();
        LoadCompressionUi();
    }

    private void PreviewSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int px = (int)Math.Round(e.NewValue);
        if (PreviewSizeText != null) PreviewSizeText.Text = $"{px} px";
        if (_loading) return;
        SettingsService.Update(s =>
        {
            s.PreviewMaxSidePx = px;
            s.PreviewSize = px <= 260 ? 0 : px >= 400 ? 2 : 1;
        });
    }

    private void PreviewSizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out int px))
            PreviewSizeSlider.Value = Math.Clamp(px, 150, 560);
    }

    private void HoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverDelayText == null) return;
        HoverDelayText.Text = $"{(int)e.NewValue} ms";
        if (_loading) return;
        SettingsService.Update(s => s.HoverDelayMs = (int)e.NewValue);
    }

    private void BrowseAutoOrganize_Click(object sender, RoutedEventArgs e)
    {
        var path = ShellActions.PickFolder(AutoOrganizeFolderBox.Text, this);
        if (path == null) return;
        AutoOrganizeFolderBox.Text = path;
        Setting_Changed(sender, e);
    }

    private void AutoOrganizeFolderBox_LostFocus(object sender, RoutedEventArgs e) => Setting_Changed(sender, e);

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        bool wanted = StartupCheck.IsChecked == true;
        if (!StartupManager.SetEnabled(wanted))
        {
            StartupCheck.IsChecked = StartupManager.IsEnabled();
            MessageBox.Show(this, "تغییر تنظیم اجرای خودکار ممکن نشد.", "Shamsa", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SettingsService.Save(); // notify listeners (tray menu)
    }

    private void UpdateFfmpegStatus()
    {
        string? path = FfmpegManager.GetFfmpegPath();
        FfmpegStatusText.Text = path != null
            ? "✅ FFmpeg آماده است"
            : "⚠️ FFmpeg یافت نشد. برای تبدیل ویدیو/صدا/تصویر دکمه زیر را بزنید (یا: winget install Gyan.FFmpeg).";
        FfmpegPathText.Text = path ?? string.Empty;
        FfmpegPathText.ToolTip = path;
        FfmpegStatusText.Foreground = (System.Windows.Media.Brush)FindResource(path != null ? "SuccessBrush" : "WarnBrush");
        InstallFfmpegBtn.Content = path != null ? "⬇ دانلود مجدد / به‌روزرسانی FFmpeg" : "⬇ دانلود و نصب خودکار FFmpeg";
        if (GuideFfmpegStatusText != null)
        {
            GuideFfmpegStatusText.Text = path != null
                ? "✅ موتور FFmpeg روی سیستم شما آماده است! می‌توانید هر ویدیو، صدا یا تصویری را کاملاً آفلاین و سریع تبدیل کنید."
                : "⚠️ برای تبدیل فرمت ویدیو و صدا نیاز به FFmpeg است. کافی است دکمه زیر را بزنید تا خودش یک‌بار و برای همیشه نصب شود.";
            GuideInstallFfmpegBtn.Visibility = path != null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void RecheckFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        FfmpegManager.GetFfmpegPath(refresh: true);
        UpdateFfmpegStatus();
    }

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        InstallFfmpegBtn.IsEnabled = false;
        if (GuideInstallFfmpegBtn != null) GuideInstallFfmpegBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => FfmpegProgressText.Text = msg);
            var (_, resultMsg) = await FfmpegManager.DownloadFfmpegAsync(progress);
            FfmpegProgressText.Text = resultMsg;
            UpdateFfmpegStatus();
        }
        finally
        {
            InstallFfmpegBtn.IsEnabled = true;
            if (GuideInstallFfmpegBtn != null) GuideInstallFfmpegBtn.IsEnabled = true;
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        ShellActions.OpenPath(AppPaths.LogDir);
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        ShellActions.OpenPath(AppPaths.DataDir);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _dupCts?.Cancel();
        _convertCts?.Cancel();
        _renameDebounce.Stop();
        _organizerDebounce.Stop();
        base.OnClosing(e);
    }
}
