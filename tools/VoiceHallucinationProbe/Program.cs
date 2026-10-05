using System.Reflection;
using System.Text.Json;
using NAudio.Wave;
using SpankyhoOptimizer.Services;

string dir = Path.GetFullPath(args[0]), phase = args[1];
Directory.CreateDirectory(dir);
string apiKey = VoiceCloudAiSettingsService.GetApiKey();
var cases = new List<(string Name, byte[] Pcm, bool Speech)>
{
    ("silence", new byte[32000], false),
    ("soft-hum", Noise(650, 0.009, 7), false),
    ("breathy-noise", Noise(700, 0.024, 13), false),
    ("short-speech", File.ReadAllBytes(Path.Combine(dir, "..", "voice-612", "first.pcm")), true)
};
if (args.Length > 2)
    cases = cases.Where(item => item.Name == args[2]).ToList();
if (cases.Count == 0) throw new ArgumentException("Unknown probe case.");
var reports = new List<object>();
bool pass = true, complete = true;
foreach (var item in cases)
{
    bool primaryBlocked = GeminiSttRateLimitStateService.TryGetActive(apiKey, DateTime.UtcNow, out var primary);
    bool fallbackBlocked = GeminiSttRateLimitStateService.TryGetActive(apiKey, DateTime.UtcNow, out var fallback, fallback: true);
    string outcome = "unavailable", result = "";
    bool? ok = null;
    bool requestCompleted = false;
    string[] requestEvidence = Array.Empty<string>();
    if (!string.IsNullOrWhiteSpace(apiKey) && !(primaryBlocked && fallbackBlocked))
    {
        string correlation = "noise-probe-" + Guid.NewGuid().ToString("N");
        using var scope = LogService.BeginScope("voice-hallucination-probe", correlation);
        await using var service = new GeminiSegmentTranscriptionService();
        typeof(GeminiSegmentTranscriptionService).GetField("_apiKey", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, apiKey);
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(16000, 16, 1))) { writer.Write(item.Pcm, 0, item.Pcm.Length); writer.Flush(); }
        var method = typeof(GeminiSegmentTranscriptionService).GetMethod("TranscribeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            result = await (Task<string>)method.Invoke(service, new object[] { ms.ToArray(), CancellationToken.None })!;
            requestCompleted = true;
        }
        catch (Exception ex)
        {
            outcome = "request-error-" + ex.GetType().Name;
        }
        // Correlated HTTP status is required: an empty cooldown/timeout is never a silence pass.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(50);
            try
            {
                requestEvidence = File.ReadLines(Path.Combine(LogService.Root, "voice.log"))
                    .Where(line => line.Contains("[id=" + correlation + "]", StringComparison.Ordinal) &&
                        (line.Contains("gemini_http status=", StringComparison.Ordinal) ||
                         line.Contains("gemini_fallback model=", StringComparison.Ordinal) ||
                         line.Contains("VOICE_STT rate_limited provider=", StringComparison.Ordinal)))
                    .ToArray();
            }
            catch (IOException) { }
            if (requestEvidence.Any(line => line.Contains(" status=200 ", StringComparison.Ordinal))) break;
        }
        if (requestCompleted && requestEvidence.Any(line => line.Contains(" status=200 ", StringComparison.Ordinal)))
        {
            outcome = "evaluated";
            ok = item.Speech ? string.Join(" ", result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray())).Where(word => word.Length > 0)).Equals("Jakou teplotu", StringComparison.OrdinalIgnoreCase) : string.IsNullOrWhiteSpace(result);
            pass &= ok.Value;
        }
        else if (outcome == "unavailable")
            outcome = requestEvidence.Length == 0 ? "no-success-evidence" : "provider-unavailable";
    }
    complete &= ok.HasValue;
    reports.Add(new { name = item.Name, transcript = result, outcome, ok, primaryBlocked, fallbackBlocked,
        primaryUntilUtc = primary?.UntilUtc, fallbackUntilUtc = fallback?.UntilUtc, requestEvidence });
    Console.WriteLine($"NOISE_PROBE phase={phase} case={item.Name} chars={result.Length} outcome={outcome} ok={ok?.ToString() ?? "unavailable"}");
}
File.WriteAllText(Path.Combine(dir, phase + "-noise.json"), JsonSerializer.Serialize(new { complete, pass = complete && pass, cases = reports }, new JsonSerializerOptions { WriteIndented = true }));
if (phase.StartsWith("after", StringComparison.Ordinal)) Environment.ExitCode = !complete ? 3 : !pass ? 2 : 0;
static byte[] Noise(int ms, double level, int seed)
{
    int count = ms * 16; double smooth = 0; double[] values = new double[count]; var random = new Random(seed);
    for (int i = 0; i < count; i++) { smooth = smooth * 0.85 + (random.NextDouble() * 2 - 1) * 0.15; values[i] = smooth * Math.Sin(Math.PI * i / count); }
    double rms = Math.Sqrt(values.Sum(x => x * x) / count); byte[] pcm = new byte[count * 2];
    for (int i = 0; i < count; i++) { short value = (short)Math.Clamp(Math.Round(values[i] / rms * level * 32768), short.MinValue, short.MaxValue); pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8); }
    return pcm;
}
