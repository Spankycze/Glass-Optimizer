using System;

namespace SpankyhoOptimizer.Services;

public static class OpenAiBrainFailurePolicy
{
    public static TimeSpan ResolveCooldown(string? errorText)
    {
        string text = errorText ?? string.Empty;

        if (ContainsAny(text, "429", "rate limit", "rate_limit", "too many requests",
            "quota", "insufficient_quota", "usage limit"))
            return TimeSpan.FromMinutes(5);

        if (ContainsAny(text, "401", "unauthorized", "invalid api key", "incorrect api key"))
            return TimeSpan.FromMinutes(15);

        if (ContainsAny(text, "model", "not found", "does not exist", "unavailable"))
            return TimeSpan.FromMinutes(1);

        return TimeSpan.FromSeconds(20);
    }

    private static bool ContainsAny(string text, params string[] markers)
    {
        foreach (string marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
