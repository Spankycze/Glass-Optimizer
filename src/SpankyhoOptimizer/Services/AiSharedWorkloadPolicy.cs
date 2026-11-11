using System;

namespace SpankyhoOptimizer.Services;

public static class AiSharedWorkloadPolicy
{
    public static AiWorkloadClass Resolve(
        AdaptivePowerRuntimeStatus? status,
        bool realIdle,
        double loadPercent,
        DateTime nowUtc)
    {
        if (AdaptivePowerRuntimeService.IsStatusFresh(status, nowUtc) &&
            status is not null)
        {
            // Direct game detection is stronger than a temporarily UNKNOWN
            // stabilized workload. Do not misclassify a known running game
            // as ordinary background/idle automation.
            if (status.GameActive) return AiWorkloadClass.Game;
            if (Enum.TryParse(status.Workload, true, out AiWorkloadClass parsed) &&
                parsed != AiWorkloadClass.Unknown)
                return parsed;

            // A true foreground-interaction fallback requires a genuinely
            // comparable, recent native process CPU snapshot and exact window
            // identity. A fresh outer JSON record alone is insufficient.
            if (!realIdle && AiCpuLoadSelectionPolicy.IsValid(loadPercent) &&
                !status.RealIdle && status.ForegroundProcessId > 0 &&
                status.ForegroundWindowHandle != 0 &&
                !string.IsNullOrWhiteSpace(status.ForegroundProcess) &&
                AiCpuLoadSelectionPolicy.IsValid(status.TopCpuProcessPercent) &&
                AiSystemSmoothnessGuardService.IsFresh(
                    status.ProcessCpuObservedUtc, nowUtc, TimeSpan.FromSeconds(15)))
                return AiWorkloadClass.Interactive;
        }

        return realIdle && double.IsFinite(loadPercent) && loadPercent <= 3
            ? AiWorkloadClass.Idle
            : AiWorkloadClass.Unknown;
    }

    public static bool AllowsBackgroundAutomation(AiWorkloadClass workload) =>
        workload is AiWorkloadClass.Idle or AiWorkloadClass.Background;

    public static bool IsInteractionSensitive(AiWorkloadClass workload) =>
        workload is AiWorkloadClass.Interactive or AiWorkloadClass.Game or AiWorkloadClass.Unknown;
}
