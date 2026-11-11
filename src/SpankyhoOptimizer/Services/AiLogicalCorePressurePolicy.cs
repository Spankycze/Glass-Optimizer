using System;

namespace SpankyhoOptimizer.Services;

/// <summary>Conservative, passive evidence gate for a busy logical CPU.
/// A single transient spike is never proof of a Windows performance issue.</summary>
internal static class AiLogicalCorePressurePolicy
{
    internal const double MinimumPeakPercent = 92;
    internal const double MaximumOverallPercent = 74.9;
    internal const int RequiredConsecutiveSamples = 3;
    internal static readonly TimeSpan ProviderRetryCooldown = TimeSpan.FromMinutes(5);

    // Counter registration can fail temporarily during Windows startup or a
    // performance-counter rebuild. Retry infrequently, never on every tick.
    internal static bool CanRetryProvider(DateTime failedUtc, DateTime nowUtc) =>
        failedUtc == default || nowUtc < failedUtc ||
        nowUtc - failedUtc >= ProviderRetryCooldown;

    internal static int NextStreak(int previous, double? peakPercent, double overallCpuPercent)
    {
        if (peakPercent is not double peak || !double.IsFinite(peak) ||
            !double.IsFinite(overallCpuPercent) || peak < MinimumPeakPercent ||
            peak > 100 || overallCpuPercent < 0 || overallCpuPercent > MaximumOverallPercent)
            return 0;
        return Math.Min(RequiredConsecutiveSamples, Math.Max(0, previous) + 1);
    }

    internal static bool IsSustained(int streak) => streak >= RequiredConsecutiveSamples;

    internal static bool IsLogicalProcessorName(string? name)
    {
        // PDH Processor Information uses group,logical-processor (e.g. 0,6).
        // Never include _Total group aggregations; they create false hotspots.
        if (string.IsNullOrWhiteSpace(name) || name.Contains("_Total", StringComparison.OrdinalIgnoreCase))
            return false;
        int comma = name.IndexOf(',');
        if (comma < 0)
            return int.TryParse(name, out int single) && single >= 0;
        return int.TryParse(name.AsSpan(0, comma), out int group) && group >= 0 &&
               int.TryParse(name.AsSpan(comma + 1), out int logical) && logical >= 0;
    }
}
