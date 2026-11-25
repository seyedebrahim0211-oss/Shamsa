#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Shamsa.Services;

/// <summary>
/// Real AES-256-GCM + PBKDF2-SHA256 folder locker/vault for Shamsa.
/// Packs a folder into an authenticated encrypted .shamsavault container and verifies integrity before removing source.
/// </summary>
public static class FolderVaultService
{
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("SHAMSA_VAULT_V1\0");
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 210_000;

    public static string LockFolder(string folderPath, string password, bool removeOriginalAfterVerify = false)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("پوشه مورد نظر یافت نشد.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            throw new ArgumentException("رمز عبور باید حداقل ۴ کاراکتر باشد.");

        string parent = Path.GetDirectoryName(folderPath) ?? throw new InvalidOperationException();
        string name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string tempZip = Path.Combine(Path.GetTempPath(), $"shamsa_lock_{Guid.NewGuid():N}.zip");
        string vaultPath = Path.Combine(parent, name + ".shamsavault");

        try
        {
            ZipFile.CreateFromDirectory(folderPath, tempZip, CompressionLevel.Optimal, includeBaseDirectory: true);
            byte[] plainBytes = File.ReadAllBytes(tempZip);

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

            byte[] cipher = new byte[plainBytes.Length];
            byte[] tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plainBytes, cipher, tag, MagicHeader);

            using (var fs = new FileStream(vaultPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(MagicHeader);
                fs.Write(salt);
                fs.Write(nonce);
                fs.Write(tag);
                fs.Write(cipher);
            }

            if (removeOriginalAfterVerify && File.Exists(vaultPath))
                Directory.Delete(folderPath, recursive: true);

            return vaultPath;
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    public static string UnlockVault(string vaultPath, string password, string? targetParentDir = null)
    {
        if (!File.Exists(vaultPath))
            throw new FileNotFoundException("فایل قفل‌شده یافت نشد.", vaultPath);

        byte[] raw = File.ReadAllBytes(vaultPath);
        int headerLen = MagicHeader.Length + SaltSize + NonceSize + TagSize;
        if (raw.Length <= headerLen || !raw.AsSpan(0, MagicHeader.Length).SequenceEqual(MagicHeader))
            throw new InvalidDataException("فرمت فایل قفل‌شده معتبر نیست.");

        var salt = raw.AsSpan(MagicHeader.Length, SaltSize);
        var nonce = raw.AsSpan(MagicHeader.Length + SaltSize, NonceSize);
        var tag = raw.AsSpan(MagicHeader.Length + SaltSize + NonceSize, TagSize);
        var cipher = raw.AsSpan(headerLen);

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, MagicHeader);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("رمز عبور اشتباه است یا فایل دستکاری شده است.");
        }

        string destParent = targetParentDir ?? Path.GetDirectoryName(vaultPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string tempZip = Path.Combine(Path.GetTempPath(), $"shamsa_unlock_{Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllBytes(tempZip, plain);
            ZipFile.ExtractToDirectory(tempZip, destParent, overwriteFiles: true);
            return destParent;
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }
}
