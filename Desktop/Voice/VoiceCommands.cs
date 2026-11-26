using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Shamsa.Desktop.Launcher;
using Shamsa.Services;

namespace Shamsa.Desktop.Voice;

/// <summary>Which phrase set a recogniser understands.</summary>
public enum VoiceLanguage { Persian, Finglish }

/// <summary>A recognisable phrase and what it does.</summary>
public sealed record VoiceCommand(string Phrase, string Description, Action Run);

/// <summary>
/// Builds the command phrases (Persian or Finglish) and runs them. Commands that belong to other
/// modules are raised as events so the host can wire them (window manager, layouts, recorder).
/// </summary>
public static class VoiceCommands
{
    /// <summary>Window-manager layout id: "Bsp", "Columns", "Rows", "Monocle", "MainStack".</summary>
    public static event Action<string>? LayoutCommand;
    /// <summary>Layout profile name (e.g. «حالت کار») or "#1", "#2"… for the n-th saved profile.</summary>
    public static event Action<string>? LayoutProfileCommand;
    /// <summary>"screenshot" or "record".</summary>
    public static event Action<string>? AppCommand;

    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    public static List<VoiceCommand> Build(VoiceLanguage lang, string wakeWord, IEnumerable<LauncherResult> apps, IEnumerable<string> profileNames)
    {
        var list = new List<VoiceCommand>();
        void Add(string phrase, string desc, Action run)
        {
            phrase = Clean(phrase, lang);
            if (phrase.Length > 0 && list.All(c => c.Phrase != phrase)) list.Add(new VoiceCommand(phrase, desc, run));
        }

        string wake = lang == VoiceLanguage.Persian
            ? (string.IsNullOrWhiteSpace(wakeWord) ? "نگار" : wakeWord.Trim())
            : (IsLatin(wakeWord) && !string.IsNullOrWhiteSpace(wakeWord) ? wakeWord.Trim().ToLowerInvariant() : "negar");

        var layouts = lang == VoiceLanguage.Persian
            ? new (string, string)[] { ("ستونی", "Columns"), ("سطری", "Rows"), ("مارپیچی", "Bsp"), ("تک پنجره", "Monocle"), ("اصلی", "MainStack") }
            : new (string, string)[] { ("sotuni", "Columns"), ("satri", "Rows"), ("marpichi", "Bsp"), ("tak panjere", "Monocle"), ("asli", "MainStack") };
        foreach (var (word, id) in layouts)
            Add(lang == VoiceLanguage.Persian ? $"{wake} چیدمان {word}" : $"{wake} chidman {word}", "چیدمان " + id, () => LayoutCommand?.Invoke(id));

        if (lang == VoiceLanguage.Persian)
        {
            Add($"{wake} جستجو", "باز کردن جستجوی سریع", OpenLauncher);
            Add($"{wake} قفل کن", "قفل صفحه", () => LockWorkStation());
            Add($"{wake} مرکز کنترل", "مرکز کنترل", DesktopEvents.RequestControlCenter);
            Add($"{wake} اسکرین شات", "اسکرین‌شات", () => AppCommand?.Invoke("screenshot"));
            Add($"{wake} عکس بگیر", "اسکرین‌شات", () => AppCommand?.Invoke("screenshot"));
            Add($"{wake} مکث ضبط", "مکث یا ادامهٔ ضبط", () => AppCommand?.Invoke("pause"));
            Add($"{wake} پایان ضبط", "پایان ضبط", () => { if (Recording.RecorderFeature.IsBusy) AppCommand?.Invoke("record"); });
            Add($"{wake} ضبط کن", "شروع/توقف ضبط", () => AppCommand?.Invoke("record"));
            Add($"{wake} کلیپ بورد", "تاریخچه کلیپ‌بورد", OpenClipboard);
            Add($"{wake} تنظیمات", "استودیو دسکتاپ", () => DesktopEvents.RequestStudio(null));
            foreach (var name in profileNames)
            {
                string n = name;
                Add($"{wake} {n}", "چیدمان ذخیره‌شده: " + n, () => LayoutProfileCommand?.Invoke(n));
            }
            Add($"{wake} حالت کار", "چیدمان «حالت کار»", () => LayoutProfileCommand?.Invoke("حالت کار"));
            foreach (var (phrase, desc, run) in SystemCommands(VoiceLanguage.Persian)) Add($"{wake} {phrase}", desc, run);
        }
        else
        {
            Add($"{wake} jostojoo", "باز کردن جستجوی سریع", OpenLauncher);
            Add($"{wake} search", "باز کردن جستجوی سریع", OpenLauncher);
            Add($"{wake} ghofl kon", "قفل صفحه", () => LockWorkStation());
            Add($"{wake} lock", "قفل صفحه", () => LockWorkStation());
            Add($"{wake} markaze control", "مرکز کنترل", DesktopEvents.RequestControlCenter);
            Add($"{wake} control center", "مرکز کنترل", DesktopEvents.RequestControlCenter);
            Add($"{wake} screenshot", "اسکرین‌شات", () => AppCommand?.Invoke("screenshot"));
            Add($"{wake} aks begir", "اسکرین‌شات", () => AppCommand?.Invoke("screenshot"));
            Add($"{wake} zabt kon", "شروع/توقف ضبط", () => AppCommand?.Invoke("record"));
            Add($"{wake} record", "شروع/توقف ضبط", () => AppCommand?.Invoke("record"));
            Add($"{wake} clipboard", "تاریخچه کلیپ‌بورد", OpenClipboard);
            Add($"{wake} tanzimat", "استودیو دسکتاپ", () => DesktopEvents.RequestStudio(null));
            Add($"{wake} halate kar", "چیدمان «حالت کار»", () => LayoutProfileCommand?.Invoke("حالت کار"));
            string[] ordinals = { "yek", "do", "se", "chahar", "panj" };
            for (int i = 0; i < ordinals.Length; i++)
            {
                string id = "#" + (i + 1);
                Add($"{wake} halate {ordinals[i]}", "چیدمان ذخیره‌شده شماره " + (i + 1), () => LayoutProfileCommand?.Invoke(id));
            }
            foreach (var name in profileNames.Where(IsLatin))
            {
                string n = name;
                Add($"{wake} {n}", "چیدمان ذخیره‌شده: " + n, () => LayoutProfileCommand?.Invoke(n));
            }
            foreach (var (phrase, desc, run) in SystemCommands(VoiceLanguage.Finglish)) Add($"{wake} {phrase}", desc, run);
        }

        // Apps last (most phrases). Persian grammar accepts Persian and Latin names; Finglish only Latin.
        int count = 0;
        foreach (var app in apps)
        {
            if (count >= 400) break;
            string title = app.Title;
            if (lang == VoiceLanguage.Finglish && !IsLatin(title)) continue;
            string name = Clean(title, lang);
            if (name.Length < 2 || name.Length > 40) continue;
            var target = app;
            int before = list.Count;
            if (lang == VoiceLanguage.Persian) Add($"{wake} باز کن {name}", "اجرای " + title, () => RunApp(target));
            else
            {
                Add($"{wake} baz kon {name}", "اجرای " + title, () => RunApp(target));
                Add($"{wake} open {name}", "اجرای " + title, () => RunApp(target));
            }
            if (list.Count > before) count++;
        }
        return list;
    }

    // ------------------------------------------------------------------ Persian system commands

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    private static void Tap(byte vk, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2 /* KEYUP */, UIntPtr.Zero);
        }
    }

    private static void Shell(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("Voice shell command failed: " + ex.Message); }
    }

    private static void ShowDesktop()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            shell.ToggleDesktop();
        }
        catch (Exception ex) { AppLog.Warn("Voice show desktop failed: " + ex.Message); }
    }

    /// <summary>Windows light/dark for apps and taskbar (HKCU only, the same switch as Settings › Personalization).</summary>
    private static void SetWindowsDark(bool dark)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            key.SetValue("AppsUseLightTheme", dark ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) { AppLog.Warn("Voice theme switch failed: " + ex.Message); }
    }

    private static void OpenFolderStudio()
    {
        try { FolderStudio.FolderStudioFeature.ShowWindow(); }
        catch (Exception ex) { AppLog.Warn("Voice folder studio failed: " + ex.Message); }
    }

    /// <summary>Everyday Persian commands (and their Finglish twins) that do not depend on other modules.</summary>
    private static IEnumerable<(string Phrase, string Description, Action Run)> SystemCommands(VoiceLanguage lang)
    {
        bool fa = lang == VoiceLanguage.Persian;
        (string, string, string, Action)[] all =
        {
            ("صدا بیشتر", "sedaa bishtar", "بلندتر کردن صدا", () => Tap(VK_VOLUME_UP, 5)),
            ("صدا کمتر", "sedaa kamtar", "کم کردن صدا", () => Tap(VK_VOLUME_DOWN, 5)),
            ("بی صدا", "bi sedaa", "قطع/وصل صدا", () => Tap(VK_VOLUME_MUTE)),
            ("پخش", "pakhsh", "پخش/مکث رسانه", () => Tap(VK_MEDIA_PLAY_PAUSE)),
            ("مکث", "maks", "پخش/مکث رسانه", () => Tap(VK_MEDIA_PLAY_PAUSE)),
            ("آهنگ بعدی", "ahang badi", "آهنگ بعدی", () => Tap(VK_MEDIA_NEXT)),
            ("آهنگ قبلی", "ahang ghabli", "آهنگ قبلی", () => Tap(VK_MEDIA_PREV)),
            ("دسکتاپ", "desktop", "نمایش دسکتاپ", ShowDesktop),
            ("این کامپیوتر", "in computer", "باز کردن This PC", () => Shell("shell:MyComputerFolder")),
            ("مای کامپیوتر", "my computer", "باز کردن This PC", () => Shell("shell:MyComputerFolder")),
            ("سطل زباله", "satle zobale", "باز کردن سطل بازیافت", () => Shell("shell:RecycleBinFolder")),
            ("دانلودها", "downloads", "پوشه دانلودها", () => Shell("shell:Downloads")),
            ("تنظیمات ویندوز", "tanzimate windows", "تنظیمات ویندوز", () => Shell("ms-settings:")),
            ("وای فای", "wifi", "تنظیمات وای‌فای", () => Shell("ms-settings:network-wifi")),
            ("بلوتوث", "bluetooth", "تنظیمات بلوتوث", () => Shell("ms-settings:bluetooth")),
            ("مدیر وظایف", "task manager", "Task Manager", () => Shell("taskmgr.exe")),
            ("حالت تاریک", "halate tarik", "تم تیره ویندوز", () => SetWindowsDark(true)),
            ("حالت روشن", "halate roshan", "تم روشن ویندوز", () => SetWindowsDark(false)),
            ("نگارخانه", "negarkhaneh", "باز کردن نگارخانه", () => DesktopEvents.RequestStudio(null)),
            ("داشبورد", "dashboard", "باز کردن نگارخانه", () => DesktopEvents.RequestStudio(null)),
            ("اوقات شرعی", "oghate sharei", "اوقات شرعی و ابزارک‌ها", () => DesktopEvents.RequestStudio("widgets")),
            ("آب و هوا", "ab o hava", "آب‌وهوا و ابزارک‌ها", () => DesktopEvents.RequestStudio("widgets")),
            ("شخصی سازی", "shakhsi sazi", "شخصی‌سازی ویندوز", () => DesktopEvents.RequestStudio("personalize")),
            ("استودیو پوشه", "studio pooshe", "استودیو پوشه", OpenFolderStudio),
            ("آیکون پوشه", "icon pooshe", "استودیو پوشه", OpenFolderStudio),
        };
        foreach (var (faPhrase, enPhrase, desc, run) in all)
            yield return (fa ? faPhrase : enPhrase, desc, run);
    }

    /// <summary>Command table for the help page: (Persian, Finglish, what it does).</summary>
    public static IEnumerable<(string Persian, string Finglish, string Description)> SystemCommandHelp()
    {
        var fa = SystemCommands(VoiceLanguage.Persian).ToList();
        var en = SystemCommands(VoiceLanguage.Finglish).ToList();
        for (int i = 0; i < fa.Count; i++) yield return (fa[i].Phrase, en[i].Phrase, fa[i].Description);
    }

    private static void OpenLauncher()
    {
        if (!LauncherModule.OpenWithQuery("")) DesktopEvents.RequestLauncher();
    }

    private static void OpenClipboard()
    {
        if (LauncherModule.Instance != null) LauncherModule.Instance.Show(clipboardMode: true);
        else DesktopEvents.RequestLauncher();
    }

    private static void RunApp(LauncherResult app)
    {
        try
        {
            if (app.Execute != null) app.Execute();
            else LauncherIndex.ShellRun(app.Target, null);
        }
        catch (Exception ex) { AppLog.Warn("Voice app launch failed: " + ex.Message); }
    }

    public static bool IsLatin(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.All(c => c < 0x0250 || char.IsWhiteSpace(c) || char.IsPunctuation(c));

    /// <summary>Recogniser-friendly phrase: lower case, letters/digits/spaces only, Persian letters unified.</summary>
    public static string Clean(string s, VoiceLanguage lang)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace('ي', 'ی').Replace('ك', 'ک').Replace('‌', ' ').Replace('‌', ' ');
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s.Normalize(NormalizationForm.FormC))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(lang == VoiceLanguage.Finglish ? char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch));
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
