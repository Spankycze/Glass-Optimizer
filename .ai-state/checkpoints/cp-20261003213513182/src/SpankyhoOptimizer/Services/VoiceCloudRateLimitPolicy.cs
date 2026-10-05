using System;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public static class VoiceCloudRateLimitPolicy
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan QuotaCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinimumCooldown = TimeSpan.FromSeconds(15);

    public static bool ShouldTryNextModel(HttpStatusCode status) =>
        status == HttpStatusCode.ServiceUnavailable ||
        status == HttpStatusCode.NotFound;

    public static TimeSpan ResolveCooldown(string? responseBody, TimeSpan? retryAfter)
    {
        TimeSpan? bodyDelay = ResolveGoogleRetryDelay(responseBody);
        TimeSpan serverDelay = retryAfter.GetValueOrDefault();
        if (bodyDelay.HasValue && bodyDelay.Value > serverDelay)
            serverDelay = bodyDelay.Value;
        if (serverDelay > TimeSpan.Zero)
            return serverDelay < MinimumCooldown ? MinimumCooldown : serverDelay;

        string body = responseBody ?? string.Empty;
        if (body.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
            return QuotaCooldown;
        return DefaultCooldown;
    }

    public static TimeSpan? ResolveGoogleRetryDelay(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out JsonElement error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("details", out JsonElement details) ||
                details.ValueKind != JsonValueKind.Array)
                return null;

            TimeSpan? longest = null;
            foreach (JsonElement detail in details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object ||
                    !detail.TryGetProperty("@type", out JsonElement type) ||
                    type.ValueKind != JsonValueKind.String ||
                    type.GetString() != "type.googleapis.com/google.rpc.RetryInfo" ||
                    !detail.TryGetProperty("retryDelay", out JsonElement delay) ||
                    delay.ValueKind != JsonValueKind.String)
                    continue;
                string value = delay.GetString()!;
                if (!value.EndsWith("s", StringComparison.Ordinal) ||
                    !decimal.TryParse(value.AsSpan(0, value.Length - 1),
                        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds) ||
                    seconds <= 0 || seconds > (decimal)TimeSpan.MaxValue.TotalSeconds)
                    continue;
                var parsed = TimeSpan.FromSeconds((double)seconds);
                if (!longest.HasValue || parsed > longest.Value)
                    longest = parsed;
            }
            return longest;
        }
        catch (JsonException) { return null; }
        catch (OverflowException) { return null; }
    }
}
