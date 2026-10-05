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
            Enum.TryParse(status?.Workload, true, out AiWorkloadClass parsed) &&
            parsed != AiWorkloadClass.Unknown)
        {
            return parsed;
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
