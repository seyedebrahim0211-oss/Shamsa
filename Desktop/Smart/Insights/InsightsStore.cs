using System.Text.Json;
using System.Text.Json.Serialization;
using Shamsa.Services;

namespace Shamsa.Desktop.Smart.Insights;

/// <summary>User choices for the smart suggestions, desktop tidy and archive.</summary>
public sealed class InsightsSettings
{
    /// <summary>Show suggestions on the home page (only local reads; nothing is changed without a click).</summary>
    public bool SuggestionsEnabled { get; set; } = true;
    /// <summary>Learn which apps are opened in the morning (stored only on this PC).</summary>
    public bool LearnRoutine { get; set; } = true;
    public bool SuggestDownloads { get; set; } = true;
    public bool SuggestDuplicates { get; set; } = true;
    public bool SuggestDisk { get; set; } = true;
    public bool SuggestRoutine { get; set; } = true;

    /// <summary>Desktop groups (view only — files are not moved).</summary>
    public bool TidyEnabled { get; set; } = false;
    /// <summary>Move desktop files older than <see cref="ArchiveDays"/> into Desktop\بایگانی (with undo).</summary>
    public bool ArchiveEnabled { get; set; } = false;
    public int ArchiveDays { get; set; } = 30;

    public void Normalize()
    {
        if (ArchiveDays < 3) ArchiveDays = 3;
        if (ArchiveDays > 365) ArchiveDays = 365;
    }
}

/// <summary>One file move done by the archive, kept so it can be undone.</summary>
public sealed record ArchiveMove(string From, string To, DateTime AtUtc);

/// <summary>Learned morning routine: for each program, the days it was first used before noon.</summary>
public sealed class RoutineData
{
    public Dictionary<string, List<string>> MorningDays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LastDay { get; set; } = "";
    public List<string> SeenToday { get; set; } = new();
}

public sealed class InsightsState
{
    public InsightsSettings Settings { get; set; } = new();
    /// <summary>Suggestion id → when it was dismissed (UTC). Dismissed ids return after their cool-down.</summary>
    public Dictionary<string, DateTime> Dismissed { get; set; } = new();
    public RoutineData Routine { get; set; } = new();
    public List<ArchiveMove> LastArchive { get; set; } = new();
    /// <summary>Moves of the last «مرتب کن» on Downloads (for the undo suggestion).</summary>
    public List<ArchiveMove> LastOrganize { get; set; } = new();
    public DateTime LastOrganizeUtc { get; set; }
}

/// <summary>Loads/saves %LocalAppData%\Shamsa\smart-insights.json. Thread-safe.</summary>
public static class InsightsStore
{
    private static readonly object Gate = new();
    private static InsightsState? _state;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Overridable for tests.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppPaths.DataDir, "smart-insights.json");

    public static event Action? Changed;

    public static InsightsState State
    {
        get
        {
            lock (Gate) return _state ??= Load();
        }
    }

    public static InsightsSettings Settings => State.Settings;

    private static InsightsState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<InsightsState>(File.ReadAllText(FilePath), Json);
                if (s != null) return Fix(s);
            }
        }
        catch (Exception ex) { AppLog.Warn("smart-insights.json unreadable: " + ex.Message); }
        return Fix(new InsightsState());
    }

    private static InsightsState Fix(InsightsState s)
    {
        s.Settings ??= new();
        s.Settings.Normalize();
        s.Dismissed ??= new();
        s.Routine ??= new();
        s.Routine.MorningDays ??= new(StringComparer.OrdinalIgnoreCase);
        s.Routine.SeenToday ??= new();
        s.LastArchive ??= new();
        s.LastOrganize ??= new();
        return s;
    }

    public static void Save(bool notify = true)
    {
        string text;
        lock (Gate)
        {
            var s = State;
            s.Settings.Normalize();
            text = JsonSerializer.Serialize(s, Json);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { AppLog.Warn("Saving smart-insights.json failed: " + ex.Message); }
        if (notify) Changed?.Invoke();
    }

    /// <summary>Test hook: forget the cached state (next access reloads <see cref="FilePath"/>).</summary>
    public static void Reset()
    {
        lock (Gate) _state = null;
    }

    /// <summary>Reads under the store lock (the state is also written from timers).</summary>
    public static T Read<T>(Func<InsightsState, T> read)
    {
        lock (Gate) return read(State);
    }

    public static void Update(Action<InsightsState> change, bool notify = true)
    {
        lock (Gate) change(State);
        Save(notify);
    }
}
