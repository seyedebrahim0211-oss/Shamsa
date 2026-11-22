using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Shamsa.Services;

public record ConversionResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Outputs,
    IReadOnlyDictionary<string, string>? OutputByInput = null);

public record ConversionProgress(int FileIndex, int FileCount, string FileName, double Fraction)
{
    public double Overall => FileCount == 0 ? 0 : Math.Clamp((FileIndex + Fraction) / FileCount, 0, 1);
}

public readonly record struct CompressionEstimate(
    int OrigWidth,
    int OrigHeight,
    int OutWidth,
    int OutHeight,
    long OrigBytes,
    long EstimatedBytes,
    string OutputExtension);

/// <summary>
/// File conversion (FFmpeg), smart customizable compression (with size &amp; dimension estimation), and archiving.
/// Actions: mp4, mp3, wav, gif, webp, png, jpg, compress, zip.
/// </summary>
public sealed class ConverterService
{
    public static readonly string[] ConvertActions = { "compress", "mp4", "mp3", "wav", "gif", "webp", "png", "jpg" };

    public static readonly IReadOnlyDictionary<string, string> ActionLabels = new Dictionary<string, string>
    {
        ["compress"] = "کم‌حجم‌سازی هوشمند (عکس و ویدیو)",
        ["mp4"] = "MP4 (ویدیو)",
        ["mp3"] = "MP3 (استخراج صدا)",
        ["wav"] = "WAV (صدای بدون افت)",
        ["gif"] = "GIF (انیمیشن)",
        ["webp"] = "WEBP",
        ["png"] = "PNG (تصویر / فریم ویدیو)",
        ["jpg"] = "JPG (تصویر / فریم ویدیو)",
        ["zip"] = "ZIP (فشرده‌سازی)"
    };

    public bool MoveOriginalsToRecycleBin
    {
        get => SettingsService.Current.MoveOriginalsToRecycleBin;
        set => SettingsService.Update(s => s.MoveOriginalsToRecycleBin = value);
    }

    public static bool IsSupported(string file, string action)
    {
        var kind = FileKinds.FromExtension(Path.GetExtension(file));
        bool isGif = Path.GetExtension(file).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        return action.ToLowerInvariant() switch
        {
            "zip" => true,
            "mp4" => kind == FileKind.Video || isGif,
            "mp3" or "wav" => kind is FileKind.Video or FileKind.Audio,
            "gif" => kind == FileKind.Video,
            "webp" or "png" or "jpg" => kind is FileKind.Image or FileKind.Video,
            "compress" => kind is FileKind.Video or FileKind.Image or FileKind.Audio,
            _ => false
        };
    }

    public static bool IsApplicable(IEnumerable<string> items, string action)
    {
        if (action is "zip" or "shelf" or "copypath") return items.Any();
        return items.Any(i => IsSupported(i, action) || Directory.Exists(i));
    }

    public static List<string> ExpandInputs(IEnumerable<string> items, string action)
    {
        var result = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var item in items)
        {
            if (File.Exists(item))
            {
                if (IsSupported(item, action)) result.Add(item);
            }
            else if (Directory.Exists(item))
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(item, "*", options).Where(f => IsSupported(f, action)));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not enumerate {item}: {ex.Message}");
                }
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>محاسبه دقیق ابعاد خروجی با حفظ نسبت تصویر (پشتیبانی کامل از حالت عمودی و افقی بدون بزرگ‌نمایی اضافی).</summary>
    public static (int OutW, int OutH) ComputeTargetDimensions(int origW, int origH, FileKind kind, CompressionSettings opts)
    {
        if (origW <= 0 || origH <= 0) return (0, 0);

        double outW = origW;
        double outH = origH;

        switch (opts.ResolutionMode)
        {
            case 1: ScaleToMaxLongSide(ref outW, ref outH, 1920); break;
            case 2: ScaleToMaxLongSide(ref outW, ref outH, 1280); break;
            case 3: ScaleToMaxLongSide(ref outW, ref outH, 1024); break;
            case 4: ScaleToMaxLongSide(ref outW, ref outH, 720); break;
            case 5: outW *= 0.75; outH *= 0.75; break;
            case 6: outW *= 0.50; outH *= 0.50; break;
            default: break; // 0 = ابعاد اصلی
        }

        int finalW = Math.Max(2, (int)Math.Round(outW));
        int finalH = Math.Max(2, (int)Math.Round(outH));

        if (kind == FileKind.Video)
        {
            // libx264 نیازمند ابعاد زوج است
            finalW = Math.Max(2, (finalW / 2) * 2);
            finalH = Math.Max(2, (finalH / 2) * 2);
        }

        return (finalW, finalH);
    }

    private static void ScaleToMaxLongSide(ref double w, ref double h, double maxSide)
    {
        double longSide = Math.Max(w, h);
        if (longSide <= maxSide) return;
        double factor = maxSide / longSide;
        w *= factor;
        h *= factor;
    }

    /// <summary>تخمین هوشمند حجم و ابعاد خروجی قبل از شروع کم‌حجم‌سازی (برای عکس، ویدیو و صدا).</summary>
    public static CompressionEstimate EstimateCompressedOutput(string path, MediaMeta meta, CompressionSettings opts)
    {
        var kind = FileKinds.Of(path);
        var (ext, _) = GetOutputSpec(path, "compress", opts);
        long origBytes = meta.SizeBytes;
        if (origBytes <= 0)
        {
            try { origBytes = File.Exists(path) ? new FileInfo(path).Length : 0; } catch { }
        }

        var (outW, outH) = ComputeTargetDimensions(meta.Width, meta.Height, kind, opts);
        double pixelRatio = (meta.Width > 0 && meta.Height > 0 && outW > 0 && outH > 0)
            ? ((double)outW * outH) / ((double)meta.Width * meta.Height)
            : 1.0;

        long estBytes = origBytes;

        if (kind == FileKind.Image)
        {
            double q = Math.Clamp(opts.QualityPercent, 15, 95) / 100.0;
            if (outW > 0 && outH > 0)
            {
                double pixels = (double)outW * outH;
                double bpp = ext switch
                {
                    ".webp" => 0.14 + 1.35 * Math.Pow(q, 2.2),
                    ".png" => 2.2 + 2.8 * q,
                    _ => 0.22 + 1.95 * Math.Pow(q, 2.1) // jpg
                };
                long rawEst = (long)(pixels * bpp / 8.0);
                double origScaled = origBytes * Math.Pow(pixelRatio, 0.78) * (ext == ".webp" ? 0.72 : 0.92);
                estBytes = ext == ".png"
                    ? Math.Min(origBytes, Math.Max(4096, (long)(origBytes * Math.Pow(pixelRatio, 0.85) * 0.88)))
                    : Math.Max(4096, (long)Math.Min(origBytes * 0.96, (rawEst * 0.65 + origScaled * 0.35)));
            }
            else
            {
                estBytes = Math.Max(4096, (long)(origBytes * (0.20 + 0.65 * q)));
            }
        }
        else if (kind == FileKind.Video)
        {
            int crf = opts.EffectiveVideoCrf;
            int audioKbps = opts.EffectiveAudioKbps;

            if (opts.TargetVideoSizeMb > 0)
            {
                long targetCap = (long)(opts.TargetVideoSizeMb * 1024.0 * 1024.0 * 0.96);
                estBytes = origBytes > 0 ? Math.Min(origBytes, targetCap) : targetCap;
            }
            else if (meta.DurationSeconds > 0.5 && outW > 0 && outH > 0)
            {
                // برآورد بیت‌ریت libx264 بر اساس رزولوشن و CRF
                double refPixels = 1280.0 * 720.0;
                double resFactor = Math.Pow(((double)outW * outH) / refPixels, 0.78);
                double crfFactor = Math.Pow(2.0, (27.0 - crf) / 6.0);
                double videoKbps = Math.Clamp(1200.0 * resFactor * crfFactor, 160.0, 14000.0);
                long byBitrate = (long)(((videoKbps + audioKbps) * 1000.0 / 8.0) * meta.DurationSeconds);

                // اگر ویدیوی اصلی خودش کم‌تحرک یا کم‌بیت‌ریت است، با حجم اصلی متعادل کن
                double ratioFallback = Math.Clamp(Math.Pow(pixelRatio, 0.72) * Math.Pow(2.0, (24.0 - crf) / 8.0), 0.12, 0.92);
                long byRatio = (long)(origBytes * ratioFallback);
                estBytes = origBytes > 0
                    ? Math.Max(32_768, Math.Min((long)(origBytes * 0.95), (long)(byBitrate * 0.6 + byRatio * 0.4)))
                    : Math.Max(32_768, byBitrate);
            }
            else
            {
                double ratio = Math.Clamp(Math.Pow(pixelRatio, 0.75) * (0.18 + 0.65 * (opts.QualityPercent / 100.0)), 0.15, 0.90);
                estBytes = Math.Max(32_768, (long)(origBytes * ratio));
            }
        }
        else if (kind == FileKind.Audio)
        {
            if (meta.DurationSeconds > 0.5)
                estBytes = Math.Max(8192, (long)((opts.EffectiveAudioKbps * 1000.0 / 8.0) * meta.DurationSeconds));
            else
                estBytes = Math.Max(8192, (long)(origBytes * 0.55));
        }

        return new CompressionEstimate(meta.Width, meta.Height, outW, outH, origBytes, estBytes, ext);
    }

    public static (string Extension, string Suffix) GetOutputSpec(string input, string action, CompressionSettings? opts = null)
    {
        string inExt = Path.GetExtension(input).ToLowerInvariant();
        var kind = FileKinds.FromExtension(inExt);
        opts ??= SettingsService.Current.Compression;

        if (action == "compress")
        {
            if (kind == FileKind.Video) return (".mp4", "_small");
            if (kind == FileKind.Audio) return (".mp3", "_small");

            string imgExt = opts.ImageFormatMode switch
            {
                1 => ".webp",
                2 => ".jpg",
                3 => inExt is ".png" or ".jpg" or ".jpeg" or ".webp" ? (inExt == ".jpeg" ? ".jpg" : inExt) : ".jpg",
                _ => inExt is ".png" or ".webp" or ".gif" ? ".webp" : ".jpg"
            };
            return (imgExt, "_small");
        }

        return ("." + action, "_converted");
    }

    public static string GetOutputPath(string input, string action, CompressionSettings? opts = null)
    {
        var (ext, suffix) = GetOutputSpec(input, action, opts);
        string dir = Path.GetDirectoryName(input)!;
        string name = Path.GetFileNameWithoutExtension(input);
        return GetUniquePath(Path.Combine(dir, name + suffix + ext));
    }

    public static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static List<string> BuildArguments(string action, string input, string output, CompressionSettings? opts = null, MediaMeta? meta = null)
    {
        opts ??= SettingsService.Current.Compression;
        var kind = FileKinds.FromExtension(Path.GetExtension(input));
        bool videoInput = kind == FileKind.Video;

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };

        switch (action)
        {
            case "mp4":
                args.AddRange(new[]
                {
                    "-c:v", "libx264", "-preset", "fast", "-crf", "23", "-pix_fmt", "yuv420p",
                    "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                    "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"
                });
                break;

            case "mp3":
                args.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" });
                break;

            case "wav":
                args.AddRange(new[] { "-vn", "-c:a", "pcm_s16le" });
                break;

            case "gif":
                args.AddRange(new[]
                {
                    "-vf", "fps=12,scale='min(480,iw)':-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5",
                    "-loop", "0"
                });
                break;

            case "webp":
                if (videoInput)
                    args.AddRange(new[] { "-vf", "fps=12,scale='min(480,iw)':-1", "-c:v", "libwebp", "-loop", "0", "-quality", "75", "-an" });
                else
                    args.AddRange(new[] { "-c:v", "libwebp", "-quality", "85", "-frames:v", "1" });
                break;

            case "png":
                if (videoInput) args.AddRange(new[] { "-vf", "thumbnail", "-frames:v", "1" });
                else args.AddRange(new[] { "-frames:v", "1" });
                break;

            case "jpg":
                if (videoInput) args.AddRange(new[] { "-vf", "thumbnail", "-q:v", "2", "-frames:v", "1" });
                else args.AddRange(new[] { "-q:v", "2", "-frames:v", "1" });
                break;

            case "compress":
                BuildCompressArguments(args, kind, input, output, opts, meta ?? MediaProbeService.GetMediaMeta(input));
                break;

            default:
                throw new ArgumentException($"Unknown action: {action}", nameof(action));
        }

        args.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
        return args;
    }

    private static void BuildCompressArguments(
        List<string> args, FileKind kind, string input, string output, CompressionSettings opts, MediaMeta meta)
    {
        if (kind == FileKind.Video)
        {
            string scaleExpr = BuildScaleFilter(opts.ResolutionMode, isVideo: true);
            int audioKbps = opts.EffectiveAudioKbps;

            args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p" });

            if (opts.TargetVideoSizeMb > 0 && meta.DurationSeconds > 1.0)
            {
                long targetBits = (long)(opts.TargetVideoSizeMb * 1024.0 * 1024.0 * 8.0 * 0.93);
                long totalKbps = (long)(targetBits / meta.DurationSeconds / 1000.0);
                int adjAudio = totalKbps < 320 ? 64 : audioKbps;
                long videoKbps = Math.Clamp(totalKbps - adjAudio, 120, 12000);
                audioKbps = adjAudio;
                args.AddRange(new[]
                {
                    "-b:v", $"{videoKbps}k",
                    "-maxrate", $"{(long)(videoKbps * 1.35)}k",
                    "-bufsize", $"{videoKbps * 2}k"
                });
            }
            else
            {
                args.AddRange(new[] { "-crf", opts.EffectiveVideoCrf.ToString(CultureInfo.InvariantCulture) });
            }

            args.AddRange(new[]
            {
                "-vf", scaleExpr,
                "-c:a", "aac", "-b:a", $"{audioKbps}k",
                "-movflags", "+faststart"
            });
            return;
        }

        if (kind == FileKind.Audio)
        {
            args.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-b:a", $"{opts.EffectiveAudioKbps}k" });
            return;
        }

        // Image compression
        string? imgScale = BuildScaleFilter(opts.ResolutionMode, isVideo: false);
        if (!string.IsNullOrEmpty(imgScale))
            args.AddRange(new[] { "-vf", imgScale });

        string outExt = Path.GetExtension(output).ToLowerInvariant();
        if (outExt == ".webp")
        {
            args.AddRange(new[] { "-c:v", "libwebp", "-quality", opts.QualityPercent.ToString(CultureInfo.InvariantCulture), "-frames:v", "1" });
        }
        else if (outExt == ".png")
        {
            args.AddRange(new[] { "-compression_level", "9", "-frames:v", "1" });
        }
        else
        {
            args.AddRange(new[] { "-q:v", opts.EffectiveJpgQ.ToString(CultureInfo.InvariantCulture), "-frames:v", "1" });
        }
    }

    private static string BuildScaleFilter(int resolutionMode, bool isVideo)
    {
        if (resolutionMode == 0)
            return isVideo ? "scale=trunc(iw/2)*2:trunc(ih/2)*2" : string.Empty;

        if (resolutionMode == 5) // 75%
            return isVideo ? "scale=trunc(iw*0.75/2)*2:trunc(ih*0.75/2)*2" : "scale=trunc(iw*0.75):trunc(ih*0.75)";

        if (resolutionMode == 6) // 50%
            return isVideo ? "scale=trunc(iw*0.5/2)*2:trunc(ih*0.5/2)*2" : "scale=trunc(iw*0.5):trunc(ih*0.5)";

        int maxSide = resolutionMode switch
        {
            1 => 1920,
            2 => 1280,
            3 => 1024,
            4 => 720,
            _ => 1280
        };

        // مقیاس‌دهی هوشمند روی ضلع بزرگ‌تر (هم برای عمودی و هم برای افقی، بدون بزرگ‌نمایی فایل‌های کوچک)
        return isVideo
            ? $"scale='if(gte(iw,ih),trunc(min({maxSide},iw)/2)*2,-2)':'if(gte(iw,ih),-2,trunc(min({maxSide},ih)/2)*2)'"
            : $"scale='if(gte(iw,ih),min({maxSide},iw),-1)':'if(gte(iw,ih),-1,min({maxSide},ih))'";
    }

    public async Task<ConversionResult> ConvertFilesAsync(
        IEnumerable<string> items,
        string action,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default,
        CompressionSettings? compressionOverride = null)
    {
        action = action.ToLowerInvariant();
        var compOpts = compressionOverride ?? SettingsService.Current.Compression;
        var inputList = items.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (inputList.Count == 0)
            return new ConversionResult(false, "فایلی انتخاب نشده است.", Array.Empty<string>());

        try
        {
            if (action == "zip")
                return await CreateZipAsync(inputList, ct);

            var files = ExpandInputs(inputList, action);
            if (files.Count == 0)
                return new ConversionResult(false, $"هیچ‌کدام از فایل‌ها برای «{ActionLabel(action)}» مناسب نیستند.", Array.Empty<string>());

            string? ffmpegPath = FfmpegManager.GetFfmpegPath();
            if (ffmpegPath == null)
                return new ConversionResult(false, "FFmpeg یافت نشد! از داشبورد ← تنظیمات، دکمه «نصب خودکار FFmpeg» را بزنید.", Array.Empty<string>());

            var outputs = new List<string>();
            var outputMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            long origTotal = 0;
            long outTotal = 0;

            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string file = files[i];
                var meta = MediaProbeService.GetMediaMeta(file);
                string output = GetOutputPath(file, action, compOpts);
                int index = i;
                progress?.Report(new ConversionProgress(i, files.Count, Path.GetFileName(file), 0));

                bool ok;
                string error;
                try
                {
                    (ok, error) = await RunFfmpegAsync(ffmpegPath, BuildArguments(action, file, output, compOpts, meta),
                        f => progress?.Report(new ConversionProgress(index, files.Count, Path.GetFileName(file), f)), ct);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(output);
                    throw;
                }

                if (ok && File.Exists(output) && new FileInfo(output).Length > 0)
                {
                    outputs.Add(output);
                    outputMap[file] = output;
                    try
                    {
                        origTotal += new FileInfo(file).Length;
                        outTotal += new FileInfo(output).Length;
                    }
                    catch { }

                    if (MoveOriginalsToRecycleBin)
                    {
                        try
                        {
                            FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"Could not recycle {file}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    TryDelete(output);
                    errors.Add($"{Path.GetFileName(file)}: {error}");
                    AppLog.Warn($"FFmpeg failed for {file} ({action}): {error}");
                }

                progress?.Report(new ConversionProgress(i, files.Count, Path.GetFileName(file), 1));
            }

            if (outputs.Count == 0)
                return new ConversionResult(false, "تبدیل ناموفق بود. " + (errors.FirstOrDefault() ?? string.Empty), outputs, outputMap);

            string msg = $"{outputs.Count} از {files.Count} فایل با موفقیت پردازش شد ({ActionLabel(action)}).";
            if (origTotal > 0 && outTotal > 0)
            {
                long saved = origTotal - outTotal;
                if (saved > 0)
                {
                    int pct = (int)Math.Round(saved * 100.0 / origTotal);
                    msg += $" حجم: {FileKinds.FormatSize(origTotal)} ← {FileKinds.FormatSize(outTotal)} ({pct}٪ کاهش)";
                }
                else
                {
                    msg += $" حجم خروجی: {FileKinds.FormatSize(outTotal)}";
                }
            }
            if (errors.Count > 0)
                msg += $" | {errors.Count} خطا";

            return new ConversionResult(true, msg, outputs, outputMap);
        }
        catch (OperationCanceledException)
        {
            return new ConversionResult(false, "عملیات لغو شد.", Array.Empty<string>());
        }
        catch (Exception ex)
        {
            AppLog.Error($"Conversion {action} failed", ex);
            return new ConversionResult(false, $"خطا: {ex.Message}", Array.Empty<string>());
        }
    }

    public static string ActionLabel(string action) =>
        ActionLabels.TryGetValue(action, out var label) ? label : action.ToUpperInvariant();

    private static readonly Regex DurationRegex = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static async Task<(bool Ok, string Error)> RunFfmpegAsync(
        string ffmpegPath, List<string> arguments, Action<double>? onFraction, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrTail = new Queue<string>();
        double durationSeconds = 0;

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderrTail)
            {
                if (durationSeconds <= 0)
                {
                    var m = DurationRegex.Match(e.Data);
                    if (m.Success)
                    {
                        durationSeconds = int.Parse(m.Groups[1].Value) * 3600
                                          + int.Parse(m.Groups[2].Value) * 60
                                          + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    }
                }
                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > 12) stderrTail.Dequeue();
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || onFraction == null) return;
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) || e.Data.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                string value = e.Data[(e.Data.IndexOf('=') + 1)..];
                if (long.TryParse(value, out long micros) && durationSeconds > 0)
                    onFraction(Math.Clamp(micros / 1_000_000.0 / durationSeconds, 0, 1));
            }
        };

        try
        {
            if (!process.Start()) return (false, "اجرای FFmpeg ممکن نشد");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch { }
            throw;
        }

        if (process.ExitCode == 0) return (true, string.Empty);

        string tail;
        lock (stderrTail) tail = stderrTail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? $"exit code {process.ExitCode}";
        return (false, tail);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static Task<ConversionResult> CreateZipAsync(List<string> items, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var existing = items.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (existing.Count == 0)
                return new ConversionResult(false, "هیچ فایل معتبری برای فشرده‌سازی وجود ندارد.", Array.Empty<string>());

            string first = existing[0].TrimEnd('\\', '/');
            string targetDir = Path.GetDirectoryName(first) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string baseName = existing.Count == 1
                ? Path.GetFileNameWithoutExtension(first)
                : $"Shamsa_Archive_{DateTime.Now:yyyyMMdd_HHmmss}";
            string zipPath = GetUniquePath(Path.Combine(targetDir, baseName + ".zip"));

            int added = 0, skipped = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var item in existing)
                {
                    ct.ThrowIfCancellationRequested();
                    if (File.Exists(item))
                    {
                        if (TryAddEntry(archive, item, UniqueEntryName(usedNames, Path.GetFileName(item)))) added++;
                        else skipped++;
                    }
                    else
                    {
                        string root = item.TrimEnd('\\', '/');
                        string folderName = UniqueEntryName(usedNames, Path.GetFileName(root));
                        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                        foreach (var file in Directory.EnumerateFiles(root, "*", options))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (file.Equals(zipPath, StringComparison.OrdinalIgnoreCase)) continue;
                            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                            if (TryAddEntry(archive, file, folderName + "/" + rel)) added++;
                            else skipped++;
                        }
                    }
                }
            }
            catch
            {
                TryDelete(zipPath);
                throw;
            }

            if (added == 0)
            {
                TryDelete(zipPath);
                return new ConversionResult(false, "هیچ فایلی قابل خواندن نبود.", Array.Empty<string>());
            }

            string msg = $"آرشیو ساخته شد: {Path.GetFileName(zipPath)} ({added} فایل، {FileKinds.FormatSize(new FileInfo(zipPath).Length)})";
            if (skipped > 0) msg += $" | {skipped} فایل قفل/غیرقابل‌خواندن رد شد";
            return new ConversionResult(true, msg, new[] { zipPath });
        }, ct);
    }

    private static string UniqueEntryName(HashSet<string> used, string name)
    {
        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
        return candidate;
    }

    private static bool TryAddEntry(ZipArchive archive, string file, string entryName)
    {
        try
        {
            var kind = FileKinds.FromExtension(Path.GetExtension(file));
            var level = kind is FileKind.Video or FileKind.Audio or FileKind.Archive || FileKinds.IsImage(file)
                ? CompressionLevel.Fastest
                : CompressionLevel.Optimal;
            archive.CreateEntryFromFile(file, entryName, level);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"ZIP skipped {file}: {ex.Message}");
            return false;
        }
    }
}
