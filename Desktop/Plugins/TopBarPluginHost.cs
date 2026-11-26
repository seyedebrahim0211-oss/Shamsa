using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Shamsa.Services;

namespace Shamsa.Desktop.Plugins;

public sealed class PluginSource
{
    /// <summary>command | file | http | builtin (or "builtin:uptime" shorthand).</summary>
    public string Type { get; set; } = "builtin";
    public string Value { get; set; } = "";
    public int IntervalSeconds { get; set; } = 60;
}

public sealed class PluginClick
{
    /// <summary>open (URL, file, ms-settings:…) | command (run without a console window).</summary>
    public string Type { get; set; } = "open";
    public string Value { get; set; } = "";
}

/// <summary>A declarative top-bar plugin read from %LocalAppData%\Shamsa\plugins\*.json.</summary>
public sealed class PluginDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Icon { get; set; }
    public string? Text { get; set; }
    public PluginSource? Source { get; set; }
    public PluginClick? OnClick { get; set; }
    public string Position { get; set; } = "right";
    [JsonIgnore] public string FileName { get; set; } = "";
    [JsonIgnore] public string? LoadError { get; set; }
}

/// <summary>Live state of one enabled plugin.</summary>
public sealed class PluginState
{
    public PluginDefinition Definition { get; }
    public string Text { get; internal set; }
    public string? Error { get; internal set; }
    internal DispatcherTimer? Timer;
    internal bool Busy;
    public PluginState(PluginDefinition d) { Definition = d; Text = d.Text ?? ""; }
}

/// <summary>
/// Loads and runs declarative top-bar plugins. Nothing runs unless plugins are enabled in
/// desktop.json and the plugin isn't disabled; commands get 5 s, no console window and only their first output line.
/// </summary>
public sealed class TopBarPluginHost
{
    public static TopBarPluginHost Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly List<PluginState> _active = new();
    private bool _running;
    private string? _signature;

    public static string PluginsDir => Path.Combine(AppPaths.DataDir, "plugins");

    public event Action? Changed;

    public IReadOnlyList<PluginState> Active => _active;
    public bool IsRunning => _running;

    /// <summary>All plugin files (enabled or not) — for the studio page.</summary>
    public static List<PluginDefinition> LoadAll()
    {
        var list = new List<PluginDefinition>();
        try
        {
            if (!Directory.Exists(PluginsDir)) return list;
            foreach (var file in Directory.EnumerateFiles(PluginsDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                PluginDefinition d;
                try
                {
                    d = JsonSerializer.Deserialize<PluginDefinition>(File.ReadAllText(file), Json) ?? new PluginDefinition();
                }
                catch (Exception ex) { d = new PluginDefinition { LoadError = ex.Message }; }
                d.FileName = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(d.Id)) d.Id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(d.Title)) d.Title = d.Id;
                d.Position = (d.Position ?? "right").Trim().ToLowerInvariant() is "left" or "center" ? d.Position!.Trim().ToLowerInvariant() : "right";
                list.Add(d);
            }
        }
        catch (Exception ex) { AppLog.Warn("Plugins: listing failed: " + ex.Message); }
        return list;
    }

    /// <summary>Writes the two harmless samples the very first time (only when the folder doesn't exist yet).</summary>
    public static void EnsureSamples()
    {
        try
        {
            if (Directory.Exists(PluginsDir)) return;
            Directory.CreateDirectory(PluginsDir);
            File.WriteAllText(Path.Combine(PluginsDir, "uptime.json"), SampleUptime);
            File.WriteAllText(Path.Combine(PluginsDir, "local-ip.json"), SampleIp);
        }
        catch (Exception ex) { AppLog.Warn("Plugins: samples not written: " + ex.Message); }
    }

    public void Apply(DesktopSettings s)
    {
        if (!s.Plugins.Enabled) { Stop(); return; }
        EnsureSamples();
        var defs = LoadAll().Where(d => d.LoadError == null && !s.Plugins.Disabled.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        string sig = string.Join("|", defs.Select(d => d.Id + ":" + d.FileName + ":" + JsonSerializer.Serialize(d)));
        if (_running && sig == _signature) return;
        StopTimers();
        _signature = sig;
        _running = true;
        foreach (var d in defs)
        {
            var st = new PluginState(d);
            _active.Add(st);
            if (d.Source != null)
            {
                _ = RefreshAsync(st);
                st.Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(10, d.Source.IntervalSeconds)) };
                st.Timer.Tick += (_, _) => _ = RefreshAsync(st);
                st.Timer.Start();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Preview windows: load and fetch each plugin once without timers when the host isn't running.</summary>
    public void LoadOnceForPreview(DesktopSettings s)
    {
        if (_running || !s.Plugins.Enabled) return;
        Apply(s);
        foreach (var st in _active) st.Timer?.Stop();
        _running = false; // timers stopped; a later Apply() rebuilds normally
        _signature = null;
    }

    public void Stop()
    {
        if (!_running && _active.Count == 0) return;
        StopTimers();
        _running = false;
        _signature = null;
        Changed?.Invoke();
    }

    private void StopTimers()
    {
        foreach (var st in _active) st.Timer?.Stop();
        _active.Clear();
    }

    private async Task RefreshAsync(PluginState st)
    {
        if (st.Busy || st.Definition.Source == null) return;
        st.Busy = true;
        try
        {
            string text = await Task.Run(() => FetchAsync(st.Definition.Source));
            st.Text = Clean(text);
            st.Error = null;
        }
        catch (Exception ex)
        {
            st.Error = ex.Message;
            if (string.IsNullOrEmpty(st.Text)) st.Text = "—";
        }
        finally { st.Busy = false; }
        Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    private static string Clean(string s)
    {
        string line = (s ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 40 ? line[..39] + "…" : line;
    }

    private static async Task<string> FetchAsync(PluginSource src)
    {
        string type = (src.Type ?? "").Trim().ToLowerInvariant();
        string value = src.Value ?? "";
        if (type.StartsWith("builtin:")) { value = type["builtin:".Length..]; type = "builtin"; }
        switch (type)
        {
            case "builtin": return Builtin(value.Trim().ToLowerInvariant());
            case "file":
            {
                string path = Environment.ExpandEnvironmentVariables(value);
                using var reader = new StreamReader(path);
                return await reader.ReadLineAsync() ?? "";
            }
            case "http":
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException("only http/https");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return await Http.GetStringAsync(uri, cts.Token);
            }
            case "command": return await RunCommandAsync(value, capture: true);
            default: throw new InvalidOperationException("unknown source type " + type);
        }
    }

    private static string Builtin(string name)
    {
        switch (name)
        {
            case "uptime":
            {
                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                string text = up.TotalDays >= 1 ? $"{(int)up.TotalDays} روز {up.Hours} ساعت" : $"{up.Hours} ساعت {up.Minutes} دقیقه";
                return DesktopTheme.Digits(text, true);
            }
            case "localip":
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = nic.GetIPProperties();
                    if (!props.GatewayAddresses.Any()) continue;
                    var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (ip != null) return ip.Address.ToString();
                }
                return "بدون شبکه";
            }
            case "date": return PersianDateText(DateTime.Now);
            default: return "?";
        }
    }

    private static string PersianDateText(DateTime d)
    {
        var pc = new System.Globalization.PersianCalendar();
        return DesktopTheme.Digits($"{pc.GetYear(d)}/{pc.GetMonth(d):00}/{pc.GetDayOfMonth(d):00}", true);
    }

    /// <summary>Splits "program args" (first token may be quoted) and runs it hidden with a 5 s limit.</summary>
    internal static async Task<string> RunCommandAsync(string commandLine, bool capture)
    {
        commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string file, args;
        if (commandLine.StartsWith('"'))
        {
            int end = commandLine.IndexOf('"', 1);
            file = end > 0 ? commandLine[1..end] : commandLine.Trim('"');
            args = end > 0 ? commandLine[(end + 1)..].Trim() : "";
        }
        else
        {
            int sp = commandLine.IndexOf(' ');
            file = sp > 0 ? commandLine[..sp] : commandLine;
            args = sp > 0 ? commandLine[(sp + 1)..] : "";
        }
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (capture) psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        if (!capture) return "";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var read = p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return await read;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("command took longer than 5 s");
        }
    }

    /// <summary>Runs the plugin's click action.</summary>
    public static void Click(PluginDefinition d)
    {
        var c = d.OnClick;
        if (c == null || string.IsNullOrWhiteSpace(c.Value)) return;
        try
        {
            if (string.Equals(c.Type, "command", StringComparison.OrdinalIgnoreCase))
                _ = RunCommandAsync(c.Value, capture: false);
            else
                Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(c.Value)) { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Warn($"Plugin {d.Id} click: {ex.Message}"); }
    }

    private const string SampleUptime = """
    {
      "id": "uptime",
      "title": "مدت روشن بودن سیستم",
      "icon": "",
      "source": { "type": "builtin:uptime", "intervalSeconds": 60 },
      "onClick": { "type": "open", "value": "taskmgr.exe" },
      "position": "right"
    }
    """;

    private const string SampleIp = """
    {
      "id": "local-ip",
      "title": "نشانی IP محلی",
      "icon": "",
      "source": { "type": "builtin", "value": "localip", "intervalSeconds": 30 },
      "onClick": { "type": "open", "value": "ms-settings:network-status" },
      "position": "right"
    }
    """;
}
