using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Slow down optional device probes without delaying live-use detection
/// until the ten-minute historical observation interval. Read-only only.
/// </summary>
internal static class AiOptionalFeatureProbeCadencePolicy
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(2);

    internal static bool Due(DateTime? lastProbeUtc, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            return false;
        if (lastProbeUtc is not DateTime last || last.Kind != DateTimeKind.Utc ||
            last > nowUtc)
            return true;
        return nowUtc - last >= MinimumInterval;
    }

    internal static bool MustRefresh(
        AiOptionalFeatureObservation? previous,
        AiOptionalFeatureObservation next,
        bool deviceProbeDue) =>
        deviceProbeDue || previous != next;
}
