using System;

namespace SpankyhoOptimizer.Services;

public static class AiPowerDemandPolicy
{
    public static bool HasPerformanceDemand(
        bool gameActive,
        double loadPercent,
        double? packagePowerW,
        double? clockMHz,
        double anomalyScore,
        AiWorkloadClass workload = AiWorkloadClass.Unknown,
        double confidence = 100)
    {
        double load = Math.Clamp(loadPercent, 0, 100);
        if (gameActive || workload == AiWorkloadClass.Game)
            return true;

        bool conservative = AiWorkloadContextPolicy.RequiresConservativePerformanceThreshold(workload);
        double loadThreshold = conservative ? 68 : workload == AiWorkloadClass.Interactive ? 40 : 45;
        double powerThreshold = conservative ? 88 : 75;
        double clockThreshold = conservative ? 4000 : 3600;
        double supportingLoad = conservative ? 40 : 25;

        if (load >= loadThreshold) return true;
        if (packagePowerW is double watts && watts >= powerThreshold && load >= supportingLoad) return true;
        if (clockMHz is double mhz && mhz >= clockThreshold && load >= supportingLoad) return true;
        if (!conservative && confidence >= 60 && anomalyScore >= 75 && load >= 50) return true;
        return false;
    }

    public static bool ShouldThermalGuard(
        bool gameActive,
        double predictedTemp10s,
        double loadPercent,
        double confidence)
    {
        if (gameActive || confidence < 55 || !double.IsFinite(predictedTemp10s))
            return false;

        double load = Math.Clamp(loadPercent, 0, 100);
        return predictedTemp10s >= 78 && load < 60;
    }
}
