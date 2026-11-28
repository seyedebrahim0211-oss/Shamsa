using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shamsa.Desktop.Personalize;

/// <summary>8-bit RGB Photoshop document. The two optional overlay layers start hidden.</summary>
internal static class WallpaperPsd
{
    private static void Short(Stream s, int n)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, unchecked((ushort)n)); s.Write(b);
    }

    private static void Word(Stream s, int n)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, n); s.Write(b);
    }

    private static int ReadShort(Stream s)
    {
        Span<byte> b = stackalloc byte[2]; s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private static int ReadWord(Stream s)
    {
        Span<byte> b = stackalloc byte[4]; s.ReadExactly(b);
        return BinaryPrimitives.ReadInt32BigEndian(b);
    }

    private static void Size(int w, int h)
    {
        if (w < 1 || h < 1 || w > 30000 || h > 30000 || (long)w * h > 32_000_000)
            throw new InvalidDataException("تصویر باید حداکثر ۳۲ مگاپیکسل باشد.");
    }

    // Patch section lengths after streaming their contents to avoid keeping a second PSD in memory.
    private static void Section(Stream s, Action body, bool even = false)
    {
        long at = s.Position;
        Word(s, 0); body();
        if (even && ((s.Position - at) & 1) != 0) s.WriteByte(0);
        long end = s.Position;
        s.Position = at; Word(s, checked((int)(end - at - 4))); s.Position = end;
    }

    public static void Export(string source, string destination, Color accent)
    {
        using var input = File.OpenRead(source);
        var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        int w = frame.PixelWidth, h = frame.PixelHeight;
        Size(w, h);
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        byte[] pixels = new byte[w * h * 3], row = new byte[w];
        rgb.CopyPixels(pixels, w * 3, 0);

        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write("8BPS"u8); Short(output, 1); output.Write(new byte[6]);
                Short(output, 3); Word(output, h); Word(output, w); Short(output, 8); Short(output, 3);
                Word(output, 0); Word(output, 0);
                string[] names = ["Edge shade", "Theme tint", "Artwork"];
                Section(output, () =>
                {
                    Section(output, () =>
                    {
                        Short(output, names.Length);
                        for (int layer = 0; layer < names.Length; layer++)
                        {
                            Word(output, 0); Word(output, 0); Word(output, h); Word(output, w);
                            int channels = layer == 0 ? 4 : 3;
                            Short(output, channels);
                            for (int ch = 0; ch < channels; ch++)
                            {
                                Short(output, ch == 3 ? -1 : ch); Word(output, w * h + 2);
                            }
                            output.Write("8BIMnorm"u8);
                            output.WriteByte(layer == 1 ? (byte)45 : (byte)255);
                            output.WriteByte(0); output.WriteByte(layer == 2 ? (byte)0 : (byte)2); output.WriteByte(0);
                            Section(output, () =>
                            {
                                Word(output, 0); Word(output, 0);
                                byte[] name = Encoding.ASCII.GetBytes(names[layer]);
                                output.WriteByte((byte)name.Length); output.Write(name);
                                output.Write(new byte[(4 - (name.Length + 1) % 4) % 4]);
                            });
                        }
                        for (int layer = 0; layer < 3; layer++)
                            for (int ch = 0; ch < (layer == 0 ? 4 : 3); ch++)
                            {
                                Short(output, 0);
                                Plane(output, layer, ch);
                            }
                    }, even: true);
                    Word(output, 0);
                });
                Short(output, 0);
                for (int ch = 0; ch < 3; ch++) Plane(output, 2, ch);
                output.Flush(true);
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        void Plane(Stream stream, int layer, int channel)
        {
            Array.Fill(row, layer == 1 ? new[] { accent.R, accent.G, accent.B }[channel] : (byte)0);
            for (int y = 0; y < h; y++)
            {
                if (layer == 2)
                    for (int x = 0; x < w; x++) row[x] = pixels[(y * w + x) * 3 + channel];
                if (layer == 0 && channel == 3)
                    for (int x = 0; x < w; x++)
                    {
                        double dx = (x + 0.5) * 2 / w - 1, dy = (y + 0.5) * 2 / h - 1;
                        row[x] = (byte)Math.Clamp((dx * dx + dy * dy - 0.22) * 94, 0, 170);
                    }
                stream.Write(row);
            }
        }
    }

    public static BitmapSource ReadComposite(Stream s)
    {
        Span<byte> signature = stackalloc byte[4]; s.ReadExactly(signature);
        if (!signature.SequenceEqual("8BPS"u8) || ReadShort(s) != 1)
            throw new InvalidDataException("فایل PSD معمولی را انتخاب کنید.");
        s.Seek(6, SeekOrigin.Current);
        int channels = ReadShort(s), h = ReadWord(s), w = ReadWord(s);
        Size(w, h);
        if (channels is < 3 or > 56 || ReadShort(s) != 8 || ReadShort(s) != 3)
            throw new InvalidDataException("فایل را در حالت RGB و ۸ بیت ذخیره کنید یا از فتوشاپ PNG خروجی بگیرید.");
        for (int i = 0; i < 3; i++)
        {
            int length = ReadWord(s);
            if (length < 0 || length > s.Length - s.Position) throw new InvalidDataException("ساختار PSD ناقص است.");
            s.Seek(length, SeekOrigin.Current);
        }
        int codec = ReadShort(s);
        if (codec is not (0 or 1)) throw new InvalidDataException("برای این فشرده‌سازی، یک نسخهٔ PNG از فتوشاپ وارد کنید.");
        int[] lengths = codec == 1 ? new int[channels * h] : [];
        for (int i = 0; i < lengths.Length; i++) lengths[i] = ReadShort(s);
        byte[] rgb = new byte[w * h * 3], row = new byte[w];
        byte[] packed = lengths.Length > 0 ? new byte[lengths.Max()] : [];
        for (int ch = 0; ch < 3; ch++)
            for (int y = 0; y < h; y++)
            {
                if (codec == 0) s.ReadExactly(row);
                else
                {
                    var data = packed.AsSpan(0, lengths[ch * h + y]);
                    s.ReadExactly(data); DecodeRow(data, row);
                }
                for (int x = 0; x < w; x++) rgb[(y * w + x) * 3 + ch] = row[x];
            }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, rgb, w * 3);
        result.Freeze(); return result;
    }

    internal static void DecodeRow(ReadOnlySpan<byte> data, Span<byte> row)
    {
        int source = 0, dest = 0;
        while (source < data.Length)
        {
            int op = unchecked((sbyte)data[source++]);
            if (op == -128) continue;
            int count = op >= 0 ? op + 1 : 1 - op;
            int consumed = op >= 0 ? count : 1;
            if (count > row.Length - dest || consumed > data.Length - source) throw new InvalidDataException("دادهٔ تصویر PSD ناقص است.");
            if (op >= 0) data.Slice(source, count).CopyTo(row.Slice(dest));
            else row.Slice(dest, count).Fill(data[source]);
            dest += count; source += consumed;
        }
        if (dest != row.Length) throw new InvalidDataException("ردیف تصویر PSD ناقص است.");
    }

    public static string Import(string path)
    {
        using var input = File.OpenRead(path);
        BitmapSource frame = Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase)
            ? ReadComposite(input) : BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnDemand);
        Size(frame.PixelWidth, frame.PixelHeight);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        Directory.CreateDirectory(PersonalizeStore.WallpapersDir);
        string output = Path.Combine(PersonalizeStore.WallpapersDir, "edit-" + Guid.NewGuid().ToString("N") + ".png");
        try { using var f = File.Create(output); encoder.Save(f); return output; }
        catch { File.Delete(output); throw; }
    }
}
