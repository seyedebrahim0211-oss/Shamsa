namespace Shamsa.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shamsa");

    public static string LogDir => Path.Combine(DataDir, "logs");
}

/// <summary>Tiny thread-safe file logger so silent failures become diagnosable.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1024 * 1024;

    public static string LogFile => Path.Combine(AppPaths.LogDir, "shamsa.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string old = LogFile + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogFile, old);
                }
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
