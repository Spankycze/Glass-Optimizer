using System;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public static class GeminiSttRateLimitStateService
{
    public static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "Voice",
        "gemini-stt-rate-limit.json");

    public static readonly string FallbackStatePath = Path.Combine(Path.GetDirectoryName(StatePath)!, "gemini-stt-fallback-rate-limit.json");

    public static bool TryGetActive(
        string apiKey,
        DateTime nowUtc,
        out VoiceCloudRateLimitState? state,
        bool fallback = false)
    {
        string path = fallback ? FallbackStatePath : StatePath;
        state = null;
        try
        {
            if (!File.Exists(path))
                return false;

            VoiceCloudRateLimitState? loaded =
                JsonSerializer.Deserialize<VoiceCloudRateLimitState>(
                    File.ReadAllText(path));
            if (loaded == null ||
                loaded.UntilUtc <= nowUtc ||
                !string.Equals(
                    loaded.ApiKeyFingerprint,
                    VoiceCloudRateLimitStateService.Fingerprint(apiKey),
                    StringComparison.Ordinal))
            {
                return false;
            }

            state = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(
        string apiKey,
        DateTime untilUtc,
        int statusCode,
        string reason,
        bool fallback = false)
    {
        var state = new VoiceCloudRateLimitState(
            untilUtc,
            VoiceCloudRateLimitStateService.Fingerprint(apiKey),
            statusCode,
            reason);

        AtomicFileService.WriteText(
            fallback ? FallbackStatePath : StatePath,
            JsonSerializer.Serialize(state));
    }
}
