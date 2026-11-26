using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Shamsa.Services;

namespace Shamsa.Desktop.Widgets;

// Chrome invokes this small entry point before WPF, the tray and the single-instance shell start.
internal static class DesktopEntry
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
        {
            if (args[0] == EyvanBridge.Origin) EyvanBridge.Receive(Console.OpenStandardInput(), Console.OpenStandardOutput());
            return;
        }
        App.Main();
    }
}

internal static class EyvanBridge
{
    internal const string Id = "jlcnljdceclkahacnhbmaepjdloilbcp";
    internal const string Origin = "chrome-extension://" + Id + "/";
    private const string Host = "ir.honarmardomi.eyvan_desktop";
    private const string Key = @"Software\Google\Chrome\NativeMessagingHosts\" + Host;
    private static string Manifest => Path.Combine(AppPaths.DataDir, "eyvan-host.json");
    internal static string Snapshot => Path.Combine(AppPaths.DataDir, "eyvan-snapshot.json");

    internal sealed record State(int Schema, long At, string City, string NextName, long NextAt, bool ShowSeconds = true);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string Register()
    {
        using var current = Registry.CurrentUser.OpenSubKey(Key);
        using var machine = Registry.LocalMachine.OpenSubKey(Key);
        string? existing = (current?.GetValue("") ?? machine?.GetValue("")) as string;
        if (existing != null && !string.Equals(existing, Manifest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("رابط ایوان دسکتاپ از قبل فعال است. فایل وضعیت همان ویجت را از گزینهٔ «فایل ایوان» انتخاب کنید.");
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(Manifest, JsonSerializer.Serialize(new { name = Host, description = "Eyvan for Negarkhaneh",
            path = Environment.ProcessPath, type = "stdio", allowed_origins = new[] { Origin } }));
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("", Manifest);
        return "در صفحهٔ «اتصال به ویندوز» افزونهٔ ایوان، اتصال را روشن کنید.";
    }

    internal static void Unregister()
    {
        using var root = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (string.Equals(root?.GetValue("") as string, Manifest, StringComparison.OrdinalIgnoreCase)) root!.DeleteValue("");
    }

    internal static State Validate(JsonElement root)
    {
        var s = root.Deserialize<State>(Json) ?? throw new InvalidDataException();
        if (s.Schema != 1 || string.IsNullOrWhiteSpace(s.City) || s.City.Length > 80 ||
            s.NextName?.Length > 80 || s.At <= 0 || s.At > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds() ||
            s.NextAt < 0 || s.NextAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            throw new InvalidDataException("ساختار وضعیت ایوان معتبر نیست.");
        return s;
    }

    internal static void Receive(Stream input, Stream output)
    {
        Span<byte> size = stackalloc byte[4];
        bool ok = false;
        string tmp = Snapshot + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            input.ReadExactly(size);
            int len = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (len is <= 0 or > 16384) throw new InvalidDataException();
            var bytes = new byte[len];
            input.ReadExactly(bytes);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.GetProperty("extensionId").GetString() != Id) throw new InvalidDataException();
            var snapshot = Validate(doc.RootElement);
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(tmp, Snapshot, true);
            ok = true;
        }
        catch (Exception ex) { AppLog.Warn("Eyvan message: " + ex.GetType().Name); }
        finally { try { File.Delete(tmp); } catch { } }
        byte[] reply = JsonSerializer.SerializeToUtf8Bytes(new { ok });
        BinaryPrimitives.WriteInt32LittleEndian(size, reply.Length);
        output.Write(size); output.Write(reply); output.Flush();
    }

    internal static State? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length > 16384) return null;
            using var doc = JsonDocument.Parse(f);
            return Validate(doc.RootElement);
        }
        catch { return null; }
    }
}
