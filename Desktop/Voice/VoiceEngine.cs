using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text.Json;
using Shamsa.Services;
using Vosk;

namespace Shamsa.Desktop.Voice;

internal static class VoiceEngine
{
    private const string ModelName = "vosk-model-small-fa-0.42";
    private const string Digest = "977cb5faa538f3a835ccfd35f5f6d8284b5c450b89c700b9bd4736b66536ad46";
    private static readonly string Parent = Path.Combine(AppPaths.DataDir, "speech");
    private static string Location => Path.Combine(Parent, ModelName);
    internal static bool Installed => File.Exists(Path.Combine(Location, "am", "final.mdl"));
    private static readonly SemaphoreSlim InstallGate = new(1);
    private static readonly object RecognitionGate = new();
    private static Model? _model;

    internal static async Task InstallAsync()
    {
        await InstallGate.WaitAsync();
        string stage = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        try
        {
            if (Installed) return;
            Directory.CreateDirectory(stage);
            string archive = Path.Combine(stage, "model.zip");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync($"https://alphacephei.com/vosk/models/{ModelName}.zip", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using (var output = File.Create(archive))
            using (var source = await response.Content.ReadAsStreamAsync())
            {
                byte[] buffer = new byte[65536];
                int count;
                while ((count = await source.ReadAsync(buffer)) > 0)
                {
                    if (output.Length + count > 60L * 1024 * 1024) throw new InvalidDataException("حجم بسته نامعتبر است.");
                    await output.WriteAsync(buffer.AsMemory(0, count));
                }
            }
            using (var file = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file)).Equals(Digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("بستهٔ فارسی تأیید نشد.");
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, stage));
            Directory.Move(Path.Combine(stage, ModelName), Location);
        }
        finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } finally { InstallGate.Release(); } }
    }

    internal static string Decode(byte[] pcm, VoiceLanguage language, List<VoiceCommand> commands)
    {
        lock (RecognitionGate)
        {
            if (language == VoiceLanguage.Persian)
            {
                _model ??= new Model(Location);
                using var rec = new VoskRecognizer(_model, 16000);
                rec.SetWords(true);
                rec.AcceptWaveform(pcm, pcm.Length);
                using var result = JsonDocument.Parse(rec.FinalResult());
                if (!result.RootElement.TryGetProperty("result", out var words) || words.GetArrayLength() == 0 ||
                    words.EnumerateArray().Average(w => w.GetProperty("conf").GetDouble()) < 0.65) return "";
                return result.RootElement.GetProperty("text").GetString() ?? "";
            }
            var info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                ?? throw new InvalidOperationException("بستهٔ گفتار انگلیسی را از تنظیمات زبان ویندوز نصب کنید.");
            using var engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(commands.Select(c => c.Phrase).ToArray())) { Culture = info.Culture }));
            using var input = new MemoryStream(pcm);
            engine.SetInputToAudioStream(input, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            var heard = engine.Recognize();
            return heard?.Confidence >= 0.65 ? heard.Text : "";
        }
    }
}
