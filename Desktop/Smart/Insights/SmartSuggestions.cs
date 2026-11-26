using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>A small, dismissible card on the home page. <see cref="Run"/> always asks before changing files.</summary>
public sealed record SmartSuggestion(
    string Id,
    string Glyph,
    string Title,
    string Detail,
    string ActionLabel,
    Action Run,
    Action Dismiss,
    int Priority);

/// <summary>
/// Cheap, local suggestions: cluttered Downloads, morning apps, duplicate files, low disk, big temp/recycle bin.
/// Computed in the background at most every 30 minutes (or on demand); never changes anything without a click
/// and a confirmation. Results are delivered on the UI thread through <see cref="Changed"/>.
/// </summary>
public static class SmartSuggestions
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);

    /// <summary>Overridable folders (tests use temp folders).</summary>
    public static Func<string> DownloadsFolder { get; set; } = () => NativeMethods.GetDownloadsFolder();
    public static Func<string> DesktopFolder { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    /// <summary>Opens a tool page of the app (e.g. "duplicates"); set by the host. Fallback: opens the folder.</summary>
    public static Action<string, string?>? OpenToolRequested { get; set; }

    private static readonly object Gate = new();
    private static List<SmartSuggestion> _current = new();
    private static DateTime _lastComputeUtc = DateTime.MinValue;
    private static int _computing;
    private static Facts? _facts;

    public static IReadOnlyList<SmartSuggestion> Current
    {
        get { lock (Gate) return _current; }
    }

    public static DateTime LastComputeUtc => _lastComputeUtc;

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>Recomputes in the background unless it ran in the last 30 minutes (<paramref name="force"/> overrides).</summary>
    public static void Refresh(bool force = false)
    {
        if (!InsightsStore.Settings.SuggestionsEnabled)
        {
            Publish(new List<SmartSuggestion>());
            return;
        }
        if (!force && DateTime.UtcNow - _lastComputeUtc < MinInterval && _facts != null)
        {
            Publish(Build(_facts));
            return;
        }
        if (Interlocked.Exchange(ref _computing, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                var facts = Gather();
                _facts = facts;
                _lastComputeUtc = DateTime.UtcNow;
                var list = Build(facts);
                RunOnUi(() => Publish(list));
            }
            catch (Exception ex) { AppLog.Warn("Smart suggestions failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _computing, 0); }
        });
    }

    /// <summary>Synchronous variant for tests and the render harness.</summary>
    public static IReadOnlyList<SmartSuggestion> ComputeNow()
    {
        var facts = Gather();
        _facts = facts;
        _lastComputeUtc = DateTime.UtcNow;
        var list = Build(facts);
        Publish(list);
        return list;
    }

    private static void Publish(List<SmartSuggestion> list)
    {
        lock (Gate) _current = list;
        try { Changed?.Invoke(); } catch (Exception ex) { AppLog.Warn("Suggestions listener: " + ex.Message); }
    }

    private static void RunOnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    // ------------------------------------------------------------------ facts (background)

    /// <summary>Everything measured in one pass, so dismissing a card does not rescan disks.</summary>
    internal sealed class Facts
    {
        public string Downloads = "";
        public int DownloadFiles;
        public long DownloadBytes;
        public List<(long Size, List<string> Paths)> Duplicates = new();
        public string SystemDrive = "C:\\";
        public long DriveFree, DriveTotal;
        public long TempBytes;
        public long RecycleBytes;
        public long RecycleItems;
        public List<string> MorningApps = new();
    }

    private static Facts Gather()
    {
        var s = InsightsStore.Settings;
        var f = new Facts();
        string downloads = SafeFolder(DownloadsFolder);
        f.Downloads = downloads;

        if (s.SuggestDownloads && downloads.Length > 0)
        {
            foreach (var file in TopFiles(downloads, 5000))
            {
                f.DownloadFiles++;
                try { f.DownloadBytes += new FileInfo(file).Length; } catch { }
            }
        }

        if (s.SuggestDuplicates)
        {
            var folders = new[] { downloads, SafeFolder(DesktopFolder) }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            f.Duplicates = QuickDuplicates(folders.SelectMany(p => TopFiles(p, 3000)));
        }

        if (s.SuggestDisk)
        {
            try
            {
                f.SystemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
                var d = new DriveInfo(f.SystemDrive);
                f.DriveFree = d.AvailableFreeSpace;
                f.DriveTotal = d.TotalSize;
            }
            catch { }
            f.TempBytes = FolderSize(Path.GetTempPath(), TimeSpan.FromSeconds(1.5));
            (f.RecycleBytes, f.RecycleItems) = RecycleBinInfo();
        }

        if (s.SuggestRoutine)
        {
            var now = DateTime.Now;
            if (now.Hour >= RoutineTracker.MorningStartHour && now.Hour < RoutineTracker.MorningEndHour)
                f.MorningApps = RoutineTracker.MorningApps(now).Where(p => !RoutineTracker.IsRunningProcess(p)).ToList();
        }
        return f;
    }

    private static string SafeFolder(Func<string> get)
    {
        try
        {
            string p = get();
            return !string.IsNullOrWhiteSpace(p) && Directory.Exists(p) ? p : "";
        }
        catch { return ""; }
    }

    /// <summary>Top-level, user-visible, complete files only (no recursion).</summary>
    public static IEnumerable<string> TopFiles(string folder, int cap)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder); }
        catch { yield break; }
        int n = 0;
        foreach (var file in files)
        {
            if (FileKinds.IsHiddenOrSystem(file) || FileKinds.IsPartialDownload(file)) continue;
            if (++n > cap) yield break;
            yield return file;
        }
    }

    /// <summary>Size buckets → 64 KB head hash → confirmed with a tail hash. Files under 1 MB and at most 120 candidates are checked.</summary>
    public static List<(long Size, List<string> Paths)> QuickDuplicates(IEnumerable<string> files)
    {
        var bySize = new Dictionary<long, List<string>>();
        foreach (var file in files)
        {
            long len;
            try { len = new FileInfo(file).Length; } catch { continue; }
            if (len < 1024 * 1024) continue;
            if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = new List<string>();
            list.Add(file);
        }

        var result = new List<(long, List<string>)>();
        int budget = 120;
        foreach (var (size, paths) in bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key).Select(kv => (kv.Key, kv.Value)))
        {
            if (budget <= 0) break;
            var byHash = new Dictionary<string, List<string>>();
            foreach (var p in paths)
            {
                if (budget-- <= 0) break;
                string? h = HeadTailHash(p, size);
                if (h == null) continue;
                if (!byHash.TryGetValue(h, out var l)) byHash[h] = l = new List<string>();
                l.Add(p);
            }
            foreach (var group in byHash.Values.Where(g => g.Count > 1)) result.Add((size, group));
        }
        return result;
    }

    private static string? HeadTailHash(string path, long size)
    {
        try
        {
            const int Block = 64 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Block);
            var buf = new byte[Math.Min(Block, size)];
            int read = fs.Read(buf, 0, buf.Length);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(buf, 0, read);
            if (size > Block * 2)
            {
                fs.Seek(-Block, SeekOrigin.End);
                read = fs.Read(buf, 0, buf.Length);
                sha.AppendData(buf, 0, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch { return null; }
    }

    private static long FolderSize(string folder, TimeSpan budget)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var fi in new DirectoryInfo(folder).EnumerateFiles("*", opts))
            {
                try { total += fi.Length; } catch { }
                if (sw.Elapsed > budget) break;
            }
        }
        catch { }
        return total;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint flags);

    private static (long Bytes, long Items) RecycleBinInfo()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch { return (0, 0); }
    }

    // ------------------------------------------------------------------ cards

    internal static List<SmartSuggestion> Build(Facts f)
    {
        var list = new List<SmartSuggestion>();

        void Add(string id, TimeSpan coolDown, string glyph, string title, string detail, string action, Action run, int priority)
        {
            bool dismissed = InsightsStore.Read(s => s.Dismissed.TryGetValue(id, out var at) && DateTime.UtcNow - at < coolDown);
            if (dismissed) return;
            list.Add(new SmartSuggestion(id, glyph, title, detail, action, run, () => Dismiss(id), priority));
        }

        // Undo of the last Downloads organize (first 24 hours).
        var lastOrganize = InsightsStore.Read(s => (s.LastOrganize.ToList(), s.LastOrganizeUtc));
        if (lastOrganize.Item1.Count > 0 && DateTime.UtcNow - lastOrganize.LastOrganizeUtc < TimeSpan.FromHours(24))
            Add("downloads-undo-" + lastOrganize.LastOrganizeUtc.Ticks, TimeSpan.FromDays(2), "\uE7A7", "مرتب‌سازی دانلودها را برگردانم؟",
                $"{Fa(lastOrganize.Item1.Count)} فایل جابه‌جا شد. اگر نمی‌خواهی، همه به جای قبلی برمی‌گردند.", "برگردان",
                UndoOrganize, 5);

        if (f.DownloadFiles >= 25)
            Add("downloads-clutter", TimeSpan.FromDays(3), "\uE896", "پوشه دانلود شلوغ است",
                $"{Fa(f.DownloadFiles)} فایل ({Size(f.DownloadBytes)}) در Downloads. بر اساس نوع در پوشه‌های جدا مرتب شود؟",
                "مرتبش کن", () => OrganizeDownloads(f.Downloads), 20);

        if (f.MorningApps.Count >= 2)
        {
            string names = string.Join("، ", f.MorningApps.Take(4).Select(RoutineTracker.FriendlyName));
            var apps = f.MorningApps.ToList();
            Add("routine-" + RoutineTracker.DayKey(DateTime.Now), TimeSpan.FromHours(20), "\uE823", "شروع روز با یک کلیک",
                $"صبح‌ها معمولاً این‌ها را باز می‌کنی: {names}", "باز کردن یکجا", () => LaunchAll(apps), 30);
        }

        if (f.Duplicates.Count > 0)
        {
            long wasted = f.Duplicates.Sum(g => g.Size * (g.Paths.Count - 1));
            int extra = f.Duplicates.Sum(g => g.Paths.Count - 1);
            if (wasted >= 20L * 1024 * 1024 || f.Duplicates.Count >= 3)
            {
                var groups = f.Duplicates.ToList();
                Add("duplicates", TimeSpan.FromDays(7), "\uE8C8", "فایل تکراری پیدا شد",
                    $"{Fa(extra)} نسخه اضافه در دانلود و دسکتاپ، {Size(wasted)} فضا.", "بررسی و پاک‌سازی",
                    () => ReviewDuplicates(groups), 40);
            }
        }

        if (f.DriveTotal > 0)
        {
            double ratio = (double)f.DriveFree / f.DriveTotal;
            if (ratio < 0.10 || f.DriveFree < 15L * 1024 * 1024 * 1024)
                Add("disk-low", TimeSpan.FromDays(2), "\uEDA2", "فضای درایو " + f.SystemDrive.TrimEnd('\\') + " کم است",
                    $"فقط {Size(f.DriveFree)} خالی مانده ({Fa((int)Math.Round(ratio * 100))}٪). پاک‌سازی خودکار ویندوز را باز کنم؟",
                    "باز کردن پاک‌سازی", () => OpenShell("ms-settings:storagesense"), 10);
        }

        if (f.TempBytes >= 1L * 1024 * 1024 * 1024)
            Add("temp-big", TimeSpan.FromDays(7), "\uE74D", "فایل‌های موقت زیاد شده",
                $"پوشه Temp حدود {Size(f.TempBytes)} است. با «پاک‌سازی حافظه» ویندوز امن پاک می‌شود.",
                "باز کردن پاک‌سازی", () => OpenShell("ms-settings:storagesense"), 50);

        if (f.RecycleBytes >= 1L * 1024 * 1024 * 1024)
            Add("recycle-big", TimeSpan.FromDays(7), "\uE74D", "سطل بازیافت پر است",
                $"{Fa((int)Math.Min(int.MaxValue, f.RecycleItems))} مورد، {Size(f.RecycleBytes)}.", "خالی کردن",
                () => EmptyRecycleBin(f.RecycleBytes), 60);

        return list.OrderBy(x => x.Priority).ToList();
    }

    public static void Dismiss(string id)
    {
        InsightsStore.Update(s =>
        {
            s.Dismissed[id] = DateTime.UtcNow;
            foreach (var old in s.Dismissed.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromDays(30)).Select(kv => kv.Key).ToList())
                s.Dismissed.Remove(old);
        }, notify: false);
        if (_facts != null) Publish(Build(_facts));
        else Publish(Current.Where(x => x.Id != id).ToList());
    }

    // ------------------------------------------------------------------ actions (UI thread, always confirm)

    private static MessageBoxResult Ask(string text, string title) =>
        MessageBox.Show(text, "نگارخانه — " + title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

    private static void OrganizeDownloads(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var organizer = new SmartOrganizerService();
        var plan = organizer.BuildPlan(folder);
        if (plan.Moves.Count == 0)
        {
            Notifier.Show("چیزی برای مرتب‌سازی پیدا نشد.");
            return;
        }
        string summary = string.Join("\n", plan.Summary.Take(8).Select(x => $"• {FileKinds.Ltr(x.Category)}: {Fa(x.Count)} فایل"));
        if (Ask($"{Fa(plan.Moves.Count)} فایل در این پوشه‌ها مرتب می‌شود:\n\n{summary}\n\nبعداً با یک کلیک قابل برگشت است. ادامه؟", "مرتب‌سازی دانلودها") != MessageBoxResult.Yes)
            return;
        _ = Task.Run(() => organizer.Execute(plan)).ContinueWith(t =>
        {
            if (t.IsFaulted) { AppLog.Warn("Organize failed: " + t.Exception?.GetBaseException().Message); return; }
            var result = t.Result;
            InsightsStore.Update(s =>
            {
                s.LastOrganize = result.Done.Select(m => new ArchiveMove(m.From, m.To, DateTime.UtcNow)).ToList();
                s.LastOrganizeUtc = DateTime.UtcNow;
            });
            RunOnUi(() =>
            {
                Notifier.Show(result.Summary, result.Done.Count > 0 ? folder : null);
                Refresh(force: true);
            });
        });
    }

    private static void UndoOrganize()
    {
        var moves = InsightsStore.Read(s => s.LastOrganize.ToList());
        if (moves.Count == 0) return;
        int back = new SmartOrganizerService().Undo(moves.Select(m => new OrganizeMove(m.From, m.To, "")));
        InsightsStore.Update(s => { s.LastOrganize.Clear(); });
        Notifier.Show($"{Fa(back)} فایل به جای قبلی برگشت.");
        Refresh(force: true);
    }

    private static void LaunchAll(List<string> apps)
    {
        foreach (var exe in apps)
        {
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn("Routine launch failed: " + ex.Message); }
        }
        Dismiss("routine-" + RoutineTracker.DayKey(DateTime.Now));
    }

    /// <summary>Keeps the copy with the shortest name (usually the original, not «(1)»/«- Copy») and recycles the others.</summary>
    public static List<string> ExtraCopies(IEnumerable<(long Size, List<string> Paths)> groups) =>
        groups.SelectMany(g => g.Paths
                .OrderBy(p => Path.GetFileName(p).Length)
                .ThenBy(p => SafeCreation(p))
                .Skip(1))
            .ToList();

    private static DateTime SafeCreation(string p)
    {
        try { return File.GetCreationTimeUtc(p); } catch { return DateTime.MaxValue; }
    }

    private static void ReviewDuplicates(List<(long Size, List<string> Paths)> groups)
    {
        var extras = ExtraCopies(groups).Where(File.Exists).ToList();
        if (extras.Count == 0) { Refresh(force: true); return; }
        long bytes = extras.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
        string sample = string.Join("\n", extras.Take(8).Select(p => "• " + FileKinds.Ltr(Path.GetFileName(p))));
        if (extras.Count > 8) sample += $"\n… و {Fa(extras.Count - 8)} فایل دیگر";
        var answer = MessageBox.Show(
            $"این {Fa(extras.Count)} نسخه اضافه ({Size(bytes)}) به سطل بازیافت می‌رود؛ از هر فایل یک نسخه می‌ماند:\n\n{sample}\n\n«خیر» = فقط پوشه را نشان بده.",
            "نگارخانه — فایل‌های تکراری", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        if (answer == MessageBoxResult.No)
        {
            string folder = Path.GetDirectoryName(extras[0]) ?? "";
            if (OpenToolRequested != null) OpenToolRequested("duplicates", folder);
            else OpenShell("explorer.exe", $"/select,\"{extras[0]}\"");
            return;
        }
        if (answer != MessageBoxResult.Yes) return;
        int moved = 0;
        foreach (var p in extras)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                moved++;
            }
            catch (Exception ex) { AppLog.Warn("Duplicate recycle failed: " + ex.Message); }
        }
        Notifier.Show($"{Fa(moved)} نسخه تکراری به سطل بازیافت رفت (قابل بازگردانی).");
        Refresh(force: true);
    }

    private static void EmptyRecycleBin(long bytes)
    {
        if (Ask($"سطل بازیافت ({Size(bytes)}) برای همیشه خالی شود؟ این کار برگشت‌پذیر نیست.", "سطل بازیافت") != MessageBoxResult.Yes)
            return;
        const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
        try { SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND); }
        catch (Exception ex) { AppLog.Warn("Empty recycle bin failed: " + ex.Message); }
        Refresh(force: true);
    }

    private static void OpenShell(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "" }); }
        catch (Exception ex) { AppLog.Warn("Open failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ Persian formatting

    public static string Fa(int n) => DesktopTheme.Digits(n.ToString("N0", CultureInfo.InvariantCulture).Replace(",", "٬"), true);

    public static string Size(long bytes)
    {
        string[] units = { "بایت", "کیلوبایت", "مگابایت", "گیگابایت", "ترابایت" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        string num = (u == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)).Replace('.', '٫');
        return DesktopTheme.Digits(num, true) + " " + units[u];
    }
}
