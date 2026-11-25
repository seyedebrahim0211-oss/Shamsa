#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace Shamsa.Services;

public sealed record DocumentFirstPagePreview(
    string FilePath,
    string DocumentKind,
    BitmapSource? ThumbnailImage,
    string? ExtractedFirstPageSnippet,
    int? EstimatedPagesOrSheets,
    string SummaryLabel);

/// <summary>
/// Renders high-DPI first-page visual previews for Office documents (.docx, .xlsx, .pptx, .doc, .xls, .ppt)
/// and PDF (.pdf) files using Windows Shell IShellItemImageFactory, with a fast offline OpenXML
/// first-page text/metadata fallback when Office preview handlers are not installed.
/// </summary>
public static class DocumentPreviewService
{
    private static readonly string[] OfficeWordExts = { ".docx", ".doc", ".rtf", ".odt" };
    private static readonly string[] OfficeExcelExts = { ".xlsx", ".xls", ".csv", ".ods" };
    private static readonly string[] OfficePptExts = { ".pptx", ".ppt", ".odp" };

    public static bool IsSupportedDocument(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".pdf"
            || OfficeWordExts.Contains(ext)
            || OfficeExcelExts.Contains(ext)
            || OfficePptExts.Contains(ext);
    }

    public static DocumentFirstPagePreview GenerateFirstPagePreview(string filePath, int maxSidePx = 640)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string kind = ext switch
        {
            ".pdf" => "PDF",
            _ when OfficeWordExts.Contains(ext) => "Word",
            _ when OfficeExcelExts.Contains(ext) => "Excel",
            _ when OfficePptExts.Contains(ext) => "PowerPoint",
            _ => "Document"
        };

        BitmapSource? thumb = TryExtractShellFirstPageBitmap(filePath, maxSidePx);
        string? snippet = null;
        int? count = null;

        if (ext == ".docx")
            (snippet, count) = TryReadDocxFirstPageSnippet(filePath);
        else if (ext == ".xlsx")
            (snippet, count) = TryReadXlsxFirstSheetSnippet(filePath);
        else if (ext == ".pptx")
            (snippet, count) = TryReadPptxFirstSlideSnippet(filePath);
        else if (ext == ".pdf")
            count = TryEstimatePdfPages(filePath);

        string summary = kind switch
        {
            "Excel" when count.HasValue => $"فایل اکسل • {count.Value} کاربرگ (Sheet)",
            "PowerPoint" when count.HasValue => $"ارائه پاورپوینت • {count.Value} اسلاید",
            "PDF" when count.HasValue => $"سند PDF • حدود {count.Value} صفحه",
            "Word" => "سند ورد (پیش‌نمایش صفحه اول)",
            _ => $"سند {kind}"
        };

        return new DocumentFirstPagePreview(filePath, kind, thumb, snippet, count, summary);
    }

    private static BitmapSource? TryExtractShellFirstPageBitmap(string filePath, int size)
    {
        if (!File.Exists(filePath)) return null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            Guid shellItemGuid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref shellItemGuid, out IShellItemImageFactory? factory);
            if (hr != 0 || factory == null) return null;

            var sz = new SIZE { cx = size, cy = size };
            // SIIGBF_THUMBNAILONLY (0x08) | SIIGBF_BIGGERSIZEOK (0x01)
            hr = factory.GetImage(sz, 0x09, out hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                // Fallback to resize-to-fit with icon/preview handler
                hr = factory.GetImage(sz, 0x01, out hBitmap);
            }
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
    }

    private static (string? Snippet, int? Pages) TryReadDocxFirstPageSnippet(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var docEntry = zip.GetEntry("word/document.xml");
            if (docEntry == null) return (null, null);

            using var s = docEntry.Open();
            var xdoc = XDocument.Load(s);
            var sb = new StringBuilder();
            foreach (var p in xdoc.Descendants().Where(e => e.Name.LocalName == "p").Take(14))
            {
                string line = string.Concat(p.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)).Trim();
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine(line);
                if (sb.Length > 750) break;
            }
            return (sb.ToString().Trim(), null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string? Snippet, int? Sheets) TryReadXlsxFirstSheetSnippet(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            int sheetCount = zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase));
            var sharedStrings = Array.Empty<string>();
            var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (ssEntry != null)
            {
                using var ssStream = ssEntry.Open();
                var ssDoc = XDocument.Load(ssStream);
                sharedStrings = ssDoc.Descendants().Where(e => e.Name.LocalName == "si")
                    .Select(si => string.Concat(si.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
                    .ToArray();
            }

            var sheet1 = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (sheet1 == null) return (null, sheetCount);

            using var s = sheet1.Open();
            var xdoc = XDocument.Load(s);
            var sb = new StringBuilder();
            foreach (var row in xdoc.Descendants().Where(e => e.Name.LocalName == "row").Take(10))
            {
                var vals = row.Elements().Where(c => c.Name.LocalName == "c").Take(6).Select(c =>
                {
                    var t = c.Attribute("t")?.Value;
                    var v = c.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value ?? "";
                    if (t == "s" && int.TryParse(v, out int idx) && idx >= 0 && idx < sharedStrings.Length)
                        return sharedStrings[idx];
                    return v;
                }).Where(v => !string.IsNullOrWhiteSpace(v));
                string rowText = string.Join(" | ", vals);
                if (!string.IsNullOrWhiteSpace(rowText)) sb.AppendLine(rowText);
            }
            return (sb.ToString().Trim(), sheetCount);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string? Snippet, int? Slides) TryReadPptxFirstSlideSnippet(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            int slideCount = zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            var slide1 = zip.GetEntry("ppt/slides/slide1.xml");
            if (slide1 == null) return (null, slideCount);

            using var s = slide1.Open();
            var xdoc = XDocument.Load(s);
            var texts = xdoc.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value.Trim()).Where(v => v.Length > 0).Take(12);
            return (string.Join(Environment.NewLine, texts), slideCount);
        }
        catch
        {
            return (null, null);
        }
    }

    private static int? TryEstimatePdfPages(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length > 35_000_000) return null;
            string raw = File.ReadAllText(filePath, Encoding.Latin1);
            int idx = 0, count = 0;
            while ((idx = raw.IndexOf("/Type /Page", idx, StringComparison.Ordinal)) >= 0)
            {
                if (idx + 11 < raw.Length && raw[idx + 11] != 's')
                    count++;
                idx += 11;
            }
            return count > 0 ? count : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
