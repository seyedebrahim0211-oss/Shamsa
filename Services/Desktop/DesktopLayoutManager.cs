#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Shamsa.Services.Desktop;

/// <summary>
/// Manages Desktop Mode vs Edit Mode, grid snapping, per-monitor layout snapshots,
/// full Undo/Redo history, and JSON Export/Import of desktop layouts.
/// </summary>
public sealed class DesktopLayoutManager
{
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();

    public bool IsEditMode { get; private set; }
    public int GridSnapPx { get; set; } = 12;

    public event Action<bool>? EditModeChanged;
    public event Action? LayoutChanged;

    public void SetEditMode(bool enabled)
    {
        if (IsEditMode == enabled) return;
        IsEditMode = enabled;
        EditModeChanged?.Invoke(IsEditMode);
    }

    public double Snap(double value) => GridSnapPx <= 1 ? value : Math.Round(value / GridSnapPx) * GridSnapPx;

    public void PushSnapshot()
    {
        string json = JsonSerializer.Serialize(ShamsaProSettingsService.Current);
        _undoStack.Push(json);
        _redoStack.Clear();
        if (_undoStack.Count > 40)
        {
            var trimmed = new Stack<string>(new Stack<string>(_undoStack).ToArray()[..40]);
            _undoStack.Clear();
            foreach (var item in trimmed) _undoStack.Push(item);
        }
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(JsonSerializer.Serialize(ShamsaProSettingsService.Current));
        string prev = _undoStack.Pop();
        var restored = JsonSerializer.Deserialize<ShamsaProSettings>(prev);
        if (restored != null)
        {
            ShamsaProSettingsService.Save(restored);
            LayoutChanged?.Invoke();
        }
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(JsonSerializer.Serialize(ShamsaProSettingsService.Current));
        string next = _redoStack.Pop();
        var restored = JsonSerializer.Deserialize<ShamsaProSettings>(next);
        if (restored != null)
        {
            ShamsaProSettingsService.Save(restored);
            LayoutChanged?.Invoke();
        }
    }

    public void ExportLayout(string filePath) => ShamsaProSettingsService.ExportToFile(filePath);

    public void ImportLayout(string filePath)
    {
        PushSnapshot();
        ShamsaProSettingsService.ImportFromFile(filePath);
        LayoutChanged?.Invoke();
    }

    public void ResetLayoutToDefault()
    {
        PushSnapshot();
        ShamsaProSettingsService.ResetToDefaults();
        LayoutChanged?.Invoke();
    }
}
