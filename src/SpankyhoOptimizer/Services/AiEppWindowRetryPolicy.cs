using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// The bounded EPP A/B/A sampler may repeat a noisy measurement window while
/// holding the SAME original A or candidate B setting. A workload transition,
/// thermal problem, invalid native sample, or changed Windows plan is never
/// recoverable by "trying until it wins".
/// </summary>
internal static class AiEppWindowRetryPolicy
{
    internal const int MaxRetriesPerPhase = 2;
    internal const int MaxRetriesPerTrial = 3;

    internal static bool CanRetry(
        string? windowBlocker, int previousPhaseRetries, int previousTrialRetries) =>
        (string.Equals(windowBlocker, "Measurements too noisy", StringComparison.Ordinal) ||
         string.Equals(windowBlocker, "Insufficient observation window", StringComparison.Ordinal) ||
         string.Equals(windowBlocker, "Duplicate/out-of-order/gapped measurements",
             StringComparison.Ordinal)) &&
        previousPhaseRetries >= 0 && previousPhaseRetries < MaxRetriesPerPhase &&
        previousTrialRetries >= 0 && previousTrialRetries < MaxRetriesPerTrial;
}
