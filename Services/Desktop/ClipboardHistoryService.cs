#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Shamsa.Services.Desktop;

public sealed record ClipboardHistoryItem(
    string Id,
    string TextPreview,
    string FullText,
    DateTimeOffset CopiedAt,
    bool IsPinned);

/// <summary>
/// Local-only Clipboard History Service using Win32 AddClipboardFormatListener.
/// Feeds directly into Spotlight Quick Search without ever transmitting data over the network.
/// </summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxItems = 50;

    private HwndSource? _hwndSource;
    private readonly List<ClipboardHistoryItem> _items = new();

    public IReadOnlyList<ClipboardHistoryItem> Items => _items;
    public event Action? HistoryChanged;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public void StartListening()
    {
        if (_hwndSource != null) return;
        var parameters = new HwndSourceParameters("ShamsaClipboardSink") { Width = 0, Height = 0 };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        AddClipboardFormatListener(_hwndSource.Handle);
    }

    public void StopListening()
    {
        if (_hwndSource == null) return;
        RemoveClipboardFormatListener(_hwndSource.Handle);
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
        _hwndSource = null;
    }

    public IReadOnlyList<ClipboardHistoryItem> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _items;
        return _items.Where(i => i.FullText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void ClearUnpinned()
    {
        _items.RemoveAll(i => !i.IsPinned);
        HistoryChanged?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText().Trim();
                    if (!string.IsNullOrEmpty(text) && (_items.Count == 0 || _items[0].FullText != text))
                    {
                        string preview = text.Length > 120 ? text[..120].Replace('\n', ' ') + "…" : text.Replace('\n', ' ');
                        _items.Insert(0, new ClipboardHistoryItem(Guid.NewGuid().ToString("N"), preview, text, DateTimeOffset.Now, false));
                        if (_items.Count > MaxItems)
                            _items.RemoveAt(_items.Count - 1);
                        HistoryChanged?.Invoke();
                    }
                }
            }
            catch { /* Clipboard temporarily locked by another process */ }
        }
        return IntPtr.Zero;
    }

    public void Dispose() => StopListening();
}
