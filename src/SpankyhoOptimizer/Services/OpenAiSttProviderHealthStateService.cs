using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record OpenAiSttProviderHealthState(
    string ApiKeyFingerprint,
    Dictionary<string, DateTime> CooldownUntilUtc,
    Dictionary<string, string> Reasons);

public static class OpenAiSttProviderHealthStateService
{
    private static readonly object Gate = new();
    public static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "Voice",
        "openai-stt-provider-health.json");

    public static readonly TimeSpan RealtimeFailureCooldown = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SegmentFailureCooldown = TimeSpan.FromMinutes(3);

    public static bool IsCoolingDown(
        string apiKey,
        string route,
        DateTime nowUtc,
        out DateTime untilUtc)
    {
        lock (Gate)
        {
            OpenAiSttProviderHealthState state = Load(apiKey);
            if (state.CooldownUntilUtc.TryGetValue(route, out untilUtc) && untilUtc > nowUtc)
                return true;
            untilUtc = DateTime.MinValue;
            return false;
        }
    }

    public static void RecordFailure(string apiKey, string route, string reason)
    {
        lock (Gate)
        {
            OpenAiSttProviderHealthState state = Load(apiKey);
            TimeSpan cooldown = route.Equals("realtime", StringComparison.OrdinalIgnoreCase)
                ? RealtimeFailureCooldown
                : SegmentFailureCooldown;
            state.CooldownUntilUtc[route] = DateTime.UtcNow.Add(cooldown);
            state.Reasons[route] = Trim(reason);
            Save(state);
        }
    }

    public static void RecordSuccess(string apiKey, string route)
    {
        lock (Gate)
        {
            OpenAiSttProviderHealthState state = Load(apiKey);
            bool changed = state.CooldownUntilUtc.Remove(route);
            changed |= state.Reasons.Remove(route);
            if (changed)
                Save(state);
        }
    }

    private static OpenAiSttProviderHealthState Load(string apiKey)
    {
        string fingerprint = VoiceCloudRateLimitStateService.Fingerprint(apiKey);
        try
        {
            if (File.Exists(StatePath))
            {
                OpenAiSttProviderHealthState? state =
                    JsonSerializer.Deserialize<OpenAiSttProviderHealthState>(File.ReadAllText(StatePath));
                if (state != null &&
                    string.Equals(state.ApiKeyFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return state with
                    {
                        CooldownUntilUtc = new Dictionary<string, DateTime>(
                            state.CooldownUntilUtc ?? new(), StringComparer.OrdinalIgnoreCase),
                        Reasons = new Dictionary<string, string>(
                            state.Reasons ?? new(), StringComparer.OrdinalIgnoreCase)
                    };
                }
            }
        }
        catch { }

        return new OpenAiSttProviderHealthState(
            fingerprint,
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static void Save(OpenAiSttProviderHealthState state)
    {
        AtomicFileService.WriteText(StatePath, JsonSerializer.Serialize(state));
    }

    private static string Trim(string? value)
    {
        string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 180 ? text : text[..180];
    }
}
