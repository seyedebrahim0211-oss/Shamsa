using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shamsa.FolderStudio;

/// <summary>Friendly, Persian error for the UI.</summary>
public sealed class FolderLockException : Exception
{
    public FolderLockException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record LockProgress(double Fraction, string Message);
public sealed record LockResult(string VaultPath, int Files, int Folders, long Bytes, bool OriginalDeleted, string? DeleteWarning);
public sealed record UnlockResult(string FolderPath, int Files, int Folders, long Bytes, bool VaultDeleted);

/// <summary>
/// Encrypts a folder into one ".shamsalock" vault and restores it.
///
/// Vault format v1 (little-endian unless noted):
///   Header (plaintext): "SHMLOCK1" | u16 version=1 | u8 kdf=1 (PBKDF2-HMAC-SHA256) | u32 iterations |
///                       16-byte salt | 8-byte nonce prefix | u32 chunkSize | u16 hintLength | hint (UTF-8)
///   Records:            u32 plainLength | ciphertext (plainLength bytes) | 16-byte GCM tag
///   Each record = one AES-256-GCM chunk (≤ chunkSize). Nonce = prefix(8) ‖ recordCounter (u32 big-endian, unique).
///   AAD = SHA-256(header) ‖ u32 entryIndex ‖ u32 chunkIndex ‖ u8 finalFlag ‖ u8 recordType,
///   so records cannot be reordered, swapped between entries, truncated or moved to another vault.
///   Record stream: manifest chunks (type 1, entry 0xFFFFFFFF, JSON: names, sizes, timestamps, attributes — encrypted),
///   then per file entry its data chunks (type 2, last one final; empty file = one empty final chunk),
///   then trailer (type 3, entry 0xFFFFFFFE, final; JSON with SHA-256 of every file). Bytes after the trailer are rejected.
/// </summary>
public static class FolderLockService
{
    public const string Extension = ".shamsalock";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SHMLOCK1");
    private const ushort Version = 1;
    private const byte KdfPbkdf2Sha256 = 1;
    public const int Iterations = 600_000;
    private const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private const uint ManifestEntry = 0xFFFFFFFF;
    private const uint TrailerEntry = 0xFFFFFFFE;
    private const byte TypeManifest = 1, TypeData = 2, TypeTrailer = 3;
    public const long LargeFolderWarningBytes = 4L * 1024 * 1024 * 1024;
    public const int MinPasswordLength = 6;

    private sealed class Manifest
    {
        public string FolderName { get; set; } = string.Empty;
        public long CreatedUtcTicks { get; set; }
        public List<Entry> Entries { get; set; } = new();
    }

    private sealed class Entry
    {
        public string P { get; set; } = string.Empty; // relative path with '/'
        public bool D { get; set; }                   // directory
        public long L { get; set; }                   // length
        public long W { get; set; }                   // last write (UTC ticks)
        public long C { get; set; }                   // creation (UTC ticks)
        public int A { get; set; }                    // FileAttributes
    }

    private sealed class Trailer
    {
        public List<string> Hashes { get; set; } = new();
        public string End { get; set; } = "SHMLOCK-END";
    }

    public sealed record FolderScan(string Folder, int Files, int Folders, long Bytes);

    // ------------------------------------------------------------------ scanning

    /// <summary>Enumerates the folder without following reparse points. Throws <see cref="FolderLockException"/> when it cannot be locked safely.</summary>
    public static FolderScan Scan(string folder) => ScanInternal(folder, out _);

    private static FolderScan ScanInternal(string folder, out List<(Entry Entry, string FullPath)> items)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\');
        if (!Directory.Exists(full)) throw new FolderLockException("پوشه وجود ندارد.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new FolderLockException("این پوشه یک میانبر/Junction است و قفل نمی‌شود.");

        items = new List<(Entry, string)>();
        int files = 0, dirs = 0;
        long bytes = 0;
        var stack = new Stack<string>();
        stack.Push(full);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false }).ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new FolderLockException($"دسترسی به «{Relative(full, dir)}» ممکن نیست؛ هیچ تغییری انجام نشد.", ex);
            }
            foreach (string child in children.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            {
                var attrs = File.GetAttributes(child);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new FolderLockException($"«{Relative(full, child)}» یک میانبر نمادین/Junction است. برای امنیت، پوشه‌های دارای لینک قفل نمی‌شوند.");
                bool isDir = (attrs & FileAttributes.Directory) != 0;
                var e = new Entry { P = Relative(full, child).Replace('\\', '/'), D = isDir, A = (int)attrs };
                if (isDir)
                {
                    var di = new DirectoryInfo(child);
                    e.W = di.LastWriteTimeUtc.Ticks; e.C = di.CreationTimeUtc.Ticks;
                    dirs++;
                    stack.Push(child);
                }
                else
                {
                    var fi = new FileInfo(child);
                    e.L = fi.Length; e.W = fi.LastWriteTimeUtc.Ticks; e.C = fi.CreationTimeUtc.Ticks;
                    files++;
                    bytes += fi.Length;
                }
                items.Add((e, child));
            }
        }
        return new FolderScan(full, files, dirs, bytes);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);

    // ------------------------------------------------------------------ lock

    public static async Task<LockResult> LockAsync(string folder, string password, string? hint, bool deleteOriginalAfterVerify,
        IProgress<LockProgress>? progress = null, CancellationToken ct = default)
    {
        if (password.Length < MinPasswordLength) throw new FolderLockException($"رمز باید حداقل {MinPasswordLength} نویسه باشد.");
        return await Task.Run(() => LockCore(folder, password, hint ?? string.Empty, deleteOriginalAfterVerify, progress, ct), ct);
    }

    private static LockResult LockCore(string folder, string password, string hint, bool deleteOriginal, IProgress<LockProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new LockProgress(0, "بررسی محتوای پوشه…"));
        var scan = ScanInternal(folder, out var items);
        string full = scan.Folder;
        string parent = Path.GetDirectoryName(full) ?? throw new FolderLockException("پوشه والد پیدا نشد.");
        string name = Path.GetFileName(full);

        // Every file must be readable up front — nothing is skipped silently.
        var unreadable = new List<string>();
        foreach (var (e, path) in items.Where(i => !i.Entry.D))
        {
            ct.ThrowIfCancellationRequested();
            try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable.Add(e.P); }
        }
        if (unreadable.Count > 0)
            throw new FolderLockException("این فایل‌ها باز یا غیرقابل خواندن هستند؛ آن‌ها را ببندید و دوباره تلاش کنید. هیچ تغییری انجام نشد:\n" +
                string.Join("\n", unreadable.Take(12)) + (unreadable.Count > 12 ? $"\n… و {unreadable.Count - 12} فایل دیگر" : ""));

        long overhead = (scan.Bytes / ChunkSize + items.Count + 16) * (TagSize + 4) + 4 * 1024 * 1024;
        long needed = scan.Bytes + overhead + 64L * 1024 * 1024;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(parent)!);
            if (drive.AvailableFreeSpace < needed)
                throw new FolderLockException($"فضای خالی درایو کافی نیست. حدود {needed / 1048576:N0} مگابایت لازم است.");
        }
        catch (ArgumentException) { }

        string finalPath = UniquePath(Path.Combine(parent, name + Extension));
        string temp = Path.Combine(parent, $".{name}.{Guid.NewGuid():N}.shamsalock.part");
        progress?.Report(new LockProgress(0.02, "ساخت کلید رمزنگاری از رمز عبور…"));

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] prefix = RandomNumberGenerator.GetBytes(8);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var hashes = new List<string>(items.Count);
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                byte[] header = BuildHeader(Iterations, salt, prefix, ChunkSize, hint);
                output.Write(header);
                using var writer = new RecordWriter(output, key, prefix, SHA256.HashData(header));

                var manifest = new Manifest { FolderName = name, CreatedUtcTicks = DateTime.UtcNow.Ticks, Entries = items.Select(i => i.Entry).ToList() };
                writer.WriteBlob(JsonSerializer.SerializeToUtf8Bytes(manifest), ManifestEntry, TypeManifest);

                long done = 0;
                var buffer = new byte[ChunkSize];
                for (int index = 0; index < items.Count; index++)
                {
                    var (e, path) = items[index];
                    if (e.D) { hashes.Add(string.Empty); continue; }
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long fileRead = 0;
                    uint chunk = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int read = ReadFull(input, buffer);
                        fileRead += read;
                        if (fileRead > e.L) throw new FolderLockException($"«{e.P}» هنگام قفل شدن تغییر کرد؛ عملیات متوقف شد.");
                        bool last = fileRead == e.L && (read < buffer.Length || input.Position >= input.Length);
                        if (read < buffer.Length && fileRead != e.L)
                            throw new FolderLockException($"«{e.P}» هنگام قفل شدن تغییر کرد؛ عملیات متوقف شد.");
                        hash.AppendData(buffer, 0, read);
                        writer.Write(buffer.AsSpan(0, read), (uint)index, chunk++, last, TypeData);
                        done += read;
                        progress?.Report(new LockProgress(0.05 + 0.5 * Frac(done, scan.Bytes), $"رمزنگاری: {e.P}"));
                        if (last) break;
                    }
                    hashes.Add(Convert.ToHexString(hash.GetHashAndReset()));
                }
                writer.WriteBlob(JsonSerializer.SerializeToUtf8Bytes(new Trailer { Hashes = hashes }), TrailerEntry, TypeTrailer);
                output.Flush(flushToDisk: true);
            }

            // Verification pass: decrypt the whole vault and compare every file hash with what was read from the originals.
            progress?.Report(new LockProgress(0.56, "تأیید صحت فایل قفل (رمزگشایی کامل آزمایشی)…"));
            var verified = Decrypt(temp, password, sink: null, ct, (d, msg) => progress?.Report(new LockProgress(0.56 + 0.4 * Frac(d, scan.Bytes), msg)));
            if (verified.Manifest.Entries.Count != items.Count || !verified.ComputedHashes.SequenceEqual(hashes, StringComparer.OrdinalIgnoreCase))
                throw new FolderLockException("تأیید فایل قفل ناموفق بود؛ پوشه اصلی دست‌نخورده باقی ماند.");

            File.Move(temp, finalPath);
            temp = string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw new FolderLockException("عملیات لغو شد؛ پوشه اصلی بدون تغییر است.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new FolderLockException("ساخت فایل قفل ناموفق بود؛ پوشه اصلی بدون تغییر است. " + ex.Message, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (!string.IsNullOrEmpty(temp)) { try { File.Delete(temp); } catch { } }
        }

        bool deleted = false;
        string? warning = null;
        if (deleteOriginal)
        {
            progress?.Report(new LockProgress(0.97, "حذف پوشه اصلی پس از تأیید…"));
            try
            {
                DeleteTree(full);
                deleted = true;
            }
            catch (Exception ex)
            {
                warning = "فایل قفل سالم ساخته شد اما حذف بخشی از پوشه اصلی ممکن نشد: " + ex.Message;
            }
        }
        progress?.Report(new LockProgress(1, "پوشه با موفقیت قفل شد."));
        return new LockResult(finalPath, scan.Files, scan.Folders, scan.Bytes, deleted, warning);
    }

    private static double Frac(long done, long total) => total <= 0 ? 1 : Math.Clamp((double)done / total, 0, 1);

    private static int ReadFull(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static void DeleteTree(string folder)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            try
            {
                var a = File.GetAttributes(path);
                if ((a & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                    File.SetAttributes(path, a & ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden));
            }
            catch { }
        }
        try { File.SetAttributes(folder, FileAttributes.Directory); } catch { }
        Directory.Delete(folder, recursive: true);
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
        string dir = Path.GetDirectoryName(desired)!, stem = Path.GetFileNameWithoutExtension(desired), ext = Path.GetExtension(desired);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // ------------------------------------------------------------------ unlock

    /// <summary>Reads the clear-text password hint (empty when none). Throws for non-vault files.</summary>
    public static string ReadHint(string vaultPath)
    {
        using var fs = new FileStream(vaultPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadHeader(fs).Hint;
    }

    public static async Task<UnlockResult> UnlockAsync(string vaultPath, string password, bool deleteVaultAfter,
        IProgress<LockProgress>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => UnlockCore(vaultPath, password, deleteVaultAfter, progress, ct), ct);

    private static UnlockResult UnlockCore(string vaultPath, string password, bool deleteVault, IProgress<LockProgress>? progress, CancellationToken ct)
    {
        string full = Path.GetFullPath(vaultPath);
        if (!File.Exists(full)) throw new FolderLockException("فایل قفل پیدا نشد.");
        string parent = Path.GetDirectoryName(full)!;
        string temp = Path.Combine(parent, $".shamsa-unlock-{Guid.NewGuid():N}");
        long vaultSize = new FileInfo(full).Length;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(parent)!);
            if (drive.AvailableFreeSpace < vaultSize + 64L * 1024 * 1024)
                throw new FolderLockException("فضای خالی درایو برای بازکردن قفل کافی نیست.");
        }
        catch (ArgumentException) { }

        DecryptResult result;
        ExtractSink? sink = null;
        try
        {
            progress?.Report(new LockProgress(0.02, "بررسی رمز عبور…"));
            Directory.CreateDirectory(temp);
            sink = new ExtractSink(temp);
            result = Decrypt(full, password, sink, ct, (done, msg) => progress?.Report(new LockProgress(0.05 + 0.85 * Frac(done, Math.Max(1, vaultSize)), msg)));
            sink.Close();
            sink.ApplyMetadata(result.Manifest.Entries);
        }
        catch (Exception ex)
        {
            try { sink?.Close(); } catch { }
            TryDeleteDir(temp);
            if (ex is FolderLockException) throw;
            if (ex is OperationCanceledException) throw new FolderLockException("بازکردن قفل لغو شد؛ فایل قفل سالم است.");
            throw new FolderLockException("بازکردن قفل ناموفق بود: " + ex.Message, ex);
        }

        string folderName = SanitizeName(result.Manifest.FolderName);
        string target = Path.Combine(parent, folderName);
        if (Directory.Exists(target) || File.Exists(target))
        {
            target = Path.Combine(parent, folderName + " (unlocked)");
            for (int i = 2; Directory.Exists(target) || File.Exists(target); i++)
                target = Path.Combine(parent, $"{folderName} (unlocked {i})");
        }
        try { Directory.Move(temp, target); }
        catch (Exception ex)
        {
            TryDeleteDir(temp);
            throw new FolderLockException("انتقال پوشه بازشده ممکن نشد: " + ex.Message, ex);
        }

        bool vaultDeleted = false;
        if (deleteVault)
        {
            try { File.Delete(full); vaultDeleted = true; } catch { }
        }
        progress?.Report(new LockProgress(1, "قفل باز شد."));
        int files = result.Manifest.Entries.Count(e => !e.D);
        return new UnlockResult(target, files, result.Manifest.Entries.Count - files, result.Manifest.Entries.Where(e => !e.D).Sum(e => e.L), vaultDeleted);
    }

    private static string SanitizeName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Unlocked" : name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name is "." or ".." ? "Unlocked" : name;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) DeleteTree(dir); } catch { }
    }

    // ------------------------------------------------------------------ shared decrypt

    private sealed record Header(int Iterations, byte[] Salt, byte[] Prefix, int ChunkSize, string Hint, byte[] HeaderHash);
    private sealed record DecryptResult(Manifest Manifest, List<string> ComputedHashes);

    private static byte[] BuildHeader(int iterations, byte[] salt, byte[] prefix, int chunk, string hint)
    {
        byte[] hintBytes = Encoding.UTF8.GetBytes(hint.Length > 200 ? hint[..200] : hint);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);
        w.Write(KdfPbkdf2Sha256);
        w.Write(iterations);
        w.Write(salt);
        w.Write(prefix);
        w.Write(chunk);
        w.Write((ushort)hintBytes.Length);
        w.Write(hintBytes);
        w.Flush();
        return ms.ToArray();
    }

    private static Header ReadHeader(Stream s)
    {
        var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        try
        {
            byte[] magic = r.ReadBytes(8);
            if (!magic.AsSpan().SequenceEqual(Magic)) throw new FolderLockException("این فایل، فایل قفل نگارخانه نیست.");
            ushort version = r.ReadUInt16();
            if (version != Version) throw new FolderLockException($"نسخه فایل قفل ({version}) پشتیبانی نمی‌شود؛ نگارخانه را به‌روزرسانی کنید.");
            byte kdf = r.ReadByte();
            int iterations = r.ReadInt32();
            if (kdf != KdfPbkdf2Sha256 || iterations < 100_000 || iterations > 50_000_000) throw new FolderLockException("سرآیند فایل قفل نامعتبر است.");
            byte[] salt = r.ReadBytes(16);
            byte[] prefix = r.ReadBytes(8);
            int chunk = r.ReadInt32();
            if (chunk <= 0 || chunk > 16 * 1024 * 1024) throw new FolderLockException("سرآیند فایل قفل نامعتبر است.");
            ushort hintLen = r.ReadUInt16();
            if (hintLen > 1024) throw new FolderLockException("سرآیند فایل قفل نامعتبر است.");
            byte[] hint = r.ReadBytes(hintLen);
            if (salt.Length != 16 || prefix.Length != 8 || hint.Length != hintLen) throw new FolderLockException("فایل قفل ناقص است.");
            byte[] header = BuildHeader(iterations, salt, prefix, chunk, Encoding.UTF8.GetString(hint));
            return new Header(iterations, salt, prefix, chunk, Encoding.UTF8.GetString(hint), SHA256.HashData(header));
        }
        catch (EndOfStreamException) { throw new FolderLockException("فایل قفل ناقص است."); }
    }

    private interface IEntrySink
    {
        void BeginFile(int index, string relativePath);
        void WriteData(ReadOnlySpan<byte> data);
        void EndFile();
        void CreateDirectory(string relativePath);
    }

    private static DecryptResult Decrypt(string vault, string password, IEntrySink? sink, CancellationToken ct, Action<long, string>? onProgress)
    {
        using var fs = new FileStream(vault, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var header = ReadHeader(fs);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), header.Salt, header.Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var reader = new RecordReader(fs, key, header.Prefix, header.HeaderHash, header.ChunkSize);
            byte[] manifestBytes;
            try { manifestBytes = reader.ReadBlob(ManifestEntry, TypeManifest); }
            catch (AuthenticationTagMismatchException) { throw new FolderLockException("رمز عبور اشتباه است (یا فایل قفل آسیب دیده است)."); }
            var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes) ?? throw new FolderLockException("فهرست فایل قفل نامعتبر است.");
            foreach (var e in manifest.Entries) ValidateRelativePath(e.P);

            var computed = new List<string>(manifest.Entries.Count);
            for (int index = 0; index < manifest.Entries.Count; index++)
            {
                var e = manifest.Entries[index];
                if (e.D) { sink?.CreateDirectory(e.P); computed.Add(string.Empty); continue; }
                sink?.BeginFile(index, e.P);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                uint chunk = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var (data, final) = reader.Read((uint)index, chunk++, TypeData);
                    total += data.Length;
                    if (total > e.L) throw new FolderLockException("فایل قفل آسیب دیده است (اندازه نامعتبر).");
                    hash.AppendData(data.Span);
                    sink?.WriteData(data.Span);
                    onProgress?.Invoke(reader.BytesRead, e.P);
                    if (final) break;
                }
                if (total != e.L) throw new FolderLockException("فایل قفل آسیب دیده است (داده ناقص).");
                sink?.EndFile();
                computed.Add(Convert.ToHexString(hash.GetHashAndReset()));
            }

            var trailer = JsonSerializer.Deserialize<Trailer>(reader.ReadBlob(TrailerEntry, TypeTrailer));
            if (trailer == null || trailer.End != "SHMLOCK-END" || !trailer.Hashes.SequenceEqual(computed, StringComparer.OrdinalIgnoreCase))
                throw new FolderLockException("تأیید هش فایل‌ها ناموفق بود؛ فایل قفل آسیب دیده است.");
            if (fs.Position != fs.Length) throw new FolderLockException("داده اضافه بعد از پایان فایل قفل یافت شد؛ فایل دستکاری شده است.");
            return new DecryptResult(manifest, computed);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new FolderLockException("فایل قفل آسیب دیده یا دستکاری شده است.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void ValidateRelativePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p) || p.Contains('\0') || p.StartsWith('/') || p.StartsWith('\\') || p.Contains(':'))
            throw new FolderLockException("مسیر نامعتبر داخل فایل قفل.");
        foreach (var part in p.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new FolderLockException("مسیر نامعتبر داخل فایل قفل.");
        }
    }

    private sealed class ExtractSink : IEntrySink
    {
        private readonly string _root;
        private FileStream? _current;
        public ExtractSink(string root) => _root = Path.GetFullPath(root);

        private string Resolve(string rel)
        {
            string path = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new FolderLockException("مسیر نامعتبر داخل فایل قفل.");
            return path;
        }

        public void CreateDirectory(string rel) => Directory.CreateDirectory(Resolve(rel));

        public void BeginFile(int index, string rel)
        {
            string path = Resolve(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _current = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        }

        public void WriteData(ReadOnlySpan<byte> data) => _current!.Write(data);

        public void EndFile()
        {
            _current?.Dispose();
            _current = null;
        }

        public void Close() => EndFile();

        public void ApplyMetadata(List<Entry> entries)
        {
            // Files first, then directories deepest-first so later writes don't bump their timestamps.
            foreach (var e in entries.Where(e => !e.D))
            {
                string path = Resolve(e.P);
                try
                {
                    File.SetCreationTimeUtc(path, new DateTime(e.C, DateTimeKind.Utc));
                    File.SetLastWriteTimeUtc(path, new DateTime(e.W, DateTimeKind.Utc));
                    File.SetAttributes(path, (FileAttributes)e.A & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
                }
                catch { }
            }
            foreach (var e in entries.Where(e => e.D).OrderByDescending(e => e.P.Count(c => c == '/')))
            {
                string path = Resolve(e.P);
                try
                {
                    Directory.SetCreationTimeUtc(path, new DateTime(e.C, DateTimeKind.Utc));
                    Directory.SetLastWriteTimeUtc(path, new DateTime(e.W, DateTimeKind.Utc));
                    var keep = (FileAttributes)e.A & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                    if (keep != 0) File.SetAttributes(path, FileAttributes.Directory | keep);
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------ record framing

    private static byte[] Aad(byte[] headerHash, uint entry, uint chunk, bool final, byte type)
    {
        var aad = new byte[32 + 4 + 4 + 1 + 1];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(32), entry);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(36), chunk);
        aad[40] = final ? (byte)1 : (byte)0;
        aad[41] = type;
        return aad;
    }

    private static byte[] Nonce(byte[] prefix, uint counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), counter);
        return nonce;
    }

    private sealed class RecordWriter : IDisposable
    {
        private readonly Stream _out;
        private readonly AesGcm _gcm;
        private readonly byte[] _prefix, _headerHash;
        private uint _counter;
        private byte[] _cipher = new byte[ChunkSize];
        private readonly byte[] _tag = new byte[TagSize];
        private readonly byte[] _len = new byte[4];

        public RecordWriter(Stream output, byte[] key, byte[] prefix, byte[] headerHash)
        {
            _out = output; _prefix = prefix; _headerHash = headerHash;
            _gcm = new AesGcm(key, TagSize);
        }

        public void Write(ReadOnlySpan<byte> plain, uint entry, uint chunk, bool final, byte type)
        {
            if (_counter == uint.MaxValue) throw new FolderLockException("پوشه برای یک فایل قفل بیش از حد بزرگ است.");
            if (_cipher.Length < plain.Length) _cipher = new byte[plain.Length];
            var cipher = _cipher.AsSpan(0, plain.Length);
            _gcm.Encrypt(Nonce(_prefix, _counter++), plain, cipher, _tag, Aad(_headerHash, entry, chunk, final, type));
            BinaryPrimitives.WriteUInt32LittleEndian(_len, (uint)plain.Length);
            _out.Write(_len);
            _out.Write(cipher);
            _out.Write(_tag);
        }

        public void WriteBlob(byte[] data, uint entry, byte type)
        {
            int offset = 0;
            uint chunk = 0;
            do
            {
                int n = Math.Min(ChunkSize, data.Length - offset);
                Write(data.AsSpan(offset, n), entry, chunk++, offset + n >= data.Length, type);
                offset += n;
            } while (offset < data.Length);
        }

        public void Dispose() => _gcm.Dispose();
    }

    private sealed class RecordReader : IDisposable
    {
        private readonly Stream _in;
        private readonly AesGcm _gcm;
        private readonly byte[] _prefix, _headerHash;
        private readonly int _chunkSize;
        private uint _counter;
        private byte[] _plain;
        private byte[] _cipher;
        private readonly byte[] _tag = new byte[TagSize];
        private readonly byte[] _len = new byte[4];
        public long BytesRead => _in.Position;

        public RecordReader(Stream input, byte[] key, byte[] prefix, byte[] headerHash, int chunkSize)
        {
            _in = input; _prefix = prefix; _headerHash = headerHash; _chunkSize = chunkSize;
            _gcm = new AesGcm(key, TagSize);
            _plain = new byte[chunkSize];
            _cipher = new byte[chunkSize];
        }

        private void ReadExactly(Span<byte> buffer)
        {
            try { _in.ReadExactly(buffer); }
            catch (EndOfStreamException) { throw new FolderLockException("فایل قفل ناقص است (بریده شده)."); }
        }

        /// <summary>Reads one record; tries the non-final AAD first, then the final one (the flag is authenticated, not stored).</summary>
        public (ReadOnlyMemory<byte> Data, bool Final) Read(uint entry, uint chunk, byte type)
        {
            ReadExactly(_len);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_len);
            if (length < 0 || length > _chunkSize) throw new FolderLockException("فایل قفل آسیب دیده است.");
            ReadExactly(_cipher.AsSpan(0, length));
            ReadExactly(_tag);
            var nonce = Nonce(_prefix, _counter++);
            var cipher = _cipher.AsSpan(0, length);
            var plain = _plain.AsSpan(0, length);
            bool final = length < _chunkSize; // a short chunk can only be the last one
            try
            {
                _gcm.Decrypt(nonce, cipher, _tag, plain, Aad(_headerHash, entry, chunk, final, type));
            }
            catch (AuthenticationTagMismatchException) when (!final)
            {
                final = true; // full-size last chunk
                _gcm.Decrypt(nonce, cipher, _tag, plain, Aad(_headerHash, entry, chunk, true, type));
            }
            return (_plain.AsMemory(0, length), final);
        }

        public byte[] ReadBlob(uint entry, byte type)
        {
            using var ms = new MemoryStream();
            uint chunk = 0;
            while (true)
            {
                var (data, final) = Read(entry, chunk++, type);
                ms.Write(data.Span);
                if (ms.Length > 256L * 1024 * 1024) throw new FolderLockException("فهرست فایل قفل بیش از حد بزرگ است.");
                if (final) return ms.ToArray();
            }
        }

        public void Dispose() => _gcm.Dispose();
    }
}
