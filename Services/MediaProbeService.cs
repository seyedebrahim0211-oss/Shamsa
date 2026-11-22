using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace Shamsa.Services;

public readonly record struct MediaMeta(int Width, int Height, double DurationSeconds, long SizeBytes);

/// <summary>
/// Ultra-fast (&lt; 1-3 ms) metadata probe for both videos (respecting 90°/270° smartphone rotation &amp; duration)
/// and images (respecting EXIF orientation). Used by Hover Preview and Smart Compression size/dimension estimator.
/// </summary>
public static class MediaProbeService
{
    private static readonly ConcurrentDictionary<string, (DateTime WriteUtc, MediaMeta Meta)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static (int Width, int Height) GetVideoDisplaySize(string path)
    {
        var meta = GetMediaMeta(path);
        return (meta.Width, meta.Height);
    }

    public static MediaMeta GetMediaMeta(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return default;

            var fi = new FileInfo(path);
            var writeUtc = fi.LastWriteTimeUtc;
            long sizeBytes = fi.Length;

            if (Cache.TryGetValue(path, out var hit) && hit.WriteUtc == writeUtc && hit.Meta.SizeBytes == sizeBytes)
                return hit.Meta;

            var kind = FileKinds.Of(path);
            int w = 0, h = 0;
            double dur = 0;

            if (kind == FileKind.Image)
            {
                (w, h) = ProbeImageSize(path);
            }
            else if (kind == FileKind.Video || kind == FileKind.Audio)
            {
                if (kind == FileKind.Video)
                    (w, h, dur) = ProbeVideoBinaryHeader(path);

                if (w <= 0 || h <= 0 || dur <= 0)
                {
                    var (sw, sh, sdur) = ProbeWindowsShell(path);
                    if (w <= 0 || h <= 0) (w, h) = (sw, sh);
                    if (dur <= 0) dur = sdur;
                }
            }

            var meta = new MediaMeta(w, h, dur, sizeBytes);
            if (Cache.Count > 512) Cache.Clear();
            Cache[path] = (writeUtc, meta);
            return meta;
        }
        catch
        {
            return default;
        }
    }

    // ------------------------------------------------------------------ Images (with EXIF rotation)

    private static (int Width, int Height) ProbeImageSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;

            if (frame.Metadata is BitmapMetadata meta)
            {
                string[] queries = { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/xmp/tiff:Orientation" };
                foreach (var q in queries)
                {
                    try
                    {
                        if (!meta.ContainsQuery(q)) continue;
                        object? val = meta.GetQuery(q);
                        int orient = val switch
                        {
                            ushort u => u,
                            int i => i,
                            string s when int.TryParse(s, out int p) => p,
                            _ => 0
                        };
                        if (orient is 5 or 6 or 7 or 8)
                            (w, h) = (h, w);
                        if (orient > 0) break;
                    }
                    catch { }
                }
            }
            return (w, h);
        }
        catch
        {
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------ Videos (MP4/MOV, MKV/WebM, AVI)

    private static (int Width, int Height, double DurationSec) ProbeVideoBinaryHeader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            if (fs.Length < 16) return (0, 0, 0);

            if (ext is ".mp4" or ".mov" or ".m4v" or ".3gp")
                return ParseIsoBmff(fs);
            if (ext is ".mkv" or ".webm")
            {
                var (w, h) = ParseEbml(fs);
                return (w, h, 0);
            }
            if (ext == ".avi")
                return ParseAvi(fs);
        }
        catch { }
        return (0, 0, 0);
    }

    private static (int Width, int Height, double DurationSec) ParseIsoBmff(FileStream fs)
    {
        long fileLen = fs.Length;
        long pos = 0;
        Span<byte> hdr = stackalloc byte[16];
        int safety = 0;

        while (pos + 8 <= fileLen && ++safety < 128)
        {
            fs.Position = pos;
            if (fs.Read(hdr[..8]) < 8) break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(4, 4));
            long boxSize = size32;
            long payloadOffset = 8;

            if (size32 == 1)
            {
                if (pos + 16 > fileLen || fs.Read(hdr.Slice(8, 8)) < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
                payloadOffset = 16;
            }
            else if (size32 == 0)
            {
                boxSize = fileLen - pos;
            }

            if (boxSize < payloadOffset || pos + boxSize > fileLen) break;

            if (type == 0x6D6F6F76) // "moov"
                return ParseMoov(fs, pos + payloadOffset, pos + boxSize);

            pos += boxSize;
        }

        return (0, 0, 0);
    }

    private static (int Width, int Height, double DurationSec) ParseMoov(FileStream fs, long start, long end)
    {
        long pos = start;
        Span<byte> hdr = stackalloc byte[16];
        int safety = 0;
        int foundW = 0, foundH = 0;
        double durationSec = 0;

        while (pos + 8 <= end && ++safety < 256)
        {
            fs.Position = pos;
            if (fs.Read(hdr[..8]) < 8) break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(4, 4));
            long boxSize = size32 == 0 ? (end - pos) : size32;
            long payloadOffset = 8;

            if (size32 == 1)
            {
                if (pos + 16 > end || fs.Read(hdr.Slice(8, 8)) < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
                payloadOffset = 16;
            }

            if (boxSize < payloadOffset || pos + boxSize > end) break;

            if (type == 0x6D766864 && durationSec <= 0) // "mvhd" -> movie duration
            {
                long payloadLen = boxSize - payloadOffset;
                if (payloadLen >= 20)
                {
                    Span<byte> mvhd = stackalloc byte[32];
                    int toRead = (int)Math.Min(32, payloadLen);
                    fs.Position = pos + payloadOffset;
                    if (fs.Read(mvhd[..toRead]) >= 20)
                    {
                        byte ver = mvhd[0];
                        if (ver == 0 && toRead >= 20)
                        {
                            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.Slice(12, 4));
                            uint dur = BinaryPrimitives.ReadUInt32BigEndian(mvhd.Slice(16, 4));
                            if (timescale > 0) durationSec = (double)dur / timescale;
                        }
                        else if (ver == 1 && toRead >= 32)
                        {
                            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.Slice(20, 4));
                            ulong dur = BinaryPrimitives.ReadUInt64BigEndian(mvhd.Slice(24, 8));
                            if (timescale > 0) durationSec = (double)dur / timescale;
                        }
                    }
                }
            }
            else if (type == 0x7472616B && (foundW == 0 || foundH == 0)) // "trak"
            {
                var (w, h) = ParseTrak(fs, pos + payloadOffset, pos + boxSize);
                if (w > 0 && h > 0) (foundW, foundH) = (w, h);
            }

            if (foundW > 0 && foundH > 0 && durationSec > 0) break;
            pos += boxSize;
        }

        return (foundW, foundH, durationSec);
    }

    private static (int Width, int Height) ParseTrak(FileStream fs, long start, long end)
    {
        long pos = start;
        Span<byte> hdr = stackalloc byte[16];
        int safety = 0;

        while (pos + 8 <= end && ++safety < 64)
        {
            fs.Position = pos;
            if (fs.Read(hdr[..8]) < 8) break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(4, 4));
            long boxSize = size32 == 0 ? (end - pos) : size32;
            long payloadOffset = 8;

            if (size32 == 1)
            {
                if (pos + 16 > end || fs.Read(hdr.Slice(8, 8)) < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
                payloadOffset = 16;
            }

            if (boxSize < payloadOffset || pos + boxSize > end) break;

            if (type == 0x746B6864) // "tkhd"
            {
                long payloadLen = boxSize - payloadOffset;
                if (payloadLen >= 84)
                {
                    Span<byte> buf = stackalloc byte[96];
                    int toRead = (int)Math.Min(96, payloadLen);
                    fs.Position = pos + payloadOffset;
                    if (fs.Read(buf[..toRead]) >= 84)
                    {
                        byte version = buf[0];
                        int mOff = version == 1 ? 52 : 40;
                        int wOff = version == 1 ? 88 : 76;
                        if (toRead >= wOff + 8)
                        {
                            int w = (int)(BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(wOff, 4)) >> 16);
                            int h = (int)(BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(wOff + 4, 4)) >> 16);
                            if (w > 0 && h > 0)
                            {
                                int a = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(mOff, 4));
                                int b = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(mOff + 4, 4));
                                int c = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(mOff + 12, 4));
                                int d = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(mOff + 16, 4));

                                if (Math.Abs((long)b) > Math.Abs((long)a) && Math.Abs((long)c) > Math.Abs((long)d))
                                    (w, h) = (h, w);

                                return (w, h);
                            }
                        }
                    }
                }
            }

            pos += boxSize;
        }

        return (0, 0);
    }

    private static (int Width, int Height, double DurationSec) ParseAvi(FileStream fs)
    {
        int toRead = (int)Math.Min(4096, fs.Length);
        byte[] buf = new byte[toRead];
        int read = fs.Read(buf, 0, toRead);
        if (read < 64) return (0, 0, 0);

        for (int i = 12; i < read - 48; i++)
        {
            if (buf[i] == 'a' && buf[i + 1] == 'v' && buf[i + 2] == 'i' && buf[i + 3] == 'h')
            {
                uint usPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 8, 4));
                uint totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 8 + 16, 4));
                int w = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i + 8 + 32, 4));
                int h = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i + 8 + 36, 4));
                double dur = usPerFrame > 0 && totalFrames > 0 ? (usPerFrame * (double)totalFrames) / 1_000_000.0 : 0;
                if (w is > 0 and < 16384 && h is > 0 and < 16384)
                    return (w, h, dur);
            }
        }
        return (0, 0, 0);
    }

    private static (int Width, int Height) ParseEbml(FileStream fs)
    {
        int toRead = (int)Math.Min(65536, fs.Length);
        byte[] buf = new byte[toRead];
        int len = fs.Read(buf, 0, toRead);
        if (len < 32 || buf[0] != 0x1A || buf[1] != 0x45 || buf[2] != 0xDF || buf[3] != 0xA3)
            return (0, 0);

        int tracksIdx = -1;
        for (int i = 4; i < len - 12; i++)
        {
            if (buf[i] == 0x16 && buf[i + 1] == 0x54 && buf[i + 2] == 0xAE && buf[i + 3] == 0x6B)
            {
                tracksIdx = i + 4;
                break;
            }
        }
        if (tracksIdx < 0) return (0, 0);

        if (!ReadEbmlSize(buf, ref tracksIdx, len, out long tracksLen)) return (0, 0);
        int tracksEnd = (int)Math.Min(len, tracksIdx + tracksLen);

        int pw = 0, ph = 0, dw = 0, dh = 0;
        int pos = tracksIdx;
        int guard = 0;
        while (pos < tracksEnd - 3 && ++guard < 512)
        {
            if (!ReadEbmlId(buf, ref pos, tracksEnd, out uint id)) break;
            if (!ReadEbmlSize(buf, ref pos, tracksEnd, out long size) || size < 0) break;

            if (id == 0xAE || id == 0xE0) continue;

            int elemEnd = (int)Math.Min(tracksEnd, pos + size);
            if (size is >= 1 and <= 4 && pos + size <= tracksEnd)
            {
                int val = 0;
                for (int k = 0; k < size; k++) val = (val << 8) | buf[pos + k];
                if (id == 0xB0) pw = val;
                else if (id == 0xBA) ph = val;
                else if (id == 0x54B0) dw = val;
                else if (id == 0x54BA) dh = val;
            }
            pos = elemEnd;

            if (pw > 0 && ph > 0 && dw > 0 && dh > 0) break;
        }

        if (dw > 0 && dh > 0) return (dw, dh);
        if (pw > 0 && ph > 0) return (pw, ph);
        return (0, 0);
    }

    private static bool ReadEbmlId(byte[] buf, ref int pos, int end, out uint id)
    {
        id = 0;
        if (pos >= end) return false;
        byte b = buf[pos];
        if (b == 0) return false;
        int width = 1;
        byte mask = 0x80;
        while ((b & mask) == 0 && width < 4) { mask >>= 1; width++; }
        if (pos + width > end) return false;
        for (int i = 0; i < width; i++) id = (id << 8) | buf[pos + i];
        pos += width;
        return true;
    }

    private static bool ReadEbmlSize(byte[] buf, ref int pos, int end, out long size)
    {
        size = 0;
        if (pos >= end) return false;
        byte b = buf[pos];
        if (b == 0) return false;
        int width = 1;
        byte mask = 0x80;
        while ((b & mask) == 0 && width < 8) { mask >>= 1; width++; }
        if (pos + width > end) return false;
        long val = b & ~mask;
        for (int i = 1; i < width; i++) val = (val << 8) | buf[pos + i];
        pos += width;
        size = val;
        return true;
    }

    // ------------------------------------------------------------------ Windows Shell Property Store

    private static readonly Guid FmtIdVideo = new("64440491-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid FmtIdMedia = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public int intVal;
        [FieldOffset(8)] public ulong ulongVal;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, out PropVariant pv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, uint flags, ref Guid riid, out IPropertyStore? ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private static (int Width, int Height, double DurationSec) ProbeWindowsShell(string path)
    {
        IPropertyStore? store = null;
        try
        {
            Guid iid = IidPropertyStore;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store) != 0 || store == null)
                return (0, 0, 0);

            int w = (int)ReadULongProp(store, new PropertyKey { fmtid = FmtIdVideo, pid = 3 });
            int h = (int)ReadULongProp(store, new PropertyKey { fmtid = FmtIdVideo, pid = 4 });
            int orient = (int)ReadULongProp(store, new PropertyKey { fmtid = FmtIdVideo, pid = 42 });
            ulong dur100ns = ReadULongProp(store, new PropertyKey { fmtid = FmtIdMedia, pid = 3 }); // System.Media.Duration

            if (w > 0 && h > 0 && (orient == 90 || orient == 270))
                (w, h) = (h, w);

            double durSec = dur100ns > 0 ? dur100ns / 10_000_000.0 : 0;
            return (w, h, durSec);
        }
        catch
        {
            return (0, 0, 0);
        }
        finally
        {
            if (store != null && Marshal.IsComObject(store))
                Marshal.ReleaseComObject(store);
        }
    }

    private static ulong ReadULongProp(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out PropVariant pv) != 0) return 0;
        try
        {
            return pv.vt switch
            {
                19 => pv.uintVal,            // VT_UI4
                3 => (ulong)Math.Max(0, pv.intVal), // VT_I4
                21 => pv.ulongVal,           // VT_UI8
                _ => 0
            };
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }
}
