using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record VoiceCloudRateLimitState(
    DateTime UntilUtc,
    string ApiKeyFingerprint,
    int StatusCode,
    string Reason);

public static class VoiceCloudRateLimitStateService
{
    public static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "Voice",
        "gemini-conversation-rate-limit.json");

    public static bool TryGetActive(
        string apiKey,
        DateTime nowUtc,
        out VoiceCloudRateLimitState? state)
    {
        state = null;
        try
        {
            if (!File.Exists(StatePath))
                return false;

            VoiceCloudRateLimitState? loaded =
                JsonSerializer.Deserialize<VoiceCloudRateLimitState>(
                    File.ReadAllText(StatePath));

            if (loaded == null ||
                loaded.UntilUtc <= nowUtc ||
                !string.Equals(
                    loaded.ApiKeyFingerprint,
                    Fingerprint(apiKey),
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
        string reason)
    {
        var state = new VoiceCloudRateLimitState(
            untilUtc,
            Fingerprint(apiKey),
            statusCode,
            reason);

        AtomicFileService.WriteText(
            StatePath,
            JsonSerializer.Serialize(state));
    }

    public static void ClearBestEffort()
    {
        try
        {
            if (File.Exists(StatePath))
                File.Delete(StatePath);
        }
        catch { }
    }

    internal static string Fingerprint(string apiKey)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(apiKey ?? string.Empty));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
