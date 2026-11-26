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
            ? (string.IsNullOrWhiteSpace(wakeWord) ? "شمسا" : wakeWord.Trim())
            : (IsLatin(wakeWord) && !string.IsNullOrWhiteSpace(wakeWord) ? wakeWord.Trim().ToLowerInvariant() : "shamsa");

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
            Add($"{wake} ضبط کن", "شروع/توقف ضبط", () => AppCommand?.Invoke("record"));
            Add($"{wake} کلیپ بورد", "تاریخچه کلیپ‌بورد", OpenClipboard);
            Add($"{wake} تنظیمات", "استودیو دسکتاپ", () => DesktopEvents.RequestStudio(null));
            foreach (var name in profileNames)
            {
                string n = name;
                Add($"{wake} {n}", "چیدمان ذخیره‌شده: " + n, () => LayoutProfileCommand?.Invoke(n));
            }
            Add($"{wake} حالت کار", "چیدمان «حالت کار»", () => LayoutProfileCommand?.Invoke("حالت کار"));
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
