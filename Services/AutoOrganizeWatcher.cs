using System.Collections.Concurrent;

namespace Shamsa.Services;

/// <summary>
/// Watches a folder (default: Downloads) and files new arrivals into category sub-folders,
/// but only once the file is finished: not a partial download, not locked, and size stable.
/// </summary>
public sealed class AutoOrganizeWatcher : IDisposable
{
    private readonly ConcurrentDictionary<string, (long Size, DateTime SeenAt, int StableTicks)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private string _folder = string.Empty;

    /// <summary>Raised on a thread-pool thread: (file name, category folder, destination path).</summary>
    public event Action<string, string, string>? FileOrganized;

    public bool IsRunning => _watcher != null;

    public void Start(string folder)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        _folder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, e) => Track(e.FullPath);
        _watcher.Renamed += (_, e) => Track(e.FullPath); // browsers rename .crdownload → final name
        _watcher.Changed += (_, e) => Track(e.FullPath);
        _watcher.Error += (_, e) => AppLog.Error("Auto-organize watcher error", e.GetException());

        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        AppLog.Info($"Auto-organize watching {folder}");
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
        _pending.Clear();
    }

    private void Track(string path)
    {
        if (FileKinds.IsPartialDownload(path)) return;
        _pending[path] = (-1, DateTime.UtcNow, 0);
    }

    private int _ticking;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return; // never overlap ticks
        try { ProcessPending(); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void ProcessPending()
    {
        foreach (var (path, state) in _pending.ToArray())
        {
            try
            {
                if (!File.Exists(path))
                {
                    _pending.TryRemove(path, out _);
                    continue;
                }

                long size = new FileInfo(path).Length;
                int stable = size == state.Size ? state.StableTicks + 1 : 0;
                _pending[path] = (size, state.SeenAt, stable);

                // require ~4 s of stable size and an exclusive open before moving
                if (stable < 2 || !CanOpenExclusively(path))
                {
                    if (DateTime.UtcNow - state.SeenAt > TimeSpan.FromHours(2)) _pending.TryRemove(path, out _);
                    continue;
                }

                _pending.TryRemove(path, out _);
                string? category = SmartOrganizerService.CategorizeFile(path);
                if (category == null) continue;

                string targetDir = Path.Combine(_folder, category);
                if (File.Exists(targetDir)) continue;
                Directory.CreateDirectory(targetDir);
                string dest = ConverterService.GetUniquePath(Path.Combine(targetDir, Path.GetFileName(path)));
                File.Move(path, dest);
                FileOrganized?.Invoke(Path.GetFileName(path), category, dest);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Auto-organize skipped {path}: {ex.Message}");
            }
        }
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Stop();
}
