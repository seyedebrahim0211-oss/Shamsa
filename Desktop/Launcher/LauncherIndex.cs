using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Shamsa.Desktop.Dock;
using Shamsa.Services;

namespace Shamsa.Desktop.Launcher;

public enum LauncherKind { App, Setting, File, Command, Calc, Web }

public sealed class LauncherResult
{
    public LauncherKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    /// <summary>What is launched (path, URI or AppsFolder id) — also the history key.</summary>
    public string Target { get; init; } = "";
    /// <summary>Path whose shell icon is shown, or null for a glyph.</summary>
    public string? IconPath { get; init; }
    public string Glyph { get; init; } = "";
    /// <summary>Custom action (commands, calculator); null = shell-execute <see cref="Target"/>.</summary>
    public Action? Execute { get; init; }
    /// <summary>File-system path for "open location" (Ctrl+Enter), when meaningful.</summary>
    public string? LocationPath { get; init; }
    public double Score { get; set; }
    internal string[] Keys { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Search sources for the Spotlight launcher: Start-menu shortcuts and the AppsFolder (UWP + desktop
/// apps), curated ms-settings pages, recent/shallow user files, commands and a safe calculator.
/// Indexing runs in the background; <see cref="Search"/> is cheap and runs on the UI thread.
/// </summary>
public sealed class LauncherIndex : IDisposable
{
    private List<LauncherResult> _apps = new();
    private List<LauncherResult> _files = new();
    private readonly List<LauncherResult> _settings = BuildSettings();
    private readonly List<LauncherResult> _commands = BuildCommands();
    private readonly List<FileSystemWatcher> _watchers = new();
    private DateTime _filesIndexedUtc = DateTime.MinValue;
    private int _appsDirty = 1;
    private Task? _appsTask, _filesTask;
    private Timer? _debounce;

    public event Action? Updated;
    public bool IsReady => _apps.Count > 0;

    public void Warm(bool files)
    {
        if (Interlocked.Exchange(ref _appsDirty, 0) == 1 && (_appsTask == null || _appsTask.IsCompleted))
            _appsTask = Task.Run(IndexApps);
        if (files && DateTime.UtcNow - _filesIndexedUtc > TimeSpan.FromSeconds(60) && (_filesTask == null || _filesTask.IsCompleted))
            _filesTask = Task.Run(IndexFiles);
        if (_watchers.Count == 0) StartWatchers();
    }

    public Task WhenIndexedAsync() => Task.WhenAll(new[] { _appsTask, _filesTask }.Where(t => t != null)!);

    private void StartWatchers()
    {
        foreach (var dir in StartMenuDirs())
        {
            try
            {
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                FileSystemEventHandler h = (_, _) => ScheduleReindex();
                w.Created += h; w.Deleted += h; w.Changed += h; w.Renamed += (_, _) => ScheduleReindex();
                _watchers.Add(w);
            }
            catch (Exception ex) { AppLog.Warn("Launcher watcher: " + ex.Message); }
        }
    }

    private void ScheduleReindex()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => { _appsDirty = 1; Warm(false); }, null, 2000, Timeout.Infinite);
    }

    private static IEnumerable<string> StartMenuDirs()
    {
        foreach (var f in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            string d = Environment.GetFolderPath(f);
            if (Directory.Exists(d)) yield return d;
        }
    }

    // ------------------------------------------------------------------ indexing

    private void IndexApps()
    {
        var list = new List<LauncherResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in StartMenuDirs())
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).ToList(); } catch { continue; }
                foreach (var f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                    string name = Path.GetFileNameWithoutExtension(f);
                    string n = Normalize(name);
                    if (n.Contains("uninstall") || n.Contains("حذف نصب") || n.StartsWith("readme") || n.Contains("help")) continue;
                    if (!seen.Add(n)) continue;
                    string folder = Path.GetFileName(Path.GetDirectoryName(f)) ?? "";
                    bool topLevel = string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase) || folder == "Programs";
                    list.Add(new LauncherResult
                    {
                        Kind = LauncherKind.App, Title = name, Target = f, IconPath = f, Glyph = "",
                        Subtitle = topLevel ? "برنامه" : "برنامه · " + folder, LocationPath = f,
                        Keys = new[] { n }
                    });
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Launcher start-menu index: " + ex.Message); }

        // AppsFolder (Store apps + anything Start knows about) — Shell.Application needs an STA thread.
        try
        {
            var items = new List<(string Name, string Path)>();
            var t = new Thread(() =>
            {
                try
                {
                    var type = Type.GetTypeFromProgID("Shell.Application");
                    if (type == null) return;
                    dynamic shell = Activator.CreateInstance(type)!;
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    if (folder == null) return;
                    foreach (dynamic item in folder.Items())
                    {
                        try { items.Add(((string)item.Name, (string)item.Path)); } catch { }
                    }
                    Marshal.FinalReleaseComObject(shell);
                }
                catch (Exception ex) { AppLog.Warn("Launcher AppsFolder: " + ex.Message); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromSeconds(15));
            foreach (var (name, path) in items)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                string n = Normalize(name);
                if (n.Contains("uninstall") || !seen.Add(n)) continue;
                bool isFile = path.Length > 2 && path[1] == ':' && File.Exists(path);
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                string shellPath = isFile ? path : @"shell:AppsFolder\" + path;
                list.Add(new LauncherResult
                {
                    Kind = LauncherKind.App, Title = name, Target = shellPath, IconPath = shellPath, Glyph = "",
                    Subtitle = isFile ? "برنامه" : "برنامه ویندوز", LocationPath = isFile ? path : null,
                    Keys = new[] { n }
                });
            }
        }
        catch (Exception ex) { AppLog.Warn("Launcher AppsFolder index: " + ex.Message); }

        _apps = list;
        Updated?.Invoke();
    }

    private void IndexFiles()
    {
        var list = new List<LauncherResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string subtitle, bool isLink)
        {
            string title = isLink ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
            if (string.IsNullOrEmpty(title) || !seen.Add(title + "|" + subtitle)) return;
            bool dir = !isLink && Directory.Exists(path);
            list.Add(new LauncherResult
            {
                Kind = LauncherKind.File, Title = title, Subtitle = subtitle, Target = path, IconPath = path,
                Glyph = dir ? "" : "", LocationPath = path, Keys = new[] { Normalize(title) }
            });
        }
        try
        {
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (Directory.Exists(recent))
                foreach (var f in new DirectoryInfo(recent).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(150))
                    Add(f.FullName, "اخیر", true);
            var roots = new List<(string Dir, string Label)>();
            foreach (var d in NativeMethods.GetDesktopFolders()) roots.Add((d, "دسکتاپ"));
            roots.Add((Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "اسناد"));
            roots.Add((NativeMethods.GetDownloadsFolder(), "دانلودها"));
            foreach (var (dir, label) in roots)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos().Take(400))
                {
                    if ((e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (e.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    Add(e.FullName, label, false);
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Launcher file index: " + ex.Message); }
        _files = list;
        _filesIndexedUtc = DateTime.UtcNow;
        Updated?.Invoke();
    }

    // ------------------------------------------------------------------ static sources

    private static List<LauncherResult> BuildSettings()
    {
        (string Fa, string Keys, string Uri)[] table =
        {
            ("تنظیمات ویندوز", "settings windows", "ms-settings:"),
            ("وای‌فای", "wifi wi-fi wireless بی سیم وای فای", "ms-settings:network-wifi"),
            ("وضعیت شبکه و اینترنت", "network internet شبکه اینترنت", "ms-settings:network-status"),
            ("بلوتوث و دستگاه‌ها", "bluetooth devices دستگاه", "ms-settings:bluetooth"),
            ("نمایشگر", "display screen resolution scale صفحه نمایش رزولوشن مقیاس", "ms-settings:display"),
            ("نور شب", "night light blue light نور آبی", "ms-settings:nightlight"),
            ("صدا", "sound audio volume speaker بلندگو", "ms-settings:sound"),
            ("اعلان‌ها", "notifications اعلان نوتیفیکیشن", "ms-settings:notifications"),
            ("مزاحم نشوید / تمرکز", "focus assist do not disturb quiet", "ms-settings:quiethours"),
            ("صرفه‌جویی باتری", "battery saver باتری", "ms-settings:batterysaver"),
            ("برق و خواب", "power sleep خواب نیرو انرژی", "ms-settings:powersleep"),
            ("فضای ذخیره‌سازی", "storage disk space حافظه دیسک", "ms-settings:storagesense"),
            ("چندوظیفگی و چیدمان پنجره", "multitasking snap windows اسنپ", "ms-settings:multitasking"),
            ("درباره سیستم", "about system pc info مشخصات سیستم", "ms-settings:about"),
            ("شخصی‌سازی", "personalization شخصی سازی", "ms-settings:personalization"),
            ("تصویر پس‌زمینه", "background wallpaper والپیپر تصویر زمینه", "ms-settings:personalization-background"),
            ("رنگ‌ها و حالت تاریک", "colors dark mode accent تیره روشن", "ms-settings:personalization-colors"),
            ("تم‌ها", "themes تم", "ms-settings:themes"),
            ("صفحه قفل", "lock screen", "ms-settings:lockscreen"),
            ("نوار وظیفه", "taskbar تسکبار", "ms-settings:taskbar"),
            ("منوی شروع", "start menu استارت", "ms-settings:personalization-start"),
            ("فونت‌ها", "fonts قلم فونت", "ms-settings:fonts"),
            ("برنامه‌های نصب‌شده", "apps features uninstall installed حذف برنامه", "ms-settings:appsfeatures"),
            ("برنامه‌های پیش‌فرض", "default apps پیش فرض", "ms-settings:defaultapps"),
            ("برنامه‌های راه‌اندازی", "startup apps استارتاپ", "ms-settings:startupapps"),
            ("اطلاعات حساب", "account your info حساب کاربری", "ms-settings:yourinfo"),
            ("گزینه‌های ورود", "sign-in password pin رمز عبور پین", "ms-settings:signinoptions"),
            ("تاریخ و ساعت", "date time clock زمان", "ms-settings:dateandtime"),
            ("زبان و صفحه‌کلید", "language keyboard input فارسی زبان کیبورد", "ms-settings:regionlanguage"),
            ("منطقه و قالب‌ها", "region format منطقه", "ms-settings:regionformatting"),
            ("تایپ", "typing autocorrect تایپ", "ms-settings:typing"),
            ("گیم بار", "gaming game bar بازی", "ms-settings:gaming-gamebar"),
            ("دسترسی‌پذیری", "accessibility ease of access", "ms-settings:easeofaccess"),
            ("ذره‌بین", "magnifier zoom بزرگنمایی", "ms-settings:easeofaccess-magnifier"),
            ("حریم خصوصی", "privacy security", "ms-settings:privacy"),
            ("دسترسی دوربین", "camera webcam وبکم", "ms-settings:privacy-webcam"),
            ("دسترسی میکروفون", "microphone mic میکروفن", "ms-settings:privacy-microphone"),
            ("موقعیت مکانی", "location gps مکان", "ms-settings:privacy-location"),
            ("به‌روزرسانی ویندوز", "windows update آپدیت بروزرسانی", "ms-settings:windowsupdate"),
            ("امنیت ویندوز", "windows security defender antivirus آنتی ویروس ضدویروس", "windowsdefender:"),
            ("بازیابی و بازنشانی", "recovery reset ریست", "ms-settings:recovery"),
            ("فعال‌سازی ویندوز", "activation license لایسنس", "ms-settings:activation"),
            ("چاپگرها و اسکنرها", "printers scanners پرینتر", "ms-settings:printers"),
            ("ماوس", "mouse موس", "ms-settings:mousetouchpad"),
            ("تاچ‌پد", "touchpad تاچ پد", "ms-settings:devices-touchpad"),
            ("کلیپ‌بورد", "clipboard history کلیپ بورد", "ms-settings:clipboard"),
            ("پروکسی", "proxy پراکسی", "ms-settings:network-proxy"),
            ("VPN", "vpn وی پی ان فیلترشکن", "ms-settings:network-vpn"),
            ("هات‌اسپات موبایل", "mobile hotspot هات اسپات", "ms-settings:network-mobilehotspot"),
        };
        return table.Select(t => new LauncherResult
        {
            Kind = LauncherKind.Setting, Title = t.Fa, Subtitle = "تنظیمات ویندوز", Target = t.Uri, Glyph = "",
            IconPath = SettingsIconPath, Keys = new[] { Normalize(t.Fa), Normalize(t.Keys) }
        }).ToList();
    }

    internal static string? SettingsIconPath
    {
        get
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"ImmersiveControlPanel\SystemSettings.exe");
            return File.Exists(p) ? p : null;
        }
    }

    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    private static List<LauncherResult> BuildCommands()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        var list = new List<LauncherResult>
        {
            Cmd("قفل صفحه", "lock screen قفل کردن", "", "cmd:lock", () => LockWorkStation()),
            Cmd("تنظیمات میز کار شمسا", "shamsa desktop studio settings شمسا تنظیمات", "", "cmd:studio", () => DesktopEvents.RequestStudio(null)),
            Cmd("تنظیمات داک", "dock settings داک", "", "cmd:studio-dock", () => DesktopEvents.RequestStudio("dock")),
            Cmd("تنظیمات جستجوی سریع", "launcher spotlight settings میانبر", "", "cmd:studio-launcher", () => DesktopEvents.RequestStudio("launcher")),
            Cmd("سطل بازیافت", "recycle bin trash سطل زباله", "", "cmd:recycle", () => ShellRun("explorer.exe", "shell:RecycleBinFolder")),
            Cmd("پوشه دانلودها", "downloads دانلود", "", "cmd:downloads", () => ShellRun(NativeMethods.GetDownloadsFolder(), null)),
            Cmd("مدیر وظایف", "task manager تسک منیجر", "", "cmd:taskmgr", () => ShellRun(Path.Combine(sys, "Taskmgr.exe"), null)),
            Cmd("کنترل پنل", "control panel", "", "cmd:control", () => ShellRun(Path.Combine(sys, "control.exe"), null)),
            Cmd("ترمینال", "terminal cmd command prompt powershell خط فرمان", "", "cmd:terminal",
                () => ShellRun(File.Exists(wt) ? wt : Path.Combine(sys, "cmd.exe"), null)),
            Cmd("ابزار برش صفحه", "snipping tool screenshot اسکرین شات", "", "cmd:snip", () => ShellRun("ms-screenclip:", null)),
        };
        _ = win;
        return list;

        static LauncherResult Cmd(string fa, string keys, string glyph, string id, Action a) => new()
        {
            Kind = LauncherKind.Command, Title = fa, Subtitle = "فرمان", Target = id, Glyph = glyph, Execute = a,
            Keys = new[] { Normalize(fa), Normalize(keys) }
        };
    }

    public static void ShellRun(string target, string? args)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex) { AppLog.Warn($"Launcher run '{target}': {ex.Message}"); }
    }

    // ------------------------------------------------------------------ search

    public List<LauncherResult> Search(string query, LauncherSettings s, LauncherKind? only)
    {
        var results = new List<LauncherResult>();
        string q = Normalize(query);
        if (q.Length == 0) return results;

        if (s.Calculator && (only == null || only == LauncherKind.Calc) && Calculator.TryEvaluate(query, out double value))
        {
            string text = Calculator.Format(value);
            results.Add(new LauncherResult
            {
                Kind = LauncherKind.Calc, Title = "= " + text, Subtitle = "ماشین‌حساب · Enter برای کپی", Target = "calc:" + text,
                Glyph = "", Score = 5000,
                Execute = () => { try { System.Windows.Clipboard.SetText(text); } catch { } }
            });
        }

        IEnumerable<LauncherResult> pool = Enumerable.Empty<LauncherResult>();
        if (s.SearchApps && only is null or LauncherKind.App) pool = pool.Concat(_apps);
        if (s.SearchSettings && only is null or LauncherKind.Setting) pool = pool.Concat(_settings);
        if (only is null or LauncherKind.Command) pool = pool.Concat(_commands);
        if (s.SearchFiles && only is null or LauncherKind.File) pool = pool.Concat(_files);

        var history = s.History;
        foreach (var r in pool)
        {
            double best = 0;
            for (int i = 0; i < r.Keys.Length; i++)
            {
                double sc = Score(q, r.Keys[i]);
                if (i > 0) sc *= 0.85; // keyword match ranks below a title match
                if (sc > best) best = sc;
            }
            if (best <= 0) continue;
            best += r.Kind switch { LauncherKind.App => 40, LauncherKind.Setting => 15, LauncherKind.Command => 20, _ => 0 };
            int h = history.IndexOf(r.Target);
            if (h >= 0) best += Math.Max(0, 50 - h) * 6;
            r.Score = best;
            results.Add(r);
        }

        var ordered = results.OrderByDescending(r => r.Score).ThenBy(r => r.Title.Length)
                             .GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                             .Take(s.MaxResults).ToList();
        if (s.WebSearch && only is null or LauncherKind.Web && !string.IsNullOrWhiteSpace(s.WebSearchUrl))
        {
            string url = s.WebSearchUrl.Contains("{0}") ? s.WebSearchUrl.Replace("{0}", Uri.EscapeDataString(query.Trim()))
                                                       : s.WebSearchUrl + Uri.EscapeDataString(query.Trim());
            ordered.Add(new LauncherResult
            {
                Kind = LauncherKind.Web, Title = $"جستجوی «{query.Trim()}» در وب", Subtitle = new Uri(url).Host,
                Target = url, Glyph = "", Score = 0
            });
        }
        return ordered;
    }

    /// <summary>prefix &gt; word-start &gt; contains &gt; subsequence; 0 = no match.</summary>
    internal static double Score(string q, string text)
    {
        if (text.Length == 0) return 0;
        if (text == q) return 1000;
        if (text.StartsWith(q, StringComparison.Ordinal)) return 900 - Math.Min(100, text.Length - q.Length);
        int idx = text.IndexOf(q, StringComparison.Ordinal);
        if (idx > 0)
        {
            // word start?
            int pos = idx;
            while (pos >= 0)
            {
                if (pos == 0 || text[pos - 1] is ' ' or '-' or '_' or '.' or '(') return 700 - Math.Min(100, pos);
                pos = text.IndexOf(q, pos + 1, StringComparison.Ordinal);
            }
            return 500 - Math.Min(100, idx);
        }
        // initials ("vsc" → "visual studio code")
        if (q.Length >= 2)
        {
            var initials = new StringBuilder();
            bool start = true;
            foreach (char c in text)
            {
                if (c == ' ' || c == '-' || c == '_' || c == '.') { start = true; continue; }
                if (start) initials.Append(c);
                start = false;
            }
            if (initials.ToString().StartsWith(q, StringComparison.Ordinal)) return 600;
        }
        // subsequence with gap penalty — must start at a word start and stay compact, or noise floods in
        if (q.Length < 3) return 0;
        int first = -1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == q[0] && (i == 0 || text[i - 1] is ' ' or '-' or '_' or '.' or '(')) { first = i; break; }
        if (first < 0) return 0;
        int ti = first, gaps = 0, last = -1;
        foreach (char c in q)
        {
            if (c == ' ') continue;
            int found = text.IndexOf(c, ti);
            if (found < 0) return 0;
            if (last >= 0) gaps += found - last - 1;
            last = found;
            ti = found + 1;
        }
        if (gaps > q.Length * 2) return 0;
        double score = 300 - gaps * 12;
        return score > 50 ? score : 0;
    }

    /// <summary>Lower-case, unify Arabic/Persian letters, strip ZWNJ/diacritics/tatweel, ASCII digits.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (char ch in s.Normalize(NormalizationForm.FormC))
        {
            char c = ch;
            switch (c)
            {
                case 'ي': case 'ى': case 'ئ': c = 'ی'; break;
                case 'ك': c = 'ک'; break;
                case 'ة': c = 'ه'; break;
                case 'أ': case 'إ': case 'آ': case 'ٱ': c = 'ا'; break;
                case 'ؤ': c = 'و'; break;
                case '‌': case '‍': case '‏': case '‎': case 'ـ': continue;
            }
            if (c >= 'ً' && c <= 'ٟ' || c == 'ٰ') continue;
            if (c >= '۰' && c <= '۹') c = (char)('0' + (c - '۰'));
            else if (c >= '٠' && c <= '٩') c = (char)('0' + (c - '٠'));
            if (char.IsWhiteSpace(c))
            {
                if (lastSpace || sb.Length == 0) continue;
                sb.Append(' '); lastSpace = true; continue;
            }
            lastSpace = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}

/// <summary>Safe arithmetic: + - * / % ^ parentheses, unary minus, Persian/Arabic digits, × ÷.</summary>
public static class Calculator
{
    public static bool TryEvaluate(string input, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var sb = new StringBuilder();
        foreach (char ch in input.Trim().TrimStart('='))
        {
            char c = ch;
            if (c >= '۰' && c <= '۹') c = (char)('0' + (c - '۰'));
            else if (c >= '٠' && c <= '٩') c = (char)('0' + (c - '٠'));
            else if (c is '×' or 'x' or 'X') c = '*';
            else if (c == '÷') c = '/';
            else if (c is '٫' or ',') c = '.';
            else if (c == '٪') c = '%';
            if (c == ' ') continue;
            if (!("0123456789.+-*/%^()".Contains(c))) return false;
            sb.Append(c);
        }
        string expr = sb.ToString();
        // must contain an operator between numbers — a bare number is not a calculation
        if (expr.Length < 3 || !expr.Any(char.IsDigit) || !expr.Skip(1).Any(c => "+-*/%^".Contains(c))) return false;
        try
        {
            var p = new Parser(expr);
            value = p.ParseExpression();
            if (p.Pos != expr.Length || double.IsNaN(value) || double.IsInfinity(value)) return false;
            return true;
        }
        catch { return false; }
    }

    public static string Format(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-10 && Math.Abs(v) < 1e15
            ? Math.Round(v).ToString("N0", CultureInfo.InvariantCulture)
            : v.ToString("0.##########", CultureInfo.InvariantCulture);

    private sealed class Parser
    {
        private readonly string _s;
        public int Pos;
        private int _depth;
        public Parser(string s) { _s = s; }
        private char Peek => Pos < _s.Length ? _s[Pos] : '\0';

        public double ParseExpression()
        {
            double v = ParseTerm();
            while (Peek is '+' or '-') { char op = _s[Pos++]; double r = ParseTerm(); v = op == '+' ? v + r : v - r; }
            return v;
        }

        private double ParseTerm()
        {
            double v = ParseFactor();
            while (Peek is '*' or '/' or '%')
            {
                char op = _s[Pos++];
                double r = ParseFactor();
                v = op switch { '*' => v * r, '/' => v / r, _ => v % r };
            }
            return v;
        }

        private double ParseFactor()
        {
            double b = ParseUnary();
            if (Peek == '^') { Pos++; double e = ParseFactor(); return Math.Pow(b, e); } // right-assoc
            return b;
        }

        private double ParseUnary()
        {
            if (Peek == '-') { Pos++; return -ParseUnary(); }
            if (Peek == '+') { Pos++; return ParseUnary(); }
            if (Peek == '(')
            {
                if (++_depth > 64) throw new FormatException();
                Pos++;
                double v = ParseExpression();
                if (Peek != ')') throw new FormatException();
                Pos++; _depth--;
                return v;
            }
            int start = Pos;
            while (char.IsDigit(Peek) || Peek == '.') Pos++;
            if (start == Pos) throw new FormatException();
            return double.Parse(_s[start..Pos], CultureInfo.InvariantCulture);
        }
    }
}
