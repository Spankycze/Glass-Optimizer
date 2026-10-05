using System;

namespace SpankyhoOptimizer.Services;

public static class AiPowerTransitionPolicy
{
    public static int PerformanceVoteGain(
        bool performanceSignal,
        AiWorkloadClass workload,
        double confidence,
        double loadPercent,
        double? packagePowerW)
    {
        if (!performanceSignal)
            return 0;

        double load = Math.Clamp(loadPercent, 0, 100);
        double conf = Math.Clamp(confidence, 0, 100);

        if (workload == AiWorkloadClass.Game)
            return 3;

        bool urgentMeasuredDemand =
            load >= 75 ||
            (packagePowerW is double watts && watts >= 100 && load >= 35);

        if (urgentMeasuredDemand)
            return 3;

        return workload switch
        {
            AiWorkloadClass.Interactive => conf >= 60 ? 2 : 1,
            AiWorkloadClass.Background => 1,
            AiWorkloadClass.Idle => 1,
            AiWorkloadClass.Unknown => 1,
            _ => 1
        };
    }
}
