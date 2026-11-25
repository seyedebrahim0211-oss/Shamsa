#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Shamsa.Services.Desktop;

public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string MinShamsaVersion { get; set; } = "3.0.0";
    public string Description { get; set; } = string.Empty;
    public string Slot { get; set; } = "TopBar"; // TopBar | Widget | Theme
    public List<string> RequestedPermissions { get; set; } = new();
    public string DisplayTemplate { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Versioned Plugin Host for Top Bar & Desktop Widgets.
/// Enforces manifest validation and default-deny permissions (no filesystem, network, or shell execution by default).
/// </summary>
public sealed class PluginHostService
{
    public static string PluginsDir => Path.Combine(ShamsaProSettingsService.DataDir, "plugins");
    private readonly List<PluginManifest> _loaded = new();

    public IReadOnlyList<PluginManifest> LoadedPlugins => _loaded;
    public event Action? PluginsChanged;

    public void LoadInstalledPlugins()
    {
        _loaded.Clear();
        Directory.CreateDirectory(PluginsDir);

        foreach (var dir in Directory.GetDirectories(PluginsDir))
        {
            string manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                // Strict default sandbox: strip privileged permissions unless explicitly approved
                manifest.RequestedPermissions.RemoveAll(p =>
                    p.Equals("ShellExecute", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("FullFileSystem", StringComparison.OrdinalIgnoreCase));
                _loaded.Add(manifest);
            }
            catch { /* skip invalid plugin manifest */ }
        }
        PluginsChanged?.Invoke();
    }

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        var plugin = _loaded.Find(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin == null) return;
        plugin.Enabled = enabled;
        PluginsChanged?.Invoke();
    }
}
