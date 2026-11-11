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
        // An invalid CPU sample cannot justify switching to an economical
        // power plan; retain performance rather than misclassifying as idle.
        if (!double.IsFinite(loadPercent) || loadPercent < 0 || loadPercent > 100)
            return true;
        double load = loadPercent;
        if (gameActive || workload == AiWorkloadClass.Game)
            return true;

        bool conservative = AiWorkloadContextPolicy.RequiresConservativePerformanceThreshold(workload);
        double loadThreshold = conservative ? 68 : workload == AiWorkloadClass.Interactive ? 40 : 45;
        double powerThreshold = conservative ? 88 : 75;
        double clockThreshold = conservative ? 4000 : 3600;
        double supportingLoad = conservative ? 40 : 25;

        if (load >= loadThreshold) return true;
        if (AiPowerTelemetryEvidencePolicy.HasUsablePower(packagePowerW) &&
            packagePowerW!.Value >= powerThreshold && load >= supportingLoad) return true;
        if (AiPowerTelemetryEvidencePolicy.HasUsableClock(clockMHz) &&
            clockMHz!.Value >= clockThreshold && load >= supportingLoad) return true;
        if (!conservative && AiPowerTelemetryEvidencePolicy.HasUsableConfidence(confidence) &&
            AiPowerTelemetryEvidencePolicy.HasUsableAnomaly(anomalyScore) &&
            confidence >= 60 && anomalyScore >= 75 && load >= 50) return true;
        return false;
    }

    public static bool ShouldThermalGuard(
        bool gameActive,
        double predictedTemp10s,
        double loadPercent,
        double confidence)
    {
        if (gameActive || !AiPowerTelemetryEvidencePolicy.HasUsableConfidence(confidence) || confidence < 55 ||
            !AiPowerTelemetryEvidencePolicy.HasUsablePredictedTemperature(predictedTemp10s) || !double.IsFinite(loadPercent) ||
            loadPercent < 0 || loadPercent > 100)
            return false;

        double load = Math.Clamp(loadPercent, 0, 100);
        return predictedTemp10s >= 78 && load < 60;
    }
}
