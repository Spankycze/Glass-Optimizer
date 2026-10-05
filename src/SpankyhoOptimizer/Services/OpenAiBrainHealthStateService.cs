using System;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record OpenAiBrainHealthState(
    DateTime UntilUtc,
    string ApiKeyFingerprint,
    string Reason);

public static class OpenAiBrainHealthStateService
{
    public static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "Voice",
        "openai-brain-provider-health.json");

    public static bool TryGetActive(
        string apiKey,
        DateTime nowUtc,
        out OpenAiBrainHealthState? state)
    {
        state = null;
        try
        {
            if (!File.Exists(StatePath))
                return false;

            OpenAiBrainHealthState? loaded =
                JsonSerializer.Deserialize<OpenAiBrainHealthState>(
                    File.ReadAllText(StatePath));

            if (!IsActiveForKey(loaded, apiKey, nowUtc))
                return false;

            state = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsActiveForKey(
        OpenAiBrainHealthState? state,
        string apiKey,
        DateTime nowUtc) =>
        state != null &&
        state.UntilUtc > nowUtc &&
        string.Equals(
            state.ApiKeyFingerprint,
            VoiceCloudRateLimitStateService.Fingerprint(apiKey),
            StringComparison.Ordinal);

    public static DateTime RecordFailure(
        string apiKey,
        string errorText,
        DateTime nowUtc)
    {
        TimeSpan cooldown = OpenAiBrainFailurePolicy.ResolveCooldown(errorText);
        DateTime untilUtc = nowUtc.Add(cooldown);
        var state = new OpenAiBrainHealthState(
            untilUtc,
            VoiceCloudRateLimitStateService.Fingerprint(apiKey),
            Trim(errorText));

        try
        {
            AtomicFileService.WriteText(
                StatePath,
                JsonSerializer.Serialize(state));
        }
        catch { }
        return untilUtc;
    }

    public static void RecordSuccess(string apiKey)
    {
        try
        {
            if (!File.Exists(StatePath))
                return;

            OpenAiBrainHealthState? loaded =
                JsonSerializer.Deserialize<OpenAiBrainHealthState>(
                    File.ReadAllText(StatePath));

            if (loaded != null &&
                string.Equals(
                    loaded.ApiKeyFingerprint,
                    VoiceCloudRateLimitStateService.Fingerprint(apiKey),
                    StringComparison.Ordinal))
            {
                File.Delete(StatePath);
            }
        }
        catch { }
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

    private static string Trim(string? value)
    {
        string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 240 ? text : text[..240];
    }
}
