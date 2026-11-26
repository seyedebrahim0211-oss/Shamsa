using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

namespace Shamsa.Services;

/// <summary>Rendered first page of a document plus a short Persian summary (pages, sheets, slides).</summary>
public sealed record DocumentPreview(BitmapSource Image, string Summary, Color AccentColor);

/// <summary>
/// Read-only first-page previews for Office, OpenDocument, PDF and text files.
/// Order of preference: the document's own embedded thumbnail → Windows renderer (PDF) →
/// a lightweight local rendering of the first page's text. Files are never executed,
/// macros are never evaluated and decompressed reads are capped to resist zip bombs.
/// </summary>
public static class DocumentPreviewService
{
    /// <summary>Compatibility alias for the uploaded Shamsa Pro windows.</summary>
    public static bool IsSupportedDocument(string path) => IsSupported(path);

    /// <summary>Synchronous, shell-thumbnail based preview for the uploaded Shamsa Pro windows (UI/STA thread).</summary>
    public static DocumentFirstPagePreview GenerateFirstPagePreview(string filePath, int maxSidePx = 640)
    {
        string ext = Path.GetExtension(filePath);
        BitmapSource? image = null;
        try { image = ShellThumbnailService.TryGetShellImage(filePath, maxSidePx, thumbnailOnly: true) ?? ShellThumbnailService.TryGetShellImage(filePath, maxSidePx); } catch { }
        return new DocumentFirstPagePreview(filePath, LabelFor(ext), image, null, null, LabelFor(ext));
    }

    private const long MaxFileBytes = 250_000_000;
    private const int MaxXmlChars = 1_500_000;

    private static readonly HashSet<string> WordExt = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx" };
    private static readonly HashSet<string> ExcelExt = new(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".xlsm", ".xltx" };
    private static readonly HashSet<string> PowerPointExt = new(StringComparer.OrdinalIgnoreCase) { ".pptx", ".pptm", ".ppsx", ".potx" };
    private static readonly HashSet<string> OpenDocExt = new(StringComparer.OrdinalIgnoreCase) { ".odt", ".ods", ".odp" };
    private static readonly HashSet<string> LegacyOfficeExt = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".xls", ".ppt", ".rtf", ".xps", ".vsdx" };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".log", ".ini", ".json", ".xml", ".yaml", ".yml", ".cs", ".js", ".ts", ".tsx", ".jsx",
        ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".html", ".htm", ".css", ".scss",
        ".sql", ".sh", ".ps1", ".bat", ".cmd", ".kt", ".swift", ".dart", ".vue", ".srt", ".vtt"
    };

    public static readonly Color WordColor = Color.FromRgb(0x2B, 0x7C, 0xE9);
    public static readonly Color ExcelColor = Color.FromRgb(0x21, 0xA3, 0x66);
    public static readonly Color PowerPointColor = Color.FromRgb(0xE8, 0x6A, 0x3A);
    public static readonly Color PdfColor = Color.FromRgb(0xE1, 0x3D, 0x45);
    public static readonly Color TextColor = Color.FromRgb(0x94, 0xA3, 0xB8);

    public static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || WordExt.Contains(ext) || ExcelExt.Contains(ext)
            || PowerPointExt.Contains(ext) || OpenDocExt.Contains(ext) || LegacyOfficeExt.Contains(ext) || TextExt.Contains(ext);
    }

    public static IEnumerable<string> SupportedExtensions =>
        WordExt.Concat(ExcelExt).Concat(PowerPointExt).Concat(OpenDocExt).Concat(LegacyOfficeExt).Concat(TextExt).Append(".pdf");

    /// <summary>Renders the preview. The non-PDF path builds WPF visuals, so it is executed on the supplied STA worker.</summary>
    public static async Task<DocumentPreview?> RenderAsync(string path, int maxSide, StaWorker sta)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) return null;
            if ((info.Attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) return null;
        }
        catch { return null; }

        string ext = Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pdf = await Task.Run(() => RenderPdfAsync(path, maxSide));
            if (pdf != null) return pdf;
        }
        return await sta.RunAsync(() => RenderSync(path, maxSide));
    }

    private static DocumentPreview? RenderSync(string path, int maxSide)
    {
        string ext = Path.GetExtension(path);
        try
        {
            if (WordExt.Contains(ext)) return RenderWord(path, maxSide);
            if (ExcelExt.Contains(ext)) return RenderExcel(path, maxSide);
            if (PowerPointExt.Contains(ext)) return RenderPowerPoint(path, maxSide);
            if (OpenDocExt.Contains(ext)) return RenderOpenDocument(path, maxSide);
            if (TextExt.Contains(ext)) return RenderText(path, maxSide);
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || LegacyOfficeExt.Contains(ext))
            {
                var shell = ShellThumbnailService.TryGetShellImage(path, Math.Max(256, maxSide), thumbnailOnly: true);
                return shell == null ? null : new DocumentPreview(shell, LabelFor(ext), ColorFor(ext));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            AppLog.Warn($"Document preview failed for {Path.GetFileName(path)}: {ex.Message}");
        }
        return null;
    }

    public static string LabelFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".dotx" or ".rtf" or ".odt" => "سند Word",
        ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".ods" or ".csv" => "کاربرگ Excel",
        ".ppt" or ".pptx" or ".pptm" or ".ppsx" or ".potx" or ".odp" => "ارائه PowerPoint",
        ".pdf" => "سند PDF",
        ".xps" => "سند XPS",
        ".md" => "Markdown",
        ".txt" or ".log" => "متن",
        _ => "کد / متن"
    };

    public static Color ColorFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".dotx" or ".rtf" or ".odt" => WordColor,
        ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".ods" or ".csv" => ExcelColor,
        ".ppt" or ".pptx" or ".pptm" or ".ppsx" or ".potx" or ".odp" => PowerPointColor,
        ".pdf" => PdfColor,
        _ => TextColor
    };

    // ------------------------------------------------------------------ PDF (Windows.Data.Pdf)

    private static async Task<DocumentPreview?> RenderPdfAsync(string path, int maxSide)
    {
        var (bmp, pages) = await RenderPdfPageAsync(path, 0, maxSide);
        return bmp == null ? null : new DocumentPreview(bmp, $"PDF · {ToFa(pages)} صفحه", PdfColor);
    }

    /// <summary>Number of pages of a PDF (0 when unreadable / protected).</summary>
    public static async Task<int> GetPdfPageCountAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            return (int)doc.PageCount;
        }
        catch { return 0; }
    }

    public static string PersianNumber(int n) => ToFa(n);

    /// <summary>Renders one PDF page (0-based) read-only through Windows.Data.Pdf. Thread-agnostic; result is frozen.</summary>
    public static async Task<(BitmapSource? Image, int PageCount)> RenderPdfPageAsync(string path, int pageIndex, int maxSide)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            if (doc.PageCount == 0) return (null, 0);
            pageIndex = (int)Math.Clamp(pageIndex, 0, doc.PageCount - 1);
            using var page = doc.GetPage((uint)pageIndex);
            double w = page.Size.Width, h = page.Size.Height;
            if (w <= 0 || h <= 0) return (null, (int)doc.PageCount);
            double target = Math.Clamp(maxSide * 1.5, 320, 1400);
            double scale = target / Math.Max(w, h);
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(w * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(h * scale)),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
            };
            await page.RenderToStreamAsync(ras, options);
            ras.Seek(0);
            using var net = ras.AsStreamForRead();
            var copy = new MemoryStream();
            await net.CopyToAsync(copy);
            copy.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = copy;
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, (int)doc.PageCount);
        }
        catch (Exception ex)
        {
            // Password-protected or damaged PDFs fall back to the shell thumbnail.
            AppLog.Warn($"PDF preview fallback for {Path.GetFileName(path)}: {ex.Message}");
            return (null, 0);
        }
    }

    // ------------------------------------------------------------------ Office Open XML

    private sealed record Para(string Text, double Size, bool Bold, bool Rtl, bool Bullet = false);

    private static DocumentPreview? RenderWord(string path, int maxSide)
    {
        using var zip = OpenZip(path);
        string pages = ReadAppProperty(zip, "Pages");
        string summary = "سند Word" + (pages.Length > 0 ? $" · {ToFaDigits(pages)} صفحه" : "");

        var thumb = ReadEmbeddedThumbnail(zip);
        if (thumb != null) return new DocumentPreview(thumb, summary, WordColor);

        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return null;
        var paras = new List<Para>();
        using (var reader = CreateReader(entry))
        {
            var sb = new StringBuilder();
            string style = string.Empty;
            bool bold = false, rtl = false, bullet = false, runBold = false;
            int total = 0;
            while (reader.Read() && paras.Count < 70 && total < 4000)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "p": sb.Clear(); style = string.Empty; bold = false; rtl = false; bullet = false; break;
                        case "pStyle": style = reader.GetAttribute("val", reader.NamespaceURI) ?? string.Empty; break;
                        case "bidi": rtl = true; break;
                        case "numPr": bullet = true; break;
                        case "b" when reader.GetAttribute("val", reader.NamespaceURI) is null or "1" or "true": runBold = true; break;
                        case "r": runBold = false; break;
                        case "tab": sb.Append("    "); break;
                        case "br": sb.Append(' '); break;
                        case "t":
                            string t = reader.ReadElementContentAsString();
                            if (runBold && t.Trim().Length > 0) bold = true;
                            sb.Append(t);
                            total += t.Length;
                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p")
                {
                    string text = sb.ToString();
                    var (size, isBold) = WordStyle(style);
                    paras.Add(new Para(text, size, isBold || (bold && text.Length < 120), rtl || HasRtl(text), bullet));
                }
            }
        }
        if (paras.All(p => string.IsNullOrWhiteSpace(p.Text))) return null;
        var image = RenderPage(paras, 620, 877, Colors.White, Color.FromRgb(0x1F, 0x29, 0x37), WordColor, maxSide);
        return new DocumentPreview(image, summary, WordColor);
    }

    private static (double Size, bool Bold) WordStyle(string style)
    {
        string s = style.ToLowerInvariant();
        if (s.Contains("title")) return (26, true);
        if (s.Contains("subtitle")) return (16, false);
        if (s.Contains("heading1") || s == "1") return (20, true);
        if (s.Contains("heading2") || s == "2") return (16.5, true);
        if (s.Contains("heading")) return (14, true);
        return (11.5, false);
    }

    private static DocumentPreview? RenderExcel(string path, int maxSide)
    {
        using var zip = OpenZip(path);
        var sheetNames = new List<string>();
        string? firstSheetRid = null;
        var wb = zip.GetEntry("xl/workbook.xml");
        if (wb != null)
        {
            using var r = CreateReader(wb);
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                {
                    sheetNames.Add(r.GetAttribute("name") ?? "Sheet");
                    firstSheetRid ??= r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                }
            }
        }
        string summary = "کاربرگ Excel" + (sheetNames.Count > 0 ? $" · {ToFa(sheetNames.Count)} برگه" : "");

        string sheetPath = "xl/worksheets/sheet1.xml";
        if (firstSheetRid != null && zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            using var r = CreateReader(rels);
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") == firstSheetRid)
                {
                    string target = (r.GetAttribute("Target") ?? string.Empty).Replace('\\', '/');
                    sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                    break;
                }
            }
        }

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
        {
            using var r = CreateReader(sst);
            var sb = new StringBuilder();
            bool inSi = false;
            // ReadElementContentAsString already advances the reader, so only call Read() otherwise.
            bool advance = r.Read();
            while (advance && shared.Count < 20000)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && inSi)
                {
                    sb.Append(r.ReadElementContentAsString());
                    continue;
                }
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") { inSi = true; sb.Clear(); }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si") { shared.Add(sb.ToString()); inSi = false; }
                advance = r.Read();
            }
        }

        const int maxRows = 22, maxCols = 9;
        var grid = new string?[maxRows, maxCols];
        var sheet = zip.GetEntry(sheetPath);
        int usedRows = 0, usedCols = 0;
        if (sheet != null)
        {
            using var r = CreateReader(sheet);
            string? cellRef = null, cellType = null;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.LocalName == "row" && int.TryParse(r.GetAttribute("r"), out int rowNo) && rowNo > maxRows) break;
                if (r.LocalName == "c") { cellRef = r.GetAttribute("r"); cellType = r.GetAttribute("t"); }
                else if ((r.LocalName == "v" || (r.LocalName == "t" && cellType == "inlineStr")) && cellRef != null)
                {
                    string raw = r.ReadElementContentAsString();
                    var (row, col) = ParseCellRef(cellRef);
                    if (row < 0 || row >= maxRows || col < 0 || col >= maxCols) continue;
                    string value = cellType switch
                    {
                        "s" when int.TryParse(raw, out int idx) && idx >= 0 && idx < shared.Count => shared[idx],
                        "b" => raw == "1" ? "TRUE" : "FALSE",
                        _ => FormatNumber(raw)
                    };
                    grid[row, col] = value;
                    usedRows = Math.Max(usedRows, row + 1);
                    usedCols = Math.Max(usedCols, col + 1);
                }
            }
        }

        var thumb = ReadEmbeddedThumbnail(zip);
        if (usedRows == 0 && thumb != null) return new DocumentPreview(thumb, summary, ExcelColor);
        if (usedRows == 0) return null;
        var image = RenderSheet(grid, Math.Max(usedRows, 12), Math.Max(usedCols, 6), sheetNames.FirstOrDefault() ?? "Sheet1", maxSide);
        return new DocumentPreview(image, summary, ExcelColor);
    }

    private static DocumentPreview? RenderPowerPoint(string path, int maxSide)
    {
        using var zip = OpenZip(path);
        string slides = ReadAppProperty(zip, "Slides");
        string summary = "ارائه PowerPoint" + (slides.Length > 0 ? $" · {ToFaDigits(slides)} اسلاید" : "");
        var thumb = ReadEmbeddedThumbnail(zip);
        if (thumb != null) return new DocumentPreview(thumb, summary, PowerPointColor);

        var entry = zip.GetEntry("ppt/slides/slide1.xml");
        if (entry == null) return null;
        var lines = new List<string>();
        using (var r = CreateReader(entry))
        {
            var sb = new StringBuilder();
            while (r.Read() && lines.Count < 14)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t") sb.Append(r.ReadElementContentAsString());
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "p")
                {
                    if (sb.Length > 0) lines.Add(sb.ToString());
                    sb.Clear();
                }
            }
        }
        if (lines.Count == 0) return null;
        var paras = new List<Para> { new(lines[0], 30, true, HasRtl(lines[0])) };
        paras.AddRange(lines.Skip(1).Select(l => new Para(l, 15, false, HasRtl(l), true)));
        var image = RenderPage(paras, 960, 540, Color.FromRgb(0xFB, 0xF7, 0xF4), Color.FromRgb(0x2A, 0x1E, 0x17), PowerPointColor, maxSide, slide: true);
        return new DocumentPreview(image, summary, PowerPointColor);
    }

    private static DocumentPreview? RenderOpenDocument(string path, int maxSide)
    {
        using var zip = OpenZip(path);
        var entry = zip.GetEntry("Thumbnails/thumbnail.png");
        string ext = Path.GetExtension(path);
        if (entry == null || entry.Length > 8_000_000) return null;
        using var s = entry.Open();
        var bmp = DecodeBitmap(s, maxSide);
        return bmp == null ? null : new DocumentPreview(bmp, LabelFor(ext) + " · OpenDocument", ColorFor(ext));
    }

    // ------------------------------------------------------------------ text / code

    private static DocumentPreview? RenderText(string path, int maxSide)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192))
        {
            var buffer = new byte[12_000];
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read == 0) return null;
            if (buffer.AsSpan(0, Math.Min(read, 2000)).IndexOf((byte)0) >= 0 && !HasUtf16Bom(buffer)) return null; // binary
            text = DecodeText(buffer, read);
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool code = ext is not (".txt" or ".md" or ".log" or ".srt" or ".vtt");
        var paras = new List<Para>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n').Take(80))
        {
            string line = rawLine.Replace("\t", "    ");
            if (ext == ".md" && line.StartsWith('#'))
            {
                int level = line.TakeWhile(c => c == '#').Count();
                string t = line.TrimStart('#').Trim();
                paras.Add(new Para(t, level == 1 ? 22 : level == 2 ? 17 : 14, true, HasRtl(t)));
            }
            else if (ext == ".csv") paras.Add(new Para(string.Join("  │  ", line.Split(',').Take(8)), 10.5, false, HasRtl(line)));
            else paras.Add(new Para(line, code ? 10.5 : 11.5, false, !code && HasRtl(line)));
        }
        long lines = 0;
        try { lines = text.Count(c => c == '\n'); } catch { }
        string summary = LabelFor(ext);
        var image = RenderPage(paras, 620, 877, code ? Color.FromRgb(0x0F, 0x17, 0x2A) : Colors.White,
            code ? Color.FromRgb(0xD6, 0xE2, 0xF0) : Color.FromRgb(0x1F, 0x29, 0x37), TextColor, maxSide, monospace: code);
        return new DocumentPreview(image, summary, TextColor);
    }

    private static bool HasUtf16Bom(byte[] b) => b.Length > 1 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    private static string DecodeText(byte[] buffer, int count)
    {
        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return Encoding.UTF8.GetString(buffer, 3, count - 3);
        if (HasUtf16Bom(buffer)) return (buffer[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(buffer, 2, count - 2);
        try { return new UTF8Encoding(false, true).GetString(buffer, 0, count); }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(1256).GetString(buffer, 0, count); } // Windows Persian/Arabic
            catch { return Encoding.Latin1.GetString(buffer, 0, count); }
        }
    }

    // ------------------------------------------------------------------ rendering

    private static readonly FontFamily UiFamily = new("Vazirmatn, Segoe UI, Tahoma");
    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas, Courier New");

    private static BitmapSource RenderPage(List<Para> paras, double pageW, double pageH, Color paper, Color ink, Color accent,
        int maxSide, bool slide = false, bool monospace = false)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(paper), null, new Rect(0, 0, pageW, pageH));
            if (slide)
            {
                dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(0, 0, 14, pageH));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)), null, new Rect(pageW - 220, pageH - 140, 220, 140));
            }
            else
            {
                dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(0, 0, pageW, 5));
            }

            double margin = slide ? 64 : (monospace ? 26 : 52);
            double y = slide ? 70 : (monospace ? 24 : 54);
            double maxW = pageW - margin * 2;
            var dpi = 1.25;
            var inkBrush = new SolidColorBrush(ink);
            var mutedInk = new SolidColorBrush(Color.FromArgb(150, ink.R, ink.G, ink.B));
            foreach (var p in paras)
            {
                if (y > pageH - margin * 0.6) break;
                string text = p.Text.Length > 900 ? p.Text[..900] + "…" : p.Text;
                if (string.IsNullOrWhiteSpace(text)) { y += p.Size * 0.9; continue; }
                if (p.Bullet) text = (p.Rtl ? "" : "• ") + text + (p.Rtl ? " •" : "");
                var ft = new FormattedText(text, p.Rtl ? CultureInfo.GetCultureInfo("fa-IR") : CultureInfo.InvariantCulture,
                    p.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    new Typeface(monospace ? MonoFamily : UiFamily, FontStyles.Normal, p.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                    p.Size, p.Size >= 16 && !slide ? new SolidColorBrush(accent) : (monospace && text.TrimStart().StartsWith("//") ? mutedInk : inkBrush), dpi)
                {
                    MaxTextWidth = maxW,
                    MaxTextHeight = Math.Max(p.Size * 1.6, pageH - margin * 0.6 - y),
                    Trimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Left
                };
                if (monospace) { ft.MaxLineCount = 1; }
                // RTL FormattedText right-aligns itself inside [origin, origin + MaxTextWidth].
                dc.DrawText(ft, new Point(margin, y));
                y += ft.Height + (monospace ? 1.5 : p.Size * 0.55);
            }
        }
        return Rasterize(visual, pageW, pageH, maxSide);
    }

    private static BitmapSource RenderSheet(string?[,] grid, int rows, int cols, string sheetName, int maxSide)
    {
        rows = Math.Min(rows, grid.GetLength(0));
        cols = Math.Min(cols, grid.GetLength(1));
        const double rowH = 26, headH = 26, numW = 38, tabH = 30;
        double colW = 104;
        double width = numW + cols * colW, height = headH + rows * rowH + tabH;
        var visual = new DrawingVisual();
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEA)), 1);
        var headBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        var headInk = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        var ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
        var tf = new Typeface(UiFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var tfBold = new Typeface(UiFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(headBrush, null, new Rect(0, 0, width, headH));
            dc.DrawRectangle(headBrush, null, new Rect(0, 0, numW, headH + rows * rowH));
            for (int c = 0; c < cols; c++)
            {
                var ft = new FormattedText(((char)('A' + c)).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, headInk, 1.25);
                dc.DrawText(ft, new Point(numW + c * colW + colW / 2 - ft.Width / 2, 6));
            }
            for (int r = 0; r < rows; r++)
            {
                double y = headH + r * rowH;
                var num = new FormattedText((r + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 10.5, headInk, 1.25);
                dc.DrawText(num, new Point(numW / 2 - num.Width / 2, y + 6));
                for (int c = 0; c < cols; c++)
                {
                    string? v = grid[r, c];
                    if (string.IsNullOrEmpty(v)) continue;
                    bool rtl = HasRtl(v);
                    bool numeric = double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
                    var ft = new FormattedText(v, rtl ? CultureInfo.GetCultureInfo("fa-IR") : CultureInfo.InvariantCulture,
                        rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, r == 0 ? tfBold : tf, 11.5, ink, 1.25)
                    {
                        MaxTextWidth = colW - 10,
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis
                    };
                    double x0 = numW + c * colW;
                    double x = numeric && !rtl ? x0 + colW - 5 - ft.Width : x0 + 5;
                    dc.DrawText(ft, new Point(x, y + 5));
                }
            }
            for (int c = 0; c <= cols; c++) dc.DrawLine(gridPen, new Point(numW + c * colW, 0), new Point(numW + c * colW, headH + rows * rowH));
            for (int r = 0; r <= rows; r++) dc.DrawLine(gridPen, new Point(0, headH + r * rowH), new Point(width, headH + r * rowH));
            dc.DrawLine(gridPen, new Point(numW, 0), new Point(numW, headH + rows * rowH));

            double tabY = headH + rows * rowH;
            dc.DrawRectangle(headBrush, null, new Rect(0, tabY, width, tabH));
            var tab = new FormattedText(sheetName, HasRtl(sheetName) ? CultureInfo.GetCultureInfo("fa-IR") : CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tfBold, 11.5, new SolidColorBrush(ExcelColor), 1.25);
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(ExcelColor), 1), new Rect(numW + 6, tabY + 3, tab.Width + 24, tabH - 8));
            dc.DrawText(tab, new Point(numW + 18, tabY + 7));
        }
        return Rasterize(visual, width, height, maxSide);
    }

    private static BitmapSource Rasterize(Visual visual, double w, double h, int maxSide)
    {
        double scale = Math.Clamp(maxSide * 1.5 / Math.Max(w, h), 0.4, 2.0);
        int pw = Math.Max(1, (int)Math.Round(w * scale));
        int ph = Math.Max(1, (int)Math.Round(h * scale));
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ------------------------------------------------------------------ zip helpers

    private static ZipArchive OpenZip(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768);
        try { return new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false); }
        catch { fs.Dispose(); throw; }
    }

    private static XmlReader CreateReader(ZipArchiveEntry entry)
    {
        if (entry.Length > 120_000_000) throw new InvalidDataException("XML part too large for preview.");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = false,
            MaxCharactersInDocument = MaxXmlChars * 40L,
            CloseInput = true
        });
    }

    private static string ReadAppProperty(ZipArchive zip, string name)
    {
        try
        {
            var entry = zip.GetEntry("docProps/app.xml");
            if (entry == null) return string.Empty;
            using var r = CreateReader(entry);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == name)
                    return r.ReadElementContentAsString().Trim();
        }
        catch { }
        return string.Empty;
    }

    private static BitmapSource? ReadEmbeddedThumbnail(ZipArchive zip)
    {
        foreach (var name in new[] { "docProps/thumbnail.jpeg", "docProps/thumbnail.jpg", "docProps/thumbnail.png" })
        {
            var entry = zip.GetEntry(name);
            if (entry == null || entry.Length > 8_000_000) continue;
            try
            {
                using var s = entry.Open();
                var bmp = DecodeBitmap(s, 900);
                if (bmp != null && Math.Max(bmp.PixelWidth, bmp.PixelHeight) >= 120) return bmp;
            }
            catch { }
        }
        return null;
    }

    private static BitmapSource? DecodeBitmap(Stream s, int maxSide)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = Math.Max(200, (int)(maxSide * 1.4));
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static (int Row, int Col) ParseCellRef(string cellRef)
    {
        int col = 0, i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i])) { col = col * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1); i++; }
        return int.TryParse(cellRef.AsSpan(i), out int row) ? (row - 1, col - 1) : (-1, -1);
    }

    private static string FormatNumber(string raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return Math.Abs(d) >= 1e15 ? raw : d.ToString(Math.Abs(d % 1) < 1e-9 ? "#,0" : "#,0.##", CultureInfo.InvariantCulture);
        return raw;
    }

    public static bool HasRtl(string text)
    {
        foreach (char c in text)
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return true;
        return false;
    }

    private static string ToFa(int n) => ToFaDigits(n.ToString(CultureInfo.InvariantCulture));

    private static string ToFaDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is >= '0' and <= '9' ? (char)('۰' + (c - '0')) : c);
        return sb.ToString();
    }
}

/// <summary>Result shape used by the "Shamsa Pro" windows that were uploaded to main (kept for compatibility).</summary>
public sealed record DocumentFirstPagePreview(
    string FilePath,
    string DocumentKind,
    BitmapSource? ThumbnailImage,
    string? ExtractedFirstPageSnippet,
    int? EstimatedPagesOrSheets,
    string SummaryLabel);
