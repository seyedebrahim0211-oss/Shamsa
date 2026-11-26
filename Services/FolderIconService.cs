#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Shamsa.Services;

public sealed record FolderIconHistoryEntry(
    string FolderPath,
    string IconPath,
    string Label,
    DateTimeOffset AppliedAtUtc);

/// <summary>
/// Fold11-inspired Folder Icon Studio & Customizer for Shamsa.
/// Supports generating custom multi-color/gradient/text folder .ico files, converting PNG/JPG/ICO,
/// applying icons to single or multiple folders via desktop.ini + SHChangeNotify, favorites/history,
/// and non-admin HKCU Windows Explorer context menu integration.
/// </summary>
public static class FolderIconService
{
    private const uint SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;

    public static string IconLibraryDir => Path.Combine(ShamsaProSettingsService.DataDir, "folder-icons");
    public static string HistoryFile => Path.Combine(IconLibraryDir, "history.json");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Generates a modern Windows 11-style folder .ico file with custom gradient colors and optional badge text.
    /// </summary>
    public static string CreateCustomFolderIcon(
        string name,
        string topHex,
        string bottomHex,
        string? badgeText = null,
        string? overlayImagePath = null)
    {
        Directory.CreateDirectory(IconLibraryDir);
        try
        {
            bool hasImage = !string.IsNullOrWhiteSpace(overlayImagePath) && File.Exists(overlayImagePath);
            var design = new Shamsa.FolderStudio.IconDesign
            {
                Name = string.IsNullOrWhiteSpace(name) ? "پوشه" : name,
                BaseHex = bottomHex,
                GradientHex = topHex,
                Overlay = hasImage ? Shamsa.FolderStudio.IconOverlayKind.Image
                        : string.IsNullOrWhiteSpace(badgeText) ? Shamsa.FolderStudio.IconOverlayKind.None : Shamsa.FolderStudio.IconOverlayKind.Text,
                OverlayText = badgeText?.Trim() ?? "",
                ImagePath = hasImage ? overlayImagePath! : "",
                ImageMode = Shamsa.FolderStudio.IconImageMode.Badge
            };
            if (Shamsa.FolderStudio.FolderIconRenderer.IsValidHex(bottomHex))
                return Shamsa.FolderStudio.FolderIconRenderer.EnsureIcoFile(design);
        }
        catch (Exception ex) { AppLog.Warn("High-quality folder icon failed, using fallback: " + ex.Message); }
        string safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "folder";
        string icoPath = Path.Combine(IconLibraryDir, $"{safeName}_{DateTime.UtcNow:yyyyMMddHHmmss}.ico");

        using var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            Color top = ColorTranslator.FromHtml(topHex);
            Color bottom = ColorTranslator.FromHtml(bottomHex);

            // Back folder tab
            using (var tabBrush = new SolidBrush(ControlPaintDarken(top)))
            using (var tabPath = RoundedRect(new Rectangle(18, 36, 98, 42), 14))
                g.FillPath(tabBrush, tabPath);

            // Main folder body with gradient
            using (var bodyBrush = new LinearGradientBrush(new Rectangle(18, 56, 220, 174), top, bottom, 90f))
            using (var bodyPath = RoundedRect(new Rectangle(18, 56, 220, 174), 22))
                g.FillPath(bodyBrush, bodyPath);

            // Glass highlight strip
            using (var glossBrush = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
            using (var glossPath = RoundedRect(new Rectangle(26, 66, 204, 34), 12))
                g.FillPath(glossBrush, glossPath);

            // Optional imported PNG/JPG overlay
            if (!string.IsNullOrWhiteSpace(overlayImagePath) && File.Exists(overlayImagePath))
            {
                using var img = Image.FromFile(overlayImagePath);
                g.DrawImage(img, new Rectangle(78, 94, 100, 100));
            }
            else if (!string.IsNullOrWhiteSpace(badgeText))
            {
                using var font = new Font("Vazirmatn", 30, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(Color.White);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(badgeText.Trim(), font, textBrush, new RectangleF(24, 96, 208, 100), sf);
            }
        }

        WritePngWrappedIco(bmp, icoPath);
        return icoPath;
    }

    public static string ConvertImageToIco(string imagePath)
    {
        Directory.CreateDirectory(IconLibraryDir);
        string ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (ext == ".ico")
        {
            string copyDest = Path.Combine(IconLibraryDir, Path.GetFileName(imagePath));
            File.Copy(imagePath, copyDest, overwrite: true);
            return copyDest;
        }

        // Preferred path: the Folder Studio renderer writes every Explorer size (16…256) with high-quality
        // resampling and keeps the picture's proportions. The old single 256-px frame below stretched the
        // picture and let Windows shrink it, which looked soft.
        try
        {
            var design = new Shamsa.FolderStudio.IconDesign
            {
                Name = Path.GetFileNameWithoutExtension(imagePath),
                Overlay = Shamsa.FolderStudio.IconOverlayKind.Image,
                ImagePath = imagePath,
                ImageMode = Shamsa.FolderStudio.FolderIconRenderer.SuggestMode(imagePath),
                RoundedImage = true
            };
            return Shamsa.FolderStudio.FolderIconRenderer.EnsureIcoFile(design);
        }
        catch (Exception ex) { AppLog.Warn("High-quality icon conversion failed, using fallback: " + ex.Message); }

        using var src = Image.FromFile(imagePath);
        using var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, 256, 256));
        }

        string dest = Path.Combine(IconLibraryDir, $"{Path.GetFileNameWithoutExtension(imagePath)}_{DateTime.UtcNow:HHmmss}.ico");
        WritePngWrappedIco(bmp, dest);
        return dest;
    }

    public static void ApplyIconToFolders(IEnumerable<string> folderPaths, string icoPath, string label = "آیکون سفارشی")
    {
        if (!File.Exists(icoPath))
            throw new FileNotFoundException("فایل آیکون یافت نشد.", icoPath);

        var history = LoadHistory();
        foreach (var folder in folderPaths)
        {
            if (!Directory.Exists(folder)) continue;
            string desktopIni = Path.Combine(folder, "desktop.ini");
            if (File.Exists(desktopIni))
                File.SetAttributes(desktopIni, FileAttributes.Normal);

            string content =
                "[.ShellClassInfo]\r\n" +
                $"IconResource={icoPath},0\r\n" +
                $"IconFile={icoPath}\r\n" +
                "IconIndex=0\r\n";

            File.WriteAllText(desktopIni, content);
            File.SetAttributes(desktopIni, FileAttributes.Hidden | FileAttributes.System);

            var dirAttr = File.GetAttributes(folder);
            File.SetAttributes(folder, dirAttr | FileAttributes.ReadOnly);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, folder, IntPtr.Zero);

            history.Insert(0, new FolderIconHistoryEntry(folder, icoPath, label, DateTimeOffset.UtcNow));
        }

        SaveHistory(history);
    }

    public static void ResetFolderIcon(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;
        string desktopIni = Path.Combine(folderPath, "desktop.ini");
        if (File.Exists(desktopIni))
        {
            File.SetAttributes(desktopIni, FileAttributes.Normal);
            File.Delete(desktopIni);
        }
        var attr = File.GetAttributes(folderPath);
        File.SetAttributes(folderPath, attr & ~FileAttributes.ReadOnly);
        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, folderPath, IntPtr.Zero);
    }

    /// <summary>
    /// Registers or unregisters "شخصی‌سازی آیکون پوشه با نگارخانه" in HKCU (no Administrator required).
    /// </summary>
    public static void SetExplorerContextMenu(bool enabled, string shamsaExePath)
    {
        const string keyPath = @"Software\Classes\Directory\shell\ShamsaFolderIcon";
        if (!enabled)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key?.SetValue("", "شخصی‌سازی آیکون و قفل پوشه با نگارخانه");
        key?.SetValue("Icon", shamsaExePath);
        using var cmd = key?.CreateSubKey("command");
        cmd?.SetValue("", $"\"{shamsaExePath}\" --folder-icon \"%1\"");
    }

    public static List<FolderIconHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFile))
                return JsonSerializer.Deserialize<List<FolderIconHistoryEntry>>(File.ReadAllText(HistoryFile)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveHistory(List<FolderIconHistoryEntry> list)
    {
        Directory.CreateDirectory(IconLibraryDir);
        if (list.Count > 100) list.RemoveRange(100, list.Count - 100);
        File.WriteAllText(HistoryFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WritePngWrappedIco(Bitmap bmp, string icoPath)
    {
        using var pngStream = new MemoryStream();
        bmp.Save(pngStream, ImageFormat.Png);
        byte[] pngBytes = pngStream.ToArray();

        using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write((short)0); // Reserved
        bw.Write((short)1); // Icon type
        bw.Write((short)1); // Image count
        bw.Write((byte)0);  // Width (0 = 256)
        bw.Write((byte)0);  // Height (0 = 256)
        bw.Write((byte)0);  // Color palette
        bw.Write((byte)0);  // Reserved
        bw.Write((short)1); // Color planes
        bw.Write((short)32);// Bits per pixel
        bw.Write(pngBytes.Length);
        bw.Write(22);       // Offset to image data
        bw.Write(pngBytes);
    }

    private static Color ControlPaintDarken(Color c) => Color.FromArgb(c.A, (int)(c.R * 0.78), (int)(c.G * 0.78), (int)(c.B * 0.78));

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
